using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;
using PharmaOS.ESig;

namespace PharmaOS.Platform.Host.ESignature;

/// <summary>
/// The login is already recorded for a different identity-provider subject: user ids are never reassigned
/// (doc 14), so this needs an administrator, not a silent re-link.
/// </summary>
public sealed class IdentityConflictException(string login)
    : Exception($"The login '{login}' is already linked to a different identity in PharmaOS.")
{
    public string Login { get; } = login;
}

/// <summary>Answers <see cref="IdentityConflictException"/> with 409 instead of a server error.</summary>
public sealed class IdentityConflictHandler(IProblemDetailsService problems) : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is not IdentityConflictException e) return false;
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Identity conflict",
                Detail = $"{e.Message} An administrator must resolve it; user ids are never reassigned.",
                Extensions = { ["code"] = "IDENTITY_CONFLICT" },
            },
        });
    }
}

/// <summary>What the signer saw and agreed to sign, held by the challenge until the signature completes.</summary>
public sealed record ChallengeIntent(
    string Entity, Guid RecordId, int RecordVersion, string RecordHash, string Meaning, string? Reason, bool Acknowledge,
    string? Action = null);

public sealed record ChallengeRow(Guid UserId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt, ChallengeIntent Intent);

public sealed record SignatureView(
    Guid Id,
    string SignerName,
    string? SignerLogin,
    string Meaning,
    string MeaningLabel,
    DateTimeOffset SignedAt,
    int RecordVersion,
    string RecordHash,
    string? Reason,
    string? Manifest);

/// <summary>
/// SQL for the esig, iam and meta tables, run inside the caller's transaction so the GxP context
/// (tenant for RLS, actor for the audit triggers) applies to every statement.
/// </summary>
public sealed class EsigStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public NpgsqlConnection Connection => conn;
    public NpgsqlTransaction Transaction => tx;

    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    /// <summary>
    /// Just-in-time user record from the identity provider's claims. The id is the IdP subject, the same
    /// value the audit trail records as actor. A login reused by a different subject is refused (unique index).
    /// </summary>
    public async Task EnsureUserAsync(GxpContext ctx, string login, string displayName, string? email, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO iam.app_user (id, tenant_id, username, display_name, email, idp_subject, status, esig_enabled, created_by, updated_by)
            VALUES (@id, @tenant, @login, @name, @email, @sub, 'ACTIVE', true, @id, @id)
            ON CONFLICT (id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("id", ctx.UserId);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("login", login);
        cmd.Parameters.AddWithValue("name", displayName);
        cmd.Parameters.AddWithValue("email", email ?? "");
        cmd.Parameters.AddWithValue("sub", ctx.UserId.ToString("D"));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new IdentityConflictException(login);
        }
    }

    /// <summary>Module and entity definition rows for a signable record type (the signature references them).</summary>
    public async Task<Guid> EnsureEntityDefinitionAsync(GxpContext ctx, ISignableSource source, CancellationToken ct)
    {
        await using (var cmd = Cmd("""
            INSERT INTO meta.module (tenant_id, code, name, is_enabled, created_by, updated_by)
            VALUES (@tenant, @code, @name, true, @user, @user)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("code", source.ModuleCode);
            cmd.Parameters.AddWithValue("name", source.ModuleName);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = Cmd("""
            INSERT INTO meta.entity_definition (tenant_id, module_id, code, name, physical_table, is_custom, is_gxp, created_by, updated_by)
            SELECT @tenant, m.id, @code, @name, @table, false, true, @user, @user FROM meta.module m WHERE m.code = @module
            ON CONFLICT (tenant_id, code) DO NOTHING
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("code", source.EntityCode);
            cmd.Parameters.AddWithValue("name", source.EntityName);
            cmd.Parameters.AddWithValue("table", source.PhysicalTable);
            cmd.Parameters.AddWithValue("module", source.ModuleCode);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return await EntityDefinitionIdAsync(source.EntityCode, ct)
               ?? throw new InvalidOperationException($"Entity definition {source.EntityCode} could not be created.");
    }

    public async Task<Guid?> EntityDefinitionIdAsync(string entityCode, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT id FROM meta.entity_definition WHERE code = @code");
        cmd.Parameters.AddWithValue("code", entityCode);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<Guid> EnsureMeaningAsync(GxpContext ctx, MeaningDefinition meaning, CancellationToken ct)
    {
        await using (var cmd = Cmd("""
            INSERT INTO esig.signature_meaning (tenant_id, code, label, description, created_by, updated_by)
            VALUES (@tenant, @code, @label, @statement, @user, @user)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("code", meaning.Code);
            cmd.Parameters.AddWithValue("label", meaning.Label);
            cmd.Parameters.AddWithValue("statement", meaning.Statement);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var get = Cmd("SELECT id FROM esig.signature_meaning WHERE code = @code");
        get.Parameters.AddWithValue("code", meaning.Code);
        return (Guid)(await get.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> HasAcknowledgedAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM esig.user_esig_acknowledgement WHERE user_id = @user AND statement_version = @v LIMIT 1");
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("v", EsigCatalog.StatementVersion);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Every signature on the record, any version: segregation of duties looks at the whole history.</summary>
    public async Task<IReadOnlyList<PriorSignature>> SignaturesOnRecordAsync(Guid entityDefinitionId, Guid recordId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT s.signer_id, m.code FROM esig.signature s JOIN esig.signature_meaning m ON m.id = s.meaning_id
             WHERE s.entity_definition_id = @ed AND s.record_id = @record
            """);
        cmd.Parameters.AddWithValue("ed", entityDefinitionId);
        cmd.Parameters.AddWithValue("record", recordId);
        var list = new List<PriorSignature>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new PriorSignature(r.GetGuid(0), r.GetString(1)));
        return list;
    }

    public async Task InsertChallengeAsync(
        GxpContext ctx, Guid id, DateTimeOffset issuedAt, DateTimeOffset expiresAt, ChallengeIntent intent, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO esig.signature_challenge (id, tenant_id, user_id, nonce, issued_at, expires_at, method, ext, created_by, updated_by)
            VALUES (@id, @tenant, @user, @nonce, @issued, @expires, @method, @ext, @user, @user)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        cmd.Parameters.AddWithValue("nonce", Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        cmd.Parameters.AddWithValue("issued", issuedAt);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        cmd.Parameters.AddWithValue("method", EsigCatalog.AuthMethod);
        cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(intent, Json) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Locks the challenge row so two completions of the same challenge cannot both succeed.</summary>
    public async Task<ChallengeRow?> LockChallengeAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT user_id, issued_at, expires_at, consumed_at, ext::text FROM esig.signature_challenge WHERE id = @id FOR UPDATE");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ChallengeRow(
            r.GetGuid(0),
            r.GetFieldValue<DateTimeOffset>(1),
            r.GetFieldValue<DateTimeOffset>(2),
            r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
            JsonSerializer.Deserialize<ChallengeIntent>(r.GetString(4), Json)!);
    }

    public async Task ConsumeChallengeAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = Cmd("UPDATE esig.signature_challenge SET consumed_at = @at WHERE id = @id AND consumed_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertFailureAsync(GxpContext ctx, DateTimeOffset at, string reason, Guid? recordId, Guid challengeId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO esig.signature_failure (tenant_id, user_id, attempted_at, reason, record_id, ext, created_by, updated_by)
            VALUES (@tenant, @user, @at, @reason, @record, @ext, @user, @user)
            """);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("record", (object?)recordId ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(new { challengeId }, Json) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertSignatureAsync(
        GxpContext ctx, SignatureEvidence s, Guid entityDefinitionId, Guid meaningId, string signerName, string meaningLabel,
        string signatureHash, object ext, string manifest, bool acknowledge, CancellationToken ct)
    {
        await using (var cmd = Cmd("""
            INSERT INTO esig.signature (id, tenant_id, signer_id, signer_full_name, meaning_id, meaning_label, signed_at,
                   entity_definition_id, record_id, record_version, record_hash, auth_method, reason, signature_hash, ext,
                   created_by, updated_by)
            VALUES (@id, @tenant, @signer, @name, @meaning, @label, @at, @ed, @record, @version, @hash, @method, @reason,
                    @sighash, @ext, @signer, @signer)
            """))
        {
            cmd.Parameters.AddWithValue("id", s.SignatureId);
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("signer", s.SignerId);
            cmd.Parameters.AddWithValue("name", signerName);
            cmd.Parameters.AddWithValue("meaning", meaningId);
            cmd.Parameters.AddWithValue("label", meaningLabel);
            cmd.Parameters.AddWithValue("at", s.SignedAt);
            cmd.Parameters.AddWithValue("ed", entityDefinitionId);
            cmd.Parameters.AddWithValue("record", s.RecordId);
            cmd.Parameters.AddWithValue("version", s.RecordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("hash", s.RecordHash);
            cmd.Parameters.AddWithValue("method", EsigCatalog.AuthMethod);
            cmd.Parameters.AddWithValue("reason", (object?)s.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sighash", signatureHash);
            cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(ext, Json) });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = Cmd("""
            INSERT INTO esig.signature_manifest (tenant_id, signature_id, rendered_text, locale, created_by, updated_by)
            VALUES (@tenant, @sig, @text, 'en', @user, @user)
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("sig", s.SignatureId);
            cmd.Parameters.AddWithValue("text", manifest);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (acknowledge && !await HasAcknowledgedAsync(ctx.UserId, ct))
        {
            await using var cmd = Cmd("""
                INSERT INTO esig.user_esig_acknowledgement (tenant_id, user_id, acknowledged_at, statement_version, signature_id, created_by, updated_by)
                VALUES (@tenant, @user, @at, @v, @sig, @user, @user)
                """);
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            cmd.Parameters.AddWithValue("at", s.SignedAt);
            cmd.Parameters.AddWithValue("v", EsigCatalog.StatementVersion);
            cmd.Parameters.AddWithValue("sig", s.SignatureId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<SignatureView>> SignaturesForRecordAsync(string entityCode, Guid recordId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT s.id, s.signer_full_name, s.ext->>'signerLogin', m.code, s.meaning_label, s.signed_at,
                   s.record_version, s.record_hash, s.reason, mf.rendered_text
              FROM esig.signature s
              JOIN esig.signature_meaning m ON m.id = s.meaning_id
              JOIN meta.entity_definition ed ON ed.id = s.entity_definition_id
              LEFT JOIN esig.signature_manifest mf ON mf.signature_id = s.id
             WHERE ed.code = @entity AND s.record_id = @record
             ORDER BY s.signed_at, s.id
            """);
        cmd.Parameters.AddWithValue("entity", entityCode);
        cmd.Parameters.AddWithValue("record", recordId);
        var list = new List<SignatureView>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SignatureView(
                r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5), int.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                r.GetString(7).Trim(), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
        }
        return list;
    }
}
