using System.Globalization;
using PharmaOS.Gxp;

namespace PharmaOS.ESig;

/// <summary>An executed electronic signature as stored in esig.signature.</summary>
public sealed record SignatureEvidence(
    Guid SignatureId,
    Guid SignerId,
    string MeaningCode,
    string EntityCode,
    Guid RecordId,
    int RecordVersion,
    string RecordHash,
    DateTimeOffset SignedAt,
    string? Reason,
    Guid? PolicyId);

public sealed record SignaturePolicy(
    Guid Id,
    string MeaningCode,
    bool RequireSecondPerson,
    bool RequireAllComponentsEverySignature,
    IReadOnlySet<string> ConflictingMeanings);

public sealed record PriorSignature(Guid SignerId, string MeaningCode);

public enum BindingResult
{
    Bound,
    NotSignersOwnSignature,
    StaleRecordVersion,
    RecordChanged,
    WrongMeaning,
    SegregationOfDuties,
}

public enum RequiredAuthentication
{
    /// <summary>User ID and password (plus second factor where the policy demands it).</summary>
    AllComponents,

    /// <summary>Within a continuous session after a first full signing: at least the password.</summary>
    PasswordOnly,
}

/// <summary>
/// Signature-to-record linking checks (21 CFR Part 11 §11.70) and the signing-component rule
/// for continuous sessions (§11.200). Pure functions: the service calls them inside the
/// transaction that applies the state change.
/// </summary>
public static class SignatureBinding
{
    public static BindingResult Verify(
        SignatureEvidence signature,
        Guid actingUserId,
        Guid recordId,
        int currentRecordVersion,
        string currentRecordHash,
        SignaturePolicy policy,
        Guid? recordAuthorId,
        IEnumerable<PriorSignature> priorSignatures)
    {
        if (signature.SignerId != actingUserId) return BindingResult.NotSignersOwnSignature;
        if (signature.RecordId != recordId || signature.RecordVersion != currentRecordVersion)
            return BindingResult.StaleRecordVersion;
        if (!string.Equals(signature.RecordHash, currentRecordHash, StringComparison.OrdinalIgnoreCase))
            return BindingResult.RecordChanged;
        if (!string.Equals(signature.MeaningCode, policy.MeaningCode, StringComparison.Ordinal))
            return BindingResult.WrongMeaning;
        if (policy.RequireSecondPerson
            && (recordAuthorId == signature.SignerId
                || priorSignatures.Any(p => p.SignerId == signature.SignerId
                                            && policy.ConflictingMeanings.Contains(p.MeaningCode))))
            return BindingResult.SegregationOfDuties;
        return BindingResult.Bound;
    }

    public static RequiredAuthentication RequiredComponents(SignaturePolicy policy, bool hasSignedInCurrentSession) =>
        policy.RequireAllComponentsEverySignature || !hasSignedInCurrentSession
            ? RequiredAuthentication.AllComponents
            : RequiredAuthentication.PasswordOnly;

    /// <summary>signature_hash per DS §10.5. Timestamps are normalised to UTC with 100 ns precision.</summary>
    public static string ComputeSignatureHash(SignatureEvidence s) =>
        CanonicalJson.Sha256Hex(string.Join('|',
            s.SignerId.ToString("D"),
            s.MeaningCode,
            s.SignedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            s.EntityCode,
            s.RecordId.ToString("D"),
            s.RecordVersion.ToString(CultureInfo.InvariantCulture),
            s.RecordHash,
            s.Reason ?? "",
            s.PolicyId?.ToString("D") ?? ""));
}
