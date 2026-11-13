using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tray;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 가용 메모리가 모자라면 예열을 통째로 건너뛰지 않고 앞쪽에서 들어가는 만큼만 읽는가 (부분 예열): 크기, 너무 작으면 건너뜀, 압박 중단, 완전 예열로 기록하지 않음.
public sealed class PartialMemoryPrewarmTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_partial_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly RecordingStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private long _avail;
    private long _physical;
    private long _hotMib = 8000;
    private Queue<long>? _availableReads;
    private int _snapshots;
    private int _snapshotChecks;
    private bool _gameRunning;

    private List<int> Probed { get; } = [];

    public PartialMemoryPrewarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class RecordingStreamer : IGameStreamer
    {
        public List<(int MaxStreamMib, long MaxBytes)> Calls { get; } = [];

        public Action<Action<long, long>>? OnProgress { get; set; }

        /// 실제로 읽었다고 알릴 양 (MiB, 기본은 받은 상한 그대로).
        public long? ReadMib { get; set; }

        /// 읽기 통계에 담을 오류 수.
        public long ErrorCount { get; set; }

        public Func<GameSettings, long?>? ReadSetBytes { get; set; }

        public Func<GameSettings, bool, long?>? ScheduledReadSetBytes { get; set; }

        public Func<GameSettings, long, long>? ScheduledStreamBytes { get; set; }

        public long? PrewarmReadBytes(GameSettings game) => ReadSetBytes?.Invoke(game);

        public long? PrewarmReadBytes(GameSettings game, bool partial) => ScheduledReadSetBytes?.Invoke(game, partial) ?? PrewarmReadBytes(game);

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            long total = ScheduledStreamBytes?.Invoke(game, maxBytes) ?? (long)game.MaxStreamMib * Mib;
            Calls.Add((game.MaxStreamMib, maxBytes));
            progress?.Invoke(0, total);
            if (OnProgress is not null && progress is not null)
            {
                OnProgress(progress);
            }

            return (new StreamStats(ReadMib is { } read ? read * Mib : total, 1, ErrorCount, 0.01), !stop.IsCancellationRequested);
        }
    }

    private Prewarmer Make(GameSettings game, params GameSettings[] others)
    {
        _streamer.ReadSetBytes ??= g => Math.Min(_hotMib, g.StreamCapBytes(_hotMib * Mib) / Mib) * Mib;
        _streamer.ScheduledStreamBytes ??= (g, _) => g.StreamCapBytes(_hotMib * Mib);
        return new Prewarmer([game, .. others], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availableReads is { Count: > 0 } ? _availableReads.Dequeue() : _avail,
            GameRunning = _ => _gameRunning,
            GameRunningSnapshot = () =>
            {
                _snapshots++;
                return _ =>
                {
                    _snapshotChecks++;
                    return _gameRunning;
                };
            },
            ZoneBytes = g => Math.Min(_hotMib, g.StreamCapBytes(_hotMib * Mib) / Mib) * Mib, // 계획의 존 (핫 집합) 을 max_stream_mib 로 자른 값
            TraceExists = _ => true,
            PhysicalRamBytes = () => _physical,
            ProbeReadMs = g =>
            {
                Probed.Add(g.MaxStreamMib);
                return new ProbeResult(8, 8, 5.0); // 읽은 구간이 밀려나 차가움
            },
        }, _log, StatusPath, () => _now);
    }

    private static GameSettings Game() => Games.Make("g", "g.exe");

    private JObject Status() => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["g"]!;

    private GameSettings ScheduledGame(string name, int maxStreamMib, params (string File, int Mib)[] ranges) => ScheduledGame(name, maxStreamMib, false, ranges);

    // residual: 계획의 task 합계가 목록보다 훨씬 큰 잔여 계획 (8000 MiB)
    private GameSettings ScheduledGame(string name, int maxStreamMib, bool residual, params (string File, int Mib)[] ranges)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        string trace = Path.Combine(_dir, name + ".prefetch.json");
        string plan = Path.Combine(_dir, name + ".plan.json");
        var rows = new List<TraceEvent>();
        foreach ((string file, int mib) in ranges)
        {
            string path = Path.Combine(root, file);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength((long)mib * Mib);
            rows.Add(new TraceEvent(path, 0, (long)mib * Mib));
        }

        var stats = new JObject();
        PlanTask[] tasks = [];
        if (residual)
        {
            stats["residual"] = 1L;
            tasks = [new PlanTask(rows[0].Path, 0, 8000L * Mib / 4096, 0)];
        }

        new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root).ToUpperInvariant(), 4096, 65536,
            new PlanZone(0, 1), tasks, [], [], stats).Save(plan);
        PlanOutput.SavePrefetch(trace, rows);
        return Game() with
        {
            OriginRoot = root,
            AllowedRoots = [root],
            Trace = trace,
            Plan = plan,
            MaxStreamMib = maxStreamMib,
            MaxStreamSet = true,
        };
    }

    [Theory]
    [InlineData(6000L, 1648)] // 가용 6000 - 예비 4096 - 처음 창에도 두는 여유 256
    [InlineData(4096L + 256 + 256, 256)] // 경계: 256 MiB 면 읽음
    public void ReadsOnlyTheFrontThatFitsAndSkipsTheTailGapFilling(long avail, long expectedMib)
    {
        _avail = avail;
        Prewarmer p = Make(Game());

        PrewarmResult r = p.Run(Game(), "test");

        Assert.True(r.Ran);
        Assert.Equal((int)expectedMib, Assert.Single(_streamer.Calls).MaxStreamMib); // 앞쪽부터 들어가는 만큼만
        Assert.Equal(0, _streamer.Calls[0].MaxBytes); // 틈 메우기 예산 0
        Assert.Contains(_log.Lines, l => l.Contains($"partial {expectedMib}/{_hotMib} MiB", StringComparison.Ordinal));
        Assert.True(p.SupplementPending("g")); // 완전 예열로 기록하지 않음: 나머지가 남음
        JObject s = Status();
        Assert.Equal((expectedMib, _hotMib, true), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!, (bool)s["partial"]!));
    }

    [Fact]
    public void BelowTheMinimumPartialSizeItIsSkippedAsBefore()
    {
        _avail = 4096 + 256 + 255;
        Prewarmer p = Make(Game());

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.StartsWith("low memory", r.SkipReason, StringComparison.Ordinal);
        Assert.Empty(_streamer.Calls);
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void PhysicalRamHalfAlsoLimitsThePartialSize()
    {
        (_avail, _physical, _hotMib) = (12000, 4L << 30, 9000); // 가용 12000 - 4096 = 7904 이지만 물리 4 GiB / 2 = 2048
        Prewarmer p = Make(Game());

        Assert.True(p.Run(Game(), "test").Ran);

        Assert.Equal(2048, Assert.Single(_streamer.Calls).MaxStreamMib);
    }

    [Fact]
    public void EnoughMemoryStillReadsEverythingAndForgetsTheEarlierPartialRecord()
    {
        _avail = 6000;
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "test").Ran);
        Assert.True(p.SupplementPending("g"));
        _avail = 20000;
        _now += TimeSpan.FromHours(1);

        PrewarmResult full = p.Run(Game(), "rewarm", ignoreCooldown: true);

        Assert.True(full.Ran);
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(Game().MaxStreamMib, _streamer.Calls[1].MaxStreamMib); // 줄이지 않은 설정 그대로
        Assert.False(p.SupplementPending("g"));
        Assert.False(Status().ContainsKey("partial"));
    }

    [Fact]
    public void MemoryPressureWhileReadingStopsThePartialReadAndRecordsNothingAsWarm()
    {
        _avail = 6000;
        _streamer.ReadMib = 0; // 아직 아무것도 읽지 못한 채 멈춤
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096; // 읽는 중에 메모리 압박
            progress(64 * Mib, 1904 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.False(p.SupplementPending("g")); // 0 MiB 에서 멈췄으면 부분 상태도 남기지 않음
        Assert.Null(p.PartialInfo("g"));
    }

    [Fact]
    public void AMemoryStopKeepsWhatWasReadAsPartialAndTheNextSupplementReadsPastIt()
    {
        _avail = 6000;
        _streamer.ReadMib = 300;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096; // 300 MiB 읽은 뒤 메모리 압박
            progress(300 * Mib, 1648 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal((300L, _hotMib), p.PartialInfo("g")); // 읽은 데까지가 부분 상태 (설정한 창 1648 이 아니라)
        int cancelledAt = _log.Lines.ToList().FindIndex(l => l.Contains("prewarm cancelled (test) after 300 MiB (memory reserve", StringComparison.Ordinal));
        int keptAt = _log.Lines.ToList().FindIndex(l => l.Contains("prewarm stopped by memory; kept the 300 of 8000 MiB read as partial", StringComparison.Ordinal));
        Assert.True(cancelledAt >= 0 && keptAt > cancelledAt); // 취소 줄 다음에 남긴 양
        Assert.Equal((300L, _hotMib, true), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!, (bool)Status()["partial"]!));
        Assert.Equal(300, p.WarmedPart(Game()).MaxStreamMib); // 재예열 점검의 표본도 읽은 앞부분에서만
        _streamer.OnProgress = null;
        _streamer.ReadMib = null;
        _avail = 20000;
        _now += TimeSpan.FromMinutes(3);

        Assert.True(p.Supplement(Game(), CancellationToken.None));

        Assert.Equal(2, _streamer.Calls.Count);
        Assert.True(_streamer.Calls[1].MaxStreamMib > 300);
    }

    [Fact]
    public void AMemoryStopKeepsTheRetryPendingAndRespectsTheFloorAndTheRecoveryCondition()
    {
        _avail = 6000;
        _streamer.ReadMib = 300;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096;
            progress(300 * Mib, 1648 * Mib);
        };
        Assert.False(p.Run(Game(), "test").Ran);
        _streamer.OnProgress = null;
        Assert.True(p.HasPendingWarm("g"));

        _avail = 20000; // 메모리는 넉넉해졌지만 멈춘 지 2 분 전
        Assert.False(p.Supplement(Game(), CancellationToken.None));
        Assert.Equal("memory retry floor", p.Run(Game(), "rewarm", ignoreCooldown: true).SkipReason); // 재시도 성격의 예열도 같은 하한
        Assert.Single(_streamer.Calls);

        _now += Prewarmer.MemoryRetryFloor + TimeSpan.FromSeconds(1);
        _avail = 4096 + Prewarmer.FinalShrinkHeadroomMib + 300; // 읽은 양 + 여유 + 예비량에 못 미침: 읽지 않고 대기는 남김
        Assert.False(p.Supplement(Game(), CancellationToken.None));
        Assert.Single(_streamer.Calls);
        Assert.True(p.HasPendingWarm("g"));

        _avail = 4096 + Prewarmer.FinalShrinkHeadroomMib + 301; // 읽은 양 + 여유 + 예비량을 1 MiB 넘기면 이어 읽음
        Assert.True(p.Supplement(Game(), CancellationToken.None));
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(301, _streamer.Calls[1].MaxStreamMib);
    }

    [Fact]
    public void AMemoryStopOfTheWholePlanIsNotRecordedAsPartialButRetriesSoonWhenSomethingWasRead()
    {
        (_hotMib, _avail) = (8000, 12500); // 전체 (8000 + 여유 256 + 예비 4096) 가 들어감
        _streamer.ReadMib = 300;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096;
            progress(300 * Mib, 8000 * Mib);
        };

        Assert.False(p.Run(Game(), "test").Ran);

        Assert.Equal(Game().MaxStreamMib, Assert.Single(_streamer.Calls).MaxStreamMib); // 줄이지 않은 전체 계획
        Assert.Null(p.PartialInfo("g")); // 읽은 곳이 목록 앞쪽과 같다고 보장할 수 없어 남기지 않음
        Assert.False(Status().ContainsKey("partial"));
        Assert.Equal(_now + Prewarmer.MemoryRetryFloor, p.PendingRetryAt("g"));
    }

    [Fact]
    public void AMemoryStopWithErrorsIsNotRecordedAsPartial()
    {
        _avail = 6000;
        (_streamer.ReadMib, _streamer.ErrorCount) = (300, 1);
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096;
            progress(300 * Mib, 1648 * Mib);
        };

        Assert.False(p.Run(Game(), "test").Ran);

        Assert.Null(p.PartialInfo("g"));
    }

    [Fact]
    public void AWholeSetThatFitsByLessThanTheHeadroomIsReadAsAWindowSoABoundaryDoesNotStopAtZero()
    {
        (_hotMib, _avail) = (1000, 1000 + 4096 + 29); // 전체가 29 MiB 여유로 들어감
        Prewarmer p = Make(Game());

        Assert.True(p.Run(Game(), "test").Ran);

        Assert.Equal(1000 + 29 - Prewarmer.FinalShrinkHeadroomMib, Assert.Single(_streamer.Calls).MaxStreamMib); // 여유 256 을 남긴 창
    }

    [Fact]
    public void AStopThatReadNothingLeavesNoPartialStateAndKeepsTheNormalRetryPeriod()
    {
        _avail = 6000;
        _streamer.ReadMib = 0;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096;
            progress(0, 1648 * Mib);
        };

        Assert.False(p.Run(Game(), "test").Ran);

        Assert.False(p.SupplementPending("g"));
        Assert.Null(p.PartialInfo("g"));
        Assert.False(Status().ContainsKey("partial"));
        Assert.True(p.HasPendingWarm("g"));
        Assert.Equal(_now + TimeSpan.FromMinutes(AutoSettings.Default.RewarmMinutes), p.PendingRetryAt("g")); // 읽은 게 없으면 기존 재시도 주기 그대로
    }

    [Theory]
    [InlineData(0L)] // 사전 검사 때는 전체가 들어갔다가 첫 묶음 검사에서 가용이 줄어 0 MiB 에서 멈춘 경우
    [InlineData(7L)] // 몇 MiB 읽은 뒤 멈춘 경우
    public void AFullPathStoppedByMemoryIsReadAgainOnceAsTheWindowThatFitsNow(long firstReadMib)
    {
        _avail = 8000 + 4096 + Prewarmer.FinalShrinkHeadroomMib + 100; // 사전 검사에는 전체 8000 이 들어감
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            if (_streamer.Calls.Count > 1)
            {
                _streamer.ReadMib = null; // 다시 읽기는 끝까지 읽음
                return;
            }

            (_streamer.ReadMib, _avail) = (firstReadMib, 8000); // 첫 읽기 도중 가용이 줄어 묶음 검사가 메모리로 멈춤
            progress(firstReadMib * Mib, 8000 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.True(r.Ran);
        Assert.Equal([Game().MaxStreamMib, 8000 - 4096 - Prewarmer.FinalShrinkHeadroomMib], _streamer.Calls.Select(c => c.MaxStreamMib)); // 전체 → 그 시점 가용에 들어가는 창 (3648)
        Assert.Equal(0, _streamer.Calls[1].MaxBytes); // 창 경로는 틈 메우기 없음
        Assert.Contains(_log.Lines, l => l.Contains("prewarm stopped by memory before the full list was read; reading the first 3648 MiB of 8000 MiB instead", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("prewarm cancelled", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("partial 3648/8000 MiB", StringComparison.Ordinal));
        Assert.Equal((3648L, 8000L), p.PartialInfo("g")); // 부분 예열로 기록
        Assert.True((bool)Status()["partial"]!);
    }

    [Fact]
    public void AFullPathStoppedByAnythingButMemoryIsNeverReadAgain()
    {
        _avail = 8000 + 4096 + Prewarmer.FinalShrinkHeadroomMib + 100;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            (_gameRunning, _avail, _streamer.ReadMib) = (true, 8000, 7); // 가용도 줄었지만 먼저 걸리는 사유는 게임 시작
            progress(7 * Mib, 8000 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("prewarm cancelled (test) after 7 MiB (game started)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("before the full list was read", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameThatStartsRightAfterTheMemoryStopKeepsTheFullPathFromBeingReadAgain()
    {
        _avail = 8000 + 4096 + Prewarmer.FinalShrinkHeadroomMib + 100;
        Prewarmer p = Make(Game());
        p.StreamEnded += (_, _) => _gameRunning = true; // 첫 읽기가 메모리로 멈춘 직후, 다시 읽기 전에 게임이 뜸
        _streamer.OnProgress = progress =>
        {
            (_streamer.ReadMib, _avail) = (7, 8000);
            progress(7 * Mib, 8000 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Single(_streamer.Calls);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("before the full list was read", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWindowReadAfterTheFallbackIsNotFallenBackAgainWhenItStopsByMemoryToo()
    {
        _avail = 8000 + 4096 + Prewarmer.FinalShrinkHeadroomMib + 100;
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            bool first = _streamer.Calls.Count == 1;
            (_streamer.ReadMib, _avail) = (300, first ? 8000 : 4500); // 다시 읽는 창도 가용이 더 줄어 멈춤
            progress(300 * Mib, (first ? 8000 : 3648) * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Equal(2, _streamer.Calls.Count); // 한 회에 한 번만
        Assert.Single(_log.Lines, l => l.Contains("before the full list was read", StringComparison.Ordinal));
        Assert.Equal((300L, 8000L), p.PartialInfo("g")); // 창 경로의 부분 확정 규칙 그대로
    }

    [Fact]
    public void TheBatchCheckTakesOneProcessSnapshotNoMatterHowManyGamesItJudges()
    {
        _avail = 20000;
        Prewarmer p = Make(Game(), Games.Make("h", "h.exe"));
        _streamer.OnProgress = progress =>
        {
            progress(1 * Mib, 8000 * Mib);
            progress(2 * Mib, 8000 * Mib);
        };

        Assert.True(p.Run(Game(), "test").Ran);

        Assert.Equal(3, _snapshots); // 묶음 점검 3 번 (시작 0 MiB 와 위의 둘) 마다 한 번
        Assert.Equal(_snapshots * 2, _snapshotChecks); // 한 스냅샷으로 두 게임을 판정
    }

    [Fact]
    public void A39MibDropAfterPlanningReadsTheFrontAndRecordsTheRestForSupplement()
    {
        _hotMib = 8785;
        _avail = 12842;
        _availableReads = new Queue<long>([12881, 12842, 12842]);
        Prewarmer p = Make(Game());

        PrewarmResult result = p.Run(Game(), "test");

        Assert.True(result.Ran);
        Assert.Equal((8490, 0L), Assert.Single(_streamer.Calls)); // 39 MiB 부족에서 취소하지 않고 256 MiB 여유를 둔 앞부분
        Assert.Equal((8490L, 8785L), p.PartialInfo("g"));
        Assert.True(p.SupplementPending("g"));
        Assert.Equal((8490L, 8785L, true), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!, (bool)Status()["partial"]!));
    }

    [Fact]
    public void A39MibDropOnAnOversizedScheduledRangeReads1609MibAndRecordsPartialSuccess()
    {
        _hotMib = 8000;
        _avail = 5961;
        _availableReads = new Queue<long>([6256, 5961]);
        GameSettings game = ScheduledGame("boundary", 8000, ("large.bin", 8000));
        var scheduler = new PrefetchStreamer();
        _streamer.ScheduledReadSetBytes = (g, partial) => scheduler.PrewarmReadBytes(g, partial);
        _streamer.ScheduledStreamBytes = (g, maxBytes) => PrefetchStreamer.PrewarmOps(g, maxBytes).Ops.Sum(op => op.Length);
        Prewarmer p = Make(game);

        PrewarmResult result = p.Run(game, "test");

        GameSettings read = game with { MaxStreamMib = 1609, MaxStreamSet = true };
        long actualScheduledBytes = PrefetchStreamer.PrewarmOps(read, 0).Ops.Sum(op => op.Length);
        Assert.True(result.Ran);
        Assert.Equal(1609L * Mib, actualScheduledBytes);
        Assert.Equal((1609, 0L), Assert.Single(_streamer.Calls));
        Assert.Equal((1609L, 8000L), p.PartialInfo("g"));
        Assert.True(p.SupplementPending("g"));
        Assert.Equal((1609L, 8000L, true), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!, (bool)Status()["partial"]!));
    }

    [Fact]
    public void AListReadToTheEndWithinItsAllocationIsDoneEvenWhenWholeFileFillInsWouldExceedIt()
    {
        // 잔여 계획 (task 합 8000 MiB) 이고 목록이 큰 파일의 40% 만 담음: 예산이 task 합이면 스케줄러가 그 파일을 통째 (100 MiB) 로 승격해 배정량 (340 MiB) 을 넘음.
        // head.bin 300 MiB 는 앞부분 (256 MiB) 로 먼저 나가 승격 대상이 아님
        GameSettings game = ScheduledGame("promoted", 4096, true, ("head.bin", 300), ("big.bin", 100)) with { MaxStreamSet = false };
        PlanOutput.SavePrefetch(game.Trace, [new TraceEvent(Path.Combine(game.OriginRoot, "head.bin"), 0, 300 * Mib), new TraceEvent(Path.Combine(game.OriginRoot, "big.bin"), 0, 40 * Mib)]);
        var scheduler = new PrefetchStreamer();
        _streamer.ScheduledReadSetBytes = (g, partial) => scheduler.PrewarmReadBytes(g, partial);
        _streamer.ScheduledStreamBytes = (g, maxBytes) => PrefetchStreamer.PrewarmOps(g, maxBytes).Ops.Sum(op => op.Length);
        var p = new Prewarmer([game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 20000,
            GameRunning = _ => false,
            ZoneBytes = PrewarmEnv.Create().ZoneBytes, // 실제 계획·목록을 읽는 판정 (배정·표시와 같은 기준)
            TraceExists = _ => true,
            PhysicalRamBytes = () => 32L << 30,
        }, _log, StatusPath, () => _now);

        PrewarmResult result = p.Run(game, "rewarm", ignoreCooldown: true, roomMib: 340);

        Assert.True(result.Ran);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("fell after planning", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm done (rewarm): 340 MiB requested", StringComparison.Ordinal));
        Assert.Null(p.PartialInfo("g"));
        Assert.False(p.SupplementPending("g"));
        Assert.False(Status().ContainsKey("partial"));
        Assert.Equal(340L * Mib, scheduler.PrewarmReadBytes(game)); // 읽을 목록 = 배정량
    }

    [Fact]
    public void PartialReadSizeMatchesTheScheduledListWithOversizedAndSmallRangesMixed()
    {
        _hotMib = 11;
        GameSettings game = ScheduledGame("mixed", 6,
            ("small-a.bin", 1), ("large.bin", 8), ("small-b.bin", 1), ("small-c.bin", 1));
        var scheduler = new PrefetchStreamer();
        GameSettings partial = game with { MaxStreamMib = 6, MaxStreamSet = true };

        long? recalculatedBytes = scheduler.PrewarmReadBytes(partial, partial: true);
        long actualScheduledBytes = PrefetchStreamer.PrewarmOps(partial, 0).Ops.Sum(op => op.Length);

        Assert.Equal(6L * Mib, recalculatedBytes);
        Assert.Equal(recalculatedBytes, actualScheduledBytes);
    }

    [Fact]
    public void APreviouslyPartialPlanIsTrimmedAgainWhenTheFinalMemoryCheckDrops()
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 처음 창에도 두는 여유를 더해 창을 1904 로 유지
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "test").Ran);
        Assert.Equal((1904L, 8000L), p.PartialInfo("g"));
        _streamer.Calls.Clear();
        _avail = 5600;
        _availableReads = new Queue<long>([6000, 5600, 5600]);

        PrewarmResult result = p.Run(Game(), "test", ignoreCooldown: true);

        Assert.True(result.Ran);
        Assert.Equal(1248, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.Equal((1248L, 8000L), p.PartialInfo("g"));
    }

    [Fact]
    public void FinalReadListSizeTakesPrecedenceOverTheZoneSize()
    {
        _hotMib = 1000;
        _avail = 6000;
        _streamer.ReadSetBytes = g => Math.Min(5000, g.StreamCapBytes(5000 * Mib) / Mib) * Mib;
        _availableReads = new Queue<long>([6000, 6000, 6000]);
        Prewarmer p = Make(Game());

        PrewarmResult result = p.Run(Game(), "test");

        Assert.True(result.Ran);
        Assert.Equal(1648, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.Equal((1648L, 5000L), p.PartialInfo("g"));
    }

    [Fact]
    public void FinalTrimUsesConfiguredReserveWhenItExceedsTheDefault()
    {
        GameSettings game = Game() with { Prewarm = Game().Prewarm with { MinFreeRamMib = 6000 } };
        _avail = 10000;
        _availableReads = new Queue<long>([14000, 10000, 10000]);
        Prewarmer p = Make(game);

        PrewarmResult result = p.Run(game, "test");

        Assert.True(result.Ran);
        Assert.Equal(3744, Assert.Single(_streamer.Calls).MaxStreamMib); // 10000 - 6000 예비 - 256 여유
        Assert.Equal((3744L, 8000L), p.PartialInfo("g"));
    }

    [Fact]
    public void FinalTrimBelowTheMinimumSkipsWithoutReading()
    {
        _avail = 4607;
        _availableReads = new Queue<long>([6000, 4607]);
        Prewarmer p = Make(Game());

        PrewarmResult result = p.Run(Game(), "test");

        Assert.False(result.Ran);
        Assert.StartsWith("low memory", result.SkipReason, StringComparison.Ordinal);
        Assert.Empty(_streamer.Calls);
        Assert.False(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, line => line.Contains("prewarm skipped (low memory", StringComparison.Ordinal));
    }

    [Fact]
    public void RewarmChecksSampleOnlyThePartReadIntoMemory()
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 처음 창에도 두는 여유를 더해 창을 1904 로 유지
        Prewarmer p = Make(Game());
        Assert.Equal(Game().MaxStreamMib, p.WarmedPart(Game()).MaxStreamMib);

        Assert.True(p.Run(Game(), "test").Ran);

        GameSettings part = p.WarmedPart(Game());
        Assert.Equal((1904, true), (part.MaxStreamMib, part.MaxStreamSet)); // 표본은 읽은 앞부분에서만
        Assert.True(p.MemoryPartialPending("g"));
    }

    [Fact]
    public void ARewarmCheckProbesOnlyTheReadFrontAndReadsItAgainWhenItWentCold()
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 처음 창에도 두는 여유를 더해 창을 1904 로 유지
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "service start").Ran);
        AutoSettings auto = AutoSettings.Default;
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(auto.RewarmMinutes + 1);

        p.Tick(auto);

        Assert.Equal([1904], Probed); // 표본은 읽은 앞부분 (남은 구간은 항상 차가우므로 넣지 않음)
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(1904, _streamer.Calls[1].MaxStreamMib); // 밀려난 앞부분만 다시 읽음
        Assert.True(p.SupplementPending("g")); // 여전히 완전 예열이 아님
    }

    [Fact]
    public void StartupCapAndMemoryCapTogetherRecordTheOriginalSizeAndTheActuallyReadAmountOnce()
    {
        _avail = 4096 + 256 + 512; // 시작 상한 1024 도 가용 - 예비 - 여유 = 512 보다 큼
        Prewarmer p = Make(Game());
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024 });

        PrewarmResult r = Assert.Single(p.RunServiceStart());

        Assert.True(r.Ran);
        Assert.Equal(512, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.Equal((512L, 8000L), p.PartialInfo("g")); // 원래 전체 크기 8000, 실제 읽은 512
        JObject s = Status();
        Assert.Equal((512L, 8000L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!));
        Assert.True(p.SupplementPending("g"));
    }

    [Fact]
    public void RestartedServiceRestoresTheMemoryPartialStateSoRewarmChecksStillSampleOnlyTheReadFront()
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 처음 창에도 두는 여유를 더해 창을 1904 로 유지
        Assert.True(Make(Game()).Run(Game(), "test").Ran);

        Prewarmer second = Make(Game()); // 같은 상태 파일, 부팅은 하루 전

        Assert.True(second.MemoryPartialPending("g"));
        Assert.Equal(1904, second.WarmedPart(Game()).MaxStreamMib);
        Assert.Equal((1904L, 8000L), second.PartialInfo("g"));
    }

    [Fact]
    public void ASuccessfulSupplementClearsThePartialMarkAndTheMemoryState()
    {
        _avail = 6000;
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "test").Ran);
        _avail = 20000; // 이제 전체가 들어감

        Assert.True(p.Supplement(Game(), CancellationToken.None));

        JObject s = Status();
        Assert.Equal((8000L, 8000L, false), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!, (bool)s["partial"]!));
        Assert.False(p.MemoryPartialPending("g"));
        Assert.Null(p.PartialInfo("g"));
        Assert.Equal(Game().MaxStreamMib, p.WarmedPart(Game()).MaxStreamMib);
    }

    [Fact]
    public void PartialPrewarmIsShownAsPartialInBothLanguagesAndNotCountedAsPrewarmed()
    {
        var game = new AutoGame("auto", null, "done", "prewarmed (partial 1904/8000 MiB)");

        Assert.Equal("부분 예열됨 1904/8000 MiB", AutoStatusFile.StageExtra(game, "ko"));
        Assert.Equal("partially prewarmed 1904/8000 MiB", AutoStatusFile.StageExtra(game, "en"));
        Assert.Equal("예열됨", AutoStatusFile.StageExtra(game with { Detail = "prewarmed" }, "ko"));
        Assert.Equal(1, AutoProgress.From([("a", "done", "prewarmed (partial 1/2 MiB)", null), ("b", "done", "prewarmed", null)], _dir).Prewarmed);
    }

    [Fact]
    public void PartialStateUsesWhatTheStreamActuallyReadNotTheConfiguredCap()
    {
        _avail = 6000;
        _streamer.ReadMib = 1000; // 상한은 1904 지만 실제로는 1000 MiB 만 읽음
        Prewarmer p = Make(Game());

        Assert.True(p.Run(Game(), "test").Ran);

        Assert.Equal((1000L, 8000L), p.PartialInfo("g"));
        Assert.Equal((1000L, 8000L), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!));
        Assert.Equal(1000, p.WarmedPart(Game()).MaxStreamMib);
        Assert.Contains(_log.Lines, l => l.Contains("partial 1000/8000 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkippedRecordAfterAPartialPrewarmKeepsThePartialStateAcrossARestart()
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib; // 처음 창에도 두는 여유를 더해 창을 1904 로 유지
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "test").Ran);

        PrewarmResult again = p.Run(Game(), "test"); // 쿨다운 안: 건너뜀 기록이 상태 파일을 덮어씀

        Assert.False(again.Ran);
        JObject s = Status();
        Assert.Equal(("skipped", true, 1904L, 8000L), ((string)s["result"]!, (bool)s["partial"]!, (long)s["warmed_mib"]!, (long)s["hot_mib"]!));
        Prewarmer second = Make(Game()); // 재시작
        Assert.True(second.MemoryPartialPending("g"));
        Assert.Equal(1904, second.WarmedPart(Game()).MaxStreamMib);
    }

    [Fact]
    public void AStreamThatReadNothingIsNotRecordedAsOkNorAsPartial()
    {
        _avail = 6000;
        _streamer.ReadMib = 0;
        Prewarmer p = Make(Game());

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Equal("nothing read", r.SkipReason);
        Assert.Equal("skipped", (string)Status()["result"]!);
        Assert.False(Status().ContainsKey("partial"));
        Assert.False(p.SupplementPending("g"));
        Assert.Null(p.PartialInfo("g"));
        Assert.Null(p.LastWarmed("g"));
    }

    [Fact]
    public void ASmallActualPartialReadBelowTheStartMinimumIsStillRestoredAfterARestart()
    {
        _avail = 6000;
        _streamer.ReadMib = 128; // 실행 최소 (256) 보다 작게 읽었지만 부분 성공
        Prewarmer p = Make(Game());
        Assert.True(p.Run(Game(), "test").Ran);
        Assert.False(p.Run(Game(), "test").Ran); // 쿨다운 건너뜀 기록이 상태 파일을 덮어씀

        Prewarmer second = Make(Game()); // 재시작

        Assert.True(second.MemoryPartialPending("g"));
        Assert.Equal((128L, 8000L), second.PartialInfo("g"));
        Assert.Equal(128, second.WarmedPart(Game()).MaxStreamMib); // 표본 제한 유지
    }

    [Fact]
    public void AStreamThatReadNothingIsNotAnnouncedAsFinishedSoTheAutoStatusStaysUnprewarmed()
    {
        _avail = 6000;
        _streamer.ReadMib = 0;
        Prewarmer p = Make(Game());
        var ended = new List<bool>();
        p.StreamEnded += (_, complete) => ended.Add(complete);

        Assert.False(p.Run(Game(), "test").Ran);

        Assert.Equal([false], ended);
    }

    [Fact]
    public void PartialSelectionKeepsObservedRangesFirstAndSplitsAtTheBudgetBoundary()
    {
        TraceEvent[] rows = [
            new("observed-a", 0, 100),
            new("supplement", 0, 500, Observed: false),
            new("observed-b", 0, 200),
        ];

        List<TraceEvent> partial = PrefetchStreamer.SelectToBudget(rows, 150, rows.Length, 0, out _);
        List<TraceEvent> full = PrefetchStreamer.SelectToBudget(rows, 1000, rows.Length, 0, out _);

        Assert.Equal([("observed-a", 100L), ("observed-b", 50L)], partial.Select(e => (e.Path, e.Length)));
        Assert.All(partial, e => Assert.True(e.Observed));
        Assert.Equal(rows, full); // 전체가 들어가면 선택 집합과 순서는 기존 목록 그대로
    }

    [Fact]
    public void PartialSelectionPutsTheEarlyWarmOnlyRowsFirstAndOldWarmOnlyRowsLast()
    {
        TraceEvent[] rows = [
            new("hot-a", 0, 100),
            new("hot-b", 0, 100),
            new("guess", 0, 100, Observed: false),
            new("warm-late", 0, 100, TMs: 90_000),
            new("warm-early", 0, 100, TMs: 5_000),
            new("warm-old", 0, 100), // t_ms 없는 옛 행
        ];

        List<TraceEvent> tight = PrefetchStreamer.SelectToBudget(rows, 250, 3, 60_000, out long tightFront);
        List<TraceEvent> wide = PrefetchStreamer.SelectToBudget(rows, 450, 3, 60_000, out long wideFront);
        List<TraceEvent> all = PrefetchStreamer.SelectToBudget(rows, 1000, 3, 60_000, out long allFront);

        Assert.Equal([("warm-early", 100L), ("hot-a", 100L), ("hot-b", 50L)], tight.Select(e => (e.Path, e.Length))); // 앞부분 예열 전용이 맨 앞, 선읽기 꼬리가 잘림
        Assert.Equal([("warm-early", 100L), ("hot-a", 100L), ("hot-b", 100L), ("warm-late", 100L), ("warm-old", 50L)], wide.Select(e => (e.Path, e.Length)));
        Assert.Equal((100L, 100L), (tightFront, wideFront)); // 스케줄러 앞부분에 더할 바이트
        Assert.Equal(rows, all); // 예산이 충분하면 목록 그대로
        Assert.Equal(0, allFront);
    }

    [Fact]
    public void PartialSelectionDoesNotLetAnOverflowRowWithAnEarlyTimePushOutObservedRows()
    {
        TraceEvent[] rows = [
            new("hot-a", 0, 100),
            new("hot-b", 0, 100),
            new("extra", 0, 100, TMs: 1_000, Overflow: true), // 존 밖에서 덧붙인 행: t_ms 가 앞부분이어도 (가) 가 아님
            new("volatile-early", 0, 100, TMs: 2_000),
        ];

        List<TraceEvent> kept = PrefetchStreamer.SelectToBudget(rows, 250, 2, 60_000, out long front);

        Assert.Equal([("volatile-early", 100L), ("hot-a", 100L), ("hot-b", 50L)], kept.Select(e => (e.Path, e.Length)));
        Assert.Equal(100, front);
    }

    [Fact]
    public void ThePartialScheduleReadsTheEarlyVolatileRangeFirstAndUsesTheDefaultFrontWithoutALoadBoundary()
    {
        GameSettings game = ScheduledGame("front", 12, ("hot.bin", 10));
        string root = game.OriginRoot;
        TraceEvent Warm(string file, long tMs, bool overflow = false)
        {
            string path = Path.Combine(root, file);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(2 * Mib);
            return new TraceEvent(path, 0, 2 * Mib, TMs: tMs, Overflow: overflow);
        }

        PlanOutput.SavePrefetch(game.Trace, [new TraceEvent(Path.Combine(root, "hot.bin"), 0, 10 * Mib)],
            [Warm("late.bin", 500_000), Warm("early.bin", 100_000), Warm("extra.bin", 1_000, overflow: true)]); // 로딩 경계가 없으면 120 s 전이 앞부분
        game = game with { MaxStreamMib = 12, MaxStreamSet = true };

        List<ReadOp> ops = PrefetchStreamer.PrewarmOps(game, 0).Ops;

        Assert.Equal(12 * Mib, ops.Sum(o => o.Length));
        Assert.EndsWith("early.bin", ops[0].Path); // 앞부분 가변 범위가 스케줄러의 접근 순서 앞부분 맨 앞
        Assert.DoesNotContain(ops, o => o.Path.EndsWith("late.bin", StringComparison.Ordinal) || o.Path.EndsWith("extra.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLoadBoundaryLookupWithoutParsingOnlyUsesWhatIsCached()
    {
        GameSettings game = ScheduledGame("cached", 12, ("hot.bin", 10));
        PlanOutput.SavePrefetch(game.Trace, [new TraceEvent(Path.Combine(game.OriginRoot, "hot.bin"), 0, Mib)]);
        string raw = AutoTracer.RawTracePath(game);
        File.WriteAllText(raw, PyJson.Dumps(new[] { new TraceEvent(Path.Combine(game.OriginRoot, "hot.bin"), 0, Mib, TMs: 0) }.Select(e => e.ToJson()).ToList(), ensureAscii: false));

        Assert.Null(LoadTiers.For(game, default, allowParse: false)); // 캐시가 비어 있으면 파싱하지 않음
        Assert.NotNull(LoadTiers.For(game, default));
        Assert.NotNull(LoadTiers.For(game, default, allowParse: false));
    }
}
