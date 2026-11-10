using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.Platform.Host.ConfigPackages;

public static class PackageTrust
{
    /// <summary>
    /// The keys this installation accepts package signatures from: those configured in ConfigPackages:TrustedKeys, plus its own
    /// signing key (every version), so a package built and signed here can be imported here. Own keys are left out when signing is
    /// not set up or OpenBao cannot be reached; configured keys still count, so importing does not depend on OpenBao.
    /// </summary>
    public static async Task<IReadOnlyList<TrustedKey>> KeysAsync(ConfigPackageOptions options, IPackageSigner signer, CancellationToken ct)
    {
        var trusted = new List<TrustedKey>(options.TrustedKeys);
        try
        {
            var own = await signer.KeyInfoAsync(ct);
            trusted.AddRange(own.PublicKeysPem.Select(k => new TrustedKey($"this installation ({own.Name})", k.Key, k.Value)));
        }
        catch (SigningUnavailableException)
        {
        }
        return trusted;
    }
}
