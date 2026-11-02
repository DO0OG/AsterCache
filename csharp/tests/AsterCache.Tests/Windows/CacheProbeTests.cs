using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 캐시 온도 표본: 고르게 고르기, 열기 실패 표본 제외.
public sealed class CacheProbeTests
{
    private static List<ReadOp> Zone(int n) => Enumerable.Range(0, n).Select(i => new ReadOp("f" + i, i * 100L, 10)).ToList();

    [Fact]
    public void PickSamplesIsEvenAcrossTheOrderedList()
    {
        List<(string Path, long Offset)> picked = CacheProbe.PickSamples(Zone(80));

        Assert.Equal(8, picked.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "f" + (i * 10)), picked.Select(p => p.Path));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 1000L), picked.Select(p => p.Offset));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(1000, 8)]
    public void PickSamplesNeverExceedsCountOrList(int n, int expected)
    {
        List<(string Path, long Offset)> picked = CacheProbe.PickSamples(Zone(n));

        Assert.Equal(expected, picked.Count);
        Assert.Equal(picked.Count, picked.Select(p => p.Path).Distinct().Count());
    }

    [Fact]
    public void PickSamplesHonoursCustomCount()
    {
        Assert.Equal(["f0", "f50"], CacheProbe.PickSamples(Zone(100), 2).Select(p => p.Path));
    }

    [Fact]
    public void FailedSamplesAreLeftOutAndAllFailedGivesNull()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 3 << 20);
        tmp.WriteRandom("b.bin", 3 << 20);

        double? ok = CacheProbe.AverageReadMs([(tmp.Combine("a.bin"), 0), (tmp.Combine("nope.bin"), 0), (tmp.Combine("b.bin"), 1 << 20)]);
        double? none = CacheProbe.AverageReadMs([(tmp.Combine("nope.bin"), 0), (tmp.Combine("nope2.bin"), 0)]);
        double? empty = CacheProbe.AverageReadMs([]);

        Assert.NotNull(ok);
        Assert.True(ok >= 0);
        Assert.Null(none);
        Assert.Null(empty);
    }

    [Fact]
    public void ExclusivelyOpenedFileIsLeftOutAndOffsetPastTheEndCountsNothing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 1 << 20);
        tmp.WriteRandom("locked.bin", 1 << 20);
        using var exclusive = new FileStream(tmp.Combine("locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.NotNull(CacheProbe.AverageReadMs([(tmp.Combine("a.bin"), 0), (tmp.Combine("locked.bin"), 0)]));
        Assert.Null(CacheProbe.AverageReadMs([(tmp.Combine("locked.bin"), 0), (tmp.Combine("a.bin"), 5 << 20)]));
    }

    [Fact]
    public void ThresholdBetweenCachedAndSpinningDiskTimes() => Assert.InRange(CacheProbe.ColdThresholdMs, 1.0, 10.0);
}
