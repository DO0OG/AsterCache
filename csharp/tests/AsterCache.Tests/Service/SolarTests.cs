using System.Reflection;
using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// SOLAR 판정 (순수 함수), 본체 (표식·만지기·로그), 예열·유지 틱과의 연결 (가짜 환경과 가짜 스트리머).
public sealed class SolarTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _touched = [];
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private bool _enabled = true;
    private MemoryLists? _lists = new(0, 0, 8000 * Mib);
    private int _listQueries;
    private TouchResult _result = new(100 * Mib, 0.4, 0, 0, TouchEnd.Completed);

    private static readonly ReadOp[] Reads = [new ReadOp(@"D:\a\1.bin", 0, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib)];

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    private Action? _onTouch;

    private PrewarmEnv Env() => new()
    {
        AvailableRamMib = () => 20000,
        GameRunning = _ => false,
        ZoneBytes = _ => 1000 * Mib,
        TraceExists = _ => true,
        StandbyLadderEnabled = () => _enabled,
        Lists = () =>
        {
            _listQueries++;
            return _lists;
        },
        TouchRanges = (ranges, ct) =>
        {
            _touched.Add(ranges);
            _onTouch?.Invoke();
            return ct.IsCancellationRequested ? _result with { End = TouchEnd.Canceled } : _result;
        },
    };

    private Solar Make() => new(Env(), _log, () => _now);

    // ---- 판정 (순수 함수) ----

    private static LadderMark Mark(long? repurposedMib, long aheadMib, DateTimeOffset at) => new(at, repurposedMib is { } r ? r * Mib : null, aheadMib * Mib, 0, []);

    [Theory]
    [InlineData(10000, 3999, false)] // A 10000 MiB 의 0.4 배 = 4000 MiB 미만
    [InlineData(10000, 4000, true)]
    [InlineData(1000, 511, false)] // A 가 작으면 하한 512 MiB (0.4 x 1000 = 400 보다 큼)
    [InlineData(1000, 512, true)]
    [InlineData(0, 512, true)]
    [InlineData(0, 0, false)] // 재사용이 없으면 아무 일도 하지 않음
    public void RefreshIsDueWhenCacheReuseGrowsByAFractionOfWhatIsAheadWithAFloor(long aheadMib, long grownMib, bool due)
    {
        LadderMark mark = Mark(5000, aheadMib, _now);

        Assert.Equal(due, SolarPolicy.RefreshDue(mark, new MemoryLists(0, (5000 + grownMib) * Mib), _now));
    }

    [Fact]
    public void WithoutMemoryListsTheTimeRuleOfTenMinutesApplies()
    {
        LadderMark mark = Mark(5000, 10000, _now);

        Assert.False(SolarPolicy.RefreshDue(mark, null, _now + TimeSpan.FromMinutes(9.9)));
        Assert.True(SolarPolicy.RefreshDue(mark, null, _now + TimeSpan.FromMinutes(10)));
        Assert.False(SolarPolicy.RefreshDue(Mark(null, 10000, _now), new MemoryLists(0, 1L << 40), _now + TimeSpan.FromMinutes(1))); // 표식을 만들 때 못 읽었으면 지금 값이 있어도 시간으로
        Assert.True(SolarPolicy.RefreshDue(Mark(null, 10000, _now), new MemoryLists(0, 1L << 40), _now + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void AheadIsStandbyMinusWhatThisGameTouchedAndNeverNegative()
    {
        Assert.Equal(7000 * Mib, SolarPolicy.Ahead(8000 * Mib, 1000 * Mib));
        Assert.Equal(0, SolarPolicy.Ahead(500 * Mib, 1000 * Mib));
    }

    // ---- 본체 ----

    [Fact]
    public void OrderTouchesTheReadsInReverseAndLeavesAMarkWithTheAheadAmount()
    {
        Solar solar = Make();

        TouchEnd? end = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.Equal(TouchEnd.Completed, end);
        Assert.Equal([new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 0, 10 * Mib)], _touched.Single());
        Assert.True(solar.HasMark("a"));
        Assert.Contains("a: ladder ordered 100 MiB in 0.4 s", _log.Lines.Single());
    }

    [Fact]
    public void OrderDoesNothingWhenTheSettingIsOffOrThereIsNothingToTouch()
    {
        _enabled = false;
        Solar solar = Make();

        Assert.Null(solar.Order(Game("a"), Reads, CancellationToken.None));
        _enabled = true;
        Assert.Null(solar.Order(Game("a"), [], CancellationToken.None));
        Assert.Empty(_touched);
        Assert.False(solar.HasMark("a"));
    }

    [Theory]
    [InlineData(TouchEnd.Canceled, "ladder stopped after 100 MiB")]
    [InlineData(TouchEnd.NeedsDiskReads, "ladder was too slow without disk reads seen (3 slow pieces), skipped")]
    public void AnInterruptedOrderLeavesNoMark(TouchEnd end, string text)
    {
        Solar solar = Make();
        Assert.Equal(TouchEnd.Completed, solar.Order(Game("a"), Reads, CancellationToken.None));
        _result = new TouchResult(100 * Mib, 0.4, 3, 0, end);

        Assert.Equal(end, solar.Order(Game("a"), Reads, CancellationToken.None));

        Assert.False(solar.HasMark("a"));
        Assert.Contains(text, _log.Lines.Last());
    }

    [Fact]
    public void BytesSeenReadFromDiskAreAppendedToTheLogLine()
    {
        _result = _result with { DiskBytes = 20 * Mib };

        _ = Make().Order(Game("a"), Reads, CancellationToken.None);

        Assert.EndsWith("MiB in 0.4 s, 20 MiB read from disk", _log.Lines.Single());
    }

    [Fact]
    public void ALadderThatFindsDataMissingFlagsTheGameOnceAndItIsClearedWhenTaken()
    {
        Solar solar = Make();
        _result = new TouchResult(37 * Mib, 0.6, 36, 0, TouchEnd.NeedsDiskReads, 37 * Mib);

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.True(solar.HasMissing("a"));
        Assert.Equal("Info: a: ladder found data missing from memory (36 slow pieces, 37 MiB read from disk), left to the rewarm check", _log.Lines.Single());
        Assert.True(solar.TakeMissing("a"));
        Assert.False(solar.TakeMissing("a"));
        Assert.False(solar.HasMissing("a"));
    }

    [Fact]
    public void ARefreshThatFindsDataMissingFlagsTheGameAndALaterCompleteLadderClearsIt()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = _result with { SlowPieces = 5, End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.True(solar.HasMissing("a"));
        Assert.False(solar.HasMark("a"));

        _result = _result with { SlowPieces = 0, End = TouchEnd.Completed };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.False(solar.HasMissing("a"));
        Assert.True(solar.HasMark("a"));
    }

    [Fact]
    public void ALadderThatEndedSlowWithoutAnyDiskReadSeenClearsTheMarkButDoesNotFlagTheGame()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = new TouchResult(10 * Mib, 0.6, 5, 1, TouchEnd.NeedsDiskReads);

        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.False(solar.HasMark("a"));
        Assert.False(solar.HasMissing("a"));
        Assert.Equal("Info: a: cache refresh was too slow without disk reads seen (5 slow pieces, 1 errors), left to the rewarm check", _log.Lines.Last());

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.False(solar.HasMissing("a"));
        Assert.Equal("Info: a: ladder was too slow without disk reads seen (5 slow pieces, 1 errors), skipped", _log.Lines.Last());
    }

    [Fact]
    public void RestoreMissingFlagsAgainOnlyWhileThereIsNoMark()
    {
        Solar solar = Make();
        solar.RestoreMissing("a");
        Assert.True(solar.HasMissing("a"));

        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // 사다리가 서서 표시가 지워짐
        solar.RestoreMissing("a");

        Assert.False(solar.HasMissing("a"));
    }

    [Fact]
    public void ACanceledLadderDoesNotFlagAndPruneDropsTheFlagOfAGameThatLeftTheSettings()
    {
        Solar solar = Make();
        _result = _result with { End = TouchEnd.Canceled };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.False(solar.HasMissing("a"));

        _result = _result with { End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        solar.Prune(new HashSet<string>(["b"], StringComparer.OrdinalIgnoreCase));

        Assert.False(solar.HasMissing("a"));
        Assert.True(solar.HasMissing("b"));
    }

    [Fact]
    public void ErrorsAreAppendedToTheLogLine()
    {
        _result = _result with { Errors = 2 };

        _ = Make().Order(Game("a"), Reads, CancellationToken.None);

        Assert.EndsWith("MiB in 0.4 s, 2 errors", _log.Lines.Single());
    }

    [Fact]
    public void RefreshTouchesTheStoredLadderAgainAndLogsTheReuseAgainstWhatWasAhead()
    {
        Solar solar = Make();
        _lists = new MemoryLists(0, 5000 * Mib, 8000 * Mib);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A = 8000 - 100 = 7900 MiB
        _lists = new MemoryLists(0, 8500 * Mib, 8000 * Mib);

        Assert.Equal(TouchEnd.Completed, solar.Refresh(Game("a"), CancellationToken.None));

        Assert.Equal(2, _touched.Count);
        Assert.Equal(_touched[0], _touched[1]); // 같은 사다리를 다시 만짐
        Assert.Contains("a: cache refreshed in memory: 100 MiB in 0.4 s (3500 MiB repurposed of 7900 MiB ahead)", _log.Lines.Last());
    }

    [Fact]
    public void RefreshWithoutAMarkOrWithTheSettingOffTouchesNothing()
    {
        Solar solar = Make();
        Assert.Null(solar.Refresh(Game("a"), CancellationToken.None));
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _enabled = false;

        Assert.Null(solar.Refresh(Game("a"), CancellationToken.None));
        Assert.Single(_touched);
    }

    [Fact]
    public void RefreshThatNeedsDiskReadsClearsTheMarkSoItIsNotRetriedUntilANewLadderExists()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = new TouchResult(10 * Mib, 0.6, 5, 1, TouchEnd.NeedsDiskReads, 32 * Mib);

        Assert.Equal(TouchEnd.NeedsDiskReads, solar.Refresh(Game("a"), CancellationToken.None));

        Assert.False(solar.HasMark("a"));
        Assert.Contains("a: cache refresh found data missing from memory (5 slow pieces, 32 MiB read from disk, 1 errors), left to the rewarm check", _log.Lines.Last());
        Assert.Empty(solar.Due([Game("a")], _now + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ACanceledRefreshKeepsTheMark()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = _result with { End = TouchEnd.Canceled };

        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.True(solar.HasMark("a"));
    }

    [Fact]
    public void DueKeepsTheInputOrderOnlyListsMarkedGamesAndReadsTheListsOnceOnlyWhenSomethingIsMarked()
    {
        Solar solar = Make();
        Assert.Empty(solar.Due([Game("a")], _now));
        Assert.Equal(0, _listQueries);

        _lists = new MemoryLists(0, 0, 8000 * Mib);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        _ = solar.Order(Game("c"), Reads, CancellationToken.None);
        _lists = new MemoryLists(0, 9000 * Mib, 8000 * Mib); // 모든 표식이 시점에 이름
        int before = _listQueries;

        IReadOnlyList<GameSettings> due = solar.Due([Game("c"), Game("x"), Game("a"), Game("b")], _now);

        Assert.Equal(["c", "a", "b"], due.Select(g => g.Name));
        Assert.Equal(before + 1, _listQueries);
    }

    [Fact]
    public void PruneDropsTheMarksOfGamesThatLeftTheSettings()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);

        solar.Prune(new HashSet<string>(["b"], StringComparer.OrdinalIgnoreCase));

        Assert.False(solar.HasMark("a"));
        Assert.True(solar.HasMark("b"));
    }

    [Fact]
    public void TheSettingDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParseMemory("{}").StandbyLadder);
        Assert.True(ParseMemory("""{"background_yield":false}""").StandbyLadder);
        Assert.False(ParseMemory("""{"standby_ladder":false}""").StandbyLadder);
        Assert.True(ParseMemory("""{"standby_ladder":false}""").BackgroundYield);
        Assert.Throws<SettingsException>(() => ParseMemory("""{"standby_ladder":"yes"}"""));
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

    // ---- 예열·유지 틱과의 연결 ----

    private sealed class PlannedStreamer : IGameStreamer
    {
        public List<(string Game, int MaxStreamMib)> Calls { get; } = [];

        public bool Complete { get; set; } = true;

        /// 핫 집합 크기 (MiB).
        public long ZoneMib { get; set; } = 30;

        public IReadOnlyList<ReadOp> All { get; set; } = SolarTests.Reads;

        public Action? DuringStream { get; set; }

        /// 참이면 스트림이 읽기 전에 예외로 실패함.
        public bool Fail { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add((game.Name, game.MaxStreamMib));
            if (Fail)
            {
                throw new IOException("read failed");
            }

            long total = Math.Min(game.MaxStreamMib == 0 ? ZoneMib : game.MaxStreamMib, ZoneMib) * Mib;
            progress?.Invoke(0, total);
            DuringStream?.Invoke();
            return (new StreamStats(total, 1, 0, 0.01), Complete && !stop.IsCancellationRequested);
        }

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) => StreamPrewarm(game, stop, progress, 0);

        /// 부분 예열 뒤 표본을 고르는 기준과 같게 MaxStreamMib 로 잘린 읽은 범위.
        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => PrefetchStreamer.Head(All, game.MaxStreamSet ? (long)game.MaxStreamMib * Mib : long.MaxValue);
    }

    private readonly PlannedStreamer _streamer = new();
    private readonly HashSet<string> _running = [];
    private bool _diskBusy;
    private long _avail = 20000;
    private ProbeResult? _probe = new ProbeResult(8, 0, 0.2); // warm (null: 표본을 읽을 수 없음)
    private IReadOnlyList<ReadOp> _common = [];

    private Prewarmer MakePrewarmer(params GameSettings[] games)
    {
        PrewarmEnv env = Env();
        return new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = g => _running.Contains(g.Name),
            ZoneBytes = g => Math.Min(_streamer.ZoneMib, g.StreamCapBytes(_streamer.ZoneMib * Mib) / Mib) * Mib,
            TraceExists = env.TraceExists,
            PhysicalRamBytes = () => 64L << 30,
            ProbeReadMs = _ => _probe,
            DiskBytesPerSecond = _ => _diskBusy ? 1e9 : 0,
            CommonFirstOps = (_, _, _) => (_common, _common.Sum(o => o.Length)),
            ColdRead = (ops, _) => (new StreamStats(ops.Sum(o => o.Length), 1, 0, 0.01), true),
            StandbyLadderEnabled = env.StandbyLadderEnabled,
            Lists = env.Lists,
            TouchRanges = env.TouchRanges,
        }, _log, null, () => _now);
    }

    private static AutoSettings Auto() => AutoSettings.Default with { RewarmMinutes = 10 };

    private void GrowReuse(long mib) => _lists = new MemoryLists(0, mib * Mib, 8000 * Mib);

    private static ReadOp[] Reversed(IEnumerable<ReadOp> readOrder) => [.. readOrder.Reverse()];

    [Fact]
    public void AfterTheStreamFinishesTheWholeReadRangeIsTouchedInReverseWithTheCommonBlockLast()
    {
        _common = [new ReadOp(@"D:\a\c.bin", 0, 5 * Mib)];
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Equal([new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 0, 10 * Mib), _common[0]], _touched.Single());
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ACanceledStreamIsNotTouchedAndASettingOffTouchesNothing()
    {
        _streamer.Complete = false;
        Prewarmer cancelled = MakePrewarmer(Game("a"));
        _ = cancelled.Run(Game("a"), "service start");
        _streamer.Complete = true;
        _enabled = false;
        Prewarmer off = MakePrewarmer(Game("b"));

        Assert.True(off.Run(Game("b"), "service start").Ran);

        Assert.Empty(_touched);
        Assert.False(cancelled.Solar.HasMark("a"));
    }

    [Fact]
    public void AGameThatStartsDuringTheStreamIsNotTouched()
    {
        Prewarmer? p = null;
        _streamer.DuringStream = () => p!.NoteGameStart("a");
        p = MakePrewarmer(Game("a"));

        _ = p.Run(Game("a"), "service start");

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
    }

    [Fact]
    public void AGameThatStartsDuringTheLadderStopsItAtOnceAndLeavesNoMark()
    {
        Prewarmer? p = null;
        _onTouch = () => p!.NoteGameStart("a"); // 만지는 도중 게임 시작: 스트림 중지 신호가 만지기의 토큰으로 이어짐
        p = MakePrewarmer(Game("a"));

        _ = p.Run(Game("a"), "service start");

        Assert.Single(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: ladder stopped after"));
    }

    [Fact]
    public void KeepStreamOnGameStartSkipsTheLadderBecauseTheStreamLeaseIsNotCanceledByTheGame()
    {
        Prewarmer p = MakePrewarmer(Game("a"));
        p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Empty(_touched);
    }

    [Fact]
    public void TheTickRefreshesOnlyWhenTheReuseReachesTheThreshold()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start"); // A = 8000 - 100 MiB (만진 양은 가짜 결과의 100 MiB)
        _touched.Clear();
        _now += TimeSpan.FromMinutes(5); // 방금 (1 분 안) 예열한 게임은 새로 고치지 않음

        GrowReuse(1000 + 3000); // 0.4 x 7900 = 3160 MiB 미만
        p.Tick(Auto());
        Assert.Empty(_touched);

        GrowReuse(1000 + 3200);
        p.Tick(Auto());
        Assert.Single(_touched);
        Assert.Contains("a: cache refreshed in memory", _log.Lines.Last());

        p.Tick(Auto()); // 새 표식 기준으로 다시 세므로 바로 또 하지는 않음
        Assert.Single(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileTheGameRunsOrTheDriveIsBusyOrTheTokenIsCanceled()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        _ = _running.Add("a");
        p.Tick(Auto());
        _ = _running.Remove("a");
        _diskBusy = true;
        p.Tick(Auto());
        _diskBusy = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        p.Tick(Auto(), cts.Token);

        Assert.Empty(_touched);
        p.Tick(Auto());
        Assert.Single(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileATriggerPrewarmWaitsOrReads()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        using (p.BeginTriggerWait([Game("b")])) // 트리거가 기다리거나 읽는 동안의 표시
        {
            p.Tick(Auto());
            Assert.Empty(_touched);
        }

        p.Tick(Auto());
        Assert.Single(_touched);
    }

    [Fact]
    public void AStreamStoppedByATriggerPreemptionIsNotLaddered()
    {
        Prewarmer? p = null;
        MethodInfo preempt = typeof(Prewarmer).GetMethod("PreemptForTrigger", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _streamer.DuringStream = () => preempt.Invoke(p, [new GameSettings[] { Game("b") }]); // 트리거 경로가 부르는 정지 신호
        p = MakePrewarmer(Game("a"), Game("b"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.False(result.Ran);
        Assert.Equal(Prewarmer.PreemptedByTrigger, result.CancelDetail);
        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ATriggerThatStartedWaitingJustBeforeTheStreamEndedSkipsTheLadderToo()
    {
        Prewarmer? p = null;
        IDisposable? wait = null;
        _streamer.DuringStream = () => wait = p!.BeginTriggerWait([Game("b")]); // 정지 신호를 놓친 경우: 스트림은 끝까지 읽고 사다리 시작 때 표시를 봄
        p = MakePrewarmer(Game("a"), Game("b"));

        _ = p.Run(Game("a"), "service start");
        wait?.Dispose();

        Assert.Empty(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileAnotherGameRunsOnTheSameDisk()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b")); // 둘 다 D:
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        _ = _running.Add("b"); // 같은 디스크의 다른 게임 (읽기 카운터를 못 읽으므로 한가하다고 보지 않음)
        p.Tick(Auto());

        Assert.Empty(_touched);
    }

    [Fact]
    public void RefreshWaitsForTheRewarmCheckSamplesAndLeavesColdOnesToIt()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _probe = new ProbeResult(8, 8, 5.0); // 이미 식음

        p.Tick(Auto());

        Assert.Empty(_touched); // 식은 캐시는 만지지 않음
        Assert.False(p.Solar.HasMark("a"));
        Assert.Empty(_streamer.Calls); // 재예열은 부르지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: cache refresh skipped, the samples are cold") && l.EndsWith("left to the rewarm check"));
    }

    [Fact]
    public void RefreshThatCannotReadItsSamplesLeavesTheGameToTheRewarmCheckWithoutRepeatingItself()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _probe = null; // 표본을 읽을 수 없음

        p.Tick(Auto());
        _now += TimeSpan.FromSeconds(30);
        GrowReuse(30000);
        p.Tick(Auto());

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Single(_log.Lines, l => l.Contains("a: cache refresh skipped, there are no readable samples, left to the rewarm check")); // 표식을 지웠으니 30 초마다 되풀이하지 않음
    }

    [Fact]
    public void WhenTheRefreshNeedsDiskReadsNoRewarmIsCalledAndTheLadderReturnsOnlyAfterTheRewarmCheckFindsItWarm()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        GrowReuse(20000);
        _result = _result with { SlowPieces = 4, End = TouchEnd.NeedsDiskReads, DiskBytes = 40 * Mib };
        _now += TimeSpan.FromMinutes(5);
        AutoSettings auto = Auto();

        p.Tick(auto); // 새로 고침이 디스크 읽기를 요구 -> 표식만 지우고 재예열은 부르지 않음
        Assert.Single(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: cache refresh found data missing from memory (4 slow pieces, 40 MiB read from disk), left to the rewarm check"));

        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };
        for (int i = 0; i < 3; i++)
        {
            _now += TimeSpan.FromMinutes(2);
            GrowReuse(20000 + (i + 1) * 5000L);
            p.Tick(auto); // 표식이 없으니 재사용이 늘어도 다시 시도하지 않음
        }

        Assert.Single(_touched);
        Assert.Empty(_streamer.Calls);

        _now += TimeSpan.FromMinutes(6); // 첫 틱 뒤 10 분이 지나 재예열 점검 차례: 표본이 warm 이어도 다시 채움 표시 때문에 재예열
        p.Tick(auto);

        Assert.Single(_streamer.Calls);
        Assert.Equal(2, _touched.Count); // 재예열 직후의 사다리
        Assert.True(p.Solar.HasMark("a"));
        Assert.False(p.Solar.HasMissing("a"));
        Assert.Contains(_log.Lines, l => l == "Info: a: rewarm check: part of the list is not in memory (found by the ladder), prewarming again (cooldown ignored)");
    }

    // 예열 직후 사다리가 디스크 읽기를 만난 상태 (표식 없음, 다시 채움 표시 있음) 의 게임 하나, 첫 틱까지 지난 상태.
    private Prewarmer FlaggedGame(out AutoSettings auto)
    {
        _result = _result with { SlowPieces = 3, End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        Assert.True(p.Solar.HasMissing("a"));
        Assert.False(p.Solar.HasMark("a"));
        _touched.Clear();
        _streamer.Calls.Clear();
        auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        return p;
    }

    [Fact]
    public void ARewarmThatStillFindsDataMissingIsRepeatedOnlyOncePerCheckPeriod()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 표본은 warm 인데도 한 번 다시 읽음
        Assert.True(p.Solar.HasMissing("a")); // 재예열 뒤 사다리가 또 디스크 읽기를 만나 다시 섬

        _now += TimeSpan.FromMinutes(2);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 주기 안에서는 되풀이하지 않음

        _now += TimeSpan.FromMinutes(9);
        p.Tick(auto);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void AfterASuccessfulRefillTheLadderMarkStandsAndLaterChecksDoNotPrewarmAgain()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMark("a"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void AChecksPulledInByAGameExitDoNotRefillBeforeRewarmMinutesHavePassedSinceTheLastPrewarm()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        p.SetAuto(auto);
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a")); // 재예열 뒤 사다리가 또 디스크 읽기를 만나 표시가 다시 섬

        _now += TimeSpan.FromMinutes(1);
        p.NoteGameExit(); // 다른 게임이 끝남: 점검을 2 분 뒤로 당김
        _now += TimeSpan.FromMinutes(2.5);
        p.Tick(auto);

        Assert.Single(_streamer.Calls); // 마지막 예열 뒤 10 분 전이라 다시 읽지 않고 표시를 둠
        Assert.True(p.Solar.HasMissing("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check: part of the list is not in memory (found by the ladder), waiting until 10 min after the last prewarm"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void AFailedRewarmIsNotRetriedByPulledInChecksBeforeRewarmMinutesHavePassed()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        p.SetAuto(auto);
        _streamer.Fail = true; // 스트림이 돌지 않고 예외: 마지막 성공 예열 시각은 그대로
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        _now += TimeSpan.FromMinutes(1);
        p.NoteGameExit(); // 점검을 2 분 뒤로 당김
        _now += TimeSpan.FromMinutes(2.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 마지막 시도 뒤 10 분 전이라 다시 시도하지 않음
        Assert.True(p.Solar.HasMissing("a"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void ARewarmThatIsSkippedBeforeReadingKeepsTheFlagForTheNextCheck()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        _avail = 100; // 메모리가 모자라 읽기 전에 건너뜀
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        _avail = 20000;
        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Single(_streamer.Calls);
        Assert.False(p.Solar.HasMissing("a"));
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void WhileTheFlagStandsTheLadderTickDoesNotTouchTheGame()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        GrowReuse(30000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(auto);

        Assert.Empty(_touched);
        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));
    }

    [Fact]
    public void TheFlagIsKeptWhileTheRewarmCheckConditionsBlockOrTheCheckIsOff()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);

        _ = _running.Add("a"); // 게임 실행 중
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        _ = _running.Remove("a");
        _diskBusy = true; // 디스크가 바쁨
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        _diskBusy = false;
        using (p.BeginTriggerWait([Game("b")])) // 트리거 대기 중
        {
            _now += TimeSpan.FromMinutes(10.5);
            p.Tick(auto);
        }

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto with { RewarmMinutes = 0 }); // 점검 끔
        p.Tick(auto with { Enabled = false }); // 자동 모드 끔

        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        p.Tick(auto); // 끈 동안 다음 점검 시각이 지워져 다시 정함
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto); // 조건이 풀리면 한 번 채움
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ARecentlyPrewarmedGameIsLeftAloneByTheRefreshTick()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromSeconds(30); // 1 분 안

        p.Tick(Auto());

        Assert.Empty(_touched);
    }

    [Fact]
    public void WithSeveralGamesTheMostRecentlyPrewarmedOneIsTouchedLast()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"), Game("c"));
        foreach (string name in new[] { "b", "a", "c" })
        {
            _now += TimeSpan.FromMinutes(1);
            Assert.True(p.Run(Game(name), "service start").Ran);
        }

        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(Auto());

        string[] refreshed = _log.Lines.Where(l => l.Contains("cache refreshed in memory")).Select(l => l.Split(':')[1].Trim()).ToArray();
        Assert.Equal(["b", "a", "c"], refreshed); // 가장 오래 전에 예열한 b 부터, 가장 최근 c 가 마지막
    }

    [Fact]
    public void AGameWithoutAMarkGetsItsFirstLadderWhenTheRewarmCheckFindsItWarm()
    {
        GrowReuse(1000);
        _enabled = false; // 서비스를 다시 시작한 뒤처럼 예열 직후 사다리가 없었음
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _enabled = true;
        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));

        AutoSettings auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto); // 점검: 표본이 warm -> 첫 사다리

        Assert.Equal(3, _touched.Single().Count);
        Assert.True(p.Solar.HasMark("a"));
    }

    private static ReadOp[] HotSet(int files) => [.. Enumerable.Range(0, files).Select(i => new ReadOp($@"D:\a\f{i}.bin", 0, 100 * Mib))];

    [Fact]
    public void AfterAPartialPrewarmIsSuppliedTheLadderCoversTheWholeHotSetWithTheCommonBlockLast()
    {
        ReadOp[] all = HotSet(50);
        _streamer.All = all;
        _streamer.ZoneMib = 5000;
        _common = [all[0]];
        _avail = 7000; // 핫 집합 5000 MiB + 예비 4096 MiB 가 다 들어가지 않아 앞부분만 읽음
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran);
        Assert.True(p.SupplementPending("a"));

        ReadOp[] partial = [.. _touched.Single()];
        Assert.DoesNotContain(all[49], partial); // 읽은 앞부분까지만
        Assert.Equal(_common[0], partial[^1]);
        Assert.True(partial.Length < 51);
        _touched.Clear();

        _avail = 20000;
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.False(p.SupplementPending("a"));
        ReadOp[] ladder = [.. _touched.Single()];
        Assert.Equal([.. Reversed([.. _common, .. all])], ladder); // 보충분의 뒤쪽 -> ... -> 앞부분 -> 공통 블록
        Assert.Equal(all[49], ladder[0]);
        Assert.Equal(_common[0], ladder[^1]);
    }
}
