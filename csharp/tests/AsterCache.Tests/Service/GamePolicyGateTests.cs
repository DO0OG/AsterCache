using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임별 정책 (games[].mode, games[].paused) 과 auto.start_stream_enabled · auto.relocate_hours 가 서비스 진입점에 적용되는가.
/// 정책은 전역 스위치·안티치트 게이트와 AND 로만 보므로 이 시험은 정책만 바꾸고 나머지는 기본값으로 둡니다.
public sealed class GamePolicyGateTests
{
    private const long Mib = 1L << 20;
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private long _avail = 64000;
    private long _zoneMib = 100;

    private static GameSettings Policy(string name, GameMode mode = GameMode.Inherit, bool paused = false) =>
        Games.Make(name, name + ".exe") with { Mode = mode, Paused = paused, Prewarm = PrewarmSettings.Default with { TriggerExes = ["launcher.exe"] } };

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => false,
        ZoneBytes = _ => _zoneMib * Mib,
        TraceExists = _ => true,
        PhysicalRamBytes = () => 8L << 30,
    }, _log, null, () => _now);

    // ---- 예열 계열 진입점 ----

    [Theory]
    [InlineData(GameMode.Inherit, false, true)]
    [InlineData(GameMode.PrewarmOnly, false, true)]
    [InlineData(GameMode.RelocateOnly, false, false)]
    [InlineData(GameMode.Off, false, false)]
    [InlineData(GameMode.Inherit, true, false)]
    [InlineData(GameMode.PrewarmOnly, true, false)]
    public void EveryPrewarmEntryPointFollowsThePolicy(GameMode mode, bool paused, bool reads)
    {
        GameSettings g = Policy("g", mode, paused);
        int Reads(Action<Prewarmer> act)
        {
            _streamer.Calls.Clear();
            act(Make(g));
            return _streamer.Calls.Count;
        }

        int expected = reads ? 1 : 0;
        Assert.Equal(expected, Reads(p => p.Run(g, "test")));
        Assert.Equal(expected, Reads(p => p.RunAll([g], "test")));
        Assert.Equal(expected, Reads(p => p.OnProcessStart(new ProcessStart(1, "launcher.exe", 0))));
        Assert.Equal(expected, Reads(p => p.RunServiceStart()));
        Assert.Equal(expected, Reads(p => p.RewarmGame(g)));
        Assert.Equal(expected, Reads(p => p.RunServiceStart([g], default, _ => true, name => name == "g" ? _now : null)));
    }

    [Fact]
    public void ABlockedRunKeepsTheStatusHistoryAndReportsThePolicyReason()
    {
        GameSettings g = Policy("g");
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "test").Ran);
        DateTimeOffset? warmedAt = p.LastWarmed("g");

        p.SetGames([g with { Paused = true }]);
        PrewarmResult blocked = p.Run(g, "test");

        Assert.False(blocked.Ran);
        Assert.Equal(Prewarmer.GamePolicySkip, blocked.SkipReason);
        Assert.Equal(warmedAt, p.LastWarmed("g")); // 마지막 예열 이력은 그대로
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ABlockedBatchGameLeavesTheBatchWhileTheOtherStillReadsInTheGivenOrder()
    {
        GameSettings a = Policy("a", paused: true), b = Policy("b");
        Prewarmer p = Make(a, b);

        IReadOnlyList<PrewarmResult> results = p.RunAll([a, b], "test");

        Assert.Equal(["a", "b"], results.Select(r => r.Game));
        Assert.Equal((false, Prewarmer.GamePolicySkip), (results[0].Ran, results[0].SkipReason));
        Assert.True(results[1].Ran);
        Assert.Equal(["b"], _streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void AServiceStartSlotIsNotSpentOnAGameThePolicyBlocks()
    {
        GameSettings a = Policy("a", GameMode.Off), b = Policy("b");
        Prewarmer p = Make(a, b);
        p.SetAuto(AutoSettings.Default with { StartupWarmGames = 1 });

        p.RunServiceStart([a, b], default, _ => true, name => _now.AddMinutes(name == "a" ? -1 : -2)); // a 가 더 최근이지만 막혀 있음

        Assert.Equal(["b"], _streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void ThePolicyReasonIsLoggedOncePerStateAndAgainAfterItWasAllowedBetween()
    {
        GameSettings g = Policy("g", paused: true);
        Prewarmer p = Make(g);

        p.Run(g, "test");
        p.Run(g, "test");
        p.RunAll([g], "test");

        Assert.Single(_log.Lines, l => l.Contains("g: prewarm [test] skipped (game policy: paused)", StringComparison.Ordinal));

        p.SetGames([g with { Paused = false }]);
        p.SetGames([g with { Mode = GameMode.Off, Paused = false }]);

        Assert.Single(_log.Lines, l => l.Contains("g: prewarm, supplement and pin stopped (game policy: off)", StringComparison.Ordinal));
    }

    [Fact]
    public void WarmFilesReadsNothingForABlockedGame()
    {
        GameSettings g = Policy("g", GameMode.RelocateOnly);
        Prewarmer p = Make(g);

        long read = p.WarmFiles(g, [Path.Combine(g.OriginRoot, "a.bin")], 1L << 30, CancellationToken.None);

        Assert.Equal(0, read);
        Assert.Contains(_log.Lines, l => l.Contains("g: warm files skipped (game policy: relocate_only)", StringComparison.Ordinal));
    }

    [Fact]
    public void ALongReadStopsAtTheNextBatchBoundaryWhenThePolicyChangesMidway()
    {
        GameSettings g = Policy("g");
        Prewarmer? p = null;
        var streamer = new PolicyFlipStreamer(n =>
        {
            if (n == 2)
            {
                p!.SetGames([g with { Paused = true }]);
            }
        });
        p = new Prewarmer([g], streamer, new PrewarmEnv { AvailableRamMib = () => _avail, GameRunning = _ => false, ZoneBytes = _ => 6 * 64 * Mib, TraceExists = _ => true }, _log, null, () => _now);

        PrewarmResult r = p.Run(g, "test");

        Assert.False(r.Ran);
        Assert.Equal("game policy", r.CancelDetail);
        Assert.Equal(2, streamer.Read); // 다음 묶음 경계에서 멈춤
    }

    private sealed class PolicyFlipStreamer(Action<int> afterBatch) : IGameStreamer
    {
        private const long Batch = 64L << 20;

        public int Read { get; private set; }

        public StreamStats Stream(GameSettings game) => new(6 * Batch, 6, 0, 0.01);

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Read = 0;
            while (Read < 6)
            {
                if (stop.IsCancellationRequested)
                {
                    return (new StreamStats(Read * Batch, Read, 0, 0.01), false);
                }

                Read++;
                afterBatch(Read);
                progress?.Invoke(Read * Batch, 6 * Batch);
            }

            return (new StreamStats(6 * Batch, 6, 0, 0.01), true);
        }
    }

    // ---- 캐시 예산과 정책이 바뀔 때의 정리 ----

    [Fact]
    public void ABlockedGameLeavesTheCacheBudgetAndTheOtherGameTakesItsShare()
    {
        _zoneMib = 3000; // 예산 4096 MiB (물리 RAM 8 GiB 의 절반): 두 게임이 모두 들어가지 못함
        GameSettings a = Policy("a"), b = Policy("b");
        Prewarmer p = Make(a, b);
        p.WarmRank = games => games;
        Assert.True(p.Run(a, "test").Ran);
        _now += TimeSpan.FromMinutes(1);
        Assert.True(p.Run(b, "test").Ran); // 최근 순서: b, a

        var both = p.WarmBudget();
        Assert.Equal((3000L, 1096L), (both!.Value.AllocMib["b"], both.Value.AllocMib["a"]));

        p.SetGames([a, b with { Paused = true }]);
        var withoutB = p.WarmBudget();
        Assert.DoesNotContain("b", withoutB!.Value.In);
        Assert.Equal(3000L, withoutB.Value.AllocMib["a"]); // b 의 몫을 a 가 받음
        Assert.Equal(["a"], p.Warmed().Select(g => g.Name));

        p.SetGames([a, b]); // 이력은 보존돼 다음 점검부터 평소처럼
        var again = p.WarmBudget();
        Assert.Equal((3000L, 1096L), (again!.Value.AllocMib["b"], again.Value.AllocMib["a"]));
    }

    [Fact]
    public void ForbiddingPrewarmDropsThePendingWarmAndAllowingItAgainWarmsNormally()
    {
        GameSettings g = Policy("g");
        _avail = 100; // 메모리 부족: 건너뛰고 대기 재시도로 남음
        Prewarmer p = Make(g);
        Assert.StartsWith("low memory", p.Run(g, "test").SkipReason, StringComparison.Ordinal);
        Assert.True(p.HasPendingWarm("g"));

        p.SetGames([g with { Mode = GameMode.RelocateOnly }]);

        Assert.False(p.HasPendingWarm("g"));
        _avail = 64000;
        _now += TimeSpan.FromHours(7);
        p.Tick(AutoSettings.Default);
        Assert.Empty(_streamer.Calls); // 읽지 않음

        p.SetGames([g]);
        Assert.True(p.Run(g, "test").Ran);
    }

    // ---- 고정 ----

    [Fact]
    public void APinIsReleasedWhenThePolicyForbidsPrewarmAndNotTakenWhileItDoes()
    {
        GameSettings g = Policy("g") with { Prewarm = PrewarmSettings.Default with { Pin = true, PinSet = true } };
        var pins = new List<bool>();
        Prewarmer p = new([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => false,
            ZoneBytes = _ => _zoneMib * Mib,
            TraceExists = _ => true,
            PhysicalRamBytes = () => 32L << 30,
            PinPaths = _ => [@"D:\Games\g\a.bin"],
            OpenPin = paths => new PinHandle(new Pin(pins), paths.Count, 0),
        }, _log, null, () => _now);
        Assert.True(p.Run(g, "service start").Ran);
        Assert.Equal(["g"], p.Pinned);

        p.SetGames([g with { Paused = true }]);

        Assert.Empty(p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("g: pin released (game policy)", StringComparison.Ordinal));
        Assert.Equal([true], pins); // 풀렸음

        p.SetGames([g]);
        _now += TimeSpan.FromHours(1);
        Assert.True(p.Run(g, "service start").Ran);
        Assert.Equal(["g"], p.Pinned); // 다시 허용되면 평소처럼 고정
    }

    private sealed class Pin(List<bool> disposed) : IDisposable
    {
        public void Dispose() => disposed.Add(true);
    }

    // ---- 게임 시작 스트림 ----

    private GameDispatcher Dispatcher(GameSettings game, Func<bool>? enabled = null) =>
        new([game], _streamer, _log, () => _now, new DispatchEnv
        {
            OtherProcesses = (g, s) => DispatchEnv.RegisteredOthers(g, [], s.Pid),
            Age = _ => TimeSpan.Zero,
            IsWarm = _ => null,
            StartStreamEnabled = enabled ?? (() => true),
        });

    [Theory]
    [InlineData(GameMode.Inherit, false, true)]
    [InlineData(GameMode.PrewarmOnly, false, true)]
    [InlineData(GameMode.RelocateOnly, false, false)]
    [InlineData(GameMode.Off, false, false)]
    [InlineData(GameMode.Inherit, true, false)]
    public void TheStartStreamFollowsThePolicy(GameMode mode, bool paused, bool streams)
    {
        GameSettings g = Policy("g", mode, paused);

        Assert.Equal(streams, Dispatcher(g).Handle(new ProcessStart(5, "g.exe", 0)));

        Assert.Equal(streams ? 1 : 0, _streamer.Calls.Count);
        Assert.Equal(!streams, _log.Lines.Any(l => l.Contains("g: start stream skipped (game policy: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheStartStreamSwitchSkipsOnlyTheStartStreamAndNamesTheReason()
    {
        GameSettings g = Policy("g");
        bool enabled = false;
        GameDispatcher d = Dispatcher(g, () => enabled);

        Assert.False(d.Handle(new ProcessStart(5, "g.exe", 0)));
        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("g: start stream skipped (auto.start_stream_enabled is false)", StringComparison.Ordinal));
        Assert.True(Make(g).Run(g, "trigger").Ran); // 다른 예열은 그대로

        enabled = true;
        Assert.True(d.Handle(new ProcessStart(6, "g.exe", 0)));
        Assert.Equal(2, _streamer.Calls.Count);
    }

    // ---- 재적용 (Worker): 정책과 재배치 시간대 ----

    private int _layoutChecks;

    private Worker ReapplyWorker(GameSettings game, AutoSettings auto, Func<DateTimeOffset> now)
    {
        var env = new ReapplyEnv
        {
            CanApply = false,
            GameRunning = _ => false,
            DiskBusy = _ => false,
            LoadPlan = _ =>
            {
                _layoutChecks++;
                throw new FileNotFoundException();
            },
            Extents = _ => throw new InvalidOperationException(),
            Stat = _ => throw new InvalidOperationException(),
            RealPath = Path.GetFullPath,
            Apply = (_, _, _, _) => throw new InvalidOperationException(),
        };
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        var quiet = new BootQuiet(() => 6 * 60_000, () => true, _log, () => DateTimeOffset.UtcNow, () => 6 * 60_000);
        return new Worker(new AsterSettings(50, [game with { OriginRoot = Path.GetTempPath() }]) { Auto = auto }, _log, new Silent(), null, _streamer, new ReapplyChecker(env, _log),
            quiet: quiet, coordinator: coordinator, now: now);
    }

    private sealed class Silent : IProcessWatcher
    {
        public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private void ReapplyTick(Worker w, Dictionary<string, DateTimeOffset> due) =>
        w.ReapplyTick(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2), due, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

    [Theory]
    [InlineData(GameMode.Inherit, false, 1)]
    [InlineData(GameMode.RelocateOnly, false, 1)]
    [InlineData(GameMode.PrewarmOnly, false, 0)]
    [InlineData(GameMode.Off, false, 0)]
    [InlineData(GameMode.Inherit, true, 0)]
    public void ThePeriodicLayoutCheckAndReapplyFollowTheRelocatePolicy(GameMode mode, bool paused, int checks)
    {
        Worker w = ReapplyWorker(Policy("g", mode, paused), AutoSettings.Default, () => _now);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        ReapplyTick(w, due);
        ReapplyTick(w, due);

        Assert.Equal(checks, _layoutChecks); // 허용이면 첫 틱에 한 번, 기한이 다시 잡혀 둘째 틱에는 없음
        Assert.Equal(checks == 0 ? 1 : 0, _log.Lines.Count(l => l.Contains("g: layout check and reapply skipped (game policy: ", StringComparison.Ordinal))); // 상태가 바뀔 때만 한 번
    }

    [Fact]
    public void RelocateHoursKeepTheReapplyOutsideTheWindowAndStartItInsideAnEmptyListAlwaysAllows()
    {
        DateTimeOffset at = new(2026, 11, 12, 3, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 12, 3, 30, 0)));
        int hour = at.ToLocalTime().Hour;
        GameSettings g = Policy("g");
        Worker w = ReapplyWorker(g, AutoSettings.Default with { RelocateHours = [(hour + 12) % 24] }, () => at);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        ReapplyTick(w, due);
        ReapplyTick(w, due);

        Assert.Equal(0, _layoutChecks);
        Assert.Single(_log.Lines, l => l.Contains("g: layout check and reapply skipped (outside auto.relocate_hours)", StringComparison.Ordinal));
        Assert.True(due["g"] <= DateTimeOffset.UtcNow); // 기한을 미루지 않아 시간대에 들어서는 첫 틱에 검사

        w.Apply(new AsterSettings(50, [g with { OriginRoot = Path.GetTempPath() }]) { Auto = AutoSettings.Default with { RelocateHours = [hour] } });
        ReapplyTick(w, due);
        Assert.Equal(1, _layoutChecks);

        due["g"] = DateTimeOffset.MinValue;
        w.Apply(new AsterSettings(50, [g with { OriginRoot = Path.GetTempPath() }]) { Auto = AutoSettings.Default });
        ReapplyTick(w, due);
        Assert.Equal(2, _layoutChecks); // 빈 목록은 항상 허용
    }

    // ---- 그 밖의 읽기 경로 ----

    [Theory]
    [InlineData(GameMode.Inherit, false, true)]
    [InlineData(GameMode.PrewarmOnly, false, true)]
    [InlineData(GameMode.RelocateOnly, false, false)]
    [InlineData(GameMode.Off, false, false)]
    [InlineData(GameMode.Inherit, true, false)]
    public void TheChampionSelectWatcherOnlyFollowsAGameThatMayPrewarm(GameMode mode, bool paused, bool found)
    {
        GameSettings lol = Games.Make("lol", "LeagueClient.exe", "League of Legends.exe") with { AntiCheat = "vanguard", Mode = mode, Paused = paused };

        Assert.Equal(found, RiotPhaseWatcher.FindGame(new AsterSettings(50, [lol])) is not null);
    }
}
