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

        /// PVM 힌트가 받아들였다고 알릴 바이트 수. MiB 경계 시험에서는 ReadMib 보다 우선합니다.
        public long? ReadBytes { get; set; }

        /// 계획 뒤 다시 계산하는 최종 읽기 목록 크기.
        public long? ReadSetMib { get; set; }

        /// 읽을 목록의 요청량 (MiB), 없으면 실제 목록 길이.
        public long? RequestedMib { get; set; }

        /// 최종 읽기 목록을 다시 계산하는 순간에 일어나는 일 (가용 메모리 변동 등).
        public Action? OnReadSet { get; set; }

        public long? PrewarmReadBytes(GameSettings game, bool partial)
        {
            OnReadSet?.Invoke();
            return ReadSetMib is { } mib ? mib * Mib : null;
        }

        /// 읽는 도중에 일어나는 일 (게임 시작 등).
        public Action? OnStream { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            return StreamPrewarm(game, stop, progress, maxBytes, 0);
        }

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes)
        {
            Calls.Add((game.MaxStreamMib, maxBytes));
            Skips.Add(skipBytes);
            OnStream?.Invoke();
            long total = Math.Max(0, (ScheduleMib ?? RequestedMib ?? game.MaxStreamMib) * Mib - skipBytes);
            progress?.Invoke(0, total);
            long requested = Math.Max(0, (RequestedMib ?? ScheduleMib ?? game.MaxStreamMib) * Mib - skipBytes);
            return (new StreamStats(ReadBytes ?? (ReadMib ?? game.MaxStreamMib) * Mib, 1, ErrorCount, 0.01, RequestedBytes: requested), Complete && !stop.IsCancellationRequested);
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

    // 가용 6000 MiB 에서 부분 예열 (1904 MiB) 을 한 상태로 만든 뒤 가용 RAM 을 바꿈. 창을 잡을 때 두는 여유 (Prewarmer.FinalShrinkHeadroomMib) 는 가용에 미리 더해 창 크기가 가용 - 예비가 되게 함.
    private Prewarmer PartiallyWarmed(GameSettings g, long laterAvail)
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib;
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "service start").Ran);
        Assert.Equal((1904L, HotMib), p.PartialInfo("g"));
        _streamer.Calls.Clear();
        _streamer.Skips.Clear();
        _avail = laterAvail + Prewarmer.FinalShrinkHeadroomMib;
        return p;
    }

    private Prewarmer PartiallyWarmedAtServiceCap(GameSettings g, long laterAvail)
    {
        _avail = 6000 + Prewarmer.FinalShrinkHeadroomMib;
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024, RewarmMinutes = 5 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _streamer.Calls.Clear();
        _streamer.Skips.Clear();
        _avail = laterAvail + Prewarmer.FinalShrinkHeadroomMib;
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
    public void FinalSupplementPlanShrinksWithHeadroomAndDropsAnOldWindowOffset()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 창 3904, 이어 읽기 위치 2000

        _streamer.ReadMib = null;
        _streamer.Complete = true;
        _streamer.ReadSetMib = 4100; // 다시 계산한 목록이 최초 예산보다 큼
        _streamer.OnReadSet = () => _avail = 8000; // 계획 뒤 가용이 줄어 최종 재산정이 창을 3648 로 줄임

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(3648, _streamer.Calls[^1].MaxStreamMib); // 8000 - 4096 예비 - 256 최종 여유
        Assert.Equal(0, _streamer.Skips[^1]); // 창이 바뀌어 이전 목록의 위치를 재사용하지 않음
    }

    [Fact]
    public void AFinalLowMemorySkipDefersTheRetryInsteadOfRepeatingTheSampleReadEveryIdle()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 이어 읽기 위치 2000 MiB 가 남음
        int calls = _streamer.Calls.Count;
        _streamer.ReadMib = null;
        _streamer.Complete = true;
        _streamer.ReadSetMib = 4100;
        _streamer.OnReadSet = () => _avail = 4096 + 256 + 100; // 표본 읽기 뒤 가용 메모리가 줄어 최종 재산정에서 256 MiB 미만만 들어감
        int before = _doneProbeCalls;

        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Equal(before + 1, _doneProbeCalls); // 첫 건너뜀은 표본을 한 번 읽음
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm deferred (low memory", StringComparison.Ordinal));

        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Equal(before + 1, _doneProbeCalls); // 재시도 기한 전에는 표본을 다시 읽지 않음
        Assert.Equal(calls, _streamer.Calls.Count);
    }

    [Fact]
    public void AFinalCapAtOrBelowWhatIsAlreadyReadReturnsWithoutReReadingTheFront()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000); // 시작 상한으로 1024 MiB 읽은 부분 상태
        _streamer.ReadSetMib = 4100;
        _streamer.OnReadSet = () => _avail = 5200; // 최종 재산정의 상한 848 MiB <= 이미 읽은 1024 MiB

        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm skipped (the 848 MiB that fits is already read", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNothingNewLogIsWrittenOnceWhileOnlyTheFluctuatingCapChanges()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 4096 + 256 + 1500); // 들어가는 양 1500 은 이미 읽은 양 (1648) 이하

        Assert.False(p.Supplement(g, CancellationToken.None));
        _avail = 4096 + 256 + 1400; // 가용이 흔들려 cap 만 바뀜
        Assert.False(p.Supplement(g, CancellationToken.None));
        _avail = 4096 + 256 + 1600;
        Assert.False(p.Supplement(g, CancellationToken.None));

        Assert.Empty(_streamer.Calls);
        Assert.Single(_log.Lines, l => l.Contains("is already read", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePausedPolicyStopsTheSupplementWithoutDroppingThePartialStateAndResumingReadsAgain()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);

        p.SetGames([g with { Paused = true }]);
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Empty(_streamer.Calls);
        Assert.True(p.SupplementPending("g")); // 이력 (부분 상태) 은 보존

        p.SetGames([g]);
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Single(_streamer.Calls);
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
    public void AShortCompletedRequestListRetriesAfterTheConfiguredDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000); // 계획 상한 3904 MiB, 실제 요청 목록은 1305 MiB
        (_streamer.ReadMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1305, 1305, 0);

        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.True(p.SupplementPending("g"));
        Assert.Equal((1305L, HotMib), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!));

        _avail = 8255; // 255 MiB 증가는 조기 재시도 기준보다 작음
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Single(_streamer.Calls);

        _now += TimeSpan.FromMinutes(5);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 입력이 같아도 기한이 지나면 다시 판정
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.True(p.SupplementPending("g"));
    }

    [Fact]
    public void AShorterAcceptedListDoesNotResetTheProgressUsedByItsRetry()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 이전 진전 1904 MiB, 이번 계획 상한 3904 MiB
        (_streamer.ReadMib, _streamer.ScheduleMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1305, 1305, 1305, 0);

        Assert.True(p.Supplement(g, CancellationToken.None)); // 받아들인 양은 이전 진전보다 작음
        Assert.True(p.SupplementPending("g"));
        Assert.False(p.Supplement(g, CancellationToken.None)); // 같은 목록을 바로 다시 읽지 않음
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ACancelledSupplementWithoutProgressIsNotReadAgainBeforeItsRetry()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 이전 진전 1904 MiB
        (_streamer.ReadMib, _streamer.Complete) = (1305, false); // 메모리 재판정으로 이전 진전에 못 미쳐 끊김

        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.True(p.SupplementPending("g"));
        Assert.False(p.Supplement(g, CancellationToken.None)); // 다음 유휴에 같은 앞부분을 다시 읽지 않음
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ASupplementCancelledByAGameStartIsNotHeldByTheNoProgressDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000); // 이전 진전 1904 MiB
        (_streamer.ReadMib, _streamer.Complete) = (1305, false);
        _streamer.OnStream = () => p.NoteGameStart(g.Name); // 보충 도중 게임이 시작돼 끊김
        Assert.False(p.Supplement(g, CancellationToken.None));
        _streamer.OnStream = null;
        (_streamer.ReadMib, _streamer.Complete) = (null, true);

        p.Supplement(g, CancellationToken.None); // 게임이 끝난 뒤의 보충은 재시도 기한에 묶이지 않음

        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void AShortPartialListIsRecheckedWhenMemoryAllowsTheFullPath()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        (_streamer.ReadMib, _streamer.ScheduleMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1305, 1305, 1305, 0);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 부분 경로의 목록은 1305 MiB 에서 끝남

        _avail = 20000;
        (_streamer.ReadBytes, _streamer.ScheduleMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1158 * Mib + 62 * Mib / 100, 2464, 2464, 0);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 기한 전이라도 전체 경로를 한 번 재판정

        Assert.Equal(2, _streamer.Calls.Count);
        Assert.False(p.SupplementPending("g"));
        Assert.Equal((HotMib, HotMib), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!));
    }

    [Fact]
    public void AFluctuatingCapAllowsOnlyOneEarlyRetryBeforeTheDeadline()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        (_streamer.ReadMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1305, 2464, 1);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 1024 -> 1305 MiB 진전

        _avail = 7000; // cap 2904 MiB: 같은 바이트에서 기한 시작
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(2, _streamer.Calls.Count);

        _now += TimeSpan.FromSeconds(30);
        _avail = 7256; // cap 3160 MiB: 조기 재시도 한 번
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);

        _avail = 7512; // cap 이 다시 256 MiB 증가해도 같은 기한에는 반복하지 않음
        Assert.False(p.Supplement(g, CancellationToken.None));
        _avail = 8000; // 큰 폭의 흔들림도 조기 재시도를 추가하지 않음
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);

        _now += TimeSpan.FromMinutes(5);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 원래 기한에 한 번 재시도
        Assert.Equal(4, _streamer.Calls.Count);
    }

    [Fact]
    public void ChangingCapsAndOneMibRoundingDoNotResetTheNoProgressDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        _streamer.ReadBytes = 1305 * Mib + 100;
        _streamer.ErrorCount = 1;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 1024 MiB 에서 진전

        _avail = 7000; // 계획 상한은 3904 에서 2904 MiB 로 달라짐
        _streamer.ReadBytes = 1305 * Mib; // 내림/올림 표시가 1305/1306 MiB 로 달라도 바이트는 늘지 않음
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(2, _streamer.Calls.Count);

        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.Supplement(g, CancellationToken.None));
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm deferred (no progress", StringComparison.Ordinal));

        _now += TimeSpan.FromMinutes(4);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 기한이 지나면 한 번 다시 시도
        Assert.Equal(3, _streamer.Calls.Count);
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm deferred (no progress", StringComparison.Ordinal));
    }

    [Fact]
    public void LargerMemoryCanRetryEarlyAndRealByteProgressClearsTheDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        _streamer.ReadMib = 1305;
        _streamer.ErrorCount = 1;
        Assert.True(p.Supplement(g, CancellationToken.None));

        _avail = 7000;
        Assert.True(p.Supplement(g, CancellationToken.None)); // no progress: cool-down starts
        Assert.Equal(2, _streamer.Calls.Count);

        _avail = 7256; // cap 2904 -> 3160 MiB: MinPartialMib 만큼 늘어남
        _streamer.ReadMib = 1500;
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);

        Assert.True(p.Supplement(g, CancellationToken.None)); // 실제 증가가 기한도 해제함
        Assert.Equal(4, _streamer.Calls.Count);
    }

    [Fact]
    public void ChangedGameSettingsReleaseTheNoProgressDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        _streamer.ReadMib = 1305;
        _streamer.ErrorCount = 1;
        Assert.True(p.Supplement(g, CancellationToken.None));
        _avail = 7000;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 같은 수락량으로 지연 등록

        GameSettings changed = g with { Prewarm = g.Prewarm with { MinFreeRamMib = g.Prewarm.MinFreeRamMib + 1 } };
        p.SetGames([changed]);

        Assert.True(p.Supplement(changed, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);
    }

    [Fact]
    public void RemovingAndReaddingAGameReleasesItsNoProgressDelay()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        _streamer.ReadMib = 1305;
        _streamer.ErrorCount = 1;
        Assert.True(p.Supplement(g, CancellationToken.None));
        _avail = 7000;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 같은 수락량으로 지연 등록
        p.SetGames([]);
        p.SetGames([g]);

        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);
    }

    [Fact]
    public void GameStartReleasesTheNoProgressDelayForRewarmAfterExit()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmedAtServiceCap(g, 8000);
        _streamer.ReadMib = 1305;
        _streamer.ErrorCount = 1;
        Assert.True(p.Supplement(g, CancellationToken.None));
        _avail = 7000;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 같은 수락량으로 지연 등록

        p.NoteGameStart(g.Name);

        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(3, _streamer.Calls.Count);
    }

    [Fact]
    public void RepeatedNothingNewSkipsLogOnlyWhenItsStateChanges()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 6000);

        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm skipped (the", StringComparison.Ordinal));

        _avail = 8000 + Prewarmer.FinalShrinkHeadroomMib;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 보충 진행으로 같은 상태 기억을 지움
        Assert.False(p.Supplement(g, CancellationToken.None)); // 읽은 양이 바뀐 새 상태

        Assert.Equal(2, _log.Lines.Count(line => line.Contains("supplement prewarm skipped (the", StringComparison.Ordinal)));
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
        _avail = 8000 + Prewarmer.FinalShrinkHeadroomMib;
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
        _now += TimeSpan.FromMinutes(10); // 진전 없이 끊긴 두 번째 시도는 재시도 기한까지 다시 읽지 않음
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
        _avail = 4096 + 3000 + Prewarmer.FinalShrinkHeadroomMib; // 예산이 3000 으로 줄어 옛 창을 끝낼 수 없음
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
        _avail = 4096 + 5000 + Prewarmer.FinalShrinkHeadroomMib; // 예산이 커짐
        (_streamer.ReadMib, _streamer.Complete) = (null, true);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal([0L, 0L], _streamer.Skips); // 옛 창의 빈 꼬리를 이어 읽지 않음
        Assert.Equal(5000, _streamer.Calls[^1].MaxStreamMib);
    }

    [Fact]
    public void AResumedWindowWithNoProgressWaitsThenStartsFresh()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 8000);
        p.SetAuto(AutoSettings.Default with { RewarmMinutes = 1 });
        _doneProbe = new ProbeResult(8, 0, 0.3);
        (_streamer.ReadMib, _streamer.Complete) = (2000, false);
        Assert.False(p.Supplement(g, CancellationToken.None)); // 위치 2000 저장
        (_streamer.ReadMib, _streamer.Complete) = (0, true); // 남은 구간이 없어 0 바이트

        Assert.True(p.Supplement(g, CancellationToken.None)); // 수락 바이트가 늘지 않아 재시도 기한을 둠
        Assert.True(p.SupplementPending("g"));
        _now += TimeSpan.FromMinutes(1);
        _streamer.ReadMib = null;
        Assert.True(p.Supplement(g, CancellationToken.None)); // 기한 뒤에는 저장한 이어 읽기 위치를 버리고 처음부터

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
        _avail = 4096 + 5000 + Prewarmer.FinalShrinkHeadroomMib; // 다음 보충이 읽을 구간이 생김
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
        _avail = 8000 + Prewarmer.FinalShrinkHeadroomMib;
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
        (_streamer.ReadMib, _streamer.ScheduleMib, _streamer.RequestedMib) = (HotMib, HotMib, HotMib);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(g.MaxStreamMib, Assert.Single(_streamer.Calls).MaxStreamMib); // 줄이지 않은 설정 그대로
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void AFullReadWithAShortRequestListCompletesAndClearsThePartialState()
    {
        GameSettings g = Game();
        Prewarmer p = PartiallyWarmed(g, 20000);
        (_streamer.ReadBytes, _streamer.ScheduleMib, _streamer.RequestedMib, _streamer.ErrorCount) = (1158 * Mib + 62 * Mib / 100, 2464, 2464, 0);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal(g.MaxStreamMib, Assert.Single(_streamer.Calls).MaxStreamMib);
        Assert.False(p.SupplementPending("g"));
        Assert.Equal((HotMib, HotMib), ((long)Status()["warmed_mib"]!, (long)Status()["hot_mib"]!));
    }
}
