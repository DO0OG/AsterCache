using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// Prewarmer.WarmFiles: 허용 루트, 예산, 이미 캐시된 파일, 재배치 배타, 취소. 읽기는 가짜 (FileWarm.Read 만 진짜 파일로 따로 시험).
public sealed class WarmFilesTests : IDisposable
{
    private const long Mib = 1L << 20;
    private bool _completeEvenIfCancelled; // 마지막 묶음을 다 읽고 나서 취소된 경우를 흉내 (읽기 쪽이 Complete 를 참으로 돌려줌)
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aster_wf_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _reads = [];
    private readonly HashSet<string> _cached = new(StringComparer.OrdinalIgnoreCase);
    private long _avail = 64000;
    private Action? _onRead;
    private Action? _onProbe;
    private DiskActivity.SampleState _sample = DiskActivity.SampleState.Ok; // 디스크 표본 상태 (시험이 읽는 도중 바꿈)
    private int _probes;
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    public WarmFilesTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Other);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Game => Path.Combine(_root, "game");

    private string Other => Path.Combine(_root, "other");

    private GameSettings Settings() => Games.Make("g", "g.exe") with { OriginRoot = Game, AllowedRoots = [_root] };

    private Prewarmer Make(DiskReadCoordinator? coordinator = null)
    {
        // 같은 드라이브의 다른 게임 "h" 도 등록 (시험의 SameDisk 기본은 드라이브 문자 비교)
        return new Prewarmer([Settings(), Games.Make("h", "h.exe") with { OriginRoot = Other, AllowedRoots = [_root] }], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = g => _running.Contains(g.Name),
            DiskSample = _ => (0, _sample),
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ProbeFileSamples = (s, ct) =>
            {
                _probes++;
                _onProbe?.Invoke();
                return ct.IsCancellationRequested ? null : new ProbeResult(s.Count, s.All(x => _cached.Contains(x.Path)) ? 0 : s.Count, 0.1);
            },
            FileRead = (ops, ct) =>
            {
                _reads.Add(ops);
                _onRead?.Invoke();
                return (new StreamStats(ops.Sum(o => o.Length), ops.Count, 0, 0.01), _completeEvenIfCancelled || !ct.IsCancellationRequested);
            },
        }, _log, coordinator: coordinator);
    }

    private string File(string dir, string name, long mib)
    {
        string path = Path.Combine(dir, name);
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(mib * Mib);
        return path;
    }

    [Fact]
    public void WarmFilesDoesNotTakeTheLockWhileATriggerPrewarmIsWaiting()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        IDisposable mark = p.BeginTriggerWait([Games.Make("h", "h.exe")]);

        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, default));

        Assert.Empty(_reads);
        Assert.Contains(_log.Lines, l => l.Contains("warm files skipped (another prewarm is running)", StringComparison.Ordinal));
        mark.Dispose();
        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default));
    }

    [Fact]
    public void OnlyFilesUnderTheOriginRootAreRead()
    {
        Prewarmer p = Make();
        string ok = File(Game, "a.wad", 4);
        string sibling = File(Other, "b.wad", 4);
        string[] bad =
        [
            sibling,
            Path.Combine(Game, "..", "other", "b.wad"), // ".." 로 빠져나감
            Path.Combine(Game, "sub", "..", "a.wad"),   // 루트 안으로 돌아와도 ".." 조각은 거부
            @"Z:\Games\x.bin",                          // 다른 드라이브
            "a.wad",                                    // 상대 경로
            ok + ":stream",                             // 대체 스트림
            Game + "x\\a.wad",                          // 이름만 비슷한 형제 폴더
        ];

        long read = p.WarmFiles(Settings(), [.. bad, ok], 1L << 40, default);

        Assert.Equal(4 * Mib, read);
        ReadOp op = Assert.Single(Assert.Single(_reads));
        Assert.Equal(ok, op.Path, ignoreCase: true);
    }

    [Fact]
    public void NothingIsReadWhenEveryPathIsRejected()
    {
        Prewarmer p = Make();

        long read = p.WarmFiles(Settings(), [File(Other, "b.wad", 4), @"Z:\x.bin"], 1L << 40, default);

        Assert.Equal(0, read);
        Assert.Empty(_reads);
    }

    [Theory]
    [InlineData(@"D:\Games\g\a.wad", @"D:\Games\g", true)]
    [InlineData(@"D:\Games\g\..\h\a.wad", @"D:\Games\g", false)]
    [InlineData(@"D:\Games\g2\a.wad", @"D:\Games\g", false)]
    [InlineData(@"E:\Games\g\a.wad", @"D:\Games\g", false)]
    [InlineData(@"\\?\D:\Games\g\a.wad", @"D:\Games\g", false)]
    [InlineData(@"\\server\share\g\a.wad", @"D:\Games\g", false)]
    [InlineData("", @"D:\Games\g", false)]
    [InlineData(@"\", @"D:\Games\g", false)]
    public void AllowedNormalisesAndChecksTheRoot(string path, string root, bool allowed) =>
        Assert.Equal(allowed, FileWarm.Allowed(root, path, p => p) is not null);

    [Fact]
    public void BudgetCapsTheBytesAndCutsTheLastFile()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 10);
        string b = File(Game, "b.wad", 10);

        long read = p.WarmFiles(Settings(), [a, b], 15 * Mib, default);

        Assert.Equal(15 * Mib, read);
        IReadOnlyList<ReadOp> ops = Assert.Single(_reads);
        Assert.Equal([10 * Mib, 5 * Mib], ops.Select(o => o.Length));
    }

    [Fact]
    public void SharedRamBudgetLimitsTheReadEvenWithABigCallerBudget()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 10);
        _avail = 4096 + 6; // 기본 여유 4096 MiB 를 빼면 6 MiB

        long read = p.WarmFiles(Settings(), [a], 1L << 40, default);

        Assert.Equal(6 * Mib, read);

        _avail = 4096; // 남는 RAM 없음
        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, default));
        Assert.Single(_reads);
    }

    [Fact]
    public void AlreadyCachedFilesAreSkippedAndDoNotUseTheBudget()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 10);
        string b = File(Game, "b.wad", 10);
        _cached.Add(a);

        long read = p.WarmFiles(Settings(), [a, b], 10 * Mib, default);

        Assert.Equal(10 * Mib, read);
        ReadOp op = Assert.Single(Assert.Single(_reads));
        Assert.Equal(b, op.Path, ignoreCase: true);
    }

    [Fact]
    public void DuplicatePathsAndMissingFilesAreIgnored()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);

        long read = p.WarmFiles(Settings(), [a, a.ToUpperInvariant(), Path.Combine(Game, "missing.wad")], 1L << 40, default);

        Assert.Equal(2 * Mib, read);
        Assert.Single(Assert.Single(_reads));
    }

    [Fact]
    public void ReadingMarksTheDiskAndARelocationOnTheSameDiskBlocksIt()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        bool during = false;
        _onRead = () => during = p.StreamingOnDisk("g") && !p.TryBeginRelocation("g");

        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default));
        Assert.True(during);
        Assert.False(p.StreamingOnDisk("g"));

        Assert.True(p.TryBeginRelocation("g")); // 재배치가 시작된 동안에는 읽지 않음
        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, default));
        Assert.Single(_reads);
        p.EndRelocation("g");
        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default));
    }

    [Fact]
    public void NothingIsProbedOrReadWhileARelocationIsRunning()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        Assert.True(p.TryBeginRelocation("g"));

        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, default));

        Assert.Equal(0, _probes); // 표본 읽기도 재배치 배제 안에서만
        Assert.Empty(_reads);
    }

    [Fact]
    public void ProbingRunsInsideTheReadMarkSoARelocationCannotStartDuringIt()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        bool blocked = false;
        _onProbe = () => blocked = p.StreamingOnDisk("g") && !p.TryBeginRelocation("g");

        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default));

        Assert.True(blocked);
    }

    [Fact]
    public void YieldDuringFileCacheSamplingStopsBeforeTheNextSampleAndRead()
    {
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        Prewarmer p = Make(coordinator);
        string a = File(Game, "a.wad", 64);
        _onProbe = () => coordinator.Yield(Game, "game started", "other");

        long read = p.WarmFiles(Settings(), [a], 1L << 40, default);

        Assert.Equal(0, read);
        Assert.Equal(1, _probes);
        Assert.Empty(_reads);
    }

    [Fact]
    public void ACancelledTokenStopsTheProbeAndReadsNothing()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        string b = File(Game, "b.wad", 2);
        using var cts = new CancellationTokenSource();
        _onProbe = cts.Cancel; // 첫 파일 점검 중 취소

        Assert.Equal(0, p.WarmFiles(Settings(), [a, b], 1L << 40, cts.Token));

        Assert.Empty(_reads);
        Assert.False(p.StreamingOnDisk("g"));
    }

    [Fact]
    public void NothingIsReadWhileAnotherGameRunsOnTheSameDrive()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        _running.Add("h");

        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, default));

        Assert.Equal(0, _probes);
        Assert.Empty(_reads);
        Assert.Contains(_log.Lines, l => l.Contains("warm files skipped (a game is running", StringComparison.Ordinal));

        _running.Clear();
        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default));
    }

    [Theory]
    [InlineData(false, "h", 1)] // 읽는 중 같은 디스크의 다른 등록 게임이 알림 없이 뜸: 다음 묶음 앞에서 취소
    [InlineData(true, "h", 1)] // keep_stream_on_game_start 여도 다른 게임은 봄
    [InlineData(true, "g", 2)] // 읽던 게임 자신의 실행만 예외
    [InlineData(false, "g", 1)]
    public void ARunningGameDetectedBetweenBatchesCancelsTheRead(bool keep, string runner, int reads)
    {
        Prewarmer p = Make();
        p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = keep });
        string a = File(Game, "a.wad", 64);
        string b = File(Game, "b.wad", 64);
        _onRead = () => _running.Add(runner); // 첫 묶음을 읽는 동안 뜸 (NoteGameStart 는 부르지 않음)

        long read = p.WarmFiles(Settings(), [a, b], 1L << 40, default);

        Assert.Equal(reads, _reads.Count);
        Assert.Equal(reads * 64 * Mib, read);
        Assert.Equal(reads == 1, _log.Lines.Any(l => l.Contains("warm files cancelled", StringComparison.Ordinal)));
        Assert.False(p.StreamingOnDisk("g"));
    }

    [Theory]
    [InlineData(DiskActivity.SampleState.Ok, 2)]
    [InlineData(DiskActivity.SampleState.Warming, 1)] // 읽는 도중 표본이 없음/낡음으로 바뀌면 다음 묶음은 읽지 않음
    [InlineData(DiskActivity.SampleState.Stale, 1)]
    [InlineData(DiskActivity.SampleState.Broken, 2)] // 고장은 기존 동작 (읽기 허용으로 넓히지도 막지도 않음)
    public void ADiskSampleThatTurnsUnknownBetweenBatchesStopsTheFileRead(DiskActivity.SampleState during, int reads)
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 64);
        string b = File(Game, "b.wad", 64);
        _onRead = () => _sample = during;

        p.WarmFiles(Settings(), [a, b], 1L << 40, default);

        Assert.Equal(reads, _reads.Count);
    }

    [Fact]
    public void AGameStartingDuringOneHugeFileStopsAtTheNextBatchBoundary()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 200);
        _onRead = () => _running.Add("g"); // 첫 64 MiB 를 읽는 동안 뜸

        long read = p.WarmFiles(Settings(), [a], 1L << 40, default);

        Assert.Single(_reads);
        Assert.Equal(64 * Mib, read);
        Assert.Contains(_log.Lines, l => l.Contains("warm files cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelAfterTheLastBatchIsNotRecordedAsDone()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        using var cts = new CancellationTokenSource();
        _completeEvenIfCancelled = true;
        _onRead = cts.Cancel;

        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, cts.Token));
        Assert.Contains(_log.Lines, l => l.Contains("warm files cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("warm files done", StringComparison.Ordinal));
    }

    [Fact]
    public void ASameDriveGameStartingMidReadCancelsTheRead()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        _onRead = () => p.NoteGameStart("h"); // 읽는 중 같은 드라이브의 다른 게임이 뜸

        p.WarmFiles(Settings(), [a], 1L << 40, default);

        Assert.Contains(_log.Lines, l => l.Contains("warm files cancelled", StringComparison.Ordinal));
        Assert.False(p.StreamingOnDisk("g"));
        _onRead = null;
        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, default)); // 다음 읽기는 영향 없음
    }

    [Fact]
    public void CancellationStopsWithoutThrowingAndReportsWhatWasRead()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(0, p.WarmFiles(Settings(), [a], 1L << 40, cts.Token));
        Assert.Empty(_reads);

        using var late = new CancellationTokenSource();
        _onRead = late.Cancel; // 읽는 도중 취소: 예외 없이 끝나고 표시를 내림
        Assert.Equal(2 * Mib, p.WarmFiles(Settings(), [a], 1L << 40, late.Token));
        Assert.False(p.StreamingOnDisk("g"));
        Assert.Contains(_log.Lines, l => l.Contains("warm files cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void CoordinatorYieldStopsWarmFilesBeforeTheNextBatch()
    {
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        Prewarmer p = Make(coordinator);
        string a = File(Game, "a.wad", 40);
        string b = File(Game, "b.wad", 40);
        _onRead = () => coordinator.Yield(Game, "game started");

        long read = p.WarmFiles(Settings(), [a, b], 1L << 40, default);

        Assert.Single(_reads);
        Assert.Equal(64 * Mib, _reads[0].Sum(op => op.Length));
        Assert.True(read <= 64 * Mib);
    }

    [Fact]
    public void OriginRootOutsideTheAllowedRootsReadsNothing()
    {
        Prewarmer p = Make();
        string a = File(Game, "a.wad", 2);
        GameSettings bad = Settings() with { AllowedRoots = [Other] };

        Assert.Equal(0, p.WarmFiles(bad, [a], 1L << 40, default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void LowPriorityReaderReadsTheRealFileAndStopsWhenCancelled()
    {
        string a = File(Game, "a.wad", 3);
        string missing = Path.Combine(Game, "gone.wad");

        (StreamStats stats, bool complete) = FileWarm.Read([new ReadOp(a, 0, 3 * Mib), new ReadOp(a, Mib, Mib), new ReadOp(missing, 0, Mib)], default);

        Assert.True(complete);
        Assert.Equal(4 * Mib, stats.Bytes);
        Assert.Equal(1, stats.Errors); // 없는 파일은 오류 1 로 건너뜀

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (StreamStats none, bool done) = FileWarm.Read([new ReadOp(a, 0, 3 * Mib)], cts.Token);
        Assert.False(done);
        Assert.Equal(0, none.Bytes);
    }
}
