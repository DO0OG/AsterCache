using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임·유휴 이벤트 기반의 짧은 준비: 부팅 직후는 5 분 게이트 유지, 부팅 한참 뒤 재시작은 유휴 60 초만, 게임 실행 중 읽기 0, 읽는 도중 표본이 모름 (Warming·Stale) 이면 다음 묶음부터 중단.
public sealed class IdleAdmissionTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly BatchStreamer _streamer = new();
    private readonly FakeStreamer _plain = new();
    private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private long _uptimeMs;
    private TimeSpan _waited;
    private bool _running;
    private DiskActivity.SampleState _sample = DiskActivity.SampleState.Ok;

    // 100 MiB 묶음 다섯 개를 차례로 읽고 묶음마다 진행을 알림 (StreamPrewarmCore 의 Batches 와 같은 순서: 취소 확인 → 읽기 → 진행).
    private sealed class BatchStreamer : IGameStreamer
    {
        public int Issued { get; private set; }

        public Action<int>? OnIssue { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            progress?.Invoke(0, 500 * Mib);
            long done = 0;
            for (int i = 0; i < 5 && !stop.IsCancellationRequested; i++)
            {
                Issued++;
                done += 100 * Mib;
                OnIssue?.Invoke(i);
                progress?.Invoke(done, 500 * Mib);
            }

            return (new StreamStats(done, Issued, 0, 0.01), !stop.IsCancellationRequested);
        }
    }

    private Prewarmer Make(IGameStreamer streamer) => new([Game()], streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => _running,
        ZoneBytes = _ => 500 * Mib,
        TraceExists = _ => true,
        UptimeMs = () => _uptimeMs,
        Sleep = (t, _) =>
        {
            Assert.Empty(_plain.Calls); // 기다리는 동안은 읽지 않음
            (_uptimeMs, _waited) = (_uptimeMs + (long)t.TotalMilliseconds, _waited + t);
        },
        DiskBytesPerSecond = _ => 0,
        DiskSample = _ => (0, _sample),
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음
    }, _log, null, () => _now);

    private static GameSettings Game() => Games.Make("a", "a.exe");

    [Fact]
    public void ARestartLongAfterBootWaitsOnlyForTheIdleMinuteNotFiveMinutes()
    {
        _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
        Prewarmer p = Make(_plain);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.InRange(_waited, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60)); // IdleNeeded 만, 부팅 5 분을 다시 기다리지 않음
        Assert.Single(_plain.Calls);
    }

    [Fact]
    public void AServiceStartedRightAfterBootStillWaitsForTheFiveMinuteGate()
    {
        _uptimeMs = 10_000;
        Prewarmer p = Make(_plain);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.True(_uptimeMs >= 5 * 60_000 + 55_000, $"started too early: {_uptimeMs} ms"); // 부팅 5 분 + 유휴 60 초 (Sleep 이 그동안 읽기 0 을 확인)
    }

    [Fact]
    public void NothingIsReadAtServiceStartWhileTheGameRuns()
    {
        _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
        _running = true;
        Prewarmer p = Make(_plain);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.False(Assert.Single(results).Ran);
        Assert.Empty(_plain.Calls);
    }

    [Theory]
    [InlineData(DiskActivity.SampleState.Ok, 5)] // 표본이 계속 Ok 면 기존과 같음
    [InlineData(DiskActivity.SampleState.Warming, 1)]
    [InlineData(DiskActivity.SampleState.Stale, 1)]
    [InlineData(DiskActivity.SampleState.Broken, 5)] // 고장은 기존 동작 (읽기 허용으로 넓히지 않음)
    public void ASampleThatTurnsUnknownAfterTheFirstBatchStopsTheNextOne(DiskActivity.SampleState during, int issued)
    {
        Prewarmer p = Make(_streamer);
        _streamer.OnIssue = _ => _sample = during;

        PrewarmResult r = p.Run(Game(), "test");

        Assert.Equal(issued, _streamer.Issued);
        Assert.Equal(issued == 5, r.Ran);
    }

    [Theory]
    [InlineData(DiskActivity.SampleState.Warming)]
    [InlineData(DiskActivity.SampleState.Stale)]
    public void AnUnknownSampleAtEntryIssuesNoBatch(DiskActivity.SampleState state)
    {
        Prewarmer p = Make(_streamer);
        _sample = state;

        PrewarmResult r = p.Run(Game(), "test");

        Assert.Equal(0, _streamer.Issued);
        Assert.False(r.Ran);
    }
}
