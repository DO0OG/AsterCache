# AsterCache C#

Python 참조 구현(`relayout/`, `prefetch/`, `bench/`)을 C#으로 옮긴 최종본입니다. Python 은 참조 구현과 교차 검증 정답 생성기로 그대로 둡니다.

## 빌드와 시험

```
dotnet build  csharp\AsterCache.sln -c Release
dotnet test   csharp\AsterCache.sln -c Release --filter "Category!=Live"
dotnet test   csharp\AsterCache.sln -c Release --filter "Category=Live"    # D:\AsterBench 와 Python 참조 필요, 비관리자
dotnet format csharp\AsterCache.sln --verify-no-changes
```

대상은 .NET 10 (`global.json` 은 SDK 10.0.400, `latestPatch`). CI(`.github/workflows/csharp-ci.yml`)는 Live 시험을 뺀 build·test·format 을 돌립니다.

## 실행 파일 게시

스크립트는 아래 위치를 기본으로 씁니다. 게시는 사용자가 직접 합니다.

```
dotnet publish csharp\src\AsterCache.Cli\AsterCache.Cli.csproj -c Release -r win-x64 --self-contained -o csharp\publish\cli
dotnet publish csharp\src\AsterCache.Service\AsterCache.Service.csproj -c Release -r win-x64 --self-contained -o csharp\publish\service
```

CLI 명령(`aster plan`, `probe`, `apply`, `verify`, `revert`, `trace`, `prefetch`, `status`, `launch`, `stream-bench`)과 옵션은 `aster <명령> --help` 로 확인합니다. 재배치와 선읽기 명령은 Python `relayout/cli.py`, `prefetch/cli.py` 와 같은 이름을 씁니다.

서비스는 `AsterCache.Service.exe [--console] [--data-dir <폴더>]` 로 실행합니다. 기본 데이터 폴더는 `%ProgramData%\AsterCache` 이며 `config.json`, `plans\`, `journals\`, `logs\` 를 씁니다.

`AsterCache.Service.exe --console --watch-test <초>` 는 서비스를 띄우지 않고 설정의 exe 이름으로 ETW 감시기를 그 시간 동안 돌려, 감지한 시작과 세션으로 들어온 Kernel-Process 이벤트 집계(길·ID 별: 원시 `raw`, TDH 로 푼 `decoded`), 첫 이벤트의 페이로드 모양, 예외를 출력하고 이어서 폴링 감시기로 같은 시험을 한 번 더 돌립니다 (관리자 필요). ETW 감시기(`"watcher": "etw"`)는 시작 이벤트를 두 길로 받고, 60초 동안 시작 이벤트가 하나도 안 오면 예외로 끝나 서비스가 폴링으로 내려갑니다. ETW 실시간 세션은 버퍼를 약 1초마다 내려 보내므로 감지 지연이 폴링(250 ms)보다 클 수 있습니다.

`aster prefetch --timing` 은 출력 JSON 에 `timing_ms` (load, schedule, stream 과 경로 해석·배치 조회·크기 조회의 합계·횟수, 명령 안 총시간) 를 더합니다. 게시할 때(`-r win-x64`) `PublishReadyToRun` 이 켜져 시작 JIT 를 줄입니다.

## 관리자 스크립트 (사용자가 직접 실행)

`csharp\scripts\` 의 스크립트는 모두 관리자 PowerShell 에서 사용자가 한 번씩 실행합니다. 자동화나 작업자가 실행하지 않습니다. 각 스크립트는 첫 단계에서 관리자 권한을 확인하고, 없으면 아무것도 바꾸지 않고 멈춥니다. 결과는 `results\csharp_<yyyyMMdd_HHmmss>\` 에 쌓이며, 되돌리기 명령은 그 폴더의 `README.txt` 에 적히지만 스크립트가 실행하지는 않습니다.

| 스크립트 | 용도 | 판정 기준 |
|---|---|---|
| S1 `cs_e2_vhdx.ps1` | 일회용 VHDX 에서 재배치 전 과정 (강제 종료 5회 후 재개, 강제 분리 1회, chkdsk, revert) | 해시 불일치 0, 재개 5/5, chkdsk 오류 0, 처리량 20 MB/s 이상 |
| S2 `cs_e3_check.ps1` | D:\AsterBench 에서 probe 비교, 계획 경로 검사, dry-run, verify, 콜드 스트림 (a) pvm, (b) 중첩 읽기 | C# 중앙값 ≤ Python 중앙값, 매니페스트 불변 |
| S3 `cs_etw_check.ps1` | 실시간 ETW 트레이스(`trace --live --stop-file` 로 정상 종료)와 WPR .etl 수집, .etl 변환 진단(`etl_dump.txt`) | 비교 단계 미구현, 판정 `not_evaluated` |
| S4 `cs_service.ps1 -Action install [-TestConfig] / test / uninstall [-PurgeData]` | 서비스 등록(LocalSystem), 데이터 폴더 ACL, 스스로 끝나는 `cmd.exe` 감지 지연 20회 (프로세스를 죽이지 않고 PID 로 로그와 맞춤), 감시기 종류(ETW/폴링) 기록 | p95 ≤ 100 ms, 제거 뒤 서비스 없음 |
| S5 `cs_relayout_window.ps1` | 창 묶음 계획 (미채택 옵션) | 스켈레톤, 실행하면 바로 멈춤 |
| S6 `wdk_install.ps1 -AcceptWdkInstall` | WDK 설치만 (사용자가 결정한 뒤) | km 폴더 존재 |

### 권한 경계

- 스크립트와 설치 프로그램은 드라이버를 적재하거나 서명하지 않습니다. 부트 설정, 보안 부팅, 가상화 기반 보안, Hyper-V 설정도 바꾸지 않습니다.
- 데이터 폴더는 기본적으로 제거하지 않습니다. `cs_service.ps1 -Action uninstall -PurgeData` 를 명시했을 때만 지웁니다.
- 재배치(`apply --execute`, `revert --execute`)는 `--allowed-root` 와 `--require-volume-label` 로 지정 폴더와 볼륨만 대상으로 합니다.
- 서비스는 관리자 권한으로 상주합니다. 서비스가 SYSTEM 으로 `config.json`·계획에 적힌 파일을 옮기므로, 데이터 폴더 전체(`%ProgramData%\AsterCache`: config.json, plans, journals, logs)를 SYSTEM·Administrators 만 쓸 수 있고 일반 사용자는 읽기만 합니다 (설치 프로그램과 `cs_service.ps1 install` 이 같은 ACL 을 겁니다). 그래서 트레이로 게임을 등록·제거해 `config.json` 을 저장하려면 트레이를 관리자 권한으로 실행해야 하고, 권한이 없으면 "설정을 저장할 권한이 없음: 관리자 권한으로 트레이를 다시 실행하십시오" 를 표시합니다 (설정은 그대로). 트레이 로그는 쓸 수 없으면 `%LocalAppData%\AsterCache\logs` 에 씁니다.

## 서비스 동작

- 재적용 중 등록된 게임이 시작되면 (ETW/폴링 시작 알림) 취소 토큰이 `Applier` 의 조각 루프까지 전해집니다. 이동 중이던 한 조각(`MOVE_FILE` 한 번)만 끝내고 멈추며 `stopped = "cancelled"` 와 저널의 재개 지점이 남습니다. 게임이 끝난 뒤 5분 간격 검사에서 같은 계획·저널로 이어갑니다 (`aster verify` 는 이 상태를 오류가 아니라 `verdict = "resume-needed"` 로 보고합니다).
- 재적용은 핫 데이터의 존 적중률이 0.9 아래일 때만 합니다. 취소돼 부분 적용(`resume-needed`) 이어도 적중률이 0.9 이상이면 서비스는 이어하지 않습니다 (`aster apply --execute` 로 직접 이어갑니다).
- 등록된 게임이 0개인 `config.json` 은 정상입니다. 서비스는 감시·재적용 없이 대기만 하고, 트레이는 "등록된 게임 없음" 으로 표시합니다. 서비스는 시작할 때만 설정을 읽으므로 게임을 추가·제거한 뒤에는 서비스를 다시 시작해야 합니다.
- 계획·추적 파일이 아직 없는 게임은 검사·스트림을 건너뛰고 정보 줄만 남깁니다 (오류 아님). 게임 프로세스 감지는 로그에 `<이름>: <exe> started (pid N)` 로 매번 남고, 어떤 감시기를 쓰는지는 `process watcher: EtwProcessWatcher` 또는 `PollingProcessWatcher` 로 남습니다.

## 트레이 갱신과 서비스

트레이(Velopack, `%LocalAppData%\<packId>\current`)와 서비스(Inno Setup, `{autopf}\AsterCache`, `sc create` 경로)는 설치 경로가 다르고, 둘이 `%ProgramData%\AsterCache` (`config.json`, `plans\`, `journals\`, `logs\`) 를 공유합니다. Velopack 갱신은 트레이 폴더만 바꾸고 서비스 실행 파일은 건드리지 않습니다 (`UpdateService` 는 서비스 제어에 접근하지 않고 시험으로 고정). 그래서 서비스는 설치 프로그램으로 다시 설치하기 전까지 옛 버전으로 남습니다.

- 형식 호환: `config.json` 은 알려진 키만 받으므로 새 트레이가 새 키를 쓰면 옛 서비스는 시작하지 못하고(종료 코드 2), 계획 파일은 `version` 이 정확히 일치해야 하며 (현재 1), 저널은 형식 버전이 없고 계획 SHA 로만 짝을 맞춥니다. 지금까지 형식을 바꾼 적은 없지만 바뀌면 어긋납니다.
- 결정: 갱신 경로는 합치지 않고, 트레이가 SCM 에 등록된 서비스 실행 파일의 FileVersion 을 자기 FileVersion 과 비교해 다르면 상태 표시줄에 "서비스 재설치 필요 (서비스 x, 트레이 y)" 를 띄웁니다. 트레이와 서비스는 같은 `-p:Version` 으로 게시해야 비교가 의미 있습니다 (개발 빌드는 모두 1.0.0.0).

## 설치 프로그램

`packaging\windows\aster.iss` (Inno Setup 6) 는 서비스 파일을 설치하고, `AsterCache` 서비스를 등록하며(LocalSystem, 자동 시작), 데이터 폴더 전체의 ACL 을 설정합니다. 관리자 설치만 허용합니다. 제거하면 서비스를 지우고 데이터 폴더는 남깁니다. 컴파일은 `ISCC.exe /DServiceDir=<서비스 publish 경로> /DAppVersion=<버전> aster.iss` 로 합니다. 이 파일은 아직 컴파일해 보지 않았습니다.

## 배포와 CI

- `.github/workflows/csharp-ci.yml`: windows-latest, .NET 10.0.x, restore·build·test(Live 제외)·format. 푸시를 하지 않았으므로 실제 실행은 미확인입니다.
- `.github/workflows/csharp-release.yml`: 수동 실행. 서비스와 CLI 를 게시하고, 설치 프로그램은 Inno Setup, CLI 는 Velopack 으로 묶어 산출물로만 올립니다. GitHub 릴리스 게시는 설정하지 않았습니다(릴리스마다 사용자 승인). 서명은 아직 없습니다. 트레이 패키징은 WP9 이후입니다.

## 성능 결론 (참조 측정)

- 재배치 존 기준 Python 스트림은 1.42 s, 같은 핫 바이트를 한 파일로 읽은 상한은 1.09 s 입니다 (`results/prefetch_methods_20261102` 와 `results/prefetch_sweep_20261101/contig_upper_bound.json` 참조).
- 핫 집합 축소 옵션은 이득이 없어 채택하지 않았습니다.
- 요청 묶기는 읽기 순서를 깨뜨려 채택하지 않았습니다. 창 묶음 옵션(S5)은 미채택 상태입니다.
- 게임 시작 감지: 기본은 폴링(`config.json` 의 `"watcher": "poll"`, 간격 `poll_ms` 기본 50 ms). PID 목록만 받아(`K32EnumProcesses`, 한 번에 약 0.05 ms) 새 PID 의 이미지 이름만 조회해 유휴 시 한 코어의 약 1%이고, 20회 시험에서 감지 지연 중앙값 31 ms, p95 47 ms 였습니다 (50 ms 간격, Toolhelp 전체 스냅샷은 한 번에 약 10 ms 라 같은 간격이면 25%). `"watcher": "etw"` 는 관리자일 때만 쓰이며 (실패하면 폴링으로 내려감) 실시간 세션 버퍼가 초 단위로 내려가 S4 실측 지연이 약 1.65 초였습니다. 이를 줄이려고 세션 버퍼를 50 ms 마다 `FlushTrace` 로 내려 보내지만 효과는 관리자 S4 시험(`-Watcher etw`)으로 확인해야 합니다. 밀리초 플러시 플래그(`EVENT_TRACE_USE_MS_FLUSH_TIMER`)는 공개 SDK 헤더(10.0.26100)에 없어서 쓰지 않았습니다. 로그의 `started (pid N) via poll|etw-raw|etw-tdh` 가 어느 길로 잡았는지 보여 줍니다.

## 한계와 미검증 항목

- S1~S6 와 Live 시험은 실행하지 않았습니다. 관리자 권한과 실제 D: 디스크가 필요합니다.
- 비관리자 실행 확인(첫 단계 멈춤)은 실행 금지 지시에 따라 하지 않았습니다. 정적 검사만 했습니다.
- 성능 수치는 비관리자 언버퍼드 프록시 콜드, SMR 의심 드라이브 1대, 합성 데이터 기준입니다. 이동 직후 캐시 영향 때문에 24시간 뒤 지속성은 측정하지 않았습니다.
- TraceEvent 3.0.2 의 커널 파서 필드(`FileIORead`: `Offset` `IoSize` `FileKey` `FileObject` `ProcessID`, `FileIOName/FileCreate/FileRundown`: `FileKey` `FileName`, `FileIOCreate`: `FileObject` `FileName`, `ProcessStart`: `KernelImageFileName` `CommandLine` `ParentID`)는 어셈블리 리플렉션으로 이름·형식을 대조했고, 서비스가 읽는 `Microsoft-Windows-Kernel-Process` 이벤트 1 의 `ProcessID` `ImageName` 은 이 PC 에 등록된 매니페스트와 대조했습니다. 실제 .etl 변환은 `aster trace --etl <파일> --exe <이름> --origin-root <루트> --out <파일> --dump-first 20` 로 확인합니다 (요약 한 줄과 처음 20건의 읽기). 사설 커널 세션의 실시간 동작, FileIO 읽기가 캐시 적중을 포함하는지, PrefetchVirtualMemory 의 동기성은 확인하지 않았습니다.
- ETW 수집 버퍼: 실시간 세션(`LiveSession`)은 버퍼 256 MB 로 열고 (WPR 로 받은 .etl 에서 수백만 건의 유실이 관찰돼 키움, 열지 못하면 64 MB 로 한 번 다시 시도), `trace --live`/`--etl` 의 통계 줄에 `'events_lost': N` 을 남깁니다 (trace JSON 형식은 그대로). `cs_etw_check.ps1` 의 WPR 수집은 `scripts\aster_fileio.wprp` (FileIO, 1 MB × 512 버퍼, 파일 모드) 를 씁니다.
- 읽기 재현율의 상한: 실제 .etl 의 파이썬 재생 읽기 4,000건 중 FileIO 읽기 이벤트는 앱 수준 IRP(`IoFlags` 0 또는 0x60900) 약 2,811건과, 캐시 관리자의 페이징 읽기(0x60403) 1,244건이었습니다. 페이징 읽기는 같은 오프셋에서 약 50 µs 뒤에 한 번 더 나와 중복 블록(1,358개 대 기준 284개)의 원인이고 (Init/Complete 이중 집계가 아님: Complete 는 별도 `FileIOOperationEnd`), 캐시 적중으로 끝난 읽기(FastIO)는 이벤트가 없어서 서로 다른 블록이 기준의 약 72.6% 에 그쳤습니다. 블록 단위 합집합에서는 중복이 사라지지만, 캐시 적중 읽기는 ETW 로 얻을 수 없으므로 수집 전에 캐시를 비우거나 언버퍼드 재생으로 모아야 합니다.
- 알려진 한계: 파일 삭제(`FileIOFileDelete`)로 사라진 FileKey 이름은 처리하지 않습니다.
- 파일 이름은 FileKey(정리 이벤트는 세션 끝에 옴)를 우선하고 읽을 때의 FileObject 이름을 보조로 씁니다. FileObject 주소는 재사용되기 때문입니다. WPR FileIO 프로필의 실제 .etl 에서 이벤트 유실이 크면 (한 번의 수집에서 3.8M건) 읽기 일부가 빠지므로, 이 .etl 변환의 재현율은 수집 손실과 분리해 평가할 수 없습니다.
- 안티치트 게임(Ricochet, Vanguard)에서 ETW 수집과 서비스 상주의 영향은 검증하지 않았습니다. 안티치트가 없는 게임으로 먼저 확인합니다.
- Velopack 갱신은 트레이만 바꾸므로 서비스는 설치 프로그램으로 따로 갱신합니다 (위 "트레이 갱신과 서비스"). 실제 Velopack 설치본에서의 갱신 시험은 하지 않았습니다 (트레이 패키징 전).
- WDK 설치 크기와 winget 패키지 ID 는 확인하지 않았습니다.
- 커널 미니필터(AsterFlt)는 소스 단계이며, 이 PC 에 적재하지 않습니다. 적재 검증은 VM 절차로만 다룹니다.
