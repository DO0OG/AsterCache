using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 시작 예열·재예열(읽기) 과 재배치(쓰기) 가 같은 디스크에서 겹치지 않게: 재배치 쪽 게이트.
public sealed partial class AutoRelocatorTests
{
    private Prewarmer AttachPrewarm(IGameStreamer streamer)
    {
        var prewarm = new Prewarmer(_settings.Games, streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _w.Running,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(_w.Env(), _status, _log, () => _settings, prewarm);
        return prewarm;
    }

    private static void Finish(Task task) => Assert.True(task.Wait(Bound));

    [Fact]
    public void RelocationWaitsWhileAPrewarmStreamReadsTheSameDiskAndRunsAfterItEnds()
    {
        var blocking = new BlockingStreamer();
        Prewarmer prewarm = AttachPrewarm(blocking);
        Task warm = Task.Run(() => prewarm.Run(_game, "service start"));
        Assert.True(blocking.Started.Wait(Bound));

        Run(TimeSpan.FromMinutes(10)); // 유휴 시간(3 분)을 훨씬 넘겨도 읽는 동안은 시작하지 않음

        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("auto relocation waits: " + AutoRelocator.StartupPrewarmBusy, StringComparison.Ordinal))); // 틱마다 쌓이지 않고 한 번

        blocking.Release.Set();
        Finish(warm);
        Run(TimeSpan.FromMinutes(10));

        Assert.NotEmpty(Vol.Moves); // 예열이 끝난 뒤 유휴를 다시 채우면 재배치
    }

    [Fact]
    public void PrewarmStartingAfterTheIdleCheckStopsTheRelocationBeforeItWritesAndItRunsLater()
    {
        var blocking = new BlockingStreamer();
        Prewarmer prewarm = AttachPrewarm(blocking);
        Task? warm = null;
        Tick(); // 계획 만들기 + 유휴 구간 시작
        _w.Now += TimeSpan.FromMinutes(3);
        _w.OnDiskSample = () =>
        {
            _w.OnDiskSample = null; // 이 틱의 유휴 판정을 마친 뒤 (디스크 표본 직후) 예열이 읽기 시작함
            warm = Task.Run(() => prewarm.Run(_game, "service start"));
            Assert.True(blocking.Started.Wait(Bound));
        };

        TickReturnsPromptly();

        Assert.NotNull(warm);
        Assert.Empty(Vol.Moves); // 쓰기를 시작하지 않음
        Assert.Equal(0, ApplyCalls);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains(AutoRelocator.StartupPrewarmBusy, Entry.Detail, StringComparison.Ordinal);

        blocking.Release.Set();
        Finish(warm!);
        WarmDone();
        Run(TimeSpan.FromMinutes(10));

        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void RelocationWaitsWhileAWholeFolderPrewarmReadsTheSameDiskAndRunsAfterItEnds()
    {
        var read = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var prewarm = new Prewarmer(_settings.Games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _w.Running,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => false, // 기록 없는 게임: 통째·프로필 예열
            ColdPlanner = (_, _, _, _) => new ColdPlan("full", [new ReadOp(@"D:\g.bin", 0, 1 << 20)], 1 << 20, null),
            ColdRead = (_, _) =>
            {
                read.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return (new StreamStats(1 << 20, 1, 0, 0.01), true);
            },
        }, _log, null, () => _w.Now);
        prewarm.SetAuto(AutoSettings.Default);
        _relocator = new AutoRelocator(_w.Env(), _status, _log, () => _settings, prewarm);
        Task warm = Task.Run(() => prewarm.Run(_game, "service start"));
        Assert.True(read.Wait(Bound));

        Run(TimeSpan.FromMinutes(10)); // 유휴 시간을 훨씬 넘겨도 통째 읽기가 도는 동안은 시작하지 않음

        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);

        release.Set();
        Finish(warm);
        Run(TimeSpan.FromMinutes(10));

        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void PrewarmCannotStartReadingWhileARelocationWritesAndMayAfterwards()
    {
        Prewarmer prewarm = AttachPrewarm(new FakeStreamer());
        var during = new List<PrewarmResult>();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.DryRun)
            {
                during.Add(prewarm.Run(_game, "trigger", ignoreCooldown: true)); // 쓰는 도중 같은 디스크 예열 요청
            }

            return inner(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(10));

        Assert.NotEmpty(Vol.Moves);
        PrewarmResult r = Assert.Single(during);
        Assert.Equal(Prewarmer.RelocationRunning, r.SkipReason);
        Assert.True(prewarm.TryBeginRelocation(_game.Name)); // 끝난 뒤에는 표시가 내려가 있음
        prewarm.EndRelocation(_game.Name);
    }

    [Fact]
    public void RelocationStartMarkIsClearedWhenTheRelocationThrows()
    {
        Prewarmer prewarm = AttachPrewarm(new FakeStreamer());
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun ? inner(g, plan, options, ct) : throw new IOException("disk");

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.True(prewarm.TryBeginRelocation(_game.Name));
        prewarm.EndRelocation(_game.Name);
    }
}
