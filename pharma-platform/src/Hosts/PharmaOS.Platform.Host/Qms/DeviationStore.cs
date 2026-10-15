using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.Qms;

public sealed record SiteRow(Guid Id, string Code, string Name, string Timezone);

/// <summary>SQL for qms.deviation, run in the caller's transaction (GxP context for RLS and the audit triggers).</summary>
public sealed class DeviationStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    public NpgsqlConnection Connection => conn;
    public NpgsqlTransaction Transaction => tx;

    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    private const string Select = """
        SELECT d.id, d.site_id, s.code, d.deviation_no, d.title, d.description, d.classification, d.deviation_type, d.is_planned,
               d.occurred_at, d.discovered_at, d.immediate_action, d.impact_assessment, d.root_cause_summary, d.risk_score,
               d.owner_id, o.display_name, d.qa_owner_id, q.display_name, d.due_date, d.closed_at, d.status, d.workflow_instance_id,
               d.ext::text, d.row_version, d.updated_at, d.updated_by,
               (SELECT count(*)::int FROM qms.capa c
                 WHERE c.source_type = 'DEVIATION' AND c.source_id = d.id AND c.status IN ('DRAFT', 'PLAN_REVIEW'))
          FROM qms.deviation d
          JOIN org.site s ON s.id = d.site_id
          LEFT JOIN iam.app_user o ON o.id = d.owner_id
          LEFT JOIN iam.app_user q ON q.id = d.qa_owner_id
        """;

    public async Task<IReadOnlyList<SiteRow>> SitesAsync(CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT id, code, name, timezone FROM org.site WHERE status = 'ACTIVE' ORDER BY code");
        var list = new List<SiteRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new SiteRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public async Task<bool> SiteExistsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM org.site WHERE id = @id AND status = 'ACTIVE'");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public Task<string> NextNumberAsync(GxpContext ctx, int year, CancellationToken ct) =>
        NumberingStore.NextAsync(conn, tx, ctx, "DEVIATION", "DEV", year, ct);

    public async Task InsertAsync(
        GxpContext ctx, Guid id, string number, Guid workflowInstanceId, string status, IReadOnlyDictionary<string, object?> columns,
        JsonObject ext, CancellationToken ct)
    {
        var names = new List<string> { "id", "tenant_id", "deviation_no", "owner_id", "status", "workflow_state", "workflow_instance_id",
            "recurrence_flag", "ext", "created_by", "updated_by" };
        await using var cmd = Cmd("");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant_id", ctx.TenantId);
        cmd.Parameters.AddWithValue("deviation_no", number);
        cmd.Parameters.AddWithValue("owner_id", ctx.UserId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("workflow_state", status);
        cmd.Parameters.AddWithValue("workflow_instance_id", workflowInstanceId);
        cmd.Parameters.AddWithValue("recurrence_flag", false);
        cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb) { Value = ext.ToJsonString() });
        cmd.Parameters.AddWithValue("created_by", ctx.UserId);
        cmd.Parameters.AddWithValue("updated_by", ctx.UserId);
        foreach (var (column, value) in columns)
        {
            names.Add(column);
            cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        }
        cmd.CommandText = $"INSERT INTO qms.deviation ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select(n => "@" + n))})";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DeviationRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(Select + " WHERE d.id = @id" + (forUpdate ? " FOR UPDATE OF d" : ""));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<DeviationRow>> ListAsync(string? status, int limit, CancellationToken ct)
    {
        await using var cmd = Cmd(Select + (status is null ? "" : " WHERE d.status = @status") + " ORDER BY d.deviation_no DESC LIMIT @limit");
        if (status is not null) cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<DeviationRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>Optimistic concurrency: false when the row is no longer at <paramref name="expectedVersion"/>.</summary>
    public async Task<bool> UpdateAsync(
        Guid id, int expectedVersion, IReadOnlyDictionary<string, object?> columns, JsonObject? ext, CancellationToken ct)
    {
        await using var cmd = Cmd("");
        var sets = new List<string>();
        foreach (var (column, value) in columns)
        {
            sets.Add($"{column} = @{column}");
            cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        }
        if (ext is not null)
        {
            sets.Add("ext = @ext");
            cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb) { Value = ext.ToJsonString() });
        }
        if (sets.Count == 0) return true;
        cmd.CommandText = $"UPDATE qms.deviation SET {string.Join(", ", sets)} WHERE id = @__id AND row_version = @__version";
        cmd.Parameters.AddWithValue("__id", id);
        cmd.Parameters.AddWithValue("__version", expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static DeviationRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetBoolean(8), r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10), Str(r, 11), Str(r, 12), Str(r, 13),
        r.IsDBNull(14) ? null : r.GetInt32(14), r.GetGuid(15), Str(r, 16), r.IsDBNull(17) ? null : r.GetGuid(17), Str(r, 18),
        r.GetFieldValue<DateOnly>(19), r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20), r.GetString(21),
        r.IsDBNull(22) ? null : r.GetGuid(22), r.GetString(23), r.GetInt32(24), r.GetFieldValue<DateTimeOffset>(25), r.GetGuid(26),
        r.GetInt32(27));

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
