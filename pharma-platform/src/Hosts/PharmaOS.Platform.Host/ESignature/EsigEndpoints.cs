using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PharmaOS.ESig;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Security;

namespace PharmaOS.Platform.Host.ESignature;

public sealed record CreateChallengeRequest(
    string? Entity, Guid RecordId, int? RecordVersion, string? Meaning, string? Reason, bool Acknowledge, string? Action = null);

public sealed record ChallengeResponse(Guid ChallengeId, DateTimeOffset ExpiresAt, int RecordVersion, string RecordHash);

public sealed record SignatureResponse(
    Guid Id,
    string SignerName,
    string? SignerLogin,
    string Meaning,
    string MeaningLabel,
    DateTimeOffset SignedAt,
    int RecordVersion,
    string RecordHash,
    string? Reason,
    string? Manifest,
    bool AppliesToCurrentVersion);

/// <summary>
/// Electronic signatures (DS §10.5, doc 14). Two steps around a Keycloak re-authentication:
/// <list type="number">
/// <item>POST challenges: the signer states what they sign (record, version, meaning, reason). The server
/// binds the record hash and checks segregation of duties before anyone types a password.</item>
/// <item>The browser goes to /bff/reauth; Keycloak asks for the password again (prompt=login, max_age=0).</item>
/// <item>POST challenges/{id}/complete: the server requires an auth_time after the challenge was issued,
/// re-checks the record under a lock, and writes the signature, its manifest and the audit entry in one transaction.</item>
/// </list>
/// The password is only ever typed at Keycloak.
/// </summary>
public static class EsigEndpoints
{
    private const string Base = "/api/esig/v1";

    public static void MapEsigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Base);
        group.MapGet("/meanings", Meanings).RequireAuthorization(Permissions.EsigSign);
        group.MapPost("/challenges", CreateChallenge).RequireAuthorization(Permissions.EsigSign);
        group.MapPost("/challenges/{id:guid}/complete", Complete).RequireAuthorization(Permissions.EsigSign);
        // Reading signatures needs read access to the record type, checked per entity below.
        group.MapGet("/records/{entity}/{recordId:guid}/signatures", List);
    }

    private static async Task<IResult> Meanings(
        string? entity, SignableRegistry registry, PlatformDbContext db, IGxpContextAccessor gxp, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (registry.Find(entity) is not { } source) return UnknownEntity(entity);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var acknowledged = await Store(db).HasAcknowledgedAsync(ctx.UserId, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new
        {
            statement = new { version = EsigCatalog.StatementVersion, text = EsigCatalog.SignatureStatement, acknowledged },
            meanings = source.Meanings.Select(code => EsigCatalog.FindMeaning(code)!).Select(m => new
            {
                code = m.Code, label = m.Label, statement = m.Statement, requiresReason = m.RequireReason,
                requiresSecondPerson = m.RequireSecondPerson,
            }),
        });
    }

    private static async Task<IResult> CreateChallenge(
        CreateChallengeRequest body, SignableRegistry registry, PlatformDbContext db, IGxpContextAccessor gxp,
        TimeProvider time, IConfiguration config, ClaimsPrincipal user, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        if (registry.Find(body.Entity) is not { } source) return UnknownEntity(body.Entity);
        var meaning = EsigCatalog.FindMeaning(body.Meaning);
        if (meaning is null || !source.Meanings.Contains(meaning.Code))
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["meaning"] = [$"Allowed for {source.EntityCode}: {string.Join(", ", source.Meanings)}."] });
        var reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
        if (reason is { Length: > 1000 })
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Maximum length is 1000."] });
        if (meaning.RequireReason && reason is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A reason is required for this signature."] });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var store = Store(db);
        var record = await source.LoadAsync(store.Connection, store.Transaction, body.RecordId, lockRow: false, ct);
        if (record is null) return Results.NotFound();
        if (body.RecordVersion is { } shown && shown != record.Version)
            return Problem(StatusCodes.Status409Conflict, "RECORD_CHANGED", "Record changed",
                $"You are looking at version {shown}; the record is now at version {record.Version}. Reload it before signing.");

        if (!await store.HasAcknowledgedAsync(ctx.UserId, ct) && !body.Acknowledge)
            return Problem(StatusCodes.Status400BadRequest, "ACKNOWLEDGEMENT_REQUIRED", "Acknowledgement required",
                EsigCatalog.SignatureStatement);

        var action = string.IsNullOrWhiteSpace(body.Action) ? null : body.Action.Trim();
        if (action is null && source.SignaturesRequireAction)
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["action"] = [$"Signatures on {source.EntityCode} are given with a workflow action."] });
        if (action is not null
            && await source.CheckActionAsync(store.Connection, store.Transaction,
                new SignedAction(ctx, PermissionsOf(user), record.Id, action, meaning, reason, time.GetUtcNow()), ct) is { } refusal)
            return Problem(refusal.Status, refusal.Code, refusal.Title, refusal.Detail);

        // Segregation of duties is checked now, so nobody types a password for a signature that would be refused.
        var edId = await store.EntityDefinitionIdAsync(source.EntityCode, ct);
        var prior = edId is { } ed ? await store.SignaturesOnRecordAsync(ed, record.Id, ct) : [];
        var preview = Evidence(Guid.Empty, ctx, source, meaning, record.Id, record.Version, record.Hash, time.GetUtcNow(), reason);
        if (SignatureBinding.Verify(preview, ctx.UserId, record.Id, record.Version, record.Hash, meaning.Policy, record.LastEditor, prior)
            == BindingResult.SegregationOfDuties)
            return SegregationOfDuties(meaning);

        await store.EnsureUserAsync(ctx, Login(user), SignerName(user), user.FindFirst("email")?.Value, ct);
        var id = Guid.CreateVersion7();
        var issued = Microseconds(time.GetUtcNow());
        var expires = issued.AddSeconds(config.GetValue("ESig:ChallengeTtlSeconds", 120));
        await store.InsertChallengeAsync(ctx, id, issued, expires,
            new ChallengeIntent(source.EntityCode, record.Id, record.Version, record.Hash, meaning.Code, reason, body.Acknowledge, action), ct);
        await tx.CommitAsync(ct);
        return Results.Created($"{Base}/challenges/{id}", new ChallengeResponse(id, expires, record.Version, record.Hash));
    }

    private static async Task<IResult> Complete(
        Guid id, SignableRegistry registry, PlatformDbContext db, IGxpContextAccessor gxp, TimeProvider time,
        ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct)
    {
        if (gxp.Current is not { } ctx) return MissingIdentity();
        var now = Microseconds(time.GetUtcNow());
        var log = logs.CreateLogger("PharmaOS.ESig");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var store = Store(db);
        var challenge = await store.LockChallengeAsync(id, ct);
        if (challenge is null) return Results.NotFound();
        var check = Reauthentication.Check(challenge.UserId, ctx.UserId, challenge.IssuedAt, challenge.ExpiresAt,
            challenge.ConsumedAt, AuthenticationRecency.AuthTime(user), now);
        // Another user's challenge is reported as not found and left untouched.
        if (check == ChallengeCheck.WrongUser) return Results.NotFound();
        if (check != ChallengeCheck.Ok)
        {
            var (status, code, title, detail) = check switch
            {
                ChallengeCheck.AlreadyUsed => (StatusCodes.Status409Conflict, "CHALLENGE_USED", "Already used",
                    "This signing request was already used. Start the signature again."),
                ChallengeCheck.Expired => (StatusCodes.Status410Gone, "CHALLENGE_EXPIRED", "Signing request expired",
                    "The signing request expired before the password was confirmed. Start the signature again."),
                _ => (StatusCodes.Status403Forbidden, "AUTHENTICATION_NOT_FRESH", "Password not confirmed",
                    "The signature needs your password to be entered at sign-in after the signing request. Start the signature again."),
            };
            return await FailAsync(db, tx, ctx, now, id, challenge.Intent.RecordId, code, status, title, detail, consume: check != ChallengeCheck.AlreadyUsed, log, ct);
        }

        var intent = challenge.Intent;
        var source = registry.Find(intent.Entity) ?? throw new InvalidOperationException($"Unknown entity {intent.Entity}");
        var meaning = EsigCatalog.FindMeaning(intent.Meaning) ?? throw new InvalidOperationException($"Unknown meaning {intent.Meaning}");
        var record = await source.LoadAsync(store.Connection, store.Transaction, intent.RecordId, lockRow: true, ct);
        if (record is null) return Results.NotFound();

        var edId = await store.EnsureEntityDefinitionAsync(ctx, source, ct);
        var prior = await store.SignaturesOnRecordAsync(edId, record.Id, ct);
        var evidence = Evidence(Guid.CreateVersion7(), ctx, source, meaning, intent.RecordId, intent.RecordVersion, intent.RecordHash, now, intent.Reason);
        switch (SignatureBinding.Verify(evidence, ctx.UserId, record.Id, record.Version, record.Hash, meaning.Policy, record.LastEditor, prior))
        {
            case BindingResult.Bound:
                break;
            case BindingResult.SegregationOfDuties:
                return await FailAsync(db, tx, ctx, now, id, record.Id, "SEGREGATION_OF_DUTIES", StatusCodes.Status403Forbidden,
                    "Segregation of duties", SodDetail(meaning), consume: true, log, ct);
            default:
                return await FailAsync(db, tx, ctx, now, id, record.Id, "RECORD_CHANGED", StatusCodes.Status409Conflict,
                    "Record changed", "The record changed after you started signing. Review the new version and sign again.", consume: true, log, ct);
        }

        var signerName = SignerName(user);
        var login = Login(user);
        var meaningId = await store.EnsureMeaningAsync(ctx, meaning, ct);
        var manifest = SignatureManifest.Render(signerName, login, meaning.Label, meaning.Statement, now, source.EntityName,
            record.Number, intent.RecordVersion, intent.RecordHash, intent.Reason);
        var signatureHash = SignatureBinding.ComputeSignatureHash(evidence);
        await store.InsertSignatureAsync(ctx, evidence, edId, meaningId, signerName, meaning.Label, signatureHash,
            new
            {
                signerLogin = login,
                meaningStatement = meaning.Statement,
                challengeId = id,
                action = intent.Action,
                authTime = AuthenticationRecency.AuthTime(user),
                sessionId = user.FindFirst("sid")?.Value,
            },
            manifest, intent.Acknowledge, ct);
        // The workflow action the signature authorises happens in the same transaction, or not at all.
        if (intent.Action is { } signedAction
            && await source.ApplyActionAsync(store.Connection, store.Transaction,
                new SignedAction(ctx, PermissionsOf(user), record.Id, signedAction, meaning, intent.Reason, now), evidence.SignatureId, ct) is { } refused)
            return await FailAsync(db, tx, ctx, now, id, record.Id, refused.Code, refused.Status, refused.Title, refused.Detail, consume: true, log, ct);
        await store.ConsumeChallengeAsync(id, now, ct);
        await tx.CommitAsync(ct);

        return Results.Created($"{Base}/records/{source.EntityCode}/{record.Id}/signatures",
            new SignatureResponse(evidence.SignatureId, signerName, login, meaning.Code, meaning.Label, now, intent.RecordVersion,
                intent.RecordHash, intent.Reason, manifest, AppliesToCurrentVersion: true));
    }

    private static async Task<IResult> List(
        string entity, Guid recordId, SignableRegistry registry, PlatformDbContext db, IAuthorizationService authz,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (registry.Find(entity) is not { } source) return UnknownEntity(entity);
        if (!(await authz.AuthorizeAsync(user, source.ReadPermission)).Succeeded) return Results.Forbid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var store = Store(db);
        var record = await source.LoadAsync(store.Connection, store.Transaction, recordId, lockRow: false, ct);
        if (record is null) return Results.NotFound();
        var signatures = await store.SignaturesForRecordAsync(source.EntityCode, recordId, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new
        {
            currentVersion = record.Version,
            items = signatures.Select(s => new SignatureResponse(s.Id, s.SignerName, s.SignerLogin, s.Meaning, s.MeaningLabel,
                s.SignedAt, s.RecordVersion, s.RecordHash, s.Reason, s.Manifest,
                // The record hash covers the signed content; it is unchanged when only the workflow state moved on.
                string.Equals(s.RecordHash, record.Hash, StringComparison.OrdinalIgnoreCase))),
        });
    }

    /// <summary>
    /// A refused completion still consumes the challenge (single use) and is recorded in esig.signature_failure,
    /// in its own transaction because the signing transaction is rolled back.
    /// </summary>
    private static async Task<IResult> FailAsync(
        PlatformDbContext db, IDbContextTransaction tx, GxpContext ctx, DateTimeOffset now, Guid challengeId, Guid? recordId,
        string code, int status, string title, string detail, bool consume, ILogger log, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        await tx.DisposeAsync();
        await using (var failTx = await db.Database.BeginTransactionAsync(ct))
        {
            var store = Store(db);
            if (consume) await store.ConsumeChallengeAsync(challengeId, now, ct);
            await store.InsertFailureAsync(ctx, now, code, recordId, challengeId, ct);
            await failTx.CommitAsync(ct);
        }
        log.LogWarning("Signature refused ({Code}) for challenge {ChallengeId}", code, challengeId);
        return Problem(status, code, title, detail);
    }

    private static SignatureEvidence Evidence(
        Guid id, GxpContext ctx, ISignableSource source, MeaningDefinition meaning, Guid recordId, int version, string hash,
        DateTimeOffset at, string? reason) =>
        new(id, ctx.UserId, meaning.Code, source.EntityCode, recordId, version, hash, at, reason, PolicyId: null);

    /// <summary>PostgreSQL keeps microseconds; the signature hash must be recomputable from the stored signed_at.</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset t) => new(t.Ticks - t.Ticks % 10, t.Offset);

    private static EsigStore Store(PlatformDbContext db) =>
        new((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());

    private static IReadOnlySet<string> PermissionsOf(ClaimsPrincipal user) => user.PermissionsOf();

    private static string Login(ClaimsPrincipal user) => user.Login();

    private static string SignerName(ClaimsPrincipal user) => user.DisplayName();

    private static string SodDetail(MeaningDefinition meaning) =>
        $"You cannot sign '{meaning.Label}' for a version you last changed, or for a record you already signed as "
        + $"{string.Join(" or ", meaning.ConflictingMeanings.Order(StringComparer.Ordinal).Select(c => EsigCatalog.FindMeaning(c)?.Label ?? c))}. "
        + "Another person must sign.";

    private static IResult SegregationOfDuties(MeaningDefinition meaning) =>
        Problem(StatusCodes.Status403Forbidden, "SEGREGATION_OF_DUTIES", "Segregation of duties", SodDetail(meaning));

    private static IResult UnknownEntity(string? entity) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["entity"] = [$"'{entity}' cannot be signed."] });

    private static IResult MissingIdentity() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing identity",
            detail: "The session must carry 'sub' and 'tenant_id' claims.");

    private static IResult Problem(int status, string code, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
