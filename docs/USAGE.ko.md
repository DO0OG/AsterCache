[English](USAGE.md) | **한국어**

# 사용법

> 트레이 창과 `aster` 명령의 사용법을 예시와 함께 설명합니다. 설치하고 나면 [자동 모드](AUTO_MODE.ko.md)가 알아서 다 합니다. 이 문서는 상태를 보고, 설정을 바꾸고, 단계를 직접 실행할 때 보는 문서입니다.

## 평소 사용

- **할 일은 없습니다.** 설치한 뒤 평소처럼 게임을 하면 됩니다([시작하기](../README.ko.md#시작하기) 참고).
- **창 열기.** 트레이 아이콘을 두 번 누르거나 바탕화면 바로가기를 씁니다. 창을 닫으면 앱은 트레이로 돌아갑니다.
- **관리자 권한.** 설정 저장, 트레이스 수집, 재배치, 되돌리기, 서비스 제어에 필요합니다. 없으면 위쪽에 안내 띠와 **관리자로 다시 실행** 버튼이 나타납니다. Program Files 아래에 설치하면 로그온 예약 작업이 트레이를 관리자 권한으로 시작하므로 확인 창(UAC)이 뜨지 않습니다.

## 관리 창

왼쪽에는 고정 드라이브 목록(문자, 레이블, HDD/SSD, 여유 공간, 등록된 게임 수)이 있습니다. 하나를 고르면 그 드라이브의 게임만 보입니다.

| 탭 | 할 수 있는 일 |
|---|---|
| **상태** | 서비스 상태와 마지막 예열, **자동 진행** 숫자 타일 다섯 개(찾은 게임, 만든 계획, 재배치, 예열, 평균 적중률), 게임별 카드(단계, 배치 판정, 마지막 재배치, 예열 결과, `예열 직후 적중 N%`)를 봅니다. |
| **게임** | 게임별 상태, 예열 상태, 보류 이유, 안티치트 표시를 봅니다. 버튼은 등록, 제거, 트레이스 수집, 계획 만들기, 재배치 실행, 되돌리기, 지금 예열, 측정입니다(아래 참고). **고급 열 보기**를 켜면 경로와 설정이 더 보입니다. |
| **설정** | **자동 모드** 카드(자동 모드 사용, 발견, 트레이스, 재배치, SSD 보조 저장, 예열할 최근 게임 수, 제외할 실행 파일, 존 상한, 하루 쓰기 상한), **서비스** 카드(설치, 제거, 시작, 정지), 감시기, **언어**, 데이터·로그 폴더 열기, 트레이·서비스 버전을 다룹니다. |
| **로그** | `service.log` 와 `tray.log` 의 마지막 200줄을 새로 고침과 함께 보여 줍니다. |

트레이 아이콘 메뉴에는 **열기**와 **지금 예열 (스트림)** 이 있습니다.

## 자주 하는 작업

**AsterCache 가 무엇을 하는지 보기.** **상태** 탭을 엽니다. 게임 카드에 단계가 나옵니다: 발견 → 트레이스 수집 → 재배치 대기 → 재배치 중 → 완료.

**자동 모드 끄기.** **설정** 탭에서 **자동 모드 사용** 체크를 풀고 **자동 모드 적용**을 누릅니다. 또는 `%ProgramData%\AsterCache\config.json` 에 `"auto": {"enabled": false}` 를 씁니다.

**게임 하나만 빼기.** 그 exe 를 `auto.ignore_exes` 에 넣거나, **게임** 탭에서 게임을 고르고 **제거**를 누릅니다(exe 가 목록에 들어가 다시 등록되지 않습니다).

**게임 되돌리기.** **게임** 탭에서 게임을 고르고 **되돌리기**를 누릅니다. 저널이 예전 배치로 복원하며, 파일 내용은 처음부터 바뀌지 않았습니다. 게임이 실행 중이면 되돌리기는 거절됩니다.

**큰 게임 패치 뒤.** 서비스가 스스로 배치를 다시 검사합니다([자주 묻는 질문](FAQ.ko.md) 참고). 직접 다시 하려면 **트레이스 수집**, **계획 만들기**, **재배치 실행**을 다시 합니다.

**제거.** Windows 설치된 앱에서 지웁니다. 데이터 폴더 `%ProgramData%\AsterCache` 는 남으며, 함께 지우려면 `cs_service.ps1 -Action uninstall -PurgeData` 를 실행합니다.

**기록을 다른 PC 로 옮기기.** `aster profile export` 와 `aster profile import` 를 씁니다(아래). 가져올 때는 파일 크기와 표본 해시가 같을 때만 받습니다.

## 직접 하려면(선택)

단계를 직접 다루고 싶거나, 자동 모드가 건너뛴 게임을 시험할 때 씁니다. 관리자 권한으로 **게임** 탭을 열고 아래 버튼을 순서대로 누릅니다.

| 순서 | 버튼 | 하는 일 |
|---|---|---|
| 1 | 등록 | 게임의 exe 파일을 고릅니다. 원본·허용 폴더는 exe 가 있는 폴더로, 볼륨 레이블은 그 드라이브 값으로 채워지며, 예열 트리거 프로그램을 포함해 모든 칸을 고칠 수 있습니다. |
| 2 | 트레이스 수집 | 게임을 실행해 첫 맵까지 로딩하고 끕니다. 읽기 순서를 기록합니다(ETW, 관리자 권한). |
| 3 | 계획 만들기 | 조각을 어디에 둘지 정합니다. 존은 핫 데이터의 1.1배입니다. |
| 4 | 재배치 실행 | 옮길 바이트 수와 되돌릴 수 있다는 안내가 확인 창에 나온 뒤 조각을 옮기며 저널이 남습니다. 한 번에 최대 2 GiB 를 쓰므로 큰 이동은 "다시 실행하면 이어서 합니다" 라고 나올 수 있습니다. |
| 5 | 지금 예열 | 그 구역을 바로 RAM 에 올립니다. |
| 6 | 측정 | 구역을 한 번 읽는 시간을 잽니다. 읽으면 파일 캐시가 데워지므로 다시 재려면 캐시를 비워야 합니다. |

## `aster` 명령

`aster` 는 관리 창과 같은 작업을 명령으로 실행해 자동화할 수 있게 합니다. 명령마다 `aster <명령> --help` 로 확인하며, 모든 옵션은 [csharp/README.ko.md](../csharp/README.ko.md)에 있습니다.

- `trace --live`, `probe`, `apply --execute`, `revert --execute`, `tier apply|revert|clean` 은 **관리자** PowerShell 에서 실행합니다.
- 출력은 한 줄 JSON 입니다(`aster discover` 는 `--json` 을 붙이지 않으면 글자로 출력합니다). 종료 코드는 0 성공, 1 실행 오류(`{"error": ...}` 한 줄), 2 잘못된 인자, 3 관리자 아님(`{"error":"not_admin"}`), 4 사용 중(다른 실행이 데이터 폴더를 잡고 있음)입니다.
- 전역 옵션은 명령 앞에 둡니다. `--lang ko|en` 은 안내 문구의 언어를 정하고([언어](CONFIGURATION.ko.md#언어) 참고), `--result-file <경로>` 는 출력을 파일에도 씁니다.

| 명령 | 하는 일 |
|---|---|
| `discover [--json]` | 런처 라이브러리에서 찾은 게임 목록을 보여 줍니다. 읽기 전용입니다. |
| `status [--plan <계획>] [--journal <저널>]` | 읽기 전용 현황입니다. 관리자 권한, 디스크, 마지막 예열, 자동 모드를 보여 주며, `--plan` 을 주면 배치 판정도 보여 줍니다. |
| `trace --live --exe <exe> --origin-root <폴더> --out <파일> [--seconds N]` | ETW 로 게임이 읽는 내용을 기록합니다. `--csv`, `--etl` 은 기존 기록을 변환합니다. |
| `plan --trace <파일> --origin-root <폴더> --zone-mib N --out <파일> [--dry-run]` | 트레이스로 배치 계획을 만듭니다. `--dry-run` 은 수치만 보여 줍니다. |
| `probe --volume D:` | 재배치 전에 볼륨을 점검합니다. |
| `apply --plan <파일> --allowed-root <폴더> --require-volume-label <레이블> [--dry-run \| --execute]` | 조각을 옮깁니다. `--execute` 가 없으면 아무것도 옮기지 않습니다. 레이블이 다르면 아무것도 건드리지 않습니다. |
| `verify --plan <파일>` | 배치가 그대로 있는지 검사합니다. |
| `revert --journal <파일> --allowed-root <폴더> [--execute]` | 저널로 예전 배치를 복원합니다. |
| `prewarm --plan <파일> --origin-root <폴더>` | 핫 데이터를 존 순서로 RAM 에 읽어 둡니다. `prefetch` 는 같은 명령이며 옵션(`--budget-mib`, `--method`, `--timing`)이 더 있습니다. |
| `launch --game <프로그램 또는 URL> --trace <파일> --origin-root <폴더>` | 선읽기를 먼저 시작하고 게임을 켭니다. `--no-prefetch` 는 게임만 켭니다(비교용). |
| `learn --exe <exe> --root <폴더>` | 콘솔에서 실험 기능인 첫 실행 선행 읽기를 합니다(자동 모드에서는 기본 꺼짐). |
| `profile export --game <이름> --out <파일>` / `profile import --file <파일> --game <이름>` | 게임 하나의 기록을 PC 사이에서 주고받습니다. |
| `tier status` / `tier apply --game <이름>` / `tier revert --game <이름>` 또는 `--all` / `tier clean` | 선택 기능인 SSD 보조 저장입니다. |
| `orbit mode off\|shadow\|active` | ORBIT 모드를 정합니다. [ORBIT](ORBIT.ko.md)을 보십시오. |
| `orbit calibrate`, `orbit model`, `orbit-bench run\|rescore\|summarize` | ORBIT 보정과 측정입니다. [ORBIT](ORBIT.ko.md)을 보십시오. |
| `stream-bench` | 선읽기 방식의 읽기 벤치마크입니다. |

## 예시

아래 값은 예시이며 실제 값은 다릅니다.

**런처에서 찾은 게임 목록 보기.** 게임마다 한 줄이며, 이름, 런처, `hdd`·`ssd`·`?`, exe(또는 설치 폴더), 안티치트 표시(없으면 `-`)를 탭으로 나눠 보여 줍니다.

```bat
aster discover
```
```text
Frostpunk	steam	hdd	D:\Games\Frostpunk\Frostpunk.exe	-
```

`--json` 을 붙이면 같은 목록이 `name`, `launcher`, `install_dir`, `exe`, `on_hdd`, `trigger_exe`, `anticheat` 키를 가진 JSON 한 줄로 나옵니다.

**단계를 직접 실행하기.** 첫째, 둘째, 넷째는 관리자 PowerShell 에서 실행합니다.

```bat
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

`--zone-mib 1536` 은 예시입니다. 게임의 핫 데이터 크기에 맞춰 정하십시오. 먼저 `--dry-run` 으로 실행하면 아무것도 옮기지 않고 계획만 보여 줍니다.

**관리자 권한 없이 실행하기.** `apply --execute` 는 아무것도 바꾸지 않고 종료 코드 3 으로 끝납니다.

```text
{"error":"not_admin"}
```

**배치가 그대로인지 검사하기.**

```bat
aster verify --plan plan.json
```

결과에는 검사 내용과 `verdict` 가 들어 있습니다. `contiguous`(제자리), `non-contiguous`(흐트러짐), `resume-needed`(이동이 중단되어 나중에 이어짐) 중 하나입니다.

**전체 상태 보기.** 결과에는 `admin`, `plan`, `journal`, `disks`, `prewarm`, `auto` 키 등이 들어 있습니다(여기서는 줄여서 적었습니다). `prewarm` 은 게임별 마지막 예열, `auto` 는 자동 모드 상태입니다.

```bat
aster status
```
```text
{"admin":true,...,"disks":[...],"prewarm":{...},"auto":{...}}
```

**ORBIT 모드 정하기.**

```bat
aster orbit mode shadow
```
```text
{"mode":"shadow"}
```

**SSD 보조 저장 쓰기** (`auto.ssd_tier.enabled` 가 켜져 있어야 하며, `tier apply` 와 `tier revert` 는 관리자 권한이 필요합니다).

```bat
aster tier status
aster tier apply --game Frostpunk
aster tier revert --all
```
