using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>CAPA as a workflow record type.</summary>
public sealed class CapaEntity : IWorkflowEntity
{
    public static readonly CapaEntity Instance = new();

    public string Noun => "CAPA";
    public WorkflowDefinition Definition => CapaWorkflow.Definition;

    public async Task<IWorkflowRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct) =>
        await new CapaStore(conn, tx).GetAsync(id, forUpdate, ct);

    public Task<bool> SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, IWorkflowRecord row, TransitionDefinition t,
        DateTimeOffset now, CancellationToken ct)
    {
        var columns = new Dictionary<string, object?> { ["status"] = t.To, ["workflow_state"] = t.To };
        if (t.To == CapaWorkflow.Closed) columns["closed_at"] = now;
        return new CapaStore(conn, tx).UpdateAsync(row.Id, row.RowVersion, columns, ct);
    }
}

/// <summary>CAPAs are signed only through transitions: plan approval, verification of implementation and effectiveness, cancel.</summary>
public sealed class CapaSignable() : WorkflowSignable(CapaEntity.Instance)
{
    public override string EntityCode => "qms.capa";
    public override string EntityName => "CAPA";
    public override string ModuleCode => "qms";
    public override string ModuleName => "Quality management";
    public override string PhysicalTable => "qms.capa";
    public override string ReadPermission => Permissions.CapaRead;
    public override IReadOnlyList<string> Meanings { get; } =
        [EsigCatalog.Approved.Code, EsigCatalog.Verified.Code, EsigCatalog.Cancelled.Code];
}

/// <summary>
/// CAPA (URS-QMS-006..008, FS §9.3.2): header, actions and effectiveness check. Conventions as deviations: ETag/If-Match on the
/// CAPA for every change (actions and the check included), state-dependent changes, transitions through the workflow.
/// </summary>
public static class CapaEndpoints
{
    private const string Base = "/api/qms/v1/capas";
    private const int ListLimit = 500;

    public static void MapCapaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/iam/v1/users", Users);
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.CapaWrite);
        group.MapGet("", List).RequireAuthorization(Permissions.CapaRead);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.CapaRead);
        group.MapPatch("{id:guid}", UpdateHeader).RequireAuthorization(Permissions.CapaWrite);
        group.MapPost("{id:guid}/actions", AddAction).RequireAuthorization(Permissions.CapaWrite);
        group.MapPatch("{id:guid}/actions/{no:int}", UpdateAction).RequireAuthorization(Permissions.CapaWrite);
        group.MapPost("{id:guid}/actions/{no:int}/complete", CompleteAction).RequireAuthorization(Permissions.CapaWrite);
        group.MapPut("{id:guid}/actions/{no:int}/change", LinkChange).RequireAuthorization(Permissions.CapaWrite);
        group.MapDelete("{id:guid}/actions/{no:int}/change", UnlinkChange).RequireAuthorization(Permissions.CapaWrite);
        group.MapPut("{id:guid}/effectiveness", DefineEffectiveness).RequireAuthorization(Permissions.CapaWrite);
        group.MapPost("{id:guid}/effectiveness/result", RecordEffectiveness).RequireAuthorization(Permissions.CapaWrite);
        group.MapPost("{id:guid}/transitions", Transition).RequireAuthorization(Permissions.CapaRead);
        group.MapGet("{id:guid}/history", History).RequireAuthorization(Permissions.CapaRead);
        group.MapGet("{id:guid}/available-actions", Available).RequireAuthorization(Permissions.CapaRead);
    }

    /// <summary>People known to PharmaOS (signed in at least once), for assigning actions.</summary>
    private static async Task<IResult> Users(PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        await using var cmd = new NpgsqlCommand("SELECT id, username, display_name FROM iam.app_user WHERE status = 'ACTIVE' ORDER BY display_name", conn, sqlTx);
        var list = new List<object>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) list.Add(new { id = r.GetGuid(0), username = r.GetString(1), displayName = r.GetString(2) });
        await tx.CommitAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> Create(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, SignableRegistry registry, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var header = RequestParsing.Parse(body, CapaWorkflow.HeaderFields, requireAll: true, errors, extra: ["sourceType", "sourceId", "siteId"]);
        var sourceType = (body["sourceType"]?.GetValue<string>() ?? "OTHER").Trim().ToUpperInvariant();
        if (sourceType is not ("DEVIATION" or "OTHER")) errors["sourceType"] = ["One of: DEVIATION, OTHER."];
        Guid? sourceId = null, siteId = null;
        if (RequestParsing.TryGuid(body, "sourceId", errors) is { } s) sourceId = s;
        if (RequestParsing.TryGuid(body, "siteId", errors) is { } site) siteId = site;
        if (sourceType == "DEVIATION" && sourceId is null) errors.TryAdd("sourceId", ["Required for a deviation source."]);
        if (sourceType == "OTHER" && siteId is null) errors.TryAdd("siteId", ["Required when the CAPA has no source record."]);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        if (sourceType == "DEVIATION")
        {
            var deviation = await new DeviationStore(conn, sqlTx).GetAsync(sourceId!.Value, forUpdate: false, ct);
            if (deviation is null) return Invalid("sourceId", "Unknown deviation.");
            if (WorkflowTransitions.IsFinal(DeviationWorkflow.Definition, deviation.Status))
                return Invalid("sourceId", $"Deviation {deviation.DeviationNo} is {deviation.Status.ToLowerInvariant()}.");
            siteId = deviation.SiteId;
        }
        else if (!await new DeviationStore(conn, sqlTx).SiteExistsAsync(siteId!.Value, ct))
            return Invalid("siteId", "Unknown or inactive site.");

        var esig = new EsigStore(conn, sqlTx);
        var wf = new WorkflowStore(conn, sqlTx);
        var store = new CapaStore(conn, sqlTx);
        await esig.EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        var entityDefinitionId = await esig.EnsureEntityDefinitionAsync(ctx, registry.Find("qms.capa")!, ct);
        var definitionId = await wf.EnsureDefinitionAsync(ctx, CapaWorkflow.Definition, "CAPA (standard)", entityDefinitionId, ct);
        var id = Guid.CreateVersion7();
        var number = await store.NextNumberAsync(ctx, now.UtcDateTime.Year, ct);
        var instance = await wf.StartInstanceAsync(ctx, definitionId, await wf.CurrentAsync(definitionId, CapaWorkflow.Definition, ct), entityDefinitionId, number, id, now, ct);
        header.TryAdd("due_date", DateOnly.FromDateTime(now.UtcDateTime).AddDays(CapaWorkflow.DefaultDueDays));
        header["site_id"] = siteId;
        header["source_type"] = sourceType;
        header["source_id"] = sourceId;
        await store.InsertAsync(ctx, id, number, instance, header, ct);
        await wf.AddHistoryAsync(ctx, instance, null, CapaWorkflow.Draft, now, "Created", null, ct);
        var row = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Created($"{Base}/{id}", CapaResponse.From(row));
    }

    private static async Task<IResult> List(Guid? sourceId, string? status, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var rows = await new CapaStore(conn, sqlTx).ListAsync(sourceId, string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), ListLimit, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { items = rows.Select(CapaResponse.From), truncated = rows.Count == ListLimit });
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new CapaStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        SetETag(http, row.RowVersion);
        return Results.Ok(CapaResponse.From(row));
    }

    private static Task<IResult> UpdateHeader(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft], "Plan changed", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, CapaWorkflow.HeaderFields, requireAll: false, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return await store.UpdateAsync(row.Id, row.RowVersion, columns, ct) ? null : Stale("CAPA");
        }, touch: false, ct);

    private static Task<IResult> AddAction(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft], "Action planned", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, CapaWorkflow.ActionFields, requireAll: true, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            columns.TryAdd("assignee_id", ctx.UserId);
            if (columns["assignee_id"] is null) columns["assignee_id"] = ctx.UserId;
            if (!await store.UserExistsAsync((Guid)columns["assignee_id"]!, ct))
                return Invalid("assigneeId", "Unknown user. People appear here after they have signed in once.");
            await store.InsertActionAsync(ctx, row.Id, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> UpdateAction(
        Guid id, int no, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft], $"Action {no} changed", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            if (row.Actions.All(a => a.No != no)) return Results.NotFound();
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, CapaWorkflow.ActionFields, requireAll: false, errors, extra: ["withdrawn"]);
            if (body["withdrawn"] is { } w)
            {
                if (w.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    columns["status"] = w.GetValue<bool>() ? CapaActionStatus.Withdrawn : CapaActionStatus.Planned;
                else errors["withdrawn"] = ["true or false."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (columns.TryGetValue("assignee_id", out var assignee) && (assignee is null || !await store.UserExistsAsync((Guid)assignee, ct)))
                return Invalid("assigneeId", "Unknown user.");
            await store.UpdateActionAsync(row.Id, no, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> CompleteAction(
        Guid id, int no, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Implementation], $"Action {no} completed", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var action = row.Actions.FirstOrDefault(a => a.No == no);
            if (action is null) return Results.NotFound();
            if (action.Status != CapaActionStatus.Planned)
                return Problem(StatusCodes.Status409Conflict, "ACTION_NOT_OPEN", "Action not open", $"Action {no} is {action.Status.ToLowerInvariant()}.");
            var evidence = body["evidence"]?.GetValueKind() == JsonValueKind.String ? body["evidence"]!.GetValue<string>().Trim() : "";
            if (evidence.Length == 0) return Invalid("evidence", "Describe what was done and where the evidence is.");
            if (evidence.Length > 20000) return Invalid("evidence", "Maximum length is 20000.");
            await store.UpdateActionAsync(row.Id, no,
                new Dictionary<string, object?> { ["status"] = CapaActionStatus.Done, ["completed_at"] = now, ["evidence"] = evidence }, ct);
            return null;
        }, touch: true, ct);

    /// <summary>Links an open action to an existing change control (to raise a new one, create the change from the action).</summary>
    private static Task<IResult> LinkChange(
        Guid id, int no, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft, CapaWorkflow.Implementation], $"Action {no} linked to a change", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            if (!user.PermissionsOf().Contains(Permissions.ChangeRead))
                return Problem(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Linking a change needs the permission {Permissions.ChangeRead}.");
            var errors = new Dictionary<string, string[]>();
            if (RequestParsing.TryGuid(body, "changeId", errors) is not { } changeId) return Invalid("changeId", "Required.");
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (ChangeEndpoints.CheckLinkable(row, no) is { } refused) return refused;
            var (conn, sqlTx) = Sql(db);
            var change = await new ChangeStore(conn, sqlTx).GetAsync(changeId, forUpdate: false, ct);
            if (change is null) return Invalid("changeId", "Unknown change control.");
            if (WorkflowTransitions.IsFinal(ChangeWorkflow.Definition, change.Status))
                return Invalid("changeId", $"{change.CcNo} is {change.Status.ToLowerInvariant()}.");
            await store.UpdateActionAsync(row.Id, no, new Dictionary<string, object?> { ["change_control_id"] = changeId }, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> UnlinkChange(
        Guid id, int no, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft, CapaWorkflow.Implementation], $"Action {no} unlinked from its change", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var action = row.Actions.FirstOrDefault(a => a.No == no);
            if (action is null) return Results.NotFound();
            if (action.Status != CapaActionStatus.Planned)
                return Problem(StatusCodes.Status409Conflict, "ACTION_NOT_OPEN", "Action not open", $"Action {no} is {action.Status.ToLowerInvariant()}.");
            if (action.ChangeId is null) return Problem(StatusCodes.Status409Conflict, "NOT_LINKED", "Not linked", $"Action {no} is not linked to a change.");
            await store.UpdateActionAsync(row.Id, no, new Dictionary<string, object?> { ["change_control_id"] = null }, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> DefineEffectiveness(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.Draft], "Effectiveness check planned", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            var errors = new Dictionary<string, string[]>();
            var columns = RequestParsing.Parse(body, CapaWorkflow.EffectivenessFields, requireAll: true, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            columns.TryAdd("method", null);
            await store.SaveEffectivenessAsync(ctx, row.Id, columns, ct);
            return null;
        }, touch: true, ct);

    private static Task<IResult> RecordEffectiveness(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        Change(id, [CapaWorkflow.EffectivenessCheck], "Effectiveness check result", db, gxp, time, user, http, async (store, ctx, row, now) =>
        {
            if (row.Effectiveness is null) return Problem(StatusCodes.Status409Conflict, "NO_CHECK", "No effectiveness check", "The plan has no effectiveness check.");
            var result = body["result"]?.GetValueKind() == JsonValueKind.String ? body["result"]!.GetValue<string>().Trim().ToUpperInvariant() : "";
            if (result is not ("EFFECTIVE" or "INEFFECTIVE")) return Invalid("result", "EFFECTIVE or INEFFECTIVE.");
            var evidence = body["evidence"]?.GetValueKind() == JsonValueKind.String ? body["evidence"]!.GetValue<string>().Trim() : "";
            if (evidence.Length == 0) return Invalid("evidence", "Describe the data the result is based on.");
            await store.SaveEffectivenessAsync(ctx, row.Id, new Dictionary<string, object?>
                { ["result"] = result, ["evidence"] = evidence, ["performed_at"] = now, ["performed_by"] = ctx.UserId }, ct);
            return null;
        }, touch: true, ct);

    private static async Task<IResult> Transition(
        Guid id, TransitionRequest body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        gxp.Current is not { } ctx
            ? MissingIdentity()
            : await WorkflowTransitions.HandleAsync(CapaEntity.Instance, id, body, db, ctx, time, user, http,
                async (conn, tx, rowId, c) => CapaResponse.From((await new CapaStore(conn, tx).GetAsync(rowId, false, c))!), ct);

    private static async Task<IResult> History(Guid id, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new CapaStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        var history = row.WorkflowInstanceId is { } instance ? await new WorkflowStore(conn, sqlTx).HistoryAsync(instance, ct) : [];
        await tx.CommitAsync(ct);
        return Results.Ok(history);
    }

    private static async Task<IResult> Available(Guid id, PlatformDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await new CapaStore(conn, sqlTx).GetAsync(id, forUpdate: false, ct);
        var definition = row is null ? null : await new WorkflowStore(conn, sqlTx).DefinitionOfAsync(row, CapaEntity.Instance, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        var permissions = user.PermissionsOf();
        var write = permissions.Contains(Permissions.CapaWrite);
        return Results.Ok(new
        {
            state = row.Status,
            canEditPlan = write && row.Status == CapaWorkflow.Draft,
            canCompleteActions = write && row.Status == CapaWorkflow.Implementation,
            canRecordEffectiveness = write && row.Status == CapaWorkflow.EffectivenessCheck,
            canLinkChanges = write && row.Status is CapaWorkflow.Draft or CapaWorkflow.Implementation,
            actions = WorkflowTransitions.Available(definition!, row, permissions),
        });
    }

    /// <summary>
    /// A change to the CAPA or its plan in one state: If-Match on the CAPA, a share of the GxP context's reason (or
    /// <paramref name="what"/>) for the audit trail, and, for child records, a version bump of the CAPA so its ETag changes.
    /// </summary>
    private static async Task<IResult> Change(
        Guid id, string[] states, string what, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, Func<CapaStore, GxpContext, CapaRow, DateTimeOffset, Task<IResult?>> change, bool touch, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new CapaStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return Stale("CAPA");
        if (!states.Contains(row.Status))
            return Problem(StatusCodes.Status409Conflict, "WRONG_STATE", "Not possible now",
                $"This can only be done while the CAPA is in {string.Join(" or ", states)}; it is in {row.Status}.");
        // Actions default to the acting user as assignee, and the check records who performed it.
        await new EsigStore(conn, sqlTx).EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        await WorkflowTransitions.SetChangeReasonAsync(conn, sqlTx, ctx.ChangeReason ?? what, ct);
        if (await change(store, ctx, row, time.GetUtcNow()) is { } failed) return failed;
        if (touch && !await store.UpdateAsync(id, expected, new Dictionary<string, object?>(), ct)) return Stale("CAPA");
        var updated = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);
        SetETag(http, updated.RowVersion);
        return Results.Ok(CapaResponse.From(updated));
    }
}
