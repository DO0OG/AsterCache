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
