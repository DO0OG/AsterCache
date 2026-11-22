**English** | [한국어](USAGE.ko.md)

# Usage

> How to use the tray window and the `aster` command, with examples. After installing, [automatic mode](AUTO_MODE.md) runs each step by itself; this page is for looking, changing, and doing steps by hand.

## Everyday use

- **Nothing to do.** Install, then play as usual (see [Getting started](../README.md#getting-started)).
- **Open the window.** Double-click the tray icon, or use the desktop shortcut. Closing the window sends the app back to the tray.
- **Administrator rights.** Saving settings, tracing, relocation, revert, and service control need them. Without them a banner appears at the top with a **Restart as administrator** button. When installed under Program Files, a logon task starts the tray with administrator rights, so no UAC prompt appears.

## The management window

The left side lists your fixed drives (letter, label, HDD or SSD, free space, number of registered games). Select one to show only its games.

| Tab | What you can do |
|---|---|
| **Status** | See the service state and the last prewarm; the five **Automatic progress** tiles (games found, plans made, relocated, prewarmed, average hit rate); and one card per game with its stage, layout verdict, last relocation, prewarm result, `hit right after prewarm N%`, and the last play. The last play takes two lines: when it was played, the prewarm state at start, and disk read statistics for the first 30 seconds, the first 120 seconds, and the whole play. The play ends at the moment the service first saw the game's processes gone, so the end time and the whole-play figures do not include the 60-second wait before the exit is confirmed. These are disk read counters, not a load-time or frame measurement. A non-default policy or pause also shows on the card. A game that closed right after launch shows "exited right after launch N times, new drivers: ..." on its card (see [FAQ](FAQ.md)). |
| **Games** | See each game's status, prewarm state, hold reason, and anti-cheat marker. Buttons: Register, Remove, Game options, Collect trace, Create plan, Apply relocation, Revert, Prewarm now, Measure, Export profile, Import profile (below). **Show advanced columns** adds paths and settings. Select a game and click **Game options** to open a window with a **Paused** checkbox, a **policy** (`games[].mode`: Everything (default), Prewarm only, Relocate only, or Off, each with a one-line description), and three display-name boxes (default, Korean, English). **Save** applies them and **Cancel** closes the window. The internal name does not change. A game whose policy is not the default, or that is paused, shows that under its name in the list (for example "Paused · Prewarm only"). |
| **Settings** | The **Automatic mode** card (use automatic mode, discover, trace, relocate, SSD assist storage, recent games to prewarm, excluded executables, zone limit, daily write limit); the **Read ahead when a game starts (start stream)** checkbox (`auto.start_stream_enabled`) in the Automatic mode card; **Relocation hours** (`auto.relocate_hours`): **Any time**, or **Set hours** with a start and end hour that may cross midnight (a value that is not a single range is kept and shown as **Custom (edit in the file)**; equal start and end is refused); the **Updates** card (Install automatically, Notify only, or Off; include prereleases; check now); the **Notifications** checkbox; the **Service** card (install, uninstall, start, stop); the watcher; **language**; open the data and log folders; tray and service versions. At the bottom, the **Advanced** section stays closed until you tick **Show advanced settings**; it is described below. |
| **Logs** | The last 200 lines of `service.log` and `tray.log`, with refresh. **Export diagnostics** writes a ZIP file to a place you choose (see the question about it in [FAQ](FAQ.md)). |

**Advanced section (Settings tab).** It holds numeric limits that otherwise can be edited only in `config.json`. Each one shows its default and a one-line meaning; a value outside its range is refused and nothing is saved, and **Apply advanced settings** saves them together with the Automatic mode inputs above. The prewarm cache budget (`prewarm.max_cache_mib`) is shared by all games, not set per game; 0 means half of physical RAM. Saving it in the window writes the same value to every registered game (0 removes the key). The other items are the number of games and the size cap for the prewarm at service start (`auto.startup_warm_games`, `auto.startup_warm_max_mib`), the wait after a game exits before prewarming again (`auto.rewarm_after_exit_min`), the daily relocation count (`auto.max_relocations_per_day`), the idle time (`auto.idle_seconds`), and the time limit of the start stream (`auto.start_stream_cap_seconds`). All keys are in [CONFIGURATION](CONFIGURATION.md).

The tray icon menu has **Open**, **Prewarm now (stream)**, **Check for updates**, and **Install update (version)** when a newer version is ready, plus **Open logs**.

## Common tasks

**See what AsterCache is doing.** Open the **Status** tab. Each game card shows its stage: discovered → collecting trace → waiting to relocate → relocating → done.

**Turn automatic mode off.** In **Settings**, clear **Use automatic mode** and click **Apply automatic mode**. Or set `"auto": {"enabled": false}` in `%ProgramData%\AsterCache\config.json`.

**Keep one game out.** Add its exe to `auto.ignore_exes`, or select it on the **Games** tab and click **Remove** (the exe is added to the list so it is not registered again).

**Pause a game, or limit what it may do.** On the **Games** tab, select the game, click **Game options**, and tick **Paused** or choose a policy. A paused game and a game set to **Off** start no new work and are not traced; a trace that was already running keeps the reads it has recorded. **Prewarm only** prewarms and plans but never moves files. **Relocate only** moves files but does not prewarm. Other games are not affected.

**A game closes right after it starts.** Look at its card in the **Status** tab and at `service.log` for `exited quickly`, `quick exit N in a row`, and `backing off for this run`. AsterCache does not stop the closing; it shows which kernel drivers are new since the last normal run. See the question in [FAQ](FAQ.md).

**Turn updates off, or only be told about them.** In **Settings**, open the **Updates** card and choose **Off** or **Notify only**. See the updates questions in [FAQ](FAQ.md).

**Put a game back.** On the **Games** tab, select the game and click **Revert**. The journal restores the previous layout; the file contents never changed. Revert is refused while the game is running.

**After a big game patch.** The service re-checks the layout itself (see [FAQ](FAQ.md)). To redo it by hand, run **Collect trace**, **Create plan**, and **Apply relocation** again.

**Uninstall.** Use Windows' Installed apps. The data folder `%ProgramData%\AsterCache` stays; run `cs_service.ps1 -Action uninstall -PurgeData` to delete it too.

**Move a recording to another PC.** `aster profile export` and `aster profile import` (below). An import is accepted only when the file size and a sampled hash match.

## Doing it by hand (optional)

Use this to control each step, or to try a game that automatic mode skipped. Open the **Games** tab as administrator and press the buttons in this order.

| Step | Button | What it does |
|---|---|---|
| 1 | Register | Pick the game's exe file. The origin and allowed folders are set to its folder and the volume label is filled in; every field can be edited, including the prewarm trigger program. |
| 2 | Collect trace | Run the game, load up to the first map, and quit. This records the read order (ETW, administrator). |
| 3 | Create plan | Decide where the pieces should go. The zone is 1.1 times the hot data. |
| 4 | Apply relocation | A dialog shows the bytes to move and that reverting is possible. Then the pieces are moved and a journal is kept. At most 2 GiB is written at a time, so a big move may say "Run again and it continues". |
| 5 | Prewarm now | Load the zone into RAM right away. |
| 6 | Measure | Time one read of the zone. Reading warms the file cache, so repeating needs a cleared cache. |

## The `aster` command

`aster` runs the same operations as the window, for automation. Check any command with `aster <command> --help`; the full option list is in [csharp/README.md](../csharp/README.md).

- Run `trace --live`, `probe`, `apply --execute`, `revert --execute`, and `tier apply|revert|clean` from an **administrator** PowerShell.
- Output is one line of JSON (`aster discover` prints text unless you add `--json`). Exit codes: 0 success, 1 run error (an `{"error": ...}` line), 2 bad argument, 3 not administrator (`{"error":"not_admin"}`), 4 busy (another run holds the data folder).
- Global options, placed before the command: `--lang ko|en` sets the language of messages (see [Language](CONFIGURATION.md#language)); `--result-file <path>` also writes the output to a file.

| Command | What it does |
|---|---|
| `discover [--json]` | Lists games found in launcher libraries. Read only. |
| `status [--plan <plan>] [--journal <journal>]` | Read-only overview: administrator rights, disks, last prewarm, automatic mode. With `--plan`, also the layout verdict. |
| `trace --live --exe <exe> --origin-root <folder> --out <file> [--seconds N] [--io-summary-root <folder>]` | Records a game's reads with ETW. Repeat `--io-summary-root` to also collect disk-read completions and, after the session stops, convert them to files under those folders by reading file layouts (not file contents); the result is saved to `<out>.io.json`. `--csv` and `--etl` convert an existing recording instead. |
| `plan --trace <file> --origin-root <folder> --zone-mib N --out <file> [--dry-run]` | Builds the layout plan from a trace. `--dry-run` only shows the numbers. |
| `plan estimate --io-summary <file> [--io-summary <file> ...] --origin-root <game folder> [--plan <file>] [--zone-mib 2048,4096,...] [--zone-start-lcn N] [--evaluate-io-summary <file> ...] [--position-base-ms 6] [--position-full-ms 12] [--transfer-mbps 150] [--session-decay 0.7]` | Estimates current and virtual placement from physical-read summaries under the required game folder without moving files. Files outside the folder are excluded and counted. Prints one JSON result to stdout and short English summaries to stderr. Repeated `session_id` values are counted once. `--evaluate-io-summary` plays are never used for selection; they are only placed by each selection and measured. |
| `probe --volume D:` | Checks a volume before relocation. |
| `apply --plan <file> --allowed-root <folder> --require-volume-label <label> [--dry-run \| --execute]` | Moves the pieces. Without `--execute` nothing moves. If the label differs, nothing is touched. |
| `verify --plan <file>` | Checks whether the layout is still in place. |
| `revert --journal <file> --allowed-root <folder> [--execute]` | Restores the previous layout from the journal. |
| `prewarm --plan <file> --origin-root <folder>` | Reads the hot data into RAM in zone order. `prefetch` is the same command with more options (`--budget-mib`, `--method`, `--timing`). |
| `launch --game <program or URL> --trace <file> --origin-root <folder>` | Starts the prefetch first, then the game. `--no-prefetch` starts only the game (for comparison). |
| `learn --exe <exe> --root <folder>` | The experimental first-run read-ahead from a console (off in automatic mode by default). |
| `profile export --game <name> --out <file>` / `profile import --file <file> --game <name>` | Moves one game's recording between PCs. |
| `tier status` / `tier apply --game <name>` / `tier revert --game <name>` or `--all` / `tier clean` | The optional SSD assist storage. |
| `orbit mode off\|shadow\|active` | Sets the ORBIT mode. See [ORBIT](ORBIT.md). |
| `orbit calibrate`, `orbit model`, `orbit-bench run\|rescore\|summarize` | ORBIT calibration and measurement. See [ORBIT](ORBIT.md). |
| `stream-bench` | Read benchmark of the prefetch methods. |

## Examples

Values below are examples; yours will differ.

**List the games found in your launchers.** One line per game: name, launcher, `hdd`, `ssd`, or `?`, the exe (or install folder), and the anti-cheat marker (`-` for none), separated by tabs.

```bat
aster discover
```
```text
Frostpunk	steam	hdd	D:\Games\Frostpunk\Frostpunk.exe	-
```

With `--json`, the same list is one JSON line with the keys `name`, `launcher`, `install_dir`, `exe`, `on_hdd`, `trigger_exe`, and `anticheat`.

**Do the steps by hand.** Run the first, second, and fourth from an administrator PowerShell.

```bat
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

`--zone-mib 1536` is an example; set it to fit the game's hot data size. Run `--dry-run` first: it moves nothing and only shows the plan.

**Run without administrator rights.** `apply --execute` changes nothing and exits with code 3:

```text
{"error":"not_admin"}
```

**Estimate relocation from physical reads.** The required game folder is the basis for anti-cheat and running-game checks. The command reads only the summary, optional plan, and file metadata/layout; it excludes and counts files outside the folder. It does not read game-file contents or write plans or journals.

```bat
aster plan estimate --io-summary trace.run14.json.io.json --origin-root D:\Games\Frostpunk --plan plan.json --zone-mib 2048,4096,8192,16384
```

The JSON includes `inputs` and `combined` estimates, block classification and reason totals, paging-request totals (`paging`), `Order` logical-range plan coverage, and per-zone current-placement coverage that counts a block as inside only when all of its physical pieces are inside. Sequence distance includes movement between pieces within a block. `current_policy` separates attribute-movable blocks from current-policy candidates, exclusions, and unknowns. Selection movement and required contiguous space use cluster-occupied lengths, including a short final block. Each selection reports selected read-byte and latency ratios, bytes to move, and required contiguous space. `files_outside_origin_root` counts unique excluded files. If neither `--zone-start-lcn` nor a resolved plan zone is available, the estimate uses LCN 0 and identifies that source under `zone_starts`. `zone_starts` is set from the first valid layout lookup on each volume and also reports `plan_zone_clusters`, the plan's zone cluster count; `notes` states that a zone is assumed contiguously free from its start and that the plan's fixed target positions are not counted as inside a zone. A file on a volume whose cluster is larger than 64 KiB is `layout_unavailable` (`cluster_larger_than_block`). `--origin-root` must be an existing folder. If policy exclusion data is unavailable, `notes` says so. Results are comparison metrics; they do not predict head travel or loading-time improvement.

Three selection criteria are reported per zone size: `by_latency`, `by_requests`, and `by_link_gain`. `by_link_gain` groups blocks into read segments (neighbouring blocks in first-read order whose disk pieces are also adjacent; a block whose pieces break splits the segment) and picks the segments whose joins save the most model position time within the zone budget, with a dynamic program over a per-byte value found by a fixed 48-step bisection. Segments that cannot be chosen (unmovable, changed during the session, excluded by policy, unknown location) never gain from either neighbouring join. Several `--io-summary` plays are joined most recent first, skipping blocks already seen; play weights are 1, `--session-decay`, `--session-decay` squared, and so on, and a join's weight is the mean block weight of the two segments. Every selection and the current placement report `model` (`segments`, `model_position_ms`, `model_transfer_ms`, `links`, and the median and 90th percentile link distance). The model charges base + full * sqrt(min(distance, disk size) / disk size) milliseconds per non-zero join (`--position-base-ms` 6, `--position-full-ms` 12) and bytes over `--transfer-mbps` (150) for transfer; without `--transfer-mbps` the stored device calibration throughput is used when `disk_model.json` in the data folder has a usable fit. `link_model.sources` lists `defaults`, `arguments`, or `calibration` for each value; the calibration file is only read, never changed or measured. `read_segments` reports the segment count, size distribution, and join distance distribution. `evaluation` places each `--evaluate-io-summary` play by each selection (selected blocks at their virtual zone position, the rest where they are now) and reports the same `model` values plus the share of its blocks not seen in training; a `session_id` present in both `--io-summary` and `--evaluate-io-summary` is an argument error. All three criteria choose only movable blocks that policy does not exclude (`candidate_set`). `realized_link_gain_ms` (`by_link_gain` only) is the current time of the joins the selection makes zero and ignores joins to unselected neighbours; `model_position_reduction_ms` is the real change (current model position time minus the virtual placement's). Each `evaluation` selection also has `link_change`: `removed` (joins that are non-zero now and zero after), `crossing_zone` (one end moved into the zone, non-zero after) and `inside_zone` (both ends in the zone, non-zero), each with `links`, `model_position_ms` (after) and `before_model_position_ms` (the same joins now), so the model time change equals crossing and inside after-minus-before minus removed before. Compare criteria by segment count and model time; the movement-distance sum is dominated by a few full-stroke seeks and is kept only for reference.

**Check that a layout is still in place.**

```bat
aster verify --plan plan.json
```

The reply is the check result plus a `verdict`: `contiguous` (in place), `non-contiguous` (drifted), or `resume-needed` (a move was interrupted and continues later).

**Look at the overall state.** The reply includes the keys `admin`, `plan`, `journal`, `disks`, `prewarm`, and `auto` (abridged here). `prewarm` holds the last prewarm per game and `auto` holds the automatic-mode state.

```bat
aster status
```
```text
{"admin":true,...,"disks":[...],"prewarm":{...},"auto":{...}}
```

**Set the ORBIT mode.**

```bat
aster orbit mode shadow
```
```text
{"mode":"shadow"}
```

**Use the SSD assist storage** (needs `auto.ssd_tier.enabled`; `tier apply` and `tier revert` need administrator rights).

```bat
aster tier status
aster tier apply --game Frostpunk
aster tier revert --all
```
