using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>A workflow draft as the author wrote it. Nothing here is released; see WorkflowStore for released versions.</summary>
public sealed record DraftInput(string Code, int Version, string InitialState, IReadOnlyList<TransitionDefinition> Transitions);

public sealed record DraftRow(
    Guid Id, string Code, int Version, string InitialState, JsonArray Transitions, string Status, IReadOnlyList<string> Errors,
    Guid? SubmittedBy, DateTimeOffset? SubmittedAt, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, string? DecisionComment,
    DateTimeOffset? ActivatedAt, int RowVersion, DateTimeOffset UpdatedAt)
{
    public DraftInput ToInput() => new(Code, Version, InitialState, WorkflowStore.TransitionsFromJson(Transitions));
}

public sealed class WorkflowAuthoringOptions
{
    public const string Section = "WorkflowAuthoring";

    /// <summary>
    /// When true, the person who submitted a draft cannot approve it. Off by default so a single-user installation can activate its
    /// own workflows; the audit trail still shows who submitted and who approved.
    /// </summary>
    public bool RequireSecondPerson { get; set; }
}

/// <summary>Drafts of workflow definitions in wf.workflow_draft. A draft of version N is refused once N is released for that code.</summary>
public sealed class WorkflowDraftStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private const string Columns = """
        id, code, version_no, initial_state, transitions::text, status, validation_errors::text, submitted_by, submitted_at,
        approved_by, approved_at, decision_comment, activated_at, row_version, updated_at
        """;

    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    public async Task<bool> ReleasedAsync(string code, int version, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT 1 FROM wf.process_definition_version v
              JOIN wf.process_definition d ON d.id = v.entity_id
             WHERE d.code = @code AND v.version_no = @version
             LIMIT 1
            """);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("version", version);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int?> NewestReleasedAsync(string code, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT max(v.version_no) FROM wf.process_definition_version v
              JOIN wf.process_definition d ON d.id = v.entity_id
             WHERE d.code = @code
            """);
        cmd.Parameters.AddWithValue("code", code);
        return await cmd.ExecuteScalarAsync(ct) is int newest ? newest : null;
    }

    public async Task<bool> ExistsAsync(string code, int version, Guid? except, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT 1 FROM wf.workflow_draft
             WHERE code = @code AND version_no = @version AND (@except::uuid IS NULL OR id <> @except::uuid)
             LIMIT 1
            """);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("except", (object?)except ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<Guid> InsertAsync(GxpContext ctx, DraftInput input, string status, IReadOnlyList<string> errors, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO wf.workflow_draft (tenant_id, code, version_no, initial_state, transitions, status, validation_errors,
                   created_by, updated_by)
            VALUES (@tenant, @code, @version, @initial, @transitions, @status, @errors, @user, @user)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        AddContent(cmd, input, status, errors);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Replaces the draft's content if it is still at <paramref name="expectedRowVersion"/>. The table's trigger advances row_version.</summary>
    public async Task<bool> UpdateAsync(Guid id, int expectedRowVersion, DraftInput input, string status, IReadOnlyList<string> errors,
        CancellationToken ct)
    {
        await using var cmd = Cmd("""
            UPDATE wf.workflow_draft
               SET code = @code, version_no = @version, initial_state = @initial, transitions = @transitions, status = @status,
                   validation_errors = @errors
             WHERE id = @id AND row_version = @expected
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected", expectedRowVersion);
        AddContent(cmd, input, status, errors);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Moves the draft through its lifecycle. Column names come from this class's callers, never from a request.</summary>
    public async Task<bool> SetAsync(Guid id, int expectedRowVersion, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var cmd = Cmd("");
        var sets = new List<string>();
        foreach (var (column, value) in columns)
        {
            sets.Add($"{column} = @{column}");
            cmd.Parameters.Add(value switch
            {
                null => new NpgsqlParameter(column, DBNull.Value),
                IReadOnlyList<string> list => new NpgsqlParameter(column, NpgsqlDbType.Jsonb) { Value = ErrorsJson(list) },
                _ => new NpgsqlParameter(column, value),
            });
        }
        cmd.CommandText = $"UPDATE wf.workflow_draft SET {string.Join(", ", sets)} WHERE id = @__id AND row_version = @__version";
        cmd.Parameters.AddWithValue("__id", id);
        cmd.Parameters.AddWithValue("__version", expectedRowVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<DraftRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd($"SELECT {Columns} FROM wf.workflow_draft WHERE id = @id {(forUpdate ? "FOR UPDATE" : "")}");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<DraftRow>> ListAsync(CancellationToken ct)
    {
        await using var cmd = Cmd($"SELECT {Columns} FROM wf.workflow_draft ORDER BY code, version_no");
        var list = new List<DraftRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static void AddContent(NpgsqlCommand cmd, DraftInput input, string status, IReadOnlyList<string> errors)
    {
        cmd.Parameters.AddWithValue("code", input.Code);
        cmd.Parameters.AddWithValue("version", input.Version);
        cmd.Parameters.AddWithValue("initial", input.InitialState);
        cmd.Parameters.Add(new NpgsqlParameter("transitions", NpgsqlDbType.Jsonb)
            { Value = WorkflowStore.TransitionsJson(input.Transitions).ToJsonString() });
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Jsonb) { Value = ErrorsJson(errors) });
    }

    private static DraftRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
        JsonNode.Parse(r.GetString(4))!.AsArray(), r.GetString(5),
        r.IsDBNull(6) ? new List<string>() : JsonNode.Parse(r.GetString(6))!.AsArray().Select(e => e!.GetValue<string>()).ToList(),
        r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        r.GetInt32(13), r.GetFieldValue<DateTimeOffset>(14));

    private static string ErrorsJson(IReadOnlyList<string> errors) => new JsonArray([.. errors.Select(e => (JsonNode)JsonValue.Create(e)!)]).ToJsonString();
}

/// <summary>
/// Workflow authoring: a draft of a record type's workflow is written and checked, submitted, approved and activated. Activation
/// releases it as a new version; records already in progress stay on the version they started on (URS-WF-005), new records start
/// on it. A draft is stored whether or not it is valid; it can be submitted only when every rule passes.
/// </summary>
public static class WorkflowAuthoringEndpoints
{
    private const string Base = "/api/workflow/v1/definitions";

    public const string Draft = "DRAFT";
    public const string Valid = "VALID";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Active = "ACTIVE";

    public static void MapWorkflowAuthoringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapGet("", List).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPut("{id:guid}", Replace).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPost("{id:guid}/validate", Validate).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPost("{id:guid}/submit", Submit).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPost("{id:guid}/approve", Approve).RequireAuthorization(Permissions.WorkflowApprove);
        group.MapPost("{id:guid}/reject", Reject).RequireAuthorization(Permissions.WorkflowApprove);
        group.MapPost("{id:guid}/activate", Activate).RequireAuthorization(Permissions.WorkflowApprove);
    }

    private static async Task<IResult> Create(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var errors = new Dictionary<string, string[]>();
        var input = ParseInput(body, errors);
        if (input is null) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new WorkflowDraftStore(conn, sqlTx);
        if (await store.ReleasedAsync(input.Code, input.Version, ct)) return ReleasedConflict(input);
        if (await store.ExistsAsync(input.Code, input.Version, null, ct)) return DraftConflict(input);

        var (status, problems) = await CheckAsync(store, input, ct);
        var id = await store.InsertAsync(ctx, input, status, problems, ct);
        var row = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Created($"{Base}/{id}", DraftResponse.From(row));
    }

    private static async Task<IResult> List(PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var rows = await new WorkflowDraftStore(conn, sqlTx).ListAsync(ct);
        return Results.Ok(new { items = rows.Select(DraftResponse.From) });
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new WorkflowDraftStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        SetETag(http, row.RowVersion);
        return Results.Ok(DraftResponse.From(row));
    }

    private static async Task<IResult> Replace(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is null) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();
        var errors = new Dictionary<string, string[]>();
        var input = ParseInput(body, errors);
        if (input is null) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new WorkflowDraftStore(conn, sqlTx);
        var current = await store.GetAsync(id, forUpdate: true, ct);
        if (current is null) return Results.NotFound();
        if (current.RowVersion != expected) return Stale("draft");
        if (current.Status is not (Draft or Valid)) return WrongStatus(current, "editing", "DRAFT or VALID");
        if (await store.ReleasedAsync(input.Code, input.Version, ct)) return ReleasedConflict(input);
        if (await store.ExistsAsync(input.Code, input.Version, id, ct)) return DraftConflict(input);

        var (status, problems) = await CheckAsync(store, input, ct);
        if (!await store.UpdateAsync(id, expected, input, status, problems, ct)) return Stale("draft");
        var row = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Ok(DraftResponse.From(row));
    }

    /// <summary>Runs the rules against the stored draft without changing it.</summary>
    private static async Task<IResult> Validate(Guid id, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new WorkflowDraftStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        var (_, problems) = await CheckAsync(store, row.ToInput(), ct);
        return Results.Ok(new { valid = problems.Count == 0, errors = problems });
    }

    private static Task<IResult> Submit(
        Guid id, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, HttpContext http, CancellationToken ct) =>
        StepAsync(id, db, gxp, http, ct, async (step) =>
        {
            if (step.Row.Status is not (Draft or Valid)) return WrongStatus(step.Row, "submitting", "DRAFT or VALID");
            var (_, problems) = await CheckAsync(step.Store, step.Row.ToInput(), ct);
            if (problems.Count > 0)
                return Problem(StatusCodes.Status422UnprocessableEntity, "NOT_VALID", "Draft is not valid", string.Join(" ", problems));
            return await step.SetAsync(new Dictionary<string, object?>
            {
                ["status"] = Submitted, ["submitted_by"] = step.Ctx.UserId, ["submitted_at"] = time.GetUtcNow(), ["decision_comment"] = null,
            });
        });

    private static Task<IResult> Approve(
        Guid id, DecisionBody? body, PlatformDbContext db, IGxpContextAccessor gxp, IOptions<WorkflowAuthoringOptions> options,
        TimeProvider time, HttpContext http, CancellationToken ct) =>
        StepAsync(id, db, gxp, http, ct, async (step) =>
        {
            if (step.Row.Status != Submitted) return WrongStatus(step.Row, "approval", Submitted);
            if (options.Value.RequireSecondPerson && step.Row.SubmittedBy == step.Ctx.UserId)
                return Problem(StatusCodes.Status403Forbidden, "SAME_PERSON", "Second person required",
                    "The person who submitted a workflow cannot approve it. Ask someone else with the approval permission.");
            return await step.SetAsync(new Dictionary<string, object?>
            {
                ["status"] = Approved, ["approved_by"] = step.Ctx.UserId, ["approved_at"] = time.GetUtcNow(),
                ["decision_comment"] = string.IsNullOrWhiteSpace(body?.Comment) ? null : body.Comment.Trim(),
            });
        });

    private static Task<IResult> Reject(
        Guid id, DecisionBody? body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct) =>
        StepAsync(id, db, gxp, http, ct, async (step) =>
        {
            if (step.Row.Status is not (Submitted or Approved)) return WrongStatus(step.Row, "rejection", "SUBMITTED or APPROVED");
            if (string.IsNullOrWhiteSpace(body?.Comment)) return Invalid("comment", "Say why the workflow is rejected.");
            var (status, problems) = await CheckAsync(step.Store, step.Row.ToInput(), ct);
            return await step.SetAsync(new Dictionary<string, object?>
            {
                ["status"] = status, ["validation_errors"] = problems, ["submitted_by"] = null, ["submitted_at"] = null,
                ["approved_by"] = null, ["approved_at"] = null, ["decision_comment"] = body.Comment.Trim(),
            });
        });

    /// <summary>Releases an approved draft as the newest version of its record type's workflow. New records start on it.</summary>
    private static Task<IResult> Activate(
        Guid id, PlatformDbContext db, IGxpContextAccessor gxp, SignableRegistry registry, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        StepAsync(id, db, gxp, http, ct, async (step) =>
        {
            if (step.Row.Status != Approved) return WrongStatus(step.Row, "activation", Approved);
            var input = step.Row.ToInput();
            var (_, problems) = await CheckAsync(step.Store, input, ct);
            if (problems.Count > 0)
                return Problem(StatusCodes.Status409Conflict, "NO_LONGER_VALID", "Draft is no longer valid", string.Join(" ", problems));

            var target = BuiltInWorkflows.Find(input.Code)!;
            var esig = new EsigStore(step.Conn, step.Tx);
            var wf = new WorkflowStore(step.Conn, step.Tx);
            await esig.EnsureUserAsync(step.Ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
            var entityDefinitionId = await esig.EnsureEntityDefinitionAsync(step.Ctx, registry.Find(target.EntityCode)!, ct);
            var definitionId = await wf.EnsureDefinitionAsync(step.Ctx, target.Release, target.Name, entityDefinitionId, ct);
            await WorkflowTransitions.SetChangeReasonAsync(step.Conn, step.Tx, $"Workflow {input.Code} v{input.Version} activated from draft", ct);
            await wf.WriteVersionAsync(step.Ctx, definitionId,
                new WorkflowDefinition(input.Code, input.Version, input.InitialState, input.Transitions), ct);
            await WorkflowTransitions.SetChangeReasonAsync(step.Conn, step.Tx, step.Ctx.ChangeReason, ct);
            return await step.SetAsync(new Dictionary<string, object?> { ["status"] = Active, ["activated_at"] = time.GetUtcNow() });
        });

    public sealed record DecisionBody(string? Comment);

    /// <summary>One lifecycle step on a locked draft at the version the client last read.</summary>
    private sealed record Step(GxpContext Ctx, WorkflowDraftStore Store, DraftRow Row, NpgsqlConnection Conn, NpgsqlTransaction Tx)
    {
        /// <summary>Applies the change; null when it applied, a 412 when the draft moved on meanwhile.</summary>
        public async Task<IResult?> SetAsync(IReadOnlyDictionary<string, object?> columns) =>
            await Store.SetAsync(Row.Id, Row.RowVersion, columns, CancellationToken.None) ? null : Stale("draft");
    }

    private static async Task<IResult> StepAsync(
        Guid id, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct, Func<Step, Task<IResult?>> apply)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new WorkflowDraftStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return Stale("draft");
        if (await apply(new Step(ctx, store, row, conn, sqlTx)) is { } refused) return refused;
        var updated = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, updated.RowVersion);
        return Results.Ok(DraftResponse.From(updated));
    }

    /// <summary>
    /// The rules a draft must pass: a well-formed definition (<see cref="DefinitionValidator"/>), one its record type can run
    /// (<see cref="BuiltInWorkflows"/>), and a version newer than anything released for the code, including the code's own release.
    /// </summary>
    private static async Task<(string Status, IReadOnlyList<string> Problems)> CheckAsync(
        WorkflowDraftStore store, DraftInput input, CancellationToken ct)
    {
        var problems = new List<string>(DefinitionValidator.Validate(input.Code, input.Version, input.InitialState, input.Transitions));
        problems.AddRange(BuiltInWorkflows.Check(input.Code, input.InitialState, input.Transitions));
        var newest = Math.Max(await store.NewestReleasedAsync(input.Code, ct) ?? 0, BuiltInWorkflows.Find(input.Code)?.Release.Version ?? 0);
        if (newest > 0 && input.Version <= newest)
            problems.Add($"Version must be higher than {newest}, the newest released version of {input.Code}.");
        return (problems.Count == 0 ? Valid : Draft, problems);
    }

    private static IResult WrongStatus(DraftRow row, string what, string needed) =>
        Problem(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
            $"The draft is {row.Status}; {what} needs {needed}.");

    private static IResult ReleasedConflict(DraftInput input) =>
        Problem(StatusCodes.Status409Conflict, "VERSION_RELEASED", "Version already released",
            $"Workflow {input.Code} version {input.Version} is already released. A changed workflow needs a new version number.");

    private static IResult DraftConflict(DraftInput input) =>
        Problem(StatusCodes.Status409Conflict, "DRAFT_EXISTS", "Draft already exists",
            $"There is already a draft of {input.Code} version {input.Version}. Edit that draft or choose another version number.");

    /// <summary>Reads the request. Shape problems are reported per field; rule problems are left to the validator.</summary>
    private static DraftInput? ParseInput(JsonObject body, Dictionary<string, string[]> errors)
    {
        var code = Text(body, "code", errors);
        var initialState = Text(body, "initialState", errors);
        int version = 0;
        if (body["version"] is JsonValue v && v.TryGetValue<int>(out var n)) version = n;
        else errors["version"] = ["A whole number."];
        var transitions = new List<TransitionDefinition>();
        if (body["transitions"] is not JsonArray array)
            errors["transitions"] = ["A list of transitions."];
        else
            for (var i = 0; i < array.Count; i++)
            {
                var field = $"transitions[{i}]";
                if (array[i] is not JsonObject t)
                {
                    errors[field] = ["A transition object."];
                    continue;
                }
                var action = Text(t, "action", errors, field);
                var from = Text(t, "from", errors, field);
                var to = Text(t, "to", errors, field);
                var permission = OptionalText(t, "permission", errors, field);
                var label = OptionalText(t, "label", errors, field);
                var guardMessage = OptionalText(t, "guardMessage", errors, field);
                var signatureMeaning = OptionalText(t, "signatureMeaning", errors, field);
                var requireReason = false;
                if (t["requireReason"] is { } rr && !(rr is JsonValue rv && rv.TryGetValue(out requireReason)))
                    errors[$"{field}.requireReason"] = ["true or false, if given."];
                transitions.Add(new TransitionDefinition(action ?? "", from ?? "", to ?? "",
                    Permission: permission, Guard: t["guard"]?.DeepClone(), SignatureMeaning: signatureMeaning, Label: label,
                    RequireReason: requireReason, GuardMessage: guardMessage));
            }
        if (errors.Count > 0) return null;
        return new DraftInput(code!, version, initialState!, transitions);
    }

    private static string? Text(JsonObject o, string name, Dictionary<string, string[]> errors, string? prefix = null)
    {
        var key = prefix is null ? name : $"{prefix}.{name}";
        if (o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        errors[key] = ["Required text."];
        return null;
    }

    private static string? OptionalText(JsonObject o, string name, Dictionary<string, string[]> errors, string prefix)
    {
        if (o[name] is null) return null;
        if (o[name] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        errors[$"{prefix}.{name}"] = ["Text, if given."];
        return null;
    }

    private sealed record DraftResponse(
        Guid Id, string Code, int Version, string InitialState, JsonArray Transitions, string Status, IReadOnlyList<string> Errors,
        Guid? SubmittedBy, DateTimeOffset? SubmittedAt, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, string? DecisionComment,
        DateTimeOffset? ActivatedAt, int RowVersion, DateTimeOffset UpdatedAt)
    {
        public static DraftResponse From(DraftRow r) =>
            new(r.Id, r.Code, r.Version, r.InitialState, r.Transitions, r.Status, r.Errors, r.SubmittedBy, r.SubmittedAt, r.ApprovedBy,
                r.ApprovedAt, r.DecisionComment, r.ActivatedAt, r.RowVersion, r.UpdatedAt);
    }
}
