**English** | [한국어](CONFIGURATION.ko.md)

# Configuration

> This page describes the `config.json` keys, the watcher, and the display language. The automatic mode keys are in [AUTO_MODE.md](AUTO_MODE.md).

The configuration file is `%ProgramData%\AsterCache\config.json`. Editing it in the management window is recommended. When editing by hand, write JSON backslashes as `\\`.

ORBIT is turned on and off from the CLI, not in `config.json` (`aster orbit mode off|shadow|active`). See [ORBIT: prewarm order adjustment](ORBIT.md).

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

| Key | Meaning |
|---|---|
| `language` | UI language. `auto` (default), `ko`, or `en`. `auto` follows the Windows display language and falls back to English unless it is Korean. |
| `watcher` | How game starts are detected. `poll` (default) or `etw`. `etw` requires administrator rights and falls back to polling if it fails. |
| `games[].name`, `exe_names` | Game name and list of executable names |
| `trace`, `plan` | Paths to the trace file and plan file |
| `origin_root` | Game install folder |
| `allowed_roots` | Folders where relocation is allowed |
| `volume_label` | Label of the target volume. If it differs, nothing is relocated. |
| `max_stream_mib` | Upper limit of what one stream or prewarm reads (default: the larger of 4096 and the zone limit; an explicit value wins) |
| `prewarm.on_service_start` | Run prewarm when the service starts (default `true`) |
| `prewarm.trigger_exes` | Run prewarm when one of these programs starts (for example a launcher). Empty by default |
| `prewarm.min_free_ram_mib` | Available RAM to leave free even after prewarm (default 4096) |
| `prewarm.cooldown_min` | Minimum interval between prewarms (default 30 minutes) |
| `prewarm.max_cache_mib` | Total MiB that prewarm may hold in the file cache, shared by all games. `0` or omitted = automatic (half of physical RAM); a positive value is used as is but cannot exceed physical RAM. If several games set it, the largest value applies. The available-RAM reserve (`min_free_ram_mib`) still applies. |
| `auto` | Automatic mode settings. Omitted keys take their defaults. See [Automatic mode](AUTO_MODE.md). |
| `max_zone_mib` | Upper limit of the zone size for automatic planning, in MiB (default 2048), or `"auto"` (physical RAM ÷ 4, between 2048 and 8192 MiB; capped at 2048 when the disk type is unknown or the disk is on USB). On a 32 GB PC, `"auto"` is recommended over a fixed 2048. |
| `games[].source` | `auto` for a game found automatically, `manual` for a game you registered |
| `games[].anticheat` | Anti-cheat folders found in the install folder, for example `EasyAntiCheat`. Shown in the Games tab; it does not change behavior. For the policy on anti-cheat games and the usage records from this PC, see [Anti-cheat compatibility](ANTICHEAT.md) |
| `games[].display_name` | Game name shown on screen (optional). When not set, uses the launcher name the service recorded, or else the key with underscores replaced by spaces. The internal key is unchanged. |
| `games[].display_names` | Per-language display names (optional). Example: `{"ko": "발로란트", "en": "VALORANT"}`. Only the keys `ko` and `en` are accepted. |
| `auto.ssd_tier` | Optional SSD assist storage: `enabled` (default `false`), `budget_gib` (default 8), `root` (default `"auto"`). See [SSD assist storage](AUTO_MODE.md#ssd-assist-storage-optional) |
| `auto.trace_runs` | Default 3. If the number of plays is below this value, the service traces again to accumulate the hot set and widen the zone. |
| `auto.trace_target_coverage` | Default 70. No longer used to decide additional trace runs; kept only for compatibility. After `trace_runs`, extra collection is decided by the previous merge adding at least `trace_merge_min_mib` of new blocks and fewer than `trace_runs_max` valid runs existing. |
| `auto.trace_runs_max` | Default 8. The most valid trace runs (at least 64 MiB read each) collected by the additional-run rule. |
| `auto.restream_cooldown_min` | Default 10. The start-up stream of the same game is not run again within this many minutes. It is also skipped while the game process is already running or the disk is busy. |
| `auto.rewarm_after_exit_min` | Default 2. When a game exit is detected, the rewarm check is pulled forward to this many minutes later. |
| `auto.start_stream_cap_seconds` | Default 120. Time limit in seconds for the start-up stream. It also stops at the loading boundary (the point where the reads in a 10-second moving window fall below 10% of their peak, at most 60 seconds in). 0 means no time limit (the old behavior). |
| `auto.trace_late_interval_min` | Default 10. If the game is still running after the first trace window (`trace_cap_seconds`) ends, a late window opens every this many minutes. |
| `auto.trace_late_window_seconds` | Default 90. Recording time in seconds for each late trace window. |
| `auto.trace_late_windows` | Default 6. The most late windows opened after the first one. 0 turns late tracing off. The read cap applies to the session total, and saving and merging happen only after the game exits. |
| `auto.load_boundary` | Default `false`. When `true`, the service measures when the loading head (tier0) is in RAM at each game start. It reads the disk, so it stops when the game exits. |
| `auto.max_relocate_gib_per_day` | Default 8. The most GiB a day that relocation may write (0 = unlimited, range 0 to 1048576). An explicit 8 is treated as the default, so SMR disks still use 2 GiB. |
| `riot.champ_select_prewarm` | Default `false`. While a Riot game's champion select runs, reads the chosen champions' and the map's files into RAM in advance, using the local League client API. Korean account LCU policy not confirmed. |
| `riot.champ_select_budget_mib` | Default 2048. The most MiB the champion select prewarm may read. |
| `prewarm.pin` | Keep prewarmed data open as read-only mappings in the service. When not written, it is on automatically with 24 GiB of RAM or more; `false` turns it off; an explicit `true` works with 16 GiB of RAM or more. Effect unverified (needs an experiment). See [Anti-cheat compatibility](ANTICHEAT.md) |

The service re-reads the whole `config.json` every 5 seconds (when its size or modified time changes), so adding, removing, or changing games and the watcher takes effect without a restart. If the new configuration fails validation or the folder permission check, the previous settings stay in force and the reason is written to the log.

`games[].display_name` and `display_names` are optional keys you can write by hand. If you roll back to beta.24 with these keys in the configuration, that version rejects the settings (exit code 2), so remove both keys before rolling back.

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
