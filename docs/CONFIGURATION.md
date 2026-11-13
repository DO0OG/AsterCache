**English** | [한국어](CONFIGURATION.ko.md)

# Configuration

> Every `config.json` setting with its default, the watcher, and the display language. This page is the one place for defaults; other pages link here.

The configuration file is `%ProgramData%\AsterCache\config.json`. Editing it in the management window is recommended. When editing by hand, write JSON backslashes as `\\`.

- The service re-reads the whole file every 5 seconds (when its size or modified time changes), so changes take effect without a restart. If the new configuration fails validation or the folder permission check, the previous settings stay in force and the reason is written to the log.
- Unknown keys are rejected. Omitted keys take the defaults below.
- ORBIT is turned on and off from the CLI, not in `config.json` (`aster orbit mode off|shadow|active`). See [ORBIT](ORBIT.md).
- The management window's Settings and Games tabs write most of these keys. Its **Advanced** section (closed by default) holds seven numeric limits: `prewarm.max_cache_mib`, `auto.startup_warm_games`, `auto.startup_warm_max_mib`, `auto.rewarm_after_exit_min`, `auto.max_relocations_per_day`, `auto.idle_seconds`, and `auto.start_stream_cap_seconds`. A value the window writes equal to its default is removed from the file.
- The pop-up notification switch is not in `config.json`; it is `notifications.enabled` in `tray.json`.

```json
{
  "language": "auto",
  "watcher": "poll",
  "games": [
    {
      "name": "frostpunk",
      "exe_names": ["Frostpunk.exe"],
      "trace": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.prefetch.json",
      "plan": "C:\\ProgramData\\AsterCache\\plans\\frostpunk.plan.json",
      "origin_root": "D:\\Games\\Frostpunk",
      "allowed_roots": ["D:\\Games"],
      "volume_label": "GAMES",
      "max_stream_mib": 4096,
      "prewarm": {
        "on_service_start": true,
        "trigger_exes": ["Steam.exe"],
        "min_free_ram_mib": 4096,
        "cooldown_min": 30
      }
    }
  ]
}
```

## Top level

| Key | Default | Meaning |
|---|---|---|
| `language` | `auto` | UI language: `auto`, `ko`, or `en`. `auto` follows the Windows display language and falls back to English unless it is Korean. |
| `watcher` | `poll` | How game starts are detected: `poll` or `etw`. `etw` needs administrator rights and falls back to polling if it fails. See [Watcher](#watcher). |
| `poll_ms` | 50 | Polling interval of the `poll` watcher, in ms (50 to 5000). |
| `max_zone_mib` | 2048 | Upper limit of the zone size for automatic planning, in MiB (64 to 65536), or `"auto"`: physical RAM ÷ 4, between 2048 and 8192 MiB, capped at 2048 when the disk type is unknown or the disk is on USB. On a 32 GB PC, `"auto"` is recommended over a fixed 2048. |
| `riot.champ_select_prewarm` | `false` | While a Riot game's champion select runs, reads the chosen champions' and the map's files into RAM in advance, using the local League client API. Riot announced on 2019-01-24 that players in Korea may not use applications that use the League Client API, and no withdrawal was found, so do not turn this on with a Korean account. See [the League of Legends reference](GAME_GUIDE.md#reference-league-of-legends-vanguard). |
| `riot.champ_select_budget_mib` | 2048 | The most MiB the champion select prewarm may read (64 to 16384). |
| `memory.background_yield` | `true` | During a game session, programs unrelated to the game give up memory: their working sets are emptied and their memory priority is lowered, and the priority is restored when the session ends. Game, launcher, anti-cheat, and service processes are not touched. `false` turns it off; there is no switch for it in the Settings tab. See [Background memory yield](AUTO_MODE.md#background-memory-yield). |
| `games` | `[]` | The game list (up to 32 games). The keys of one game are below. |

## One game: `games[]`

| Key | Default | Meaning |
|---|---|---|
| `name` | required | Internal name: 1 to 64 characters of letters, digits, `.`, `_`, `-`. It names the plan, journal, and status files. |
| `exe_names` | required | List of executable names (1 to 16, each ending in `.exe`). |
| `trace`, `plan` | required | Paths of the trace file and plan file. They must be `.json` files inside the plans folder. |
| `origin_root` | required | Game install folder. It must be inside `allowed_roots`. |
| `allowed_roots` | required | Folders where relocation is allowed (1 to 16, on the same drive as `origin_root`, never a whole drive). |
| `volume_label` | required | Label of the target volume. If it differs, nothing is relocated. |
| `reapply_interval_minutes` | 1440 | Interval of the periodic layout check, in minutes (5 to 10080). |
| `max_stream_mib` | the larger of 4096 and the zone size | Upper limit of what one stream or prewarm reads, in MiB. An explicit value wins. |
| `source` | `manual` | `auto` for a game found automatically, `manual` for a game you registered. |
| `anticheat` | none | Anti-cheat found in the install folder: `eac`, `battleye`, `vanguard`, `kernel`, `ricochet`, or `unknown`. Shown in the Games tab; it does not change behavior. For the policy, see [Anti-cheat compatibility](ANTICHEAT.md). |
| `display_name` | none | Game name shown on screen. When not set, uses the launcher name the service recorded, or else the key with underscores replaced by spaces. The internal key is unchanged. You can also set it in the Game options window on the Games tab (default, Korean, and English name boxes). |
| `display_names` | none | Per-language display names. Example: `{"ko": "발로란트", "en": "VALORANT"}`. Only the keys `ko` and `en` are accepted. |
| `mode` | `inherit` | What the service does for this game: `inherit` (everything), `prewarm_only` (no relocation or periodic re-apply), `relocate_only` (no prewarm, re-prewarm, top-up, start-up stream, or pin), `off` (nothing). Tracing stays allowed unless the game is `off` or `paused`. Also set by the policy choice in the Game options window on the Games tab. |
| `paused` | `false` | When `true`, the service starts no new work for this game, whatever `mode` says. Learned data and records are kept. Also set by the Paused checkbox in the Game options window on the Games tab. |

`display_name` and `display_names` are optional keys you can write by hand. If you roll back to beta.24 with these keys in the configuration, that version rejects the settings (exit code 2), so remove both keys before rolling back.

### Prewarm: `games[].prewarm`

| Key | Default | Meaning |
|---|---|---|
| `on_service_start` | `true` | Run prewarm when the service starts. |
| `trigger_exes` | `[]` | Run prewarm when one of these programs starts, for example a launcher (up to 16). |
| `min_free_ram_mib` | 4096 | Available RAM to leave free even after prewarm. Not shown in the Settings window; edit the file. |
| `cooldown_min` | 30 | Minimum interval between prewarms, in minutes. |
| `max_cache_mib` | 0 (automatic) | Total MiB that prewarm may hold in the file cache, shared by all games. `0` or omitted = half of physical RAM; a positive value is used as is but cannot exceed physical RAM. If several games set it, the largest value applies. The `min_free_ram_mib` reserve still applies. The Advanced section writes the value to every registered game (0 removes the key). |
| `pin` | automatic | Keep prewarmed data open as read-only mappings in the service. When not written, it is on automatically with 24 GiB of RAM or more; `false` turns it off; an explicit `true` works with 16 GiB of RAM or more. Effect unverified (needs an experiment). See [Anti-cheat compatibility](ANTICHEAT.md). |

## Automatic mode: `auto`

What each stage does is in [Automatic mode](AUTO_MODE.md). Ranges are given in parentheses.

**Switches and lists**

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Turns the whole automatic mode on or off. |
| `discover`, `trace`, `relocate` | `true` | Turn off one stage each: discovery, tracing, relocation. |
| `ignore_exes` | `[]` | Executables never registered automatically (up to 64). A game you remove in the management window is added here. |
| `ssd_tier` | `enabled: false`, `budget_gib: 8`, `root: "auto"` | Optional SSD assist storage. `budget_gib` (1 to 4096) is the most SSD space one game may use. `root` is `"auto"` (the system drive if it is an SSD, otherwise the SSD with the most free space) or an absolute path on an SSD. |

**Before the first run**

| Key | Default | Meaning |
|---|---|---|
| `shared_profiles` | `enabled: true`, `url: ` the project's profiles release | Weekly download of shared profiles over HTTPS (the URL must be https). The shared repository is still being prepared. |
| `full_prewarm` | `true` | For a game with no trace, prewarm the whole install folder when it fits the RAM budget. |
| `full_prewarm_max_mib` | 0 | Folder size limit for that prewarm, in MiB (0 to 1048576). `0` means 1024 MiB. |
| `engine_profiles` | `true` | For a larger game with no trace, prewarm the files of its engine (Unity, Unreal, Source, and others). |
| `learn` | `enabled: false`, `mode: "stream"`, `window_mib: 512`, `max_total_mib: 0`, `saturation_mbps: 80` | Experimental first-run read-ahead; off because E7 to E7f showed no gain. `mode` is `"stream"`, `"gap"`, `"bounded"`, or `"adaptive"`. `max_total_mib` 0 = the smaller of a quarter of physical RAM and 4096 MiB. |

**Tracing**

| Key | Default | Meaning |
|---|---|---|
| `trace_cap_seconds` | 1800 | Time limit of the first trace window, in seconds (30 to 7200). |
| `trace_cap_mib` | 6144 | Read limit of a trace, in MiB (64 to 65536). The default value means the larger of 6144 and twice the zone limit; any other value is used as written. |
| `trace_runs` | 3 | If the number of plays is below this value (1 to 32), the service traces again to accumulate the hot set and widen the zone. |
| `trace_runs_max` | 8 | The most valid trace runs (at least 64 MiB read each) collected by the additional-run rule (1 to 32). |
| `trace_merge_min_mib` | 64 | After `trace_runs`, extra collection continues while the previous merge added at least this many MiB of new blocks (1 to 65536). |
| `trace_target_coverage` | 70 | No longer used to decide additional runs; kept only for compatibility. |
| `trace_late_interval_min` | 10 | If the game is still running after the first window, a late window opens every this many minutes (1 to 1440). |
| `trace_late_window_seconds` | 90 | Recording time of each late window, in seconds (1 to 7200). |
| `trace_late_windows` | 6 | The most late windows after the first one (0 to 32). 0 turns late tracing off. |

**Relocation**

| Key | Default | Meaning |
|---|---|---|
| `idle_seconds` | 30 | How long the PC must stay idle before relocation, in seconds (10 to 86400). The older `idle_minutes` (default 3) is used only if it is written and `idle_seconds` is not. |
| `relocate_hours` | `[]` | Local hours of the day (0 to 23, no duplicates, up to 24 values) at which automatic relocation and periodic re-apply may start. Empty means no time limit. The Settings tab offers one start-to-end range that may cross midnight; a file with several ranges is kept as written and shown as custom. |
| `max_relocations_per_day` | 4 | The most relocations in 24 hours (0 to 24). |
| `max_relocate_gib_per_day` | 8 | The most GiB a day that relocation may write (0 = unlimited, up to 1048576). An explicit 8 is treated as the default, so SMR disks still use 2 GiB. |
| `relocate_chunk_mib` | 4 | Size of one moved piece, in MiB (1 to 256). |
| `relocate_parallel` | automatic | Files moved at once (1 to 8). When not written: 1 on a drive that looks like SMR, 2 for USB or an unknown disk type, 8 otherwise. |
| `relocate_io_priority` | `low` | I/O priority hint of a move: `low` or `normal`. |
| `relocate_verify` | `sample` | Content comparison before and after a move: `sample`, `all`, or `off`. |
| `relocate_order` | `auto` | Order of moves: `auto` (same as `target`), `target`, or `source` (experimental). |
| `post_update_delay_min` | 15 | After a game update is detected, re-planning, relocation, and prewarm for that game wait this many minutes (0 to 1440; 0 = no delay). |
| `post_relocate_recovery` | `probe` | How the next job on the same drive waits after a relocation: `probe` (reads a short sample every 2 minutes; the drive counts as recovered after 2 samples in a row at 90% or more of the baseline), `fixed` (waits `post_update_delay_min` minutes), or `off`. |
| `recovery_max_min` | 60 | The longest `probe` waits for recovery, in minutes (1 to 1440). |
| `recovery_hard_max_min` | 180 | If recovery is still unconfirmed at `recovery_max_min`, further relocations stay held up to this limit, in minutes (1 to 1440). Prewarm is not affected. |

**Prewarm upkeep and start**

| Key | Default | Meaning |
|---|---|---|
| `prewarm_recent` | 0 | `0` prewarms as many recent games as fit the budget; `1` to `32` caps the number of games; `-1` turns recent-game prewarm off. |
| `startup_warm_games` | 2 | At service start, prewarm this many of the games played in the last 14 days (0 to 32; 0 = no prewarm at service start). |
| `startup_warm_max_mib` | 1024 | Total MiB the service-start prewarm reads at once (1 to 1048576). Games beyond it are skipped. |
| `rewarm_minutes` | 10 | Interval of the cache check that prewarms again when the data went cold (0 to 1440; 0 = off). |
| `rewarm_after_exit_min` | 2 | When a game exit is detected, the cache check is pulled forward to this many minutes later (0 to 1440; 0 = off). |
| `start_stream_enabled` | `true` | When `false`, no start-up stream runs when a game starts. Also a switch in the Settings tab. |
| `restream_cooldown_min` | 10 | The start-up stream of the same game is not run again within this many minutes (0 to 1440). It is also skipped while the game is running or the disk is busy. |
| `start_stream_cap_seconds` | 120 | Time limit of the start-up stream, in seconds (0 to 7200). It also stops at the loading boundary (the point where the reads in a 10-second moving window fall below 10% of their peak, at most 60 seconds in). 0 means no time limit. |
| `keep_awake` | `true` | A small 4 KiB read every 10 minutes, while a launcher is open or at the hours you usually play, so the HDD does not spin down. Windows power settings are not changed. |
| `keep_stream_on_game_start` | `false` | When `true`, a prewarm reads to the end even if its game starts (for experiments). Normally it stops at the next 64 MiB batch to yield the disk to the game. |
| `load_boundary` | `false` | When `true`, measures when the loading head (tier0) is in RAM at each game start. It reads the disk, so it stops when the game exits. |

## Updates: `update`

| Key | Default | Meaning |
|---|---|---|
| `mode` | `auto` | How updates are handled: `auto`, `notify`, or `off`. |
| `prerelease` | `false` | When `true`, prereleases are also considered. |

The Updates card in the Settings tab sets `update.mode` and `update.prerelease`.

Older versions reject the keys `games[].mode`, `games[].paused`, `auto.start_stream_enabled`, `auto.relocate_hours`, `update`, and `memory`, so remove them before rolling back. `memory` is new in 1.0.3: versions 1.0.2 and earlier reject a settings file that contains it, whatever its value.

## Watcher

| Watcher | How it works | Permission | Detection latency (S4 measurement) | Load | Recommended |
|---|---|---|---|---|---|
| `poll` | Reads the PID list every 50 ms and looks up the names of new PIDs only | No administrator rights | median 27 ms, p95 58 ms | About 1% of one core when idle | Default and recommended |
| `etw` | Subscribes to kernel process events in a real-time session and flushes the buffer every 50 ms with `FlushTrace` | Administrator rights (the service runs as LocalSystem) | p95 119 ms (1.65 s before the 50 ms flush) | Session buffer memory | Falls back to polling if it fails |

In the **Settings** tab the saved value is `poll` or `etw`, and the label shows `poll (recommended)` or `etw`.

## Language

The management window and the `aster` CLI are available in Korean and English.

- The language is chosen by `"language"` in `config.json`: `"auto"` (default, follows the Windows display language and uses English unless it is Korean), `"ko"`, or `"en"`. You can also change it in the **Language** item on the management window's Settings tab.
- The CLI accepts the global option `--lang ko|en` and the environment variable `ASTER_LANG`. Priority: `--lang` > `ASTER_LANG` > `config.json` > OS language.
- JSON output does not depend on the language.
