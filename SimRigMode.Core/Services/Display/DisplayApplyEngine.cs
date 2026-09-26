using SimRigMode.Models;
using N = SimRigMode.NativeInterop.DisplayConfigNative;

namespace SimRigMode.Services.Display;

/// <summary>
/// The seam over the two mutating DisplayConfig calls, so the whole pipeline below can be tested
/// without a graphics card.
/// </summary>
public interface IDisplayConfigGateway
{
    /// <summary>QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS), fully resolved.</summary>
    DisplaySnapshot CaptureActive();

    /// <summary>QueryDisplayConfig(QDC_ALL_PATHS): every monitor Windows can drive, including
    /// ones that are connected but switched out of the layout. Read-only.</summary>
    IReadOnlyList<ConnectedTarget> CaptureConnected();

    /// <summary>SetDisplayConfig. Returns the Win32 result; 0 is success.</summary>
    int Submit(DisplaySnapshot snapshot, uint sdcFlags);
}

public enum DisplayApplyStatus
{
    /// <summary>Applied and verified.</summary>
    Committed,

    /// <summary>Nothing was submitted. The previous topology is untouched.</summary>
    Refused,

    /// <summary>Dry run: validated only, nothing applied.</summary>
    DryRunValidated,

    /// <summary>Apply or verification failed and the previous topology was restored.</summary>
    RolledBack,

    /// <summary>Apply failed AND the previous topology could not be restored.</summary>
    RollbackFailed
}

public sealed record DisplayApplyOptions(
    bool DryRun = false,
    int SettleMilliseconds = 750,
    int IndirectSettleMilliseconds = 3000,
    int VerifyRetries = 5,
    int VerifyRetryDelayMilliseconds = 250,
    Action? Applied = null,
    IReadOnlyCollection<string>? SwitchedIndirect = null);

public sealed record DisplayApplyOutcome(DisplayApplyStatus Status, string Summary, IReadOnlyList<string> Log)
{
    public bool IsFailure => Status is DisplayApplyStatus.Refused or DisplayApplyStatus.RolledBack or DisplayApplyStatus.RollbackFailed;
}

/// <summary>
/// Applies a display profile safely.
///
///   capture rollback -> validate rollback -> validate forward -> apply -> settle -> verify
///                                                                          |
///                                                            failure -> roll back -> verify
///
/// Two deliberate choices, both direct responses to docs/report.md.
///
/// SDC_SAVE_TO_DATABASE is NEVER passed here. Every switch used to rewrite Windows' persistent
/// display database, which is what made a transient mode switch outlive the process and the
/// reboot. SimRigMode no longer persists anything there at all; DisplayService.Submit refuses it.
///
/// The ROLLBACK IS VALIDATED FIRST. A rollback that has not been proven executable is not a
/// rollback. If the topology currently on screen cannot even be expressed as a valid submission,
/// the transition is refused before anything is touched.
/// </summary>
public sealed class DisplayApplyEngine
{
    private const uint ValidateStrict = N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_VALIDATE;
    private const uint ValidateRelaxed = ValidateStrict | N.SDC_ALLOW_CHANGES;
    private const uint ApplyStrict = N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_APPLY;
    private const uint ApplyRelaxed = ApplyStrict | N.SDC_ALLOW_CHANGES;

    private readonly IDisplayConfigGateway _gateway;
    private readonly Func<int, CancellationToken, Task> _delay;

    public DisplayApplyEngine(IDisplayConfigGateway gateway, Func<int, CancellationToken, Task>? delay = null)
    {
        _gateway = gateway;
        _delay = delay ?? ((ms, token) => ms > 0 ? Task.Delay(ms, token) : Task.CompletedTask);
    }

    public async Task<DisplayApplyOutcome> ApplyAsync(DisplaySnapshot profile, DisplayApplyOptions options, CancellationToken token = default)
    {
        var log = new List<string>();

        // ---- 1. Capture what is on screen now, and prove we could put it back --------------
        DisplaySnapshot live;
        try { live = _gateway.CaptureActive(); }
        catch (Exception ex) { return Refuse(log, "The current display layout could not be read: " + ex.Message); }

        var rollback = DisplaySubmissionBuilder.Build(live, live);
        if (rollback.Submission is null)
            return Refuse(log, "The current display layout could not be expressed as a rollback point, so no change was attempted. "
                             + Describe(rollback.Issues));

        var rollbackValidation = _gateway.Submit(rollback.Submission, ValidateStrict);
        if (rollbackValidation != 0)
            return Refuse(log, $"Windows rejected a rollback to the current layout (Win32 {rollbackValidation}), "
                             + "so no change was attempted.");
        log.Add("Rollback point captured and validated.");

        // ---- 2. Build the forward submission -----------------------------------------------
        // Without the connected-but-switched-out monitors, a profile that needs to switch a
        // monitor ON is refused as "not connected". That made Sim Rig -> Desktop impossible.
        IReadOnlyList<ConnectedTarget>? connected = null;
        try { connected = _gateway.CaptureConnected(); }
        catch (Exception ex) { log.Add("Connected monitors could not be listed (" + ex.Message + "); only displays that are already on can be used."); }

        var forward = DisplaySubmissionBuilder.Build(profile, live, connected, options.SwitchedIndirect);
        log.AddRange(forward.Notes);
        if (forward.Submission is null)
            return Refuse(log, "This mode's display layout does not match the connected displays. " + Describe(forward.Issues));

        var carried = forward.Submission.Paths.Count(path => path.IsIndirect && !path.IndirectOptIn);
        if (carried > 0)
            log.Add($"{carried} indirect display(s) carried through unchanged rather than being switched.");

        // ---- 3. Validate, strict first --------------------------------------------------
        var applyFlags = ApplyStrict;
        var validation = _gateway.Submit(forward.Submission, ValidateStrict);
        if (validation != 0)
        {
            log.Add($"Exact layout not accepted (Win32 {validation}); retrying with adjustments permitted.");
            validation = _gateway.Submit(forward.Submission, ValidateRelaxed);
            if (validation != 0)
                return Refuse(log, $"Windows rejected this display layout (Win32 {validation}). Nothing was changed.");
            applyFlags = ApplyRelaxed;
        }
        log.Add(applyFlags == ApplyStrict ? "Layout validated exactly." : "Layout validated with adjustments permitted.");

        if (options.DryRun)
            return new(DisplayApplyStatus.DryRunValidated,
                "Dry run: the layout is valid and would be applied. Nothing was changed.", log);

        // ---- 4. Apply. Note the absence of SDC_SAVE_TO_DATABASE. ---------------------------
        var applied = _gateway.Submit(forward.Submission, applyFlags);
        if (applied != 0)
        {
            log.Add($"Apply failed (Win32 {applied}).");
            return await RollBackAsync(rollback, options, log, $"Windows rejected the layout (Win32 {applied}).", token);
        }
        // The layout is in. Whatever waits on that, a parked monitor's cover, starts now rather
        // than after the settle and the checks. It is never the engine's problem.
        try { options.Applied?.Invoke(); } catch { /* the caller reports its own failures */ }

        // ---- 5. Settle, then verify --------------------------------------------------------
        var settle = forward.Submission.Paths.Any(path => path.IsIndirect)
            ? Math.Max(options.SettleMilliseconds, options.IndirectSettleMilliseconds)
            : options.SettleMilliseconds;
        await _delay(settle, token);

        var report = await VerifyWithRetriesAsync(forward.Submission, options, token);
        log.AddRange(report.Findings.Select(finding => finding.ToString()));

        if (report.Passed)
        {
            log.Add("Resulting layout verified.");
            return new(DisplayApplyStatus.Committed, "Display layout applied and verified.", log);
        }

        var failures = string.Join("; ", report.Failures.Select(f => $"{f.Display} {f.Field}"));
        return await RollBackAsync(rollback, options, log,
            "The layout Windows applied did not match what was requested (" + failures + ").", token);
    }

    private async Task<VerificationReport> VerifyWithRetriesAsync(DisplaySnapshot expected, DisplayApplyOptions options, CancellationToken token)
    {
        VerificationReport report = new([]);
        for (var attempt = 1; attempt <= Math.Max(1, options.VerifyRetries); attempt++)
        {
            // Retry the READ, never the apply. Windows can report a transitional topology for a
            // short window after SetDisplayConfig returns.
            try { report = DisplayVerifier.Verify(expected, _gateway.CaptureActive()); }
            catch (Exception ex)
            {
                report = new([new(VerificationSeverity.Failure, "(topology)", "read", "readable", ex.Message)]);
            }
            if (report.Passed) return report;
            if (attempt < options.VerifyRetries) await _delay(options.VerifyRetryDelayMilliseconds, token);
        }
        return report;
    }

    private async Task<DisplayApplyOutcome> RollBackAsync(SubmissionResult rollback, DisplayApplyOptions options,
        List<string> log, string cause, CancellationToken token)
    {
        log.Add("Rolling back to the previous layout.");
        var result = _gateway.Submit(rollback.Submission!, ApplyRelaxed);
        if (result != 0)
            return new(DisplayApplyStatus.RollbackFailed,
                cause + " The previous layout could not be restored either (Win32 " + result +
                "). Use Windows Settings > System > Display to recover.", log);

        await _delay(options.SettleMilliseconds, token);

        // Verify the rollback too, but do NOT loop: a failed rollback is reported, not retried.
        var report = DisplayVerifier.Verify(rollback.Submission!, Safe(_gateway.CaptureActive));
        if (!report.Passed)
            return new(DisplayApplyStatus.RollbackFailed,
                cause + " A rollback was attempted but could not be confirmed. Use Windows Settings > System > Display to recover.",
                log.Concat(report.Findings.Select(f => f.ToString())).ToList());

        log.Add("Previous layout restored.");
        return new(DisplayApplyStatus.RolledBack, cause + " The previous layout was restored.", log);
    }

    private static DisplaySnapshot Safe(Func<DisplaySnapshot> read)
    {
        try { return read(); } catch { return new DisplaySnapshot(); }
    }

    private static DisplayApplyOutcome Refuse(List<string> log, string summary)
    {
        log.Add("Refused: " + summary);
        return new(DisplayApplyStatus.Refused, summary, log);
    }

    private static string Describe(IReadOnlyList<SubmissionIssue> issues) =>
        string.Join(" ", issues.Select(issue => $"[{issue.DisplayName}] {issue.Reason}"));
}
