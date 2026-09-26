using System.Runtime.InteropServices;
using SimRigMode.Models;
using SimRigMode.NativeInterop;
using SimRigMode.Services.Display;
using SimRigMode.Tests.Fakes;
using Xunit;

namespace SimRigMode.Tests;

/// <summary>
/// The 2026-09-24 regression suite. The stutter blamed on the Prompter was the PG27AQDP (HDMI):
/// detached from the desktop, it dropped off the bus and reconnected every 7.6 s, and every
/// reconnect stalled Windows' input and window handling. Attached, it never dropped out. So a
/// monitor on the park list is kept attached in a corner instead of being detached.
/// See docs/report.md.
///
/// Pure logic and the fake gateway only. SimRigMode sends monitors no commands at all, and
/// WindowsOwnedStateTests fails if a monitor power call ever comes back.
/// </summary>
public sealed class DisplayPowerTests
{
    private const string Rig = TopologyFixture.RigMonitorPath;
    private const string Desk = TopologyFixture.DeskMonitorPath;
    private const string Q27G4 = @"\\?\DISPLAY#AOCB403#5&1176aab&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string G2WG3 = @"\\?\DISPLAY#AOC2702#5&1176aab&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static readonly DateTime T0 = new(2026, 9, 24, 13, 3, 24, DateTimeKind.Utc);

    /// <summary>The configuration on the 24th: desk layout, rig layout, rig monitor on the park list.</summary>
    private static AppConfig ConfigWithRigListed()
    {
        var config = new AppConfig { CurrentMode = WorkspaceMode.Desktop };
        config.Desktop.Displays = TopologyFixture.Empty().WithDeskMonitor();
        config.SimRig.Displays = TopologyFixture.Empty().WithRigMonitor();
        DisplayPowerPolicy.Remember(config.General, Rig, "PG27AQDP", "watchdog");
        return config;
    }

    // ===== parking instead of detaching ============================================

    [Fact]
    public void SwitchingToDesktop_ParksTheRigMonitorInsteadOfDetachingIt()
    {
        var config = ConfigWithRigListed();
        var live = TopologyFixture.Empty().WithRigMonitor();                 // Sim Rig mode on screen
        var connected = new[] { live.Paths[0].ToConnected(), TopologyFixture.Empty().WithDeskMonitor().Paths[0].ToConnected() };

        var plan = DisplayPowerPolicy.PlanParking(config, config.Desktop.Displays!, live, connected);
        Assert.Equal(Rig, Assert.Single(plan).Path.MonitorDevicePath);

        var result = DisplaySubmissionBuilder.Build(DisplayPowerPolicy.WithParked(config.Desktop.Displays!, plan), live, connected);
        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        Assert.Contains(submission.Paths, path => path.MonitorDevicePath == Rig);    // still attached
        Assert.Contains(submission.Paths, path => path.MonitorDevicePath == Desk);   // desk switched on
    }

    [Fact]
    public void ADetachedLoopingMonitor_IsParkedWithTheModeFromItsSavedLayout()
    {
        // The state on the 24th: Desktop on screen, the rig monitor connected but detached and looping.
        var config = ConfigWithRigListed();
        var live = TopologyFixture.Empty().WithDeskMonitor();
        var connected = new[] { live.Paths[0].ToConnected(), TopologyFixture.Empty().WithRigMonitor().Paths[0].ToConnected() };

        var park = Assert.Single(DisplayPowerPolicy.PlanParking(config, live, live, connected));
        Assert.Same(config.SimRig.Displays, park.ModeSource);

        var result = DisplaySubmissionBuilder.Build(DisplayPowerPolicy.WithParked(live, [park]), live, connected);
        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        var parked = submission.Paths.Single(path => path.MonitorDevicePath == Rig);
        var mode = submission.Modes[(int)parked.SourceModeInfoIdx];
        Assert.Equal((2560u, 1440u), (mode.Width, mode.Height));
        Assert.Equal((3840, 2160), (mode.PositionX, mode.PositionY));   // bottom-right corner of the 4K main screen
    }

    [Fact]
    public async Task Parking_GoesThroughTheVerifiedPipeline_AndNeverWritesTheDatabase()
    {
        var config = ConfigWithRigListed();
        var gateway = new FakeDisplayConfigGateway(TopologyFixture.Empty().WithRigMonitor());
        gateway.SwitchedOut.Add(TopologyFixture.Empty().WithDeskMonitor().Paths[0].ToConnected());

        var plan = DisplayPowerPolicy.PlanParking(config, config.Desktop.Displays!, gateway.CaptureActive(), gateway.CaptureConnected());
        var outcome = await new DisplayApplyEngine(gateway, (_, _) => Task.CompletedTask).ApplyAsync(
            DisplayPowerPolicy.WithParked(config.Desktop.Displays!, plan),
            new DisplayApplyOptions(SettleMilliseconds: 0, IndirectSettleMilliseconds: 0, VerifyRetries: 2, VerifyRetryDelayMilliseconds: 0));

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        Assert.Equal(2, gateway.Current.Paths.Count);
        Assert.All(gateway.SubmittedFlags, flags => Assert.Equal(0u, flags & DisplayConfigNative.SDC_SAVE_TO_DATABASE));
    }

    [Fact]
    public async Task TheLayoutIsReportedAppliedOnce_SoTheParkedMonitorIsCoveredBeforeTheSwitchEnds()
    {
        // 2026-09-26: the cover came up only when the whole switch had ended, 18 s after the
        // screens changed, because Home Assistant did not answer.
        var config = ConfigWithRigListed();
        var gateway = new FakeDisplayConfigGateway(TopologyFixture.Empty().WithRigMonitor());
        gateway.SwitchedOut.Add(TopologyFixture.Empty().WithDeskMonitor().Paths[0].ToConnected());
        var plan = DisplayPowerPolicy.PlanParking(config, config.Desktop.Displays!, gateway.CaptureActive(), gateway.CaptureConnected());
        var applied = 0;

        var outcome = await new DisplayApplyEngine(gateway, (_, _) => Task.CompletedTask).ApplyAsync(
            DisplayPowerPolicy.WithParked(config.Desktop.Displays!, plan),
            new DisplayApplyOptions(SettleMilliseconds: 0, IndirectSettleMilliseconds: 0, VerifyRetries: 2, VerifyRetryDelayMilliseconds: 0, Applied: () => applied++));

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        Assert.Equal(1, applied);
        var screen = Assert.Single(DisplayPowerPolicy.LocateParked(gateway.CaptureActive(), [Rig]));
        Assert.Equal((3840, 2160), (screen.X, screen.Y));
    }

    [Fact]
    public async Task ADryRun_NeverReportsALayoutApplied()
    {
        var gateway = new FakeDisplayConfigGateway(TopologyFixture.Empty().WithRigMonitor());
        var applied = 0;

        await new DisplayApplyEngine(gateway, (_, _) => Task.CompletedTask).ApplyAsync(
            TopologyFixture.Empty().WithRigMonitor(),
            new DisplayApplyOptions(DryRun: true, SettleMilliseconds: 0, IndirectSettleMilliseconds: 0, Applied: () => applied++));

        Assert.Equal(0, applied);
    }

    [Fact]
    public void TheNormalBuildParks()
        // Only the hand-run standby test build leaves listed monitors out (-p:StandbyTest=true).
        => Assert.False(Product.IsStandbyTest);

    [Fact]
    public void AMonitorTheLayoutUses_IsNotParked()
    {
        var config = ConfigWithRigListed();
        var live = TopologyFixture.Empty().WithDeskMonitor();
        var connected = new[] { live.Paths[0].ToConnected(), TopologyFixture.Empty().WithRigMonitor().Paths[0].ToConnected() };

        Assert.Empty(DisplayPowerPolicy.PlanParking(config, config.SimRig.Displays!, live, connected));
    }

    [Fact]
    public void AnUnlistedMonitor_IsDetachedAsBefore()
    {
        var config = ConfigWithRigListed();
        DisplayPowerPolicy.Forget(config.General, Rig);
        var live = TopologyFixture.Empty().WithRigMonitor();

        Assert.Empty(DisplayPowerPolicy.PlanParking(config, config.Desktop.Displays!, live, [live.Paths[0].ToConnected()]));
    }

    [Fact]
    public void AMonitorThatIsNotConnected_IsNotParked()
    {
        var config = ConfigWithRigListed();
        var live = TopologyFixture.Empty().WithDeskMonitor();

        Assert.Empty(DisplayPowerPolicy.PlanParking(config, live, live, [live.Paths[0].ToConnected()]));
    }

    [Fact]
    public void AnIndirectDisplay_IsNeverParked()
    {
        var config = ConfigWithRigListed();
        DisplayPowerPolicy.Remember(config.General, TopologyFixture.PrompterMonitorPath, "Elgato Prom.", "user");
        var live = TopologyFixture.Empty().WithDeskMonitor().WithPrompter();

        var plan = DisplayPowerPolicy.PlanParking(config, TopologyFixture.Empty().WithDeskMonitor(), live, []);

        Assert.DoesNotContain(plan, park => park.Path.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
    }

    // ===== where a parked monitor goes =============================================

    [Fact]
    public void OnYourDesk_TheParkingSpotIsTheMainScreensBottomRightCorner()
    {
        // The real desk: 4K main at 0,0, Q27G4 above it, 27G2WG3 to its left.
        var layout = TopologyFixture.Empty()
            .With("PG27UCDM", Desk, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 1, 4355, 3840, 2160, 0, 0)
            .With("Q27G4", Q27G4, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 2, 4357, 2560, 1440, 735, -1440)
            .With("27G2WG3-", G2WG3, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 0, 4353, 1920, 1080, -1920, 567);

        Assert.Equal((3840, 2160), DisplayPowerPolicy.ParkingPosition(layout, 2560, 1440));
    }

    [Fact]
    public void WhenThatCornerIsTaken_AnotherCornerIsUsed()
    {
        var layout = TopologyFixture.Empty()
            .With("main", "a", TopologyFixture.GpuAdapterPath, 1, 0, 100, 1920, 1080, 0, 0)
            .With("other", "b", TopologyFixture.GpuAdapterPath, 1, 1, 101, 1920, 1080, 1920, 1080);

        // Bottom-right of "main" would overlap "other"; top-right touches "main" at one point only.
        Assert.Equal((1920, -1000), DisplayPowerPolicy.ParkingPosition(layout, 1000, 1000));
    }

    [Fact]
    public void AParkingSpot_NeverSharesAnEdgeOrOverlaps()
    {
        // A 3x1 row: every corner spot of every display is checked against all of them.
        var layout = TopologyFixture.Empty()
            .With("left", "a", TopologyFixture.GpuAdapterPath, 1, 0, 100, 1920, 1080, -1920, 0)
            .With("main", "b", TopologyFixture.GpuAdapterPath, 1, 1, 101, 1920, 1080, 0, 0)
            .With("right", "c", TopologyFixture.GpuAdapterPath, 1, 2, 102, 1920, 1080, 1920, 0);

        var (x, y) = DisplayPowerPolicy.ParkingPosition(layout, 2560, 1440);

        foreach (var mode in layout.Modes.Where(mode => mode.InfoType == 1))
        {
            var overlaps = x < mode.PositionX + mode.Width && mode.PositionX < x + 2560 && y < mode.PositionY + mode.Height && mode.PositionY < y + 1440;
            Assert.False(overlaps);
        }
        Assert.True(y == 1080 || y == -1440);   // diagonal to a corner, never beside a display
    }

    // ===== the park list =============================================================

    [Fact]
    public void Remember_IgnoresCaseAndDuplicates_AndForgetRemovesParkedStateToo()
    {
        var general = new GeneralConfig();

        Assert.True(DisplayPowerPolicy.Remember(general, Rig, "PG27AQDP", "user"));
        Assert.False(DisplayPowerPolicy.Remember(general, Rig.ToUpperInvariant(), "PG27AQDP", "watchdog"));
        Assert.Single(general.SwitchOffWhenUnused);
        general.ParkedDisplays.Add(Rig);
        Assert.True(DisplayPowerPolicy.IsParked(general, Rig.ToLowerInvariant()));

        Assert.True(DisplayPowerPolicy.Forget(general, Rig.ToLowerInvariant()));
        Assert.Empty(general.SwitchOffWhenUnused);
        Assert.Empty(general.ParkedDisplays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a path")]
    [InlineData(@"USB\VID_346E&PID_0004\SN123")]
    [InlineData(@"C:\Windows\System32")]
    public void Remember_RefusesAnythingThatIsNotAMonitorPath(string path)
        => Assert.False(DisplayPowerPolicy.Remember(new GeneralConfig(), path, "x", "user"));

    [Fact]
    public void Sanitize_DropsUnusableAndDuplicateEntries_AndStrayParkedState()
    {
        var general = new GeneralConfig();
        general.SwitchOffWhenUnused.Add(new() { MonitorDevicePath = Rig, FriendlyName = "PG27AQDP" });
        general.SwitchOffWhenUnused.Add(new() { MonitorDevicePath = Rig.ToUpperInvariant(), FriendlyName = "duplicate" });
        general.SwitchOffWhenUnused.Add(new() { MonitorDevicePath = @"C:\Windows", FriendlyName = "not a monitor" });
        general.SwitchOffWhenUnused.Add(null!);
        general.ParkedDisplays.AddRange([Rig, Desk, "junk"]);

        var findings = DisplayPowerPolicy.Sanitize(general);

        Assert.Equal("PG27AQDP", Assert.Single(general.SwitchOffWhenUnused).FriendlyName);
        Assert.Equal(3, findings.Count);
        Assert.Equal(Rig, Assert.Single(general.ParkedDisplays));   // Desk is not listed, "junk" is no path
    }

    [Fact]
    public void Sanitize_ToleratesMissingLists()
    {
        var general = new GeneralConfig { SwitchOffWhenUnused = null!, ParkedDisplays = null! };

        Assert.Empty(DisplayPowerPolicy.Sanitize(general));
        Assert.NotNull(general.SwitchOffWhenUnused);
        Assert.NotNull(general.ParkedDisplays);
    }

    [Fact]
    public void Describe_PrefersTheSavedName_AndFallsBackToTheMonitorId()
    {
        var config = new AppConfig();
        Assert.Equal("AUS2740", DisplayPowerPolicy.Describe(config, Rig));

        config.SimRig.Displays = TopologyFixture.Empty().WithRigMonitor();
        Assert.Equal("PG27AQDP", DisplayPowerPolicy.Describe(config, Rig));
    }

    [Fact]
    public void IsInCurrentLayout_FollowsTheActiveMode()
    {
        var config = ConfigWithRigListed();

        Assert.False(DisplayPowerPolicy.IsInCurrentLayout(config, Rig));
        Assert.True(DisplayPowerPolicy.IsInCurrentLayout(config, Desk));

        config.CurrentMode = WorkspaceMode.SimRig;
        Assert.True(DisplayPowerPolicy.IsInCurrentLayout(config, Rig));
    }

    // ===== the dropout watchdog ======================================================

    [Fact]
    public void TheLoopFromThe24th_IsReportedOnTheThirdDropout()
    {
        // Measured: the PG27AQDP left the bus every 7.6 s.
        var detector = new DisplayBounceDetector();

        Assert.Null(detector.RecordDropout(Rig, T0));
        Assert.Null(detector.RecordDropout(Rig, T0.AddSeconds(7.6)));
        var bounce = detector.RecordDropout(Rig, T0.AddSeconds(15.2));

        Assert.NotNull(bounce);
        Assert.Equal(3, bounce!.Dropouts);
        Assert.Equal(15.2, bounce.Span.TotalSeconds, 1);
        Assert.Equal(Rig, bounce.MonitorDevicePath);
    }

    [Fact]
    public void OrdinaryDropouts_AreNotReported()
    {
        // A monitor switched off, a cable pulled, a mode switch: minutes apart, not seconds.
        var detector = new DisplayBounceDetector();
        for (var i = 0; i < 10; i++)
            Assert.Null(detector.RecordDropout(Rig, T0.AddMinutes(i * 2)));
    }

    [Fact]
    public void ALoopThatKeepsRunning_IsReportedOncePerCooldown()
    {
        // 25 minutes of the 7.6 s loop: reported at the start and after each 10-minute cooldown.
        var detector = new DisplayBounceDetector();

        var alerts = Enumerable.Range(0, 200)
            .Select(i => detector.RecordDropout(Rig, T0.AddSeconds(i * 7.6)))
            .Count(alert => alert is not null);

        Assert.Equal(3, alerts);
    }

    [Fact]
    public void DifferentMonitors_AreCountedSeparately()
    {
        var detector = new DisplayBounceDetector();

        Assert.Null(detector.RecordDropout(Rig, T0));
        Assert.Null(detector.RecordDropout(Desk, T0.AddSeconds(1)));
        Assert.Null(detector.RecordDropout(Rig, T0.AddSeconds(7)));
        Assert.Null(detector.RecordDropout(Desk, T0.AddSeconds(8)));
    }

    [Fact]
    public void MonitorPaths_AreComparedWithoutCase()
    {
        // Device interface paths do not promise a case.
        var detector = new DisplayBounceDetector();

        detector.RecordDropout(Rig.ToUpperInvariant(), T0);
        detector.RecordDropout(Rig.ToLowerInvariant(), T0.AddSeconds(7));

        Assert.NotNull(detector.RecordDropout(Rig, T0.AddSeconds(14)));
    }

    // ===== parked monitors: where they are, and the windows on them ==================
    // 2026-09-26: after a switch to Desktop the parked PG27AQDP stayed lit, with the user's Claude
    // window still on it and out of the mouse's reach.

    private static DisplaySnapshot DeskWithRigAt(int x, int y) => TopologyFixture.Empty().WithDeskMonitor()
        .With("PG27AQDP", Rig, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 3, 4352, 2560, 1440, x, y);

    [Fact]
    public void AParkedMonitor_IsFoundWhereItIs_WhileItTouchesTheDesktopOnlyAtACorner()
    {
        var screen = Assert.Single(DisplayPowerPolicy.LocateParked(DeskWithRigAt(3840, 2160), [Rig]));
        Assert.Equal((3840, 2160, 2560, 1440), (screen.X, screen.Y, screen.Width, screen.Height));
        Assert.Equal("PG27AQDP", screen.FriendlyName);
    }

    [Fact]
    public void AParkedMonitorTheUserMovedNextToAnother_IsTheirsAgain()
        => Assert.Empty(DisplayPowerPolicy.LocateParked(DeskWithRigAt(3840, 0), [Rig]));

    [Fact]
    public void OnlyAMonitorPitlaneParked_AndStillInTheLayout_CountsAsParked()
    {
        Assert.Empty(DisplayPowerPolicy.LocateParked(DeskWithRigAt(3840, 2160), []));
        Assert.Empty(DisplayPowerPolicy.LocateParked(TopologyFixture.Empty().WithDeskMonitor(), [Rig]));
    }

    [Theory]
    // Top-left of the parked screen: top-left of the work area.
    [InlineData(3840, 2160, 1280, 720, 0, 0, 1280, 720)]
    // Centred stays centred.
    [InlineData(4480, 2520, 1280, 720, 1280, 696, 1280, 720)]
    // Bottom-right stays bottom-right, above the taskbar.
    [InlineData(5120, 2880, 1280, 720, 2560, 1392, 1280, 720)]
    // Larger than the work area: made to fit it.
    [InlineData(3800, 2100, 5000, 3000, 0, 0, 3840, 2112)]
    public void AWindowOffAParkedScreen_KeepsItsRelativePlace_AndLandsWhole(int x, int y, int width, int height, int toX, int toY, int toWidth, int toHeight)
    {
        var parked = new PixelRect(3840, 2160, 2560, 1440);
        var workArea = new PixelRect(0, 0, 3840, 2112);   // the PG27UCDM above a 48 px taskbar
        Assert.Equal(new PixelRect(toX, toY, toWidth, toHeight), ParkedWindowMover.Rehome(new(x, y, width, height), parked, workArea));
    }

    // ===== interop ===================================================================

    [Theory]
    [InlineData(typeof(DeviceNotificationNative.CM_NOTIFY_FILTER), 416)]
    [InlineData(typeof(WindowNative.WINDOWPLACEMENT), 44)]
    [InlineData(typeof(WindowNative.MONITORINFO), 40)]
    [InlineData(typeof(WindowNative.WINDOWPOS), 40)]
    [InlineData(typeof(WindowNative.MSG), 48)]
    public void NewInteropStructsMatchTheNativeLayout(Type type, int expected)
        => Assert.Equal(expected, Marshal.SizeOf(type));

    [Fact]
    public void TheWatcherListensForTheInterfaceClassThatMonitorPathsEndIn()
    {
        // The watchdog compares notification paths with the paths saved in layouts.
        var suffix = "{" + DeviceNotificationNative.GUID_DEVINTERFACE_MONITOR + "}";
        Assert.EndsWith(suffix, Rig, StringComparison.OrdinalIgnoreCase);
    }
}
