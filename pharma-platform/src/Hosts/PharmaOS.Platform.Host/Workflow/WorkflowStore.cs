using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Workflow;

public sealed record HistoryEntry(
    DateTimeOffset OccurredAt, string? FromState, string ToState, Guid? ActorId, string? ActorName, string? Comment,
    Guid? SignatureId, string? SignatureMeaning);

/// <summary>
/// Process definitions, instances and history in the wf schema. Definitions are code-defined for now; their JSON is
/// stored so each instance records which definition and version it ran under (FS-WF-007).
/// </summary>
public sealed class WorkflowStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    public async Task<Guid> EnsureDefinitionAsync(GxpContext ctx, WorkflowDefinition def, string name, Guid entityDefinitionId, CancellationToken ct)
    {
        await using (var cmd = Cmd("""
            INSERT INTO wf.process_definition (tenant_id, code, name, entity_definition_id, bpmn_xml, model_json, status, is_gxp, ext, created_by, updated_by)
            VALUES (@tenant, @code, @name, @ed, '', @model, 'ACTIVE', true, @ext, @user, @user)
            ON CONFLICT (tenant_id, code) DO UPDATE SET model_json = EXCLUDED.model_json, ext = EXCLUDED.ext
             WHERE coalesce((wf.process_definition.ext->>'version')::int, 0) < @version
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("code", def.Code);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("ed", entityDefinitionId);
            cmd.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Jsonb) { Value = ModelObject(def).ToJsonString() });
            cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb) { Value = $$"""{"source":"code","version":{{def.Version}}}""" });
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            cmd.Parameters.AddWithValue("version", def.Version);
            // A newer release upgrades the stored definition (audited); running instances keep the version they started with.
            await WorkflowTransitions.SetChangeReasonAsync(conn, tx, $"Workflow {def.Code} v{def.Version} from the release", ct);
            await cmd.ExecuteNonQueryAsync(ct);
            await WorkflowTransitions.SetChangeReasonAsync(conn, tx, ctx.ChangeReason, ct);
        }
        await using var get = Cmd("SELECT id FROM wf.process_definition WHERE code = @code");
        get.Parameters.AddWithValue("code", def.Code);
        var definitionId = (Guid)(await get.ExecuteScalarAsync(ct))!;
        if (await WriteVersionAsync(ctx, definitionId, def, ct))
            await CancelDraftsAsync(ctx, def, ct);
        return definitionId;
    }

    /// <summary>
    /// A release from code takes its version number, so a draft that was aiming for the same number can no longer be activated.
    /// It is cancelled (audited) rather than left to fail at activation. An already active authored version is not touched.
    /// </summary>
    private async Task CancelDraftsAsync(GxpContext ctx, WorkflowDefinition def, CancellationToken ct)
    {
        await WorkflowTransitions.SetChangeReasonAsync(conn, tx, $"Workflow {def.Code} v{def.Version} released from code", ct);
        await using (var cmd = Cmd("""
            UPDATE wf.workflow_draft SET status = 'CANCELLED', decision_comment = @comment
             WHERE code = @code AND version_no = @version AND status NOT IN ('ACTIVE', 'CANCELLED')
            """))
        {
            cmd.Parameters.AddWithValue("code", def.Code);
            cmd.Parameters.AddWithValue("version", def.Version);
            cmd.Parameters.AddWithValue("comment", $"Version {def.Version} was released from code.");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await WorkflowTransitions.SetChangeReasonAsync(conn, tx, ctx.ChangeReason, ct);
    }

    /// <summary>
    /// The definition new records start on: the newest released version, which is an activated authored version when one is newer
    /// than the code's release. Falls back to <paramref name="release"/> when nothing is stored.
    /// </summary>
    public async Task<WorkflowDefinition> CurrentAsync(Guid definitionId, WorkflowDefinition release, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT snapshot::text FROM wf.process_definition_version
             WHERE entity_id = @id ORDER BY version_no DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("id", definitionId);
        return await cmd.ExecuteScalarAsync(ct) is string snapshot ? FromModel(JsonNode.Parse(snapshot)!.AsObject()) : release;
    }

    /// <summary>
    /// Stores version N of a definition the first time it is released. A released version never changes (URS-WF-005):
    /// releasing different content under the same number is refused, so a change needs a new version number.
    /// </summary>
    /// <returns>True when the version was stored now, false when the same content was already stored.</returns>
    public async Task<bool> WriteVersionAsync(GxpContext ctx, Guid definitionId, WorkflowDefinition def, CancellationToken ct)
    {
        var model = ModelObject(def);
        var hash = CanonicalJson.Sha256Hex(model);
        await using (var check = Cmd("SELECT snapshot_hash FROM wf.process_definition_version WHERE entity_id = @id AND version_no = @version"))
        {
            check.Parameters.AddWithValue("id", definitionId);
            check.Parameters.AddWithValue("version", def.Version);
            if (await check.ExecuteScalarAsync(ct) is string released)
            {
                if (released != hash)
                    throw new InvalidOperationException(
                        $"Workflow {def.Code} version {def.Version} is already released with different content. A changed workflow needs a new version number.");
                return false;
            }
        }
        await using var insert = Cmd("""
            INSERT INTO wf.process_definition_version (tenant_id, entity_id, version_no, version_label, lifecycle_status, snapshot,
                   snapshot_hash, effective_from, created_by)
            VALUES (@tenant, @id, @version, @label, 'EFFECTIVE', @snapshot, @hash, now(), @user)
            """);
        insert.Parameters.AddWithValue("tenant", ctx.TenantId);
        insert.Parameters.AddWithValue("id", definitionId);
        insert.Parameters.AddWithValue("version", def.Version);
        insert.Parameters.AddWithValue("label", def.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Jsonb) { Value = model.ToJsonString() });
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("user", ctx.UserId);
        await insert.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>The definition an instance started on, as stored in its version (URS-WF-005).</summary>
    public async Task<WorkflowDefinition> PinnedAsync(Guid instanceId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT v.snapshot::text
              FROM wf.process_instance i
              JOIN wf.process_definition_version v ON v.entity_id = i.process_definition_id AND v.version_no = i.definition_version::int
             WHERE i.id = @id
            """);
        cmd.Parameters.AddWithValue("id", instanceId);
        var snapshot = await cmd.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"No released version is stored for workflow instance {instanceId}.");
        return FromModel(JsonNode.Parse(snapshot)!.AsObject());
    }

    /// <summary>
    /// The definition that governs a record: the version its instance started on. A record without an instance (not
    /// created through the workflow) can only use the code definition.
    /// </summary>
    public Task<WorkflowDefinition> DefinitionOfAsync(IWorkflowRecord row, IWorkflowEntity entity, CancellationToken ct) =>
        row.WorkflowInstanceId is { } instance ? PinnedAsync(instance, ct) : Task.FromResult(entity.Definition);

    public async Task<Guid> StartInstanceAsync(
        GxpContext ctx, Guid definitionId, WorkflowDefinition def, Guid entityDefinitionId, string businessKey, Guid recordId,
        DateTimeOffset at, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = Cmd("""
            INSERT INTO wf.process_instance (id, tenant_id, process_definition_id, definition_version, entity_definition_id, business_key,
                   record_id, status, started_at, started_by, created_by, updated_by)
            VALUES (@id, @tenant, @def, @version, @ed, @key, @record, 'ACTIVE', @at, @user, @user, @user)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("def", definitionId);
        cmd.Parameters.AddWithValue("version", def.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("ed", entityDefinitionId);
        cmd.Parameters.AddWithValue("key", businessKey);
        cmd.Parameters.AddWithValue("record", recordId);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task AddHistoryAsync(
        GxpContext ctx, Guid instanceId, string? from, string to, DateTimeOffset at, string? comment, Guid? signatureId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO wf.process_history (tenant_id, process_instance_id, from_node_key, to_node_key, actor_id, occurred_at, comment,
                   signature_id, created_by, updated_by)
            VALUES (@tenant, @instance, @from, @to, @user, @at, @comment, @sig, @user, @user)
            """);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("instance", instanceId);
        cmd.Parameters.AddWithValue("from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sig", (object?)signatureId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteInstanceAsync(Guid instanceId, string status, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = Cmd("UPDATE wf.process_instance SET status = @status, completed_at = @at WHERE id = @id");
        cmd.Parameters.AddWithValue("id", instanceId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid instanceId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT h.occurred_at, h.from_node_key, h.to_node_key, h.actor_id, u.display_name, h.comment, h.signature_id, s.meaning_label
              FROM wf.process_history h
              LEFT JOIN iam.app_user u ON u.id = h.actor_id
              LEFT JOIN esig.signature s ON s.id = h.signature_id
             WHERE h.process_instance_id = @instance
             ORDER BY h.occurred_at, h.id
            """);
        cmd.Parameters.AddWithValue("instance", instanceId);
        var list = new List<HistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HistoryEntry(r.GetFieldValue<DateTimeOffset>(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    private static JsonObject ModelObject(WorkflowDefinition def) => new()
    {
        ["code"] = def.Code,
        ["version"] = def.Version,
        ["initialState"] = def.InitialState,
        ["transitions"] = TransitionsJson(def.Transitions),
    };

    /// <summary>Transitions in the stored shape, shared by released versions and workflow drafts.</summary>
    internal static JsonArray TransitionsJson(IEnumerable<TransitionDefinition> transitions) => new([.. transitions.Select(t => (JsonNode)new JsonObject
    {
        ["action"] = t.Action,
        ["from"] = t.From,
        ["to"] = t.To,
        ["label"] = t.Label,
        ["permission"] = t.Permission,
        ["guard"] = t.Guard?.DeepClone(),
        ["guardMessage"] = t.GuardMessage,
        ["signatureMeaning"] = t.SignatureMeaning,
        ["requireReason"] = t.RequireReason,
    })]);

    internal static IReadOnlyList<TransitionDefinition> TransitionsFromJson(JsonArray array) =>
        [.. array.Select(n =>
        {
            var o = n!.AsObject();
            return new TransitionDefinition(
                o["action"]!.GetValue<string>(), o["from"]!.GetValue<string>(), o["to"]!.GetValue<string>(),
                Permission: Text(o, "permission"),
                Guard: o["guard"]?.DeepClone(),
                SignatureMeaning: Text(o, "signatureMeaning"),
                Label: Text(o, "label"),
                RequireReason: o["requireReason"]?.GetValue<bool>() ?? false,
                GuardMessage: Text(o, "guardMessage"));
        })];

    private static WorkflowDefinition FromModel(JsonObject m) => new(
        m["code"]!.GetValue<string>(),
        m["version"]!.GetValue<int>(),
        m["initialState"]!.GetValue<string>(),
        TransitionsFromJson(m["transitions"]!.AsArray()));

    private static string? Text(JsonObject o, string name) => o[name]?.GetValue<string>();
}
