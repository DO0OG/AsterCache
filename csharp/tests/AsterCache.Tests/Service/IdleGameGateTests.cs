using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 같은 디스크의 다른 게임이 떠 있어도 읽기 증가율·디스크 처리량이 IdleNeeded 동안 낮으면 예열·재예열이 막히지 않는가 (대상 자신과 재배치는 그대로).
public sealed class IdleGameGateTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly GateStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly HashSet<string> _running = [];
    private readonly Dictionary<string, long?> _read = [];
    private double? _disk = 0;
    private double? _observed; // 읽기를 멈춘 관측 구간 (Sleep) 에서 재는 디스크 처리량, null 이면 Sleep 이 바꾸지 않음
    private int _slept;
    private bool _blindOnPause; // 읽기를 멈춘 구간에서 처리량을 읽지 못함 (null)
    private bool _giveUp; // 수집이 60 초 넘게 정체해 일반 표본은 Broken 이지만 게임 보호 판정(GameDiskSample)은 낡음 상태를 유지, 읽기를 멈춘 구간부터
    private bool _warming; // 그 null 이 카운터 고장이 아니라 표본이 아직 없거나 낡음 (Warming), 읽기를 멈춘 구간부터 (진입·묶음 사이 점검은 Ok)

    private sealed class GateStreamer : IGameStreamer
    {
        public int Calls { get; private set; }

        public Action<Action<long, long>>? OnBatch { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Calls++;
            progress?.Invoke(0, 100 * Mib);
            if (OnBatch is not null && progress is not null)
            {
                OnBatch(progress);
            }

            return (new StreamStats(100 * Mib, 1, 0, 0.01), !stop.IsCancellationRequested);
        }
    }

    private Prewarmer Make(params GameSettings[] games)
    {
        var p = new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => _running.Contains(g.Name),
            GameReadBytes = g => _read.GetValueOrDefault(g.Name),
            DiskBytesPerSecond = _ => _disk,
            DiskSample = _ => _warming && _slept > 0 ? (null, DiskActivity.SampleState.Warming) : (_disk, _disk is null ? DiskActivity.SampleState.Broken : DiskActivity.SampleState.Ok),
            GameDiskSample = _ => _giveUp && _slept > 0 ? (null, DiskActivity.SampleState.Stale) : _warming && _slept > 0 ? (null, DiskActivity.SampleState.Warming) : (_disk, _disk is null ? DiskActivity.SampleState.Broken : DiskActivity.SampleState.Ok),
            Sleep = (_, _) =>
            {
                _slept++;
                _disk = _blindOnPause ? null : _observed ?? _disk;
            },
            ZoneBytes = _ => 100 * Mib,
            TraceExists = _ => true,
            ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음
        }, _log, null, () => _now);
        return p;
    }

    private static GameSettings Game(string name, string root = @"D:\Games\") => Games.Make(name, name + ".exe") with { OriginRoot = root + name };

    private static AutoSettings Auto() => AutoSettings.Default with { RewarmMinutes = 10 };

    [Fact]
    public void AQuietRunningGameOnTheSameDiskDoesNotBlockPrewarmAfterSixtySeconds()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        _read["client"] = 1000 * Mib;

        Assert.False(p.Run(a, "test").Ran); // 첫 표본만 있어 아직 한가하지 않음
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.Run(a, "test").Ran);
        _read["client"] += 10 * Mib; // 30 초에 10 MiB: 1 MiB/s 미만
        _now += TimeSpan.FromSeconds(31);

        Assert.True(p.Run(a, "test").Ran);
        Assert.Equal(1, _streamer.Calls);
    }

    [Fact]
    public void ARisingReadRateKeepsTheOtherGameBusyAndRestartsTheIdleClock()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        _read["client"] = 0;
        p.Run(a, "test");
        _now += TimeSpan.FromSeconds(61);
        _read["client"] = 200 * Mib; // 61 초에 200 MiB

        Assert.False(p.Run(a, "test").Ran);
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.Run(a, "test").Ran); // 한가한 상태가 30 초뿐
        _now += TimeSpan.FromSeconds(31);

        Assert.True(p.Run(a, "test").Ran);
    }

    [Fact]
    public void AnUnreadableCounterIsTreatedAsBusy()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        _read["client"] = null;

        for (int i = 0; i < 4; i++)
        {
            Assert.False(p.Run(a, "test").Ran);
            _now += TimeSpan.FromSeconds(61);
        }

        Assert.Equal(0, _streamer.Calls);
    }

    [Fact]
    public void ABusyDiskKeepsTheOtherGameBlocking()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        (_read["client"], _disk) = (0, DiskActivity.BusyBytesPerSecond);
        p.Run(a, "test");
        _now += TimeSpan.FromSeconds(120);

        Assert.False(p.Run(a, "test").Ran);

        _disk = 0;
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.Run(a, "test").Ran); // 마지막 표본 뒤 처리량이 낮은 상태가 60 초 이상
    }

    [Fact]
    public void TheTargetGameItselfRunningAlwaysBlocks()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a);
        _running.Add("a");
        _read["a"] = 0;
        p.Run(a, "test");
        _now += TimeSpan.FromMinutes(5);

        PrewarmResult r = p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal("game is running", r.SkipReason);
        Assert.Equal(0, _streamer.Calls);
    }

    [Fact]
    public void AQuietOtherGameStillStopsTheReadWhenItsReadRateRisesBetweenBatches()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        _read["client"] = 0;
        p.Run(a, "test");
        _now += TimeSpan.FromSeconds(61);
        _streamer.OnBatch = progress =>
        {
            _read["client"] += 300 * Mib; // 읽는 도중 그 게임이 크게 읽기 시작함
            _now += TimeSpan.FromSeconds(2);
            progress(10 * Mib, 100 * Mib);
        };

        PrewarmResult r = p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
    }

    // 한가한 client 가 도는 같은 디스크에서 a 를 예열한 상태 (a 는 Warmed): 첫 표본은 t0, 61 초 뒤 다시 읽을 준비가 됨.
    private Prewarmer WarmedWithQuietClient(out AutoSettings auto)
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        Assert.True(p.Run(a, "test").Ran);
        _running.Add("client");
        _read["client"] = 0;
        auto = Auto();
        p.Tick(auto); // client 의 첫 표본
        _now += TimeSpan.FromSeconds(61);
        return p;
    }

    [Fact]
    public void OurOwnReadIsNotTakenForAQuietGamesDiskLoadWhenTickRunsDuringTheStream()
    {
        Prewarmer p = WarmedWithQuietClient(out AutoSettings auto);
        _streamer.OnBatch = progress =>
        {
            _now += TimeSpan.FromSeconds(2);
            _disk = 25 * Mib; // 2 초에 50 MiB 읽은 우리 읽기 (25 MiB/s) 가 디스크 처리량의 전부
            progress(50 * Mib, 100 * Mib);
            p.Tick(auto); // 트리거 예열과 유지 틱이 겹침
        };

        PrewarmResult r = p.Run(Game("a"), "test", ignoreCooldown: true);

        Assert.True(r.Ran); // 우리 읽기만으로는 양보하지 않음
        _disk = 0;
        Assert.Empty(p.BusyOthers(Game("a"))); // 한가함 기록이 지워지지 않음
    }

    // 읽는 도중 10 초가 지난 묶음 경계에서 우리 읽기 100 MiB/s + 외부 부하가 섞인 처리량을 보이게 하고, 읽기를 멈춘 구간의 처리량은 observed 로.
    private PrewarmResult RunWithPausedLoad(Prewarmer p, double observed)
    {
        _observed = observed;
        _streamer.OnBatch = progress =>
        {
            _now += TimeSpan.FromSeconds(11);
            _disk = 120 * Mib; // 우리 읽기 100 MiB/s + 외부 20 MiB/s: 읽는 중의 처리량으로는 나눌 수 없음
            progress(50 * Mib, 100 * Mib);
        };

        return p.Run(Game("a"), "test", ignoreCooldown: true);
    }

    [Fact]
    public void AnExternalLoadSeenWhileTheReadIsPausedStopsTheRead()
    {
        Prewarmer p = WarmedWithQuietClient(out _);

        PrewarmResult r = RunWithPausedLoad(p, 20 * Mib); // 멈춘 구간의 처리량 20 MiB/s 는 모두 외부 부하

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(1, _slept);
    }

    [Fact]
    public void NoSampleYetWhileTheReadIsPausedStopsTheRead()
    {
        _blindOnPause = true;
        _warming = true;
        PrewarmResult r = RunWithPausedLoad(WarmedWithQuietClient(out _), 0);
        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
    }

    [Fact]
    public void ASampleStalledPastTheGiveUpLimitWhileTheReadIsPausedStillStopsTheRead()
    {
        _blindOnPause = true; // 일반 판정에서는 Broken (위 시험: 계속 읽음) 이지만
        _giveUp = true; // 게임 보호 판정에서는 정체가 고장이 아니므로 바쁨 유지
        PrewarmResult r = RunWithPausedLoad(WarmedWithQuietClient(out _), 0);
        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
    }

    [Fact]
    public void ABrokenCounterWhileTheReadIsPausedKeepsTheReadGoing()
    {
        _blindOnPause = true; // 카운터 고장 (첫 표본 전이 아님): 기존 동작, 예열이 영구히 막히지 않음
        Assert.True(RunWithPausedLoad(WarmedWithQuietClient(out _), 0).Ran);
    }

    [Fact]
    public void ALowThroughputWhileTheReadIsPausedKeepsTheReadGoing()
    {
        Prewarmer p = WarmedWithQuietClient(out _);

        PrewarmResult r = RunWithPausedLoad(p, 1 * Mib); // 우리 읽기가 높아도 멈춘 구간이 한가하면 계속

        Assert.True(r.Ran);
        Assert.Equal(1, _slept);
    }

    [Fact]
    public void TheReadPausesToMeasureEvenWhenNoOtherGameRunsAndYieldsInsteadOfStopping()
    {
        GameSettings a = Game("a");
        Prewarmer alone = Make(a);
        _streamer.OnBatch = progress =>
        {
            _now += TimeSpan.FromSeconds(11);
            _disk = 20 * Mib; // 멈춘 구간의 처리량이 바쁨 기준 이상이어도 다른 게임이 없으면 취소하지 않고 양보
            progress(50 * Mib, 100 * Mib);
        };

        Assert.True(alone.Run(a, "test").Ran);
        Assert.Equal(1, _slept);
    }

    [Fact]
    public void TheReadIsNotPausedToMeasureWhenTheIntervalIsShort()
    {
        Prewarmer p = WarmedWithQuietClient(out _);
        _streamer.OnBatch = progress =>
        {
            _now += TimeSpan.FromSeconds(5); // 아직 10 초 전
            progress(50 * Mib, 100 * Mib);
        };
        Assert.True(p.Run(Game("a"), "test", ignoreCooldown: true).Ran);
        Assert.Equal(0, _slept);
    }

    [Fact]
    public void AnUnreadableDiskCounterBlocksTheEntryButNotTheMiddleOfTheRead()
    {
        Prewarmer p = WarmedWithQuietClient(out _);
        int before = _streamer.Calls;
        _disk = null; // 진입 시점의 조회 실패는 바쁨
        Assert.False(p.Run(Game("a"), "test", ignoreCooldown: true).Ran);
        Assert.Equal(before, _streamer.Calls);

        _disk = 0;
        _now += TimeSpan.FromSeconds(61);
        _streamer.OnBatch = progress =>
        {
            _disk = null; // 읽는 도중 조회 실패 (관측 구간 포함): 진행 판단에서 제외
            _now += TimeSpan.FromSeconds(11);
            progress(50 * Mib, 100 * Mib);
        };

        Assert.True(p.Run(Game("a"), "test", ignoreCooldown: true).Ran);
    }

    [Fact]
    public void RelocationStillBlocksEvenWhenTheOtherGameIsQuiet()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a, Game("client"));
        _running.Add("client");
        _read["client"] = 0;
        p.Run(a, "test");
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.TryBeginRelocation("a")); // 재배치가 시작함

        PrewarmResult r = p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.RelocationRunning, r.SkipReason);
        Assert.Equal(0, _streamer.Calls);
    }

    [Fact]
    public void RewarmChecksTheOtherTargetsWhileAGameRunsAndSkipsOnlyTheBusyOnes()
    {
        GameSettings a = Game("a");
        GameSettings other = Game("e", @"E:\Games\"); // 다른 디스크
        Prewarmer p = Make(a, other, Game("client"));
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(other, "service start").Ran);
        _running.Add("client");
        _read["client"] = 0;
        AutoSettings auto = Auto();
        p.Tick(auto);
        int before = _streamer.Calls;

        _now += TimeSpan.FromMinutes(5);
        p.Tick(auto); // 표본이 쌓임
        _now += TimeSpan.FromMinutes(5.5);
        p.Tick(auto); // 점검: client 는 한가하고 e 는 다른 디스크

        Assert.Equal(before + 2, _streamer.Calls); // a (client 가 한가함) 와 e 모두 식어서 다시 읽음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("rewarm check skipped: a game is running", StringComparison.Ordinal));
    }

    [Fact]
    public void RewarmSkipsATargetWhoseSameDiskGameKeepsReading()
    {
        GameSettings a = Game("a");
        GameSettings other = Game("e", @"E:\Games\");
        Prewarmer p = Make(a, other, Game("client"));
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(other, "service start").Ran);
        _running.Add("client");
        _read["client"] = 0;
        AutoSettings auto = Auto();
        p.Tick(auto);
        int before = _streamer.Calls;

        _now += TimeSpan.FromMinutes(5);
        _read["client"] = 5000 * Mib; // 계속 읽는 중
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(5.5);
        _read["client"] = 10000 * Mib;
        p.Tick(auto);

        Assert.Equal(before + 1, _streamer.Calls); // 다른 디스크의 e 만 다시 읽음
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check skipped: a game is running on the same drive", StringComparison.Ordinal));
    }
}
