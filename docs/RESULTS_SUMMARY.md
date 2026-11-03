**English** | [한국어](RESULTS_SUMMARY.ko.md)

# Results summary

This document carries over the measurement tables and interpretation from the earlier README that were not in `VALIDATION_REPORT.md`. The full record and the validation items are in `VALIDATION_REPORT.md`, and the source path of each table is listed with it.

## HDD and SSD loading benchmark (synthetic dataset, median of 3 runs)

The dataset has 64 files, about 2.0 GiB (2,135,949,312 bytes), 4,000 trace entries, and 64 KiB blocks.

| Item | Median | Values of the 3 runs |
|---|---:|---|
| HDD cold | 71.21 s | 55.66 / 71.21 / 71.32 s |
| SSD cold | 1.11 s | 1.022 / 1.150 / 1.111 s |
| HDD warm | 0.080 s | 0.080 / 0.212 / 0.067 s |
| SSD warm | 0.063 s | — |
| Replay after naive prefetch (total including prewarm) | 29.16 s | prewarm 29.09 s, replay 0.070 s |

- HDD cold is about 64.1 times SSD cold, and the naive prefetch total is about 26.2 times SSD cold.
- Source: `results/bench_20261101/20261101_204602.json`

## Prefetch in physical-position order (separate experiment, median of 3 runs)

This was run with the same synthetic dataset (64 files, 2.1 GiB). The HDD cold in this experiment was 17.3~19.0 s (median 17.8 s), while the benchmark's HDD cold was 55.7~71.3 s. Because the variation is this large even under the same conditions, the difference between the two values is treated as variation, not as a difference in method.

| Configuration | Prefetch | Replay | Note |
|---|---:|---:|---|
| Physical-position sort (`final_physical`) | 11.1 s | 0.05 s | HDD cold 17.8 s, 13.94 times SSD cold |
| Path sort (`final_path`) | 11.4 s | 0.05 s | HDD cold 17.3 s |
| Best of the sweep: physical sort, queue depth 4, gap 0 | 7.80 s | 0.04 s | Total 7.84 s, 7.0 times the SSD cold of 1.12 s |
| Contiguous-layout upper bound: 243 MB of hot data read sequentially as a single file, 4 MiB chunks | 1.36 s | 0.04 s | Total about 1.40 s, 1.6 times the SSD cold of 0.865 s. 1 MiB chunks: 1.09 s |

- The SSD cold of the same experiment is 0.8 s (`ssd_cold`).
- Replay after prefetch is about 37% shorter than HDD cold (calculated value). Based on 431 MiB read, the prefetch throughput is about 39 MiB/s (calculated value).
- On this dataset the difference between physical sort and path sort is at noise level, because the files were written sequentially and had few fragments.
- Source: `results/prefetch_20261101/summary_table.json`, `results/prefetch_sweep_20261101/sweep_20261101_212410.json`, `results/prefetch_sweep_20261101/contig_upper_bound.json`

## Relocation experiment details (2026-11-01)

- **E2 HDD (VHDX, D: HDD).** Cold 27.1 s (13.7 / 64.6 / 27.1), 5.73 s after relocation (6.07 / 5.73 / 5.68). Source: `results/relayout_e2_hdd/summary.json`
- **E3 median (3 runs, after clearing the standby list with administrator rights).** Before relocation, prefetch + replay 8.04 s, SSD (C: NVMe) direct 0.763 s. After relocation, the prefetch median is 1.05 s (0.93~1.63 s, values of the 3 runs 0.97 / 1.09 / 1.68 s) + replay 0.047 s.
  - Ratios: D/E 1.43, C/E 10.1, A/E 22.4, D/A 0.064 (about 15.6 times shorter than before relocation)
  - The earlier README did not give the raw-data path for this median, so it could not be confirmed.

## SQLite cache (256 blocks, 64 KiB, median of 3 runs)

| Configuration | Cold load (ms/block) | Hit (ms/block) |
|---|---:|---:|
| Before the fix, Python 3.11 | 21.338 | 1.757 |
| After the fix, Python 3.11 | 4.206 | 1.140 |
| After the fix, Python 3.12 | 3.967 | 0.979 |

- The 3.11 hit of 1.140 ms exceeds this file's criterion of 1.1 ms.
- Source: `results/windows_20261101/cache_fix_bench.json`

## Stream read tuning (relocated zone, non-administrator)

Values measured on the relocated 243.5 MB zone (1,048 operations). The table values are the median of 3 runs, except `reuse_qd1.json`, which has 5 runs. The cold condition is an unbuffered substitute made without administrator rights (`cold_method: unbuffered_proxy`).

| Condition | Time | Source |
|---|---:|---|
| Queue depth 4, close handles, 1 MiB | 1.66 s | `sweep.json` |
| Queue depth 1, close handles, 1 MiB | 1.48 s | `sweep.json` |
| Queue depth 1, keep handles, 1 MiB | 1.42 s | `reuse_qd1.json` |
| Queue depth 1, chunks of 1/4/8/16 MiB (close handles) | 1.48~1.49 s (no difference) | `sweep.json` |
| 256 KiB, queue depth 1/2/4 | 6.4~6.9 s (3.9~4.6 times slower) | `sweep.json` |
| SEQUENTIAL_SCAN hint | 10.7~16.9 times slower than the same chunk and queue depth | `sweep.json` |

- Queue depth 4 versus 1 is 1.66 s versus 1.48 s within the same run (about 11%). Adding handle reuse, it is 1.66 s versus 1.42 s (about 15%), and those two values come from different runs.
- On the scattered layout before relocation, queue depth 4 helped (`results/prefetch_sweep_20261101`). So this conclusion applies only to the relocated zone.

## Concurrent stream details (2026-11-01)

| Game compute per request | Wall-clock median (run values) | Versus SSD under the same conditions |
|---|---:|---|
| 0 µs | 1.65 s (1.58 / 1.75 / 1.65) | 1.35 times the SSD cold of 1.22 s (2.2 times against the earlier run's 0.763 s) |
| 250 µs | 2.36 s | 0.79 times the non-administrator measurement of 2.98 s |
| 1000 µs | 6.13 s | 0.97 times the non-administrator measurement of 6.30 s |

- The SSD baseline values for 250 µs and 1000 µs are in `results/stream_tuning_20261101/ssd_think_baseline.json` (non-administrator, same compute conditions). They are reference values because the conditions differ from the administrator measurement.
- Source: `results/relayout_e3/20261101_235628/summary.json`, `results/relayout_e3/20261101_235628/F-concurrent.json`

## E6 cold limit experiment (2026-11-02)

The hot set of Frostpunk is 2,020,769,792 bytes (30,843 blocks) on the D: WDC WD20EZAZ (SATA). The standby list was purged before each measurement, and the game was not running. The full record is [results/cold_limit_20261102_155900/README.md](../results/cold_limit_20261102_155900/README.md). Why the experiment was run is in [research_cold_limit.md](research_cold_limit.md).

| Stage | Cold prefetch (pvm), seconds | Direct replay, seconds | Zone position | Share of hot data inside the zone |
|---|---:|---:|---|---:|
| Before relocation (original layout, 35 runs) | 15.05 (1 run) | 24.3 (1 run) | Scattered, 15.47~63.08% | Not applicable |
| First relocation, preservation rule on (earlier value) | 15.13 | 21.7 / 22.6 (2 runs) | Not recorded in the result folder | Not recorded in the result folder |
| Full move, 42.142% zone, first measurement | 12.06 (median of 3; 11.84~12.83) | 21.64 (median of 3; 18.94~27.32) | 42.142% (LCN 205,810,188) | 100% (493,352 of 493,352 clusters) |
| Full move, 42.142% zone, final layout | 11.93 (median of 3; 11.84~12.19) | 17.64 (median of 3; 17.51~18.33) | 42.142% (LCN 205,810,272) | 100% |
| Full move, high band 90.00% | 19.06 (median of 3; 18.12~19.08) | 27.86 (median of 3; 27.59~31.77) | 90.00% (LCN 439,536,840) | 100% |

- Reading width and queue depth had no effect. In the original layout, queue depth 1, 2, 4, and 8 with 4 MiB requests, queue depth 4 with 1 MiB and 16 MiB requests took 16.05~16.91 s.
- The WOF compression ratio is 1.014 at 8 KiB chunks and 1.016 at 16 KiB chunks. This saves only 1.4~1.6% of the bytes read, so it is not worth the decompression cost. 97.7% of the hot bytes are in nine `.dat` files, whose ratio is 1.002~1.003.
- The high band (the outer 90% of the disk) was about 1.6 times slower than the 42% zone (19.06 s against 11.93 s, pvm). The 300 MB/s points of the LBA curve (73.7%, 78.9%, 89.4%, 94.7%, 100%) were measured on empty space, so they are not valid speeds for data.
- Conclusion: the default plan now moves everything (preservation off, `--preserve-mib 0`, no segments kept in place). The final layout stays in the 42.142% zone. The cost is a larger move, about 2 GB taking 270~280 s.

## E7 first-run read-ahead

Conclusion: first-run read-ahead (`auto.learn.enabled`) stays off by default. In the conditions measured, turning it on slowed the replay or showed no proven gain. Stream mode (`stream`), which reads ahead while the game is still reading, was measured as E7d and is not adopted either (see E7d below).

All figures are replay times of traced read patterns, not total load times of a running game. Cold runs purged the standby list before each run. A is the replay without read-ahead; B is the replay with read-ahead on. Runs were alternated in the order A,B,B,A,A,B, three runs each. The synthetic dataset is `D:\AsterBench_e7`, and Frostpunk is `D:\Frostpunk`.

### E7, E7b: first measurement and after the name-resolution fix

| Configuration | Cold replay, seconds (A → B, median of 3) | Read-ahead bytes (B) | Live read names resolved (B) | Notes |
|---|---|---|---|---|
| E7 synthetic scattered, first measurement ([result](../results/csharp_e7_20261102_203358/README.md)) | 21.67 → 44.90 (A/B 0.48) | 1.67~1.84 GB | 0 (no read events) | Names unresolved; reading whole open files competed with the replay for the disk |
| E7b synthetic scattered, after the name-resolution fix ([result](../results/csharp_e7b_gen/README.md)) | 19.82 → 38.71 (A/B 0.51) | 1.14~1.18 GB | 9,913~14,860 resolved, 315~384 unresolved | Hit ratio 26~29% |
| E7b Frostpunk, 42% zone contiguous layout ([result](../results/csharp_e7b_gen/README.md)) | 18.98 → 37.40 (A/B 0.51) | 2.18~2.42 GB | 41,730~44,186 resolved, 369~440 unresolved | Hit ratio 50~58%, no gain |

The name-resolution defect (zero read events) was fixed in E7b. Even so, read-ahead competed with the replay for the same disk, and B was about twice as slow as A.

### Relocation effect (same read pattern, read-ahead off)

| Layout | Saturated replay, A median (s) | Gapped replay, A median (s) |
|---|---:|---:|
| Scattered, a replica of the Frostpunk trace (`fpset`) | 55.3 | 41.0 |
| Frostpunk contiguous (42% zone) | 20.5 | 19.3 |

With the same read pattern, the scattered layout took 41.0~55.3 s and the contiguous layout took 19.3~20.5 s (about 2.1 to 2.7 times, on a median basis). This is the basis for relocation reducing read time ([result](../results/csharp_e7c_gen/README.md)).

### E7c: gap-only read-ahead (`--gap-ms` / `--seq-runs`)

This build reads ahead only in the gaps between game reads. Saturated replay (`bench.replay`, 45 s) and gapped replay (a 120 ms wait every 100 requests) were measured separately. The times in the table are replay seconds (`replay_s` in `summary.json`).

| Dataset | Replay | A 3 runs | B 3 runs | A median | B median | Ratio (A/B) | gap_count | yielded | idle read-ahead (MB) | total read-ahead (MB) | hit ratio |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Synthetic random | saturated | 22.2/37.1/32.5 | 20.1/26.3/31.4 | 32.5 | 26.3 | 1.24 | 5/13/11 | 4/12/10 | 47/40/42 | 51 | 0 |
| Frostpunk contiguous | saturated | 20.5/18.8/21.6 | 19.0/18.1/20.5 | 20.5 | 19.0 | 1.08 | 13/9/9 | 12/8/8 | 117/117/118 | 125~128 | 0.00 |
| **fpset scattered** | **saturated** | 55.3/49.0/55.8 | 49.2/54.9/49.5 | 55.3 | 49.5 | 1.12 | 12/18/12 | 12/18/12 | 6/17/9 | 18/36/21 | 0.003~0.010 |
| Synthetic random | gapped | 22.1/19.4/19.4 | 18.6/22.2/22.3 | 19.5 | 22.2 | 0.88 | 6/8/7 | 5/7/6 | 21/45/45 | 26/51/51 | 0 |
| Frostpunk contiguous | gapped | 23.4/19.3/15.2 | 16.4/19.9/15.1 | 19.3 | 16.4 | 1.18 | 149/135/128 | 148/134/126 | 616/520/600 | 766/656/731 | 0.19/0.16/0.19 |
| **fpset scattered** | **gapped** | 49.7/41.0/40.0 | 52.2/50.1/41.0 | 41.0 | 50.1 | **0.82** | 150/148/163 | 148/147/162 | 436/479/532 | 589/628/700 | 0.14/0.13/0.17 |

- Saturated replay: all three datasets ran 1.08 to 1.24 times faster with B. Read-ahead shrank to 18~128 MB, and the earlier build's roughly twofold slowdown is gone. A varied widely (19~37 s on the synthetic dataset), so this is not counted as a gain; the reading is only that it did no harm.
- Gapped replay, the main evidence (`fpset` scattered): the B median of 50.1 s was slower than the A median of 41.0 s (0.82). The hit ratio was 13~17%, and the 0.4~0.5 GB read ahead in the gaps probably competed with the game reads for the disk.
- The gapped Frostpunk contiguous result of 1.18 times is not significant, because A ranged from 15.2 to 23.4 s.
- Gap detection worked. gap_count was 128~163 in the gapped Frostpunk contiguous and `fpset` runs, and 6~8 in the gapped synthetic random runs.
- The replay is trace-based, and the 120 ms gap is an artificial value. It cannot be taken as matching a real game's pattern.

### E7d: stream mode (`--mode stream`, 16 MiB steps, 512 MiB window)

Same conditions as above: cold purge before each run, order A,B,B,A,A,B, three runs each. Times are replay seconds (`replay_s` in `summary.json`). Raw data: [E7d](../results/csharp_e7d_gen/README.md).

| Condition | A median (s) | B median (s) | A/B | Notes |
|---|---:|---:|---:|---|
| fpset scattered, saturated | 45.1 | 56.2 | 0.80 | B 20% slower; about 2.0 GB read ahead competed with the game reads for the disk |
| fpset scattered, gapped | 51.4 | 46.7 | 1.10 | B runs 80.9/46.7/38.4 s; too noisy to count as a gain in 3 runs |
| Frostpunk contiguous, gapped | 17.7 | 19.5 | 0.91 | read ahead 3.2~3.4 GB, 1.6 times the replay request (2.0 GB); no gain |

Stream mode is not adopted. The saturated case fails the no-harm check (B at least 0.95 × A), and the gapped cases show no reproducible gain.

### E7e: bounded read-ahead (`--mode bounded`)

Reads ahead only sequential files, in 16 MiB steps. The total stays at or below the game's cumulative reads plus 32 MiB, and it yields for 50 ms after each game read. Conditions are the same as E7d (cold standby list purge, order A,B,B,A,A,B, three runs each). Times are the sum of read delays (s). Raw data: [E7e](../results/csharp_e7e_gen/README.md).

| Dataset | Replay | A 3 runs (s) | B 3 runs (s) | A median | B median | A/B | Read-ahead / request | Hit ratio | yielded |
|---|---|---|---|---:|---:|---:|---|---|---|
| fpset scattered | saturated | 49.6/48.0/46.6 | 53.4/54.6/51.4 | 48.0 | 53.4 | 0.90 | about 1.00 | 0.31~0.36 | 120~129 |
| fpset scattered | gapped | 47.2/59.3/42.1 | 40.4/49.0/41.7 | 47.2 | 41.7 | 1.13 | about 1.01 | 0.53~0.55 | 238~261 |
| Frostpunk contiguous | gapped | 16.4/15.2/14.9 | 18.8/17.3/18.2 | 15.2 | 18.2 | 0.84 | about 1.01 | 0.50~0.52 | 160~188 |

- The total limit held (read-ahead ≈ game reads, +0 to 1%).
- The gapped fpset replay is 1.13, but the saturated fpset replay is 0.90, which misses the no-harm criterion (0.95 or more). The Frostpunk contiguous gapped replay shows no gain. Three runs are too noisy to count any gain.
- Verdict: bounded is not yet justified as a default.

### E7f: adaptive (`--mode adaptive`)

Same as bounded, plus a pause while the game's reads over the last 500 ms run at 80 MiB/s or more, and a 200 ms pause when our request latency doubles. Conditions: cold purge before each run; fpset order A,B,B,A,A,B,B,A,A,B (five runs each); Frostpunk three runs. Times are sums of read delays (s). Raw data: [E7f](../results/csharp_e7f_gen/README.md).

| Dataset | Replay | A median (s) | B median (s) | A/B | Read-ahead / request | Hit ratio | yielded |
|---|---|---:|---:|---:|---|---|---|
| fpset scattered | saturated | 45.1 | 52.6 | 0.86 | 0.83~1.00 | 0.26~0.29 | 98~116 |
| fpset scattered | gapped | 38.6 | 38.8 | 0.995 | 1.01~1.02 | 0.51~0.53 | 217~230 |
| Frostpunk contiguous | gapped | 14.8 | 17.4 | 0.85 | 1.01~1.02 | 0.52~0.53 | 160~169 |

- Saturated fpset (0.86) misses the no-harm bar (0.95). The hit ratio is low, and most B runs were slower than most A runs.
- Gapped fpset (0.995) misses the gain bar (1.10). Read latency did not drop even though read-ahead matched the request volume. The earlier 1.13 was likely noise in A; A was stable this time (38.3~38.9 s).
- Read-ahead read about 1.3~1.5 times the replay's total disk reads on the same disk, so the time saved by hits was lost again.
- Verdict: adaptive is not justified as a default.

### Conclusion

- First-run read-ahead (`auto.learn.enabled`) stays off by default. No gain was proven in E7 to E7f (saturated and gapped, contiguous and scattered layouts).
- Stream mode (`stream`) was measured as E7d and is not adopted. `auto.learn.enabled` stays off by default, and this is confirmed.
- The real-time read-ahead family on the same HDD (E7 to E7f, six rounds) showed no gain in any round, and is closed. `learn` stays an experimental feature, off by default. First-run improvement goes to bootstrap (shared profile and Windows prefetch).
- This record is synthetic data and trace replay. It is not the total load time of Frostpunk running in the game.
- Raw data: [E7](../results/csharp_e7_20261102_203358/README.md), [E7b](../results/csharp_e7b_gen/README.md), [E7c](../results/csharp_e7c_gen/README.md), [E7d](../results/csharp_e7d_gen/README.md), [E7e](../results/csharp_e7e_gen/README.md), [E7f](../results/csharp_e7f_gen/README.md).

## Interpretation of the results (carried over)

- **Speed by drive position.** There was no difference in speed by position on the drive (`results/zone_curve_20261101`). So the optimization of placing the hot zone at a particular position was dropped from the design.
- **No gain on the first run.** Relocation takes time to move the files. The gain appears in repeated reads after relocation.
- **Limit of prefetch before relocation.** Before relocation, the number of HDD seeks and the read throughput (about 39 MiB/s) set the lower bound. After relocation the hot zone is read sequentially as one stretch, so this lower bound dropped substantially. Because the prewarm cost is much larger than the replay, prewarm is meaningful only when it can finish before loading.
- **Limit of the cold condition.** The non-administrator measurements replaced the cold condition with an unbuffered re-read (`FILE_FLAG_NO_BUFFERING`). The drive and controller internal caches were not cleared. The run-to-run variation of 55.7~71.3 s in the benchmark is presumed to come from concurrent disk use, but the cause was not measured.
- **Warm state.** In the warm state HDD and SSD differ by a factor of 1.28 (benchmark median). The OS file cache is already filled, so the difference is small.
- **SMR.** The WD20EZAZ has been reported as drive-managed SMR (secondary source). A measurement right after a move may be in a state before remapping. The stable sequential speed of the same drive was 90~110 MB/s (E1b). By this value the prefetch expectation is about 2.5 s.
- **Goal verdict.** The goal (p50 within 1.25 times of SSD) was not judged, because there is not yet a total game load time or the p50/p95 of repeated loads.
- **This is not game load time.** These are results reproduced with a synthetic dataset and a synthetic trace. Frostpunk was traced once with ETW and then replayed. There is not yet a Procmon trace or a measurement of the full game launch time.

## Next directions (the "next directions" section of the earlier README)

- The details of the access-order physical relocation design are in `docs/design_v1_relayout.md`. Section 1.4a has the E1 measurement results.
- The idea of block-level access-order relocation was first presented by BORG (FAST'09). The sources and the Windows API constraints are in `docs/prior_art_and_windows_api.md`.
- The SMR effect has not been isolated yet. Bulk move writes can cause write stalls, so measure with small chunks and a write cap.

## References (technical basis from the earlier README)

- Microsoft, File Caching: https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching
- Microsoft, BypassIO: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- Microsoft, Filter Manager concepts: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/filter-manager-concepts
- SQLite WAL: https://sqlite.org/wal.html
- SQLite `synchronous` pragma: https://sqlite.org/pragma.html#pragma_synchronous

## E8 general HDD I/O (2026-11-03)

- Cold-opening 3,000 files took 30 s (10 ms per file, MFT lookup). Existing prewarm opens and reads the hot files, so their metadata is also warmed as a side effect.
- The first cold read right after 3 GB of scattered writes took 58.8 s (normally 24 s). This looks like post-write cleanup on the DM-SMR drive. Prewarm right after the sequential-write relocation ran at 154 MB/s, which is normal.
- E9: after 2 GB of scattered writes, cold replay took up to 4.45× the baseline (26.3 s) and recovered after about 10 min, so the post-update delay defaults to 15 min.
- Notes: [research_hdd_general.md](research_hdd_general.md)

## E10–E16 HDD general I/O (2026-11-03)

- E10–E11: 4→16 MiB chunks made no difference (2.0 MB/s); per-file fixed cost dominates. Sample reduction, journal batching and a pre-job sample cut the time from 186 s to 88 s.
- E12–E14: original-order relocation was slower (88 → 110 s), so auto uses plan order. Eight parallel reads took the HDD to 41 s (4.5×) and NVMe to 10.6 s, which sets the default concurrency to 8.
- E15–E16: outer-zone raw bandwidth is non-monotonic and unrepresentative, so outer zones are held back. After a 2 GB move, cold reads went 19.5 → 33 s and still took 30 s 15 min after reverting (SMR slowdown).
- Notes: [research_hdd_general.md](research_hdd_general.md)
