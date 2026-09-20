using SimRigMode.Models;
using SimRigMode.Services;

// CONTAINMENT GATE — added 2026-09-20 during safety hardening.
//
// This suite mutates the live machine. It performs real SetDisplayConfig applies
// with SDC_APPLY | SDC_SAVE_TO_DATABASE against the current desktop, and it seeds a
// UsbRule that reaches the production elevation path. That elevation path relaunches
// Environment.ProcessPath with "runas"; under this test host that is
// SimRigMode.SmokeTests.exe, which re-runs this file and elevates again — the
// unbounded UAC/process chain described in docs/report.md.
//
// Worse, the rule seeded below never sets InstanceId. UsbTopologyService.MatchRule
// compares InstanceId with no emptiness guard, and the synthetic Computer and Port
// nodes also have an empty InstanceId — so that rule matches every port in the tree
// and ClosestRule makes it the effective policy for EVERY device on the machine.
// The resolved batch is not empty; it is machine-wide.
//
// This project is scheduled for replacement by tests/SimRigMode.Tests (xUnit, fakes,
// no hardware). Until then it refuses to run without a deliberate opt-in.
if (Environment.GetEnvironmentVariable("SIMRIGMODE_ALLOW_HARDWARE_TESTS") != "1")
{
    Console.Error.WriteLine(
        "REFUSED: SimRigMode.SmokeTests mutates the live machine (real display topology\n" +
        "applies and a machine-wide USB disable batch that reaches UAC elevation).\n" +
        "It is quarantined pending replacement by tests/SimRigMode.Tests.\n" +
        "See docs/report.md. Do not set SIMRIGMODE_ALLOW_HARDWARE_TESTS=1 on a machine\n" +
        "you care about.");
    return 2;
}

var root = Path.Combine(Path.GetTempPath(), "SimRigMode-SmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var log = new LoggingService(Path.Combine(root, "Logs"));
    var config = new ConfigurationService(log, Path.Combine(root, "State"));
    await config.LoadAsync();
    config.Current.General.UsbEnumerationWaitSeconds = 0;
    config.Current.General.HardwarePowerOnWaitSeconds = 0;
    config.Current.General.HardwarePowerOffDelaySeconds = 0;
    config.Current.General.DesktopHotkey = "Ctrl+Shift+F10";
    config.Current.UsbRules.Add(new UsbRule { IdentityKey = "port:test:1234:5678", FriendlyName = "Offline rig hub", IsSubtree = true, Desktop = DeviceModeAction.Disabled, SimRig = DeviceModeAction.Enabled, LastKnownTargets = [new() { InstanceId = "USB\\VID_1234&PID_5678\\TEST", FriendlyName = "Rig device" }] });
    await config.SaveAsync();
    Assert(File.Exists(config.ConfigPath), "atomic config save");
    var reloaded = new ConfigurationService(log, Path.Combine(root, "State")); await reloaded.LoadAsync();
    Assert(reloaded.Current.General.DesktopHotkey == "Ctrl+Shift+F10", "config round trip");
    Assert(reloaded.Current.SchemaVersion == 2 && reloaded.Current.UsbRules.Single().IsSubtree && reloaded.Current.UsbRules.Single().LastKnownTargets.Count == 1, "USB subtree identity persistence");
    Assert(!File.Exists(config.ConfigPath + ".tmp"), "temporary config cleanup");

    var displays = new DisplayService(); var snapshot = displays.CaptureCurrent(); Assert(snapshot.Paths.Count > 0, "DisplayConfig capture");
    Assert(snapshot.Paths.All(p => !string.IsNullOrWhiteSpace(p.MonitorDevicePath) || !string.IsNullOrWhiteSpace(p.SourceName)), "display stable identity");
    var originalDisplayCount = snapshot.Paths.Count;
    var manualProfile = displays.SetDisplayIncluded(null, snapshot, snapshot.Paths[0], true);
    Assert(manualProfile.Paths.Count == 1 && manualProfile.Modes.Count > 0, "manual display inclusion");
    Assert(manualProfile.Paths.All(p => p.SourceModeInfoIdx < manualProfile.Modes.Count && p.TargetModeInfoIdx < manualProfile.Modes.Count), "manual display mode remapping");
    Assert(manualProfile.Paths.Any(p => manualProfile.Modes[(int)p.SourceModeInfoIdx] is { PositionX: 0, PositionY: 0 }), "manual display primary normalization");
    if (snapshot.Paths.Count > 1)
    {
        manualProfile = displays.SetDisplayIncluded(manualProfile, snapshot, snapshot.Paths[1], true);
        Assert(manualProfile.Paths.Count == 2, "manual second display inclusion");
        manualProfile = displays.SetDisplayIncluded(manualProfile, snapshot, snapshot.Paths[0], false);
        Assert(manualProfile.Paths.Count == 1, "manual display exclusion");
    }
    Assert(snapshot.Paths.Count == originalDisplayCount, "manual editing preserves live topology");
    var primaryPath = snapshot.Paths.FirstOrDefault(p => snapshot.Modes[(int)p.SourceModeInfoIdx] is { PositionX: 0, PositionY: 0 });
    if (primaryPath is not null && snapshot.Paths.Count > 1)
    {
        var withoutPrimary = displays.SetDisplayIncluded(snapshot, snapshot, primaryPath, false);
        Assert(withoutPrimary.Paths.Any(p => withoutPrimary.Modes[(int)p.SourceModeInfoIdx] is { PositionX: 0, PositionY: 0 }), "manual primary removal rebases topology");
    }
    var usb = new UsbDeviceService(); var devices = usb.Enumerate(); Assert(devices.All(d => !string.IsNullOrWhiteSpace(d.InstanceId)), "PnP instance IDs");
    var topology = new UsbTopologyService(usb).Enumerate();
    Assert(topology.Flatten().Any(node => node.Kind == UsbTopologyNodeKind.HostController), "USB host-controller topology roots");
    Assert(topology.Flatten().Any(node => node.Kind == UsbTopologyNodeKind.Port), "physical USB port hierarchy");
    Assert(topology.Flatten().Where(node => node.Kind == UsbTopologyNodeKind.Device).All(node => !string.IsNullOrWhiteSpace(node.Name)), "USB topology product names");
    Assert(topology.Flatten().Where(node => node.Kind == UsbTopologyNodeKind.Device).Any(node => !string.IsNullOrWhiteSpace(node.Speed)), "USB hub IOCTL connection speeds");
    var policyRoot = topology.Flatten().First(node => node.Kind == UsbTopologyNodeKind.RootHub && node.Flatten().Count(child => child.Kind == UsbTopologyNodeKind.Device && !child.IsCritical) >= 2);
    var policyLeaves = policyRoot.Flatten().Where(node => node.Kind == UsbTopologyNodeKind.Device && !node.IsCritical).ToList();
    var subtreeRule = new UsbRule { IdentityKey = policyRoot.IdentityKey, InstanceId = policyRoot.InstanceId, FriendlyName = policyRoot.Name, IsSubtree = true, Desktop = DeviceModeAction.Disabled };
    var overrideRule = new UsbRule { IdentityKey = policyLeaves[0].IdentityKey, InstanceId = policyLeaves[0].InstanceId, FriendlyName = policyLeaves[0].Name, Desktop = DeviceModeAction.Enabled };
    var resolved = new UsbTopologyService(usb).ResolveOperations([subtreeRule, overrideRule], WorkspaceMode.Desktop, DeviceModeAction.Disabled);
    Assert(resolved.Count > 0 && resolved.All(operation => !operation.InstanceId.Equals(policyLeaves[0].InstanceId, StringComparison.OrdinalIgnoreCase)), "subtree inheritance and closest-device override");
    foreach (var known in topology.Flatten().Where(node => node.Vid is "346E" or "2433")) Console.WriteLine($"RIG DEVICE: {known.Name} [{known.Vid}:{known.Pid}] at port {known.PortChain}");
    var audio = new AudioService(); var endpoints = audio.Enumerate(); Assert(endpoints.Count >= 2, "Core Audio enumeration");

    var switcher = new ModeSwitchService(reloaded, displays, usb, audio, new HomeAssistantService(), log);
    var desktop = await switcher.SwitchAsync(WorkspaceMode.Desktop); Assert(desktop.Any(s => s.Name == "Displays configured" && !s.Success), "missing display layout warning");
    var rig = await switcher.SwitchAsync(WorkspaceMode.SimRig); Assert(rig.Any(s => s.Name == "Displays configured" && !s.Success), "repeat transition with missing hardware");
    Assert(reloaded.Current.CurrentMode == WorkspaceMode.SimRig, "current mode persistence");
    reloaded.Current.Desktop.Displays = snapshot; reloaded.Current.SimRig.Displays = snapshot;
    var appliedDesktop = await switcher.SwitchAsync(WorkspaceMode.Desktop); Assert(appliedDesktop.First(s => s.Name == "Displays configured").Success, "apply captured native display topology");
    var appliedRig = await switcher.SwitchAsync(WorkspaceMode.SimRig); Assert(appliedRig.First(s => s.Name == "Displays configured").Success, "repeat complete mode transition");
    var cancelled = new CancellationToken(true); try { await switcher.SwitchAsync(WorkspaceMode.Desktop, cancelled); throw new Exception("Cancellation was not observed"); } catch (OperationCanceledException) { }
    Console.WriteLine($"Smoke tests passed: {snapshot.Paths.Count} displays; {devices.Count} USB/PnP nodes; {endpoints.Count - 2} audio endpoints.");
    return 0;
}
finally { try { Directory.Delete(root, true); } catch { } }

static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS: " + name); }
