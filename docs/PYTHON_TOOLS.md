**English** | [한국어](PYTHON_TOOLS.ko.md)

# Python research and measurement tools

This document describes the layout and usage of the repository's Python reference implementation (`bench/`, `prefetch/`, `relayout/`, `astercache_v07.py`, and so on). For the final C# version, see [README.md](../README.md) and [csharp/README.md](../csharp/README.md).

## Layout

- `bench/` — synthetic dataset generation (about 2 GiB, 4,000 trace entries), cold and warm replay, cold-condition preparation, and the scenario runner
- `prefetch/` — NTFS extent lookup (`layout.py`), physical-position sorting and break-even gap merging (`scheduler.py`), sequential prefetch (`reader.py`), and the command-line entry point (`cli.py`)
- `relayout/` — hot-block planning from a trace (`plan.py`), in-place NTFS moves (`mover.py`), plan apply, verify, and revert (`apply.py`), and the command-line entry point (`cli.py`)
- `scripts/` — experiment scripts run once from an administrator PowerShell: `e2_vhdx_trial.ps1` (throwaway VHDX prototype) and `e3_hdd_dataset.ps1` (D:\AsterBench measurement)
- `sqlite_cache.py` — a SQLite cache that stores a block and its SHA-256 in one row, with origin verification, eviction, and failure fallback
- `astercache_v07.py` — trace planning (`plan`), evaluation (`evaluate`), inspection (`inspect`), and RAM/SSD prewarm (`ram-warm`, `ssd-warm`) commands
- `session_planner.py`, `procmon_import.py` — the learning planner and Procmon CSV importer carried over from an earlier version. Behavior on real Windows CSV files has not been validated yet.
- `blockcache_v06.py` — code kept for comparison with the earlier approach
- `tests/` — 59 unit and regression tests
- `stress.py`, `integration_smoke.py`, `bench_api.py`, `repeat_validation.py` — validation and micro-benchmarks
- `windows_diagnostics.ps1` — a read-only PC environment check script
- `results/` — measurement result JSON and logs

## Requirements

- Python 3.11 or later. There are no external package dependencies. Validated on Windows 11 with Python 3.11.4 and 3.12.10.
- The origin path and the cache path must be in different directory trees. A real speed comparison needs separate physical HDD and SSD devices.
- Benchmarks and stream tuning were run without administrator rights. The cold condition for the relocation measurements (E2, E3) was produced by clearing the standby list with administrator rights, and relocation itself also requires administrator rights.
- Do not use it directly on important game data or on a system drive. Run it on a test folder first.
- Linux file I/O and Windows NTFS I/O are not equivalent. The Linux container validation results are documented separately in `VALIDATION_REPORT.md`.

## Commands

### Benchmark (`bench`)

`bench/run.py` must be run as a package. Running it as `python bench/run.py` gives a relative import error. Run it from the top-level folder of the repository as follows.

```bash
python -m bench.run --root-hdd D:\AsterBench --root-ssd C:\AsterBench [--repeat 3] [--prewarm none|naive] [--prewarm-cmd CMD] [--files 64] [--min-mib 16] [--max-mib 48] [--trace 4000] [--min-free-gib 5] [--out-dir DIR] [--cleanup]
```

- `--root-hdd` and `--root-ssd` are the dataset folders on each physical device. After creating the datasets, it performs cold and warm replay and the prewarm comparison.
- The default of `--prewarm` is `naive`. If `--prewarm-cmd` is given, an external prewarm command is run, and it receives the environment variables `ASTER_TRACE` (path of the trace JSON) and `ASTER_ROOT`.
- The default of `--out-dir` is `results/bench_20261101`.
- `--cleanup` deletes both datasets and exits.

### Stream tuning and concurrent mode (`bench`)

```bash
python -m bench.stream_tune --root D:\AsterBench --repeat 3 --out results\stream_tuning_20261101\sweep.json [--chunks-kib ...] [--qds ...] [--hints ...] [--reuse] [--flush-mib N]
python -m bench.concurrent_run --root D:\AsterBench --repeat 3 --think-us 0 250 1000 --out results\stream_tuning_20261101\concurrent.json [--threads 1] [--chunk-kib 1024] [--seq-hint] [--no-reuse]
```

- `stream_tune` varies chunk size, queue depth, and the SEQUENTIAL_SCAN hint on the relocated zone and measures each. As the tool describes, the cold preparation pushes out the hot range with unbuffered reads and then reads `--flush-mib` of other data. Whether the drive cache was fully cleared has not been confirmed. With `--reuse`, file handles are kept open.
- `concurrent_run` starts the stream and the trace replay at the same time and measures the wall-clock time until the last request. `--think-us` is the game compute time per request (in microseconds).

### Relocation (`relayout`)

```bash
python -m relayout.cli plan --trace trace.json --origin-root D:\AsterBench --zone-mib 300 --out plan.json [--dry-run]
python -m relayout.cli verify --plan plan.json
python -m relayout.cli probe --volume D: [--require-volume-label LABEL] [--plan plan.json | --zone-mib 300]          # administrator
python -m relayout.cli apply --plan plan.json --allowed-root D:\AsterBench [--require-volume-label LABEL] [--dry-run | --execute] [--journal j.jsonl]   # administrator
python -m relayout.cli revert --journal j.jsonl --allowed-root D:\AsterBench [--require-volume-label LABEL] [--execute]   # administrator
```

- `plan` and `verify` need no privileges. `probe`, `apply`, and `revert` end with `{"error":"not_admin"}` unless run as administrator.
- Without `--execute`, nothing is moved. `--dry-run` saves the chosen hot zone into the plan file.
- The plan's volume, the origin root, `--allowed-root`, and the opened volume must all be on the same drive, and if the label differs, nothing is touched.
- The journal is JSON Lines. `revert` applies the journal in reverse to restore the previous physical layout. The data content does not change.
- Run the two experiment scripts from an administrator PowerShell, relative to the top-level folder of the repository.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2_vhdx_trial.ps1 [-VhdxPath PATH] [-SizeGiB 8] [-Files 16]
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e3_hdd_dataset.ps1 [-Root D:\AsterBench] [-SsdRoot C:\AsterBench] [-SkipRelayout]
```

- `e2_vhdx_trial.ps1` creates a throwaway fixed-size VHDX, runs relocation to the end, and detaches it. The VHDX file is not deleted. The default path is on C:, so its timings are SSD-based and are not HDD evidence.
- `e3_hdd_dataset.ps1` moves the `dataset_meta.json` and `trace.json` of the existing `D:\AsterBench`. The script leaves the revert command as `README.txt` in the result folder `results\relayout_e3\<date-time>`.
- `-SkipRelayout` is an option to re-measure an already relocated dataset. Without moving anything, it measures C (direct), D (prefetch + replay), F (concurrent stream), and E (SSD), and compares the manifest with the most recent `manifest_after.json`. It is used to measure persistence some time after relocation.

### Prefetch planning and execution (`prefetch`)

```bash
python -m prefetch.cli --plan trace.json --origin-root D:\Games [--strategy physical|path|none] [--budget-mib 1024] [--read-mbps 100] [--seek-ms 12] [--gap-kib N] [--full-file-ratio 0.3] [--threads 1] [--chunk-kib 1024] [--no-reuse] [--seq-hint] [--execute]
```

- Without `--execute`, it prints only the plan as JSON without actually reading.
- `--plan` (or `--trace`) and `--origin-root` can be replaced by the environment variables `ASTER_TRACE` and `ASTER_ROOT`.
- `--strategy physical` sorts by physical position based on NTFS extents, `path` sorts by path, and `none` reads without sorting.
- The defaults are 1 thread (strict zone order), 1 MiB chunks, file handles kept open, and SEQUENTIAL_SCAN off. This combination was the fastest on the relocated zone (`results/stream_tuning_20261101`). `--no-reuse` closes the handle for every file.
- The default of `--gap-kib` is 0 (best of the sweep). `--read-mbps` and `--seek-ms` are not currently used in the gap calculation.

### Learning planner and prewarm (`astercache_v07.py`)

```bash
python astercache_v07.py plan --csv run1.csv run2.csv run3.csv --prefix "D:\Games" --budget-mib 256 --out plan.json
python astercache_v07.py evaluate --plan plan.json --csv run4.csv --prefix "D:\Games"
python astercache_v07.py inspect --plan plan.json --origin-root "D:\Games" --budget-mib 256
python astercache_v07.py ram-warm --plan plan.json --origin-root "D:\Games" --budget-mib 256 [--execute]
python astercache_v07.py ssd-warm --plan plan.json --origin-root "D:\Games" --cache-dir "C:\AsterCacheTest" --budget-mib 256 [--execute]
```

- `plan` predicts the read blocks of the next run from several Procmon CSV files. `evaluate` checks the prediction accuracy with a CSV not used for learning.
- `ram-warm` and `ssd-warm` only show the plan unless `--execute` is given.
- The result of `ssd-warm` is used only by programs that call this API. Existing game processes do not read this cache.

### Procmon trace conversion and prefetch input (`trace`)

```bash
python astercache_v07.py trace --csv run1.csv run2.csv run3.csv --prefix "D:\Games" --out trace.json
python -m relayout.cli plan --trace trace.run1.json trace.run2.json trace.run3.json --origin-root "D:\Games" --zone-mib 512 --out relayout_plan.json [--min-sessions N]
python -m prefetch.cli --trace relayout_plan.json.prefetch.json --origin-root "D:\Games" --full-file-ratio 2 [--execute]
```

- `trace` converts Procmon CSV files into `trace.json` (the union of the 64 KiB blocks read in all runs) and per-run `trace.run1.json`, `trace.run2.json`, ... (in the order they were read).
- `relayout plan` also writes `<out>.prefetch.json` next to `--out`. It is the prefetch input holding only the blocks that went into the relocation zone, in zone order, and `prefetch.cli` accepts it as is. After relocation, prefetch with this file instead of `trace.json` (bytes read = the plan's `hot_bytes`).
- `--min-sessions N` removes from the hot set the blocks read in fewer than N runs. The default is 2 if there are two or more traces, and 1 if there is one.

### E6 cold-limit measurement scripts (`bench`, `csharp/scripts`)

- `bench/lba_curve.py` — read-only raw transfer-rate curve of a volume: 512 MiB at each of 20 points, 4 MiB requests, queue depth 4 (administrator)
- `bench/raw_hot_read.py` — maps a trace's hot ranges to LCNs (no administrator); `--read` reads them in LCN order (administrator)
- `bench/wof_ratio.py` — WOF-style XPRESS Huffman compression ratio of the hot ranges at 8 and 16 KiB chunks (no administrator)
- `bench/free_runs.py` — lists free-space runs of an NTFS volume from the volume bitmap, read-only (administrator)
- `csharp/scripts/cs_e6_cold_limit.ps1` — run once as administrator: LBA curve, raw read, full move (no preservation), re-measure, and SHA-256 check
- `csharp/scripts/cs_e6_highband.ps1` — run once as administrator: moves the whole hot set to a zone at 90% of the volume and re-measures against the 42% zone
- `csharp/scripts/cs_e6_moveback.ps1` — run once as administrator: moves the hot set back to the lowest free zone, verifies, and re-measures
- `csharp/scripts/aster_diskio.wprp` — WPR profile for DiskIo (read size and offset) used by `cs_e6_cold_limit.ps1`

### Cache API

```python
from sqlite_cache import SQLiteBlockCache

cache = SQLiteBlockCache(origin_root, cache_dir, capacity_bytes=256 << 20)
data = cache.read("relative/path.bin", offset, length)   # returns bytes
print(cache.stats)
```

The default of `block_size` is 64 KiB, and it must be a power of two between 4 KiB and 4 MiB. `capacity_bytes` is based on the logical cache payload.
