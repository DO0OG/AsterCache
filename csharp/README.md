**English** | [한국어](README.ko.md)

# AsterCache C#

The final C# port of the Python reference implementation (`relayout/`, `prefetch/`, `bench/`). Python stays as the reference implementation and as the generator of ground truth for cross-validation.

## Build and test

```
dotnet build  csharp\AsterCache.sln -c Release
dotnet test   csharp\AsterCache.sln -c Release --filter "Category!=Live"
dotnet test   csharp\AsterCache.sln -c Release --filter "Category=Live"    # needs D:\AsterBench and the Python reference, non-administrator
dotnet format csharp\AsterCache.sln --verify-no-changes
```

The target is .NET 10 (`global.json` uses SDK 10.0.400, `latestPatch`). CI (`.github/workflows/csharp-ci.yml`) runs build, test, and format without the Live tests.

## Publishing the executables

The scripts use the locations below by default. Publishing is done by the user.

```
dotnet publish csharp\src\AsterCache.Cli\AsterCache.Cli.csproj -c Release -r win-x64 --self-contained -o csharp\publish\cli
dotnet publish csharp\src\AsterCache.Service\AsterCache.Service.csproj -c Release -r win-x64 --self-contained -o csharp\publish\service
```

Check the CLI commands (`aster plan`, `probe`, `apply`, `verify`, `revert`, `trace`, `prefetch`, `status`, `launch`, `stream-bench`, `learn`, `profile`) and their options with `aster <command> --help`. The relocation and prefetch commands use the same names as Python `relayout/cli.py` and `prefetch/cli.py`.

`aster discover [--json]` lists the games found in the supported launchers' libraries, read only, with the anticheat marker. The `launcher` value of each entry can also be `riot`, `uninstall`, or `xbox`. `aster status --auto-status` prints the automatic mode state (`auto_status.json`).

`aster learn --exe <name>... --root <folder> [--seconds N] [--window-mib M] [--max-mib M] [--mode stream|gap|bounded|adaptive] [--saturation-mbps N] [--gap-ms N] [--seq-runs N] [--out trace.json] [--json]` runs the first-run learning (trace plus read-ahead) without the service, for measurement and debugging. It needs administrator rights. Its output includes `read_ahead_bytes`, `game_read_bytes`, `hit_ratio`, `events`, and `events_lost`. The new JSON keys are `resolved_reads`, `late_reads`, `unresolved_reads`, `sequential_files`, `hit_bytes`, `gap_count`, `yielded_requests`, and `read_ahead_while_idle_bytes`.

- `--gap-ms` (default 100): read-ahead starts only after this much time has passed since the game's last read (gap-only).
- `--seq-runs` (default 6): a file counts as sequential only when increasing reads of 4 MiB or less continue for this many runs and at least 8 MiB of the file was read.
- The total read-ahead is capped at game reads × 1.5 + 64 MiB.
- `--mode stream` (default) reads ahead in 16 MiB steps without waiting for gaps in the game's reads; `--mode gap` is the earlier method (reads only after 100 ms of quiet, 1 MiB requests); `--mode bounded` reads ahead only sequential files, 16 MiB ahead, total ≤ game reads + 32 MiB, yielding 50 ms after each game read (measured in E7e: no gain); `--mode adaptive` adds to bounded a pause while the game's reads over the last 500 ms run at `--saturation-mbps` (default 80 MiB/s) or more and a 200 ms pause when our request latency doubles, reported as `saturated_ms` and `paused_requests` (measured in E7f: no gain). Stream is the default because alternating 1 MiB reads with the game's reads costs a rotational delay at each switch, and the gap-only mode was about 2 times slower in the E7b re-measurement (`results\csharp_e7b_gen\README.md`: synthetic 19.82 → 38.71 s, Frostpunk 42% zone 18.98 → 37.40 s). Stream was measured in E7d and is not adopted either (no gain), so read-ahead stays off by default.

`aster profile export --game <name> --out <file.json|file.zip> [--data-dir <folder>]` writes one game's trace as a file with relative paths (format `astercache-profile`, version 1: `files` with path, size, and `sample_sha256`, plus `raw_trace` and `prefetch_trace`). `aster profile import --file <file> --game <name> [--data-dir <folder>] [--json]` accepts the file only when the size and sample SHA of every file match. On a mismatch it lists the mismatches and exits with code 1. Writing needs administrator rights.

The CLI language is set with the global option `--lang ko|en` or the environment variable `ASTER_LANG` (priority: `--lang` > `ASTER_LANG` > `config.json` `"language"` > OS language). JSON output does not depend on the language.

Run the service with `AsterCache.Service.exe [--console] [--data-dir <folder>]`. The default data folder is `%ProgramData%\AsterCache`, which holds `config.json`, `plans\`, `journals\`, and `logs\`.

`AsterCache.Service.exe --console --watch-test <seconds>` does not start the service. For that duration it runs the ETW watcher on the exe names in the configuration and prints the starts it detected, the tally of Kernel-Process events that came into the session (per path and ID: raw `raw`, decoded with TDH `decoded`), the payload shape of the first event, and exceptions; it then runs the same test once more with the polling watcher (administrator required). The ETW watcher (`"watcher": "etw"`) receives start events by two paths, and if no start event arrives for 60 seconds it ends with an exception and the service falls back to polling. The ETW live session flushes its buffers about once per second, so detection latency can be larger than polling (250 ms).

`aster prefetch --timing` adds `timing_ms` to the output JSON (load, schedule, stream, plus the totals and counts of path resolution, layout lookup, and size lookup, and the total time inside the command). When publishing (`-r win-x64`), `PublishReadyToRun` is on and reduces start-up JIT.

## Management window

The tray app (`AsterCache.Tray`) has a main window "AsterCache" for seeing and operating everything at a glance, like a drive management utility. It opens by default when the app starts, and closing it sends the app to the tray (reopen with a double-click on the tray icon or the "Open" menu). No new logic is built in: the window launches `aster.exe` from the same folder as a child process and reads its one-line JSON (`aster status`, `plan`, `apply`, `verify`, `revert`, `trace`, `prefetch`), while configuration editing and service control use the tray's `ConfigEditor` and `ServiceControl`. Every executed command and its result (JSON or error) is written to `tray.log` and shown on the Log tab.

```
dotnet publish csharp\src\AsterCache.Tray\AsterCache.Tray.csproj -c Release -r win-x64 --self-contained -o csharp\publish\gui
csharp\publish\gui\AsterCache.Tray.exe [--data-dir <folder>]
```

`aster.exe` and `AsterCache.Service.exe` are published together into the same `gui` folder (the default of `--data-dir` is `%ProgramData%\AsterCache`).

| Area | Content |
|---|---|
| Drive list on the left | For each fixed volume: letter, label, file system, total/free capacity, disk model, HDD/SSD (`IncursSeekPenalty` from `IOCTL_STORAGE_QUERY_PROPERTY`; "unknown" on failure), and the number of registered games. Selecting one shows only that drive's games on the right ("All drives" is at the top). |
| Status tab | Whether the service is installed and running, watcher type, last prewarm (time, MiB, seconds), per-game layout verdict (`verdict` of `aster status --plan`), zone hit rate (`in_zone_clusters / hot_clusters`), last relocation time (journal file time), and per-game prewarm results. Refresh button. |
| Games tab | List: name, exe, origin root, plan/trace present or not, layout verdict, prewarm settings. The buttons are in the table below. |
| Settings tab | Service install, uninstall, start, and stop; watcher poll/etw; display of prewarm defaults; the **Automatic mode** group (on, discover, trace, relocate, prewarm count, zone cap, rewarm interval, excluded exes); open the data folder and log folder; tray and service versions; **language** (auto / Korean / English). |
| Log tab | The last 200 lines of `service.log` and `tray.log`, with refresh. |
| Top banner | Without administrator rights: "No administrator rights: saving settings, trace, relocation, and service control require relaunching as administrator", with a [Relaunch as administrator] button. |

| Games tab button | Action |
|---|---|
| Register | Pick an exe file and a registration dialog opens with the origin root and allowed root set to the exe's folder, the name to the file name, and the volume label filled from that drive (every field is editable, including the trigger program field); it is saved only if it passes the existing configuration validation. |
| Remove | Removes the game from the configuration (files stay as they are). |
| Capture trace | Administrator. Runs `aster trace --live --exe <exe> --origin-root <root> --out plans\<name>.trace.json --stop-file <temp>` elevated. It shows "Run the game, load up to the first map, then exit", and once the game process has appeared and then disappeared (or on [Stop capture]) it creates the stop file for a clean shutdown and then shows the record count and MiB. |
| Create plan | Looks at the hot bytes with `aster plan --dry-run` (zone 16 GiB), sets the zone size to 1.1 times the hot bytes rounded up to 16 MiB, and writes the plan with an elevated `aster plan --out plans\<name>.plan.json`. It shows hot, zone, and moved MiB and the number of operations. |
| Run relocation | A confirmation dialog shows the bytes to move and a note that reverting is possible, then runs an elevated `aster apply --dry-run` followed by `--execute --allowed-root <root> --require-volume-label <label>`. Progress is shown as a count of journal lines, and the result of `aster verify` is shown at the end. |
| Revert | After confirmation, an elevated `aster revert --journal <journal> --execute`. |
| Prewarm now | The same immediate stream as the tray's "Prewarm now (stream)". |
| Measure | One `aster prefetch --execute --timing`: prefetch MiB, seconds, MB/s, and per-stage (load, schedule, stream) ms. Reading warms the file cache, so to measure the same value again you must clear the cache. |

- File locations: trace `plans\<name>.trace.json`, plan `plans\<name>.plan.json`, the read order the plan also uses `plans\<name>.plan.json.prefetch.json` (the registration dialog fills the configuration's `trace` with this name), journal `journals\<name>.jsonl` (the same file as the service's re-apply journal, so resume and revert match the service).
- Administrator rights: the data folder is writable only by administrators, so trace, plan, relocation, revert, and saving the configuration need administrator rights. When opened without them, `aster.exe` is elevated directly via UAC (arguments are passed one by one; the result is received through a file into which the global option `aster --result-file <path> <command> ...` copies standard output, in a fresh temporary subfolder for each run), and saving the configuration is refused. If ordinary users can modify the `aster.exe` folder, a warning appears above the window. Viewing, prewarm, and measurement work without administrator rights. The relaunch-as-administrator button starts a new elevated process with the same arguments and ends the current one.
- Service installation: runs the same sequence as the installer (create folders, set owner and permissions, `sc create` LocalSystem automatic start, start) with a single UAC prompt. Because the service runs the executable as SYSTEM, if ordinary users can modify the executable folder (a development folder, for example), installation is refused and you are told to use the installer. If `config.json` does not exist, register a game first.
- Relocation and revert are refused while a game is running, and the amount written at one time is limited to 2 GiB (`--write-cap-mib`), so a large move may need several runs with the note "Run again and it continues". Trace capture lasts at most 3600 seconds, and when the app exits or relaunches as administrator while a capture is in progress, it waits up to 10 seconds for the save.
- Images for documentation: `AsterCache.Tray.exe --smoke --screenshot <png> [--tab <0..3>] --data-dir <folder>` saves the main window at 1100x720 and exits (`docs\images\main-window.png`; the drive list is sample values). Adding `--lang en` or the environment variable `ASTER_LANG=en` renders the English UI (this is how `docs\images\main-window.en.png` is made).
- The service re-reads the whole `config.json` every 5 seconds, so registering or removing games and changing the watcher take effect without a restart. If the new configuration fails validation or the folder permission check, the previous settings stay in force and the reason is logged.
- Prewarm pinning (`prewarm.pin`) is not on the Settings tab. Set it per game in `config.json` (off by default; 16 GiB of RAM or more only; a prewarm started by a trigger program is not pinned). Off by default, effect unverified (needs an experiment).
- Not done yet: editing prewarm defaults (display only), window screen tests (only view-model and command-assembly tests exist; `Avalonia.Headless` is not used), and an actual UAC test of the elevation path.

## Administrator scripts (run by the user)

The scripts in `csharp\scripts\` are each run once by the user from an administrator PowerShell. Automation and workers do not run them. Each script checks for administrator rights as its first step and stops without changing anything if it lacks them. Results accumulate in `results\csharp_<yyyyMMdd_HHmmss>\`, and revert commands are written to the `README.txt` in that folder, but the scripts do not run them.

| Script | Purpose | Pass criteria |
|---|---|---|
| S1 `cs_e2_vhdx.ps1` | Whole relocation flow on a throwaway VHDX (5 forced terminations then resume, 1 forced detach, chkdsk, revert) | 0 hash mismatches, resume 5/5, 0 chkdsk errors, throughput 20 MB/s or more |
| S2 `cs_e3_check.ps1` | On D:\AsterBench: probe comparison, plan path check, dry-run, verify, cold stream (a) pvm, (b) overlapped reads | C# median ≤ Python median, manifest unchanged |
| S3 `cs_etw_check.ps1` | Live ETW trace (ended cleanly with `trace --live --stop-file`) and WPR .etl capture, .etl conversion diagnostics (`etl_dump.txt`) | Comparison step not implemented, verdict `not_evaluated` |
| S4 `cs_service.ps1 -Action install [-TestConfig] / test / uninstall [-PurgeData]` | Service registration (LocalSystem), data folder ACL, detection latency of a self-terminating `cmd.exe` 20 times (without killing the process; matched to the log by PID), recording of the watcher type (ETW/polling) | p95 ≤ 100 ms, no service after uninstall |
| S5 `cs_relayout_window.ps1` | Window-batch plan (option not adopted) | Skeleton; stops immediately when run |
| S6 `wdk_install.ps1 -AcceptWdkInstall` | WDK installation only (after the user decides) | km folder exists |
| E7 `cs_e7_learn.ps1` | First-run learning and read-ahead (trace replay) | Experimental feature. Measured on synthetic data and trace replay (E7 to E7f): no gain, so read-ahead is off by default. The first version slowed the cold replay of the scattered synthetic dataset from 21.67 s to 44.90 s (`results\csharp_e7_20261102_203358\README.md`) |

### Privilege boundaries

- The scripts and the installer do not load or sign drivers. They also do not change boot configuration, Secure Boot, virtualization-based security, or Hyper-V settings.
- The data folder is not removed by default. It is deleted only when you explicitly pass `cs_service.ps1 -Action uninstall -PurgeData`.
- Relocation (`apply --execute`, `revert --execute`) targets only the specified folders and volume through `--allowed-root` and `--require-volume-label`.
- The service stays resident with administrator rights. Because the service moves, as SYSTEM, the files named in `config.json` and the plan, the whole data folder (`%ProgramData%\AsterCache`: config.json, plans, journals, logs) is writable only by SYSTEM and Administrators, and ordinary users can only read it (the installer and `cs_service.ps1 install` apply the same ACL). So to register or remove games from the tray and save `config.json`, the tray must run with administrator rights; without them it shows "No permission to save settings: relaunch the tray as administrator" (the settings stay unchanged). If the tray log cannot be written, it is written to `%LocalAppData%\AsterCache\logs`.

## Service behavior

- If a registered game starts during a re-apply (ETW/polling start notification), a cancellation token reaches the fragment loop of `Applier`. Only the fragment being moved (one `MOVE_FILE`) is finished, then it stops, leaving `stopped = "cancelled"` and a resume point in the journal. After the game ends, the 5-minute check continues with the same plan and journal (`aster verify` reports this state not as an error but as `verdict = "resume-needed"`).
- Re-apply happens only when the layout is not contiguous by the same judgment as `aster verify` (`non-contiguous`, including a cancelled, partially applied `resume-needed`). The in-zone ratio (`zone hit`) is only a reference log: when the plan preserves already-contiguous stretches in place, the layout is contiguous even if the ratio is low (10.5% in a real game plan), so no re-apply happens. If any entry's file layout could not be read, no judgment is made and it is treated as healthy.
- A `config.json` with 0 registered games is normal. The service just waits without watching or re-applying, and the tray shows "No registered games". Adding or removing games takes effect within 5 seconds without a restart.
- A game whose plan or trace file does not exist yet skips the check and stream and logs only an info line (not an error). Every detection of a game process is logged as `<name>: <exe> started (pid N)`, and which watcher is used is logged as `process watcher: EtwProcessWatcher` or `PollingProcessWatcher`.
- Automatic mode (see [Automatic mode](../README.md#automatic-mode)) runs inside the service. The service re-reads the whole `config.json` every 5 seconds, so changes take effect without a restart. Its state is written to `%ProgramData%\AsterCache\auto_status.json`, which only the service writes.
- The read-ahead engine (`Prefetch\ReadAhead`) runs first-run learning inside the service. `Tracer`'s real-time callback feeds it, and it flushes every 50 ms. See [Automatic mode](../README.md#automatic-mode).
- Full prewarm of games with no record (`auto.full_prewarm`).
- Engine-profile prewarm (`auto.engine_profiles`, embedded `Core\Profiles\engines.json`).

## Automatic boot sources: components and shared profile index

- `Service\ProfileFeed.cs`: downloads the shared profile zip (`auto.shared_profiles`) once a week with an ETag, caps the download at 64 MiB, and matches the index entries against installed games. Only https URLs are used.
- `Service\AutoRegistry.cs`: launcher discovery and the heuristic. It holds the fixed path and tool exe exclusion lists and the "looks like a game" check (a file of 256 MiB or more, or a launcher mark).
- `Windows\PrefetchFiles.cs` and `Core\Prefetch\PfParser.cs`: read the Windows prefetch `.pf` files (read-only) and turn the file list into a boot trace. Files flagged "do not prefetch" are dropped.
- `Service\AutoRelocator.cs` (`PrefetchBootstrap`) and `Service\AutoTracer.cs` (`BootFromPrefetch`): build the boot trace for a game with no record and plan from it. Only files of 64 MiB or less enter the prefetch plan.
- `Prefetch\ReadAhead.cs`: the first-run read-ahead engine (experimental, off by default; `stream`, `gap`, `bounded`, and `adaptive` modes).

### Shared profile index (for contributors)

The shared profile zip holds `index.json` and the profile files:

```json
{"format":"astercache-profile-index","version":1,"entries":[
  {"steam":{"appid":"730","depots":{"731":"<manifest gid>"}},"profile":"p/cs2.json"},
  {"epic":{"app_name":"..","version":".."},"profile":".."},
  {"exe":{"size":0,"sample_sha256":".."},"profile":".."}
]}
```

- Each entry has exactly one of `steam`, `epic`, or `exe`.
- `profile` is the path of a `.json` file inside the zip, in the `aster profile export` format.
- Matching: Steam needs the app ID and the manifest gid of every listed depot; Epic needs the app name and version; exe needs the file size and sample SHA-256 of the game exe. Even after a match, the boot trace is saved only if the profile file's size and sample hash match this PC.

## Two operating modes: concurrent prefetch / prewarm

- **Concurrent prefetch** (default behavior): reading starts in zone order the moment the game exe starts. The relocated zone is read at the HDD sequential limit (about 140 MB/s on the test PC), so a hot set of 1.6 GB takes about 11.6 s and cannot beat an SSD (2~4 s for 2 GB).
- **Prewarm** (RAM-resident, better than SSD): *before* you start the game, the same zone stream (pvm, in-place execution) runs and loads the hot set into RAM (the file cache standby list). When the game starts it hits the cache and reads no disk. It runs when the service starts (after boot or logon) and when a program in a game's `trigger_exes` (a launcher, for example) starts. The concurrent prefetch at game exe start stays as it is, so if the game starts during prewarm the stream is not stopped and continues (it is the same data).

Prewarm runs only if all of these hold: available RAM ≥ zone size + `min_free_ram_mib`, the game is not running, the cooldown has passed, and the plan and trace files exist. Otherwise it is skipped and the reason is written to `service.log`. The result (bytes, seconds, MB/s, skip reason) is recorded in `service.log` and `%ProgramData%\AsterCache\prewarm_status.json` and appears as `prewarm` in `aster status`.

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
      "max_stream_mib": 4096,
      "volume_label": "GAMES",
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

The amount one stream or prewarm reads is the plan's zone size (the trace's total bytes if there is no plan or the zone is empty), capped by each game's `max_stream_mib` (default 4096). The RAM check for prewarm is also based on this amount, and an empty zone is skipped as `no zone` rather than passing. When the service's data folder cannot be trusted (`canApply` is false), `prewarm_status.json` is not written.

The defaults are `on_service_start` true, `trigger_exes` empty, `min_free_ram_mib` 4096, and `cooldown_min` 30, and omitting `prewarm` gives these defaults. `aster prewarm --plan <plan.json> --origin-root <root>` is an alias of `prefetch --execute --method pvm` (same options). The tray's "Prewarm now (stream)" runs the same stream directly in the tray process (the file cache is system-wide, so the effect is the same), and the "Prewarm trigger programs" field of the game registration dialog takes `trigger_exes` separated by commas. The effect on a real game (start time on a RAM hit) has not been measured yet.

## Tray updates and the service

An installer install puts the tray, `aster.exe`, and the service in `Program Files\AsterCache`, and they share `%ProgramData%\AsterCache` (`config.json`, `plans\`, `journals\`, `logs\`). A newer version is installed by running the new installer again, which stops the service, replaces the files, and starts it again. Velopack automatic update applies only to a tray installed with Velopack, not to an installer install (`UpdateService` does not access service control and is pinned by a test). An actual Velopack install update test has not been done.

- Format compatibility: `config.json` accepts only known keys, so if a new tray writes a new key, the old service cannot start (exit code 2); plan files require an exact `version` match (currently 1); the journal has no format version and is matched only by the plan SHA. The formats have never changed so far, but if they do, the two will go out of step.
- Decision: the update paths are not merged. The tray compares the FileVersion of the service executable registered with the SCM against its own FileVersion, and if they differ it shows "Service reinstall required (service x, tray y)" in the status bar. For the comparison to mean anything, the tray and the service must be published with the same `-p:Version` (development builds are all 1.0.0.0).

## Installer

`packaging\windows\aster.iss` (Inno Setup 6) installs the management window, `aster.exe`, the service, and the runtime into `Program Files\AsterCache`. It registers the `AsterCache` service (LocalSystem, automatic start) and sets the ACL of the whole data folder. It allows administrator installs only. The setup asks for the language (English or Korean, saved as `"language"` in `config.json`), desktop and Start menu shortcuts, opening the management window and keeping the tray icon at sign-in (the shortcut is in the all-users Startup folder), installing and starting the service, and starting the management window at the end. All of them are checked by default. Uninstalling stops and removes the service, quits the tray, and leaves the data folder.

Defines: `/DAppDir` (folder with the management window, `aster.exe`, and the runtime), `/DAppVersion` (version shown to the user), `/DAppNumVersion` (numeric version), and `/DOutputDir` (folder for the setup `.exe`). Compile it with `ISCC.exe /DAppDir=<gui publish path> /DAppVersion=<version> aster.iss`; the release workflow does the same.

## Distribution and CI

- `.github/workflows/csharp-ci.yml`: windows-latest, .NET 10.0.x, restore, build, test (without Live), format.
- `.github/workflows/csharp-release.yml`: manual run (`workflow_dispatch`, `version` input, default `0.9.0-beta.3`, runner windows-2022). It runs verify → package (management-window zip `AsterCache-<version>-win-x64.zip`, Inno Setup installer) → release (creates the GitHub Release; a version containing `-` is marked as a pre-release; the zip and the installer `.exe` are attached as assets). There is no code signing (only the unsigned-publisher warning).

## Performance conclusions (reference measurements)

- On the relocated zone the Python stream takes 1.42 s, and the upper bound of reading the same hot bytes as one file is 1.09 s (see `results/prefetch_methods_20261102` and `results/prefetch_sweep_20261101/contig_upper_bound.json`).
- The hot-set reduction option gave no gain and was not adopted.
- Request coalescing breaks the read order and was not adopted. The window-batch option (S5) remains not adopted.
- Game start detection: polling by default (`"watcher": "poll"` in `config.json`, interval `poll_ms` default 50 ms). It gets only the PID list (`K32EnumProcesses`, about 0.05 ms per call) and looks up the image name of new PIDs only, which is about 1% of one core when idle, and in a 20-run test the detection latency had median 31 ms and p95 47 ms (50 ms interval; a full Toolhelp snapshot takes about 10 ms per call, so at the same interval it would be 25%). `"watcher": "etw"` is used only with administrator rights (it falls back to polling on failure), and because the live session buffers are flushed on a seconds scale, the measured S4 latency before the change was about 1.65 seconds. With the session buffer flushed with `FlushTrace` every 50 ms, the S4 measurement is p95 119 ms (VALIDATION_REPORT, item D; the S4 polling values are median 27 ms, p95 58 ms). The 20-run test above is a separate measurement, not the S4 value. The millisecond flush flag (`EVENT_TRACE_USE_MS_FLUSH_TIMER`) is not in the public SDK header (10.0.26100), so it is not used. Limits of polling: it cannot see a process that starts and ends between two ticks, nor a PID reused between ticks. The `started (pid N) via poll|etw-raw|etw-tdh` in the log shows which path caught it.

| Watcher | How it works | Permission | Detection latency (S4 measurement) | Load | Recommended |
|---|---|---|---|---|---|
| `poll` | Reads the PID list every 50 ms and looks up the names of new PIDs only | No administrator rights | median 27 ms, p95 58 ms | About 1% of one core when idle | Default and recommended |
| `etw` | Subscribes to kernel process events in a real-time session and flushes the buffer every 50 ms with `FlushTrace` | Administrator rights (the service runs as LocalSystem) | p95 119 ms (1.65 s before the 50 ms flush) | Session buffer memory | Falls back to polling if it fails |

## Limits and unverified items

- S1~S6 and the Live tests have not been run. They need administrator rights and the real D: disk.
- The non-administrator run check (stopping at the first step) was not done, following the instruction not to run it. Only static checks were done.
- Performance numbers are based on an unbuffered-proxy cold read without administrator rights, one suspected-SMR drive, and synthetic data. Persistence after 24 hours was not measured because of cache effects right after the move.
- The TraceEvent 3.0.2 kernel parser fields (`FileIORead`: `Offset` `IoSize` `FileKey` `FileObject` `ProcessID`; `FileIOName/FileCreate/FileRundown`: `FileKey` `FileName`; `FileIOCreate`: `FileObject` `FileName`; `ProcessStart`: `KernelImageFileName` `CommandLine` `ParentID`) were checked for name and type by assembly reflection, and `ProcessID` and `ImageName` of `Microsoft-Windows-Kernel-Process` event 1, which the service reads, were checked against the manifest registered on the test PC. Actual .etl conversion is checked with `aster trace --etl <file> --exe <name> --origin-root <root> --out <file> --dump-first 20` (a one-line summary and the first 20 reads). The live behavior of the private kernel session, whether FileIO reads include cache hits, and the synchronicity of PrefetchVirtualMemory were not confirmed.
- ETW capture buffer: the live session (`LiveSession`) opens with 256 MB buffers (increased after millions of lost events were observed in an .etl captured with WPR; if it cannot open them, it retries once with 64 MB), and the statistics line of `trace --live`/`--etl` records `'events_lost': N` (the trace JSON format is unchanged). The WPR capture in `cs_etw_check.ps1` uses `scripts\aster_fileio.wprp` (FileIO, 1 MB × 512 buffers, file mode).
- Upper bound on read recall: of 4,000 Python-replayed reads in a real .etl, the FileIO read events were about 2,811 application-level IRPs (`IoFlags` 0 or 0x60900) and 1,244 paging reads from the cache manager (0x60403). The paging reads come out once more about 50 µs later at the same offset, which causes the duplicate blocks (1,358 versus 284 in the baseline) (this is not Init/Complete double counting: Complete is a separate `FileIOOperationEnd`), and reads served from the cache (FastIO) produce no event, so the distinct blocks reached only about 72.6% of the baseline. A block-level union removes the duplicates, but cache-hit reads cannot be obtained from ETW, so you must clear the cache before capture or collect with unbuffered replay.
- Known limit: names of FileKeys that disappeared through file deletion (`FileIOFileDelete`) are not handled.
- File names prefer the FileKey (the cleanup event arrives at the end of the session) and use the FileObject name at read time as a fallback, because FileObject addresses are reused. In a real .etl from the WPR FileIO profile, large event loss (3.8M in one capture) drops some reads, so the recall of this .etl conversion cannot be evaluated separately from capture loss.
- The effect of ETW capture and a resident service on anti-cheat games (Ricochet, Vanguard) has not been verified. Check first with a game that has no anti-cheat.
- A Velopack update changes only a tray installed with Velopack, and an installer install is updated by running the new installer (see "Tray updates and the service" above). An actual Velopack install update test has not been done.
- The WDK install size and the winget package ID were not checked.
- The kernel minifilter (AsterFlt) is at the source stage and is not loaded on the test PC. Load verification is covered only by the VM procedure.
