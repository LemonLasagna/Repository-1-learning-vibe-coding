using System.Text.Json.Nodes;
using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.Platform.Host.ConfigPackages;

/// <summary>A public key this installation accepts package signatures from.</summary>
public sealed record TrustedKey(string Name, int Version, string PublicKeyPem);

/// <summary>
/// Configuration for packages. <see cref="TrustedKeys"/> are the public keys of the installations packages may come from (their
/// <c>pharmaos-packages</c> key, one entry per key version); this installation's own key is always trusted as well. Whoever can
/// change this setting decides what the installation will accept, so it belongs under change control.
/// </summary>
public sealed class ConfigPackageOptions
{
    public const string Section = "ConfigPackages";

    /// <summary>Which environment this installation is (CONFIG, VAL, PROD). Recorded in packages built here.</summary>
    public string EnvironmentName { get; set; } = "DEV";

    public List<TrustedKey> TrustedKeys { get; set; } = [];

    /// <summary>When true, the person who built a package cannot approve it. Off by default so one person can run an installation.</summary>
    public bool RequireSecondPerson { get; set; }
}

public sealed record VerifiedPackage(PackageDocument Document, string ContentHash, string SignatureValue, int KeyVersion, string TrustedAs);

public sealed record VerificationResult(VerifiedPackage? Package, IReadOnlyList<string> Problems)
{
    public bool Ok => Package is not null;
}

/// <summary>
/// Decides whether a package file can be believed: it is well formed, its hash matches its content, and its signature is valid
/// under a key this installation trusts. The public key written inside the file is never used to verify it; anyone could put their
/// own key there.
/// </summary>
public static class PackageVerifier
{
    public static VerificationResult Verify(JsonNode? file, IReadOnlyList<TrustedKey> trusted)
    {
        var problems = new List<string>();
        if (file is not JsonObject o)
            return new(null, ["The file is not a JSON object."]);

        var document = PackageDocument.Parse(o["package"], problems);
        var claimed = o["contentHash"] is JsonValue h && h.TryGetValue<string>(out var s) ? s : null;
        var signature = o["signature"] as JsonObject;
        var value = signature?["value"] is JsonValue sv && sv.TryGetValue<string>(out var sv2) ? sv2 : null;
        if (document is null) return new(null, problems);

        var actual = document.ContentHash;
        if (claimed != actual)
        {
            problems.Add("HASH_MISMATCH: the package's content does not match its recorded hash; it was changed after it was built.");
            return new(null, problems);
        }
        if (string.IsNullOrEmpty(value))
        {
            problems.Add("SIGNATURE_MISSING: the package is not signed.");
            return new(null, problems);
        }

        int version;
        try
        {
            version = PackageSignatureVerifier.VersionOf(value);
        }
        catch (FormatException)
        {
            problems.Add("SIGNATURE_INVALID: the signature is not in a form this platform reads.");
            return new(null, problems);
        }
        var bytes = PackageDocument.SignedBytes(actual);
        foreach (var key in trusted.Where(k => k.Version == version))
            if (PackageSignatureVerifier.Verify(bytes, value, key.PublicKeyPem))
                return new(new VerifiedPackage(document, actual, value, version, key.Name), []);

        problems.Add($"UNTRUSTED_SIGNER: the signature is not valid under any key this installation trusts for key version {version}. " +
                     "Add the source installation's public key to ConfigPackages:TrustedKeys if it should be accepted.");
        return new(null, problems);
    }
}
