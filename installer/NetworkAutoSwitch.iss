; Network Auto Switch: installer (Inno Setup 6). Build steps are in docs/RELEASING.md.
;
;   dotnet publish src\Tray -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist\publish
;   ISCC.exe /DAppVersion=0.1.0 installer\NetworkAutoSwitch.iss        ->  dist\installer\NetworkAutoSwitch-Setup-0.1.0.exe
;
; The version is NEVER typed in this file: it is passed in by the build (it comes from Directory.Build.props).
; /DTestInstall builds a variant for automated checks: per-user, no login task, and it never touches a running copy of the app.

#ifndef AppVersion
  #error Pass the version, for example: ISCC.exe /DAppVersion=0.1.0 installer\NetworkAutoSwitch.iss
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

#define AppName     "Network Auto Switch"
#define AppExe      "NetworkAutoSwitchTray.exe"
#define TaskName    "NetworkAutoSwitchTray"
#define RepoUrl     "https://github.com/Sijan079/NetworkAutoSwitch"

; Windows file properties need four plain numbers: "0.2.0-beta.1" -> 0.2.0.0
#define Dash Pos("-", AppVersion)
#if Dash > 0
  #define CoreVersion Copy(AppVersion, 1, Dash - 1)
#else
  #define CoreVersion AppVersion
#endif

[Setup]
; The AppId identifies this program to Windows FOREVER: a new version with the same id upgrades the old one in place. Never change it.
AppId={{F526DCD2-54FA-4F51-93CF-57240F6AD155}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Sijan079
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#CoreVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=NetworkAutoSwitch-Setup-{#AppVersion}
SetupIconFile=..\src\Tray\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=no
RestartApplications=no
#ifdef TestInstall
PrivilegesRequired=lowest
#else
; the app changes network settings and starts elevated: installing for all users needs administrator rights
PrivilegesRequired=admin
#endif

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when I sign in (recommended)"; GroupDescription: "Startup:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
#ifndef TestInstall
; "Start when I sign in": a scheduled task that runs the app with the highest rights, 30 s after sign-in (so a slow Wi-Fi connect is not taken for an outage).
; Running as a task is what lets it start already elevated, without a UAC prompt at every sign-in.
Filename: "powershell.exe"; Flags: runhidden waituntilterminated; Tasks: startup; StatusMsg: "Setting up start at sign-in..."; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name; $a=New-ScheduledTaskAction -Execute '{app}\{#AppExe}' -WorkingDirectory '{app}'; $t=New-ScheduledTaskTrigger -AtLogOn -User $u; $t.Delay='PT30S'; $p=New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; Register-ScheduledTask -TaskName {#TaskName} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"""
; the box was unticked on an upgrade: remove the task an earlier install created
Filename: "powershell.exe"; Flags: runhidden waituntilterminated; Check: not WizardIsTaskSelected('startup'); \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Unregister-ScheduledTask -TaskName {#TaskName} -Confirm:$false -ErrorAction SilentlyContinue"""
#endif
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
#ifndef TestInstall
; (these run in reverse order) 2nd: remove the sign-in task, 1st: put the network settings back exactly as the app found them
Filename: "powershell.exe"; Flags: runhidden; RunOnceId: "RemoveTask"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Unregister-ScheduledTask -TaskName {#TaskName} -Confirm:$false -ErrorAction SilentlyContinue"""
Filename: "{app}\{#AppExe}"; Parameters: "--restore"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreNetwork"
#endif

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F526DCD2-54FA-4F51-93CF-57240F6AD155}_is1';   // must match AppId above

// ---------- SemVer 2.0 comparison (same rules as AppInfo.Compare in the app) ----------
procedure SplitVersion(const V: String; var Core, Pre: String);
var P: Integer; S: String;
begin
  S := V;
  P := Pos('+', S); if P > 0 then S := Copy(S, 1, P - 1);          // build metadata is ignored
  P := Pos('-', S);
  if P > 0 then begin Core := Copy(S, 1, P - 1); Pre := Copy(S, P + 1, 1000); end
  else begin Core := S; Pre := ''; end;
end;

function NextPart(var S: String): String;
var P: Integer;
begin
  P := Pos('.', S);
  if P > 0 then begin Result := Copy(S, 1, P - 1); S := Copy(S, P + 1, 1000); end
  else begin Result := S; S := ''; end;
end;

function IsNumber(const S: String): Boolean;
var I: Integer;
begin
  Result := S <> '';
  for I := 1 to Length(S) do if (S[I] < '0') or (S[I] > '9') then Result := False;
end;

function Sign(N: Integer): Integer;
begin
  if N < 0 then Result := -1 else if N > 0 then Result := 1 else Result := 0;
end;

// negative: A is older than B; 0: same; positive: A is newer
function CompareVersion(const A, B: String): Integer;
var CA, PA, CB, PB, XA, XB: String; I, NA, NB: Integer;
begin
  Result := 0;
  SplitVersion(A, CA, PA); SplitVersion(B, CB, PB);
  for I := 1 to 3 do
  begin
    NA := StrToIntDef(NextPart(CA), 0); NB := StrToIntDef(NextPart(CB), 0);
    if NA <> NB then begin Result := Sign(NA - NB); Exit; end;
  end;
  if (PA = '') and (PB = '') then Exit;
  if PA = '' then begin Result := 1; Exit; end;                    // a release is newer than its own prerelease
  if PB = '' then begin Result := -1; Exit; end;
  while (PA <> '') or (PB <> '') do
  begin
    if PA = '' then begin Result := -1; Exit; end;                 // fewer parts = older
    if PB = '' then begin Result := 1; Exit; end;
    XA := NextPart(PA); XB := NextPart(PB);
    if IsNumber(XA) and IsNumber(XB) then
    begin
      NA := StrToIntDef(XA, 0); NB := StrToIntDef(XB, 0);
      if NA <> NB then begin Result := Sign(NA - NB); Exit; end;
    end
    else if IsNumber(XA) then begin Result := -1; Exit; end        // numbers rank below words
    else if IsNumber(XB) then begin Result := 1; Exit; end
    else if XA <> XB then begin Result := Sign(CompareStr(XA, XB)); Exit; end;
  end;
end;

function InstalledVersion(): String;
var V: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', V) then Result := V
  else if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', V) then Result := V;
end;

// ---------- never go backwards ----------
function InitializeSetup(): Boolean;
var Old: String;
begin
  Result := True;
  Old := InstalledVersion();
  if (Old <> '') and (CompareVersion(Old, '{#AppVersion}') > 0) then
  begin
    MsgBox('A newer version of {#AppName} (' + Old + ') is already installed, so this older installer (' + '{#AppVersion}' + ') will not continue.' + #13#10#13#10 +
           'To go back to an older version, uninstall the current one first.', mbInformation, MB_OK);
    Result := False;
  end;
end;

// ---------- stopping a running copy the NORMAL way (it puts the network settings back before it exits) ----------
function AppRunning(): Boolean;
var Rc: Integer;
begin
  Result := Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "exit [int](@(Get-Process NetworkAutoSwitchTray -ErrorAction SilentlyContinue).Count -gt 0)"',
                 '', SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 1);
end;

function StopApp(): Boolean;
var Rc, I: Integer;
begin
  Result := True;
  if not AppRunning() then Exit;
  // the app listens for this signal (the same one "NetworkAutoSwitchTray.exe --quit" sends)
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "try { $e=[Threading.EventWaitHandle]::OpenExisting(''Global\NetworkAutoSwitchTray-quit''); [void]$e.Set() } catch { }"',
       '', SW_HIDE, ewWaitUntilTerminated, Rc);
  for I := 1 to 60 do
  begin
    if not AppRunning() then Exit;
    Sleep(1000);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
#ifndef TestInstall
  while not StopApp() do
    if MsgBox('{#AppName} is still running and did not close by itself.' + #13#10#13#10 +
              'Right-click its icon in the tray (near the clock, you may need the ^ arrow), choose Exit, then click Retry.', mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := '{#AppName} must be closed before it can be updated.';
      Exit;
    end;
#endif
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
#ifndef TestInstall
  while not StopApp() do
    if MsgBox('{#AppName} is still running and did not close by itself.' + #13#10#13#10 +
              'Right-click its icon in the tray, choose Exit, then click Retry.', mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
#endif
end;

// ---------- your settings, policy and log are kept unless you say otherwise ----------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\NetworkAutoSwitch');
    if DirExists(Data) and (not UninstallSilent) then
      if MsgBox('Also delete your settings, policy and log?' + #13#10#13#10 + Data + #13#10#13#10 +
                'Choose No to keep them (for example if you plan to reinstall).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
