# Safety invariants

These are the rules that keep SimRigMode from repeating the 2026-09-13 and 2026-09-24
incidents (`report.md`, section 0 first). The app is called Pitlane since 2026-09-24; SimRigMode
is its codename in the code and in these documents. Each one has a test. **If you are changing this
codebase, break one of these only deliberately, and update its test when you do.**

---

## 1. A non-production process can never elevate

`ElevationGuard.AssertMayElevate()` runs before any `runas` relaunch and before any startup
registration. It requires the entry assembly to be `SimRigMode`, the assembly to carry the
compiled-in `SimRigMode.ProductIdentity` metadata, and the process image to be named
`SimRigMode.exe`.

*Why:* `UsbDeviceService.ApplyBatchElevatedAsync` relaunched `Environment.ProcessPath` with
`runas`. Under the smoke-test host that was the test binary, whose entry point ignored the worker
argument and re-ran the whole suite — reaching the same call again, unboundedly.

- Never use `Environment.ProcessPath` for elevation or startup. Use `ElevationGuard.VerifiedSelfPath`.
- The elevated worker runs in `Program.Main` **before** `Application.Start`, and returns an exit
  code. It must never be able to fall through into application startup: a high-integrity tray app
  would take the singleton mutex and lock the user's own instance out.
- `ElevationPolicy` is a one-way latch. The worker disables it before touching a device; the test
  assembly disables it in a `[ModuleInitializer]`.

*Tests:* `ElevationGuardTests`.

---

## 2. SimRigMode never writes Windows' display database

Not in a switch, not from a button, not at all. `DisplayService.Submit`, the only caller of
`SetDisplayConfig`, throws if `SDC_SAVE_TO_DATABASE` is passed. Saved layouts belong to Windows:
its own Settings writes them, and Windows re-applies them at boot and on every monitor change.

*Why:* every switch used to pass it, so a transient mode change rewrote persistent system state
and outlived both the process and the reboot. That is how "PG27AQDP dark" kept being re-applied
by Windows itself, with SimRigMode closed (`report.md`, section 0). A later "Set as Windows
default" button kept the ability behind a click; it was removed on 2026-09-24.

*Tests:* `WindowsOwnedStateTests.NothingSavesALayoutIntoWindowsDisplayDatabase`,
`WindowsOwnedStateTests.TheOnlySetDisplayConfigCall_RefusesTheSaveFlagFirst`,
`DisplayPipelineTests.NoSubmissionEverCarriesSaveToDatabase`,
`DisplayActivationTests.SwitchingOn_NeverWritesTheWindowsDisplayDatabase`.

---

## 3. Indirect displays are carried through, not switched

`SDC_USE_SUPPLIED_DISPLAY_CONFIG` has **no "leave this display alone" semantics**. Any active path
omitted from the submitted array becomes inactive. Excluding a display from a profile therefore
*tears it down*.

`DisplaySubmissionBuilder` re-appends every live indirect display the user has not opted into
managing, unchanged. This is driven by the **live** classification, so it does not depend on what
an old profile happens to contain. The one thing that can change is its position: if it would
overlap a display the new layout places, it is moved the shortest distance that clears it. Windows
does not allow overlaps and settles them itself, moving whichever display it picks; on 2026-09-26
it moved two desk monitors instead, and the switch was rolled back.

*Why:* this was introduced believing that removing and re-adding the DisplayLink Prompter was the
mechanism of the 2026-09-13 incident. It was not: see `report.md`, section 0, and invariant 11.
It stays, because an indirect display stack gains nothing from being torn down on every switch, and
a missing or switched-out indirect display never blocks a switch either. But it is the default, not a
rule: an indirect display the user turns **Switch with modes** on for
(`GeneralConfig.SwitchedIndirectDisplays`) follows the layouts like any other display, and is switched
off and on again. The user asked for that for the Prompter on 2026-09-26, to have it off in Sim Rig.

**Indirect displays are identified by adapter device path, never by output technology.** The
Elgato Prompter reports `OutputTechnology = 6` (LVDS) — the obvious check would have missed the
exact device that caused the problem. An adapter that cannot be classified is treated as indirect.

*Tests:* `DisplayPipelineTests.SwitchingToSimRig_CarriesThePrompterThrough_InsteadOfDeactivatingIt`,
`DisplayPipelineTests.AnIndirectDisplaySwitchedWithModes_IsLeftOutOfAModeThatDoesNotUseIt`,
`DisplayPipelineTests.AnIndirectDisplaySwitchedWithModes_IsSwitchedBackOnInAModeThatUsesIt`,
`DisplayPipelineTests.ACarriedDisplay_ThatWouldOverlapTheNewLayout_IsMovedClearTheShortestWay`,
`IndirectDisplayDetectorTests.Prompter_IsClassifiedIndirect_DespiteReportingLvds`.

---

## 4. Nothing per-boot is replayed

Adapter LUIDs are allocated per boot. Profiles are re-resolved against the live topology by stable
identity, and submitted mode arrays are rebuilt from empty. Every match tier requires a **unique**
live match; ambiguity refuses rather than guesses.

*Test:* `DisplayPipelineTests.AdapterLuidsReshuffledByAReboot_AreReResolvedRatherThanReplayed`.

---

## 5. Apply is validated, verified, and reversible

`validate rollback → validate forward → apply → settle → verify → commit | roll back`.

The **rollback is validated first**: a rollback that has not been proven executable is not a
rollback. Verification compares intent — which displays are active, resolution, rotation, relative
geometry — not submitted bytes. A missing display, a wrong resolution or a wrong rotation rolls the
switch back. A display Windows placed somewhere else is logged as a warning and does not.

*Why:* a zero return from `SetDisplayConfig` proves synchronous API acceptance and nothing else.
"Transition completed with 0 warning(s)" was compatible with a display having silently vanished.

*Tests:* `DisplayPipelineTests` rollback and verifier cases.

---

## 6. A rule with no identity matches nothing

`UsbRule.IsAddressable` must be true, and structural nodes (Computer, Port, Interfaces) are never
rule targets.

*Why:* `UsbRule.InstanceId` defaults to `""`, and so do the synthetic Computer and Port nodes.
`MatchRule` compared them with no emptiness guard, so a rule that never set `InstanceId` matched
every port and rule inheritance made it the effective policy for **every device on the machine**.
That is what the old smoke test did; its batch was machine-wide, not empty.

*Tests:* `UsbRuleMatchingTests`.

---

## 7. Criticality propagates upward

`IsCritical == IsSelfCritical || HasCriticalDescendant`.

*Why:* a USB composite keyboard's own devnode has class `USB`; only its HID child carries class
`Keyboard`, and the topology builder filters interface nodes out of the physical tree. A composite
keyboard whose product name lacks the word "keyboard" was not protected.

A hub with a protected device in its branch cannot be self-disabled: the resolver deliberately does
not operate children of a self-disabled hub, so they are collateral rather than protected.

*Tests:* `CriticalityTests`.

---

## 8. The elevated side trusts nothing

`ElevatedWorker.Validate` re-checks every operation inside the elevated process. Only `USB\`
devnodes are operable, so a forged or corrupted payload cannot reach a PCI host controller, a PS/2
keyboard or a storage volume. The **whole batch** is validated before any of it is applied.

*Why:* the previous handler decoded its command line and applied it with no validation at all.

*Tests:* `ElevationGuardTests` validation cases.

---

## 9. Tests never touch hardware

- `SimRigMode.Core` is a class library, so it emits no apphost and referencing it cannot place a
  runnable `SimRigMode.exe` into a test output folder.
- `tests/Directory.Build.props` sets `UseAppHost=false` for everything under `tests/` and **fails
  the build** if a runnable binary appears there.
- No test may call `SetDisplayConfig`, `CM_Enable_DevNode`/`CM_Disable_DevNode`, `IPolicyConfig`,
  or trigger elevation. The display pipeline is tested through `IDisplayConfigGateway`.

---

## 10. Interop layout is asserted

Every DisplayConfig struct size and union offset is checked at runtime. A wrong size here does not
fail loudly — it hands the kernel a misaligned array and produces something that looks like a
driver fault. `PROPVARIANT` is 24 bytes on x64; it was unset, computed to 16, and overran the
marshalled buffer on every audio enumeration.

*Tests:* `DisplayConfigLayoutTests`, `ConfigSanitizerTests.PropVariantIsTwentyFourBytesOnX64`,
`DisplayPowerTests.NewInteropStructsMatchTheNativeLayout`.

---

## 11. A monitor on the park list is parked, never detached

A connected monitor that is detached from the desktop is not in a neutral state. The PG27AQDP
(HDMI) drops off the bus about 6.6 s after it is detached, returns 1 s later, and repeats every
7.6 s. It did so switched on, in standby and switched off, though not every time it was off.
Every return stalls Windows' window manager and input delivery for the whole machine. Attached,
it never drops out. This, not the Prompter, was both incidents.

So a monitor on `GeneralConfig.SwitchOffWhenUnused` that a mode leaves out is **parked**:
`DisplayPowerPolicy.WithParked` keeps it in the submitted layout at a corner where it touches the
desktop at a single point. Staying attached is what stops the loop. SimRigMode does not switch it
off: a DDC/CI power-off was tried and removed, because a monitor's power is the user's, and the
monitor's own button does the same. `GeneralConfig.ParkedDisplays` records what is parked so that
a layout capture leaves it out. Parking only happens inside a switch, never in dry run, and never
from a test.

Staying attached has two costs that detaching does not. A parked monitor stays lit, and it is
still part of the desktop, so windows end up on it, out of the mouse's reach. On 2026-09-26 the
first switches to Desktop left the user's Claude window on the lit, unreachable rig monitor.
Moving windows once, during the switch, was not enough: Windows 11 puts each window back where it
was the last time the same monitors were connected, seconds later. So while a monitor is parked,
`ParkedScreenKeeper` keeps it black with a window of Pitlane's own, which on the PG27AQDP's OLED
panel is as good as off, and moves any window that lands on it to the main screen
(`ParkedWindowMover`): at once when one is brought to the front, and within two seconds otherwise.
It never activates a window, and touches no window on any other monitor. It starts as soon as the
switch has applied its layout. Only a monitor that is parked and still out of reach counts; one the
user has moved next to another display is theirs again (`DisplayPowerPolicy.LocateParked`).

The standby test build (`dotnet publish -p:StandbyTest=true`, `Product.IsStandbyTest`) breaks this
invariant on purpose: it leaves listed monitors out, so the user can try by hand whether the rig
monitor still loops without input. It says so in the flyout, the overview and the tray tooltip,
logs every monitor dropout, and is swapped back for the normal build after the test.

*Tests:* `DisplayPowerTests` parking and parked-monitor cases, `DisplayPowerTests.TheNormalBuildParks`,
`WindowsOwnedStateTests.NothingSendsAMonitorAPowerCommand`.

---

## 12. A monitor that keeps dropping out is reported, never touched

`MonitorHotplugWatcher` listens for monitor device-interface arrivals and removals with
`CM_Register_Notification`. It is event-driven and read-only, and it never asks anything of a
device. `DisplayBounceDetector` reports a monitor that drops out **3 times within 45 s**, then stays
quiet for 10 minutes per monitor. For an unused monitor that loops, the user is told how to attach
it in Windows' own display settings, or to unplug it. It is also put on the park list, so that
SimRigMode's own next switch does not detach it again. A monitor in the current layout that loops
gets a cable or port warning instead.

The watchdog never changes the layout. It did at first, re-attaching the monitor on its own, and
that is the kind of change behind the user's back that invariant 15 rules out.

*Why:* the loop ran for 90 minutes on 2026-09-13 and was blamed on the wrong device, because
nothing reported it. The first measurement named it within 16 seconds.

*Tests:* `DisplayPowerTests` watchdog cases, `WindowsOwnedStateTests.TheDisplayWatchdogOnlyReports`.

---

## 13. A mode switch can switch a connected monitor on

Monitors that are connected but out of the layout are invisible to `QDC_ONLY_ACTIVE_PATHS`.
`DisplaySubmissionBuilder` also resolves profiles against `QDC_ALL_PATHS`
(`IDisplayConfigGateway.CaptureConnected`). Each monitor it switches on gets a free source on its
own adapter. It prefers the source it was captured on and never takes one from a display that
stays on. A monitor that is not plugged in is still refused, and an indirect display is switched on
only when it switches with the modes (invariant 3).

*Why:* the builder only saw active displays, so every profile that had to switch a monitor ON was
refused as "not connected". Sim Rig -> Desktop could never have succeeded. It went unnoticed
because the build actually in use predated this pipeline.

*Tests:* `DisplayActivationTests`.

---

## 14. Linked apps are checked input, and tests never run programs

A mode switch starts the programs in `ModeConfig.LinkedApps`, so the list is sanitized like any
other input from outside the process: only full `.exe` paths survive, and an **imported** file's
apps never start until the user turns them on. SimRigMode starts programs the way double-clicking
would, and never elevates one itself. Closing asks first, and ends a program only when its entry
allows it. A program that runs as administrator cannot be ended, and that is reported.

`AppLauncherService` works through `IProcessController`. Tests use `FakeProcessController`, and
`new SystemProcessController(` is on the forbidden list in `NoHardwareAccessFromTestsTests`.

*Tests:* `LinkedAppsTests`.

---

## 15. Nothing changes outside a switch you start

SimRigMode changes the machine only inside a mode switch that the user starts: by holding the
switch in the tray flyout or on the full app's overview for three seconds, from the tray menu,
with a global shortcut, or with a mode page's Apply button. Nothing starts a switch automatically, not startup, not resume from
sleep, and not the watchdog. A dry run reaches no display, audio device, USB device, smart plug
or app, and does not change the mode on record. A switch changes only what the user configured it for:
the display layout, and, if set up, the default audio devices, the USB devices a rule names, the
smart plug and the linked apps.

Everything that runs in the background only listens: the tray icon, the global shortcuts
(`RegisterHotKey`, not a keyboard hook), the display watchdog and the power-mode notification.
One thing acts on its own, and only on a monitor a switch has parked: it is kept black, and any
window that lands on it is moved to the main screen, because nothing can be used there (invariant 11).
Opening Settings reads the display, USB and audio configuration and changes none of it.

SimRigMode never writes what Windows owns and keeps by itself: the saved display layouts
(invariant 2), and a monitor's power (invariant 11).

*Why:* on 2026-09-24 SimRigMode did all three. Old builds saved every switch into Windows' display
database, the watchdog re-attached monitors on its own, and parking sent monitors a DDC/CI
power-off. Each one made it harder to tell what SimRigMode had done from what Windows did.

*Tests:* `WindowsOwnedStateTests`.
