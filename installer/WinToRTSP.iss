; =========================================================================
; WinToRTSP - Inno Setup 6 Script
; Generates a lightweight, secure Windows installer
; =========================================================================

#define MyAppName "WinToRTSP"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "WinToRTSP Open Source"
#define MyAppURL "https://github.com/WinToRTSP/WinToRTSP"
#define MyAppExeName "WinToRTSP.exe"

[Setup]
AppId={{8B2DF5B2-1678-43B8-8BF4-22F2625CE50A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish\installer
OutputBaseFilename=WinToRTSP-Setup-v{#MyAppVersion}
SetupIconFile=..\Resources\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupboot"; Description: "Start WinToRTSP automatically when Windows boots"; GroupDescription: "System Integration:"

[Files]
Source: "..\publish\portable\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Resources\app.ico"; DestDir: "{app}\Resources"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: isreadme ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\app.ico"; Tasks: desktopicon

[Registry]
; Auto-start on boot via HKCU Run key
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startupboot

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WinToRTSP"
Type: files; Name: "{app}\Resources\app.ico"
Type: dirifempty; Name: "{app}\Resources"
Type: dirifempty; Name: "{app}"

[Code]
// Gracefully close any running instances before setup begins
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
