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

    /// 존 앞 여유를 두기 시작하는 최소 남는 길이 (필요 길이를 뺀 빈 구간의 나머지). 이보다 짧으면 구간 시작에 둡니다:
    /// 나머지의 절반이 512 MiB 도 안 되면 (다른 프로그램의 쓰기 약 0.8 GiB/시간 기준 40 분 미만) 여유의 이득이 작습니다.
    public const long ZoneMarginMinSlackBytes = 1L << 30;

    /// 앞 여유의 상한. 여유가 클수록 존이 더 높은 LCN (HDD 안쪽) 으로 가므로 상한을 둡니다. 다른 프로그램이 7 분마다 약 90 MiB 를 새로 쓰는 실측 (약 0.8 GiB/시간) 으로 몇 시간치.
    public const long ZoneMarginCapBytes = 4L << 30;

    /// 빈 구간에서 필요한 길이를 뺀 나머지 (slackClusters) 에서 존 앞에 둘 여유 (클러스터, align 의 배수). 나머지가 최소보다 짧으면 0, 아니면 나머지의 절반을 상한까지.
    public static long ZoneMarginClusters(long slackClusters, long clusterBytes, long align)
    {
        if (clusterBytes <= 0 || slackClusters * clusterBytes < ZoneMarginMinSlackBytes)
        {
            return 0;
        }

        long margin = Math.Min(slackClusters / 2, ZoneMarginCapBytes / clusterBytes);
        return align > 1 ? margin / align * align : margin;
    }

    /// 새 존 자리: FindFreeRun (낮은 LCN 우선) 이 고르는 빈 구간 안에서 시작이 아니라 ZoneMarginClusters 만큼 뒤. 구간 시작·여유도 함께 돌려줍니다.
    public ZonePlacement? FindZoneRun(long clustersNeeded, long clusterBytes, long align, IReadOnlyList<(long Lcn, long Clusters)>? excluded = null)
    {
        foreach ((long lcn, long n) in Without(FreeRuns(), excluded))
        {
            long start = AlignUp(lcn, align);
            long slack = lcn + n - start - clustersNeeded;
            if (slack >= 0)
            {
                long margin = ZoneMarginClusters(slack, clusterBytes, align);
                return new ZonePlacement(start + margin, start, margin);
            }
        }

        return null;
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

/// 새 존 자리: 존 시작, 고른 빈 구간의 (정렬된) 시작, 그 앞에 둔 여유 (클러스터, 0 이면 구간 시작).
public readonly record struct ZonePlacement(long Start, long RunStart, long MarginClusters);
