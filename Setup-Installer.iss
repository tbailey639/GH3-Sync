; Script generated for Inno Setup 7
; GH3-Sync: Guitar Hero III Custom Song Creator, Clone Hero Converter & P2P Synchronizer

#define MyAppName "GH3-Sync"
#define MyAppVersion "1.4.1"
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
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
Name: "{app}\tools"; Permissions: users-modify
Name: "{app}\tools\compiler"; Permissions: users-modify
Name: "{app}\tools\compiler\Backups"; Permissions: users-modify
Name: "{app}\tools\compiler\Backups\GH3"; Permissions: users-modify
Name: "{app}\tools\compiler\Replacements"; Permissions: users-modify

[Files]
Source: "dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Assets\app_icon.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "Assets\gh3_sync_logo.png"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "dist\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "tools\compiler\*"; DestDir: "{app}\tools\compiler"; Excludes: "Backups\*,QBDebug\*_user.txt"; Flags: ignoreversion recursesubdirs createallsubdirs; Permissions: users-modify

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app_icon.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app_icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall runasoriginaluser skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifnotsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataRoot: string;
  AiToolsDir: string;
  KeepAi: Boolean;
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    AppDataRoot := ExpandConstant('{userappdata}\GH3-Sync');
    AiToolsDir := AppDataRoot + '\tools\ai';
    KeepAi := True;

    if DirExists(AiToolsDir) then
    begin
      if MsgBox('Do you want to KEEP the downloaded Stem Separator & Portable Python tools (~1 GB) in:' + #13#10 +
                AiToolsDir + #13#10#13#10 +
                'Click YES to keep them (recommended if you plan to reinstall or update later).' + #13#10 +
                'Click NO to delete them and free disk space.',
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        KeepAi := False;
        DelTree(AiToolsDir, True, True, True);
      end;
    end;

    if DirExists(AppDataRoot) then
    begin
      if MsgBox('Do you want to KEEP your GH3-Sync settings, Trusted Friends list, and Career Save backups in:' + #13#10 +
                AppDataRoot + #13#10#13#10 +
                'Click YES to preserve your settings and save backups.' + #13#10 +
                'Click NO to remove them.',
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        if not KeepAi then
        begin
          DelTree(AppDataRoot, True, True, True);
        end
        else
        begin
          DeleteFile(AppDataRoot + '\config.json');
          DeleteFile(AppDataRoot + '\debug.log');
          DeleteFile(AppDataRoot + '\song_meta_cache.json');
          DeleteFile(AppDataRoot + '\temp_session.json');
          DeleteFile(AppDataRoot + '\temp_session_customs.pak.xen');
          DelTree(AppDataRoot + '\SaveBackups', True, True, True);
          DelTree(AppDataRoot + '\p2p_root', True, True, True);
          DelTree(AppDataRoot + '\compiler_work', True, True, True);
        end;
      end;
    end;
  end;
end;
