# Safety invariants

These are the rules that keep SimRigMode from repeating the 2026-09-13 incident
(`report.md`). Each one has a test. **If you are changing this codebase, break one of
these only deliberately, and update its test when you do.**

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

## 2. A mode switch never writes Windows' display database

`SDC_SAVE_TO_DATABASE` is reachable only from `DisplayService.PersistCurrentTopologyAsWindowsDefault`,
which is wired to one explicit button.

*Why:* every switch used to pass it, so a transient mode change rewrote persistent system state
and outlived both the process and the reboot.

*Test:* `DisplayPipelineTests.NoSubmissionEverCarriesSaveToDatabase`.

---

## 3. Indirect displays are carried through, not switched

`SDC_USE_SUPPLIED_DISPLAY_CONFIG` has **no "leave this display alone" semantics**. Any active path
omitted from the submitted array becomes inactive. Excluding a display from a profile therefore
*tears it down*.

`DisplaySubmissionBuilder` re-appends every live indirect display the user has not opted into
managing, unchanged. This is driven by the **live** classification, so it does not depend on what
an old profile happens to contain.

*Why:* removing and re-adding a DisplayLink display on every switch is the mechanism at the centre
of the incident.

**Indirect displays are identified by adapter device path, never by output technology.** The
Elgato Prompter reports `OutputTechnology = 6` (LVDS) — the obvious check would have missed the
exact device that caused the problem. An adapter that cannot be classified is treated as indirect.

*Tests:* `DisplayPipelineTests.SwitchingToSimRig_CarriesThePrompterThrough_InsteadOfDeactivatingIt`,
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
geometry — not submitted bytes.

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

*Tests:* `DisplayConfigLayoutTests`, `ConfigSanitizerTests.PropVariantIsTwentyFourBytesOnX64`.
