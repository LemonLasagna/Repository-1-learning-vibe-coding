using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Qms;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.Work;

/// <summary>How the user takes part: they do the work, they sign it, or it waits on something else first.</summary>
public static class InboxKind
{
    public const string Do = "DO";
    public const string Sign = "SIGN";
    public const string Waiting = "WAITING";
}

public sealed record InboxItem(
    string Entity, Guid RecordId, string Number, string Title, string State, string Task, string Kind, string? Detail, DateOnly? DueDate,
    bool Overdue);

/// <summary>Collects the items one record type puts in the signed-in user's inbox.</summary>
public interface IInboxSource
{
    Task<IReadOnlyList<InboxItem>> ItemsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid me, IReadOnlySet<string> permissions,
        DateOnly today, CancellationToken ct);
}

/// <summary>
/// "My work": everything waiting on the signed-in user that they may do now (FS-WF task list). Who is expected to act
/// follows the workflows: owners for drafts and investigations, assignees for actions, QA for triage, reviews and
/// approvals. Items the user could not sign because of segregation of duties are left out.
/// </summary>
public static class InboxEndpoints
{
    public static void MapInboxEndpoints(this IEndpointRouteBuilder app) => app.MapGet("/api/work/v1/inbox", Inbox);

    private static async Task<IResult> Inbox(
        PlatformDbContext db, IGxpContextAccessor gxp, IEnumerable<IInboxSource> sources, TimeProvider time, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var permissions = user.PermissionsOf();
        var items = new List<InboxItem>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var (conn, sqlTx) = Sql(db);
            foreach (var source in sources) items.AddRange(await source.ItemsAsync(conn, sqlTx, ctx.UserId, permissions, today, ct));
            await tx.CommitAsync(ct);
        }
        var ordered = items
            .OrderByDescending(i => i.Overdue)
            .ThenBy(i => i.Kind == InboxKind.Waiting)
            .ThenBy(i => i.DueDate ?? DateOnly.MaxValue)
            .ThenBy(i => i.Number, StringComparer.Ordinal)
            .ToList();
        return Results.Ok(new
        {
            items = ordered,
            counts = new { total = ordered.Count, overdue = ordered.Count(i => i.Overdue), actionable = ordered.Count(i => i.Kind != InboxKind.Waiting) },
        });
    }

    public static InboxItem Item(string entity, Guid id, string number, string title, string state, string task, string kind,
        string? detail, DateOnly? due, DateOnly today) =>
        new(entity, id, number, title, state, task, kind, detail, due, due is { } d && d < today);
}

public sealed class DeviationInbox : IInboxSource
{
    public async Task<IReadOnlyList<InboxItem>> ItemsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid me, IReadOnlySet<string> permissions,
        DateOnly today, CancellationToken ct)
    {
        var write = permissions.Contains(Permissions.DeviationWrite);
        var triage = permissions.Contains(Permissions.DeviationTriage);
        var approve = permissions.Contains(Permissions.DeviationApprove);
        if (!permissions.Contains(Permissions.DeviationRead) || !(write || triage || approve)) return [];

        await using var cmd = new NpgsqlCommand("""
            SELECT d.id, d.deviation_no, d.title, d.status, d.due_date, d.owner_id, d.updated_by,
                   (SELECT count(*)::int FROM qms.capa c
                     WHERE c.source_type = 'DEVIATION' AND c.source_id = d.id AND c.status IN ('DRAFT', 'PLAN_REVIEW')),
                   EXISTS (SELECT 1 FROM esig.signature s
                             JOIN esig.signature_meaning m ON m.id = s.meaning_id
                             JOIN meta.entity_definition ed ON ed.id = s.entity_definition_id
                            WHERE ed.code = 'qms.deviation' AND s.record_id = d.id AND s.signer_id = @me
                              AND m.code IN ('AUTHORED', 'REVIEWED'))
              FROM qms.deviation d
             WHERE (d.status IN ('DRAFT', 'INVESTIGATION') AND d.owner_id = @me AND @write)
                OR (d.status = 'TRIAGE' AND @triage)
                OR (d.status = 'QA_APPROVAL' AND @approve)
            """, conn, tx);
        cmd.Parameters.AddWithValue("me", me);
        cmd.Parameters.AddWithValue("write", write);
        cmd.Parameters.AddWithValue("triage", triage);
        cmd.Parameters.AddWithValue("approve", approve);
        var items = new List<InboxItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var (id, number, title, state, due) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4));
            InboxItem Item(string task, string kind, string? detail = null) =>
                InboxEndpoints.Item("qms.deviation", id, number, title, state, task, kind, detail, due, today);
            switch (state)
            {
                case DeviationWorkflow.Draft:
                    items.Add(Item("Complete and submit", InboxKind.Sign));
                    break;
                case DeviationWorkflow.Investigation:
                    items.Add(Item("Investigate and submit for QA approval", InboxKind.Do));
                    break;
                case DeviationWorkflow.Triage:
                    items.Add(Item("Triage", InboxKind.Do));
                    break;
                case DeviationWorkflow.QaApproval:
                    // Segregation of duties: not for the last editor, nor for an author or reviewer of the deviation.
                    if (r.GetGuid(6) == me || r.GetBoolean(8)) break;
                    var openCapas = r.GetInt32(7);
                    items.Add(openCapas > 0
                        ? Item("Approve and close", InboxKind.Waiting, $"Waiting for {openCapas} linked CAPA plan(s) to be approved")
                        : Item("Approve and close", InboxKind.Sign));
                    break;
            }
        }
        return items;
    }
}

public sealed class CapaInbox : IInboxSource
{
    public async Task<IReadOnlyList<InboxItem>> ItemsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid me, IReadOnlySet<string> permissions,
        DateOnly today, CancellationToken ct)
    {
        var write = permissions.Contains(Permissions.CapaWrite);
        var approve = permissions.Contains(Permissions.CapaApprove);
        if (!permissions.Contains(Permissions.CapaRead) || !(write || approve)) return [];
        var items = new List<InboxItem>();

        await using (var cmd = new NpgsqlCommand("""
            SELECT c.id, c.capa_no, c.title, c.status, c.due_date, c.owner_id, c.updated_by,
                   (SELECT count(*)::int FROM qms.capa_action a WHERE a.capa_id = c.id AND a.status = 'PLANNED'),
                   e.result IS NOT NULL, e.planned_date
              FROM qms.capa c
              LEFT JOIN qms.capa_effectiveness_check e ON e.capa_id = c.id
             WHERE (c.status IN ('DRAFT', 'EFFECTIVENESS_CHECK') AND c.owner_id = @me AND @write)
                OR (c.status IN ('PLAN_REVIEW', 'IMPLEMENTATION', 'EFFECTIVENESS_CHECK') AND @approve)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("me", me);
            cmd.Parameters.AddWithValue("write", write);
            cmd.Parameters.AddWithValue("approve", approve);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (id, number, title, state, due) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4));
                var owner = r.GetGuid(5) == me;
                // The verifier and approver must not be the last editor (second person).
                var lastEditor = r.GetGuid(6) == me;
                var openActions = r.GetInt32(7);
                var resultRecorded = !r.IsDBNull(8) && r.GetBoolean(8);
                DateOnly? checkDate = r.IsDBNull(9) ? null : r.GetFieldValue<DateOnly>(9);
                InboxItem Item(string task, string kind, string? detail = null, DateOnly? dueDate = null) =>
                    InboxEndpoints.Item("qms.capa", id, number, title, state, task, kind, detail, dueDate ?? due, today);
                switch (state)
                {
                    case CapaWorkflow.Draft when owner && write:
                        items.Add(Item("Plan the actions and effectiveness check, then submit", InboxKind.Do));
                        break;
                    case CapaWorkflow.PlanReview when approve && !lastEditor:
                        items.Add(Item("Review and approve the plan", InboxKind.Sign));
                        break;
                    case CapaWorkflow.Implementation when approve && !lastEditor && openActions == 0:
                        items.Add(Item("Verify implementation", InboxKind.Sign));
                        break;
                    case CapaWorkflow.EffectivenessCheck when owner && write && !resultRecorded:
                        items.Add(Item("Record the effectiveness check result", InboxKind.Do, $"Planned for {checkDate:yyyy-MM-dd}", checkDate));
                        break;
                    case CapaWorkflow.EffectivenessCheck when approve && !lastEditor && resultRecorded:
                        items.Add(Item("Verify effectiveness and close", InboxKind.Sign));
                        break;
                }
            }
        }

        if (write)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT c.id, c.capa_no, c.title, c.status, a.action_no, a.description, a.due_date
                  FROM qms.capa_action a JOIN qms.capa c ON c.id = a.capa_id
                 WHERE a.assignee_id = @me AND a.status = 'PLANNED' AND c.status = 'IMPLEMENTATION'
                """, conn, tx);
            cmd.Parameters.AddWithValue("me", me);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var description = r.GetString(5);
                items.Add(InboxEndpoints.Item("qms.capa", r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    $"Complete action {r.GetInt32(4)}", InboxKind.Do, description.Length > 120 ? description[..117] + "…" : description,
                    r.GetFieldValue<DateOnly>(6), today));
            }
        }
        return items;
    }
}
