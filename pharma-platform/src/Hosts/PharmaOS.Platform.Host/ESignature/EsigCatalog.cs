using Npgsql;
using PharmaOS.ESig;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.ESignature;

/// <summary>
/// A signature meaning (§11.50(a)(3)) and the rules that come with it. The standard set is part of the
/// validated release; tenant-specific meanings and policies arrive later as configuration packages (doc 10).
/// </summary>
public sealed record MeaningDefinition(
    string Code,
    string Label,
    string Statement,
    bool RequireReason,
    bool RequireSecondPerson,
    IReadOnlySet<string> ConflictingMeanings)
{
    public SignaturePolicy Policy => new(Guid.Empty, Code, RequireSecondPerson, RequireAllComponentsEverySignature: true, ConflictingMeanings);
}

public static class EsigCatalog
{
    /// <summary>Shown once per user before their first signature, and recorded (user_esig_acknowledgement).</summary>
    public const string StatementVersion = "1";

    public const string SignatureStatement =
        "I understand that my electronic signature is the legally binding equivalent of my handwritten signature.";

    public const string AuthMethod = "KEYCLOAK_REAUTH";

    public static readonly MeaningDefinition Authored = new(
        "AUTHORED", "Authored", "I am the author of this record and confirm that it is complete and correct to the best of my knowledge.",
        RequireReason: false, RequireSecondPerson: false, ConflictingMeanings: new HashSet<string>());

    public static readonly MeaningDefinition Reviewed = new(
        "REVIEWED", "Reviewed", "I have reviewed this record and confirm that it is complete and correct.",
        RequireReason: false, RequireSecondPerson: false, ConflictingMeanings: new HashSet<string>());

    /// <summary>
    /// Four eyes: the last editor of the version cannot approve it, nor can anyone who authored or reviewed
    /// any version of the record.
    /// </summary>
    public static readonly MeaningDefinition Approved = new(
        "APPROVED", "Approved", "I approve this record for use.",
        RequireReason: false, RequireSecondPerson: true, ConflictingMeanings: new HashSet<string> { "REVIEWED", "AUTHORED" });

    public static readonly MeaningDefinition Cancelled = new(
        "CANCELLED", "Cancelled", "I cancel this record for the reason given.",
        RequireReason: true, RequireSecondPerson: false, ConflictingMeanings: new HashSet<string>());

    public static readonly IReadOnlyList<MeaningDefinition> Meanings = [Authored, Reviewed, Approved, Cancelled];

    public static MeaningDefinition? FindMeaning(string? code) =>
        Meanings.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.Ordinal));
}

/// <summary>The state of a record at the moment it is signed.</summary>
public sealed record SignableRecord(Guid Id, string Number, int Version, string Hash, Guid LastEditor);

/// <summary>A workflow action requested together with a signature (e.g. "approve" signed as Approved).</summary>
public sealed record SignedAction(
    GxpContext Context, IReadOnlySet<string> Permissions, Guid RecordId, string Action, MeaningDefinition Meaning,
    string? Reason, DateTimeOffset Now);

/// <summary>Why a signed action cannot go ahead, as an HTTP problem.</summary>
public sealed record ActionRefusal(int Status, string Code, string Title, string Detail);

/// <summary>A record type that can be signed. Each module registers its own.</summary>
public interface ISignableSource
{
    /// <summary>
    /// True when every signature on this record type must authorise a workflow action, so that signature and
    /// state change are one event (doc 21: transitions carry the signature).
    /// </summary>
    bool SignaturesRequireAction => false;

    /// <summary>Checks before the password is asked for. Null means the action can go ahead.</summary>
    Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction action, CancellationToken ct) =>
        Task.FromResult<ActionRefusal?>(new(StatusCodes.Status400BadRequest, "ACTION_NOT_SUPPORTED", "Action not supported",
            $"'{action.Action}' cannot be signed on {EntityCode}."));

    /// <summary>Applies the action in the signing transaction, after the signature row exists.</summary>
    Task<ActionRefusal?> ApplyActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction action, Guid signatureId, CancellationToken ct) =>
        CheckActionAsync(conn, tx, action, ct);

    string EntityCode { get; }
    string EntityName { get; }
    string ModuleCode { get; }
    string ModuleName { get; }
    string PhysicalTable { get; }
    string ReadPermission { get; }
    IReadOnlyList<string> Meanings { get; }

    /// <param name="lockRow">Hold a share lock so the record cannot change before the signature commits.</param>
    Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct);
}

public sealed class SignableRegistry(IEnumerable<ISignableSource> sources)
{
    private readonly Dictionary<string, ISignableSource> _byCode = sources.ToDictionary(s => s.EntityCode, StringComparer.Ordinal);

    public ISignableSource? Find(string? entityCode) =>
        entityCode is not null && _byCode.TryGetValue(entityCode, out var s) ? s : null;
}
