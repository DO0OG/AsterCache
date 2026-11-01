# AsterCache — HDD에서 SSD 수준의 로딩 속도를 얻기 위한 실험

AsterCache는 HDD에 설치된 게임과 대용량 자산을 SSD에 가까운 시간에 읽는 알고리즘과 시스템을 만드는 실험 프로젝트입니다. 목표는 HDD 로딩 시간을 SSD 로딩 시간에 가깝게 줄이는 것이며, 지금은 그 목표에 도달하지 못했습니다.

**현재 상태 (2026-11-01):** 구현은 세 가지입니다. ① HDD/SSD 로딩을 같은 조건으로 재는 측정 하네스(`bench/`), ② 학습된 읽기 블록을 HDD 물리 배치 순서로 RAM에 미리 읽는 선읽기(`prefetch/`), ③ Python API로 명시적으로 호출하는 SSD 블록 캐시(`sqlite_cache.py`). 기존 게임 프로세스를 투명하게 가속하는 경로는 아직 없습니다. 일반 게임은 이 캐시를 자동으로 사용하지 않습니다. 측정은 Windows 11 실기에서 합성 데이터셋으로 수행했으며, 실제 게임 로딩 시간은 아직 재지 않았습니다.

## 구성

- `bench/` — 합성 데이터셋(약 2 GiB, 트레이스 4,000건) 생성, 콜드·웜 재생, 콜드 조건 준비, 시나리오 실행기
- `prefetch/` — NTFS extent 조회(`layout.py`), 물리 위치 정렬과 손익분기 간격 병합(`scheduler.py`), 순차 선읽기(`reader.py`), 명령행 진입점(`cli.py`)
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
- 측정은 비관리자 권한으로 수행했습니다. 관리자 권한은 선택 사항이며, 스탠바이 목록 비우기에만 쓰입니다.
- 중요한 게임 데이터나 시스템 드라이브에서 바로 쓰지 않습니다. 테스트 폴더에서 먼저 실행하세요.
- Linux 파일 I/O와 Windows NTFS I/O는 동등하지 않습니다. Linux 컨테이너 검증 결과는 `VALIDATION_REPORT.md`에 따로 정리했습니다.

## 빠른 시작

### 테스트

```bash
python -m unittest discover -s tests -v
```

### 벤치마크 (`bench`)

`bench/run.py`는 패키지로 실행해야 합니다. `python bench/run.py`로 실행하면 상대 import 오류가 납니다. 저장소 최상위 폴더에서 다음과 같이 실행합니다.

```bash
python -m bench.run --root-hdd D:\AsterBench --root-ssd C:\AsterBench [--repeat 3] [--prewarm none|naive] [--prewarm-cmd CMD] [--files 64] [--min-mib 16] [--max-mib 48] [--trace 4000] [--min-free-gib 5] [--out-dir DIR] [--cleanup]
```

- `--root-hdd`, `--root-ssd`는 각 물리 장치 위의 데이터셋 폴더입니다. 데이터셋을 만든 뒤 콜드·웜 재생과 예열 비교를 수행합니다.
- `--prewarm`의 기본값은 `naive`입니다. `--prewarm-cmd`를 주면 외부 예열 명령을 실행하며, 이 명령은 환경변수 `ASTER_TRACE`(트레이스 JSON 경로)와 `ASTER_ROOT`를 받습니다.
- `--out-dir`의 기본값은 `results/bench_20261101`입니다.
- `--cleanup`은 두 데이터셋을 지우고 종료합니다.

### 선읽기 계획과 실행 (`prefetch`)

```bash
python -m prefetch.cli --plan trace.json --origin-root D:\Games [--strategy physical|path|none] [--budget-mib 1024] [--read-mbps 100] [--seek-ms 12] [--gap-kib N] [--full-file-ratio 0.3] [--threads 1] [--seq-hint] [--execute]
```

- `--execute`를 붙이지 않으면 실제 읽기 없이 계획만 JSON으로 출력합니다.
- `--plan`(또는 `--trace`)과 `--origin-root`는 환경변수 `ASTER_TRACE`, `ASTER_ROOT`로 대신할 수 있습니다.
- `--strategy physical`은 NTFS extent 기준 물리 위치 정렬, `path`는 경로 정렬, `none`은 정렬 없이 읽습니다.
- `--gap-kib`를 주지 않으면 간격은 `--read-mbps` × `--seek-ms`로 정해집니다.

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

### 캐시 API

```python
from sqlite_cache import SQLiteBlockCache

cache = SQLiteBlockCache(origin_root, cache_dir, capacity_bytes=256 << 20)
data = cache.read("relative/path.bin", offset, length)   # bytes 반환
print(cache.stats)
```

`block_size`의 기본값은 64 KiB이며 4 KiB에서 4 MiB 사이의 2의 거듭제곱이어야 합니다. `capacity_bytes`는 논리 캐시 페이로드 기준입니다.

## 측정 결과

### 측정 환경

- Windows 11 Pro 10.0.26200, 비관리자 권한, 메모리 32 GiB, 논리 코어 16개
- Python 3.11.4(SQLite 3.42.0), Python 3.12.10(SQLite 3.49.1)
- 원본 HDD: WDC WD20EZAZ (SATA, `Get-PhysicalDisk` MediaType=HDD), D:
- 캐시 SSD: SAMSUNG MZVLB1T0HBLR (NVMe, MediaType=SSD), C:

### SQLite 캐시 (블록 256개, 64 KiB, 3회 중앙값, 블록당 ms)

| 구성 | 콜드 적재 | 적중 |
|---|---:|---:|
| 수정 전, Python 3.11 | 21.338 | 1.757 |
| 수정 후, Python 3.11 | 4.206 | 1.140 |
| 수정 후, Python 3.12 | 3.967 | 0.979 |

출처: `results/windows_20261101/cache_fix_bench.json`. 3.11의 적중 1.140은 이 파일이 정한 기준 1.1 ms를 넘습니다.

### HDD·SSD 로딩 벤치마크 (합성 데이터셋, 3회 중앙값)

데이터셋은 64개 파일, 약 2.0 GiB(2,135,949,312 바이트), 트레이스 4,000건, 블록 64 KiB입니다.

| 항목 | 중앙값 | 3회 값 | 출처 |
|---|---:|---|---|
| HDD 콜드 | 71.21 s | 55.66 / 71.21 / 71.32 s | `results/bench_20261101/20261101_204602.json` |
| SSD 콜드 | 1.11 s | 1.022 / 1.150 / 1.111 s | 같은 파일 |
| HDD 웜 | 0.080 s | 0.080 / 0.212 / 0.067 s | 같은 파일 |
| SSD 웜 | 0.063 s | — | 같은 파일 |
| 단순 선읽기 후 재생 (예열 포함 총합) | 29.16 s | 예열 29.09 s, 재생 0.070 s | 같은 파일 `prewarm_naive` |

HDD 콜드는 SSD 콜드의 약 64.1배(`verdict.hdd_cold_over_ssd_cold`), 단순 선읽기 총합은 SSD 콜드의 약 26.2배입니다.

### 물리 위치 순서 선읽기 (별도 실험, 3회 중앙값)

이 실험은 위 벤치마크와 같은 데이터셋(64개 파일, 2.1 GiB 합성 데이터셋, bench 데이터셋과 동일)으로 수행했습니다. 같은 데이터셋의 HDD 콜드 시간은 이 실험에서 17.3~19.0 s(중앙값 17.8 s), 벤치마크에서 55.7~71.3 s(중앙값 71.2 s)였습니다. 같은 조건에서도 실행 간 편차가 이만큼 크므로, 두 값의 차이는 방법의 차이가 아니라 편차로 봅니다.

| 구성 | HDD 콜드 | 선읽기 | 재생 | SSD 콜드 대비 |
|---|---:|---:|---:|---:|
| 물리 위치 정렬 (`final_physical`) | 17.8 s | 11.1 s | 0.05 s | 13.94배 |
| 경로 정렬 (`final_path`) | 17.3 s | 11.4 s | 0.05 s | — |
| 스윕 최선: 물리 정렬, 큐 깊이 4, 간격 0 (`results/prefetch_sweep_20261101/sweep_20261101_212410.json`, 3회 중앙값) | — | 7.80 s | 0.04 s | 7.0배 (합계 7.84 s, SSD 콜드 1.12 s 기준) |
| 연속 배치 상한: 핫 243 MB 단일 파일 순차 읽기 (`results/prefetch_sweep_20261101/contig_upper_bound.json`, 4 MiB 청크, 3회 중앙값, 38 extents) | — | 1.36 s | 0.04 s | 1.6배 (합계 약 1.40 s, SSD 콜드 0.865 s 기준, 1 MiB 청크는 1.09 s) |

- 같은 실험의 SSD 콜드는 0.8 s입니다(`ssd_cold`). 위 벤치마크의 SSD 콜드 1.11 s와 다른 실험입니다.
- 선읽기 후 재생은 HDD 콜드보다 약 37% 짧습니다(계산값). 읽은 양 431 MiB 기준 선읽기 처리량은 약 39 MiB/s입니다(계산값).
- 이 데이터셋에서는 물리 정렬과 경로 정렬의 차이가 노이즈 수준입니다. 파일이 순차로 쓰여 조각이 적었기 때문입니다.

출처: `results/prefetch_20261101/summary_table.json`.

## 결과 해석

- **SSD와의 거리.** 콜드 기준으로 HDD는 SSD보다 약 64배 느렸습니다. 선읽기를 적용해도 SSD 콜드의 약 13.9배입니다. 웜 상태에서는 HDD와 SSD가 1.28배 차이가 납니다(벤치마크 중앙값). 웜은 OS 파일 캐시가 이미 채워진 상태라서 차이가 작습니다. 목표로 정한 반복 로딩 p50 1.25배 이내에는 도달하지 못했습니다.
- **아직 멀리 있는 이유.** 선읽기는 HDD에서 필요한 바이트를 미리 읽어 로딩 중의 랜덤 읽기를 RAM 적중으로 바꿉니다. 하지만 HDD의 읽기 처리량(약 39 MiB/s)과 탐색 횟수가 하한을 정합니다. 예열 비용이 재생보다 훨씬 크므로 예열을 로딩 전에 끝낼 수 있는 경우에만 의미가 있습니다.
- **콜드 측정 조건의 한계.** 측정은 비관리자로 실행했습니다. 스탠바이 목록을 비우지 못해서 콜드 조건을 언버퍼드 재읽기(`FILE_FLAG_NO_BUFFERING`)로 대체했습니다(`cold_method: unbuffered_proxy`). 드라이브와 컨트롤러 내장 캐시는 비우지 않았습니다. 실행 간 편차가 55.7~71.3 s로 큽니다. 동시에 일어난 디스크 사용의 영향으로 추정하지만, 원인은 측정하지 않았습니다.
- **게임 로딩 시간이 아닙니다.** 합성 데이터셋과 합성 트레이스로 재현한 결과입니다. 실제 게임의 Procmon 트레이스와 로딩 시간 측정은 아직 없습니다.

## 다음 방향

- **접근 순서 물리 재배치 설계.** 게임 트레이스의 접근 순서대로 핫 블록을 NTFS 안에서 제자리 이동(FSCTL_MOVE_FILE)해 한 구간에 모으고, 그 구간을 선읽기하는 설계입니다. 자세한 내용은 [docs/design_v1_relayout.md](docs/design_v1_relayout.md)에 있습니다. 1.4a 절에 E1 실측 결과가 있습니다.
- **선행 기술과의 관계.** 블록 단위 접근 순서 재배치라는 개념은 BORG(FAST'09)가 먼저 제시했습니다. 이 설계의 차이는 드라이버와 복사본 없이 NTFS 제자리 이동을 쓰는 구현 방식과, 게임 로딩 구간에 맞춘 조합입니다. 출처와 Windows API 제약은 [docs/prior_art_and_windows_api.md](docs/prior_art_and_windows_api.md)에 있습니다.
- **관리자 권한 필요.** 볼륨 핸들은 관리자만 열 수 있으므로 재배치 실행에는 관리자 권한이 필요합니다. 측정과 선읽기는 지금처럼 비관리자 권한으로 계속합니다.
- **SMR 드라이브 주의.** WD20EZAZ 는 드라이브 관리형 SMR 로 보도되었습니다(2차 자료). 대량 이동 쓰기가 쓰기 정체를 일으킬 수 있으므로, 작은 청크와 쓰기 상한으로 나눠 실측합니다. SMR 영향은 아직 분리하지 않았습니다.

## 경고

- 안티치트가 적용된 게임, 온라인 계정과 연동되는 게임, 중요한 데이터가 있는 드라이브에서는 사용하지 않습니다.
- MIT 라이선스에 따라 무보증으로 제공합니다. 위 수치는 한 PC에서 얻은 결과입니다.
- 예열과 계획 명령은 원본 파일을 열어 읽기만 하며 원본 내용을 바꾸지 않습니다. 벤치마크는 지정한 루트 아래에 자체 데이터셋을 만들고, `--cleanup`으로 지웁니다.
- 디스크 읽기 부하가 큽니다. HDD 예열은 수백 MiB에서 수 GiB를 읽을 수 있습니다. SSD 캐시는 파일에 쓰며, 예산은 논리 페이로드 기준이므로 SQLite 데이터베이스와 WAL 파일은 예산 밖의 공간을 더 씁니다. 사용 가능한 SSD 여유 공간을 충분히 남겨 두십시오.
- 캐시는 2초 이내에 수정된 원본 파일의 블록을 적재하지 않습니다. 외부 쓰기를 모두 탐지하는 것은 아닙니다. SHA-256은 캐시 데이터의 우발적 손상을 찾을 뿐이며, 원본과의 재해시는 하지 않습니다.
- 정전과 SSD 손상 상황은 실제 하드웨어에서 시험하지 않았습니다. 캐시 DB가 심각하게 손상되면 초기화가 실패할 수 있습니다.
- 현재 구현은 드라이버나 파일 시스템 계층이 아닙니다. 파일 시스템을 가로채지 않으므로 기존 게임의 I/O 경로를 바꾸지 않습니다.

## 라이선스

MIT License. 저작권 표시는 `LICENSE` 파일을 보십시오. Copyright (c) 2026 DO0OG.

## 기술 근거

- Microsoft, File Caching: https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching
- Microsoft, BypassIO: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- Microsoft, Filter Manager concepts: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/filter-manager-concepts
- SQLite WAL: https://sqlite.org/wal.html
- SQLite `synchronous` pragma: https://sqlite.org/pragma.html#pragma_synchronous
