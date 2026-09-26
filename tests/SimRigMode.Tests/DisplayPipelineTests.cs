using SimRigMode.Models;
using SimRigMode.Services.Display;
using SimRigMode.Tests.Fakes;
using Xunit;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Tests;

/// <summary>
/// The display-pipeline regression suite. Everything here runs against FakeDisplayConfigGateway;
/// no graphics call is made.
/// </summary>
public sealed class DisplayPipelineTests
{
    private static DisplayApplyEngine Engine(FakeDisplayConfigGateway gateway)
        // No real delays: settle and verify-retry waits would otherwise make the suite slow
        // enough that someone deletes it.
        => new(gateway, (_, _) => Task.CompletedTask);

    private static DisplayApplyOptions Options(bool dryRun = false)
        => new(DryRun: dryRun, SettleMilliseconds: 0, IndirectSettleMilliseconds: 0,
               VerifyRetries: 2, VerifyRetryDelayMilliseconds: 0);

    // ===== THE incident regression ==================================================

    [Fact]
    public async Task SwitchingToSimRig_CarriesThePrompterThrough_InsteadOfDeactivatingIt()
    {
        // Live: the rig monitor plus the DisplayLink Prompter.
        // Profile: the rig monitor ONLY - exactly how the Sim Rig profile was captured.
        //
        // SDC_USE_SUPPLIED_DISPLAY_CONFIG has no "leave this alone" semantics, so simply omitting
        // the Prompter deactivates it. That is the incident. It must be carried through instead.
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);

        var applied = gateway.Submitted[gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_APPLY) != 0)];
        Assert.Contains(applied.Paths, p => p.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        Assert.Contains(applied.Paths, p => p.MonitorDevicePath == TopologyFixture.RigMonitorPath);
        Assert.Equal(2, applied.Paths.Count);
    }

    [Fact]
    public async Task CarriedThroughPrompter_KeepsItsExactGeometryAndTiming()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter(x: -1024);
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        await Engine(gateway).ApplyAsync(profile, Options());

        var applied = gateway.Submitted.First(s => s.Paths.Count == 2);
        var prompter = applied.Paths.Single(p => p.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        var mode = applied.Modes[(int)prompter.SourceModeInfoIdx];

        Assert.Equal(1024u, mode.Width);
        Assert.Equal(600u, mode.Height);
        Assert.Equal(-1024, mode.PositionX);
        Assert.Equal(256u, prompter.TargetId);
    }

    [Fact]
    public async Task AnOptedInIndirectDisplay_IsManagedRatherThanCarriedThrough()
    {
        // Opting in means "yes, switch this one too" - so omitting it from the profile really
        // does deactivate it. That is the user's explicit choice.
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter(optIn: true);
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var applied = gateway.Submitted.First(s => gateway.Submitted.IndexOf(s) == gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_APPLY) != 0));
        Assert.DoesNotContain(applied.Paths, p => p.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
    }

    [Fact]
    public async Task AProfileThatCapturedThePrompterAsAManagedDisplay_StillCarriesItThrough()
    {
        // The real Desktop profile from the incident holds the Prompter as an ordinary managed
        // path, because indirect displays were not understood when it was captured. Switching to
        // Desktop must NOT actively re-add it. Carry-through is driven by the LIVE classification,
        // so it does not depend on migrating old config.
        var live = TopologyFixture.Empty().WithDeskMonitor().WithPrompter(x: -1024);
        var profile = TopologyFixture.Empty().WithDeskMonitor().WithPrompter(x: -1024);
        // Simulate an old profile: captured before classification existed.
        foreach (var path in profile.Paths) { path.IsIndirect = false; path.AdapterDevicePath = ""; }

        var gateway = new FakeDisplayConfigGateway(live);
        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var applied = gateway.Submitted[gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_APPLY) != 0)];
        var prompter = applied.Paths.Single(p => p.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);

        // Present, and carrying its live state rather than the profile's stale copy.
        Assert.True(prompter.IsIndirect);
        Assert.Contains(outcome.Log, line => line.Contains("carried through"));
    }

    // ===== persistence ==============================================================

    [Fact]
    public async Task NoSubmissionEverCarriesSaveToDatabase()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        await Engine(gateway).ApplyAsync(profile, Options());

        Assert.NotEmpty(gateway.SubmittedFlags);
        Assert.All(gateway.SubmittedFlags, flags => Assert.Equal(0u, flags & N.SDC_SAVE_TO_DATABASE));
    }

    [Fact]
    public async Task ValidationHappensBeforeAnythingIsApplied()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        await Engine(gateway).ApplyAsync(profile, Options());

        var firstApply = gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_APPLY) != 0);
        var firstValidate = gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_VALIDATE) != 0);
        Assert.True(firstValidate >= 0 && firstValidate < firstApply);
        // Validate and apply are mutually exclusive, never combined on one call.
        Assert.All(gateway.SubmittedFlags, flags =>
            Assert.False((flags & N.SDC_VALIDATE) != 0 && (flags & N.SDC_APPLY) != 0));
    }

    // ===== stale identifiers ========================================================

    [Fact]
    public async Task AdapterLuidsReshuffledByAReboot_AreReResolvedRatherThanReplayed()
    {
        // The profile was captured on a previous boot, when the GPU held LUID 117063.
        // It now holds 999999. Replaying the saved LUID would target nothing.
        var profile = TopologyFixture.Empty().WithRigMonitor(adapter: 117063, sourceId: 3);
        var live = TopologyFixture.Empty().WithRigMonitor(adapter: 999999, sourceId: 7);
        var gateway = new FakeDisplayConfigGateway(live);

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Committed, outcome.Status);
        var applied = gateway.Submitted[gateway.SubmittedFlags.FindIndex(f => (f & N.SDC_APPLY) != 0)];
        var path = Assert.Single(applied.Paths);
        Assert.Equal(999999, path.SourceAdapter);
        Assert.Equal(7u, path.SourceId);
        // and the mode entries were rebound to match
        Assert.Equal(999999, applied.Modes[(int)path.SourceModeInfoIdx].Adapter);
        Assert.Equal(7u, applied.Modes[(int)path.SourceModeInfoIdx].Id);
    }

    [Fact]
    public async Task AMissingDisplay_IsRefused_AndNothingIsSubmittedForApply()
    {
        var profile = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor();
        var live = TopologyFixture.Empty().WithRigMonitor();   // desk monitor unplugged
        var gateway = new FakeDisplayConfigGateway(live);

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Refused, outcome.Status);
        Assert.False(gateway.AnythingWasApplied);
        Assert.Contains("not currently connected", string.Join(" ", outcome.Log));
    }

    // ===== rollback =================================================================

    [Fact]
    public async Task IfTheRollbackCannotBeValidated_NothingIsAttemptedAtAll()
    {
        // A rollback you have not proven you can execute is not a rollback.
        var live = TopologyFixture.Empty().WithRigMonitor();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live)
        {
            Reject = (_, flags) => (flags & N.SDC_VALIDATE) != 0 ? N.ERROR_GEN_FAILURE : 0
        };

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.Refused, outcome.Status);
        Assert.False(gateway.AnythingWasApplied);
    }

    [Fact]
    public async Task IfWindowsAppliesSomethingElse_VerificationFailsAndTheChangeIsRolledBack()
    {
        // Windows returns success but quietly drops the Prompter - the exact failure mode the
        // old "0 warnings" logging could not distinguish from success.
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live)
        {
            ApplyInstalls = TopologyFixture.Empty().WithRigMonitor()
        };

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.RolledBack, outcome.Status);
        Assert.Contains(outcome.Log, line => line.Contains("Rolling back"));
        // The last apply put the original topology back.
        Assert.Contains(gateway.Current.Paths, p => p.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
    }

    [Fact]
    public async Task IfTheApplyItselfFails_ThePreviousLayoutIsRestored()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var applyCount = 0;
        var gateway = new FakeDisplayConfigGateway(live)
        {
            // Fail the first apply (the forward one), allow the rollback.
            Reject = (_, flags) => (flags & N.SDC_APPLY) != 0 && applyCount++ == 0 ? N.ERROR_BAD_CONFIGURATION : 0
        };

        var outcome = await Engine(gateway).ApplyAsync(profile, Options());

        Assert.Equal(DisplayApplyStatus.RolledBack, outcome.Status);
        Assert.Equal(2, gateway.Current.Paths.Count);
    }

    // ===== dry run ==================================================================

    [Fact]
    public async Task DryRun_ValidatesButNeverApplies()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();
        var gateway = new FakeDisplayConfigGateway(live);

        var outcome = await Engine(gateway).ApplyAsync(profile, Options(dryRun: true));

        Assert.Equal(DisplayApplyStatus.DryRunValidated, outcome.Status);
        Assert.False(gateway.AnythingWasApplied);
        Assert.NotEmpty(gateway.ValidatedFlags);
    }

    // ===== builder invariants ========================================================

    [Fact]
    public void SubmittedModeIndicesAlwaysPointAtTheRightOwnerAndInfoType()
    {
        var live = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor();

        var result = DisplaySubmissionBuilder.Build(profile, live);
        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);

        foreach (var path in submission.Paths)
        {
            Assert.True(path.SourceModeInfoIdx < submission.Modes.Count);
            var source = submission.Modes[(int)path.SourceModeInfoIdx];
            Assert.Equal(1u, source.InfoType);
            Assert.Equal(path.SourceAdapter, source.Adapter);
            Assert.Equal(path.SourceId, source.Id);

            if (path.TargetModeInfoIdx == N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID) continue;
            var target = submission.Modes[(int)path.TargetModeInfoIdx];
            Assert.Equal(2u, target.InfoType);
            Assert.Equal(path.TargetAdapter, target.Adapter);
            Assert.Equal(path.TargetId, target.Id);
        }
    }

    [Fact]
    public void AnIndirectDisplayIsSubmittedWithoutReplayedTimings()
    {
        // DisplayLink timings are synthesized by the driver, not read from an EDID. Letting
        // Windows derive the mode from the refresh rate is the safer choice.
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();

        var result = DisplaySubmissionBuilder.Build(profile, live);
        var prompter = result.Submission!.Paths.Single(p => p.IsIndirect);
        var rig = result.Submission.Paths.Single(p => !p.IsIndirect);

        Assert.NotEqual(N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID, rig.TargetModeInfoIdx);
        Assert.NotEqual(N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID, prompter.SourceModeInfoIdx);
    }

    [Fact]
    public void AnEmptyProfileIsRefusedRatherThanSubmitted()
    {
        var result = DisplaySubmissionBuilder.Build(TopologyFixture.Empty(), TopologyFixture.Empty().WithRigMonitor());
        Assert.Null(result.Submission);
        Assert.NotEmpty(result.Issues);
    }

    // ===== verifier ==================================================================

    [Fact]
    public void Verifier_FailsWhenAnExpectedDisplayIsNotActive()
    {
        var expected = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var actual = TopologyFixture.Empty().WithRigMonitor();

        var report = DisplayVerifier.Verify(expected, actual);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Display == "Elgato Prom." && f.Field == "active");
    }

    [Fact]
    public void Verifier_FailsWhenAnUnexpectedDisplayIsActive()
    {
        var expected = TopologyFixture.Empty().WithRigMonitor();
        var actual = TopologyFixture.Empty().WithRigMonitor().WithDeskMonitor();

        Assert.False(DisplayVerifier.Verify(expected, actual).Passed);
    }

    [Fact]
    public void Verifier_FailsOnAResolutionMismatch()
    {
        var expected = TopologyFixture.Empty().WithRigMonitor();
        var actual = TopologyFixture.Empty().With("PG27AQDP", TopologyFixture.RigMonitorPath,
            TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 3, 4352, 1920, 1080);

        var report = DisplayVerifier.Verify(expected, actual);
        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Field == "resolution");
    }

    [Fact]
    public void Verifier_TreatsARefreshRateNudgeAsAWarningNotAFailure()
    {
        // Windows is allowed to land on a nearby supported rate under SDC_ALLOW_CHANGES.
        var expected = TopologyFixture.Empty().WithRigMonitor();
        var actual = TopologyFixture.Empty().With("PG27AQDP", TopologyFixture.RigMonitorPath,
            TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 3, 4352, 2560, 1440,
            outputTechnology: 5, refreshNumerator: 477000, refreshDenominator: 1000);

        var report = DisplayVerifier.Verify(expected, actual);
        Assert.True(report.Passed);
        Assert.Contains(report.Findings, f => f.Field == "refresh rate" && f.Severity == VerificationSeverity.Warning);

        // ...and a rate inside the tolerance is not even worth mentioning.
        var nudged = TopologyFixture.Empty().With("PG27AQDP", TopologyFixture.RigMonitorPath,
            TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 3, 4352, 2560, 1440,
            outputTechnology: 5, refreshNumerator: 479000, refreshDenominator: 1000);
        Assert.DoesNotContain(DisplayVerifier.Verify(expected, nudged).Findings, f => f.Field == "refresh rate");
    }

    [Fact]
    public void Verifier_IgnoresOriginShifts_AndReportsRelativeMovesWithoutFailing()
    {
        // Same relative arrangement, whole desktop rebased: that is Windows being Windows.
        var expected = TopologyFixture.Empty()
            .With("A", "pathA", TopologyFixture.GpuAdapterPath, 1, 0, 100, 1920, 1080, 0, 0)
            .With("B", "pathB", TopologyFixture.GpuAdapterPath, 1, 1, 101, 1920, 1080, 1920, 0);
        var shifted = TopologyFixture.Empty()
            .With("A", "pathA", TopologyFixture.GpuAdapterPath, 1, 0, 100, 1920, 1080, -1920, 0)
            .With("B", "pathB", TopologyFixture.GpuAdapterPath, 1, 1, 101, 1920, 1080, 0, 0);
        Assert.True(DisplayVerifier.Verify(expected, shifted).Passed);

        // Genuinely rearranged: B moved from the right of A to the left. Reported, but not a
        // failure: Windows moves displays itself, and a switch is not undone for a position.
        var moved = TopologyFixture.Empty()
            .With("A", "pathA", TopologyFixture.GpuAdapterPath, 1, 0, 100, 1920, 1080, 0, 0)
            .With("B", "pathB", TopologyFixture.GpuAdapterPath, 1, 1, 101, 1920, 1080, -1920, 0);
        var report = DisplayVerifier.Verify(expected, moved);
        Assert.True(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Field == "position" && finding.Severity == VerificationSeverity.Warning);
    }

    [Fact]
    public void ACarriedDisplay_ThatWouldOverlapTheNewLayout_IsMovedClearTheShortestWay()
    {
        // 2026-09-26: the Desktop layout, captured before the Prompter was plugged back in, puts
        // the 27G2WG3 at y 567; the Prompter, carried through at -1024,0, reaches down to y 600.
        const string sidePath = @"\\?\DISPLAY#AOC2702#5&1176aab&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var profile = TopologyFixture.Empty().WithDeskMonitor()
            .With("27G2WG3-", sidePath, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 2, 4353, 1920, 1080, -1920, 567);
        var live = TopologyFixture.Empty().WithDeskMonitor()
            .With("27G2WG3-", sidePath, TopologyFixture.GpuAdapterPath, TopologyFixture.GpuAdapter, 2, 4353, 1920, 1080, -1920, 600)
            .WithPrompter();

        var result = DisplaySubmissionBuilder.Build(profile, live);

        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        var prompter = submission.Paths.Single(path => path.FriendlyName == "Elgato Prom.");
        var mode = submission.Modes[(int)prompter.SourceModeInfoIdx];
        Assert.Equal((-1024, -33), (mode.PositionX, mode.PositionY));   // up 33, not across the desk
        Assert.Contains(result.Notes, note => note.StartsWith("Elgato Prom.: moved 0,-33", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIndirectDisplaySwitchedWithModes_IsLeftOutOfAModeThatDoesNotUseIt()
    {
        // 2026-09-26: the user wants the Prompter off in Sim Rig mode.
        var live = TopologyFixture.Empty().WithRigMonitor().WithPrompter();
        var profile = TopologyFixture.Empty().WithRigMonitor();

        var carried = DisplaySubmissionBuilder.Build(profile, live);
        var switched = DisplaySubmissionBuilder.Build(profile, live, switchedIndirect: [TopologyFixture.PrompterMonitorPath]);

        Assert.Contains(Assert.IsType<DisplaySnapshot>(carried.Submission).Paths, path => path.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        var submission = Assert.IsType<DisplaySnapshot>(switched.Submission);
        Assert.Equal(TopologyFixture.RigMonitorPath, Assert.Single(submission.Paths).MonitorDevicePath);
    }

    [Fact]
    public void AnIndirectDisplaySwitchedWithModes_IsSwitchedBackOnInAModeThatUsesIt()
    {
        var live = TopologyFixture.Empty().WithRigMonitor();                        // the Prompter is off
        var profile = TopologyFixture.Empty().WithRigMonitor().WithPrompter();      // this mode uses it
        var connected = new[] { live.Paths[0].ToConnected(), TopologyFixture.Empty().WithPrompter().Paths[0].ToConnected() };

        var carried = DisplaySubmissionBuilder.Build(profile, live, connected);
        var switched = DisplaySubmissionBuilder.Build(profile, live, connected, [TopologyFixture.PrompterMonitorPath]);

        // Not switched with modes: left off, as before.
        Assert.DoesNotContain(Assert.IsType<DisplaySnapshot>(carried.Submission).Paths, path => path.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        var submission = Assert.IsType<DisplaySnapshot>(switched.Submission);
        var prompter = Assert.Single(submission.Paths, path => path.MonitorDevicePath == TopologyFixture.PrompterMonitorPath);
        Assert.True(prompter.IndirectOptIn);
        Assert.Equal(2, submission.Paths.Count);
    }

    [Fact]
    public void AnIndirectDisplaySwitchedWithModes_ThatIsNotConnected_NeverBlocksASwitch()
    {
        var live = TopologyFixture.Empty().WithRigMonitor();
        var profile = TopologyFixture.Empty().WithRigMonitor().WithPrompter();   // the Prompter is unplugged

        var result = DisplaySubmissionBuilder.Build(profile, live, [live.Paths[0].ToConnected()], [TopologyFixture.PrompterMonitorPath]);

        Assert.Empty(result.Issues);
        Assert.Single(Assert.IsType<DisplaySnapshot>(result.Submission).Paths);
        Assert.Contains(result.Notes, note => note.Contains("not connected, so it stays off", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIndirectDisplayThatDidNotComeOnOrGoOff_IsReportedNotRolledBack()
    {
        // Expected on, still off: a slow DisplayLink display.
        var expected = DisplaySubmissionBuilder.Build(TopologyFixture.Empty().WithRigMonitor().WithPrompter(),
            TopologyFixture.Empty().WithRigMonitor().WithPrompter(), switchedIndirect: [TopologyFixture.PrompterMonitorPath]).Submission!;
        var slow = DisplayVerifier.Verify(expected, TopologyFixture.Empty().WithRigMonitor());
        Assert.True(slow.Passed);
        Assert.Contains(slow.Findings, finding => finding.Field == "active" && finding.Severity == VerificationSeverity.Warning);

        // Expected off, still on.
        var stuck = DisplayVerifier.Verify(TopologyFixture.Empty().WithRigMonitor(), TopologyFixture.Empty().WithRigMonitor().WithPrompter());
        Assert.True(stuck.Passed);

        // A monitor is still held to it: the rig monitor turning up unasked undoes the switch.
        Assert.False(DisplayVerifier.Verify(TopologyFixture.Empty().WithDeskMonitor(), TopologyFixture.Empty().WithDeskMonitor().WithRigMonitor()).Passed);
    }

    [Fact]
    public void ACarriedDisplay_ThatOverlapsNothing_StaysExactlyWhereItWas()
    {
        var profile = TopologyFixture.Empty().WithDeskMonitor();
        var live = TopologyFixture.Empty().WithDeskMonitor().WithPrompter();

        var result = DisplaySubmissionBuilder.Build(profile, live);

        var submission = Assert.IsType<DisplaySnapshot>(result.Submission);
        var prompter = submission.Paths.Single(path => path.FriendlyName == "Elgato Prom.");
        var mode = submission.Modes[(int)prompter.SourceModeInfoIdx];
        Assert.Equal((-1024, 0), (mode.PositionX, mode.PositionY));
        Assert.DoesNotContain(result.Notes, note => note.Contains("moved", StringComparison.Ordinal));
    }
}
