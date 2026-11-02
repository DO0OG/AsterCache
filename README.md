**English** | [한국어](README.ko.md)

<p align="center"><img src="csharp/src/AsterCache.Tray/Assets/aster.png" width="128" alt="AsterCache icon"></p>

<h1 align="center">AsterCache</h1>

<p align="center">A Windows desktop utility that brings load times of games installed on an HDD closer to SSD speeds</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt="Platform: Windows 11 x64" src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4.svg">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4.svg">
  <a href="https://github.com/DO0OG/AsterCache/actions/workflows/csharp-ci.yml"><img alt="C# CI" src="https://github.com/DO0OG/AsterCache/actions/workflows/csharp-ci.yml/badge.svg"></a>
  <a href="https://github.com/DO0OG/AsterCache/actions/workflows/kernel-build.yml"><img alt="Kernel Build" src="https://github.com/DO0OG/AsterCache/actions/workflows/kernel-build.yml/badge.svg"></a>
</p>

> **Beta.** AsterCache is distributed as a beta pre-release. It physically moves file data, so back up drives that hold important data before using it. Validation covers one SMR drive (WD20EZAZ) and one real game (Frostpunk); CMR drives and anti-cheat games have not been tested (see [Known limitations](#known-limitations)).

## Table of contents

- [What it does](#what-it-does)
- [Features](#features)
- [Measured results](#measured-results)
- [Screenshots](#screenshots)
- [System requirements](#system-requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Configuration](#configuration)
- [Safety measures](#safety-measures)
- [Known limitations](#known-limitations)
- [Troubleshooting](#troubleshooting)
- [Project layout](#project-layout)
- [Development](#development)
- [License](#license)
- [Disclaimer](#disclaimer)

## What it does

AsterCache records which file blocks a game reads while loading, then gathers those blocks into one zone of the drive in that read order. The goal is that the drive head no longer has to jump between scattered locations when the game reads the same data. Relocation moves file data in place inside NTFS; it makes no copies and loads no driver.

There are two ways to read the relocated zone.

1. **Concurrent prefetch.** The moment the game exe starts, the relocated zone is read in access order. This is bound by the HDD's sequential read speed.
2. **Prewarm.** Before you start the game, the same zone is loaded into RAM (the file cache). It runs automatically when the service starts, or when a trigger program such as a launcher you specify starts. When the game starts, it reads almost nothing from disk.

Measurements used a synthetic dataset and a replay of the traced read pattern of one real game (Frostpunk, no anti-cheat). Total game load time and the distribution of repeated load times have not been measured yet. See [Measured results](#measured-results) and [VALIDATION_REPORT.md](VALIDATION_REPORT.md) for details.

## Features

- **Drive list.** Shows HDD/SSD type, capacity, model, and the number of registered games.
- **Game registration.** Set the game name, exe name, install folder, allowed folders, and volume label, and specify a prewarm trigger program.
- **Trace capture.** Run the game once and load up to the first map; the read blocks are recorded with a live ETW session (administrator rights required).
- **Plan, relocate, revert.** Builds a layout plan from the trace and applies it. The apply journal can be replayed in reverse to return to the previous layout.
- **Prewarm.** Runs automatically (on service start, or when a trigger program launches) or on demand with "Prewarm now".
- **Service.** Detects game launches to run concurrent prefetch, and manages prewarm and re-apply. Runs as a Windows service (LocalSystem, automatic start).
- **Status and logs.** Tabs show service and watcher state, the last prewarm result, per-game layout state, and logs.
- **Measurement.** Measures cold replay time per game before and after relocation.
- **Management window.** Without administrator rights a banner appears at the top, and you can relaunch as administrator.
- **Command line `aster`.** Runs the same operations as the management window as commands, for automation.
- **English and Korean UI.** The management window and the CLI support both languages (see [Language](#language)).

## Measured results

The figures are replay times of traced read patterns, not total load times of a running game.

| Target | Measurement | Result |
|---|---|---|
| Synthetic dataset 2.1 GiB | HDD cold read → prefetch after relocation | 17.1 s → 1.14 s (holds 10 hours after relocation) |
| Synthetic dataset 2.1 GiB | SSD cold read under the same conditions | 0.76~1.22 s |
| Frostpunk, 2.02 GB read while loading the first map | HDD cold replay → concurrent prefetch after relocation | 24.3 s → 15.1 s (HDD sequential read limit) |
| Frostpunk | Replay after prewarm | 0.48 s |
| Frostpunk, same read pattern | NVMe SSD cold replay | 3.8 s |
| Service | Game start detection latency (polling, p95) | 58 ms |

- The HDD is a WD20EZAZ (SATA, D:) and the SSD is NVMe (C:). The test machine runs Windows 11 Pro 10.0.26200 with 32 GiB of memory.
- The Frostpunk results come from tracing one game once and replaying it. There are no p50/p95 figures for repeated loads yet.
- The SSD value for the synthetic dataset and the NVMe value for Frostpunk use different data and methods, so do not compare them directly.
- For prewarm to beat an SSD cold read, prewarm must finish at least 15 s before the game starts. Memory pressure reduces the effect.
- More tables and interpretation are in [docs/RESULTS_SUMMARY.md](docs/RESULTS_SUMMARY.md) and [VALIDATION_REPORT.md](VALIDATION_REPORT.md).

## Screenshots

The management window (Games tab) and the Status tab. They were produced with `AsterCache.Tray.exe --smoke --screenshot <png>`, so the drive information is sample data.

<p align="center"><img src="docs/images/main-window.en.png" width="800" alt="Management window, Games tab"></p>
<p align="center"><img src="docs/images/status-tab.en.png" width="800" alt="Management window, Status tab"></p>

## System requirements

- Windows 11 x64. The validated environment is Windows 11 Pro 10.0.26200.
- Building from source requires the .NET 10 SDK. The repository's `csharp/global.json` uses SDK 10.0.400 (latestPatch).
- Trace capture (ETW), relocation (`apply`, `revert`), and service installation require administrator rights.
- The relocation target must be an NTFS volume. Comparing origin against cache requires two different physical devices.

## Installation

### 1. Release (beta)

Download `AsterCache-0.9.0-beta.1-win-x64.zip` from the **v0.9.0-beta.1** pre-release on [GitHub Releases](https://github.com/DO0OG/AsterCache/releases). It contains `AsterCache.Tray.exe` (management window), `aster.exe` (command line), and `AsterCache.Service.exe` (service). Extract it, run `AsterCache.Tray.exe` as administrator, and install and start the service from the Settings tab.

### 2. Installer

You can build an installer from `csharp/packaging/windows/aster.iss` (Inno Setup 6). It installs the service files, registers the `AsterCache` service for automatic start, and sets permissions on the data folder. The release workflow builds it and attaches the installer `.exe` to the GitHub release, or you can build it yourself:

```bat
ISCC.exe /DServiceDir=<service publish path> /DAppVersion=<version> csharp\packaging\windows\aster.iss
```

### 3. Build from source

Publish the three executables with the .NET 10 SDK. The output goes under `csharp\publish\` and is ignored by Git.

```bat
dotnet publish csharp\src\AsterCache.Cli\AsterCache.Cli.csproj -c Release -r win-x64 --self-contained -o csharp\publish\cli
dotnet publish csharp\src\AsterCache.Service\AsterCache.Service.csproj -c Release -r win-x64 --self-contained -o csharp\publish\service
dotnet publish csharp\src\AsterCache.Tray\AsterCache.Tray.csproj -c Release -r win-x64 --self-contained -o csharp\publish\gui
```

- `csharp\publish\cli\aster.exe` is the command-line tool.
- `csharp\publish\gui\AsterCache.Tray.exe` is the management window.
- Install the service from an administrator PowerShell.

```powershell
csharp\scripts\cs_service.ps1 -Action install
```

## Quick start

### With the management window

1. Run the management window as administrator. If you started it without administrator rights, relaunch from the banner at the top.
2. Register a game on the Games tab. Specify the exe name and install folder.
3. Capture a trace. Run the game once and load up to the first map.
4. Build a plan from the captured trace.
5. Relocate the plan. Reverting is done on the same tab.
6. Install and start the service on the Settings tab. Prewarm options are set in the game's settings.

### With the command line `aster`

```bat
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster verify --plan plan.json
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

- Instead of `--seconds` you can pass `--stop-file <file>` and create that file to end the capture.
- Run `trace --live` and `apply --execute` from an administrator PowerShell.
- `--dry-run` moves nothing and only shows the plan.
- If `--require-volume-label` does not match the actual volume label, nothing is moved.
- `aster prewarm` is an alias of `aster prefetch --execute --method pvm`.
- The `--zone-mib` value is an example. Set it to fit the game's hot data size.
- The global option `--lang ko|en` sets the output language (see [Language](#language)).

## How it works

- **Access-order physical relocation.** Hot blocks are moved in place inside NTFS (FSCTL_MOVE_FILE) in the traced read order to gather them into one zone. SHA-256 before and after the move confirms the content is identical.
- **Zone-ordered stream.** The relocated zone is read in access order, close to sequential. It starts the moment the game starts, and the data read is placed in the OS file cache.
- **Prewarm.** Before you start the game, the same zone is loaded into the RAM file cache. It runs only when available RAM is at least the zone size plus `min_free_ram_mib`.

## Configuration

The configuration file is `%ProgramData%\AsterCache\config.json`. Editing it in the management window is recommended. When editing by hand, write JSON backslashes as `\\`.

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
| `max_stream_mib` | Upper limit of what one stream or prewarm reads (default 4096) |
| `prewarm.on_service_start` | Run prewarm when the service starts (default `true`) |
| `prewarm.trigger_exes` | Run prewarm when one of these programs starts (for example a launcher). Empty by default |
| `prewarm.min_free_ram_mib` | Available RAM to leave free even after prewarm (default 4096) |
| `prewarm.cooldown_min` | Minimum interval between prewarms (default 30 minutes) |

The service reads the configuration only when it starts. After adding or removing a game, restart the service.

### Language

The management window and the `aster` CLI are available in Korean and English.

- The language is chosen by `"language"` in `config.json`: `"auto"` (default, follows the Windows display language and uses English unless it is Korean), `"ko"`, or `"en"`. You can also change it in the **Language** item on the management window's Settings tab.
- The CLI accepts the global option `--lang ko|en` and the environment variable `ASTER_LANG`. Priority: `--lang` > `ASTER_LANG` > `config.json` > OS language.
- JSON output does not depend on the language.

## Safety measures

- **Content verification.** SHA-256 is compared before and after relocation. If the content differs, it is recorded as a failure.
- **Journal and revert.** A journal (JSON Lines) is written for every moved fragment, and `revert` returns to the previous physical layout. The data content does not change.
- **Allowed folders and volume pinning.** Files outside `allowed_roots` and volumes whose label differs from `volume_label` are not touched.
- **Interruption handling.** If a game starts during relocation, only the fragment currently being moved is finished and then it stops. A resume point is left in the journal, and the same plan continues after the game exits.
- **Data folder permissions.** Only SYSTEM and Administrators can write to `%ProgramData%\AsterCache`; ordinary users can only read. Uninstalling the service leaves the data folder; it is deleted only when you explicitly pass `cs_service.ps1 -Action uninstall -PurgeData`.
- **Permission check.** Without administrator rights, relocation and revert change nothing and end with `not_admin`.

## Known limitations

- This is not a driver or file system filter. It does not intercept the game's I/O path. The kernel minifilter (`kernel/`) has been through source, build, static analysis, and INF verification, but it has not been signed, loaded, or tested on real hardware, and it is not included in the release. Automatic warm-up right after boot is handled by the user-mode service. See [kernel/README.md](kernel/README.md).
- Tracing records only reads that went to disk. Reads that were already in the cache may be missed.
- Only one real game, Frostpunk, has been measured. Anti-cheat games (MW4, LoL, and so on) have not been validated.
- Persistence was confirmed up to 10 hours after relocation. Beyond 24 hours has not been measured.
- Measured on a single SMR drive (WD20EZAZ). CMR drives have not been measured.
- The cold condition was produced by clearing the standby list with administrator rights. The drive's internal cache was not cleared.
- Relocation actually moves files, so the first run takes a long time. The gain shows up in repeated loads after relocation.
- Power loss, SSD damage, BitLocker, and DirectStorage environments have not been tested.
- Release binaries are not code-signed, so Windows may show an unsigned-publisher warning.
- If only the tray is updated, the service stays on the old version. The tray shows the version mismatch, and you must reinstall the service with the installer.

## Troubleshooting

**It says you do not have permission to save settings.**
The data folder is writable only by administrators. Relaunch the management window as administrator.

**I added or removed a game but it was not applied.**
The service reads the configuration only when it starts. Stop the service on the Settings tab and start it again.

**The tray shows "Service reinstall required".**
A tray update does not replace the service executable. Reinstall the service with the installer.

**A relocation command ends with `not_admin`.**
Run relocation, revert, and volume commands from an administrator PowerShell.

**Prewarm did not run.**
The reason it was skipped is recorded in `service.log`. Typical causes are insufficient available RAM, the game already running, an active cooldown, or a missing plan file. You can also check the `prewarm` entry of `aster status`.

## Project layout

- `csharp/` — the final C# implementation: command-line tool `aster`, service, management window, installer script, and tests
- `kernel/` — source of the AsterFlt kernel minifilter. Experimental; it is not loaded.
- `prefetch/`, `relayout/`, `bench/` — Python reference implementation (prefetch, relocation, and measurement tools). It is the cross-validation baseline for the C# code. For commands and layout see [docs/PYTHON_TOOLS.md](docs/PYTHON_TOOLS.md).
- `scripts/` — experimental administrator PowerShell scripts for the reference implementation
- `docs/` — design documents, game guide, and results summary
- `tests/` — unit and regression tests of the Python reference implementation
- `results/` — measurement result JSON and logs
- `.github/workflows/` — C# build and test workflows and the kernel build workflow
- `*.py` in the root — older versions, the SQLite cache reference implementation, and test and measurement scripts
- `VALIDATION_REPORT.md` — validation report

## Development

```bat
dotnet build csharp/AsterCache.sln -c Release
dotnet test csharp/AsterCache.sln -c Release --filter "Category!=Live"
dotnet format csharp/AsterCache.sln --verify-no-changes
py -3.12 -m unittest discover -s tests
```

- `Category=Live` tests need `D:\AsterBench` and the Python reference implementation, so they are excluded above.
- The Python reference implementation requires Python 3.11 or later and has no external packages. It was validated on Python 3.11.4 and 3.12.10. For commands and layout see [docs/PYTHON_TOOLS.md](docs/PYTHON_TOOLS.md).

## License

MIT License. See the [LICENSE](LICENSE) file for details. Copyright (c) 2026 DO0OG.

## Disclaimer

- It has not been validated on games with anti-cheat or games tied to online accounts.
- Relocation actually moves file data. Back up before using it on a drive that holds important data, and try it on a test folder first.
- Provided without warranty under the MIT license. The measurements are results from a single PC.
