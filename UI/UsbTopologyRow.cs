using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using SimRigMode.Models;

namespace SimRigMode.UI;

public sealed class UsbTopologyRow : INotifyPropertyChanged
{
    private int _desktopIndex, _simRigIndex;
    private string _effectivePolicy = "No workspace policy";
    public required UsbTopologyNode Node { get; init; }
    public string Name => Node.Name;
    public string Detail => string.Join(" · ", new[] { Node.Manufacturer, string.IsNullOrWhiteSpace(Node.Vid) ? "" : $"VID {Node.Vid} · PID {Node.Pid}", Node.Kind == UsbTopologyNodeKind.Port ? Node.PortChain : Node.Speed, Node.IsPresent ? Node.IsEnabled ? "Connected" : "Disabled" : "Missing" }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Glyph => Node.Kind switch { UsbTopologyNodeKind.Computer => "\uE7F4", UsbTopologyNodeKind.HostController => "\uE950", UsbTopologyNodeKind.RootHub => "\uECF0", UsbTopologyNodeKind.Hub => "\uECF0", UsbTopologyNodeKind.Port => "\uE839", UsbTopologyNodeKind.Interfaces => "\uE8D4", UsbTopologyNodeKind.Interface => "\uE950", _ => Node.DeviceClass.Contains("Audio", StringComparison.OrdinalIgnoreCase) ? "\uE767" : Node.DeviceClass.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? "\uE765" : Node.DeviceClass.Contains("Mouse", StringComparison.OrdinalIgnoreCase) ? "\uE962" : "\uECF0" };
    public bool CanConfigure => Node.Kind is UsbTopologyNodeKind.Hub or UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Missing;
    public Visibility PolicyVisibility => CanConfigure ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TechnicalVisibility { get; set; } = Visibility.Collapsed;
    public string Technical => string.Join(" · ", new[] { Node.InstanceId, Node.PortChain.Length > 0 ? "Port " + Node.PortChain : "" }.Where(value => value.Length > 0));
    public int DesktopIndex { get => _desktopIndex; set { if (_desktopIndex == value) return; _desktopIndex = value; Changed(); PolicyChanged?.Invoke(this); } }
    public int SimRigIndex { get => _simRigIndex; set { if (_simRigIndex == value) return; _simRigIndex = value; Changed(); PolicyChanged?.Invoke(this); } }
    public DeviceModeAction Desktop => (DeviceModeAction)_desktopIndex;
    public DeviceModeAction SimRig => (DeviceModeAction)_simRigIndex;
    public string EffectivePolicy { get => _effectivePolicy; set { _effectivePolicy = value; Changed(); } }
    public event Action<UsbTopologyRow>? PolicyChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
