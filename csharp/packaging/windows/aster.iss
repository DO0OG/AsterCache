; AsterCache Windows 설치 프로그램 (Inno Setup 6). 바탕: YttStudio packaging/windows/yttStudio.iss
;
; 릴리즈 워크플로가 아래 정의를 넘긴다.
;   /DAppVersion=0.1.0  /DServiceDir=<서비스 publish 경로>  /DOutputDir=<출력 경로>
;   /DHaveKorean        (컴파일러에 Korean.isl 이 있을 때만)
; 로컬 시험용 기본값은 아래 #ifndef 블록에 있다. 이 스크립트는 컴파일만 하고 실행하지 않는다.
;
; CLI(aster.exe)는 이 설치 프로그램에 넣지 않는다. 서비스만 설치한다.
;
; 권한: 서비스 등록과 plans 폴더 ACL 때문에 관리자 설치만 허용한다 (사용자 범위 설치 없음).
; 제거 때 데이터 폴더(plans, journals, logs)는 남긴다. 사용자 데이터이기 때문이다.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef ServiceDir
  #define ServiceDir "..\..\publish\service"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "AsterCache"
#define ServiceExe "AsterCache.Service.exe"
#define ServiceName "AsterCache"

[Setup]
; 이 GUID 는 업그레이드 판정 기준이다. 새로 생성했으며 절대 바꾸지 않는다.
AppId={{3C9E4A1D-6B27-4F8E-9A10-5D7E2B8C4F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DO0OG
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=AsterCache-v{#AppVersion}-win-x64-setup
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
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
#ifdef HaveKorean
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif

[Files]
Source: "{#ServiceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallRun]
; 서비스 제거만 한다. 데이터 폴더는 남긴다.
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopAsterCacheService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAsterCacheService"

[Code]
// Exit codes of sc.exe and icacls.exe are checked here; Inno's [Run] section ignores them.
function RunTool(const Exe, Args: String; const MustSucceed: Boolean): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(Exe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if MustSucceed and (not Result) then
    MsgBox('Command failed (exit ' + IntToStr(ResultCode) + '): ' + Exe + ' ' + Args, mbCriticalError, MB_OK);
end;

// Before file copy: an earlier install's service must not hold the old files. Failure here is expected on a first install.
procedure StopAndDeleteOldService();
begin
  RunTool(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', False);
  RunTool(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', False);
end;

procedure RegisterService();
var
  App, Common, Args: String;
begin
  App := ExpandConstant('{app}');
  Common := ExpandConstant('{commonappdata}');
  // binPath= "\"<exe>\" --data-dir \"<ProgramData>\AsterCache\""  (sc.exe quoting; LocalSystem, automatic start)
  Args := 'create {#ServiceName} binPath= "\"' + App + '\{#ServiceExe}\" --data-dir \"' + Common + '\{#AppName}\"" start= auto obj= LocalSystem DisplayName= {#AppName}';
  RunTool(ExpandConstant('{sys}\sc.exe'), Args, True);
  RunTool(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "AsterCache game-start prefetch and layout check"', False);
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
  RunTool(ExpandConstant('{sys}\icacls.exe'), '"' + Data + '" /setowner *S-1-5-32-544 /T /C', True);
  RunTool(ExpandConstant('{sys}\icacls.exe'), '"' + Data + '" /reset /T /C', True);
  RunTool(ExpandConstant('{sys}\icacls.exe'),
    '"' + Data + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then StopAndDeleteOldService();
  if CurStep = ssPostInstall then
  begin
    RegisterService();
    SetDataAcl();
    RunTool(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}', False);
  end;
end;
