using System.Globalization;
using Npgsql;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>Record numbers such as DEV-2026-00001 (meta.numbering_scheme / numbering_sequence).</summary>
public static class NumberingStore
{
    /// <summary>
    /// Next {prefix}-yyyy-nnnnn. The counter row is locked by the upsert until commit, so numbers are unique and,
    /// because a rolled-back creation also rolls back the counter, gap-free.
    /// </summary>
    public static async Task<string> NextAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, string scheme, string prefix, int year, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO meta.numbering_scheme (tenant_id, code, pattern, reset_policy, site_scoped, created_by, updated_by)
            VALUES (@tenant, @scheme, @pattern, 'YEARLY', false, @user, @user)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("scheme", scheme);
            cmd.Parameters.AddWithValue("pattern", prefix + "-{yyyy}-{seq:5}");
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var next = new NpgsqlCommand("""
            INSERT INTO meta.numbering_sequence (tenant_id, numbering_scheme_id, scope_key, current_value, created_by, updated_by)
            SELECT @tenant, id, @year, 1, @user, @user FROM meta.numbering_scheme WHERE code = @scheme
            ON CONFLICT (tenant_id, numbering_scheme_id, scope_key)
            DO UPDATE SET current_value = meta.numbering_sequence.current_value + 1
            RETURNING current_value
            """, conn, tx);
        next.Parameters.AddWithValue("tenant", ctx.TenantId);
        next.Parameters.AddWithValue("scheme", scheme);
        next.Parameters.AddWithValue("year", year.ToString(CultureInfo.InvariantCulture));
        next.Parameters.AddWithValue("user", ctx.UserId);
        var n = Convert.ToInt64(await next.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return $"{prefix}-{year}-{n:D5}";
    }
}
