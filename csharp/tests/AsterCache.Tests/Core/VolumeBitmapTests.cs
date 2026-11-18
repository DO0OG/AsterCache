using AsterCache.Core.Volume;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class VolumeBitmapTests
{
    [Fact]
    public void FreeRunsMatchBruteForce()
    {
        var rng = new Random(7);
        foreach (long nbits in new long[] { 1, 8, 13, 64, 1000, 4099 })
        {
            foreach (double density in new[] { 0.0, 0.02, 0.5, 0.98, 1.0 })
            {
                var used = new HashSet<long>();
                for (long i = 0; i < nbits; i++)
                {
                    if (rng.NextDouble() < density)
                    {
                        used.Add(i);
                    }
                }

                Assert.Equal(Brute(used, nbits), Make(used, nbits).FreeRuns().ToList());
            }
        }
    }

    [Fact]
    public void FindFreeRunPicksLowestOrHighestFittingRun()
    {
        VolumeBitmap bm = Make(Span(0, 10).Concat(Span(30, 10)).ToHashSet(), 100);

        Assert.Equal(10L, bm.FindFreeRun(5));
        Assert.Equal(40L, bm.FindFreeRun(21));
        Assert.Equal(40L, bm.FindFreeRun(20, preferLowLcn: false));
        Assert.Equal(10L, bm.FindFreeRun(20));
        Assert.Null(bm.FindFreeRun(61));
        Assert.Equal(12L, bm.FindFreeRun(5, minLcn: 12));
        Assert.True(bm.Used(5) && !bm.Used(15) && bm.Used(1000));
    }

    private const long Cb = 4096;
    private const long MinSlack = (1L << 30) / Cb; // 262144 클러스터
    private const long Cap = (4L << 30) / Cb; // 1048576 클러스터

    [Fact]
    public void ZoneMarginIsZeroBelowTheMinimumHalfOfTheSlackAboveItAndCapped()
    {
        Assert.Equal(0, VolumeBitmap.ZoneMarginClusters(MinSlack - 1, Cb, 4));
        Assert.Equal(MinSlack / 2, VolumeBitmap.ZoneMarginClusters(MinSlack, Cb, 4));
        Assert.Equal(400000 / 2, VolumeBitmap.ZoneMarginClusters(400000, Cb, 4));
        Assert.Equal(Cap, VolumeBitmap.ZoneMarginClusters(100_000_000, Cb, 4));
        Assert.Equal(0, VolumeBitmap.ZoneMarginClusters(400001, Cb, 4) % 4); // 정렬의 배수로 내림
        Assert.Equal(200000, VolumeBitmap.ZoneMarginClusters(400001, Cb, 4));
    }

    [Fact]
    public void ALongFreeRunGetsTheZoneAfterAMarginAndAShortOneKeepsTheStart()
    {
        var bm = new VolumeBitmap(0, 4_000_000, new byte[500_000]); // 전부 비어 있음
        ZonePlacement p = bm.FindZoneRun(1000, Cb, 4)!.Value;
        Assert.Equal((Cap, 0L, Cap), (p.Start, p.RunStart, p.MarginClusters)); // 상한까지

        var medium = new VolumeBitmap(0, 400_000, new byte[50_000]);
        Assert.Equal(149_996, medium.FindZoneRun(100_001, Cb, 4)!.Value.Start); // 나머지 299999 의 절반 149999 를 4 의 배수로 내림
    }

    [Fact]
    public void AFreeRunBarelyLongerThanNeededKeepsTheStartAndAlignmentIsKept()
    {
        var small = new VolumeBitmap(0, 1000, new byte[125]);
        Assert.Equal(new ZonePlacement(0, 0, 0), small.FindZoneRun(100, Cb, 4));
        Assert.Equal(small.FindFreeRun(100, true, 0, 4), small.FindZoneRun(100, Cb, 4)!.Value.Start);
        Assert.Null(small.FindZoneRun(1001, Cb, 4));

        var bm = new VolumeBitmap(0, 4_000_000, new byte[500_000]);
        foreach (long need in new long[] { 1, 7, 12345, 3_000_000 })
        {
            ZonePlacement p = bm.FindZoneRun(need, Cb, 4)!.Value;
            Assert.Equal(0, p.Start % 4);
            Assert.True(p.Start + need <= 4_000_000); // 구간 밖으로 나가지 않음
            Assert.Equal(p.RunStart + p.MarginClusters, p.Start);
        }
    }

    [Fact]
    public void TheMarginIsAppliedToTheRunLeftAfterExcludedReservationsAndAFittingEarlierRunIsStillPreferred()
    {
        var bm = new VolumeBitmap(0, 4_000_000, new byte[500_000]);
        ZonePlacement p = bm.FindZoneRun(1000, Cb, 4, excluded: [(0, 1_000_001)])!.Value;
        Assert.Equal(1_000_004, p.RunStart); // 예약 뒤 4 의 배수로 올림한 구간 시작
        Assert.Equal(1_000_004 + Cap, p.Start);

        // 앞의 짧은 구간은 건너뛰고 (필요 길이에 못 미침) 처음으로 맞는 긴 구간에 여유를 둠
        HashSet<long> used = Span(0, 10).Concat(Span(500, 10)).ToHashSet();
        VolumeBitmap two = Make(used, 4_000_000);
        ZonePlacement q = two.FindZoneRun(1000, Cb, 4)!.Value;
        Assert.Equal(512, q.RunStart); // 510 을 4 의 배수로 올림
        Assert.Equal(512 + Cap, q.Start);
    }

    private static VolumeBitmap Make(HashSet<long> used, long nbits)
    {
        var data = new byte[(nbits + 7) / 8];
        foreach (long i in used)
        {
            data[i >> 3] |= (byte)(1 << (int)(i & 7));
        }

        return new VolumeBitmap(0, nbits, data);
    }

    private static List<(long Lcn, long Length)> Brute(HashSet<long> used, long nbits)
    {
        var runs = new List<(long Lcn, long Length)>();
        long? cur = null;
        for (long i = 0; i <= nbits; i++)
        {
            bool free = i < nbits && !used.Contains(i);
            if (free && cur is null)
            {
                cur = i;
            }
            else if (!free && cur is { } start)
            {
                runs.Add((start, i - start));
                cur = null;
            }
        }

        return runs;
    }

    private static IEnumerable<long> Span(long from, int count) => Enumerable.Range(0, count).Select(i => from + i);
}
