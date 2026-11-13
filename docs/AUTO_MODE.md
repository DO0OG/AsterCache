**English** | [한국어](AUTO_MODE.ko.md)

# Automatic mode

> Details that continue from [Getting started](../README.md#getting-started) in the README: the stages of what the service does by itself, the supported launchers, and the setting keys.

Automatic mode is on by default. In the usual case you do not need to register games or set anything. Each part can be turned off in the **Automatic mode** group of the Settings tab, or in `config.json`. Setting names are listed here; their meanings and defaults are in [CONFIGURATION.md](CONFIGURATION.md). Folded blocks (click to open) hold the conditions, exceptions, and numbers.

Each game can also be limited. Its **policy** (`games[].mode`: `inherit`, `prewarm_only`, `relocate_only`, or `off`) decides which stages may act on it, and **pause** (`games[].paused`) stops new work for it. A paused game, or one set to `off`, is not traced, prewarmed, or relocated. `prewarm_only` allows prewarming and planning but no relocation or periodic re-apply. `relocate_only` allows relocation but no prewarm, start-up stream, or pinning. Neither setting overrides the anti-cheat gate or the ban on pinning. A game that is no longer allowed to prewarm drops its waiting prewarm entries, and it resumes at the next check when allowed again.

## The stages at a glance

| Stage | One line |
|---|---|
| 1. Discovery | Finds HDD games in launcher libraries and registers them. |
| 2. Recording reads | Records what a game reads while it loads (the trace). |
| 3. Relocation | Moves the pieces the game reads into one zone while the PC is idle. |
| 4. Prewarm | Reads the zone into RAM before the game starts. |
| 5. Upkeep | Re-prewarms, tops up, retries, and rechecks the layout. |
| 6. While a game runs | Yields the disk to the game and records how the disk behaved. |
| 7. Drive recovery wait | Waits for an SMR drive to settle after bulk writes. |

"Idle" is defined in [What "idle" means](#what-idle-means).

## Sources before the first run

A game with no trace takes its first layout data from the first source below that applies: a shared profile, a Windows prefetch record, an engine profile, or the whole folder. The first real trace replaces it.

<details>
<summary>Details: the four sources</summary>

When the first real run records a trace, the plan made from it replaces the earlier one, trying the same zone position first (the old plan is kept as `.stale`).

- **Shared profile** (`auto.shared_profiles`, on by default). Once a week the service downloads one zip over HTTPS and looks each game up by Steam app ID and depot manifest IDs, Epic app name and version, or exe size and sample SHA-256.
- A profile is used only when the file size and sample hash on this PC match. Offline or 404 responses are skipped silently. The download is capped at 64 MiB. The shared repository is still being prepared.
- **Windows prefetch record** (`C:\Windows\Prefetch\*.pf`). Used when the SysMain service runs and the application bit of `EnablePrefetcher` is on. The service only reads the game's `.pf` files.
- It builds a boot trace from the files in the install folder, skipping files flagged "do not prefetch". The ranges the record refers to, plus 64 MiB before and after each (merged where they overlap, no file size limit), are moved into the zone in physical order.
- If the zone covers less than 30 % of what a play reads, the status detail shows `partial (N%)`. The management window shows this as "relocated (prefetch)".
- **Engine profile** (Unity, Unreal, or Source), prewarmed without a trace.
- **Whole folder**, prewarmed without a trace when the folder fits the RAM budget.

</details>

## Stages

### 1. Discovery

- **When.** Ten seconds after the service starts, then every hour.
- **What.** Reads the [supported launchers](#supported-launchers) and registers each HDD game with `"source": "auto"`. A process that reads a lot after it starts is caught by the [heuristic](#heuristic-detection).
- **Does not.** Register SSD games, resident utilities, or a game in the same folder as one you registered by hand. A game on the Windows system drive is only prewarmed.
- **Settings.** `auto.discover`, `auto.ignore_exes`.
- **Log.** `auto <launcher>: registered <name> (<exe>)`

<details>
<summary>Details: limits, skipped games, resident utilities</summary>

**Limits and skips**

- The list holds up to 32 games. When it is full, launcher discovery stops, and a game that is actually run and detected replaces the least recently used automatic entry.
- A game is skipped if its volume label is empty or longer than 32 characters (one log line), or its exe name or an overlapping folder is already registered.
- Games on an SSD are not registered, traced, or relocated. On an HDD that is the Windows system drive, a game is only discovered and prewarmed (status "prewarm only (system drive)").
- A game you remove in the management window has its exe added to `auto.ignore_exes`, so it is not registered again (up to 64 exes; the oldest are dropped).
- Other log lines: `auto <launcher>: skipped <name>: volume has no usable label`, `auto discovery: game list is full, skipping the rest`.

**Resident utilities**

- Desktop utilities that are not games and stay running once started (Wallpaper Engine, YoloMouse, Lossless Scaling, OBS Studio, SteamVR, and the like) are never registered.
- A fixed list of Steam app IDs and exe names excludes them from launcher discovery and the heuristic. This list is separate from `auto.ignore_exes`.
- One already registered automatically is unregistered once after the service starts, even if it is running, unless files are being relocated at that moment (then at the next discovery). Entries you registered yourself are not touched.
- Files already moved stay where they are. With the SSD assist storage (`auto.ssd_tier`), the links of a game that is no longer registered are reverted. A game that is no longer registered is not prewarmed or pinned.
- Log line: `auto: unregistered <game> (resident utility, not a game)`.

</details>

### 2. Recording reads (trace)

- **When.** The first time a registered game without a trace starts (the trace is recorded from the next run, since the detected start has already begun loading). It repeats for a few plays.
- **What.** Records the game's reads with a live ETW session and saves `plans\<name>.trace.json`, the same file as the manual **Capture trace** button.
- **Does not.** Trace a game whose anti-cheat forbids it ([ANTICHEAT.md](ANTICHEAT.md)), or open the game process. Reads are only observed through ETW.
- **Settings.** `auto.trace`, `trace_cap_seconds`, `trace_cap_mib`, `trace_runs`, `trace_merge_min_mib`, `trace_late_*`.
- **Log.** `<game>: auto trace started (run 2/3)`, `<game>: auto trace ended (<why>), N read events saved`

<details>
<summary>Details: caps, several plays, zone extension, first-run learning</summary>

**Recording**

- The session is named `AsterCacheAutoTrace`. Recording stops at the first of: the game exiting, 1800 seconds (`trace_cap_seconds`), or the larger of 6144 MiB and twice the zone limit (`trace_cap_mib`).
- If a cap is reached while the game is still running, cleanup, saving, and merging wait until it exits.
- The trace records only reads that went to disk, so reads already served from the cache may be missed, and some of the first reads before the session starts may be missed too.

**Late windows**

- If the game is still running when the first window ends, every `auto.trace_late_interval_min` minutes it records for `auto.trace_late_window_seconds` seconds, up to `auto.trace_late_windows` times.
- The session stays open between windows, from the first window until the game exits, the last late window ends, or a cap is reached. Reads that happen while no window is open are dropped at once (not kept in memory), and the file is written to disk only after the game has exited. A new session per window could not tell which file a read belonged to if the game had opened that file before the window began, so those reads were dropped and a late window saved nothing; keeping one session lets the later windows save them. This was reproduced with a test program; it has not been confirmed with a real game yet.
- The read cap counts only the reads inside open windows.
- Keeping the session open means Windows delivers file events to the service for the whole play, outside the windows too. Its CPU cost has not been measured. Each window writes `<game>: trace window ended, N reads, P target processes, L events lost so far` to the service log; if the late windows save nothing, look at `N`. When the session stops, `trace session stopped: L events lost, M reads dropped (name unresolved), K file names, R records` gives the total; `M` should be small next to the total reads.
- Games whose anti-cheat forbids tracing never get a session, so none of this applies to them ([ANTICHEAT.md](ANTICHEAT.md)).

**Several plays**

- The next plays are recorded the same way until `trace_runs` are collected, each saved as `plans\<name>.trace.runN.json`.
- `trace.json` becomes the union of their blocks (the first run's access order, then blocks first seen in later runs), replaced atomically. The status column shows `trace 2/3` meanwhile.
- When the run-file number limit is reached, the oldest run is replaced and the merged block list is kept.
- Once the merge gain is too small and collection stops, the game is traced once more if any of these holds since its last valid run: 5 plays, 7 days, or a detected game update.

**Zone extension**

- If the merged hot set is at least `trace_merge_min_mib` larger than the plan's, and the game is not running, the zone is extended in place.
- Blocks already in the zone stay and only new blocks are appended after them, within `max_zone_mib` (the earliest run's blocks win), through the same idle, daily-limit, and write-budget checks as any relocation.
- If the region after the zone is not free, the extension is skipped, the previous plan stays, and it is retried only when a newer play changes the trace. Log line: `<game>: zone extension blocked: ...`.
- When the zone has already reached its size limit, blocks that the merged trace holds but that no longer fit in it are not moved. Without moving anything (and so without writing to the game drive), they are added to the game's prewarm list instead, as long as the whole list stays inside the cache budget (`prewarm.max_cache_mib`, half of physical RAM by default). When the list is cut to fit memory, they rank after the zone's observed ranges (see [Order and budget](#4-prewarm)), and the start stream does not read them. Log line: `<game>: prewarm list gained N MiB of blocks outside the full zone`.

**First-run learning (experimental, off by default)**

- While the trace is recorded, the service can also read ahead. It is off (`auto.learn.enabled` is `false`; no gain in E7 to E7f measurements, see [RESULTS_SUMMARY.md](RESULTS_SUMMARY.md#e7-first-run-read-ahead)).
- When on, it reads ahead only in files the game reads sequentially: 6 forward reads in a row, each at most 4 MiB past the end of the previous read, and at least 8 MiB of that file read.
- Such a file is read ahead in 16 MiB steps from the current position, up to `auto.learn.window_mib` further, without waiting for gaps in the game's reads (`auto.learn.mode` `"stream"`, the default). With `"gap"`, it reads only after 100 ms of quiet, in 1 MiB requests.
- Reads come from the service's own process, with one reader thread, low I/O priority, and the most recently read file first. Files the game has opened exclusively are skipped.
- The total is at most the game's reads × 1.5 + 64 MiB, and stays within `auto.learn.max_total_mib` and the free-RAM reserve (`min_free_ram_mib`). Only games on an HDD that is not the system drive are learned.
- The other modes `"bounded"` and `"adaptive"` are experimental variants; see [CONFIGURATION.md](CONFIGURATION.md).

</details>

### 3. Relocation

- **When.** After a trace exists, the game is not running, and the PC is idle, one game at a time.
- **What.** Plans a zone, moves the pieces into it (journal and SHA-256 sample check), confirms the layout, then prewarms the zone.
- **Does not.** Run while a game runs (a move in progress stops after the current fragment), move sparse, compressed, or encrypted files, or change file content. A failed check reverts from the journal.
- **Hours.** If `auto.relocate_hours` is set, new relocations and periodic re-applies start only in those local hours. A relocation that already started and left a journal finishes outside them.
- **Settings.** `auto.relocate`, `idle_seconds`, `relocate_hours`, `max_relocations_per_day`, `max_zone_mib`, `max_relocate_gib_per_day`. How much it writes: [Effect on the hard disk](GAME_GUIDE.md#effect-on-the-hard-disk).
- **Log.** `<game>: auto relocation done`, `[ERROR] <game>: auto relocation stopped: ...`

<details>
<summary>Details: limits, interrupted and split moves, game updates</summary>

**Limits**

- Zone size: hot data × 1.1, capped by `max_zone_mib`.
- At most `max_relocations_per_day` relocations run in 24 hours, counted from the start of each. Those under 64 MiB are not counted but still count toward the byte budget.
- Each game gets one automatic first relocation. Later upkeep uses the periodic check in [Upkeep](#5-upkeep).
- `auto.relocate` also applies to games you registered by hand. To prevent it, turn it off or add the exe to `auto.ignore_exes`.

**Interrupted and split moves**

- If a game starts during a move, the move stops after the current fragment and resumes later from the journal.
- If another file takes the zone still to be moved, or the plan changes, the parts already moved are kept and only the remaining part is planned again into a new free area.
- A plan whose zone is already at its maximum size is not planned again, and the screen says the work resumes automatically.
- If the data check fails or an error occurs while the game is off, the journal is used to revert and the failure is recorded. Only that automatic change is reverted.

**Game updates**

- If a game's files change size or modification time after relocation, the trace and plan are set aside as `.stale` (checked every 10 minutes), and the stage goes back to discovery.
- The game is traced and relocated again on its next run, trying its previous zone position first.

**Other**

- Moves carry a low I/O priority hint (`FileIoPriorityHintInfo` Low). The documentation does not guarantee that the kernel applies it to the copy I/O of a move, and this has not been measured yet.
- Right after a move finishes (or fails, if a trace exists), the zone is prewarmed.
- Other log line: `<game>: partially applied plan replanned, the parts already moved are kept`.

</details>

### 4. Prewarm

- **When.** At service start, when a supported launcher starts, and after a game exits.
- **What.** Reads the zones of the most recently played games into RAM, within a RAM budget, in 64 MiB batches.
- **Does not.** Hold the disk against a game: it stops at the next batch boundary when any game starts, including the game being prewarmed.
- **Settings.** `auto.full_prewarm`, `auto.full_prewarm_max_mib`, `auto.engine_profiles`, `prewarm_recent`, `prewarm.max_cache_mib`, `prewarm.min_free_ram_mib`, `auto.startup_warm_max_mib`, `auto.rewarm_after_exit_min`.
- **Log.** `<game>: prewarm done (<reason>): N MiB requested, ... in S s = R hint MB/s, ...`

<details>
<summary>Details: when, order, budget, reading, games without a trace</summary>

**When exactly**

- A game counts as exited only when all of its registered exe files have been gone for 60 seconds, so a launcher-to-game handoff is not mistaken for an exit. Unregistered processes in the install folder do not delay it.
- After an exit, relocation, SSD tier apply, and prewarm or re-prewarm for a new trace on the same physical disk wait for the longer of 60 seconds and `auto.rewarm_after_exit_min` minutes.
- **Switching games.** While the 60-second exit check of a game that just closed is still running, a prewarm started by a launcher (a trigger prewarm) may read, if the games still registered on that disk are only ones waiting for that check. If one of them comes back, or another game starts, the prewarm is cancelled at once. Nothing changes while a game is actually running: no new reads on its disk. Re-prewarm checks, top-ups, and relocation still wait as above.
- A trigger prewarm refused because a game was running is written down. The first time the service sees that game's processes gone (processes are checked every 30 seconds), it tries once more if the launcher is still running and the refusal is less than 30 minutes old (log line: `<game>: retrying the refused trigger prewarm (<exe>) now that the blocking game is gone`). If it is refused again, the usual waiting rules apply. How this behaves in real play has not been checked yet.

**Order and budget**

- The most recently played games first, with those played often at this time of day ahead. Games triggered recently (for example by a launcher) are prewarmed first for 6 hours.
- With `prewarm_recent` at 0, as many recent games as fit in the budget; a value from 1 to 32 caps the number of games instead.
- Budget: the smaller of half of physical RAM (or `prewarm.max_cache_mib`) and free RAM minus `min_free_ram_mib`.
- Free memory is measured again right before reading. If the list no longer fits, it reads the front part that fits and the rest is supplemented later. A 256 MiB margin is kept on top of the reserve (`prewarm.min_free_ram_mib`, 4,096 MiB by default) both in the last check before reading (whether the whole list still fits) and when it picks the front part, so a small change in free memory does not cancel the read. The reserve and the cache budget are unchanged.
- The cache budget is divided between games by the size of each game's actual prewarm list. A partly relocated plan, which holds only the part still to be moved, used to be counted at its whole hot set, which gave that game a larger share than it read.
- When the list has to be cut, the ranges kept first are, in this order: (1) ranges in files the game also writes to (these are not moved with the zone and are kept in a separate prewarm-only list) that were first read early in the play, up to the loading boundary or 120 seconds when no boundary is known; (2) the ranges observed in play; (3) the rest of the prewarm-only ranges, by the time they were first read; (4) guessed ranges. Ranges added from outside a full zone (see [Zone extension](#2-recording-reads-trace)) take part only at (3). The early ranges of (1) are also read first. They used to sit at the end of the list, so a cut dropped them before anything else.

**Memory runs short while reading**

- If a prewarm that was reading only the front part loses memory (available memory falls below the reserve, or the remaining budget no longer fits), it stops. When it had read something in list order, with no errors and no gap filling, that amount is kept as a partial prewarm (`partial`, `warmed_mib`, and `hot_mib` in `prewarm_status.json`) instead of being counted as nothing. Otherwise nothing is kept: a prewarm that was reading the whole list stops the same way but keeps nothing, and neither does a stop before anything was read.
- After any stop caused by memory, including one that kept nothing, top-ups and retries (the waiting-list retry and the re-prewarm check) leave that game alone for 2 minutes; a launcher trigger or a service start does not wait. If the stop had read something, a waiting retry is pulled forward to 2 minutes later instead of the usual period (`rewarm_minutes`, 10 minutes by default); a stop that read nothing keeps the usual period. A game with a kept partial prewarm stays on the waiting list instead of being dropped, and the rest is read once free memory exceeds the amount already read plus 256 MiB plus the reserve.
- Log lines: `prewarm cancelled (...) after N MiB (memory reserve: ...)` (or `(memory budget: ...)`), followed by `<game>: prewarm stopped by memory; kept the N of M MiB read as partial`, and `<game>: prewarm skipped (stopped by memory less than 2 min ago) [<reason>]`. The `prewarm done` line ends with `room X MiB` (or `unlimited`) and `available Y MiB`, and a `rewarm check: cold (...)` line includes `available N MiB`, so the memory at that moment can be read from the log.

**Reading**

- In the disk's physical order. When a batch holds more than one game, the loading head (tier0) of each game is read first, in recent order, and then each game's whole zone.
- Each range is marked as observed in play or guessed from an engine profile. When the budget is short, the observed ranges of all games are filled first and the leftover goes to the guessed ranges.
- At service start only the head part is read, up to `auto.startup_warm_max_mib`. The rest is read later, once the disk is idle. A game that will be skipped anyway (for example during its cooldown, while it is running, or in the post-update delay) does not take a share of that limit, so the next game gets it.
- The management window shows `prewarming x/y MiB`, and a tray notification is sent when a game's prewarm finishes (once per game per day).
- If another game starts, it stops at the next batch boundary. If the game being prewarmed starts, it also stops and yields (`auto.keep_stream_on_game_start`, default `false`; `true` reads to the end, for experiments).

**Games without a trace**

- A small game (folder within the RAM budget) gets its whole folder prewarmed, up to `auto.full_prewarm_max_mib`. A larger game gets its engine profile files.
- A game that is no longer registered is not prewarmed or pinned.
- Other log lines: `prewarm cancelled <reason>`, `<game>: prewarm skipped (...)`, and a `prewarm phases` line with the elapsed time per phase. A partial prewarm has `partial A/B MiB, the rest does not fit in memory:` in front.

</details>

### 5. Upkeep

- **When.** Re-prewarm check every `rewarm_minutes`; top-ups and retries as the disk stays idle; layout check periodically.
- **What.** Samples 8 places in each zone and reads it again if it went cold; reads the part of a hot set that did not fit; retries a prewarm that was cut short; restores a drifted layout.
- **Does not.** Re-warm a game outside the prewarm budget, or touch the layout while the game runs.
- **Settings.** `rewarm_minutes`, `min_free_ram_mib`, `post_update_delay_min`.
- **Log.** `<game>: rewarm check skipped (...)`, `<game>: layout degraded while source files are unchanged (...)`, `<game>: reapplied, moved N bytes, N failed`

<details>
<summary>Details: re-prewarm, top-up, retry, layout recheck and reapply</summary>

**Re-prewarm (keeping the prewarm)**

- Reads 1 MiB from each of 8 places in the zone (the first 4 in read order and 4 evenly spaced). If at least 2 of them take more than 2 ms, the data has probably been evicted from the file cache, so it prewarms again while keeping `min_free_ram_mib` free.
- Only games inside the prewarm budget are re-warmed, in prewarm-score order (recent play and time-of-day habits). A game outside the budget is not re-warmed after it cools, and the reason is logged.
- One running game does not stop the check for other games; each target is judged separately.

**Top-up (supplement)**

- When a hot set does not fit the budget, a window that fits is read as the drive stays idle: the smaller of the remaining hot set, free RAM minus the 256 MiB margin and the reserve, and the prewarm budget, at least 256 MiB.
- If a top-up stops midway, the position it reached is remembered while the service runs, and the next one continues from there. One that mixed in gap filling, or whose front has gone cold, starts again from the beginning.
- A failed read or a settings change discards the position, and a service restart loses it.
- Log lines: `<game>: rewarm check: the rest of the hot set is not read yet, supplementing once the drive has stayed idle`, `<game>: supplement prewarm skipped (...)`.

**Retrying an unfinished prewarm**

- A prewarm of a traced game that was cancelled before it finished, or skipped by the service-start cap, stays on a waiting list and is requested again once the disk has been idle for 60 seconds, no game is running on that disk, and the cache budget has room.
- Games without a trace are not retried this way.
- A cancelled prewarm is read again even while the drive recovers from a relocation. Further relocations still wait for recovery.

**Layout recheck and reapply**

- A periodic check (1440 minutes by default) finds that the layout has drifted while the source files are unchanged, and reapplies the plan.
- If a temporary condition blocks the check (a running game, a busy disk, drive recovery, battery power, not idle long enough, the post-update delay), it is repeated every 5 minutes instead. A reapply that stops at the per-round write cap keeps the 5-minute recheck.
- When reapply is rejected because another file occupies part of the zone (`zone_not_free`), the game is handed to automatic relocation, which keeps the moved parts and replans only the remaining part, under its existing holds and daily limits.
- It is handed over only when automatic relocation is on, the game has a trace, and it is not stopped by a failed data check. Otherwise it fails as before. A rejection before any move is recorded as 0 bytes.
- These checks (extent and fingerprint lookups) are rechecked about every second and stop when a game starts.
- Other log line: `<game>: reapply blocked by zone_not_free, handing over to auto relocation to replan the remaining part`.

</details>

### 6. While a game runs

- **When.** From a game's start to its exit (plus a short grace period).
- **What.** Blocks relocation, reapply, and tier writes on that disk, yields prewarm, and records how the disk behaved (`play record:`).
- **Does not.** Read the game's files or process. The play record uses only disk device counters, and it does not make the game faster by itself.
- **Settings.** `auto.start_stream_enabled` (when `false`, no start stream runs), `auto.start_stream_cap_seconds`, `auto.restream_cooldown_min`, `auto.keep_awake`.
- **Log.** `<game>: start stream skipped (<why>)`, `play record: ...`

<details>
<summary>Details: same-disk rules, start stream, play record</summary>

**Same disk**

- The running game's own prewarm, and any relocation, re-layout, reapply, or tier write, stay blocked.
- A prewarm of another game on that disk runs only if the running game reads under 1 MiB/s and the disk stays under its busy threshold for 60 seconds in a row.
- While reading, it checks that game's read growth between batches, and about every 10 seconds it pauses for 1 second to measure disk throughput again and yields if the disk is busy. A failed read-count query counts as busy.
- The log line for a prewarm skipped because a game is running names the process (name and pid) judged to be the running game. The elapsed run time is counted from when the service observed the start.

**Start stream**

- When a game starts, the service can read its hot set (the start stream). It stops at the loading boundary (the 10-second moving read rate falls below 10% of its peak, at most 60 seconds in) or at `auto.start_stream_cap_seconds`.
- It is not repeated within `auto.restream_cooldown_min`. Log line: `<game>: start stream stopped at load boundary after N bytes, S s (R MB/s); boundary at B ms, peak P MiB per 10 s, last window W MiB, observed T s`. The numbers after the semicolon show why it stopped there: the boundary time, the largest and the latest 10-second read amounts, and how long the start was watched.

**Small checks**

- The keep-awake read, pin creation, and boot checks check for a running game before they start.
- The keep-awake read (`auto.keep_awake`) is 4 KiB every 10 minutes while a launcher is open or at your usual play hour, so the HDD does not spin down. Power settings are never changed.

**Play record**

- The service samples the physical disk's cumulative read counters once per second and logs a `play record:` line and saves `play_samples.json` at 30, 60, 120, 300, and 600 seconds after the start and at exit.
- It reads only the disk device counters, never the game's files or process. It is for comparing loading and stutter.
- The end time, the length, and the totals (bytes read, read time, disk idle time) of a play stop at the moment the service first saw the game's processes gone, so the 60-second exit check is not counted. If the game comes back during that check, the play continues.

</details>

### 7. Drive recovery wait

- **When.** After a relocation on an SMR drive, and after a game update is detected.
- **What.** Waits until short test reads show the drive back at its normal speed, then allows the next job.
- **Does not.** Block prewarm. It holds back relocation, and after a game update that game's re-planning and prewarm.
- **Settings.** `post_relocate_recovery`, `recovery_max_min`, `recovery_hard_max_min`, `post_update_delay_min`.
- **Log.** `drive D: recovering after relocation (...)`, `drive D: recovered after N min`

<details>
<summary>Details: probe rules, limits, other modes, pauses</summary>

**Probe (default)**

- The baseline is measured right before the write at the three positions the samples rotate through, and their median is used.
- Every 2 minutes it reads a short sample. The drive counts as recovered after 2 consecutive samples at 90% or more of the baseline. When the measurements fluctuate, it also counts as recovered once at least 4 of the last 6 samples (two rotations) are at 90% or more.
- When samples keep falling short, the interval grows to 4 and then 8 minutes after the first two rotations. Each sample logs its position, speed and baseline, verdict, and reason.
- Samples above the cache-hit ceiling at all three rotating positions in a row also count as recovered. Samples that cannot be judged are spaced out up to 8 minutes apart, and the discarded ones are summarized in one log line.
- It waits at most `recovery_max_min` minutes. If recovery is still unconfirmed, further relocations stay on hold until the drive is recovered or `recovery_hard_max_min` minutes have passed.
- `"fixed"` waits `post_update_delay_min` minutes; `"off"` turns it off.

**After a game update**

- Re-planning, relocation, and prewarm for that game wait `post_update_delay_min` minutes, because the drive may still be reorganizing scattered writes (E9).

**Pauses**

- The wait stops while a game on the same disk is running, and continues from the remaining time after it exits. The recovery sample reads are skipped meanwhile.
- A service restart keeps the paused state and the remaining time.
- Other log lines: `drive D: recovery clock paused (game running)`, `drive D: recovery discarded N cache-suspect samples ...`.

</details>

### Right after boot

If the service starts within 5 minutes of boot, it holds the disk work it would start on its own until 5 minutes have passed and the game drive has been idle for 60 seconds. The quiet period ends 15 minutes after boot at the latest.

<details>
<summary>Details: what is held</summary>

- Held work: trigger prewarm such as a launcher starting, relocation, new-trace prewarm, layout checks and reapply, and the keep-awake read. The held work then runs under the normal rules.
- Boot-time prewarm covers only recently played games. A trace that was already prewarmed before the restart is not treated as new again.
- The period also ends 15 minutes after boot even if the disk is still busy or unreadable.
- Log lines: `<what> deferred: boot quiet period`, `boot quiet period ended: ...`.

</details>

### Pinning (optional)

With `prewarm.pin`, prewarmed pages stay open as read-only mappings in the service so they are less likely to be evicted. Effect unverified (needs an experiment).

<details>
<summary>Details: when pinning is on and released</summary>

- When the key is not written, it turns on automatically with 24 GiB of RAM or more; `false` turns it off; an explicit `true` works with 16 GiB of RAM or more.
- The mappings are released at once when free RAM falls below the reserve or when a game or launcher starts, so launcher updates can change the files. A prewarm started by a trigger program is not pinned.

</details>

## What "idle" means

"Idle" means all of these:

- No registered game installed on the same drive as the games waiting to move is running (games on other drives are ignored).
- The game's drive stays under 4 MiB/s for 30 seconds in a row.
- The PC is on AC power, or on a battery at 50 % or more.

CPU use is not part of the test: relocation uses low I/O priority and only affects the target drive. If the PC has not been idle for 6 hours, `service.log` records one line with the reason (`game running`, `on battery`, or `disk busy`). To see which stage each game is in, open the management window and look at the **Status** tab and the stage column of the **Games** tab.

## Supported launchers

Steam, Epic Games Store, GOG Galaxy, Ubisoft Connect, Riot Client, the Windows uninstall list, and the Xbox app folders are read. Battle.net and EA app libraries are not read; their games are found by the uninstall list or the heuristic.

<details>
<summary>Details: how each launcher is read</summary>

**Steam** (prewarm trigger `steam.exe`)

- `libraryfolders.vdf` and `appmanifest` files. The executable is the largest `.exe` in the game folder, excluding installers, redistributables, and crash tools.

**Epic Games Store** (trigger `EpicGamesLauncher.exe`)

- `LaunchExecutable` in `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`.

**GOG Galaxy** (trigger `GalaxyClient.exe`)

- Registry `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games`.

**Ubisoft Connect** (triggers `upc.exe`, `UbisoftConnect.exe`)

- Registry `Launcher\Installs`, value `InstallDir`.

**Riot Client** (trigger `RiotClientServices.exe`)

- `product_install_full_path` in `%ProgramData%\Riot Games\Metadata\<product>.live\*.product_settings.yaml`. Marked as Vanguard anti-cheat. Entries that are not `.live` (for example PBE) are skipped.
- League of Legends (`LeagueClient.exe`, `League of Legends.exe`) and Teamfight Tactics use the same exe names, and their entries are merged when they share a folder.
- VALORANT uses `VALORANT.exe` and `VALORANT-Win64-Shipping.exe`. Other products use the largest `.exe` in the folder.

**Windows uninstall list** (no trigger)

- `Uninstall` keys of HKLM and HKCU (32-bit too). Kept when the install folder is on an HDD (not the system drive), is at least 1 GiB, is not from a common publisher (Microsoft, Intel, NVIDIA, and others), and is not a launcher's own folder.
- The exe is the largest `.exe` in the folder. Catches standalone games such as Battle.net, EA, Nexon, and HoYoverse titles.
- An entry skipped because its name matched a launcher exclusion word has its exe chosen again at service start under the current rules (one log line).
- Limits: a launcher-style folder can give the wrong exe; after the game runs, the heuristic adds the real exe.

**Xbox (Microsoft Store)** (no trigger)

- `XboxGames\<game>\Content` folder on each HDD. The exe is the largest `.exe`. Folders that cannot be read are skipped.

**Checked and not checked**

- The GOG and Ubisoft readers have not been checked on a real PC yet. The Riot reader was checked on the test PC with League of Legends on D: (an HDD). The Xbox reader has not been checked, because the test PC has no such folder.
- `Battle.net.exe` and `EADesktop.exe` are listed only as prewarm trigger programs; the service does not read their libraries.

</details>

## Heuristic detection

A process that is not registered, starts from an exe on an HDD volume, and reads 256 MiB or more within 20 seconds is treated as a game and registered, if its folder looks like a game. The first trace is recorded from the next run.

<details>
<summary>Details: exclusions and the folder test</summary>

- **Excluded.** The Windows folder, AsterCache itself, launchers and tool programs, and the exes in `auto.ignore_exes`.
- Also excluded: exes whose path contains `\bin\Debug\`, `\bin\Release\`, `\obj\`, `\.git\`, `\node_modules\`, or `\AppData\Local\Temp\`.
- Also excluded: the built-in tool exes `testhost*`, `dotnet`, `msbuild`, `vstest*`, `python*`, `node`, `java*`, `powershell`, `pwsh`, `cmd`, `conhost`, `explorer`, `7z*`, and `winrar`. This fixed list is separate from `auto.ignore_exes`.
- **The folder must look like a game.** It holds a file of 256 MiB or more, or a launcher mark (`steam_appid.txt`, `.egstore`, `goggame-*.info`, or a Steam manifest). Games installed under `AppData\Local\Temp` are therefore not found.

</details>

## Engine profiles

The engine (Unity, Unreal, Source, Riot, MapleStory, CASC, SCS, EA FC Online) is detected from file names in the game folder, and the files to read during loading are chosen from it. A TFT install is detected as Unreal.

<details>
<summary>Details: markers and read rules per engine</summary>

If several profiles match, their rules are merged and the rule that matched first wins; the tail beyond the budget is cut.

| Profile | Detection markers | Main read rules |
|---|---|---|
| Unity | `UnityPlayer.dll`, `<name>_Data/globalgamemanagers` | globalgamemanagers, resources.assets, sharedassets0, level0~9 whole; Managed dll; StreamingAssets first 256 MiB; il2cpp and MonoBleedingEdge dll |
| Unreal | `<Game>/Content/Paks/*.pak`, `*.utoc` | pakchunk0 whole; Binaries and Engine/Binaries dll; other pak first 256 MiB and last 64 MiB; utoc whole |
| Source | `**/*_dir.vpk` | `*_dir.vpk` whole; pak01 chunks first 256 MiB; bin dll |
| Riot | `Game/DATA/FINAL/*.wad.client` | Global, UI, Bootstrap, DATA, ShaderCache, Scripts; maps 11, 12, 30, 453 whole; root exe and dll; champions first 64 MiB; maps 22 (TFT) and Companions whole; other maps first 64 MiB |
| MapleStory | Root `*.wz`, `Data/*.wz` | `Data.wz` whole; other wz and img first 1 GiB; root exe and dll |
| CASC (Overwatch, StarCraft) | `data/casc/data/*.idx` | Indexes whole; each `data.###` first 256 MiB; root exe and dll |
| SCS (ETS2) | Root `base.scs` | base, def, core, effect, locale `.scs` each first 512 MiB |
| EA FC Online | Root `fczf.exe`, `data/*.big` | `data_default.big` whole; root exe and dll; other `*.big` each first 256 MiB |

</details>

## Settings

All `auto.*` keys, their meanings, and their defaults are in [CONFIGURATION.md](CONFIGURATION.md).

- `"auto": {"enabled": false}` turns off the whole automatic mode. `discover`, `trace`, and `relocate` turn off one stage each.
- The state is written to `%ProgramData%\AsterCache\auto_status.json` (only the service writes it). `aster status` has an `"auto"` key, and `aster discover [--json]` lists the launcher games read-only.
- The manual buttons (register, capture trace, create plan, run relocation, prewarm now) work as before.

<details>
<summary>Details: what the screens show</summary>

- The Status tab shows one line per game, such as "Auto: Frostpunk discovered → collecting trace → waiting to relocate → relocating → [done]". The Games tab shows the source, stage, and anti-cheat columns.
- The learning stage shows "First run: learning (read-ahead n MiB)", and "Prewarmed" appears after prewarm.
- The per-game status line also shows `hit right after prewarm N%` (the hit rate measured right after a prewarm ends) and `SSD assist N GB` (the size of the game's SSD copies).
- Tray notifications are sent for discovery, for a finished relocation, and when a first run ends ("Faster from the next launch: <game>"). The pop-up can be turned off in the Settings tab's Notifications card (`notifications.enabled` in `tray.json`); the notice is still written to the tray log.

</details>

## Effect

How much automatic mode shortens total game load time has not been measured. The service log of the test PC (2026-11-02 to 2026-11-10) gives usage records, which are not a controlled benchmark and have no comparison with prewarm off.

- League of Legends (Vanguard): 10 game start detections, 2 traces collected and then skipped 8 times, relocation completed 4 times, hit rate right after prewarm 100% (latest value only).
- Call of Duty (Ricochet): 62 game start detections, 14 trace collections started, relocation completed 5 times, hit rate right after prewarm 75% (latest value only).

The method and sample counts are in [Real-world usage records](RESULTS_SUMMARY.md#real-world-usage-records), and the policy for anti-cheat games is in [Anti-cheat compatibility](ANTICHEAT.md). No guarantee is made about account sanctions.

## SSD assist storage (optional)

Off by default (`auto.ssd_tier.enabled`). While it is off, AsterCache does not touch any file, except that it reverts links left by an earlier run. When on, it copies the hottest files of an HDD game to an SSD within a budget and puts a symbolic link in their place.

<details>
<summary>Details: choosing, applying, keeping, reverting</summary>

**Choosing and applying**

- Each file is scored by its hot bytes (the parts the trace read) divided by its size. Files are taken from the highest score down while they fit the budget (`budget_gib`, default 8 GiB per game, 1 to 4096). Game `.exe` and `.dll` files and files outside `allowed_roots` are never chosen.
- The file is copied to the SSD, and the size and a sampled SHA-256 are checked. The copy is recorded in the journal, the original is renamed to `<name>.aster-orig` in the same folder, a symbolic link takes its place, and the link is checked by size. If one file fails, only that file is reverted.
- `root` is `"auto"` (the system drive if it is an SSD, otherwise the SSD with the most free space, under `AsterCache\tier`) or an absolute path on an SSD.

**When it runs**

- One game at a time, only for an HDD game that is not running, is not on the system drive, has no anti-cheat folder, and has finished relocation (or has relocation turned off).
- It waits for idle like relocation does, and retries a game it could not finish after 6 hours.

**Keeping**

- At each service tick the journal is checked. A missing or wrong-size copy is reverted at once. A file that became a normal file, or whose copy changed through the link, is marked stale and left alone.
- If the SSD has less than 1 GiB free, no new copies are added and the oldest game is reverted first. A change to the budget or the folder makes the game revert and apply again with the new setting.

**Reverting and commands**

- `aster tier revert --game <name>` or `--all` removes the links and restores the originals. The copies stay in the tier folder until `aster tier clean` deletes the unused ones. Uninstalling reverts all games first.
- The journal is `%ProgramData%\AsterCache\tier\<game>.json`. If a journal cannot be read, that game is skipped, nothing is touched, and a log line is written.
- Commands: `aster tier status`, `aster tier apply --game <name>`, `aster tier revert --game <name>` or `--all`, and `aster tier clean` (apply, revert, and clean need administrator rights).
- The checkbox and the budget are in the Settings tab, under Automatic mode. The per-game status line shows `SSD assist N GB`.

</details>
