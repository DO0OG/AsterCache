**English** | [한국어](FAQ.ko.md)

# FAQ and troubleshooting

> Continues from [Getting started](../README.md#getting-started) in the README.

## FAQ

**Does it work for games installed on an SSD?**
No. It only targets games on an HDD. Games on an SSD are skipped, because an SSD does not need it. A game on an HDD that is also the Windows system drive is only discovered and prewarmed (status "prewarm only (system drive)"); it is not relocated or tiered.

**Is it safe with anti-cheat games?**
AsterCache never touches the game process; it only reads files from its own process, watches file reads with Windows event tracing, and moves files after the game has exited. But it has not been tested with any anti-cheat game yet. For such a game, turn automatic mode off or add the game to `auto.ignore_exes` first. Details are in [Anti-cheat compatibility](ANTICHEAT.md).

**How much RAM does it use?**
About the size of the zone, which is the game's recorded read set (up to 2 GiB by default). Prewarm runs only when at least 4 GB of RAM would still be free afterwards, and Windows can reclaim the cache when your programs need the memory.

**What happens after a game update?**
A patch can change files, so a saved plan may no longer match them. AsterCache checks the plan before moving anything and skips files that changed, and every 5 minutes (while the game is off) it re-applies the plan if the layout has fallen apart. In automatic mode, when only some files changed (less than half of the planned hot data), only the ranges of those files are dropped from the learned trace and prewarm list (the originals are kept as `.stale`), the plan is made again from the rest, and the next play learns the changed files again. When half or more changed, the whole trace and plan are set aside as `.stale` and learned again. After a big patch, run Collect trace, Create plan, and Apply relocation again. This has not been tried with a real game update yet.

**Does it matter whether my drive is SMR or CMR?**
Only one SMR drive (WD20EZAZ) has been measured; CMR drives have not. Removing head movement should help both kinds, but that is not verified. SMR drives can write slowly during the one-time move.

**Is the first run required?**
Relocation needs a trace (the record of what the game reads). Without one from a first run, the service tries a matching shared profile (`auto.shared_profiles`) and then the game's Windows prefetch record. Each of these gives a boot trace, so relocation can start before the first run. The shared profile repository (https://github.com/DO0OG/AsterCache-profiles) is still being prepared and does not exist yet; until it does, that step is skipped silently. Prewarm still runs without a trace. A game whose whole install folder fits the RAM budget (the smaller of a quarter of physical RAM and free RAM minus `prewarm.min_free_ram_mib`) is prewarmed in full (`auto.full_prewarm`, on by default; the budget can be changed with `auto.full_prewarm_max_mib`). A larger game gets only the files its engine (Unity, Unreal, or Source) reads almost every time at the start of loading (`auto.engine_profiles`, on by default). Their effect has not been measured yet. A trace recorded on another PC can be brought in with `aster profile import` (see `aster profile export`). It is saved as `plans\<name>.boot.trace.json`, so relocation and prewarm can start from it without a first run; the first run is still recorded, and the plan is then made again from that recording (same zone first), and the old plan is kept as `.stale`.

**Will the very first launch be faster?**
First-run read-ahead is an experimental feature, off by default (no gain in E7 to E7f measurements; `auto.learn.enabled`). The first launch only records what the game reads; the full effect comes from the second launch, after the move and prewarm. A game found only by activity detection (not from a launcher library) starts recording on its next launch. The measurements are in [docs/RESULTS_SUMMARY.md](RESULTS_SUMMARY.md#e7-first-run-read-ahead). Prewarm (the whole folder or an engine profile) also runs without a trace, but its effect has not been measured yet.

**Can my files get damaged?**
Every move is verified with a SHA-256 check and written to a journal that can be reverted. Still, this is a beta that moves real data: back up important drives first. See [Safety measures](../README.md#safety-measures).

## Troubleshooting

**It says you do not have permission to save settings.**
The data folder is writable only by administrators. Relaunch the management window as administrator.

**I added or removed a game but it was not applied.**
The service re-reads `config.json` every 5 seconds, so this normally applies without a restart. If the new configuration fails validation or the folder permission check, the previous settings stay in force and the reason is written to the log.

**The tray shows "Service reinstall required".**
A tray update does not replace the service executable. Reinstall the service with the installer.

**A relocation command ends with `not_admin`.**
Run relocation, revert, and volume commands from an administrator PowerShell.

**Prewarm did not run.**
The reason it was skipped is recorded in `service.log`. Typical causes are insufficient available RAM, the game already running, an active cooldown, or a missing plan file. A game counts as running only by its registered exe names; an unregistered helper process in the install folder does not count. The skip line names the process and its pid, and the running age is counted from when the service saw the start (the process start time is not read). You can also check the `prewarm` entry of `aster status`.
