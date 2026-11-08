using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 보충 예열이 일반 예열과 같은 부분 예산을 쓰고, 끊겨도 읽은 양을 기억해 이어 읽는가 (식었으면 앞부분부터).
public sealed class SupplementResumeTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_supp_resume_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly ResumeStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private long _avail;
    private ProbeResult? _probe;
    private ProbeResult? _doneProbe; // 이어 읽기 전 끊긴 구간 표본의 결과 (null = 확인 못 함 → 처음부터)
    private int _doneProbeCalls;
    private const long HotMib = 8000;

    public SupplementResumeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class ResumeStreamer : IGameStreamer
    {
        public List<(int MaxStreamMib, long MaxBytes)> Calls { get; } = [];

        /// 호출마다 받은 건너뛸 바이트.
        public List<long> Skips { get; } = [];

        /// 이번에 실제로 읽었다고 알릴 양 (MiB), 없으면 받은 상한 전부.
        public long? ReadMib { get; set; }

        /// 거짓이면 읽다가 끊긴 것으로 돌려줌.
        public bool Complete { get; set; } = true;

        /// 읽을 목록의 길이 (MiB, 건너뛰기 전), 없으면 받은 MaxStreamMib.
        public long? ScheduleMib { get; set; }

        /// 읽기 통계에 담을 오류 수.
        public long ErrorCount { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            return StreamPrewarm(game, stop, progress, maxBytes, 0);
        }

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes)
        {
            Calls.Add((game.MaxStreamMib, maxBytes));
            Skips.Add(skipBytes);
            long total = Math.Max(0, (ScheduleMib ?? game.MaxStreamMib) * Mib - skipBytes);
            progress?.Invoke(0, total);
            return (new StreamStats((ReadMib ?? game.MaxStreamMib) * Mib, 1, ErrorCount, 0.01), Complete && !stop.IsCancellationRequested);
        }
    }

    private Prewarmer Make(GameSettings game) => new([game], _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => false,
        ZoneBytes = g => Math.Min(HotMib, g.StreamCapBytes(HotMib * Mib) / Mib) * Mib,
        TraceExists = _ => true,
        ProbeReadMs = _ => _probe,
        ProbeDone = (_, _) =>
        {
            _doneProbeCalls++;
            return _doneProbe;
        },
    }, _log, StatusPath, () => _now);

    private static GameSettings Game() => Games.Make("g", "g.exe");

    private JObject Status() => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["g"]!;

    // 가용 6000 MiB 에서 부분 예열 (1904 MiB) 을 한 상태로 만든 뒤 가용 RAM 을 바꿈.
    private Prewarmer PartiallyWarmed(GameSettings g, long laterAvail)
    {
        _avail = 6000;
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "service start").Ran);
        Assert.Equal((1904L, HotMib), p.PartialInfo("g"));
        _streamer.Calls.Clear();
        _streamer.Skips.Clear();
        _avail = laterAvail;
        return p;
    }

    [Fact]
    public void ASupplementWhoseHotSetDoesNotFitReadsTheFrontThatFitsInsteadOfWaiting()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 핫 집합 8000 은 전체가 안 들어감: 가용 8000 - 예비 4096 = 3904

        Assert.True(p.Supplement(g, CancellationToken.None));

        (int maxMib, long maxBytes) = Assert.Single(_streamer.Calls);
        Assert.Equal((3904, 0L), (maxMib, maxBytes)); // 앞쪽부터 들어가는 만큼만, 틈 메우기 없음
        Assert.True(p.SupplementPending("g")); // 나머지는 남음
        Assert.Equal((3904L, HotMib), p.PartialInfo("g"));
        JObject s = Status();
        Assert.Equal((3904L, HotMib), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!)); // 상태 표시가 진행을 반영
    }

    [Fact]
    public void ASupplementWhoseBudgetIsBelowTheMinimumPartialSizeIsSkipped()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 4096 + 255);

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm skipped (hot set", StringComparison.Ordinal));
    }

    [Fact]
    public void AYieldedDiskDoesNotIssueTheResumeSampleOfASupplement()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        (_streamer.ReadMib, _streamer.Complete) = (2500, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 끊긴 위치를 기억함
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        p.SetDiskReadCoordinator(coordinator);
        coordinator.Yield(@"C:\Games\other", "game started", "other");
        (_streamer.ReadMib, _streamer.Complete) = (null, true);
        _streamer.Calls.Clear();

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Equal(0, _doneProbeCalls); // 같은 디스크에서 다른 게임이 도는 동안은 표본도 읽지 않음
        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void ADeniedResumeSampleKeepsTheResumePositionForTheNextSupplement()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2500, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 끊긴 위치를 기억함
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        p.SetDiskReadCoordinator(coordinator);
        coordinator.Yield(@"C:\Games\other", "game started", "other");
        (_streamer.ReadMib, _streamer.Complete) = (null, true);
        _streamer.Calls.Clear();
        _streamer.Skips.Clear();

        Assert.False(p.Supplement(g, CancellationToken.None)); // 표본이 거절됨: 기억을 버리지 않고 조용히 멈춤
        coordinator.Resume(@"C:\Games\other", "other");
        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([2500 * Mib], _streamer.Skips); // 처음부터가 아니라 끊긴 위치에서 이어 읽음
    }

    [Fact]
    public void ADeniedFrontSampleStopsQuietlyInsteadOfReportingNothingNew()
    {
        GameSettings g = Game();
        _avail = 6000;
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1000 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 8000;
        Assert.True(p.Supplement(g, CancellationToken.None));
        _streamer.Calls.Clear();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        p.SetDiskReadCoordinator(coordinator);
        coordinator.Yield(@"C:\Games\other", "game started", "other");

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.DoesNotContain(_log.Lines, l => l.Contains("supplement prewarm skipped (the", StringComparison.Ordinal)); // 읽지 못했을 뿐 새로 읽을 것이 없다는 뜻이 아님
        coordinator.Resume(@"C:\Games\other", "other");
        _probe = new ProbeResult(8, 8, 5.0); // 읽은 앞부분이 식음
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3904, Assert.Single(_streamer.Calls).MaxStreamMib);
    }

    [Fact]
    public void ACancelledSupplementKeepsWhatItReadAndTheNextOneContinuesFromThere()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        (_streamer.ReadMib, _streamer.Complete) = (2500, false); // 3904 중 2500 읽고 끊김

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Equal((2500L, HotMib), p.PartialInfo("g")); // 처음(1904)이 아니라 읽은 만큼 앞당겨짐
        Assert.Equal(2500L, (long)Status()["warmed_mib"]!);
        (_streamer.ReadMib, _streamer.Complete) = (null, true);
        _streamer.Calls.Clear();

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(3904, Assert.Single(_streamer.Calls).MaxStreamMib); // 같은 예산의 앞부분을 이어서 채움 (읽은 앞부분은 캐시에 있어 다시 읽지 않음)
        Assert.Contains(_log.Lines, l => l.Contains("(2500 of 8000 MiB read, reading the first 3904 MiB)", StringComparison.Ordinal));
        Assert.Equal((3904L, HotMib), p.PartialInfo("g"));
    }

    [Fact]
    public void RepeatedCancelsMakeProgressEachNextRequestStartsWhereTheLastOneStopped()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 창 3904 MiB
        _doneProbe = new ProbeResult(8, 0, 0.3); // 읽은 앞부분은 따뜻함
        _streamer.Complete = false;

        _streamer.ReadMib = 2000;
        Assert.False(p.Supplement(g, CancellationToken.None)); // 취소 1
        _streamer.ReadMib = 500;
        Assert.False(p.Supplement(g, CancellationToken.None)); // 취소 2
        Assert.Equal((2500L, HotMib), p.PartialInfo("g")); // 건너뛴 2000 + 새로 읽은 500
        (_streamer.ReadMib, _streamer.Complete) = (1404, true);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 세 번째

        Assert.Equal([0L, 2000 * Mib, 2500 * Mib], _streamer.Skips); // 시작 위치가 앞선 진행량
        Assert.All(_streamer.Calls, c => Assert.Equal(3904, c.MaxStreamMib)); // 같은 창을 끝냄
        Assert.Equal((3904L, HotMib), p.PartialInfo("g"));
    }

    [Fact]
    public void ACancelledWindowIsReadFromTheStartWhenItsReadFrontWentColdOrCannotBeChecked()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        _doneProbe = new ProbeResult(8, 8, 5.0); // 식음
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.False(p.Supplement(g, CancellationToken.None));

        _doneProbe = null; // 확인할 수 없음
        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 0L, 0L], _streamer.Skips); // 매번 처음부터 (진행을 거짓으로 세지 않음)
    }

    [Fact]
    public void AWindowThatNoLongerFitsTheBudgetIsNotContinued()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 창 3904
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None));
        _avail = 4096 + 3000; // 예산이 3000 으로 줄어 옛 창을 끝낼 수 없음
        (_streamer.ReadMib, _streamer.Complete) = (null, true);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 0L], _streamer.Skips);
        Assert.Equal(3000, _streamer.Calls[^1].MaxStreamMib);
    }

    [Fact]
    public void ACancelledFullHotSetReadContinuesWithoutGapFillingAndEndsLikeANormalSupplement()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 20000); // 핫 집합 8000 이 통째로 들어감
        (_doneProbe, _streamer.ScheduleMib) = (new ProbeResult(8, 0, 0.3), HotMib);
        (_streamer.ReadMib, _streamer.Complete) = (3000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 3000 읽고 끊김
        (_streamer.ReadMib, _streamer.Complete) = (5000, true);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 3000 * Mib], _streamer.Skips); // 끊긴 위치에서 이어 읽음
        Assert.Equal((HotMib, 0L), ((long)_streamer.Calls[1].MaxStreamMib, _streamer.Calls[1].MaxBytes)); // 이어 읽을 때는 틈 메우기 없이 전체 크기의 같은 스케줄
        Assert.False(p.SupplementPending("g")); // 끝나면 평소처럼 부분 상태를 지움
    }

    [Fact]
    public void AFullHotSetCancelledAfterTheWholeListWasReadIsFinishedInsteadOfKeptForAnEmptyTail()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 20000);
        (_doneProbe, _streamer.ScheduleMib) = (new ProbeResult(8, 0, 0.3), HotMib);
        (_streamer.ReadMib, _streamer.Complete) = (HotMib, false); // 목록 끝까지 읽은 뒤 (갱신 읽기 도중) 취소

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void ACancelAfterTheWholeWindowWasReadFinishesTheWindowSoALargerBudgetStartsAFreshOne()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 창 3904
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (3904, false); // 창 끝까지 읽은 뒤 취소
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Equal((3904L, HotMib), p.PartialInfo("g"));
        _avail = 4096 + 5000; // 예산이 커짐
        (_streamer.ReadMib, _streamer.Complete) = (null, true);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 0L], _streamer.Skips); // 옛 창의 빈 꼬리를 이어 읽지 않음
        Assert.Equal(5000, _streamer.Calls[^1].MaxStreamMib);
    }

    [Fact]
    public void AResumedWindowWithNothingLeftToReadIsFinishedAndTheNextOneStartsFresh()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 위치 2000 저장
        (_streamer.ReadMib, _streamer.Complete) = (0, true); // 남은 구간이 없어 0 바이트

        Assert.True(p.Supplement(g, CancellationToken.None)); // 정체되지 않고 창 완료로 정리
        _streamer.ReadMib = null;
        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 2000 * Mib, 0L], _streamer.Skips); // 위치 기억은 지워짐
    }

    [Fact]
    public void AReadWithErrorsDoesNotKeepAResumePosition()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3); // 앞부분이 따뜻해도
        (_streamer.ReadMib, _streamer.Complete, _streamer.ErrorCount) = (2000, false, 2);
        Assert.False(p.Supplement(g, CancellationToken.None));
        (_streamer.ErrorCount, _streamer.ReadMib, _streamer.Complete) = (0, null, true);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 0L], _streamer.Skips); // 연속 완료를 보장 못 하므로 처음부터
    }

    [Fact]
    public void SetGamesDropsTheResumePositionOfARemovedOrChangedGameButKeepsAnUnchangedOne()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 위치 2000

        p.SetGames([g]); // 같은 설정
        _streamer.ReadMib = 500;
        Assert.False(p.Supplement(g, CancellationToken.None)); // 2000 에서 이어 읽고 위치 2500

        GameSettings changed = g with { MaxStreamMib = 2048 };
        p.SetGames([changed]); // 설정이 바뀜
        Assert.False(p.Supplement(changed, CancellationToken.None)); // 처음부터, 위치 500

        p.SetGames([]); // 게임이 빠짐
        p.SetGames([changed]);
        Assert.False(p.Supplement(changed, CancellationToken.None));

        Assert.Equal([0L, 2000 * Mib, 0L, 0L], _streamer.Skips);
    }

    [Fact]
    public void ANormalPrewarmThatReplacesThePartialStateClearsTheResumePosition()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 위치 2000
        (_streamer.ReadMib, _streamer.Complete) = (null, true);
        Assert.True(p.Run(g, "test", ignoreCooldown: true).Ran); // 일반 예열이 부분 상태를 새로 정함
        _avail = 4096 + 5000; // 다음 보충이 읽을 구간이 생김
        (_streamer.ReadMib, _streamer.Complete) = (500, false);
        _streamer.Skips.Clear();

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L], _streamer.Skips); // 옛 위치 (2000) 를 쓰지 않음
    }

    [Fact]
    public void TailDropsTheLeadingBytesAndCutsTheOpThatStraddlesThem()
    {
        List<ReadOp> ops = [new("a", 0, 100), new("b", 10, 50)];

        Assert.Equal(ops, PrefetchStreamer.Tail(ops, 0));
        Assert.Equal([new ReadOp("a", 30, 70), new ReadOp("b", 10, 50)], PrefetchStreamer.Tail(ops, 30));
        Assert.Equal([new ReadOp("b", 30, 30)], PrefetchStreamer.Tail(ops, 120));
        Assert.Empty(PrefetchStreamer.Tail(ops, 150));
    }

    [Fact]
    public void WithNothingNewToReadItWaitsAndTheMemoryPartialsColdFrontIsLeftToTheRewarmCheck()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 3904 까지 읽음
        _streamer.Calls.Clear();
        _probe = new ProbeResult(8, 8, 5.0); // 식었어도

        Assert.False(p.Supplement(g, CancellationToken.None)); // 더 들어갈 구간이 없음

        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void AReadFrontThatWentColdIsReadAgainFromTheFrontEvenWithNothingNew()
    {
        GameSettings g = Game();
        _avail = 6000;
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1000 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran); // 시작 상한으로 1000 MiB 만 읽음
        _avail = 8000;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 3904 까지 읽음
        _streamer.Calls.Clear();

        Assert.False(p.Supplement(g, CancellationToken.None)); // 새로 들어갈 구간이 없고 앞부분은 따뜻함
        Assert.Empty(_streamer.Calls);

        _probe = new ProbeResult(8, 8, 5.0); // 읽은 앞부분이 식음
        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(3904, Assert.Single(_streamer.Calls).MaxStreamMib); // 앞부분부터 다시
    }

    [Fact]
    public void AHotSetThatFitsIsStillReadWholeAndClearsThePartialState()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 20000);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(g.MaxStreamMib, Assert.Single(_streamer.Calls).MaxStreamMib); // 줄이지 않은 설정 그대로
        Assert.False(p.SupplementPending("g"));
    }
}
