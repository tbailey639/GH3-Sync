; Script generated for Inno Setup 7
; GH3-Sync: Complete P2P Song Synchronizer, Friends Network & ONOGAMI Song Creator for BetterGH3

#define MyAppName "GH3-Sync"
#define MyAppVersion "1.3.0-beta.3"
#define MyAppPublisher "Antigravity & ONOGAMI"
#define MyAppURL "https://github.com/tbailey639/GH3-Sync"
#define MyAppExeName "GH3-Sync.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
AppId={{E0D77F0F-E384-4BE6-BE0B-D8193B1DA090}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=dist
OutputBaseFilename=GH3-Sync-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "dist\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
