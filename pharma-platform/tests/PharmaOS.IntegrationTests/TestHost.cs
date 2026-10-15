using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// Runs the real host against the fixture database. Only authentication is replaced: a test scheme
/// builds the same claims Keycloak would issue (sub, tenant_id, name, preferred_username, perm).
/// The test scheme exists only in this test assembly.
/// </summary>
public sealed class PlatformFactory(PostgresFixture db) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Db", db.AppConnectionString);
        builder.UseSetting("ConnectionStrings:AuditDb", db.AuditConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuth.SchemeName;
                o.DefaultAuthenticateScheme = TestAuth.SchemeName;
                o.DefaultChallengeScheme = TestAuth.SchemeName;
            });
        });
    }

    public HttpClient ClientFor(TestUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuth.Header, user.Encode());
        return client;
    }
}

public sealed record TestUser(Guid Sub, Guid Tenant, string Name, string Login, params string[] Permissions)
{
    public string Encode() => string.Join('|', Sub, Tenant, Name, Login, string.Join(',', Permissions));
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string Header = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var raw)) return Task.FromResult(AuthenticateResult.NoResult());
        var parts = raw.ToString().Split('|');
        var claims = new List<Claim>
        {
            new("sub", parts[0]),
            new("tenant_id", parts[1]),
            new("name", parts[2]),
            new("preferred_username", parts[3]),
        };
        claims.AddRange(parts[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("perm", p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal static class HttpExtensions
{
    public static HttpRequestMessage WithHeaders(this HttpRequestMessage req, string? ifMatch = null, string? reason = null)
    {
        if (ifMatch is not null) req.Headers.IfMatch.Add(new EntityTagHeaderValue(ifMatch));
        if (reason is not null) req.Headers.Add("X-Change-Reason", reason);
        return req;
    }
}
