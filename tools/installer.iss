[Setup]
AppId={{259E5824-71DE-4C63-BCBD-FB018532D421}
AppName=OpenKuro
AppVersion={#AppVersion}
AppPublisher=OpenKuro contributors
AppPublisherURL=https://github.com/asharaamer0/OpenKuro
DefaultDirName={localappdata}\Programs\OpenKuro
DefaultGroupName=OpenKuro
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ChangesAssociations=yes
SetupIconFile=..\src\Kuro\Assets\Kuro.ico
UninstallDisplayIcon={app}\Kuro.exe
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=OpenKuro-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "*.bak,*.pdb,*.lib"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: "desktop"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\OpenKuro"; Filename: "{app}\Kuro.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\OpenKuro"; Filename: "{app}\Kuro.exe"; WorkingDir: "{app}"; Tasks: desktop

[Registry]
Root: HKCU; Subkey: "Software\Classes\.kr"; ValueType: string; ValueData: "OpenKuro.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.kuro"; ValueType: string; ValueData: "OpenKuro.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.kr\OpenWithProgids"; ValueType: none; ValueName: "OpenKuro.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.kuro\OpenWithProgids"; ValueType: none; ValueName: "OpenKuro.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\OpenKuro.Project"; ValueType: string; ValueData: "Kuro project"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\OpenKuro.Project\DefaultIcon"; ValueType: string; ValueData: "{app}\Kuro.exe,0"
Root: HKCU; Subkey: "Software\Classes\OpenKuro.Project\shell\open\command"; ValueType: string; ValueData: """{app}\Kuro.exe"" ""%1"""
Root: HKCU; Subkey: "Software\OpenKuro\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "OpenKuro"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\OpenKuro\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Native Windows video and motion editor"
Root: HKCU; Subkey: "Software\OpenKuro\Capabilities\FileAssociations"; ValueType: string; ValueName: ".kr"; ValueData: "OpenKuro.Project"
Root: HKCU; Subkey: "Software\OpenKuro\Capabilities\FileAssociations"; ValueType: string; ValueName: ".kuro"; ValueData: "OpenKuro.Project"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "OpenKuro"; ValueData: "Software\OpenKuro\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{localappdata}\Microsoft\WindowsApps\winget.exe"; Parameters: "install --id Gyan.FFmpeg --exact --accept-package-agreements --accept-source-agreements"; Description: "Install FFmpeg for media import and export"; Flags: postinstall skipifsilent; Check: NeedsFfmpeg
Filename: "{app}\Kuro.exe"; Description: "Launch OpenKuro"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
function NeedsFfmpeg: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe')) and
    (FileSearch('ffmpeg.exe', GetEnv('PATH')) = '') and
    not FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe'));
end;
