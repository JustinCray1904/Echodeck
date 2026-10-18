; Inno Setup script. The CI workflow passes AppVersion, SourceDir and IconFile on the command line;
; the defaults below let you also compile it by hand after running `dotnet publish -o ..\publish`.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef IconFile
  #define IconFile "..\DeefxultMediaPlayer\Assets\app.ico"
#endif

#define AppName "Deefxult Media Player"
#define AppExe  "DeefxultMediaPlayer.exe"

[Setup]
AppId={{8F2B6E34-5C1D-4A7B-9E0F-3D6A1C7B2E94}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Deefxult
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=DeefxultMediaPlayer-Setup-{#AppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Tells Explorer to refresh file associations after install/uninstall.
ChangesAssociations=yes
; Per-user install by default (no admin prompt); the wizard offers "all users" if the user wants it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Register {#AppName} for video files (MP4, MKV, AVI, MOV, WMV, WEBM, M4V)"; GroupDescription: "File associations:"

[Files]
; Everything from dotnet publish, including the libvlc\win-x64 folder LibVLC needs at runtime.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; HKA = HKCU for a per-user install, HKLM for an all-users install.
; --- ProgID: what Explorer runs when a registered video is opened ("%1" is the file path, which the app reads from its command line)
Root: HKA; Subkey: "Software\Classes\DeefxultMediaPlayer.Video"; ValueType: string; ValueName: ""; ValueData: "{#AppName} video file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DeefxultMediaPlayer.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DeefxultMediaPlayer.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

; --- Application registration: shows up in "Open with" and Settings > Apps > Default apps
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Video player with a built-in encrypted vault"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "DeefxultMediaPlayer.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "DeefxultMediaPlayer"; ValueData: "Software\Clients\Media\DeefxultMediaPlayer\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

; --- Offer the player for each extension without overriding whatever the user currently uses
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "DeefxultMediaPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Windows 10/11 won't let an installer silently take over the default player; this opens the page where the user confirms it.
Filename: "ms-settings:defaultapps"; Description: "Open Windows Default apps to make {#AppName} the default video player"; Flags: shellexec postinstall skipifsilent unchecked; Tasks: fileassoc

; NOTE: there is intentionally no [UninstallDelete] for the vault. Uninstalling the app never touches
; %USERPROFILE%\.SecureVideoVault or %LOCALAPPDATA%\DeefxultMediaPlayer (the key and PIN record).
