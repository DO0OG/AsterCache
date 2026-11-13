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
    private bool _triggerAlive;

    private sealed class TestStreamer : IGameStreamer
    {
        public List<string> Calls { get; } = [];

        public List<(string Game, int MaxStreamMib)> Limits { get; } = [];

        public bool IncompleteNext { get; set; }

        public Action<GameSettings>? BeforeProgress { get; set; }

        public Func<GameSettings, long> SizeBytes { get; set; } = _ => 2 * Mib;

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add(game.Name);
            Limits.Add((game.Name, game.MaxStreamMib));
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
            ProcessRunning = _ => _triggerAlive,
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
        _available = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 창을 잡을 때 두는 여유를 더해 1904 MiB 창
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        Assert.True(prewarmer.Run(game, "test").Ran);
        Assert.Equal((1904L, 8000L), prewarmer.PartialInfo("a"));
        prewarmer.Recovery = new TestRecovery { Recovering = true };
        _available = 10000 + Prewarmer.FinalShrinkHeadroomMib;

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

    private GameSettings TriggeredGame(string name) => Game(name) with { Prewarm = Game(name).Prewarm with { TriggerExes = ["launcher.exe"] } };

    private static ProcessStart Launcher() => new(7, "launcher.exe", 1);

    // 다른 게임들이 실행 중일 때 (기본 "other" 하나) 트리거 예열이 거절된 상태를 만듭니다. processVisible 거짓이면 프로세스는 안 보이고 양보 등록만 있어 대여 거절로 거절됨.
    private (Prewarmer Prewarmer, DiskReadCoordinator Coordinator, AutoSettings Auto) RefusedTrigger(bool processVisible = true, params string[] blockers)
    {
        blockers = blockers.Length == 0 ? ["other"] : blockers;
        GameSettings game = TriggeredGame("a");
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        Prewarmer prewarmer = Make(coordinator, [game, .. blockers.Select(Game)]);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        foreach (string name in blockers)
        {
            if (processVisible)
            {
                _running.Add(name);
            }

            coordinator.Yield(Game(name).OriginRoot, "game started", name);
        }

        Assert.False(Assert.Single(prewarmer.OnProcessStart(Launcher())).Ran);
        Assert.True(prewarmer.HasPendingWarm("a"));
        return (prewarmer, coordinator, auto);
    }

    // 막던 게임들의 프로세스가 사라져 종료 유예에 들어선 틱 (Worker.NoteGameExits 와 같은 순서).
    private void GameGone(Prewarmer prewarmer, DiskReadCoordinator coordinator, params string[] names)
    {
        HashSet<string> grace = new(names, StringComparer.OrdinalIgnoreCase);
        _running.ExceptWith(names);
        coordinator.MarkExiting(grace);
        prewarmer.NoteGamesGone(grace);
    }

    [Fact]
    public void TriggerPrewarmRefusedByARunningGameRetriesOnceRightAfterItsProcessIsGone()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = true;

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto); // +10분 대기도, 유휴 60초도, 종료 후 유예도 기다리지 않음

        Assert.Equal(["a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void PendingRetryDoesNotTakeTheLockWhileATriggerPrewarmIsWaiting()
    {
        GameSettings game = Game("a");
        Prewarmer prewarmer = Make(null, game);
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "test").SkipReason);
        IDisposable mark = prewarmer.BeginTriggerWait([Game("b")]);

        ReachRetryAndIdle(prewarmer, auto);

        Assert.Equal(["a"], _streamer.Calls); // 재시도 기한과 유휴를 채웠어도 표시가 서 있는 동안은 읽지 않음
        Assert.True(prewarmer.HasPendingWarm("a"));
        mark.Dispose();
        _now += TimeSpan.FromSeconds(1);
        prewarmer.Tick(auto);
        Assert.Equal(["a", "a"], _streamer.Calls);
    }

    [Fact]
    public void TriggerRetryDoesNotTakeTheLockWhileATriggerPrewarmIsWaitingAndRunsAfterTheMarkIsGone()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = true;
        GameGone(prewarmer, coordinator, "other");
        IDisposable mark = prewarmer.BeginTriggerWait([Game("b")]);

        prewarmer.Tick(auto);

        Assert.Empty(_streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("a")); // 당겨진 재시도 표시는 남음
        mark.Dispose();
        prewarmer.Tick(auto);
        Assert.Equal(["a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void APartialStateGameStillRetriesTheRefusedTriggerRightAfterTheBlockerIsGone()
    {
        GameSettings game = TriggeredGame("a");
        _zones["a"] = 8000;
        _available = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 핫 집합 8000 MiB 중 앞부분만 읽는 부분 예열
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        Prewarmer prewarmer = Make(coordinator, game, Game("other"));
        AutoSettings auto = Auto();
        prewarmer.SetAuto(auto);
        Assert.True(prewarmer.Run(game, "test").Ran);
        Assert.True(prewarmer.SupplementPending("a"));
        _now += TimeSpan.FromHours(2); // 쿨다운이 지나 트리거가 게임 실행 중이라는 사유로 거절되게 함
        _running.Add("other");
        coordinator.Yield(Game("other").OriginRoot, "game started", "other");
        Assert.False(Assert.Single(prewarmer.OnProcessStart(Launcher())).Ran);
        Assert.True(prewarmer.HasPendingWarm("a"));
        _triggerAlive = true;

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto); // 보충이 유휴 60초를 기다리는 사이에도 트리거 재시도가 먼저 돎

        Assert.Equal(["a", "a"], _streamer.Calls);
    }

    [Fact]
    public void TriggerPrewarmRefusedByTheLeaseAloneRetriesWhenTheRegisteredGameLeavesToo()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger(processVisible: false);
        _triggerAlive = true;

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto);

        Assert.Equal(["a"], _streamer.Calls);
    }

    [Fact]
    public void TriggerRetryWaitsForEveryBlockingGameButNotForOnesThatAlreadyLeft()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger(true, "o1", "o2");
        _triggerAlive = true;

        GameGone(prewarmer, coordinator, "o1"); // o2 는 아직 실행 중
        prewarmer.Tick(auto);
        Assert.Empty(_streamer.Calls);

        coordinator.Resume(Game("o1").OriginRoot, "o1"); // o1 의 종료가 확정돼 등록이 풀림
        GameGone(prewarmer, coordinator, "o2");
        prewarmer.Tick(auto);

        Assert.Equal(["a"], _streamer.Calls); // 같은 틱에 함께 유예인 것이 아니어도 막던 게임이 모두 떠났으면 재시도
    }

    [Fact]
    public void TriggerRetryIsDroppedWhenTheRefusalIsOlderThanTheAgeLimit()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = true;
        _now += TriggerRetries.MaxAge + TimeSpan.FromMinutes(1);

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto);

        Assert.Empty(_streamer.Calls); // 기존 대기 규칙 (같은 디스크 등록 게임이 있는 동안 막힘)
    }

    [Fact]
    public void PulledInTriggerRetrySurvivesAnotherPathsFailure()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = true;
        GameGone(prewarmer, coordinator, "other");

        Assert.False(prewarmer.Run(TriggeredGame("a"), "rewarm").Ran); // 트리거가 아닌 경로는 유예 중에도 거절돼 실패를 기록
        prewarmer.Tick(auto);

        Assert.Equal(["a"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void TriggerRetryRunsOnlyOnceAndFallsBackToTheNormalWaitWhenRefusedAgain()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = true;
        _streamer.IncompleteNext = true;

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto);
        prewarmer.Tick(auto);

        Assert.Equal(["a"], _streamer.Calls); // 다시 거절돼도 같은 틱들에서 되풀이하지 않음
        Assert.True(prewarmer.HasPendingWarm("a"));
    }

    [Fact]
    public void TriggerRetryIsNotPulledInWhenTheTriggerProcessIsAlreadyGone()
    {
        (Prewarmer prewarmer, DiskReadCoordinator coordinator, AutoSettings auto) = RefusedTrigger();
        _triggerAlive = false;

        GameGone(prewarmer, coordinator, "other");
        prewarmer.Tick(auto);

        Assert.Empty(_streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("a")); // 기존 대기 규칙 (+재시도 간격, 유휴, 종료 후 유예) 그대로
    }

    [Fact]
    public void TriggerPrewarmRunsWhileOnlyAnExitingGameIsRegisteredButNormalRewarmDoesNot()
    {
        GameSettings game = TriggeredGame("a"), other = Game("other");
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        Prewarmer prewarmer = Make(coordinator, game, other);
        prewarmer.SetAuto(Auto());
        coordinator.Yield(other.OriginRoot, "game started", other.Name);
        coordinator.MarkExiting(new HashSet<string> { "other" }); // 프로세스는 이미 없고 종료 판정만 기다림

        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(game, "rewarm").SkipReason);
        Assert.Empty(_streamer.Calls);
        Assert.True(Assert.Single(prewarmer.OnProcessStart(Launcher())).Ran);
        Assert.Equal(["a"], _streamer.Calls);
    }

    [Fact]
    public void PendingBudgetRefusalWaitsForItsConfiguredRetryAndRetriesWhenTheShareOpens()
    {
        GameSettings a = Game("a") with { MaxStreamMib = 8192, MaxStreamSet = true };
        GameSettings b = Game("b") with { MaxStreamMib = 7000, MaxStreamSet = true };
        _zones["a"] = 8192;
        _zones["b"] = 6000;
        _rank = ["a", "b"];
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto() with { RewarmMinutes = 10 };
        prewarmer.SetAuto(auto);
        Rank(prewarmer);
        Assert.True(prewarmer.Run(a, "test").Ran);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(b, "test").SkipReason);

        var budget = prewarmer.WarmBudget()!.Value;
        Assert.Contains("a", budget.In);
        Assert.DoesNotContain("b", budget.In);
        Assert.Equal(8192, budget.AllocMib["a"]);

        _now += TimeSpan.FromMinutes(10);
        prewarmer.Tick(auto); // 기한은 지났지만 유휴 연속 시간이 아직 모자람
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto); // 예산 거절: 다음 판단은 다시 10분 뒤

        Assert.Equal(["a", "b"], _streamer.Calls);
        Assert.True(prewarmer.HasPendingWarm("b"));
        string refusal = "b: pending prewarm skipped (the cache budget of 8192 MiB has 8192 MiB assigned to warm targets; no allocation of at least 256 MiB fits this game)";
        Assert.Contains(_log.Lines, line => line.Contains(refusal, StringComparison.Ordinal));
        Assert.Single(_log.Lines, line => line.Contains("b: pending prewarm skipped", StringComparison.Ordinal));

        GameSettings largerBudgetA = a with { Prewarm = a.Prewarm with { MaxCacheMib = 8300 } };
        GameSettings largerBudgetB = b with { Prewarm = b.Prewarm with { MaxCacheMib = 8300 } };
        prewarmer.SetGames([largerBudgetA, largerBudgetB]); // cap 이 바뀌어도 정해진 기한 전에는 다시 판정하지 않음
        _now += TimeSpan.FromSeconds(30);
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(30);
        prewarmer.Tick(auto);
        Assert.Equal(["a", "b"], _streamer.Calls);
        Assert.Single(_log.Lines, line => line.Contains("b: pending prewarm skipped", StringComparison.Ordinal));

        _now += TimeSpan.FromMinutes(9);
        prewarmer.Tick(auto);

        Assert.Equal(["a", "b"], _streamer.Calls);
        Assert.Equal(2, _log.Lines.Count(line => line.Contains("b: pending prewarm skipped", StringComparison.Ordinal)));
        Assert.Contains(_log.Lines, line => line.Contains(
            "b: pending prewarm skipped (the cache budget of 8300 MiB has 8192 MiB assigned to warm targets; no allocation of at least 256 MiB fits this game)",
            StringComparison.Ordinal));

        prewarmer.SetGames([largerBudgetB]); // a 가 빠진 뒤 새 배정표에서 대기 게임에 예산이 열림
        prewarmer.Tick(auto);

        Assert.Equal(["a", "b", "b"], _streamer.Calls);
        Assert.False(prewarmer.HasPendingWarm("b"));
    }

    [Fact]
    public void ABudgetShareThatOpensDoesNotPullInARetryTimeSetByALaterFailure()
    {
        GameSettings a = Game("a") with { MaxStreamMib = 8192, MaxStreamSet = true };
        GameSettings b = Game("b") with { MaxStreamMib = 7000, MaxStreamSet = true };
        _zones["a"] = 8192;
        _zones["b"] = 6000;
        _rank = ["a", "b"];
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto() with { RewarmMinutes = 10 };
        prewarmer.SetAuto(auto);
        Rank(prewarmer);
        Assert.True(prewarmer.Run(a, "test").Ran);
        _streamer.IncompleteNext = true;
        Assert.Equal(Prewarmer.Cancelled, prewarmer.Run(b, "test").SkipReason);
        _now += TimeSpan.FromMinutes(10);
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto); // 예산 거절
        Assert.Single(_log.Lines, line => line.Contains("b: pending prewarm skipped", StringComparison.Ordinal));

        _now += TimeSpan.FromMinutes(10);
        _streamer.IncompleteNext = true;
        Assert.False(prewarmer.Run(b, "test").Ran); // 예산 거절 기한이 지난 뒤의 다른 실패가 새 기한을 잡음
        int calls = _streamer.Calls.Count;

        prewarmer.SetGames([b]); // a 가 빠져 예산이 열려도 새 기한은 그대로
        prewarmer.Tick(auto);
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto);

        Assert.Equal(calls, _streamer.Calls.Count);
        Assert.True(prewarmer.HasPendingWarm("b"));
    }

    [Fact]
    public void PendingWarmUsesTheRemainingBudgetAfterTheHigherRankedGameIsSupplemented()
    {
        GameSettings a = Game("a") with
        {
            Prewarm = Game("a").Prewarm with { MaxCacheMib = 16347 },
        };
        GameSettings b = Game("b");
        _zones["a"] = 10026;
        _zones["b"] = 8182;
        _rank = ["a", "b"];
        Prewarmer prewarmer = Make(null, a, b);
        AutoSettings auto = Auto(startupCap: 1024);
        prewarmer.SetAuto(auto);
        Rank(prewarmer);

        IReadOnlyList<PrewarmResult> started = prewarmer.RunServiceStart([a, b]);

        Assert.True(started.Single(result => result.Game == "a").Ran);
        Assert.Equal("startup byte cap", started.Single(result => result.Game == "b").SkipReason);
        Assert.Contains(("a", 1024), _streamer.Limits);
        Assert.True(prewarmer.SupplementPending("a"));
        Assert.True(prewarmer.HasPendingWarm("b"));

        Assert.False(prewarmer.SupplementWhenIdle(a, CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(prewarmer.SupplementWhenIdle(a, CancellationToken.None));
        Assert.False(prewarmer.SupplementPending("a"));

        var budget = prewarmer.WarmBudget()!.Value;
        Assert.Equal(16347, budget.CapMib);
        Assert.Equal(16347, budget.UsedMib);
        Assert.Equal(6321, budget.AllocMib["b"]);

        prewarmer.Tick(auto); // b 의 유휴 시계를 시작
        _now += TimeSpan.FromSeconds(61);
        prewarmer.Tick(auto);

        Assert.Contains(("b", 6321), _streamer.Limits);
        Assert.Equal((6321L, 8182L), prewarmer.PartialInfo("b"));
        Assert.False(prewarmer.HasPendingWarm("b"));
        Assert.Equal(16347, prewarmer.WarmBudget()!.Value.UsedMib);
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
