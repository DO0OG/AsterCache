using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 캐시 온도 표본: 앞 4 곳 + 고르게 4 곳 고르기, 표본 길이, 판정, 열기 실패 표본 제외.
public sealed class CacheProbeTests
{
    private static List<ReadOp> Zone(int n) => Enumerable.Range(0, n).Select(i => new ReadOp("f" + i, i * 100L, 10)).ToList();

    [Fact]
    public void PickSamplesTakesTheFirstFourThenFourEvenlyFromTheRest()
    {
        List<(string Path, long Offset, int Bytes)> picked = CacheProbe.PickSamples(Zone(84));

        Assert.Equal(8, picked.Count);
        Assert.Equal(["f0", "f1", "f2", "f3", "f4", "f24", "f44", "f64"], picked.Select(p => p.Path)); // 앞 4 + 나머지 80 개에서 20 칸 간격
        Assert.Equal(8, picked.Select(p => p.Path).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(1000, 8)]
    public void PickSamplesNeverExceedsCountOrList(int n, int expected)
    {
        List<(string Path, long Offset, int Bytes)> picked = CacheProbe.PickSamples(Zone(n));

        Assert.Equal(expected, picked.Count);
        Assert.Equal(picked.Count, picked.Select(p => p.Path).Distinct().Count());
    }

    [Fact]
    public void PickSamplesHonoursCustomCounts()
    {
        Assert.Equal(["f0", "f1", "f2", "f51"], CacheProbe.PickSamples(Zone(100), 2, 2).Select(p => p.Path));
    }

    [Fact]
    public void SampleLengthIsTheOpLengthCappedAtOneMiB()
    {
        List<(string Path, long Offset, int Bytes)> picked = CacheProbe.PickSamples([new ReadOp("a", 0, 4096), new ReadOp("b", 5, 1 << 20), new ReadOp("c", 0, 200L << 20)]);

        Assert.Equal([4096, 1 << 20, 1 << 20], picked.Select(p => p.Bytes));
    }

    [Fact]
    public void ColdMeansTwoOrMoreSlowSamplesOrAllWhenFewer()
    {
        Assert.False(new ProbeResult(8, 1, 3).Cold);
        Assert.True(new ProbeResult(8, 2, 3).Cold);
        Assert.False(new ProbeResult(8, 0, 0.5).Cold);
        Assert.True(new ProbeResult(1, 1, 9).Cold); // 표본이 하나뿐이면 그것이 느리면 식음
        Assert.False(new ProbeResult(1, 0, 0.5).Cold);
    }

    [Fact]
    public void FailedSamplesAreLeftOutAndAllFailedGivesNull()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 3 << 20);
        tmp.WriteRandom("b.bin", 3 << 20);

        ProbeResult? ok = CacheProbe.Probe([(tmp.Combine("a.bin"), 0, 1 << 20), (tmp.Combine("nope.bin"), 0, 1 << 20), (tmp.Combine("b.bin"), 1 << 20, 1 << 20)]);
        ProbeResult? none = CacheProbe.Probe([(tmp.Combine("nope.bin"), 0, 1 << 20), (tmp.Combine("nope2.bin"), 0, 1 << 20)]);
        ProbeResult? empty = CacheProbe.Probe([]);

        Assert.NotNull(ok);
        Assert.Equal(2, ok.Samples);
        Assert.True(ok.AverageMs >= 0);
        Assert.Null(none);
        Assert.Null(empty);
    }

    [Fact]
    public void ACancelledTokenStopsBetweenSamplesAndGivesNoPartialResult()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 1 << 20);
        tmp.WriteRandom("b.bin", 1 << 20);
        (string, long, int)[] samples = [(tmp.Combine("a.bin"), 0, 1 << 20), (tmp.Combine("b.bin"), 0, 1 << 20)];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(CacheProbe.Probe(samples, ct: cts.Token));
        Assert.Equal(2, CacheProbe.Probe(samples)!.Samples); // 기본값은 이전과 같음
    }

    [Fact]
    public void ReadsOnlyTheSampleLengthSoAShortOpDoesNotTouchItsNeighbour()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 1 << 20);

        Assert.Equal(1, CacheProbe.Probe([(tmp.Combine("a.bin"), 0, 4096)])!.Samples);
        Assert.Null(CacheProbe.Probe([(tmp.Combine("a.bin"), 0, 0)])); // 길이 0 은 읽을 것이 없음
    }

    [Fact]
    public void ExclusivelyOpenedFileIsLeftOutAndOffsetPastTheEndCountsNothing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 1 << 20);
        tmp.WriteRandom("locked.bin", 1 << 20);
        using var exclusive = new FileStream(tmp.Combine("locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.NotNull(CacheProbe.Probe([(tmp.Combine("a.bin"), 0, 1 << 20), (tmp.Combine("locked.bin"), 0, 1 << 20)]));
        Assert.Null(CacheProbe.Probe([(tmp.Combine("locked.bin"), 0, 1 << 20), (tmp.Combine("a.bin"), 5 << 20, 1 << 20)]));
    }


    [Fact]
    public void RoundsMoveEverySampleAndAreDeterministic()
    {
        List<ReadOp> zone = Zone(200);
        var rounds = Enumerable.Range(0, 4).Select(r => CacheProbe.PickSamples(zone, round: r).Select(p => p.Path).ToList()).ToList();

        Assert.Equal(rounds[1], CacheProbe.PickSamples(zone, round: 1).Select(p => p.Path)); // 같은 회차는 같은 위치
        Assert.All(rounds, r => Assert.Equal(8, r.Distinct().Count()));
        Assert.Equal(rounds.Count, rounds.Select(r => string.Join(',', r)).Distinct().Count()); // 회차마다 다른 묶음
        Assert.True(rounds.Skip(1).All(r => r.Count(p => rounds[0].Contains(p)) < 8));
        Assert.All(rounds.Skip(1), r => Assert.All(r.Take(4), p => Assert.True(int.Parse(p[1..]) < 16))); // 앞쪽 표본은 앞쪽 16 곳 안
    }

    [Fact]
    public void NextRoundCountsPerKeyFromZero()
    {
        string key = "k" + Guid.NewGuid().ToString("N");

        Assert.Equal([0, 1, 2], [CacheProbe.NextRound(key), CacheProbe.NextRound(key), CacheProbe.NextRound(key)]);
        Assert.Equal(0, CacheProbe.NextRound(key + "x"));
    }

    [Fact]
    public void SlowThresholdIsBelowASataDiskCacheHitAndAboveARamHit()
    {
        Assert.Equal(0.8, CacheProbe.ColdThresholdMs);
        Assert.InRange(CacheProbe.ColdThresholdMs, 0.5, 1.7);
    }

    // 표본마다 읽기 전후로 누계를 한 번씩 읽으므로, 두 번째 호출마다 perSample 쪽 늘어난 가짜 누계 (넘침 시험은 start 를 끝 가까이로).
    private static Func<uint?> Pages(uint perSample, uint start = 0)
    {
        int calls = 0;
        uint value = start;
        return () =>
        {
            if (calls++ % 2 == 1)
            {
                value = unchecked(value + perSample);
            }

            return value;
        };
    }

    // 시간 기준은 -1 (항상 넘음) 이나 1e9 (절대 안 넘음) 로 고정해 실제 경과 시간에 기대지 않음.
    private static ProbeResult EightSamples(int bytes, double slowMs, Func<uint?> pageReads)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 1 << 20);
        string path = tmp.Combine("a.bin");

        return CacheProbe.Probe(Enumerable.Range(0, 8).Select(_ => (path, 0L, bytes)).ToList(), slowMs, pageReads: pageReads)!;
    }

    [Fact]
    public void SamplesOverTheTimeLimitWithFewPageReadsAreNotSlow()
    {
        ProbeResult r = EightSamples(1 << 20, -1, Pages(5));

        Assert.Equal((8, 0), (r.Samples, r.Slow));
        Assert.False(r.Cold);
    }

    [Fact]
    public void SamplesOverTheTimeLimitWithPageReadsFittingTheirSizeAreSlowAndCold()
    {
        ProbeResult r = EightSamples(1 << 20, -1, Pages(256));

        Assert.Equal((8, 8), (r.Samples, r.Slow));
        Assert.True(r.Cold);
    }

    [Fact]
    public void ManyPageReadsWithinTheTimeLimitAreNotSlow()
    {
        Assert.Equal(0, EightSamples(1 << 20, 1e9, Pages(256)).Slow);
    }

    [Theory]
    [InlineData(1 << 20, 63, 0)] // 1/4 (64 쪽) 바로 아래
    [InlineData(1 << 20, 64, 8)] // 1/4
    [InlineData(1 << 17, 15, 0)] // 128 KiB (32 쪽): 1/4 는 8 쪽이라 하한 16 쪽이 우선
    [InlineData(1 << 17, 16, 8)]
    [InlineData(1 << 14, 3, 0)] // 16 KiB (4 쪽): 하한이 표본의 쪽 수로 낮아짐
    [InlineData(1 << 14, 4, 8)]
    [InlineData(4096, 0, 0)] // 4 KiB (1 쪽)
    [InlineData(4096, 1, 8)]
    public void PageReadGrowthMustReachAQuarterOfTheSampleWithAFloor(int bytes, uint pages, int slow)
    {
        Assert.Equal(slow, EightSamples(bytes, -1, Pages(pages)).Slow);
    }

    [Fact]
    public void WithoutAPageReadCountTheTimeAloneDecides()
    {
        Assert.Equal(8, EightSamples(1 << 20, -1, () => null).Slow);
        Assert.Equal(0, EightSamples(1 << 20, 1e9, () => null).Slow);
        int calls = 0;
        Assert.Equal(8, EightSamples(1 << 20, -1, () => calls++ % 2 == 0 ? 0u : null).Slow); // 읽은 뒤의 누계만 실패해도 시간만
    }

    [Fact]
    public void AWrappedPageReadCountIsComparedWithUnsignedSubtraction()
    {
        Assert.Equal(8, EightSamples(1 << 20, -1, Pages(256, uint.MaxValue - 100)).Slow);
        Assert.Equal(0, EightSamples(1 << 20, -1, Pages(5, uint.MaxValue - 2)).Slow);
    }
}
