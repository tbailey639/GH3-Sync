; Script generated for Inno Setup 7
; GH3-Sync: Native Windows 11 P2P Song Synchronizer, Friends Network & ONOGAMI AI Song Creator for BetterGH3

#define MyAppName "GH3-Sync"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Mad Scientist (tbailey639)"
#define MyAppURL "https://github.com/tbailey639/GH3-Sync"
#define MyAppExeName "GH3-Sync.exe"

[Setup]
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
SetupIconFile=Assets\app_icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "Assets\app_icon.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "Assets\gh3_sync_logo.png"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "dist\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "tools\compiler\*"; DestDir: "{app}\tools\compiler"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app_icon.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app_icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
