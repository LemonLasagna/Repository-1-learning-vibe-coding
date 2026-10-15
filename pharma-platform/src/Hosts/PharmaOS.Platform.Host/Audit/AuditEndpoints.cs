using System.Text.Json;
using Npgsql;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.Audit;

public sealed record AuditEntry(
    DateTimeOffset OccurredAt,
    string Operation,
    IReadOnlyList<string> ChangedFields,
    JsonElement? OldValues,
    JsonElement? NewValues,
    Guid ActorUserId,
    string? ActorDisplay,
    string? Reason);

/// <summary>
/// Reads audit.audit_trail through a separate login that holds pharmaos_auditor (SELECT only).
/// The application login cannot read or change the audit trail (OQ-DI-007), so this reader
/// filters by tenant explicitly.
/// </summary>
public sealed class AuditTrailReader(NpgsqlDataSource auditDataSource)
{
    private const string Sql = """
        SELECT occurred_at, operation, coalesce(changed_fields, '{}'), old_values::text, new_values::text,
               actor_user_id, actor_display, reason
          FROM audit.audit_trail
         WHERE tenant_id = @tenant AND schema_name = @schema AND table_name = @table AND record_id = @record
         ORDER BY occurred_at, seq
        """;

    public async Task<IReadOnlyList<AuditEntry>> ForRecordAsync(
        Guid tenantId, string schema, string table, Guid recordId, CancellationToken ct)
    {
        await using var cmd = auditDataSource.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("record", recordId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var entries = new List<AuditEntry>();
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new AuditEntry(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.GetFieldValue<string[]>(2),
                ParseJson(reader, 3),
                ParseJson(reader, 4),
                reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return entries;
    }

    private static JsonElement? ParseJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : JsonDocument.Parse(reader.GetString(ordinal)).RootElement.Clone();
}

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/audit/v1/records/{schema}/{table}/{recordId:guid}",
                async (string schema, string table, Guid recordId, IGxpContextAccessor gxp, AuditTrailReader reader,
                       CancellationToken ct) =>
                {
                    if (gxp.Current is not { } ctx)
                        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing identity");
                    return Results.Ok(await reader.ForRecordAsync(ctx.TenantId, schema, table, recordId, ct));
                })
            .RequireAuthorization(Permissions.AuditTrailRead);
}
