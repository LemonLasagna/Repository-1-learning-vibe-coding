using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PharmaOS.Platform.Host.Keys;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// The signer against a stand-in for OpenBao's HTTP API: what it sends, how it reads the answers and what it does when OpenBao
/// refuses or is not configured. The same requests run against a real OpenBao in <see cref="OpenBaoLiveTests"/>.
/// </summary>
public sealed class OpenBaoSignerTests
{
    private const string Address = "http://openbao.test:8200";

    private sealed class FakeOpenBao(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Token, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Vault-Token", out var t) ? t.Single() : null, body));
            var (status, text) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (OpenBaoTransitSigner Signer, FakeOpenBao Bao) Build(
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer, OpenBaoOptions? options = null, TimeProvider? time = null)
    {
        var bao = new FakeOpenBao(answer);
        var o = Options.Create(options ?? new OpenBaoOptions { OpenBaoAddress = Address, OpenBaoToken = "fixed-token" });
        var tokens = new OpenBaoTokenSource(new Factory(bao), o, time ?? TimeProvider.System);
        return (new OpenBaoTransitSigner(new HttpClient(bao), o, tokens), bao);
    }

    private static (HttpStatusCode, string) Ok(string json) => (HttpStatusCode.OK, json);

    [Fact]
    public async Task Sign_sends_the_bytes_with_the_token_to_the_package_key_and_reads_the_signature()
    {
        var (signer, bao) = Build((_, _) => Ok("""{"data":{"signature":"vault:v3:abc","key_version":3}}"""));

        var result = await signer.SignAsync(Encoding.UTF8.GetBytes("package"), CancellationToken.None);

        Assert.Equal(new PackageSignature("vault:v3:abc", "pharmaos-packages", 3), result);
        var request = Assert.Single(bao.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/transit/sign/pharmaos-packages/sha2-256", request.Path);
        Assert.Equal("fixed-token", request.Token);
        var sent = JsonNode.Parse(request.Body)!;
        Assert.Equal(Convert.ToBase64String("package"u8), (string?)sent["input"]);
        Assert.Equal("jws", (string?)sent["marshaling_algorithm"]);
    }

    [Fact]
    public async Task Verify_reports_what_OpenBao_decides()
    {
        var (signer, bao) = Build((_, body) =>
        {
            var valid = JsonNode.Parse(body)!["signature"]!.ToString() == "vault:v1:good";
            return Ok("{\"data\":{\"valid\":" + (valid ? "true" : "false") + "}}");
        });

        Assert.True(await signer.VerifyAsync("x"u8.ToArray(), "vault:v1:good", CancellationToken.None));
        Assert.False(await signer.VerifyAsync("x"u8.ToArray(), "vault:v1:bad", CancellationToken.None));
        Assert.All(bao.Requests, r => Assert.Equal("/v1/transit/verify/pharmaos-packages/sha2-256", r.Path));
    }

    [Fact]
    public async Task KeyInfo_lists_the_public_key_of_every_version()
    {
        var (signer, bao) = Build((_, _) => Ok("""
            {"data":{"type":"ecdsa-p256","latest_version":2,"keys":{"1":{"public_key":"PEM1"},"2":{"public_key":"PEM2"}}}}
            """));

        var info = await signer.KeyInfoAsync(CancellationToken.None);

        Assert.Equal("pharmaos-packages", info.Name);
        Assert.Equal(2, info.LatestVersion);
        Assert.Equal("PEM1", info.PublicKeysPem[1]);
        Assert.Equal("PEM2", info.PublicKeysPem[2]);
        Assert.Equal(HttpMethod.Get, bao.Requests.Single().Method);
        Assert.Equal("/v1/transit/keys/pharmaos-packages", bao.Requests.Single().Path);
    }

    [Fact]
    public async Task KeyInfo_refuses_a_key_of_another_type()
    {
        var (signer, _) = Build((_, _) => Ok("""{"data":{"type":"ed25519","latest_version":1,"keys":{}}}"""));

        var e = await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.KeyInfoAsync(CancellationToken.None));
        Assert.Contains("ecdsa-p256", e.Message);
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_reason_but_never_the_token()
    {
        var (signer, _) = Build((_, _) => (HttpStatusCode.Forbidden, """{"errors":["1 error occurred:\n\t* permission denied"]}"""));

        var e = await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));

        Assert.Contains("403", e.Message);
        Assert.Contains("permission denied", e.Message);
        Assert.DoesNotContain("fixed-token", e.Message);
    }

    [Theory]
    [InlineData("", "token")]
    [InlineData(Address, "")]
    public async Task Without_an_address_or_credentials_signing_says_it_is_not_configured(string address, string token)
    {
        var (signer, bao) = Build((_, _) => Ok("{}"), new OpenBaoOptions { OpenBaoAddress = address, OpenBaoToken = token });

        var e = await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));

        Assert.Contains("not configured", e.Message);
        Assert.Empty(bao.Requests);
    }

    [Fact]
    public async Task An_answer_that_is_not_JSON_is_reported_not_thrown_raw()
    {
        var (signer, _) = Build((_, _) => Ok("<html>proxy error</html>"));

        await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));
    }

    private static OpenBaoOptions AppRole() => new() { OpenBaoAddress = Address, AppRoleId = "role", AppRoleSecretId = "secret" };

    private static (HttpStatusCode, string) AppRoleAnswer(HttpRequestMessage r, string loginToken, int lease = 3600) =>
        r.RequestUri!.AbsolutePath == "/v1/auth/approle/login"
            ? Ok("{\"auth\":{\"client_token\":\"" + loginToken + "\",\"lease_duration\":" + lease + "}}")
            : Ok("""{"data":{"signature":"vault:v1:s","key_version":1}}""");

    [Fact]
    public async Task AppRole_logs_in_once_and_reuses_the_token_while_it_lasts()
    {
        var (signer, bao) = Build((r, _) => AppRoleAnswer(r, "login-token"), AppRole());

        await signer.SignAsync("a"u8.ToArray(), CancellationToken.None);
        await signer.SignAsync("b"u8.ToArray(), CancellationToken.None);

        var login = Assert.Single(bao.Requests, r => r.Path == "/v1/auth/approle/login");
        Assert.Equal("role", (string?)JsonNode.Parse(login.Body)!["role_id"]);
        Assert.Equal("secret", (string?)JsonNode.Parse(login.Body)!["secret_id"]);
        Assert.All(bao.Requests.Where(r => r.Path.Contains("/sign/")), r => Assert.Equal("login-token", r.Token));
    }

    [Fact]
    public async Task AppRole_logs_in_again_before_the_token_expires()
    {
        var time = new ManualTime();
        var (signer, bao) = Build((r, _) => AppRoleAnswer(r, "login-token", lease: 100), AppRole(), time);

        await signer.SignAsync("a"u8.ToArray(), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(70));
        await signer.SignAsync("b"u8.ToArray(), CancellationToken.None);
        Assert.Single(bao.Requests, r => r.Path == "/v1/auth/approle/login");

        time.Advance(TimeSpan.FromSeconds(20)); // 90 s of a 100 s lease: past 80%
        await signer.SignAsync("c"u8.ToArray(), CancellationToken.None);
        Assert.Equal(2, bao.Requests.Count(r => r.Path == "/v1/auth/approle/login"));
    }

    [Fact]
    public async Task A_token_OpenBao_rejects_is_replaced_by_logging_in_again_once()
    {
        var logins = 0;
        var (signer, bao) = Build((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/auth/approle/login") return AppRoleAnswer(r, "token-" + ++logins);
            return r.Headers.GetValues("X-Vault-Token").Single() == "token-1"
                ? (HttpStatusCode.Forbidden, """{"errors":["permission denied"]}""")
                : Ok("""{"data":{"signature":"vault:v1:s","key_version":1}}""");
        }, AppRole());

        var result = await signer.SignAsync("a"u8.ToArray(), CancellationToken.None);

        Assert.Equal("vault:v1:s", result.Signature);
        Assert.Equal(2, logins);
        Assert.Equal(["token-1", "token-2"], bao.Requests.Where(r => r.Path.Contains("/sign/")).Select(r => r.Token));
    }

    [Fact]
    public async Task A_refused_AppRole_login_says_so_without_the_secret()
    {
        var (signer, _) = Build((_, _) => (HttpStatusCode.BadRequest, """{"errors":["invalid secret id"]}"""), AppRole());

        var e = await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));

        Assert.Contains("AppRole", e.Message);
        Assert.DoesNotContain("secret\"", e.Message);
        Assert.DoesNotContain("role", e.Message.Replace("AppRole", ""));
    }

    [Fact]
    public async Task A_fixed_token_that_is_rejected_is_not_retried()
    {
        var (signer, bao) = Build((_, _) => (HttpStatusCode.Forbidden, """{"errors":["permission denied"]}"""));

        await Assert.ThrowsAsync<SigningUnavailableException>(() => signer.SignAsync("x"u8.ToArray(), CancellationToken.None));

        Assert.Single(bao.Requests);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>Verifying a transit signature with the public key alone, using a key made here in the form OpenBao returns.</summary>
public sealed class PackageSignatureVerifierTests
{
    private static string Sign(ECDsa key, byte[] data, int version = 1) =>
        $"vault:v{version}:" + Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void A_signature_verifies_with_the_public_key_and_names_its_key_version()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "the package"u8.ToArray();
        var signature = Sign(key, data, version: 4);

        Assert.True(PackageSignatureVerifier.Verify(data, signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(4, PackageSignatureVerifier.VersionOf(signature));
    }

    [Fact]
    public void Changed_bytes_another_key_or_a_malformed_signature_do_not_verify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Sign(key, "the package"u8.ToArray());
        var pem = key.ExportSubjectPublicKeyInfoPem();

        Assert.False(PackageSignatureVerifier.Verify("the package!"u8, signature, pem));
        Assert.False(PackageSignatureVerifier.Verify("the package"u8, signature, other.ExportSubjectPublicKeyInfoPem()));
        Assert.False(PackageSignatureVerifier.Verify("the package"u8, "not a signature", pem));
        Assert.False(PackageSignatureVerifier.Verify("the package"u8, "vault:v1:@@@@", pem));
        Assert.False(PackageSignatureVerifier.Verify("the package"u8, "vault:v1:AAAA", pem));
    }
}
