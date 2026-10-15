using Microsoft.AspNetCore.Authorization;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host;

public static class Permissions
{
    public const string ClaimType = "perm";
    public const string UomRead = "mdm.uom:read";
    public const string UomWrite = "mdm.uom:write";
    public const string AuditTrailRead = "audit.trail:read";

    /// <summary>
    /// May execute electronic signatures. Grant only after the person's identity has been verified
    /// (21 CFR Part 11 §11.100(b)); the Keycloak role assignment is that controlled step.
    /// </summary>
    public const string EsigSign = "esig.sign";

    /// <summary>
    /// One policy per permission, satisfied by a "perm" claim. In production the identity-access service
    /// resolves role assignments (scoped to org nodes) into these claims; Keycloak roles are not used directly.
    /// </summary>
    public static AuthorizationBuilder AddPermissionPolicy(this AuthorizationBuilder builder, string permission) =>
        builder.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireClaim(ClaimType, permission));
}

/// <summary>Builds the GxP context from the validated token and request headers.</summary>
public sealed class HttpGxpContextAccessor(IHttpContextAccessor http) : IGxpContextAccessor
{
    private const string CorrelationItem = "pharmaos.correlation";

    public GxpContext? Current
    {
        get
        {
            var ctx = http.HttpContext;
            var user = ctx?.User;
            if (ctx is null || user?.Identity?.IsAuthenticated != true) return null;
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)
                || !Guid.TryParse(user.FindFirst("tenant_id")?.Value, out var tenantId))
                return null;

            var name = user.FindFirst("name")?.Value;
            var login = user.FindFirst("preferred_username")?.Value;
            var display = (name, login) switch
            {
                ({ } n, { } l) => $"{n} ({l})",
                ({ } n, null) => n,
                (null, { } l) => l,
                _ => userId.ToString("D"),
            };

            if (ctx.Items[CorrelationItem] is not Guid correlation)
            {
                correlation = Guid.TryParse(ctx.Request.Headers["X-Correlation-Id"], out var supplied)
                    ? supplied
                    : Guid.CreateVersion7();
                ctx.Items[CorrelationItem] = correlation;
            }

            var reason = ctx.Request.Headers["X-Change-Reason"].ToString();
            return new GxpContext(
                tenantId,
                userId,
                display,
                OnBehalfOf: null,
                ChangeReason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                correlation,
                ClientApp: "platform-host");
        }
    }
}
