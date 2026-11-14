**English** | [한국어](FAQ.ko.md)

# FAQ and troubleshooting

> Short answers. Each one links to the page that explains it in full. Start with [Getting started](../README.md#getting-started) in the README.

## FAQ

**Does it work for games installed on an SSD?**
No. It only targets games on an HDD; games on an SSD are skipped because an SSD does not need it. A game on an HDD that is also the Windows system drive is only discovered and prewarmed (status "prewarm only (system drive)"); it is not relocated.

**Is it bad for my hard disk's lifespan?**
Relocation writes to the game drive, but with limits: at most 1 GiB per round on an SMR drive (2 GiB otherwise), 2 GiB a day on SMR (8 GiB otherwise), and only while the PC is idle. After that it mostly reads. The effect on drive lifespan, temperature, and noise has not been measured. See [Effects on your hard disk](../README.md#effects-on-your-hard-disk-and-cautions) and the [game guide](GAME_GUIDE.md).

**What does it do while I am playing?**
When a game starts, it reads that game's recorded data in order for up to 120 seconds (it stops sooner at the loading boundary), and background work on the same disk (relocation, prewarm, other reads) waits or stops. It also samples the disk's read counters, not the game's files or process, to keep a play record. For a game on which tracing is allowed, the recording session stays open for the whole play, but reads are saved only inside the recording windows. Programs that have nothing to do with the game also give up memory for the session ([Background memory yield](AUTO_MODE.md#background-memory-yield)). The steps are in [Automatic mode](AUTO_MODE.md); the settings are in [Configuration](CONFIGURATION.md).

**What happens if I uninstall it or remove a game?**
Uninstalling first reverts SSD assist links, then removes the program and the service. The data folder `%ProgramData%\AsterCache` stays. Pieces already moved are not moved back, and the file contents are unchanged either way; to restore the previous layout, click **Revert** on the Games tab first. Removing a game from the list leaves its files as they are. See [Usage](USAGE.md#common-tasks).

**How much RAM does it use?**
Prewarmed data is ordinary file cache. Across all games it is limited to half of physical RAM by default (`prewarm.max_cache_mib`), and prewarm runs only when at least 4 GB of RAM would still be free afterwards. Windows reclaims the cache when your programs need the memory. If free memory does not hold the whole list, prewarm reads the front part that fits, keeping 256 MiB more free than the 4 GB reserve, and reads the rest later when memory is free again; the reserve and the cache budget are not lowered. See [Configuration](CONFIGURATION.md). The tray and the service also release memory they used to keep (the tray no longer reads whole logs, and the service drops data left behind by a removed game); the before-and-after figures have not been measured.

**A program I had open got slow for a moment after a game started. Why?**
This is background memory yield. When a game session starts, AsterCache lowers the memory priority of programs that have nothing to do with the game, so that Windows takes their memory before the prewarm cache. It also empties their working sets, but only when memory is short at the start (free memory plus the low-priority standby memory is below `max(4 GiB, physical RAM / 4)`) and, later in the session, only when the cache is really being reused. A program that was emptied has to bring its pages back when you use it again, so it can be slow for a moment; in a test, a program touching 2 GiB again took 0.8 seconds. A program that wins back most of its memory after being emptied is not emptied again in that session. Background programs can also be slightly slower during the session because their memory is reused first. This is intended, and the priority is restored when the game ends. The game, launcher, and anti-cheat programs are never touched. What this changes in real play has not been measured. See [Background memory yield](AUTO_MODE.md#background-memory-yield).

**How do I turn off background memory yield?**
Add `"memory": {"background_yield": false}` to `config.json`. There is no switch for it in the Settings tab. The service re-reads the file within 5 seconds; if you turn it off during a game session, the priorities are restored at the next 30-second check. If you roll back to an older version, remove the `memory` key first, because versions 1.0.2 and earlier reject it ([Configuration](CONFIGURATION.md)).

**What does "the cache goes cold" mean?**
Prewarmed data sits in the file cache, which is ordinary RAM that Windows takes back when other programs need memory or read other files. When part of a game's data has been taken back, the cache is cold there: the next read of it goes to the HDD again. The re-prewarm check reads 1 MiB at 8 places and calls the cache cold when at least 2 of them take more than 2 ms. A place counts as slow only if the system's page-read count also grew in proportion to the sample size during the read, so a cached sample that was read slowly while another program was using the CPU is not taken for a cold one (not yet checked on an installed build; the figures below were measured before this rule). On the test PC, the median time from a complete prewarm to the first cold check was 29.0 minutes for League of Legends and 71.5 minutes for Call of Duty (the checks run about every 10 minutes), and since 2026-11-11 47% and 71% of the checks found the cache cold or partly cold. [SOLAR](SOLAR.md) touches the cache again in memory to keep it longer; whether that helps in real play has not been measured.

**How do I turn SOLAR off?**
Add `"memory": {"standby_ladder": false}` to `config.json`. There is no switch for it in the Settings tab. The service re-reads the file within 5 seconds, and it then stops touching the cache; the prewarm itself and the re-prewarm check are not affected. If you roll back to version 1.0.3 or earlier, remove the key first, because those versions reject `memory.standby_ladder` ([Configuration](CONFIGURATION.md)).

**Is it safe with anti-cheat games?**
AsterCache never touches the game process: it reads files from its own process, watches file reads with Windows event tracing, and moves files only after the game has exited. The one thing it does to other programs is, during a game session, to lower the memory priority of programs that have nothing to do with the game and, only when memory is short or the cache is being reused, to empty their working set; it never opens a game, launcher, or anti-cheat process ([Anti-cheat compatibility](ANTICHEAT.md)). But no controlled validation has been done, only usage records from one PC, and no guarantee is made about account sanctions. For an anti-cheat game, turn automatic mode off or add the game to `auto.ignore_exes` before you play. See [Anti-cheat compatibility](ANTICHEAT.md).

**What happens after a game update?**
A patch can change files, so a saved plan may no longer match. AsterCache checks the plan before moving anything and skips changed files, and every 5 minutes (while the game is off) it re-applies the plan if the layout has fallen apart. In automatic mode it also learns the changed files again; when half or more of the planned data changed, it sets the whole trace and plan aside as `.stale`. This has not been tried with a real game update yet. See [Automatic mode](AUTO_MODE.md).

**I closed one game and started another right away, but the second one loaded slowly. Was it prewarmed?**
It should be now, but this has not been checked in real play yet. A game counts as closed only after its processes have been gone for 60 seconds, and a prewarm started by the launcher used to be refused during that wait and put off. Now such a prewarm may read while the only games left on that disk are ones waiting for that check; it never reads while a game is actually running. If the launcher's prewarm was refused because the first game was still running, it is tried once more when that game is first seen gone, as long as the launcher is still open. Look for `prewarm triggered by` and `retrying the refused trigger prewarm` in `service.log` ([Automatic mode](AUTO_MODE.md#4-prewarm)).

**Does it matter whether my drive is SMR or CMR?**
Only one SMR drive (WD20EZAZ) has been measured; CMR drives have not. Removing head movement should help both, but that is not verified. SMR drives can write slowly during the one-time move.

**Is the first run required?**
Relocation needs a trace (the record of what the game reads). Before a first run the service tries a matching shared profile (the repository is still being prepared, so this step is skipped for now) and then the game's Windows prefetch record; each gives a boot trace, so relocation can start early. Prewarm needs no trace: a game whose whole folder fits the RAM budget is prewarmed in full, and a larger one gets the files of its engine. Their effect has not been measured. A trace from another PC can be brought in with `aster profile import` ([Usage](USAGE.md#the-aster-command)).

**Will the very first launch be faster?**
No. The first launch only records what the game reads; the full effect comes from the second launch, after the move and prewarm. First-run read-ahead is an experimental feature, off by default because the E7 to E7f measurements showed no gain (`auto.learn.enabled`; see [RESULTS_SUMMARY](RESULTS_SUMMARY.md)).

**Can I turn automatic updates off?**
Yes. In the Settings tab, open the **Updates** card and choose **Off**, or **Notify only** to be told about a new version without installing it. The same settings are `update.mode` (`auto`, `notify`, or `off`) in `config.json`. Prereleases are not offered unless `update.prerelease` is `true`.

**What does automatic update download, and when does it install?**
The tray checks the official GitHub releases 5 minutes after it starts and then every 24 hours. It downloads only a newer version: the `AsterCache-v<version>-win-x64-setup.exe` of that release, from GitHub only. It checks the installer's SHA-256 against the release's digest or `SHA256SUMS.txt`, and installs nothing if neither is available. It installs silently only when the tray is an installed copy running with administrator rights, no registered game is running, and no relocation is in progress; the installer runs with `/SILENT`, and the tray comes back afterwards. Otherwise it announces the version, or opens the release page for a zip copy or a copy without administrator rights. If the installation of one version does not change the installed version twice, it only announces it. A failed check or download is tried again after 15 minutes, then 1 hour, then 6 hours. The service does not use the network. This silent installation has not yet been confirmed on a real machine.

**What is in the diagnostics ZIP?**
Logs tab → **Export diagnostics** writes one ZIP file where you choose. It holds `info.txt` (tray, service, and OS versions, .NET runtime), `config.json`, `tray.json`, `auto_status.json`, `prewarm_status.json`, `play_samples.json`, the last 2,000 lines of `logs/service.log` and `logs/tray.log`, and `manifest.txt`, which lists what was included and what was left out and why. The total is limited to 20 MiB. It does not read game folders. Your user name in paths and in the text, the user part of URLs, token-like strings, and the values of JSON keys whose names contain token, secret, password, or similar are replaced with placeholders. This masking is rule-based and not complete, so check the file before you share it. Nothing is sent over the network.

**Can I exclude only one game?**
Yes. On the **Games** tab, select the game, click **Game options**, and tick **Paused** or set the policy to **Off**; neither starts new work for that game. To keep it from being registered again, click **Remove**, or add its exe to `auto.ignore_exes`. Removing a game leaves its files where they are.

**Does it optimize programs that stay running, such as Wallpaper Engine?**
No. Desktop utilities that are not games and stay running once started, such as Wallpaper Engine, YoloMouse, Lossless Scaling, OBS Studio, and SteamVR, are left out of automatic registration, and one already registered is unregistered once after the service starts. For any other program, remove it on the Games tab; it is then not registered again.

**Can my files get damaged?**
Every move is verified with a SHA-256 check and written to a journal that can be reverted. Still, AsterCache moves real data: back up important drives first. See [Safety measures](DEVELOPMENT.md#safety-measures).

## Troubleshooting

**It says you do not have permission to save settings.**
The data folder is writable only by administrators. Restart the management window as administrator.

**I added or removed a game but it was not applied.**
The service re-reads `config.json` every 5 seconds, so this normally applies without a restart. If the new configuration fails validation, the previous settings stay in force and the reason is written to `service.log`.

**The tray shows "Service reinstall required".**
A tray update does not replace the service executable. Reinstall the service with the installer.

**A relocation command ends with `not_admin`.**
Run relocation, revert, and volume commands from an administrator PowerShell.

**Prewarm did not run.**
The reason it was skipped is recorded in `service.log`. Typical causes are insufficient available RAM, the game already running, an active cooldown (it does not apply when a sample read shows that the cache has gone cold, for example after a reboot), or a missing plan file. A game counts as running only by its registered exe names. You can also check the `prewarm` entry of `aster status`.

**A prewarm ends as `partial`, or the log says it was stopped by memory.**
The free memory did not hold the whole prewarm list, so only the front part was read. If memory dropped below the reserve while it was reading the front part in list order, the prewarm no longer throws away what it had read: it reads on only up to what fits at that moment and keeps that as a partial prewarm; the rest is read once free memory exceeds the amount already read plus 256 MiB plus the reserve (`prewarm.min_free_ram_mib`, 4,096 MiB by default), and not sooner than 2 minutes after a stop. The reserve and the cache budget are not lowered for this. A prewarm that read its whole list is not recorded as `partial`; earlier versions could do that for a game whose plan holds only the part still to be moved (for example `partial 1164/1306 MiB`). The lines to look for in `service.log`:
- `<game>: prewarm read the first N of M MiB, the rest does not fit in memory (available A MiB, reserve R MiB) [<reason>]`: the read ended at the limit and what it read is kept as a partial prewarm. No error.
- `<game>: supplement waits for memory (read X MiB, room for Y MiB)`: the rest is not read yet because less than `max(256 MiB, 5% of what was read)` more would fit. Nothing is read from the disk while it waits.
- `<game>: prewarm stopped by memory; kept the N of M MiB read as partial`
- `<game>: prewarm skipped (stopped by memory less than 2 min ago) [<reason>]`
- `<game>: prewarm done (...): ..., room X MiB, available Y MiB`, and `rewarm check: cold (..., available N MiB)`. These show the memory at that moment.
- `<game>: prewarm list gained N MiB of blocks outside the full zone`. Blocks that no longer fit in a full zone were added to the prewarm list without moving anything.

**What do the `background yield` lines in the log mean?**
They come from [Background memory yield](AUTO_MODE.md#background-memory-yield) and contain counts and sizes only, never program names or paths. (Program names, without paths, are written only to `background_yield.json` while a session runs.)
- `background yield (trigger): trimmed N processes (X MiB working set), memory priority lowered for M, available B -> A MiB, headroom H MiB`: written when a launcher or other trigger program starts a prewarm and memory was short. N programs had their working set emptied (X MiB in total; only programs of 32 MiB or more are emptied), M programs had their memory priority lowered, and available memory was B MiB before and A MiB after waiting up to 3 seconds. `headroom` is the free memory plus the standby memory of priority 0 to 4 before emptying. `trimmed 0` means no program qualified or none could be opened.
- `background yield (trigger): memory priority lowered for M, no trim needed (headroom H MiB, available A MiB)`: the spare memory (headroom) was at least `max(4 GiB, physical RAM / 4)`, so only the priorities were lowered and no working set was emptied.
- `background yield (game started): trimmed N processes (X MiB working set), memory priority lowered for M, available B MiB, headroom H MiB`: the same when a registered game's exe starts, without waiting, so only the figure before. It does not appear when a session started less than 60 seconds earlier. A `no trim needed` line can appear here too.
- `background yield: trimmed again N processes (X MiB working set), cache repurposed R MiB in S s, K active processes skipped`: during the game, the cache was really reused (R MiB in the last S seconds, at least 512 MiB) and at least 2 minutes had passed since the last emptying, so the chosen programs were emptied once more. K programs that had won back most of their memory were left alone.
- `background yield: memory lists unavailable, trimming once per session`: Windows' memory statistics could not be read, so the working sets are emptied once at the start and not again.
- `background yield ended: memory priority restored for N processes`: the session ended and N programs got their original priority back.
- `background yield: restored memory priority for N processes left by the previous run`: at service start, after a previous run stopped abnormally.
- `background yield state file not written: <exception type>`: `background_yield.json` could not be written, so the priorities were not lowered that time (the working sets are still emptied). `background yield failed` with an error is any other failure; the rest of the service is not affected.

**What do the `ladder` and `cache refreshed` lines in the log mean?**
They come from [SOLAR](SOLAR.md) and are written to `service.log`; the full list is in [Checking that it runs](SOLAR.md#checking-that-it-runs).
- `<game>: ladder ordered N MiB in S s`: right after a prewarm, N MiB of the game's cache was touched in the reverse of the read order, with no disk read.
- `<game>: cache refreshed in memory: N MiB in S s (R MiB repurposed of A MiB ahead)`: the cache was touched again because Windows had reused R MiB of cache, close enough to the A MiB that lie ahead of the game's pages.
- `<game>: ladder found data missing from memory (K slow pieces, N MiB read from disk), left to the rewarm check`: part of the list was no longer in the cache. The touch stopped, and the next re-prewarm check prewarms the game again even if its samples look warm (`rewarm check: part of the list is not in memory (found by the ladder), prewarming again (cooldown ignored)`).
- `<game>: cache refresh skipped, the samples are cold (...), left to the rewarm check`: the sample check before a refresh found the cache cold; the re-prewarm check takes over.

**What do the `cooldown ignored`, `yields`, and `preempted` lines in the log mean?**
- `<game>: cooldown ignored, the cache is cold [service start]` (or `[trigger <exe>]`): the game was inside its cooldown, but a sample read showed that its cache had gone cold, for example after a reboot, so it was prewarmed anyway.
- `service start prewarm waits for 60 s after boot and 20 s of idle disk`, and `held prewarm triggers released: N (boot + idle disk)`: the prewarm at service start and a launcher's prewarm held at boot wait only for those conditions ([Right after boot](AUTO_MODE.md#right-after-boot)).
- `<game>: prewarm yields to other disk activity (low I/O priority)`, and `, yielded N.N s` at the end of the `prewarm done` line: other programs were reading the same disk, so the prewarm read at low I/O priority for N.N seconds in all.
- `<game>: prewarm cancelled (...) (preempted by trigger prewarm)` and `<game>: trigger prewarm waited N s for <game>`: a launcher started a prewarm, so a background prewarm of another game on the same disk stopped to let it read first; the second line appears when the launcher's prewarm still had to wait 1 second or more.
- `<game>: device calibration stopped at the 5 s limit, sample discarded`: the short disk-speed read before a prewarm took more than 5 seconds, so its sample was dropped.

**What do the `trace window ended` lines in the log mean?**
`<game>: trace window ended, N reads, P target processes, L events lost so far` is written each time a recording window closes. `N` is the number of reads saved in that window and `P` the number of game processes alive when the window closed; a late window with `N` of 0 saved nothing. `L` counts events the trace session lost since it began. `trace session stopped: ... M reads dropped (name unresolved)` is written when the session stops and counts reads whose file could not be identified. See [Automatic mode](AUTO_MODE.md#2-recording-reads-trace).
