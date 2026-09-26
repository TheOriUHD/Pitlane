using SimRigMode.Models;
using SimRigMode.Services.Display;
using SimRigMode.Tests.Fakes;
using Xunit;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Tests;

/// <summary>
/// Switching a monitor ON. The pipeline used to resolve profiles against the displays that were
/// already on, so a profile that needed a connected-but-switched-out monitor was refused as
/// "not connected": switching from Sim Rig back to Desktop could never have worked.
/// Everything here runs against FakeDisplayConfigGateway; no graphics call is made.
/// </summary>
public sealed class DisplayActivationTests
{
    private static DisplayApplyEngine Engine(FakeDisplayConfigGateway gateway) => new(gateway, (_, _) => Task.CompletedTask);

    private static DisplayApplyOptions Options(bool dryRun = false)
        => new(DryRun: dryRun, SettleMilliseconds: 0, IndirectSettleMilliseconds: 0,
               VerifyRetries: 2, VerifyRetryDelayMilliseconds: 0);

    private static DisplaySnapshot Applied(FakeDisplayConfigGateway gateway) =>
        gateway.Submitted[gateway.SubmittedFlags.FindIndex(flags => (flags & N.SDC_APPLY) != 0)];

    [Fact]
    public async Task SimRigToDesktop_SwitchesOnTheDeskMonitorThatWasConnectedButOff()
    {
        // On screen: the rig monitor alone. The desk monitor is plugged in but out of the layout.
        var live = TopologyFixture.Empty().WithRigMonitor(sourceId: 3);
        var desktop = TopologyFixture.Empty().WithDeskMonitor(sourceId: 1);
        var gateway = new FakeDisplayConfigGateway(live);
        gateway.SwitchedOut.Add(desktop.Paths[0].ToConnected());

        var outcome = await Engine(gateway).ApplyAsync(desktop, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var desk = Assert.Single(Applied(gateway).Paths);
        Assert.Equal(TopologyFixture.DeskMonitorPath, desk.MonitorDevicePath);
        Assert.Equal(4355u, desk.TargetId);
        Assert.Equal(1u, desk.SourceId);   // the source it was captured on was free, so it keeps it
        Assert.NotEqual(0u, desk.StatusFlags & N.DISPLAYCONFIG_PATH_ACTIVE);

        var mode = Applied(gateway).Modes[(int)desk.SourceModeInfoIdx];
        Assert.Equal((3840u, 2160u), (mode.Width, mode.Height));
        Assert.Equal((desk.SourceAdapter, desk.SourceId), (mode.Adapter, mode.Id));
        Assert.Contains(outcome.Log, line => line.Contains("switched on"));
    }

    [Fact]
    public async Task DesktopToSimRig_SwitchesOnTheRigMonitorAndLeavesTheDeskOut()
    {
        var live = TopologyFixture.Empty().WithDeskMonitor(sourceId: 0);
        var simRig = TopologyFixture.Empty().WithRigMonitor(sourceId: 3);
        var gateway = new FakeDisplayConfigGateway(live);
        gateway.SwitchedOut.Add(simRig.Paths[0].ToConnected());

        var outcome = await Engine(gateway).ApplyAsync(simRig, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var rig = Assert.Single(Applied(gateway).Paths);
        Assert.Equal(TopologyFixture.RigMonitorPath, rig.MonitorDevicePath);
        Assert.Equal(3u, rig.SourceId);
    }

    [Fact]
    public async Task ASwitchedOnMonitor_GetsAnotherSourceWhenItsSavedOneIsStillInUse()
    {
        // The rig monitor stays on, using source 1 - which is where the desk monitor was captured.
        var live = TopologyFixture.Empty().WithRigMonitor(sourceId: 1);
        var profile = TopologyFixture.Empty().WithRigMonitor(sourceId: 2).WithDeskMonitor(sourceId: 1);
        var gateway = new FakeDisplayConfigGateway(live);
        gateway.SwitchedOut.Add(TopologyFixture.Empty().WithDeskMonitor(sourceId: 1).Paths[0].ToConnected());

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var applied = Applied(gateway);
        var rig = applied.Paths.Single(path => path.MonitorDevicePath == TopologyFixture.RigMonitorPath);
        var desk = applied.Paths.Single(path => path.MonitorDevicePath == TopologyFixture.DeskMonitorPath);
        Assert.Equal(1u, rig.SourceId);
        Assert.Equal(0u, desk.SourceId);
    }

    [Fact]
    public void WithNoFreeSource_TheSwitchIsRefusedRatherThanTakingOneAway()
    {
        var live = TopologyFixture.Empty().WithRigMonitor(sourceId: 0);
        var profile = TopologyFixture.Empty().WithRigMonitor(sourceId: 0).WithDeskMonitor(sourceId: 1);
        // Only source 0 can drive the desk monitor, and the rig monitor keeps it.
        var desk = TopologyFixture.Empty().WithDeskMonitor(sourceId: 1).Paths[0].ToConnected(0);

        var result = DisplaySubmissionBuilder.Build(profile, live, [desk]);

        Assert.Null(result.Submission);
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("no free output"));
    }

    [Fact]
    public void AMonitorThatIsNotPluggedIn_IsStillRefused()
    {
        var live = TopologyFixture.Empty().WithRigMonitor();
        var profile = TopologyFixture.Empty().WithDeskMonitor();

        var result = DisplaySubmissionBuilder.Build(profile, live, []);

        Assert.Null(result.Submission);
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("not currently connected"));
    }

    [Fact]
    public void ASwitchedOutIndirectDisplay_IsNeverSwitchedOn()
    {
        // Indirect displays are carried through, never switched - in either direction.
        var live = TopologyFixture.Empty().WithDeskMonitor();
        var profile = TopologyFixture.Empty().WithDeskMonitor().WithPrompter();
        var prompter = TopologyFixture.Empty().WithPrompter().Paths[0].ToConnected();

        var result = DisplaySubmissionBuilder.Build(profile, live, [prompter]);

        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        Assert.DoesNotContain(submission.Paths, path => path.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        Assert.Contains(result.Notes, note => note.Contains("indirect displays are left as they are"));
    }

    [Fact]
    public void AnUnpluggedIndirectDisplay_DoesNotBlockTheSwitch()
    {
        // The Prompter being unplugged must not make Desktop mode unusable.
        var live = TopologyFixture.Empty().WithDeskMonitor();
        var profile = TopologyFixture.Empty().WithDeskMonitor().WithPrompter();

        var result = DisplaySubmissionBuilder.Build(profile, live, []);

        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        Assert.Single(submission.Paths);
    }

    [Fact]
    public async Task SwitchingOn_NeverWritesTheWindowsDisplayDatabase()
    {
        var live = TopologyFixture.Empty().WithRigMonitor();
        var desktop = TopologyFixture.Empty().WithDeskMonitor();
        var gateway = new FakeDisplayConfigGateway(live);
        gateway.SwitchedOut.Add(desktop.Paths[0].ToConnected());

        await Engine(gateway).ApplyAsync(desktop, Options());

        Assert.NotEmpty(gateway.SubmittedFlags);
        Assert.All(gateway.SubmittedFlags, flags => Assert.Equal(0u, flags & N.SDC_SAVE_TO_DATABASE));
    }

    [Fact]
    public async Task DryRun_ValidatesTheSwitchOnButAppliesNothing()
    {
        var live = TopologyFixture.Empty().WithRigMonitor();
        var desktop = TopologyFixture.Empty().WithDeskMonitor();
        var gateway = new FakeDisplayConfigGateway(live);
        gateway.SwitchedOut.Add(desktop.Paths[0].ToConnected());

        var outcome = await Engine(gateway).ApplyAsync(desktop, Options(dryRun: true));

        Assert.Equal(DisplayApplyStatus.DryRunValidated, outcome.Status);
        Assert.False(gateway.AnythingWasApplied);
    }
}
