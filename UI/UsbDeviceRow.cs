using System.ComponentModel;
using System.Runtime.CompilerServices;
using SimRigMode.Models;
using Microsoft.UI.Xaml;

namespace SimRigMode.UI;

public sealed class UsbDeviceRow : INotifyPropertyChanged
{
    private DeviceModeAction _desktop, _simRig; private bool _ack;
    public string FriendlyName { get; init; } = ""; public string Manufacturer { get; init; } = ""; public string InstanceId { get; init; } = ""; public string DeviceClass { get; init; } = ""; public string VidPid { get; init; } = ""; public bool IsEnabled { get; init; } public bool IsCritical { get; init; }
    public string State => IsEnabled ? "Connected / enabled" : "Connected / disabled";
    public string DeviceSummary => string.Join("  •  ", new[] { Manufacturer, VidPid, State }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string DetailSummary => string.Join("  •  ", new[] { DeviceClass, VidPid }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public int StateBadgeValue => IsEnabled ? 1 : 0;
    public Visibility CriticalVisibility => IsCritical ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<string> Actions { get; } = ["Do not change", "Enabled", "Disabled"];
    public string DesktopChoice { get => Format(_desktop); set { Desktop = Parse(value); } }
    public string SimRigChoice { get => Format(_simRig); set { SimRig = Parse(value); } }
    public DeviceModeAction Desktop { get => _desktop; set { _desktop = value; Changed(); } }
    public DeviceModeAction SimRig { get => _simRig; set { _simRig = value; Changed(); } }
    public bool CriticalDeviceAcknowledged { get => _ack; set { _ack = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private static string Format(DeviceModeAction value) => value == DeviceModeAction.DoNotChange ? "Do not change" : value.ToString();
    private static DeviceModeAction Parse(string? value) => value switch { "Enabled" => DeviceModeAction.Enabled, "Disabled" => DeviceModeAction.Disabled, _ => DeviceModeAction.DoNotChange };
}
