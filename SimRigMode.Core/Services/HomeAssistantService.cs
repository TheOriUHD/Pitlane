using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimRigMode.Models;

namespace SimRigMode.Services;

/// <summary>What a connection check found, in one sentence for the user.</summary>
public sealed record HomeAssistantStatus(bool Connected, string Message);

/// <summary>
/// Switches the rig's power through Home Assistant's REST API. It needs three things: where Home
/// Assistant runs, a long-lived access token, and the entity that powers the rig.
/// </summary>
public sealed partial class HomeAssistantService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Turns the entity on or off. Does nothing unless switched on and set up.</summary>
    public async Task ExecuteAsync(IntegrationConfig config, bool turnOn, CancellationToken token)
    {
        if (!config.IsActive) return;
        var entity = EntityOrThrow(config.Entity);
        using var request = Authorized(HttpMethod.Post, ServiceUri(BaseUri(config.Host), turnOn), config);
        request.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = entity }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, entity, token);
    }

    /// <summary>
    /// Reads the entity's state without changing it. One request proves the address, the token
    /// and the entity, so it is safe to run whenever the settings change.
    /// </summary>
    public async Task<HomeAssistantStatus> CheckAsync(IntegrationConfig config, CancellationToken token)
    {
        try
        {
            var entity = EntityOrThrow(config.Entity);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = Authorized(HttpMethod.Get, StateUri(BaseUri(config.Host), entity), config);
            using var response = await SendAsync(request, entity, timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = json.RootElement;
            var state = root.TryGetProperty("state", out var value) ? value.GetString() : null;
            var name = root.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("friendly_name", out var friendly)
                ? friendly.GetString() : null;
            return new(true, $"Connected. {(string.IsNullOrWhiteSpace(name) ? entity : name)} is {state ?? "in an unknown state"}.");
        }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
        catch (JsonException) { return new(false, "Something answered, but not like Home Assistant does. Check the host."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "Home Assistant did not answer in time. Check the host."); }
    }

    // ===== addresses ==================================================================

    /// <summary>
    /// The address as typed, made into Home Assistant's base URL. "homeassistant.local" and
    /// "192.168.1.20" become http://…:8123/, Home Assistant's own default; any other name
    /// becomes https://; a full URL is kept as it is.
    ///
    /// The token is sent over plain http only to an address on the local network. Across the
    /// internet it would be readable by anyone on the way, so there it has to be https.
    /// </summary>
    public static Uri BaseUri(string host)
    {
        var text = (host ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) throw new InvalidOperationException("Enter where Home Assistant runs, like homeassistant.local.");

        var typedScheme = text.Contains("://", StringComparison.Ordinal);
        if (!typedScheme)
        {
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var probe) || probe.Host.Length == 0)
                throw new InvalidOperationException($"\"{text}\" is not an address Home Assistant can be reached at.");
            text = (IsLocal(probe.Host) ? "http://" : "https://") + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"\"{host!.Trim()}\" is not an address Home Assistant can be reached at.");
        if (uri.Scheme == "http" && !IsLocal(uri.Host))
            throw new InvalidOperationException(
                $"{uri.Host} is not on your local network, so the token would cross the internet unencrypted. Use Home Assistant's https address.");

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!typedScheme && uri.Scheme == "http" && uri.IsDefaultPort) builder.Port = 8123;
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    /// <summary>
    /// True for addresses that stay inside the home network: private and link-local IP ranges,
    /// loopback, names without a dot, and the suffixes local networks use. 100.64.0.0/10 and
    /// .ts.net are Tailscale's, whose traffic is encrypted between devices anyway.
    /// </summary>
    public static bool IsLocal(string host)
    {
        host = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || (ip.IsIPv4MappedToIPv6 && IsLocal(ip.MapToIPv4().ToString()));
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        if (!host.Contains('.')) return true;
        string[] suffixes = [".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain", ".ts.net"];
        return suffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>homeassistant.turn_on and turn_off work for switches, lights, input_booleans and
    /// anything else that can be turned on and off.</summary>
    public static Uri ServiceUri(Uri baseUri, bool turnOn) => new(baseUri, $"api/services/homeassistant/{(turnOn ? "turn_on" : "turn_off")}");

    public static Uri StateUri(Uri baseUri, string entity) => new(baseUri, "api/states/" + Uri.EscapeDataString(entity));

    /// <summary>"switch.sim_rig": a domain, a dot and an object id, in lower case.</summary>
    public static string EntityOrThrow(string entity)
    {
        var text = (entity ?? "").Trim().ToLowerInvariant();
        if (!EntityPattern().IsMatch(text))
            throw new InvalidOperationException($"\"{entity?.Trim()}\" is not an entity ID. It looks like switch.sim_rig.");
        return text;
    }

    [GeneratedRegex("^[a-z0-9_]+\\.[a-z0-9_]+$")]
    private static partial Regex EntityPattern();

    // ===== requests ===================================================================

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, IntegrationConfig config)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UnprotectOrThrow(config.ProtectedToken));
        return request;
    }

    /// <summary>Sends, and turns every way it can fail into a sentence that says what to fix.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string entity, CancellationToken token)
    {
        var where = request.RequestUri!.GetLeftPart(UriPartial.Authority);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, token); }
        catch (HttpRequestException ex) { throw new InvalidOperationException($"Home Assistant could not be reached at {where}. {ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { throw new InvalidOperationException($"Home Assistant did not answer at {where}.", ex); }

        if (response.IsSuccessStatusCode) return response;
        var code = (int)response.StatusCode;
        var reason = response.ReasonPhrase;
        response.Dispose();
        throw new InvalidOperationException(code switch
        {
            401 or 403 => "Home Assistant turned the access token down. Create a long-lived access token in your Home Assistant profile, under Security, and paste it again.",
            404 => $"Home Assistant has no entity called {entity}.",
            _ => $"Home Assistant answered {code} {reason}."
        });
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
                "The saved Home Assistant token could not be decrypted on this machine or user account. Paste it again.", ex);
        }
    }

    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
