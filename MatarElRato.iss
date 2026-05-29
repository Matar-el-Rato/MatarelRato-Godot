; ─────────────────────────────────────────────────────────────────────────────
; Inno Setup script — "Matar el Rato" install wizard
;
; HOW TO BUILD THE INSTALLER:
;   1. In Godot: Project > Export > "game" (Windows Desktop) > Export Project,
;      saving to the  build\  folder next to this file (it produces
;      build\MatarElRato.exe plus the bundled .NET runtime/assemblies).
;      The .pck is already embedded in the exe (binary_format/embed_pck = true).
;   2. Install Inno Setup (https://jrsoftware.org/isdl.php), open this file,
;      and press Compile  — or from a terminal:  ISCC.exe MatarElRato.iss
;   3. The wizard installer is written to  Output\MatarElRato-Setup.exe
; ─────────────────────────────────────────────────────────────────────────────

#define MyAppName      "Matar el Rato"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "David Garcia, Eric Menaya & Xavi Guillamon"
#define MyAppExeName   "MatarElRato.exe"
; Export output lives one level up (export_path = "../build/MatarElRato.exe").
#define MyBuildDir     "..\build"

[Setup]
; AppId uniquely identifies the app for upgrades/uninstall — keep it stable across versions.
AppId={{8F3A1C7E-2B4D-4E6A-9C1F-7A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=auto
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=MatarElRato-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Godot exports a 64-bit binary. ("x64compatible" needs Inno Setup 6.3+;
; on older Inno use "x64" instead for both lines below.)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Default to per-machine (Program Files); the user can switch to a per-user
; install on the privileges page if they lack admin rights.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; Icon for the wizard window + Add/Remove Programs entry.
SetupIconFile=Assets\Icons\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Grab the whole export output: the exe (with embedded game data) and every
; bundled .NET file/folder. Skip debug symbols and the console wrapper for a
; clean release install.
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,*.console.exe"

[Icons]
Name: "{group}\{#MyAppName}";                          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}";    Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";                    Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
