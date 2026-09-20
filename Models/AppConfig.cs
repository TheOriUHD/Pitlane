using System.Text.Json.Serialization;

namespace SimRigMode.Models;

public enum WorkspaceMode { Unknown, Desktop, SimRig }
public enum DeviceModeAction { DoNotChange, Enabled, Disabled }

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    public WorkspaceMode CurrentMode { get; set; } = WorkspaceMode.Unknown;
    public ModeConfig Desktop { get; set; } = new() { Name = "Desktop Mode" };
    public ModeConfig SimRig { get; set; } = new() { Name = "Sim Rig Mode" };
    public List<UsbRule> UsbRules { get; set; } = [];
    public IntegrationConfig Integration { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
}

public sealed class ModeConfig
{
    public string Name { get; set; } = "Mode";
    public DisplaySnapshot? Displays { get; set; }
    public AudioSelection Audio { get; set; } = new();
}

public sealed class GeneralConfig
{
    public int HardwarePowerOnWaitSeconds { get; set; } = 5;
    public int UsbEnumerationWaitSeconds { get; set; } = 3;
    public int HardwarePowerOffDelaySeconds { get; set; } = 2;
    public string DesktopHotkey { get; set; } = "Ctrl+Alt+D";
    public string SimRigHotkey { get; set; } = "Ctrl+Alt+R";
    public bool HotkeysEnabled { get; set; }
}

public sealed class IntegrationConfig
{
    public bool Enabled { get; set; }
    public string OnWebhookUrl { get; set; } = "";
    public string OffWebhookUrl { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
}

public sealed class UsbRule
{
    public string IdentityKey { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Serial { get; set; } = "";
    public string LocationPath { get; set; } = "";
    public string PortChain { get; set; } = "";
    public bool IsSubtree { get; set; }
    public bool DisableHubItself { get; set; }
    public List<UsbRuleTarget> LastKnownTargets { get; set; } = [];
    public DeviceModeAction Desktop { get; set; }
    public DeviceModeAction SimRig { get; set; }
    public bool CriticalDeviceAcknowledged { get; set; }
}

public sealed class UsbRuleTarget { public string InstanceId { get; set; } = ""; public string FriendlyName { get; set; } = ""; public bool IsCritical { get; set; } }

public sealed class AudioSelection
{
    public string PlaybackDefaultId { get; set; } = "";
    public string PlaybackCommunicationsId { get; set; } = "";
    public string RecordingDefaultId { get; set; } = "";
    public string RecordingCommunicationsId { get; set; } = "";
}

public sealed class DisplaySnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public List<DisplayPathRecord> Paths { get; set; } = [];
    public List<DisplayModeRecord> Modes { get; set; } = [];
}

public sealed class DisplayPathRecord
{
    public long SourceAdapter { get; set; }
    public uint SourceId { get; set; }
    public uint SourceModeInfoIdx { get; set; }
    public long TargetAdapter { get; set; }
    public uint TargetId { get; set; }
    public uint TargetModeInfoIdx { get; set; }
    public uint DesktopModeInfoIdx { get; set; } = uint.MaxValue;
    public uint OutputTechnology { get; set; }
    public uint Rotation { get; set; }
    public uint Scaling { get; set; }
    public uint RefreshNumerator { get; set; }
    public uint RefreshDenominator { get; set; }
    public uint ScanLineOrdering { get; set; }
    public bool TargetAvailable { get; set; }
    public uint StatusFlags { get; set; }
    public string MonitorDevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "Display";
    public string SourceName { get; set; } = "";
}

public sealed class DisplayModeRecord
{
    public uint InfoType { get; set; }
    public uint Id { get; set; }
    public long Adapter { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint PixelFormat { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public ulong PixelRate { get; set; }
    public uint HSyncNumerator { get; set; }
    public uint HSyncDenominator { get; set; }
    public uint VSyncNumerator { get; set; }
    public uint VSyncDenominator { get; set; }
    public uint ActiveWidth { get; set; }
    public uint ActiveHeight { get; set; }
    public uint TotalWidth { get; set; }
    public uint TotalHeight { get; set; }
    public uint VideoStandard { get; set; }
    public uint ScanLineOrdering { get; set; }
}

public sealed class UsbDeviceInfo
{
    public string FriendlyName { get; set; } = "Unknown device";
    public string Manufacturer { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string VidPid { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsCritical { get; set; }
    public uint DevInst { get; set; }
    public string ParentInstanceId { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string LocationPath { get; set; } = "";
    public string LocationInfo { get; set; } = "";
    public string DriverKey { get; set; } = "";
    public string Service { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public bool IsHub { get; set; }
    public bool IsRootHub { get; set; }
    public bool IsHostController { get; set; }
    public bool IsComposite { get; set; }
    public bool IsPresent { get; set; } = true;
}

public enum UsbTopologyNodeKind { Computer, HostController, RootHub, Hub, Port, Device, Interfaces, Interface, Missing }
public sealed class UsbTopology
{
    public DateTime EnumeratedUtc { get; set; } = DateTime.UtcNow;
    public List<UsbTopologyNode> Roots { get; set; } = [];
    public IEnumerable<UsbTopologyNode> Flatten() => Roots.SelectMany(node => node.Flatten());
}
public sealed class UsbTopologyNode
{
    public UsbTopologyNodeKind Kind { get; set; }
    public string IdentityKey { get; set; } = "";
    public string Name { get; set; } = "USB device";
    public string Manufacturer { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Serial { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string LocationPath { get; set; } = "";
    public string PortChain { get; set; } = "";
    public string Speed { get; set; } = "";
    public int PortNumber { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsPresent { get; set; }
    public bool IsCritical { get; set; }
    [JsonIgnore] public UsbTopologyNode? Parent { get; set; }
    public List<UsbTopologyNode> Children { get; set; } = [];
    public IEnumerable<UsbTopologyNode> Flatten() { yield return this; foreach (var child in Children) foreach (var item in child.Flatten()) yield return item; }
}

public sealed class AudioEndpointInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPlayback { get; set; }
    public override string ToString() => Name;
}

public sealed record TransitionStep(string Name, bool Success, string Detail);
