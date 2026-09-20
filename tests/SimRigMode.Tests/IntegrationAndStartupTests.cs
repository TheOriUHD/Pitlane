using SimRigMode.Services;
using Xunit;

namespace SimRigMode.Tests;

public sealed class IntegrationAndStartupTests
{
    [Theory]
    [InlineData("http://homeassistant.local:8123")]
    [InlineData("http://192.168.1.50:8123")]
    [InlineData("HTTP://homeassistant.local")]
    public void PlaintextHttp_IsRefused_BecauseTheBearerTokenWouldGoOverTheWire(string baseUrl)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HomeAssistantService.BuildServiceUri(baseUrl, true));
        Assert.Contains("clear text", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message has to tell the user what to do instead, not just say no.
        Assert.Contains("webhook", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("homeassistant.local:8123")]
    public void MalformedUrlsAreRejectedWithATypedError(string baseUrl)
        => Assert.Throws<InvalidOperationException>(() => HomeAssistantService.BuildServiceUri(baseUrl, true));

    [Theory]
    [InlineData("https://ha.example.com", true, "https://ha.example.com/api/services/switch/turn_on")]
    [InlineData("https://ha.example.com/", false, "https://ha.example.com/api/services/switch/turn_off")]
    [InlineData("https://ha.example.com:8123/  ", true, "https://ha.example.com:8123/api/services/switch/turn_on")]
    public void HttpsUrlsAreComposedWithUriRatherThanStringConcatenation(string baseUrl, bool turnOn, string expected)
        => Assert.Equal(expected, HomeAssistantService.BuildServiceUri(baseUrl, turnOn).ToString());

    [Fact]
    public void StartupIsNotReportedEnabled_WhenTheRunEntryPointsSomewhereElse()
    {
        // Under the test host Environment.ProcessPath is testhost.exe, so no Run entry can
        // legitimately point at "this" executable. IsEnabled used to return true whenever ANY
        // value existed, which made the tray checkmark lie after the app moved or was updated.
        var startup = new StartupService();
        Assert.False(startup.IsEnabled);
    }
}
