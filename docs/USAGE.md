**English** | [한국어](USAGE.ko.md)

# Usage

> How to use the tray window and the `aster` command, with examples. After installing, [automatic mode](AUTO_MODE.md) does everything on its own; this page is for looking, changing, and doing steps by hand.

## Everyday use

- **Nothing to do.** Install, then play as usual (see [Getting started](../README.md#getting-started)).
- **Open the window.** Double-click the tray icon, or use the desktop shortcut. Closing the window sends the app back to the tray.
- **Administrator rights.** Saving settings, tracing, relocation, revert, and service control need them. Without them a banner appears at the top with a **Restart as administrator** button. When installed under Program Files, a logon task starts the tray with administrator rights, so no UAC prompt appears.

## The management window

The left side lists your fixed drives (letter, label, HDD or SSD, free space, number of registered games). Select one to show only its games.

| Tab | What you can do |
|---|---|
| **Status** | See the service state and the last prewarm; the five **Automatic progress** tiles (games found, plans made, relocated, prewarmed, average hit rate); and one card per game with its stage, layout verdict, last relocation, prewarm result, `hit right after prewarm N%`, and the last play. The last play takes two lines: when it was played, the prewarm state at start, and disk read statistics for the first 30 seconds, the first 120 seconds, and the whole play. These are disk read counters, not a load-time or frame measurement. A non-default policy or pause also shows on the card. |
| **Games** | See each game's status, prewarm state, hold reason, and anti-cheat marker. Buttons: Register, Remove, Game options, Collect trace, Create plan, Apply relocation, Revert, Prewarm now, Measure, Export profile, Import profile (below). **Show advanced columns** adds paths and settings. Select a game and click **Game options** to open a window with a **Paused** checkbox, a **policy** (`games[].mode`: Everything (default), Prewarm only, Relocate only, or Off, each with a one-line description), and three display-name boxes (default, Korean, English). **Save** applies them and **Cancel** closes the window. The internal name does not change. A game whose policy is not the default, or that is paused, shows that under its name in the list (for example "Paused · Prewarm only"). |
| **Settings** | The **Automatic mode** card (use automatic mode, discover, trace, relocate, SSD assist storage, recent games to prewarm, excluded executables, zone limit, daily write limit); the **Read ahead when a game starts (start stream)** checkbox (`auto.start_stream_enabled`) in the Automatic mode card; **Relocation hours** (`auto.relocate_hours`): **Any time**, or **Set hours** with a start and end hour that may cross midnight (a value that is not a single range is kept and shown as **Custom (edit in the file)**; equal start and end is refused); the **Updates** card (Install automatically, Notify only, or Off; include prereleases; check now); the **Notifications** checkbox; the **Service** card (install, uninstall, start, stop); the watcher; **language**; open the data and log folders; tray and service versions. At the bottom, the **Advanced** section stays closed until you tick **Show advanced settings**; it is described below. |
| **Logs** | The last 200 lines of `service.log` and `tray.log`, with refresh. **Export diagnostics** writes a ZIP file to a place you choose (see the question about it in [FAQ](FAQ.md)). |

**Advanced section (Settings tab).** It holds the numeric limits that were previously only editable in `config.json`. Each one shows its default and a one-line meaning; a value outside its range is refused and nothing is saved, and **Apply advanced settings** saves them together with the Automatic mode inputs above. The prewarm cache budget (`prewarm.max_cache_mib`) is shared by all games, not set per game; 0 means half of physical RAM. The other items are the number of games and the size cap for the prewarm at service start (`auto.startup_warm_games`, `auto.startup_warm_max_mib`), the wait after a game exits before prewarming again (`auto.rewarm_after_exit_min`), the daily relocation count (`auto.max_relocations_per_day`), the idle time (`auto.idle_seconds`), and the time limit of the start stream (`auto.start_stream_cap_seconds`). All keys are in [CONFIGURATION](CONFIGURATION.md).

The tray icon menu has **Open**, **Prewarm now (stream)**, **Check for updates**, and **Install update (version)** when a newer version is ready, plus **Open logs**.

## Common tasks

**See what AsterCache is doing.** Open the **Status** tab. Each game card shows its stage: discovered → collecting trace → waiting to relocate → relocating → done.

**Turn automatic mode off.** In **Settings**, clear **Use automatic mode** and click **Apply automatic mode**. Or set `"auto": {"enabled": false}` in `%ProgramData%\AsterCache\config.json`.

**Keep one game out.** Add its exe to `auto.ignore_exes`, or select it on the **Games** tab and click **Remove** (the exe is added to the list so it is not registered again).

**Pause a game, or limit what it may do.** On the **Games** tab, select the game, click **Game options**, and tick **Paused** or choose a policy. A paused game and a game set to **Off** start no new work and are not traced; a trace that was already running keeps the reads it has recorded. **Prewarm only** prewarms and plans but never moves files. **Relocate only** moves files but does not prewarm. Other games are not affected.

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
| `trace --live --exe <exe> --origin-root <folder> --out <file> [--seconds N]` | Records a game's reads with ETW. `--csv` and `--etl` convert an existing recording instead. |
| `plan --trace <file> --origin-root <folder> --zone-mib N --out <file> [--dry-run]` | Builds the layout plan from a trace. `--dry-run` only shows the numbers. |
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
