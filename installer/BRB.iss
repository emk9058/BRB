#define MyAppName "BRB"
#define MyAppVersion "1.0.0"
#define MyAppExeName "BRB.exe"

[Setup]
AppId={{7F3C1A92-6E4D-4B8A-9C15-2D8E4F0B6A31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=BRB
DefaultDirName={localappdata}\BRB
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=BRB-Setup
SetupIconFile=..\host\Assets\brb.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\BRB"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\BRB"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Open BRB"; Flags: nowait postinstall skipifsilent
