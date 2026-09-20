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
        var tokenText = Unprotect(config.ProtectedToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/api/services/switch/{(turnOn ? "turn_on" : "turn_off")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenText);
        request.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = config.EntityId }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, token); response.EnsureSuccessStatusCode();
    }
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
