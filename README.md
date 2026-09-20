# SimRigMode

SimRigMode is a Windows 11 tray utility that switches a PC between captured Desktop and Sim Rig hardware/workspace states. Its settings experience uses WinUI 3, Windows App SDK, Fluent Design, Mica, NavigationView, and Windows Community Toolkit settings controls.

## Run

Copy only `dist\SimRigMode.exe` to any folder and launch it. No .NET runtime, Windows App SDK runtime, installer, scripts, DLLs, or adjacent configuration files are required. The unpackaged single-file application extracts its bundled framework files to a temporary runtime location when needed. Left-click the tray icon to configure the app; right-click it to switch modes, control Windows startup, open Settings, or exit.

Configuration is written atomically to `%LOCALAPPDATA%\SimRigMode\config.json`. Logs are retained for 14 days under `%LOCALAPPDATA%\SimRigMode\Logs`.

## First-time setup

1. Arrange the desktop monitors in Windows, then use **Desktop Mode → Capture Current Display Layout**.
2. Power/connect the rig and arrange its monitors, then capture the **Sim Rig Mode** layout.
3. In **USB Devices**, choose the desired action for each mode. Device changes use Windows Configuration Manager and request one narrowly scoped UAC elevation per device batch.
4. Choose optional playback/recording defaults for each mode.
5. Optionally configure Home Assistant webhooks or REST credentials. REST tokens are protected with Windows DPAPI for the current user.

Display captures preserve native adapter/source/target IDs, persistent monitor device paths, exact timing rationals, resolution, refresh, orientation, position, and topology. There is no refresh-rate cap.

## Build and test

```powershell
dotnet restore SimRigMode.csproj
dotnet build SimRigMode.csproj -c Release
dotnet run --project tests\SimRigMode.SmokeTests\SimRigMode.SmokeTests.csproj -c Release
dotnet publish SimRigMode.csproj -c Release -r win-x64 --self-contained true -o dist
```

Publishing is configured for a single, untrimmed, compressed, self-contained x64 executable using Windows App SDK 2.4.0 and stable Windows Community Toolkit settings controls. WinUI 3, COM interop, and native P/Invoke metadata are retained.

## Safety and limitations

- Keyboard, mouse, system-class, USB host-controller, and root-hub rules require an explicit strong-warning acknowledgement.
- USB device enable/disable may be refused by Windows, may require a reboot, or may be cancelled at UAC. The remaining safe transition steps continue.
- Audio default switching uses Windows' undocumented `IPolicyConfig` COM surface behind `AudioService`; failures are isolated and reported.
- A captured display layout needs the relevant GPU outputs/displays to be available. A missing device produces a visible warning without terminating the tray application.
