; Pitlane's installer: a standard setup wizard, built with Inno Setup 6.
;
; 1. Publish the app:
;      dotnet publish SimRigMode.csproj -c Release -r win-x64 --self-contained true -o dist
; 2. Build the setup:
;      ISCC installer\Pitlane.iss
;    It lands in dist\installer\PitlaneSetup-<version>.exe.
;
; Installs for the current user, without an administrator prompt, into a folder of their choice
; (by default %LOCALAPPDATA%\Programs\Pitlane), with a Start menu shortcut and an uninstaller in
; Windows' Apps list. Settings and logs in %LOCALAPPDATA%\Pitlane are the user's and stay put.

#ifndef AppVersion
  ; Keep in step with <Version> in SimRigMode.csproj.
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change the AppId: it is how a new version finds and replaces the installed one.
AppId={{3CDAB13C-1FF4-47FA-B9AA-EEDA22FF206B}
AppName=Pitlane
AppVersion={#AppVersion}
AppVerName=Pitlane {#AppVersion}
AppPublisher=TheOriUHD
AppPublisherURL=https://github.com/TheOriUHD/Pitlane
AppSupportURL=https://github.com/TheOriUHD/Pitlane/issues
AppUpdatesURL=https://github.com/TheOriUHD/Pitlane/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Pitlane
VersionInfoDescription=Pitlane setup
DefaultDirName={autopf}\Pitlane
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; A running Pitlane is asked to close before its files are replaced or removed.
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\Assets\Pitlane.ico
UninstallDisplayIcon={app}\Pitlane.exe
UninstallDisplayName=Pitlane
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist\installer
OutputBaseFilename=PitlaneSetup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\Pitlane.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Pitlane"; Filename: "{app}\Pitlane.exe"

[Run]
Filename: "{app}\Pitlane.exe"; Description: "{cm:LaunchProgram,Pitlane}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ Start with Windows is Pitlane's own setting: a Run value naming the Pitlane.exe that turned it on.
  Uninstalling removes it only when it names the copy being removed, never another one. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not RegQueryStringValue(HKCU, RunKey, 'Pitlane', Command) then Exit;
  if Pos(Lowercase(ExpandConstant('{app}\Pitlane.exe')), Lowercase(Command)) > 0 then
    RegDeleteValue(HKCU, RunKey, 'Pitlane');
end;
