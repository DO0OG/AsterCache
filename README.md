# AsterCache — HDD에서 SSD 수준의 로딩 속도를 얻기 위한 실험

AsterCache는 HDD에 설치된 게임과 대용량 자산을 SSD에 가까운 시간에 읽는 알고리즘과 시스템을 만드는 실험 프로젝트입니다. 목표는 HDD 로딩 시간을 SSD 로딩 시간에 가깝게 줄이는 것입니다. 접근 순서 물리 재배치와 스트림 선읽기를 합성 데이터셋에 적용한 결과, D: HDD 의 콜드 로딩이 SSD 콜드 로딩의 1.43배까지 근접했고 재배치 전 대비 15.6배 단축되었습니다. 실제 게임, 지속성, CMR 드라이브는 아직 검증하지 않았습니다.

**현재 상태 (2026-11-01):** 구현은 네 가지입니다. ① HDD/SSD 로딩을 같은 조건으로 재는 측정 하네스(`bench/`), ② 학습된 읽기 블록을 HDD 물리 배치 순서로 RAM에 미리 읽는 선읽기(`prefetch/`), ③ Python API로 명시적으로 호출하는 SSD 블록 캐시(`sqlite_cache.py`), ④ 핫 블록을 NTFS 안에서 한 구간으로 옮기는 재배치(`relayout/`, FSCTL_MOVE_FILE). 기존 게임 프로세스를 투명하게 가속하는 경로는 아직 없습니다. 일반 게임은 이 캐시를 자동으로 사용하지 않습니다. 측정은 Windows 11 실기에서 합성 데이터셋으로 수행했으며, 실제 게임 로딩 시간은 아직 재지 않았습니다.

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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e3_hdd_dataset.ps1 [-Root D:\AsterBench] [-SsdRoot C:\AsterBench]
```

- `e2_vhdx_trial.ps1`은 일회용 고정 크기 VHDX를 만들어 재배치를 끝까지 돌리고 분리합니다. VHDX 파일은 지우지 않습니다. 기본 경로는 C: 위이므로 그 시간은 SSD 기준이며 HDD 증거가 아닙니다.
- `e3_hdd_dataset.ps1`은 이미 있는 `D:\AsterBench`의 `dataset_meta.json`과 `trace.json`을 옮깁니다. 스크립트는 결과 폴더 `results\relayout_e3\<날짜시각>`에 되돌리기 명령을 `README.txt`로 남깁니다.

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

### 접근 순서 재배치 실측 (2026-11-01)

| 실험 | 구성 | 재배치 전 | 재배치 후 | 무결성 | 출처 |
|---|---|---:|---:|---|---|
| E2 SSD | 16 파일 442 MiB(463,470,592 바이트) VHDX, C: NVMe. 이동 194.6 MB(949개 작업) | 콜드 재생 0.736 s (중앙값) | 0.342 s | SHA-256 16/16 일치, 매니페스트 동일, 순차 구간 949 → 1 | `results/relayout_e2/summary.json`, `dataset.txt` |
| E2 HDD | 같은 데이터, VHDX 를 D: HDD 위에 둠 | 콜드 27.1 s (13.7 / 64.6 / 27.1) | 5.73 s (6.07 / 5.73 / 5.68) | 같음 | `results/relayout_e2_hdd/summary.json` |
| E3 실제 데이터셋 | D:\AsterBench, 64 파일 2.1 GiB, 트레이스 4,000건. 이동 243.5 MB(1,048 작업), 실패 0 | A 직접 17.11 s | C 직접 7.72 s, D 선읽기+재생 1.09 s | SHA-256 64/64 일치, 매니페스트 동일, 순차 구간 1,059 → 1 | `results/relayout_e3/20261101_225446/summary.json` |

E3 중앙값(3회, 관리자 스탠바이 목록 비우기 뒤 측정)은 다음과 같습니다. B 는 재배치 전 선읽기 후 재생 8.04 s, E 는 SSD(C: NVMe) 콜드 직접 0.763 s입니다.

- D/E 1.43, C/E 10.1, A/E 22.4, D/A 0.064(재배치 전 대비 약 15.6배 단축)
- 선읽기 0.93~1.63 s(중앙값 1.05 s) + 재생 0.047 s. D 의 실행 3회 값은 0.97 / 1.09 / 1.68 s입니다.

## 결과 해석

- **재배치의 효과.** 같은 데이터셋에서 재배치는 HDD 콜드 직접 읽기를 17.1 s 에서 7.7 s 로 줄였고, 여기에 선읽기를 더하면 1.1 s 가 됩니다. 재배치 뒤 선읽기 시간은 이전 선읽기 실험(11.1 s)과 비교해 크게 짧습니다. 다만 이 수치는 아래 캐시 가능성을 포함합니다.
- **캐시 가능성.** D 의 선읽기가 243 MB 를 1 초 안팎(150 MB/s 이상)에 읽은 것은 이동 직후 드라이브 캐시(SMR 미디어 캐시와 DRAM)의 영향일 수 있습니다. 같은 드라이브의 안정 순차 속도는 90~110 MB/s 였습니다(E1b). 보수적 기대치는 선읽기 약 2.5 s 이며, 이는 SSD 콜드 0.763 s 의 약 3.3배입니다. 24 시간 뒤 재측정으로 지속성을 판정합니다. 그 전까지 이 값을 지속 성능으로 보지 않습니다.
- **SMR.** WD20EZAZ 는 드라이브 관리형 SMR 로 보도되었습니다(2차 자료). 이동 직후 측정은 재매핑 전 상태일 수 있습니다. 드라이브 위치별 속도는 차이가 없어(`results/zone_curve_20261101`), 핫 존의 위치를 맞추는 최적화는 설계에서 뺐습니다.
- **첫 실행에는 이득이 없습니다.** 재배치는 파일을 옮기는 데 시간이 듭니다. 이득은 재배치 이후 반복 읽기에서만 나타납니다.
- **관리자 권한이 필요합니다.** 볼륨 핸들과 FSCTL_MOVE_FILE 은 관리자만 씁니다. 측정 자체는 스탠바이 목록 비우기를 위해 관리자로 실행했습니다.
- **E2 의 범위.** E2 는 무결성, 순서, 재생 시간을 확인했습니다. 강제 종료 5회, VHDX 강제 분리, `chkdsk /scan` 은 `summary.json` 에 기록이 없어 실행하지 않은 것으로 봅니다.
- **E3 의 범위.** 이동 직후 1회 측정만 있습니다. 10 분 유휴 뒤와 `defrag D: /O` 뒤의 측정은 없습니다.
- **합성 데이터입니다.** 실제 게임의 트레이스와 로딩 시간은 아직 없습니다. 목표 1.25배에는 아직 도달했다고 볼 수 없습니다.

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
- 재배치는 NTFS 디프래그 API(FSCTL_MOVE_FILE)로 파일 데이터를 실제로 옮깁니다. 테스트 폴더에서만 실행하고, 되돌리기는 `revert` 명령으로 합니다. 이동 중 강제 종료에 대한 시험은 아직 하지 않았습니다.
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
