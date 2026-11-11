using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class PendingPrewarmTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly TestStreamer _streamer = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _zones = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private long _available = 16000;
    private long _uptimeMs = 10_000_000;
    private bool _coldProbe;
    private int _probeCalls;
    private int _observedReadCalls;
    private string[] _rank = ["a", "b", "other"];
    private Func<GameSettings, double?> _diskBytesPerSecond = _ => 0;
    private Action? _onWarmRank;
    private bool _noTrace;

    private sealed class TestStreamer : IGameStreamer
    {
        public List<string> Calls { get; } = [];

        public bool IncompleteNext { get; set; }

        public Action<GameSettings>? BeforeProgress { get; set; }

        public Func<GameSettings, long> SizeBytes { get; set; } = _ => 2 * Mib;

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add(game.Name);
            long requested = Math.Min(SizeBytes(game), game.MaxStreamSet ? (long)game.MaxStreamMib * Mib : long.MaxValue);
            BeforeProgress?.Invoke(game);
            progress?.Invoke(0, requested);
            if (IncompleteNext)
            {
                IncompleteNext = false;
                return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
            }

            if (stop.IsCancellationRequested)
            {
                return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
            }

            return (new StreamStats(requested, 1, 0, 0.01), true);
        }
    }

    private GameSettings Game(string name) => Games.Make(name, name + ".exe") with { OriginRoot = @"D:\Games\" + name };

    private Prewarmer Make(DiskReadCoordinator? coordinator = null, params GameSettings[] games)
    {
        _streamer.SizeBytes = g => (_zones.TryGetValue(g.Name, out long mib) ? mib : 2) * Mib;
        PrewarmEnv env = new()
        {
            AvailableRamMib = () => _available,
            UptimeMs = () => _uptimeMs,
            GameRunning = g => _running.Contains(g.Name),
            SameDisk = (_, _) => true,
            ZoneBytes = g =>
            {
                if (_noTrace && g.Name == "no-trace")
                {
                    return 2 * Mib;
                }

                long bytes = _zones.TryGetValue(g.Name, out long mib) ? mib * Mib : 2 * Mib;
                return Math.Min(bytes, g.StreamCapBytes(bytes));
            },
            TraceExists = g => !_noTrace || g.Name != "no-trace",
            PhysicalRamBytes = () => 16L << 30,
            DiskBytesPerSecond = g => _diskBytesPerSecond(g),
            ObservedBytes = _ => { _observedReadCalls++; return null; },
            ObservedBytesIfCached = _ => null,
            ProbeReadMs = _ =>
            {
                _probeCalls++;
                return _coldProbe ? new ProbeResult(8, 8, 5.0) : null;
            },
        };
        return new Prewarmer(games, _streamer, env, _log, now: () => _now, coordinator: coordinator);
    }

    private Prewarmer MakeCold(GameSettings hot, GameSettings cold, List<long> coldReadsMib)
    {
        _streamer.SizeBytes = g => (_zones.TryGetValue(g.Name, out long mib) ? mib : 2) * Mib;
        PrewarmEnv env = new()
        {
            AvailableRamMib = () => _available,
            PhysicalRamBytes = () => 16L << 30,
            GameRunning = g => _running.Contains(g.Name),
            SameDisk = (_, _) => true,
            ZoneBytes = g => g.Name == cold.Name ? null : (_zones.TryGetValue(g.Name, out long mib) ? mib : 2) * Mib,
            TraceExists = g => g.Name != cold.Name,
            RawTraceExists = _ => false,
            DiskBytesPerSecond = g => _diskBytesPerSecond(g),
            ObservedBytes = _ => null,
            ColdPlanner = (_, _, _, _) =>
            {
                List<ReadOp> ops = [new ReadOp(Path.Combine(cold.OriginRoot, "f.bin"), 0, (int)Mib)];
                return new ColdPlan("full", ops, Mib, null);
            },
            ColdRead = (ops, _) =>
            {
                long bytes = ops.Sum(op => (long)op.Length);
                if (coldReadsMib.Count == 0)
                {
                    coldReadsMib.Add(0); // 첫 시도는 0바이트 취소
                    return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
                }

                coldReadsMib.Add(bytes >> 20);
                return (new StreamStats(bytes, ops.Count, 0, 0.01), true);
            },
        };
        return new Prewarmer([hot, cold], _streamer, env, _log, now: () => _now);
    }

    private static AutoSettings Auto(int startupCap = 1024) => AutoSettings.Default with
    {
        RewarmMinutes = 1,
        StartupWarmMaxMib = startupCap,
    };

    private void Rank(Prewarmer prewarmer) => prewarmer.WarmRank = games =>
    {
        Interlocked.Exchange(ref _onWarmRank, null)?.Invoke();
        return _rank.Select(name => games.FirstOrDefault(game => game.Name == name)).OfType<GameSettings>();
    };

    private void ReachRetryAndIdle(Prewarmer prewarmer, AutoSettings auto)
    {
        _now += TimeSpan.FromMinutes(1);
        prewarmer.Tick(auto); // 재시도 간격은 지났지만 연속 유휴 60초는 아직 아님
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto);
    }

    [Fact]
    public void ZeroByteCancellationIsRetriedAtIdleAndOnlySuccessBecomesLastWarm()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;

        PrewarmResult first = prewarmer.Run(game, "test");

        Assert.Equal(Prewarmer.Cancelled, first.SkipReason);
        Assert.True(prewarmer.HasPendingWarm("a"));
        Assert.Null(prewarmer.LastWarmed("a"));

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a", "a"], _streamer.Calls);
        Assert.NotNull(prewarmer.LastWarmed("a"));
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void SupplementThatFindsNothingNewToReadClearsThePendingWarm()
    {
        GameSettings game = Game("a");
        _zones["a"] = 8000;
        _available = 6000; // 핫 집합 8000 MiB 중 들어가는 앞부분만 읽는 부분 예열
        Prewarmer prewarmer = Make(null, game);
        prewarmer.SetAuto(Auto());
        Assert.True(prewarmer.Run(game, "test").Ran);
        Assert.NotNull(prewarmer.PartialInfo("a"));
        _now += TimeSpan.FromMinutes(2);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.RewarmGame(game).SkipReason);
        Assert.True(prewarmer.HasPendingWarm("a"));
        int calls = _streamer.Calls.Count;

        Assert.False(prewarmer.Supplement(game, CancellationToken.None)); // 들어가는 만큼은 이미 읽음

        Assert.Equal(calls, _streamer.Calls.Count);
        Assert.Contains(_log.Lines, line => line.Contains("supplement prewarm skipped (the", StringComparison.Ordinal));
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void PendingSupplementCanContinueAfterSixtyIdleSecondsWhileDriveRecovers()
    {
        GameSettings game = Game("a");
        _zones["a"] = 8000;
        _available = 6000;
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        Assert.True(prewarmer.Run(game, "test").Ran);
        Assert.Equal((1904L, 8000L), prewarmer.PartialInfo("a"));
        prewarmer.Recovery = new TestRecovery { Recovering = true };
        _available = 10000;

        Assert.False(prewarmer.SupplementWhenIdle(game, CancellationToken.None));
        Assert.Single(_streamer.Calls);
        _now += TimeSpan.FromSeconds(61);

        Assert.True(prewarmer.SupplementWhenIdle(game, CancellationToken.None));

        Assert.Equal(["a", "a"], _streamer.Calls);
        Assert.Equal((5904L, 8000L), prewarmer.PartialInfo("a"));
    }

    [Fact]
    public void StartupCapSkippedGameIsRetriedWithoutChangingStartupCap()
    {
        GameSettings a = Game("a"), b = Game("b");
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto(startupCap: 2);
        prewarmer.SetAuto(auto);
        Rank(prewarmer);

        IReadOnlyList<PrewarmResult> started = prewarmer.RunServiceStart([a, b]);

        Assert.True(started.Single(result => result.Game == "a").Ran);
        Assert.Equal("startup byte cap", started.Single(result => result.Game == "b").SkipReason);
        Assert.True(prewarmer.HasPendingWarm("b"));
        Assert.Equal(["a"], _streamer.Calls);
        Assert.Equal(2, auto.StartupWarmMaxMib);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Contains("b", _streamer.Calls);
        Assert.NotNull(prewarmer.LastWarmed("b"));
        Assert.False(prewarmer.HasPendingWarm("b"));
        Assert.Equal(2, auto.StartupWarmMaxMib);
    }

    [Fact]
    public void SameDiskGameRunningBlocksPendingWarmUntilItExits()
    {
        GameSettings game = Game("a"), other = Game("other");
        Prewarmer prewarmer = Make(null, game, other);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        _running.Add("other");

        PrewarmResult first = prewarmer.Run(game, "test");

        Assert.Equal("game is running", first.SkipReason);
        Assert.True(prewarmer.HasPendingWarm("a"));
        Assert.Empty(_streamer.Calls);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Empty(_streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("a"));

        _running.Remove("other");
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto);

        Assert.Equal("a", Assert.Single(_streamer.Calls));
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void CoordinatorActiveGameBlocksPendingWarmUntilTheDiskResumes()
    {
        GameSettings game = Game("a"), other = Game("other");
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        Prewarmer prewarmer = Make(coordinator, game, other);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "test").SkipReason);
        coordinator.Yield(other.OriginRoot, "game started", other.Name);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a"], _streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("a"));

        coordinator.Resume(other.OriginRoot, other.Name);
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto);

        Assert.Equal(["a", "a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void PendingWarmCannotTakeAnotherGamesCacheBudgetShare()
    {
        GameSettings a = Game("a") with { MaxStreamMib = 8192, MaxStreamSet = true };
        GameSettings b = Game("b") with { MaxStreamMib = 7000, MaxStreamSet = true };
        _zones["a"] = 8192;
        _zones["b"] = 6000;
        _rank = ["a", "b"];
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        Rank(prewarmer);
        Assert.True(prewarmer.Run(a, "test").Ran);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(b, "test").SkipReason);

        var budget = prewarmer.WarmBudget()!.Value;
        Assert.Contains("a", budget.In);
        Assert.DoesNotContain("b", budget.In);
        Assert.Equal(8192, budget.AllocMib["a"]);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a", "b"], _streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("b"));
        Assert.Contains(_log.Lines, line => line.Contains("b: pending prewarm skipped (the cache budget of 8192 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void ColdGameIsNeverPendingWhenCancelledOrSkippedByTheStartupCap()
    {
        GameSettings hot = Game("a");
        GameSettings cold = Game("cold") with { OriginRoot = Path.GetTempPath() };
        _rank = ["a", "cold"];
        List<long> coldReadsMib = [];
        Prewarmer prewarmer = MakeCold(hot, cold, coldReadsMib);
        AutoSettings auto = Auto(startupCap: 2) with { FullPrewarm = true, FullPrewarmMaxMib = 8192, EngineProfiles = false };
        prewarmer.SetAuto(auto);
        Rank(prewarmer);

        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(cold, "test").SkipReason); // 첫 읽기가 0바이트로 취소됨
        IReadOnlyList<PrewarmResult> started = prewarmer.RunServiceStart([hot, cold]);

        Assert.Equal("startup byte cap", started.Single(result => result.Game == "cold").SkipReason);
        Assert.False(prewarmer.HasPendingWarm(cold.Name));

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal([0L], coldReadsMib); // 유휴 틱이 기록 없는 게임을 다시 읽지 않음
        Assert.Null(prewarmer.LastWarmed(cold.Name));
        Assert.False(prewarmer.HasPendingWarm(cold.Name));
    }

    [Fact]
    public void PendingWarmRetriesWhileDriveRecoversAfterTheIdleGate()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        var recovery = new TestRecovery { Recovering = true };
        prewarmer.Recovery = recovery;
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "test").SkipReason);
        _observedReadCalls = 0;

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a", "a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm(game.Name));
        Assert.True(_observedReadCalls > 0); // pending 상태여도 회복 중 예산 표본을 읽음
    }

    [Fact]
    public void PendingGameWithEarlierSuccessRewarmsDuringDriveRecovery()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        var recovery = new TestRecovery();
        prewarmer.Recovery = recovery;
        prewarmer.SetAuto(auto);
        Assert.True(prewarmer.Run(game, "test").Ran);
        _now += TimeSpan.FromMinutes(2); // 방금 끝난 예열로 보는 창을 지남
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.RewarmGame(game).SkipReason);
        Assert.True(prewarmer.HasPendingWarm(game.Name));
        Assert.NotNull(prewarmer.LastWarmed(game.Name)); // 이전 성공 이력이 남아 재예열 점검 대상
        recovery.Recovering = true;
        _coldProbe = true; // 표본이 식어 있음
        _now += TimeSpan.FromHours(1);
        _uptimeMs = 0; // 대기 재시도는 부팅 대기 중이지만 재예열 표본은 회복 중에도 확인
        prewarmer.Tick(auto); // 재예열 점검 주기 시작
        _now += TimeSpan.FromMinutes(2);
        prewarmer.Tick(auto);

        Assert.Equal(["a", "a", "a"], _streamer.Calls);
        Assert.True(_probeCalls > 0);
        Assert.False(prewarmer.HasPendingWarm(game.Name));
    }

    [Fact]
    public void GameWithoutPendingWarmStillRewarmsWhileTheDriveRecovers()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.Recovery = new TestRecovery { Recovering = true };
        prewarmer.SetAuto(auto);
        Assert.True(prewarmer.Run(game, "test").Ran);
        _coldProbe = true;
        _now += TimeSpan.FromHours(1);
        prewarmer.Tick(auto);
        _now += TimeSpan.FromMinutes(2);
        prewarmer.Tick(auto);

        Assert.Equal(["a", "a"], _streamer.Calls); // 대기 중이 아닌 게임의 기존 재예열은 회복 중에도 읽음
        Assert.False(prewarmer.HasPendingWarm(game.Name));
    }

    [Fact]
    public void PendingRetryStillStopsWhenTheGameStartsBeforeTheFirstRead()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        prewarmer.Recovery = new TestRecovery { Recovering = true };
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "test").SkipReason);
        _streamer.BeforeProgress = _ => _running.Add(game.Name);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a", "a"], _streamer.Calls);
        Assert.Null(prewarmer.LastWarmed(game.Name));
        Assert.True(prewarmer.HasPendingWarm(game.Name));
        Assert.Contains(_log.Lines, line => line.Contains("game started", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveringPendingWarmStillWaitsForIdleAndTheSameDiskTurn()
    {
        GameSettings held = Game("a") with { OriginRoot = @"E:\Games\a" };
        GameSettings other = Game("b");
        Prewarmer prewarmer = Make(null, held, other);
        AutoSettings auto = Auto();
        prewarmer.Recovery = new TestRecovery { Recovering = true, Drive = "E:" };
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(held, "test").SkipReason);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(other, "test").SkipReason);

        _now += TimeSpan.FromMinutes(1);
        prewarmer.Tick(auto); // 재시도 기한이 지나 b 의 유휴 표본이 시작됨
        _now += TimeSpan.FromSeconds(30); // 틱 간격이 IdleNeeded 보다 짧아도 a 의 보류가 b 의 누적을 지우지 않아야 함
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(30);
        prewarmer.Tick(auto);

        Assert.Equal(["a", "b", "a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm(held.Name));
        Assert.True(prewarmer.HasPendingWarm(other.Name));
    }

    [Fact]
    public void OnlyOnePendingWarmPerPhysicalDiskStartsDuringOneTick()
    {
        GameSettings a = Game("a"), b = Game("b");
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(a, "test").SkipReason);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(b, "test").SkipReason);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a", "b", "a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm(a.Name));
        Assert.True(prewarmer.HasPendingWarm(b.Name));
    }

    [Fact]
    public async Task TriggerSuccessAfterMaintenanceSnapshotsPendingWarmIsNotRepeated()
    {
        GameSettings game = Game("a") with
        {
            Prewarm = Game("a").Prewarm with { TriggerExes = ["launcher.exe"] },
        };
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        Rank(prewarmer);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "test").SkipReason);
        _now += TimeSpan.FromMinutes(1);
        prewarmer.Tick(auto); // 재시도 기한이 지나 첫 유휴 표본을 남깁니다.
        _now += TimeSpan.FromSeconds(61); // 아직 다음 유지 틱을 돌리지 않아 대기는 남아 있습니다.

        using var snapshotTaken = new ManualResetEventSlim();
        using var allowMaintenance = new ManualResetEventSlim();
        _onWarmRank = () =>
        {
            snapshotTaken.Set();
            allowMaintenance.Wait();
        };
        Task maintenance = Task.Run(() => prewarmer.Tick(auto));
        try
        {
            Assert.True(await Task.Run(() => snapshotTaken.Wait(TimeSpan.FromSeconds(5))));

            PrewarmResult trigger = Assert.Single(prewarmer.OnProcessStart(new ProcessStart(1, "launcher.exe", 0)));
            Assert.True(trigger.Ran);
            Assert.False(prewarmer.HasPendingWarm(game.Name));
        }
        finally
        {
            allowMaintenance.Set();
        }

        await maintenance.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["a", "a"], _streamer.Calls);
    }

    [Fact]
    public void PermanentNoTraceRefusalDoesNotBecomePending()
    {
        GameSettings game = Game("no-trace");
        _noTrace = true;
        Prewarmer prewarmer = Make(null, game);

        PrewarmResult result = prewarmer.Run(game, "test");

        Assert.Equal("no trace file", result.SkipReason);
        Assert.False(prewarmer.HasPendingWarm(game.Name));
    }

    [Fact]
    public void CancellationLogIncludesTheFirstMemoryStopReason()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        _streamer.BeforeProgress = _ => _available = 0;

        PrewarmResult result = prewarmer.Run(game, "test");

        Assert.Equal(Prewarmer.Cancelled, result.SkipReason);
        Assert.Contains(_log.Lines, line => line.Contains(
            "a: prewarm cancelled (test) after 0 MiB (memory reserve: available 0 MiB, final set 2 MiB + reserve 4096 MiB)",
            StringComparison.Ordinal));
    }

    private sealed class TestRecovery : IDriveRecovery
    {
        public bool Recovering { get; set; }

        public string? Drive { get; init; } // 지정하면 그 드라이브만 회복 중

        public bool IsRecovering(string drive) => Recovering && (Drive is null || drive.StartsWith(Drive, StringComparison.OrdinalIgnoreCase));

        public bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit) => null;
    }
}
