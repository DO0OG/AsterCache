[English](README.md) | **한국어**

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

> **베타.** AsterCache 는 베타 프리릴리스로 배포합니다. 파일 데이터를 실제로 옮기므로 중요한 데이터가 있는 드라이브는 백업한 뒤 쓰십시오. 검증 범위는 SMR 드라이브(WD20EZAZ) 1대와 실제 게임 1종(Frostpunk)이며, CMR 드라이브와 안티치트 게임은 시험하지 않았습니다([알려진 제한](#알려진-제한) 참고).

## 목차

- [무엇을 하는가](#무엇을-하는가)
- [주요 기능](#주요-기능)
- [실측 결과](#실측-결과)
- [스크린샷](#스크린샷)
- [시스템 요구 사항](#시스템-요구-사항)
- [설치](#설치)
- [빠른 시작](#빠른-시작)
- [자동 모드](#자동-모드)
- [작동 원리](#작동-원리)
- [설정](#설정)
- [안전 장치](#안전-장치)
- [안티치트 호환](#안티치트-호환)
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
- **자동 모드(기본 켬).** 지원 런처 라이브러리와 게임 시작 감지로 HDD 게임을 찾아 첫 실행을 트레이스하고, PC 가 쉬는 동안 재배치합니다. [자동 모드](#자동-모드) 를 보십시오.
- **서비스.** 게임 시작을 감지해 동시 선읽기를 실행하고, 예열과 재적용을 관리합니다. Windows 서비스(LocalSystem, 자동 시작)로 상주합니다.
- **상태와 로그.** 서비스와 감시기 상태, 마지막 예열 결과, 게임별 배치 상태, 로그를 탭에서 확인합니다.
- **측정.** 게임별로 재배치 전후 콜드 재생 시간을 잽니다.
- **관리 창.** 관리자 권한이 없으면 상단 안내 띠가 나타나며, 관리자 권한으로 다시 실행할 수 있습니다.
- **명령행 `aster`.** 관리 창의 같은 작업을 명령으로 실행합니다. 자동화에 씁니다.
- **한국어·영어 화면.** 관리 창과 명령행이 두 언어를 지원합니다([언어](#언어) 참고).

## 실측 결과

수치는 추적한 읽기 패턴을 재생한 시간입니다. 게임을 실행한 전체 로딩 시간이 아닙니다.

| 대상 | 측정 | 결과 |
|---|---|---|
| 합성 데이터셋 2.1 GiB | HDD 콜드 읽기 → 재배치 후 선읽기 | 17.1 s → 1.14 s (재배치 10시간 뒤에도 유지) |
| 합성 데이터셋 2.1 GiB | 같은 조건 SSD 콜드 읽기 | 0.76~1.22 s |
| Frostpunk, 첫 맵 로딩 읽기 2.02 GB | HDD 콜드 재생 → 재배치 후 동시 선읽기 | 24.3 s → 11.93 s (3회 중앙값, 각주 참고) |
| Frostpunk | 사전 예열 뒤 재생 | 0.48 s |
| Frostpunk, 같은 읽기 패턴 | NVMe SSD 콜드 재생 | 3.8 s |
| 서비스 | 게임 시작 감지 지연 (폴링, p95) | 58 ms |

- HDD 는 WD20EZAZ(SATA, D:), SSD 는 NVMe(C:)입니다. 측정 환경은 Windows 11 Pro 10.0.26200, 메모리 32 GiB 입니다.
- Frostpunk 결과는 게임 1종을 1회 추적한 뒤 재생한 값입니다. 반복 로딩의 p50·p95 는 아직 없습니다.
- 합성 데이터셋의 SSD 값과 Frostpunk 의 NVMe 값은 데이터와 방법이 달라 직접 비교하지 않습니다.
- 사전 예열이 SSD 콜드보다 빠르려면 예열이 게임 시작보다 15 s 이상 앞서 끝나야 합니다. 메모리 압박이 있으면 효과가 줄어듭니다.
- 11.93 s 값은 E6 실험에서 나왔습니다. 재배치가 이제 기본으로 핫 데이터를 전부 한 존으로 옮깁니다(보존 끔). 8 MiB 이상 연속 구간을 제자리에 두던 이전 방식의 값은 15.1 s 였습니다. [docs/research_cold_limit.md](docs/research_cold_limit.md) 를 보십시오.
- 더 많은 표와 해석은 `docs/RESULTS_SUMMARY.md` 와 `VALIDATION_REPORT.md` 에 있습니다.

## 스크린샷

관리 창(게임 탭)과 상태 탭입니다. `AsterCache.Tray.exe --smoke --screenshot <png>` 로 만든 예시 화면이라 드라이브와 게임 정보는 샘플 값입니다.

<p align="center"><img src="docs/images/main-window.png" width="800" alt="관리 창 게임 탭"></p>
<p align="center"><img src="docs/images/status-tab.png" width="800" alt="관리 창 상태 탭"></p>

## 시스템 요구 사항

- Windows 11 x64. 검증 환경은 Windows 11 Pro 10.0.26200 입니다.
- 직접 빌드할 때는 .NET 10 SDK 가 필요합니다. 저장소의 `csharp/global.json` 은 SDK 10.0.400 을 씁니다(latestPatch).
- 트레이스 수집(ETW), 재배치(`apply`, `revert`), 서비스 설치에는 관리자 권한이 필요합니다.
- 재배치 대상은 NTFS 볼륨이어야 합니다. 원본과 캐시를 비교하려면 서로 다른 물리 장치가 필요합니다.

## 설치

### ① 릴리스(베타)

[GitHub Releases](https://github.com/DO0OG/AsterCache/releases) 의 **v0.9.0-beta.2** 프리릴리스에서 `AsterCache-0.9.0-beta.2-win-x64.zip` 을 받습니다. 안에 `AsterCache.Tray.exe`(관리 창), `aster.exe`(명령행), `AsterCache.Service.exe`(서비스)가 들어 있습니다. 압축을 풀고 `AsterCache.Tray.exe` 를 관리자 권한으로 실행한 뒤, 설정 탭에서 서비스를 설치하고 시작합니다.

### ② 설치 프로그램

`AsterCache-v<버전>-win-x64-setup.exe` 설치본은 관리 창, `aster.exe`, 서비스, 런타임 전체를 `Program Files\AsterCache` 에 설치합니다. `AsterCache` 서비스를 자동 시작으로 등록하고 데이터 폴더 권한을 설정합니다. 설치 중에 다음을 고를 수 있으며, 모두 기본으로 켜져 있습니다.

- 언어: English 또는 한국어. 고른 언어는 `config.json` 의 `"language"` 에 기록됩니다.
- 바탕화면 바로가기, 시작 메뉴 바로가기
- Windows 로그인 때 관리 창을 열고 트레이 아이콘을 상주(바로가기는 모든 사용자 시작 프로그램 폴더)
- AsterCache 서비스 설치와 시작
- 마지막 화면에서 관리 창 실행

같은 설치본을 다시 실행하면 업그레이드합니다. 서비스를 멈추고 파일을 바꾼 뒤 서비스를 다시 등록하고 시작합니다. 제거하면 서비스를 멈추고 지우며 트레이 앱을 종료합니다. 데이터 폴더 `%ProgramData%\AsterCache` 는 남으며, 제거 화면에서 이를 안내합니다. 서명되지 않아 SmartScreen 경고가 뜰 수 있습니다.

릴리스 워크플로가 이 설치본을 GitHub 릴리스에 `.exe` 로 첨부합니다. 설치본 정의는 [csharp/README.ko.md](csharp/README.ko.md) 에 있습니다. 직접 빌드할 때는 다음과 같이 합니다.

```bat
ISCC.exe /DAppDir=<관리 창 publish 경로> /DAppVersion=<버전> csharp\packaging\windows\aster.iss
```

### ③ 직접 빌드

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
- 전역 옵션 `--lang ko|en` 으로 출력 언어를 정합니다([언어](#언어) 참고).

## 자동 모드

자동 모드는 기본으로 켜져 있습니다. 보통은 게임을 등록하거나 따로 설정할 필요가 없습니다. 각 부분은 설정 탭의 **자동 모드** 묶음이나 `config.json` 에서 끌 수 있습니다.

### 단계

1. **발견.** 서비스가 시작한 1분 뒤와 그 뒤 6시간마다 지원 런처 라이브러리(아래)를 읽습니다. HDD(회전 디스크) 볼륨에 설치된 게임은 `"source": "auto"` 로 자동 등록합니다. 볼륨 레이블이 비었거나 32자를 넘으면 등록하지 않고 로그를 남기며, 같은 exe 이름이나 겹치는 폴더가 이미 등록되어 있으면 건너뜁니다. 게임 목록 상한은 32개입니다. 가득 차면 런처 발견은 멈추고, 실제로 실행되어 잡힌 게임이 가장 오래 쓰지 않은 자동 항목을 밀어냅니다.
2. **트레이스.** 트레이스가 없는 등록 게임이 시작되면 서비스가 `AsterCacheAutoTrace` 라는 실시간 ETW 세션으로 읽기를 기록합니다. 게임 종료, 600초(`trace_cap_seconds`), 6144 MiB(`trace_cap_mib`) 중 먼저 오는 때 멈춥니다. 결과는 `plans\<이름>.trace.json` 에 저장되며, 수동 **트레이스 수집** 버튼이 만드는 것과 같은 파일입니다. 시작을 감지한 실행은 이미 로딩이 시작된 뒤이므로, 트레이스는 다음 실행부터 기록합니다.
3. **계획과 재배치.** 트레이스가 생기고 게임이 꺼져 있으며 PC 가 `idle_minutes`(기본 3분) 동안 연속으로 쉬고 있으면, 한 번에 게임 하나씩 처리합니다. 존을 계획하고(핫 데이터 × 1.1, `max_zone_mib` 를 넘지 않음), 수동 재배치와 같은 Applier 로 옮기며(저널과 SHA-256 표본 검사), 배치를 확인합니다. 쉬고 있다는 뜻은 게임 드라이브 처리량 4 MiB/s 미만, CPU 20% 미만, AC 전원이거나 배터리 50% 이상입니다. 24시간에 최대 `max_relocations_per_day`(기본 2)번만 재배치하며, 게임마다 자동 첫 재배치는 한 번입니다. 이후 유지는 기존 주기 검사가 맡습니다. 이동 중 게임이 시작되면 이동 중이던 조각을 끝내고 멈추며, 나중에 저널로 이어갑니다. 게임이 꺼진 상태에서 데이터 검사가 실패하거나 오류가 나면 저널대로 되돌리고 실패로 기록합니다.
4. **예열.** 서비스가 시작할 때 최근에 플레이한 게임 `prewarm_recent`(기본 1)개를 예열합니다. 지원 런처가 뜨면 그 런처의 최근 게임 `prewarm_recent`개를 예열합니다. 예열을 유지하기 위해 `rewarm_minutes`(기본 10, 0이면 끔)마다 존의 8곳을 1 MiB씩 읽어 잽니다. 평균이 2 ms 를 넘으면 파일 캐시에서 밀려난 것이므로, `min_free_ram_mib` 여유를 지키며 다시 예열합니다. 게임이 실행 중이면 하지 않습니다.
5. **고정(선택).** `prewarm.pin` 을 `true` 로 두면(기본 꺼짐, RAM 16 GiB 이상에서만 동작) 예열한 페이지를 서비스가 읽기 전용 매핑으로 열어 두어 캐시에서 덜 밀려나게 합니다. 가용 RAM 이 여유 아래로 내려가거나 게임·런처가 시작되면 즉시 해제하므로, 런처 업데이트가 파일을 고칠 수 있습니다. 트리거 프로그램으로 뜬 예열은 고정하지 않습니다. 고정의 효과는 아직 측정하지 않았습니다.

### 지원 런처

| 런처 | 게임 목록을 읽는 방법 | 자동 등록 게임의 예열 트리거 |
|---|---|---|
| Steam | `libraryfolders.vdf` 와 `appmanifest` 파일. 실행 파일은 게임 폴더에서 가장 큰 `.exe` 이며, 설치 프로그램·재배포 파일·크래시 도구는 제외합니다 | `steam.exe` |
| Epic Games Store | `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` 의 `LaunchExecutable` | `EpicGamesLauncher.exe` |
| GOG Galaxy | 레지스트리 `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games` | `GalaxyClient.exe` |
| Ubisoft Connect | 레지스트리 `Launcher\Installs` 의 `InstallDir` 값 | `upc.exe`, `UbisoftConnect.exe` |

GOG 와 Ubisoft 읽기는 아직 실제 PC 에서 확인하지 않았습니다.

발견을 지원하지 않는 런처: Battle.net, EA 앱, Riot, Xbox(PC Game Pass). 이들과 런처 없이 설치한 게임은 아래 휴리스틱으로만 잡힙니다. `Battle.net.exe`, `RiotClientServices.exe`, `EADesktop.exe` 는 예열 트리거 프로그램으로만 적혀 있으며, 서비스가 이 런처들의 라이브러리를 읽지는 않습니다.

### 휴리스틱 감지

등록되지 않은 프로세스가 HDD 볼륨의 exe 로 시작해 20초 안에 256 MiB 이상을 읽으면 게임으로 보고 등록합니다. 제외 대상은 Windows 폴더, AsterCache 자신, 런처와 도구 프로그램, `auto.ignore_exes` 의 exe 입니다. 시작 부분은 이미 놓쳤으므로 트레이스는 다음 실행부터 기록합니다.

### 설정

```json
{
  "max_zone_mib": 2048,
  "auto": {
    "enabled": true,
    "discover": true,
    "trace": true,
    "relocate": true,
    "prewarm_recent": 1,
    "idle_minutes": 3,
    "trace_cap_seconds": 600,
    "trace_cap_mib": 6144,
    "max_relocations_per_day": 2,
    "rewarm_minutes": 10,
    "ignore_exes": []
  }
}
```

- `"auto": {"enabled": false}` 는 자동 모드 전체를 끕니다. `discover`, `trace`, `relocate` 는 각 단계만 끕니다. `max_zone_mib` 는 `"auto"` 로 줄 수도 있습니다.
- 수동 버튼(등록, 트레이스 수집, 계획 만들기, 재배치 실행, 지금 예열)은 전과 같이 동작합니다.
- 서비스는 상태를 `%ProgramData%\AsterCache\auto_status.json` 에 씁니다. 이 파일은 서비스만 씁니다.
- 관리 창 상태 탭에는 게임마다 "자동: Frostpunk 발견 → 트레이스 수집 중 → 재배치 대기 → 재배치 중 → [완료]" 형태의 줄이 나오고, 게임 탭에는 출처·단계·안티치트 열이 있습니다. 트레이 알림은 발견과 재배치 완료 두 가지만 보냅니다.
- `aster status` 출력에 `"auto"` 키가 있고, `aster discover [--json]` 은 런처 게임 목록을 읽기 전용으로, 안티치트 표시와 함께 보여 줍니다.
- 수동 등록 게임도 `auto.relocate` 가 켜져 있으면 트레이스가 생긴 뒤 PC 가 쉴 때 자동으로 재배치됩니다. 원치 않으면 `auto.relocate` 를 끄거나(관리 창 설정 탭에서 "재배치" 체크 해제), 그 게임 exe 를 `auto.ignore_exes` 에 넣으십시오.
- Windows 가 있는 드라이브(시스템 드라이브)와 SSD 의 게임은 자동 등록·트레이스·재배치 대상이 아닙니다.
- 관리 창에서 지운 자동 등록 게임은 exe 가 `auto.ignore_exes` 에 들어가 다시 자동 등록되지 않습니다. 목록은 최대 64개이며, 넘으면 오래된 것부터 빠집니다.
- 자동 적용 하루 상한(`max_relocations_per_day`)은 재배치를 시작한 시각부터 24시간 기준으로 셉니다.
- 자동 적용이 실패하면 저널대로 되돌리되, 되돌리는 것은 그 자동 적용분만입니다.

### 효과

자동 모드의 속도 효과는 아직 측정하지 않았습니다. 측정 결과가 나오면 [docs/RESULTS_SUMMARY.md](docs/RESULTS_SUMMARY.md) 에 추가할 예정입니다.

## 작동 원리

- **접근 순서 물리 재배치.** 추적한 읽기 순서대로 핫 블록을 NTFS 안에서 제자리 이동(FSCTL_MOVE_FILE)해 한 구간에 모읍니다. 이동 전후 SHA-256 으로 내용이 같은지 확인합니다.
- **존 순서 스트림.** 재배치된 구간을 접근 순서대로 순차에 가깝게 읽습니다. 게임이 뜨는 순간 시작하며, 읽은 데이터는 OS 파일 캐시에 올라갑니다.
- **사전 예열.** 게임을 켜기 전에 같은 구간을 RAM 파일 캐시에 올려 둡니다. 가용 RAM 이 구간 크기와 `min_free_ram_mib` 의 합 이상일 때만 실행합니다.

## 설정

설정 파일은 `%ProgramData%\AsterCache\config.json` 입니다. 관리 창에서 편집하는 것을 권장합니다. 직접 고칠 때는 JSON 의 백슬래시를 `\\` 로 씁니다.

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
| `language` | 화면 언어. `auto`(기본), `ko`, `en`. `auto` 는 Windows 표시 언어를 따르며 한국어가 아니면 영어를 씁니다. |
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
| `auto` | 자동 모드 설정. 생략한 키는 기본값을 씁니다. [자동 모드](#자동-모드) 를 보십시오. |
| `max_zone_mib` | 자동 계획의 존 크기 상한(MiB, 기본 2048), 또는 `"auto"`(물리 RAM ÷ 4, 2048~8192 MiB 사이) |
| `games[].source` | 자동으로 찾은 게임은 `auto`, 직접 등록한 게임은 `manual` |
| `games[].anticheat` | 설치 폴더에서 찾은 안티치트 흔적(예: `EasyAntiCheat`). 게임 탭에 표시되며 동작은 바꾸지 않습니다. |
| `prewarm.pin` | 예열한 데이터를 서비스가 읽기 전용 매핑으로 열어 둡니다. 기본 `false`, RAM 16 GiB 이상에서만 동작합니다. [안티치트 호환](#안티치트-호환) 을 보십시오. |

서비스는 `config.json` 전체를 5초마다 다시 읽습니다(크기나 수정 시각이 바뀔 때). 그래서 게임을 등록·제거하거나 감시기를 바꿔도 다시 시작하지 않아도 반영됩니다. 새 설정이 검증이나 폴더 권한 검사에 실패하면 옛 설정을 유지하고 이유를 로그에 남깁니다.

### 언어

관리 창과 `aster` 명령행은 한국어와 영어로 쓸 수 있습니다.

- 언어는 `config.json` 의 `"language"` 로 정합니다. `"auto"`(기본, Windows 표시 언어를 따르며 한국어가 아니면 영어), `"ko"`, `"en"` 입니다. 관리 창 **설정 탭의 언어** 항목에서도 바꿀 수 있습니다.
- 명령행은 전역 옵션 `--lang ko|en` 과 환경변수 `ASTER_LANG` 을 받습니다. 우선순위는 `--lang` > `ASTER_LANG` > `config.json` > OS 언어 입니다.
- JSON 출력은 언어와 무관합니다.

## 안전 장치

- **내용 검증.** 재배치 전후 SHA-256 을 비교합니다. 내용이 다르면 실패로 기록합니다.
- **저널과 되돌리기.** 이동한 조각마다 저널(JSON Lines)을 남기며, `revert` 로 예전 물리 배치로 되돌립니다. 데이터 내용은 바뀌지 않습니다.
- **허용 폴더와 볼륨 고정.** `allowed_roots` 밖의 파일과 `volume_label` 이 다른 볼륨은 건드리지 않습니다.
- **중단 처리.** 재배치 중 게임이 시작되면 이동 중이던 한 조각만 끝내고 멈춥니다. 저널에 재개 지점을 남기고, 게임이 끝난 뒤 같은 계획으로 이어갑니다.
- **데이터 폴더 권한.** `%ProgramData%\AsterCache` 는 SYSTEM 과 Administrators 만 쓸 수 있고, 일반 사용자는 읽기만 합니다. 서비스를 제거해도 데이터 폴더는 남으며, `cs_service.ps1 -Action uninstall -PurgeData` 를 명시했을 때만 지웁니다.
- **권한 확인.** 관리자 권한이 없으면 재배치와 되돌리기는 아무것도 바꾸지 않고 `not_admin` 으로 끝납니다.

## 안티치트 호환

요즘 게임 대부분은 커널 수준 안티치트를 씁니다. 이 절은 AsterCache 가 하는 일과 하지 않는 일을 적어, 게임에 쓸지 판단할 수 있게 합니다. 안티치트 정책은 게임마다 다르고 바뀔 수 있습니다. AsterCache 는 아직 어떤 안티치트 게임에서도 시험하지 않았습니다.

### 하는 일

- 서비스가 자기 프로세스 안에서 게임 파일을 읽기 전용, 공유 모드(읽기·쓰기·삭제 공유)로 읽거나 매핑만 합니다.
- 실시간 ETW 커널 세션으로 파일 읽기 이벤트를 관찰합니다. 게임 프로세스에 대한 핸들은 필요하지 않습니다.
- 게임이 완전히 종료된 뒤에만 NTFS 조각 모음 API(`FSCTL_MOVE_FILE`)로 클러스터 위치를 옮깁니다. 파일 내용·크기·수정 시각은 바뀌지 않으며, 옮기기 전에 계획의 지문(크기와 수정 시각)을 확인합니다.

### 하지 않는 일

- 게임 프로세스에 코드 주입, 스레드 생성, 메모리 읽기·쓰기, 디버그 권한, 후킹, 오버레이, 핸들 복제를 하지 않습니다.
- 게임 프로세스는 `PROCESS_QUERY_LIMITED_INFORMATION`(조회 전용)으로만 열어 이름·경로·읽은 바이트를 봅니다. 보호된 프로세스가 이것마저 거부하면 ETW 이벤트로만 판정하고, 그 프로세스를 다시 열지 않습니다. 이 경우 거부가 확인된 뒤 20초 동안의 읽기로 판정하며, 앞 20초의 읽기는 세지 않습니다.
- 커널 드라이버를 쓰지 않습니다.

### 건너뛰거나 표시하는 파일

- 게임이 배타적으로 연 파일은 건너뜁니다.
- 자동 발견 때 설치 폴더 아래 깊이 4까지의 파일·폴더 이름만 봅니다. EasyAntiCheat, BattlEye, Vanguard(`vgc`, `vgk`), Ricochet 는 인식하고, 그 밖에 anticheat, xigncode, nprotect, gameguard 가 들어간 이름은 `unknown` 으로 표시합니다. 그런 이름이 있는 게임은 게임 탭과 `anticheat` 설정에 표시되고 로그 한 줄이 남습니다. 동작은 달라지지 않습니다. 이 PC 의 `aster discover` 에서는 Steam 게임 The Outlast Trials 가 `eac` 로 표시되었습니다.

### 속도 기능별로 안전하다고 보는 이유

아래는 각 기능을 안전하다고 보는 이유이며, 검증된 결과가 아닙니다.

- **예열:** 서비스 자기 프로세스에서 일반 파일 읽기를 합니다.
- **예열 유지:** 1 MiB 일반 파일 읽기 표본을 잽니다.
- **고정:** 서비스 자기 프로세스의 읽기 전용 매핑이며, 게임이나 런처가 시작되면 해제합니다.
- **재배치:** 게임이 종료된 뒤에만 하며, 파일 내용은 바뀌지 않습니다.
- **감지:** 조회 전용 프로세스 핸들 또는 ETW 이벤트로 합니다.

### 서명

실행 파일에는 코드 서명이 없어 SmartScreen 경고가 뜰 수 있습니다. 지금까지 확인된 영향은 이 경고뿐입니다. 서명 여부와 안티치트 판정의 관계는 확인하지 않았습니다.

### 한계와 권고

- 자동 모드는 기본으로 켜져 있습니다. 안티치트가 있는 게임은 플레이 전에 설정 탭에서 자동 모드를 끄거나, 그 게임의 exe 를 `auto.ignore_exes` 에 넣으십시오.
- 자동 트레이스도 수동 방식과 같은 종류의 ETW 수집을 씁니다. 그래서 [안내서](docs/GAME_GUIDE.ko.md)의 Ricochet·Vanguard 주의가 그대로 적용됩니다.
- 실제 안티치트 게임에서는 아직 시험하지 않았습니다. 현재 검증 범위 밖입니다.

## 알려진 제한

- 드라이버나 파일 시스템 필터가 아닙니다. 게임의 I/O 경로를 가로채지 않습니다. 커널 미니필터(`kernel/`)는 소스·빌드·정적 분석·INF 검증까지 마쳤지만, 서명·적재·실기기 시험은 하지 않았고 릴리스에 포함되지 않습니다. 부팅 직후 자동 예열은 사용자 모드 서비스가 담당합니다. [kernel/README.ko.md](kernel/README.ko.md) 를 보십시오.
- 추적은 디스크까지 간 읽기만 기록합니다. 이미 캐시에 있던 읽기는 빠질 수 있습니다.
- 실제 게임은 Frostpunk 1종만 측정했습니다. 안티치트 게임(MW4, LoL 등)은 검증하지 않았습니다.
- 지속성은 재배치 뒤 10시간까지 확인했습니다. 24시간 이후는 측정하지 않았습니다.
- SMR 드라이브(WD20EZAZ) 1대에서만 측정했습니다. CMR 드라이브는 측정하지 않았습니다.
- 콜드 조건은 관리자 권한으로 스탠바이 목록을 비워 만들었습니다. 드라이브 내장 캐시는 비우지 않았습니다.
- 재배치는 파일을 실제로 옮기므로 처음 한 번은 시간이 오래 걸립니다. 이득은 재배치 뒤 반복 로딩에서 나타납니다.
- 정전, SSD 손상, BitLocker, DirectStorage 환경은 시험하지 않았습니다.
- 릴리스 바이너리는 코드 서명이 없어 Windows 가 게시자 미확인 경고를 띄울 수 있습니다.
- 트레이만 갱신되면 서비스는 옛 버전으로 남습니다. 트레이가 버전 차이를 표시하며, 설치 프로그램으로 서비스를 다시 설치해야 합니다.
- Steam 라이브러리의 게임이 아닌 앱(예: Wallpaper Engine, YoloMouse 같은 도구)도 HDD 에 있으면 자동 등록될 수 있습니다. 원치 않으면 `auto.ignore_exes` 에 넣거나 관리 창 게임 탭에서 제거하십시오.
- 자동 트레이스는 세션이 시작되기 전 게임의 첫 읽기 일부를 놓칠 수 있습니다. 자동 트레이스와 자동 재배치는 실제 게임으로 아직 실행해 보지 않았고, 가짜 환경 시험만 했습니다.

## 문제 해결

**설정을 저장할 권한이 없다고 나옵니다.**
데이터 폴더는 관리자만 쓸 수 있습니다. 관리 창을 관리자 권한으로 다시 실행하십시오.

**게임을 추가하거나 제거했는데 반영되지 않습니다.**
서비스는 `config.json` 을 5초마다 다시 읽으므로 보통 다시 시작하지 않아도 반영됩니다. 새 설정이 검증이나 폴더 권한 검사에 실패하면 옛 설정을 유지하고 이유를 로그에 남깁니다.

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
