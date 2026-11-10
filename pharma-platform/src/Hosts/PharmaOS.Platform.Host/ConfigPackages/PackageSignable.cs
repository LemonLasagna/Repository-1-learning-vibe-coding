using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.Platform.Host.ConfigPackages;

public static class PlatformInfo
{
    /// <summary>The platform version recorded in packages built here.</summary>
    public static readonly string Version = typeof(PlatformInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>
/// Approving a package is an electronic signature (APPROVED) and, in the same transaction, the installation's OpenBao key signs the
/// package's hash. Both succeed or neither does: if OpenBao cannot be reached the approval is refused, and the person is told before
/// they are asked for their password. Approval needs the package's stored rows to still hash to its recorded content hash.
/// </summary>
public sealed class PackageSignable(IOptions<ConfigPackageOptions> options, IPackageSigner signer) : ISignableSource
{
    public const string Entity = "meta.config_package";
    public const string Approve = "approve";

    public string EntityCode => Entity;
    public string EntityName => "Configuration package";
    public string ModuleCode => "meta";
    public string ModuleName => "Configuration";
    public string PhysicalTable => "meta.config_package";
    public string ReadPermission => Permissions.ConfigPackageAuthor;
    public IReadOnlyList<string> Meanings { get; } = [EsigCatalog.Approved.Code];
    public bool SignaturesRequireAction => true;

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct) =>
        await new PackageStore(conn, tx).GetAsync(id, forUpdate: lockRow, ct) is { } p
            ? new SignableRecord(p.Id, $"{p.Code} {p.VersionLabel}", p.RowVersion, SignedHash(p),
                options.Value.RequireSecondPerson ? p.CreatedBy : Guid.Empty)
            : null;

    public async Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var refusal = (await CheckAsync(conn, tx, a, ct)).Refusal;
        if (refusal is not null) return refusal;
        try
        {
            await signer.KeyInfoAsync(ct); // fail now, before the password is asked for, if the key cannot be reached
        }
        catch (SigningUnavailableException e)
        {
            return Unavailable(e);
        }
        return null;
    }

    public async Task<ActionRefusal?> ApplyActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, Guid signatureId, CancellationToken ct)
    {
        var (refusal, package) = await CheckAsync(conn, tx, a, ct);
        if (refusal is not null) return refusal;
        PackageSignature signature;
        try
        {
            signature = await signer.SignAsync(PackageDocument.SignedBytes(package!.ContentHash), ct);
        }
        catch (SigningUnavailableException e)
        {
            return Unavailable(e);
        }
        var columns = new Dictionary<string, object?>
        {
            ["status"] = PackageStore.Approved, ["approved_by"] = a.Context.UserId, ["approved_at"] = a.Now,
            ["approval_signature_id"] = signatureId, ["kms_signature"] = signature.Signature,
            ["signing_key_name"] = signature.KeyName, ["signing_key_version"] = signature.KeyVersion,
        };
        return await new PackageStore(conn, tx).SetAsync(package.Id, package.RowVersion, columns, ct)
            ? null
            : new(StatusCodes.Status412PreconditionFailed, "STALE_VERSION", "Stale version",
                "The package was changed by someone else. Reload it and review the changes.");
    }

    private async Task<(ActionRefusal? Refusal, PackageRow? Package)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var store = new PackageStore(conn, tx);
        var package = await store.GetAsync(a.RecordId, forUpdate: true, ct);
        if (package is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", "The configuration package does not exist."), null);
        if (a.Action != Approve)
            return (new(StatusCodes.Status400BadRequest, "ACTION_NOT_SUPPORTED", "Action not supported", $"'{a.Action}' cannot be signed on a configuration package."), package);
        if (!a.Permissions.Contains(Permissions.ConfigPackageApprove))
            return (new(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Missing permission '{Permissions.ConfigPackageApprove}'"), package);
        if (package.Status != PackageStore.Draft)
            return (new(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The package is {package.Status}; approval needs {PackageStore.Draft}."), package);
        if (a.Meaning.Code != EsigCatalog.Approved.Code)
            return (new(StatusCodes.Status400BadRequest, "WRONG_MEANING", "Wrong signature meaning", $"Approving a package is signed as {EsigCatalog.Approved.Label}."), package);
        if (options.Value.RequireSecondPerson && package.CreatedBy == a.Context.UserId)
            return (new(StatusCodes.Status403Forbidden, "SAME_PERSON", "Second person required",
                "The person who built a package cannot approve it. Ask someone else with the approval permission."), package);

        // What is about to be signed must be what the package's content hash says it is.
        var document = await store.DocumentAsync(package, PackagePlatformVersion(package), ct);
        if (document.ContentHash != package.ContentHash)
            return (new(StatusCodes.Status409Conflict, "PACKAGE_CORRUPT", "Package content does not match its hash",
                "The stored items no longer produce the package's recorded hash. Do not approve it; build the package again."), package);
        return (null, package);
    }

    private static ActionRefusal Unavailable(SigningUnavailableException e) =>
        new(StatusCodes.Status503ServiceUnavailable, "SIGNING_UNAVAILABLE", "Signing key unavailable",
            $"The package cannot be signed now: {e.Message} Nothing was changed.");

    /// <summary>The platform version a package was built with is part of its hash, so it is read back from its manifest.</summary>
    internal static string PackagePlatformVersion(PackageRow p) => p.Manifest["platformVersion"]?.GetValue<string>() ?? "";

    private static string SignedHash(PackageRow p) => CanonicalJson.Sha256Hex(new JsonObject
    {
        ["entity"] = Entity, ["id"] = p.Id.ToString("D"), ["code"] = p.Code, ["versionLabel"] = p.VersionLabel, ["contentHash"] = p.ContentHash,
    });
}
