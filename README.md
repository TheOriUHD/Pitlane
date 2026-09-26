# Pitlane

Pitlane is a Windows 11 tray utility that switches a PC between its Desktop and Sim Rig setups:
displays, default audio devices, USB devices, the rig's smart plug and the apps that belong to each.
It is built with WinUI 3 and the Windows App SDK. It was called SimRigMode until 2026-09-24, and
the code still uses that name.

## Run

Build it first (see below), then copy the published `Pitlane.exe` to any folder and launch it. No prebuilt binary ships in this repository. No .NET runtime, Windows App SDK runtime, installer, scripts, DLLs, or adjacent configuration files are required. The unpackaged single-file application extracts its bundled framework files to a temporary runtime location when needed.

- **Left-click the tray icon** to open the flyout: the active mode and the switch. **Hold the
  switch for 3 seconds** to change modes; let go earlier and nothing happens. Space or Enter works
  too while the switch has focus. **Open**, top right, opens the full app.
- **Right-click the tray icon** for a menu: open the app, switch modes directly, Start with
  Windows, and Quit.
- The tray icon's loop is blue in Desktop mode and orange in Sim Rig mode.
- **Settings → Advanced → Test switch without the displays** runs a real switch minus the display
  step: linked apps, Home Assistant, audio and USB. **Toggle** alternates, **Sim Rig** and
  **Desktop** run that mode's steps. The monitors, and the mode Pitlane shows, stay as they are.

Configuration is written atomically to `%LOCALAPPDATA%\Pitlane\config.json`. Logs are retained for 14 days under `%LOCALAPPDATA%\Pitlane\Logs`. On its first start Pitlane copies the configuration SimRigMode left in `%LOCALAPPDATA%\SimRigMode`, and leaves that folder as it was.

## First-time setup

1. Arrange the desktop monitors in Windows, then use **Desktop Mode → Capture Current Display Layout**.
2. Power/connect the rig and arrange its monitors, then capture the **Sim Rig Mode** layout.
3. In **USB Devices**, choose the desired action for each mode. Rules are saved but **inert**:
   USB switching ships disarmed and must be armed explicitly in **Settings → Safety**. Device
   changes use Windows Configuration Manager and request one narrowly scoped UAC elevation per
   batch.
4. Choose optional playback/recording defaults for each mode.
5. Optionally, in **Integrations**, enter where Home Assistant runs, a long-lived access token and
   the entity that powers the rig. The page checks the connection by reading the entity, and the
   **Home Assistant control** switch turns it on and off without losing the three fields. The
   token is protected with Windows DPAPI for the current user.
6. Before the first real switch, turn on **Settings → Safety → Dry run transitions**, switch
   once, and read **Settings → Diagnostics**. A dry run validates everything and calls no
   mutating API, so it shows exactly what a real switch would do. It changes no display, audio
   device, USB device, smart plug or app, and the mode on record stays as it was.

Display captures preserve persistent monitor device paths, EDID identity, adapter device paths,
exact timing rationals, resolution, refresh, orientation, position, and topology. There is no
refresh-rate cap.

Captured adapter LUIDs and mode indices are deliberately **not** replayed. Windows allocates
adapter LUIDs per boot, so a profile is re-resolved against the displays actually connected at
the time of the switch - by monitor device path, then EDID identity, then adapter device path -
and the submitted arrays are rebuilt from scratch. If a display in a profile is not connected, or
two connected displays match it equally well, the switch is refused rather than guessed at.

## Build and test

```powershell
dotnet restore SimRigMode.csproj
dotnet build SimRigMode.csproj -c Release
dotnet test tests\SimRigMode.Tests\SimRigMode.Tests.csproj -c Release
dotnet publish SimRigMode.csproj -c Release -r win-x64 --self-contained true -o dist
```

### Layout

| Project | Kind | Purpose |
|---|---|---|
| `SimRigMode.csproj` | WinUI 3 `WinExe` | Tray app, settings window, composition root |
| `SimRigMode.Core` | class library | Models, native interop, hardware-facing services |
| `tests/SimRigMode.Tests` | xUnit | Hardware-free tests; references **Core only** |

`SimRigMode.Core` is a class library deliberately. A library emits no apphost, so
referencing it can never place a runnable `Pitlane.exe` into a test output folder.
The previous smoke-test project did exactly that, and reached the production `runas`
elevation path through `Environment.ProcessPath`; see `docs/report.md`.
`tests/Directory.Build.props` sets `UseAppHost=false` for everything under `tests/`
and **fails the build** if a runnable binary ever reappears there.

Tests must never touch real hardware. Nothing under `tests/` may call `SetDisplayConfig`,
`CM_Enable_DevNode`/`CM_Disable_DevNode`, `IPolicyConfig`, or trigger elevation.

Publishing is configured for a single, untrimmed, compressed, self-contained x64 executable using Windows App SDK 2.4.0 and stable Windows Community Toolkit settings controls. WinUI 3, COM interop, and native P/Invoke metadata are retained.

## Safety and limitations

**Displays**

- Pitlane changes your displays only in a mode switch you start, and never writes Windows'
  persistent display database: not in a switch, not from a button. Every switch validates first,
  applies without `SDC_SAVE_TO_DATABASE`, re-queries to confirm the result, and rolls back if the
  result does not match. Saved layouts are Windows' own, written by its Settings.
- Indirect displays - DisplayLink, USB docks, virtual displays - are detected from their adapter
  device path and are **carried through a mode switch untouched** by default. They are neither
  removed nor re-added, because that is the operation implicated in the incident described in
  `docs/report.md`. Note that output technology does not identify them: the device at the centre
  of that incident reports LVDS.
- A display step that fails or is rolled back aborts the transition instead of being logged as a
  warning while the remaining steps run anyway.
- Some monitors must never be detached. The PG27AQDP on HDMI drops off the bus and reconnects
  every few seconds while detached, and every reconnect stalls Windows' input handling. That was
  the real cause of the incidents in `docs/report.md`. Monitors on the park list
  (**Displays → Park when not in use**) stay attached in a corner of the desktop, where the mouse
  practically never reaches them. While parked they are kept black, and any window that lands
  on one is moved to the main screen. Pitlane sends monitors no commands: switch
  the screen off and on with its own power button.
- A watchdog reports any monitor that keeps dropping out and says how to fix it. It never changes
  your displays itself. If a monitor is left out of the layout and keeps dropping out, attach it
  in Windows: **Settings → System → Display**, select it, and choose **Extend desktop to this
  display**, or unplug its cable.
- A mode switch can turn on monitors that are connected but currently out of the layout.

**Apps**

- **Settings → Apps** links programs to a mode. They start when the mode begins, unless they
  are already running, and close when you switch to the other mode. The picker lists what is in
  your Start menu and what is running, with sim racing tools first, so you rarely need to browse
  for an .exe.
- Closing first asks the app to close, like clicking its X. Many sim tools hide in the tray
  instead, so an app still running a few seconds later is ended, unless you turn that off for it.
- Apps that run as administrator can be started, but Pitlane cannot end them. A shortcut that
  launches a helper, such as RaceHub's elevator, starts fine but cannot be closed by path.
- Apps in an imported configuration never start until you turn them on.

**USB devices**

- USB switching ships **disarmed** and does nothing until armed in Settings.
- Keyboards, mice, system-class devices, USB host controllers and root hubs are protected, and
  protection propagates upward: a USB composite device with a keyboard behind it is protected even
  though its own device class is plain `USB`. A hub with a protected device in its branch cannot
  be disabled at all.
- A rule that carries no usable device identity matches nothing. Imported and on-disk
  configurations are sanitized before use.
- The elevated helper validates every operation independently before touching a device, and
  refuses anything that is not a `USB\` devnode. It cannot itself request elevation, and it
  refuses to run unless it is the genuine Pitlane executable.
- Enable/disable may still be refused by Windows, may require a reboot, or may be cancelled at
  UAC. Nothing is applied unless the whole batch validates.

**Other**

- Outside a switch you start, Pitlane only listens: the tray icon, the global shortcuts
  (`RegisterHotKey`, not a keyboard hook) and the display watchdog change nothing. Nothing starts
  a switch automatically. See `docs/SAFETY.md`, invariant 15.
- Audio default switching uses Windows' undocumented `IPolicyConfig` COM surface; it is probed for
  availability and failures are isolated and reported.
- The Home Assistant token goes over plain http only to an address on the local network, such as
  homeassistant.local or 192.168.x.x. Anywhere else Pitlane insists on https.
- A failed smart-plug power-on aborts a Sim Rig transition rather than continuing to devices that
  may have no power.
