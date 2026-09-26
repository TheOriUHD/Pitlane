# SimRigMode Incident Forensic Report

## 0. Correction, 2026-09-24: the cause was the PG27AQDP, not the Prompter

**The conclusion of the original report below is wrong. Read this section first.**

The stutter of 2026-09-13 recurred on 2026-09-24 with the Elgato Prompter unplugged. It was then
measured directly, and the cause is the ASUS PG27AQDP (the Sim Rig monitor, `DISPLAY\AUS2740`,
HDMI on the RTX 5090):

- **The loop.** Left connected but detached from the desktop, the PG27AQDP drops off the bus
  about 6.6 s after it is detached and comes back about 1 s later. It repeats every **7.6 s** for
  as long as it is left like that, whether the monitor is on, in standby, or switched off. Switched
  off over DDC/CI with its LED dark, it looped just the same. Attached to the desktop, it never
  drops out, even switched off: after parking it at 16:22 there were 0 dropouts and 0 stalls.
  Windows logs each dropout as Kernel-PnP Device Management event 1010, "surprise removed as it
  is reported as missing on the bus".
- **The symptom.** Every return stalls Windows' window manager and input delivery. Measured: posted
  messages arriving 40-75 ms late, window-manager calls blocked for 45 ms, and the compositor stalled
  for up to 88 ms, all within 150 ms of a reconnect, and no stalls at any other time. Apps that
  react to monitor changes pause as well. The user saw typing freeze and then appear all at
  once, with any keyboard. The active screens never flickered, because the monitor that was
  reconnecting was the dark one, and audio was unaffected.
- **The trigger.** SimRigMode's Desktop mode put the PG27AQDP in exactly that state. The build
  in use (published 2026-09-20 20:39, before Phase 3c) also passed `SDC_SAVE_TO_DATABASE`,
  so Windows re-applied "PG27AQDP dark" by itself on every reconnect, even with SimRigMode closed
  and after reboots.
- **The timeline.** The PG27AQDP was first connected on 2026-09-12 at 00:05. Every loop began 5 to
  18 s after a switch to Desktop, or after a monitor-set change that made Windows re-apply a saved
  layout. Loops ended when Sim Rig mode made the monitor active again. On 2026-09-12 at 23:23:12
  the loop stopped at the exact second of the Sim Rig switch.

| Loop | Dropouts | Started after |
|---|---|---|
| 2026-09-12 23:22-23:23 | 10 | Desktop switch 23:21:56 |
| 2026-09-13 00:03-01:29 | 629 | Desktop switch 00:02:57 |
| 2026-09-20 21:48-22:05 | 128 | Prompter re-plugs; SimRigMode not running |
| 2026-09-24 15:03-(ongoing) | 205+ | Desktop switch 15:03:19 |

**Why the Prompter looked guilty.** Windows stores one saved layout per *set of connected
monitors*. Plugging or unplugging the Prompter changes the set, so Windows applies a different
saved layout. With the Prompter connected, the layout the old build saved kept the PG27AQDP
dark, so the loop ran. Without the Prompter, a different layout applied and the loop stopped. That
is the "connected equals stutter, disconnected equals smooth" A/B result below. It was real, but
it pointed at the wrong device. The Prompter's DisplayLink stack was never shown to be faulty.

**What was changed** (see `docs/SAFETY.md`, invariants 2, 11-13 and 15):

1. **SimRigMode never writes Windows' display database.** Not in a switch, not from a button,
   not at all. `DisplayService.Submit` refuses `SDC_SAVE_TO_DATABASE`, and
   `WindowsOwnedStateTests` fails if anything else in the product names it. Only Windows itself
   (Settings > System > Display) saves layouts now.
2. **The layout changes only in a switch the user starts.** The watchdog reports a looping
   monitor and says how to fix it in Windows' own settings; it no longer attaches it. It only
   puts the monitor on the park list, so SimRigMode's own next switch does not detach it again.
3. **SimRigMode sends monitors no commands.** Parking used to add a DDC/CI power-off; that is
   removed. A monitor's power is switched with its own button.
4. A monitor on the park list is never detached. When a mode leaves it out, it stays attached
   in a corner where it touches the desktop at a single point.
5. The display pipeline can switch a connected-but-dark monitor ON. It only resolved profiles
   against active displays, so Sim Rig -> Desktop was always refused as "not connected". This was
   never noticed because the build in use predated that pipeline.

On 2026-09-24 the layout on screen was saved as Windows' default three times, for the monitor
sets with and without the Prompter, the last at 19:07, from a development harness running
SimRigMode's code. That replaced the "PG27AQDP detached" layouts the old build had saved. What is
saved now is the layout Windows' own Settings would save, with the PG27AQDP attached in the
corner. The feature that did this has been removed.

**A second, separate loop the same evening.** After an unclean power-off at 18:41, a different
device looped: the RØDECaster Duo's built-in SD card storage (`USB\VID_05E3&PID_0764`),
171 dropouts between 18:56 and 19:03, one every 4.6 s, with the same stutter. Ejecting only
that storage function at 19:06 stopped it. SimRigMode has no USB rules and never touched it.
The same device has single dropouts logged on 8 days back to 2026-05-26, months before
SimRigMode existed.

## 1. Executive Summary

> Superseded by section 0. Kept unchanged as the record of the original investigation.

The current stutter is **CONFIRMED at the symptom level to be triggered by the Elgato Prompter/DisplayLink USB path**: connected causes the stutter, unplugged removes it, and reconnecting immediately restores it. The repository contains a credible mechanism by which SimRigMode could have triggered or exposed a DisplayLink defect: each switch to Sim Rig mode calls `SetDisplayConfig` with a supplied topology that contains only the ASUS PG27AQDP and omits the DisplayLink-backed Prompter path; each switch back to Desktop calls it again with the raw captured Prompter adapter/source/target path. Both calls use `SDC_SAVE_TO_DATABASE`, so Windows is explicitly asked to persist the resulting topology.

The evidence does **not** prove that SimRigMode created the underlying DisplayLink fault. In particular, Windows recorded `WUDFRd` load warnings for both Prompter USB functions at 20:03 on 2026-09-12, before the project files were created (about 21:54) and before the first SimRigMode start recorded at 22:05. Those warnings were paired with DriverFrameworks event 10118 saying that the UMDF reflector could not yet reach SCM during boot and would retry. They therefore show a pre-existing/boot-time DisplayLink condition, not an app-induced failure by themselves.

Direct USB/PnP disruption by the mode transitions is **UNSUPPORTED for the last confirmed transition and unlikely for the logged transition series**. The configuration written by the last Desktop transition contains `UsbRules: []`; the transition saves the in-memory configuration only after executing its steps, so the last 00:02 transition used no USB rules. The same file also has audio selections empty, Home Assistant disabled, and hotkeys disabled. The last transition therefore consisted materially of one persistent `SetDisplayConfig` call plus a configuration save.

SimRigMode does not install, modify, or remove DisplayLink drivers. Removal of `oem4.inf`/`dlidusb.inf` and physical disconnection of the Prompter were later manual troubleshooting actions. Read-only inspection confirms the backup directory exists and `C:\Windows\INF\oem4.inf` is currently absent.

A separate later incident occurred during development testing at approximately 00:52: the smoke-test executable used the production same-EXE elevation service, causing the test host to relaunch itself recursively and create many UAC prompts/processes. Static control-flow analysis shows these test children did not run the production elevated argument handler and did not reach a real PnP operation; they repeatedly enumerated USB/hub data and waited on another elevated child. This explains the acute UAC storm, load, RGB pauses, and USB-input lag observed at that time, but not the earlier reboot-surviving Prompter-specific stutter. At the final read-only check there were zero `SimRigMode` processes, no Run entry, no SimRigMode service, and no SimRigMode scheduled task.

Overall conclusion: **SimRigMode is a plausible trigger for the DisplayLink malfunction through aggressive persistent topology replay, but the evidence does not establish it as the origin of the DisplayLink defect.** The direct failing subsystem is strongly supported to be Prompter/DisplayLink; direct SimRigMode USB disablement, hotkey hooks, and unrelated RGB/input software are not supported as explanations for the persistent failure.

## 2. Known Timeline

- **2026-09-12 20:03:31** — Windows System log records Kernel-PnP event 219 twice: `\Driver\WUDFRd failed to load` for `USB\VID_17E9&PID_FF1A&MI_00` and `MI_01`, status `0xC0000365`. Each is accompanied by DriverFrameworks-UserMode event 10118 stating that UMDF could not connect to SCM during boot and would retry. This predates SimRigMode and is not proof of an app-caused fault.
- **21:07-21:08** — Kernel-PnP event 225 records various applications preventing removal/ejection of MOZA, Corsair, Logitech, RØDE, and other USB interfaces. These events also predate SimRigMode. They show an already complex USB/user-mode device environment but do not implicate the Prompter or SimRigMode.
- **About 21:54-21:57** — Earliest repository source creation times for models/services. `DisplayService.cs` was last modified at 23:12, before the delivered executable was produced.
- **22:05:04** — First SimRigMode start in the application log.
- **22:37 onward** — WinUI migration starts appear. One initial XAML icon creation failure is recorded; subsequent WinUI starts succeed.
- **23:19:15** — Current `dist\SimRigMode.exe` timestamp. SHA-256: `5049798F71B63F88F382C2E90F6370EF5B59166D11C6C7318EB3BC1CFA5434DB`. This binary predates the logged mode transitions and the later USB topology source files.
- **23:21:30 local-equivalent capture data** — Desktop profile contains four active paths: Elgato Prompter (`\\.\DISPLAY10`), ASUS PG27UCDM, AOC Q27G4, and AOC 27G2WG3. The Prompter is a distinct adapter LUID/path (`SourceAdapter`/`TargetAdapter` 1582295; source 0; target 256).
- **23:21:32 local-equivalent capture data** — Sim Rig profile contains only ASUS PG27AQDP at 2560×1440 and approximately 479.989 Hz.
- **23:21:42.803–23:21:43.656** — Sim Rig transition; log says zero warnings. This removes all Desktop paths, including the Prompter, from the supplied active topology and saves the result to the Windows display database.
- **23:21:54.268–23:21:56.935** — Desktop transition; zero warnings. This resupplies the DisplayLink path and three GPU display paths and saves them to the display database.
- **23:23:12.948–23:23:14.597** — Second Sim Rig transition; zero warnings.
- **2026-09-13 00:02:55.902–00:02:57.089** — Desktop transition; zero warnings. The resulting config was created/written at 00:02:56/57 and contains no USB rules, no audio changes, disabled integration, and disabled hotkeys.
- **00:29 onward** — USB topology/tree source is created and subsequently edited. This postdates all four logged mode transitions.
- **00:44:54** — Later WinUI start.
- **00:45:04** — UI-only USB tree error: `WinRT.IInspectable` could not be cast to `SimRigMode.UI.UsbTopologyRow`. This does not itself call a display or PnP setter.
- **00:46 and 00:48** — Further WinUI starts.
- **About 00:52** — Separate smoke-test elevation recursion begins during development testing. It creates repeated UAC prompts and many test processes. It is distinct from the earlier Prompter-specific/reboot-surviving condition.
- **00:57:52** — After a later boot, the same two Prompter Kernel-PnP 219 events and paired UMDF 10118 boot messages recur.
- **Subsequent manual troubleshooting** — Prompter unplugged; DisplayLink driver backed up to `C:\Users\<user>\Desktop\DisplayLink-driver-backup`; the user manually ran `pnputil /delete-driver oem4.inf /uninstall /force`; Windows reported successful uninstall and deletion. No clean replacement driver was installed by the time of this report.
- **Current read-only checks** — Prompter backup exists; `C:\Windows\INF\oem4.inf` is absent; SimRigMode process count is zero; no HKCU Run value, service, or scheduled task named SimRigMode exists.

There is no Git repository in this directory. `git status` and `git log` report that the directory is not a repository, so commit-level attribution or recovery of prior source revisions is impossible. File timestamps and the delivered binary timestamp are the only available change chronology.

## 3. What SimRigMode Actually Does

### 3.1 Display switching

`DisplayService.CaptureCurrent` calls `GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS)` and `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`. It serializes every active path and the entire returned mode array, including raw adapter LUIDs, source IDs, target IDs, mode indices, timing rationals, positions, rotations, scaling values, output technology, target availability, and path flags. It additionally calls `DisplayConfigGetDeviceInfo` for a friendly target name/device path and source GDI name.

`DisplayService.Apply` reconstructs native path and mode arrays directly from the saved JSON and calls:

`SetDisplayConfig(pathCount, paths, modeCount, modes, SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES)`.

Consequences:

- Only supplied paths are requested as active. A display omitted from a profile is removed from that active topology.
- `SDC_APPLY` applies immediately.
- `SDC_SAVE_TO_DATABASE` asks Windows to save the resulting configuration in its persistent display database. The active topology can therefore survive app exit and reboot.
- `SDC_ALLOW_CHANGES` lets Windows alter supplied source/target mode details as necessary to find a workable configuration.
- The code does not use `SDC_VALIDATE` first, does not query the resulting state afterward, and does not wait for display-driver stabilization.
- It does not use `SDC_TOPOLOGY_*`, `SDC_VIRTUAL_MODE_AWARE`, or `SDC_VIRTUAL_REFRESH_RATE_AWARE`.
- It stores persistent monitor device paths, but `Apply` does not use those paths to rebind a profile to current adapter/source/target identifiers. The device path is used only in UI-side matching/manual editing. Replay relies on saved raw LUIDs and IDs, which can change after driver restart, re-enumeration, GPU driver update, or DisplayLink stack reset.

There is no DisplayLink-specific branch. A DisplayLink/indirect/USB display is treated exactly like a GPU-attached output. The current Desktop config proves that the generic path included the Prompter as `\\.\DISPLAY10`; the Sim Rig config proves that path was omitted.

### 3.2 USB/PnP switching

The PnP mutation primitive is `UsbDeviceService.PerformLocalOperation`. It calls `CM_Locate_DevNode(instanceId, flags: 0)` and then either `CM_Enable_DevNode(devInst, 0)` or `CM_Disable_DevNode(devInst, 0)`.

The code does not pass a persistent-disable flag such as `CM_DISABLE_PERSIST`; it does not write device registry properties and does not install/remove a driver. A successful disable can remain effective for the current Windows session, but persistence across reboot is not explicitly requested. Persisted application rules could cause the app to reapply a state on later launches, but no startup entry currently exists.

The current topology policy layer can select a hub subtree, resolve descendants, and operate physical leaf device instance IDs. If `DisableHubItself` is explicitly selected for an external hub, it can instead submit the hub instance ID, which disconnects the whole downstream branch. Host controllers and root hubs are marked critical/protected in the normal UI/policy resolver. However, the low-level elevated command handler itself does not independently enforce a safety allowlist; it trusts its encoded command-line operation list.

The topology page also uses SetupAPI, Configuration Manager parent relationships, hub device interfaces, and USB hub IOCTLs to enumerate names, connections, speeds, driver keys, and descriptors. Those IOCTLs are query operations, not enable/disable operations. This tree code was created after the four logged transitions and therefore was not the mechanism used by them.

With `UsbRules: []`, the current transition implementation returns before topology resolution. The older delivered binary also had a zero-operation guard in `ApplyBatchElevatedAsync`; an empty rule list could not call `CM_Enable_DevNode` or `CM_Disable_DevNode`. The config saved by the 00:02 Desktop transition confirms that transition had no USB rule to apply. Earlier historical rules cannot be absolutely excluded because there is no config version history, but there is no log or file evidence that such rules existed.

### 3.3 Elevation

USB changes are the only normal mode operation deliberately elevated. The tray process runs `asInvoker`. For a nonempty batch, `ApplyBatchElevatedAsync` starts `Environment.ProcessPath` with Shell execution and verb `runas`, passing `--elevated-device-batch` plus URL-safe Base64 JSON. The elevated instance decodes the operations in `App.TryRunElevatedCommand`, applies them sequentially, exits on the first nonzero Configuration Manager result, and otherwise exits zero.

No service, scheduled task, installer helper, or resident elevated process is created. The helper is intended to exit after the batch. A successful call changes machine PnP devnode state, not merely application state. A partial batch is possible: devices changed before the first failure are not rolled back.

The later smoke-test incident is a separate design flaw. `UsbDeviceService` uses `Environment.ProcessPath`, which in the smoke-test host is `SimRigMode.SmokeTests.exe`. That test program does not parse `--elevated-device-batch`; it begins the tests again, reaches the same elevation path, spawns another elevated copy, and waits. This produces a chain of processes/UAC prompts. Because the test entry point ignores the helper arguments, static flow shows that these children do not call `App.TryRunElevatedCommand` and therefore do not execute `PerformLocalOperation`; they do repeatedly enumerate DisplayConfig, Core Audio, SetupAPI, and USB hub descriptors before reaching the next elevation attempt. This can readily cause acute CPU/process pressure and USB/RGB/input pauses while it is happening. It is not a persistent service and was no longer running at the current check.

### 3.4 Audio

Audio enumeration uses Core Audio `IMMDeviceEnumerator`. Switching uses the undocumented `IPolicyConfig.SetDefaultEndpoint` interface for Console, Multimedia, and Communications roles. Empty endpoint IDs are skipped. The incident config has all eight mode endpoint fields empty, so the logged transitions did not alter default audio devices.

### 3.5 Hotkeys/input

The app uses `RegisterHotKey` on a message-only window with `MOD_NOREPEAT`. It does not install `WH_KEYBOARD_LL`, any other Windows hook, raw-input registration, DLL injection, key simulation, or an input filter. When hotkeys are disabled, no hotkey is registered, although the message-loop object is still constructed. The current config has `HotkeysEnabled: false`, and no SimRigMode process is running now.

Accordingly, a SimRigMode input hook causing current per-keystroke stalls is **RULED OUT by the implementation and current state**. The smoother elevated PowerShell observation may reflect differences in window/compositor scheduling or how the stall was perceived, but SimRigMode has no integrity-level-specific keyboard interception mechanism that explains it.

### 3.6 Persistence/startup

- `%LOCALAPPDATA%\SimRigMode\config.json`: application mode/profile/rule state. Saves use a temporary file followed by replace/move.
- `%LOCALAPPDATA%\SimRigMode\Logs`: asynchronous text logs.
- Windows display database: changed by every successful display apply because of `SDC_SAVE_TO_DATABASE`.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SimRigMode`: created only if Start with Windows is enabled; currently absent.
- No service or scheduled task is implemented by the repository; none is currently present.
- No startup-folder mechanism is implemented.
- No driver installation/removal is implemented.
- USB state uses nonpersistent Configuration Manager flags, although an active app with persisted rules can reapply changes.
- Audio defaults, when configured, are global per-user Windows endpoint defaults and remain after application exit.
- Home Assistant can perform external HTTP switch actions when enabled; it was disabled in the incident config.

## 4. Exact Actions During Mode Transitions

### Desktop -> SimRig

`App.SwitchModeAsync` calls `ModeSwitchService.SwitchAsync(SimRig)` under a nonblocking `SemaphoreSlim` guard.

Order in `ModeSwitchService.SwitchAsync`:

1. Log `Starting transition to SimRig`.
2. Call `HomeAssistantService.ExecuteAsync(..., turnOn: true)`. In the incident config, `Integration.Enabled` is false, so this returns immediately and no five-second power wait occurs.
3. Resolve/apply USB `Enabled` rules. With an empty rule list, no PnP operation or UAC helper occurs.
4. Wait `UsbEnumerationWaitSeconds` only if at least one Sim Rig USB-enable rule exists. With no rules, there is no three-second wait.
5. Call `DisplayService.Apply(SimRig.Displays)`. The supplied array contains only PG27AQDP (adapter 117063, source 3, target 4352, approximately 479.989 Hz). `SetDisplayConfig` immediately applies and saves that supplied topology. DISPLAY10/Prompter and all desk paths are absent and therefore inactive.
6. Call `AudioService.Apply`; all IDs are empty, so it returns immediately.
7. Resolve/apply USB `Disabled` rules; none exist.
8. Set `CurrentMode = SimRig`, save config, and log the warning count.

### SimRig -> Desktop

1. Log `Starting transition to Desktop`.
2. Call `DisplayService.Apply(Desktop.Displays)` first. The supplied array contains the Prompter and three desk outputs. Windows is asked to recreate and persist the DisplayLink path using saved adapter/source/target/mode identifiers.
3. Call audio apply; no-op in this config.
4. Apply Desktop USB `Enabled` rules; none.
5. Apply Desktop USB `Disabled` rules; none.
6. Apply the hardware-power-off delay only if integration is enabled; it was disabled.
7. Call Home Assistant OFF; no-op.
8. Set `CurrentMode = Desktop`, save config, and log the warning count.

The observed 0.85–2.67 second transition durations are consistent with almost all steps being no-ops except `SetDisplayConfig`. They do not include a DisplayLink-specific settle/re-enumeration wait.

## 5. Elgato Prompter / DisplayLink Analysis

The Desktop profile identifies the Prompter unambiguously:

- Friendly name: `Elgato Prom.`
- Monitor device path: `\\?\DISPLAY#IDI3311#...`
- Source name at capture: `\\.\DISPLAY10`
- Distinct adapter LUID: 1582295
- Source ID 0; target ID 256
- 1024×600 at 60 Hz

The Sim Rig profile contains no path on this adapter. Therefore, on every successful Desktop-to-Sim transition, SimRigMode asked Windows to remove the Prompter from the active desktop; on every reverse transition it asked Windows to restore it.

The app does **not** directly address `DISPLAY10`, a DisplayLink service, `WUDFRd`, the USB functions, `oem4.inf`, or `dlidusb.inf`. `DISPLAY10` and the monitor path are stored as metadata; replay uses the raw adapter/source/target IDs. It does not physically disable USB, unload a driver, restart a service, or remove the driver package merely to deactivate the display path.

Nevertheless, a DisplayLink display is implemented through an indirect/USB display stack. Deactivation and reactivation of its Windows display path necessarily causes the DisplayLink/indirect display components to process topology, mode, surface, and device-lifecycle changes. It is therefore **PLAUSIBLE** that repeated persistent topology replay triggered a bug or exposed an already unhealthy DisplayLink/UMDF state. The absence of post-apply verification means Windows returning success could precede an asynchronous driver failure or retry loop.

Important counterevidence:

- Identical Prompter `WUDFRd` event 219 warnings existed at 20:03, before SimRigMode existed or ran.
- Those event 219 warnings are paired with event 10118 saying SCM is unavailable during boot and retry is expected; they may be benign boot-order noise rather than evidence of the stutter.
- No matching DisplayLink, Kernel-PnP, WUDF, or Application event was found in the exact 22:00–00:10 transition window.
- Only four successful switches are logged, and they were not a rapid subsecond toggle loop.
- The app used a supported Windows topology API and Windows returned success for each call.

The strongest current evidence is not the event log but the physical A/B test: connected equals stutter, disconnected equals smooth, reconnect equals immediate stutter. That identifies the active failing subsystem but does not distinguish driver corruption/bug, firmware/hardware failure, or an app-triggered latent fault.

## 6. USB/PnP Analysis

For the last 00:02 Desktop transition, direct Prompter PnP mutation is **RULED OUT to a high degree**:

- `UsbRules` in the configuration saved by that same transition is empty.
- USB setters are reached only through resolved rules/nonempty batches.
- There is no hard-coded VID 17E9/PID FF1A, Elgato, DisplayLink, or DISPLAY10-specific USB operation anywhere in the source.
- The current topology query layer did not yet exist when the logged switches occurred.

For the earlier three logged transitions, direct PnP action is **UNSUPPORTED but not mathematically impossible** because there is no historic copy of config before 00:02 and the logs did not record rule counts or targets in the delivered version. There is no affirmative evidence of an earlier Prompter rule, UAC batch, PnP result, or USB warning. The short transition timings and the later empty configuration support, but do not prove, that rules were empty throughout.

The low-level capability is broad: if a rule names a device, the elevated process can call `CM_Disable_DevNode`; if `DisableHubItself` targets an external hub in the newer code, a whole branch can be disconnected. A parent hub/controller could therefore be affected if it were deliberately or incorrectly selected. Normal resolver/UI logic protects host controllers/root hubs and skips critical leaf nodes, but the elevated handler itself has no independent validation. Nothing in the evidence connects such an operation to the Prompter during the four transitions.

No persistent PnP-disable flag is used. A direct devnode change, if one had occurred, would be a system-wide state change for that boot, but a reboot should normally remove a nonpersistent disable request unless Windows/driver state or an app rule reapplies it. The fact that the stutter survived reboot with no startup entry/process further weakens direct SimRigMode PnP disablement as the ongoing cause.

## 7. DisplayConfig Analysis

| Item | Implementation | Forensic significance |
|---|---|---|
| Capture query | `QDC_ONLY_ACTIVE_PATHS` | Saves only currently active paths; inactive/missing paths are absent rather than represented as disabled entries. |
| Apply source | Raw serialized path/mode arrays | Replays adapter LUIDs, IDs, indices, timings, and flags captured earlier. |
| `SDC_USE_SUPPLIED_DISPLAY_CONFIG` | Present | Windows uses the submitted paths/modes rather than a simple extend/clone preset. Omitted paths become inactive. |
| `SDC_APPLY` | Present | Applies immediately. |
| `SDC_SAVE_TO_DATABASE` | Present | Persists the topology in Windows' display configuration database across process exit and potentially reboot. |
| `SDC_ALLOW_CHANGES` | Present | Allows Windows to adjust supplied mode data to find a compatible result. It can mask that the exact snapshot was not replayed. |
| `SDC_VALIDATE` | Absent | No validation-only pass before mutation. |
| `SDC_TOPOLOGY_*` | Absent | No generic internal/clone/extend/external topology selection. |
| `SDC_VIRTUAL_MODE_AWARE` | Absent | Virtual-mode-aware clone-group/source-mode index semantics are not requested. |
| `SDC_VIRTUAL_REFRESH_RATE_AWARE` | Absent | Newer virtual refresh behavior is not requested. |
| Result check | `SetDisplayConfig` return value checked | A nonzero immediate Win32 return becomes a warning. A zero proves only synchronous API acceptance. |
| Postcondition check | None | No `QueryDisplayConfig` after apply, no Prompter/UMDF health test, and no driver stabilization wait. |
| Rebinding | None during apply | Persistent monitor path is saved but not used to map stale IDs to current targets. |
| Rollback | None | A partially workable or driver-destabilizing accepted topology remains active/persisted. |

The most consequential design choice is the combination of raw topology replay and `SDC_SAVE_TO_DATABASE`. It is more invasive than temporarily toggling a UI preference: it deliberately rewrites the persisted Windows display topology on every switch.

## 8. Logging Gaps / Silent Failure Possibilities

`Transition completed with 0 warning(s)` proves only that each wrapped step returned without throwing at that time. In this incident, it strongly supports that `SetDisplayConfig` returned zero. It does not prove that DisplayLink remained healthy after the call, that the actual resulting topology exactly matched the snapshot, or that no asynchronous UMDF failure began later.

Specific gaps:

- Transition logs do not contain path names, adapter/source/target IDs, flags, return durations, pre/post topology, or the actual `SetDisplayConfig` return code on success.
- There is no post-apply `QueryDisplayConfig` or health verification.
- No DisplayLink/PnP/UMDF settle state is observed.
- Step success detail is always the generic `Completed`.
- `ModeSwitchService.Run` catches step exceptions, logs only the message, marks a warning, and continues. No rollback occurs.
- USB batches stop on the first nonzero result. Earlier operations in the batch may already have succeeded; neither those targets nor the failing target are logged.
- The elevated handler catches all exceptions and exits 1 without diagnostic details.
- USB topology enumeration ignores several Configuration Manager/property return codes and swallows all IOCTL enrichment exceptions; this affects diagnostics accuracy, though not the logged pre-topology transitions.
- Audio enumeration ignores several COM return values; audio mutation checks `SetDefaultEndpoint` HRESULTs.
- Logging writes are fire-and-forget tasks. File write exceptions are swallowed, and lines can be lost if the process exits before queued writes finish.
- Logs record exception messages only, not exception type, stack trace, inner exception, full HRESULT, or native context.
- No configuration-edit history or USB-rule audit trail exists.

Thus, zero warnings is meaningful evidence of immediate API acceptance, not evidence of end-to-end hardware health.

## 9. Race Conditions / Unsafe Assumptions

1. **No DisplayLink settle delay:** The Sim transition applies USB rules, optionally waits only when an explicit USB-enable rule exists, and immediately applies display topology. With empty USB rules there is no delay at all.
2. **Desktop restores DisplayLink first:** Desktop mode calls `SetDisplayConfig` before any USB enable step. If a DisplayLink device were governed by USB rules, its display path could be requested before its PnP device finished starting.
3. **No post-display barrier:** Windows API return is treated as completion even though indirect display/UMDF work can continue asynchronously.
4. **Persisted raw IDs:** Captured LUID/source/target identifiers are assumed stable. The monitor device path is not used to re-resolve them during apply.
5. **`SDC_ALLOW_CHANGES` without verification:** Windows can accept a modified result, while the app records full success.
6. **No rollback:** A failed later audio/USB/integration step does not restore the prior display topology; a failed display apply does not stop later operations.
7. **Partial USB batches:** The elevated child exits at the first failing operation, leaving previous changes in place.
8. **Cancellation does not terminate elevated helper:** Cancellation can stop waiting for the child but the already elevated child may continue its batch.
9. **No target validation in elevated handler:** Safety checks reside before the helper. The helper will apply any decoded instance IDs it receives.
10. **Current subtree identity matching is broad:** Rules may match by identity, container, serial+VID/PID, location+VID/PID, or instance ID. This code postdates the incident but warrants future validation.
11. **USB IOCTL calls are synchronous and have no timeout:** Repeated hub/descriptor probing can stall the calling worker if a USB stack is unhealthy. This is query-only but contributed plausibly to acute load during the later recursive test incident.
12. **Test/production helper coupling:** Using `Environment.ProcessPath` assumes the host executable is the production app. That assumption is false under the smoke-test executable and caused the later elevation recursion.

The first Sim-to-Desktop reversal occurred about 11 seconds after the Sim transition completed, not immediately. Later reversals were separated by over a minute or tens of minutes. This is not an extreme toggle storm. However, the implementation itself provides no driver-specific readiness test; elapsed wall time between user actions is not equivalent to confirmed DisplayLink initialization.

## 10. Current Machine State

### 10.1 Likely SimRigMode-induced state

- The last successful app transition saved the **Desktop** supplied display topology into Windows' display database. That topology includes the DisplayLink Prompter path.
- SimRigMode is capable of having exercised the Prompter display path through four deactivate/reactivate operations. Whether those calls caused the lasting DisplayLink malfunction is plausible but unproven.
- The application config records CurrentMode Desktop and the two raw captured profiles.
- There is no evidence that SimRigMode installed, removed, or corrupted DisplayLink driver files.
- There is no evidence that the last transition directly disabled the Prompter USB functions, their parent hub, or controller.

### 10.2 Manual troubleshooting state

- The Prompter is physically unplugged.
- The old DisplayLink package was backed up under `C:\Users\<user>\Desktop\DisplayLink-driver-backup`.
- The user manually executed `pnputil /delete-driver oem4.inf /uninstall /force` only **after** reproducing and isolating the failure.
- Windows reported successful uninstall and deletion. Read-only inspection confirms `C:\Windows\INF\oem4.inf` is absent.
- A clean/new DisplayLink installation has not yet been performed.

These are later recovery actions and must not be treated as causes of the original failure.

### 10.3 Unknown / requires further evidence

- Whether the Prompter hardware/USB cable/controller path has developed an electrical or firmware fault.
- Whether DisplayLink 12.1.2424.0 had corrupted runtime state or a latent bug independent of SimRigMode.
- Whether the stutter will recur after a clean driver installation before SimRigMode is launched.
- Whether Windows' persisted display database contains stale DisplayLink records beyond what is visible in `config.json`.
- Whether any USB rules existed before the configuration written at 00:02. No historical config is available.
- Whether ETW would show the one-second stalls in UMDF, DisplayLink, USBXHCI, DWM, or another dependent process.
- The exact reason elevated PowerShell appeared smoother.

Separately, the later smoke-test recursion caused transient machine load/UAC prompts. It is no longer running and does not explain a condition that follows the Prompter across reboot/physical reconnect.

## 11. Ranked Root-Cause Hypotheses

| Rank | Hypothesis | Confidence | Evidence For | Evidence Against |
|---:|---|---|---|---|
| 1 | **A: SimRigMode's persistent `SetDisplayConfig` remove/re-add cycle triggered or exposed a latent DisplayLink/indirect-display malfunction.** | **PLAUSIBLE / moderate** | Exact code path deactivates Prompter in Sim mode and reactivates it in Desktop; saves both states; connected/disconnected A/B isolates this stack; no competing SimRigMode action materially ran in the last transition. | WUDF warnings predate app; no event at switch times; only four switches; Windows accepted all calls; standard API usage does not by itself prove driver corruption. |
| 2 | **DisplayLink software state/driver defect existed independently and became symptomatic on connection or boot.** | **STRONGLY PLAUSIBLE / moderate-high** | Prompter connection is the direct trigger; WUDF events existed before app; same events recur at later boot; clean reinstall outcome is not yet known. | Device reportedly worked for months and symptom onset was temporally associated with SimRigMode use. |
| 3 | **D: Prompter hardware, cable, or USB-side electronics coincidentally failed.** | **PLAUSIBLE / low-moderate** | Physical connection immediately triggers the fault and driver reinstall has not yet discriminated hardware from software. | Strong DisplayLink/UMDF involvement and topology switching provide a software mechanism; no hardware diagnostics establish failure. |
| 4 | **B: SimRigMode directly disabled/disrupted a Prompter USB device, hub, or controller.** | **UNSUPPORTED / very low for last transition** | Code is capable of elevated PnP changes when rules exist; newer code can operate subtrees/external hubs. | Last-transition config has no rules; no Prompter-specific target; topology code postdates switches; reboot/no autostart should clear/refrain from reapplying nonpersistent changes. |
| 5 | **E: An unrelated RGB/input/overlay application is the primary cause.** | **UNSUPPORTED / low** | Complex USB environment and pre-existing vendor apps can amplify shared-bus symptoms. | Stopping tested apps had no effect; physical Prompter A/B is highly specific; RGB/input pauses are more likely downstream symptoms of USB/system stalls. |
| 6 | **C: A SimRigMode hotkey/input hook causes the stalls.** | **RULED OUT / negligible** | None. | No hooks/raw input/injection exist; hotkeys disabled; app not running; stutter follows Prompter connection. |

The later smoke-test/UAC process chain is **CONFIRMED as the cause of the separate acute elevation-prompt/process-load event**, not ranked as the root cause of the original persistent Prompter-specific malfunction.

## 12. Exact Code Locations of Concern

| File | Class/Method | Approx Line | API/Operation | Concern |
|---|---|---:|---|---|
| `NativeInterop/DisplayConfigNative.cs` | `DisplayConfigNative` | 7–20 | Query/Set DisplayConfig declarations and flags | Defines active-only capture and persistent supplied-topology apply. |
| `Services/DisplayService.cs` | `CaptureCurrent` | 27–55 | `GetDisplayConfigBufferSizes`, `QueryDisplayConfig` with `QDC_ONLY_ACTIVE_PATHS` | Captures only active paths and raw volatile identifiers. |
| `Services/DisplayService.cs` | `Apply` | 58–75 | `SetDisplayConfig(..., 0x20|0x80|0x200|0x400)` | Immediately replays and persists raw topology; no validation, post-check, settle wait, or rollback. |
| `Services/DisplayService.cs` | `SameDisplay` | 125–127 | Device-path or target-ID comparison | Persistent path is used for UI matching but not for apply-time rebinding. |
| `SettingsWindow.xaml.cs` | `CaptureAsync` | 155–172 | Capture and config save | Stores current active DisplayLink path as part of Desktop profile. |
| `SettingsWindow.xaml.cs` | `DisplayModeToggle_Toggled` | 239–258 | `SetDisplayIncluded` and config save | Allows generic inclusion/exclusion of the Prompter without DisplayLink-specific safeguards. |
| `Services/ModeSwitchService.cs` | `SwitchAsync` SimRig branch | 22–30 | Integration → USB enable/wait → display → audio → USB disable | No DisplayLink settle/health step; wait is conditional on USB rules. |
| `Services/ModeSwitchService.cs` | `SwitchAsync` Desktop branch | 31–39 | Display first, then audio/USB/integration | Can request a DisplayLink path before a governed USB device is ready. |
| `Services/ModeSwitchService.cs` | `Run` | 59–64 | Catch-and-continue | Converts step exceptions to warnings; no rollback; success means only no synchronous exception. |
| `Services/UsbDeviceService.cs` | `PerformLocalOperation` | 42–46 | `CM_Locate_DevNode`, `CM_Enable_DevNode`, `CM_Disable_DevNode` | Global PnP state mutation; no persistent flag; low-level method has no safety validation. |
| `Services/UsbDeviceService.cs` | `ApplyBatchElevatedAsync` | 52–65 | `Process.Start`, `Verb="runas"`, `Environment.ProcessPath` | Same-EXE elevation; partial batches; unsafe assumption under a test host. |
| `App.xaml.cs` | `TryRunElevatedCommand` | 75–86 | Decode batch and execute sequentially | Exits on first failure; no per-target log/rollback; trusts command payload. |
| `Services/UsbTopologyService.cs` | `ResolveOperations` | 58–92 | Inherited subtree expansion | Post-incident code can operate many descendants or a hub; fallback identities and safety need review. |
| `Services/UsbTopologyService.cs` | `MatchRule` | 95–100 | Multi-key identity match | Broad matching could choose an unintended re-enumerated node if identity data collide/change. |
| `Services/UsbHubIoctlService.cs` | `Probe`/`ProbeHub`/`ProbePort` | 12–78 | Synchronous USB hub IOCTL queries | Query-only, but no timeout; failures are silently ignored by caller. Postdates logged transitions. |
| `Services/HotkeyService.cs` | `Register`/message loop | 16–42, 71–72 | `RegisterHotKey`/`WM_HOTKEY` | Not a hook; cannot explain current stalls; disabled by config. |
| `Services/AudioService.cs` | `Apply` | 40–54 | `IPolicyConfig.SetDefaultEndpoint` | Persistent per-user audio defaults when configured; no-op here. |
| `Services/StartupService.cs` | `SetEnabled` | 7–16 | HKCU Run value | Only persistence mechanism for automatic app launch; currently absent. |
| `Services/ConfigurationService.cs` | `SaveAsync` | 32–38 | Temp write then move | Atomic-ish config persistence; no version history/audit. |
| `Services/LoggingService.cs` | `Write` | 25–35 | Fire-and-forget file append | Can lose logs and silently suppress write failures; records no stack/native context. |
| `tests/SimRigMode.SmokeTests/Program.cs` | top-level test | 15, 59–66 | Fake USB rule followed by production transitions | Coupled to same-EXE elevation and caused the later recursive UAC/process chain. |

## 13. Could SimRigMode Have Caused This?

**Yes, it plausibly could have triggered the current DisplayLink malfunction, but direct causation is not proven.**

The reason is concrete: SimRigMode repeatedly submitted and persisted a topology without the USB DisplayLink Prompter, then submitted and persisted another topology that reconstructed it from raw identifiers. That is exactly the kind of transition that makes the indirect display/UMDF stack tear down and rebuild an active display path. There is no DisplayLink-aware readiness check, stabilization delay, postcondition check, or rollback.

What SimRigMode did **not** do during the last confirmed transition is equally important: it did not have USB rules, did not change audio defaults, did not invoke Home Assistant, did not register hotkeys, and did not install/uninstall a driver. It cannot be blamed from this evidence for direct USB disablement or driver-package removal.

The pre-app WUDF warnings mean the DisplayLink stack already produced boot-time warnings before SimRigMode existed. Therefore the strongest supportable conclusion is that the app may have **triggered or exposed a pre-existing/latent DisplayLink weakness**, not that it necessarily created the defect from a previously healthy software stack.

## 14. What Evidence Would Prove It

Additional read-only evidence that would discriminate the hypotheses:

1. A clean boot trace/ETW capture while connecting the Prompter before SimRigMode is ever run, showing whether stalls originate in USBXHCI, WUDFHost, DisplayLink, DWM, or another process.
2. A before/after clean DisplayLink reinstall A/B test, still without launching SimRigMode. Smooth operation afterward would strongly favor damaged/buggy driver state over hardware.
3. A test of the Prompter, cable, and USB port on another machine or isolated controller, distinguishing hardware from this Windows installation.
4. Windows SetupAPI logs (`setupapi.dev.log`) around the first symptom onset and the relevant Prompter instance IDs.
5. DisplayLink support/diagnostic logs containing device resets, failed transfers, or indirect-display retries at the one-second stall cadence.
6. Windows Performance Recorder data covering a stall, with USB, PnP, DPC/ISR, process/thread scheduling, DWM, and UMDF providers.
7. A historical copy of `%LOCALAPPDATA%\SimRigMode\config.json` or UAC/process audit proving whether any USB rule ever targeted the Prompter or an ancestor hub.
8. A captured `QueryDisplayConfig` state before/after each app apply and after reboot, showing whether Windows accepted a changed/stale DisplayLink mapping.
9. Application Control/4688 process-creation logs for the mode-switch times, which could prove whether an elevated SimRigMode helper was launched then.
10. Exact onset time correlated against the four transition timestamps, not merely the same general work session.

## 15. Recommended Recovery Direction

Recovery should remain separate from forensic attribution:

- Keep the Prompter disconnected until the DisplayLink software stack is cleanly reinstalled.
- Install a current, known-compatible DisplayLink/Elgato driver and test the Prompter before launching SimRigMode.
- First test simple connection stability, then reboot stability, then display activation/deactivation using Windows-supported UI before involving SimRigMode.
- If stutter persists after a clean driver install, isolate cable, port/controller, and hardware on another system.
- Avoid replaying the saved SimRigMode DisplayLink profile until the implementation is revised and verified.
- Preserve the current logs/config/report and collect a performance trace if symptoms recur.

No recovery action was executed as part of this report.

## 16. Changes Required Before SimRigMode Is Safe To Run Again

Analysis only; none of these changes were implemented:

1. Remove `SDC_SAVE_TO_DATABASE` from routine transient switching unless persistent Windows topology changes are explicitly desired and justified.
2. Resolve current displays by stable monitor path/EDID/container identity at apply time instead of replaying stale adapter LUID/source/target IDs blindly.
3. Add `SDC_VALIDATE` before apply and reject mismatched/stale DisplayLink paths.
4. Query and verify the actual topology after apply.
5. Add DisplayLink/indirect-display-aware readiness and stabilization checks, especially before re-adding the Prompter.
6. Do not request the Desktop DisplayLink path until its USB/PnP device and driver are confirmed started.
7. Add rollback to the last known-good active topology on failure.
8. Record every submitted display path, flags, native return, duration, and verified post-state.
9. Log USB operation targets/results individually and handle partial batches explicitly.
10. Enforce critical-device/hub/controller protection again inside the elevated handler, not only in the caller/UI.
11. Eliminate `Environment.ProcessPath` coupling for test hosts or make the elevated protocol terminate safely when the host is not the production app.
12. Ensure tests never execute production setters/elevation unless explicitly isolated and opt-in; use nonmutating abstractions/fakes by default.
13. Add bounded timeouts/cancellation around USB hub IOCTL probing and avoid repeated whole-machine probes during transitions.
14. Keep DisplayLink displays outside mode switching by default until dedicated testing proves safe.
15. Do not run the currently delivered executable or current smoke tests on this machine until these issues are addressed and a clean build is independently reviewed.

## 17. Final Conclusion

The **direct current trigger is the Elgato Prompter/DisplayLink connection**, based on repeatable physical A/B testing. SimRigMode supplied and persisted display topologies that repeatedly removed and restored that exact DisplayLink display path. This is a technically credible trigger for an indirect-display/UMDF driver malfunction and is the only material state-changing action confirmed in the last logged transition.

However, causation is not conclusively established. Prompter WUDF warnings occurred before SimRigMode existed, no matching failure event appears at the transition times, and Windows accepted every `SetDisplayConfig` request. The best-supported formulation is: **SimRigMode plausibly triggered or exposed a latent DisplayLink stack problem; it is not proven to have created the underlying defect.**

Direct SimRigMode USB/PnP interference with the Prompter is unsupported and very unlikely for the last transition because `UsbRules` was empty. A hotkey/input-hook cause is ruled out. Later manual driver removal changed the current machine state only after the original failure was isolated. The later smoke-test elevation recursion was a separate confirmed development-test failure that caused acute load and UAC prompts but did not account for the earlier Prompter-specific, reboot-surviving stutter.
