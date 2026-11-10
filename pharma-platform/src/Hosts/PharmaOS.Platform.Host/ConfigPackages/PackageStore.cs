using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.ConfigPackages;

public sealed record DeploymentRow(Guid Id, string TargetEnvironment, DateTimeOffset DeployedAt, Guid DeployedBy, string Result, string? Log);

public sealed record PackageRow(
    Guid Id, string Code, string Name, string VersionLabel, string SourceEnvironment, string ContentHash, string Status,
    JsonObject Manifest, string? Signature, string? SigningKeyName, int? SigningKeyVersion, Guid? ApprovedBy, DateTimeOffset? ApprovedAt,
    Guid? ApprovalSignatureId, DateTimeOffset? ExportedAt, JsonNode? ImportLog, int RowVersion, DateTimeOffset UpdatedAt, Guid CreatedBy);

/// <summary>Configuration packages in meta.config_package and meta.config_package_item, inside the caller's transaction.</summary>
public sealed class PackageStore(NpgsqlConnection conn, NpgsqlTransaction tx)
{
    public const string Draft = "DRAFT";
    public const string Approved = "APPROVED";
    public const string Imported = "IMPORTED";
    public const string Deployed = "DEPLOYED";

    private const string Columns = """
        id, code, name, version_label, source_environment, content_hash, status, manifest::text, kms_signature, signing_key_name,
        signing_key_version, approved_by, approved_at, approval_signature_id, exported_at, import_log::text, row_version, updated_at,
        created_by
        """;

    private NpgsqlCommand Cmd(string sql) => new(sql, conn, tx);

    public async Task<bool> CodeExistsAsync(string code, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT 1 FROM meta.config_package WHERE code = @code");
        cmd.Parameters.AddWithValue("code", code);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Stores the package and its items. <paramref name="artifactIds"/> holds the source id of each item, by code and version.</summary>
    public async Task<Guid> InsertAsync(
        GxpContext ctx, PackageDocument doc, string status, IReadOnlyDictionary<(string Code, int Version), Guid> artifactIds,
        string? signature, string? keyName, int? keyVersion, JsonNode? importLog, CancellationToken ct)
    {
        Guid id;
        await using (var cmd = Cmd("""
            INSERT INTO meta.config_package (tenant_id, code, name, version_label, source_environment, content_hash, manifest, status,
                   kms_signature, signing_key_name, signing_key_version, import_log, created_by, updated_by)
            VALUES (@tenant, @code, @name, @label, @source, @hash, @manifest, @status, @sig, @keyname, @keyversion, @log, @user, @user)
            RETURNING id
            """))
        {
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            cmd.Parameters.AddWithValue("code", doc.Code);
            cmd.Parameters.AddWithValue("name", doc.Name);
            cmd.Parameters.AddWithValue("label", doc.VersionLabel);
            cmd.Parameters.AddWithValue("source", doc.SourceEnvironment);
            cmd.Parameters.AddWithValue("hash", doc.ContentHash);
            cmd.Parameters.Add(new NpgsqlParameter("manifest", NpgsqlDbType.Jsonb) { Value = doc.Manifest().ToJsonString() });
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("sig", (object?)signature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("keyname", (object?)keyName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("keyversion", (object?)keyVersion ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("log", NpgsqlDbType.Jsonb) { Value = (object?)importLog?.ToJsonString() ?? DBNull.Value });
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        foreach (var item in doc.Items)
        {
            await using var cmd = Cmd("""
                INSERT INTO meta.config_package_item (tenant_id, config_package_id, artifact_type, artifact_id, artifact_version, payload,
                       created_by, updated_by)
                VALUES (@tenant, @package, @type, @artifact, @version, @payload, @user, @user)
                """);
            cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
            cmd.Parameters.AddWithValue("user", ctx.UserId);
            cmd.Parameters.AddWithValue("package", id);
            cmd.Parameters.AddWithValue("type", item.ArtifactType);
            cmd.Parameters.AddWithValue("artifact", artifactIds.TryGetValue((item.Code, item.Version), out var a) ? a : Guid.NewGuid());
            cmd.Parameters.AddWithValue("version", item.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = item.ToJson().ToJsonString() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return id;
    }

    /// <summary>The package's items as stored, in the standard order.</summary>
    public async Task<IReadOnlyList<PackageItem>> ItemsAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd("SELECT payload::text FROM meta.config_package_item WHERE config_package_id = @id");
        cmd.Parameters.AddWithValue("id", id);
        var problems = new List<string>();
        var items = new List<PackageItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var doc = JsonNode.Parse(r.GetString(0))!.AsObject();
            var d = doc["definition"]!.AsObject();
            items.Add(new PackageItem(doc["artifactType"]!.GetValue<string>(), doc["code"]!.GetValue<string>(), doc["version"]!.GetValue<int>(),
                d["initialState"]!.GetValue<string>(), (JsonArray)d["transitions"]!.DeepClone()));
        }
        return PackageDocument.Create("", "", "", "", "", items).Items;
    }

    /// <summary>The document the stored rows describe. Its hash must equal the stored content hash, or the rows were altered.</summary>
    public async Task<PackageDocument> DocumentAsync(PackageRow row, string platformVersion, CancellationToken ct) =>
        PackageDocument.Create(row.Code, row.Name, row.VersionLabel, row.SourceEnvironment, platformVersion, await ItemsAsync(row.Id, ct));

    public async Task<PackageRow?> GetAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd($"SELECT {Columns} FROM meta.config_package WHERE id = @id {(forUpdate ? "FOR UPDATE" : "")}");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<PackageRow>> ListAsync(CancellationToken ct)
    {
        await using var cmd = Cmd($"SELECT {Columns} FROM meta.config_package ORDER BY created_at DESC, code");
        var list = new List<PackageRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task AddDeploymentAsync(
        GxpContext ctx, Guid packageId, string environment, DateTimeOffset at, string result, string log, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            INSERT INTO meta.config_deployment (tenant_id, config_package_id, target_environment, deployed_at, deployed_by, result, log,
                   created_by, updated_by)
            VALUES (@tenant, @package, @env, @at, @user, @result, @log, @user, @user)
            """);
        cmd.Parameters.AddWithValue("tenant", ctx.TenantId);
        cmd.Parameters.AddWithValue("user", ctx.UserId);
        cmd.Parameters.AddWithValue("package", packageId);
        cmd.Parameters.AddWithValue("env", environment);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("result", result);
        cmd.Parameters.AddWithValue("log", log);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentRow>> DeploymentsAsync(Guid packageId, CancellationToken ct)
    {
        await using var cmd = Cmd("""
            SELECT id, target_environment, deployed_at, deployed_by, result, log
              FROM meta.config_deployment WHERE config_package_id = @id ORDER BY deployed_at
            """);
        cmd.Parameters.AddWithValue("id", packageId);
        var list = new List<DeploymentRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetGuid(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Column names come from callers in this namespace, never from a request.</summary>
    public async Task<bool> SetAsync(Guid id, int expectedRowVersion, IReadOnlyDictionary<string, object?> columns, CancellationToken ct)
    {
        await using var cmd = Cmd("");
        var sets = new List<string>();
        foreach (var (column, value) in columns)
        {
            sets.Add($"{column} = @{column}");
            cmd.Parameters.Add(new NpgsqlParameter(column, value ?? DBNull.Value));
        }
        cmd.CommandText = $"UPDATE meta.config_package SET {string.Join(", ", sets)} WHERE id = @__id AND row_version = @__version";
        cmd.Parameters.AddWithValue("__id", id);
        cmd.Parameters.AddWithValue("__version", expectedRowVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static PackageRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5).Trim(), r.GetString(6),
        JsonNode.Parse(r.GetString(7))!.AsObject(), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        r.IsDBNull(13) ? null : r.GetGuid(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        r.IsDBNull(15) ? null : JsonNode.Parse(r.GetString(15)), r.GetInt32(16), r.GetFieldValue<DateTimeOffset>(17), r.GetGuid(18));
}
