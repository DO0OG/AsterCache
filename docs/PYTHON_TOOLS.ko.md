[English](PYTHON_TOOLS.md) | **한국어**

# Python 연구·측정 도구

이 문서는 저장소의 Python 참조 구현(`bench/`, `prefetch/`, `relayout/`, `astercache_v07.py` 등)의 구성과 실행 방법을 담습니다. C# 최종본의 사용법은 README 와 `csharp/README.md` 를 보십시오.

## 구성

- `bench/` — 합성 데이터셋(약 2 GiB, 트레이스 4,000건) 생성, 콜드·웜 재생, 콜드 조건 준비, 시나리오 실행기
- `prefetch/` — NTFS extent 조회(`layout.py`), 물리 위치 정렬과 손익분기 간격 병합(`scheduler.py`), 순차 선읽기(`reader.py`), 명령행 진입점(`cli.py`)
- `relayout/` — 트레이스 기준 핫 블록 계획(`plan.py`), NTFS 제자리 이동(`mover.py`), 계획 적용·검증·되돌리기(`apply.py`), 명령행 진입점(`cli.py`)
- `scripts/` — 관리자 PowerShell에서 한 번 실행하는 실험 스크립트. `e2_vhdx_trial.ps1`(일회용 VHDX 시제품), `e3_hdd_dataset.ps1`(D:\AsterBench 실측)
- `sqlite_cache.py` — 블록·SHA-256을 한 행에 저장하는 SQLite 캐시와 원본 검증, 축출, 장애 폴백
- `astercache_v07.py` — 추적 계획(`plan`), 평가(`evaluate`), 점검(`inspect`), RAM·SSD 예열(`ram-warm`, `ssd-warm`) 명령
- `session_planner.py`, `procmon_import.py` — 이전 버전에서 가져온 학습 계획과 Procmon CSV 가져오기 구성요소. 실제 Windows CSV에서의 동작은 아직 검증하지 않았습니다.
- `blockcache_v06.py` — 이전 방식과의 비교용으로 보존한 코드
- `tests/` — 단위·회귀 테스트 59개
- `stress.py`, `integration_smoke.py`, `bench_api.py`, `repeat_validation.py` — 검증과 마이크로벤치마크
- `windows_diagnostics.ps1` — 읽기 전용 PC 환경 점검 스크립트
- `results/` — 측정 결과 JSON과 로그

## 실행 요구조건

- Python 3.11 이상. 외부 패키지 의존성은 없습니다. Windows 11에서 Python 3.11.4와 3.12.10으로 검증했습니다.
- 원본 경로와 캐시 경로는 서로 다른 디렉터리 트리여야 합니다. 실제 속도 비교에는 서로 다른 물리 HDD와 SSD가 필요합니다.
- 벤치마크와 스트림 튜닝은 비관리자 권한으로 수행했습니다. 재배치 실측(E2·E3)의 콜드 조건은 관리자 권한으로 스탠바이 목록을 비운 뒤 만들었고, 재배치 자체도 관리자 권한이 필요합니다.
- 중요한 게임 데이터나 시스템 드라이브에서 바로 쓰지 않습니다. 테스트 폴더에서 먼저 실행하세요.
- Linux 파일 I/O와 Windows NTFS I/O는 동등하지 않습니다. Linux 컨테이너 검증 결과는 `VALIDATION_REPORT.md`에 따로 정리했습니다.

## 명령 모음

### 벤치마크 (`bench`)

`bench/run.py`는 패키지로 실행해야 합니다. `python bench/run.py`로 실행하면 상대 import 오류가 납니다. 저장소 최상위 폴더에서 다음과 같이 실행합니다.

```bash
python -m bench.run --root-hdd D:\AsterBench --root-ssd C:\AsterBench [--repeat 3] [--prewarm none|naive] [--prewarm-cmd CMD] [--files 64] [--min-mib 16] [--max-mib 48] [--trace 4000] [--min-free-gib 5] [--out-dir DIR] [--cleanup]
```

- `--root-hdd`, `--root-ssd`는 각 물리 장치 위의 데이터셋 폴더입니다. 데이터셋을 만든 뒤 콜드·웜 재생과 예열 비교를 수행합니다.
- `--prewarm`의 기본값은 `naive`입니다. `--prewarm-cmd`를 주면 외부 예열 명령을 실행하며, 이 명령은 환경변수 `ASTER_TRACE`(트레이스 JSON 경로)와 `ASTER_ROOT`를 받습니다.
- `--out-dir`의 기본값은 `results/bench_20261101`입니다.
- `--cleanup`은 두 데이터셋을 지우고 종료합니다.

### 스트림 튜닝과 동시 모드 (`bench`)

```bash
python -m bench.stream_tune --root D:\AsterBench --repeat 3 --out results\stream_tuning_20261101\sweep.json [--chunks-kib ...] [--qds ...] [--hints ...] [--reuse] [--flush-mib N]
python -m bench.concurrent_run --root D:\AsterBench --repeat 3 --think-us 0 250 1000 --out results\stream_tuning_20261101\concurrent.json [--threads 1] [--chunk-kib 1024] [--seq-hint] [--no-reuse]
```

- `stream_tune`은 재배치된 존에서 청크 크기, 큐 깊이, SEQUENTIAL_SCAN 힌트를 바꿔 가며 잽니다. 콜드 준비는 도구 설명대로 언버퍼드 읽기로 핫 범위를 밀어낸 뒤 `--flush-mib`만큼 다른 데이터를 읽는 방식입니다. 드라이브 캐시를 완전히 비웠는지는 확인하지 않았습니다. `--reuse`를 주면 파일 핸들을 유지합니다.
- `concurrent_run`은 스트림과 트레이스 재생을 동시에 시작하고, 마지막 요청까지의 벽시계 시간을 잽니다. `--think-us`는 요청당 게임 연산 시간(마이크로초)입니다.

### 재배치 (`relayout`)

```bash
python -m relayout.cli plan --trace trace.json --origin-root D:\AsterBench --zone-mib 300 --out plan.json [--dry-run]
python -m relayout.cli verify --plan plan.json
python -m relayout.cli probe --volume D: [--require-volume-label 라벨] [--plan plan.json | --zone-mib 300]          # 관리자
python -m relayout.cli apply --plan plan.json --allowed-root D:\AsterBench [--require-volume-label 라벨] [--dry-run | --execute] [--journal j.jsonl]   # 관리자
python -m relayout.cli revert --journal j.jsonl --allowed-root D:\AsterBench [--require-volume-label 라벨] [--execute]   # 관리자
```

- `plan`과 `verify`는 권한이 필요 없습니다. `probe`, `apply`, `revert`는 관리자가 아니면 `{"error":"not_admin"}`으로 끝납니다.
- `--execute`를 붙이지 않으면 아무것도 옮기지 않습니다. `--dry-run`은 고른 핫 존을 계획 파일에 저장합니다.
- 계획의 볼륨, 원본 루트, `--allowed-root`, 열린 볼륨이 모두 같은 드라이브여야 하고, 라벨이 다르면 아무것도 건드리지 않습니다.
- 저널은 JSON Lines입니다. `revert`는 저널을 거꾸로 적용해 예전 물리 배치를 되돌립니다. 데이터 내용은 바뀌지 않습니다.
- 두 실험 스크립트는 관리자 PowerShell에서 저장소 최상위 폴더 기준으로 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2_vhdx_trial.ps1 [-VhdxPath 경로] [-SizeGiB 8] [-Files 16]
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e3_hdd_dataset.ps1 [-Root D:\AsterBench] [-SsdRoot C:\AsterBench] [-SkipRelayout]
```

- `e2_vhdx_trial.ps1`은 일회용 고정 크기 VHDX를 만들어 재배치를 끝까지 돌리고 분리합니다. VHDX 파일은 지우지 않습니다. 기본 경로는 C: 위이므로 그 시간은 SSD 기준이며 HDD 증거가 아닙니다.
- `e3_hdd_dataset.ps1`은 이미 있는 `D:\AsterBench`의 `dataset_meta.json`과 `trace.json`을 옮깁니다. 스크립트는 결과 폴더 `results\relayout_e3\<날짜시각>`에 되돌리기 명령을 `README.txt`로 남깁니다.
- `-SkipRelayout`은 이미 재배치된 데이터셋을 다시 재는 옵션입니다. 이동하지 않고 C(직접)·D(선읽기+재생)·F(동시 스트림)·E(SSD)를 재며, 매니페스트를 가장 최근의 `manifest_after.json`과 비교합니다. 재배치 뒤 시간이 지난 뒤의 지속성을 재는 데 씁니다.

### 선읽기 계획과 실행 (`prefetch`)

```bash
python -m prefetch.cli --plan trace.json --origin-root D:\Games [--strategy physical|path|none] [--budget-mib 1024] [--read-mbps 100] [--seek-ms 12] [--gap-kib N] [--full-file-ratio 0.3] [--threads 1] [--chunk-kib 1024] [--no-reuse] [--seq-hint] [--execute]
```

- `--execute`를 붙이지 않으면 실제 읽기 없이 계획만 JSON으로 출력합니다.
- `--plan`(또는 `--trace`)과 `--origin-root`는 환경변수 `ASTER_TRACE`, `ASTER_ROOT`로 대신할 수 있습니다.
- `--strategy physical`은 NTFS extent 기준 물리 위치 정렬, `path`는 경로 정렬, `none`은 정렬 없이 읽습니다.
- 기본값은 스레드 1(엄격한 존 순서), 청크 1 MiB, 파일 핸들 유지, SEQUENTIAL_SCAN 끔입니다. 재배치 존에서 이 조합이 가장 빨랐습니다(`results/stream_tuning_20261101`). `--no-reuse`는 파일마다 핸들을 닫습니다.
- `--gap-kib`의 기본값은 0입니다(스윕 최선). `--read-mbps`와 `--seek-ms`는 현재 간격 계산에 쓰지 않습니다.

### 학습 계획과 예열 (`astercache_v07.py`)

```bash
python astercache_v07.py plan --csv run1.csv run2.csv run3.csv --prefix "D:\Games" --budget-mib 256 --out plan.json
python astercache_v07.py evaluate --plan plan.json --csv run4.csv --prefix "D:\Games"
python astercache_v07.py inspect --plan plan.json --origin-root "D:\Games" --budget-mib 256
python astercache_v07.py ram-warm --plan plan.json --origin-root "D:\Games" --budget-mib 256 [--execute]
python astercache_v07.py ssd-warm --plan plan.json --origin-root "D:\Games" --cache-dir "C:\AsterCacheTest" --budget-mib 256 [--execute]
```

- `plan`은 Procmon CSV 여러 개에서 다음 실행의 읽기 블록을 예측합니다. `evaluate`는 학습에 쓰지 않은 CSV로 예측 정확도를 봅니다.
- `ram-warm`과 `ssd-warm`은 `--execute`가 없으면 계획만 표시합니다.
- `ssd-warm`의 결과는 이 API를 호출하는 프로그램에서만 쓰입니다. 기존 게임 프로세스는 이 캐시를 읽지 않습니다.

### Procmon 트레이스 변환과 선읽기 입력 (`trace`)

```bash
python astercache_v07.py trace --csv run1.csv run2.csv run3.csv --prefix "D:\Games" --out trace.json
python -m relayout.cli plan --trace trace.run1.json trace.run2.json trace.run3.json --origin-root "D:\Games" --zone-mib 512 --out relayout_plan.json [--min-sessions N]
python -m prefetch.cli --trace relayout_plan.json.prefetch.json --origin-root "D:\Games" --full-file-ratio 2 [--execute]
```

- `trace`는 Procmon CSV를 `trace.json`(모든 실행에서 읽힌 64 KiB 블록의 합집합)과 실행별 `trace.run1.json`, `trace.run2.json`, ...(읽은 순서 그대로)으로 바꿉니다.
- `relayout plan`은 `--out` 옆에 `<out>.prefetch.json`을 함께 씁니다. 재배치 존에 들어간 블록만 존 순서로 담은 선읽기 입력이며, `prefetch.cli`가 그대로 받습니다. 재배치한 뒤에는 `trace.json` 대신 이 파일로 선읽기합니다(읽는 바이트 = 계획의 `hot_bytes`).
- `--min-sessions N`은 N개 미만의 실행에서만 읽힌 블록을 핫 집합에서 뺍니다. 기본값은 트레이스가 2개 이상이면 2, 1개면 1입니다.

### E6 콜드 한계 측정 스크립트 (`bench`, `csharp/scripts`)

- `bench/lba_curve.py` — 볼륨의 원시 전송 속도 곡선을 읽기 전용으로 잽니다. 20개 지점마다 512 MiB, 4 MiB 요청, 큐 깊이 4 (관리자 권한 필요)
- `bench/raw_hot_read.py` — 추적한 핫 범위를 LCN 으로 옮깁니다(관리자 권한 불필요). `--read` 를 주면 LCN 순서로 읽습니다(관리자 권한 필요)
- `bench/wof_ratio.py` — 핫 범위를 8·16 KiB 청크로 나눠 WOF 방식 XPRESS Huffman 압축률을 잽니다(관리자 권한 불필요)
- `bench/free_runs.py` — 볼륨 비트맵으로 NTFS 볼륨의 빈 구간을 읽기 전용으로 나열합니다(관리자 권한 필요)
- `csharp/scripts/cs_e6_cold_limit.ps1` — 관리자 권한으로 한 번 실행합니다. 곡선 측정, 원시 읽기, 보존 없이 전부 이동, 재측정, SHA-256 검사를 합니다
- `csharp/scripts/cs_e6_highband.ps1` — 관리자 권한으로 한 번 실행합니다. 핫 집합 전체를 볼륨의 90% 위치 존으로 옮기고 42% 존과 비교합니다
- `csharp/scripts/cs_e6_moveback.ps1` — 관리자 권한으로 한 번 실행합니다. 핫 집합을 가장 낮은 빈 존으로 되돌리고 검사한 뒤 재측정합니다
- `csharp/scripts/aster_diskio.wprp` — `cs_e6_cold_limit.ps1` 이 쓰는 DiskIo(읽기 크기와 위치) WPR 프로필입니다

### 캐시 API

```python
from sqlite_cache import SQLiteBlockCache

cache = SQLiteBlockCache(origin_root, cache_dir, capacity_bytes=256 << 20)
data = cache.read("relative/path.bin", offset, length)   # bytes 반환
print(cache.stats)
```

`block_size`의 기본값은 64 KiB이며 4 KiB에서 4 MiB 사이의 2의 거듭제곱이어야 합니다. `capacity_bytes`는 논리 캐시 페이로드 기준입니다.
