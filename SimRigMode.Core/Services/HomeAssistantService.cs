using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimRigMode.Models;

namespace SimRigMode.Services;

public sealed class HomeAssistantService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public async Task ExecuteAsync(IntegrationConfig config, bool turnOn, CancellationToken token)
    {
        if (!config.Enabled) return;
        var webhook = turnOn ? config.OnWebhookUrl : config.OffWebhookUrl;
        if (!string.IsNullOrWhiteSpace(webhook))
        {
            using var result = await _http.PostAsync(webhook, new StringContent("{}", Encoding.UTF8, "application/json"), token);
            result.EnsureSuccessStatusCode(); return;
        }
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.EntityId)) throw new InvalidOperationException("Home Assistant URL and entity ID are required.");

        var endpoint = BuildServiceUri(config.BaseUrl, turnOn);
        var tokenText = UnprotectOrThrow(config.ProtectedToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenText);
        request.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = config.EntityId }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, token); response.EnsureSuccessStatusCode();
    }
    /// <summary>
    /// Builds the REST endpoint and refuses to send a bearer token over plaintext HTTP.
    /// Webhooks are exempt: they carry no credential, and a LAN Home Assistant on plain http is
    /// a perfectly ordinary setup. The token path is not exempt, because the token would go over
    /// the wire in the clear.
    /// </summary>
    internal static Uri BuildServiceUri(string baseUrl, bool turnOn)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed))
            throw new InvalidOperationException($"'{baseUrl}' is not a valid Home Assistant URL.");

        if (parsed.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Home Assistant must be reached over https when an access token is used; '{parsed.Scheme}' would send " +
                "the token in clear text. Use an https URL, or configure the webhook fields instead - webhooks carry no token.");

        return new Uri(parsed, $"/api/services/switch/{(turnOn ? "turn_on" : "turn_off")}");
    }

    private static string UnprotectOrThrow(string protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
            throw new InvalidOperationException("No Home Assistant access token has been saved.");
        try { return Unprotect(protectedToken); }
        catch (Exception ex)
        {
            // DPAPI CurrentUser: a config copied from another user or machine cannot be
            // decrypted here, and a hand-edited value is not valid base64.
            throw new InvalidOperationException(
                "The saved Home Assistant token could not be decrypted on this machine or user account. Re-enter it in Settings.", ex);
        }
    }

    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
