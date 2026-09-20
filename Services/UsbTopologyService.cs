using System.Text.RegularExpressions;
using SimRigMode.Models;

namespace SimRigMode.Services;

public sealed class UsbTopologyService
{
    private readonly UsbDeviceService _devices;
    public UsbTopologyService(UsbDeviceService devices) => _devices = devices;

    public UsbTopology Enumerate(bool includeHubIoctlDetails = true)
    {
        var inventory = _devices.Enumerate();
        var byId = inventory.ToDictionary(item => item.InstanceId, StringComparer.OrdinalIgnoreCase);
        var physicalItems = inventory.Where(item => IsPhysical(item, byId)).ToList();
        var physical = physicalItems.ToDictionary(item => item.InstanceId, CreateNode, StringComparer.OrdinalIgnoreCase);
        try
        {
            var portFacts = includeHubIoctlDetails ? new UsbHubIoctlService().Probe() : new Dictionary<string, UsbHubIoctlService.PortFacts>();
            foreach (var item in physicalItems.Where(item => !string.IsNullOrWhiteSpace(item.DriverKey) && portFacts.ContainsKey(item.DriverKey)))
            {
                var facts = portFacts[item.DriverKey];
                physical[item.InstanceId].PortNumber = facts.Port;
                physical[item.InstanceId].Speed = facts.Speed;
                if (!string.IsNullOrWhiteSpace(facts.ProductString) && IsGenericName(physical[item.InstanceId].Name)) physical[item.InstanceId].Name = facts.ProductString;
            }
        }
        catch { /* Configuration Manager location paths remain a reliable fallback. */ }
        var computer = new UsbTopologyNode { Kind = UsbTopologyNodeKind.Computer, IdentityKey = "computer", Name = Environment.MachineName, IsPresent = true, IsEnabled = true };

        foreach (var item in physicalItems)
        {
            var node = physical[item.InstanceId]; var parentId = item.ParentInstanceId;
            while (!string.IsNullOrWhiteSpace(parentId) && !physical.ContainsKey(parentId) && byId.TryGetValue(parentId, out var parentItem)) parentId = parentItem.ParentInstanceId;
            var parent = !string.IsNullOrWhiteSpace(parentId) && physical.TryGetValue(parentId, out var found) ? found : computer;
            if (parent.Kind is UsbTopologyNodeKind.Hub or UsbTopologyNodeKind.RootHub && node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Hub)
            {
                var port = new UsbTopologyNode { Kind = UsbTopologyNodeKind.Port, Name = $"Port {node.PortNumber}", PortNumber = node.PortNumber, PortChain = node.PortChain, IdentityKey = $"{parent.IdentityKey}/port:{node.PortNumber}", IsPresent = true, IsEnabled = true, Parent = parent };
                parent.Children.Add(port); node.Parent = port; port.Children.Add(node);
            }
            else { node.Parent = parent; parent.Children.Add(node); }
        }

        foreach (var item in inventory.Where(item => !physical.ContainsKey(item.InstanceId)))
        {
            var parentId = item.ParentInstanceId;
            while (!string.IsNullOrWhiteSpace(parentId) && !physical.ContainsKey(parentId) && byId.TryGetValue(parentId, out var parentItem)) parentId = parentItem.ParentInstanceId;
            if (string.IsNullOrWhiteSpace(parentId) || !physical.TryGetValue(parentId, out var parent)) continue;
            var group = parent.Children.FirstOrDefault(child => child.Kind == UsbTopologyNodeKind.Interfaces);
            if (group is null) { group = new() { Kind = UsbTopologyNodeKind.Interfaces, Name = "Interfaces", IdentityKey = parent.IdentityKey + "/interfaces", Parent = parent, IsPresent = true, IsEnabled = true }; parent.Children.Add(group); }
            var child = CreateNode(item); child.Kind = UsbTopologyNodeKind.Interface; child.Parent = group; group.Children.Add(child);
        }

        PromoteUsefulInterfaceNames(computer);
        Sort(computer); return new UsbTopology { Roots = [computer] };
    }

    public IReadOnlyList<UsbDeviceService.DeviceOperation> ResolveOperations(
        IReadOnlyList<UsbRule> rules, WorkspaceMode mode, DeviceModeAction requested)
    {
        var topology = Enumerate(false);
        var nodes = topology.Flatten().Where(node => node.Kind is UsbTopologyNodeKind.Hub or UsbTopologyNodeKind.Device).ToList();
        var operations = new Dictionary<string, UsbDeviceService.DeviceOperation>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            var directRule = MatchRule(rules, node);
            if (node.Kind == UsbTopologyNodeKind.Hub && directRule?.DisableHubItself == true)
            {
                if (Action(directRule, mode) == requested && NeedsChange(node, requested) && !node.IsCritical && !string.IsNullOrWhiteSpace(node.InstanceId))
                    operations[node.InstanceId] = new(node.InstanceId, requested == DeviceModeAction.Enabled);
                continue;
            }

            // A normal subtree rule operates the physical endpoints, not the hub. This
            // preserves the possibility of a closer child override.
            if (node.Kind != UsbTopologyNodeKind.Device) continue;
            var effectiveRule = ClosestRule(rules, node);
            if (effectiveRule is null || Action(effectiveRule, mode) != requested || !NeedsChange(node, requested) || node.IsCritical || string.IsNullOrWhiteSpace(node.InstanceId)) continue;
            if (HasSelfDisabledHubAncestor(rules, node, mode)) continue;
            operations[node.InstanceId] = new(node.InstanceId, requested == DeviceModeAction.Enabled);
        }

        // Disabled branches normally retain devnodes. If Windows temporarily omits a
        // child during re-enumeration, last-known identities let the next pass recover it.
        foreach (var rule in rules.Where(rule => Action(rule, mode) == requested))
        {
            if (nodes.Any(node => ReferenceEquals(MatchRule([rule], node), rule))) continue;
            foreach (var target in rule.LastKnownTargets.Where(target => !target.IsCritical && UsbDeviceService.IsKnownDevice(target.InstanceId)))
                operations.TryAdd(target.InstanceId, new(target.InstanceId, requested == DeviceModeAction.Enabled));
        }
        return operations.Values.ToList();
    }

    public static UsbRule? MatchRule(IEnumerable<UsbRule> rules, UsbTopologyNode node) => rules.FirstOrDefault(rule =>
        !string.IsNullOrWhiteSpace(rule.IdentityKey) && rule.IdentityKey.Equals(node.IdentityKey, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(rule.ContainerId) && rule.ContainerId.Equals(node.ContainerId, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(rule.Serial) && rule.Serial.Equals(node.Serial, StringComparison.OrdinalIgnoreCase) && rule.Vid == node.Vid && rule.Pid == node.Pid ||
        !string.IsNullOrWhiteSpace(rule.LocationPath) && rule.LocationPath.Equals(node.LocationPath, StringComparison.OrdinalIgnoreCase) && rule.Vid == node.Vid && rule.Pid == node.Pid ||
        rule.InstanceId.Equals(node.InstanceId, StringComparison.OrdinalIgnoreCase));

    private static UsbRule? ClosestRule(IReadOnlyList<UsbRule> rules, UsbTopologyNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            var rule = MatchRule(rules, current);
            if (rule is not null) return rule;
        }
        return null;
    }
    private static bool HasSelfDisabledHubAncestor(IReadOnlyList<UsbRule> rules, UsbTopologyNode node, WorkspaceMode mode)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var rule = MatchRule(rules, current);
            if (rule?.DisableHubItself == true && Action(rule, mode) == DeviceModeAction.Disabled) return true;
        }
        return false;
    }
    private static DeviceModeAction Action(UsbRule rule, WorkspaceMode mode) => mode == WorkspaceMode.Desktop ? rule.Desktop : rule.SimRig;
    private static bool NeedsChange(UsbTopologyNode node, DeviceModeAction requested) => requested == DeviceModeAction.Enabled ? !node.IsEnabled : node.IsEnabled;

    public static string Identity(UsbDeviceInfo item)
    {
        if (!string.IsNullOrWhiteSpace(item.Serial) && !string.IsNullOrWhiteSpace(item.Vid)) return $"usb:{item.Vid}:{item.Pid}:{item.Serial}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(item.ContainerId)) return "container:" + item.ContainerId.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(item.LocationPath)) return $"port:{item.LocationPath}:{item.Vid}:{item.Pid}".ToLowerInvariant();
        return "instance:" + item.InstanceId.ToLowerInvariant();
    }

    private static bool IsPhysical(UsbDeviceInfo item, IReadOnlyDictionary<string, UsbDeviceInfo> byId)
    {
        if (item.IsHostController || item.IsRootHub || item.IsHub) return true;
        if (!item.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || item.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(item.ParentInstanceId)) return true;
        return byId.TryGetValue(item.ParentInstanceId, out var parent) && (parent.IsHub || parent.IsRootHub || parent.IsHostController);
    }
    private static UsbTopologyNode CreateNode(UsbDeviceInfo item)
    {
        var chain = string.Join('.', Regex.Matches(item.LocationPath, @"USB\((\d+)\)").Select(match => match.Groups[1].Value));
        var port = int.TryParse(chain.Split('.').LastOrDefault(), out var number) ? number : ParsePort(item.LocationInfo);
        var name = BetterName(item);
        return new() { Kind = item.IsHostController ? UsbTopologyNodeKind.HostController : item.IsRootHub ? UsbTopologyNodeKind.RootHub : item.IsHub ? UsbTopologyNodeKind.Hub : UsbTopologyNodeKind.Device, IdentityKey = Identity(item), Name = name, Manufacturer = item.Manufacturer, InstanceId = item.InstanceId, ContainerId = item.ContainerId, Vid = item.Vid, Pid = item.Pid, Serial = item.Serial, DeviceClass = item.DeviceClass, LocationPath = item.LocationPath, PortChain = chain, PortNumber = port, IsEnabled = item.IsEnabled, IsPresent = item.IsPresent, IsCritical = item.IsCritical };
    }
    private static string BetterName(UsbDeviceInfo item) => item.IsHostController && !string.IsNullOrWhiteSpace(item.FriendlyName) ? item.FriendlyName : !string.IsNullOrWhiteSpace(item.ProductName) ? item.ProductName : !string.IsNullOrWhiteSpace(item.FriendlyName) ? item.FriendlyName : string.IsNullOrWhiteSpace(item.VidPid) ? "USB device" : $"USB device {item.VidPid}";
    private static void PromoteUsefulInterfaceNames(UsbTopologyNode node)
    {
        foreach (var child in node.Children) PromoteUsefulInterfaceNames(child);
        if (node.Kind != UsbTopologyNodeKind.Device || !IsGenericName(node.Name)) return;
        var candidate = node.Children.Where(child => child.Kind == UsbTopologyNodeKind.Interfaces)
            .SelectMany(child => child.Children).Select(child => child.Name)
            .Where(name => !IsGenericName(name)).OrderByDescending(NameScore).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(candidate)) node.Name = candidate;
    }
    private static bool IsGenericName(string name) => string.IsNullOrWhiteSpace(name) ||
        name.Contains("USB Composite Device", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("USB Input Device", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("USB device", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Device", StringComparison.OrdinalIgnoreCase);
    private static int NameScore(string name) => (name.Contains("MOZA", StringComparison.OrdinalIgnoreCase) || name.Contains("Asetek", StringComparison.OrdinalIgnoreCase) ? 100 : 0) + name.Length;
    private static int ParsePort(string location) { var match = Regex.Match(location, @"Port_#0*(\d+)", RegexOptions.IgnoreCase); return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : 0; }
    private static void Sort(UsbTopologyNode node) { node.Children = node.Children.OrderBy(child => child.Kind == UsbTopologyNodeKind.Interfaces ? 1 : 0).ThenBy(child => child.PortNumber).ThenBy(child => child.Name).ToList(); foreach (var child in node.Children) Sort(child); }
}
