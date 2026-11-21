using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 캐시 없이만 읽히는 파일: 저장된 플레이 집계 두 개 → 목록 파일 → 예열·시작 스트림 목록에서 빠지고 트레이스 행 (재배치 계획의 입력) 에는 남는지 실제 임시 폴더로 시험합니다.
public sealed class UnbufferedFilesTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _root = Directory.CreateTempSubdirectory("aster_unbuffered_").FullName;
    private readonly GameSettings _game;
    private readonly string _bare;
    private readonly string _cached;

    public UnbufferedFilesTests()
    {
        string gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        _game = Games.Make("unbuf", "unbuf.exe") with
        {
            OriginRoot = gameRoot,
            AllowedRoots = [gameRoot],
            Trace = Path.Combine(_root, "unbuf.prefetch.json"),
            Plan = Path.Combine(_root, "unbuf.plan.json"),
        };
        _bare = Path.Combine(gameRoot, "bare.bin");
        _cached = Path.Combine(gameRoot, "cached.bin");
        foreach (string file in new[] { _bare, _cached })
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(4 * Mib);
        }

        _ = TraceWriter.Write([[new TraceEvent(_bare, 0, 4 * Mib), new TraceEvent(_cached, 0, 4 * Mib)]], _game.Trace);
        File.WriteAllText(AutoTracer.RawTracePath(_game), "[]");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void SavePlay(int run, params IoSummaryReadMode[] modes)
    {
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(run); // 파일을 만든 뒤에 관찰이 시작된 것으로
        var summary = new IoSummary(observed, observed.AddMinutes(30), [],
            new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0, ReadModesObservedUntilUtc: observed.AddMinutes(30)),
            [], [], Guid.NewGuid().ToString("N"), ReadModes: modes);
        string runPath = TraceWriter.RunPath(AutoTracer.RawTracePath(_game), run);
        File.WriteAllText(runPath, "[]");
        IoSummaryStore.WriteForRun(runPath, summary);
    }

    private IoSummaryReadMode Bare => new(_bare, 0, 0, 5, 4 * Mib, 0, 0);

    private IoSummaryReadMode Cached => new(_cached, 2, Mib, 0, 0, 1, Mib);

    private static string[] Paths(IEnumerable<ReadOp> ops) => [.. ops.Select(op => op.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void FileReadOnlyWithoutTheCacheInTwoPlaysLeavesTheStreamListButStaysInTheTraceRows()
    {
        SavePlay(1, Bare, Cached);
        SavePlay(2, Bare, Cached);

        string line = UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow);

        Assert.StartsWith("unbuffered file list built from 2 plays: 1 files/4.0 MiB read only without the cache", line, StringComparison.Ordinal);
        Assert.Equal([_cached], Paths(PrefetchStreamer.Ops(_game)));
        Assert.Equal([_cached], Paths(PrefetchStreamer.IncumbentPrewarmOps(_game, 0)));
        Assert.Equal([_bare, _cached], PrefetchStreamer.LoadRows(_game, true).Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)); // 재배치 계획이 보는 트레이스 행은 그대로
    }

    [Fact]
    public void OnePlayOrACachedReadInAnyPlayKeepsTheFileInTheList()
    {
        SavePlay(1, Bare, Cached);
        UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow);
        Assert.Equal([_bare, _cached], Paths(PrefetchStreamer.Ops(_game)));

        SavePlay(2, Bare, Cached);
        SavePlay(3, new IoSummaryReadMode(_bare, 1, Mib, 0, 0, 0, 0));
        UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow);
        Assert.Empty(UnbufferedFileStore.Read(UnbufferedFiles.PathOf(_game))!.Files); // 판정된 파일이 없으면 빈 목록
        Assert.Equal([_bare, _cached], Paths(PrefetchStreamer.Ops(_game)));
    }

    [Fact]
    public void AFileChangedAfterTheJudgmentComesBackIntoTheListAndRebuildJudgesItAgainFromTheNewFile()
    {
        SavePlay(1, Bare, Cached);
        SavePlay(2, Bare, Cached);
        UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow);
        Assert.Equal([_cached], Paths(PrefetchStreamer.Ops(_game)));

        File.SetLastWriteTimeUtc(_bare, DateTime.UtcNow.AddHours(1)); // 업데이트로 다시 쓰임
        Assert.Equal([_bare, _cached], Paths(PrefetchStreamer.Ops(_game)));

        UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow); // 새 파일은 두 플레이 뒤에 쓰였으므로 판정하지 않음
        Assert.Empty(UnbufferedFileStore.Read(UnbufferedFiles.PathOf(_game))!.Files); // 판정된 파일이 없으면 빈 목록
        Assert.Equal([_bare, _cached], Paths(PrefetchStreamer.Ops(_game)));
    }

    [Fact]
    public void RebuildAlsoSavesTheFilesReadMostlyWithoutTheCacheAndTheMissingListMakesItStale()
    {
        Func<string, DateTimeOffset?> time = p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        SavePlay(1, Bare, Cached);
        SavePlay(2, Bare, new IoSummaryReadMode(_cached, 2, Mib, 1, Mib, 0, 0)); // 캐시 읽기가 더 많은 파일 (캐시 경유 4 + 페이징 1 > 캐시 없음 1)

        string line = UnbufferedFiles.Rebuild(_game, DateTimeOffset.UtcNow);

        Assert.Contains("1 files read mostly without the cache", line, StringComparison.Ordinal);
        Assert.Equal([_bare], UnbufferedFiles.NoCacheRead(_game)); // _cached 는 캐시 없는 읽기가 있어도 캐시 경유가 더 많아 목록에 없음
        Assert.False(UnbufferedFiles.IsStale(_game, time));

        File.Delete(UnbufferedFiles.NoCacheReadPathOf(_game));
        Assert.True(UnbufferedFiles.IsStale(_game, time)); // 이 목록이 없던 설치본은 서비스 시작 때 한 번 다시 만듦
    }
}
