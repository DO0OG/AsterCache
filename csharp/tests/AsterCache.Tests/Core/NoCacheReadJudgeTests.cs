using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// 캐시 없는 읽기가 있었던 파일 판정 (붙들기가 뺌): 한 번이라도 있으면 판정하고, 페이징 읽기만이면 판정하지 않으며, 읽기 방식을 기록하지 않은 옛 집계는 모름이라 쓰지 않고, 저장 파일 왕복을 시험합니다.
public sealed class NoCacheReadJudgeTests : IDisposable
{
    private const string A = @"D:\Games\a.bin";
    private const string B = @"D:\Games\b.bin";
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_nocache_" + Guid.NewGuid().ToString("N"));

    public NoCacheReadJudgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static IoSummary Play(int day, params IoSummaryReadMode[] modes) =>
        new(Started.AddDays(day), Started.AddDays(day).AddMinutes(30), [],
            new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0, ReadModesObservedUntilUtc: Started.AddDays(day).AddMinutes(30)),
            [], [], Guid.NewGuid().ToString("N"), ReadModes: modes);

    [Fact]
    public void AFileWhoseNoCacheReadsOutnumberItsCacheAndPagingReadsOverThePlaysIsInTheListAndOneWithMoreCacheReadsIsNot()
    {
        var mostly = new IoSummaryReadMode(A, 2, 2 << 20, 3, 3 << 20, 0, 0); // 플레이 하나에서는 캐시 경유가 더 많지만
        var mostlyLater = new IoSummaryReadMode(A, 0, 0, 5, 5 << 20, 1, 1 << 20); // 합치면 캐시 없음 8 > 캐시 2 + 페이징 1
        var oneNoCache = new IoSummaryReadMode(B, 9, 9 << 20, 1, 1 << 20, 0, 0); // 한 건이라도 있어도 캐시 경유가 더 많으면 뺌: 이 PC 기록에서 한 건이라도 기준은 순위 목록의 93% 를 덮음

        NoCacheReadFileList list = NoCacheReadJudge.Collect([Play(0, mostly, oneNoCache), Play(1, mostlyLater)]);

        Assert.Equal([A], list.Files);
        Assert.Equal(2, list.Plays);
    }

    [Fact]
    public void PagingReadsAreNotCountedAsNoCacheReadsSoAFileWithOnlyPagingReadsIsNotInTheList()
    {
        var paging = new IoSummaryReadMode(A, 0, 0, 0, 0, 7, 7 << 20); // 페이징 요청에도 캐시 없음 표시가 서지만 읽기 방식 집계는 페이징을 따로 셈
        var pagingAndOne = new IoSummaryReadMode(B, 0, 0, 3, 3 << 20, 7, 7 << 20); // 페이징이 더 많으면 캐시 없는 읽기가 더 많은 것이 아님

        NoCacheReadFileList list = NoCacheReadJudge.Collect([Play(0, paging, pagingAndOne)]);

        Assert.Empty(list.Files);
        Assert.Equal(1, list.Plays);
    }

    [Fact]
    public void SummariesWithoutReadModesAreUnknownSoAGameWithOnlyThoseHasAnEmptyListAndSameSessionsCountOnce()
    {
        var oldFormat = new IoSummary(Started, Started, [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0), [], []);
        IoSummary play = Play(0, new IoSummaryReadMode(A, 0, 0, 3, 3 << 20, 0, 0));

        Assert.Empty(NoCacheReadJudge.Collect([oldFormat, oldFormat]).Files);
        Assert.Equal(0, NoCacheReadJudge.Collect([oldFormat]).Plays);
        Assert.Equal(1, NoCacheReadJudge.Collect([play, play]).Plays); // 같은 세션은 한 번
    }

    [Fact]
    public void TheListSurvivesAWriteAndReadAndBadOrMissingFilesReadAsNone()
    {
        string path = Path.Combine(_dir, "g.trace.json.nocache-read.json");
        NoCacheReadFileStore.Write(path, new NoCacheReadFileList(new HashSet<string> { A, B }, 3), Started);

        NoCacheReadFileList back = Assert.IsType<NoCacheReadFileList>(NoCacheReadFileStore.Read(path));

        Assert.Equal(3, back.Plays);
        Assert.Equal([A, B], back.Files.Order());
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Null(NoCacheReadFileStore.Read(Path.Combine(_dir, "none.json")));
        File.WriteAllText(path, "not json");
        Assert.Null(NoCacheReadFileStore.Read(path));
        File.WriteAllText(path, """{"version": 1, "plays": 1, "files": [5]}""");
        Assert.Null(NoCacheReadFileStore.Read(path));
    }
}
