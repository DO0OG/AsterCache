**English** | [한국어](VALIDATION_REPORT.ko.md)

# AsterCache v0.7 validation report — named checks 86~120

## Summary as of 2026-11-03

The figures below are the current state. The sections after this one are the record from 2026-11-01 and 2026-11-02.

| Item | Value | Source |
|---|---:|---|
| Game loading, cold (HDD, Frostpunk 1.9 GB loading read) | 12.9 s | [README.md](README.md) measured results table |
| Game loading, after prewarm | 0.48 s | README.md measured results table; section 2026-11-02, B |
| Game loading, NVMe SSD cold | 3.8 s | section 2026-11-02, B |
| Zone prefetch after the full move (E6, median of 3) | 11.93 s | section E6 below |
| SSD assist storage, cold (warm) | 2.81 s (0.48 s) | README.md measured results table; not measured in this document |
| Relocation work, 8 parallel reads (E12–E14) | 186 s → 41 s (4.5×) | [docs/RESULTS_SUMMARY.md](docs/RESULTS_SUMMARY.md), E10–E16 |
| Automatic relocation of real games | Call of Duty 1.3 GB; Frostpunk 2 GB (profile); MapleStory 63 MB, 3.9 s; LoL 76 MB, 4.1 s | run record not yet in this repository |
| SMR recovery after a 2 GB move (E16, E16c) | 25 min to 1.5 h | [docs/RESULTS_SUMMARY.md](docs/RESULTS_SUMMARY.md), E10–E16 |

Held back or not adopted:

- Raw bandwidth of the outer zones is non-monotonic (E15), and the outer-zone benefit is not proven, so outer zones are held back.
- First-run read-ahead (E7 to E7f) gave no proven gain. It stays off by default, and the stream, bounded, and adaptive modes are not adopted.

## Windows hardware validation (2026-11-01)

The sections below are the record from 2026-11-01 and 2026-11-02, not the current state.

### Environment

- Windows 11 Pro 10.0.26200, non-administrator, developer mode on, 32 GiB of memory, 16 logical cores
- Python 3.11.4 (SQLite 3.42.0), Python 3.12.10 (SQLite 3.49.1)
- D: WDC WD20EZAZ (SATA, MediaType=HDD, origin measurements), C: SAMSUNG MZVLB1T0HBLR (NVMe, MediaType=SSD, cache and baseline), E: Micron Crucial X8 (USB, SSD)
- Source: `results/windows_20261101/win_checks.md`, `results/windows_20261101/physicaldisk.json`

### Test results

| Group | Tests | Result | Source |
|---|---:|---|---|
| Before the fixes (original v0.7, Python 3.11) | 35 | 34 passed, 1 skipped (test_104) | `results/windows_20261101/unittest_win.log` |
| Before the fixes, repeated (10 fresh processes) | 35 × 10 | `OK (skipped=1)` all 10 times | `results/windows_20261101/repeat10_win.json` |
| After the fixes (Python 3.11.4) | 59 | 59 passed, 0 skipped | `python -B -m unittest discover -s tests -p "test_*.py"` |
| After the fixes (Python 3.12.10) | 59 | 59 passed, 0 skipped | the same command run with `py -3.12` |

Of the 35 before the fixes, test_104 was skipped on Windows. It was enabled after the fixes, and all 59 now pass on both versions.

### Defects found and fixed

1. **The cache failed entirely on Python 3.12 Windows.** On 3.12, `st_ctime` from `fstat` of a file handle is the ChangeTime while `st_ctime` from `stat` of a path is the creation time, so the two values differ. As a result almost every read ended in an origin-replaced error. On Windows the path stat comparison was reduced to the first 4 fields, and ChangeTime is now compared separately from the handle (commit `bc287ef`).
2. **Load performance.** Cold loading was 21.3 ms per block because of a SUM that scanned the whole cache blob on every write and the fsync of `synchronous=FULL`. With `synchronous=NORMAL`, a (touched, cache_key, byte_count) covering index, and `BEGIN IMMEDIATE` it became 4.2 ms (`results/windows_20261101/cache_fix_bench.json`).
3. **Race guard.** When loading the blocks of a file that was just modified, a re-modification within the same clock tick cannot be distinguished by fingerprint. Files whose last modification is within 2 seconds are no longer loaded (`RACY_WINDOW_NS`).
4. **The repeat validation counted a skip as a pass.** The condition `'skipped' not in proc.stderr` was added to the verdict in `repeat_validation.py`. With this fix, the `OK (skipped=1)` in the repeat record above counts as a failure.
5. **The diagnostics script could not tell SSD from HDD.** `MediaType` of `Win32_DiskDrive` showed only `Fixed hard disk media` for all three devices (`results/windows_20261101/diagnostics.json`). The result of `Get-PhysicalDisk` was added to `windows_diagnostics.ps1` under the key `physical_disks_storage`. The existing keys were kept.

### Benchmark and prefetch figures

| Item | Value | Source |
|---|---|---|
| SQLite cache cold load (3.11) | 21.338 → 4.206 ms/block | `results/windows_20261101/cache_fix_bench.json` |
| SQLite cache hit (3.11 / 3.12) | 1.757 → 1.140 ms/block (0.979 on 3.12 after the fix) | same file |
| HDD cold / SSD cold (median of 3) | 71.21 s / 1.11 s, non-administrator unbuffered substitute | `results/bench_20261101/20261101_204602.json` |
| Naive prefetch prewarm / replay | 29.09 s / 0.070 s | same file `prewarm_naive` |
| Physical-position-sorted prefetch | cold 17.8 s → prewarm 11.1 s + replay 0.05 s (13.94 times the SSD cold of 0.8 s) | `results/prefetch_20261101/summary_table.json` |
| Stress (Windows) | 50 seeds × 250 operations = 12,500 operations, 11,239 reads verified, 0 corruptions | `results/windows_20261101/stress_win.json` |

### Relocation experiments (E2, E3)

Access-order relocation (`relayout/`, FSCTL_MOVE_FILE) was run with administrator rights. The design criteria and verdicts are in section 5.1 of `docs/design_v1_relayout.md`.

| Experiment | Before relocation | After relocation | Integrity | Source |
|---|---:|---:|---|---|
| E2 VHDX (C: NVMe, 16 files, 463,470,592 bytes) | cold replay 0.736 s | 0.342 s | hashes 16/16 match, manifest identical, order 949 → 1 stretch | `results/relayout_e2/summary.json`, `results/relayout_e2/dataset.txt` |
| E2 VHDX (D: HDD, same data) | cold 27.1 s (13.7 / 64.6 / 27.1) | 5.73 s (6.07 / 5.73 / 5.68) | hashes identical | `results/relayout_e2_hdd/summary.json` |
| E3 D:\AsterBench (64 files, 2.1 GiB, 4,000 trace entries) | direct 17.11 s, prefetch + replay 8.04 s | direct 7.72 s, prefetch + replay 1.09 s (prefetch 1.05 s + replay 0.047 s) | 1,048 tasks moved, 243.5 MB, 0 failures, hashes 64/64 match, manifest identical, order 1,059 → 1 stretch | `results/relayout_e3/20261101_225446/summary.json` |
| E3 re-measurement (1 hour after the move, no move, `-SkipRelayout`) | — | direct 7.49 s, prefetch + replay 1.14 s (prefetch 1.09 s + replay 0.056 s) | manifest identical | `results/relayout_e3/20261101_235628/summary.json` |

- The cold condition was produced by clearing the standby list with administrator rights (`purge` succeeded in E2 SSD, E2 HDD, and E3).
- The SSD baseline of E3 is the C: NVMe direct cold of 0.763 s. The prefetch + replay of 1.09 s is 1.43 times this value. In the re-measurement 1 hour later the SSD was 1.218 s and prefetch + replay was 1.14 s (0.94 times). The SSD cold value fluctuated between 0.76 and 1.22 s from run to run.
- The prefetch of 1.09 s may reflect the drive cache (SMR media cache, DRAM) right after the move. Using the stable sequential speed of the same drive, 90~110 MB/s, it would be about 2.5 s. It is not treated as sustained performance until a re-measurement 24 hours later.
- As of 2026-11-01, E2 did not run the 5 forced terminations, the forced VHDX detach, or `chkdsk /scan` (on 2026-11-02, S1 ran resume after 5 interruptions and the forced detach; see the section below). E3 has 3 measurements right after the move and 1 re-measurement after 1 hour, and has no measurement after 10 minutes idle and `defrag D: /O`.
- Relocation revert (`relayout.cli revert`) was not run. Hash verification confirmed only that the data content is the same.
- Concurrent stream (1 hour later, administrator cold, `results/relayout_e3/20261101_235628/F-concurrent.json`): at 0 µs of compute per request the wall clock was 1.65 s (8.9% of requests went to disk), at 250 µs it was 2.36 s, and at 1000 µs it was 6.13 s (1 disk request each). These were shorter than the non-administrator SSD baselines for the same compute conditions of 2.98 s and 6.30 s (`results/stream_tuning_20261101/ssd_think_baseline.json`). The two SSD values are reference values because the conditions differ.
- Stream tuning (`results/stream_tuning_20261101/`, non-administrator, unbuffered substitute cold): queue depth 1, 1 MiB chunks, and keeping handles was the fastest (on a close-handles basis queue depth 4 was 1.66 s, depth 1 was 1.48 s, depth 1 with handles kept was 1.42 s). The SEQUENTIAL_SCAN hint was 10.7~16.9 times slower under the same conditions.

### Result file list

- `results/windows_20261101/`: `win_checks.md`, `win_checks.json`, `win_checks_raw.txt`, `cache_fix_bench.json`, `bench_cross_drive.json`, `api_microbenchmark_win.json`, `physicaldisk.json`, `diagnostics.json`, `unittest_win.log`, `repeat10_win.json`, `stress_win.json`, `stress.out.txt`, `integration_win.json`
- `results/bench_20261101/`: `20261101_204602.json` (adopted), `run_stdout.txt`, `superseded_20261101_203628.json.txt` (earlier run, for reference)
- `results/prefetch_20261101/`: `summary_table.json`, `measure.py`, `prefetch_measure_partial.json`

### Remaining issues

- `repeat_validation.py` still expects `'Ran 35 tests'`. The current `unittest discover` runs 59, so this script judges a full run of 59 as a failure. Changing the expected count was outside the scope of this fix, so it was left as is.
- The cache hit of 1.140 ms on 3.11 exceeds the criterion of 1.1 ms set in `cache_fix_bench.json`. On 3.12 it does not exceed the criterion (0.979 ms).
- The cold measurements are an unbuffered substitute under non-administrator conditions (this section is limited to non-administrator measurements; clearing the standby list with administrator rights was done in E2 and E3).
- The two HDD cold experiments (benchmark 71.2 s, prefetch experiment 17.8 s) differ in dataset and cold method and cannot be compared directly.
- Power loss during writes, SSD damage, BitLocker, DirectStorage, and minifilter environments were not tested.

## Windows hardware validation (2026-11-02)

### Environment

- Windows 11 Pro 10.0.26200, administrator rights (administrator scripts were run), 32 GiB of memory
- D: WDC WD20EZAZ (SATA, origin HDD, reported as SMR in secondary sources), C: SAMSUNG MZVLB1T0HBLR (NVMe, SSD baseline and sparse copy)
- Real game: Frostpunk 9.2 GB, D:\Frostpunk, no anti-cheat

### Results

| Item | Result | Source |
|---|---|---|
| A. Real game trace | One recording of loading the first map with the ETW live session (`aster trace --live`), 0 lost. 24 files, 2.02 GB, 30,844 blocks of 64 KiB | `results/csharp_e4_20261102_105938`, `results/csharp_e4_20261102_111921` |
| A. Relocation | Pass 1: 639 MB moved into a 1.5 GiB zone in 131 s (4.9 MB/s); pass 2: another 212 MB moved into a 2 GiB zone in 45 s. SHA-256 identical before and after, contiguity-verified fragments 1,394 → 50 | same folders |
| A. Cold replay (standby list cleared, median of 3) | 24.3 s before relocation; 21.7~22.6 s direct after relocation; zone-order prefetch 15.1 s + replay 0.3 s. The prefetch of 15.1 s (about 135 MB/s) is the sequential limit of this HDD | same folders |
| B. SSD baseline (NVMe sparse copy, same read pattern) | cold replay 3.36 / 3.82 / 4.55 s, median 3.8 s | `results/csharp_e4cmp_20261102_111921` |
| B. Concurrent prefetch | Reading together with the game start is bound by the HDD sequential limit (around 15 s) and cannot beat the SSD | same folder |
| B. Prewarm | Clear standby list → zone prefetch 15 s → wait 60 s → replay 0.30 / 0.48 / 0.71 s, median 0.48 s = 0.13 times the SSD cold. Assumption: prewarm must finish at least 15 s before the game starts, and the effect shrinks under memory pressure | same folder |
| C. Synthetic dataset persistence | Layout held 10 hours after relocation (39 extents, same start LCN), prefetch + replay 1.14 s (1.09 s right after, 1.14 s after 1 hour), direct cold with the standby list cleared 8.65 s (17.1 s before relocation). 24 hours not measured | `results/prefetch_sweep_20261101/contig_persistence_t10h.json`, `results/relayout_e3/20261102_091906` |
| D. S1 VHDX relocation | Resume after 5 interruptions, forced detach, SHA match, manifest identical after reattach, identical after revert. `chkdsk` could not start with standard handle redirection, so it is an optional item | The result folder path could not be confirmed when this document was written, so it is not listed |
| D. S2 probe and comparison | C# probe and Python probe results match (0 to move on the relocated dataset, normal). The C# stream is about 0.5 s slower than Python, cause not determined. Per-stage times in the warm state are the same | Result folder path not confirmed |
| D. S3 ETW and WPR | Live loss 0. Union of 3,722 blocks, 243.9 MB versus 3,716 blocks baseline. 0 `.etl` loss with a 1 MB × 512 buffer `.wprp`. A name resolution defect caused by FileObject reuse was found and fixed with the first `.etl` | Result folder path not confirmed |
| D. S4 service game start detection | Polling 50 ms: detection latency median 27 ms, p95 58 ms (passed). ETW FlushTrace 50 ms: p95 119 ms (previously 1,650 ms). Polling is the default | Result folder path not confirmed |
| E. Kernel minifilter AsterFlt | Release and Debug x64 builds succeeded with NuGet WDK 10.0.26100.6584, 0 warnings. 0 static analysis warnings. `infverif /u` passed. `infverif /w` failed on the Instances key and DIRID 12 issues, decision deferred. The port name escape bug was fixed. No signing, loading, or installation. The CI workflow `kernel-build.yml` was added (not yet run) | section 7 of `kernel/README.md` |
| F. Python reference implementation | Eviction determinism, 3.12 summation determinism (same order as C#), resolving the drive root D:, skipping missing files and still tallying, `bench.shadow_copy` added. Tests 129 → 135 (pass on 3.11 and 3.12). The intermittent failure `test_116` was resolved (0 failures in 40 runs) | Result file path not confirmed (test run record) |
| G. Final C# version | Re-apply cancellation (stops per fragment when a game starts, `resume-needed`), empty game list allowed, Velopack updates only the tray (service version mismatch is displayed), data folder owner and ACL lockdown, ETW field validation, `--dump-first`, `--stop-file`, RealPaths D: fix, service watcher default polling, prewarm (`prewarm` configuration, `aster prewarm`, `aster status`), tests 405 → 475 or more. CI (`csharp-ci.yml`) succeeded on the last push on 2026-11-01 | `csharp/README.md` |

### Remaining limits (2026-11-02)

- 24-hour persistence not measured. Anti-cheat games (MW4 Ricochet, LoL Vanguard) not validated. MW4 is not installed. CMR drives not measured.
- The cause of the 0.5 s delay of the C# stream in S2 is not determined.
- The real behavior of in-service prewarm is still being tested with administrator rights.
- The `infverif /w` decision is deferred.
- The Python replayer has an overhead of about 0.24 ms per request (a limit of the measuring tool).
- The sparse copy tool allocates all the space (being fixed).
- The SSD 3.8 s in B and the SSD cold value of the synthetic dataset above differ in data and method and are not compared directly.

### Result folder list

- `results/csharp_e4_20261102_105938`, `results/csharp_e4_20261102_111921`
- `results/csharp_e4cmp_20261102_111921`
- `results/prefetch_sweep_20261101/contig_persistence_t10h.json`, `results/relayout_e3/20261102_091906`

## E6 cold limit experiment (2026-11-02)

- Method: `csharp/scripts/cs_e6_cold_limit.ps1` (administrator). Raw LBA curve, raw read of the hot set, full move with preservation off, re-measure with the standby list purged before each run, SHA-256 before and after, DiskIo trace. Source: `results/cold_limit_20261102_155900/README.md`.
- Result: after the full move to the 42.142% zone, cold prefetch median 11.93 s (3 runs; 15.13 s with preservation on). Direct replay median 17.64 s. SHA-256 identical for 20 files, contiguous verify (493,352 clusters). The move took 270 s for 2.02 GB.
- High band (90.00%): prefetch median 19.06 s, about 1.6 times slower. The hot set was moved back to the 42.142% zone, SHA-256 identical, contiguous.
- Reading width and queue depth had no effect (16.05~16.91 s). WOF compression ratio 1.014. Not run: E6-4 (overwrite-fill of the cache).
- Conclusion: the default plan moves everything (preservation off). Limits: one SMR drive and one game.

## Linux container validation

Validation date: **2026-11-01**. Environment: Linux container, Python 3.13.5, built-in SQLite library. It was not run on a real user's Windows/HDD/SSD.

## Reproduction and test record

| Group | Actually run | Result | Description |
|---|---:|---|---|
| New v0.7 regression checks | 35 | **35/35 passed** | test_86..test_120 |
| Re-run of the new checks | 10 × 35 | **350/350 passed** | tests run in a fresh Python process each time |
| Random real-file data operations | 50 seeds × 250 operations | **12,500 operations finished without errors** | origin writes/replacements + exact read byte comparison |
| Earlier v0.6 regression checks | 35 | **35/35 passed** | the old source was run from a separate directory |
| Earlier v0.5 regression checks | 24 | **24/24 passed** | the old source was run from a separate directory |
| Plan → evaluate → RAM prewarm → SSD prewarm → API re-read | 1 temporary project | **passed** | origin SHA256 identical before and after prewarm |
| API processing overhead comparison | 15 alternating-order runs | done | includes Linux / same file system / OS file cache effects |

The cumulative number 120 is a **validation item ID** numbered continuously from earlier versions. It does not mean 120 independent Windows game speed validations. The 350 checks from 10 repetitions are the new 35 repeated under the same test conditions, and they are not independent real game datasets.

### Scope of the 35 new checks

| Numbers | Focus |
|---|---|
| 86~93 | Byte correctness, overlapping reads, block boundaries, EOF, rejecting invalid requests |
| 94~99 | Path escape / symbolic links, reuse after restart, capacity limit, eviction |
| 100~108 | SQL cache data/hash corruption, deletion, file modify/replace/truncate, change during a read |
| 109~115 | Cache write failure / low free space, concurrent threads, multiple DB connections, incomplete transactions, prewarm plan separation |
| 116~120 | Protection of frequently read blocks, lookup from a second DB connection, small capacity, no-cache-fill mode, byte consistency after eviction |

## Handling of origin changes and data errors

- **Before and after a read**, the size, inode, and modify/change times of the open file are checked, and whether the path points to a different file is also compared.
- If the origin is observed to have changed, the block keys of a new generation are used, or the request is treated as an error.
- If the internal SSD cache data bytes and SHA-256 do not match, the data is re-read from the origin.
- Only after the whole API request has passed origin information validation are the new blocks loaded in one SQLite transaction.
- Unlike v0.6, which split data and hash into two files, they are stored together in one row for an atomic commit. However, this does not solve physical failure of the SQLite DB itself or simultaneous malicious tampering with the origin.
- WAL + `synchronous=NORMAL` is used. On power loss or an OS crash the last commit may be rolled back (the DB is not corrupted), and since it is a cache it recovers by reloading. The durability this setting provides follows the conditional guarantees in the official SQLite documentation. There was no direct power-cut experiment. Blocks of an origin whose last modification is within 2 seconds are not loaded (race guard, `RACY_WINDOW_NS`).

## Real-byte replay with 50 random seeds

This run of `results/stress.json`:

- 50 seeds, 250 operations per seed = 12,500 operations
- Response compared with the expected origin bytes and passed for **11,239 reads**
- Includes **768 range modifications** and **493 atomic replacements** of origin files
- Test files are created only in temporary directories, and the cache and origin may be on the same physical device
- The recorded `origin_bytes` is **the length the application read through origin.read()**, not the physical bytes the real HDD head moved to read.

## Container API micro-benchmark (for reference)

**Median of 15 alternating-order tests.** Eight 64 KiB blocks in one file are accessed five times each (1 batch = 40 requests), and the same 40 are repeated. The first batch is not all cache misses but **8 misses + 32 hits**, so it must not be interpreted as pure cold loading performance.

| Method | First batch of 40 requests | Repeat batch of the same 40 requests |
|---|---:|---:|
| v0.6 per-file cache | 34.701 ms | 12.082 ms |
| v0.7 SQLite cache | 25.976 ms | 10.396 ms |

The values above are **only the Python API processing times seen in that run**. Windows/HDD/SSD separation, running a game, forcibly flushing the OS page cache, memory mapping, and DirectStorage were not controlled. They may even invert on another system, so there is no basis for saying "the game got some percent faster".

The v0.7 cache is not connected to a real game's path, so games do not use the cache above.

## Additional defects found and fixed

1. A problem in the first test where parent path access became `FileNotFoundError` instead of `ValueError` was fixed, and `..` is now rejected beforehand.
2. If SQLite cache eviction depends only on the write time, frequently used old blocks are evicted first. Recent hit history is now kept in bounded memory and updated in a batch at the next cache load.
3. Instead of v0.6's per-block two-file + sync structure, an atomic data + hash SQLite storage was added.
4. Origin reads now continue on an SSD write error or low free space. An initialization failure when the DB itself cannot be opened still exists.
5. Beyond file content, the state **where the path was swapped for a different inode** is now also compared with the stat of the open file.

## Unresolved risks and evaluation gates

- **Automatic acceleration of existing game processes is still 0% implemented.** SSD data is served only to programs that call the user-mode Python API.
- BitLocker, DirectStorage, and minifilter environments are unvalidated. As for real game load time, only the traced replay of one game, Frostpunk, was measured (2026-11-02 section), and anti-cheat games and 24-hour persistence are unvalidated.
- External write detection depends on `stat`/`ChangeTime`, so not every file mutation may be detected.
- Separately from the logical payload limit, the SQLite DB/WAL/page fragmentation takes up storage. A 256MiB limit does not mean the file on disk stays at 256MiB.
- The overhead benchmark includes the cost of Python execution, `fsync`, SQLite, and cache writes, but **it is not a comparison of physical HDD/SSD load**.

Windows validation must compare the same game at least 5 times across HDD alone / RAM-prewarmed HDD / HDD with a transparent cache (not implemented) / direct SSD install, and report p50/p95 including prewarm time, and the physical read volume and wait time.

## Original evidence files

- `results/repeat10.json`
- `results/stress.json`
- `results/integration.json`
- `results/api_microbenchmark.json`
- `tests/test_86_115.py`
- `tests/test_116_120.py`
