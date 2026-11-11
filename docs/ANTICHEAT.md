**English** | [한국어](ANTICHEAT.ko.md)

# Anti-cheat compatibility

> What AsterCache does and does not do around anti-cheat games. How it protects your data is in [Safety measures](DEVELOPMENT.md#safety-measures).

Many current games run kernel-level anti-cheat. This page says what AsterCache does and does not do, so you can decide whether to use it with a game. Anti-cheat policies differ per game and can change. There are usage records of playing a Vanguard game (League of Legends) and a Ricochet game (Call of Duty) with the installed build on one PC ([Usage records on this PC](#usage-records-on-this-pc)). This is not a controlled validation, and no guarantee is made about account sanctions.

## What it does

- The service reads game files from inside its own process, read-only, with shared access (read, write, and delete sharing), or only maps them.
- First-run read-ahead also reads files from the service's own process. The game's reads are only observed, with a live ETW kernel session that needs no handle to the game process.
- It reads Windows prefetch records (`C:\Windows\Prefetch\*.pf`) with read-only access, from the service only, and downloads one shared-profile zip a week over HTTPS. Neither touches the game process.
- It moves file clusters with the NTFS defragmentation API (`FSCTL_MOVE_FILE`), and only after the game has fully exited. File content, size, and modified time do not change. Before a move, the plan's fingerprint (size and modified time) is checked.

## What it does not do

- No code injection, no thread creation in the game process, no reading or writing of its memory, no debug privilege, no hooking, no overlay, and no handle duplication.
- Game processes are opened only with `PROCESS_QUERY_LIMITED_INFORMATION`, to read the name, path, and bytes read. If a protected process refuses even that, the service decides from ETW events alone and does not try to open that process again. In that case it judges from the reads in the 20 seconds after the refusal is confirmed; reads before those 20 seconds are not counted.
- No kernel driver.

## Skipped and marked files

- Files that the game has opened exclusively are skipped.
- During automatic discovery, the service looks at file and folder names up to 4 levels deep in the install folder. It recognizes EasyAntiCheat, BattlEye, Vanguard (`vgc`, `vgk`), and Ricochet; other names containing anticheat, xigncode, nprotect, or gameguard are marked `unknown`. A game with such a name is marked in the Games tab and in the `anticheat` setting, and one log line is written. The behavior does not change. For example, `aster discover` marked The Outlast Trials (Steam) with `eac`.

| Anti-cheat | Markers | Policy |
|---|---|---|
| EasyAntiCheat | `.exe` starting with `EasyAntiCheat`, `EasyAntiCheat_EOS`, `start_protected_game.exe` | No automatic trace or pin; prewarm and relocation allowed |
| BattlEye | `BattlEye`, `BEService*.exe`, `BEClient*.dll` | No automatic trace or pin; prewarm and relocation allowed |
| Vanguard | `vgc*`, `vgk*` (`.exe`, `.sys`), `Riot Vanguard` | No automatic trace or pin; prewarm and relocation allowed |
| Ricochet | Names containing `ricochet`, `randgrid.sys` | Automatic trace allowed; no pin; prewarm and relocation allowed |
| BlackCipher | `BlackCipher` folder, `BlackCall64.aes` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| nProtect GameGuard | `GameGuard` folder, `npgg*.dll`, `GameMon.des` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| XIGNCODE3 | `xigncode` or `XIGNCODE3` folder, `x3.xem`, `xcorona*` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| Wellbia | `uncheater.exe`, `wellbia` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| HackShield | `HShield` folder | No ETW trace, pin, or tier; prewarm and relocation allowed |
| HoYoverse mhyprot | `mhyprot2.sys`, `mhyprot3.sys` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| Denuvo Anti-Cheat | `denuvo-anti-cheat*` | No ETW trace, pin, or tier; prewarm and relocation allowed |
| FACEIT | `faceit*` | No ETW trace, pin, or tier; prewarm and relocation allowed |

## Why each speed feature is considered safe

These are the reasons each feature is considered safe. They are reasoning, not verified results.

- **Prewarm:** reads ordinary files from the service's own process.
- **Prewarm upkeep:** samples ordinary 1 MiB file reads.
- **Pinning:** read-only mappings in the service's own process, released when a game or launcher starts.
- **Relocation:** runs only after the game has exited, and file content does not change.
- **Prefetch records and shared profiles:** the `.pf` files are read only by the service, and the download is one file a week.
- **Detection:** a query-only process handle, or ETW events.

## Signing

The executables are not code-signed, so Windows SmartScreen may show a warning. That warning is the only effect confirmed so far. Whether signing status relates to anti-cheat decisions has not been checked.

## Usage records on this PC

This is a tally of the service log of one PC from 2026-11-02 to 2026-11-10. It is not a controlled validation. Whether any account was sanctioned cannot be known from the log, and no guarantee is made. Sample counts and line formats are in [RESULTS_SUMMARY.md](RESULTS_SUMMARY.md#real-world-usage-records).

- **League of Legends.** Judged as `vanguard`. The rule is a Riot game on a PC that has Vanguard; it was not found from an install-folder name. Over 10 game start detections: 2 traces collected and then 8 skipped, pin skipped 4 times, relocation completed 4 times.
- **Call of Duty.** Judged as `ricochet` from a `ricochet` name in the install folder. Over 62 game start detections: 14 trace collections started, pin skipped 7 times, relocation completed 5 times.
- Prewarm was applied to both games.

## Limits and advice

- Automatic mode is on by default. For a game with anti-cheat, turn automatic mode off in the Settings tab, or add the game's exe to `auto.ignore_exes`, before you play.
- Automatic tracing uses the same kind of ETW collection as the manual route, so the Ricochet and Vanguard cautions in [GAME_GUIDE](GAME_GUIDE.md#anti-cheat-games) (and its League of Legends reference) still apply.
- SSD assist storage skips games with anti-cheat folders, and never copies game exe or dll files.
- Other anti-cheat games have not been tested.
