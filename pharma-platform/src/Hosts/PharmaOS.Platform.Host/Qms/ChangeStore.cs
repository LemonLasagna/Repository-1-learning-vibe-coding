using Npgsql;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>SQL for qms.change_control, its impact assessments and its tasks, in the caller's transaction.</summary>
public sealed class ChangeStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    private const string Select = """
        SELECT c.id, c.cc_no, c.site_id, s.code, c.title, c.description, c.justification, c.change_type, c.change_category,
               c.is_temporary, c.temporary_end_date, c.regulatory_impact, c.implementation_due, c.effective_date, c.owner_id,
               o.display_name, c.status, c.updated_at, c.row_version, c.updated_by, c.workflow_instance_id
          FROM qms.change_control c
          JOIN org.site s ON s.id = c.site_id
          LEFT JOIN iam.app_user o ON o.id = c.owner_id
        """;

    public Task<string> NextNumberAsync(GxpContext ctx, int year, CancellationToken ct) =>
        NumberingStore.NextAsync(conn, tx, ctx, "CHANGE", "CC", year, ct);

    public async Task<ChangeRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        ChangeRow? row;
        await using (var cmd = Cmd(Select + " WHERE c.id = @id" + (forUpdate ? " FOR UPDATE OF c" : "")))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            row = await r.ReadAsync(ct) ? ReadHeader(r) : null;
        }
        if (row is null) return null;
        return row with { Impacts = await ImpactsAsync(id, ct), Tasks = await TasksAsync(id, ct), LinkedActions = await LinkedActionsAsync(id, ct) };
    }

    public async Task<IReadOnlyList<ChangeRow>> ListAsync(string? status, int limit, CancellationToken ct)
    {
        await using var cmd = Cmd("");
        var where = "";
        if (status is not null) { where = " WHERE c.status = @status"; cmd.Parameters.AddWithValue("status", status); }
        cmd.CommandText = Select + where + " ORDER BY c.cc_no DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<ChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadHeader(r));
        return list;
    }

    public async Task InsertAsync(GxpContext ctx, Guid id, string number, Guid instanceId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(columns)
        {
            ["id"] = id, ["tenant_id"] = ctx.TenantId, ["cc_no"] = number, ["owner_id"] = ctx.UserId, ["status"] = ChangeWorkflow.Draft,
            ["workflow_state"] = ChangeWorkflow.Draft, ["workflow_instance_id"] = instanceId, ["created_by"] = ctx.UserId, ["updated_by"] = ctx.UserId,
        };
        values.TryAdd("is_temporary", false);
        await InsertRowAsync("qms.change_control", values, ct);
    }

    /// <summary>Optimistic concurrency on the change row. With no columns it only advances row_version, so the ETag
    /// changes whenever an assessment or a task changes.</summary>
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
        cmd.CommandText = $"UPDATE qms.change_control SET {string.Join(", ", sets)} WHERE id = @__id AND row_version = @__version";
        cmd.Parameters.AddWithValue("__id", id);
        cmd.Parameters.AddWithValue("__version", expectedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>One assessment per impacted area; assessing again replaces it.</summary>
    public async Task SaveImpactAsync(GxpContext ctx, Guid changeId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var exists = Cmd("SELECT id FROM qms.change_impact_assessment WHERE change_control_id = @change AND impacted_area = @area");
        exists.Parameters.AddWithValue("change", changeId);
        exists.Parameters.AddWithValue("area", columns["impacted_area"]!);
        var values = new Dictionary<string, object?>(columns) { ["assessor_id"] = ctx.UserId, ["updated_by"] = ctx.UserId };
        values.TryAdd("action_required", null);
        if (await exists.ExecuteScalarAsync(ct) is Guid id)
        {
            await using var cmd = Cmd($"UPDATE qms.change_impact_assessment SET {string.Join(", ", values.Keys.Select(c => $"{c} = @{c}"))} WHERE id = @__id");
            foreach (var (column, value) in values) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("__id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }
        values["tenant_id"] = ctx.TenantId;
        values["change_control_id"] = changeId;
        values["created_by"] = ctx.UserId;
        await InsertRowAsync("qms.change_impact_assessment", values, ct);
    }

    public async Task<bool> DeleteImpactAsync(Guid changeId, string area, CancellationToken ct)
    {
        await using var cmd = Cmd("DELETE FROM qms.change_impact_assessment WHERE change_control_id = @change AND impacted_area = @area");
        cmd.Parameters.AddWithValue("change", changeId);
        cmd.Parameters.AddWithValue("area", area);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> InsertTaskAsync(GxpContext ctx, Guid changeId, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var next = Cmd("SELECT coalesce(max(task_no), 0) + 1 FROM qms.change_task WHERE change_control_id = @change");
        next.Parameters.AddWithValue("change", changeId);
        var no = (int)(await next.ExecuteScalarAsync(ct))!;
        var values = new Dictionary<string, object?>(columns)
        {
            ["tenant_id"] = ctx.TenantId, ["change_control_id"] = changeId, ["task_no"] = no, ["status"] = ChangeTaskStatus.Planned,
            ["created_by"] = ctx.UserId, ["updated_by"] = ctx.UserId,
        };
        await InsertRowAsync("qms.change_task", values, ct);
        return no;
    }

    public async Task<bool> UpdateTaskAsync(Guid changeId, int no, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        if (columns.Count == 0) return true;
        await using var cmd = Cmd($"UPDATE qms.change_task SET {string.Join(", ", columns.Keys.Select(c => $"{c} = @{c}"))} WHERE change_control_id = @__change AND task_no = @__no");
        foreach (var (column, value) in columns) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("__change", changeId);
        cmd.Parameters.AddWithValue("__no", no);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SiteExistsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM org.site WHERE id = @id AND status = 'ACTIVE'");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> UserExistsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM iam.app_user WHERE id = @id AND status = 'ACTIVE'");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<IReadOnlyList<ChangeImpactRow>> ImpactsAsync(Guid changeId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT i.impacted_area, i.assessor_id, u.display_name, i.is_impacted, i.assessment, i.action_required, i.updated_at
              FROM qms.change_impact_assessment i LEFT JOIN iam.app_user u ON u.id = i.assessor_id
             WHERE i.change_control_id = @change ORDER BY i.impacted_area
            """);
        cmd.Parameters.AddWithValue("change", changeId);
        var list = new List<ChangeImpactRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ChangeImpactRow(r.GetString(0), r.GetGuid(1), Str(r, 2), r.GetBoolean(3), Str(r, 4), Str(r, 5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    private async Task<IReadOnlyList<ChangeTaskRow>> TasksAsync(Guid changeId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT t.task_no, t.phase, t.description, t.assignee_id, u.display_name, t.due_date, t.status, t.completed_at
              FROM qms.change_task t LEFT JOIN iam.app_user u ON u.id = t.assignee_id
             WHERE t.change_control_id = @change ORDER BY t.task_no
            """);
        cmd.Parameters.AddWithValue("change", changeId);
        var list = new List<ChangeTaskRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ChangeTaskRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetGuid(3), Str(r, 4), r.GetFieldValue<DateOnly>(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    private async Task<IReadOnlyList<ChangeLinkedAction>> LinkedActionsAsync(Guid changeId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT c.id, c.capa_no, a.action_no, a.description, a.status
              FROM qms.capa_action a JOIN qms.capa c ON c.id = a.capa_id
             WHERE a.change_control_id = @change ORDER BY c.capa_no, a.action_no
            """);
        cmd.Parameters.AddWithValue("change", changeId);
        var list = new List<ChangeLinkedAction>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ChangeLinkedAction(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>Column names come from the field specs in code, never from the request.</summary>
    private async Task InsertRowAsync(string table, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        await using var cmd = Cmd($"INSERT INTO {table} ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Keys.Select(k => "@" + k))})");
        foreach (var (column, value) in values) cmd.Parameters.AddWithValue(column, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ChangeRow ReadHeader(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8), r.GetBoolean(9), Date(r, 10), Str(r, 11), Date(r, 12), Date(r, 13), r.GetGuid(14), Str(r, 15), r.GetString(16),
        r.GetFieldValue<DateTimeOffset>(17), r.GetInt32(18), r.GetGuid(19), r.IsDBNull(20) ? null : r.GetGuid(20), [], [], []);

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateOnly? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
}
