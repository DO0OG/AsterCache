using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임이 실행 뒤 잡는 메모리 (몫) 의 학습과, 그 몫을 반영한 예열 양·게임 사이 순서·스파스 범위 처리.
public sealed class GameFootprintTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly FootprintStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
    private long _avail = 19783;
    private (long Mib, int Plays)? _footprint;
    private Func<string, bool> _isSparse = _ => false;
    private bool _ladder; // memory.standby_ladder
    private bool _keepSparse; // memory.keep_sparse
    private List<TraceEvent>? _ranked; // 블록 순위 목록 (null: 순위 목록이 없는 게임)
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<bool> _probedSparse = [];
    private int _snapshots;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private sealed class FootprintStreamer : IGameStreamer
    {
        public List<(string Game, int MaxStreamMib)> Calls { get; } = [];

        public List<GameSettings> Games { get; } = [];

        public long ZoneMib { get; set; } = 13653;

        public List<ReadOp>? Planned { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public Func<string, bool> IsSparse { get; set; } = _ => false;

        public long? PrewarmReadBytes(GameSettings game) => game.SkipSparse && Planned is not null ? Planned.Where(o => !IsSparse(o.Path)).Sum(o => o.Length) : Math.Min(ZoneMib, game.MaxStreamSet ? game.MaxStreamMib : ZoneMib) * Mib;

        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => Planned;

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add((game.Name, game.MaxStreamMib));
            Games.Add(game);
            long total = Math.Min(game.MaxStreamSet ? game.MaxStreamMib : ZoneMib, ZoneMib) * Mib;
            progress?.Invoke(0, total);
            return (new StreamStats(total, 1, 0, 0.01), !stop.IsCancellationRequested);
        }

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) =>
            StreamPrewarm(game, stop, progress, 0);
    }

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe") with { OriginRoot = @"D:\Games\" + name };

    public GameFootprintTests() => _streamer.IsSparse = p => _isSparse(p);

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = g => _running.Contains(g.Name),
        GameRunningSnapshot = () =>
        {
            _snapshots++;
            return g => _running.Contains(g.Name);
        },
        PhysicalRamBytes = () => 32694L << 20,
        ZoneBytes = g => Math.Min(_streamer.ZoneMib, g.StreamCapBytes(_streamer.ZoneMib * Mib) / Mib) * Mib,
        TraceExists = _ => true,
        IsSparse = p => _isSparse(p),
        StandbyLadderEnabled = () => _ladder,
        KeepSparseEnabled = () => _keepSparse,
        OpenBlockReader = () => throw new NotSupportedException(), // 예열 판정은 읽기 도구가 있는지만 봄
        RankedRows = _ => _ranked,
        ProbeReadMs = g =>
        {
            _probedSparse.Add(g.SkipSparse);
            return new ProbeResult(8, 8, 0.1);
        },
    }, _log, null, () => _now)
    {
        Footprint = _ => _footprint,
    };

    private static PlaySampleRecord Play(DateTimeOffset at, long start, long? min, double seconds = 1800, bool observedLate = false) => new()
    {
        SessionId = at.ToString("O"),
        Game = "cod",
        StartedAt = at,
        DurationSeconds = seconds,
        AvailableRamMib = start,
        MinAvailableRamMib = min,
        ObservedAfterGameStart = observedLate,
    };

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 23, 19, 0, TimeSpan.Zero);

    [Fact]
    public void TheFootprintIsTheMedianOfTheLastThreeUsablePlays()
    {
        PlaySampleRecord[] plays =
        [
            Play(T0.AddDays(-3), 20000, 13000), // 가장 오래된 네 번째: 최근 세 번에 들지 못함
            Play(T0, 20436, 10885), // 9,551
            Play(T0.AddHours(4), 20560, 11039), // 9,521
            Play(T0.AddHours(14), 19783, 9788), // 9,995
        ];

        Assert.Equal((9551L, 3), GameFootprint.Median(plays)!.Value);
    }

    [Fact]
    public void PlaysThatCannotTellTheFootprintAreLeftOutAndNoRecordMeansNoFootprint()
    {
        Assert.Null(GameFootprint.Median([]));
        Assert.Null(GameFootprint.Median([
            Play(T0, 20000, 10000, seconds: 599), // 시작 뒤 10 분을 다 보지 못함
            Play(T0.AddHours(1), 20000, 10000, observedLate: true), // 게임이 뜬 뒤에 관측을 시작함: 시작 가용을 모름
            Play(T0.AddHours(2), 20000, 500), // 메모리가 바닥나 몫이 잘렸을 수 있음
            Play(T0.AddHours(3), 20000, null), // 최소 가용을 기록하기 전 형식
            Play(T0.AddHours(4), 0, 100),
        ]));
    }

    [Fact]
    public void OneUsablePlayMovesTheReserveHalfwayToTheOldOneTwoTakeTheSmallerAndThreeTheMedian()
    {
        PlaySampleRecord nine = Play(T0, 20000, 11000); // 9,000
        PlaySampleRecord ten = Play(T0.AddHours(1), 20000, 10000); // 10,000
        PlaySampleRecord twelve = Play(T0.AddHours(2), 20000, 8000); // 12,000

        // 1 개: 예비 = 예전 4096 과 몫 예비 (9000 + 1024) 의 중간값
        (long mib, int plays) = GameFootprint.Median([nine, Play(T0.AddHours(1), 20000, 100)])!.Value;
        Assert.Equal(1, plays);
        Assert.Equal((4096 + 9000 + 1024) / 2, GameFootprint.ReserveMib(0, mib));
        Assert.Equal((9000L, 2), GameFootprint.Median([nine, ten])!.Value); // 2 개: 작은 쪽
        Assert.Equal((10000L, 3), GameFootprint.Median([nine, ten, twelve])!.Value); // 3 개: 중앙값
        Assert.Equal(7000L, GameFootprint.Of(Play(T0, 7000 + 1024, 1024))); // 몫은 그 플레이의 시작 가용을 넘지 않음
    }

    [Fact]
    public void ThePartialCapKeepsTheFootprintPlusMarginInsteadOfTheFixedReserve()
    {
        GameSettings game = Game("cod");
        long window = Prewarmer.WindowAvailableMib(19783);

        Assert.Null(Prewarmer.PartialCapMib(game, 13653, window, 16347)); // 기록이 없으면 지금 상한: 고정 예비 4096 으로 전부 들어감
        Assert.Equal(8952, Prewarmer.PartialCapMib(game, 13653, window, 16347, footprintMib: 9551)); // 19783 - 256 - (9551 + 1024)
        Assert.Equal(window - 12000, Prewarmer.PartialCapMib(game with { Prewarm = game.Prewarm with { MinFreeRamMib = 12000 } }, 13653, window, 16347, footprintMib: 1000)); // min_free_ram_mib 가 더 크면 그것
    }

    [Fact]
    public void ARunReadsOnlyWhatStaysAfterTheGameStartsAndLogsTheFootprint()
    {
        _footprint = (9551, 3);
        GameSettings game = Game("cod");
        Prewarmer p = Make(game);

        Assert.True(p.Run(game, "service start").Ran);

        Assert.Equal(8952, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.Contains(_log.Lines, l => l.Contains("cod: game footprint 9551 MiB (median of 3 plays), cap 8952 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAFootprintTheRunReadsTheWholeListAsBefore()
    {
        GameSettings game = Game("cod");
        Prewarmer p = Make(game);

        Assert.True(p.Run(game, "service start").Ran);

        Assert.NotEqual(8952, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("game footprint", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBudgetLeavesTheNextGameItsFootprintSoOtherGamesCannotEatIt()
    {
        _footprint = (9551, 3);
        GameSettings cod = Game("cod");
        GameSettings lol = Game("lol");
        Prewarmer p = Make(cod, lol);
        p.WarmRank = list => list.OrderBy(g => g.Name == "cod" ? 0 : 1);
        _streamer.ZoneMib = 5000;
        _avail = 30000; // 이때는 예산이 남아 두 게임이 모두 들어감
        Assert.True(p.Run(lol, "service start").Ran);
        Assert.True(p.Run(cod, "service start").Ran);
        Assert.Equal(10000, p.WarmBudget()!.Value.UsedMib);

        _avail = 19783; // 곧 플레이할 cod 의 몫을 빼면 남는 캐시는 19783 - 256 - 10575 = 8952
        _now += TimeSpan.FromSeconds(11); // 예산의 짧은 캐시가 지남

        (HashSet<string> inBudget, long cap, long used, Dictionary<string, long> alloc) = p.WarmBudget()!.Value;

        Assert.Equal(8952, cap);
        Assert.Equal(8952, used);
        Assert.Equal(5000, alloc["cod"]); // 곧 플레이할 게임이 먼저: 예산 배정 1순위와 사다리 1순위가 같은 게임
        Assert.Equal(3952, alloc["lol"]);
        Assert.Contains("lol", inBudget);
        Assert.Equal("cod", p.FirstPlayPriority()!.Name);
    }

    [Fact]
    public void TheFootprintCapNeverFallsBelowHalfOfTheCapWithoutIt()
    {
        GameSettings game = Game("cod");

        // 16 GiB PC: 가용 11,264, 물리 RAM / 2 = 8,192, 몫 7,168. 몫 상한 2,816 은 예전 상한 6,912 의 절반 3,456 아래라 3,456
        Assert.Equal(3456, Prewarmer.PartialCapMib(game, 20000, Prewarmer.WindowAvailableMib(11264), 8192, footprintMib: 7168));
        // 8 GiB PC: 가용 5,120, 물리 RAM / 2 = 4,096, 몫 3,584. 몫 상한 256 은 예전 상한 768 의 절반 384 아래라 384 (읽을 수 있는 최소량 256 이상)
        Assert.Equal(384, Prewarmer.PartialCapMib(game, 6000, Prewarmer.WindowAvailableMib(5120), 4096, footprintMib: 3584));
        Assert.Equal(768, Prewarmer.PartialCapMib(game, 6000, Prewarmer.WindowAvailableMib(5120), 4096)); // 몫을 모르면 그대로
        Assert.Equal(8173, Prewarmer.FootprintCap(16347, 503)); // 물리 RAM 32,694 의 예산 16,347 도 같은 하한
        Assert.Equal(251, Prewarmer.FootprintCap(503, 100)); // 하한은 예전 상한의 절반
        Assert.Equal(503, Prewarmer.FootprintCap(503, 900)); // 몫을 반영한 값이 더 커도 예전 상한을 넘지 않음
    }

    [Fact]
    public void TheBudgetFollowsTheSameFloorAndIgnoresTheFootprintWhileAnyGameRuns()
    {
        _footprint = (18000, 3); // 몫 예비 19024 > 가용: 하한이 없으면 예산이 0 근처
        GameSettings cod = Game("cod");
        GameSettings lol = Game("lol");
        Prewarmer p = Make(cod, lol);
        p.WarmRank = list => list.OrderBy(g => g.Name, StringComparer.Ordinal);
        _streamer.ZoneMib = 100;
        Assert.True(p.Run(cod, "service start").Ran);
        Assert.True(p.Run(lol, "service start").Ran);

        Assert.Equal(8173, p.WarmBudget()!.Value.CapMib); // 16,347 의 절반

        _running.Add("lol"); // 어떤 게임이든 실행 중이면 그 몫은 이미 가용에서 빠졌으므로 예전 예산
        _now += TimeSpan.FromSeconds(11);

        Assert.Equal(16347, p.WarmBudget()!.Value.CapMib);
    }

    [Fact]
    public void TheBudgetTakesNoProcessSnapshotWithoutAKnownFootprintAndKeepsItsValueBrieflyWithKnownOnes()
    {
        GameSettings cod = Game("cod");
        Prewarmer p = Make(cod);
        p.WarmRank = list => list;
        _streamer.ZoneMib = 100;
        Assert.True(p.Run(cod, "service start").Ran);
        _snapshots = 0;

        Assert.Equal(16347, p.WarmBudget()!.Value.CapMib);
        Assert.Equal(0, _snapshots); // 몫을 아는 게임이 없으면 스냅샷도 정렬도 없음

        _footprint = (9551, 3);
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(8952, p.WarmBudget()!.Value.CapMib);
        Assert.True(_snapshots > 0);
        _avail = 15000; // 짧은 시간 안의 가용 변동은 같은 값을 씀
        _now += TimeSpan.FromSeconds(5);
        Assert.Equal(8952, p.WarmBudget()!.Value.CapMib);
        _now += TimeSpan.FromSeconds(6);
        Assert.Equal(8173, p.WarmBudget()!.Value.CapMib); // 새 가용에서는 4,169 지만 하한 (16,347 의 절반)
    }

    [Fact]
    public void ARelocationWarmGetsTheBudgetAllocationAndAGameOutsideItGetsTheLeftover()
    {
        GameSettings a = Game("a") with { MaxStreamMib = 5000, MaxStreamSet = true };
        GameSettings b = Game("b") with { MaxStreamMib = 5000, MaxStreamSet = true };
        GameSettings c = Game("c");
        _streamer.ZoneMib = 5000;
        _avail = 30000;
        Prewarmer p = Make(a, b, c);
        p.WarmRank = list => list.OrderBy(g => g.Name);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);

        Assert.Equal(5000, p.BudgetRoomMib(a));
        Assert.Equal(5000, p.BudgetRoomMib(b)); // 두 게임 합 10000 이 예산 16347 안
        Assert.Equal(16347 - 10000, p.BudgetRoomMib(c)); // 예산에 든 적 없는 게임은 남은 예산
        Assert.Null(Make(a).BudgetRoomMib(a)); // 순위가 없으면 제한 없음 (예산을 나누지 않음)

        _streamer.Calls.Clear();
        PrewarmResult r = p.Run(c, "auto after relocation", ignoreCooldown: true, roomMib: 1000);
        Assert.True(r.Ran);
        Assert.Equal(1000, Assert.Single(_streamer.Calls).MaxStreamMib); // 배정만큼만 읽음
    }

    [Fact]
    public void WarmedFollowsTheBudgetRankAndTheOldOrderWithoutOne()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings c = Game("c");
        _streamer.ZoneMib = 100;
        Prewarmer p = Make(a, b, c);
        Assert.True(p.Run(c, "service start").Ran);
        _now += TimeSpan.FromMinutes(1);
        Assert.True(p.Run(b, "service start").Ran);
        _now += TimeSpan.FromMinutes(1);
        Assert.True(p.Run(a, "service start").Ran); // 예열은 c, b, a 순: 마지막 예열 시각 순이면 a, b, c

        Assert.Equal(["a", "b", "c"], p.Warmed().Select(g => g.Name));
        Assert.Null(p.FirstPlayPriority()); // 예산 순위를 모르면 순서가 예열 시각뿐

        p.WarmRank = list => list.OrderBy(g => g.Name == "c" ? 0 : g.Name == "b" ? 1 : 2);

        Assert.Equal(["c", "b", "a"], p.Warmed().Select(g => g.Name));
        Assert.Equal("c", p.FirstPlayPriority()!.Name);

        p.WarmRank = list => list.Where(g => g.Name == "b"); // 순위에 없는 게임은 뒤에 예열 시각 순

        Assert.Equal(["b", "a", "c"], p.Warmed().Select(g => g.Name));
    }

    [Fact]
    public void SparseRangesAreLeftOutOfAMemoryPrewarmThatDoesNotFitAndKeptWhenItDoes()
    {
        _footprint = (9551, 3);
        GameSettings game = Game("sparsegame");
        _streamer.Planned = [new ReadOp(@"D:\Games\sparsegame\s.bin", 0, 3000 * Mib), new ReadOp(@"D:\Games\sparsegame\n.bin", 0, 10653 * Mib)];
        _isSparse = path => path.EndsWith("s.bin", StringComparison.Ordinal);
        Prewarmer p = Make(game);

        Assert.True(p.Run(game, "service start").Ran);

        Assert.Equal(8952, Assert.Single(_streamer.Calls).MaxStreamMib); // 상한은 그대로, 일반 파일 범위에 씀
        Assert.Contains(_log.Lines, l => l.Contains("sparsegame: 3000 MiB in sparse files left out of the memory prewarm", StringComparison.Ordinal));
        Assert.True(_streamer.Games.Single().SkipSparse);
        using var tmp = new TempDir();
        string sparse = tmp.Combine("s.bin");
        string normal = tmp.Combine("n.bin");
        using (SafeFileHandle h = File.OpenHandle(sparse, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE
        }

        File.WriteAllBytes(normal, new byte[10]);
        List<ReadOp> ops = [new(sparse, 0, 10), new(normal, 0, 10)];
        Assert.Equal([normal], SparseWarmExclusion.Without(game with { SkipSparse = true }, ops).Select(o => o.Path)); // 표시가 있는 사본만 스파스 구간이 빠짐
        Assert.Equal(2, SparseWarmExclusion.Without(game, ops).Count); // 다른 게임·표시 없는 계획은 영향이 없음

        _footprint = null; // 몫이 없으면 전부 들어가므로 표시가 풀림
        _streamer.Calls.Clear();
        _streamer.Games.Clear();
        _now += TimeSpan.FromHours(1);
        Assert.True(p.Run(game, "rewarm", ignoreCooldown: true).Ran);
        Assert.False(_streamer.Games.Single().SkipSparse);
    }

    private (Prewarmer Prewarmer, GameSettings Game) KeepableSparseGame(string name, string? antiCheat = null)
    {
        _footprint = (9551, 3);
        GameSettings game = Game(name) with { AntiCheat = antiCheat };
        _streamer.Planned = [new ReadOp($@"D:\Games\{name}\s.bin", 0, 3000 * Mib), new ReadOp($@"D:\Games\{name}\n.bin", 0, 10653 * Mib)];
        _isSparse = path => path.EndsWith("s.bin", StringComparison.Ordinal);
        return (Make(game), game);
    }

    [Fact]
    public void WhenTheKeepWhilePlayingCanKeepThemSparseRangesStayInAMemoryPrewarmThatDoesNotFitAndShareItsBudget()
    {
        (Prewarmer p, GameSettings game) = KeepableSparseGame("keepsparse");
        (_ladder, _keepSparse, _ranked) = (true, true, [new TraceEvent(@"D:\Games\keepsparse\n.bin", 0, 65536)]);

        Assert.True(p.Run(game, "service start").Ran);

        Assert.True(p.SparseKeepable(game));
        Assert.Equal(8952, Assert.Single(_streamer.Calls).MaxStreamMib); // 같은 상한, 스파스 범위를 빼지 않은 목록에서 앞쪽부터 들어가는 만큼
        Assert.Contains(_log.Lines, l => l.Contains("keepsparse: 3000 MiB in sparse files stay in the memory prewarm and share its budget by rank", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("left out of the memory prewarm", StringComparison.Ordinal));
        Assert.False(_streamer.Games.Single().SkipSparse);
        Assert.False(p.WithSparseMark(game).SkipSparse); // 표식이 없으니 보충·표본·유지 목록도 스파스 범위를 그대로 봄
    }

    [Fact]
    public void WhenKeepingThemSparseRangesShareTheAmountThatThePrewarmWouldHaveReadWithoutThemAndNeverAddToIt()
    {
        _footprint = (9551, 3);
        GameSettings game = Game("sparsefit2");
        _streamer.Planned = [new ReadOp(@"D:\Games\sparsefit2\s.bin", 0, 6000 * Mib), new ReadOp(@"D:\Games\sparsefit2
.bin", 0, 7653 * Mib)];
        _isSparse = path => path.EndsWith("s.bin", StringComparison.Ordinal);
        Prewarmer p = Make(game);
        (_ladder, _keepSparse, _ranked) = (true, true, [new TraceEvent(@"D:\Games\sparsefit2
.bin", 0, 65536)]);

        Assert.True(p.Run(game, "service start").Ran);

        // 스파스 6,000 MiB 를 빼면 7,653 MiB 로 전체가 들어가 그만큼 읽었을 것: 스파스를 넣어도 읽는 창은 그 양 (13,653 MiB 가 아님) 을 넘지 않고, 그 안에서 점수 순으로 나눔
        Assert.Equal(7653, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.False(_streamer.Games.Single().SkipSparse);
    }

    [Theory]
    [InlineData("keep_sparse off")]
    [InlineData("standby ladder off")]
    [InlineData("no ranked list")]
    [InlineData("anti-cheat forbids the hold")]
    public void WhenTheKeepWhilePlayingCannotKeepThemTheSparseRangesAreLeftOutAsBefore(string why)
    {
        (Prewarmer p, GameSettings game) = KeepableSparseGame("nokeep", why == "anti-cheat forbids the hold" ? "vanguard" : null);
        (_ladder, _keepSparse, _ranked) = (why != "standby ladder off", why != "keep_sparse off", why == "no ranked list" ? null : [new TraceEvent(@"D:\Games\nokeep\n.bin", 0, 65536)]);

        Assert.True(p.Run(game, "service start").Ran);

        Assert.False(p.SparseKeepable(game));
        Assert.Equal(8952, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.Contains(_log.Lines, l => l.Contains("nokeep: 3000 MiB in sparse files left out of the memory prewarm", StringComparison.Ordinal));
        Assert.True(_streamer.Games.Single().SkipSparse);
    }

    [Fact]
    public void WhenTheListFitsOnceSparseRangesAreLeftOutTheRewarmSamplesAreNotTakenFromThem()
    {
        _footprint = (9551, 3);
        GameSettings game = Game("sparsefit");
        _streamer.Planned = [new ReadOp(@"D:\Games\sparsefit\s.bin", 0, 6000 * Mib), new ReadOp(@"D:\Games\sparsefit\n.bin", 0, 7653 * Mib)];
        _isSparse = path => path.EndsWith("s.bin", StringComparison.Ordinal);
        Prewarmer p = Make(game);

        Assert.True(p.Run(game, "service start").Ran);

        // 13,653 > 상한 8,952 이지만 스파스 6,000 을 빼면 7,653 이라 전체가 들어감: 부분 예열이 아니라 제한 없이 읽음
        Assert.Equal(game.MaxStreamMib, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.True(_streamer.Games.Single().SkipSparse);
        Assert.Null(p.PartialInfo("sparsefit"));

        _ = p.ProbeFront(game); // 재예열·사다리 표본 (표시 없는 등록 게임을 넘겨도 표시가 붙음)
        Assert.Equal([true], _probedSparse);

        Assert.True(p.WithSparseMark(game).SkipSparse);
        Assert.False(p.WithSparseMark(Game("other")).SkipSparse); // 게임별 상태
    }

    [Fact]
    public async Task ARecordKeepsTheLowestAvailableRamSeenAfterTheStart()
    {
        using var dir = new TempDir();
        long avail = 20000;
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => new TickCounter(), () => Volatile.Read(ref avail), TimeSpan.FromMilliseconds(10));
        GameSettings game = Game("fp");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(61, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await Task.Delay(100);
        Volatile.Write(ref avail, 11000);
        await Task.Delay(150);
        Volatile.Write(ref avail, 15000);
        await Task.Delay(100);
        recorder.EndSession(game.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal(20000, sample.GetProperty("availableRamMib").GetInt64());
        Assert.Equal(11000, sample.GetProperty("minAvailableRamMib").GetInt64());
        Assert.Null(recorder.Footprint(game.Name)); // 시작 뒤 10 분을 다 보지 못한 짧은 플레이로는 몫을 정하지 않음
    }

    private sealed class TickCounter : IPlayDiskCounter
    {
        private int _reads;

        public string? PhysicalDisk => "disk1";

        public IReadOnlyList<PlayDiskCounters> Initial { get; } = [new(1, 0, 0, 0, 0, 0)];

        public IReadOnlyList<PlayDiskCounters>? Read()
        {
            int reads = Interlocked.Increment(ref _reads);
            return [new PlayDiskCounters(1, reads * 1024, (uint)(reads * 5), reads * 5_000_000L, reads * 1_000_000L, 0)];
        }

        public void Dispose()
        {
        }
    }
}
