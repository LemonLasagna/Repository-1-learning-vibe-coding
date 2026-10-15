namespace PharmaOS.Gxp;

/// <summary>
/// Who is acting, for which tenant, and why. Every GxP database transaction carries this context;
/// the database rejects GxP writes without it (audit.fn_row_audit, OQ-DI-001).
/// </summary>
public sealed record GxpContext(
    Guid TenantId,
    Guid UserId,
    string UserDisplay,
    Guid? OnBehalfOf,
    string? ChangeReason,
    Guid CorrelationId,
    string ClientApp);

public interface IGxpContextAccessor
{
    /// <summary>The current context, or null when the caller is not an authenticated actor.</summary>
    GxpContext? Current { get; }
}
