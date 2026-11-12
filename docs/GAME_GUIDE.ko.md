[English](GAME_GUIDE.md) | **한국어**

# 실제 게임에 적용하는 방법

이 안내서는 AsterCache 가 하드디스크에 하는 일, 조심할 점, 게임 종류별 적용 방법을 다룹니다. 자동 모드가 이 모든 것을 대신 해 줍니다([자동 모드](AUTO_MODE.ko.md)). Python 참조 도구로 직접 하는 수동 절차는 끝에 있습니다. 아래 명령은 저장소 최상위 폴더(`D:\Git\AsterCache`)에서 실행하며, 빌드와 pip 설치는 필요 없습니다.

검증 범위: 트레이스 변환, 계획, 선읽기 실행, 실행 스크립트는 D:\AsterBench의 실제 파일 경로로 만든 Procmon 형식 CSV 3개(각 2,000행)로 끝까지 실행해 확인했습니다. Python 의 관리자 단계(재배치 `probe`, `apply`, `revert`)는 비관리자로 `not_admin`이 나오는 것까지만 확인했고, 게임에서는 실행하지 않았습니다. 2026-11-02 에는 실제 게임 Frostpunk(D:\Frostpunk, 안티치트 없음)로 C# 도구의 추적·재배치·재생·사전 예열을 끝까지 수행했습니다. 안티치트 게임은 사용 기록만 있고 통제된 검증은 하지 않았습니다.

## 하드디스크에 미치는 영향

기능별로 게임이 설치된 드라이브에 하는 일입니다.

| 기능 | 읽기 | 쓰기 | 제한 |
|---|---|---|---|
| 트레이스 | Windows 이벤트 추적으로 관찰만 합니다 | 데이터 폴더의 파일(트레이스, 계획, 저널, 로그) | 1800초 또는 6144 MiB 에서 멈춤(`trace_cap_seconds`, `trace_cap_mib`) |
| 재배치 | 옮기는 조각 | **유일한 큰 쓰기.** 옮기는 조각을 같은 드라이브의 새 자리에 다시 씁니다. 파일 내용은 바뀌지 않습니다 | 아래 "재배치 상한" 참고 |
| 예열 | 존을 RAM 파일 캐시로 | 없음 | RAM 예산: 물리 RAM 의 절반, 가용 RAM 은 `prewarm.min_free_ram_mib` 이상 유지 |
| 예열 유지 | 10분마다(`rewarm_minutes`) 1 MiB 표본 8개. 캐시가 식었으면 존을 다시 읽음 | 없음 | RAM 예산 안의 게임만 |
| 깨워 두기 | 런처가 떠 있거나 평소 플레이 시간대에 10분마다 4 KiB | 없음 | `auto.keep_awake`. 전원 설정은 바꾸지 않음 |
| ORBIT 보정 | 표본 스트림당 12 MiB 남짓, 캐시를 거치지 않음([ORBIT](ORBIT.ko.md)) | 없음 | 게임이 없고 디스크가 한가할 때만 |
| SSD 보조 저장(기본 꺼짐) | 복사할 파일 | HDD 가 아니라 SSD 에 복사 | 게임당 예산 8 GiB([자동 모드](AUTO_MODE.ko.md#ssd-보조-저장-선택)) |

- **재배치 상한.** 존은 기본 최대 2048 MiB(`max_zone_mib`)입니다. 24시간에 최대 4번(`max_relocations_per_day`) 재배치합니다. 하루 최대 8 GiB(`max_relocate_gib_per_day`, SMR 로 보이는 드라이브는 2 GiB), 회차당 최대 2 GiB(SMR 로 보이면 1 GiB)입니다.
- **걸리는 시간.** 재배치는 2 GB 에 약 5분이 걸렸습니다. Frostpunk 에서는 SMR HDD 한 대에서 639 MB 를 131 s(4.9 MB/s), 이어서 212 MB 를 45 s 에 옮겼습니다. 서비스 로그에서 핫 집합 10 GB 의 예열은 128 ~ 416 s 가 걸렸습니다([측정 결과 요약](RESULTS_SUMMARY.ko.md)). 그동안 드라이브는 바쁩니다.
- **수명·발열·소음은 재지 않았습니다.** AsterCache 를 쓸 때의 드라이브 수명, 온도, 소음을 잰 적이 없어서 아무것도 주장하지 않습니다. 쓰기는 위의 재배치 쓰기뿐이고, 하루 상한과 회차 상한이 그 양을 제한합니다.
- **SMR 드라이브.** 대량 쓰기 뒤 SMR 드라이브는 한동안 느려질 수 있습니다. 시험한 SMR 드라이브에서 흩어진 쓰기 3 GB 직후 첫 콜드 읽기는 58.8 s(평소 24 s)였고, 2 GB 이동 뒤 콜드 읽기가 19.5 s → 33 s 로 느려졌으며 회복에 25분 ~ 1.5시간이 걸렸습니다. 서비스 로그에서 재배치 뒤 회복 확인에 걸린 시간은 6분에서 149분까지 관찰됐고 옮긴 양과 비례하지 않았습니다. 그래서 서비스는 다음 작업 전에 회복을 기다립니다([자동 모드](AUTO_MODE.ko.md#7-드라이브-회복-대기)의 드라이브 회복 대기). CMR 드라이브는 재지 않았습니다. 재배치는 기본으로 4 MiB 씩 나눠 옮깁니다.
- **플레이 중에는 하지 않습니다.** 재배치는 게임과 런처가 꺼져 있고 드라이브가 한가할 때(30초 동안 4 MiB/s 미만, AC 전원 또는 배터리 50% 이상)만 합니다. 예열은 게임이 시작되면 다음 64 MiB 묶음 경계에서 양보합니다. 이동 중 게임이 시작되면 이동 중이던 조각을 끝내고 멈췄다가 나중에 저널로 이어갑니다.
- **RAM.** 예열한 데이터는 일반 파일 캐시여서 프로그램이 메모리를 필요로 하면 Windows 가 버릴 수 있습니다. 기본으로 예열은 물리 RAM 의 절반까지, 첫 실행 선행 읽기(기본 꺼짐)는 4분의 1(4 GiB 상한)까지 씁니다.

## 주의사항

- **먼저 백업하십시오.** AsterCache 는 파일 데이터를 물리적으로 옮깁니다. 중요한 데이터가 든 드라이브는 쓰기 전에 백업하십시오.
- **이동 중에는 PC 를 끄거나 절전으로 두지 마십시오.** 이동은 저널로 이어갈 수 있지만, 쓰기 중 정전은 시험하지 않았습니다. 노트북에서는 AC 전원이나 배터리 50% 이상일 때까지 재배치가 기다립니다.
- **드라이브를 Windows 드라이브 최적화에서 제외하십시오.** Windows 는 주 1회 예약 작업으로 HDD 를 디프래그하며 이것이 재배치한 배치를 되돌릴 수 있습니다. 시작 메뉴에서 "드라이브 조각 모음 및 최적화"(Defragment and Optimize Drives)를 열고, 최적화 예약의 "설정 변경"을 눌러 "선택"으로 드라이브 목록을 열고 게임 드라이브의 체크를 해제합니다. 직접 `defrag D:`도 실행하지 마십시오. 그래도 배치가 흐트러지면 서비스가 알아채 복구하는데, 이것도 쓰기입니다.
- **수동 재배치 전에는 게임과 런처를 끄고** 작업 관리자에서 관련 프로세스가 남아 있지 않은지 확인하십시오.
- **패치나 파일 복구는 파일을 새로 씁니다.** 게임 업데이트나 런처 복구가 배치를 풀거나 계획을 거부당하게 할 수 있습니다. 서비스는 옛 계획을 `.stale` 로 치우고 다음 실행 때 다시 트레이스합니다. 수동으로 쓴다면 패치 뒤에 `verify` 를 돌리십시오.
- **빈 공간.** 존에는 같은 크기의 연속 빈 공간이 게임 드라이브에 있어야 합니다. 여유를 남겨 두십시오.
- **압축 폴더.** 압축, 스파스, 암호화 속성이 있는 파일과 재파싱 지점은 재배치가 건너뜁니다. 재배치 이득만 못 받을 뿐입니다. 게임 폴더를 NTFS 압축한 적이 있다면 `compact /u /s:D:\Games\CoD4` 로 먼저 풀어 둘 수 있습니다.
- **안티치트 게임.** 먼저 [안티치트 호환](ANTICHEAT.ko.md)을 읽으십시오.
- **시험하지 않은 것.** 쓰기 중 정전, SSD 손상, BitLocker, DirectStorage, CMR 드라이브. SSD 보조 저장은 Frostpunk 로딩 읽기만 측정했고, 복사 중 정전, SSD 가 가득 찬 경우, 안티치트 게임은 시험하지 않았습니다.

### 안전 장치

- **내용 확인.** 재배치 전후에 SHA-256 표본을 비교합니다. 내용이 다르면 실패로 기록합니다. 더 확실히 하려면 큰 파일 몇 개를 적용 전후에 직접 해시해 보십시오.

  ```powershell
  Get-FileHash 'D:\Games\CoD4\큰파일.ff' | Select-Object Hash   # 적용 전후에 각각 실행해서 비교
  ```
- **저널과 되돌리기.** 옮긴 조각마다 저널(JSON Lines)을 남기고, `revert` 는 예전 물리 배치로 되돌립니다. 이전 저널은 `<게임>.jsonl.<UTC 시각>` 으로 보존합니다. 저널을 지우지 마십시오.
- **허용 폴더와 볼륨 고정.** `allowed_roots` 밖의 파일과 `volume_label` 이 다른 볼륨은 건드리지 않습니다.
- **중단 처리.** 재배치 중 게임이 시작되면 지금 옮기던 조각만 끝내고 멈춥니다. 저널에 재개 지점을 남깁니다.
- **며칠에 나눠 하기.** 옮길 구역을 다른 파일이 차지했거나 계획이 바뀌면, 이미 옮긴 부분은 다시 옮기지 않고 남은 부분만 새 빈 영역으로 다시 계획합니다. 같은 디스크의 게임들이 같은 빈 영역을 차지하지 않습니다.
- **권한.** `%ProgramData%\AsterCache` 는 SYSTEM 과 Administrators 만 쓸 수 있습니다. 관리자가 아니면 재배치와 되돌리기는 아무것도 바꾸지 않고 `not_admin` 으로 끝납니다.
- 재배치 중 강제 종료는 실제 게임 데이터로 시험하지 않았습니다. VHDX 시험에서는 중단 5회 뒤 재개를 확인했습니다.

### 되돌리기와 남는 것

- **게임을 원래대로.** **게임** 탭에서 게임을 고르고 **되돌리기**를 누릅니다. 저널을 따라 예전 디스크 배치로 복원하며, 파일 내용은 처음부터 바뀌지 않았습니다. 명령줄은 `revert` 입니다(수동 절차 3단계).
- **끄기.** 설정 탭에서 **자동 모드 사용** 체크를 풀거나, 그 exe 를 `auto.ignore_exes` 에 넣습니다. 이미 옮긴 파일은 그대로 있습니다.
- **SSD 보조 저장.** `aster tier revert --all` 이 링크를 지우고 원본을 복구합니다. 사본은 `aster tier clean` 을 실행할 때까지 tier 폴더에 남습니다.
- **제거.** 제거 프로그램은 SSD 보조 저장 링크를 먼저 되돌린 뒤 프로그램과 서비스를 지웁니다. 데이터 폴더 `%ProgramData%\AsterCache`(설정, 계획, 저널, 로그)는 남기며, `cs_service.ps1 -Action uninstall -PurgeData` 를 실행해야만 지웁니다. 예전 디스크 배치로 돌리려면 제거하기 전에 **되돌리기**를 누르십시오.

## 어떤 경로가 맞나

| 내 게임 | 할 일 |
|---|---|
| 커널 안티치트가 없는 일반 게임 | 없습니다. 자동 모드가 찾고, 기록하고, PC 가 한가할 때 재배치하고, 예열합니다. [자동 모드](AUTO_MODE.ko.md)를 보십시오. |
| 안티치트가 있는 게임 | 플레이 전에 아래 [안티치트 게임](#안티치트-게임)과 [안티치트 호환](ANTICHEAT.ko.md)을 읽으십시오. |
| 계속 켜져 있는 유틸리티(Wallpaper Engine, OBS Studio 등) | 게임으로 등록하지 않습니다([자동 모드](AUTO_MODE.ko.md#1-발견)). |
| 핫 집합이 아주 큰 게임 | [큰 게임](#큰-게임)을 보십시오. |
| 단계를 직접 확인하고 싶을 때 | [직접 해 보기](USAGE.ko.md)나 여기의 [수동 절차](#수동-절차-python-참조-도구)를 쓰십시오. |
| 실제 게임으로 전 과정을 먼저 검증하고 싶을 때 | [Frostpunk 절차](#frostpunk-로-검증한-절차-2026-11-02)를 따르십시오. |

## 안티치트 게임

요즘 게임 대부분은 커널 수준 안티치트를 씁니다. AsterCache 가 하는 일과 하지 않는 일, 종류별 정책, 사용 기록은 [안티치트 호환](ANTICHEAT.ko.md)에 있습니다. 요약하면, 서비스 자기 프로세스에서 게임 파일을 읽기 전용으로 읽고, 실시간 ETW 세션으로 읽기를 관찰하며, 게임이 완전히 종료된 뒤에만 파일 클러스터를 옮깁니다. 코드 주입, 후킹, 커널 드라이버는 없습니다. 안티치트 게임은 사용 기록(Vanguard 게임 1종, Ricochet 게임 1종)만 있고 통제된 검증은 하지 않았으며, 계정 제재 여부는 보증하지 않습니다.

<details>
<summary>세부: 안티치트 게임을 위한 조언</summary>

- 자동 모드는 기본으로 켜져 있습니다. 안티치트가 있는 게임은 플레이 전에 설정 탭에서 자동 모드를 끄거나, 그 게임의 exe 를 `auto.ignore_exes` 에 넣으십시오.
- **Ricochet 게임에서는 Procmon을 쓰지 않는 것을 권합니다.** Procmon과 Ricochet를 함께 쓴 공개 보고는 찾지 못했고, 안전하다는 증거도 아닙니다. 다른 안티치트(BattlEye, EA)에서는 Procmon 드라이버 때문에 게임 실행이 거부되고, Procmon을 종료한 뒤에도 드라이버가 남아 재부팅이 필요했던 사례가 있습니다. 대안은 Windows 내장 ETW(`wpr`, `logman`)입니다. 다만 "ETW가 더 안전하다"는 것은 추측이며, Ricochet와 ETW의 충돌 여부를 다룬 보고도 찾지 못했습니다.
- **Call of Duty: Modern Warfare 4**(2026-10-23 출시)는 TPM 2.0, Secure Boot, Ricochet 커널 안티치트 드라이버가 필수입니다. Ricochet는 게임과 상호작용하는 애플리케이션을 감시하고 보고하며, 어떤 도구가 허용되는지 공개된 목록은 없습니다. 베타에서 오탐으로 영구 제재된 사례가 있었고 Activision이 오탐을 인정했습니다. 계정을 잃을 위험이 현실에 있습니다.
- 시도 순서: 먼저 커널 안티치트가 없는 게임으로 전 과정을 검증합니다(예: Steam의 Call of Duty 4: Modern Warfare, 2007, 약 8 GB. 싱글플레이 캠페인만 쓰고 온라인 모드에는 접속하지 않습니다). 그다음 Call of Duty: Modern Warfare 4 는 오프라인 캠페인에서만 시험하고, 재배치할 때는 게임과 Battle.net(또는 Steam) 런처를 완전히 종료하며, 멀티플레이와 DMZ는 첫 시험에서 제외합니다.

</details>

## 큰 게임

Call of Duty: Modern Warfare 4 의 HDD 설치는 공식 지원 범위 밖이며, 이 도구가 줄이는 것은 정해진 로딩 구간의 읽기뿐이고 플레이 중 스트리밍 읽기는 줄이지 못할 수 있습니다. 선읽기 바닥 시간은 핫 집합 크기 ÷ 약 160 MB/s 입니다(2 GB 약 12.5초, 10 GB 약 62.5초). 그 게임의 로딩 구간 핫 집합 크기는 재지 않았습니다.

<details>
<summary>세부: 바닥 시간 표와 근거</summary>

Call of Duty: Modern Warfare 4 의 HDD 설치는 공식 지원 범위 밖입니다. Activision 공식 PC 요구 사양은 세 등급 모두 "SSD 115 GB 이상의 여유 공간"입니다. HDD 설치가 막히는지는 확인하지 못했습니다. 모든 모드에 온디맨드 텍스처 스트리밍이 있다고 하므로, HDD에서는 로딩 외에 플레이 중 끊김도 예상됩니다. 이 도구가 줄이는 것은 정해진 로딩 구간의 읽기뿐이고, 플레이 중 계속 발생하는 스트리밍 읽기는 줄이지 못할 수 있습니다. HDD에서 Call of Duty: Modern Warfare 4의 로딩 시간은 보고된 바가 없습니다.

재배치와 선읽기는 탐색 시간을 없애지만 읽는 바이트 수 자체는 줄이지 못합니다. 순차 읽기를 약 160 MB/s로 두면 선읽기 바닥 시간은 바이트 수 ÷ 160 MB/s입니다(1 GB = 1000 MB 기준, GiB로 재면 약 7% 늘어납니다). 이는 계산값이며 Call of Duty: Modern Warfare 4의 로딩 구간 핫 집합 크기는 재지 않았습니다. 참고로 시험 PC 에서 서비스가 기록한 Call of Duty 의 예열 핫 집합은 10,218 MiB 입니다([실사용 기록](RESULTS_SUMMARY.ko.md#실사용-기록)).

| 핫 집합 | 선읽기 바닥 시간 | 기대 |
|---:|---:|---|
| 2 GB 이하 | 약 12.5초 이하 | 탐색 제거 효과가 크게 나올 수 있음 |
| 5 GB | 약 31초 | 이득은 있을 수 있으나 선읽기 시간이 로딩과 비슷해질 수 있음 |
| 10 GB | 약 62.5초 | 선읽기 바닥이 로딩보다 길 수 있어 효과 제한적 |
| 20 GB | 약 125초 | 효과를 기대하기 어려움 |
| 30 GB | 약 188초 | 효과를 기대하기 어려움 |

핫 집합이 수 GB를 넘으면 선읽기 시간이 바닥이 되고 이득은 주로 탐색(seek) 제거에서 나옵니다. 합성 데이터셋(243 MB)에서는 선읽기가 1.1~1.7초였습니다. Frostpunk 는 읽기 패턴이 2.02 GB 였고, 재배치 존에서 HDD 순차 한계(약 135 MB/s)로 15.1 s 가 걸렸습니다.

</details>

## 수동 절차 (Python 참조 도구)

수동 경로는 Python 도구와 Procmon CSV 트레이스를 씁니다. 트레이스 수집(1단계), 계획(2단계), 관리자 PowerShell 에서 재배치(3단계), 선읽기와 함께 게임 실행(4단계), 유지 관리(5단계) 순서입니다. 자동 모드가 이 모든 것을 대신 해 주며, 수동 경로는 단계를 직접 확인하고 싶을 때 씁니다. 이 수동 절차 전체는 안티치트 게임으로 실행해 본 적이 없습니다.

<details>
<summary>세부: 1~5단계와 Call of Duty: Modern Warfare 4 적용</summary>

아래는 Python 도구와 Procmon 으로 직접 하는 수동 경로입니다. Windows 11 과 Python 3.11 이상(3.11.4와 3.12.10에서 시험)이 필요합니다. 게임은 D:(NTFS HDD)에 설치하고, 트레이스와 계획 파일은 게임 폴더 밖(예: `D:\AsterWork`)에 둡니다. 관리자 PowerShell은 3단계에서만 쓰고, 2, 4, 5단계는 일반 사용자로 실행합니다. 경로 끝에는 백슬래시를 붙이지 마십시오(CMD에서는 `"D:\Games\"`가 따옴표를 닫지 못합니다).

```powershell
mkdir D:\AsterWork
cd D:\Git\AsterCache
python --version
```

**Python 도구는 Procmon CSV만 읽습니다.** ETW `.etl` 변환은 C# CLI `aster trace --etl <파일> --exe <게임 exe> --origin-root <게임 폴더> --out <파일>` 로 합니다(S3 `cs_etw_check.ps1` 에서 `.etl` 로 시험했고, 그 과정에서 이름 해석 결함을 고쳤습니다). Python 의 `trace`·`plan` 명령은 `.etl`을 읽지 못합니다.

**1단계. 트레이스 수집**

게임이 로딩 중에 어떤 파일의 어느 구간을 읽는지 기록합니다. 같은 로딩을 3~5회 반복해 기록하면 매번 읽는 부분과 우연히 읽은 부분을 구분할 수 있습니다.

**안티치트가 없는 게임: Process Monitor**

1. Microsoft Sysinternals에서 Process Monitor(Procmon)를 내려받아 관리자 권한으로 실행합니다.
2. Filter > Filter...에서 아래 네 조건을 Include로 추가하고 Add, Apply를 누릅니다. `Process Name` `is` `게임 실행 파일.exe`, `Operation` `is` `ReadFile`, `Result` `is` `SUCCESS`, `Path` `begins with` `D:\Games\CoD4`(게임 설치 폴더).
3. Options > Select Columns에서 Time of Day, Process Name, PID, Operation, Path, Result, Detail이 켜져 있는지 확인합니다. **Operation, Path, Result, Detail은 필수입니다.** 화면을 다른 열로 정렬하지 마십시오. 도구는 CSV의 행 순서를 읽기 순서로 씁니다.
4. 캡처를 끄고(돋보기 아이콘, Ctrl+E) Edit > Clear Display(Ctrl+X)로 비운 뒤 캡처를 켭니다.
5. 게임을 실행해 로딩이 끝나는 지점(메인 메뉴나 레벨 시작 화면)까지 진행하고 캡처를 끕니다. 게임 종료는 그다음에 합니다.
6. File > Save...에서 `Events displayed using current filter`, 형식 `CSV`를 골라 `D:\AsterWork\run1.csv`로 저장합니다.
7. 게임을 완전히 종료하고 4~6을 반복해 `run2.csv`, `run3.csv`(가능하면 `run4.csv`, `run5.csv`)를 만듭니다. 매번 같은 로딩(같은 메뉴 경로, 같은 레벨)이어야 합니다.

이 도구는 프로세스 이름으로 거르지 않고 Path 접두사만 봅니다. Process Name 필터를 빼면 런처와 백신 같은 다른 프로세스의 읽기도 같은 폴더면 섞입니다.

**Ricochet 게임: ETW로 `.etl`만 남기기** (참고용, C# `aster trace --etl` 변환은 S3에서 검증). 이 `FileIO` 프로필 명령은 실행해 보지 않았고, S3 는 저장소의 `csharp\scripts\aster_fileio.wprp` 프로필로 시험했습니다. 수집기는 게임 시작 전에 켜고 종료 후에 끄며, 이상 징후(실행 거부, 경고, 계정 알림)가 있으면 즉시 중단합니다. 수집기 자체가 Ricochet에 어떻게 보이는지는 미검증이므로 첫 수집은 오프라인의 짧은 임무 하나로 합니다.

```powershell
# 관리자 PowerShell. 프로필 이름은 wpr -profiles 로 로컬에서 확인해야 합니다(미확인).
wpr -start FileIO -filemode
# ... 게임 실행 -> 로딩 완료 -> 게임 종료 ...
wpr -stop D:\AsterWork\ricochet_run1.etl
```

`.etl`은 C# CLI 의 `aster trace --etl <파일> --origin-root <게임 폴더> --out <파일> --dump-first 20` 으로 변환해 읽기 건수와 처음 20건을 확인할 수 있습니다. Ricochet 게임에서 Procmon 을 썼다면 Procmon 드라이버가 종료 후에도 남을 수 있으므로, **Procmon을 완전히 종료(File > Exit)하고 PC를 재부팅**한 뒤에 다음 수집이나 플레이를 하십시오.

**2단계. 계획 만들기**

**2a. 선읽기 계획 `plan.json`과 트레이스 변환**

```powershell
python astercache_v07.py plan --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --budget-mib 512 --out D:\AsterWork\plan.json

python astercache_v07.py trace --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --out D:\AsterWork\trace.json
```

`--prefix`는 게임 폴더입니다. 그 아래 경로의 읽기만 쓰며 `D:\Games\CoD4Other` 같은 이름이 비슷한 폴더는 걸러집니다. CSV마다 `{'rows': ..., 'read_success_rows': ..., 'parsed': ..., 'ignored': ...}` 한 줄이 나옵니다. `parsed`가 0이거나 `rows`에 비해 아주 작으면 Procmon 필터 또는 `--prefix`가 틀린 것입니다(시험 CSV에서는 `parsed`가 1,463~1,481이었습니다).

| 파일 | 만든 명령 | 여러 CSV를 합치는 방식 | 쓰는 곳 |
|---|---|---|---|
| `plan.json` | `plan` | 2회 이상 읽힌 64 KiB 블록만, 최근 실행에 가중치(0.85씩 감쇠), `--budget-mib`까지 | 선읽기(`prefetch.cli --trace`) |
| `trace.json` | `trace` | 모든 실행에서 한 번이라도 읽힌 64 KiB 블록의 합집합(중복 제거) | 선읽기, `game_launch.ps1` |
| `trace.run1.json`, `trace.run2.json`, ... | `trace` | 실행마다 따로, 읽은 순서 그대로 | `relayout.cli plan --trace` (실행마다 하나씩) |

재배치는 실행별 읽기 순서를 비교해 일관된 순서를 찾기 때문에 실행마다 따로 받습니다. 두 명령은 Procmon이 같은 파일을 다른 대소문자(`d:\games\a.bin`과 `D:\Games\A.bin`)로 기록해도 실제 파일의 대소문자로 통일합니다. 통일하지 않으면 같은 파일이 둘로 나뉩니다.

**2b. 재배치 계획 `relayout_plan.json`**

```powershell
python -m relayout.cli plan --trace D:\AsterWork\trace.run1.json D:\AsterWork\trace.run2.json D:\AsterWork\trace.run3.json `
  --origin-root D:\Games\CoD4 --zone-mib 512 --out D:\AsterWork\relayout_plan.json
```

PowerShell은 `trace.run*.json` 같은 와일드카드를 네이티브 명령에 넘겨주지 않으므로 파일을 하나씩 적습니다. `--out` 대신 `--dry-run`을 쓰면 통계만 봅니다(시험 CSV 3개, `--zone-mib 512`).

```
"sessions":3,"cluster_bytes":4096,
"stats":{"hot_bytes":205193216,"moved_bytes":205193216,"tasks":3029,"zone_used_clusters":50096,
         "overflow_bytes":0,"skipped_files":0,"current_runs":3049,"expected_runs":1,...}
```

- `hot_bytes`는 옮길 핫 데이터의 크기(여기서는 약 196 MiB)입니다. `--zone-mib`는 이보다 커야 하며, `overflow_bytes`가 0보다 크면 존이 모자란 것입니다. 핫 바이트의 1.2배 정도를 기준으로 삼으십시오.
- `current_runs`는 핫 데이터가 현재 몇 조각으로 흩어졌는지, `expected_runs`는 재배치 뒤 몇 조각인지입니다(1이면 한 구간). `skipped_files`는 위치를 알 수 없어 건너뛴 파일 수입니다.
- 3단계 `probe`의 `zone_candidate_lcn`이 `null`이면 연속 빈 공간이 없는 것이므로 `--zone-mib`를 줄이거나 공간을 비우십시오.

`relayout plan`은 계획과 함께 `relayout_plan.json.prefetch.json`을 씁니다. 재배치 존에 들어간 블록만, 존 순서대로 담은 선읽기 입력이므로 재배치한 뒤에는 `trace.json` 대신 이 파일을 넘깁니다. `python -m prefetch.cli --trace D:\AsterWork\relayout_plan.json.prefetch.json --origin-root D:\Games\CoD4 --full-file-ratio 2` 로 미리 볼 수 있고, `--execute` 없이는 읽지 않고 `bytes` 와 `requests` 만 보여 줍니다. `--min-sessions N`은 N개 미만의 실행에서만 읽힌 블록을 핫 집합에서 뺍니다.

**3단계. 재배치 (관리자 PowerShell)**

게임과 런처를 완전히 종료하고, 시작 메뉴에서 PowerShell을 우클릭해 "관리자 권한으로 실행"을 선택합니다.

```powershell
cd D:\Git\AsterCache

# 1) 읽기 전용 점검
python -m relayout.cli probe --volume D: --plan D:\AsterWork\relayout_plan.json

# 2) 모의 실행: 존 위치를 정해 계획 파일에 저장하고 아무것도 옮기지 않음
python -m relayout.cli apply --plan D:\AsterWork\relayout_plan.json --allowed-root D:\Games\CoD4 --dry-run

# 3) 실제 이동
python -m relayout.cli apply --plan D:\AsterWork\relayout_plan.json --allowed-root D:\Games\CoD4 --execute

# 4) 결과 확인
python -m relayout.cli verify --plan D:\AsterWork\relayout_plan.json
```

- `probe`: `free_match`가 `true`여야 하며 `false`면 중단합니다. `zone_candidate_lcn`은 숫자여야 합니다. 관리자가 아니면 `{"error":"not_admin"}`으로 끝납니다.
- `apply --dry-run`: `tasks_todo`가 0보다 크고 `skipped_files`에 예상 밖의 파일이 없어야 합니다. `bytes_todo`가 옮길 바이트입니다.
- `apply --execute`: `failed`가 빈 배열, `stopped`가 `null`, `sha_mismatch`가 빈 배열, `moved_bytes`가 `bytes_todo`와 같아야 합니다. 한 번 실행에 기본 2,048 MiB까지 옮기고 `"stopped":"write_cap"`으로 멈출 수 있습니다. 이때는 **같은 `--execute` 명령을 다시 실행**하면 이어서 진행합니다(`--write-cap-mib`로 한도를 올릴 수도 있습니다).
- `verify`: `"verdict":"contiguous"`여야 합니다.

저널은 `--journal`을 주지 않으면 `D:\AsterWork\relayout_plan.json.journal.jsonl`에 생깁니다. 되돌리기에 필요하므로 지우지 마십시오. 되돌리려면 다음과 같이 합니다(`--execute`를 빼면 아무것도 옮기지 않습니다).

```powershell
python -m relayout.cli revert --journal D:\AsterWork\relayout_plan.json.journal.jsonl --allowed-root D:\Games\CoD4 --execute
```

소요 시간: D:\AsterBench의 243 MB(1,048개 작업)는 1~2분이 걸렸습니다. 게임의 핫 데이터는 GB 단위이므로 이에 비례해 더 걸릴 것으로 예상합니다(5 GB면 수십 분이라는 외삽이며 실측이 아닙니다). Frostpunk 수치는 [하드디스크에 미치는 영향](#하드디스크에-미치는-영향)에 있습니다.

**4단계. 게임 실행 (선읽기와 동시에)**

일반 PowerShell에서 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\game_launch.ps1 `
  -Trace D:\AsterWork\relayout_plan.json.prefetch.json -OriginRoot D:\Games\CoD4 -Game D:\Games\CoD4\iw3sp.exe
```

스크립트는 선읽기(`prefetch.cli`, 존 순서)를 숨은 백그라운드 프로세스로 시작하고 곧바로 게임을 실행한 뒤, 스트림이 끝날 때까지 기다려 소요 시간과 읽은 바이트를 출력합니다. 시험 출력(게임 대신 `cmd.exe /c exit`, 캐시가 따뜻한 상태라 속도 참고용이 아닙니다)

```
prefetch started (pid 26632), game started 0.07 s later (pid 32532)
stream done: 0.46 s since launch (read time 0.06 s), 195.7 MiB, 2169 requests, errors 0, 3,445 MiB/s
```

`errors`가 0이 아니면 트레이스가 낡은 것입니다(패치 등).

- `-Game`에는 실행 파일 외에 `battlenet://...`, `steam://rungameid/...` 같은 URI도 줄 수 있습니다. 런처가 게임을 띄우는 구조에서는 선읽기가 게임 시작에 앞서 끝나지 않을 수 있습니다.
- `-NoPrefetch`를 붙이면 선읽기 없이 게임만 실행합니다(비교용). 게임 인자는 `-GameArgs '+set','x'`처럼 배열로 줍니다.
- 기본 `-FullFileRatio 2`는 기록된 블록만 읽습니다. 재배치 뒤 파일의 나머지는 핫 존에 없으므로 파일 전체를 읽는 것(`prefetch.cli` 기본값 0.3)은 시간 낭비입니다(읽는 양이 205 MB에서 402 MB로 늘었습니다).

**효과 재는 법.** PC를 재부팅한 직후(캐시가 비어 있는 상태)에 `-NoPrefetch`로 게임을 실행해 스톱워치로 로딩 시간(실행부터 로딩 완료까지)을 잽니다. 재부팅하고 3회 반복합니다. 같은 방식으로 `-NoPrefetch` 없이 3회 잰 뒤 두 결과의 중앙값을 비교합니다. 재부팅 없이 반복하면 Windows 파일 캐시 때문에 항상 빠르게 나와 비교가 무의미합니다.

**5단계. 유지 관리**

- 게임 패치, 파일 검증/복구 뒤에는 `verify`로 배치가 깨졌는지 봅니다. `verdict`가 `non-contiguous`이거나 `apply`가 `source_changed`를 보고하면 1단계부터 트레이스를 다시 모으고 2, 3단계를 다시 합니다. 패치된 파일은 크기/수정 시간이 바뀌어 기존 계획이 거부됩니다.
- 게임 드라이브를 Windows 자동 드라이브 최적화에서 제외하십시오([주의사항](#주의사항)).
- 시간이 지난 뒤에도 배치가 유지되는지는 합성 데이터셋에서 10시간까지 확인했습니다. 24시간 확인은 없습니다.

**Call of Duty: Modern Warfare 4에 직접 적용하는 절차**

이 수동 절차 전체는 Call of Duty: Modern Warfare 4로 한 번도 실행해 보지 않았고, Ricochet와 ETW/Procmon 수동 수집의 충돌 여부는 미검증입니다. 서비스의 자동 모드 사용 기록은 별개입니다([안티치트 호환](ANTICHEAT.ko.md)).

1. **설치 경로.** D: 드라이브에 설치하고, 런처가 보여 주는 설치 폴더(예: `D:\...\Call of Duty`)를 그대로 `--prefix`, `--origin-root`, `--allowed-root`에 씁니다. 설치 용량은 공식 115 GB(SSD 기준)이지만 팩 구성에 따라 2차 자료에서는 200 GB 안팎으로도 나옵니다. 설치 화면의 실제 용량을 기준으로 하고, 재배치용 연속 빈 공간(핫 집합 크기 이상)도 D:에 남겨 두십시오. 셰이더 컴파일 등 설치 후 작업이 끝난 뒤에 시작합니다.
2. **기준 구간.** 오프라인 캠페인에서 같은 지점(예: 메인 메뉴에서 임무 하나를 시작해 조작 가능한 화면이 뜰 때까지)을 로딩 1회로 정하고, 스톱워치로 먼저 잽니다(`-NoPrefetch` 기준값).
3. **트레이스(1단계).** ETW 를 권합니다. 첫 수집은 짧은 임무 하나로 하고, 실행 거부, 경고, 계정 알림이 있으면 즉시 중단합니다. Procmon 은 위험을 감수할 때만 씁니다.
4. **계획(2단계).** Procmon CSV가 있으면 `trace`, `plan`, `relayout plan`을 실행합니다. ETW만 모은 경우는 C# 변환을 실제 `.etl`로 검증하기 전까지 여기서 멈춥니다.
5. **재배치(3단계).** 런처와 게임을 완전히 종료하고, 설치 후 업데이트가 없는 상태에서 합니다. 끝난 뒤 런처의 파일 검사/복구를 한 번 돌려 영향이 없는지 확인합니다.
6. **실행과 측정(4단계).** `game_launch.ps1`로 시작합니다. 런처가 게임을 띄우는 구조이면 `-Game`에 런처 URI 를 줍니다.
7. **유지(5단계).**

</details>

## Frostpunk 로 검증한 절차 (2026-11-02)

2026-11-02 에 두 스크립트로 Frostpunk(9.2 GB, 안티치트 없음, D: SMR HDD)의 추적, 재배치, 콜드 재생, SSD 비교를 끝까지 실행했습니다. `csharp\scripts\cs_e4_game.ps1` 과 `csharp\scripts\cs_e4_compare.ps1` 입니다. 측정값은 추적한 읽기 패턴의 재생 시간이며, 게임을 실행한 전체 로딩 시간이 아닙니다([VALIDATION_REPORT.ko.md](../VALIDATION_REPORT.ko.md)).

<details>
<summary>세부: 두 스크립트와 권장 설정</summary>

대상은 D:\Frostpunk(9.2 GB, 안티치트 없음, D: SMR HDD)입니다. 아래 두 스크립트를 관리자 PowerShell 에서 저장소 최상위 폴더 기준으로 실행했습니다. C# CLI(`csharp\publish\cli\aster.exe`)가 먼저 게시되어 있어야 합니다(`csharp\README.md`).

1. **추적과 재배치, 콜드 재생.** 게임을 켜지 않은 상태에서 실행합니다.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_game.ps1
   ```

   스크립트는 ETW 실시간 추적(`aster trace --live`)을 시작한 뒤 게임이 뜨고 끝날 때까지 기다립니다(`-WaitGameSeconds`, 기본 600초). 첫 맵 로딩까지 진행하고 게임을 종료하면 다음 순서로 진행합니다. 계획 → 계획 파일의 SHA-256 기록 → 스탠바이 비우기 뒤 콜드 재생 3회 → probe → `apply --dry-run` → `apply --execute` → `verify` → 스탠바이 비우기 뒤 SHA-256 재확인 → 재배치 뒤 콜드 재생 3회와 선읽기+재생 3회. 결과는 `results\csharp_e4_<날짜시각>\summary.json`에 남습니다. 되돌리기 명령은 같은 폴더의 `README.txt`에 있으며 스크립트는 실행하지 않습니다.

2. **핫 집합 전체를 담는 존으로 다시 재배치하고 SSD 와 비교.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_compare.ps1 -ZoneMiB 2048
   ```

   1단계의 추적 파일로 2 GiB 존에 다시 계획해 적용합니다(이미 옮긴 데이터는 그대로). 같은 읽기 패턴을 `C:\AsterShadow\Frostpunk` 의 희소 사본에 만들어 SSD 기준을 3회 잽니다. 그다음 스탠바이를 비우고 `aster prefetch --method pvm` 으로 존을 올린 뒤 60초를 기다려 재생을 3회 잽니다. 결과는 `results\csharp_e4cmp_<날짜시각>\summary.json`입니다.

권장 설정

- **존 크기는 핫 바이트 전체를 담게 잡습니다.** `cs_e4_game.ps1`은 존을 핫 바이트의 1.1배를 16 MiB 단위로 올린 값으로 잡고, `-MaxZoneMiB`를 넘으면 재배치를 건너뜁니다(`hot_set_too_large`). 존이 모자라면 `-MaxZoneMiB`를 핫 바이트 이상으로 주십시오.
- **사전 예열을 게임 시작 전에 끝냅니다.** 서비스 설정의 게임 항목에서 `prewarm.on_service_start`(기본 true)를 쓰거나, 런처를 `trigger_exes`에 넣습니다. 예열은 게임 시작보다 15초 이상 앞서 끝나야 한다는 것이 측정 전제입니다. 예열은 가용 RAM이 존 크기 + `min_free_ram_mib`(기본 4096 MiB) 이상일 때만 실행됩니다. 설정 형식은 `csharp\README.md`의 "두 동작 모드" 절에 있습니다.
- 재배치는 게임을 끈 상태에서만 합니다(스크립트가 실행 중이면 멈춥니다).

결과와 한계는 [VALIDATION_REPORT.ko.md](../VALIDATION_REPORT.ko.md)의 2026-11-02 절에 있습니다. 측정값은 추적한 읽기 패턴의 재생 시간이며, 게임을 실행한 전체 로딩 시간이 아닙니다.

</details>

## 한계

첫 실행에는 이득이 없고, 트레이스에 없는 읽기는 돕지 못하며, 읽기 패턴 재생을 측정한 게임은 Frostpunk 1종뿐입니다(게임을 실행한 전체 시간은 재지 않음). Call of Duty 와 League of Legends 는 서비스 로그 집계만 있습니다.

<details>
<summary>세부: 한계 전체</summary>

- **첫 실행은 이득이 없습니다.** 트레이스를 모으는 실행과 재배치 시간은 비용만 듭니다. 이득은 재배치 이후 반복 로딩에서만 나옵니다.
- **첫 실행이 꼭 필요한가요?** 재배치에는 트레이스가 필요합니다. 첫 실행에서 기록이 생기기 전에는 공유 프로필(`auto.shared_profiles`, 주간 내려받기)을 먼저 찾고, 그다음 게임의 Windows 프리페치 기록을 봅니다. 두 자료 모두 부트 트레이스가 되어 첫 실행 전에도 재배치를 시작할 수 있습니다. 둘 다 없으면 자동 모드는 예열만 합니다. 폴더 전체가 RAM 예산 이하면 전체를, 아니면 게임 엔진 프로필의 파일을 예열합니다(`auto.full_prewarm`, `auto.engine_profiles`, 둘 다 기본 켬). 두 예열의 효과는 아직 측정하지 않았습니다. 다른 PC 에서 만든 트레이스는 `aster profile import`
  로 가져올 수 있습니다(`aster profile export` 참고).
- 첫 실행 선행 읽기(E7)는 기본으로 꺼져 있습니다. [RESULTS_SUMMARY.ko.md](RESULTS_SUMMARY.ko.md#e7-첫-실행-선행-읽기) 를 보십시오.
- **트레이스에 없는 읽기는 느립니다.** 기록한 로딩과 다른 레벨/모드, 플레이 중 텍스처 스트리밍 같은 읽기는 이 도구가 돕지 못합니다. 한 번만 읽힌 블록은 존에 들어가지 않아 선읽기 때 탐색이 생길 수 있으므로 3~5회 실행의 일관성이 중요합니다.
- **안티치트.** 이 도구는 게임에 주입하거나 후킹하지 않고 별도 프로세스로 파일만 읽습니다. 그래도 안티치트가 "게임 파일에 접근하는 앱"으로 볼 수 있는지는 통제된 검증을 하지 않았고, 계정 제재 여부는 보증하지 않습니다. `FSCTL_MOVE_FILE`이 게임의 무결성 검사에 걸리는지도 공식 확인이 없습니다(파일 내용이 바뀌지 않으므로 해시 검사에는 걸리지 않을 것으로 추정). 재배치 뒤에는 런처의 파일 검사/복구를 한 번 돌려 영향이 없는지 확인하십시오.
- **읽기 패턴 재생을 측정한 게임은 Frostpunk 1종뿐입니다.** 2026-11-02 에 재생으로 측정했고, 게임을 실행한 전체 시간은 재지 않았습니다. Call of Duty 와 League of Legends 는 서비스 로그 집계만 있고, 통제된 검증이나 로딩 시간 측정이 아닙니다([실사용 기록](RESULTS_SUMMARY.ko.md#실사용-기록)).

</details>

## 참고: League of Legends (Vanguard)

League of Legends는 Riot Vanguard를 쓰며 Vanguard도 커널 수준 안티치트이므로 Ricochet와 같은 주의를 적용합니다. 2026-11-02 웹 조사에서 Procmon, ETW, 디스크 디프래그와의 충돌 보고는 찾지 못했으나, 그것이 안전의 증거는 아닙니다. 조사 범위는 얕습니다.

<details>
<summary>세부: 웹 조사, 설치 용량, 패치, 핫셋</summary>

League of Legends는 Riot Vanguard를 씁니다. Vanguard도 커널 수준의 안티치트이므로 Ricochet와 같은 주의를 적용합니다. 아래는 2026-11-02에 웹으로 조사한 결과이며, 조사 범위가 얕습니다. 각 항목의 출처 등급을 [확인] (공식·1차), [2차] (블로그·요약), [추측] (근거 없는 추정)으로 표시합니다.

**Vanguard와 수집·재배치 도구**

- Procmon, ETW, 디스크 디프래그와 Vanguard가 충돌한다는 공개 보고는 찾지 못했습니다. 그렇다고 안전하다는 근거도 없으며, 계정 제재 위험을 0이라고 볼 수 없습니다.
- Riot의 "Incompatible Software" 문서는 "호환되지 않는 소프트웨어·드라이버"라고만 쓰며 도구 범주나 위 도구 이름을 언급하지 않습니다. [확인] https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software
- 차단 대상은 주로 치트에 악용될 수 있는 취약 드라이버입니다. 차단될 때 오류에 파일 경로가 표시됩니다. [2차] https://dotesports.com/valorant/news/riot-is-shipping-a-hotfix-to-increase-vanguards-compatibility-with-cheat-vulnerable-drivers
- Procmon은 PROCMON24.SYS 필터 드라이버를 올리므로 세 도구 가운데 위험이 가장 큽니다. 트레이스와 재배치는 Vanguard를 끈 상태에서 하고, 플레이 중에는 수집·재배치 도구를 모두 닫으십시오. [추측]
- ETW 세션과 디스크 재배치 API는 별도 드라이버를 올리지 않아 위험이 상대적으로 낮을 것으로 추정합니다. 공개 근거는 없습니다. [추측]

**설치 용량**

- Riot 공식 문서는 일반 설치 약 35~40GB라고 밝힙니다. [확인] https://support.riotgames.com/en-us/league-of-legends/client/league-of-legends-takes-too-much-space
- 2차 블로그는 16\~25GB라고 해 자료가 상충합니다. 정확한 값은 직접 측정해야 하며, 그 전까지는 20\~40GB를 기준으로 봅니다. [2차] https://nokiamob.net/2026/02/19/how-big-is-league-of-legends-in-2026/
- Game 폴더에는 큰 파일이 약 400개 있습니다. 릴리스 전체는 30만 개 이상의 chunk로 나뉩니다. [확인] https://www.riotgames.com/en/news/supercharging-data-delivery-new-league-patcher

**패치와 재배치 유지**

- 새 패처는 파일을 통째로 교체하지 않고 chunk 단위로 제자리 갱신합니다. Riot은 파일 배치를 버전 간 안정적으로 유지하도록 패키징을 바꿨다고 밝혔습니다. [확인] 같은 출처
- 따라서 재배치한 파일이 패치 뒤에도 남을 가능성이 있습니다. 실측 보고는 없습니다. [추측]
- 패처가 파일을 삭제한 뒤 다시 내려받는 경로라면 재배치가 깨집니다. 패치 뒤에는 배치 상태를 다시 확인하십시오. [추측]

**로딩 특성**

- HDD와 SSD의 League of Legends 로딩 시간 차이를 보여 주는 수치는 찾지 못했습니다. 일반 게임에서 HDD가 SSD보다 2~3배 느리다는 일반론만 확인했습니다. [2차] https://www.gamepressure.com/newsroom/hdd-sshd-ssd-m2-speed-benchmark-of-hard-drives/z1692
- League of Legends는 네트워크와 CPU 대기 비중이 커서 디스크 이득이 작을 수 있습니다. [추측]
- 로딩 중 읽는 바이트 양은 공개 자료가 없으므로 직접 트레이스로 측정해야 합니다.

**권장 순서**

1. Vanguard가 없는 다른 게임에서 트레이스, 재배치, 선읽기 파이프라인을 먼저 검증합니다.
2. League of Legends는 Vanguard를 끈 상태와 오프라인 상태에서 읽기량만 측정해 HDD 이득의 여지를 판단합니다. 트레이스 수집은 이 상태에서만 합니다.
3. 재배치 기록은 ETW 대신 파일 읽기 순서를 직접 남기는 방식도 검토합니다. 플레이 중에는 수집 도구를 쓰지 않고, 재배치 뒤 읽기 속도만 비교합니다.
4. 패치 뒤 재배치가 유지되는지 확인합니다. 계정 위험이 없다고 확인된 범위에서만 다음 단계로 넘어갑니다.

**추정 핫셋과 League client 예열**

- League of Legends 추정 핫셋은 전 모드 기준 약 9 GiB입니다. Global·UI·Common·맵 11·12 기본 규칙 약 5.3 GiB에 맵 30·453·22 약 3.6 GiB를 더한 시험 PC 실측 합계이며 Companions는 제외했습니다. 존 상한이 8 GiB 이면 뒤쪽(맵 22·TFT, Companions)이 잘릴 수 있습니다.
- `riot.champ_select_prewarm`(기본 꺼짐)을 켜면 챔피언 선택 중 로컬 League of Legends 클라이언트 API(`127.0.0.1`)로 고른 챔피언과 맵 파일을 미리 RAM에 읽습니다. 게임 안 파일은 건드리지 않습니다.
- 한국 계정에서 그 클라이언트 API 를 쓰는 정책은 미확인입니다. 이 기능을 켜기 전에 직접 확인하십시오.

</details>
