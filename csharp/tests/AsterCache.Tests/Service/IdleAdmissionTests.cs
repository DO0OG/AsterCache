using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임·유휴 이벤트 기반의 짧은 준비: 부팅 직후는 60 초 게이트 유지, 부팅 한참 뒤 재시작은 유휴 20 초만, 게임 실행 중 읽기 0, 읽는 도중 표본이 모름 (Warming·Stale) 이면 다음 묶음부터 중단.
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
    private Action? _onSleep;
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

    // 앞부분 읽기와 전체 읽기를 따로 기록.
    private sealed class HeadStreamer : IGameStreamer
    {
        public List<string> Heads { get; } = [];

        public List<string> Full { get; } = [];

        public StreamStats Stream(GameSettings game)
        {
            Full.Add(game.Name);
            return new StreamStats(100 * Mib, 1, 0, 0.01);
        }

        public void StreamHead(GameSettings game, CancellationToken stop) => Heads.Add(game.Name);
    }

    private Prewarmer Make(IGameStreamer streamer, params GameSettings[] games) => new(games.Length == 0 ? new[] { Game() } : games, streamer, new PrewarmEnv
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
            _onSleep?.Invoke();
        },
        DiskBytesPerSecond = _ => 0,
        DiskSample = _ => (0, _sample),
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음
    }, _log, null, () => _now);

    private static GameSettings Game() => Games.Make("a", "a.exe");

    [Fact]
    public void ARestartLongAfterBootWaitsOnlyForTheIdleTwentySecondsNotTheBootDelay()
    {
        _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
        Prewarmer p = Make(_plain);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.InRange(_waited, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20)); // PrewarmIdleNeeded 만, 부팅 60 초를 다시 기다리지 않음
        Assert.Single(_plain.Calls);
    }

    [Fact]
    public void AServiceStartedRightAfterBootStillWaitsForTheSixtySecondGate()
    {
        _uptimeMs = 10_000;
        Prewarmer p = Make(_plain);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.True(_uptimeMs >= 60_000 + 15_000, $"started too early: {_uptimeMs} ms"); // 부팅 60 초 + 유휴 20 초 (Sleep 이 그동안 읽기 0 을 확인)
    }

    [Fact]
    public void AGameUnregisteredWhileServiceStartWaitsForIdleIsNotRead()
    {
        _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
        Prewarmer p = Make(_plain);
        _onSleep = () => p.SetGames([]); // 유휴를 기다리는 동안 설정 재적재로 등록이 해제됨

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.DoesNotContain(results, r => r.Ran);
        Assert.Empty(_plain.Calls);
    }

    [Fact]
    public void ALoadHeadIsNotReadForAGameUnregisteredWhileServiceStartWaitsForIdle()
    {
        _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
        GameSettings a = Games.Make("a", "a.exe");
        GameSettings b = Games.Make("b", "b.exe");
        var streamer = new HeadStreamer();
        Prewarmer p = Make(streamer, a, b);
        _onSleep = () => p.SetGames([b]); // 유휴를 기다리는 동안 a 의 등록이 해제됨

        p.RunServiceStart();

        Assert.Equal(["b"], streamer.Heads); // b 의 앞부분은 읽혀 이 경로를 실제로 지났음을 보임
        Assert.DoesNotContain("a", streamer.Full);
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
