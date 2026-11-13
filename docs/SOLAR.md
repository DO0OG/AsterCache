**English** | [한국어](SOLAR.ko.md)

# SOLAR: keeping the cache and its eviction order

> Details that continue from [How it works](../README.md#how-it-works) in the README. SOLAR stands for Standby Order LAdder & Refresh.

SOLAR works on data that a game's prewarm has already read into RAM. It does not read from the disk on purpose. It touches that data again in memory, so that the data a load needs first is the last to be pushed out of the cache, and so that the cache is kept up without going back to the HDD. It is on by default (`memory.standby_ladder`).

## Why it exists

Prewarmed data is ordinary file cache. Windows keeps cache pages that no program is using in its standby list and reuses them when memory is needed. On the test PC the file cache, prewarm cache included, sat at priority 5 of the list's priorities 0 to 7, and pages at the same priority were reused in the order they entered the list (see [the measurement record](#measurement-record)). Two things follow.

- **The cache cools.** Reads and memory use of other programs push cache pages out. In the service log of the test PC, the median time from a complete prewarm to the first re-prewarm check that found the cache cold was 29.0 minutes for League of Legends and 71.5 minutes for Call of Duty. Checks run about every 10 minutes, so these are upper bounds on when the cache cooled. Since 2026-11-11, 47% (League of Legends) and 71% (Call of Duty) of the re-prewarm checks found the cache at least partly cold. A cold cache is read from the HDD again.
- **The most important data goes first.** Prewarm reads the data every load needs (the common blocks and the loading head) first. Pages read first enter the list first, so the most important data is also the first to be reused when memory runs short.

SOLAR answers both without reading the disk on purpose. It touches the cache again in the reverse of the read order, so the most important data enters the list last and leaves it last, and it does this again before the pages ahead of them have been used up.

## How it decides

1. **Make the ladder.** When a prewarm of a game has read its list to the end, SOLAR touches what that game has in the cache in the reverse of the read order: the later part of the list first, and the loading head and the common blocks last. A touched page goes to the back of the standby list, so the order of eviction becomes the reverse of the order of importance. The importance order is the one the prewarm already uses, which comes from the game's own recorded reads.
2. **Make a note.** After a ladder it notes the time, Windows' running count of cache pages that have been reused, and the amount ahead: the cache in the standby list minus what this game touched. The note lives in memory only.
3. **Watch one statistic.** About every 30 seconds, when a game has a note, SOLAR reads Windows' standby-list statistics once. It is a system-wide figure and no process is opened. Pages ahead of the game's pages are reused first, so when the count of reused cache pages has grown since the note by `max(512 MiB, 0.4 x the amount ahead)`, the game's pages are getting close to the front.
4. **Refresh without the disk.** Before a refresh, the usual sample check runs (1 MiB at each of 8 places). If the samples are warm, SOLAR touches the same ladder again, which moves the pages to the back of the list. If they are cold or cannot be read, the note is dropped and the [re-prewarm check](AUTO_MODE.md#5-upkeep) takes over. When several games are due, the lowest-ranked one is touched first and the most recently prewarmed one last.
5. **Stop when the disk is read.** A page that is no longer in the cache would be read from the disk by the touch. After each view, SOLAR compares how many pages the whole system has read from the disk meanwhile and stops once more than 32 MiB is counted. Then the note is dropped and the game is flagged for the next re-prewarm check, which re-prewarms it even if its samples look warm. Parts still in the cache cost almost no disk time, so in effect only the missing parts are read. At most one such re-prewarm per game is made each `auto.rewarm_minutes`, counted from the last prewarm attempt.

If Windows' statistics cannot be read (for example because the privilege is missing), step 3 falls back to a refresh every 10 minutes after the last ladder, with no other change.

<details>
<summary>Details: the numbers, how a touch is made, how disk reads are told</summary>

- **How a touch is made.** Each range is mapped as its own read-only view of at most 64 MiB, read 1 MiB at a time from inside the service's own process, and unmapped at once. Nothing is locked and no mapping is kept. Up to 64 files are open at a time. The result is the same as if a program had read the pages normally.
- **Why a view per range.** On the test PC, a file's ranges left the cache in the order they were touched only when each range had its own view that was released right after. With one view for all ranges they left in address order (experiments E10 and E11).
- **Where 0.4 and 512 MiB come from.** In the eviction experiment (E9) the first untouched file left the cache when the reuse count had grown by 0.61 to 0.81 times the amount ahead. Some pages ahead are used again and drop out of the way, so the real moment comes earlier than a plain count suggests, and a margin was added. The lower bound of 512 MiB keeps a small amount ahead from triggering refreshes too often. Both values come from that one experiment and have not been tuned further.
- **How disk reads are told.** The system's page-read count is read before and after each view. The increase, minus an allowance for other programs (2 pages and about 8 MiB per second), counts as read from the disk, at most the size of the view. More than 32 MiB counted ends the touch, so in the worst case about 100 MiB is read from the disk before it stops (the 32 MiB counted, plus the view being touched). If the count cannot be read, a view that takes more than about 256 ms per 64 MiB counts as read from the disk. A looser safety net also stops the touch when pieces that took more than 20 ms per MiB add up to more than 2 seconds.
- **What is touched.** The list is the common blocks that the prewarm reads first followed by the whole list that was read, in read order; the ladder is its reverse. For a prewarm that read only the front part, it is the part that was read. A range that continues the previous one in the same file is joined to it.
- **Where the first ladder comes from.** After a prewarm stream of the game has finished to the end (service start, launcher, re-prewarm, top-up). A stream that ended at the memory limit does not make one at once; the first ladder is made at a re-prewarm check whose samples are warm. After a service restart the notes are gone, so each game gets a new ladder at the first such check.

</details>

## When it runs

All of these must hold. Otherwise nothing is touched.

| Requirement | Detail |
|---|---|
| Setting | `memory.standby_ladder` is `true` (the default). |
| Service rights | The service has administrator rights and a trusted data folder, the same condition as [background memory yield](AUTO_MODE.md#background-memory-yield). |
| No game running on that disk | A touch that misses the cache is a disk read, so it follows the rule that no new background reads go to a disk where a game runs. It stops when a game starts. |
| Not while a launcher prewarm waits | A launcher's prewarm has priority. SOLAR also skips a stream that was stopped for it. |
| Refresh gates | The same gates as the re-prewarm check: no relocation in progress, the game drive under 4 MiB/s, the exit grace period over, the game inside the prewarm budget, and the disk read lease granted. |
| Not with `auto.keep_stream_on_game_start` | With this experimental setting a stream is not stopped when the game starts, so SOLAR does not follow it. |

## What it does not do

- It does not keep data in memory by force: no lock, no kept mapping (unlike the optional [Pinning](AUTO_MODE.md#pinning-optional)), and no change of page priority. Windows can still reuse these pages when memory is short; SOLAR only changes which ones go first.
- It does not run on a disk where a game is running.
- It does not lower the memory reserve (`prewarm.min_free_ram_mib`) or the cache budget (`prewarm.max_cache_mib`), and it changes neither the prewarm list nor the amount read.
- It does not open any game, launcher, or anti-cheat process, and it opens no process at all. See [Anti-cheat compatibility](ANTICHEAT.md).

## Turning it off

Add this to `config.json`:

```json
"memory": { "standby_ladder": false }
```

There is no switch in the Settings tab. The service re-reads the file and then stops touching the cache. Nothing else changes: the prewarm, the re-prewarm check, and [background memory yield](AUTO_MODE.md#background-memory-yield) work as before and have their own settings. The `memory.standby_ladder` key is newer than 1.0.3, and versions 1.0.3 and earlier reject a settings file that contains it, so remove it before rolling back ([Configuration](CONFIGURATION.md)).

## Checking that it runs

Each line goes to `service.log`. `<game>` is the game's name, and a tail such as `, N MiB read from disk` or `, N errors` is added only when it is not zero.

- `<game>: ladder ordered N MiB in S s`: a ladder was made after a prewarm or a re-prewarm check.
- `<game>: cache refreshed in memory: N MiB in S s (R MiB repurposed of A MiB ahead)`: a refresh. `R` is the growth of the reuse count and `A` the amount ahead.
- `<game>: ladder found data missing from memory (K slow pieces, N MiB read from disk), left to the rewarm check`, and the same for `cache refresh found data missing from memory (...)`: part of the list was not in the cache.
- `<game>: rewarm check: part of the list is not in memory (found by the ladder), prewarming again (cooldown ignored)`: the next re-prewarm check re-prewarms the game. `..., waiting until N min after the last prewarm attempt` means it waits for the period to pass.
- `<game>: cache refresh skipped, the samples are cold (X/Y over Z ms), left to the rewarm check`, or `the samples could not be read` or `there are no readable samples`: the sample check came before the refresh.
- `<game>: ladder stopped after N MiB`, `cache refresh stopped after N MiB`: stopped because a game started or the lease ended; the note stays and a refresh is tried again.
- `<game>: ladder was too slow without disk reads seen (K slow pieces), skipped`, `cache refresh was too slow without disk reads seen (...)`: the touch took long but no disk read was seen, for example under heavy CPU load.
- `<game>: ladder skipped: <exception type>` and `<game>: cache refresh failed: <exception type>` (or `cache ladder failed`): a failure that did not affect the prewarm.

## Limits and cautions

- **The effect in real play has not been measured.** The experiments show that the order can be set and that refreshing cache that is still in memory needs no disk read. They do not show how much load time or stutter changes in a running game. Whether the share of cold re-prewarm checks has fallen has not been checked either.
- **It does not stop eviction.** When memory is truly short, Windows still reuses these pages. SOLAR changes only which ones go first and how soon they come back.
- **A very long prewarm can lose its own start.** Once, a prewarm of 11,388 MiB for Call of Duty took 322 seconds, and the ladder made right after it already found data missing. The list was close to the capacity of the standby list, so the part read first seems to have been pushed out while the rest was being read; other programs' reads may have added to it.
- **The 8-place sample check can miss a partly cold cache.** The ladder looks at the whole list, which is why a ladder that finds missing parts leads to a re-prewarm even when the samples look warm.
- **The statistic is an undocumented Windows interface.** It was checked on Windows 11 10.0.26200 only. If it cannot be read, the 10-minute fallback is used.
- **The eviction order was checked with test files on one HDD,** for pages of priority 5 only. Raising the page priority to 6 or 7 was refused by Windows (`STATUS_INVALID_PARAMETER`), so it is not used.
- Games on an SSD are skipped by the prewarm, so SOLAR has nothing to do for them.

## Measurement record

### The cache cools (service log)

Service log of the test PC, 2026-11-11 onward (12 recorded sessions, 2 games). The checks run about every 10 minutes.

| Game | Complete prewarms | Followed by a cold check | Minutes to the first cold check (minimum / median / maximum) | Cold or partly cold checks |
|---|---|---|---|---|
| League of Legends | 45 | 27 | 14.0 / 29.0 / 235.2 | 47% (34 of 72) |
| Call of Duty | 12 | 8 | 24.5 / 71.5 / 202.3 | 71% (57 of 80) |

### Experiments on the test PC (HDD D:, 32 GiB of memory, Windows 11 10.0.26200)

These use test files and an experiment tool, not the service and not a running game.

| Question | Experiment | Result |
|---|---|---|
| Does a touched file stay longer than an untouched one? | E9: eight 256 MiB test files; one read once, one touched again every 5 minutes | The one touched every 5 minutes stayed at 100% to the end of the 40 minutes (the count of reused cache pages reached about 29 GiB). The one read once left when that count was 7.0 to 9.2 GiB, which is 0.61 to 0.81 times the amount ahead (about 11.4 GiB). |
| Do files leave in the order they were put in? | E9: files read with `PrefetchVirtualMemory` in turn, and files touched while cached in turn | They left in the order they entered the list. At +28 minutes the files read first were at 7%, 5%, 2%, and 17% resident while the three touched last were at 92%, 100%, and 100%. The files that were only read and never touched again left before the ones touched later. |
| Can the order inside one file be set? | E10 and E11: one file, three ranges touched in the order C, A, B | With a view per range released at once, they left in the order C, A, B (at +220 s C was at 65% and A and B at 100%; at +250 s C and A were at 5% and B at 100%). With one view for all ranges, they left together in address order. |
| Can a disk read be told from a cache hit? | E12: 256 MiB test file touched through a 64 MiB view | Not in cache: 2.74 s and the system page-read count rose by 66,115 pages (258 MiB, the amount read). In cache: 0.13 to 0.15 s and a rise of 65 to 128 pages. Idle for 2 s: 449 pages. |
| Can page priority 6 or 7 be set? | E4 | No, refused with `STATUS_INVALID_PARAMETER`. |

### Installed build (service log, development build 1.0.4-dev.4, 2026-11-14)

- A ladder of 1,163 MiB was made in 0.5 s right after a re-prewarm, with no disk read.
- Fourteen minutes later the reuse count had grown by 4,860 MiB against 11,422 MiB ahead, above the threshold of 0.4 x 11,422 = 4,569 MiB, and the cache was refreshed: 1,163 MiB in 0.6 s, with no disk read.
- At 03:34 (and again at 03:44 and 03:54) the sample check said `warm (0/8)` for both games while the ladder found parts missing (37 and 21 MiB were read from the disk at 03:34). This is why a missing part now leads to a re-prewarm.

## Prior art

Each technique in SOLAR has an earlier precedent. Putting a cache's replacement bookkeeping into a chosen state by reading data in a set pattern from user level was done by Dust ([Burnett et al., USENIX 2002](https://static.usenix.org/event/usenix02/full_papers/burnett/burnett_html/index.html)), there to identify the replacement policy. Touching files periodically to keep them cached is what [vmtouch](https://linux.die.net/man/8/vmtouch) with a scheduled job does. Releasing the memory of background programs only under memory pressure is close to [SmartTrim in Process Lasso](https://bitsum.com/apps/process-lasso/docs/algorithms/smarttrim/), and Windows itself ranks standby pages by value ([SuperFetch](https://learn.microsoft.com/en-us/previous-versions/technet-magazine/cc162480(v=msdn.10))).

What is specific to this program is the combination: one control loop that uses a Windows statistic of the standby list per priority as its signal to decide the eviction order, the timing of the keeping, and the yielding of background memory ([background memory yield](AUTO_MODE.md#background-memory-yield) uses the same statistics), and an importance order that comes from each game's own recorded reads. A search on 2026-11-14 did not find a product, paper, patent, or public code that does the same combination. That is a statement about the search, not proof that none exists. The comparison and the sources are in [prior_art_and_windows_api.md](prior_art_and_windows_api.md#f-solar-선행-기술-캐시-유지-순서-배경-양보).

## Related

- [Automatic mode](AUTO_MODE.md#solar-keeping-the-cache-in-order): where SOLAR sits among the stages.
- [Configuration](CONFIGURATION.md): `memory.standby_ladder`.
- [FAQ](FAQ.md): what "the cache cools" means and how to turn SOLAR off.
- [ORBIT](ORBIT.md): the order in which the prewarm reads, which the ladder reverses.
