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
}
