# AsterCache v0.7 검증 보고서 — 명명된 검사 86~120

## Windows 실기 검증 (2026-11-01)

### 환경

- Windows 11 Pro 10.0.26200, 비관리자 권한, 개발자 모드 켜짐, 메모리 32 GiB, 논리 코어 16개
- Python 3.11.4(SQLite 3.42.0), Python 3.12.10(SQLite 3.49.1)
- D: WDC WD20EZAZ(SATA, MediaType=HDD, 원본 측정용), C: SAMSUNG MZVLB1T0HBLR(NVMe, MediaType=SSD, 캐시·기준선), E: Micron Crucial X8(USB, SSD)
- 출처: `results/windows_20261101/win_checks.md`, `results/windows_20261101/physicaldisk.json`

### 테스트 결과

| 구분 | 테스트 수 | 결과 | 출처 |
|---|---:|---|---|
| 수정 전 (v0.7 원본, Python 3.11) | 35 | 34 통과, test_104 skip 1 | `results/windows_20261101/unittest_win.log` |
| 수정 전 반복 (10회 새 프로세스) | 35 × 10 | 10회 모두 `OK (skipped=1)` | `results/windows_20261101/repeat10_win.json` |
| 수정 후 (Python 3.11.4) | 59 | 59 통과, skip 0 | `python -B -m unittest discover -s tests -p "test_*.py"` |
| 수정 후 (Python 3.12.10) | 59 | 59 통과, skip 0 | 같은 명령을 `py -3.12`로 실행 |

수정 전 35개 중 test_104는 Windows에서 skip되었습니다. 수정 후 해제하여 전체 59개가 두 버전에서 모두 통과합니다.

### 발견하고 고친 결함

1. **Python 3.12 Windows에서 캐시 전면 실패.** 3.12에서는 파일 핸들의 `fstat`의 `st_ctime`이 ChangeTime, 경로의 `stat`의 `st_ctime`이 생성 시각이라 두 값이 달라집니다. 그래서 거의 모든 읽기가 원본 교체 오류로 끝났습니다. Windows에서는 경로 stat 비교를 앞 4개 필드로 줄이고, ChangeTime은 핸들에서 따로 비교하도록 고쳤습니다(커밋 `bc287ef`).
2. **적재 성능.** 쓰기마다 캐시 전체 blob을 훑는 SUM과 `synchronous=FULL`의 fsync 때문에 콜드 적재가 블록당 21.3 ms였습니다. `synchronous=NORMAL`, (touched, cache_key, byte_count) covering 인덱스, `BEGIN IMMEDIATE`로 4.2 ms가 되었습니다(`results/windows_20261101/cache_fix_bench.json`).
3. **경쟁 가드.** 방금 수정된 파일의 블록을 적재하면 같은 시각 틱의 재수정을 지문으로 구분할 수 없습니다. 마지막 수정이 2초 이내인 파일은 적재하지 않도록 했습니다(`RACY_WINDOW_NS`).
4. **반복 검증이 skip을 통과로 셈.** `repeat_validation.py`의 판정에 `'skipped' not in proc.stderr` 조건을 추가했습니다. 이 수정으로 위 반복 기록의 `OK (skipped=1)`은 실패로 셉니다.
5. **진단 스크립트가 SSD와 HDD를 구분하지 못함.** `Win32_DiskDrive`의 MediaType이 세 장치 모두 `Fixed hard disk media`로만 나왔습니다(`results/windows_20261101/diagnostics.json`). `windows_diagnostics.ps1`에 `Get-PhysicalDisk` 결과를 `physical_disks_storage` 키로 추가했습니다. 기존 키는 유지했습니다.

### 벤치마크와 선읽기 수치

| 항목 | 값 | 출처 |
|---|---|---|
| SQLite 캐시 콜드 적재 (3.11) | 21.338 → 4.206 ms/블록 | `results/windows_20261101/cache_fix_bench.json` |
| SQLite 캐시 적중 (3.11 / 3.12) | 1.757 → 1.140 ms/블록 (3.12 수정 후 0.979) | 같은 파일 |
| HDD 콜드 / SSD 콜드 (3회 중앙값) | 71.21 s / 1.11 s, 비관리자 언버퍼드 대체 | `results/bench_20261101/20261101_204602.json` |
| 단순 선읽기 예열 / 재생 | 29.09 s / 0.070 s | 같은 파일 `prewarm_naive` |
| 물리 위치 정렬 선읽기 | 콜드 17.8 s → 예열 11.1 s + 재생 0.05 s (SSD 콜드 0.8 s의 13.94배) | `results/prefetch_20261101/summary_table.json` |
| 스트레스 (Windows) | 50 시드 × 250 작업 = 12,500 작업, 읽기 11,239회 검증, 손상 0 | `results/windows_20261101/stress_win.json` |

### 재배치 실험 (E2·E3)

접근 순서 재배치(`relayout/`, FSCTL_MOVE_FILE)를 관리자 권한으로 실행했습니다. 설계 기준과 판정은 `docs/design_v1_relayout.md` 5.1절에 있습니다.

| 실험 | 재배치 전 | 재배치 후 | 무결성 | 출처 |
|---|---:|---:|---|---|
| E2 VHDX (C: NVMe, 16 파일 463,470,592 바이트) | 콜드 재생 0.736 s | 0.342 s | 해시 16/16 일치, 매니페스트 동일, 순서 949 → 1 구간 | `results/relayout_e2/summary.json`, `results/relayout_e2/dataset.txt` |
| E2 VHDX (D: HDD, 같은 데이터) | 콜드 27.1 s (13.7 / 64.6 / 27.1) | 5.73 s (6.07 / 5.73 / 5.68) | 해시 동일 | `results/relayout_e2_hdd/summary.json` |
| E3 D:\AsterBench (64 파일 2.1 GiB, 트레이스 4,000건) | 직접 17.11 s, 선읽기+재생 8.04 s | 직접 7.72 s, 선읽기+재생 1.09 s (선읽기 1.05 s + 재생 0.047 s) | 이동 1,048 작업 243.5 MB, 실패 0, 해시 64/64 일치, 매니페스트 동일, 순서 1,059 → 1 구간 | `results/relayout_e3/20261101_225446/summary.json` |

- 콜드 조건은 관리자 스탠바이 목록 비우기로 만들었습니다(E2 SSD·HDD, E3 모두 `purge` 성공).
- E3 의 SSD 기준은 C: NVMe 직접 콜드 0.763 s입니다. 선읽기+재생 1.09 s는 이 값의 1.43배입니다.
- 선읽기 1.09 s는 이동 직후 드라이브 캐시(SMR 미디어 캐시·DRAM)의 영향일 수 있습니다. 같은 드라이브의 안정 순차 속도 90~110 MB/s 로 보면 약 2.5 s 입니다. 24시간 뒤 재측정 전까지 지속 성능으로 보지 않습니다.
- E2 는 강제 종료 5회, VHDX 강제 분리, `chkdsk /scan` 을 실행하지 않았습니다. E3 는 이동 직후 1회 측정이며 10분 유휴와 `defrag D: /O` 뒤 측정은 없습니다.
- 재배치 되돌리기(`relayout.cli revert`)는 실행하지 않았습니다. 해시 검증으로 데이터 내용이 같음만 확인했습니다.

### 결과 파일 목록

- `results/windows_20261101/`: `win_checks.md`, `win_checks.json`, `win_checks_raw.txt`, `cache_fix_bench.json`, `bench_cross_drive.json`, `api_microbenchmark_win.json`, `physicaldisk.json`, `diagnostics.json`, `unittest_win.log`, `repeat10_win.json`, `stress_win.json`, `stress.out.txt`, `integration_win.json`
- `results/bench_20261101/`: `20261101_204602.json`(채택), `run_stdout.txt`, `superseded_20261101_203628.json.txt`(이전 실행, 참고용)
- `results/prefetch_20261101/`: `summary_table.json`, `measure.py`, `prefetch_measure_partial.json`

### 남은 문제

- `repeat_validation.py`는 여전히 `'Ran 35 tests'`를 기대합니다. 현재 `unittest discover`는 59개를 실행하므로, 이 스크립트는 전체 59개 기준으로는 실패로 판정됩니다. 기대 개수를 바꾸는 것은 이번 수정 범위 밖이어서 그대로 두었습니다.
- 3.11의 캐시 적중 1.140 ms는 `cache_fix_bench.json`이 정한 기준 1.1 ms를 넘습니다. 3.12에서는 기준을 넘습니다.
- 콜드 측정은 비관리자 조건의 언버퍼드 대체입니다. 스탠바이 목록 비우기는 실행하지 않았습니다.
- 두 HDD 콜드 실험(벤치마크 71.2 s, 선읽기 실험 17.8 s)은 데이터셋과 콜드 방식이 달라 직접 비교할 수 없습니다.
- 쓰기 중 정전·SSD 손상, BitLocker, DirectStorage, minifilter 환경은 시험하지 않았습니다.

## Linux 컨테이너 검증

검증 날짜: **2026-11-01**. 환경: Linux 컨테이너, Python 3.13.5, SQLite 내장 라이브러리. 실제 사용자의 Windows/HDD/SSD에서는 실행하지 않았습니다.

## 재현 및 테스트 실적

| 그룹 | 실제 실행 | 결과 | 설명 |
|---|---:|---|---|
| v0.7 신규 회귀 검사 | 35개 | **35/35 통과** | test_86..test_120 |
| 신규 검사 재실행 | 10회 × 35개 | **350/350 통과** | 새 Python 프로세스마다 테스트 실행 |
| 무작위 실제 파일 데이터 작업 | 50 시드 × 250 작업 | **12,500작업 오류 없이 종료** | 원본 쓰기/교체 + 정확한 읽기 바이트 비교 |
| 이전 v0.6 회귀 검사 | 35개 | **35/35 통과** | 기존 소스는 별도 디렉터리에서 실행 |
| 이전 v0.5 회귀 검사 | 24개 | **24/24 통과** | 기존 소스는 별도 디렉터리에서 실행 |
| 계획→평가→RAM예열→SSD예열→API 재읽기 | 1개 임시 프로젝트 | **통과** | 예열 전후 원본 SHA256 일치 |
| API 처리 오버헤드 비교 | 15회 교차 순서 | 완료 | Linux/동일 파일시스템/OS 파일 캐시 영향 포함 |

누적 번호 120은 전 버전부터 연속으로 붙인 **검증 항목 ID**를 뜻합니다. 독립적인 120회 Windows 게임 속도 검증을 의미하지 않습니다. 10회 반복 350개 검사는 신규 35개를 같은 테스트 조건으로 반복한 결과이며 서로 독립된 실제 게임 데이터셋이 아닙니다.

### 35개 신규 검사 범위

| 번호 | 초점 |
|---|---|
| 86~93 | 바이트 정확성, 겹치는 읽기, 블록 경계, EOF, 잘못된 요청 차단 |
| 94~99 | 경로 이탈/심볼릭 링크, 재시작 재사용, 용량 제한, 축출 |
| 100~108 | SQL 캐시 데이터/해시 손상, 삭제, 파일 수정·교체·절단, 읽기 도중 변경 |
| 109~115 | 캐시 쓰기 장애·낮은 여유 공간, 동시 스레드, 복수 DB 연결, 미완료 트랜잭션, 예열 계획 분리 |
| 116~120 | 자주 읽는 블록 보호, 제2 DB 연결 조회, 작은 용량, 캐시 미적재 모드, 축출 후 바이트 정합성 |

## 원본 변경 및 데이터 오류 대응

- **읽기 전/후** 열린 파일의 크기·inode·수정/변경 시간을 검사하고, 경로가 다른 파일을 가리키는지도 비교.
- 원본이 변경된 것으로 관측되면 새 세대의 블록 키를 사용하거나 해당 요청을 오류 처리.
- 내부 SSD 캐시 데이터 바이트와 SHA-256이 불일치하면 원본에서 재읽기.
- 한 번의 API 요청 전체가 원본 정보 검증을 통과한 다음, 새 블록들을 하나의 SQLite 트랜잭션으로 적재.
- 데이터/해시를 두 파일에 나누던 v0.6과 달리, 한 행에 함께 저장하여 원자적 커밋. 다만 SQLite DB 자체의 물리 고장·원본 동시 악의적 조작까지 해결하지는 못함.
- WAL + `synchronous=NORMAL` 사용. 정전·OS 충돌 때 마지막 커밋이 롤백될 수 있으나(DB 손상은 아님) 캐시라 재적재로 복구됨. 이 설정이 제공하는 내구성은 SQLite 공식 문서의 조건부 보장에 따름. 직접적인 전원 차단 실험은 없음. 마지막 수정이 2초 이내인 원본의 블록은 적재하지 않음(경쟁 가드, `RACY_WINDOW_NS`).

## 50개 난수 시드 실제 바이트 리플레이

`results/stress.json`의 이번 실행:

- 시드 50개, 시드당 250개 작업 = 12,500개 작업
- **11,239개 읽기**에서 원본 기대 바이트와 응답 비교 통과
- 원본 파일에 대한 **768개 범위 수정**과 **493개 원자적 교체** 포함
- 실험 파일은 임시 디렉터리에만 생성, 캐시와 원본이 같은 물리 장치일 수도 있음
- 기록한 `origin_bytes`는 **애플리케이션이 origin.read()로 읽은 길이**이며 실제 HDD 헤드가 움직여 읽은 물리 바이트가 아닙니다.

## 컨테이너 API 마이크로벤치마크 (참고용)

**15회 교차 순서 테스트의 중간값**. 한 파일에 64KiB 블록 8개를 다섯 번씩 접근(1배치=40회), 동일 40회 반복. 첫 배치가 전부 캐시 미스가 아니라 **8개 미스 + 32개 히트**이므로 순수 콜드 로딩 성능이라고 해석하면 안 됩니다.

| 방식 | 초기 40요청 배치 | 동일 40요청 반복 배치 |
|---|---:|---:|
| v0.6 개별 파일 캐시 | 34.701 ms | 12.082 ms |
| v0.7 SQLite 캐시 | 25.976 ms | 10.396 ms |

위 값은 **오직 해당 실행에서 나타난 Python API 처리 시간**입니다. Windows/HDD/SSD 분리, 게임 실행, OS 페이지 캐시 강제 비우기, 메모리 매핑·DirectStorage 등이 통제되지 않았습니다. 다른 시스템에서는 역전될 수도 있으므로 '게임이 몇 % 빨라졌다'고 말할 근거는 없습니다.

실제 게임 경로에 v0.7 캐시가 연결되지 않았으므로 게임은 위 캐시를 사용하지 않습니다.

## 추가로 발견하고 고친 결함

1. 첫 시험에서 상위 경로 접근이 `ValueError`가 아닌 `FileNotFoundError`가 되던 문제를 수정해, `..`를 사전에 거부합니다.
2. SQLite 캐시 축출이 기록 시각에만 의존하면 자주 사용된 오래된 블록이 먼저 축출되는 문제를 발견. 최근 히트 이력을 제한된 메모리에 저장하고 다음 캐시 적재 시 배치 갱신합니다.
3. v0.6의 블록별 2파일+동기화 구조 대신 데이터+해시의 SQLite 원자적 저장 방식을 추가했습니다.
4. SSD 기록 오류나 여유 공간 부족 시 원본 읽기를 지속하도록 했습니다. DB 자체를 열 수 없는 경우 초기화 실패는 여전히 존재합니다.
5. 파일 내용뿐 아니라 **경로가 다른 inode로 교체된 상태**도 열린 파일의 stat과 비교하도록 보완했습니다.

## 아직 해소하지 못한 위험과 평가 게이트

- **기존 게임 프로세스 자동 가속은 아직 0% 구현**. 사용자 모드 Python API를 호출한 프로그램에서만 SSD 데이터가 제공됩니다.
- Windows NTFS, 실제 HDD/SSD 장치, BitLocker, DirectStorage, minifilter, 실제 게이밍 load-time 벤치마크 미검증.
- 외부 쓰기 감지에 `stat`/`ChangeTime` 의존하므로 모든 파일 변형을 탐지하지 못할 수 있습니다.
- 논리 페이로드 제한과 별도로 SQLite DB/WAL/페이지 조각화가 저장 공간을 차지합니다. 256MiB 한도라고 디스크 파일이 256MiB로 유지된다는 뜻은 아닙니다.
- 오버헤드 벤치마크는 Python 실행, `fsync`, SQLite, 캐시 기록 비용이 포함되지만 **물리 HDD/SSD 부하 비교가 아닙니다**.

Windows 검증은 최소 5회 동일 게임을 HDD 단독 / RAM 예열 HDD / 투명 캐시 적용 HDD(미구현) / SSD 직접 설치로 비교하고, 예열 시간까지 포함한 p50/p95와 물리 읽기량/대기시간을 보고해야 합니다.

## 원본 증거 파일

- `results/repeat10.json`
- `results/stress.json`
- `results/integration.json`
- `results/api_microbenchmark.json`
- `tests/test_86_115.py`
- `tests/test_116_120.py`
