namespace AsterCache.Core.Volume;

/// 클러스터 할당 비트맵 (1 = 사용 중). LCN [Start, Start + NBits) 을 덮고, 범위 밖은 사용 중으로 봅니다.
public sealed class VolumeBitmap
{
    private readonly byte[] _data;

    public VolumeBitmap(long start, long nbits, byte[] data)
    {
        Start = start;
        NBits = nbits;
        _data = (byte[])data.Clone();
    }

    public long Start { get; }

    public long NBits { get; }

    public bool Used(long lcn)
    {
        long i = lcn - Start;
        if (i < 0 || i >= NBits)
        {
            return true;
        }

        return ((_data[i >> 3] >> (int)(i & 7)) & 1) != 0;
    }

    public bool RangeFree(long lcn, long n)
    {
        for (long k = 0; k < n; k++)
        {
            if (Used(lcn + k))
            {
                return false;
            }
        }

        return true;
    }

    /// 빈 구간 (첫 LCN, 길이)을 오름차순으로 냅니다.
    public IEnumerable<(long Lcn, long Length)> FreeRuns()
    {
        long endBit = Math.Min(NBits, (long)_data.Length * 8);
        long run = -1;
        for (long s = 0; s < _data.Length; s++)
        {
            byte tok = _data[s];
            if (tok == 0)
            {
                if (run < 0)
                {
                    run = s * 8;
                }
            }
            else if (tok == 0xFF)
            {
                if (run >= 0 && Clip(run, s * 8, endBit) is { } done)
                {
                    yield return done;
                }

                run = -1;
            }
            else
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    long idx = s * 8 + bit;
                    bool free = ((tok >> bit) & 1) == 0;
                    if (free && run < 0)
                    {
                        run = idx;
                    }
                    else if (!free && run >= 0)
                    {
                        if (Clip(run, idx, endBit) is { } part)
                        {
                            yield return part;
                        }

                        run = -1;
                    }
                }
            }
        }

        if (run >= 0 && Clip(run, endBit, endBit) is { } last)
        {
            yield return last;
        }
    }

    /// 첫 LCN (align 의 배수), 없으면 null. preferLowLcn 이 false 면 가장 높은 맞는 구간. excluded 의 범위 (다른 계획이 예약한 존) 는 빈 구간에서 뺍니다.
    public long? FindFreeRun(long clustersNeeded, bool preferLowLcn = true, long minLcn = 0, long align = 1, IReadOnlyList<(long Lcn, long Clusters)>? excluded = null)
    {
        long? found = null;
        foreach ((long lcn, long n) in Without(FreeRuns(), excluded))
        {
            long start = AlignUp(Math.Max(lcn, minLcn), align);
            if (lcn + n - start >= clustersNeeded)
            {
                if (preferLowLcn)
                {
                    return start;
                }

                found = start;
            }
        }

        return found;
    }

    private static IEnumerable<(long Lcn, long Length)> Without(IEnumerable<(long Lcn, long Length)> runs, IReadOnlyList<(long Lcn, long Clusters)>? excluded)
    {
        if (excluded is null or { Count: 0 })
        {
            return runs;
        }

        var sorted = excluded.Where(x => x.Clusters > 0).OrderBy(x => x.Lcn).ToList();
        return runs.SelectMany(run => Split(run, sorted));
    }

    private static IEnumerable<(long Lcn, long Length)> Split((long Lcn, long Length) run, List<(long Lcn, long Clusters)> sorted)
    {
        long cursor = run.Lcn;
        long end = run.Lcn + run.Length;
        foreach ((long a, long n) in sorted)
        {
            if (a >= end || a + n <= cursor)
            {
                continue;
            }

            if (a > cursor)
            {
                yield return (cursor, a - cursor);
            }

            cursor = Math.Max(cursor, a + n);
        }

        if (cursor < end)
        {
            yield return (cursor, end - cursor);
        }
    }

    private (long Lcn, long Length)? Clip(long a, long b, long endBit)
    {
        b = Math.Min(b, endBit);
        return b > a ? (Start + a, b - a) : null;
    }

    private static long AlignUp(long value, long align)
    {
        long r = value % align;
        if (r < 0)
        {
            r += align;
        }

        return r == 0 ? value : value + (align - r);
    }
}
