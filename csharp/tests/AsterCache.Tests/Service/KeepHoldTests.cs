using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 실행 중 유지 목록 붙들기 (memory.keep_hold): 가짜 환경과 가짜 붙들기 (IKeepHold) 로 대상·순서·건너뛰기·읽기 상한·하한과 내놓기·점검·끝낼 때·예외·안티치트 게이트·설정 스위치를 확인합니다.
public sealed class KeepHoldTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _touched = [];
    private readonly HashSet<string> _running = [];
    private readonly FakeHold _hold = new();
    private readonly HashSet<string> _sparse = [];
    private readonly HashSet<string> _excluded = [];
    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private bool _keepHold = true;
    private bool _hasVolatileInfo = true;
    private int _opened;
    private long _avail = 20000;
    private long _reuse; // 대기 목록 재사용 누계 (조회마다 늘어 다시 만지기 회차가 건너뛰어지지 않음)
    private Func<long>? _availFn;
    private Action<TimeSpan, CancellationToken>? _sleep;
    private Func<IReadOnlyList<ReadOp>, TouchResult>? _touch;

    private static string F(int n) => @"D:\a\" + n + ".bin";

    private static ReadOp Op(int file, long offsetMib, long lengthMib) => new(F(file), offsetMib * Mib, lengthMib * Mib);

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    private sealed class Streamer(IReadOnlyList<ReadOp> all) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(30 * Mib, 1, 0, 0.01);

        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => all;
    }

    /// 붙들기 가짜: 이 경로의 파일은 메모리에 없음 (읽던 범위에서 경계), 내놓기·점검·닫기를 기록합니다.
    private sealed class FakeHold : IKeepHold
    {
        public readonly List<IReadOnlyList<ReadOp>> Batches = [];
        public readonly List<long> Released = [];
        public readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
        public long DiskPerMiss = 4 * Mib;
        public int Rebases;
        public int Rechecks;
        public int Disposed;
        public Exception? Throw;
        public Func<HoldCheckResult>? NextCheck;
        private long _held;

        public long HeldBytes => _held;

        public int Files => 3;

        public int Views => 3;

        public void Rebase() => Rebases++;

        public HoldBatchResult Hold(IReadOnlyList<ReadOp> batch, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Batches.Add(batch);
            long bytes = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (Missing.Contains(batch[i].Path))
                {
                    bytes += Mib; // 경계를 만나기 전에 읽은 한 조각
                    _held += bytes;
                    return new HoldBatchResult(bytes, DiskPerMiss, 0, 0, 0, 0, 1, 100, 0.1, 0.05, i);
                }

                bytes += batch[i].Length;
            }

            _held += bytes;
            return new HoldBatchResult(bytes, 0, 0, 0, 0, 1, 1, 0, 0.1, 0.05, -1);
        }

        public long Release(long bytes)
        {
            long released = Math.Min(_held, bytes);
            _held -= released;
            Released.Add(released);
            return released;
        }

        public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct)
        {
            Rechecks++;
            return NextCheck?.Invoke() ?? new HoldCheckResult(_held, 0, 0, false, [], 1, 0, 0.5, 0.2);
        }

        public void Dispose()
        {
            Disposed++;
            _held = 0;
        }
    }

    // 8 MiB 범위 여섯 개 (파일 1~5, 파일 2 는 두 번)
    private static readonly ReadOp[] Reads = [Op(1, 0, 8), Op(2, 0, 8), Op(2, 8, 8), Op(3, 0, 8), Op(4, 0, 8), Op(5, 0, 8)];

    private Prewarmer Make(IReadOnlyList<ReadOp> reads, GameSettings? game = null)
    {
        GameSettings a = game ?? Game("a");
        var streamer = new Streamer(reads);
        var p = new Prewarmer([a, Game("b")], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availFn?.Invoke() ?? _avail,
            GameRunning = g => _running.Contains(g.Name),
            ZoneBytes = g => Math.Min(30, g.StreamCapBytes(30 * Mib) / Mib) * Mib,
            TraceExists = _ => true,
            PhysicalRamBytes = () => 64L << 30,
            ProbeReadMs = _ => new ProbeResult(8, 0, 0.2),
            DiskBytesPerSecond = _ => 0,
            CommonFirstOps = (_, _, _) => ([], 0),
            ColdRead = (ops, _) => (new StreamStats(ops.Sum(o => o.Length), 1, 0, 0.01), true),
            StandbyLadderEnabled = () => true,
            Lists = () => new MemoryLists(0, _reuse += 1100 * Mib, 8000 * Mib),
            TouchRanges = (ranges, ct) =>
            {
                _touched.Add(ranges);
                return _touch?.Invoke(ranges) ?? new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
            },
            ListStamp = _ => "s1",
            IsSparse = path => _sparse.Contains(path),
            KeepHoldEnabled = () => _keepHold,
            OpenHold = () =>
            {
                _opened++;
                return _hold;
            },
            HoldExclusion = _ => _hasVolatileInfo ? path => _excluded.Contains(path) ? "volatile" : null : null,
            Sleep = (t, ct) => _sleep?.Invoke(t, ct),
        }, _log, null, () => _now);
        Assert.True(p.Run(a, "service start").Ran);
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        return p;
    }

    // 붙든 동안 ticks 번의 확인 뒤 등록 실행 파일이 끝남
    private void RunTicks(int ticks, Action<int>? each = null)
    {
        _ = _running.Add("a");
        int seen = 0;
        int oldRounds = 0;
        _sleep = (t, ct) =>
        {
            if (t == Prewarmer.PlayTouchEvery) // 붙들지 못해 지금의 다시 만지기가 도는 경우: 두 회차 뒤 끝남
            {
                if (++oldRounds >= 2)
                {
                    _ = _running.Remove("a");
                }

                return;
            }

            if (t != Prewarmer.HoldWatchEvery)
            {
                return;
            }

            seen++;
            each?.Invoke(seen);
            if (seen > ticks)
            {
                _ = _running.Remove("a");
            }
        };
    }

    private static List<(string Path, long OffsetMib)> Flat(IEnumerable<IReadOnlyList<ReadOp>> batches) =>
        [.. batches.SelectMany(b => b).Select(o => (o.Path, o.Offset / Mib))];

    // ---- 붙드는 때·대상·순서 ----

    [Fact]
    public void RightAfterTheStartTouchTheKeepListIsHeldInImportanceOrderInBatchesAndClosedWhenTheGameEnds()
    {
        Prewarmer p = Make(Reads);
        long heldInside = -1;
        RunTicks(2, tick => heldInside = tick == 1 ? p.KeepHeldMib : heldInside);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(3), 0), (F(4), 0), (F(5), 0)], Flat(_hold.Batches)); // 중요한 것부터, 16 MiB 배치 셋
        Assert.Equal(3, _hold.Batches.Count);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache held while playing (a.exe): 48 MiB of 5120 MiB target (48 MiB list) in 3 files, 3 views"));
        Assert.Equal(48, heldInside); // 붙든 동안 플레이 기록이 가용 메모리에 더해 기록할 양
        Assert.Equal(0, p.KeepHeldMib);
        Assert.True(_hold.Disposed >= 1);
        Assert.Contains("a: cache hold ended after 0 checks (a.exe): 48 MiB released at the end, peak 48 MiB of 5120 MiB target", _log.Lines.Last());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache kept while playing")); // 붙든 동안 대기 목록 다시 만지기는 돌지 않음
        Assert.True(_hold.Rebases >= 1);
    }

    [Fact]
    public void TheTargetIsTheSmallestOfTheBudgetTheRoomAboveTheFloorAndFiveGiB()
    {
        Assert.Equal(5L << 30, Prewarmer.HoldTargetBytes(20000, 4096)); // 상한 5 GiB
        Assert.Equal(1904 * Mib, Prewarmer.HoldTargetBytes(6000, 4096)); // 하한 위에 남는 양 (6000 - 4096), 예산 3000 MiB 보다 작음
        Assert.Equal(2500 * Mib, Prewarmer.HoldTargetBytes(5000, 2048)); // 예산 (가용의 절반)
        Assert.Equal(0, Prewarmer.HoldTargetBytes(4000, 4096)); // 하한 아래
    }

    [Fact]
    public void WhenTheFloorLeavesNoRoomNothingIsHeldAndTheOldTouchingStays()
    {
        Prewarmer p = Make(Reads);
        _avail = 4100;
        RunTicks(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: available 4100 MiB leaves no room above the floor 4096 MiB)"));
    }

    [Fact]
    public void AMissingStretchIsSkippedWithItsFollowingRangesOfTheSameFileAndTheListContinues()
    {
        _hold.Missing.Add(F(2));
        Prewarmer p = Make(Reads);
        RunTicks(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(3), 0), (F(4), 0), (F(5), 0)], Flat(_hold.Batches)); // 파일 2 의 두 번째 범위는 건너뛰고 뒤의 파일 3~5 를 계속 붙듦
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 33 MiB of 5120 MiB target") && l.Contains("1 missing stretches skipped (15 MiB not held)")); // 읽지 않은 나머지 7 MiB + 건너뛴 두 번째 범위 8 MiB
    }

    [Fact]
    public void TheDiskReadCapEndsTheRoundAndTheRestOfTheListIsNotHeld()
    {
        _hold.Missing.Add(F(2));
        _hold.Missing.Add(F(4));
        _hold.DiskPerMiss = 20 * Mib; // 두 번째 경계에서 40 MiB 가 읽혀 상한 32 MiB 를 넘음
        Prewarmer p = Make(Reads);
        RunTicks(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.DoesNotContain(_hold.Batches.SelectMany(b => b), o => o.Path == F(5));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.Contains("disk read cap reached"));
    }

    [Fact]
    public void HoldingStopsWhenTheAvailableMemoryReachesTheFloorWhileHolding()
    {
        // 가용 = 4196 - 붙든 MiB: 목표 100 MiB (하한 위에 남는 양), 붙든 양이 84 MiB 를 넘으면 (가용 - 16 < 하한) 다음 배치를 시작하지 않음
        ReadOp[] big = [.. Enumerable.Range(0, 20).Select(i => Op(10 + i, 0, 8))];
        Prewarmer p = Make(big);
        _availFn = () => 4196 - (_hold.HeldBytes >> 20);
        RunTicks(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(6, _hold.Batches.Count);
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 96 MiB of 100 MiB target") && l.Contains("stopped at the memory floor (4096 MiB"));
    }

    // ---- 붙들지 않는 파일 ----

    [Fact]
    public void SparseVolatileAndChangedFilesAreNotHeld()
    {
        _sparse.Add(F(3));
        _excluded.Add(F(4));
        Prewarmer p = Make(Reads);
        RunTicks(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(5), 0)], Flat(_hold.Batches));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 32 MiB") && l.Contains("8 MiB in volatile or changed files and 8 MiB in sparse files not held"));
    }

    [Fact]
    public void AGameWithoutVolatileFileInformationIsNotHeldAndTheOldTouchingStays()
    {
        _hasVolatileInfo = false;
        Prewarmer p = Make(Reads);
        RunTicks(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: no volatile file information yet, no trace was recorded)"));
        Assert.NotEmpty(_touched); // 지금의 다시 만지기
    }

    [Fact]
    public void ABlockedAntiCheatGameIsNeverHeld()
    {
        GameSettings cheat = Game("a") with { AntiCheat = "vanguard" };
        Prewarmer p = Make(Reads, cheat);
        RunTicks(1);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: anti-cheat: vanguard)"));
        Assert.NotEmpty(_touched);
    }

    [Fact]
    public void WithTheSwitchOffTheOldTouchingRunsAsBeforeAndNothingIsHeld()
    {
        _keepHold = false;
        Prewarmer p = Make(Reads);
        _ = _running.Add("a");
        int periods = 0;
        _sleep = (t, ct) =>
        {
            if (t == Prewarmer.PlayTouchEvery && ++periods == 2)
            {
                _ = _running.Remove("a");
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe): 48 MiB of 48 MiB still in memory"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("missing stretches")); // 꺼지면 빠진 조각을 건너뛰는 걷기도 쓰지 않음 (기존 동작 그대로)
    }

    // ---- 하한·내놓기·점검 ----

    [Fact]
    public void BelowTheFloorTheLeastImportantAreReleasedByTheMissingAmountAndNeverHeldAgain()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 4).Select(i => Op(20 + i, 0, 1024))];
        Prewarmer p = Make(gib);
        RunTicks(2, tick => _avail = tick == 1 ? 3000 : 20000); // 첫 확인에서만 하한 (4096) 아래

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(4096 - 3000 + 256) * Mib], _hold.Released); // 부족한 양 + 여유
        Assert.Contains(_log.Lines, l => l.Contains("cache hold released 1352 MiB for memory (a.exe): available 3000 MiB < floor 4096 MiB, 2744 MiB still held"));
        int batches = _hold.Batches.Count;
        Assert.Equal(256, batches); // 내놓은 뒤에는 다시 붙들지 않음: 붙들기 호출은 처음 한 번뿐
    }

    [Fact]
    public void EverythingReleasedForMemoryEndsTheHoldAndTheGameEndsAreHandled()
    {
        Prewarmer p = Make(Reads);
        RunTicks(5, tick => _avail = tick == 1 ? 100 : 20000);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(everything released for memory)"));
        Assert.Equal(0, p.KeepHeldMib);
    }

    [Fact]
    public void EveryTwoMinutesTheHeldRangesAreCheckedAndTheOutcomeIsLogged()
    {
        _hold.NextCheck = () => new HoldCheckResult(40 * Mib, 6 * Mib, 8 * Mib, true, [F(3)], 2, 120, 1.5, 0.4);
        Prewarmer p = Make(Reads);
        RunTicks(48);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(2, _hold.Rechecks); // 틱 24 와 48
        Assert.Contains(_log.Lines, l => l.Contains("cache hold checked (a.exe): 40 MiB read again in 1.5 s (cpu 0.4 s, 2 checks), 6 MiB reduced by the memory manager and read from disk (120 hard faults), refill cap reached, 8 MiB taken off the hold, 1 changed file(s) released"));
        Assert.Contains("cache hold ended after 2 checks", _log.Lines.Last());
    }

    [Fact]
    public void AnotherExeOfTheSameGameDuringTheHoldDoesNotHoldAgain()
    {
        Prewarmer p = Make(Reads);
        RunTicks(2, tick =>
        {
            if (tick == 1)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe"); // 런처 뒤에 본체가 이어 뜸
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.Equal(3, _hold.Batches.Count);
        Assert.Single(_log.Lines, l => l.Contains("cache hold ended"));
    }

    // ---- 끝낼 때·예외·서비스 정지 ----

    [Fact]
    public void AnExceptionWhileHoldingEndsOnlyTheHoldAndClosesWhatWasHeld()
    {
        _hold.Throw = new InvalidOperationException("boom");
        Prewarmer p = Make(Reads);
        RunTicks(1);

        p.TouchAtGameStart(Game("a"), "a.exe"); // 예외가 서비스로 번지지 않음

        Assert.Contains(_log.Lines, l => l.Contains("a: cache hold while playing stopped: InvalidOperationException"));
        Assert.True(_hold.Disposed >= 1);
        Assert.Equal(0, p.KeepHeldMib);
    }

    [Fact]
    public void TheServiceStoppingClosesTheHold()
    {
        Prewarmer p = Make(Reads);
        _ = _running.Add("a");
        _sleep = (t, ct) =>
        {
            if (t == Prewarmer.HoldWatchEvery)
            {
                p.Shutdown();
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.True(_hold.Disposed >= 1);
        Assert.Equal(0, p.KeepHeldMib);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended"));
    }

    [Fact]
    public void TurningTheSwitchOffWhileHoldingClosesTheHoldAndTheOldTouchingTakesOver()
    {
        Prewarmer p = Make(Reads);
        _ = _running.Add("a");
        int ticks = 0;
        int periods = 0;
        _sleep = (t, ct) =>
        {
            if (t == Prewarmer.HoldWatchEvery && ++ticks == 2)
            {
                _keepHold = false;
            }
            else if (t == Prewarmer.PlayTouchEvery && ++periods == 2)
            {
                _ = _running.Remove("a");
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(turned off in the settings)"));
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe)")); // 남은 시간은 지금의 다시 만지기
    }

    // ---- 빠진 조각에서 끝내지 않는 다시 만지기 (붙들지 않는 게임) ----

    [Fact]
    public void TheOldTouchingOfAGameThatIsNotHeldSkipsAMissingStretchAndKeepsGoing()
    {
        GameSettings cheat = Game("a") with { AntiCheat = "vanguard" };
        Prewarmer p = Make(Reads, cheat);
        _touch = ranges => ranges.Any(o => o.Path == F(2) && o.Offset == 0)
            ? new TouchResult(9 * Mib, 0.1, 3, 0, TouchEnd.NeedsDiskReads, Mib, 0, 0, 0, 40, StopReason.HardFaults) // 파일 2 의 앞 1 MiB 에서 경계
            : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
        _ = _running.Add("a");
        int periods = 0;
        _sleep = (t, ct) =>
        {
            if (t == Prewarmer.PlayTouchEvery && ++periods == 2)
            {
                _ = _running.Remove("a");
            }
        };

        p.TouchAtGameStart(cheat, "a.exe");

        List<string> touchedPaths = [.. _touched.SelectMany(r => r).Select(o => o.Path).Distinct()];
        Assert.Contains(F(5), touchedPaths); // 경계 뒤의 파일도 만짐
        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)") && l.Contains("1 missing stretches skipped"));
    }

    // ---- 설정·동작 표시 ----

    [Fact]
    public void TheSwitchDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParseMemory("{}").KeepHold);
        Assert.False(ParseMemory("""{"keep_hold":false}""").KeepHold);
        Assert.True(ParseMemory("""{"keep_hold":false}""").StandbyLadder);
        Assert.Throws<SettingsException>(() => ParseMemory("""{"keep_hold":"yes"}"""));
    }

    private static MemorySettings ParseMemory(string memory)
    {
        var root = new JsonObject
        {
            ["games"] = new JsonArray(new JsonObject
            {
                ["name"] = "mw4",
                ["exe_names"] = new JsonArray("game.exe"),
                ["trace"] = @"C:\ProgramData\AsterCache\plans\mw4.prefetch.json",
                ["plan"] = @"C:\ProgramData\AsterCache\plans\mw4.plan.json",
                ["origin_root"] = @"D:\Games\MW4",
                ["allowed_roots"] = new JsonArray(@"D:\Games"),
                ["volume_label"] = "GAMES",
            }),
            ["memory"] = JsonNode.Parse(memory),
        };
        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Memory;
    }

    [Fact]
    public void TheHoldCheckAndReleaseAreShownInTheServiceActivityOfTheSecond()
    {
        var activity = new ServiceActivity();
        using (activity.Enter(ServiceOp.Hold))
        {
        }

        using (activity.Enter(ServiceOp.Check))
        {
        }

        using (activity.Enter(ServiceOp.Release))
        {
        }

        Assert.Equal("hold+check+release", activity.Collect());
    }

    [Fact]
    public void TheAvailableMemoryRecordedForThePlayAddsWhatIsHeldSoTheLearnedFootprintDoesNotGrowByIt()
    {
        // 기록하는 값: 실제 가용 + 붙든 양. 붙들면 실제 가용이 붙든 양만큼 줄어 보이지만 기록은 그대로라 (시작 가용 - 최소 가용) 로 배우는 게임의 몫에 섞이지 않음
        long startAvail = 20000;
        long recordedDuringHold = -1;
        long actualDuringHold = -1;
        Prewarmer prewarmer = Make(Reads);
        _availFn = () => startAvail - (_hold.HeldBytes >> 20);
        RunTicks(1, tick =>
        {
            actualDuringHold = _availFn!();
            recordedDuringHold = actualDuringHold + prewarmer.KeepHeldMib;
        });

        prewarmer.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(startAvail - 48, actualDuringHold);
        Assert.Equal(startAvail, recordedDuringHold);
        Assert.Equal(0, prewarmer.KeepHeldMib); // 게임이 끝나면 더하지 않음
    }
}
