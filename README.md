<p align="center"><img src="csharp/src/AsterCache.Tray/Assets/aster.png" width="128" alt="AsterCache 아이콘"></p>

<h1 align="center">AsterCache</h1>

<p align="center">HDD에 설치한 게임의 로딩 시간을 SSD에 가깝게 줄이는 Windows 데스크톱 유틸리티</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt="Platform: Windows 11 x64" src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4.svg">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4.svg">
  <a href="https://github.com/DO0OG/AsterCache/actions/workflows/csharp-ci.yml"><img alt="C# CI" src="https://github.com/DO0OG/AsterCache/actions/workflows/csharp-ci.yml/badge.svg"></a>
  <a href="https://github.com/DO0OG/AsterCache/actions/workflows/kernel-build.yml"><img alt="Kernel Build" src="https://github.com/DO0OG/AsterCache/actions/workflows/kernel-build.yml/badge.svg"></a>
</p>

## 목차

- [무엇을 하는가](#무엇을-하는가)
- [주요 기능](#주요-기능)
- [실측 결과](#실측-결과)
- [스크린샷](#스크린샷)
- [시스템 요구 사항](#시스템-요구-사항)
- [설치](#설치)
- [빠른 시작](#빠른-시작)
- [작동 원리](#작동-원리)
- [설정](#설정)
- [안전 장치](#안전-장치)
- [알려진 제한](#알려진-제한)
- [문제 해결](#문제-해결)
- [프로젝트 구조](#프로젝트-구조)
- [개발](#개발)
- [라이선스](#라이선스)
- [면책](#면책)

## 무엇을 하는가

AsterCache는 HDD에 설치된 게임이 로딩할 때 읽는 파일 블록을 기록하고, 그 읽기 순서대로 블록을 드라이브 안의 한 구간에 모아 둡니다. 게임이 같은 데이터를 읽을 때 헤드가 여기저기 흩어진 위치를 오가지 않도록 하는 것이 목적입니다. 재배치는 NTFS 안에서 파일 데이터를 제자리에서 옮기며, 복사본을 만들거나 드라이버를 적재하지 않습니다.

재배치된 구간을 읽는 방법은 두 가지입니다.

1. **동시 선읽기.** 게임 exe 가 뜨는 순간 재배치된 구간을 접근 순서대로 읽기 시작합니다. 이 방식은 HDD 의 순차 읽기 속도에 묶입니다.
2. **사전 예열.** 게임을 켜기 전에 같은 구간을 RAM(파일 캐시)에 올려 둡니다. 서비스 시작 때, 또는 지정한 런처 같은 트리거 프로그램이 뜰 때 자동으로 실행합니다. 게임이 뜰 때는 디스크를 거의 읽지 않습니다.

측정은 합성 데이터셋과 실제 게임 1종(Frostpunk, 안티치트 없음)의 추적 읽기 패턴 재생으로 했습니다. 게임 전체 로딩 시간과 여러 번 반복한 로딩 시간 분포는 아직 재지 않았습니다. 자세한 내용은 [실측 결과](#실측-결과)와 `VALIDATION_REPORT.md` 를 보십시오.

## 주요 기능

- **드라이브 목록.** HDD/SSD 구분, 용량, 모델, 등록된 게임 수를 보여 줍니다.
- **게임 등록.** 게임 이름, exe 이름, 설치 폴더, 허용 폴더, 볼륨 레이블을 정하고 예열 트리거 프로그램을 지정합니다.
- **트레이스 수집.** 게임을 한 번 실행해 첫 맵까지 로딩하면 읽기 블록을 ETW 실시간 세션으로 기록합니다(관리자 권한).
- **계획, 재배치, 되돌리기.** 기록으로 배치 계획을 만들고 적용합니다. 적용 기록(저널)을 거꾸로 적용해 예전 배치로 되돌릴 수 있습니다.
- **사전 예열.** 자동(서비스 시작, 트리거 프로그램 실행)으로 또는 "지금 예열"로 실행합니다.
- **서비스.** 게임 시작을 감지해 동시 선읽기를 실행하고, 예열과 재적용을 관리합니다. Windows 서비스(LocalSystem, 자동 시작)로 상주합니다.
- **상태와 로그.** 서비스와 감시기 상태, 마지막 예열 결과, 게임별 배치 상태, 로그를 탭에서 확인합니다.
- **측정.** 게임별로 재배치 전후 콜드 재생 시간을 잽니다.
- **관리 창.** 관리자 권한이 없으면 상단 안내 띠가 나타나며, 관리자 권한으로 다시 실행할 수 있습니다.
- **명령행 `aster`.** 관리 창의 같은 작업을 명령으로 실행합니다. 자동화에 씁니다.

## 실측 결과

수치는 추적한 읽기 패턴을 재생한 시간입니다. 게임을 실행한 전체 로딩 시간이 아닙니다.

| 대상 | 측정 | 결과 |
|---|---|---|
| 합성 데이터셋 2.1 GiB | HDD 콜드 읽기 → 재배치 후 선읽기 | 17.1 s → 1.14 s (재배치 10시간 뒤에도 유지) |
| 합성 데이터셋 2.1 GiB | 같은 조건 SSD 콜드 읽기 | 0.76~1.22 s |
| Frostpunk, 첫 맵 로딩 읽기 2.02 GB | HDD 콜드 재생 → 재배치 후 동시 선읽기 | 24.3 s → 15.1 s (HDD 순차 읽기 한계) |
| Frostpunk | 사전 예열 뒤 재생 | 0.48 s |
| Frostpunk, 같은 읽기 패턴 | NVMe SSD 콜드 재생 | 3.8 s |
| 서비스 | 게임 시작 감지 지연 (폴링, p95) | 58 ms |

- HDD 는 WD20EZAZ(SATA, D:), SSD 는 NVMe(C:)입니다. 측정 환경은 Windows 11 Pro 10.0.26200, 메모리 32 GiB 입니다.
- Frostpunk 결과는 게임 1종을 1회 추적한 뒤 재생한 값입니다. 반복 로딩의 p50·p95 는 아직 없습니다.
- 합성 데이터셋의 SSD 값과 Frostpunk 의 NVMe 값은 데이터와 방법이 달라 직접 비교하지 않습니다.
- 사전 예열이 SSD 콜드보다 빠르려면 예열이 게임 시작보다 15 s 이상 앞서 끝나야 합니다. 메모리 압박이 있으면 효과가 줄어듭니다.
- 더 많은 표와 해석은 `docs/RESULTS_SUMMARY.md` 와 `VALIDATION_REPORT.md` 에 있습니다.

## 스크린샷

관리 창 스크린샷은 준비 중입니다.

## 시스템 요구 사항

- Windows 11 x64. 검증 환경은 Windows 11 Pro 10.0.26200 입니다.
- 직접 빌드할 때는 .NET 10 SDK 가 필요합니다. 저장소의 `csharp/global.json` 은 SDK 10.0.400 을 씁니다(latestPatch).
- 트레이스 수집(ETW), 재배치(`apply`, `revert`), 서비스 설치에는 관리자 권한이 필요합니다.
- 재배치 대상은 NTFS 볼륨이어야 합니다. 원본과 캐시를 비교하려면 서로 다른 물리 장치가 필요합니다.

## 설치

### ① 설치 프로그램

`csharp/packaging/windows/aster.iss`(Inno Setup 6)로 설치본을 만들 수 있습니다. 서비스 파일을 설치하고 `AsterCache` 서비스를 자동 시작으로 등록하며, 데이터 폴더 권한을 설정합니다. 아직 릴리스에 올리지 않았으므로 직접 빌드해야 합니다. 이 스크립트는 아직 컴파일해 보지 않았습니다.

```bat
ISCC.exe /DServiceDir=<서비스 publish 경로> /DAppVersion=<버전> csharp\packaging\windows\aster.iss
```

### ② 직접 빌드

.NET 10 SDK 로 세 실행 파일을 게시합니다. 게시 결과는 `csharp\publish\` 아래에 생기며 Git 에서 무시합니다.

```bat
dotnet publish csharp\src\AsterCache.Cli\AsterCache.Cli.csproj -c Release -r win-x64 --self-contained -o csharp\publish\cli
dotnet publish csharp\src\AsterCache.Service\AsterCache.Service.csproj -c Release -r win-x64 --self-contained -o csharp\publish\service
dotnet publish csharp\src\AsterCache.Tray\AsterCache.Tray.csproj -c Release -r win-x64 --self-contained -o csharp\publish\gui
```

- `csharp\publish\cli\aster.exe` 는 명령행 도구입니다.
- `csharp\publish\gui\AsterCache.Tray.exe` 는 관리 창입니다.
- 서비스는 관리자 PowerShell 에서 설치합니다.

```powershell
csharp\scripts\cs_service.ps1 -Action install
```

## 빠른 시작

### 관리 창으로

1. 관리 창을 관리자 권한으로 실행합니다. 비관리자로 실행했다면 상단 안내 띠에서 다시 실행합니다.
2. 게임 탭에서 게임을 등록합니다. exe 이름과 설치 폴더를 지정합니다.
3. 트레이스를 수집합니다. 게임을 한 번 실행해 첫 맵까지 로딩합니다.
4. 수집한 기록으로 계획을 만듭니다.
5. 계획을 재배치합니다. 되돌리기도 같은 탭에서 합니다.
6. 설정 탭에서 서비스를 설치하고 시작합니다. 예열 옵션은 게임 설정에서 정합니다.

### 명령행 `aster` 로

```bat
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster verify --plan plan.json
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

- `--seconds` 대신 `--stop-file <파일>` 을 주고 그 파일을 만들어 수집을 끝낼 수 있습니다.
- `trace --live`, `apply --execute` 는 관리자 PowerShell 에서 실행합니다.
- `--dry-run` 은 아무것도 옮기지 않고 계획만 보여 줍니다.
- `--require-volume-label` 이 실제 볼륨 레이블과 다르면 아무것도 옮기지 않습니다.
- `aster prewarm` 은 `aster prefetch --execute --method pvm` 의 별칭입니다.
- `--zone-mib` 값은 예시입니다. 게임의 핫 데이터 크기에 맞춰 정하십시오.

## 작동 원리

- **접근 순서 물리 재배치.** 추적한 읽기 순서대로 핫 블록을 NTFS 안에서 제자리 이동(FSCTL_MOVE_FILE)해 한 구간에 모읍니다. 이동 전후 SHA-256 으로 내용이 같은지 확인합니다.
- **존 순서 스트림.** 재배치된 구간을 접근 순서대로 순차에 가깝게 읽습니다. 게임이 뜨는 순간 시작하며, 읽은 데이터는 OS 파일 캐시에 올라갑니다.
- **사전 예열.** 게임을 켜기 전에 같은 구간을 RAM 파일 캐시에 올려 둡니다. 가용 RAM 이 구간 크기와 `min_free_ram_mib` 의 합 이상일 때만 실행합니다.

## 설정

설정 파일은 `%ProgramData%\AsterCache\config.json` 입니다. 관리 창에서 편집하는 것을 권장합니다. 직접 고칠 때는 JSON 의 백슬래시를 `\\` 로 씁니다.

```json
{
  "watcher": "poll",
  "games": [
    {
      "name": "frostpunk",
      "exe_names": ["Frostpunk.exe"],
      "trace": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.prefetch.json",
      "plan": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.plan.json",
      "origin_root": "D:\\Games\\Frostpunk",
      "allowed_roots": ["D:\\Games"],
      "volume_label": "GAMES",
      "max_stream_mib": 4096,
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

| 키 | 뜻 |
|---|---|
| `watcher` | 게임 시작 감지 방식. `poll`(기본) 또는 `etw`. `etw` 는 관리자 권한이 필요하며 실패하면 폴링으로 내려갑니다. |
| `games[].name`, `exe_names` | 게임 이름과 실행 파일 이름 목록 |
| `trace`, `plan` | 추적 파일과 계획 파일 경로 |
| `origin_root` | 게임 설치 폴더 |
| `allowed_roots` | 재배치를 허용하는 폴더 |
| `volume_label` | 대상 볼륨 레이블. 다르면 재배치하지 않습니다. |
| `max_stream_mib` | 한 번의 스트림·예열이 읽는 상한(기본 4096) |
| `prewarm.on_service_start` | 서비스 시작 때 예열 실행(기본 `true`) |
| `prewarm.trigger_exes` | 이 프로그램이 뜨면 예열 실행(예: 런처). 기본은 비어 있음 |
| `prewarm.min_free_ram_mib` | 예열 뒤에도 남겨 둘 가용 RAM(기본 4096) |
| `prewarm.cooldown_min` | 예열 사이의 최소 간격(기본 30분) |

서비스는 시작할 때만 설정을 읽습니다. 게임을 추가하거나 제거한 뒤에는 서비스를 다시 시작하십시오.

## 안전 장치

- **내용 검증.** 재배치 전후 SHA-256 을 비교합니다. 내용이 다르면 실패로 기록합니다.
- **저널과 되돌리기.** 이동한 조각마다 저널(JSON Lines)을 남기며, `revert` 로 예전 물리 배치로 되돌립니다. 데이터 내용은 바뀌지 않습니다.
- **허용 폴더와 볼륨 고정.** `allowed_roots` 밖의 파일과 `volume_label` 이 다른 볼륨은 건드리지 않습니다.
- **중단 처리.** 재배치 중 게임이 시작되면 이동 중이던 한 조각만 끝내고 멈춥니다. 저널에 재개 지점을 남기고, 게임이 끝난 뒤 같은 계획으로 이어갑니다.
- **데이터 폴더 권한.** `%ProgramData%\AsterCache` 는 SYSTEM 과 Administrators 만 쓸 수 있고, 일반 사용자는 읽기만 합니다. 서비스를 제거해도 데이터 폴더는 남으며, `cs_service.ps1 -Action uninstall -PurgeData` 를 명시했을 때만 지웁니다.
- **권한 확인.** 관리자 권한이 없으면 재배치와 되돌리기는 아무것도 바꾸지 않고 `not_admin` 으로 끝납니다.

## 알려진 제한

- 드라이버나 파일 시스템 필터가 아닙니다. 게임의 I/O 경로를 가로채지 않습니다. 커널 미니필터(`kernel/`)는 실험용 소스이며 적재하지 않습니다.
- 추적은 디스크까지 간 읽기만 기록합니다. 이미 캐시에 있던 읽기는 빠질 수 있습니다.
- 실제 게임은 Frostpunk 1종만 측정했습니다. 안티치트 게임(MW4, LoL 등)은 검증하지 않았습니다.
- 지속성은 재배치 뒤 10시간까지 확인했습니다. 24시간 이후는 측정하지 않았습니다.
- SMR 드라이브(WD20EZAZ) 1대에서만 측정했습니다. CMR 드라이브는 측정하지 않았습니다.
- 콜드 조건은 관리자 권한으로 스탠바이 목록을 비워 만들었습니다. 드라이브 내장 캐시는 비우지 않았습니다.
- 재배치는 파일을 실제로 옮기므로 처음 한 번은 시간이 오래 걸립니다. 이득은 재배치 뒤 반복 로딩에서 나타납니다.
- 정전, SSD 손상, BitLocker, DirectStorage 환경은 시험하지 않았습니다.
- 설치 프로그램(`aster.iss`)은 아직 컴파일해 보지 않았습니다.
- 트레이만 갱신되면 서비스는 옛 버전으로 남습니다. 트레이가 버전 차이를 표시하며, 설치 프로그램으로 서비스를 다시 설치해야 합니다.

## 문제 해결

**설정을 저장할 권한이 없다고 나옵니다.**
데이터 폴더는 관리자만 쓸 수 있습니다. 관리 창을 관리자 권한으로 다시 실행하십시오.

**게임을 추가하거나 제거했는데 반영되지 않습니다.**
서비스는 시작할 때만 설정을 읽습니다. 설정 탭에서 서비스를 정지한 뒤 다시 시작하십시오.

**트레이에 "서비스 재설치 필요"가 표시됩니다.**
트레이 갱신은 서비스 실행 파일을 바꾸지 않습니다. 설치 프로그램으로 서비스를 다시 설치하십시오.

**재배치 명령이 `not_admin` 으로 끝납니다.**
재배치, 되돌리기, 볼륨 명령은 관리자 PowerShell 에서 실행하십시오.

**예열이 실행되지 않았습니다.**
건너뛴 이유는 `service.log` 에 남습니다. 가용 RAM 이 부족하거나, 게임이 실행 중이거나, 쿨다운 중이거나, 계획 파일이 없는 경우입니다. `aster status` 의 `prewarm` 항목에서도 확인할 수 있습니다.

## 프로젝트 구조

- `csharp/` — 최종 C# 구현. 명령행 도구 `aster`, 서비스, 관리 창, 설치 프로그램 스크립트, 시험
- `kernel/` — 커널 미니필터 AsterFlt 소스. 실험용이며 적재하지 않습니다.
- `prefetch/`, `relayout/`, `bench/` — Python 참조 구현(선읽기, 재배치, 측정 도구). C# 의 교차 검증 기준입니다. 명령과 구성은 [docs/PYTHON_TOOLS.md](docs/PYTHON_TOOLS.md) 를 보십시오.
- `scripts/` — 참조 구현의 실험용 관리자 PowerShell 스크립트
- `docs/` — 설계 문서, 게임 적용 안내, 측정 결과 요약
- `tests/` — Python 참조 구현의 단위·회귀 시험
- `results/` — 측정 결과 JSON 과 로그
- `.github/workflows/` — C# 빌드와 시험, 커널 빌드 워크플로
- 루트의 `*.py` — 이전 버전과 SQLite 캐시 참조 구현, 시험·측정 스크립트
- `VALIDATION_REPORT.md` — 검증 보고서

## 개발

```bat
dotnet build csharp/AsterCache.sln -c Release
dotnet test csharp/AsterCache.sln -c Release --filter "Category!=Live"
dotnet format csharp/AsterCache.sln --verify-no-changes
py -3.12 -m unittest discover -s tests
```

- `Category=Live` 시험은 `D:\AsterBench` 와 Python 참조 구현이 필요하므로 위에서 제외했습니다.
- Python 참조 구현은 Python 3.11 이상이며 외부 패키지가 없습니다. Python 3.11.4 와 3.12.10 에서 검증했습니다. 명령과 구성은 [docs/PYTHON_TOOLS.md](docs/PYTHON_TOOLS.md) 를 보십시오.

## 라이선스

MIT License. 자세한 내용은 [LICENSE](LICENSE) 파일을 보십시오. Copyright (c) 2026 DO0OG.

## 면책

- 안티치트가 적용된 게임과 온라인 계정에 묶인 게임에서는 검증하지 않았습니다.
- 재배치는 파일 데이터를 실제로 옮깁니다. 중요한 데이터가 있는 드라이브에서 쓰기 전에 백업하고, 먼저 테스트 폴더에서 실행하십시오.
- MIT 라이선스에 따라 무보증으로 제공합니다. 측정 수치는 한 PC 에서 얻은 결과입니다.
