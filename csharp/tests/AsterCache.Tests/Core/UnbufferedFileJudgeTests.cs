using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// 캐시 없이만 읽힌 파일 판정: 플레이 두 번 이상, 캐시 읽기 0, 파일이 바뀌지 않았을 때만 판정하는 규칙과 저장 파일 왕복을 시험합니다.
public sealed class UnbufferedFileJudgeTests : IDisposable
{
    private const string A = @"D:\Games\a.bin";
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly LoadRefillFileStat Stat = new(1000, Started.AddDays(-1).UtcTicks); // 모든 플레이보다 먼저 쓰인 파일

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_unbuf_" + Guid.NewGuid().ToString("N"));

    public UnbufferedFileJudgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static IoSummary Play(int day, params IoSummaryReadMode[] modes) =>
        new(Started.AddDays(day), Started.AddDays(day).AddMinutes(30), [],
            new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0, ReadModesObservedUntilUtc: Started.AddDays(day).AddMinutes(30)),
            [], [], Guid.NewGuid().ToString("N"), ReadModes: modes);

    private static IoSummaryReadMode NoCache(string path = A) => new(path, 0, 0, 3, 3 << 20, 0, 0);

    private static LoadRefillFileStat? Existing(string path) => path == A ? Stat : null;

    [Fact]
    public void AFileReadOnlyWithoutTheCacheInTwoPlaysIsJudgedAndOnePlayIsNotEnough()
    {
        var noModes = new IoSummary(Started, Started, [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0), [], []);
        UnbufferedFileList two = UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, NoCache())], Existing);
        UnbufferedFileList one = UnbufferedFileJudge.Collect([Play(0, NoCache())], Existing);
        UnbufferedFileList withoutModes = UnbufferedFileJudge.Collect([Play(0, NoCache()), noModes], Existing);

        Assert.Equal([A], two.Files.Keys);
        Assert.Equal((2, 1000L), (two.Plays, two.Bytes));
        Assert.Empty(one.Files);
        Assert.Empty(withoutModes.Files); // 읽기 방식을 기록하지 않은 집계는 증거가 아님
    }

    [Fact]
    public void ASessionSeenTwiceCountsOnceAndAnyCachedOrPagingReadInAnyPlayRulesTheFileOut()
    {
        IoSummary play = Play(0, NoCache());
        Assert.Empty(UnbufferedFileJudge.Collect([play, play], Existing).Files);

        Assert.Empty(UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, NoCache()), Play(2, new IoSummaryReadMode(A, 1, 4096, 0, 0, 0, 0))], Existing).Files);
        Assert.Empty(UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, NoCache()), Play(2, new IoSummaryReadMode(A, 0, 0, 2, 2 << 20, 1, 4096))], Existing).Files);
        Assert.Empty(UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, new IoSummaryReadMode(A, 0, 0, 1, 1 << 20, 1, 4096))], Existing).Files);
    }

    [Fact]
    public void AFileThatIsMissingOrWrittenAfterThePlaysWereObservedIsNotJudged()
    {
        IoSummary[] plays = [Play(0, NoCache()), Play(1, NoCache())];

        Assert.Empty(UnbufferedFileJudge.Collect(plays, _ => null).Files);
        Assert.Empty(UnbufferedFileJudge.Collect(plays, _ => Stat with { MTimeTicks = Started.AddDays(1).AddHours(1).UtcTicks }).Files); // 마지막 관찰 시작 뒤에 다시 쓰임 (업데이트)
        Assert.Empty(UnbufferedFileJudge.Collect(plays, _ => Stat with { Size = 0 }).Files);
    }

    [Fact]
    public void StillValidDropsFilesWhoseSizeOrWriteTimeChangedSinceTheJudgment()
    {
        UnbufferedFileList list = UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, NoCache())], Existing);

        Assert.Equal([A], list.StillValid(Existing));
        Assert.Empty(list.StillValid(_ => Stat with { Size = 1001 }));
        Assert.Empty(list.StillValid(_ => Stat with { MTimeTicks = Stat.MTimeTicks + 1 }));
        Assert.Empty(list.StillValid(_ => null));
    }

    [Fact]
    public void StoreRoundTripsAndRejectsABadFile()
    {
        UnbufferedFileList list = UnbufferedFileJudge.Collect([Play(0, NoCache()), Play(1, NoCache())], Existing);
        string path = UnbufferedFileStore.PathFor(Path.Combine(_dir, "g.trace.json"));

        Assert.Null(UnbufferedFileStore.Read(path));
        UnbufferedFileStore.Write(path, list, Started);
        UnbufferedFileList? back = UnbufferedFileStore.Read(path);
        Assert.NotNull(back);
        Assert.Equal(list.Files, back.Files);
        Assert.Equal(list.Plays, back.Plays);

        File.WriteAllText(path, "{\"version\": 9, \"plays\": 1, \"files\": []}");
        Assert.Null(UnbufferedFileStore.Read(path));
    }
}
