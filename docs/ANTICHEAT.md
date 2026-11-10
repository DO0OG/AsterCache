**English** | [한국어](ANTICHEAT.ko.md)

# Anti-cheat compatibility

> Details that continue from [Safety measures](../README.md#safety-measures) in the README.

Many current games run kernel-level anti-cheat. This section says what AsterCache does and does not do, so you can decide whether to use it with a game. Anti-cheat policies differ per game and can change. AsterCache has not been tested with any anti-cheat game yet.

## What it does

- The service reads game files from inside its own process, read-only, with shared access (read, write, and delete sharing), or only maps them.
- First-run read-ahead also reads files from the service's own process. The game process is not touched; reads are only observed through ETW.
- It watches file-read events with a live ETW kernel session. It needs no handle to the game process.
- It reads Windows prefetch records (`C:\Windows\Prefetch\*.pf`) with read-only access, from the service only, and downloads one shared-profile zip a week over HTTPS. Neither touches the game process.
- It moves file clusters with the NTFS defragmentation API (`FSCTL_MOVE_FILE`), and only after the game has fully exited. File content, size, and modified time do not change. Before a move, the plan's fingerprint (size and modified time) is checked.

## What it does not do

- No code injection, no thread creation in the game process, no reading or writing of its memory, no debug privilege, no hooking, no overlay, and no handle duplication.
- Game processes are opened only with `PROCESS_QUERY_LIMITED_INFORMATION`, to read the name, path, and bytes read. If a protected process refuses even that, the service decides from ETW events alone and does not try to open that process again. In that case it judges from the reads in the 20 seconds after the refusal is confirmed; reads before those 20 seconds are not counted.
- No kernel driver.

## Skipped and marked files

- Files that the game has opened exclusively are skipped.
- During automatic discovery, the service looks at file and folder names up to 4 levels deep in the install folder. It recognizes EasyAntiCheat, BattlEye, Vanguard (`vgc`, `vgk`), and Ricochet; other names containing anticheat, xigncode, nprotect, or gameguard are marked `unknown`. A game with such a name is marked in the Games tab and in the `anticheat` setting, and one log line is written. The behavior does not change. On the test PC, `aster discover` marked The Outlast Trials (Steam) with `eac`.

| Anti-cheat | Markers | Policy |
|---|---|---|
| EasyAntiCheat | `.exe` starting with `EasyAntiCheat`, `EasyAntiCheat_EOS`, `start_protected_game.exe` | No automatic trace or pin; prewarm and relocation allowed |
| BattlEye | `BattlEye`, `BEService*.exe`, `BEClient*.dll` | No automatic trace or pin; prewarm and relocation allowed |
| Vanguard | `vgc*`, `vgk*` (`.exe`, `.sys`), `Riot Vanguard` | No automatic trace or pin; prewarm and relocation allowed |
| Ricochet | Names containing `ricochet`, `randgrid.sys` | Unchanged (automatic trace allowed; not tested on a real machine) |
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

## Limits and advice

- Automatic mode is on by default. For a game with anti-cheat, turn automatic mode off in the Settings tab, or add the game's exe to `auto.ignore_exes`, before you play.
- Automatic tracing uses the same kind of ETW collection as the manual route, so the Ricochet and Vanguard cautions in [GAME_GUIDE](GAME_GUIDE.md) still apply.
- SSD assist storage skips games with anti-cheat folders, and never copies game exe or dll files.
- Not yet tested on any real anti-cheat game. This is outside the current validation.
