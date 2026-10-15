using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PharmaOS.Platform.Host;
using PharmaOS.Platform.Host.Security;

namespace PharmaOS.IntegrationTests;

/// <summary>Server-side session (BFF) behaviour that does not need a running Keycloak.</summary>
[Collection(PostgresFixture.Collection)]
public sealed class BffTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly TestUser Analyst = new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("0000000a-0000-0000-0000-000000000000"), "Quinn Analyst", "qa.analyst",
        Permissions.UomRead, Permissions.AuditTrailRead);

    private readonly PlatformFactory _factory;

    public BffTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    [Fact]
    [Trait("Req", "URS-SEC-001")]
    public async Task Cookie_style_api_call_without_csrf_header_is_refused()
    {
        var res = await _factory.ClientFor(Analyst, csrf: false).GetAsync("/api/mdm/v1/uoms");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("CSRF", await res.Content.ReadAsStringAsync());

        var ok = await _factory.ClientFor(Analyst).GetAsync("/api/mdm/v1/uoms");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Csrf_check_does_not_block_unauthenticated_requests_from_getting_401()
    {
        var res = await _factory.CreateClient().GetAsync("/api/mdm/v1/uoms");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task User_endpoint_describes_the_signed_in_user_without_tokens()
    {
        var anon = await _factory.CreateClient().GetAsync("/bff/user");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var body = await _factory.ClientFor(Analyst).GetFromJsonAsync<JsonElement>("/bff/user");
        Assert.Equal("qa.analyst", body.GetProperty("login").GetString());
        Assert.Equal("Quinn Analyst", body.GetProperty("name").GetString());
        Assert.Equal("0000000a-0000-0000-0000-000000000000", body.GetProperty("tenantId").GetString());
        Assert.Equal([Permissions.AuditTrailRead, Permissions.UomRead],
            body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        var raw = body.GetRawText();
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_requires_the_matching_session_id()
    {
        var res = await _factory.ClientFor(Analyst).GetAsync("/bff/logout?sid=guessed");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData(null, "http://localhost/signin-oidc")]
    [InlineData("https://pharmaos-4200.app.github.dev/", "https://pharmaos-4200.app.github.dev/signin-oidc")]
    public async Task Sign_in_callback_uses_the_public_origin_when_configured(string? publicOrigin, string expected)
    {
        // Behind port forwarding the server sees localhost, but Keycloak must send the browser back to the public address.
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            if (publicOrigin is not null) b.UseSetting("Oidc:PublicOrigin", publicOrigin);
            b.ConfigureTestServices(s => s.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = "https://idp.example/auth" }));
        });
        var res = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/bff/login");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(res.Headers.Location!.Query);
        Assert.Equal(expected, query["redirect_uri"]);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/uoms/1?x=2", "/uoms/1?x=2")]
    [InlineData("//evil.example/phish", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void Return_url_must_be_local(string? input, string expected) =>
        Assert.Equal(expected, Bff.SafeReturnUrl(input));

    [Fact]
    [Trait("Req", "URS-SEC-014")]
    public void Recent_authentication_window()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        ClaimsPrincipal At(DateTimeOffset t) =>
            new(new ClaimsIdentity([new Claim("auth_time", t.ToUnixTimeSeconds().ToString())], "test"));

        Assert.True(AuthenticationRecency.IsRecent(At(now.AddSeconds(-90)), now, TimeSpan.FromMinutes(2)));
        Assert.False(AuthenticationRecency.IsRecent(At(now.AddMinutes(-3)), now, TimeSpan.FromMinutes(2)));
        Assert.False(AuthenticationRecency.IsRecent(At(now.AddMinutes(5)), now, TimeSpan.FromMinutes(2)));
        Assert.False(AuthenticationRecency.IsRecent(new ClaimsPrincipal(new ClaimsIdentity()), now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Session_store_keeps_ticket_server_side_under_an_unguessable_key()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedCacheTicketStore(cache);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "Cookies")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15) }, "Cookies");
        ticket.Properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "secret-token" }]);

        var key = await store.StoreAsync(ticket);
        Assert.Matches("^[0-9a-f]{64}$", key);
        var back = await store.RetrieveAsync(key);
        Assert.Equal("u1", back!.Principal.FindFirst("sub")!.Value);
        Assert.Equal("secret-token", back.Properties.GetTokenValue("access_token"));

        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
    }
}
