**English** | [한국어](USAGE.ko.md)

# Feature list and manual use

> The feature list moved from the README, and how to drive each step by hand without automatic mode.

## Features

- **Drive list.** Shows HDD/SSD type, capacity, model, and the number of registered games.
- **Game registration.** Set the game name, exe name, install folder, allowed folders, and volume label, and specify a prewarm trigger program.
- **Trace capture.** Run the game once and load up to the first map; the read blocks are recorded with a live ETW session (administrator rights required).
- **Plan, relocate, revert.** Builds a layout plan from the trace and applies it. The apply journal can be replayed in reverse to return to the previous layout.
- **Prewarm.** Runs automatically (on service start, or when a trigger program launches) or on demand with "Prewarm now".
- **Automatic mode (on by default).** Finds HDD games from supported launcher libraries and by game-start detection, traces them on their first run, and relocates them while the PC is idle. See [Automatic mode](AUTO_MODE.md).
- **Service.** Detects game launches to run concurrent prefetch, and manages prewarm and re-apply. Runs as a Windows service (LocalSystem, automatic start).
- **Status and logs.** Tabs show service and watcher state, the last prewarm result, per-game layout state, and logs.
- **Measurement.** Measures cold replay time per game before and after relocation.
- **Profile export and import.** Moves a recorded trace to another PC with `aster profile export` and `aster profile import`. An import is accepted only when the file size and a sampled hash match.
- **Management window.** Without administrator rights a banner appears at the top, and you can relaunch as administrator.
- **Tray at logon.** When installed under Program Files, the installer registers a logon task named `AsterCache` that starts the tray with administrator rights, so no UAC prompt appears (a standard user account starts it with normal rights). Other install folders use the Start-folder shortcut.
- **Command line `aster`.** Runs the same operations as the management window as commands, for automation.
- **English and Korean UI.** The management window and the CLI support both languages (see [Language](CONFIGURATION.md#language)).

## Doing it by hand (optional)

Use this if you want to control each step, or to try a game that automatic mode skipped. Start the management window as administrator, open the **Games** tab, and work through the buttons in this order.

| Step | Button | What it does |
|---|---|---|
| 1 | Register | Add the game by choosing its exe file. |
| 2 | Collect trace | Run the game, load up to the first map, and quit. This records the read order. |
| 3 | Create plan | Decide where the pieces should go. |
| 4 | Apply relocation | Move the pieces. A journal is kept. |
| 5 | Prewarm now | Load the zone into RAM right away. |
| 6 | Measure | Time one read of the zone. |

The same steps are available on the command line (`aster`), for example:

```bat
aster discover
aster tier status
aster trace --live --exe Frostpunk.exe --origin-root D:\Games\Frostpunk --out trace.json --seconds 180
aster plan --trace trace.json --origin-root D:\Games\Frostpunk --zone-mib 1536 --out plan.json
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --dry-run
aster apply --plan plan.json --allowed-root D:\Games --require-volume-label GAMES --execute
aster prewarm --plan plan.json --origin-root D:\Games\Frostpunk
```

- `discover` lists the games found in your launcher libraries, read only.
- `tier status` shows the SSD assist state. `tier apply --game <name>`, `tier revert --game <name>` or `--all`, and `tier clean` need administrator rights.
- Run `trace --live` and `apply --execute` from an administrator PowerShell.
- `--dry-run` moves nothing and only shows the plan. If `--require-volume-label` does not match the actual volume label, nothing is moved.
- `--zone-mib` is an example value; set it to fit the game's hot data size. The global option `--lang ko|en` sets the output language (see [Language](CONFIGURATION.md#language)).
- Every command and option is described in [csharp/README.md](../csharp/README.md).
