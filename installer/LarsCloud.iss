#define MyAppName "Lar's Cloud"
#define MyAppPublisher "Lar's Cloud"
#define MyAppExeName "LarsCloud.exe"
#define ProjectRoot SourcePath + "\.."
#define MyAppVersion GetFileVersion(ProjectRoot + "\artifacts\publish\LarsCloud.exe")

[Setup]
AppId={{B1DC0164-C516-4CE6-83BB-8BDF4D34B175}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LarsCloud
DefaultGroupName=Lar's Cloud
DisableProgramGroupPage=yes
OutputDir={#ProjectRoot}\artifacts\installer
OutputBaseFilename=LarsCloud_Setup
SetupIconFile={#ProjectRoot}\src\LarsCloud.App\Assets\app.ico
UninstallDisplayIcon={app}\Assets\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.10240
CloseApplications=yes
ForceCloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Automatic Google Drive backup for Windows
VersionInfoCompany={#MyAppPublisher}
SetupLogging=yes

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Створити ярлик на робочому столі"; GroupDescription: "Додаткові ярлики:"; Flags: checkedonce
Name: "autostart"; Description: "Запускати Lar's Cloud разом із Windows"; GroupDescription: "Автоматична робота:"; Flags: checkedonce

[Files]
Source: "{#ProjectRoot}\artifacts\publish\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\artifacts\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Lar's Cloud"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Lar's Cloud"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LarsCloud"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустити Lar's Cloud"; WorkingDir: "{app}"; Flags: nowait postinstall runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
