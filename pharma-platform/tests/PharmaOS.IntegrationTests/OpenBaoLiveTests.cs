using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// The signer against a real OpenBao that deploy/compose/openbao-bootstrap.sh has prepared. Needs PHARMAOS_TEST_OPENBAO_ADDR,
/// PHARMAOS_TEST_OPENBAO_ROLE_ID and PHARMAOS_TEST_OPENBAO_SECRET_ID, so it runs in its own CI job (Category=OpenBao) and is
/// left out of the main one. It fails, rather than skips, when asked to run without them.
/// </summary>
[Trait("Category", "OpenBao")]
public sealed class OpenBaoLiveTests
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} to run the OpenBao tests (see .github/workflows/pharmaos-schema.yml, job openbao).");

    private static readonly string Address = Env("PHARMAOS_TEST_OPENBAO_ADDR");

    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static OpenBaoTransitSigner Signer()
    {
        var options = Options.Create(new OpenBaoOptions
        {
            OpenBaoAddress = Address,
            AppRoleId = Env("PHARMAOS_TEST_OPENBAO_ROLE_ID"),
            AppRoleSecretId = Env("PHARMAOS_TEST_OPENBAO_SECRET_ID"),
        });
        return new OpenBaoTransitSigner(new HttpClient(), options, new OpenBaoTokenSource(new Clients(), options, TimeProvider.System));
    }

    [Fact]
    public async Task A_signature_made_by_OpenBao_verifies_in_OpenBao_and_with_the_public_key_alone()
    {
        var signer = Signer();
        var data = Encoding.UTF8.GetBytes("configuration package " + Guid.NewGuid());

        var signature = await signer.SignAsync(data, CancellationToken.None);
        var info = await signer.KeyInfoAsync(CancellationToken.None);

        Assert.StartsWith("vault:v", signature.Signature);
        Assert.True(await signer.VerifyAsync(data, signature.Signature, CancellationToken.None));
        Assert.False(await signer.VerifyAsync(Encoding.UTF8.GetBytes("changed"), signature.Signature, CancellationToken.None));
        Assert.Equal(signature.KeyVersion, PackageSignatureVerifier.VersionOf(signature.Signature));
        Assert.True(info.LatestVersion >= signature.KeyVersion);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", info.PublicKeysPem[signature.KeyVersion]);
        Assert.True(PackageSignatureVerifier.Verify(data, signature.Signature, info.PublicKeysPem[signature.KeyVersion]));
        Assert.False(PackageSignatureVerifier.Verify(Encoding.UTF8.GetBytes("changed"), signature.Signature, info.PublicKeysPem[signature.KeyVersion]));
    }

    [Fact]
    public async Task The_hosts_token_can_sign_and_verify_but_cannot_export_the_key_or_see_anything_else()
    {
        var login = await new HttpClient().PostAsJsonAsync($"{Address}/v1/auth/approle/login",
            new { role_id = Env("PHARMAOS_TEST_OPENBAO_ROLE_ID"), secret_id = Env("PHARMAOS_TEST_OPENBAO_SECRET_ID") });
        login.EnsureSuccessStatusCode();
        var token = (string)JsonNode.Parse(await login.Content.ReadAsStringAsync())!["auth"]!["client_token"]!;
        using var http = new HttpClient();

        async Task<HttpStatusCode> Get(string path, HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"{Address}/v1/{path}");
            request.Headers.Add("X-Vault-Token", token);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Get("transit/keys/pharmaos-packages"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("transit/export/signing-key/pharmaos-packages"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("transit/keys/pharmaos-packages/rotate", HttpMethod.Post));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("transit/keys/pharmaos-packages", HttpMethod.Delete));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("transit/keys/another-key"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("sys/mounts"));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("sys/policies/acl/pharmaos-signer"));
    }

    [Fact]
    public async Task A_wrong_secret_id_is_refused_without_revealing_it()
    {
        var options = Options.Create(new OpenBaoOptions { OpenBaoAddress = Address, AppRoleId = Env("PHARMAOS_TEST_OPENBAO_ROLE_ID"), AppRoleSecretId = "wrong-secret-id" });
        var signer = new OpenBaoTransitSigner(new HttpClient(), options, new OpenBaoTokenSource(new Clients(), options, TimeProvider.System));

        var e = await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));

        Assert.Contains("AppRole", e.Message);
        Assert.DoesNotContain("wrong-secret-id", e.Message);
    }
}
