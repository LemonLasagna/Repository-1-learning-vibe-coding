using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>
/// Deviations (URS-QMS-003, FS §9.3.1). Conventions as the reference slice (doc 21): ETag/If-Match, reason for change
/// after submission, no DELETE. State changes only through POST …/transitions; those that need a signature go through
/// the e-signature service with the action attached (see <see cref="DeviationSignable"/>).
/// </summary>
public static class DeviationEndpoints
{
    private const string Base = "/api/qms/v1/deviations";
    private const int ListLimit = 500;

    public static void MapDeviationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/org/v1/sites", Sites);
        var group = app.MapGroup(Base);
        group.MapPost("", Create).RequireAuthorization(Permissions.DeviationWrite);
        group.MapGet("", List).RequireAuthorization(Permissions.DeviationRead);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.DeviationRead);
        // The permission to change a deviation depends on its state; checked in the handlers.
        group.MapPatch("{id:guid}", Update).RequireAuthorization(Permissions.DeviationRead);
        group.MapPost("{id:guid}/transitions", Transition).RequireAuthorization(Permissions.DeviationRead);
        group.MapGet("{id:guid}/history", History).RequireAuthorization(Permissions.DeviationRead);
        group.MapGet("{id:guid}/available-actions", Actions).RequireAuthorization(Permissions.DeviationRead);
    }

    private static async Task<IResult> Sites(PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sites = await Store(db).SitesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(sites.Select(s => new { s.Id, s.Code, s.Name, s.Timezone }));
    }

    private static async Task<IResult> Create(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, SignableRegistry registry, TimeProvider time,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var now = time.GetUtcNow();
        var allowed = DeviationWorkflow.Editable[DeviationWorkflow.Draft].Fields;
        var errors = new Dictionary<string, string[]>();
        var (columns, ext) = Parse(body, allowed, errors);
        foreach (var f in DeviationFields.All.Where(f => f.Required && allowed.Contains(f.Name) && !body.ContainsKey(f.Name)))
            errors.TryAdd(f.Name, ["Required."]);
        columns.TryAdd("is_planned", false);
        columns.TryAdd("discovered_at", now.ToUniversalTime());
        if (columns.GetValueOrDefault("discovered_at") is DateTimeOffset discovered)
            columns["due_date"] = DateOnly.FromDateTime(discovered.UtcDateTime).AddDays(DeviationWorkflow.DefaultDueDays);
        CheckDates(columns.GetValueOrDefault("occurred_at") as DateTimeOffset?, columns.GetValueOrDefault("discovered_at") as DateTimeOffset?, now, errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (store, esig, wf) = Stores(db);
        if (!await store.SiteExistsAsync((Guid)columns["site_id"]!, ct))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = ["Unknown or inactive site."] });

        await esig.EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        var entityDefinitionId = await esig.EnsureEntityDefinitionAsync(ctx, registry.Find("qms.deviation")!, ct);
        var definitionId = await wf.EnsureDefinitionAsync(ctx, DeviationWorkflow.Definition, "Deviation (standard)", entityDefinitionId, ct);
        var id = Guid.CreateVersion7();
        var number = await store.NextNumberAsync(ctx, now.UtcDateTime.Year, ct);
        var instance = await wf.StartInstanceAsync(ctx, definitionId, await wf.CurrentAsync(definitionId, DeviationWorkflow.Definition, ct), entityDefinitionId, number, id, now, ct);
        var extObject = new JsonObject();
        foreach (var (k, v) in ext.Where(e => e.Value is not null)) extObject[k] = v!.DeepClone();
        await store.InsertAsync(ctx, id, number, instance, DeviationWorkflow.Draft, columns, extObject, ct);
        await wf.AddHistoryAsync(ctx, instance, null, DeviationWorkflow.Draft, now, "Created", null, ct);
        var row = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Created($"{Base}/{id}", DeviationResponse.From(row));
    }

    private static async Task<IResult> List(string? status, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await Store(db).ListAsync(string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), ListLimit, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { items = rows.Select(DeviationResponse.From), truncated = rows.Count == ListLimit });
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await Store(db).GetAsync(id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        SetETag(http, row.RowVersion);
        return Results.Ok(DeviationResponse.From(row));
    }

    private static async Task<IResult> Update(
        Guid id, JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var store = Store(db);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return Stale("deviation");
        if (!DeviationWorkflow.Editable.TryGetValue(row.Status, out var rule))
            return Problem(StatusCodes.Status409Conflict, "READ_ONLY", "Read-only", $"A deviation in state {row.Status} cannot be changed.");
        if (!user.PermissionsOf().Contains(rule.Permission))
            return Problem(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed",
                $"Changing a deviation in state {row.Status} needs '{rule.Permission}'.");
        if (row.Status != DeviationWorkflow.Draft && ctx.ChangeReason is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["X-Change-Reason"] = ["A reason for change is required."] });

        var errors = new Dictionary<string, string[]>();
        var (columns, extChanges) = Parse(body, rule.Fields, errors);
        CheckDates(columns.GetValueOrDefault("occurred_at") as DateTimeOffset? ?? row.OccurredAt,
            columns.GetValueOrDefault("discovered_at") as DateTimeOffset? ?? row.DiscoveredAt, time.GetUtcNow(), errors);
        if (columns.TryGetValue("site_id", out var site) && !await store.SiteExistsAsync((Guid)site!, ct))
            errors["siteId"] = ["Unknown or inactive site."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        JsonObject? ext = null;
        if (extChanges.Count > 0)
        {
            ext = row.Ext;
            foreach (var (k, v) in extChanges) { if (v is null) ext.Remove(k); else ext[k] = v.DeepClone(); }
        }
        if (!await store.UpdateAsync(id, expected, columns, ext, ct)) return Stale("deviation");
        var updated = (await store.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, updated.RowVersion);
        return Results.Ok(DeviationResponse.From(updated));
    }

    private static async Task<IResult> Transition(
        Guid id, TransitionRequest body, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        gxp.Current is not { } ctx
            ? MissingIdentity()
            : await WorkflowTransitions.HandleAsync(DeviationEntity.Instance, id, body, db, ctx, time, user, http,
                async (conn, tx, rowId, c) => DeviationResponse.From((await new DeviationStore(conn, tx).GetAsync(rowId, false, c))!), ct);

    private static async Task<IResult> History(Guid id, PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (store, _, wf) = Stores(db);
        var row = await store.GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        var history = row.WorkflowInstanceId is { } instance ? await wf.HistoryAsync(instance, ct) : [];
        await tx.CommitAsync(ct);
        return Results.Ok(history);
    }

    private static async Task<IResult> Actions(Guid id, PlatformDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var row = await Store(db).GetAsync(id, forUpdate: false, ct);
        var definition = row is null ? null : await new WorkflowStore(conn, sqlTx).DefinitionOfAsync(row, DeviationEntity.Instance, ct);
        await tx.CommitAsync(ct);
        if (row is null) return Results.NotFound();
        var permissions = user.PermissionsOf();
        var editable = DeviationWorkflow.Editable.TryGetValue(row.Status, out var rule) && permissions.Contains(rule.Permission)
            ? rule.Fields.Order(StringComparer.Ordinal).ToArray()
            : [];
        return Results.Ok(new
        {
            state = row.Status,
            editableFields = editable,
            reasonForChangeRequired = row.Status != DeviationWorkflow.Draft,
            actions = WorkflowTransitions.Available(definition!, row, permissions),
        });
    }

    private static (Dictionary<string, object?> Columns, Dictionary<string, JsonNode?> Ext) Parse(
        JsonObject body, IReadOnlySet<string> allowed, Dictionary<string, string[]> errors)
    {
        var columns = new Dictionary<string, object?>();
        var ext = new Dictionary<string, JsonNode?>();
        foreach (var (name, node) in body)
        {
            var f = DeviationFields.Find(name);
            if (f is null) { errors[name] = ["Unknown field."]; continue; }
            if (!allowed.Contains(name)) { errors[name] = ["Cannot be changed in this state."]; continue; }
            if (!FieldParser.TryParse(f, node, out var value, out var error)) { errors[name] = [error!]; continue; }
            if (f.Column is not null) columns[f.Column] = value;
            else ext[f.ExtKey!] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return (columns, ext);
    }

    private static void CheckDates(DateTimeOffset? occurred, DateTimeOffset? discovered, DateTimeOffset now, Dictionary<string, string[]> errors)
    {
        if (occurred > now.AddMinutes(5)) errors.TryAdd("occurredAt", ["Cannot be in the future."]);
        if (discovered > now.AddMinutes(5)) errors.TryAdd("discoveredAt", ["Cannot be in the future."]);
        if (occurred > discovered) errors.TryAdd("occurredAt", ["Cannot be after the discovery."]);
    }

    private static DeviationStore Store(PlatformDbContext db) => Stores(db).Store;

    private static (DeviationStore Store, EsigStore Esig, WorkflowStore Wf) Stores(PlatformDbContext db)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        return (new DeviationStore(conn, tx), new EsigStore(conn, tx), new WorkflowStore(conn, tx));
    }
}
