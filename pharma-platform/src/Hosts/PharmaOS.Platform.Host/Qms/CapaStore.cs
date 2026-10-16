using Npgsql;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>SQL for qms.capa and its actions and effectiveness check, in the caller's transaction.</summary>
public sealed class CapaStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    private const string Select = """
        SELECT c.id, c.capa_no, c.site_id, s.code, c.source_type, c.source_id, d.deviation_no, c.title, c.problem_statement, c.capa_type,
               c.priority, c.owner_id, o.display_name, c.due_date, c.status, c.closed_at, c.workflow_instance_id, c.row_version,
               c.updated_at, c.updated_by
          FROM qms.capa c
          JOIN org.site s ON s.id = c.site_id
          LEFT JOIN iam.app_user o ON o.id = c.owner_id
          LEFT JOIN qms.deviation d ON c.source_type = 'DEVIATION' AND d.id = c.source_id
        """;

    public Task<string> NextNumberAsync(GxpContext ctx, int year, CancellationToken ct) =>
        NumberingStore.NextAsync(conn, tx, ctx, "CAPA", "CAPA", year, ct);

    public async Task<CapaRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        CapaRow? row;
        await using (var cmd = Cmd(Select + " WHERE c.id = @id" + (forUpdate ? " FOR UPDATE OF c" : "")))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            row = await r.ReadAsync(ct) ? ReadHeader(r) : null;
        }
        if (row is null) return null;
        return row with { Actions = await ActionsAsync(id, ct), Effectiveness = await EffectivenessAsync(id, ct) };
    }

    public async Task<IReadOnlyList<CapaRow>> ListAsync(Guid? sourceId, string? status, int limit, CancellationToken ct)
    {
        var where = new List<string>();
        await using var cmd = Cmd("");
        if (sourceId is { } s) { where.Add("c.source_id = @source"); cmd.Parameters.AddWithValue("source", s); }
        if (status is not null) { where.Add("c.status = @status"); cmd.Parameters.AddWithValue("status", status); }
        cmd.CommandText = Select + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY c.capa_no DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<CapaRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadHeader(r));
        return list;
    }

    public async Task InsertAsync(GxpContext ctx, Guid id, string number, Guid instanceId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(columns)
        {
            ["id"] = id, ["tenant_id"] = ctx.TenantId, ["capa_no"] = number, ["owner_id"] = ctx.UserId, ["status"] = CapaWorkflow.Draft,
            ["workflow_state"] = CapaWorkflow.Draft, ["workflow_instance_id"] = instanceId, ["created_by"] = ctx.UserId, ["updated_by"] = ctx.UserId,
        };
        await InsertRowAsync("qms.capa", values, ct);
    }

    /// <summary>Optimistic concurrency on the CAPA row. With no columns it only advances row_version, so the ETag
    /// changes whenever an action or the effectiveness check changes.</summary>
    public async Task<bool> UpdateAsync(Guid id, int expectedVersion, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var cmd = Cmd("");
        var sets = new List<string>();
        foreach (var (column, value) in columns)
        {
            sets.Add($"{column} = @{column}");
            cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        }
        if (sets.Count == 0) sets.Add("status = status");
        cmd.CommandText = $"UPDATE qms.capa SET {string.Join(", ", sets)} WHERE id = @__id AND row_version = @__version";
        cmd.Parameters.AddWithValue("__id", id);
        cmd.Parameters.AddWithValue("__version", expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> InsertActionAsync(GxpContext ctx, Guid capaId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var next = Cmd("SELECT coalesce(max(action_no), 0) + 1 FROM qms.capa_action WHERE capa_id = @capa");
        next.Parameters.AddWithValue("capa", capaId);
        var no = (int)(await next.ExecuteScalarAsync(ct))!;
        var values = new Dictionary<string, object?>(columns)
        {
            ["tenant_id"] = ctx.TenantId, ["capa_id"] = capaId, ["action_no"] = no, ["status"] = CapaActionStatus.Planned,
            ["created_by"] = ctx.UserId, ["updated_by"] = ctx.UserId,
        };
        await InsertRowAsync("qms.capa_action", values, ct);
        return no;
    }

    public async Task<bool> UpdateActionAsync(Guid capaId, int no, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        if (columns.Count == 0) return true;
        await using var cmd = Cmd("");
        cmd.CommandText = $"UPDATE qms.capa_action SET {string.Join(", ", columns.Keys.Select(c => $"{c} = @{c}"))} WHERE capa_id = @__capa AND action_no = @__no";
        foreach (var (column, value) in columns) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("__capa", capaId);
        cmd.Parameters.AddWithValue("__no", no);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>One effectiveness check per CAPA: defined while planning, result recorded later.</summary>
    public async Task SaveEffectivenessAsync(GxpContext ctx, Guid capaId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var exists = Cmd("SELECT id FROM qms.capa_effectiveness_check WHERE capa_id = @capa");
        exists.Parameters.AddWithValue("capa", capaId);
        if (await exists.ExecuteScalarAsync(ct) is Guid id)
        {
            await using var cmd = Cmd($"UPDATE qms.capa_effectiveness_check SET {string.Join(", ", columns.Keys.Select(c => $"{c} = @{c}"))} WHERE id = @__id");
            foreach (var (column, value) in columns) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("__id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }
        var values = new Dictionary<string, object?>(columns)
            { ["tenant_id"] = ctx.TenantId, ["capa_id"] = capaId, ["created_by"] = ctx.UserId, ["updated_by"] = ctx.UserId };
        await InsertRowAsync("qms.capa_effectiveness_check", values, ct);
    }

    public async Task<bool> UserExistsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM iam.app_user WHERE id = @id AND status = 'ACTIVE'");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<IReadOnlyList<CapaActionRow>> ActionsAsync(Guid capaId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT a.action_no, a.action_type, a.description, a.assignee_id, u.display_name, a.due_date, a.status, a.completed_at, a.evidence,
                   a.change_control_id, cc.cc_no
              FROM qms.capa_action a
              LEFT JOIN iam.app_user u ON u.id = a.assignee_id
              LEFT JOIN qms.change_control cc ON cc.id = a.change_control_id
             WHERE a.capa_id = @capa ORDER BY a.action_no
            """);
        cmd.Parameters.AddWithValue("capa", capaId);
        var list = new List<CapaActionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CapaActionRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetGuid(3), Str(r, 4), r.GetFieldValue<DateOnly>(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), Str(r, 8),
                r.IsDBNull(9) ? null : r.GetGuid(9), Str(r, 10)));
        }
        return list;
    }

    private async Task<CapaEffectivenessRow?> EffectivenessAsync(Guid capaId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT e.id, e.criteria, e.method, e.planned_date, e.performed_at, e.result, e.evidence, u.display_name
              FROM qms.capa_effectiveness_check e LEFT JOIN iam.app_user u ON u.id = e.performed_by
             WHERE e.capa_id = @capa
            """);
        cmd.Parameters.AddWithValue("capa", capaId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new CapaEffectivenessRow(r.GetGuid(0), r.GetString(1), Str(r, 2), r.GetFieldValue<DateOnly>(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), Str(r, 5), Str(r, 6), Str(r, 7));
    }

    /// <summary>Column names come from the field specs in code, never from the request.</summary>
    private async Task InsertRowAsync(string table, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        await using var cmd = Cmd($"INSERT INTO {table} ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Keys.Select(k => "@" + k))})");
        foreach (var (column, value) in values) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CapaRow ReadHeader(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5), Str(r, 6),
        r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetGuid(11), Str(r, 12), r.GetFieldValue<DateOnly>(13),
        r.GetString(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15), r.IsDBNull(16) ? null : r.GetGuid(16),
        r.GetInt32(17), r.GetFieldValue<DateTimeOffset>(18), r.GetGuid(19), [], null);

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
