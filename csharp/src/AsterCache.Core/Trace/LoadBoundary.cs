namespace AsterCache.Core.Trace;

/// 로딩 경계: 이 시각 (세션 시작부터 ms) 전에 읽은 고유 범위가 tier0 (접근 순서, 64 KiB 블록 단위로 이어 붙임), 이후에만 읽은 고유 바이트가 tier1.
public sealed record LoadBoundary(long BoundaryMs, IReadOnlyList<TraceEvent> Tier0, long Tier0Bytes, long Tier1Bytes);

public static class LoadBoundaries
{
    public const long WindowSeconds = 10;
    public const long MaxBoundaryMs = 60_000;
    public const long Block = 65536;
    public const int Percent = 10;

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

/// <summary>실시간 프로세스 읽기 카운터로 로딩 경계를 찾습니다.</summary>
public sealed class RuntimeLoadBoundary
{
    private const long WindowMs = LoadBoundaries.WindowSeconds * 1000;
    // shortcut: 온라인 판정은 미래 최고치를 모름, 실기 로그로 임계 재조정
    private const long MinimumObservationMs = 15_000;
    private const long MinimumPeakBytes = 64L << 20;
    private readonly List<(long AtMs, long Bytes)> _samples = [];
    private long? _baseline;
    private long _peakBytes;
    private long _peakWindowStartMs = -1;

    public long? BoundaryMs { get; private set; }

    public bool ReachedMaximum { get; private set; }

    /// 로그용: 지금까지 본 10 초 창의 최고 읽기량, 가장 최근 창의 읽기량, 마지막 표본의 관찰 시각 (ms). 판정에는 쓰지 않습니다.
    public long PeakBytes => _peakBytes;

    public long LastWindowBytes { get; private set; }

    public long ObservedMs { get; private set; }

    /// <summary>읽기량이 계속 줄거나 60초 관찰 한도에 도달하면 true를 반환합니다.</summary>
    public bool Update(long tMs, long cumulativeBytes)
    {
        if (tMs < 0 || cumulativeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(tMs < 0 ? nameof(tMs) : nameof(cumulativeBytes));
        }

        if (BoundaryMs is not null)
        {
            return true;
        }

        if (_samples.Count > 0 && tMs <= _samples[^1].AtMs)
        {
            return false;
        }

        if (tMs > LoadBoundaries.MaxBoundaryMs && _peakBytes == 0)
        {
            return false; // 60 초 안에 읽기가 없으면 뒤늦은 읽기를 시작 로딩으로 간주하지 않습니다.
        }

        _baseline ??= cumulativeBytes;
        long relativeBytes = Math.Max(0, cumulativeBytes - _baseline.Value);
        _samples.Add((tMs, relativeBytes));
        ObservedMs = tMs;

        if (tMs >= WindowMs)
        {
            long windowStartMs = tMs - WindowMs;
            long windowStartBytes = BytesAtOrBefore(windowStartMs);
            long windowBytes = Math.Max(0, relativeBytes - windowStartBytes);
            LastWindowBytes = windowBytes;
            if (windowStartMs < LoadBoundaries.MaxBoundaryMs && windowBytes > _peakBytes)
            {
                _peakBytes = windowBytes;
                _peakWindowStartMs = windowStartMs;
            }
            else if (tMs >= MinimumObservationMs && _peakBytes >= MinimumPeakBytes
                && windowStartMs > _peakWindowStartMs
                && (decimal)windowBytes * 100 < (decimal)_peakBytes * LoadBoundaries.Percent)
            {
                BoundaryMs = windowStartMs;
                return true;
            }
        }

        if (tMs >= LoadBoundaries.MaxBoundaryMs && _peakBytes > 0)
        {
            BoundaryMs = LoadBoundaries.MaxBoundaryMs;
            ReachedMaximum = true;
            return true;
        }

        return false;
    }

    private long BytesAtOrBefore(long tMs)
    {
        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            if (_samples[i].AtMs <= tMs)
            {
                return _samples[i].Bytes;
            }
        }

        return _samples[0].Bytes;
    }
}
