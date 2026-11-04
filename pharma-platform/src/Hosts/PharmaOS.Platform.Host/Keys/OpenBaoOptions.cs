namespace PharmaOS.Platform.Host.Keys;

/// <summary>Where OpenBao is and which transit key signs packages. Configure with <c>Keys__OpenBaoAddress</c> and <c>Keys__OpenBaoToken</c>.</summary>
public sealed class OpenBaoOptions
{
    public const string Section = "Keys";

    /// <summary>Base URL, e.g. <c>http://openbao:8200</c>. Empty means signing is not configured.</summary>
    public string OpenBaoAddress { get; set; } = "";

    /// <summary>
    /// A fixed token, for development and tests. Production uses AppRole instead, because a fixed token expires or must be renewed.
    /// Its policy should allow only reading, signing and verifying with the package key (see deploy/compose/openbao-bootstrap.sh).
    /// </summary>
    public string OpenBaoToken { get; set; } = "";

    /// <summary>AppRole login: the host exchanges these for a short-lived token and logs in again when it expires.</summary>
    public string AppRoleId { get; set; } = "";
    public string AppRoleSecretId { get; set; } = "";
    public string AppRoleMount { get; set; } = "approle";

    /// <summary>Where the transit engine is mounted.</summary>
    public string TransitMount { get; set; } = "transit";

    /// <summary>The transit key (type ecdsa-p256, not exportable) that signs configuration packages.</summary>
    public string PackageKey { get; set; } = "pharmaos-packages";

    public int TimeoutSeconds { get; set; } = 10;
}
