namespace AsterCache.Core.Trace;

/// 로딩 경계: 이 시각 (세션 시작부터 ms) 전에 읽은 고유 범위가 tier0 (접근 순서, 64 KiB 블록 단위로 이어 붙임), 이후에만 읽은 고유 바이트가 tier1.
public sealed record LoadBoundary(long BoundaryMs, IReadOnlyList<TraceEvent> Tier0, long Tier0Bytes, long Tier1Bytes);

public static class LoadBoundaries
{
    public const long WindowSeconds = 10;
    public const long MaxBoundaryMs = 60_000;
    public const long Block = 65536;
    private const int Percent = 10;

    /// 앞으로 10 초 동안 읽는 양 (1 초 간격 이동 창) 이 최고치의 10% 밑으로 처음 떨어지는 시점 (최고 창 이후), 없으면 60 초 중 이른 쪽이 경계.
    /// 모든 행에 t_ms 가 있어야 하고, 하나라도 없거나 행이 없으면 null (호출부가 옛 방식을 씀). 행 순서는 접근 순서입니다.
    /// 최고치는 처음 60 초에 시작하는 창만 봅니다. 60 초 넘게 늦은 읽기는 경계 계산에 들어가지 않고 tier1 로만 셉니다.
    public static LoadBoundary? Find(IReadOnlyList<TraceEvent> events)
    {
        if (events.Count == 0 || events.Any(e => e.TMs is null))
        {
            return null;
        }

        int seconds = (int)(MaxBoundaryMs / 1000);
        var bins = new long[seconds + WindowSeconds];
        foreach (TraceEvent e in events)
        {
            long sec = e.TMs!.Value / 1000;
            if (sec < bins.Length)
            {
                bins[sec] += e.Length;
            }
        }

        var windows = new long[seconds];
        for (int t = 0; t < seconds; t++)
        {
            windows[t] = bins.Skip(t).Take((int)WindowSeconds).Sum();
        }

        long peak = windows.Max();
        int at = Array.IndexOf(windows, peak);
        long boundaryMs = peak == 0 ? 0 : MaxBoundaryMs;
        for (int t = at + 1; peak > 0 && t < seconds; t++)
        {
            if (windows[t] * 100 < peak * Percent)
            {
                boundaryMs = t * 1000L;
                break;
            }
        }

        var seen = new HashSet<(string, long)>();
        var tier0 = new List<TraceEvent>();
        foreach (TraceEvent e in events.Where(e => e.TMs!.Value < boundaryMs))
        {
            for (long blk = e.Offset / Block; blk <= (e.Offset + e.Length - 1) / Block; blk++)
            {
                if (!seen.Add((e.Path, blk)))
                {
                    continue;
                }

                if (tier0.Count > 0 && tier0[^1] is { } run && run.Path == e.Path && run.Offset + run.Length == blk * Block)
                {
                    tier0[^1] = run with { Length = run.Length + Block };
                }
                else
                {
                    tier0.Add(new TraceEvent(e.Path, blk * Block, Block, e.Cost));
                }
            }
        }

        long tier0Bytes = seen.Count * Block;
        foreach (TraceEvent e in events.Where(e => e.TMs!.Value >= boundaryMs))
        {
            for (long blk = e.Offset / Block; blk <= (e.Offset + e.Length - 1) / Block; blk++)
            {
                seen.Add((e.Path, blk));
            }
        }

        return new LoadBoundary(boundaryMs, tier0, tier0Bytes, (seen.Count - tier0Bytes / Block) * Block);
    }
}
