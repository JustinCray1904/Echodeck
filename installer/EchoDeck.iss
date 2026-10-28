; EchoDeck installer (Inno Setup 6). The CI workflow passes AppVersion, SourceDir and IconFile on the command line;
; the defaults below also let you compile it by hand after `dotnet publish -c Release -o ..\publish`.

#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef IconFile
  #define IconFile "..\EchoDeck\Assets\app.ico"
#endif

#define AppName "EchoDeck"
#define AppExe  "EchoDeck.exe"

[Setup]
AppId={{3C7A9E52-8B41-4D6F-A1E8-5F2D90B7C614}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Deefxult
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=EchoDeck-Setup-{#AppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExe}
; Smallest possible installer
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Tells Explorer to refresh file associations after install/uninstall.
ChangesAssociations=yes
; Per-user install by default (no admin prompt); the wizard offers "all users" if wanted.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Register {#AppName} for video and audio files (MP4, MKV, AVI, MP3, FLAC ...)"; GroupDescription: "File associations:"

[Files]
; Everything from dotnet publish, including the libvlc\win-x64 folder LibVLC needs at runtime.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; HKA = HKCU for a per-user install, HKLM for an all-users install.

; --- ProgIDs: what Explorer runs when a registered file is opened ("%1" = file path, read from the command line)

Root: HKA; Subkey: "Software\Classes\EchoDeck.Video"; ValueType: string; ValueName: ""; ValueData: "{#AppName} video file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\EchoDeck.Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\EchoDeck.Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\EchoDeck.Audio"; ValueType: string; ValueName: ""; ValueData: "{#AppName} audio file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\EchoDeck.Audio\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\EchoDeck.Audio\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

; --- Application registration: shows up in "Open with" for every supported extension, and in Settings > Default apps
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".aac"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ogg"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".opus"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wma"; ValueData: ""; Tasks: fileassoc

Root: HKA; Subkey: "Software\Clients\Media\EchoDeck"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Modern video and music player with an encrypted Anonymous Mode"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "EchoDeck.Video"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".opus"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Clients\Media\EchoDeck\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wma"; ValueData: "EchoDeck.Audio"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "EchoDeck"; ValueData: "Software\Clients\Media\EchoDeck\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

; --- Offer EchoDeck for each extension (OpenWithProgids) without overriding whatever the user currently uses
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Video"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: string; ValueName: "EchoDeck.Audio"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Windows 10/11 never lets an installer silently take over defaults; open the page where the user can confirm it.
Filename: "ms-settings:defaultapps"; Description: "Open Windows Default apps to make {#AppName} the default player"; Flags: shellexec postinstall skipifsilent unchecked; Tasks: fileassoc
