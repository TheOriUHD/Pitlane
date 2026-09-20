using SimRigMode.Models;
using SimRigMode.Services;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// Regression tests for the composite-device criticality hole described in docs/report.md.
///
/// A USB composite keyboard exposes its physical devnode with setup class "USB"; only the HID
/// child underneath it carries class "Keyboard", and UsbTopologyService.IsPhysical filters
/// interface nodes out of the physical tree. So the resolver, which tested only the physical
/// node's own flag, saw a harmless device and would disable it -- disconnecting system input.
///
/// IsCritical is now a computed OR of "this node is protected" and "something protected sits
/// beneath it", so both the resolver's guards inherit the fix.
/// </summary>
public sealed class CriticalityTests
{
    [Fact]
    public void IsCritical_IsFalse_WhenNeitherSelfNorDescendantIsCritical()
    {
        var node = new UsbTopologyNode { Name = "MOZA R9 Base" };
        Assert.False(node.IsSelfCritical);
        Assert.False(node.HasCriticalDescendant);
        Assert.False(node.IsCritical);
    }

    [Fact]
    public void IsCritical_IsTrue_WhenTheNodeIsItselfProtected()
    {
        var node = new UsbTopologyNode { Name = "USB Root Hub", IsSelfCritical = true };
        Assert.True(node.IsCritical);
    }

    [Fact]
    public void IsCritical_IsTrue_WhenOnlyADescendantIsProtected()
    {
        // This is the composite keyboard: the physical node is class "USB" and not itself
        // critical, but a keyboard hangs off it.
        var node = new UsbTopologyNode { Name = "Corsair K70", HasCriticalDescendant = true };
        Assert.False(node.IsSelfCritical);
        Assert.True(node.IsCritical);
    }

    [Fact]
    public void DeviceInfo_IsCritical_IsAlsoAComputedOr()
    {
        Assert.False(new UsbDeviceInfo().IsCritical);
        Assert.True(new UsbDeviceInfo { IsSelfCritical = true }.IsCritical);
        Assert.True(new UsbDeviceInfo { HasCriticalDescendant = true }.IsCritical);
    }

    [Theory]
    // Class-based detection: the reliable signal, and the one that fires on the HID child.
    [InlineData("Generic device", "Keyboard", @"HID\VID_1B1C&PID_1B49&MI_00\7&1", true)]
    [InlineData("Generic device", "Mouse", @"HID\VID_046D&PID_C52B&MI_01\8&2", true)]
    [InlineData("Generic device", "System", @"USB\VID_8087&PID_0029\5&3", true)]
    // Root hubs are protected by instance id regardless of name or class.
    [InlineData("USB Root Hub", "USB", @"USB\ROOT_HUB30\4&1", true)]
    // Name heuristics, kept as an additional signal.
    [InlineData("Wireless Keyboard", "USB", @"USB\VID_046D&PID_C52B\0", true)]
    [InlineData("USB host controller", "USB", @"PCI\VEN_8086&DEV_A36D\3", true)]
    // The case that motivated all of this: a composite keyboard whose product string contains
    // neither "keyboard" nor "mouse", presenting as a plain USB composite device. Its own flag
    // is false -- only descendant propagation protects it.
    [InlineData("Corsair K70 RGB", "USB", @"USB\VID_1B1C&PID_1B49\6&4", false)]
    [InlineData("MOZA R9 Base", "USB", @"USB\VID_346E&PID_0004\SN123", false)]
    public void IsCritical_SelfDetection_ClassifiesAsExpected(string name, string cls, string id, bool expected)
        => Assert.Equal(expected, UsbDeviceService.IsCritical(name, cls, id));

    [Fact]
    public void CompositeKeyboard_IsOnlyProtectedOnceDescendantCriticalityIsPropagated()
    {
        // Proves the hole is real and that the computed OR is what closes it.
        const string name = "Corsair K70 RGB";
        const string cls = "USB";
        const string id = @"USB\VID_1B1C&PID_1B49\6&4";

        // Self-detection alone does not catch it.
        Assert.False(UsbDeviceService.IsCritical(name, cls, id));

        var physical = new UsbTopologyNode { Kind = UsbTopologyNodeKind.Device, Name = name, InstanceId = id };
        Assert.False(physical.IsCritical);

        // Once the HID keyboard child underneath it is accounted for, it is protected.
        physical.HasCriticalDescendant = true;
        physical.CriticalDescendantNames = ["HID Keyboard Device"];
        Assert.True(physical.IsCritical);
    }
}
