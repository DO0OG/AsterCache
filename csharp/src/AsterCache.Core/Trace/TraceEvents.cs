using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 추적 한 줄 {path, offset, length, cost}. cost 가 없으면 8.0 (Python 기본값).
public readonly record struct TraceEvent(string Path, long Offset, long Length, double Cost = TraceEvents.DefaultCost)
{
    public JObject ToJson() => new() { { "path", Path }, { "offset", Offset }, { "length", Length }, { "cost", Cost } };
}

/// 검증을 마친 요청: 블록 번호 [First, Last] 를 읽음.
public readonly record struct BlockEvent(string Path, long First, long Last, double Cost);

public static class TraceEvents
{
    public const double DefaultCost = 8.0;
    public const int MaxSessionEvents = 500_000;
    public const int MaxPathLength = 32767;
    public const long MaxLength = 64L << 20;
    public const int MaxBlocksPerRequest = 1024;

    /// 읽은 JSON 행 하나를 변환합니다. 형식만 보고 (path 문자열, offset·length 정수, cost 숫자) 값 범위 검사는 <see cref="Validated"/> 가 합니다.
    public static TraceEvent FromJson(object? row)
    {
        if (row is not JObject r || !r.TryGetValue("path", out object? p) || p is not string path)
        {
            throw new PyValueException("bad row");
        }

        if (!r.TryGetValue("offset", out object? o) || o is not long offset
            || !r.TryGetValue("length", out object? n) || n is not long length)
        {
            throw new PyValueException("bad row");
        }

        double cost = DefaultCost;
        if (r.TryGetValue("cost", out object? c))
        {
            cost = c switch
            {
                long l => l,
                double d => d,
                _ => throw new PyValueException("bad cost"),
            };
        }

        return new TraceEvent(path, offset, length, cost);
    }

    /// Python _validated_events: 요청마다 값 범위를 검사하고 64 KiB 블록 번호로 바꿉니다.
    public static List<BlockEvent> Validated(IReadOnlyList<TraceEvent> session, long block)
    {
        if (session.Count > MaxSessionEvents)
        {
            throw new PyValueException("session too large");
        }

        var result = new List<BlockEvent>(session.Count);
        foreach (TraceEvent e in session)
        {
            if (e.Path.Length == 0 || e.Path.Length > MaxPathLength || e.Offset < 0 || e.Length <= 0 || e.Length > MaxLength)
            {
                throw new PyValueException("bad event");
            }

            if (!double.IsFinite(e.Cost) || e.Cost < 0)
            {
                throw new PyValueException("bad cost");
            }

            long first = e.Offset / block;
            long last = (e.Offset + e.Length - 1) / block;
            if (last - first > MaxBlocksPerRequest)
            {
                throw new PyValueException("too many blocks in one request");
            }

            result.Add(new BlockEvent(e.Path, first, last, e.Cost));
        }

        return result;
    }
}
