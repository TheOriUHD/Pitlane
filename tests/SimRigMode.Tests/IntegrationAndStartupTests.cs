using System.Text.Json;
using SimRigMode.Models;
using SimRigMode.Services;
using Xunit;

namespace SimRigMode.Tests;

public sealed class IntegrationAndStartupTests
{
    // ===== Home Assistant: host, token, entity ==========================================

    [Theory]
    [InlineData("homeassistant.local", "http://homeassistant.local:8123/")]
    [InlineData("homeassistant", "http://homeassistant:8123/")]
    [InlineData("192.168.1.20", "http://192.168.1.20:8123/")]
    [InlineData("192.168.1.20:8123", "http://192.168.1.20:8123/")]
    [InlineData("10.0.0.5:9000", "http://10.0.0.5:9000/")]
    [InlineData("  homeassistant.local/  ", "http://homeassistant.local:8123/")]
    public void ALocalName_UsesHomeAssistantsOwnDefault(string host, string expected)
        => Assert.Equal(expected, HomeAssistantService.BaseUri(host).ToString());

    [Theory]
    [InlineData("ha.example.com", "https://ha.example.com/")]
    [InlineData("https://ha.example.com:8443", "https://ha.example.com:8443/")]
    [InlineData("http://homeassistant.local:8123", "http://homeassistant.local:8123/")]
    [InlineData("https://homeassistant.local", "https://homeassistant.local/")]
    [InlineData("http://100.101.102.103:8123", "http://100.101.102.103:8123/")]
    public void OtherNames_UseHttps_AndAFullUrlIsKept(string host, string expected)
        => Assert.Equal(expected, HomeAssistantService.BaseUri(host).ToString());

    [Theory]
    [InlineData("http://ha.example.com")]
    [InlineData("http://8.8.8.8:8123")]
    public void PlainHttp_IsRefused_OutsideTheLocalNetwork_BecauseTheTokenWouldCrossTheInternet(string host)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HomeAssistantService.BaseUri(host));
        // The message has to say what to do instead, not just no.
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://homeassistant.local")]
    [InlineData("not a host")]
    public void Nonsense_IsRefusedWithAMessage(string host)
        => Assert.Throws<InvalidOperationException>(() => HomeAssistantService.BaseUri(host));

    [Fact]
    public void Requests_UseTheGenericOnAndOffServices_AndTheCheckOnlyReadsTheState()
    {
        var home = HomeAssistantService.BaseUri("homeassistant.local");
        Assert.Equal("http://homeassistant.local:8123/api/services/homeassistant/turn_on", HomeAssistantService.ServiceUri(home, true).ToString());
        Assert.Equal("http://homeassistant.local:8123/api/services/homeassistant/turn_off", HomeAssistantService.ServiceUri(home, false).ToString());
        Assert.Equal("http://homeassistant.local:8123/api/states/switch.sim_rig", HomeAssistantService.StateUri(home, "switch.sim_rig").ToString());
    }

    [Theory]
    [InlineData("switch.sim_rig", "switch.sim_rig")]
    [InlineData("  Switch.Sim_Rig ", "switch.sim_rig")]
    public void AnEntity_IsTrimmedAndLowerCased(string entity, string expected)
        => Assert.Equal(expected, HomeAssistantService.EntityOrThrow(entity));

    [Theory]
    [InlineData("sim_rig")]
    [InlineData("switch.")]
    [InlineData("switch.sim rig")]
    public void AnythingElse_IsNotAnEntity(string entity)
        => Assert.Throws<InvalidOperationException>(() => HomeAssistantService.EntityOrThrow(entity));

    [Fact]
    public void ItPowersTheRig_OnlyWhenSwitchedOn_AndHostTokenAndEntityAreAllSet()
    {
        Assert.True(new IntegrationConfig().Enabled);   // on by default: filling in the fields is enough
        Assert.False(new IntegrationConfig().IsActive);
        Assert.False(new IntegrationConfig { Host = "homeassistant.local", Entity = "switch.sim_rig" }.IsActive);
        Assert.True(new IntegrationConfig { Host = "homeassistant.local", ProtectedToken = "x", Entity = "switch.sim_rig" }.IsActive);
        Assert.False(new IntegrationConfig { Enabled = false, Host = "homeassistant.local", ProtectedToken = "x", Entity = "switch.sim_rig" }.IsActive);
    }

    [Fact]
    public void OldSettings_CarryOver_WithTheSwitchAsItWas()
    {
        var on = Read("""{"Enabled":true,"BaseUrl":"http://homeassistant.local:8123","EntityId":"switch.sim_rig","ProtectedToken":"abc","OnWebhookUrl":""}""");
        ConfigurationService.MigrateIntegration(on);
        Assert.Equal("http://homeassistant.local:8123", on.Host);
        Assert.Equal("switch.sim_rig", on.Entity);
        Assert.Equal("abc", on.ProtectedToken);
        Assert.True(on.IsActive);

        // Filled in behind a switch that was off: it stays off and must not start powering the rig.
        var off = Read("""{"Enabled":false,"BaseUrl":"http://homeassistant.local:8123","EntityId":"switch.sim_rig","ProtectedToken":"abc"}""");
        ConfigurationService.MigrateIntegration(off);
        Assert.True(off.IsSetUp);
        Assert.False(off.IsActive);
    }

    [Fact]
    public void OldFields_AreNeverWrittenBack()
    {
        var integration = Read("""{"Enabled":true,"OnWebhookUrl":"http://x","OffWebhookUrl":"http://y","Host":"homeassistant.local"}""");
        ConfigurationService.MigrateIntegration(integration);
        var json = JsonSerializer.Serialize(integration);
        Assert.DoesNotContain("Webhook", json);
        Assert.DoesNotContain("BaseUrl", json);
        Assert.Contains("homeassistant.local", json);
    }

    private static IntegrationConfig Read(string json) => JsonSerializer.Deserialize<IntegrationConfig>(json)!;

    // ===== startup ======================================================================

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
