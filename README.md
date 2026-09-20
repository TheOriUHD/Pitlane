# SimRigMode

SimRigMode is a Windows 11 tray utility that switches a PC between captured Desktop and Sim Rig hardware/workspace states. Its settings experience uses WinUI 3, Windows App SDK, Fluent Design, Mica, NavigationView, and Windows Community Toolkit settings controls.

## Run

Build it first (see below), then copy the published `SimRigMode.exe` to any folder and launch it. No prebuilt binary ships in this repository. No .NET runtime, Windows App SDK runtime, installer, scripts, DLLs, or adjacent configuration files are required. The unpackaged single-file application extracts its bundled framework files to a temporary runtime location when needed. Left-click the tray icon to configure the app; right-click it to switch modes, control Windows startup, open Settings, or exit.

Configuration is written atomically to `%LOCALAPPDATA%\SimRigMode\config.json`. Logs are retained for 14 days under `%LOCALAPPDATA%\SimRigMode\Logs`.

## First-time setup

1. Arrange the desktop monitors in Windows, then use **Desktop Mode → Capture Current Display Layout**.
2. Power/connect the rig and arrange its monitors, then capture the **Sim Rig Mode** layout.
3. In **USB Devices**, choose the desired action for each mode. Rules are saved but **inert**:
   USB switching ships disarmed and must be armed explicitly in **Settings → Safety**. Device
   changes use Windows Configuration Manager and request one narrowly scoped UAC elevation per
   batch.
4. Choose optional playback/recording defaults for each mode.
5. Optionally configure Home Assistant webhooks or REST credentials. REST tokens are protected
   with Windows DPAPI for the current user, and are only sent over https.
6. Before the first real switch, turn on **Settings → Safety → Dry run transitions**, switch
   once, and read **Settings → Diagnostics**. A dry run validates everything and calls no
   mutating API, so it shows exactly what a real switch would do.

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
referencing it can never place a runnable `SimRigMode.exe` into a test output folder.
The previous smoke-test project did exactly that, and reached the production `runas`
elevation path through `Environment.ProcessPath`; see `docs/report.md`.
`tests/Directory.Build.props` sets `UseAppHost=false` for everything under `tests/`
and **fails the build** if a runnable binary ever reappears there.

Tests must never touch real hardware. Nothing under `tests/` may call `SetDisplayConfig`,
`CM_Enable_DevNode`/`CM_Disable_DevNode`, `IPolicyConfig`, or trigger elevation.

Publishing is configured for a single, untrimmed, compressed, self-contained x64 executable using Windows App SDK 2.4.0 and stable Windows Community Toolkit settings controls. WinUI 3, COM interop, and native P/Invoke metadata are retained.

## Safety and limitations

**Displays**

- A mode switch never writes Windows' persistent display database. Every switch validates first,
  applies without `SDC_SAVE_TO_DATABASE`, re-queries to confirm the result, and rolls back if the
  result does not match. Persisting is a separate, explicit button in **Settings → Safety**.
- Indirect displays - DisplayLink, USB docks, virtual displays - are detected from their adapter
  device path and are **carried through a mode switch untouched** by default. They are neither
  removed nor re-added, because that is the operation implicated in the incident described in
  `docs/report.md`. Note that output technology does not identify them: the device at the centre
  of that incident reports LVDS.
- A display step that fails or is rolled back aborts the transition instead of being logged as a
  warning while the remaining steps run anyway.

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
  refuses to run unless it is the genuine SimRigMode executable.
- Enable/disable may still be refused by Windows, may require a reboot, or may be cancelled at
  UAC. Nothing is applied unless the whole batch validates.

**Other**

- Audio default switching uses Windows' undocumented `IPolicyConfig` COM surface; it is probed for
  availability and failures are isolated and reported.
- Home Assistant access tokens are only sent over https. Webhooks, which carry no credential, may
  use plain http.
- A failed smart-plug power-on aborts a Sim Rig transition rather than continuing to devices that
  may have no power.
