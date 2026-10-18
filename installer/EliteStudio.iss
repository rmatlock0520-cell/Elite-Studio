; ELITE STUDIO'S INSTALLER: the recipe Inno Setup (https://jrsoftware.org/isinfo.php) makes "Elite Studio Setup <version>.exe" from.
; Make-Installer.ps1 runs it; it hands over three things (/D on Inno Setup's command line):
;   AppVersion     the version, from Directory.Build.props: "1.8.0"
;   AppFiles       the folder "dotnet publish" filled with Studio and the .NET it runs on (self-contained: players need no .NET of their own)
;   WebView2Setup  Microsoft's small WebView2 Runtime installer, run only on a PC that lacks the engine Studio's window shows its pages with
; and TestBuild for a copy of the installer to try it with: "Elite Studio (test)", with an ID of its own and no shortcuts. It
; never closes or deletes anything of the player's real Studio: the Studio it closes, and whose files it offers to remove, is
; a practice copy started on %LOCALAPPDATA%\Elite Studio (test) (EliteStudio.exe --data-dir "that folder").
;
; What it does: installs Studio for every user of the PC in Program Files (Windows asks once for an administrator's yes), with a
; Start menu entry and, if wanted, a desktop shortcut; it is listed in Windows' Apps with its icon, so it is uninstalled the
; usual way. A newer setup installs over an older one, closing a Studio that is running first. Studio's own files, the
; player's settings, builds, themes and logs, are in %LOCALAPPDATA%\Elite Studio, never in Program Files: an update leaves
; them alone, and uninstalling asks whether to remove them (no, unless the player says so).

#ifndef AppVersion
  #error Run Make-Installer.ps1: it says which version this is (/DAppVersion=).
#endif
#ifndef AppFiles
  #error Run Make-Installer.ps1: it says where the published program is (/DAppFiles=).
#endif
#ifndef WebView2Setup
  #error Run Make-Installer.ps1: it fetches Microsoft's WebView2 installer (/DWebView2Setup=).
#endif

#define Home "https://github.com/rmatlock0520-cell/Elite-Studio"
#ifdef TestBuild
  #define AppName "Elite Studio (test)"
  ; never the real installer's ID: a test install and uninstall must not touch the player's real one
  #define AppIdGuid "852D1225-8EAB-4141-B109-EE3FA2C720DB"
  #define SetupName "Elite Studio Test Setup"
  #define DataFolder "Elite Studio (test)"
#else
  #define AppName "Elite Studio"
  ; Windows knows the installed program by this ID: it must never change, or a new setup would install a second copy beside the first.
  #define AppIdGuid "DF93A93A-6C55-48FE-AA94-1F969BAB77D3"
  #define SetupName "Elite Studio Setup"
  ; the player's own files, in %LOCALAPPDATA% (App\Paths.cs: DataDir)
  #define DataFolder "Elite Studio"
#endif

[Setup]
AppId={{{#AppIdGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Elite Studio
AppPublisherURL={#Home}
AppSupportURL={#Home}/issues
AppUpdatesURL={#Home}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Elite Studio
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=Elite Studio
VersionInfoDescription={#AppName} Setup

; Program Files for every user of the PC. (/CURRENTUSER on the command line installs for one user without an administrator,
; which is how the test copy is tried.)
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\EliteStudio.exe
; Studio is a 64-bit program, for Windows 10 version 2004 and later (as its build says: net10.0-windows10.0.19041.0)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; The look: Windows' own wizard, with Studio's icon and pictures, and the welcome page that shows the tall one.
WizardStyle=modern
DisableWelcomePage=no
SetupIconFile=..\src\App\studio.ico
WizardImageFile=wizard-1x.bmp,wizard-2x.bmp
WizardSmallImageFile=wizard-small-1x.bmp,wizard-small-2x.bmp
LicenseFile=..\LICENSE

; A Studio still running when its files are to be replaced is closed first (CloseStudio below); should that fail, Windows'
; own way of closing programs is offered. It is not started again by itself: the last page offers that.
CloseApplications=yes
RestartApplications=no

OutputBaseFilename={#SetupName} {#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; A log in %TEMP% (Setup Log *.txt), should an install ever need looking into.
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
#ifndef TestBuild
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#endif

[InstallDelete]
; The pages are replaced whole, so a page a newer version no longer has does not linger. Nothing of the player's is in there.
Type: filesandordirs; Name: "{app}\web"

[Files]
Source: "{#AppFiles}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The licence, and the README with the credits and the licence notices of the work Studio builds on (EDTS's BSD licence asks for them).
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
#ifndef TestBuild
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2
#endif

[Icons]
#ifndef TestBuild
Name: "{autoprograms}\Elite Studio"; Filename: "{app}\EliteStudio.exe"; Comment: "Companion app for Elite Dangerous"
Name: "{autodesktop}\Elite Studio"; Filename: "{app}\EliteStudio.exe"; Comment: "Companion app for Elite Dangerous"; Tasks: desktopicon
#endif

[Run]
#ifndef TestBuild
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Check: NeedsWebView2; Flags: waituntilterminated
#endif
; Started as the player, never as the administrator the setup ran as: Studio's files are the player's own.
Filename: "{app}\EliteStudio.exe"; Description: "{cm:LaunchProgram,Elite Studio}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  // Where the WebView2 Runtime writes its version when it is installed for the whole PC (32-bit view) or for one user.
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Version(Root: Integer): String;
begin
  if not RegQueryStringValue(Root, WebView2Key, 'pv', Result) then Result := '';
end;

// True when the engine Studio's window shows its pages with is missing (Microsoft's way of telling).
function NeedsWebView2: Boolean;
var
  Version: String;
begin
  Version := WebView2Version(HKLM32);
  if (Version = '') or (Version = '0.0.0.0') then Version := WebView2Version(HKCU);
  Result := (Version = '') or (Version = '0.0.0.0');
end;

// The running Studio leaves its address and key in this file, and deletes it as it closes.
function RunningFile: String;
begin
  Result := ExpandConstant('{localappdata}\{#DataFolder}\running.json');
end;

// One value of running.json, which Studio writes as {"port": 47820, "token": "...", "pid": 1234}.
function JsonValue(const Json, Name: String): String;
var
  At, Last: Integer;
  Rest: String;
begin
  Result := '';
  At := Pos('"' + Name + '"', Json);
  if At = 0 then Exit;
  Rest := Copy(Json, At + Length(Name) + 2, MaxInt);
  At := Pos(':', Rest);
  if At = 0 then Exit;
  Rest := Copy(Rest, At + 1, MaxInt);
  Last := 1;
  while (Last <= Length(Rest)) and (Rest[Last] <> ',') and (Rest[Last] <> '}') do Last := Last + 1;
  Rest := Trim(Copy(Rest, 1, Last - 1));
  StringChangeEx(Rest, '"', '', True);
  Result := Rest;
end;

// Asks a running Studio to close, as its own Exit does (POST /api/app/quit), and waits up to ten seconds for it to go.
procedure CloseStudio;
var
  Json: AnsiString;
  Port, Token: String;
  Http: Variant;
  Waited: Integer;
begin
  if not FileExists(RunningFile) then Exit;
  if not LoadStringFromFile(RunningFile, Json) then Exit;
  Port := JsonValue(Json, 'port');
  Token := JsonValue(Json, 'token');
  if (Port = '') or (Token = '') then Exit;
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.Open('POST', 'http://localhost:' + Port + '/api/app/quit', False);
    Http.SetTimeouts(2000, 2000, 2000, 5000);
    Http.SetRequestHeader('Content-Type', 'application/json');
    Http.SetRequestHeader('X-Studio-Token', Token);
    Http.Send('{}');
  except
    // Not running after all (the file was left behind), or it did not answer: Windows' own closing step comes next.
    Exit;
  end;
  Waited := 0;
  while FileExists(RunningFile) and (Waited < 100) do
  begin
    Sleep(100);
    Waited := Waited + 1;
  end;
  Sleep(500);   // a moment for it to let go of its files
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseStudio;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseStudio;
  Result := True;
end;

// After uninstalling: the player's own files are kept unless they say otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then Exit;
  Data := ExpandConstant('{localappdata}\{#DataFolder}');
  if not DirExists(Data) then Exit;
  if MsgBox('Also remove your Elite Studio settings, saved ship builds, themes and logs?' + #13#10#13#10 +
    'They are in ' + Data + '.' + #13#10 + 'Choose No to keep them for when you install Elite Studio again.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
