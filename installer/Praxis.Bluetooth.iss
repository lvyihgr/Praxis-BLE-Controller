#define MyAppName "Praxis Bluetooth"
#define MyAppPublisher "jiangshaozhuang"
#define MyAppURL "https://github.com/jiangshaozhuang/Praxis-BLE-Controller"
#define MyAppExeName "Praxis.Bluetooth.exe"
#define MyAppVersion GetEnv("PRAXIS_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "1.2.0"
#endif

[Setup]
AppId={{5DBFC9ED-7BAF-4C52-B561-0D3187F7F528}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppMutex=PraxisBluetoothCore_5DBFC9ED7BAF4C52B5610D3187F7F528
DefaultDirName={localappdata}\Programs\Praxis Bluetooth
DefaultGroupName=Praxis Bluetooth
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=Praxis-Bluetooth-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\Praxis.Core.Bluetooth\praxis.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
ChangesEnvironment=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Praxis Bluetooth installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Launch at Windows sign-in"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\bluetooth-win-x64\*"; DestDir: "{app}"; Excludes: "data\praxis.json,data\pairing.json,data\logs\*,data\backups\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\bluetooth-win-x64\data\praxis.json"; DestDir: "{app}\data"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\Praxis Bluetooth"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall Praxis Bluetooth"; Filename: "{uninstallexe}"
Name: "{userstartup}\Praxis Bluetooth"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startup
Name: "{userdesktop}\Praxis Bluetooth"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Praxis Bluetooth"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\data\pairing.json"
Type: files; Name: "{app}\data\pairing-*.json"
Type: files; Name: "{app}\data\devices.json"
Type: files; Name: "{app}\data\*.tmp"
Type: filesandordirs; Name: "{app}\data\logs"
Type: filesandordirs; Name: "{app}\data\backups"
Type: dirifempty; Name: "{app}\data"
