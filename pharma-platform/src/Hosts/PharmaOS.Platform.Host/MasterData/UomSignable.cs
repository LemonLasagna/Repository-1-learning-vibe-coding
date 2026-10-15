using System.Text.Json.Nodes;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;

namespace PharmaOS.Platform.Host.MasterData;

/// <summary>Units of measure can be reviewed and approved by electronic signature.</summary>
public sealed class UomSignable : ISignableSource
{
    public string EntityCode => "mdm.uom";
    public string EntityName => "Unit of measure";
    public string ModuleCode => "mdm";
    public string ModuleName => "Master data";
    public string PhysicalTable => "mdm.uom";
    public string ReadPermission => Permissions.UomRead;
    public IReadOnlyList<string> Meanings { get; } = [EsigCatalog.Reviewed.Code, EsigCatalog.Approved.Code];

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT code, name, dimension, ext::text, row_version, updated_by FROM mdm.uom WHERE id = @id"
            + (lockRow ? " FOR SHARE" : ""), conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var code = r.GetString(0);
        var version = r.GetInt32(4);
        return new SignableRecord(id, code, version, Hash(id, version, code, r.GetString(1), r.GetString(2), r.GetString(3)), r.GetGuid(5));
    }

    /// <summary>record_hash per DS §10.5: entity code, id, row_version and every business field.</summary>
    public static string Hash(Guid id, int version, string code, string name, string dimension, string extJson) =>
        CanonicalJson.Sha256Hex(new JsonObject
        {
            ["entity"] = "mdm.uom",
            ["id"] = id.ToString("D"),
            ["row_version"] = version,
            ["code"] = code,
            ["name"] = name,
            ["dimension"] = dimension,
            ["ext"] = JsonNode.Parse(extJson),
        });
}
