using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PharmaOS.Platform.Host.Keys;

/// <summary>
/// <see cref="IPackageSigner"/> over OpenBao's transit HTTP API. Signatures are ECDSA P-256 over SHA-256, returned in JWS form (the
/// fixed-width r‖s that .NET's <see cref="ECDsa"/> reads directly), so they can be checked here with only the public key.
/// </summary>
public sealed class OpenBaoTransitSigner(HttpClient http, IOptions<OpenBaoOptions> options, OpenBaoTokenSource tokens) : IPackageSigner
{
    private const string Hash = "sha2-256";
    private const string Marshaling = "jws";
    private OpenBaoOptions Options => options.Value;

    public async Task<PackageSignature> SignAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var body = new JsonObject { ["input"] = Convert.ToBase64String(data.Span), ["marshaling_algorithm"] = Marshaling };
        var response = await SendAsync(HttpMethod.Post, $"sign/{Key}/{Hash}", body, ct);
        var d = response["data"] as JsonObject ?? throw Unexpected("sign");
        var signature = d["signature"]?.GetValue<string>() ?? throw Unexpected("sign");
        var version = d["key_version"]?.GetValue<int>() ?? PackageSignatureVerifier.VersionOf(signature);
        return new PackageSignature(signature, Options.PackageKey, version);
    }

    public async Task<bool> VerifyAsync(ReadOnlyMemory<byte> data, string signature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["input"] = Convert.ToBase64String(data.Span), ["signature"] = signature, ["marshaling_algorithm"] = Marshaling,
        };
        var response = await SendAsync(HttpMethod.Post, $"verify/{Key}/{Hash}", body, ct);
        return (response["data"] as JsonObject)?["valid"]?.GetValue<bool>() ?? throw Unexpected("verify");
    }

    public async Task<SigningKeyInfo> KeyInfoAsync(CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, $"keys/{Key}", null, ct);
        var d = response["data"] as JsonObject ?? throw Unexpected("keys");
        var type = d["type"]?.GetValue<string>();
        if (type != "ecdsa-p256")
            throw new SigningUnavailableException($"Key '{Options.PackageKey}' is of type '{type}'; package signing needs ecdsa-p256.");
        var latest = d["latest_version"]?.GetValue<int>() ?? throw Unexpected("keys");
        var keys = new Dictionary<int, string>();
        foreach (var (version, entry) in d["keys"] as JsonObject ?? throw Unexpected("keys"))
            if (int.TryParse(version, out var v) && (entry as JsonObject)?["public_key"]?.GetValue<string>() is { } pem) keys[v] = pem;
        return new SigningKeyInfo(Options.PackageKey, latest, keys);
    }

    private string Key => Uri.EscapeDataString(Options.PackageKey);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));
        try
        {
            var result = await SendOnceAsync(method, path, body, timeout.Token);
            // A token the server no longer accepts (expired, or revoked and replaced): log in again once.
            if (result.Status == System.Net.HttpStatusCode.Forbidden && await tokens.ForgetAsync(result.Token, timeout.Token))
                result = await SendOnceAsync(method, path, body, timeout.Token);
            var (status, text, _) = result;
            if ((int)status is < 200 or > 299)
                throw new SigningUnavailableException($"OpenBao answered {(int)status} for {method} {path}: {ErrorsOf(text)}");
            return JsonNode.Parse(text) as JsonObject ?? throw Unexpected(path);
        }
        catch (HttpRequestException e)
        {
            throw new SigningUnavailableException($"OpenBao could not be reached at {Options.OpenBaoAddress}: {e.Message}", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SigningUnavailableException($"OpenBao did not answer within {Options.TimeoutSeconds} seconds.");
        }
        catch (JsonException e)
        {
            throw new SigningUnavailableException("OpenBao answered with something that is not JSON.", e);
        }
    }

    private async Task<(System.Net.HttpStatusCode Status, string Text, string Token)> SendOnceAsync(
        HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        using var request = new HttpRequestMessage(method,
            new Uri(new Uri(Options.OpenBaoAddress.TrimEnd('/') + "/"), $"v1/{Uri.EscapeDataString(Options.TransitMount)}/{path}"));
        request.Headers.Add("X-Vault-Token", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var result = await http.SendAsync(request, ct);
        return (result.StatusCode, await result.Content.ReadAsStringAsync(ct), token);
    }

    private static string ErrorsOf(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["errors"] is JsonArray a && a.Count > 0 ? string.Join("; ", a) : "no detail";
        }
        catch (JsonException)
        {
            return "no detail";
        }
    }

    private static SigningUnavailableException Unexpected(string what) => new($"OpenBao's answer to '{what}' was not in the expected form.");
}

/// <summary>Checks a transit signature with a public key alone, so a package can be verified where OpenBao does not hold the signing key.</summary>
public static class PackageSignatureVerifier
{
    private const string Prefix = "vault:v";

    /// <summary>The key version a <c>vault:v{n}:…</c> signature names.</summary>
    public static int VersionOf(string signature) => Parse(signature).Version;

    /// <summary>True when the signature is well formed and valid for <paramref name="data"/> under the PEM public key.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, string signature, string publicKeyPem)
    {
        byte[] raw;
        try
        {
            raw = Parse(signature).Raw;
        }
        catch (FormatException)
        {
            return false;
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return raw.Length == 64 && key.VerifyData(data, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static (int Version, byte[] Raw) Parse(string signature)
    {
        if (!signature.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("Not a transit signature.");
        var colon = signature.IndexOf(':', Prefix.Length);
        if (colon < 0 || !int.TryParse(signature.AsSpan(Prefix.Length, colon - Prefix.Length), out var version))
            throw new FormatException("Not a transit signature.");
        var b64 = signature[(colon + 1)..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return (version, Convert.FromBase64String(b64));
    }
}
