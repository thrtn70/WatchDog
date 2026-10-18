; WatchDog Installer — Inno Setup 6 Script
; Compiles into a single WatchDog-Setup.exe that installs the app,
; OBS runtime, FFmpeg, and .NET runtime (self-contained publish).
;
; Build:
;   ISCC.exe /DAppVersion=1.3.0 watchdog.iss
; Or via the build script:
;   .\tools\package.ps1

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Upgrades use Inno Setup's same-AppId, same-mode in-place installation.
; Never execute a registry-supplied uninstaller from elevated setup.
; No settings/media deletion or legacy per-user migration is performed here.
[Setup]
AppId={{E870A91B-7F6E-4AFE-8211-50EF7BC47C92}
AppName=WatchDog
AppVersion={#AppVersion}
AppVerName=WatchDog {#AppVersion}
AppPublisher=thrtn70
AppPublisherURL=https://github.com/thrtn70/WatchDog
AppSupportURL=https://github.com/thrtn70/WatchDog/issues
DefaultDirName={autopf}\WatchDog
DefaultGroupName=WatchDog
OutputDir=Output
OutputBaseFilename=WatchDog-Setup
SetupIconFile=..\src\WatchDog.App\Resources\WatchDog.ico
UninstallDisplayIcon={app}\WatchDog.App.exe
UninstallDisplayName=WatchDog
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=force
CloseApplicationsFilter=WatchDog.App.exe
RestartApplications=no
WizardStyle=modern
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=thrtn70
VersionInfoProductName=WatchDog
VersionInfoDescription=Lightweight game clipping software for Windows

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; All published output (app + .NET runtime + OBS + FFmpeg)
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
; Start Menu
Name: "{group}\WatchDog"; Filename: "{app}\WatchDog.App.exe"; Comment: "Launch WatchDog"
Name: "{group}\Uninstall WatchDog"; Filename: "{uninstallexe}"
; Desktop (optional task)
Name: "{autodesktop}\WatchDog"; Filename: "{app}\WatchDog.App.exe"; Tasks: desktopicon; Comment: "Launch WatchDog"

[Run]
; Offer to launch after install (skipped in silent mode)
Filename: "{app}\WatchDog.App.exe"; Description: "Launch WatchDog"; Flags: nowait postinstall skipifsilent
