**English** | [한국어](FAQ.ko.md)

# FAQ and troubleshooting

> Short answers. Each one links to the page that explains it in full. Start with [Getting started](../README.md#getting-started) in the README.

## FAQ

**Does it work for games installed on an SSD?**
No. It only targets games on an HDD; games on an SSD are skipped because an SSD does not need it. A game on an HDD that is also the Windows system drive is only discovered and prewarmed (status "prewarm only (system drive)"); it is not relocated.

**Is it bad for my hard disk's lifespan?**
Relocation writes to the game drive, but with limits: at most 1 GiB per round on an SMR drive (2 GiB otherwise), 2 GiB a day on SMR (8 GiB otherwise), and only while the PC is idle. After that it mostly reads. The effect on drive lifespan, temperature, and noise has not been measured. See [Effects on your hard disk](../README.md#effects-on-your-hard-disk-and-cautions) and the [game guide](GAME_GUIDE.md).

**What does it do while I am playing?**
When a game starts, it reads that game's recorded data in order for up to 120 seconds (it stops sooner at the loading boundary), and background work on the same disk (relocation, prewarm, other reads) waits or stops. It also samples the disk's read counters, not the game's files or process, to keep a play record. The steps are in [Automatic mode](AUTO_MODE.md); the settings are in [Configuration](CONFIGURATION.md).

**What happens if I uninstall it or remove a game?**
Uninstalling first reverts SSD assist links, then removes the program and the service. The data folder `%ProgramData%\AsterCache` stays. Pieces already moved are not moved back, and the file contents are unchanged either way; to restore the previous layout, click **Revert** on the Games tab first. Removing a game from the list leaves its files as they are. See [Usage](USAGE.md#common-tasks).

**How much RAM does it use?**
Prewarmed data is ordinary file cache. Across all games it is limited to half of physical RAM by default (`prewarm.max_cache_mib`), and prewarm runs only when at least 4 GB of RAM would still be free afterwards. Windows reclaims the cache when your programs need the memory. See [Configuration](CONFIGURATION.md).

**Is it safe with anti-cheat games?**
AsterCache never touches the game process: it reads files from its own process, watches file reads with Windows event tracing, and moves files only after the game has exited. But no controlled validation has been done, only usage records from one PC, and no guarantee is made about account sanctions. For an anti-cheat game, turn automatic mode off or add the game to `auto.ignore_exes` before you play. See [Anti-cheat compatibility](ANTICHEAT.md).

**What happens after a game update?**
A patch can change files, so a saved plan may no longer match. AsterCache checks the plan before moving anything and skips changed files, and every 5 minutes (while the game is off) it re-applies the plan if the layout has fallen apart. In automatic mode it also learns the changed files again; when half or more of the planned data changed, it sets the whole trace and plan aside as `.stale`. This has not been tried with a real game update yet. See [Automatic mode](AUTO_MODE.md).

**Does it matter whether my drive is SMR or CMR?**
Only one SMR drive (WD20EZAZ) has been measured; CMR drives have not. Removing head movement should help both, but that is not verified. SMR drives can write slowly during the one-time move.

**Is the first run required?**
Relocation needs a trace (the record of what the game reads). Before a first run the service tries a matching shared profile (the repository is still being prepared, so this step is skipped for now) and then the game's Windows prefetch record; each gives a boot trace, so relocation can start early. Prewarm needs no trace: a game whose whole folder fits the RAM budget is prewarmed in full, and a larger one gets the files of its engine. Their effect has not been measured. A trace from another PC can be brought in with `aster profile import` ([Usage](USAGE.md#the-aster-command)).

**Will the very first launch be faster?**
No. The first launch only records what the game reads; the full effect comes from the second launch, after the move and prewarm. First-run read-ahead is an experimental feature, off by default because the E7 to E7f measurements showed no gain (`auto.learn.enabled`; see [RESULTS_SUMMARY](RESULTS_SUMMARY.md)).

**Does it optimize programs that stay running, such as Wallpaper Engine?**
No. Desktop utilities that are not games and stay running once started, such as Wallpaper Engine, YoloMouse, Lossless Scaling, OBS Studio, and SteamVR, are left out of automatic registration, and one already registered is unregistered once after the service starts. For any other program, remove it on the Games tab; it is then not registered again.

**Can my files get damaged?**
Every move is verified with a SHA-256 check and written to a journal that can be reverted. Still, this is a beta that moves real data: back up important drives first. See [Safety measures](DEVELOPMENT.md#safety-measures).

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
The reason it was skipped is recorded in `service.log`. Typical causes are insufficient available RAM, the game already running, an active cooldown, or a missing plan file. A game counts as running only by its registered exe names. You can also check the `prewarm` entry of `aster status`.
