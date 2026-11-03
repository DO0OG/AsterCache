[English](README.md) | **한국어**

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

CLI 명령(`aster plan`, `probe`, `apply`, `verify`, `revert`, `trace`, `prefetch`, `status`, `launch`, `stream-bench`, `learn`, `profile`)과 옵션은 `aster <명령> --help` 로 확인합니다. 재배치와 선읽기 명령은 Python `relayout/cli.py`, `prefetch/cli.py` 와 같은 이름을 씁니다.

`aster discover [--json]` 은 지원 런처 라이브러리에서 찾은 게임 목록을 읽기 전용으로 보여 주며, 안티치트 표시를 함께 냅니다. 각 항목의 `launcher` 값에 `riot`, `uninstall`, `xbox` 가 추가됐습니다(Riot 라이브러리, Windows 제거 목록, Xbox 폴더). `aster status --auto-status` 는 자동 모드 상태(`auto_status.json`)를 보여 줍니다.

`aster tier status [--data-dir <폴더>]` 는 선택 기능인 SSD 보조 저장(`auto.ssd_tier`)의 상태를 한 줄 JSON 으로 보여 줍니다. 켜짐 여부, 예산, 고른 사본 폴더(또는 찾지 못한 이유), 게임별 활성 링크·바이트·손상 저널 표시를 냅니다. `aster tier apply --game <이름>`, `aster tier revert --game <이름>` 또는 `--all`, `aster tier clean` 은 관리자 권한이 필요합니다(서비스가 같은 일을 자동으로 합니다). `apply` 는 고른 핫 파일을 SSD 로 복사하고 크기와 표본 SHA-256 을 확인한 뒤, 원본을 `<이름>.aster-orig` 로 남기고 링크로 바꿉니다. 기능이 꺼져 있거나 게임이 조건에 맞지 않으면 `error`(예: `ssd_tier_disabled`, `system_volume`, `not_an_hdd`, `game_running`, `anti_cheat`, `no_ssd_root`, `no_trace`)와 함께 실패합니다. `revert` 는 링크를 지우고 원본을 복구합니다. `clean` 은 어떤 링크도 쓰지 않는 사본만 지웁니다. 저널은 데이터 폴더의 `tier\<게임>.json` 입니다.

`aster learn --exe <이름>... --root <폴더> [--seconds N] [--window-mib M] [--max-mib M] [--mode stream|gap|bounded|adaptive] [--saturation-mbps N] [--gap-ms N] [--seq-runs N] [--out trace.json] [--json]` 은 서비스 없이 첫 실행 학습(트레이스 + 선행 읽기)을 돌리는 측정·디버그용 명령입니다. 관리자 권한이 필요합니다. 출력에는 `read_ahead_bytes`, `game_read_bytes`, `hit_ratio`, `events`, `events_lost` 가 나옵니다. JSON 새 키는 `resolved_reads`, `late_reads`, `unresolved_reads`, `sequential_files`, `hit_bytes`, `gap_count`, `yielded_requests`, `read_ahead_while_idle_bytes` 입니다.

- `--gap-ms` (기본 100): 게임의 마지막 읽기 뒤 이 시간이 지나야 선행 읽기를 합니다 (틈 전용).
- `--seq-runs` (기본 6): 간격 4 MiB 이하의 증가 읽기가 이만큼 이어지고 그 파일을 8 MiB 이상 읽었을 때만 순차로 봅니다.
- 선행 읽기 총량은 게임 읽기 × 1.5 + 64 MiB 를 넘지 않습니다.
- `--mode stream`(기본)은 게임 읽기의 틈을 기다리지 않고 16 MiB 단위로 선행 읽기를 합니다. `--mode gap` 은 이전 방식(100 ms 조용할 때만, 1 MiB 요청)입니다. `--mode bounded` 는 순차 파일만 16 MiB 앞서 읽고, 총량은 게임 읽기 + 32 MiB 이하이며, 게임 읽기 뒤 50 ms 양보합니다(E7e 측정: 이득 없음). `--mode adaptive` 는 bounded 에 더해 최근 500 ms 의 게임 읽기 처리량이 `--saturation-mbps`(기본 80 MiB/s) 이상이면 쉬고 우리 요청 지연이 2 배로 오르면 200 ms 쉬며, `saturated_ms` 와 `paused_requests` 로 알려 줍니다(E7f 측정: 이득 없음). 기본을 stream 으로 한 것은, 게임 읽기와 선행 읽기가 1 MiB 씩 번갈아 나오면 전환마다 회전 지연이 생기고 틈 전용 방식이 E7b 재측정에서 약 2배 느렸기 때문입니다(`results\csharp_e7b_gen\README.md`: 합성 19.82 → 38.71 s, Frostpunk 42% 존 18.98 → 37.40 s). stream 도 E7d 에서 측정했으나 이득이 없어 채택하지 않았으므로, 선행 읽기는 기본으로 꺼져 있습니다.

`aster profile export --game <이름> --out <파일.json|파일.zip> [--data-dir <폴더>]` 는 게임 하나의 trace 를 상대 경로의 파일로 내보냅니다 (형식 `astercache-profile`, 버전 1: `files`[path·size·sample_sha256], `raw_trace`, `prefetch_trace`). `aster profile import --file <파일> --game <이름> [--data-dir <폴더>] [--json]` 는 파일마다 크기와 표본 SHA 가 모두 맞을 때만 받아들입니다. 불일치하면 목록을 보여 주고 종료 코드 1 로 끝납니다. 쓰기는 관리자 권한이 필요합니다.

명령행 언어는 전역 옵션 `--lang ko|en` 또는 환경변수 `ASTER_LANG` 으로 정합니다 (우선순위: `--lang` > `ASTER_LANG` > `config.json` 의 `"language"` > OS 언어). JSON 출력은 언어와 무관합니다.

서비스는 `AsterCache.Service.exe [--console] [--data-dir <폴더>]` 로 실행합니다. 기본 데이터 폴더는 `%ProgramData%\AsterCache` 이며 `config.json`, `plans\`, `journals\`, `logs\` 를 씁니다.

`AsterCache.Service.exe --console --watch-test <초>` 는 서비스를 띄우지 않고 설정의 exe 이름으로 ETW 감시기를 그 시간 동안 돌려, 감지한 시작과 세션으로 들어온 Kernel-Process 이벤트 집계(길·ID 별: 원시 `raw`, TDH 로 푼 `decoded`), 첫 이벤트의 페이로드 모양, 예외를 출력하고 이어서 폴링 감시기로 같은 시험을 한 번 더 돌립니다 (관리자 필요). ETW 감시기(`"watcher": "etw"`)는 시작 이벤트를 두 길로 받고, 60초 동안 시작 이벤트가 하나도 안 오면 예외로 끝나 서비스가 폴링으로 내려갑니다. ETW 실시간 세션은 버퍼를 약 1초마다 내려 보내므로 감지 지연이 폴링(250 ms)보다 클 수 있습니다.

`aster prefetch --timing` 은 출력 JSON 에 `timing_ms` (load, schedule, stream 과 경로 해석·배치 조회·크기 조회의 합계·횟수, 명령 안 총시간) 를 더합니다. 게시할 때(`-r win-x64`) `PublishReadyToRun` 이 켜져 시작 JIT 를 줄입니다.

## 관리 창

트레이 앱(`AsterCache.Tray`)에 드라이브 관리 유틸리티처럼 한눈에 보고 조작하는 메인 창 "AsterCache" 가 있습니다. 앱을 시작하면 기본으로 열리고, 닫으면 트레이로 내려갑니다 (트레이 아이콘 더블클릭 또는 메뉴 "열기" 로 다시 엽니다). 로직은 새로 만들지 않고 같은 폴더의 `aster.exe` 를 자식 프로세스로 불러 한 줄 JSON 을 읽으며 (`aster status`, `plan`, `apply`, `verify`, `revert`, `trace`, `prefetch`), 설정 편집과 서비스 제어는 트레이의 `ConfigEditor`·`ServiceControl` 을 씁니다. 실행한 명령과 결과(JSON·오류)는 모두 `tray.log` 에 남고 로그 탭에서 봅니다.

```
dotnet publish csharp\src\AsterCache.Tray\AsterCache.Tray.csproj -c Release -r win-x64 --self-contained -o csharp\publish\gui
csharp\publish\gui\AsterCache.Tray.exe [--data-dir <폴더>]
```

`aster.exe` 와 `AsterCache.Service.exe` 가 같은 `gui` 폴더에 함께 게시됩니다 (`--data-dir` 의 기본값은 `%ProgramData%\AsterCache`).

| 화면 | 내용 |
|---|---|
| 왼쪽 드라이브 목록 | 고정 볼륨마다 문자·레이블·파일시스템·전체/여유 용량·디스크 모델·HDD/SSD(`IOCTL_STORAGE_QUERY_PROPERTY` 의 `IncursSeekPenalty`, 실패하면 "알 수 없음")·등록된 게임 수. 선택하면 오른쪽에 그 드라이브의 게임만 보입니다 ("전체 드라이브" 가 맨 위). |
| 상태 탭 | 서비스 설치·실행 여부, 감시기 종류, 마지막 예열(시각·MiB·초), 게임별 배치 판정(`aster status --plan` 의 `verdict`), 존 적중률(`in_zone_clusters / hot_clusters`), 마지막 재배치 시각(저널 파일 시각), 게임별 예열 결과, 게임별 `예열 적중 N%`(마지막 게임 시작 때 잰 적중률)과 `SSD 보조 N GB`(SSD 사본 크기). 새로 고침 버튼. |
| 게임 탭 | 목록: 이름·exe·원점 루트·계획/트레이스 유무·배치 verdict·예열 설정. 버튼은 아래 표. |
| 설정 탭 | 서비스 설치·제거·시작·정지, 감시기 poll/etw, 예열 기본값 표시, **자동 모드** 묶음(켬·발견·트레이스·재배치·예열 개수·존 상한·예열 유지 주기·제외 exe, SSD 보조 저장 체크박스와 예산(GiB)), 데이터 폴더·로그 폴더 열기, 트레이·서비스 버전, **언어**(자동 / 한국어 / English). |
| 로그 탭 | `service.log` 와 `tray.log` 의 끝 200줄, 새로 고침. |
| 위쪽 띠 | 관리자 권한이 없으면 "관리자 권한 없음: 설정 저장·트레이스·재배치·서비스 제어는 관리자 재실행 필요" 와 [관리자로 다시 실행] 버튼. |

| 게임 탭 버튼 | 동작 |
|---|---|
| 등록 | exe 파일을 고르면 원점 루트·허용 루트는 exe 폴더, 이름은 파일 이름, 볼륨 라벨은 그 드라이브에서 채운 등록창이 열리고 (모든 칸 수정 가능, 트리거 프로그램 칸 포함) 기존 설정 검증을 통과해야 저장됩니다. |
| 제거 | 설정에서 게임을 뺍니다 (파일은 그대로). |
| 트레이스 수집 | 관리자. `aster trace --live --exe <exe> --origin-root <루트> --out plans\<이름>.trace.json --stop-file <임시>` 를 승격 실행합니다. "게임을 실행해 첫 맵까지 로딩한 뒤 종료하십시오" 를 보여 주고, 게임 프로세스가 뜬 뒤 사라지면 (또는 [수집 중지]) stop-file 을 만들어 정상 종료시킨 다음 건수와 MiB 를 표시합니다. |
| 계획 만들기 | `aster plan --dry-run` (존 16 GiB) 으로 핫 바이트를 본 뒤, 존 크기를 핫 바이트의 1.1 배를 16 MiB 로 올림한 값으로 정해 승격 `aster plan --out plans\<이름>.plan.json` 을 씁니다. 핫·존·이동 MiB 와 작업 수를 보여 줍니다. |
| 재배치 실행 | 확인창에 이동 바이트와 되돌리기 가능 안내를 보여 준 뒤, 승격 `aster apply --dry-run` 다음 `--execute --allowed-root <루트> --require-volume-label <라벨>` 을 돌립니다. 진행은 저널 줄 수로 표시하고 끝나면 `aster verify` 결과를 보여 줍니다. |
| 되돌리기 | 확인 뒤 승격 `aster revert --journal <저널> --execute`. |
| 지금 예열 | 트레이의 "지금 예열 (스트림)" 과 같은 즉시 스트림. |
| 측정 | `aster prefetch --execute --timing` 1회: 선읽기 MiB·초·MB/s 와 단계별(로드·스케줄·스트림) ms. 읽으면 파일 캐시가 데워지므로 같은 값을 다시 재려면 캐시를 비워야 합니다. |

- 파일 자리: 트레이스 `plans\<이름>.trace.json`, 계획 `plans\<이름>.plan.json`, 계획이 같이 쓰는 읽기 순서 `plans\<이름>.plan.json.prefetch.json` (등록창이 설정의 `trace` 로 이 이름을 채웁니다), 저널 `journals\<이름>.jsonl` (서비스의 재적용 저널과 같은 파일이라 이어하기·되돌리기가 서비스와 맞습니다).
- 관리자 권한: 데이터 폴더는 관리자만 쓰므로 트레이스·계획·재배치·되돌리기·설정 저장은 관리자가 필요합니다. 비관리자로 열면 `aster.exe` 를 UAC 로 직접 승격 실행하고 (인자는 하나씩 전달, 결과는 전역 옵션 `aster --result-file <경로> <명령> ...` 이 표준 출력을 복사해 둔 파일로 받음, 실행마다 새 임시 하위 폴더) 설정 저장은 거부됩니다. `aster.exe` 폴더를 일반 사용자가 바꿀 수 있으면 창 위에 경고가 뜹니다. 보기·예열·측정은 비관리자로 됩니다. 관리자 재실행 버튼은 같은 인자로 새 프로세스를 승격해 띄우고 지금 프로세스를 끝냅니다.
- 서비스 설치: 설치 프로그램과 같은 순서 (폴더 생성, 소유자·권한 설정, `sc create` LocalSystem 자동 시작, 시작) 를 한 번의 UAC 로 실행합니다. 서비스는 SYSTEM 으로 실행 파일을 돌리므로, 실행 파일 폴더를 일반 사용자가 바꿀 수 있으면 (개발 폴더 등) 설치를 거부하고 설치 프로그램을 쓰라고 안내합니다. `config.json` 이 없으면 먼저 게임을 등록해야 합니다.
- 재배치·되돌리기는 게임이 실행 중이면 거부하고, 한 번에 쓰는 양이 2 GiB (`--write-cap-mib`) 로 제한되어 큰 이동은 "다시 실행하면 이어서 합니다" 안내와 함께 여러 번 실행해야 할 수 있습니다. 트레이스 수집은 최대 3600 초이고, 앱 종료·관리자 재실행 때 진행 중이면 저장을 최대 10 초 기다립니다.
- 문서용 그림: `AsterCache.Tray.exe --smoke --screenshot <png> [--tab <0..3>] --data-dir <폴더>` 는 메인 창을 1100x720 으로 저장하고 끝납니다 (`docs\images\main-window.png`, 드라이브 목록은 예시 값). `--lang en` 또는 환경변수 `ASTER_LANG=en` 을 함께 주면 영어 UI 로 그립니다 (`docs\images\main-window.en.png` 가 이렇게 만든 것).
- 서비스는 `config.json` 전체를 5초마다 다시 읽으므로 게임 등록·제거와 감시기 변경은 다시 시작하지 않아도 반영됩니다. 새 설정이 검증이나 폴더 권한 검사에 실패하면 옛 설정을 유지하고 그 이유를 로그에 남깁니다.
- 예열 고정(`prewarm.pin`)은 설정 탭에 없고 게임별로 `config.json` 에서 정합니다. 설정에 적지 않으면 물리 RAM 24 GiB 이상에서 자동으로 켜지고, `false` 는 끕니다. 명시한 `true` 는 RAM 16 GiB 이상에서 동작합니다. 트리거 프로그램으로 뜬 예열은 고정하지 않습니다. 효과 미확인(실험 필요).
- 아직 안 한 것: 예열 기본값 편집 (표시만), 창 화면 시험 (뷰모델·명령 조립 시험만 있고 `Avalonia.Headless` 는 쓰지 않음), 관리자 승격 경로의 실제 UAC 실행 시험.

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
| E7 `cs_e7_learn.ps1` | 첫 실행 학습·선행 읽기(트레이스 재생) | 실험 기능입니다. 합성 데이터·트레이스 재생으로 측정(E7~E7f): 이득이 없어 선행 읽기는 기본으로 꺼져 있습니다. 첫 버전은 흩어진 합성 데이터셋의 콜드 재생을 21.67 s → 44.90 s 로 느리게 했습니다 (`results\csharp_e7_20261102_203358\README.md`) |

### 권한 경계

- 스크립트와 설치 프로그램은 드라이버를 적재하거나 서명하지 않습니다. 부트 설정, 보안 부팅, 가상화 기반 보안, Hyper-V 설정도 바꾸지 않습니다.
- 데이터 폴더는 기본적으로 제거하지 않습니다. `cs_service.ps1 -Action uninstall -PurgeData` 를 명시했을 때만 지웁니다.
- 재배치(`apply --execute`, `revert --execute`)는 `--allowed-root` 와 `--require-volume-label` 로 지정 폴더와 볼륨만 대상으로 합니다.
- 서비스는 관리자 권한으로 상주합니다. 서비스가 SYSTEM 으로 `config.json`·계획에 적힌 파일을 옮기므로, 데이터 폴더 전체(`%ProgramData%\AsterCache`: config.json, plans, journals, logs)를 SYSTEM·Administrators 만 쓸 수 있고 일반 사용자는 읽기만 합니다 (설치 프로그램과 `cs_service.ps1 install` 이 같은 ACL 을 겁니다). 그래서 트레이로 게임을 등록·제거해 `config.json` 을 저장하려면 트레이를 관리자 권한으로 실행해야 하고, 권한이 없으면 "설정을 저장할 권한이 없음: 관리자 권한으로 트레이를 다시 실행하십시오" 를 표시합니다 (설정은 그대로). 트레이 로그는 쓸 수 없으면 `%LocalAppData%\AsterCache\logs` 에 씁니다.

## 서비스 동작

- 재적용 중 등록된 게임이 시작되면 (ETW/폴링 시작 알림) 취소 토큰이 `Applier` 의 조각 루프까지 전해집니다. 이동 중이던 한 조각(`MOVE_FILE` 한 번)만 끝내고 멈추며 `stopped = "cancelled"` 와 저널의 재개 지점이 남습니다. 게임이 끝난 뒤 5분 간격 검사에서 같은 계획·저널로 이어갑니다 (`aster verify` 는 이 상태를 오류가 아니라 `verdict = "resume-needed"` 로 보고합니다).
- `aster apply --execute` 의 JSON 에 `timings` 객체가 붙습니다 (`open_s`, `digest_before_s`, `move_s`, `digest_after_s`, `journal_s`, `other_s`, `total_s`, `tasks`, `files`: 단계별 누적 초). 서비스 로그의 "auto relocation moved …" 줄 끝에는 `(open X s, digest Y s, move Z s)` 가 붙습니다. 대상 클러스터가 이미 쓰이는 중이라 `FSCTL_MOVE_FILE` 이 거부되면 (액세스 거부 / already committed) 그 단계만 `reason = "target busy"` 로 실패하고 다음 단계를 계속합니다. 연속 20 회를 넘으면 `stopped = "zone region appears occupied, stopping"` 으로 멈추고, 서비스는 계획을 `.stale` 로 치워 다음 틱에 다시 계획합니다.
- 재배치의 이동 전후 내용 비교는 `aster apply` 의 `--verify all|sample|off` (기본 `sample`) 와 서비스의 `config.json` `auto.relocate_verify` 로 정합니다. `sample` 은 각 파일의 첫 작업과 4번째마다의 작업 가장자리만 읽고 나머지는 이동 결과와 extent 위치로만 확인하며, `all` 은 모든 작업, `off` 는 읽지 않습니다. 이어하는 저널은 시작 때의 규칙을 유지합니다.
- 재배치 작업의 실행 순서는 `aster apply` 의 `--order target|source` (기본 `target`) 와 서비스의 `config.json` `auto.relocate_order` (`auto` 기본, `target`, `source`) 로 정합니다. `target` 은 계획(존) 순서, `source` 는 현재 원본 LCN 오름차순으로 실행해 읽기 탐색은 줄 수 있으나 존 쓰기가 흩어지며, 어떤 작업의 대상이 다른 작업의 현재 extent 와 겹치면 `target` 으로 돌아갑니다. 결과 배치는 같습니다. 기본은 `target` (`auto` 는 `target`) 이고 `source` 는 실험용입니다 (SMR HDD 실측에서 더 느렸음). 서비스 로그에 쓴 순서가 남습니다.
- 재배치는 서로 다른 파일의 이동을 겹쳐 실행할 수 있습니다: `aster apply --parallel N` (1~8, 기본 8) 과 서비스의 `config.json` `auto.relocate_parallel` (1~8, 기본 8). 같은 파일의 작업은 계획 순서대로 하나씩 실행하고, 작업자마다 볼륨 핸들을 따로 쓰며, 보고서의 `timings.move_wall_s` 는 이동이 진행 중이던 벽시계 시간 (`move_s` 는 모든 작업자의 합) 입니다. 기본 8, 유휴 때만 돌므로 체감 영향 없음, 실측 HDD 2.1 배·NVMe 3.3 배.
- 재적용은 `aster verify` 와 같은 판정으로 배치가 연속이 아닐 때(`non-contiguous`, 취소돼 부분 적용된 `resume-needed` 포함)만 합니다. 존 안 비율(`zone hit`)은 참고 로그일 뿐입니다: 계획이 이미 연속인 구간을 제자리에 보존하면 비율이 낮아도(실제 게임 계획에서 10.5%) 배치는 연속이라 재적용하지 않습니다. 파일 배치를 읽지 못한 항목이 있으면 판단하지 않고 건강으로 봅니다.
- 등록된 게임이 0개인 `config.json` 은 정상입니다. 서비스는 감시·재적용 없이 대기만 하고, 트레이는 "등록된 게임 없음" 으로 표시합니다. 게임을 추가·제거하면 다시 시작하지 않아도 5초 안에 반영됩니다.
- 계획·추적 파일이 아직 없는 게임은 검사·스트림을 건너뛰고 정보 줄만 남깁니다 (오류 아님). 게임 프로세스 감지는 로그에 `<이름>: <exe> started (pid N)` 로 매번 남고, 어떤 감시기를 쓰는지는 `process watcher: EtwProcessWatcher` 또는 `PollingProcessWatcher` 로 남습니다.
- 자동 모드([자동 모드](../README.ko.md#자동-모드))는 서비스 안에서 돕니다. 서비스는 `config.json` 전체를 5초마다 다시 읽으므로 바꾼 값이 다시 시작하지 않아도 적용됩니다. 상태는 `%ProgramData%\AsterCache\auto_status.json` 에 쓰며, 이 파일은 서비스만 씁니다.
- 선행 읽기 엔진(`Prefetch\ReadAhead`)은 서비스 안에서 첫 실행 학습을 맡습니다. `Tracer` 의 실시간 콜백을 받아 50 ms 마다 플러시합니다. [자동 모드](../README.ko.md#자동-모드)를 보십시오.
- 기록 없는 게임의 통째 예열 (`auto.full_prewarm`).
- 엔진 프로필 예열 (`auto.engine_profiles`, 임베디드 `Core\Profiles\engines.json`).

## 자동 부트 자료: 구성 요소와 공유 프로필 index

- `Service\ProfileFeed.cs`: 공유 프로필 묶음 zip(`auto.shared_profiles`)을 주 1회 ETag 로 받고, 내려받기를 64 MiB 로 제한하며, index 항목을 설치된 게임에 맞춥니다. https 주소만 씁니다.
- `Service\AutoRegistry.cs`: 런처 발견과 휴리스틱. 경로와 도구 exe 의 고정 제외 목록, 게임처럼 보이는지 검사(256 MiB 이상 파일 또는 런처 흔적)를 맡습니다.
- `Windows\PrefetchFiles.cs`, `Core\Prefetch\PfParser.cs`: Windows 프리페치 `.pf` 를 읽기 전용으로 읽어 파일 목록을 부트 트레이스로 바꿉니다. "미리 읽지 않음" 표시가 있는 파일은 뺍니다.
- `Service\AutoRelocator.cs`(`PrefetchBootstrap`), `Service\AutoTracer.cs`(`BootFromPrefetch`): 기록 없는 게임의 부트 트레이스를 만들고 그것으로 계획합니다. 프리페치 계획에는 64 MiB 이하 파일만 넣습니다.
- `Prefetch\ReadAhead.cs`: 첫 실행 선행 읽기 엔진(실험 기능, 기본 꺼짐; `stream`, `gap`, `bounded`, `adaptive` 모드).

### 공유 프로필 index 형식 (기여자용)

묶음 zip 에는 `index.json` 과 프로필 파일이 들어 있습니다.

```json
{"format":"astercache-profile-index","version":1,"entries":[
  {"steam":{"appid":"730","depots":{"731":"<manifest gid>"}},"profile":"p/cs2.json"},
  {"epic":{"app_name":"..","version":".."},"profile":".."},
  {"exe":{"size":0,"sample_sha256":".."},"profile":".."}
]}
```

- 항목마다 `steam`, `epic`, `exe` 중 하나만 씁니다.
- `profile` 은 zip 안의 프로필 `.json` 경로이며, 형식은 `aster profile export` 의 결과와 같습니다.
- 맞춤 규칙: Steam 은 앱 ID 와 모든 디포의 매니페스트 gid, Epic 은 앱 이름과 버전, exe 는 파일 크기와 표본 SHA-256 입니다. 맞더라도 프로필 파일의 크기와 표본 해시가 이 PC 와 맞을 때만 부트 트레이스로 저장합니다.

## 두 동작 모드: 동시 선읽기 / 사전 예열

- **동시 선읽기** (기본 동작): 게임 exe 가 뜨는 순간 존 순서로 읽기 시작합니다. 재배치된 존은 HDD 순차 한계(이 PC 에서 약 140 MB/s)로 읽히므로 핫 집합이 1.6 GB 면 약 11.6 s 가 걸려 SSD(2 GB 에 2~4 s)를 넘지 못합니다.
- **사전 예열** (RAM 상주, SSD 이상): 게임을 켜기 *전에* 같은 존 스트림(pvm, 제자리 실행)을 돌려 핫 집합을 RAM(파일 캐시 대기 목록)에 올려 둡니다. 게임이 뜰 때는 캐시 적중이라 디스크를 읽지 않습니다. 서비스가 시작될 때(부팅·로그온 뒤)와, 게임별 `trigger_exes` 의 프로그램(런처 등)이 뜰 때 실행합니다. 게임 exe 가 뜰 때의 동시 선읽기는 그대로 남아, 예열 중에 게임이 시작돼도 스트림은 멈추지 않고 이어집니다 (같은 데이터).

예열은 가용 RAM ≥ 존 크기 + `min_free_ram_mib`, 게임이 실행 중이 아님, 쿨다운이 지남, 계획·추적 파일이 있음, 이 조건을 모두 만족할 때만 합니다. 아니면 건너뛰고 이유를 `service.log` 에 남깁니다. 결과(바이트·초·MB/s·건너뜀 이유)는 `service.log` 와 `%ProgramData%\AsterCache\prewarm_status.json` 에 남고 `aster status` 의 `prewarm` 로 보입니다.

묶음에 게임이 둘 이상이면 각 게임의 로딩 앞부분(tier0)을 최근 순서로 먼저 읽고, 그다음 게임별 구역 전체를 읽습니다. 묶음 중에 게임이 시작되면 `auto.keep_stream_on_game_start` 가 `true` 가 아닌 한 다음 64 MiB 경계에서 멈춥니다.

```json
{
  "language": "auto",
  "watcher": "poll",
  "games": [
    {
      "name": "frostpunk",
      "exe_names": ["Frostpunk.exe"],
      "trace": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.prefetch.json",
      "plan": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.plan.json",
      "origin_root": "D:\\Games\\Frostpunk",
      "allowed_roots": ["D:\\Games"],
      "max_stream_mib": 4096,
      "volume_label": "GAMES",
      "prewarm": {
        "on_service_start": true,
        "trigger_exes": ["Steam.exe"],
        "min_free_ram_mib": 4096,
        "cooldown_min": 30
      }
    }
  ]
}
```

한 번의 스트림·예열이 읽는 양은 계획의 존 크기(계획이 없거나 존이 비면 추적의 총 바이트)이고 게임별 `max_stream_mib`(기본 4096)가 상한입니다. 예열의 RAM 판정도 이 읽을 양 기준이며, 존이 비어 있으면 통과가 아니라 `no zone` 으로 건너뜁니다. 서비스의 데이터 폴더를 믿을 수 없는 상태(`canApply` 가 아님)에서는 `prewarm_status.json` 을 쓰지 않습니다.

기본값은 `on_service_start` true, `trigger_exes` 비어 있음, `min_free_ram_mib` 4096, `cooldown_min` 30 이고, `prewarm` 을 생략하면 이 기본값입니다. `aster prewarm --plan <plan.json> --origin-root <root>` 는 `prefetch --execute --method pvm` 의 별칭입니다 (같은 옵션). 트레이의 "지금 예열 (스트림)" 은 같은 스트림을 트레이 프로세스에서 바로 돌리고 (파일 캐시는 시스템 공통이라 같은 효과), 게임 등록창의 "예열 트리거 프로그램" 칸에 쉼표로 `trigger_exes` 를 넣습니다. 실제 게임에서의 효과(RAM 적중 시 시작 시간)는 아직 측정하지 않았습니다.

## 트레이 갱신과 서비스

트레이(Velopack, `%LocalAppData%\<packId>\current`)와 서비스(Inno Setup, `{autopf}\AsterCache`, `sc create` 경로)는 설치 경로가 다르고, 둘이 `%ProgramData%\AsterCache` (`config.json`, `plans\`, `journals\`, `logs\`) 를 공유합니다. 설치본으로 설치하면 관리 창(트레이), `aster.exe`, 서비스가 모두 `Program Files\AsterCache` 에 설치됩니다. 새 버전은 새 설치본을 다시 실행해 갱신합니다 (서비스 중지→교체→시작). Velopack 자동 갱신은 Velopack 으로 설치한 트레이에만 해당하며 설치본 설치에는 쓰이지 않습니다 (`UpdateService` 는 서비스 제어에 접근하지 않고 시험으로 고정). 실제 Velopack 설치 갱신 시험은 아직 하지 않았습니다.

- 형식 호환: `config.json` 은 알려진 키만 받으므로 새 트레이가 새 키를 쓰면 옛 서비스는 시작하지 못하고(종료 코드 2), 계획 파일은 `version` 이 정확히 일치해야 하며 (현재 1), 저널은 형식 버전이 없고 계획 SHA 로만 짝을 맞춥니다. 지금까지 형식을 바꾼 적은 없지만 바뀌면 어긋납니다.
- 결정: 갱신 경로는 합치지 않고, 트레이가 SCM 에 등록된 서비스 실행 파일의 FileVersion 을 자기 FileVersion 과 비교해 다르면 상태 표시줄에 "서비스 재설치 필요 (서비스 x, 트레이 y)" 를 띄웁니다. 트레이와 서비스는 같은 `-p:Version` 으로 게시해야 비교가 의미 있습니다 (개발 빌드는 모두 1.0.0.0).

## 설치 프로그램

`packaging\windows\aster.iss` (Inno Setup 6) 는 관리 창, `aster.exe`, 서비스, 런타임 전체를 `Program Files\AsterCache` 에 설치하고, `AsterCache` 서비스를 등록하며(LocalSystem, 자동 시작), 데이터 폴더 전체의 ACL 을 설정합니다. 관리자 설치만 허용합니다. 설치 중 언어(English 또는 한국어, `config.json` 의 `"language"` 에 기록), 바탕화면·시작 메뉴 바로가기, 로그인 때 관리 창 실행과 트레이 아이콘 상주(바로가기는 모든 사용자 시작 프로그램 폴더), 서비스 설치와 시작, 마지막 화면의 관리 창 실행을 고를 수 있으며 모두 기본으로 켜져 있습니다. 제거하면 먼저 `aster tier revert --all` 로 SSD 보조 저장의 링크를 되돌리고(사본은 tier 폴더에 남음), 서비스를 멈추고 지우며 트레이를 종료합니다. 데이터 폴더는 남깁니다.

정의(`/D`): `/DAppDir`(관리 창·`aster.exe`·런타임이 든 폴더), `/DAppVersion`(사용자에게 보이는 버전), `/DAppNumVersion`(숫자 버전), `/DOutputDir`(설치본 `.exe` 를 쓸 폴더). 컴파일은 `ISCC.exe /DAppDir=<관리 창 publish 경로> /DAppVersion=<버전> aster.iss` 로 합니다. 릴리스 워크플로도 같은 방식으로 컴파일합니다.

## 배포와 CI

- `.github/workflows/csharp-ci.yml`: windows-latest, .NET 10.0.x, restore·build·test(Live 제외)·format.
- `.github/workflows/csharp-release.yml`: 수동 실행(`workflow_dispatch`, `version` 입력, 기본 `0.9.0-beta.3`, 러너 windows-2022). verify → package(관리 창 zip `AsterCache-<version>-win-x64.zip`, Inno Setup 설치본) → release(GitHub Release 생성, 버전에 `-` 가 있으면 프리릴리스, zip 과 설치본 `.exe` 를 자산으로 첨부) 순으로 돌아갑니다. 코드 서명은 없습니다(게시자 미확인 경고만).

## 성능 결론 (참조 측정)

- 재배치 존 기준 Python 스트림은 1.42 s, 같은 핫 바이트를 한 파일로 읽은 상한은 1.09 s 입니다 (`results/prefetch_methods_20261102` 와 `results/prefetch_sweep_20261101/contig_upper_bound.json` 참조).
- 핫 집합 축소 옵션은 이득이 없어 채택하지 않았습니다.
- 요청 묶기는 읽기 순서를 깨뜨려 채택하지 않았습니다. 창 묶음 옵션(S5)은 미채택 상태입니다.
- 게임 시작 감지: 기본은 폴링(`config.json` 의 `"watcher": "poll"`, 간격 `poll_ms` 기본 50 ms). PID 목록만 받아(`K32EnumProcesses`, 한 번에 약 0.05 ms) 새 PID 의 이미지 이름만 조회해 유휴 시 한 코어의 약 1%이고, 20회 시험에서 감지 지연 중앙값 31 ms, p95 47 ms 였습니다 (50 ms 간격, Toolhelp 전체 스냅샷은 한 번에 약 10 ms 라 같은 간격이면 25%). `"watcher": "etw"` 는 관리자일 때만 쓰이며 (실패하면 폴링으로 내려감) 실시간 세션 버퍼가 초 단위로 내려가 변경 전 S4 실측 지연이 약 1.65 초였습니다. 세션 버퍼를 50 ms 마다 `FlushTrace` 로 내려 보낸 뒤 S4 실측은 p95 119 ms 입니다(VALIDATION_REPORT D 항목, 폴링은 중앙값 27 ms, p95 58 ms). 위의 20회 시험은 S4 값과 다른 측정입니다. 밀리초 플러시 플래그(`EVENT_TRACE_USE_MS_FLUSH_TIMER`)는 공개 SDK 헤더(10.0.26100)에 없어서 쓰지 않았습니다. 폴링의 한계: 두 틱 사이에 떴다가 끝난 프로세스와 틱 사이에 재사용된 PID 는 보지 못합니다. 로그의 `started (pid N) via poll|etw-raw|etw-tdh` 가 어느 길로 잡았는지 보여 줍니다.

| 감시기 | 방식 | 권한 | 감지 지연 (S4 실측) | 부담 | 권장 |
|---|---|---|---|---|---|
| `poll` | 50 ms 마다 PID 목록만 받고 새 PID 의 이름만 조회 | 관리자 불필요 | 중앙값 27 ms, p95 58 ms | 유휴 때 한 코어의 약 1% | 기본·권장 |
| `etw` | 커널 프로세스 이벤트를 실시간 세션으로 구독하고, 50 ms 마다 `FlushTrace` 로 버퍼를 내림 | 관리자 필요(서비스는 LocalSystem 이라 가능) | p95 119 ms (플러시 전 1.65 초) | 세션 버퍼 메모리 | 실패하면 폴링으로 자동 복귀 |

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
- 0.9.0-beta.14 부터 서비스는 Vanguard·EAC·BattlEye 게임(탐지 실패 포함)에는 자동 ETW 트레이스를 시작하지 않습니다. 예열·시작 스트림·재배치는 그대로입니다 (실제 Vanguard 클라이언트가 트레이스 시작 뒤 두 번 종료되었습니다).
- Velopack 갱신은 Velopack 으로 설치한 트레이에만 해당합니다. 설치본은 새 설치 프로그램을 실행해 갱신합니다 (위 "트레이 갱신과 서비스"). 실제 Velopack 설치 갱신 시험은 하지 않았습니다.
- WDK 설치 크기와 winget 패키지 ID 는 확인하지 않았습니다.
- 커널 미니필터(AsterFlt)는 소스 단계이며, 이 PC 에 적재하지 않습니다. 적재 검증은 VM 절차로만 다룹니다.
