using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Keys;
using PharmaOS.Platform.Host.Workflow;
using static PharmaOS.Platform.Host.Api.ApiResults;

namespace PharmaOS.Platform.Host.ConfigPackages;

/// <summary>
/// Configuration packages (FS-CFG-014): released workflow versions are collected into a package with a manifest and a SHA-256 over
/// its content, approved with an electronic signature (which also has OpenBao sign the hash), exported as a file, and imported into
/// another installation, which checks the hash, checks the signature against keys it trusts and dry-runs every item before it
/// stores anything. Deploying an imported package is a separate, signed step.
/// </summary>
public static partial class PackageEndpoints
{
    private const string Base = "/api/config/v1/packages";

    [GeneratedRegex("^[A-Z][A-Z0-9_-]{2,49}$")]
    private static partial Regex CodePattern();

    public static void MapConfigPackageEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in user may ask which environment this is: the screens show production as read-only.
        app.MapGet("/api/config/v1/environment", (IOptions<ConfigPackageOptions> o) =>
            Results.Ok(new { name = o.Value.EnvironmentName, production = o.Value.IsProduction }));
        var group = app.MapGroup(Base);
        group.MapPost("", Build).RequireAuthorization(Permissions.ConfigPackageAuthor);
        group.MapGet("", List).RequireAuthorization(Permissions.ConfigPackageAuthor);
        group.MapGet("{id:guid}", Get).RequireAuthorization(Permissions.ConfigPackageAuthor);
        group.MapPost("{id:guid}/approve", (Guid id) =>
            Problem(StatusCodes.Status400BadRequest, "SIGNATURE_REQUIRED", "Signature required",
                $"Approving a package is an electronic signature. Start it with POST /api/esig/v1/challenges (entity {PackageSignable.Entity}, recordId {id}, action {PackageSignable.Approve}, meaning APPROVED)."))
            .RequireAuthorization(Permissions.ConfigPackageApprove);
        group.MapPost("{id:guid}/deploy", (Guid id) =>
            Problem(StatusCodes.Status400BadRequest, "SIGNATURE_REQUIRED", "Signature required",
                $"Deploying a package is an electronic signature. Start it with POST /api/esig/v1/challenges (entity {PackageSignable.Entity}, recordId {id}, action {PackageSignable.Deploy}, meaning RELEASED)."))
            .RequireAuthorization(Permissions.ConfigPackageDeploy);
        group.MapPost("{id:guid}/discard", Discard).RequireAuthorization(Permissions.ConfigPackageAuthor);
        group.MapPost("{id:guid}/export", Export).RequireAuthorization(Permissions.ConfigPackageAuthor);
        group.MapPost("import", Import).RequireAuthorization(Permissions.ConfigPackageAuthor);
    }

    private static async Task<IResult> Build(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, IOptions<ConfigPackageOptions> options, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var errors = new Dictionary<string, string[]>();
        var code = Text(body, "code", errors);
        var name = Text(body, "name", errors);
        var label = Text(body, "versionLabel", errors);
        if (code is not null && !CodePattern().IsMatch(code))
            errors["code"] = ["3 to 50 characters: capital letters, digits, '_' or '-', starting with a letter."];
        if (name is { Length: > 255 }) errors["name"] = ["At most 255 characters."];
        if (label is { Length: > 50 }) errors["versionLabel"] = ["At most 50 characters."];
        var requested = new List<(string Code, int Version)>();
        if (body["items"] is not JsonArray array || array.Count == 0)
            errors["items"] = ["List at least one workflow version."];
        else
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject o && o["artifactType"]?.GetValue<string>() == PackageItem.Workflow
                    && o["code"] is JsonValue c && c.TryGetValue<string>(out var itemCode)
                    && o["version"] is JsonValue v && v.TryGetValue<int>(out var version))
                    requested.Add((itemCode, version));
                else
                    errors[$"items[{i}]"] = [$"An object with artifactType '{PackageItem.Workflow}', a code and a whole-number version."];
            }
        if (requested.Count != requested.Distinct().Count()) errors["items"] = ["Each workflow version may be listed once."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var packages = new PackageStore(conn, sqlTx);
        if (await packages.CodeExistsAsync(code!, ct))
            return Problem(StatusCodes.Status409Conflict, "PACKAGE_EXISTS", "Package code in use", $"A package with code {code} already exists.");

        var wf = new WorkflowStore(conn, sqlTx);
        var items = new List<PackageItem>();
        var artifactIds = new Dictionary<(string, int), Guid>();
        var missing = new List<string>();
        foreach (var (itemCode, version) in requested)
        {
            if (await wf.ReleasedVersionAsync(itemCode, version, ct) is not { } released)
            {
                missing.Add($"{itemCode} version {version}");
                continue;
            }
            items.Add(new PackageItem(PackageItem.Workflow, itemCode, version, released.Definition.InitialState,
                WorkflowStore.TransitionsJson(released.Definition.Transitions)));
            artifactIds[(itemCode, version)] = released.VersionId;
        }
        if (missing.Count > 0)
            return Problem(StatusCodes.Status422UnprocessableEntity, "NOT_RELEASED", "Not released",
                $"Only released workflow versions can be packaged. Not released here: {string.Join(", ", missing)}.");

        var document = PackageDocument.Create(code!, name!, label!, options.Value.EnvironmentName, PlatformInfo.Version, items);
        var id = await packages.InsertAsync(ctx, document, PackageStore.Draft, artifactIds, null, null, null, null, ct);
        var row = (await packages.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);

        SetETag(http, row.RowVersion);
        return Results.Created($"{Base}/{id}", Response(row, document));
    }

    private static async Task<IResult> List(PlatformDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var rows = await new PackageStore(conn, sqlTx).ListAsync(ct);
        return Results.Ok(new { items = rows.Select(r => Response(r, null)) });
    }

    private static async Task<IResult> Get(Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new PackageStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: false, ct);
        if (row is null) return Results.NotFound();
        SetETag(http, row.RowVersion);
        return Results.Ok(Response(row, await store.DocumentAsync(row, PackageSignable.PackagePlatformVersion(row), ct),
            await store.DeploymentsAsync(id, ct)));
    }

    /// <summary>The package as a file to carry to another installation. Only an approved package is exported.</summary>
    private static async Task<IResult> Export(
        Guid id, PlatformDbContext db, IGxpContextAccessor gxp, IPackageSigner signer, CancellationToken ct)
    {
        if (gxp.Current is null) return MissingIdentity();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new PackageStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.Status != PackageStore.Approved || row.Signature is null)
            return Problem(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The package is {row.Status}; export needs {PackageStore.Approved}.");
        var document = await store.DocumentAsync(row, PackageSignable.PackagePlatformVersion(row), ct);
        if (document.ContentHash != row.ContentHash)
            return Problem(StatusCodes.Status409Conflict, "PACKAGE_CORRUPT", "Package content does not match its hash",
                "The stored items no longer produce the package's recorded hash. Do not use this package; build it again.");

        // The public key is for the person reading the file (to compare with what the target trusts); verification never uses it.
        string? publicKey = null;
        try
        {
            publicKey = (await signer.KeyInfoAsync(ct)).PublicKeysPem.GetValueOrDefault(row.SigningKeyVersion ?? 0);
        }
        catch (SigningUnavailableException)
        {
        }
        if (row.ExportedAt is null)
            await store.SetAsync(id, row.RowVersion, new Dictionary<string, object?> { ["exported_at"] = DateTimeOffset.UtcNow }, ct);
        await tx.CommitAsync(ct);

        var file = new JsonObject
        {
            ["format"] = PackageDocument.Format,
            ["package"] = document.ToJson(),
            ["contentHash"] = row.ContentHash,
            ["signature"] = new JsonObject { ["value"] = row.Signature, ["keyName"] = row.SigningKeyName, ["keyVersion"] = row.SigningKeyVersion },
            ["publicKeyPem"] = publicKey,
        };
        return Results.Content(file.ToJsonString(), "application/json");
    }

    public sealed record DiscardBody(string? Comment);

    /// <summary>
    /// Sets a package aside so it cannot be approved, exported or deployed. Nothing is deleted: the package, its items, its import
    /// record and the reason stay, but its code is free to use again. A package that was built and never approved (anyone who may build
    /// packages) or one that was imported and never deployed (needs the deploy permission) can be discarded; the reason is required.
    /// </summary>
    private static async Task<IResult> Discard(
        Guid id, DiscardBody? body, PlatformDbContext db, IGxpContextAccessor gxp, HttpContext http, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (!TryParseIfMatch(http, out var expected)) return IfMatchRequired();
        var comment = body?.Comment?.Trim();
        if (string.IsNullOrEmpty(comment)) return Invalid("comment", "Say why the package is discarded.");
        if (comment.Length > 1000) return Invalid("comment", "At most 1000 characters.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var store = new PackageStore(conn, sqlTx);
        var row = await store.GetAsync(id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return Stale("package");
        if (row.Status is not (PackageStore.Draft or PackageStore.Imported))
            return Problem(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The package is {row.Status}; only a built package that has not been approved, or an imported package that has not been deployed, can be discarded.");
        if (row.Status == PackageStore.Imported && !http.User.HasClaim(Permissions.ClaimType, Permissions.ConfigPackageDeploy))
            return Problem(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Missing permission '{Permissions.ConfigPackageDeploy}'");

        var columns = new Dictionary<string, object?>
        {
            ["status"] = PackageStore.Discarded, ["discarded_by"] = ctx.UserId, ["discarded_at"] = DateTimeOffset.UtcNow, ["discard_reason"] = comment,
        };
        if (!await store.SetAsync(id, expected, columns, ct)) return Stale("package");
        var updated = (await store.GetAsync(id, forUpdate: false, ct))!;
        var response = Response(updated, await store.DocumentAsync(updated, PackageSignable.PackagePlatformVersion(updated), ct));
        await tx.CommitAsync(ct);

        SetETag(http, updated.RowVersion);
        return Results.Ok(response);
    }

    /// <summary>
    /// Imports a package file. Nothing is stored unless the file is well formed, its hash matches, its signature is valid under a
    /// key this installation trusts, and every item passes a dry run against what is released here.
    /// </summary>
    private static async Task<IResult> Import(
        JsonObject body, PlatformDbContext db, IGxpContextAccessor gxp, IPackageSigner signer, IOptions<ConfigPackageOptions> options,
        CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var trusted = await PackageTrust.KeysAsync(options.Value, signer, ct);

        var verification = PackageVerifier.Verify(body, trusted);
        if (verification.Package is not { } package) return Rejected(verification.Problems);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = Sql(db);
        var packages = new PackageStore(conn, sqlTx);
        if (await packages.CodeExistsAsync(package.Document.Code, ct))
            return Problem(StatusCodes.Status409Conflict, "PACKAGE_EXISTS", "Package already here",
                $"A package with code {package.Document.Code} already exists in this installation.");

        var report = await PackageDryRun.RunAsync(new WorkflowDraftStore(conn, sqlTx), new WorkflowStore(conn, sqlTx), package.Document, ct);
        if (report.Any(r => r.Outcome == PackageDryRun.Refused))
            return Rejected(report.SelectMany(r => r.Problems.Select(p => $"{r.Code} version {r.Version}: {p}")), report);

        var log = new JsonObject
        {
            ["trustedAs"] = package.TrustedAs, ["importedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["items"] = new JsonArray([.. report.Select(r => (JsonNode)new JsonObject
                { ["code"] = r.Code, ["version"] = r.Version, ["outcome"] = r.Outcome })]),
        };
        var id = await packages.InsertAsync(ctx, package.Document, PackageStore.Imported, new Dictionary<(string, int), Guid>(),
            package.SignatureValue, (body["signature"] as JsonObject)?["keyName"]?.GetValue<string>(), package.KeyVersion, log, ct);
        var row = (await packages.GetAsync(id, forUpdate: false, ct))!;
        await tx.CommitAsync(ct);
        return Results.Created($"{Base}/{id}", Response(row, package.Document));
    }

    private static IResult Rejected(IEnumerable<string> problems, IReadOnlyList<DryRunItem>? report = null) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Package refused",
            detail: "The package was not imported. Nothing was stored.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "PACKAGE_REFUSED",
                ["problems"] = problems.ToList(),
                ["items"] = report?.Select(r => new { r.Code, r.Version, r.Outcome, r.Problems }),
            });

    private static object Response(PackageRow r, PackageDocument? document, IReadOnlyList<DeploymentRow>? deployments = null) => new
    {
        deployments,
        id = r.Id, code = r.Code, name = r.Name, versionLabel = r.VersionLabel, sourceEnvironment = r.SourceEnvironment,
        contentHash = r.ContentHash, status = r.Status, manifest = r.Manifest, approvedBy = r.ApprovedBy, approvedAt = r.ApprovedAt,
        approvalSignatureId = r.ApprovalSignatureId, signingKeyVersion = r.SigningKeyVersion, exportedAt = r.ExportedAt,
        importLog = r.ImportLog, rowVersion = r.RowVersion, updatedAt = r.UpdatedAt,
        discardedBy = r.DiscardedBy, discardedAt = r.DiscardedAt, discardReason = r.DiscardReason,
        items = document?.Items.Select(i => new { artifactType = i.ArtifactType, code = i.Code, version = i.Version }),
    };

    private static string? Text(JsonObject body, string name, Dictionary<string, string[]> errors)
    {
        if (body[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        errors[name] = ["Required."];
        return null;
    }
}
