using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 핫 집합 예열을 묶음 경계에서 취소하기 (다른 게임이 뜨면 멈추고, 그 게임이 뜨면 끝까지), 진행 알림, 오래된 콜드 예산.
public sealed class WarmStreamTests
{
    private const long Mib = 1L << 20;
    private const long Batch = 64 * Mib;
    private readonly ListLog _log = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private Prewarmer? _p;

    private static GameSettings Game(string name, params string[] triggers) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = triggers } };

    private Prewarmer Make(IGameStreamer streamer, params GameSettings[] games) => new(games, streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 1610 * Mib,
        TraceExists = _ => true,
    }, _log, null, () => _now);

    /// 64 MiB 묶음 batches 개를 차례로 읽는 가짜. 묶음마다 progress 를 부르고 afterBatch(읽은 묶음 수) 를 부른 뒤 다음 묶음 앞에서 stop 을 봅니다.
    private sealed class BatchStreamer(int batches, Action<int>? afterBatch = null) : IGameStreamer
    {
        public int Read { get; private set; }

        public StreamStats Stream(GameSettings game) => new(batches * Batch, batches, 0, 0.01);

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Read = 0;
            while (Read < batches)
            {
                if (stop.IsCancellationRequested)
                {
                    return (new StreamStats(Read * Batch, Read, 0, 0.01), false);
                }

                Read++;
                progress?.Invoke(Read * Batch, batches * Batch);
                afterBatch?.Invoke(Read);
            }

            return (new StreamStats(batches * Batch, batches, 0, 0.01), true);
        }
    }

    [Fact]
    public void AnotherGameStartingStopsTheStreamAtTheNextBatch()
    {
        GameSettings a = Game("a");
        using var cts = new CancellationTokenSource();
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart("b"); // 다른 게임이 뜸
                cts.Cancel();
            }
        });
        _p = Make(streamer, a);

        PrewarmResult r = _p.Run(a, "test", ct: cts.Token);

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(2, streamer.Read);
        Assert.Equal(2 * Batch, r.Stats!.Bytes);
        Assert.Null(_p.LastWarmed("a")); // 끊긴 예열은 성공으로 치지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm cancelled (test) after 128 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGameBeingPrewarmedStartingStopsTheStreamAtTheNextBatchAndLogsTheYield()
    {
        GameSettings a = Game("a");
        using var cts = new CancellationTokenSource();
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart("A"); // 지금 읽는 그 게임 (대소문자 무시)
                cts.Cancel();
            }
        });
        _p = Make(streamer, a);

        PrewarmResult r = _p.Run(a, "test", ct: cts.Token);

        Assert.False(r.Ran);
        Assert.Equal(2, streamer.Read);
        Assert.Null(_p.LastWarmed("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: yielded to A after ", StringComparison.Ordinal) && l.EndsWith(" ms, 128 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void GameStartStopsTheStreamEvenWhenNoCancelTokenWasPassed()
    {
        GameSettings a = Game("a");
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 3)
            {
                _p!.NoteGameStart("a");
            }
        });
        _p = Make(streamer, a);

        PrewarmResult r = _p.Run(a, "test");

        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(3, streamer.Read);
    }

    [Fact]
    public void KeepStreamOnGameStartLetsTheStreamFinish()
    {
        GameSettings a = Game("a");
        using var cts = new CancellationTokenSource();
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart("A");
                cts.Cancel();
            }
        });
        _p = Make(streamer, a);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        PrewarmResult r = _p.Run(a, "test", ct: cts.Token);

        Assert.True(r.Ran);
        Assert.Equal(6, streamer.Read);
        Assert.NotNull(_p.LastWarmed("a"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("yielded to", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameOnTheSameDriveStopsTheStreamButOneOnAnotherDriveDoesNot()
    {
        GameSettings a = Game("a");
        GameSettings same = Game("b");
        GameSettings other = Game("c") with { OriginRoot = @"E:\Games\c" };
        string started = "b";
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart(started);
            }
        });
        _p = Make(streamer, a, same, other);

        Assert.False(_p.Run(a, "test").Ran);
        Assert.Equal(2, streamer.Read);
        Assert.Contains(_log.Lines, l => l.Contains("a: yielded to b after ", StringComparison.Ordinal));

        started = "c";
        Assert.True(_p.Run(a, "test", ignoreCooldown: true).Ran);
        Assert.Equal(6, streamer.Read);
    }

    [Fact]
    public void ARunningGameOnTheSameDriveBlocksANewStream()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        var streamer = new BatchStreamer(3);
        _p = new Prewarmer([a, b], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => g.Name == "b",
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now);

        PrewarmResult r = _p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal(0, streamer.Read);
    }

    [Fact]
    public void ACancelledTokenBeforeTheStreamNeverStartsIt()
    {
        GameSettings a = Game("a");
        var streamer = new BatchStreamer(3);
        _p = Make(streamer, a);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _p.NoteGameStart("a"); // 스트림이 돌지 않을 때의 알림은 아무것도 남기지 않음

        PrewarmResult r = _p.Run(a, "test", ct: cts.Token);

        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(0, streamer.Read);
    }

    [Fact]
    public void ProgressIsReportedAtMostEveryTenSecondsAndTheEndIsAnnounced()
    {
        GameSettings a = Game("a");
        var streamer = new BatchStreamer(6, _ => _now += TimeSpan.FromSeconds(4)); // 묶음마다 4 초
        _p = Make(streamer, a);
        var seen = new List<(string, long, long)>();
        var ended = new List<(string, bool)>();
        _p.Progress += (g, done, total) => seen.Add((g.Name, done, total));
        _p.StreamEnded += (g, complete) => ended.Add((g.Name, complete));

        _p.Run(a, "test");

        // 묶음 1 (0 초) 에 바로, 묶음 4 (12 초 = 10 초 이상 뒤) 에 다시, 6 번째는 8 초 뒤라 건너뜀.
        Assert.Equal([("a", 64L, 384L), ("a", 256L, 384L)], seen);
        Assert.Equal([("a", true)], ended);
    }

    [Fact]
    public void EndIsAnnouncedAsIncompleteWhenCancelledOrFailed()
    {
        GameSettings a = Game("a");
        using var cts = new CancellationTokenSource();
        var ended = new List<bool>();
        _p = Make(new BatchStreamer(4, _ => cts.Cancel()), a);
        _p.StreamEnded += (_, complete) => ended.Add(complete);
        _p.Run(a, "t", ct: cts.Token);

        _p = Make(new FakeStreamer { Throw = new IOException("x") }, a);
        _p.StreamEnded += (_, complete) => ended.Add(complete);
        _p.Run(a, "t");

        Assert.Equal([false, false], ended);
    }

    [Fact]
    public void StreamersWithoutCancellationStillWork()
    {
        GameSettings a = Game("a");
        var fake = new FakeStreamer();
        _p = Make(fake, a);

        PrewarmResult r = _p.Run(a, "test");

        Assert.True(r.Ran); // 기본 구현: 한 번에 읽고 끝까지 읽은 것으로 침
        Assert.Single(fake.Calls);
    }

    // ---- 기록 없는 예열이 잡은 예산 ----

    private sealed class ColdWorld
    {
        public const long PhysBytes = 64L << 30; // 1/4 = 16384 MiB

        public ProbeResult? Probe { get; set; }

        public int Probes { get; set; }
    }

    private (Prewarmer P, ColdWorld W) MakeColdPair(GameSettings x, GameSettings y)
    {
        var w = new ColdWorld();
        var p = new Prewarmer([x, y], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 60000,
            PhysicalRamBytes = () => ColdWorld.PhysBytes,
            GameRunning = _ => false,
            ZoneBytes = g => g.Name == "y" ? 1000 * Mib : null,
            TraceExists = g => g.Name == "y",
            DiskBytesPerSecond = _ => 0,
            ColdPlanner = (_, _, _, _) => new ColdPlan("full", Enumerable.Range(0, 20).Select(i => new ReadOp($@"D:\x\f{i}.bin", 0, Mib)).ToList(), 20 * Mib, null),
            ColdRead = (_, _) => (new StreamStats(ColdWorld.PhysBytes / 4, 20, 0, 1), true), // X 가 RAM 의 1/4 를 채움
            ProbeSamples = _ =>
            {
                w.Probes++;
                return w.Probe;
            },
        }, _log, null, () => _now);
        p.SetAuto(AutoSettings.Default);
        return (p, w);
    }

    private static ProcessStart Launcher() => new(1, "launcher.exe", 0);

    [Fact]
    public void AColdWarmFillingAQuarterOfRamBlocksALaterBatchUntilItsGameStarts()
    {
        GameSettings x = Game("x");
        GameSettings y = Game("y", "launcher.exe");
        (Prewarmer p, _) = MakeColdPair(x, y);
        Assert.True(p.Run(x, "t").Ran);

        PrewarmResult blocked = p.OnProcessStart(Launcher()).Single();
        Assert.Equal("batch memory budget", blocked.SkipReason); // X 가 상주한 양 때문에

        p.NoteGameStart("x"); // 게임이 시작되면 그 게임의 콜드 몫을 풂
        PrewarmResult ran = p.OnProcessStart(Launcher()).Single();
        Assert.True(ran.Ran);
    }

    [Fact]
    public void ACooledColdWarmNoLongerCountsAgainstTheBudgetButAWarmOneDoes()
    {
        GameSettings x = Game("x");
        GameSettings y = Game("y", "launcher.exe");
        (Prewarmer p, ColdWorld w) = MakeColdPair(x, y);
        Assert.True(p.Run(x, "t").Ran);

        w.Probe = new ProbeResult(8, 0, 0.4); // 아직 상주
        Assert.Equal("batch memory budget", p.OnProcessStart(Launcher()).Single().SkipReason);
        Assert.True(w.Probes > 0);

        w.Probe = new ProbeResult(8, 5, 12); // 식음
        Assert.True(p.OnProcessStart(Launcher()).Single().Ran);
        Assert.Contains(_log.Lines, l => l.Contains("x: cold prewarm no longer counts against the budget", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableSamplesKeepTheRecordedAmount()
    {
        GameSettings x = Game("x");
        GameSettings y = Game("y", "launcher.exe");
        (Prewarmer p, ColdWorld w) = MakeColdPair(x, y);
        p.Run(x, "t");
        w.Probe = null;

        Assert.Equal("batch memory budget", p.OnProcessStart(Launcher()).Single().SkipReason);
    }
}
