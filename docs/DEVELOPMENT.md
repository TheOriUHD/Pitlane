# Developing Pitlane

Pitlane is a WinUI 3 tray app on the Windows App SDK. It was called SimRigMode until 2026-09-24,
and the code still uses that name: namespaces, project names and internal identifiers. The name
people see lives in `SimRigMode.Core/Product.cs`.

The rules the code must keep, each with its tests, are in [SAFETY.md](SAFETY.md). The incidents
that shaped them are in [report.md](report.md).

## Build, test, publish

```powershell
dotnet restore SimRigMode.csproj
dotnet build SimRigMode.csproj -c Release
dotnet test tests\SimRigMode.Tests\SimRigMode.Tests.csproj -c Release
dotnet publish SimRigMode.csproj -c Release -r win-x64 --self-contained true -o dist
```

Publishing produces a single, untrimmed, compressed, self-contained x64 `Pitlane.exe`, with the
Windows App SDK 2.4 and the Windows Community Toolkit settings controls inside. WinUI 3, COM interop
and the P/Invoke metadata are kept. No .NET runtime, Windows App SDK runtime, installer, DLLs or
configuration files are needed next to it; the single file extracts its bundled framework files to
a temporary location when it needs them. No prebuilt binary is kept in this repository.

## Installer and releases

`installer\Pitlane.iss` builds a standard setup wizard with [Inno Setup 6](https://jrsoftware.org/isinfo.php).
It installs for the current user without an administrator prompt, into a folder they choose
(`%LOCALAPPDATA%\Programs\Pitlane` by default), with a Start menu shortcut and an uninstaller in
Windows' Apps list. A running Pitlane is asked to close first. Uninstalling leaves the settings and
logs in `%LOCALAPPDATA%\Pitlane`, and removes the Start with Windows entry only when it names the
copy being removed.

To release a new version:

1. Raise `<Version>` in `SimRigMode.csproj` and `AppVersion` in `installer\Pitlane.iss` together.
   Never change the script's `AppId`: it is how a new version finds and replaces the installed one.
2. Publish, then build the setup:

   ```powershell
   dotnet publish SimRigMode.csproj -c Release -r win-x64 --self-contained true -o dist
   iscc installer\Pitlane.iss
   ```

3. Publish it on GitHub:

   ```powershell
   gh release create v<version> dist\installer\PitlaneSetup-<version>.exe --title "Pitlane <version>" --notes-file <notes>
   ```

The installer and the app are not code-signed, so Windows SmartScreen warns about the setup.

## Project layout

| Project | Kind | Purpose |
|---|---|---|
| `SimRigMode.csproj` | WinUI 3 `WinExe` | Tray app, flyout, main window, composition root |
| `SimRigMode.Core` | class library | Models, native interop, hardware-facing services |
| `tests/SimRigMode.Tests` | xUnit | Hardware-free tests; references **Core only** |

`SimRigMode.Core` is a class library on purpose. A library emits no apphost, so referencing it can
never place a runnable `Pitlane.exe` into a test output folder. The old smoke-test project did
exactly that, and reached the production `runas` elevation path through `Environment.ProcessPath`;
see `report.md`. `tests/Directory.Build.props` sets `UseAppHost=false` for everything under
`tests/` and **fails the build** if a runnable binary ever appears there.

Tests never touch real hardware. Nothing under `tests/` may call `SetDisplayConfig`,
`CM_Enable_DevNode`/`CM_Disable_DevNode`, `IPolicyConfig`, move real windows or trigger elevation;
`NoHardwareAccessFromTestsTests` enforces it.

## Where things live at runtime

- Configuration: `%LOCALAPPDATA%\Pitlane\config.json`, written atomically. On its first start
  Pitlane copies the configuration SimRigMode left in `%LOCALAPPDATA%\SimRigMode`, and leaves that
  folder as it was.
- Logs: `%LOCALAPPDATA%\Pitlane\Logs`, kept for 14 days. Every display switch logs what it
  submitted, the flags, Windows' answer and every verification finding.

## How display switching works

A captured layout keeps each monitor's device path, EDID identity, adapter device path, exact
timing rationals, resolution, refresh rate, orientation, position and topology. There is no
refresh-rate cap.

Captured adapter LUIDs and mode indices are deliberately **not** replayed. Windows allocates adapter
LUIDs per boot, so a layout is re-resolved against the displays connected at the time of the switch,
by monitor device path, then EDID identity, then adapter device path, and the submitted arrays are
rebuilt from scratch. If a display in a layout is not connected, or two connected displays match it
equally well, the switch is refused rather than guessed at. Monitors that are connected but off are
switched on, each on a free source of its own adapter.

Every switch validates the rollback first, then the new layout, applies it without
`SDC_SAVE_TO_DATABASE`, waits for the displays to settle and reads back what Windows did. A missing
display, a wrong resolution or a wrong rotation rolls the switch back. A display Windows placed
somewhere else is logged as a warning: Windows re-applies the arrangement it has stored for a set of
monitors whenever one of them wakes up, and that is not a failed switch.

**Indirect displays.** DisplayLink screens, USB docks and virtual displays are recognised by their
adapter device path; output technology does not identify them, and the Elgato Prompter reports
LVDS. By default they are carried through a switch as they are. That rule came from an early theory
that removing and re-adding the Prompter caused the 2026-09-13 incident, which turned out to be
wrong, so it is a default and not a rule: **Switch with modes** makes one follow the layouts like
any other display. A carried display that would overlap the new layout is moved the shortest way
clear. A missing or slow indirect display never blocks a switch.

**Parking.** The PG27AQDP on HDMI drops off the bus and reconnects every 7.6 s while detached, and
every reconnect stalls Windows' input handling. That was the real cause of the incidents in
`report.md`. A monitor on the park list is kept attached in a corner of the desktop, where the mouse
practically never reaches it. While it is parked, `ParkedScreenKeeper` keeps it black, and moves any
window that lands on it to the main screen: at once when one is brought to the front, and within two
seconds otherwise. Pitlane sends monitors no commands.

**The watchdog** reports a monitor that keeps dropping out and says how to fix it. It never changes
the displays itself.

## Apps

- Linked apps start when their mode begins, unless they are already running, and close when you
  switch to the other mode. The picker lists the Start menu and running programs, sim racing tools
  first.
- Closing first asks the app to close, like clicking its X. Many sim tools hide in the tray instead,
  so an app still running a few seconds later is ended, unless that is turned off for it.
- Apps that run as administrator can be started but not ended. A shortcut that launches a helper,
  such as RaceHub's elevator, starts fine but cannot be closed by path.
- Apps in an imported configuration never start until they are turned on.

## USB devices

- USB switching ships **disarmed** and does nothing until armed in Settings.
- Keyboards, mice, system-class devices, USB host controllers and root hubs are protected, and
  protection propagates upward: a composite device with a keyboard behind it is protected even
  though its own class is plain `USB`. A hub with a protected device in its branch cannot be
  disabled at all.
- A rule that carries no usable device identity matches nothing. Imported and on-disk
  configurations are sanitized before use.
- Device changes go through Windows Configuration Manager, with one narrowly scoped UAC elevation
  per batch. The elevated helper validates every operation again before touching a device, refuses
  anything that is not a `USB\` devnode, cannot request elevation itself, and refuses to run unless
  it is the genuine Pitlane executable.
- Windows may still refuse a change, ask for a reboot, or be cancelled at UAC. Nothing is applied
  unless the whole batch validates.

## Other

- Outside a switch, Pitlane only listens: the tray icon, the global shortcuts (`RegisterHotKey`, not
  a keyboard hook), the display watchdog and power-mode notifications. Nothing starts a switch
  automatically. The one exception is a parked monitor; see `SAFETY.md`, invariants 11 and 15.
- Default audio devices are set through Windows' undocumented `IPolicyConfig` COM interface. It is
  probed before use, and failures are isolated and reported.
- The Home Assistant token is protected with DPAPI for the current user. It goes over plain http
  only to an address on the local network, such as `homeassistant.local` or `192.168.x.x`; anywhere
  else Pitlane insists on https.
- A failed smart-plug power-on stops a switch to Sim Rig, rather than continuing to devices that may
  have no power.

## Test builds

`dotnet publish ... -p:StandbyTest=true` builds the standby test build (`Product.IsStandbyTest`). It
leaves monitors on the park list out of the layout instead of parking them, and logs every monitor
dropout, to test by hand whether a monitor still loops without a signal. It says so in the flyout,
the main window and the tray tooltip, and is never the normal build.

`dotnet publish ... -p:CaptureDemo=true` builds the capture demo (`Product.IsCaptureDemo`), used to
record the README's switch animation. A switch plays exactly as it looks, for 1.7 s, and changes
nothing: no display, app, sound, USB device or smart plug is touched, and the mode is never saved.
Nothing marks it, so the recording looks like the real app. Put the normal build back afterwards.
