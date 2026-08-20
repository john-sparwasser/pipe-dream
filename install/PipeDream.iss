; Inno Setup script for the shipped Windows installer — the thing a user downloads, as
; opposed to install.ps1, which builds from source for developers. Both install the same
; way (per-user, no elevation, %LOCALAPPDATA%\Programs\PipeDream) so they are interchangeable
; on one machine; this one also lands in Add/Remove Programs with a real uninstaller.
;
; Built by .github/workflows/build.yml. To build it by hand, publish first, then:
;   dotnet publish src\PipeDream.csproj -c Release -r win-x64 --self-contained true `
;     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
;     -o bin\publish
;   & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" install\PipeDream.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
; Never change AppId — it is how an upgrade recognises the install it is replacing.
AppId={{03C31094-D83C-4F9E-8855-F1E9ED03DEFF}
AppName=Pipe Dream
AppVersion={#AppVersion}
AppPublisher=john-sparwasser
AppSupportURL=https://github.com/john-sparwasser/pipe-dream
; lowest + auto* = a per-user install under %LOCALAPPDATA%\Programs, no UAC prompt, which is
; also where install.ps1 puts it.
PrivilegesRequired=lowest
DefaultDirName={autopf}\PipeDream
DefaultGroupName=Pipe Dream
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\pipe-dream.ico
UninstallDisplayIcon={app}\PipeDream.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\bin\setup
; Versionless on purpose: the download link and the updater's temp file stay stable across
; releases; the version lives in AppVersion above and in the release tag.
OutputBaseFilename=PipeDream-Setup
; Tells Explorer to re-read associations once, instead of after the next reboot.
ChangesAssociations=yes

[Files]
; One self-contained executable — the editor and the ROM tools are the same binary.
Source: "..\bin\publish\PipeDream.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Pipe Dream"; Filename: "{app}\PipeDream.exe"

[Registry]
; HKA resolves to HKCU here (PrivilegesRequired=lowest), so this is the same per-user
; registration install.ps1 writes by hand. OpenWithProgids offers Pipe Dream in the
; "Open with" list without silently stealing .pdp from something the user chose.
Root: HKA; Subkey: "Software\Classes\PipeDream.Project"; ValueType: string; ValueData: "Pipe Dream Project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\PipeDream.Project\DefaultIcon"; ValueType: string; ValueData: "{app}\PipeDream.exe,0"
Root: HKA; Subkey: "Software\Classes\PipeDream.Project\shell\open\command"; ValueType: string; ValueData: """{app}\PipeDream.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.pdp"; ValueType: string; ValueData: "PipeDream.Project"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.pdp\OpenWithProgids"; ValueType: binary; ValueName: "PipeDream.Project"; ValueData: ""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PipeDream.exe"; Description: "Launch Pipe Dream"; Flags: nowait postinstall skipifsilent
; The in-app updater installs silently, which skips the entry above — so it passes /relaunch=1
; to get the new build started again. CI's silent install check does not pass it, so the test
; stays headless.
Filename: "{app}\PipeDream.exe"; Flags: nowait; Check: WantsRelaunch

[Code]
function WantsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;
