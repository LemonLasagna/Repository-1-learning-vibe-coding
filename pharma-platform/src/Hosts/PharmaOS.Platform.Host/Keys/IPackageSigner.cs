namespace PharmaOS.Platform.Host.Keys;

/// <summary>
/// Signs and verifies with a key the platform never holds. The private key stays inside OpenBao (transit engine, not exportable);
/// the host sends the bytes to be signed and receives a signature, so a copy of the host's configuration or database cannot be used
/// to sign anything without also reaching OpenBao with a valid token.
/// </summary>
public interface IPackageSigner
{
    /// <summary>Signs <paramref name="data"/> with the newest version of the package signing key.</summary>
    Task<PackageSignature> SignAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Asks OpenBao whether <paramref name="signature"/> is valid for <paramref name="data"/> under this installation's own key.</summary>
    Task<bool> VerifyAsync(ReadOnlyMemory<byte> data, string signature, CancellationToken ct);

    /// <summary>The key's name, newest version and the public key of every version, for checking signatures without OpenBao.</summary>
    Task<SigningKeyInfo> KeyInfoAsync(CancellationToken ct);
}

/// <summary>A signature in OpenBao's own text form (<c>vault:v1:…</c>), the key it was made with and that key's version.</summary>
public sealed record PackageSignature(string Signature, string KeyName, int KeyVersion);

/// <summary>Public side of the signing key. <see cref="PublicKeysPem"/> maps each key version to its PEM public key.</summary>
public sealed record SigningKeyInfo(string Name, int LatestVersion, IReadOnlyDictionary<int, string> PublicKeysPem);

/// <summary>OpenBao could not be reached, refused the request, or answered in a way the platform does not understand.</summary>
public sealed class SigningUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
