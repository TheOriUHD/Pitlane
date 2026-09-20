using SimRigMode.Models;
using SimRigMode.Services;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// Regression tests for the rule-matching wildcard described in docs/report.md.
///
/// <see cref="UsbRule.InstanceId"/> defaults to "". So do the InstanceIds of the synthetic
/// Computer, Port and Interfaces nodes that <see cref="UsbTopologyService.Enumerate"/> builds.
/// The final clause of MatchRule compared those two empty strings with no emptiness guard, so a
/// rule that never set InstanceId matched every Port in the tree; ClosestRule then walked each
/// device up to its port and returned that rule, making it the effective policy for every device
/// on the machine.
///
/// These tests construct topologies by hand and touch no hardware.
/// </summary>
public sealed class UsbRuleMatchingTests
{
    // ---- helpers -----------------------------------------------------------------

    private static UsbTopologyNode Node(UsbTopologyNodeKind kind, string name, string identityKey = "",
                                        string instanceId = "", string containerId = "", string serial = "",
                                        string locationPath = "", string vid = "", string pid = "")
        => new()
        {
            Kind = kind, Name = name, IdentityKey = identityKey, InstanceId = instanceId,
            ContainerId = containerId, Serial = serial, LocationPath = locationPath, Vid = vid, Pid = pid,
            IsPresent = true, IsEnabled = true
        };

    private static UsbTopologyNode Attach(UsbTopologyNode parent, UsbTopologyNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
        return child;
    }

    /// <summary>Computer -> HostController -> RootHub -> Port -> Device, shaped exactly as
    /// UsbTopologyService.Enumerate builds it, including the empty InstanceIds.</summary>
    private static (UsbTopologyNode computer, UsbTopologyNode port, UsbTopologyNode device) BuildTree()
    {
        var computer = Node(UsbTopologyNodeKind.Computer, "TESTPC", identityKey: "computer");
        var controller = Attach(computer, Node(UsbTopologyNodeKind.HostController, "xHCI controller",
            identityKey: "instance:pci\\ven_8086", instanceId: "PCI\\VEN_8086&DEV_A36D"));
        var rootHub = Attach(controller, Node(UsbTopologyNodeKind.RootHub, "USB Root Hub",
            identityKey: "instance:usb\\root_hub30", instanceId: "USB\\ROOT_HUB30\\4&1"));
        var port = Attach(rootHub, Node(UsbTopologyNodeKind.Port, "Port 3",
            identityKey: "instance:usb\\root_hub30/port:3"));
        var device = Attach(port, Node(UsbTopologyNodeKind.Device, "MOZA R9 Base",
            identityKey: "usb:346e:0004:sn123", instanceId: "USB\\VID_346E&PID_0004\\SN123",
            containerId: "{aaaaaaaa-0000-0000-0000-000000000001}", serial: "SN123",
            locationPath: "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)", vid: "346E", pid: "0004"));
        return (computer, port, device);
    }

    private static IEnumerable<UsbTopologyNode> Flatten(UsbTopologyNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var item in Flatten(child))
                yield return item;
    }

    // ---- the incident regression -------------------------------------------------

    [Fact]
    public void SmokeTestRule_MatchesNothing_InsteadOfEveryPortOnTheMachine()
    {
        // The literal rule from the old tests/SimRigMode.SmokeTests/Program.cs:15.
        // Note it sets IdentityKey, so it IS addressable -- what made it dangerous is that it
        // never set InstanceId, and the empty-vs-empty comparison matched every Port node.
        var rule = new UsbRule
        {
            IdentityKey = "port:test:1234:5678",
            FriendlyName = "Offline rig hub",
            IsSubtree = true,
            Desktop = DeviceModeAction.Disabled,
            SimRig = DeviceModeAction.Enabled,
            LastKnownTargets = [new UsbRuleTarget { InstanceId = @"USB\VID_1234&PID_5678\TEST", FriendlyName = "Rig device" }]
        };

        var (computer, _, _) = BuildTree();

        foreach (var node in Flatten(computer))
            Assert.Null(UsbTopologyService.MatchRule([rule], node));
    }

    [Fact]
    public void RuleWithNoIdentityFieldsAtAll_IsNotAddressable()
    {
        // This is the config-import attack shape: {"Desktop": "Disabled"} and nothing else.
        var rule = new UsbRule { Desktop = DeviceModeAction.Disabled };
        Assert.False(rule.IsAddressable);
    }

    [Fact]
    public void RuleWithNoIdentityFieldsAtAll_MatchesNothing()
    {
        var rule = new UsbRule { Desktop = DeviceModeAction.Disabled };
        var (computer, _, _) = BuildTree();

        foreach (var node in Flatten(computer))
            Assert.Null(UsbTopologyService.MatchRule([rule], node));
    }

    [Fact]
    public void EmptyInstanceIdRule_DoesNotMatchStructuralNodeWithEmptyInstanceId()
    {
        var rule = new UsbRule { IdentityKey = "something-that-matches-no-node" };
        var (_, port, _) = BuildTree();

        Assert.Equal(string.Empty, rule.InstanceId);
        Assert.Equal(string.Empty, port.InstanceId);
        Assert.Null(UsbTopologyService.MatchRule([rule], port));
    }

    [Fact]
    public void StructuralNodes_AreNeverMatched_EvenByAnExactIdentityKey()
    {
        var (computer, port, _) = BuildTree();

        // A rule naming the Port's own identity key must still not match it: ports are
        // synthetic and carry no device identity, so they can never be a rule target.
        Assert.Null(UsbTopologyService.MatchRule([new UsbRule { IdentityKey = port.IdentityKey }], port));
        Assert.Null(UsbTopologyService.MatchRule([new UsbRule { IdentityKey = computer.IdentityKey }], computer));
    }

    // ---- the fix must not break legitimate matching ------------------------------

    [Fact]
    public void AddressableRule_StillMatchesItsDevice_ByIdentityKey()
    {
        var (_, _, device) = BuildTree();
        var rule = new UsbRule { IdentityKey = "usb:346e:0004:sn123" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], device));
    }

    [Fact]
    public void AddressableRule_StillMatchesItsDevice_ByInstanceId()
    {
        var (_, _, device) = BuildTree();
        var rule = new UsbRule { InstanceId = @"USB\VID_346E&PID_0004\SN123" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], device));
    }

    [Fact]
    public void AddressableRule_StillMatchesItsDevice_ByContainerId()
    {
        var (_, _, device) = BuildTree();
        var rule = new UsbRule { ContainerId = "{AAAAAAAA-0000-0000-0000-000000000001}" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], device));
    }

    [Fact]
    public void AddressableRule_StillMatchesItsDevice_BySerialAndVidPid()
    {
        var (_, _, device) = BuildTree();
        var rule = new UsbRule { Serial = "SN123", Vid = "346E", Pid = "0004" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], device));
    }

    [Fact]
    public void SerialMatch_RequiresVidAndPidToAgree()
    {
        var (_, _, device) = BuildTree();
        var rule = new UsbRule { Serial = "SN123", Vid = "FFFF", Pid = "FFFF" };
        Assert.Null(UsbTopologyService.MatchRule([rule], device));
    }

    [Fact]
    public void AddressableRule_StillMatchesAHub()
    {
        var hub = Node(UsbTopologyNodeKind.Hub, "Generic USB Hub",
            identityKey: "usb:0bda:5411:", instanceId: @"USB\VID_0BDA&PID_5411\6&1");
        var rule = new UsbRule { InstanceId = @"usb\vid_0bda&pid_5411\6&1" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], hub));
    }

    [Fact]
    public void MissingNodes_RemainMatchable_SoSavedRulesSurviveUnplugging()
    {
        var missing = Node(UsbTopologyNodeKind.Missing, "Rig device (unavailable)",
            identityKey: "usb:346e:0004:sn123");
        var rule = new UsbRule { IdentityKey = "usb:346e:0004:sn123" };
        Assert.Same(rule, UsbTopologyService.MatchRule([rule], missing));
    }
}
