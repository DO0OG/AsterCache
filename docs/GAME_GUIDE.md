**English** | [한국어](GAME_GUIDE.ko.md)

# Applying AsterCache to a real game

This guide covers what AsterCache does to your hard disk, what to be careful about, and how to apply it to a particular kind of game. Automatic mode does all of this for you ([Automatic mode](AUTO_MODE.md)); the manual route with the Python reference tools is at the end. Commands below run from the top-level folder of the repository (`D:\Git\AsterCache`), and no build or pip install is needed.

Scope of validation: trace conversion, planning, prefetch execution, and the launch script were run end to end with three Procmon-format CSV files (2,000 rows each) built from real file paths in D:\AsterBench. The Python administrator steps (relocation `probe`, `apply`, `revert`) were verified only up to getting `not_admin` as a non-administrator, and were not run on a game.
On 2026-11-02, tracing, relocation, replay, and prewarm with the C# tools were run end to end on a real game, Frostpunk (D:\Frostpunk, no anti-cheat). Anti-cheat games have only usage records and no controlled validation.

## Effect on the hard disk

What each feature does to the drive that holds the game:

| Feature | Reads | Writes | Limits |
|---|---|---|---|
| Trace | Only observed through Windows event tracing | Files in the data folder (trace, plan, journal, log) | Stops at 1800 s or 6144 MiB (`trace_cap_seconds`, `trace_cap_mib`) |
| Relocation | The pieces it moves | **The only large write.** It rewrites the moved pieces at a new place on the same drive; the file content does not change | See "Relocation limits" below |
| Prewarm | The zone, into the RAM file cache | None | RAM budget: half of physical RAM, and free RAM stays above `prewarm.min_free_ram_mib` |
| Keeping the prewarm | 8 samples of 1 MiB every 10 minutes (`rewarm_minutes`); a re-read of the zone when the cache has gone cold | None | Only for games inside the RAM budget |
| Keep-awake | 4 KiB every 10 minutes, while a launcher is open or at your usual play hour | None | `auto.keep_awake`; power settings are never changed |
| Keeping the cache in order (SOLAR) | Pages already in the file cache, touched again in memory; it stops once more than 32 MiB is counted as read from the disk, so at most about 100 MiB in the worst case ([SOLAR](SOLAR.md)) | None | Not on a disk where a game is running; `memory.standby_ladder` |
| ORBIT calibration | About 12 MiB per sample stream, uncached ([ORBIT](ORBIT.md)); not done for a prewarm that a launcher starts | None | Only when no game runs and the disk is idle |
| SSD assist storage (off by default) | The copied files | Copies to the SSD, not to the HDD | Budget 8 GiB per game ([Automatic mode](AUTO_MODE.md#ssd-assist-storage-optional)) |

- **Relocation limits.** The zone is at most 2048 MiB by default (`max_zone_mib`). At most 4 relocations run in 24 hours (`max_relocations_per_day`). At most 8 GiB a day (`max_relocate_gib_per_day`; 2 GiB if the drive looks like SMR), and at most 2 GiB per round (1 GiB if it looks like SMR).
- **How long it takes.** Relocation took about 5 minutes per 2 GB. On Frostpunk, 639 MB moved in 131 s (4.9 MB/s) and another 212 MB in 45 s on one SMR HDD. A prewarm of a 10 GB hot set took 128 to 416 s in the service log ([Results summary](RESULTS_SUMMARY.md)). The drive is busy during that time.
- **Wear, heat, and noise were not measured.** There is no measurement of drive lifetime, temperature, or noise under AsterCache, so none is claimed. The writes are the relocation writes above, which are bounded by the daily and per-round limits.
- **SMR drives.** After bulk writes an SMR drive can be slow for a while. On the test SMR drive, the first cold read right after 3 GB of scattered writes took 58.8 s (normally 24 s), and after a 2 GB move cold reads went from 19.5 s to 33 s and recovery took 25 minutes to 1.5 hours.
  In the service log, confirming recovery after a relocation took between 6 and 149 minutes and did not follow the amount moved. The service therefore waits for recovery before its next job (drive recovery wait in [Automatic mode](AUTO_MODE.md#7-drive-recovery-wait)). CMR drives were not measured. Relocation moves data in 4 MiB pieces by default.
- **Not while you play.** Relocation runs only when the game and its launcher are off and the drive has been idle (under 4 MiB/s for 30 seconds, on AC power or a battery at 50% or more). A prewarm yields at the next 64 MiB batch when a game starts. If a game starts during a move, the move stops after the current fragment and continues later from the journal.
- **RAM.** Prewarmed data is ordinary file cache, which Windows can drop when programs need the memory. By default prewarm holds at most half of physical RAM, and first-run read-ahead (off by default) at most a quarter (also capped at 4 GiB).

## Cautions

- **Back up first.** AsterCache physically moves file data. Back up drives that hold important data before using it.
- **Do not power off or sleep during a move.** A move can resume from its journal, but power loss during writes was not tested. On a laptop, relocation waits for AC power or a battery at 50% or more.
- **Exclude the drive from Windows drive optimization.** Windows defragments HDDs with a weekly scheduled task, which can undo the relocated layout. Open "Defragment and Optimize Drives" from the Start menu, press "Change settings" for the optimization schedule, open the drive list with "Choose", and uncheck the game drive. Do not run `defrag D:` yourself either.
  If the layout drifts anyway, the service notices it and restores it, which is another write.
- **Close the game and launcher** before a manual relocation, and check that no related process remains in Task Manager.
- **Patching or file repair rewrites files.** A game update or launcher repair can undo the layout or make the plan be rejected. The service sets the old plan aside as `.stale` and traces again on the next run. Run `verify` after a patch if you work by hand.
- **Free space.** The zone needs the same amount of contiguous free space on the game drive. Keep some free.
- **Compressed folders.** Files with compression, sparse, or encryption attributes and reparse points are skipped by relocation. They only miss the relocation gain. If you NTFS-compressed the game folder, you can undo it first with `compact /u /s:D:\Games\CoD4`.
- **Anti-cheat games.** Read [Anti-cheat compatibility](ANTICHEAT.md) first.
- **Not tested.** Power loss during writes, SSD damage, BitLocker, DirectStorage, and CMR drives. For the SSD assist storage, only the Frostpunk loading read was measured; power loss during a copy, a full SSD, and anti-cheat games were not tested.

### Safety measures

- **Content check.** SHA-256 samples are compared before and after relocation. If the content differs, it is recorded as a failure. Hash a few large files yourself before and after if you want to be surer:

  ```powershell
  Get-FileHash 'D:\Games\CoD4\bigfile.ff' | Select-Object Hash   # run before and after applying and compare
  ```
- **Journal and revert.** A journal (JSON Lines) is written for every moved fragment, and `revert` returns to the previous physical layout. The earlier journal is kept as `<game>.jsonl.<UTC time>`. Do not delete journals.
- **Allowed folders and volume pinning.** Files outside `allowed_roots` and volumes whose label differs from `volume_label` are not touched.
- **Interruption.** If a game starts during relocation, only the fragment currently being moved is finished and then it stops. A resume point is left in the journal.
- **Splitting over several days.** If another file takes the zone still to be moved, or the plan changes, the parts already moved are not moved again; only the remaining part is planned again into a new free area. Games on the same disk do not claim the same free area.
- **Permissions.** Only SYSTEM and Administrators can write to `%ProgramData%\AsterCache`. Without administrator rights, relocation and revert change nothing and end with `not_admin`.
- Forced termination during relocation was not tested with real game data. In the VHDX test, resume after 5 interruptions was confirmed.

### Undoing it, and what stays

- **Put a game back.** On the **Games** tab, select the game and click **Revert**. It follows the journal and restores the previous disk layout; the file contents never changed. The command line is `revert` (see step 3 of the manual procedure).
- **Turn it off.** Clear **Use automatic mode** in the Settings tab, or add the exe to `auto.ignore_exes`. Files already moved stay where they are.
- **SSD assist.** `aster tier revert --all` removes the links and restores the originals. The copies stay in the tier folder until `aster tier clean`.
- **Uninstall.** The uninstaller reverts the SSD assist links first, then removes the program and the service. It keeps the data folder `%ProgramData%\AsterCache` (settings, plans, journals, logs); that folder is deleted only if you run `cs_service.ps1 -Action uninstall -PurgeData`. To restore the previous disk layout, press **Revert** before uninstalling.

## Which route fits your game

| Your game | What to do |
|---|---|
| An ordinary game without kernel anti-cheat | Nothing. Automatic mode finds it, records it, relocates it while the PC is idle, and prewarms it. See [Automatic mode](AUTO_MODE.md). |
| A game with anti-cheat | Read [Anti-cheat games](#anti-cheat-games) below and [Anti-cheat compatibility](ANTICHEAT.md) before you play. |
| A utility that stays running (Wallpaper Engine, OBS Studio, and the like) | It is not registered as a game ([Automatic mode](AUTO_MODE.md#1-discovery)). |
| A game with a very large hot set | See [Large games](#large-games). |
| You want to check each step yourself | Use [Doing it by hand](USAGE.md), or the [manual procedure](#manual-procedure-python-reference-tools) here. |
| You want to validate the whole process on a real game first | Follow the [Frostpunk procedure](#procedure-validated-with-frostpunk-2026-11-02). |

## Anti-cheat games

Many current games run kernel-level anti-cheat. What AsterCache does and does not do, the per-kind policy, and the usage records are in [Anti-cheat compatibility](ANTICHEAT.md). In short: it reads game files read-only from its own process, observes reads with a live ETW session, and moves file clusters only after the game has fully exited.
It does no code injection, no hooking, and no kernel driver. Anti-cheat games have only usage records (one Vanguard game and one Ricochet game) and no controlled validation, and no guarantee is made about account sanctions.

<details>
<summary>Details: advice for anti-cheat games</summary>

- Automatic mode is on by default. For a game with anti-cheat, turn automatic mode off in the Settings tab, or add the game's exe to `auto.ignore_exes`, before you play.
- **Using Procmon with Ricochet games is not recommended.** No public report of using Procmon with Ricochet was found, and that is not evidence that it is safe. With other anti-cheats (BattlEye, EA), there are cases where the Procmon driver caused the game to refuse to launch, and where the driver stayed after Procmon was closed and a reboot was needed.
  The alternative is Windows built-in ETW (`wpr`, `logman`). "ETW is safer" is a guess, and no report on whether Ricochet conflicts with ETW was found either.
- **Call of Duty: Modern Warfare 4** (released 2026-10-23) requires TPM 2.0, Secure Boot, and the Ricochet kernel anti-cheat driver. Ricochet monitors and reports applications that interact with the game, and there is no public list of allowed tools.
  In the Call of Duty: Modern Warfare 4 beta there was a case of a permanent ban from a false positive, and Activision acknowledged it. The risk of losing an account is real.
- Order of trying: validate the whole process first with a game without kernel anti-cheat (for example Call of Duty 4: Modern Warfare on Steam, 2007, about 8 GB; use only the single-player campaign and do not connect to online modes).
  Then, for Call of Duty: Modern Warfare 4, test only in the offline campaign, with the game and the Battle.net (or Steam) launcher fully closed while relocating, and leave multiplayer and DMZ out of the first test.

</details>

## Large games

Installing Call of Duty: Modern Warfare 4 on an HDD is outside official support, and this tool shortens only the fixed loading reads, not the streaming reads during play. The prefetch floor time is the hot-set size divided by about 160 MB/s (2 GB: about 12.5 s, 10 GB: about 62.5 s); the hot set of that game during loading was not measured.

<details>
<summary>Details: floor-time table and reasoning</summary>

Installing Call of Duty: Modern Warfare 4 on an HDD is outside official support: Activision's PC requirements list "115 GB or more of free SSD space" for all three tiers. Whether installation on an HDD is blocked was not confirmed. All modes reportedly have on-demand texture streaming, so besides load times, stutter during play is also expected on an HDD.
This tool only reduces the reads of fixed loading stretches, and may not reduce the streaming reads that keep happening during play. No load time of Call of Duty: Modern Warfare 4 on an HDD has been reported.

Relocation and prefetch remove seek time but do not reduce the number of bytes read. With sequential reads at about 160 MB/s, the prefetch floor is bytes ÷ 160 MB/s (on a 1 GB = 1000 MB basis; measured in GiB it is about 7% more). These are calculated values; the hot set size of Call of Duty: Modern Warfare 4 during loading was not measured.
For reference, the prewarm hot set that the service recorded for Call of Duty on the test PC is 10,218 MiB ([Real-world usage records](RESULTS_SUMMARY.md#real-world-usage-records)).

| Hot set | Prefetch floor time | Expectation |
|---:|---:|---|
| 2 GB or less | about 12.5 s or less | The seek-removal effect can be large |
| 5 GB | about 31 s | There may be a gain, but the prefetch time may become similar to the loading |
| 10 GB | about 62.5 s | The prefetch floor may be longer than the loading, so the effect is limited |
| 20 GB | about 125 s | Hard to expect an effect |
| 30 GB | about 188 s | Hard to expect an effect |

When the hot set exceeds a few GB, the prefetch time becomes the floor and the gain comes mainly from removing seeks. On the synthetic dataset (243 MB) the prefetch took 1.1 to 1.7 seconds. Frostpunk's read pattern was 2.02 GB, and on the relocated zone it took 15.1 s at the HDD sequential limit (about 135 MB/s).

</details>

## Manual procedure (Python reference tools)

The manual route uses the Python tools and Procmon CSV traces: collect a trace (step 1), plan (step 2), relocate in an administrator PowerShell (step 3), run the game with prefetch (step 4), and maintain (step 5). Automatic mode does all of this for you; use the manual route to check each step yourself. This whole manual procedure has never been run on a game with anti-cheat.

<details>
<summary>Details: steps 1 to 5 and applying it to Call of Duty: Modern Warfare 4</summary>

The steps below are the manual route with the Python tools and Procmon. They need Windows 11 and Python 3.11 or later (tested on 3.11.4 and 3.12.10). Install the game on D: (an NTFS HDD) and keep the trace and plan files outside the game folder (for example `D:\AsterWork`). Use an administrator PowerShell only in step 3; steps 2, 4, and 5 run as an ordinary user.
Do not end paths with a backslash (in CMD `"D:\Games\"` fails to close the quote).

```powershell
mkdir D:\AsterWork
cd D:\Git\AsterCache
python --version
```

**The Python tools read only Procmon CSV.** ETW `.etl` conversion is done with the C# CLI: `aster trace --etl <file> --exe <game exe> --origin-root <game folder> --out <file>` (tested with an `.etl` in S3, `cs_etw_check.ps1`, where a name resolution defect was fixed). The Python `trace` and `plan` commands cannot read `.etl`.

**Step 1. Collect the trace**

This records which parts of which files the game reads while loading. Recording the same load 3 to 5 times lets you tell what is read every time from what is read by chance.

**Games without anti-cheat: Process Monitor**

1. Download Process Monitor (Procmon) from Microsoft Sysinternals and run it as administrator.
2. In Filter > Filter..., add these four conditions as Include and press Add, then Apply: `Process Name` `is` `game executable.exe`; `Operation` `is` `ReadFile`; `Result` `is` `SUCCESS`; `Path` `begins with` `D:\Games\CoD4` (the game install folder).
3. In Options > Select Columns, check that Time of Day, Process Name, PID, Operation, Path, Result, and Detail are on. **Operation, Path, Result, and Detail are required.** Do not sort the display by another column; the tool uses the row order of the CSV as the read order.
4. Turn capture off (magnifier icon, Ctrl+E), clear with Edit > Clear Display (Ctrl+X), and turn capture on.
5. Start the game, proceed to the point where loading ends (the main menu or the level start screen), and turn capture off. Exit the game afterward.
6. In File > Save..., choose `Events displayed using current filter` and format `CSV`, and save as `D:\AsterWork\run1.csv`.
7. Fully exit the game and repeat 4 to 6 to make `run2.csv` and `run3.csv` (and `run4.csv`, `run5.csv` if possible). Each time must be the same load (the same menu path, the same level).

This tool does not filter by process name; it looks only at the Path prefix. If you remove the Process Name filter, reads from other processes such as launchers and antivirus are mixed in if they are in the same folder.

**Ricochet games: leave only an `.etl` with ETW** (for reference; the C# `aster trace --etl` conversion was validated in S3). This `FileIO` profile command was not run; S3 was tested with the repository's `csharp\scripts\aster_fileio.wprp` profile.
Turn the collector on before the game starts and off after it exits, and stop immediately if there is any sign of trouble (launch refusal, warning, account notice). How the collector itself looks to Ricochet is unvalidated, so make the first collection one short offline mission.

```powershell
# Administrator PowerShell. The profile name must be checked locally with wpr -profiles (not confirmed).
wpr -start FileIO -filemode
# ... start game -> loading finished -> exit game ...
wpr -stop D:\AsterWork\ricochet_run1.etl
```

Convert the `.etl` with the C# CLI: `aster trace --etl <file> --origin-root <game folder> --out <file> --dump-first 20` shows the read count and the first 20 entries. If you used Procmon on a Ricochet game, **fully exit Procmon (File > Exit) and reboot the PC** before the next collection or any play, because the Procmon driver can remain after exit.

**Step 2. Create the plan**

**2a. The prefetch plan `plan.json` and trace conversion**

```powershell
python astercache_v07.py plan --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --budget-mib 512 --out D:\AsterWork\plan.json

python astercache_v07.py trace --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --out D:\AsterWork\trace.json
```

`--prefix` is the game folder. Only reads under that path are used, and similarly named folders such as `D:\Games\CoD4Other` are filtered out. For each CSV, one line `{'rows': ..., 'read_success_rows': ..., 'parsed': ..., 'ignored': ...}` appears. If `parsed` is 0 or very small compared with `rows`, the Procmon filter or `--prefix` is wrong (in the test CSV files `parsed` was 1,463 to 1,481).

| File | Made by | How multiple CSVs are combined | Used for |
|---|---|---|---|
| `plan.json` | `plan` | Only 64 KiB blocks read two or more times, weighted toward recent runs (decay of 0.85 each), up to `--budget-mib` | Prefetch (`prefetch.cli --trace`) |
| `trace.json` | `trace` | The union of 64 KiB blocks read at least once in any run (duplicates removed) | Prefetch, `game_launch.ps1` |
| `trace.run1.json`, `trace.run2.json`, ... | `trace` | Separately for each run, in the order read | `relayout.cli plan --trace` (one per run) |

Relocation compares the per-run read orders to find a consistent order, so it takes them separately per run. Both commands unify a file that Procmon recorded in different case (`d:\games\a.bin` and `D:\Games\A.bin`) to the case of the real file; without it the same file would be split in two.

**2b. The relocation plan `relayout_plan.json`**

```powershell
python -m relayout.cli plan --trace D:\AsterWork\trace.run1.json D:\AsterWork\trace.run2.json D:\AsterWork\trace.run3.json `
  --origin-root D:\Games\CoD4 --zone-mib 512 --out D:\AsterWork\relayout_plan.json
```

PowerShell does not expand wildcards such as `trace.run*.json` for native commands, so list the files one by one. With `--dry-run` instead of `--out`, you see only the statistics (3 test CSV files, `--zone-mib 512`):

```
"sessions":3,"cluster_bytes":4096,
"stats":{"hot_bytes":205193216,"moved_bytes":205193216,"tasks":3029,"zone_used_clusters":50096,
         "overflow_bytes":0,"skipped_files":0,"current_runs":3049,"expected_runs":1,...}
```

- `hot_bytes` is the hot data to move (about 196 MiB here). `--zone-mib` must be larger, and if `overflow_bytes` is greater than 0 the zone is too small. Take about 1.2 times the hot bytes as the reference.
- `current_runs` is how many fragments the hot data is scattered into now, and `expected_runs` how many after relocation (1 means one stretch). `skipped_files` counts files skipped because their position could not be determined.
- If `zone_candidate_lcn` in `probe` of step 3 is `null`, there is no contiguous free space: reduce `--zone-mib` or free space.

`relayout plan` also writes `relayout_plan.json.prefetch.json`, the prefetch input with only the blocks that went into the zone, in zone order. After relocation pass this file instead of `trace.json`.
Preview it with `python -m prefetch.cli --trace D:\AsterWork\relayout_plan.json.prefetch.json --origin-root D:\Games\CoD4 --full-file-ratio 2`; without `--execute` it reads nothing and shows only `bytes` and `requests`. `--min-sessions N` removes from the hot set blocks that were read in fewer than N runs.

**Step 3. Relocate (administrator PowerShell)**

Fully close the game and the launcher, right-click PowerShell in the Start menu, and choose "Run as administrator".

```powershell
cd D:\Git\AsterCache

# 1) Read-only check
python -m relayout.cli probe --volume D: --plan D:\AsterWork\relayout_plan.json

# 2) Dry run: decide the zone position, save it to the plan file, and move nothing
python -m relayout.cli apply --plan D:\AsterWork\relayout_plan.json --allowed-root D:\Games\CoD4 --dry-run

# 3) Actual move
python -m relayout.cli apply --plan D:\AsterWork\relayout_plan.json --allowed-root D:\Games\CoD4 --execute

# 4) Check the result
python -m relayout.cli verify --plan D:\AsterWork\relayout_plan.json
```

- `probe`: `free_match` must be `true`; if `false`, stop. `zone_candidate_lcn` must be a number. Without administrator rights it ends with `{"error":"not_admin"}`.
- `apply --dry-run`: `tasks_todo` greater than 0 and no unexpected `skipped_files`. `bytes_todo` is the bytes to be moved.
- `apply --execute`: `failed` an empty array, `stopped` `null`, `sha_mismatch` an empty array, and `moved_bytes` equal to `bytes_todo`. One run moves up to 2,048 MiB by default and may stop with `"stopped":"write_cap"`; **run the same `--execute` command again** and it continues (or raise the limit with `--write-cap-mib`).
- `verify`: it must be `"verdict":"contiguous"`.

The journal is created at `D:\AsterWork\relayout_plan.json.journal.jsonl` if you do not give `--journal`. It is needed for reverting, so do not delete it. To revert (without `--execute`, nothing is moved):

```powershell
python -m relayout.cli revert --journal D:\AsterWork\relayout_plan.json.journal.jsonl --allowed-root D:\Games\CoD4 --execute
```

Time taken: 243 MB (1,048 tasks) of D:\AsterBench took 1 to 2 minutes. The hot data of a game is in the GB range, so it is expected to take proportionally longer (5 GB being tens of minutes is an extrapolation, not a measurement). The Frostpunk figures are in [Effect on the hard disk](#effect-on-the-hard-disk).

**Step 4. Run the game (together with prefetch)**

Run from an ordinary PowerShell.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\game_launch.ps1 `
  -Trace D:\AsterWork\relayout_plan.json.prefetch.json -OriginRoot D:\Games\CoD4 -Game D:\Games\CoD4\iw3sp.exe
```

The script starts the prefetch (`prefetch.cli`, zone order) as a hidden background process, immediately starts the game, then waits until the stream finishes and prints the elapsed time and the bytes read. Test output (`cmd.exe /c exit` instead of a game, with a warm cache; not a speed reference):

```
prefetch started (pid 26632), game started 0.07 s later (pid 32532)
stream done: 0.46 s since launch (read time 0.06 s), 195.7 MiB, 2169 requests, errors 0, 3,445 MiB/s
```

If `errors` is not 0, the trace is stale (a patch, for example).

- `-Game` can also take a URI such as `battlenet://...` or `steam://rungameid/...`. When the launcher starts the game, the prefetch may not finish before the game starts.
- `-NoPrefetch` runs only the game (for comparison). Pass game arguments as an array, like `-GameArgs '+set','x'`.
- The default `-FullFileRatio 2` reads only the recorded blocks. After relocation the rest of a file is not in the hot zone, so reading whole files (the `prefetch.cli` default of 0.3) is wasted time (the amount read grew from 205 MB to 402 MB).

**How to measure the effect.** Right after rebooting the PC (empty cache), run the game with `-NoPrefetch` and time the load (launch to load complete) with a stopwatch; reboot and repeat 3 times. Measure 3 times the same way without `-NoPrefetch`, and compare the medians. If you repeat without rebooting, the Windows file cache always makes it fast and the comparison is meaningless.

**Step 5. Maintenance**

- After a game patch or file verification/repair, check with `verify` whether the layout is broken. If `verdict` is `non-contiguous` or `apply` reports `source_changed`, collect the trace again from step 1 and redo steps 2 and 3. Patched files have a changed size or modified time, so the existing plan is rejected.
- Exclude the game drive from Windows drive optimization (see [Cautions](#cautions)).
- Whether the layout persists over time was confirmed up to 10 hours on the synthetic dataset. There is no 24-hour confirmation.

**Applying it directly to Call of Duty: Modern Warfare 4**

This whole manual procedure has never been run with Call of Duty: Modern Warfare 4, and whether Ricochet conflicts with manual ETW or Procmon collection is unvalidated. The usage records of the service's automatic mode are separate ([Anti-cheat compatibility](ANTICHEAT.md)).

1. **Install path.** Install it on the D: drive and use the install folder the launcher shows (for example `D:\...\Call of Duty`) as is for `--prefix`, `--origin-root`, and `--allowed-root`. The official install size is 115 GB (SSD basis), but secondary sources also give around 200 GB depending on the pack composition.
   Use the actual size on the install screen, and leave contiguous free space on D: for relocation (at least the hot set size). Start after post-install work such as shader compilation is done.
2. **Reference stretch.** In the offline campaign, define the same point (for example, from the main menu starting one mission until a controllable screen appears) as one load, and measure it first with a stopwatch (the `-NoPrefetch` baseline).
3. **Trace (step 1).** Prefer ETW. Make the first collection one short mission and stop immediately if there is a launch refusal, a warning, or an account notice. Procmon only if you accept the risk.
4. **Plan (step 2).** With Procmon CSV files run `trace`, `plan`, and `relayout plan`. If you collected only ETW, stop here until the C# conversion has been validated on a real `.etl`.
5. **Relocate (step 3)** with the launcher and the game fully closed, and while there is no post-install update. When finished, run the launcher's file check/repair once to confirm there is no effect.
6. **Run and measure (step 4)** with `game_launch.ps1`, giving `-Game` the launcher URI if the launcher starts the game.
7. **Maintain (step 5).**

</details>

## Procedure validated with Frostpunk (2026-11-02)

On 2026-11-02 two scripts ran trace, relocation, cold replay, and an SSD comparison end to end on Frostpunk (9.2 GB, no anti-cheat, D: SMR HDD): `csharp\scripts\cs_e4_game.ps1` and `csharp\scripts\cs_e4_compare.ps1`. The measured values are replay times of traced read patterns, not total load times of a running game ([VALIDATION_REPORT.md](../VALIDATION_REPORT.md)).

<details>
<summary>Details: the two scripts and recommended settings</summary>

The target is D:\Frostpunk (9.2 GB, no anti-cheat, D: SMR HDD). The two scripts below were run from an administrator PowerShell relative to the top-level folder of the repository. The C# CLI (`csharp\publish\cli\aster.exe`) must be published first (`csharp\README.md`).

1. **Trace, relocate, and cold replay.** Run it with the game not started.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_game.ps1
   ```

   The script starts the ETW live trace (`aster trace --live`) and waits until the game starts and ends (`-WaitGameSeconds`, default 600 seconds).
   Load up to the first map and exit the game, and it proceeds in this order: plan → record the SHA-256 of the plan file → cold replay 3 times after clearing the standby list → probe → `apply --dry-run` → `apply --execute` → `verify` → re-check SHA-256 after clearing the standby list → cold replay 3 times and prefetch + replay 3 times after relocation.
   The result is left in `results\csharp_e4_<date-time>\summary.json`. The revert command is in `README.txt` in the same folder, and the script does not run it.

2. **Relocate again with a zone that holds the whole hot set, and compare with the SSD.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_compare.ps1 -ZoneMiB 2048
   ```

   Using the trace file from step 1, it plans again into a 2 GiB zone and applies it (data that was already moved stays). It builds a sparse copy of the same read pattern at `C:\AsterShadow\Frostpunk` and measures the SSD baseline 3 times. Then it clears the standby list, loads the zone with `aster prefetch --method pvm`, waits 60 seconds, and measures the replay 3 times.
   The result is `results\csharp_e4cmp_<date-time>\summary.json`.

Recommended settings:

- **Size the zone so it holds all the hot bytes.** `cs_e4_game.ps1` sets the zone to 1.1 times the hot bytes rounded up to 16 MiB units, and skips relocation if it exceeds `-MaxZoneMiB` (`hot_set_too_large`). If the zone is too small, pass `-MaxZoneMiB` at or above the hot bytes.
- **Finish prewarm before the game starts.** Use `prewarm.on_service_start` (default true) in the game entry of the service configuration, or put the launcher in `trigger_exes`. The measurement assumes prewarm finishes at least 15 seconds before the game starts. Prewarm runs only when available RAM is at least the zone size + `min_free_ram_mib` (default 4096 MiB).
  The configuration format is in the "Two operating modes" section of `csharp\README.md`.
- Relocate only while the game is off (the script stops if it is running).

Results and limits are in the 2026-11-02 section of [VALIDATION_REPORT.md](../VALIDATION_REPORT.md). The measured values are replay times of traced read patterns, not total load times of a running game.

</details>

## Limits

The first run has no gain, reads that are not in the trace are not helped, and only Frostpunk was measured by replay (not the total time of running the game). For Call of Duty and League of Legends there is only a tally of the service log.

<details>
<summary>Details: all the limits</summary>

- **The first run has no gain.** The run that collects the trace and the relocation time are pure cost. The gain comes only from repeated loads after relocation.
- **Is the first run required?** Relocation needs a trace. Without one from a first run, automatic mode tries a matching shared profile (`auto.shared_profiles`, weekly download) and then the game's Windows prefetch record. Each gives a boot trace, so relocation can start before the first run.
  Without either, automatic mode still prewarms: the whole folder if it fits the RAM budget, otherwise the files of the game's engine profile (`auto.full_prewarm`, `auto.engine_profiles`, both on by default). Their effect has not been measured yet. A trace recorded on another PC can be brought in with `aster profile import` (see `aster profile export`).
- First-run read-ahead (E7) stays off by default. See [RESULTS_SUMMARY.md](RESULTS_SUMMARY.md#e7-first-run-read-ahead).
- **Reads not in the trace are slow.** Reads such as a different level or mode from the recorded load, or texture streaming during play, are not helped by this tool. Blocks read only once do not go into the zone and can cause seeks during prefetch, so consistency across 3 to 5 runs matters.
- **Anti-cheat.** This tool does not inject into or hook the game; it only reads files from a separate process. Even so, whether an anti-cheat could regard it as an "app accessing game files" has not been controlled-validated, and no guarantee is made about account sanctions.
  There is also no official confirmation of whether `FSCTL_MOVE_FILE` triggers a game's integrity check (since the file content does not change, a hash check is presumed not to trigger). After relocation, run the launcher's file check/repair once to confirm there is no effect.
- **Frostpunk is the only game whose read pattern was measured by replay.** It was measured on 2026-11-02, and the total time of running the game was not measured. For Call of Duty and League of Legends there is only a tally of the service log, which is neither a controlled validation nor a load-time measurement ([Real-world usage records](RESULTS_SUMMARY.md#real-world-usage-records)).

</details>

## Reference: League of Legends (Vanguard)

League of Legends uses Riot Vanguard, also a kernel-level anti-cheat, so the same cautions as for Ricochet apply. A web survey on 2026-11-02 found no public report of a conflict with Procmon, ETW, or disk defragmentation, which is not evidence of safety. The survey is shallow.

<details>
<summary>Details: web survey, install size, patches, hot set</summary>

League of Legends uses Riot Vanguard, also a kernel-level anti-cheat, so the same cautions as for Ricochet apply. Below are the results of a web survey on 2026-11-02, and the scope of the survey is shallow. The source grade of each item is marked [confirmed] (official or primary), [secondary] (blogs, summaries), or [speculation] (an estimate without evidence).

**Vanguard and collection or relocation tools**

- No public report was found that Procmon, ETW, or disk defragmentation conflict with Vanguard. That is not evidence of safety either, and the risk of an account sanction cannot be regarded as zero.
- Riot's "Incompatible Software" document says only "incompatible software and drivers" and does not mention tool categories or the names of the tools above. [confirmed] https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software
- The blocked items are mainly vulnerable drivers that can be abused for cheating. When blocked, the error shows a file path. [secondary] https://dotesports.com/valorant/news/riot-is-shipping-a-hotfix-to-increase-vanguards-compatibility-with-cheat-vulnerable-drivers
- Procmon loads the PROCMON24.SYS filter driver, so it carries the highest risk of the three tools. Do the trace and relocation with Vanguard off, and close all collection and relocation tools during play. [speculation]
- ETW sessions and the disk relocation API do not load a separate driver, so the risk is presumed to be relatively lower. There is no public evidence. [speculation]

**Install size**

- Riot's official document states that a normal install is about 35 to 40 GB. [confirmed] https://support.riotgames.com/en-us/league-of-legends/client/league-of-legends-takes-too-much-space
- A secondary blog says 16 to 25 GB, so the sources conflict. Measure it on your own PC; until then take 20 to 40 GB as the reference. [secondary] https://nokiamob.net/2026/02/19/how-big-is-league-of-legends-in-2026/
- The Game folder has about 400 large files. The whole release is split into more than 300,000 chunks. [confirmed] https://www.riotgames.com/en/news/supercharging-data-delivery-new-league-patcher

**Patches and keeping the relocation**

- The new patcher does not replace files wholesale but updates them in place chunk by chunk. Riot stated that it changed packaging so the file layout stays stable across versions. [confirmed] same source
- So relocated files may remain after a patch. There is no measured report. [speculation]
- If the patcher deletes a file and downloads it again, the relocation is broken. Check the layout state again after patches. [speculation]

**Loading characteristics**

- No figures showing the difference in League of Legends load time between HDD and SSD were found. Only the general claim that an HDD is 2 to 3 times slower than an SSD in typical games was confirmed. [secondary] https://www.gamepressure.com/newsroom/hdd-sshd-ssd-m2-speed-benchmark-of-hard-drives/z1692
- League of Legends has a large share of network and CPU waiting, so the disk gain may be small. [speculation]
- There is no public data on the number of bytes read during loading, so it must be measured with your own trace.

**Recommended order**

1. First validate the trace, relocation, and prefetch pipeline on another game without Vanguard.
2. For League of Legends, measure only the read volume with Vanguard off and offline, to judge the room for an HDD gain. Collect traces only in this state.
3. For recording the relocation, also consider a method that leaves the file read order directly instead of ETW. Do not use collection tools during play, and compare only the read speed after relocation.
4. Check that the relocation persists after a patch. Move to the next stage only within the range confirmed to have no account risk.

**Estimated hot set and the League client prewarm**

- The estimated hot set of League of Legends is about 9 GiB across all modes: about 5.3 GiB from the default rules for Global, UI, Common, and maps 11 and 12, plus about 3.6 GiB for maps 30, 453, and 22, as summed on the test PC (Companions excluded). With a zone limit of 8 GiB, the later part (map 22 for TFT, and Companions) may be cut.
- With `riot.champ_select_prewarm` on (off by default), the files of the chosen champions and the map are read into RAM during champion select, through the local League client API (`127.0.0.1`). Files inside a running game are not touched.
- This feature is off by default, and we advise against turning it on with a Korean account. Riot's Developer Relations team announced on 2019-01-24 that, "for the time being", players in Korea may not use applications that use the League Client API ([announcement](https://www.riotgames.com/en/DevRel/changes-to-the-lcu-api-policy)). We did not find a statement that withdrew it, so it may still apply to Korean accounts.
- Riot also says this client API is not officially supported for third-party applications ([developer documentation](https://developer.riotgames.com/docs/lol)), and its General Policies say a product that uses the League Client API must be registered with and audited by Riot through the Developer Portal ([policies](https://developer.riotgames.com/policies/general)). Whether a free open-source tool such as this one has to register has not been confirmed. Outside Korea, no Riot sentence was found that either forbids or explicitly allows this particular use.

</details>
