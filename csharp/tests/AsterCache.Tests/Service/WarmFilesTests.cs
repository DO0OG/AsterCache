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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aster_wf_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _reads = [];
    private readonly HashSet<string> _cached = new(StringComparer.OrdinalIgnoreCase);
    private long _avail = 64000;
    private Action? _onRead;

    public WarmFilesTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Other);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Game => Path.Combine(_root, "game");

    private string Other => Path.Combine(_root, "other");

    private GameSettings Settings() => Games.Make("g", "g.exe") with { OriginRoot = Game, AllowedRoots = [_root] };

    private Prewarmer Make()
    {
        return new Prewarmer([Settings()], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ProbeSamples = s => new ProbeResult(s.Count, s.All(x => _cached.Contains(x.Path)) ? 0 : s.Count, 0.1),
            FileRead = (ops, ct) =>
            {
                _reads.Add(ops);
                _onRead?.Invoke();
                return (new StreamStats(ops.Sum(o => o.Length), ops.Count, 0, 0.01), !ct.IsCancellationRequested);
            },
        }, _log);
    }

    private string File(string dir, string name, long mib)
    {
        string path = Path.Combine(dir, name);
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(mib * Mib);
        return path;
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
