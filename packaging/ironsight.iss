; Inno Setup script for the Windows installer.
; Compile with: ISCC /DVersion=1.2.3 /DPublish=path\to\publish /DOutputDir=path packaging\ironsight.iss

[Setup]
AppId={{7E2A9C5B-4C1D-4A9E-9F3B-6D2E8A41C750}
AppName=IRONSIGHT
AppVersion={#Version}
AppPublisher=Helge Sverre
AppPublisherURL=https://fsharp-of-duty.fly.dev
DefaultDirName={autopf}\IRONSIGHT
DefaultGroupName=IRONSIGHT
DisableProgramGroupPage=yes
; Per-user install: no admin prompt, lands under LocalAppData\Programs.
PrivilegesRequired=lowest
OutputBaseFilename=ironsight-win-x64-setup
OutputDir={#OutputDir}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#SourcePath}\icon.ico
UninstallDisplayIcon={app}\Ironsight.exe

[Files]
Source: "{#Publish}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: desktopicon; Description: "Create a desktop icon"; Flags: unchecked

[Icons]
Name: "{autoprograms}\IRONSIGHT"; Filename: "{app}\Ironsight.exe"
Name: "{autodesktop}\IRONSIGHT"; Filename: "{app}\Ironsight.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Ironsight.exe"; Description: "Launch IRONSIGHT"; Flags: nowait postinstall skipifsilent
