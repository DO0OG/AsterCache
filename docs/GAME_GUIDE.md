# 실제 게임에 적용하는 방법

이 문서는 D: HDD에 설치한 게임의 로딩을 AsterCache로 줄이는 전 과정을 순서대로 안내합니다. 빌드와 pip 설치는 필요 없습니다. 아래 명령은 모두 저장소 최상위 폴더(`D:\Git\AsterCache`)에서 실행합니다.

검증 범위: 트레이스 변환, 계획, 선읽기 실행, 실행 스크립트는 D:\AsterBench의 실제 파일 경로로 만든 Procmon 형식 CSV 3개(각 2,000행)로 끝까지 실행해 확인했습니다. 관리자 단계(재배치 `probe`, `apply`, `revert`)는 비관리자로 `not_admin`이 나오는 것까지만 확인했고, 실제 게임으로는 아직 한 번도 실행하지 않았습니다.

## 먼저 알아 둘 것 (대상 게임: Call of Duty: Modern Warfare 4)

- **HDD 설치는 공식 지원 범위 밖입니다.** MW4(2026-10-23 출시)의 Activision 공식 PC 요구 사양은 최소, 권장, 레이트레이싱 세 등급 모두 "SSD 115 GB 이상의 여유 공간"입니다. HDD 설치가 막히는지는 확인하지 못했습니다. 모든 모드에 온디맨드 텍스처 스트리밍이 있다고 하므로, HDD에서는 로딩 외에 플레이 중 끊김도 예상됩니다. 이 도구가 줄이는 것은 정해진 로딩 구간의 읽기뿐이고, 플레이 중 계속 발생하는 스트리밍 읽기는 줄이지 못할 수 있습니다.
- **TPM 2.0, Secure Boot, Ricochet 커널 안티치트 드라이버가 필수입니다.** Ricochet는 게임과 상호작용하는 애플리케이션을 감시하고 보고합니다. 어떤 도구가 허용되는지 공개된 목록은 없습니다. MW4 베타에서 오탐으로 영구 제재된 사례가 있었고 Activision이 오탐을 인정했습니다. 계정을 잃을 위험이 현실에 있습니다.
- **Ricochet 게임에서는 Procmon을 쓰지 않는 것을 권합니다.** Procmon과 Ricochet를 함께 쓴 공개 보고는 찾지 못했고, 안전하다는 증거도 아닙니다. 다른 안티치트(BattlEye, EA)에서는 Procmon 드라이버 때문에 게임 실행이 거부되고, Procmon을 종료한 뒤에도 드라이버가 남아 재부팅이 필요했던 사례가 있습니다. 대안은 Windows 내장 ETW(`wpr`, `logman`)입니다. 다만 "ETW가 더 안전하다"는 것은 추측이며, Ricochet와 ETW의 충돌 여부를 다룬 보고도 찾지 못했습니다.
- **현재 코드는 Procmon CSV만 읽습니다.** ETW로 모은 `.etl`을 변환하는 기능은 아직 없고, 다음에 구현할 예정입니다(관리자 권한, C# 최종본에서 TraceEvent 사용). 지금은 아래 1단계의 참고 명령으로 `.etl`을 남기는 것까지만 가능합니다. 따라서 **지금 이 안내서의 전 과정을 끝까지 따라 할 수 있는 대상은 커널 안티치트가 없는 게임입니다.**

### 권장 순서

1. **먼저 커널 안티치트가 없는 게임으로 전 과정을 검증합니다.** 예: Steam의 Call of Duty 4: Modern Warfare (2007, 약 8 GB, 커널 안티치트 없음). 싱글플레이(캠페인)만 쓰고 온라인 모드에는 접속하지 않습니다. 계정 위험 없이 이 도구의 효과를 실제 게임에서 처음 재어 보는 방법입니다.
2. 효과와 안전을 확인한 뒤 MW4에 적용하려면: 오프라인 캠페인에서만 시험하고, 게임과 Battle.net(또는 Steam) 런처를 완전히 종료한 상태에서 재배치하고, 멀티플레이와 DMZ는 첫 시험에서 제외합니다.
3. MW4의 트레이스 수집은 ETW 변환 기능이 구현된 뒤에 합니다. 그 전까지 MW4에서 Procmon을 쓰는 것은 권하지 않습니다.

## 요구 사항

- Windows 11, Python 3.11 이상(3.11.4와 3.12.10에서 시험). `python --version`으로 확인합니다.
- 게임은 D:(NTFS HDD)에 설치합니다. 트레이스와 계획 파일은 게임 폴더 밖(예: `D:\AsterWork`)에 둡니다.
- 관리자 PowerShell은 3단계(재배치)에서만 씁니다. 2, 4, 5단계는 일반 사용자로 실행합니다. Procmon은 스스로 관리자 권한을 요구합니다.

```powershell
mkdir D:\AsterWork
cd D:\Git\AsterCache
python --version
```

경로 끝에는 백슬래시를 붙이지 않는 것을 권합니다. 붙여도 이 도구는 처리하지만 CMD에서는 `"D:\Games\"`가 따옴표를 닫지 못해 오류가 납니다. 대소문자는 구분하지 않습니다.

## MW4에 직접 적용하는 절차

위 권고(안티치트 없는 게임으로 먼저 검증)는 참고이며, MW4에 직접 적용할 때의 실제 순서는 아래와 같습니다. 각 번호의 세부 명령은 뒤의 단계 설명에 있습니다. 이 절차 전체는 MW4로 한 번도 실행해 보지 않았고, Ricochet와 ETW/Procmon의 충돌 여부는 미검증입니다.

1. **설치 경로 확인.** Battle.net 또는 Steam(또는 Xbox PC 앱)에서 MW4를 D: 드라이브에 설치하고, 런처가 보여 주는 설치 폴더(예: `D:\...\Call of Duty`)를 그대로 `--prefix`, `--origin-root`, `--allowed-root`에 씁니다. 폴더 선택 메뉴 이름은 런처 버전에 따라 다르므로 설치 화면에서 확인하십시오. 설치 용량은 공식 115 GB(SSD 기준)이지만 팩 구성에 따라 2차 자료에서는 200 GB 안팎으로도 나옵니다. 설치 화면의 실제 용량을 기준으로 하고, 재배치용 연속 빈 공간(핫 집합 크기 이상)도 D:에 따로 남겨 두십시오. 설치가 끝나고 셰이더 컴파일 등 설치 후 작업이 끝난 뒤에 시작합니다.
2. **기준 구간 정하기.** 오프라인 캠페인에서 같은 지점(예: 메인 메뉴에서 캠페인 임무 하나를 시작해 조작 가능한 화면이 뜰 때까지)을 로딩 1회로 정합니다. 매번 같은 임무, 같은 메뉴 경로로 반복합니다. 멀티플레이와 DMZ는 첫 시험에서 제외합니다. 스톱워치로 이 구간의 현재 로딩 시간을 먼저 잽니다(4단계 "효과 재는 법"의 `-NoPrefetch` 기준값).
3. **트레이스 수집(1단계).** 두 가지 방법이 있습니다.
   - **ETW(권장, 단 변환기 미구현).** 관리자 PowerShell에서 게임 시작 전에 `wpr -start FileIO -filemode`(프로필 이름은 `wpr -profiles`로 확인), 게임을 실행해 기준 구간까지 로딩한 뒤 `wpr -stop D:\AsterWork\mw4_run1.etl`을 실행합니다. 현재 Python 도구는 `.etl`을 읽지 못합니다. **ETW 변환기는 다음 구현 예정**(관리자, C# 최종본에서 TraceEvent 사용)이므로 지금은 `.etl`을 3~5개 모아 두는 것까지만 가능합니다. 수집기 자체가 Ricochet에 어떻게 보이는지는 미검증이므로 첫 수집은 짧은 임무 하나로 하고, 실행 거부, 경고, 계정 알림이 있으면 즉시 중단합니다.
   - **Procmon(사용자가 위험을 감수할 때만).** Procmon을 먼저 켜고(필터 `Process Name`, `Operation`, `Result`, `Path` 설정과 캡처 시작), 그 뒤에 게임을 실행합니다. Procmon을 켜 둔 채 Ricochet 게임을 실행하는 것이 가장 위험이 큰 조합일 수 있으며 공개된 안전 보고는 없습니다. 기준 구간까지 로딩한 뒤 캡처를 끄고 CSV로 저장하고, 게임을 종료한 다음 **Procmon을 완전히 종료(File > Exit)하고 PC를 재부팅**합니다. Procmon 드라이버가 종료 후에도 남을 수 있어서, 다음 수집 실행과 실제 플레이는 모두 재부팅 뒤에 합니다. 그래서 수집 1회마다 재부팅이 필요합니다(3~5회). 평소 플레이할 때는 Procmon을 설치된 채로 두지 말고 켜지 마십시오.
4. **계획 만들기(2단계).** Procmon CSV가 있으면 `trace`, `plan`, `relayout plan`을 실행합니다. ETW만 모은 경우는 변환기가 나올 때까지 여기서 멈춥니다.
5. **재배치(3단계).** Battle.net(또는 Steam) 런처와 MW4를 완전히 종료하고, 작업 관리자에서 관련 프로세스가 남아 있지 않은지 확인한 뒤 관리자 PowerShell에서 실행합니다. 재배치는 설치 후 업데이트가 없는 상태에서 합니다(업데이트가 파일을 새로 쓰면 계획이 거부됩니다). 끝난 뒤 런처의 파일 검사/복구를 한 번 돌려 영향이 없는지 확인합니다.
6. **실행과 측정(4단계).** `game_launch.ps1`로 시작하고 2번의 기준 구간으로 `-NoPrefetch`와 비교합니다. 런처가 게임을 띄우는 구조이므로 `-Game`에 실행 파일이 아니라 런처 URI를 써야 할 수도 있습니다.
7. **유지(5단계).** D:를 Windows 자동 최적화에서 제외하고, 업데이트 뒤에는 `verify`로 확인해 필요하면 1단계부터 다시 합니다.

### MW4에서 핫 집합이 GB 단위일 때의 기대 효과

아래 값은 순차 속도 160 MB/s를 가정한 계산이며 MW4의 실제 핫 집합 크기는 모릅니다(미측정). 재배치와 선읽기는 탐색 시간을 없애지만 읽는 바이트 수 자체는 줄이지 못합니다.

| 핫 집합 | 선읽기 바닥 시간 | 기대 |
|---:|---:|---|
| 2 GB 이하 | 약 12.5초 이하 | 탐색 제거 효과가 크게 나올 수 있음 |
| 5 GB | 약 31초 | 이득은 있을 수 있으나 선읽기 시간이 로딩과 비슷해질 수 있음 |
| 10 GB | 약 62.5초 | 선읽기 바닥이 로딩보다 길 수 있어 효과 제한적 |
| 20 GB 이상 | 약 125초 이상 | 효과를 기대하기 어려움 |

HDD에서 MW4의 로딩 시간은 보고된 바가 없습니다. 온디맨드 텍스처 스트리밍은 플레이 중에도 읽기를 일으키므로 로딩 구간 밖의 끊김은 이 도구로 줄어들지 않을 수 있습니다.

## 0단계. 안전 점검

- 위 권장 순서의 1번부터 합니다. 안티치트가 있는 게임이나 온라인 계정과 연결된 게임에서는 처음 시험하지 않습니다.
- 재배치는 파일 내용을 바꾸지 않고 디스크 위의 물리적 위치(클러스터)만 옮깁니다(NTFS 디프래그 API `FSCTL_MOVE_FILE`). 적용할 때 도구가 파일마다 SHA-256 표본을 이동 전후로 비교하고, 결과의 `sha_mismatch`가 빈 배열이어야 합니다. 더 확실히 하려면 큰 파일 몇 개를 적용 전후에 직접 해시해서 같은지 봅니다.

```powershell
Get-FileHash 'D:\Games\CoD4\큰파일.ff' | Select-Object Hash   # 적용 전후에 각각 실행해서 비교
```

- 되돌리기: 3단계의 `revert` 명령이 저널을 거꾸로 적용해 예전 물리 배치로 돌립니다. 데이터 내용은 바뀌지 않으므로 되돌리지 않아도 게임 파일은 안전합니다.
- 게임을 패치하거나 파일 검증/복구를 돌리면 파일이 새로 쓰여 배치가 풀리거나 계획이 거부됩니다. 5단계를 보십시오.
- SMR HDD는 대량 쓰기(재배치) 때 쓰기가 느려질 수 있습니다. 이 도구는 기본으로 4 MiB씩 나눠 옮기지만 실제 게임 크기로는 시험하지 않았습니다.
- 재배치 중 강제 종료에 대한 시험은 아직 하지 않았습니다. 이동 중에는 PC를 끄거나 절전 모드로 두지 않습니다.
- 압축, 스파스, 암호화 속성이 있는 파일과 재파싱 지점은 재배치가 자동으로 건너뜁니다(`apply` 결과의 `skipped_files`에 이유가 나옵니다). 그런 파일은 재배치 이득만 못 받을 뿐 문제는 생기지 않습니다. 게임 폴더를 NTFS 압축한 적이 있다면 `compact /u /s:D:\Games\CoD4`로 먼저 풀어 둘 수 있습니다.

## 1단계. 트레이스 수집

게임이 로딩 중에 어떤 파일의 어느 구간을 읽는지 기록합니다. 같은 로딩을 3~5회 반복해 기록하면 매번 읽는 부분과 우연히 읽은 부분을 구분할 수 있습니다.

### 안티치트가 없는 게임: Process Monitor

1. Microsoft Sysinternals에서 Process Monitor(Procmon)를 내려받아 관리자 권한으로 실행합니다.
2. Filter > Filter...에서 아래 네 조건을 Include로 추가하고 Add, Apply를 누릅니다.
   - `Process Name` `is` `게임 실행 파일.exe`
   - `Operation` `is` `ReadFile`
   - `Result` `is` `SUCCESS`
   - `Path` `begins with` `D:\Games\CoD4` (게임 설치 폴더)
3. Options > Select Columns에서 Time of Day, Process Name, PID, Operation, Path, Result, Detail이 켜져 있는지 확인합니다. **Operation, Path, Result, Detail은 필수입니다.** 화면을 다른 열로 정렬하지 마십시오. 도구는 CSV의 행 순서를 읽기 순서로 씁니다.
4. 캡처를 끄고(돋보기 아이콘, Ctrl+E) Edit > Clear Display(Ctrl+X)로 비운 뒤 캡처를 켭니다.
5. 게임을 실행해 로딩이 끝나는 지점(메인 메뉴나 레벨 시작 화면)까지 진행하고 캡처를 끕니다. 게임 종료는 그다음에 합니다.
6. File > Save...에서 `Events displayed using current filter`, 형식 `CSV`를 골라 `D:\AsterWork\run1.csv`로 저장합니다.
7. 게임을 완전히 종료하고 4~6을 반복해 `run2.csv`, `run3.csv`(가능하면 `run4.csv`, `run5.csv`)를 만듭니다. 매번 같은 로딩(같은 메뉴 경로, 같은 레벨)이어야 합니다.

이 도구는 프로세스 이름으로 거르지 않고 Path 접두사만 봅니다. Process Name 필터를 빼면 런처와 백신 같은 다른 프로세스의 읽기도 같은 폴더면 섞입니다.

### Ricochet 게임(MW4 등): ETW로 `.etl`만 남기기 (참고용, 아직 변환 불가)

아래는 Windows 내장 `wpr`로 파일 I/O를 기록하는 참고 명령이고, **이 문서 작성 중 실행해 보지 않았습니다.** Microsoft 문서에 따르면 ETW 파일 읽기 이벤트에는 `Offset`(64비트), `IoSize`(Microsoft-Windows-Kernel-File 공급자에서는 ByteOffset, IOSize), `FileKey`가 있어 오프셋과 길이를 얻을 수 있습니다. 수집기는 게임 시작 전에 켜고 종료 후에 끄며, 이상 징후(실행 거부, 경고, 계정 알림)가 있으면 즉시 중단합니다. 첫 시험은 오프라인 캠페인에서 짧게 합니다.

```powershell
# 관리자 PowerShell. 프로필 이름은 wpr -profiles 로 로컬에서 확인해야 합니다(미확인).
wpr -start FileIO -filemode
# ... 게임 실행 -> 로딩 완료 -> 게임 종료 ...
wpr -stop D:\AsterWork\mw4_run1.etl
```

이 `.etl`은 아직 `trace` 명령이 읽지 못합니다.

## 2단계. 계획 만들기

### 2a. 선읽기 계획 `plan.json`과 트레이스 변환

```powershell
python astercache_v07.py plan --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --budget-mib 512 --out D:\AsterWork\plan.json

python astercache_v07.py trace --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --out D:\AsterWork\trace.json
```

`--prefix`는 게임 폴더입니다. 그 아래 경로의 읽기만 쓰며 `D:\Games\CoD4Other` 같은 이름이 비슷한 폴더는 걸러집니다.

CSV마다 `{'rows': ..., 'read_success_rows': ..., 'parsed': ..., 'ignored': ...}` 한 줄이 나옵니다. `parsed`가 0이거나 `rows`에 비해 아주 작으면 Procmon 필터 또는 `--prefix`가 틀린 것입니다. 시험 CSV에서는 `parsed`가 1,463~1,481이었습니다(나머지는 END OF FILE, 폴더 밖 경로, 읽기가 아닌 행).

두 명령이 만드는 파일의 용도는 다릅니다.

| 파일 | 만든 명령 | 여러 CSV를 합치는 방식 | 쓰는 곳 |
|---|---|---|---|
| `plan.json` | `plan` | 2회 이상 읽힌 64 KiB 블록만, 최근 실행에 가중치(0.85씩 감쇠), `--budget-mib`까지 | 선읽기(`prefetch.cli --trace`) |
| `trace.json` | `trace` | 모든 실행에서 한 번이라도 읽힌 64 KiB 블록의 합집합(중복 제거) | 선읽기, `game_launch.ps1` |
| `trace.run1.json`, `trace.run2.json`, ... | `trace` | 실행마다 따로, 읽은 순서 그대로 | `relayout.cli plan --trace` (실행마다 하나씩) |

재배치는 실행별 읽기 순서를 비교해 일관된 순서를 찾기 때문에 실행마다 따로 받습니다. `plan.json`도 재배치가 받기는 하지만 경로 순으로 정렬돼 있어 읽기 순서 정보가 없으므로 권하지 않습니다.

두 명령은 Procmon이 같은 파일을 다른 대소문자(`d:\games\a.bin`과 `D:\Games\A.bin`)로 기록해도 실제 파일의 대소문자로 통일합니다. 통일하지 않으면 같은 파일이 둘로 나뉘어 이동이 중복됩니다. 시험 CSV에서 이 문제가 실제로 발생했고 수정했습니다.

### 2b. 재배치 계획 `relayout_plan.json`

```powershell
python -m relayout.cli plan --trace D:\AsterWork\trace.run1.json D:\AsterWork\trace.run2.json D:\AsterWork\trace.run3.json `
  --origin-root D:\Games\CoD4 --zone-mib 512 --out D:\AsterWork\relayout_plan.json
```

PowerShell은 `trace.run*.json` 같은 와일드카드를 네이티브 명령에 넘겨주지 않으므로 파일을 하나씩 적습니다(`trace` 명령이 마지막 줄에 파일 목록을 출력합니다). `--out` 대신 `--dry-run`을 쓰면 파일을 저장하지 않고 통계만 봅니다.

출력 통계에서 볼 것(시험 CSV 3개, `--zone-mib 512`)

```
"sessions":3,"cluster_bytes":4096,
"stats":{"hot_bytes":205193216,"moved_bytes":205193216,"tasks":3029,"zone_used_clusters":50096,
         "overflow_bytes":0,"skipped_files":0,"current_runs":3049,"expected_runs":1,...}
```

- `hot_bytes`: 옮길 핫 데이터의 크기(여기서는 약 196 MiB)입니다. `--zone-mib`는 이보다 커야 하며, `overflow_bytes`가 0보다 크면 존이 모자란 것이므로 늘립니다. 존이 너무 크면 연속 빈 공간을 더 요구하고 이동 시간이 길어지므로 핫 바이트의 1.2배 정도를 기준으로 삼으십시오.
- `tasks`: 이동 작업 수. `current_runs`는 핫 데이터가 현재 몇 조각으로 흩어졌는지, `expected_runs`는 재배치 뒤 몇 조각인지입니다(1이면 한 구간).
- `skipped_files`: 위치를 알 수 없어 건너뛴 파일 수입니다.
- 존은 D:에 같은 크기의 **연속 빈 공간**이 있어야 합니다. 3단계 `probe`의 `zone_candidate_lcn`이 `null`이면 없는 것이므로 `--zone-mib`를 줄이거나 D:의 공간을 비우십시오.

게임 폴더 밖 경로는 계획에서 자동으로 빠집니다.

### 2c. 선읽기 계획 미리 보기 (선택)

```powershell
python -m prefetch.cli --trace D:\AsterWork\relayout_plan.json.prefetch.json --origin-root D:\Games\CoD4 --full-file-ratio 2
```

`relayout plan`은 계획과 함께 `relayout_plan.json.prefetch.json`을 씁니다. 재배치 존에 들어간 블록만, 존 순서대로 담은 선읽기 입력이므로 재배치한 뒤에는 `trace.json` 대신 이 파일을 넘깁니다(읽는 바이트가 `hot_bytes`와 같습니다). `--min-sessions N`은 N개 미만의 실행에서만 읽힌 블록을 핫 집합에서 뺍니다(기본: 트레이스가 2개 이상이면 2, 1개면 1).

`--execute` 없이 실행하면 읽지 않고 `bytes`(읽을 바이트)와 `requests`(요청 수)만 보여 줍니다. 4단계가 읽을 양입니다.

## 3단계. 재배치 (관리자 PowerShell)

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

각 출력에서 볼 것

- `probe`: `free_match`가 `true`여야 합니다(비트맵의 빈 클러스터 수가 NTFS 보고값과 같다는 뜻). `false`면 중단합니다. `zone_candidate_lcn`은 숫자여야 합니다. 관리자가 아니면 `{"error":"not_admin"}`으로 끝납니다.
- `apply --dry-run`: `tasks_todo`가 0보다 크고 `skipped_files`에 예상 밖의 파일이 없는지 봅니다. `bytes_todo`가 옮길 바이트입니다.
- `apply --execute`: `failed`가 빈 배열, `stopped`가 `null`, `sha_mismatch`가 빈 배열, `moved_bytes`가 `bytes_todo`와 같아야 합니다. 한 번 실행에 기본 2,048 MiB까지 옮기고 `"stopped":"write_cap"`으로 멈출 수 있습니다. 이때는 **같은 `--execute` 명령을 다시 실행**하면 이어서 진행합니다(`--write-cap-mib`로 한도를 올릴 수도 있습니다).
- `verify`: `"verdict":"contiguous"`여야 합니다(핫 데이터가 한 구간에 순서대로 모였다는 뜻).

소요 시간: D:\AsterBench의 243 MB(1,048개 작업)는 1~2분이 걸렸습니다. 게임의 핫 데이터는 GB 단위이므로 이에 비례해 더 걸릴 것으로 예상합니다(5 GB면 수십 분이라는 외삽이며 실측이 아닙니다).

저널(JSON Lines)은 `--journal`을 주지 않으면 `D:\AsterWork\relayout_plan.json.journal.jsonl`에 생깁니다. 되돌리기에 필요하므로 지우지 마십시오.

되돌리기

```powershell
python -m relayout.cli revert --journal D:\AsterWork\relayout_plan.json.journal.jsonl --allowed-root D:\Games\CoD4 --execute
```

`--execute`를 빼면 아무것도 옮기지 않습니다.

## 4단계. 게임 실행 (선읽기와 동시에)

일반 PowerShell에서 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\game_launch.ps1 `
  -Trace D:\AsterWork\relayout_plan.json.prefetch.json -OriginRoot D:\Games\CoD4 -Game D:\Games\CoD4\iw3sp.exe
```

스크립트는 선읽기(`prefetch.cli`, 존 순서)를 먼저 숨은 백그라운드 프로세스로 시작하고 곧바로 게임을 실행한 뒤, 스트림이 끝날 때까지 기다려 소요 시간과 읽은 바이트를 출력합니다. 시험 출력(게임 대신 `cmd.exe /c exit`, 캐시가 따뜻한 상태)

```
prefetch started (pid 26632), game started 0.07 s later (pid 32532)
stream done: 0.46 s since launch (read time 0.06 s), 195.7 MiB, 2169 requests, errors 0, 3,445 MiB/s
```

이 값은 캐시가 따뜻한 상태라 속도 참고용이 아닙니다. `errors`가 0이 아니면 트레이스가 낡은 것입니다(패치 등).

- `-Game`에는 실행 파일 외에 `battlenet://...`, `steam://rungameid/...` 같은 URI도 줄 수 있습니다. 런처가 게임을 띄우는 구조에서는 선읽기가 게임 시작에 앞서 끝나지 않을 수 있습니다.
- `-NoPrefetch`를 붙이면 선읽기 없이 게임만 실행합니다(비교용).
- 게임 인자는 `-GameArgs '+set','x'`처럼 배열로 줍니다.
- 기본 `-FullFileRatio 2`는 기록된 블록만 읽습니다. 재배치 뒤 파일의 나머지는 핫 존에 없으므로 파일 전체를 읽는 것(`prefetch.cli` 기본값 0.3)은 시간 낭비입니다(시험 데이터에서 읽는 양이 205 MB에서 402 MB로 늘었습니다).

### 효과 재는 법

1. PC를 재부팅한 직후(캐시가 비어 있는 상태)에 `-NoPrefetch`로 게임을 실행해 스톱워치로 로딩 시간(실행부터 로딩 완료까지)을 잽니다. 재부팅하고 3회 반복합니다.
2. 같은 방식으로 `-NoPrefetch` 없이 3회 잽니다.
3. 두 결과의 중앙값을 비교합니다. 재부팅 없이 반복하면 Windows 파일 캐시 때문에 항상 빠르게 나와 비교가 무의미합니다.

## 5단계. 유지 관리

- 게임 패치, 파일 검증/복구 뒤에는 `verify`로 배치가 깨졌는지 봅니다. `verdict`가 `non-contiguous`이거나 `apply`가 `source_changed`를 보고하면 1단계부터 트레이스를 다시 모으고 2~3단계를 다시 합니다. 패치된 파일은 크기/수정 시간이 바뀌어 기존 계획이 거부됩니다.
- **Windows 자동 드라이브 최적화에서 D:를 제외하십시오.** Windows는 주 1회 예약 작업으로 HDD를 디프래그하며 이것이 재배치한 배치를 되돌릴 수 있습니다. 시작 메뉴에서 "드라이브 조각 모음 및 최적화"(Defragment and Optimize Drives)를 열고, 최적화 예약의 "설정 변경"을 눌러 "선택"으로 드라이브 목록을 열고 D:의 체크를 해제합니다. 직접 `defrag D:`도 실행하지 마십시오.
- 시간이 지난 뒤에도 배치가 유지되는지는 합성 데이터셋에서 1시간까지만 확인했습니다(24시간 확인은 없음).

## 한계

- **첫 실행은 이득이 없습니다.** 트레이스를 모으는 실행과 재배치 시간은 비용만 듭니다. 이득은 재배치 이후 반복 로딩에서만 나옵니다.
- **핫 집합이 클수록 선읽기 바닥 시간이 커집니다.** 순차 읽기를 약 160 MB/s로 두면 바닥은 바이트 수 ÷ 160 MB/s입니다(1 GB = 1000 MB 기준, GiB로 재면 약 7% 늘어납니다).

| 핫 집합 | 선읽기 바닥 시간 |
|---:|---:|
| 2 GB | 약 12.5초 |
| 5 GB | 약 31초 |
| 10 GB | 약 62.5초 |
| 20 GB | 약 125초 |
| 30 GB | 약 188초 |

핫 집합이 수 GB를 넘으면 선읽기 시간이 바닥이 되어 효과가 제한적이고, 이득은 주로 탐색(seek) 제거에서 나옵니다. 합성 데이터셋(243 MB)에서는 선읽기가 1.1~1.7초였고, 실제 게임의 핫 집합 크기는 아직 모릅니다.

- **트레이스에 없는 읽기는 느립니다.** 기록한 로딩과 다른 레벨/모드, 플레이 중 텍스처 스트리밍 같은 읽기는 이 도구가 돕지 못합니다. 한 번만 읽힌 블록(특히 오래된 실행에서만 나온 것)은 존에 들어가지 않아 선읽기 때 탐색이 생길 수 있으므로 3~5회 실행의 일관성이 중요합니다.
- **이 도구는 게임에 주입하거나 후킹하지 않고 별도 프로세스로 파일만 읽습니다.** 그래도 안티치트가 "게임 파일에 접근하는 앱"으로 볼 수 있는지는 검증되지 않았습니다. `FSCTL_MOVE_FILE`이 게임의 무결성 검사에 걸리는지도 공식 확인이 없습니다(파일 내용이 바뀌지 않으므로 해시 검사에는 걸리지 않을 것으로 추정). 재배치 뒤에는 런처의 파일 검사/복구를 한 번 돌려 영향이 없는지 확인하십시오.
- **실제 게임으로 검증한 적이 없습니다.** README의 모든 수치는 합성 데이터셋에서 얻었습니다.

## 참고: 리그 오브 레전드 (Vanguard)

리그 오브 레전드는 Riot Vanguard를 씁니다. Vanguard도 커널 수준의 안티치트이므로 위 Ricochet와 같은 주의를 적용합니다. 아래는 2026-11-02에 웹으로 조사한 결과이며, 조사 범위가 얕습니다. 각 항목의 출처 등급을 [확인](공식·1차), [2차](블로그·요약), [추측](근거 없는 추정)으로 표시합니다.

**Vanguard와 수집·재배치 도구**
- Procmon, ETW, 디스크 디프래그와 Vanguard가 충돌한다는 공개 보고는 찾지 못했습니다. 그렇다고 안전하다는 근거도 없습니다. 보고가 없었다는 사실이 안전을 뜻하지는 않으며, 계정 제재 위험을 0이라고 볼 수 없습니다.
- Riot의 "Incompatible Software" 문서는 "호환되지 않는 소프트웨어·드라이버"라고만 쓰며 도구 범주나 위 도구 이름을 언급하지 않습니다. [확인] https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software
- 차단 대상은 주로 치트에 악용될 수 있는 취약 드라이버입니다. 차단될 때 오류에 파일 경로가 표시됩니다. [2차] https://dotesports.com/valorant/news/riot-is-shipping-a-hotfix-to-increase-vanguards-compatibility-with-cheat-vulnerable-drivers
- Procmon은 PROCMON24.SYS 필터 드라이버를 올리므로 세 도구 가운데 위험이 가장 큽니다. 트레이스와 재배치는 Vanguard를 끈 상태에서 하고, 플레이 중에는 수집·재배치 도구를 모두 닫으십시오. [추측]
- ETW 세션과 디스크 재배치 API는 별도 드라이버를 올리지 않아 위험이 상대적으로 낮을 것으로 추정합니다. 공개 근거는 없습니다. [추측]

**설치 용량**
- Riot 공식 문서는 일반 설치 약 35~40GB라고 밝힙니다. [확인] https://support.riotgames.com/en-us/league-of-legends/client/league-of-legends-takes-too-much-space
- 2차 블로그는 16~25GB라고 해 자료가 상충합니다. 정확한 값은 사용자 PC에서 직접 측정해야 하며, 그 전까지는 20~40GB를 기준으로 봅니다. [2차] https://nokiamob.net/2026/02/19/how-big-is-league-of-legends-in-2026/
- Game 폴더에는 큰 파일이 약 400개 있습니다. 릴리스 전체는 30만 개 이상의 chunk로 나뉩니다. [확인] https://www.riotgames.com/en/news/supercharging-data-delivery-new-league-patcher

**패치와 재배치 유지**
- 새 패처는 파일을 통째로 교체하지 않고 chunk 단위로 제자리 갱신합니다. Riot은 파일 배치를 버전 간 안정적으로 유지하도록 패키징을 바꿨다고 밝혔습니다. [확인] 같은 출처
- 따라서 재배치한 파일이 패치 뒤에도 남을 가능성이 있습니다. 실측 보고는 없습니다. [추측]
- 패처가 파일을 삭제한 뒤 다시 내려받는 경로라면 재배치가 깨집니다. 패치 뒤에는 배치 상태를 다시 확인하십시오. [추측]

**로딩 특성**
- HDD와 SSD의 LoL 로딩 시간 차이를 보여 주는 수치는 찾지 못했습니다. 일반 게임에서 HDD가 SSD보다 2~3배 느리다는 일반론만 확인했습니다. [2차] https://www.gamepressure.com/newsroom/hdd-sshd-ssd-m2-speed-benchmark-of-hard-drives/z1692
- LoL은 네트워크와 CPU 대기 비중이 커서 디스크 이득이 작을 수 있습니다. [추측]
- 로딩 중 읽는 바이트 양은 공개 자료가 없으므로 직접 트레이스로 측정해야 합니다.

**권장 순서**
1. Vanguard가 없는 다른 게임에서 트레이스, 재배치, 선읽기 파이프라인을 먼저 검증합니다.
2. LoL은 Vanguard를 끈 상태와 오프라인 상태에서 읽기량만 측정해 HDD 이득의 여지를 판단합니다. 트레이스 수집은 이 상태에서만 합니다.
3. 재배치 기록은 ETW 대신 파일 읽기 순서를 직접 남기는 방식도 검토합니다. 플레이 중에는 수집 도구를 쓰지 않고, 재배치 뒤 읽기 속도만 비교합니다.
4. 패치 뒤 재배치가 유지되는지 확인합니다. 계정 위험이 없다고 확인된 범위에서만 다음 단계로 넘어갑니다.
