using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;
using PharmaOS.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>A workflow draft as the author wrote it. Nothing here is released; see WorkflowStore for released versions.</summary>
public sealed record DraftInput(string Code, int Version, string InitialState, IReadOnlyList<TransitionDefinition> Transitions);

public sealed record DraftRow(
    Guid Id, string Code, int Version, string InitialState, JsonArray Transitions, string Status, IReadOnlyList<string> Errors,
    int RowVersion, DateTimeOffset UpdatedAt);

/// <summary>Drafts of workflow definitions in wf.workflow_draft. A draft of version N is refused once N is released for that code.</summary>
public sealed class WorkflowDraftStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
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
        cmd.Parameters.AddWithValue("code", input.Code);
        cmd.Parameters.AddWithValue("version", input.Version);
        cmd.Parameters.AddWithValue("initial", input.InitialState);
        cmd.Parameters.Add(new NpgsqlParameter("transitions", NpgsqlDbType.Jsonb)
            { Value = WorkflowStore.TransitionsJson(input.Transitions).ToJsonString() });
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Jsonb) { Value = ErrorsJson(errors) });
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Replaces the draft's content if it is still at <paramref name="expectedRowVersion"/>. The table's trigger advances row_version.</summary>
    public async Task<bool> UpdateAsync(Guid id, int expectedRowVersion, DraftInput input, string status,
        IReadOnlyList<string> errors, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            UPDATE wf.workflow_draft
               SET code = @code, version_no = @version, initial_state = @initial, transitions = @transitions, status = @status,
                   validation_errors = @errors
             WHERE id = @id AND row_version = @expected
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected", expectedRowVersion);
        cmd.Parameters.AddWithValue("code", input.Code);
        cmd.Parameters.AddWithValue("version", input.Version);
        cmd.Parameters.AddWithValue("initial", input.InitialState);
        cmd.Parameters.Add(new NpgsqlParameter("transitions", NpgsqlDbType.Jsonb)
            { Value = WorkflowStore.TransitionsJson(input.Transitions).ToJsonString() });
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Jsonb) { Value = ErrorsJson(errors) });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<DraftRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd($"""
            SELECT id, code, version_no, initial_state, transitions::text, status, validation_errors::text, row_version, updated_at
              FROM wf.workflow_draft WHERE id = @id {(forUpdate ? "FOR UPDATE" : "")}
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<DraftRow>> ListAsync(CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT id, code, version_no, initial_state, transitions::text, status, validation_errors::text, row_version, updated_at
              FROM wf.workflow_draft ORDER BY code, version_no
            """);
        var list = new List<DraftRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static DraftRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
        JsonNode.Parse(r.GetString(4))!.AsArray(), r.GetString(5),
        r.IsDBNull(6) ? new List<string>() : JsonNode.Parse(r.GetString(6))!.AsArray().Select(e => e!.GetValue<string>()).ToList(),
        r.GetInt32(7), r.GetFieldValue<DateTimeOffset>(8));

    private static string ErrorsJson(IReadOnlyList<string> errors) => new JsonArray([.. errors.Select(e => (JsonNode)JsonValue.Create(e)!)]).ToJsonString();
}

/// <summary>
/// Workflow authoring, step 2a: drafts of workflow definitions and their validation. A draft is stored whether or not it is valid,
/// so an author can save work in progress; its status is VALID only when every rule passes. Submitting, approval and activation follow.
/// </summary>
public static class WorkflowAuthoringEndpoints
{
    private const string Base = "/api/workflow/v1/definitions";

    public static void MapWorkflowAuthoringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapGet("", List).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPut("{id:guid}", Replace).RequireAuthorization(Permissions.WorkflowAuthor);
        group.MapPost("{id:guid}/validate", Validate).RequireAuthorization(Permissions.WorkflowAuthor);
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

        var (status, problems) = Check(input);
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
        if (gxp.Current is not { } ctx) return MissingIdentity();
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
        if (await store.ReleasedAsync(input.Code, input.Version, ct)) return ReleasedConflict(input);
        if (await store.ExistsAsync(input.Code, input.Version, id, ct)) return DraftConflict(input);

        var (status, problems) = Check(input);
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
        var row = await new WorkflowDraftStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        var problems = DefinitionValidator.Validate(row.Code, row.Version, row.InitialState, WorkflowStore.TransitionsFromJson(row.Transitions));
        return Results.Ok(new { valid = problems.Count == 0, errors = problems });
    }

    private static (string Status, IReadOnlyList<string> Problems) Check(DraftInput input)
    {
        var problems = DefinitionValidator.Validate(input.Code, input.Version, input.InitialState, input.Transitions);
        return (problems.Count == 0 ? "VALID" : "DRAFT", problems);
    }

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
                if (array[i] is not JsonObject t)
                {
                    errors[$"transitions[{i}]"] = ["A transition object."];
                    continue;
                }
                var field = $"transitions[{i}]";
                var action = Text(t, "action", errors, field);
                var from = Text(t, "from", errors, field);
                var to = Text(t, "to", errors, field);
                var permission = OptionalText(t, "permission", errors, field);
                var label = OptionalText(t, "label", errors, field);
                var guardMessage = OptionalText(t, "guardMessage", errors, field);
                var signatureMeaning = OptionalText(t, "signatureMeaning", errors, field);
                var requireReason = t["requireReason"]?.GetValue<bool>() ?? false;
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
        int RowVersion, DateTimeOffset UpdatedAt)
    {
        public static DraftResponse From(DraftRow r) =>
            new(r.Id, r.Code, r.Version, r.InitialState, r.Transitions, r.Status, r.Errors, r.RowVersion, r.UpdatedAt);
    }
}
