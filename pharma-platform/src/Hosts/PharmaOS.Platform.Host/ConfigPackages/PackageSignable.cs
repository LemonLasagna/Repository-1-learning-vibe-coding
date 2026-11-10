using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.Platform.Host.ConfigPackages;

public static class PlatformInfo
{
    /// <summary>The platform version recorded in packages built here.</summary>
    public static readonly string Version = typeof(PlatformInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

/// <summary>
/// The two signed steps on a configuration package.
/// <para>
/// <b>Approve</b> (APPROVED): in the same transaction the installation's OpenBao key signs the package's hash. Both succeed or
/// neither does: if OpenBao cannot be reached the approval is refused, and the person is told before they are asked for their
/// password.
/// </para>
/// <para>
/// <b>Deploy</b> (RELEASED), on an imported package: re-checks everything now (the stored rows still hash to the recorded hash, the
/// signature is still valid under a trusted key, every item still passes the dry run against what is released here), releases each
/// item, records the deployment and marks the package DEPLOYED. It is one transaction with the signature: if anything fails,
/// nothing is released and no signature is kept.
/// </para>
/// </summary>
public sealed class PackageSignable(IOptions<ConfigPackageOptions> options, IPackageSigner signer) : ISignableSource
{
    public const string Entity = "meta.config_package";
    public const string Approve = "approve";
    public const string Deploy = "deploy";

    /// <summary>The signature meaning each step is signed with.</summary>
    public static readonly IReadOnlyDictionary<string, string> MeaningOf = new Dictionary<string, string>
    {
        [Approve] = EsigCatalog.Approved.Code,
        [Deploy] = EsigCatalog.Released.Code,
    };

    public string EntityCode => Entity;
    public string EntityName => "Configuration package";
    public string ModuleCode => "meta";
    public string ModuleName => "Configuration";
    public string PhysicalTable => "meta.config_package";
    public string ReadPermission => Permissions.ConfigPackageAuthor;
    public IReadOnlyList<string> Meanings { get; } = [.. MeaningOf.Values];
    public bool SignaturesRequireAction => true;

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct) =>
        await new PackageStore(conn, tx).GetAsync(id, forUpdate: lockRow, ct) is { } p
            ? new SignableRecord(p.Id, $"{p.Code} {p.VersionLabel}", p.RowVersion, SignedHash(p),
                options.Value.RequireSecondPerson ? p.CreatedBy : Guid.Empty)
            : null;

    public async Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var refusal = (await CheckAsync(conn, tx, a, ct)).Refusal;
        if (refusal is not null || a.Action != Approve) return refusal;
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
        var store = new PackageStore(conn, tx);
        Dictionary<string, object?> columns;
        if (a.Action == Approve)
        {
            PackageSignature signature;
            try
            {
                signature = await signer.SignAsync(PackageDocument.SignedBytes(package!.ContentHash), ct);
            }
            catch (SigningUnavailableException e)
            {
                return Unavailable(e);
            }
            columns = new()
            {
                ["status"] = PackageStore.Approved, ["approved_by"] = a.Context.UserId, ["approved_at"] = a.Now,
                ["approval_signature_id"] = signatureId, ["kms_signature"] = signature.Signature,
                ["signing_key_name"] = signature.KeyName, ["signing_key_version"] = signature.KeyVersion,
            };
        }
        else
        {
            var failed = await DeployAsync(conn, tx, a, package!, ct);
            if (failed is not null) return failed;
            columns = new() { ["status"] = PackageStore.Deployed };
        }
        return await store.SetAsync(package!.Id, package.RowVersion, columns, ct)
            ? null
            : new(StatusCodes.Status412PreconditionFailed, "STALE_VERSION", "Stale version",
                "The package was changed by someone else. Reload it and review the changes.");
    }

    /// <summary>Releases every item that is not already released here and records the deployment. Runs inside the signing transaction.</summary>
    private async Task<ActionRefusal?> DeployAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, PackageRow package, CancellationToken ct)
    {
        var store = new PackageStore(conn, tx);
        var document = await store.DocumentAsync(package, PackagePlatformVersion(package), ct);
        var report = await PackageDryRun.RunAsync(new WorkflowDraftStore(conn, tx), new WorkflowStore(conn, tx), document, ct);
        if (report.Any(r => r.Outcome == PackageDryRun.Refused))
            return new(StatusCodes.Status409Conflict, "NO_LONGER_VALID", "Package can no longer be deployed",
                "What is released here changed since the package was imported: " +
                string.Join(" ", report.Where(r => r.Outcome == PackageDryRun.Refused)
                    .SelectMany(r => r.Problems.Select(p => $"{r.Code} version {r.Version}: {p}"))) + " Nothing was deployed.");

        try
        {
            foreach (var item in document.Items.Where(i => report.Single(r => r.Code == i.Code && r.Version == i.Version).Outcome == PackageDryRun.Add))
                await WorkflowDraftSignable.ReleaseVersionAsync(conn, tx, a.Context,
                    new DraftInput(item.Code, item.Version, item.InitialState, WorkflowStore.TransitionsFromJson(item.Transitions)),
                    $"Workflow {item.Code} v{item.Version} deployed from configuration package {package.Code} ({package.VersionLabel})", ct);
        }
        catch (WorkflowVersionConflictException e)
        {
            return new(StatusCodes.Status409Conflict, "NO_LONGER_VALID", "Package can no longer be deployed", $"{e.Message} Nothing was deployed.");
        }
        await WorkflowTransitions.SetChangeReasonAsync(conn, tx, a.Context.ChangeReason, ct);
        await store.AddDeploymentAsync(a.Context, package.Id, options.Value.EnvironmentName, a.Now, "DEPLOYED",
            string.Join("\n", report.Select(r => $"{r.Code} version {r.Version}: {r.Outcome}")), ct);
        return null;
    }

    private async Task<(ActionRefusal? Refusal, PackageRow? Package)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var store = new PackageStore(conn, tx);
        var package = await store.GetAsync(a.RecordId, forUpdate: true, ct);
        if (package is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", "The configuration package does not exist."), null);
        if (!MeaningOf.TryGetValue(a.Action, out var meaning))
            return (new(StatusCodes.Status400BadRequest, "ACTION_NOT_SUPPORTED", "Action not supported", $"'{a.Action}' cannot be signed on a configuration package."), package);
        var permission = a.Action == Approve ? Permissions.ConfigPackageApprove : Permissions.ConfigPackageDeploy;
        if (!a.Permissions.Contains(permission))
            return (new(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Missing permission '{permission}'"), package);
        var needed = a.Action == Approve ? PackageStore.Draft : PackageStore.Imported;
        if (package.Status != needed)
            return (new(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The package is {package.Status}; {a.Action} needs {needed}."), package);
        if (a.Meaning.Code != meaning)
            return (new(StatusCodes.Status400BadRequest, "WRONG_MEANING", "Wrong signature meaning",
                $"'{a.Action}' is signed as {EsigCatalog.FindMeaning(meaning)?.Label ?? meaning}."), package);
        if (a.Action == Approve && options.Value.RequireSecondPerson && package.CreatedBy == a.Context.UserId)
            return (new(StatusCodes.Status403Forbidden, "SAME_PERSON", "Second person required",
                "The person who built a package cannot approve it. Ask someone else with the approval permission."), package);

        // What is about to be signed or deployed must be what the package's content hash says it is.
        var document = await store.DocumentAsync(package, PackagePlatformVersion(package), ct);
        if (document.ContentHash != package.ContentHash)
            return (new(StatusCodes.Status409Conflict, "PACKAGE_CORRUPT", "Package content does not match its hash",
                "The stored items no longer produce the package's recorded hash. Do not use this package; build or import it again."), package);

        if (a.Action == Deploy)
        {
            // Deploying trusts only what verifies now: the signature must still be valid under a key this installation trusts.
            var file = new JsonObject
            {
                ["format"] = PackageDocument.Format, ["package"] = document.ToJson(), ["contentHash"] = package.ContentHash,
                ["signature"] = new JsonObject { ["value"] = package.Signature },
            };
            var verified = PackageVerifier.Verify(file, await PackageTrust.KeysAsync(options.Value, signer, ct));
            if (!verified.Ok)
                return (new(StatusCodes.Status409Conflict, "PACKAGE_NOT_TRUSTED", "Package signature not trusted",
                    string.Join(" ", verified.Problems)), package);
        }
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
