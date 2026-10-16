using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace PharmaOS.Platform.Host.Security;

/// <summary>
/// Backend-for-frontend sign-in (doc 14 §14.2). The browser holds only an HttpOnly session cookie;
/// OIDC tokens stay on the server in the session store. Machine clients may still call the API
/// with a bearer token.
/// </summary>
public static class Bff
{
    public const string PolicyScheme = "pharmaos";
    public const string CsrfHeader = "X-CSRF";
    private const string ReauthItem = "pharmaos.reauth_sub";
    private const string ReauthMismatch = "Re-authentication was performed by a different user.";

    public static void AddPharmaOsAuthentication(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        // Fail at startup, not on every request: without these the OIDC handler rejects all traffic.
        foreach (var key in new[] { "Oidc:Authority", "Oidc:ClientId", "Oidc:Audience" })
            if (string.IsNullOrWhiteSpace(cfg[key]))
                throw new InvalidOperationException(
                    $"Configuration '{key}' is missing. Is appsettings.json in the content root ({builder.Environment.ContentRootPath})?");
        // Where the browser reaches the application, when it differs from the address the server sees (e.g. behind
        // GitHub Codespaces port forwarding). The sign-in callbacks sent to Keycloak are built from it.
        var publicOrigin = cfg["Oidc:PublicOrigin"]?.TrimEnd('/');
        if (publicOrigin is { Length: > 0 } && !Uri.TryCreate(publicOrigin, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Configuration 'Oidc:PublicOrigin' is not an absolute URL: '{publicOrigin}'.");
        // Optional: read Keycloak's metadata over an internal address while the issuer stays the public one.
        var metadataAddress = cfg["Oidc:MetadataAddress"];
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<ITicketStore, DistributedCacheTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);

        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = PolicyScheme;
                o.DefaultChallengeScheme = PolicyScheme;
            })
            .AddPolicyScheme(PolicyScheme, "Bearer token or session cookie", o =>
                o.ForwardDefaultSelector = ctx => IsBearer(ctx.Request)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = cfg["Session:CookieName"] ?? "__Host-pharmaos";
                o.Cookie.HttpOnly = true;
                o.Cookie.Path = "/";
                // Lax, not Strict: the cookie is set on the redirect back from Keycloak (a cross-site navigation).
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = cfg.GetValue("Session:SecureCookies", true)
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(cfg.GetValue("Session:IdleTimeoutMinutes", 15));
                o.SlidingExpiration = true;
                // The SPA starts sign-in explicitly (/bff/login); APIs answer 401/403, never a redirect.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            })
            .AddOpenIdConnect(o =>
            {
                o.Authority = cfg["Oidc:Authority"];
                o.ClientId = cfg["Oidc:ClientId"];
                o.ClientSecret = cfg["Oidc:ClientSecret"];
                o.RequireHttpsMetadata = cfg.GetValue("Oidc:RequireHttpsMetadata", true);
                if (!string.IsNullOrEmpty(metadataAddress)) o.MetadataAddress = metadataAddress;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = true; // kept in the server-side session store, never sent to the browser
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                // auth_time and acr record when and how the user last authenticated (e-signature re-auth).
                o.ClaimActions.Remove("auth_time");
                o.ClaimActions.Remove("acr");
                o.Events.OnRedirectToIdentityProvider = ctx =>
                {
                    if (!string.IsNullOrEmpty(publicOrigin)) ctx.ProtocolMessage.RedirectUri = publicOrigin + ctx.Options.CallbackPath;
                    if (ctx.Properties.Items.TryGetValue(ReauthItem, out _))
                    {
                        ctx.ProtocolMessage.Prompt = "login";
                        ctx.ProtocolMessage.MaxAge = "0";
                        if (ctx.Properties.Items.TryGetValue("login_hint", out var hint)) ctx.ProtocolMessage.LoginHint = hint;
                    }
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToIdentityProviderForSignOut = ctx =>
                {
                    if (!string.IsNullOrEmpty(publicOrigin))
                        ctx.ProtocolMessage.PostLogoutRedirectUri = publicOrigin + ctx.Options.SignedOutCallbackPath;
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = ctx =>
                {
                    // Re-authentication must be by the same person; otherwise the session would silently switch user.
                    if (ctx.Properties?.Items.TryGetValue(ReauthItem, out var expected) == true
                        && ctx.Principal?.FindFirst("sub")?.Value != expected)
                        ctx.Fail(ReauthMismatch);
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = ctx =>
                {
                    // The existing session is untouched on any failure. A different user completing a
                    // re-authentication is a security event; anything else (e.g. cancel at Keycloak) goes home.
                    ctx.HandleResponse();
                    var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PharmaOS.Bff");
                    if (ctx.Failure?.Message == ReauthMismatch)
                    {
                        log.LogWarning("Re-authentication was completed by a different user; refused. Correlation {TraceId}",
                            ctx.HttpContext.TraceIdentifier);
                        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ReauthMismatch,
                            detail: "The session was not changed. Sign the record as yourself.").ExecuteAsync(ctx.HttpContext);
                    }
                    log.LogInformation("Remote sign-in did not complete: {Reason}", ctx.Failure?.Message);
                    ctx.Response.Redirect("/");
                    return Task.CompletedTask;
                };
            })
            .AddJwtBearer(o =>
            {
                o.Authority = cfg["Oidc:Authority"];
                o.Audience = cfg["Oidc:Audience"];
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = cfg.GetValue("Oidc:RequireHttpsMetadata", true);
                if (!string.IsNullOrEmpty(metadataAddress)) o.MetadataAddress = metadataAddress;
            });
    }

    public static bool IsBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CSRF defence for cookie sessions: API calls must carry X-CSRF. A cross-site page cannot add a custom
    /// header without a CORS preflight, which this server never grants.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")
                && ctx.User.Identity?.IsAuthenticated == true
                && !IsBearer(ctx.Request)
                && ctx.Request.Headers[CsrfHeader] != "1")
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing CSRF header",
                    detail: $"Requests from the web application must include the {CsrfHeader} header.").ExecuteAsync(ctx);
                return;
            }
            await next();
        });

    public static void MapBffEndpoints(this IEndpointRouteBuilder app)
    {
        var bff = app.MapGroup("/bff");

        bff.MapGet("/login", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // Fresh authentication for an electronic signature (21 CFR Part 11 §11.200): Keycloak must ask
        // for the password again (prompt=login, max_age=0) and the same user must complete it.
        bff.MapGet("/reauth", (string? returnUrl, ClaimsPrincipal user) =>
        {
            var props = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
            props.Items[ReauthItem] = user.FindFirst("sub")?.Value ?? "";
            if (user.FindFirst("preferred_username")?.Value is { } login) props.Items["login_hint"] = login;
            return Results.Challenge(props, [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        // Logout needs the session id, so another site cannot sign the user out with a plain link.
        bff.MapGet("/logout", (string? sid, ClaimsPrincipal user) =>
        {
            var expected = user.FindFirst("sid")?.Value;
            if (expected is null || sid != expected)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid logout request");
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        });

        bff.MapGet("/user", (ClaimsPrincipal user) =>
        {
            var authTime = AuthenticationRecency.AuthTime(user);
            var sid = user.FindFirst("sid")?.Value;
            return Results.Ok(new
            {
                sub = user.FindFirst("sub")?.Value,
                login = user.FindFirst("preferred_username")?.Value,
                name = user.FindFirst("name")?.Value,
                tenantId = user.FindFirst("tenant_id")?.Value,
                permissions = user.FindAll(Permissions.ClaimType).Select(c => c.Value).Order(StringComparer.Ordinal).ToArray(),
                authTime,
                logoutUrl = sid is null ? null : $"/bff/logout?sid={Uri.EscapeDataString(sid)}",
            });
        });
    }

    /// <summary>Only local paths are allowed as return targets (no open redirect).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}

/// <summary>How recently the user typed their credentials, from the OIDC auth_time claim.</summary>
public static class AuthenticationRecency
{
    public static DateTimeOffset? AuthTime(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;

    /// <summary>True when the last authentication is within <paramref name="window"/> (and not in the future).</summary>
    public static bool IsRecent(ClaimsPrincipal user, DateTimeOffset now, TimeSpan window) =>
        AuthTime(user) is { } t && t <= now.AddSeconds(30) && now - t <= window;
}

/// <summary>
/// Server-side session store: the cookie carries only a random key. IDistributedCache is in-memory by
/// default (sessions end on restart); multi-instance deployments plug in Valkey.
/// </summary>
public sealed class DistributedCacheTicketStore(IDistributedCache cache, TimeProvider? time = null) : ITicketStore
{
    private const string Prefix = "pharmaos:session:";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = ticket.Properties.ExpiresUtc ?? _time.GetUtcNow().AddMinutes(15),
        };
        return cache.SetAsync(Prefix + key, TicketSerializer.Default.Serialize(ticket), options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await cache.GetAsync(Prefix + key);
        return bytes is null ? null : TicketSerializer.Default.Deserialize(bytes);
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(Prefix + key);
}
