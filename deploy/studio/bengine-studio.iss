; Windows installer (Inno Setup 6): BengineStudio-Setup-<version>-x64.exe. Installs per user without
; admin rights (%LOCALAPPDATA%\Programs\Bengine Studio), adds a Start menu entry, an optional desktop
; shortcut, and an uninstaller. No .NET runtime is needed.
;   iscc /DVersion=1.0.0 /DSource=<publish folder> /DOutputDir=<output> deploy\studio\bengine-studio.iss
#ifndef Version
  #define Version "0.0.0"
#endif

[Setup]
AppId={{B3E5A7C1-4F2D-4C8E-9A61-7D2E0B5C3F18}
AppName=Bengine Studio
AppVersion={#Version}
AppPublisher=Bengine
AppPublisherURL=https://github.com/Nodirbek-Abdulaxadov/bengine
DefaultDirName={localappdata}\Programs\Bengine Studio
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=BengineStudio-Setup-{#Version}-x64
SetupIconFile=bengine-studio.ico
UninstallDisplayIcon={app}\bengine-studio.exe
UninstallDisplayName=Bengine Studio
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Source}\bengine-studio.exe"; DestDir: "{app}"; Flags: ignoreversion
; Native libraries the single-file host could not embed (if any).
Source: "{#Source}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\Bengine Studio"; Filename: "{app}\bengine-studio.exe"
Name: "{autodesktop}\Bengine Studio"; Filename: "{app}\bengine-studio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\bengine-studio.exe"; Description: "{cm:LaunchProgram,Bengine Studio}"; Flags: nowait postinstall skipifsilent
