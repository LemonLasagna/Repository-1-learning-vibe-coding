using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PharmaOS.Platform.Host.Keys;

/// <summary>
/// The token the host presents to OpenBao: the fixed one when configured, otherwise a short-lived token from an AppRole login that
/// is renewed by logging in again shortly before it expires. One instance is shared by all requests.
/// </summary>
public sealed class OpenBaoTokenSource(IHttpClientFactory clients, IOptions<OpenBaoOptions> options, TimeProvider time)
{
    public const string ClientName = "openbao-login";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _validUntil;

    private OpenBaoOptions Options => options.Value;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Options.OpenBaoAddress))
            throw new SigningUnavailableException("Package signing is not configured: set Keys:OpenBaoAddress and either Keys:OpenBaoToken or Keys:AppRoleId and Keys:AppRoleSecretId.");
        if (!string.IsNullOrWhiteSpace(Options.OpenBaoToken)) return Options.OpenBaoToken;
        if (string.IsNullOrWhiteSpace(Options.AppRoleId) || string.IsNullOrWhiteSpace(Options.AppRoleSecretId))
            throw new SigningUnavailableException("Package signing is not configured: set Keys:OpenBaoToken or Keys:AppRoleId and Keys:AppRoleSecretId.");

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && time.GetUtcNow() < _validUntil) return _token;
            var body = new JsonObject { ["role_id"] = Options.AppRoleId, ["secret_id"] = Options.AppRoleSecretId };
            var uri = new Uri(new Uri(Options.OpenBaoAddress.TrimEnd('/') + "/"), $"v1/auth/{Uri.EscapeDataString(Options.AppRoleMount)}/login");
            using var response = await clients.CreateClient(ClientName).PostAsJsonAsync(uri, body, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new SigningUnavailableException($"OpenBao refused the AppRole login ({(int)response.StatusCode}); check Keys:AppRoleId and Keys:AppRoleSecretId.");
            var auth = JsonNode.Parse(text)?["auth"] as JsonObject
                ?? throw new SigningUnavailableException("OpenBao's login answer was not in the expected form.");
            _token = auth["client_token"]?.GetValue<string>()
                ?? throw new SigningUnavailableException("OpenBao's login answer had no token.");
            // Use the token for 80% of its lease; a lease of 0 means it does not expire, so look again in an hour.
            var lease = auth["lease_duration"]?.GetValue<int>() ?? 0;
            _validUntil = time.GetUtcNow() + (lease > 0 ? TimeSpan.FromSeconds(lease * 0.8) : TimeSpan.FromHours(1));
            return _token;
        }
        catch (HttpRequestException e)
        {
            throw new SigningUnavailableException($"OpenBao could not be reached at {Options.OpenBaoAddress}: {e.Message}", e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops a login token OpenBao rejected so the next call logs in again. Returns false for a fixed token (logging in again
    /// would not help) or when another request already replaced it.
    /// </summary>
    public async Task<bool> ForgetAsync(string rejected, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Options.OpenBaoToken)) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token != rejected) return false;
            _token = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
