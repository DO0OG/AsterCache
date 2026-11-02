using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class LoadBoundaryTests
{
    private const long Mib = 1 << 20;

    // sec 초에 파일 a 의 atMib 위치에서 mib MiB 를 읽음
    private static TraceEvent Read(long sec, long mib, long atMib) => new("a", atMib * Mib, mib * Mib, TMs: sec * 1000);

    [Fact]
    public void WithoutTimesTheBoundaryIsUnknown()
    {
        Assert.Null(LoadBoundaries.Find([]));
        Assert.Null(LoadBoundaries.Find([new TraceEvent("a", 0, 100)]));
        Assert.Null(LoadBoundaries.Find([Read(0, 1, 0), new TraceEvent("a", 0, 100)])); // 하나라도 없으면
    }

    [Fact]
    public void SharpDropEndsLoadingAtTheFirstWindowBelowTenPercentOfThePeak()
    {
        // 0..19 초 매초 10 MiB (고유 200 MiB), 그 뒤 조용하다가 100 초에 5 MiB
        List<TraceEvent> events = [.. Enumerable.Range(0, 20).Select(s => Read(s, 10, s * 10)), Read(100, 5, 500)];

        LoadBoundary b = LoadBoundaries.Find(events)!;

        Assert.Equal(20_000, b.BoundaryMs); // t=19 창은 1 초분 = 최고치 (100 MiB) 의 10% 로 아직 밑이 아님, t=20 은 0
        Assert.Equal(200 * Mib, b.Tier0Bytes);
        Assert.Equal(5 * Mib, b.Tier1Bytes);
        Assert.Equal(new TraceEvent("a", 0, 200 * Mib), Assert.Single(b.Tier0)); // 이어진 블록은 한 범위
    }

    [Fact]
    public void GradualDecayStaysAboveTenPercentSoTheSixtySecondCapApplies()
    {
        List<TraceEvent> events = [.. Enumerable.Range(0, 99).Select(s => Read(s, 100 - s, s * 100))];

        LoadBoundary b = LoadBoundaries.Find(events)!;

        Assert.Equal(60_000, b.BoundaryMs);
        Assert.Equal(Enumerable.Range(0, 60).Sum(s => 100L - s) * Mib, b.Tier0Bytes);
        Assert.Equal(Enumerable.Range(60, 39).Sum(s => 100L - s) * Mib, b.Tier1Bytes);
    }

    [Fact]
    public void SteadyReadingEndsAtTheCapEvenWhenTheTraceIsLonger()
    {
        List<TraceEvent> events = [.. Enumerable.Range(0, 80).Select(s => Read(s, 1, s))];

        Assert.Equal(60_000, LoadBoundaries.Find(events)!.BoundaryMs);
    }

    [Fact]
    public void ShortTraceEndsWhereTheReadingEnds()
    {
        LoadBoundary b = LoadBoundaries.Find([Read(0, 4, 0), Read(1, 4, 4)])!;

        Assert.Equal(2_000, b.BoundaryMs);
        Assert.Equal(8 * Mib, b.Tier0Bytes);
        Assert.Equal(0, b.Tier1Bytes);
    }

    [Fact]
    public void RepeatedBlocksCountOnceAndTier1ExcludesWhatTier0Has()
    {
        // 0 초에 2 MiB, 1 초에 같은 2 MiB 다시, 70 초에 (이미 읽은) 앞 1 MiB 와 새 1 MiB
        LoadBoundary b = LoadBoundaries.Find([Read(0, 2, 0), Read(1, 2, 0), Read(70, 2, 1)])!;

        Assert.Equal(2 * Mib, b.Tier0Bytes);
        Assert.Equal(1 * Mib, b.Tier1Bytes);
    }

    [Fact]
    public void Tier0KeepsAccessOrderAndSplitsNonContiguousRanges()
    {
        LoadBoundary b = LoadBoundaries.Find([new TraceEvent("b", 10 * Mib, Mib, TMs: 0), new TraceEvent("a", 0, Mib, TMs: 100), new TraceEvent("a", 5 * Mib, Mib, TMs: 200)])!;

        Assert.Equal([new TraceEvent("b", 10 * Mib, Mib), new TraceEvent("a", 0, Mib), new TraceEvent("a", 5 * Mib, Mib)], b.Tier0);
    }
}
