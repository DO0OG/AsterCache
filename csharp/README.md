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

CLI 명령(`aster plan`, `probe`, `apply`, `verify`, `revert`, `trace`, `prefetch`, `status`, `launch`)과 옵션은 Python `relayout/cli.py`, `prefetch/cli.py` 와 같은 이름을 씁니다. CLI 가 완성되기 전이라 스크립트의 인자는 [CLI 완성 후 재확인] 상태입니다.

서비스는 `AsterCache.Service.exe [--console] [--data-dir <폴더>]` 로 실행합니다. 기본 데이터 폴더는 `%ProgramData%\AsterCache` 이며 `config.json`, `plans\`, `journals\`, `logs\` 를 씁니다.

## 관리자 스크립트 (사용자가 직접 실행)

`csharp\scripts\` 의 스크립트는 모두 관리자 PowerShell 에서 사용자가 한 번씩 실행합니다. 자동화나 작업자가 실행하지 않습니다. 각 스크립트는 첫 단계에서 관리자 권한을 확인하고, 없으면 아무것도 바꾸지 않고 멈춥니다. 결과는 `results\csharp_<yyyyMMdd_HHmmss>\` 에 쌓이며, 되돌리기 명령은 그 폴더의 `README.txt` 에 적히지만 스크립트가 실행하지는 않습니다.

| 스크립트 | 용도 | 판정 기준 |
|---|---|---|
| `cs_e2_vhdx.ps1` | 일회용 VHDX 에서 재배치 전 과정 (강제 종료 5회 후 재개, 강제 분리 1회, chkdsk, revert) | 해시 불일치 0, 재개 5/5, chkdsk 오류 0, 처리량 20 MB/s 이상 |
| `cs_e3_check.ps1` | D:\AsterBench 에서 probe 비교, 계획 경로 검사, dry-run, verify, 콜드 스트림 (a) pvm, (b) 중첩 읽기 | C# 중앙값 ≤ Python 중앙값, 매니페스트 불변 |
| `cs_etw_check.ps1` | 실시간 ETW 트레이스와 WPR .etl 수집 | 비교 단계 미구현, 판정 `not_evaluated` |
| `cs_service.ps1 -Action install / test / uninstall [-PurgeData]` | 서비스 등록(LocalSystem), plans 폴더 ACL, 메모장 감지 지연 20회 | p95 ≤ 100 ms, 제거 뒤 서비스 없음 |
| `cs_relayout_window.ps1` | 창 묶음 계획 (미채택 옵션) | 스켈레톤, 실행하면 바로 멈춤 |
| `wdk_install.ps1 -AcceptWdkInstall` | WDK 설치만 (사용자가 결정한 뒤) | km 폴더 존재 |

### 권한 경계

- 스크립트와 설치 프로그램은 드라이버를 적재하거나 서명하지 않습니다. 부트 설정, 보안 부팅, 가상화 기반 보안, Hyper-V 설정도 바꾸지 않습니다.
- 데이터 폴더는 기본적으로 제거하지 않습니다. `cs_service.ps1 -Action uninstall -PurgeData` 를 명시했을 때만 지웁니다.
- 재배치(`apply --execute`, `revert --execute`)는 `--allowed-root` 와 `--require-volume-label` 로 지정 폴더와 볼륨만 대상으로 합니다.
- 서비스는 관리자 권한으로 상주합니다. plans 폴더는 SYSTEM·Administrators 만 쓸 수 있고, 일반 사용자는 읽기만 합니다.

## 설치 프로그램

`packaging\windows\aster.iss` (Inno Setup 6) 는 서비스 파일을 설치하고, `AsterCache` 서비스를 등록하며(LocalSystem, 자동 시작), plans 폴더 ACL 을 설정합니다. 관리자 설치만 허용합니다. 제거하면 서비스를 지우고 데이터 폴더는 남깁니다. 컴파일은 `ISCC.exe /DServiceDir=<서비스 publish 경로> /DAppVersion=<버전> aster.iss` 로 합니다. 이 파일은 아직 컴파일해 보지 않았습니다.

## 배포와 CI

- `.github/workflows/csharp-ci.yml`: windows-latest, .NET 10.0.x, restore·build·test(Live 제외)·format. 푸시를 하지 않았으므로 실제 실행은 미확인입니다.
- `.github/workflows/csharp-release.yml`: 수동 실행. 서비스와 CLI 를 게시하고, 설치 프로그램은 Inno Setup, CLI 는 Velopack 으로 묶어 산출물로만 올립니다. GitHub 릴리스 게시는 설정하지 않았습니다(릴리스마다 사용자 승인). 서명은 아직 없습니다. 트레이 패키징은 WP9 이후입니다.

## 성능 결론 (참조 측정)

- 재배치 존 기준 Python 스트림은 1.42 s, 같은 핫 바이트를 한 파일로 읽은 상한은 1.09 s 입니다 (`results/prefetch_methods_20261102` 와 `results/prefetch_sweep_20261101/contig_upper_bound.json` 참조).
- 핫 집합 축소 옵션은 이득이 없어 채택하지 않았습니다.
- 요청 묶기는 읽기 순서를 깨뜨려 채택하지 않았습니다. 창 묶음 옵션(S5)은 미채택 상태입니다.
- 게임 시작 감지는 ETW 와 폴링을 함께 씁니다. 관리자 권한이 없으면 폴링으로 내려갑니다.

## 한계와 미검증 항목

- S1~S6 와 Live 시험은 실행하지 않았습니다. 관리자 권한과 실제 D: 디스크가 필요합니다.
- 비관리자 실행 확인(첫 단계 멈춤)은 실행 금지 지시에 따라 하지 않았습니다. 정적 검사만 했습니다.
- 성능 수치는 비관리자 언버퍼드 프록시 콜드, SMR 의심 드라이브 1대, 합성 데이터 기준입니다. 이동 직후 캐시 영향 때문에 24시간 뒤 지속성은 측정하지 않았습니다.
- TraceEvent 의 .NET 10 동작, 사설 커널 세션, FileIO 읽기가 캐시 적중을 포함하는지, PrefetchVirtualMemory 의 동기성은 확인하지 않았습니다.
- 안티치트 게임(Ricochet, Vanguard)에서 ETW 수집과 서비스 상주의 영향은 검증하지 않았습니다. 안티치트가 없는 게임으로 먼저 확인합니다.
- Velopack 과 서비스 설치의 결합은 확인하지 않았습니다. 서비스 배포는 Inno Setup 이 기본입니다.
- WDK 설치 크기와 winget 패키지 ID 는 확인하지 않았습니다.
- 커널 미니필터(AsterFlt)는 소스 단계이며, 이 PC 에 적재하지 않습니다. 적재 검증은 VM 절차로만 다룹니다.
