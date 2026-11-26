#ifndef MyAppVersion
  #define MyAppVersion "1.2.2"
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

[Run]
Filename: "{app}\HUDMind.Native.exe"; Description: "{cm:LaunchProgram,HUDMind}"; Flags: nowait postinstall skipifsilent
