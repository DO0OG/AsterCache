using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tracer;
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
    private long _availableMib = 16000;
    private Prewarmer? _p;

    private static GameSettings Game(string name, params string[] triggers) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = triggers } };

    private Prewarmer Make(IGameStreamer streamer, params GameSettings[] games) => Make(streamer, _ => false, games);

    private Prewarmer Make(IGameStreamer streamer, Func<GameSettings, bool> gameRunning, params GameSettings[] games) => new(games, streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _availableMib,
        GameRunning = gameRunning,
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
                afterBatch?.Invoke(Read);
                progress?.Invoke(Read * Batch, batches * Batch);
            }

            return (new StreamStats(batches * Batch, batches, 0, 0.01), true);
        }

        public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue)
        {
            Read = 0;
            while (Read < batches)
            {
                if (stop.IsCancellationRequested || !canIssue())
                {
                    return (new StreamStats(Read * Batch, Read, 0, 0.01, Aborted: true), false);
                }

                Read++;
                afterBatch?.Invoke(Read);
                progress?.Invoke(Read * Batch, batches * Batch);
            }

            return (new StreamStats(batches * Batch, batches, 0, 0.01), true);
        }
    }

    private sealed class HeadBatchStreamer(Action afterFirstBatch) : IGameStreamer
    {
        private readonly Dictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);

        public int ReadFor(string game) => _reads.GetValueOrDefault(game);

        public StreamStats Stream(GameSettings game) => new(0, 0, 0, 0.01);

        public void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            long total = 3 * Batch;
            for (int read = 1; read <= 3 && !stop.IsCancellationRequested; read++)
            {
                _reads[game.Name] = read;
                if (read == 1)
                {
                    afterFirstBatch();
                }

                progress?.Invoke(read * Batch, total);
            }
        }

        public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue)
        {
            long total = 3 * Batch;
            for (int read = 1; read <= 3 && !stop.IsCancellationRequested && canIssue(); read++)
            {
                _reads[game.Name] = read;
                if (read == 1)
                {
                    afterFirstBatch();
                }

                progress?.Invoke(read * Batch, total);
            }
        }
    }

    private sealed class GapStatsStreamer : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(64 * Mib, 1, 0, 0.01);

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) => (Stream(game), true);

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes) =>
            (new StreamStats(64 * Mib, 1, 0, 0.01) { GapBytes = 256 * 1024 }, true);
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
    public void KeepStreamOnGameStartSurvivesTheWarmCancellationTokenButStopsForAnotherGame()
    {
        GameSettings a = Game("a");
        using var warmCts = new CancellationTokenSource();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        using var serviceCts = new CancellationTokenSource();
        coordinator.SetServiceToken(serviceCts.Token);
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                coordinator.Yield(a.OriginRoot, "game started", a.Name);
                _p!.NoteGameStart(a.Name);
                warmCts.Cancel();
            }
            else if (n == 3)
            {
                coordinator.Yield(@"D:\Games\other", "game started", "other");
            }
        });
        _p = new Prewarmer([a], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now, coordinator);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        PrewarmResult result = _p.Run(a, "test", ct: warmCts.Token);

        Assert.False(result.Ran);
        Assert.Equal(3, streamer.Read);
        Assert.Equal(3 * Batch, result.Stats!.Bytes);
    }

    [Fact]
    public void KeepStreamOnGameStartPreservesTheCurrentHeadsButHonorsServiceCancellation()
    {
        GameSettings a = Game("a") with { Prewarm = Game("a").Prewarm with { OnServiceStart = true } };
        GameSettings b = Game("b") with { Prewarm = Game("b").Prewarm with { OnServiceStart = true } };
        using var warmCts = new CancellationTokenSource();
        using var serviceCts = new CancellationTokenSource();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        coordinator.SetServiceToken(serviceCts.Token);
        var streamer = new HeadBatchStreamer(() =>
        {
            coordinator.Yield(a.OriginRoot, "game started", a.Name);
            _p!.NoteGameStart(a.Name);
            warmCts.Cancel();
        });
        _p = new Prewarmer([a, b], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now, coordinator);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true, StartupWarmMaxMib = 8000 });

        _ = _p.RunServiceStart(ct: warmCts.Token);

        Assert.Equal(3, streamer.ReadFor(a.Name));
        Assert.Equal(0, streamer.ReadFor(b.Name));
    }

    // keep_stream_on_game_start 가 꺼져 있으면 읽기 대여는 시작 스트림 보존 대상이 아님: 게임이 시작해 디스크가 양보되면 (취소 신호 없이도) 다음 묶음부터 읽지 않음.
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 6)]
    public void AGameStartingOnTheDiskStopsTheHotStreamAtTheNextBatchUnlessKeepStreamOnGameStartIsOn(bool keep, int read)
    {
        GameSettings a = Game("a");
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                coordinator.Yield(a.OriginRoot, "game started", a.Name); // 대여를 얻은 뒤 게임이 시작함
            }
        });
        _p = new Prewarmer([a], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now, coordinator);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = keep });

        _ = _p.Run(a, "test");

        Assert.Equal(read, streamer.Read);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void AGameStartingOnTheDiskStopsTheHeadStreamAtTheNextBatchUnlessKeepStreamOnGameStartIsOn(bool keep, int read)
    {
        GameSettings a = Game("a") with { Prewarm = Game("a").Prewarm with { OnServiceStart = true } };
        GameSettings b = Game("b") with { Prewarm = Game("b").Prewarm with { OnServiceStart = true } };
        using var warmCts = new CancellationTokenSource();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        var streamer = new HeadBatchStreamer(() => coordinator.Yield(a.OriginRoot, "game started", a.Name));
        _p = new Prewarmer([a, b], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now, coordinator);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = keep, StartupWarmMaxMib = 8000 });

        _ = _p.RunServiceStart(ct: warmCts.Token);

        Assert.Equal(read, streamer.ReadFor(a.Name));
    }

    // 계획 도중 대여가 취소돼 StreamHead 가 OperationCanceledException 을 받아도 (서비스 종료가 아니면) 다음 게임 앞부분 예열이 계속됨.
    [Fact]
    public void PlanningCancellationOfOneHeadDoesNotAbortTheRestOfTheBatch()
    {
        GameSettings a = Game("a") with { Prewarm = Game("a").Prewarm with { OnServiceStart = true } };
        GameSettings b = Game("b") with { Prewarm = Game("b").Prewarm with { OnServiceStart = true } };
        using var warmCts = new CancellationTokenSource();
        var streamer = new CancelOnHeadStreamer(a.Name);
        _p = Make(streamer, a, b);
        _p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 8000 });

        _ = _p.RunServiceStart(ct: warmCts.Token);

        Assert.Contains(a.Name, streamer.Tried);
        Assert.Equal([b.Name], streamer.Done);
        Assert.Contains(_log.Lines, l => l.Contains("a: load head prewarm cancelled (planning)", StringComparison.Ordinal));
    }

    private sealed class CancelOnHeadStreamer(string cancelling) : IGameStreamer
    {
        public List<string> Tried { get; } = [];

        public List<string> Done { get; } = [];

        public StreamStats Stream(GameSettings game) => new(0, 0, 0, 0.01);

        public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue)
        {
            Tried.Add(game.Name);
            if (game.Name == cancelling)
            {
                throw new OperationCanceledException("lease cancelled while planning");
            }

            Done.Add(game.Name);
        }
    }

    [Fact]
    public void ServiceCancellationStillStopsAKeptHotStream()
    {
        GameSettings a = Game("a");
        using var warmCts = new CancellationTokenSource();
        using var serviceCts = new CancellationTokenSource();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        coordinator.SetServiceToken(serviceCts.Token);
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                coordinator.Yield(a.OriginRoot, "game started", a.Name);
                _p!.NoteGameStart(a.Name);
                warmCts.Cancel();
            }
            else if (n == 3)
            {
                serviceCts.Cancel();
            }
        });
        _p = new Prewarmer([a], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now, coordinator);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        PrewarmResult result = _p.Run(a, "test", ct: warmCts.Token);

        Assert.False(result.Ran);
        Assert.Equal(3, streamer.Read); // 둘째 묶음의 게임 시작 취소는 무시하고 셋째 묶음의 서비스 취소에서 멈춤
        Assert.Equal(3 * Batch, result.Stats!.Bytes);
    }

    [Theory]
    [InlineData(true, 6, true)] // 게임 시작 때 스트림을 멈추지 않는 설정: 묶음 검사의 실행 판정으로도 취소하지 않음
    [InlineData(false, 2, false)]
    public void TheBatchRunningCheckCancelsTheStreamOnlyWhenKeepStreamOnGameStartIsOff(bool keep, int read, bool ran)
    {
        GameSettings a = Game("a");
        bool running = false;
        var streamer = new BatchStreamer(6, n => running = running || n == 2); // 알림 없이 둘째 묶음 뒤 실행 판정만 참으로 바뀜
        _p = Make(streamer, _ => running, a);
        _p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = keep });

        PrewarmResult r = _p.Run(a, "test");

        Assert.Equal(ran, r.Ran);
        Assert.Equal(read, streamer.Read);
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

    [Theory]
    [InlineData(true, 2, false)] // 같은 물리 디스크의 다른 볼륨: 양보
    [InlineData(false, 6, true)] // 다른 디스크: 독립
    public void AGameOnAnotherVolumeStopsTheStreamOnlyWhenThatVolumeIsTheSameDisk(bool sameDisk, int read, bool ran)
    {
        GameSettings a = Game("a");
        GameSettings other = Game("c") with { OriginRoot = @"E:\Games\c" };
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart("c");
            }
        });
        _p = new Prewarmer([a, other], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
            SameDisk = (_, _) => sameDisk,
        }, _log, null, () => _now);

        Assert.Equal(ran, _p.Run(a, "test").Ran);
        Assert.Equal(read, streamer.Read);
    }

    [Fact]
    public void AnUnknownDiskIsTreatedAsTheSameDiskSoAGameStartStopsTheStream()
    {
        GameSettings a = Game("a");
        GameSettings other = Game("c") with { OriginRoot = @"E:\Games\c" };
        var streamer = new BatchStreamer(6, n =>
        {
            if (n == 2)
            {
                _p!.NoteGameStart("c");
            }
        });
        var disks = new VolumeDisks(_ => null, () => 0); // 조회 실패
        _p = new Prewarmer([a, other], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
            SameDisk = disks.AreSame,
        }, _log, null, () => _now);

        Assert.False(_p.Run(a, "test").Ran);
        Assert.Equal(2, streamer.Read);
    }

    [Theory]
    [InlineData(true, 0)] // 같은 디스크의 다른 볼륨 게임이 돌면 새 읽기 없음
    [InlineData(false, 3)]
    public void ARunningGameOnAnotherVolumeBlocksANewStreamOnlyOnTheSameDisk(bool sameDisk, int read)
    {
        GameSettings a = Game("a");
        GameSettings other = Game("c") with { OriginRoot = @"E:\Games\c" };
        var streamer = new BatchStreamer(3);
        _p = new Prewarmer([a, other], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => g.Name == "c",
            ZoneBytes = _ => 1610 * Mib,
            TraceExists = _ => true,
            SameDisk = (_, _) => sameDisk,
        }, _log, null, () => _now);

        _ = _p.Run(a, "test");

        Assert.Equal(read, streamer.Read);
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
    public void MemoryPressureAfterABatchStopsBeforeTheNextBatchAndDoesNotMarkTheWarmupSuccessful()
    {
        GameSettings a = Game("a");
        var streamer = new BatchStreamer(3, n =>
        {
            if (n == 1)
            {
                _availableMib = 4200;
            }
        });
        _p = Make(streamer, a);

        PrewarmResult r = _p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(1, streamer.Read);
        Assert.Null(_p.LastWarmed("a"));
    }

    [Fact]
    public void UnregisteredGameProcessStartingDuringPrewarmStopsBeforeTheNextBatch()
    {
        bool helperRunning = false;
        GameSettings a = Game("a");
        var streamer = new BatchStreamer(3, n =>
        {
            if (n == 1)
            {
                helperRunning = true;
            }
        });
        _p = Make(streamer, _ => helperRunning, a);

        PrewarmResult r = _p.Run(a, "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal(1, streamer.Read);
        Assert.Null(_p.LastWarmed("a"));
    }

    [Fact]
    public void MemoryPressureAfterAHeadBatchStopsBeforeTheNextHeadBatch()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        var streamer = new HeadBatchStreamer(() => _availableMib = 4200);
        _p = Make(streamer, a, b);

        IReadOnlyList<PrewarmResult> results = _p.RunAll([a, b], "test");

        Assert.Equal(1, streamer.ReadFor("a"));
        Assert.All(results, result => Assert.False(result.Ran));
        Assert.All(results, result => Assert.Equal("low memory (available 4200 MiB < zone 1610 MiB + reserve 4096 MiB)", result.SkipReason));
    }

    [Fact]
    public void DenseGapFillingJoinsSubMiBGapsForVariableFileRangesWithoutChangingTheOriginalOps()
    {
        const long gap = 512 * 1024;
        string path = @"D:\Games\xpak_cache\cache_0_2.xpakdata";
        ReadOp[] original = [new(path, 0, 100), new(path, 100 + gap, 100)];

        (List<ReadOp> ops, long added, bool fits) = PrefetchStreamer.FillPrewarmGaps(original, 200 + gap, _ => 3 * Mib);

        Assert.True(fits);
        Assert.Equal(gap, added);
        Assert.Equal([new ReadOp(path, 0, 200 + gap)], ops);
        Assert.Equal([new ReadOp(path, 0, 100), new ReadOp(path, 100 + gap, 100)], original);
    }

    [Fact]
    public void DenseGapFillingFallsBackTo256KiBWhenTheMiBPlanExceedsTheBudget()
    {
        const long largeGap = 512 * 1024;
        const long smallGap = 128 * 1024;
        string path = @"D:\Games\cache.bin";
        ReadOp[] original =
        [
            new(path, 0, 100),
            new(path, 100 + largeGap, 100),
            new(path, 200 + largeGap + smallGap, 100),
        ];

        (List<ReadOp> ops, long added, bool fits) = PrefetchStreamer.FillPrewarmGaps(original, 300 + smallGap, _ => 3 * Mib);

        Assert.True(fits);
        Assert.Equal(smallGap, added);
        Assert.Equal([new ReadOp(path, 0, 100), new ReadOp(path, 100 + largeGap, 200 + smallGap)], ops);
    }

    [Fact]
    public void DenseGapFillingSkipsGapsWhenThe256KiBBudgetStillDoesNotFit()
    {
        const long gap = 128 * 1024;
        string path = @"D:\Games\cache.bin";
        ReadOp[] original =
        [
            new(path, 0, 100),
            new(path, 100 + gap, 100),
            new(path, 200 + 2 * gap, 100),
        ];

        (List<ReadOp> ops, long added, bool fits) = PrefetchStreamer.FillPrewarmGaps(original, 300 + gap, _ => 3 * Mib);

        Assert.True(fits);
        Assert.Equal(0, added);
        Assert.Equal(original, ops);
    }

    [Fact]
    public void DenseGapFillingPreservesOriginalRangesWhenTheyExceedTheGapBudget()
    {
        string path = @"D:\Games\cache.bin";
        ReadOp[] original = [new(path, 0, 100), new(path, 100 + 512 * 1024, 100)];

        (List<ReadOp> ops, long added, bool fits) = PrefetchStreamer.FillPrewarmGaps(original, 100, _ => Mib);

        Assert.True(fits);
        Assert.Equal(0, added);
        Assert.Equal(original, ops);
    }

    [Fact]
    public void DenseGapFillingDoesNotJoinAnExactMiBGapOrExtendPastTheFileEnd()
    {
        string path = @"D:\Games\cache.bin";
        ReadOp[] exactMiBGap = [new(path, 0, 100), new(path, 100 + Mib, 100)];
        (List<ReadOp> unchanged, long exactAdded, bool exactFits) = PrefetchStreamer.FillPrewarmGaps(exactMiBGap, 1000 * Mib, _ => 2 * Mib);
        Assert.True(exactFits);
        Assert.Equal(0, exactAdded);
        Assert.Equal(exactMiBGap, unchanged);

        ReadOp[] toEnd = [new(path, 0, 100), new(path, 900, 100)];
        (List<ReadOp> joined, long endAdded, bool endFits) = PrefetchStreamer.FillPrewarmGaps(toEnd, 1000, _ => 1000);
        Assert.True(endFits);
        Assert.Equal(800, endAdded);
        Assert.All(joined, op => Assert.True(op.Offset + op.Length <= 1000));
    }

    [Fact]
    public void GapFillBudgetUsesStreamCapQuarterRamAndAvailableRamAfterTheFixedReserve()
    {
        GameSettings a = Game("a") with { MaxStreamMib = 5000, MaxStreamSet = true };

        Assert.Equal(4096 * Mib, Prewarmer.GapFillBudgetBytes(a, 8000 * Mib, null, 16L << 30, 10000));
        Assert.Equal(904 * Mib, Prewarmer.GapFillBudgetBytes(a, 8000 * Mib, null, 64L << 30, 5000));
    }

    [Fact]
    public void GapFillBudgetUsesTheEffectiveZoneLimitInsteadOfTheSmallCurrentZone()
    {
        GameSettings a = Game("a");

        Assert.Equal(1000 * Mib, Prewarmer.GapFillBudgetBytes(a, 1000 * Mib, null, 64L << 30, 20000)); // 유효 상한을 모르면 현재 존
        Assert.Equal(8173 * Mib, Prewarmer.GapFillBudgetBytes(a, 1000 * Mib, 8173 * Mib, 64L << 30, 20000));
        Assert.Equal(2904 * Mib, Prewarmer.GapFillBudgetBytes(a, 1000 * Mib, 8173 * Mib, 64L << 30, 7000)); // 가용 RAM - 4096 MiB 가 더 작음
    }

    [Fact]
    public void PrewarmGapExpansionUsesTheGapBudgetEvenWhenTheZoneOnlyCoversTheOriginalRanges()
    {
        string temp = Directory.CreateTempSubdirectory("aster-gap-budget-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "asset.bin");
            File.WriteAllBytes(path, new byte[2 * Mib]);
            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            const long span = 64 * 1024;
            const long gap = 512 * 1024;
            GameSettings game = Game("a") with { OriginRoot = root, AllowedRoots = [root], Trace = trace, Plan = planPath };
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 1), [], [], [], new JObject()).Save(planPath);
            TraceWriter.Write([[new TraceEvent(path, 0, span), new TraceEvent(path, span + gap, span)]], trace);

            Assert.Equal(2 * span, PrefetchStreamer.ZoneBytes(game)); // 존 예산이 원래 범위로 이미 소진됨
            (List<ReadOp> expanded, long added, bool fits) = PrefetchStreamer.PrewarmOps(game, Mib);

            Assert.True(fits);
            Assert.Equal(gap, added);
            Assert.Equal([new ReadOp(path, 0, 2 * span + gap)], expanded);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private sealed class ReadSetStreamer(long? readBytes) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(64 * Mib, 1, 0, 0.01);

        public long? PrewarmReadBytes(GameSettings game) => readBytes;
    }

    [Theory]
    [InlineData(2800L, false)] // 실제 읽을 집합 + 예비 + 여유 256 이 들어가면 통째로 (가용 7200 - 4096 - 256 = 2848)
    [InlineData(3200L, true)] // 안 들어가면 건너뛰지 않고 들어가는 만큼만 (부분 예열)
    [InlineData(null, true)]
    public void InitialMemoryGateUsesTheActualReadSetInsteadOfTheZoneSizeAndOtherwiseReadsOnlyWhatFits(long? readMib, bool partial)
    {
        GameSettings a = Game("a");
        var p = new Prewarmer([a], new ReadSetStreamer(readMib is { } m ? m * Mib : null), new PrewarmEnv
        {
            AvailableRamMib = () => 7200,
            GameRunning = _ => false,
            ZoneBytes = _ => 3312 * Mib,
            TraceExists = _ => true,
        }, _log, null, () => _now);

        PrewarmResult r = p.Run(a, "test");

        Assert.True(r.Ran);
        Assert.Null(r.SkipReason);
        Assert.Equal(partial, _log.Lines.Any(l => l.Contains(" partial ", StringComparison.Ordinal)));
    }

    [Fact]
    public void PrewarmCompletionReportsTheAdditionalGapMiB()
    {
        GameSettings a = Game("a");
        _p = Make(new GapStatsStreamer(), a);

        Assert.True(_p.Run(a, "test").Ran);

        Assert.Contains(_log.Lines, line => line.Contains("0.25 MiB gaps filled", StringComparison.Ordinal));
    }

    [Fact]
    public void PrewarmGapExpansionUsesVolatileRowsWithoutChangingSharedOpsOrPinnedPaths()
    {
        string temp = Directory.CreateTempSubdirectory("aster-gap-warm-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "asset.bin");
            string variablePath = Path.Combine(root, "cache_0_2.xpakdata");
            File.WriteAllBytes(path, new byte[2 * Mib]);
            File.WriteAllBytes(variablePath, new byte[2 * Mib]);
            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            const long span = 64 * 1024;
            const long gap = 512 * 1024;
            GameSettings game = Game("a") with
            {
                OriginRoot = root,
                AllowedRoots = [root],
                Trace = trace,
                Plan = planPath,
                MaxStreamMib = 4,
                MaxStreamSet = true,
            };
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 1024), [], [], [], new JObject()).Save(planPath);
            PlanOutput.SavePrefetch(trace, [new TraceEvent(path, 0, span)], [new TraceEvent(variablePath, 0, span), new TraceEvent(variablePath, span + gap, span)]);
            File.WriteAllText(AutoTracer.VolatilePath(game), PyJson.Dumps(new JObject { { variablePath, "changed between runs" } }));

            List<ReadOp> original = PrefetchStreamer.Ops(game);
            (List<ReadOp> expanded, long added, bool fits) = PrefetchStreamer.PrewarmOps(game, Mib);

            Assert.True(fits);
            Assert.Equal(gap, added);
            Assert.DoesNotContain(original, op => op.Path == variablePath); // 시작 스트림·표본·고정이 쓰는 공용 목록에는 가변 범위가 없음
            Assert.Equal(3 * span, new PrefetchStreamer().PrewarmReadBytes(game)); // 시작 전 예열의 읽기량에는 있음
            Assert.Equal(new ReadOp(variablePath, 0, 2 * span + gap), Assert.Single(expanded, op => op.Path == variablePath));
            Assert.Contains(new ReadOp(path, 0, span), original);
            Assert.Contains(new ReadOp(path, 0, span), expanded);
            Assert.Equal(original, PrefetchStreamer.Ops(game));
            Assert.Equal([path], PrefetchStreamer.PinPaths(game));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void PartialPrewarmListCutsASingleOversizedRangeToTheCapInsteadOfSkippingIt()
    {
        string temp = Directory.CreateTempSubdirectory("aster-partial-cut-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "big.bin");
            File.WriteAllBytes(path, new byte[8 * (int)Mib]);
            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            GameSettings game = Game("a") with { OriginRoot = root, AllowedRoots = [root], Trace = trace, Plan = planPath, MaxStreamMib = 2, MaxStreamSet = true }; // 8 MiB 단일 범위, 상한 2 MiB (8000/1904 의 축소판)
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 2048), [], [], [], new JObject()).Save(planPath);
            PlanOutput.SavePrefetch(trace, [new TraceEvent(path, 0, 8 * Mib)], []);

            (List<ReadOp> partial, long gaps, bool fits) = PrefetchStreamer.PrewarmOps(game, 0); // 예산 0 = 부분 예열

            Assert.True(fits);
            Assert.Equal(0, gaps);
            Assert.Equal(new ReadOp(path, 0, 2 * Mib), Assert.Single(partial)); // 앞 2 MiB 를 읽음
            Assert.Empty(PrefetchStreamer.PrewarmOps(game, 1 << 30).Ops); // 부분 예열이 아닌 경로는 그대로 (상한보다 큰 범위는 건너뜀)
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void PrewarmPlanIsBuiltOncePerRunForTheSameGameAndBudgetAndNeverOutsideARun()
    {
        GameSettings game = Game("a");
        int builds = 0;
        (List<ReadOp>, long, bool) Build() { builds++; return ([new ReadOp("x", 0, Mib)], 0L, true); }

        _ = PrewarmRun.Plan(game, 5, default, Build); // 회 밖: 캐시 없음
        _ = PrewarmRun.Plan(game, 5, default, Build);
        Assert.Equal(2, builds);

        builds = 0;
        using (PrewarmRun.Begin())
        {
            (List<ReadOp> first, _, _) = PrewarmRun.Plan(game, 5, default, Build);
            first.Clear(); // 돌려준 목록을 바꿔도 재사용분은 그대로
            Assert.Single(PrewarmRun.Plan(game, 5, default, Build).Ops);
            Assert.Equal(1, builds);
            _ = PrewarmRun.Plan(game, 6, default, Build); // 예산이 다르면 다시 계산
            Assert.Equal(2, builds);
            using var cancelled = new CancellationTokenSource(); // 재사용 중에도 취소는 그대로 검사
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => PrewarmRun.Plan(game, 6, cancelled.Token, Build));
        }

        _ = PrewarmRun.Plan(game, 6, default, Build); // 회가 끝나면 재사용 없음
        Assert.Equal(3, builds);
    }

    [Fact]
    public void PrewarmRunRecomputesTheRealPrewarmOpsWhenTheTraceIsUpdatedDuringTheRun()
    {
        string temp = Directory.CreateTempSubdirectory("aster-plan-reuse-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "big.bin");
            File.WriteAllBytes(path, new byte[8 * (int)Mib]);
            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            GameSettings game = Game("a") with { OriginRoot = root, AllowedRoots = [root], Trace = trace, Plan = planPath, MaxStreamMib = 2, MaxStreamSet = true };
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 2048), [], [], [], new JObject()).Save(planPath);
            PlanOutput.SavePrefetch(trace, [new TraceEvent(path, 0, 8 * Mib)], []);

            using (PrewarmRun.Begin())
            {
                List<ReadOp> first = PrefetchStreamer.PrewarmOps(game, 0).Ops;
                Assert.Equal(new ReadOp(path, 0, 2 * Mib), Assert.Single(first));
                PlanOutput.SavePrefetch(trace, [new TraceEvent(path, 0, Mib)], []); // 예열 도중 업데이트 정리가 예열 목록을 줄임
                Assert.Equal(new ReadOp(path, 0, Mib), Assert.Single(PrefetchStreamer.IncumbentPrewarmOps(game, 0))); // 낡은 목록이 아니라 새로 계산
            }
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void PrewarmRunBoundariesKeepReuseInsideNestedRunsAndIsolateTheNextRunAfterEndFailureOrCancel()
    {
        GameSettings game = Game("a");
        int builds = 0;
        (List<ReadOp>, long, bool) Build() { builds++; return ([new ReadOp("x", 0, Mib)], 0L, true); }

        using (PrewarmRun.Begin())
        {
            using (PrewarmRun.Begin()) // 중첩 (보충 등의 안쪽 스트림)
            {
                _ = PrewarmRun.Plan(game, 5, default, Build);
            }

            _ = PrewarmRun.Plan(game, 5, default, Build); // 안쪽이 끝나도 바깥 회의 재사용 유지
            Assert.Equal(1, builds);
        }

        _ = PrewarmRun.Plan(game, 5, default, Build); // 최외곽이 끝나면 재계산 (회 밖은 캐시 없음)
        Assert.Equal(2, builds);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (PrewarmRun.Begin())
            {
                _ = PrewarmRun.Plan(game, 5, default, Build);
                throw new InvalidOperationException();
            }
        }));
        Assert.Equal(3, builds);

        using (PrewarmRun.Begin()) // 예외 뒤 새 회는 이전 회의 목록을 보지 않음
        {
            _ = PrewarmRun.Plan(game, 5, default, Build);
            Assert.Equal(4, builds);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => PrewarmRun.Plan(game, 5, cts.Token, Build));
        }

        using (PrewarmRun.Begin()) // 취소 뒤에도 마찬가지
        {
            _ = PrewarmRun.Plan(game, 5, default, Build);
            Assert.Equal(5, builds);
        }
    }

    [Fact]
    public void PrewarmStreamCoreRecordsRealPlanOrbitAndStreamTimesAsNonZero()
    {
        string temp = Directory.CreateTempSubdirectory("aster-phase-core-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "big.bin");
            File.WriteAllBytes(path, new byte[8 * (int)Mib]);
            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            GameSettings game = Game("a") with { OriginRoot = root, AllowedRoots = [root], Trace = trace, Plan = planPath };
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 2048), [], [], [], new JObject()).Save(planPath);
            PlanOutput.SavePrefetch(trace, [new TraceEvent(path, 0, 8 * Mib)], []);
            var streamer = new PrefetchStreamer { Reorder = (_, ops, _) => { Thread.Sleep(30); return [.. ops]; } };

            using PrewarmRun run = PrewarmRun.Begin();
            (StreamStats stats, bool complete) = streamer.StreamPrewarm(game, CancellationToken.None, null, 1L << 30);
            var e = run.Elapsed();

            Assert.True(complete);
            Assert.True(stats.Bytes > 0);
            Assert.True(e.Plan > 0);
            Assert.InRange(e.Orbit, 0.025, 5); // 실제 Reorder 호출 시간
            Assert.True(e.Stream > 0);
            Assert.Equal(e.Total, e.Plan + e.Calibration + e.Common + e.Orbit + e.Stream + e.Other, 6);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void PrewarmPhaseTimesCountPlanOnceAndSumToTheTotalElapsed()
    {
        GameSettings game = Game("a");
        using PrewarmRun run = PrewarmRun.Begin();
        using (PrewarmRun.Time(PrewarmPhase.Stream))
        {
            Thread.Sleep(40);
        }

        using (PrewarmRun.Time(PrewarmPhase.Common))
        {
            _ = PrewarmRun.Plan(game, 1, default, () => { Thread.Sleep(30); return ([], 0L, true); });
        }

        Thread.Sleep(20); // 어느 단계에도 없는 시간은 Other
        var e = run.Elapsed();

        Assert.InRange(e.Stream, 0.035, 5);
        Assert.InRange(e.Plan, 0.025, 5);
        Assert.InRange(e.Common, 0, e.Plan); // 계획 계산 시간은 Common 에 이중으로 세지 않음
        Assert.InRange(e.Other, 0.015, 5);
        Assert.Equal(e.Total, e.Plan + e.Calibration + e.Common + e.Orbit + e.Stream + e.Other, 6);
    }

    [Fact]
    public void PrewarmDoneIsFollowedByAPhaseLineWhoseTimesSumToTheElapsed()
    {
        GameSettings a = Game("a");
        _p = Make(new BatchStreamer(1), a);

        Assert.True(_p.Run(a, "test").Ran);

        string line = Assert.Single(_log.Lines, l => l.Contains("prewarm phases", StringComparison.Ordinal));
        int doneAt = _log.Lines.ToList().FindIndex(l => l.Contains("prewarm done", StringComparison.Ordinal));
        Assert.True(doneAt >= 0);
        Assert.Equal(line, _log.Lines.ElementAt(doneAt + 1)); // done 줄 바로 다음
        double[] s = [.. System.Text.RegularExpressions.Regex.Matches(line, @"(\d+\.\d+) s").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(7, s.Length);
        Assert.InRange(Math.Abs(s[..6].Sum() - s[6]), 0, 0.05); // 반올림 오차 안에서 단계 합 = 전체
    }

    [Fact]
    public void PartialRewarmSamplesAreChosenOnlyInsideTheCutFrontThatWasRead()
    {
        string temp = Directory.CreateTempSubdirectory("aster-partial-probe-").FullName;
        try
        {
            string root = Path.Combine(temp, "game");
            Directory.CreateDirectory(root);
            string big = Path.Combine(root, "big.bin");
            string a = Path.Combine(root, "a.bin");
            string b = Path.Combine(root, "b.bin");
            foreach (string f in new[] { big, a, b })
            {
                File.WriteAllBytes(f, new byte[8 * (int)Mib]);
            }

            string trace = Path.Combine(temp, "game.prefetch.json");
            string planPath = Path.Combine(temp, "game.plan.json");
            new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
                new PlanZone(0, 2048), [], [], [], new JObject()).Save(planPath);
            GameSettings Cap(int mib) => Game("a") with { OriginRoot = root, AllowedRoots = [root], Trace = trace, Plan = planPath, MaxStreamMib = mib, MaxStreamSet = true };

            // 단일 큰 범위 (8 MiB) 를 2 MiB 로 자른 부분 예열: 표본은 앞 2 MiB 안
            PlanOutput.SavePrefetch(trace, [new TraceEvent(big, 0, 8 * Mib)], []);
            List<(string Path, long Offset, int Bytes)> single = PrefetchStreamer.PartialProbeSamples(Cap(2));
            Assert.NotEmpty(single);
            Assert.All(single, s => Assert.True(s.Path == big && s.Offset + s.Bytes <= 2 * Mib));
            Assert.Empty(CacheProbe.PickSamples(PrefetchStreamer.Ops(Cap(2)))); // 자르지 않은 목록은 큰 범위를 건너뛰어 표본이 없음

            // 여러 범위 (3 MiB + 3 MiB) 를 4 MiB 로 자른 부분 예열: 첫 범위 전체와 둘째의 앞 1 MiB 안
            PlanOutput.SavePrefetch(trace, [new TraceEvent(a, 0, 3 * Mib), new TraceEvent(b, 0, 3 * Mib)], []);
            List<(string Path, long Offset, int Bytes)> multi = PrefetchStreamer.PartialProbeSamples(Cap(4));
            Assert.NotEmpty(multi);
            Assert.All(multi, s => Assert.True(s.Path == a && s.Offset + s.Bytes <= 3 * Mib || s.Path == b && s.Offset + s.Bytes <= 1 * Mib));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void RefreshReadHonorsCancellationBeforeReading()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            StreamStats r = PrefetchStreamer.Refresh([new ReadOp(path, 0, 4096)], cts.Token);

            Assert.True(r.Aborted);
            Assert.Equal(0, r.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
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
        public const long PhysBytes = 32L << 30; // 1/2 = 16384 MiB

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
            ColdRead = (_, _) => (new StreamStats(ColdWorld.PhysBytes / 2, 20, 0, 1), true), // X 가 RAM 의 1/2 를 채움
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
    public void AColdWarmFillingHalfOfRamBlocksALaterBatchUntilItsGameStarts()
    {
        GameSettings x = Game("x");
        GameSettings y = Game("y", "launcher.exe");
        (Prewarmer p, _) = MakeColdPair(x, y);
        Assert.True(p.Run(x, "t").Ran);

        PrewarmResult blocked = p.OnProcessStart(Launcher()).Single();
        Assert.Equal("batch memory budget", blocked.SkipReason); // X 가 상주한 양 때문에

        p.NoteGameStart("x"); // 게임이 시작되면 그 게임의 콜드 몫을 풂
        _now += Prewarmer.SkipLogEvery; // 같은 트리거는 1 분 안에 한 번만 처리
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

        _now += Prewarmer.SkipLogEvery; // 같은 트리거는 1 분 안에 한 번만 처리
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
