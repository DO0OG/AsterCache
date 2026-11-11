**English** | [한국어](ORBIT.ko.md)

# ORBIT: prewarm order adjustment

> Details that continue from [How it works](../README.md#how-it-works) in the README.

ORBIT is a calculation that decides in which order a game's prewarm reads its data. It changes only the order. The data read and the amount read stay the same.

## Why it exists

Prewarm reads a game's hot data into RAM in a fixed order. If a game starts while its prewarm is still running, the prewarm is interrupted, and only what was read by then helps. The data every load needs (the common blocks and the loading head, tier0) is already read first.

What is left is the later part, such as the map chunks of games that are often interrupted. ORBIT asks whether a different order of those 64 MiB chunks would make the data that plays actually need ready sooner.

## How it decides

1. **Learn what is read together.** From the play records it groups the data that is read together (an episode, for example one map's loading) and counts how often each episode occurs.
2. **Count interruptions.** It counts how often a game starts during a prewarm and interrupts it.
3. **Measure the disk.** Just before its own prewarm, the service reads a little from the disk to learn the range of seek latency and transfer rate.
4. **Try other orders.** It calculates candidate orders in chunks of about 64 MiB and compares each with the current order. The first tier0 chunks stay fixed.
5. **Change only when it is safe.** It uses a candidate only if the calculation shows a gain even in the worst case of the measured range, and every episode stays within `max(5% of its baseline ready time, 200 ms)`. Otherwise it prewarms exactly as before and records the reason (`episode_regression`) in the plan file. The whole decision has a 500 ms budget.

<details>
<summary>Details: disk measurement, method, where a gain can come from</summary>

- **Disk measurement.** About 12 MiB per stream, uncached reads (12 small reads for seek latency and 3 reads of 4 MiB for transfer rate).
- **Method.** Standard techniques (beam search, lower confidence bounds, split conformal intervals, exact binomial intervals) combined for this problem.
- **Where a gain can come from.** The common blocks and tier0 are already read first, so any gain may be limited to the map chunks of games that are often interrupted during prewarm. For a game where no interruption has been observed, the current order is always used (by design).

</details>

## When it turns on

All of these must hold. Until they do, the current order is used.

| Requirement | Detail |
|---|---|
| Mode | `active` (the default). See [Modes and safety](#modes-and-safety). |
| A recorded game | The game has a trace and a prewarm list. |
| Disk calibration | 38 prewarm streams on that disk (19 for fitting and 19 for calibration, per metric). |
| Episode model | At least 3 valid play records and at least 1 interruption event. |

To get the calibration sooner, run `aster orbit calibrate --disk <letter> [--samples N]` with the service stopped and all registered games closed. It stops as soon as the device range exists, usually around 40 samples, about 7 minutes.

<details>
<summary>Details: how the calibrate command samples</summary>

- It takes up to N samples (default 60) at 10-second intervals.
- It skips games that do not yet have a prewarm list, and draws samples from the pool of allowed game files on that disk.
- It stays more than 64 MiB away from the most recent read position, to avoid drive cache hits.
- These bootstrap samples were collected in one time window, so the coverage claim is weak. They are replaced once 38 samples have accumulated during operation.
- To build the episode model from existing play records, stop the service, close all registered games, and run `aster orbit model --game <name>`.

</details>

## Checking that it is on

- **Log.** Each decision writes one line to `service.log`, for example:

  ```
  <game>: orbit active, applied False, reason no_episode_model, blocks <n>, segments <n>, <ms> ms
  <game>: orbit shadow, applied False, reason no_device_calibration, blocks <n>, segments <n>, <ms> ms
  ```

  `applied True` means the order was changed. When the mode drops to `shadow` automatically, a line `orbit mode changed to shadow: <reason>` is written.
- **Status file.** `auto_status.json` has a `shadow_orbit` block with the keys `mode`, `applied`, `device_n`, `value_runs`, `lambda`, `d_low`, and `orbit_reason`.
- **Plan file.** `<plans>\<game>.orbit.json` keeps the episodes, the interruption risk, and the recent decisions, including the rejection reason.

<details>
<summary>Details: what each reason means</summary>

- `no_device_calibration`: the disk is not calibrated yet.
- `no_episode_model`: no usable episode model yet.
- `no_hazard_model`: no interruption rate yet.
- `episode_regression`: a candidate was rejected because an episode would be later.
- `timeout`: the 500 ms budget ran out.
- `calculation_error`: the calculation failed; two in a row drop the mode to `shadow`.

</details>

## Modes and safety

Change the mode with `aster orbit mode off|shadow|active`.

| Mode | Behavior |
|---|---|
| `off` | No calculation at all. |
| `shadow` | Calculates and records, but keeps the current order. |
| `active` | Default. Applies the new order when the rules above allow it. |

It drops to `shadow` automatically on a significant deterioration, two consecutive calibration drifts, or two consecutive calculation exceptions, and never goes back up automatically. Calibration reads run only when no game is running and the disk is idle. `config.json` has no new keys.

<details>
<summary>Details: automatic demotion, safety rules</summary>

- If the settings file is missing the mode is `active`; if it is corrupt, `shadow`.
- Demotion triggers: a significant, bootstrap-confirmed deterioration in the existing `ProbeReadyMs` proxy; two consecutive calibration drifts (coverage monitor failure); or two consecutive calculation exceptions.
- Calibration reads run only inside a disk read-arbitration lease, and only when no game is running and the disk is idle. They run only for games whose anti-cheat policy allows new prewarm (no anti-cheat, or Vanguard).

</details>

## Files

| File | Contents |
|---|---|
| `<data folder>\orbit_mode.json` | The mode. |
| `<data folder>\disk_model.json` | Per-disk calibration samples. |
| `<plans>\<game>.orbit.json` | Episodes, interruption risk, and recent decisions. |

Deleting these files makes the build behave as before. The previous build ignores them and the new keys in `auto_status.json`.

<details>
<summary>Details: orbit.json version 2</summary>

- From version 2, `<game>.orbit.json` stores a dictionary of file paths and block ranges per episode (start block, count). On the same input the file dropped from 39 MB to 2.7 MB, and reading takes tens of milliseconds.
- Version 1 files are converted automatically when read. The decision budget (500 ms) is unchanged.

</details>

## Limits and cautions

- The measured gain is based on one game, one drive, and one session (see below). Generalizing to other games or drives is premature.
- The `ProbeReadyMs` proxy used by the automatic demotion is not the actual ready time. The demotion uses a fixed-seed bootstrap of 1,000 resamples, and it never adopts a candidate automatically.
- The decision result in the actual service has not been confirmed yet.
- It is a reordering only. It does not read less data.

## Measurement record

The bench compares how soon each episode's data is ready under the baseline order and the ORBIT order (service stopped, games closed, standby list cleared before each round, ABBA crossover).

On 2026-11-09, on a test PC with Call of Duty (ABBA, 10 pairs, 20 runs):

| Check | Criterion | Result |
|---|---|---|
| Faster ready times (flag 2) | Value-weighted prep time down by 10% or more, p<0.05 | Median down 18.8%; 10/10 pairs improved by at least 10%; sign-test p=0.001. Passed |
| Total read time (flag 3) | Not worse by 5% or more | Down 0.1%. Passed |
| No late episode (flag 4) | No episode later than max(5% of baseline, 200 ms) at p95 | Worst episode p95 up by 13,096 ms. Failed |

The prep time fell, but some episodes got later.

The values of an earlier run on the same day (21.3% reduction, maximum delay 18.2 s) and the full table are in the [results summary](RESULTS_SUMMARY.md#orbit-prewarm-order-bench-2026-11-09).

Rescoring with the new rule rejected the B candidate as `episode_regression`. The modeled worst episode delay was 55.5 s against the measured p95 of 13.1 s, about 4.2 times larger, which is the conservative direction.

<details>
<summary>For developers: how to measure again</summary>

`aster orbit-bench run|summarize` and `csharp\scripts\cs_orbit_bench.ps1` (administrator rights) run the bench.

**Steps**

1. Build the inputs first. On a fresh install, stop the service and close all registered games, run `aster orbit model --game <name>` to build `<plans>\<game>.orbit.json` from existing play records, and then run `aster orbit calibrate --disk <letter>`.
2. If no prewarm-interruption record exists yet, use `--lambda-per-min` (default 1.0, assuming one interruption per minute) only for the B-arm candidates. It is never used for real service decisions. The result file records `lambda_source` (`model` or `bench_override`).
3. Rescore existing runs against the updated episode model without overwriting them, then summarize the rescored files to report flags 2 to 4:

   ```powershell
   aster orbit-bench rescore --orbit <orbit.json> --in <run.json>... --out-dir <rescored>
   aster orbit-bench summarize --in <rescored\run.json>...
   ```

**Notes on rescoring**

- It recalculates episode timings from each recorded op completion time; it does not repeat disk reads.
- It also reevaluates the candidate with the current `Decide` rule and reports whether B would be adopted, the reason, and the modeled worst episode delay.
- When the inputs contain complete ABBA groups, the output also shows the measured flag 4 p95 delay and its result beside the modeled delay.
- It reads the episode model from the file passed with `--orbit`, and reads only the device calibration (`disk_model.json`) and the game settings from the data folder. Run files outside the data folder end with `game_settings_unavailable`; `--data-dir <folder>` points the rescore at a copy of the data folder.
- `--budget-ms <ms>` sets the decision budget used only by rescore (default 500; the service always uses 500 ms).

</details>
