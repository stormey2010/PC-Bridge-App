#define MyAppName "PC Bridge Agent"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "PC Bridge Agent Contributors"
#define PublishRoot "..\artifacts\publish"

[Setup]
AppId={{D90C7654-7AE3-46F8-BE4E-C90B9FCE839A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PC Bridge Agent
DefaultGroupName=PC Bridge Agent
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=PC-Bridge-Agent-{#MyAppVersion}-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=PC Bridge Agent
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "service"; Description: "Install the background Windows service"; GroupDescription: "Agent mode:"; Flags: exclusive checkedonce
Name: "startup"; Description: "Start the desktop companion when I sign in"; GroupDescription: "Startup:"; Flags: checkedonce
Name: "launch"; Description: "Open PC Bridge Agent after installation"; GroupDescription: "Finish:"; Flags: checkedonce

[Files]
Source: "{#PublishRoot}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PC Bridge Agent"; Filename: "{app}\PcBridge.Agent.exe"
Name: "{autodesktop}\PC Bridge Agent"; Filename: "{app}\PcBridge.Agent.exe"; Tasks: startup

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PC Bridge Agent"; ValueData: """{app}\PcBridge.Agent.exe"" --tray"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create ""PC Bridge Agent"" binPath= """"{app}\service\PcBridge.Agent.Service.exe"""" start= auto DisplayName= ""PC Bridge Agent"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "description ""PC Bridge Agent"" ""Secure outbound Windows-to-Home-Assistant bridge"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "failure ""PC Bridge Agent"" reset= 86400 actions= restart/5000/restart/30000/restart/60000"; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{sys}\sc.exe"; Parameters: "start ""PC Bridge Agent"""; Flags: runhidden waituntilterminated; Tasks: service
Filename: "{app}\PcBridge.Agent.exe"; Description: "Open PC Bridge Agent"; Flags: nowait postinstall skipifsilent; Tasks: launch

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""PC Bridge Agent"""; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete ""PC Bridge Agent"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var DataPath: string;
begin
  if CurUninstallStep = usPostUninstall then begin
    DataPath := ExpandConstant('{commonappdata}\PC Bridge Agent');
    if DirExists(DataPath) and (MsgBox('Remove PC Bridge settings, protected credential, and local logs?', mbConfirmation, MB_YESNO) = IDYES) then
      DelTree(DataPath, True, True, True);
  end;
end;
