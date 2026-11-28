#ifndef MyAppVersion
  #define MyAppVersion "1.8.0"
#endif
#ifndef InputExe
  #define InputExe "..\dist\standalone-win-x64\HUDMind.Native.exe"
#endif

[Setup]
AppId={{8E9B3F4C-2B7A-4F6D-9B2B-7B68A6E4D111}
AppName=HUDMind
AppVersion={#MyAppVersion}
AppVerName=HUDMind v{#MyAppVersion}
AppPublisher=Vgarzaniti
AppPublisherURL=https://github.com/Vgarzaniti/HUDmind
AppSupportURL=https://github.com/Vgarzaniti/HUDmind/releases
DefaultDirName={autopf}\HUDMind
DefaultGroupName=HUDMind
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=HUDMind-Setup-win-x64
SetupIconFile=..\HUDMind.App\Assets\hudmind.ico
UninstallDisplayIcon={app}\HUDMind.Native.exe
UninstallDisplayName=HUDMind
AppMutex=HUDMind_Native_SingleInstance_Mutex
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#InputExe}"; DestDir: "{app}"; DestName: "HUDMind.Native.exe"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\HUDMind"; Filename: "{app}\HUDMind.Native.exe"; AppUserModelID: "HUDMind.Native.Desktop"; Comment: "HUDMind AI eSports Coach (0% GPU)"
Name: "{userdesktop}\HUDMind"; Filename: "{app}\HUDMind.Native.exe"; AppUserModelID: "HUDMind.Native.Desktop"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\.hudmind"; ValueType: string; ValueName: ""; ValueData: "HUDMind.BackupArchive"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\HUDMind.BackupArchive"; ValueType: string; ValueName: ""; ValueData: "HUDMind Encrypted Profile Backup"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\HUDMind.BackupArchive\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\HUDMind.Native.exe,0"
Root: HKCU; Subkey: "Software\Classes\HUDMind.BackupArchive\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HUDMind.Native.exe"" ""%1"""

[Run]
Filename: "{app}\HUDMind.Native.exe"; Description: "{cm:LaunchProgram,HUDMind}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\HUDMind.Native.exe"; Flags: nowait skipifdoesntexist; Check: WantsAutoRelaunch

[Code]
function WantsAutoRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;
