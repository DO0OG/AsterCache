; AsterCache Windows 설치 프로그램 (Inno Setup 6). 바탕: YttStudio packaging/windows/yttStudio.iss
;
; 릴리즈 워크플로가 아래 정의를 넘긴다.
;   /DAppVersion=0.1.0  /DAppNumVersion=0.1.0  /DAppDir=<gui publish 경로>  /DOutputDir=<출력 경로>
; 로컬 시험용 기본값은 아래 #ifndef 블록에 있다. 이 스크립트는 컴파일만 하고 실행하지 않는다.
;
; 설치 대상은 관리 창 publish 폴더 전체(Tray, aster.exe, 서비스, 런타임)다. 서비스 등록은 [Tasks] service 를 선택했을 때만 한다.
;
; 권한: 서비스 등록과 데이터 폴더 ACL 때문에 관리자 설치만 허용한다 (사용자 범위 설치 없음).
; 자동 시작: 로그인 때 UAC 창 없이 권한이 올라간 채 트레이가 뜨도록 "가장 높은 권한으로 실행" 예약 작업을 만든다 (Users 그룹의 로그온 트리거. 관리자 계정은 권한이 올라간 채, 표준 사용자는 일반 권한으로 뜬다).
; Program Files 아래 설치일 때만 만든다 (그 밖의 쓰기 가능한 폴더에서 권한 상승 자동 실행은 위험). 그 밖에서는 예전처럼 모든 사용자 시작 프로그램 바로가기 (일반 권한). 예전 바로가기는 업그레이드 때 정리한다.
; 제거 때 데이터 폴더(plans, journals, logs, config.json)는 남긴다. 사용자 데이터이기 때문이다.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppNumVersion
  #define AppNumVersion "0.1.0"
#endif
#ifndef AppDir
  #define AppDir "..\..\publish\gui"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "AsterCache"
#define ServiceExe "AsterCache.Service.exe"
#define ServiceName "AsterCache"
#define TrayExe "AsterCache.Tray.exe"

[Setup]
; 이 GUID 는 업그레이드 판정 기준이다. 새로 생성했으며 절대 바꾸지 않는다.
AppId={{3C9E4A1D-6B27-4F8E-9A10-5D7E2B8C4F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DO0OG
AppPublisherURL=https://github.com/DO0OG/AsterCache
AppSupportURL=https://github.com/DO0OG/AsterCache
AppUpdatesURL=https://github.com/DO0OG/AsterCache
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=AsterCache-v{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\AsterCache.Tray\Assets\aster.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#TrayExe}
VersionInfoVersion={#AppNumVersion}
VersionInfoProductVersion={#AppNumVersion}
VersionInfoProductTextVersion={#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
; 업그레이드 때 실행 중인 트레이 파일을 교체할 수 있도록 닫는다.
CloseApplications=yes
; 서비스 등록과 ACL 설정에 관리자 권한이 필요하다.
PrivilegesRequired=admin
; x64compatible 은 Inno Setup 6.3 부터다. 그 이전 컴파일러도 받아들이도록 분기한다.
#if Ver >= EncodeVer(6,3,0)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
english.AdditionalTasks=Additional tasks:
korean.AdditionalTasks=추가 작업:
english.TaskDesktop=Create a desktop shortcut
korean.TaskDesktop=바탕화면 바로가기 만들기
english.TaskStartMenu=Create a Start menu shortcut
korean.TaskStartMenu=시작 메뉴 바로가기 만들기
english.TaskAutostart=Start AsterCache (management window and tray icon) when I sign in to Windows (with administrator rights on an administrator account)
korean.TaskAutostart=Windows 로그인 때 AsterCache 관리 창과 트레이 아이콘 실행 (관리자 계정은 관리자 권한으로)
english.TaskService=Install and start the AsterCache service
korean.TaskService=AsterCache 서비스 설치 및 시작
english.RunTray=Open the AsterCache management window
korean.RunTray=설치 후 AsterCache 관리 창 실행
english.DataKept=The data folder (%1) was kept. Delete it yourself if you do not need it.
korean.DataKept=데이터 폴더(%1)는 남겨 두었습니다. 필요 없으면 직접 지우십시오.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:AdditionalTasks}"
Name: "startmenuicon"; Description: "{cm:TaskStartMenu}"
Name: "autostart"; Description: "{cm:TaskAutostart}"
Name: "service"; Description: "{cm:TaskService}"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#TrayExe}"; Tasks: desktopicon
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#TrayExe}"; Tasks: startmenuicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#TrayExe}"; Tasks: autostart; Check: UseStartupShortcut

[InstallDelete]
; 예전 설치의 자동 시작 바로가기 (Program Files 설치에서는 예약 작업이 대신하고, 그 밖의 위치에서 자동 시작을 켠 때만 바로가기를 남긴다)
Type: files; Name: "{commonstartup}\{#AppName}.lnk"; Check: not UseStartupShortcut

[UninstallDelete]
Type: files; Name: "{commonstartup}\{#AppName}.lnk"

[Run]
Filename: "{app}\{#TrayExe}"; Description: "{cm:RunTray}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; 자동 시작 예약 작업을 지운다 (없으면 실패해도 무시).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN {#AppName} /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAsterCacheAutostart"
; 트레이를 먼저 끈 뒤 서비스를 제거한다. 데이터 폴더는 남긴다.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#TrayExe} /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopAsterCacheTray"
; SSD 보조 저장의 링크를 서비스를 멈추기 전에 모두 되돌린다 (원본 이름 복구, 사본은 tier 폴더에 남김).
Filename: "{app}\aster.exe"; Parameters: "tier revert --all"; Flags: runhidden waituntilterminated; RunOnceId: "RevertAsterCacheTier"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopAsterCacheService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAsterCacheService"

[Code]
// Exit codes of sc.exe and icacls.exe are checked here; Inno's [Run] section ignores them. MustSucceed only shows the message; callers that cannot go on use RunRequired.
function RunTool(const Exe, Args: String; const MustSucceed: Boolean): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Exe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if MustSucceed and (not Result) then
    MsgBox('Command failed (exit ' + IntToStr(ResultCode) + '): ' + Exe + ' ' + Args, mbCriticalError, MB_OK);
end;

// A step the install cannot go on without: the reason was shown by RunTool, so stop Setup here (Abort is silent).
procedure RunRequired(const Exe, Args: String);
begin
  if not RunTool(Exe, Args, True) then Abort;
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  // sc query exits with 1060 when no such service is installed.
  Result := not (Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 1060));
end;

function ServiceStopped(): Boolean;
var
  ResultCode: Integer;
begin
  Result := (not ServiceExists()) or
    (Exec(ExpandConstant('{cmd}'), '/C sc.exe query {#ServiceName} | find "STOPPED" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0));
end;

// Polls up to 30 s. Want = True waits until the service is stopped, False until it is gone.
function WaitService(const Want: Boolean): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to 30 do
  begin
    if (Want and ServiceStopped()) or ((not Want) and (not ServiceExists())) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(1000);
  end;
end;

// Before file copy: an earlier install's service must not hold the old files. A non-empty result stops Setup with that message (nothing has been copied yet).
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // The running tray keeps its files locked, so it is closed before the copy. It may not be running.
  RunTool(ExpandConstant('{sys}\taskkill.exe'), '/IM {#TrayExe} /F', False);
  if not ServiceExists() then Exit;
  RunTool(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', False);
  if not WaitService(True) then
  begin
    Result := 'The existing {#ServiceName} service did not stop. Stop it (net stop {#ServiceName}) and run Setup again.';
    Exit;
  end;
  RunTool(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', False);
  if not WaitService(False) then
    Result := 'The existing {#ServiceName} service could not be deleted. Close programs that use it (for example Services or Task Manager) and run Setup again.';
end;

procedure ReplaceToken(var Text: AnsiString; const OldTok, NewTok: String);
var
  P: Integer;
begin
  P := Pos(OldTok, Text);
  if P > 0 then
  begin
    Delete(Text, P, Length(OldTok));
    Insert(NewTok, Text, P);
  end;
end;

// Stores the chosen installer language in config.json. Only "auto" or a missing key is changed, so an earlier ko/en choice stays. Runs before SetDataAcl so the file gets the locked ACL.
// Bytes outside the edited token are kept as they are (AnsiString, no re-encoding).
procedure WriteLanguageConfig();
var
  Dir, Path, Code: String;
  Text: AnsiString;
  P: Integer;
begin
  Dir := ExpandConstant('{commonappdata}\{#AppName}');
  Path := Dir + '\config.json';
  if ExpandConstant('{language}') = 'korean' then Code := 'ko' else Code := 'en';
  ForceDirectories(Dir);
  if not FileExists(Path) then
  begin
    SaveStringToFile(Path, '{' + #10 + '  "games": [],' + #10 + '  "language": "' + Code + '"' + #10 + '}' + #10, False);
    Exit;
  end;
  if not LoadStringFromFile(Path, Text) then Exit;
  if Pos('"language"', Text) > 0 then
  begin
    ReplaceToken(Text, '"language": "auto"', '"language": "' + Code + '"');
  end
  else
  begin
    P := Pos('{', Text);
    if P > 0 then Insert('"language": "' + Code + '", ', Text, P + 1);
  end;
  SaveStringToFile(Path, Text, False);
end;

procedure RegisterService();
var
  App, Common, Args: String;
begin
  App := ExpandConstant('{app}');
  Common := ExpandConstant('{commonappdata}');
  // binPath= "\"<exe>\" --data-dir \"<ProgramData>\AsterCache\""  (sc.exe quoting; LocalSystem, automatic start)
  Args := 'create {#ServiceName} binPath= "\"' + App + '\{#ServiceExe}\" --data-dir \"' + Common + '\{#AppName}\"" start= auto obj= LocalSystem DisplayName= {#AppName}';
  RunRequired(ExpandConstant('{sys}\sc.exe'), Args);
  RunTool(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "AsterCache game detection, prefetch and layout maintenance"', False);
end;

procedure SetDataAcl();
var
  Common, Data: String;
begin
  Common := ExpandConstant('{commonappdata}');
  Data := Common + '\{#AppName}';
  ForceDirectories(Data + '\plans');
  ForceDirectories(Data + '\journals');
  ForceDirectories(Data + '\logs');
  // The whole data folder (config.json, plans, journals, logs), not only plans: the service runs as SYSTEM and moves files named in config.json and plans,
  // so a user who could write them could escalate. SYSTEM and Administrators: full. Users: read only (the service also refuses to reapply otherwise).
  // Owner first: a user who created the folder or config.json earlier would own it and could rewrite its ACL (and AclCheck flags a non-admin owner, which would disable reapply for good).
  // /reset drops any ACL the user set on existing children so they inherit the locked one.
  RunRequired(ExpandConstant('{sys}\icacls.exe'), '"' + Data + '" /setowner *S-1-5-32-544 /T /C');
  RunRequired(ExpandConstant('{sys}\icacls.exe'), '"' + Data + '" /reset /T /C');
  RunRequired(ExpandConstant('{sys}\icacls.exe'),
    '"' + Data + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX');
end;

// Logon task for the Users group, run with the highest available privilege: an administrator gets the tray elevated with no UAC prompt, a standard user gets it unelevated.
// No battery, idle or time-limit conditions (a laptop on battery must still start it). Parallel (not IgnoreNew): a sign-in in another session (user switch, extra remote session) must start its own tray; one sign-in fires the trigger once.
// Priority 5 = normal process priority (the task default 7 is below normal). No XML declaration on purpose: schtasks reads UTF-8 only without one (a declaration or a BOM is rejected).
// The elevated task is registered only for an install under Program Files, whose ACL keeps ordinary users from replacing the tray files (an elevated autostart of a user-writable folder would be a privilege escalation).
// Anywhere else the old unelevated all-users Startup shortcut is used. Prefix match with a trailing backslash so C:\Program Files2 does not count; case ignored.
function UnderFolder(const Dir, Parent: String): Boolean;
begin
  Result := Pos(Lowercase(AddBackslash(Parent)), Lowercase(AddBackslash(Dir))) = 1;
end;

function InProgramFiles(): Boolean;
var
  App: String;
begin
  App := ExpandConstant('{app}');
  Result := UnderFolder(App, ExpandConstant('{commonpf}')) or UnderFolder(App, ExpandConstant('{commonpf32}')) or UnderFolder(App, ExpandConstant('{commonpf64}'));
end;

function UseStartupShortcut(): Boolean;
begin
  Result := WizardIsTaskSelected('autostart') and (not InProgramFiles());
end;

procedure RegisterAutostart();
var
  App, Xml, Path: String;
begin
  App := ExpandConstant('{app}');
  StringChangeEx(App, '&', '&amp;', True);
  Xml :=
    '<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #10 +
    '<RegistrationInfo><Description>Starts the AsterCache tray with administrator rights at sign-in</Description></RegistrationInfo>' + #10 +
    '<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>' + #10 +
    '<Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>' + #10 +
    '<Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>' +
    '<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>' +
    '<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>' +
    '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>' + #10 +
    '<Actions Context="Author"><Exec><Command>"' + App + '\{#TrayExe}"</Command><WorkingDirectory>' + App + '</WorkingDirectory></Exec></Actions>' + #10 +
    '</Task>' + #10;
  Path := ExpandConstant('{tmp}\AsterCacheAutostart.xml');
  SaveStringToFile(Path, UTF8Encode(Xml), False);
  // Not essential to the install: RunTool shows the failure but Setup goes on.
  RunTool(ExpandConstant('{sys}\schtasks.exe'), '/Create /TN {#AppName} /XML "' + Path + '" /F', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteLanguageConfig();
    SetDataAcl();
    // An earlier install's task is replaced (/F) or, when autostart is off or the folder is not under Program Files (the shortcut is used then), removed.
    if WizardIsTaskSelected('autostart') and InProgramFiles() then
      RegisterAutostart()
    else
      RunTool(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN {#AppName} /F', False);
    if WizardIsTaskSelected('service') then
    begin
      RegisterService();
      RunTool(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', False);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    MsgBox(Format(CustomMessage('DataKept'), [ExpandConstant('{commonappdata}\{#AppName}')]), mbInformation, MB_OK);
end;
