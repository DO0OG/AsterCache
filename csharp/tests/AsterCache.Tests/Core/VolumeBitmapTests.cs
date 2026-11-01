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
