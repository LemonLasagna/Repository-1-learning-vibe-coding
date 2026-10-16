using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>Change controls as a workflow record type.</summary>
public sealed class ChangeEntity : IWorkflowEntity
{
    public static readonly ChangeEntity Instance = new();

    public string Noun => "change control";
    public WorkflowDefinition Definition => ChangeWorkflow.Definition;

    public async Task<IWorkflowRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct) =>
        await new ChangeStore(conn, tx).GetAsync(id, forUpdate, ct);

    public Task<bool> SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, IWorkflowRecord row, TransitionDefinition t,
        DateTimeOffset now, CancellationToken ct) =>
        new ChangeStore(conn, tx).UpdateAsync(row.Id,
            row.RowVersion, new Dictionary<string, object?> { ["status"] = t.To, ["workflow_state"] = t.To }, ct);
}

/// <summary>Changes are signed only through transitions: approval, verification of the implementation, closure, cancel.</summary>
public sealed class ChangeSignable() : WorkflowSignable(ChangeEntity.Instance)
{
    public override string EntityCode => "qms.change";
    public override string EntityName => "Change control";
    public override string ModuleCode => "qms";
    public override string ModuleName => "Quality management";
    public override string PhysicalTable => "qms.change_control";
    public override string ReadPermission => Permissions.ChangeRead;
    public override IReadOnlyList<string> Meanings { get; } =
        [EsigCatalog.Approved.Code, EsigCatalog.Verified.Code, EsigCatalog.Cancelled.Code];
}

/// <summary>
/// Change control (URS-QMS-009, FS §9.3.3): header, impact assessments per area and implementation tasks. Conventions as CAPA:
/// ETag/If-Match on the change for every change (assessments and tasks included), state-dependent changes, transitions
/// through the workflow.
/// </summary>
public static class ChangeEndpoints
{
    private const string Base = "/api/qms/v1/changes";
    private const int ListLimit = 500;

    public static void MapChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.ChangeWrite);
        group.MapGet("", List).RequireAuthorization(Permissions.ChangeRead);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.ChangeRead);
        group.MapPatch("{id:guid}", UpdateHeader).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPut("{id:guid}/impacts/{area}", SaveImpact).RequireAuthorization(Permissions.ChangeWrite);
        group.MapDelete("{id:guid}/impacts/{area}", DeleteImpact).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPost("{id:guid}/tasks", AddTask).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPatch("{id:guid}/tasks/{no:int}", UpdateTask).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPost("{id:guid}/tasks/{no:int}/complete", CompleteTask).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPatch("{id:guid}/implementation", SetImplementation).RequireAuthorization(Permissions.ChangeWrite);
        group.MapPost("{id:guid}/transitions", Transition).RequireAuthorization(Permissions.ChangeRead);
        group.MapGet("{id:guid}/history", History).RequireAuthorization(Permissions.ChangeRead);
        group.MapGet("{id:guid}/available-actions", Available).RequireAuthorization(Permissions.ChangeRead);
    }

    private static async Task<IResult> Create(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, SignableRegistry registry, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var header = RequestParsing.Parse(body, ChangeWorkflow.HeaderFields, requireAll: true, errors, extra: ["siteId"]);
        Guid? siteId = RequestParsing.TryGuid(body, "siteId", errors);
        if (siteId is null) errors.TryAdd("siteId", ["Required."]);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (header.TryGetValue("is_temporary", out var temporary) && temporary is null) header["is_temporary"] = false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new ChangeStore(conn, sqlTx);
        if (!await store.SiteExistsAsync(siteId!.Value, ct)) return Invalid("siteId", "Unknown or inactive site.");

        var esig = new EsigStore(conn, sqlTx);
        var wf = new WorkflowStore(conn, sqlTx);
        await esig.EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        var entityDefinitionId = await esig.EnsureEntityDefinitionAsync(ctx, registry.Find("qms.change")!, ct);
        var definitionId = await wf.EnsureDefinitionAsync(ctx, ChangeWorkflow.Definition, "Change control (standard)", entityDefinitionId, ct);
        var id = Guid.CreateVersion7();
        var number = await store.NextNumberAsync(ctx, now.UtcDateTime.Year, ct);
        var instance = await wf.StartInstanceAsync(ctx, definitionId, ChangeWorkflow.Definition, entityDefinitionId, number, id, now, ct);
        header.TryAdd("implementation_due", DateOnly.FromDateTime(now.UtcDateTime).AddDays(ChangeWorkflow.DefaultDueDays));
        header.TryAdd("regulatory_impact", "NONE");
        header["site_id"] = siteId;
        await store.InsertAsync(ctx, id, number, instance, header, ct);
        await wf.AddHistoryAsync(ctx, instance, null, ChangeWorkflow.Draft, now, "Created", null, ct);
        var row = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Created($"{Base}/{id}", ChangeResponse.From(row));
    }

    private static async Task<IResult> List(string? status, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var rows = await new ChangeStore(conn, sqlTx).ListAsync(string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), ListLimit, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { items = rows.Select(ChangeResponse.From), truncated = rows.Count == ListLimit });
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new ChangeStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        SetETag(http, row.RowVersion);
        return Results.Ok(ChangeResponse.From(row));
    }

    private static Task<IResult> UpdateHeader(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Draft, ChangeWorkflow.Assessment], "Change description edited", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, ChangeWorkflow.HeaderFields, requireAll: false, errors);
            if (columns.TryGetValue("is_temporary", out var temporary) && temporary is null) errors["isTemporary"] = ["true or false."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return await store.UpdateAsync(row.Id, row.RowVersion, columns, ct) ? null : Stale("change control");
        }, touch: false, ct);

    private static Task<IResult> SaveImpact(
        Guid id, string area, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Assessment], $"Impact assessment {area}", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            area = area.ToUpperInvariant();
            if (!ChangeWorkflow.Areas.Contains(area)) return Invalid("impactedArea", $"One of: {string.Join(", ", ChangeWorkflow.Areas)}.");
            var errors = new Dictionary<string, string[]>();
            body["impactedArea"] = area;
            var columns = RequestParsing.Parse(body, ChangeWorkflow.ImpactFields, requireAll: true, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            await store.SaveImpactAsync(ctx, row.Id, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> DeleteImpact(
        Guid id, string area, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Assessment], $"Impact assessment {area} withdrawn", db, gxp, time, user, http, async (store, ctx, row, now) =>
            await store.DeleteImpactAsync(row.Id, area.ToUpperInvariant(), ct) ? null : Results.NotFound(), touch: true, ct);

    private static Task<IResult> AddTask(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Assessment], "Task planned", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, ChangeWorkflow.TaskFields, requireAll: true, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (columns.GetValueOrDefault("assignee_id") is null) columns["assignee_id"] = ctx.UserId;
            if (!await store.UserExistsAsync((Guid)columns["assignee_id"]!, ct))
                return Invalid("assigneeId", "Unknown user. People appear here after they have signed in once.");
            await store.InsertTaskAsync(ctx, row.Id, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> UpdateTask(
        Guid id, int no, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Assessment], $"Task {no} changed", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            if (row.Tasks.All(t => t.No != no)) return Results.NotFound();
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, ChangeWorkflow.TaskFields, requireAll: false, errors, extra: ["withdrawn"]);
            if (body["withdrawn"] is { } w)
            {
                if (w.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    columns["status"] = w.GetValue<bool>() ? ChangeTaskStatus.Withdrawn : ChangeTaskStatus.Planned;
                else errors["withdrawn"] = ["true or false."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (columns.TryGetValue("assignee_id", out var assignee) && (assignee is null || !await store.UserExistsAsync((Guid)assignee, ct)))
                return Invalid("assigneeId", "Unknown user.");
            await store.UpdateTaskAsync(row.Id, no, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> CompleteTask(
        Guid id, int no, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Implementation], $"Task {no} completed", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var task = row.Tasks.FirstOrDefault(t => t.No == no);
            if (task is null) return Results.NotFound();
            if (task.Status != ChangeTaskStatus.Planned)
                return Problem(StatusCodes.Status409Conflict, "TASK_NOT_OPEN", "Task not open", $"Task {no} is {task.Status.ToLowerInvariant()}.");
            await store.UpdateTaskAsync(row.Id, no,
                new Dictionary<string, object?> { ["status"] = ChangeTaskStatus.Done, ["completed_at"] = now }, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> SetImplementation(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [ChangeWorkflow.Implementation], "Implementation dates set", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, ChangeWorkflow.ImplementationFields, requireAll: false, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return await store.UpdateAsync(row.Id, row.RowVersion, columns, ct) ? null : Stale("change control");
        }, touch: false, ct);

    private static async Task<IResult> Transition(
        Guid id, TransitionRequest body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        gxp.Current is not { } ctx
            ? MissingIdentity()
            : await WorkflowTransitions.HandleAsync(ChangeEntity.Instance, id, body, db, ctx, time, user, http,
                async (conn, tx, rowId, c) => ChangeResponse.From((await new ChangeStore(conn, tx).GetAsync(rowId, false, c))!), ct);

    private static async Task<IResult> History(Guid id, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new ChangeStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        var history = row.WorkflowInstanceId is { } instance ? await new WorkflowStore(conn, sqlTx).HistoryAsync(instance, ct) : [];
        await tx.CommitAsync(ct);
        return Results.Ok(history);
    }

    private static async Task<IResult> Available(Guid id, PlatformDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new ChangeStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        var permissions = user.PermissionsOf();
        var write = permissions.Contains(Permissions.ChangeWrite);
        return Results.Ok(new
        {
            state = row.Status,
            canEditHeader = write && row.Status is ChangeWorkflow.Draft or ChangeWorkflow.Assessment,
            canAssess = write && row.Status == ChangeWorkflow.Assessment,
            canPlanTasks = write && row.Status == ChangeWorkflow.Assessment,
            canCompleteTasks = write && row.Status == ChangeWorkflow.Implementation,
            canSetDates = write && row.Status == ChangeWorkflow.Implementation,
            actions = WorkflowTransitions.Available(ChangeWorkflow.Definition, row, permissions),
        });
    }

    /// <summary>
    /// A change to the change control or its assessments and tasks in the given states: If-Match on the change, the GxP context's
    /// reason (or <paramref name="what"/>) for the audit trail, and, for child records, a version bump so its ETag changes.
    /// </summary>
    private static async Task<IResult> Change(
        Guid id, string[] states, string what, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, Func<ChangeStore, GxpContext, ChangeRow, DateTimeOffset, Task<IResult?>> change, bool touch, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new ChangeStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return Stale("change control");
        if (!states.Contains(row.Status))
            return Problem(StatusCodes.Status409Conflict, "WRONG_STATE", "Not possible now",
                $"This can only be done while the change control is in {string.Join(" or ", states)}; it is in {row.Status}.");
        // Assessments and tasks refer to the acting user's record.
        await new EsigStore(conn, sqlTx).EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        await WorkflowTransitions.SetChangeReasonAsync(conn, sqlTx, ctx.ChangeReason ?? what, ct);
        if (await change(store, ctx, row, time.GetUtcNow()) is { } failed) return failed;
        if (touch && !await store.UpdateAsync(id, expected, new Dictionary<string, object?>(), ct)) return Stale("change control");
        var updated = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);
        SetETag(http, updated.RowVersion);
        return Results.Ok(ChangeResponse.From(updated));
    }
}
