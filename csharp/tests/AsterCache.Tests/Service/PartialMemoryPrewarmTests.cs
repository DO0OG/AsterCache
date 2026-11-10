using AsterCache.Core;
using AsterCache.Core.Json;
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

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add((game.MaxStreamMib, maxBytes));
            long total = (long)game.MaxStreamMib * Mib;
            progress?.Invoke(0, total);
            if (OnProgress is not null && progress is not null)
            {
                OnProgress(progress);
            }

            return (new StreamStats(ReadMib is { } read ? read * Mib : total, 1, 0, 0.01), !stop.IsCancellationRequested);
        }
    }

    private Prewarmer Make(GameSettings game) => new([game], _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => false,
        ZoneBytes = g => Math.Min(_hotMib, g.StreamCapBytes(_hotMib * Mib) / Mib) * Mib, // 계획의 존 (핫 집합) 을 max_stream_mib 로 자른 값
        TraceExists = _ => true,
        PhysicalRamBytes = () => _physical,
        ProbeReadMs = g =>
        {
            Probed.Add(g.MaxStreamMib);
            return new ProbeResult(8, 8, 5.0); // 읽은 구간이 밀려나 차가움
        },
    }, _log, StatusPath, () => _now);

    private static GameSettings Game() => Games.Make("g", "g.exe");

    private JObject Status() => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["g"]!;

    [Theory]
    [InlineData(6000L, 1904)] // 가용 6000 - 예비 4096
    [InlineData(4096L + 256, 256)] // 경계: 256 MiB 면 읽음
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
        _avail = 4096 + 255;
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
        Prewarmer p = Make(Game());
        _streamer.OnProgress = progress =>
        {
            _avail = 4096; // 읽는 중에 메모리 압박
            progress(64 * Mib, 1904 * Mib);
        };

        PrewarmResult r = p.Run(Game(), "test");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void RewarmChecksSampleOnlyThePartReadIntoMemory()
    {
        _avail = 6000;
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
        _avail = 6000;
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
        _avail = 4608; // 시작 상한 1024 도 가용 4608 - 예비 4096 = 512 보다 큼
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
        _avail = 6000;
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
        _avail = 6000;
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

        List<TraceEvent> partial = PrefetchStreamer.SelectToBudget(rows, 150);
        List<TraceEvent> full = PrefetchStreamer.SelectToBudget(rows, 1000);

        Assert.Equal([("observed-a", 100L), ("observed-b", 50L)], partial.Select(e => (e.Path, e.Length)));
        Assert.All(partial, e => Assert.True(e.Observed));
        Assert.Equal(rows, full); // 전체가 들어가면 선택 집합과 순서는 기존 목록 그대로
    }
}
