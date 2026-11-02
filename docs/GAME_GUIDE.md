**English** | [한국어](GAME_GUIDE.ko.md)

# Applying AsterCache to a real game

This document walks through, in order, the whole process of reducing the load time of a game installed on the D: HDD with AsterCache. No build or pip install is needed. All commands below are run from the top-level folder of the repository (`D:\Git\AsterCache`).

Scope of validation: trace conversion, planning, prefetch execution, and the launch script were run end to end with three Procmon-format CSV files (2,000 rows each) built from real file paths in D:\AsterBench. The Python administrator steps (relocation `probe`, `apply`, `revert`) were verified only up to getting `not_admin` as a non-administrator, and were not run on a game. On 2026-11-02, tracing, relocation, replay, and prewarm with the C# tools were run end to end on a real game, Frostpunk (D:\Frostpunk, no anti-cheat) (see the section "Procedure validated with Frostpunk" below).

## What to know first (target game: Call of Duty: Modern Warfare 4)

- **Installing on an HDD is outside official support.** Activision's official PC requirements for MW4 (released 2026-10-23) list "115 GB or more of free SSD space" for all three tiers: minimum, recommended, and ray tracing. Whether installation on an HDD is blocked was not confirmed. All modes reportedly have on-demand texture streaming, so besides load times, stutter during play is also expected on an HDD. This tool only reduces the reads of fixed loading stretches, and may not reduce the streaming reads that keep happening during play.
- **TPM 2.0, Secure Boot, and the Ricochet kernel anti-cheat driver are mandatory.** Ricochet monitors and reports applications that interact with the game. There is no public list of which tools are allowed. In the MW4 beta there was a case of a permanent ban from a false positive, and Activision acknowledged the false positive. The risk of losing an account is real.
- **Using Procmon with Ricochet games is not recommended.** No public report of using Procmon with Ricochet was found, and that is not evidence that it is safe. With other anti-cheats (BattlEye, EA), there are cases where the Procmon driver caused the game to refuse to launch, and where the driver stayed after Procmon was closed and a reboot was needed. The alternative is Windows built-in ETW (`wpr`, `logman`). However, "ETW is safer" is a guess, and no report on whether Ricochet conflicts with ETW was found either.
- **The Python tools read only Procmon CSV.** ETW `.etl` conversion is done with the C# CLI `aster trace --etl <file> --exe <game exe> --origin-root <game folder> --out <file>`. It was tested with an `.etl` in S3 (`cs_etw_check.ps1`), and a name resolution defect was fixed in the process. The Python `trace` and `plan` commands cannot read `.etl`. **For a game without a kernel anti-cheat you can follow the process end to end, either on the Procmon path or on the same C# path as Frostpunk.**

### Recommended order

1. **First validate the whole process with a game that has no kernel anti-cheat.** One game, Frostpunk, was completed on the C# path on 2026-11-02 (see the section below). Another example is Call of Duty 4: Modern Warfare on Steam (2007, about 8 GB, no kernel anti-cheat). Use only single player (the campaign) and do not connect to online modes. This is a way to measure the tool's effect on a real game for the first time without account risk.
2. To apply it to MW4 after confirming the effect and safety: test only in the offline campaign, relocate with the game and the Battle.net (or Steam) launcher fully closed, and leave multiplayer and DMZ out of the first test.
3. Collect the MW4 trace through the ETW path. The C# `aster trace --etl` conversion was validated in the administrator script S3 (`csharp\scripts\cs_etw_check.ps1`). However, whether collecting on MW4 is safe is unvalidated, so test first with one short mission. Until then, using Procmon on MW4 is not recommended.

## Anti-cheat compatibility

Many current games run kernel-level anti-cheat. This section says what AsterCache does and does not do, so you can decide whether to use it with a game. Anti-cheat policies differ per game and can change. AsterCache has not been tested with any anti-cheat game yet.

### What it does

- The service reads game files from inside its own process, read-only, with shared access (read, write, and delete sharing), or only maps them.
- It watches file-read events with a live ETW kernel session. It needs no handle to the game process.
- It moves file clusters with the NTFS defragmentation API (`FSCTL_MOVE_FILE`), and only after the game has fully exited. File content, size, and modified time do not change. Before a move, the plan's fingerprint (size and modified time) is checked.

### What it does not do

- No code injection, no thread creation in the game process, no reading or writing of its memory, no debug privilege, no hooking, no overlay, and no handle duplication.
- Game processes are opened only with `PROCESS_QUERY_LIMITED_INFORMATION`, to read the name, path, and bytes read. If a protected process refuses even that, the service decides from ETW events alone and does not try to open that process again. In that case it judges from the reads in the 20 seconds after the refusal is confirmed; reads before those 20 seconds are not counted.
- No kernel driver.

### Skipped and marked files

- Files that the game has opened exclusively are skipped.
- During automatic discovery, the service looks at file and folder names up to 4 levels deep in the install folder. It recognizes EasyAntiCheat, BattlEye, Vanguard (`vgc`, `vgk`), and Ricochet; other names containing anticheat, xigncode, nprotect, or gameguard are marked `unknown`. A game with such a trace is marked in the Games tab and in the `anticheat` setting, and one log line is written. The behavior does not change.

### Why each speed feature is considered safe

These are the reasons each feature is considered safe. They are reasoning, not verified results.

- **Prewarm:** reads ordinary files from the service's own process.
- **Prewarm upkeep:** samples ordinary 1 MiB file reads.
- **Pinning:** read-only mappings in the service's own process, released when a game or launcher starts.
- **Relocation:** runs only after the game has exited, and file content does not change.
- **Detection:** a query-only process handle, or ETW events.

### Signing

The executables are not code-signed, so Windows SmartScreen may show a warning. That warning is the only effect confirmed so far. Whether signing status relates to anti-cheat decisions has not been checked.

### Limits and advice

- Automatic mode is on by default. For a game with anti-cheat, turn automatic mode off in the Settings tab, or add the game's exe to `auto.ignore_exes`, before you play.
- Automatic tracing uses the same kind of ETW collection as the manual route, so the Ricochet and Vanguard cautions in this guide ("What to know first", Step 1, and "Reference: League of Legends") still apply.
- Not yet tested on any real anti-cheat game. This is outside the current validation.

## Procedure validated with Frostpunk (2026-11-02)

The target is D:\Frostpunk (9.2 GB, no anti-cheat, D: SMR HDD). The two scripts below were run from an administrator PowerShell relative to the top-level folder of the repository. The C# CLI (`csharp\publish\cli\aster.exe`) must be published first (`csharp\README.md`).

1. **Trace, relocate, and cold replay.** Run it with the game not started.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_game.ps1
   ```

   The script starts the ETW live trace (`aster trace --live`) and waits until the game starts and ends (`-WaitGameSeconds`, default 600 seconds). Load up to the first map and exit the game, and it proceeds in this order: plan → record the SHA-256 of the plan file → cold replay 3 times after clearing the standby list → probe → `apply --dry-run` → `apply --execute` → `verify` → re-check SHA-256 after clearing the standby list → cold replay 3 times and prefetch + replay 3 times after relocation. The result is left in `results\csharp_e4_<date-time>\summary.json`. The revert command is in `README.txt` in the same folder, and the script does not run it.

2. **Relocate again with a zone that holds the whole hot set, and compare with the SSD.**

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File csharp\scripts\cs_e4_compare.ps1 -ZoneMiB 2048
   ```

   Using the trace file from step 1, it plans again into a 2 GiB zone and applies it (data that was already moved stays). It builds a sparse copy of the same read pattern at `C:\AsterShadow\Frostpunk` and measures the SSD baseline 3 times. Then it clears the standby list, loads the zone with `aster prefetch --method pvm`, waits 60 seconds, and measures the replay 3 times. The result is `results\csharp_e4cmp_<date-time>\summary.json`.

Recommended settings

- **Size the zone so it holds all the hot bytes.** `cs_e4_game.ps1` sets the zone to 1.1 times the hot bytes rounded up to 16 MiB units, and skips relocation if it exceeds `-MaxZoneMiB` (`hot_set_too_large`). If the zone is too small, pass `-MaxZoneMiB` at or above the hot bytes.
- **Finish prewarm before the game starts.** Use `prewarm.on_service_start` (default true) in the game entry of the service configuration, or put the launcher in `trigger_exes`. The measurement assumes prewarm finishes at least 15 seconds before the game starts. Prewarm runs only when available RAM is at least the zone size + `min_free_ram_mib` (default 4096 MiB). The configuration format is in the "Two operating modes" section of `csharp\README.md`.
- Relocate only while the game is off (the script stops if it is running). It is recommended to close the launcher too. After relocation, check the automatic optimization exclusion in step 5.

Results and limits are in the 2026-11-02 section of `VALIDATION_REPORT.md`. The measured values are replay times of traced read patterns, not total load times of a running game. 24-hour persistence, CMR drives, and anti-cheat games are unvalidated.

## Requirements

- Windows 11, Python 3.11 or later (tested on 3.11.4 and 3.12.10). Check with `python --version`.
- Install the game on D: (an NTFS HDD). Keep the trace and plan files outside the game folder (for example `D:\AsterWork`).
- Use an administrator PowerShell only in step 3 (relocation). Run steps 2, 4, and 5 as an ordinary user. Procmon asks for administrator rights by itself.

```powershell
mkdir D:\AsterWork
cd D:\Git\AsterCache
python --version
```

Do not end paths with a backslash. The tool handles it either way, but in CMD `"D:\Games\"` fails to close the quote and gives an error. Case is not distinguished.

## Applying directly to MW4

The recommendation above (validate first with a game without anti-cheat) is advice; the actual order when applying directly to MW4 is below. The detailed commands for each number are in the step descriptions that follow. This whole procedure has never been run with MW4, and whether Ricochet conflicts with ETW/Procmon is unvalidated.

1. **Check the install path.** Install MW4 on the D: drive from Battle.net or Steam (or the Xbox PC app), and use the install folder that the launcher shows (for example `D:\...\Call of Duty`) as is for `--prefix`, `--origin-root`, and `--allowed-root`. The folder selection menu name differs by launcher version, so check it on the install screen. The official install size is 115 GB (SSD basis), but depending on the pack composition secondary sources also give around 200 GB. Use the actual size on the install screen as the reference, and also leave contiguous free space on D: for relocation (at least the hot set size). Start after the install is finished and post-install work such as shader compilation is done.
2. **Define the reference stretch.** In the offline campaign, define the same point (for example, from the main menu starting one campaign mission until a controllable screen appears) as one load. Repeat with the same mission and the same menu path every time. Leave multiplayer and DMZ out of the first test. First measure the current load time of this stretch with a stopwatch (the `-NoPrefetch` baseline in "How to measure the effect" of step 4).
3. **Collect the trace (step 1).** There are two ways.
   - **ETW (recommended; `.etl` conversion validated in the C# CLI with S3).** From an administrator PowerShell before starting the game, run `wpr -start FileIO -filemode` (check the profile name with `wpr -profiles`), start the game and load up to the reference stretch, then run `wpr -stop D:\AsterWork\mw4_run1.etl`. The current Python tools cannot read `.etl`, so convert the `.etl` with the C# CLI `aster trace --etl`. How the collector itself looks to Ricochet is unvalidated, so make the first collection one short mission, and stop immediately if there is a launch refusal, a warning, or an account notice.
   - **Procmon (only when the user accepts the risk).** Turn on Procmon first (set the `Process Name`, `Operation`, `Result`, and `Path` filters and start capture), then start the game. Running a Ricochet game with Procmon on may be the highest-risk combination, and there is no public safety report. After loading up to the reference stretch, turn off capture and save as CSV, exit the game, then **fully exit Procmon (File > Exit) and reboot the PC**. The Procmon driver can remain after exit, so both the next collection run and actual play are done after a reboot. So each collection needs a reboot (3~5 times). Do not leave Procmon installed and running during normal play.
4. **Create the plan (step 2).** If you have Procmon CSV files, run `trace`, `plan`, and `relayout plan`. If you collected only ETW, stop here until the C# CLI conversion has been validated on a real `.etl`.
5. **Relocate (step 3).** Fully close the Battle.net (or Steam) launcher and MW4, check in Task Manager that no related process remains, then run from an administrator PowerShell. Relocate while there is no post-install update (if an update rewrites files, the plan is rejected). When finished, run the launcher's file check/repair once to confirm there is no effect.
6. **Run and measure (step 4).** Start with `game_launch.ps1` and compare with `-NoPrefetch` using the reference stretch from number 2. Because the launcher starts the game, you may need to give `-Game` the launcher URI instead of the executable.
7. **Maintain (step 5).** Exclude D: from Windows automatic optimization, and after updates check with `verify` and, if necessary, start again from step 1.

### Expected effect when the hot set in MW4 is in the GB range

The values below are calculated assuming a sequential speed of 160 MB/s, and the real hot set size of MW4 is unknown (not measured). Relocation and prefetch remove seek time but do not reduce the number of bytes read.

| Hot set | Prefetch floor time | Expectation |
|---:|---:|---|
| 2 GB or less | about 12.5 s or less | The seek-removal effect can be large |
| 5 GB | about 31 s | There may be a gain, but the prefetch time may become similar to the loading |
| 10 GB | about 62.5 s | The prefetch floor may be longer than the loading, so the effect is limited |
| 20 GB or more | about 125 s or more | Hard to expect an effect |

No load time of MW4 on an HDD has been reported. On-demand texture streaming causes reads during play too, so stutter outside loading stretches may not be reduced by this tool.

## Step 0. Safety check

- Start from number 1 of the recommended order above. Do not test first on a game with anti-cheat or a game tied to an online account.
- Relocation does not change file content; it moves only the physical position (clusters) on disk (the NTFS defrag API `FSCTL_MOVE_FILE`). When applying, the tool compares a SHA-256 sample of each file before and after the move, and `sha_mismatch` in the result must be an empty array. To be surer, hash a few large files yourself before and after applying and check that they are equal.

```powershell
Get-FileHash 'D:\Games\CoD4\bigfile.ff' | Select-Object Hash   # run before and after applying and compare
```

- Reverting: the `revert` command in step 3 applies the journal in reverse and returns to the previous physical layout. The data content does not change, so the game files are safe even if you do not revert.
- Patching the game or running file verification/repair rewrites files, which can undo the layout or cause the plan to be rejected. See step 5.
- SMR HDDs can slow down writes during bulk writes (relocation). The tool moves in 4 MiB pieces by default, but the Python tool was not tested at real game sizes. The record of relocating Frostpunk with the C# tool is in the section "Procedure validated with Frostpunk" below.
- A forced termination during relocation was not tested with real game data. In the VHDX test (S1), resume after 5 interruptions was confirmed. Do not power off the PC or put it to sleep while moving.
- Files with compression, sparse, or encryption attributes and reparse points are skipped automatically by relocation (the reason appears in `skipped_files` in the `apply` result). Such files simply do not get the relocation gain, and no problem arises. If you have ever NTFS-compressed the game folder, you can first undo it with `compact /u /s:D:\Games\CoD4`.

## Step 1. Collect the trace

This records which parts of which files the game reads while loading. Recording the same load 3~5 times lets you tell what is read every time from what is read by chance.

### Games without anti-cheat: Process Monitor

1. Download Process Monitor (Procmon) from Microsoft Sysinternals and run it as administrator.
2. In Filter > Filter..., add the four conditions below as Include and press Add, then Apply.
   - `Process Name` `is` `game executable.exe`
   - `Operation` `is` `ReadFile`
   - `Result` `is` `SUCCESS`
   - `Path` `begins with` `D:\Games\CoD4` (the game install folder)
3. In Options > Select Columns, check that Time of Day, Process Name, PID, Operation, Path, Result, and Detail are on. **Operation, Path, Result, and Detail are required.** Do not sort the display by another column. The tool uses the row order of the CSV as the read order.
4. Turn capture off (magnifier icon, Ctrl+E), clear with Edit > Clear Display (Ctrl+X), and turn capture on.
5. Start the game, proceed to the point where loading ends (the main menu or the level start screen), and turn capture off. Exit the game afterward.
6. In File > Save..., choose `Events displayed using current filter` and format `CSV`, and save as `D:\AsterWork\run1.csv`.
7. Fully exit the game and repeat 4~6 to make `run2.csv` and `run3.csv` (and `run4.csv`, `run5.csv` if possible). Each time must be the same load (the same menu path, the same level).

This tool does not filter by process name; it looks only at the Path prefix. If you remove the Process Name filter, reads from other processes such as launchers and antivirus are mixed in as well if they are in the same folder.

### Ricochet games (MW4 and others): leave only an `.etl` with ETW (for reference; the C# `aster trace --etl` conversion was validated in S3)

Below is a reference command that records file I/O with the built-in Windows `wpr`. This `FileIO` profile command was not run, and S3 was tested with the repository's `csharp\scripts\aster_fileio.wprp` profile. According to Microsoft documentation, ETW file read events have `Offset` (64-bit), `IoSize` (ByteOffset, IOSize in the Microsoft-Windows-Kernel-File provider), and `FileKey`, so offset and length can be obtained. Turn the collector on before the game starts and off after it exits, and stop immediately if there is any sign of trouble (launch refusal, warning, account notice). Make the first test short, in the offline campaign.

```powershell
# Administrator PowerShell. The profile name must be checked locally with wpr -profiles (not confirmed).
wpr -start FileIO -filemode
# ... start game -> loading finished -> exit game ...
wpr -stop D:\AsterWork\mw4_run1.etl
```

The Python `trace` command above cannot read this `.etl`. You can convert it with the C# CLI `aster trace --etl <file> --origin-root <game folder> --out <file> --dump-first 20` and check the read count and the first 20 entries.

## Step 2. Create the plan

### 2a. The prefetch plan `plan.json` and trace conversion

```powershell
python astercache_v07.py plan --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --budget-mib 512 --out D:\AsterWork\plan.json

python astercache_v07.py trace --csv D:\AsterWork\run1.csv D:\AsterWork\run2.csv D:\AsterWork\run3.csv `
  --prefix D:\Games\CoD4 --out D:\AsterWork\trace.json
```

`--prefix` is the game folder. Only reads under that path are used, and similarly named folders such as `D:\Games\CoD4Other` are filtered out.

For each CSV, one line `{'rows': ..., 'read_success_rows': ..., 'parsed': ..., 'ignored': ...}` appears. If `parsed` is 0 or very small compared with `rows`, the Procmon filter or `--prefix` is wrong. In the test CSV files `parsed` was 1,463~1,481 (the rest were END OF FILE, paths outside the folder, and non-read rows).

The files the two commands make serve different purposes.

| File | Made by | How multiple CSVs are combined | Used for |
|---|---|---|---|
| `plan.json` | `plan` | Only 64 KiB blocks read two or more times, weighted toward recent runs (decay of 0.85 each), up to `--budget-mib` | Prefetch (`prefetch.cli --trace`) |
| `trace.json` | `trace` | The union of 64 KiB blocks read at least once in any run (duplicates removed) | Prefetch, `game_launch.ps1` |
| `trace.run1.json`, `trace.run2.json`, ... | `trace` | Separately for each run, in the order read | `relayout.cli plan --trace` (one per run) |

Relocation compares the per-run read orders to find a consistent order, so it takes them separately per run. Relocation also accepts `plan.json`, but it is sorted by path and has no read order information, so it is not recommended.

Even if Procmon records the same file in different case (`d:\games\a.bin` and `D:\Games\A.bin`), both commands unify it to the case of the real file. Without unification the same file would be split in two and the move would be duplicated. This problem actually occurred in the test CSV files and was fixed.

### 2b. The relocation plan `relayout_plan.json`

```powershell
python -m relayout.cli plan --trace D:\AsterWork\trace.run1.json D:\AsterWork\trace.run2.json D:\AsterWork\trace.run3.json `
  --origin-root D:\Games\CoD4 --zone-mib 512 --out D:\AsterWork\relayout_plan.json
```

PowerShell does not expand wildcards such as `trace.run*.json` for native commands, so list the files one by one (the `trace` command prints the file list on its last line). If you use `--dry-run` instead of `--out`, the file is not saved and you see only the statistics.

What to look at in the output statistics (3 test CSV files, `--zone-mib 512`)

```
"sessions":3,"cluster_bytes":4096,
"stats":{"hot_bytes":205193216,"moved_bytes":205193216,"tasks":3029,"zone_used_clusters":50096,
         "overflow_bytes":0,"skipped_files":0,"current_runs":3049,"expected_runs":1,...}
```

- `hot_bytes`: the size of the hot data to move (about 196 MiB here). `--zone-mib` must be larger than this, and if `overflow_bytes` is greater than 0 the zone is too small, so increase it. A zone that is too large needs more contiguous free space and makes the move longer, so take about 1.2 times the hot bytes as the reference.
- `tasks`: the number of move tasks. `current_runs` is how many fragments the hot data is currently scattered into, and `expected_runs` is how many fragments it will be after relocation (1 means one stretch).
- `skipped_files`: the number of files skipped because their position could not be determined.
- The zone needs the same amount of **contiguous free space** on D:. If `zone_candidate_lcn` in `probe` of step 3 is `null`, there is none, so reduce `--zone-mib` or free space on D:.

Paths outside the game folder are dropped from the plan automatically.

### 2c. Preview the prefetch plan (optional)

```powershell
python -m prefetch.cli --trace D:\AsterWork\relayout_plan.json.prefetch.json --origin-root D:\Games\CoD4 --full-file-ratio 2
```

`relayout plan` writes `relayout_plan.json.prefetch.json` along with the plan. It is the prefetch input containing only the blocks that went into the relocation zone, in zone order, so after relocation pass this file instead of `trace.json` (the bytes read equal `hot_bytes`). `--min-sessions N` removes from the hot set blocks that were read in fewer than N runs (default: 2 if there are two or more traces, 1 if there is one).

Run without `--execute`, it does not read and shows only `bytes` (bytes to be read) and `requests` (the number of requests). This is the amount that step 4 will read.

## Step 3. Relocate (administrator PowerShell)

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

What to look at in each output

- `probe`: `free_match` must be `true` (meaning the number of free clusters in the bitmap equals the value NTFS reports). If `false`, stop. `zone_candidate_lcn` must be a number. If you are not an administrator it ends with `{"error":"not_admin"}`.
- `apply --dry-run`: check that `tasks_todo` is greater than 0 and `skipped_files` has no unexpected files. `bytes_todo` is the bytes to be moved.
- `apply --execute`: `failed` must be an empty array, `stopped` must be `null`, `sha_mismatch` must be an empty array, and `moved_bytes` must equal `bytes_todo`. One run moves up to 2,048 MiB by default and may stop with `"stopped":"write_cap"`. In that case **run the same `--execute` command again** and it continues (you can also raise the limit with `--write-cap-mib`).
- `verify`: it must be `"verdict":"contiguous"` (meaning the hot data has been gathered into one stretch in order).

Time taken: 243 MB (1,048 tasks) of D:\AsterBench took 1~2 minutes. The hot data of a game is in the GB range, so it is expected to take proportionally longer (5 GB being tens of minutes is an extrapolation, not a measurement). As measurements, on Frostpunk the first pass moved 639 MB into a 1.5 GiB zone in 131 s (4.9 MB/s), and the second pass moved 212 MB into a 2 GiB zone in 45 s (one SMR HDD; the two-pass total of 851 MB is a calculated value).

If you do not give `--journal`, the journal (JSON Lines) is created at `D:\AsterWork\relayout_plan.json.journal.jsonl`. It is needed for reverting, so do not delete it.

Reverting

```powershell
python -m relayout.cli revert --journal D:\AsterWork\relayout_plan.json.journal.jsonl --allowed-root D:\Games\CoD4 --execute
```

Without `--execute`, nothing is moved.

## Step 4. Run the game (together with prefetch)

Run from an ordinary PowerShell.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\game_launch.ps1 `
  -Trace D:\AsterWork\relayout_plan.json.prefetch.json -OriginRoot D:\Games\CoD4 -Game D:\Games\CoD4\iw3sp.exe
```

The script first starts the prefetch (`prefetch.cli`, zone order) as a hidden background process, immediately starts the game, then waits until the stream finishes and prints the elapsed time and the bytes read. Test output (`cmd.exe /c exit` instead of a game, with a warm cache)

```
prefetch started (pid 26632), game started 0.07 s later (pid 32532)
stream done: 0.46 s since launch (read time 0.06 s), 195.7 MiB, 2169 requests, errors 0, 3,445 MiB/s
```

This value is for a warm cache, so it is not a speed reference. If `errors` is not 0, the trace is stale (a patch, for example).

- `-Game` can also take a URI such as `battlenet://...` or `steam://rungameid/...` besides an executable. When the launcher starts the game, the prefetch may not finish before the game starts.
- With `-NoPrefetch` it runs only the game without prefetch (for comparison).
- Pass game arguments as an array, like `-GameArgs '+set','x'`.
- The default `-FullFileRatio 2` reads only the recorded blocks. After relocation the rest of a file is not in the hot zone, so reading whole files (the `prefetch.cli` default of 0.3) is wasted time (in the test data the amount read grew from 205 MB to 402 MB).

### How to measure the effect

1. Right after rebooting the PC (with an empty cache), run the game with `-NoPrefetch` and measure the load time (from launch to load complete) with a stopwatch. Reboot and repeat 3 times.
2. Measure 3 times the same way without `-NoPrefetch`.
3. Compare the medians of the two results. If you repeat without rebooting, the Windows file cache always makes it fast and the comparison is meaningless.

## Step 5. Maintenance

- After a game patch or file verification/repair, check with `verify` whether the layout is broken. If `verdict` is `non-contiguous` or `apply` reports `source_changed`, collect the trace again from step 1 and redo steps 2~3. Patched files have a changed size/modified time, so the existing plan is rejected.
- **Exclude D: from Windows automatic drive optimization.** Windows defragments HDDs with a weekly scheduled task, which can undo the relocated layout. Open "Defragment and Optimize Drives" from the Start menu, press "Change settings" for the optimization schedule, open the drive list with "Choose", and uncheck D:. Do not run `defrag D:` yourself either.
- Whether the layout persists over time was confirmed up to 10 hours on the synthetic dataset (the layout held). There is no 24-hour confirmation.

## Limits

- **The first run has no gain.** The run that collects the trace and the relocation time are pure cost. The gain comes only from repeated loads after relocation.
- **Is the first run required?** Relocation needs a trace from a run, so it cannot start without one. Without a trace, automatic mode still prewarms: the whole folder if it fits the RAM budget, otherwise the files of the game's engine profile (`auto.full_prewarm`, `auto.engine_profiles`, both on by default). Their effect has not been measured yet. A trace recorded on another PC can be brought in with `aster profile import` (see `aster profile export`). The README FAQ has the same entry.
- First-run read-ahead (E7) stays off by default until it is re-measured. See [RESULTS_SUMMARY.md](RESULTS_SUMMARY.md#e7-first-run-read-ahead).
- **The larger the hot set, the larger the prefetch floor time.** With sequential reads at about 160 MB/s, the floor is bytes ÷ 160 MB/s (on a 1 GB = 1000 MB basis; measured in GiB it is about 7% more).

| Hot set | Prefetch floor time |
|---:|---:|
| 2 GB | about 12.5 s |
| 5 GB | about 31 s |
| 10 GB | about 62.5 s |
| 20 GB | about 125 s |
| 30 GB | about 188 s |

When the hot set exceeds a few GB, the prefetch time becomes the floor and the effect is limited, and the gain comes mainly from removing seeks. On the synthetic dataset (243 MB) the prefetch took 1.1~1.7 seconds. Frostpunk's read pattern was 2.02 GB, and on the relocated zone it took 15.1 s at the HDD sequential limit (about 135 MB/s). The hot set size of MW4 is not yet known.

- **Reads not in the trace are slow.** Reads such as a different level/mode from the recorded load, or texture streaming during play, are not helped by this tool. Blocks read only once (especially those that appear only in old runs) do not go into the zone and can cause seeks during prefetch, so consistency across 3~5 runs matters.
- **This tool does not inject into or hook the game; it only reads files from a separate process.** Even so, whether an anti-cheat could regard it as an "app accessing game files" is unvalidated. There is also no official confirmation of whether `FSCTL_MOVE_FILE` triggers a game's integrity check (since the file content does not change, a hash check is presumed not to trigger). After relocation, run the launcher's file check/repair once to confirm there is no effect.
- **Frostpunk is the only game validated.** It was measured by replay on 2026-11-02, and the total time of running the game was not measured. The other figures in the README were obtained from the synthetic dataset. MW4 and LoL have not been validated yet.

## Reference: League of Legends (Vanguard)

League of Legends uses Riot Vanguard. Vanguard is also a kernel-level anti-cheat, so the same cautions as for Ricochet above apply. Below are the results of a web survey on 2026-11-02, and the scope of the survey is shallow. The source grade of each item is marked [confirmed] (official or primary), [secondary] (blogs, summaries), or [speculation] (an estimate without evidence).

**Vanguard and collection/relocation tools**
- No public report was found that Procmon, ETW, or disk defragmentation conflict with Vanguard. That is not evidence of safety either. The absence of reports does not mean it is safe, and the risk of an account sanction cannot be regarded as zero.
- Riot's "Incompatible Software" document says only "incompatible software and drivers" and does not mention tool categories or the names of the tools above. [confirmed] https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software
- The blocked items are mainly vulnerable drivers that can be abused for cheating. When blocked, the error shows a file path. [secondary] https://dotesports.com/valorant/news/riot-is-shipping-a-hotfix-to-increase-vanguards-compatibility-with-cheat-vulnerable-drivers
- Procmon loads the PROCMON24.SYS filter driver, so it carries the highest risk of the three tools. Do the trace and relocation with Vanguard off, and close all collection and relocation tools during play. [speculation]
- ETW sessions and the disk relocation API do not load a separate driver, so the risk is presumed to be relatively lower. There is no public evidence. [speculation]

**Install size**
- Riot's official document states that a normal install is about 35~40GB. [confirmed] https://support.riotgames.com/en-us/league-of-legends/client/league-of-legends-takes-too-much-space
- A secondary blog says 16~25GB, so the sources conflict. The exact value must be measured on the user's own PC, and until then take 20~40GB as the reference. [secondary] https://nokiamob.net/2026/02/19/how-big-is-league-of-legends-in-2026/
- The Game folder has about 400 large files. The whole release is split into more than 300,000 chunks. [confirmed] https://www.riotgames.com/en/news/supercharging-data-delivery-new-league-patcher

**Patches and keeping the relocation**
- The new patcher does not replace files wholesale but updates them in place chunk by chunk. Riot stated that it changed packaging so the file layout stays stable across versions. [confirmed] same source
- So relocated files may remain after a patch. There is no measured report. [speculation]
- If the patcher deletes a file and downloads it again, the relocation is broken. Check the layout state again after patches. [speculation]

**Loading characteristics**
- No figures showing the difference in LoL load time between HDD and SSD were found. Only the general claim that an HDD is 2~3 times slower than an SSD in typical games was confirmed. [secondary] https://www.gamepressure.com/newsroom/hdd-sshd-ssd-m2-speed-benchmark-of-hard-drives/z1692
- LoL has a large share of network and CPU waiting, so the disk gain may be small. [speculation]
- There is no public data on the number of bytes read during loading, so it must be measured with your own trace.

**Recommended order**
1. First validate the trace, relocation, and prefetch pipeline on another game without Vanguard.
2. For LoL, measure only the read volume with Vanguard off and offline, to judge the room for an HDD gain. Collect traces only in this state.
3. For recording the relocation, also consider a method that leaves the file read order directly instead of ETW. Do not use collection tools during play, and compare only the read speed after relocation.
4. Check that the relocation persists after a patch. Move to the next stage only within the range confirmed to have no account risk.
