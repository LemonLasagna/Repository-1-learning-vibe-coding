using System.Globalization;

namespace PharmaOS.ESig;

public enum ChallengeCheck
{
    Ok,
    WrongUser,
    AlreadyUsed,
    Expired,
    AuthenticationNotFresh,
}

/// <summary>
/// Proof that the signer typed their credentials for this signature (21 CFR Part 11 §11.200):
/// the identity provider's auth_time must fall after the challenge was issued and before it expired.
/// A sign-in from before the challenge, however recent, does not count.
/// </summary>
public static class Reauthentication
{
    /// <summary>Clock difference tolerated between the identity provider and this server.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static ChallengeCheck Check(
        Guid challengeUserId,
        Guid actingUserId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? consumedAt,
        DateTimeOffset? authTime,
        DateTimeOffset now)
    {
        if (challengeUserId != actingUserId) return ChallengeCheck.WrongUser;
        if (consumedAt is not null) return ChallengeCheck.AlreadyUsed;
        if (now > expiresAt) return ChallengeCheck.Expired;
        // auth_time has one-second resolution, so compare against the start of the issuing second.
        var issuedSecond = DateTimeOffset.FromUnixTimeSeconds(issuedAt.ToUnixTimeSeconds());
        if (authTime is not { } t || t < issuedSecond || t > now + ClockSkew || t > expiresAt + ClockSkew)
            return ChallengeCheck.AuthenticationNotFresh;
        return ChallengeCheck.Ok;
    }
}

/// <summary>
/// Human-readable signature manifestation (§11.50): printed name, date and time, and meaning, plus what was signed.
/// </summary>
public static class SignatureManifest
{
    public static string Render(
        string signerName,
        string signerLogin,
        string meaningLabel,
        string meaningStatement,
        DateTimeOffset signedAt,
        string entityName,
        string recordNumber,
        int recordVersion,
        string recordHash,
        string? reason)
    {
        var lines = new List<string>
        {
            $"Electronically signed by {signerName} ({signerLogin})",
            $"Date and time: {signedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
            $"Meaning: {meaningLabel} - \"{meaningStatement}\"",
            $"Record: {entityName} {recordNumber}, version {recordVersion.ToString(CultureInfo.InvariantCulture)}, SHA-256 {recordHash}",
        };
        if (!string.IsNullOrWhiteSpace(reason)) lines.Add($"Reason: {reason}");
        return string.Join('\n', lines);
    }
}
