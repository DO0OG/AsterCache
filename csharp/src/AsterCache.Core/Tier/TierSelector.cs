using AsterCache.Core.Apply;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Tier;

public sealed record TierPick(string Path, long Size, long HotBytes);

public sealed record TierSelection(IReadOnlyList<TierPick> Files, long MovedBytes, long CoveredHotBytes, long TotalHotBytes, IReadOnlyList<string> Missing);

/// SSD 로 옮길 파일 고르기 (측정용 tier_plan.py 와 같은 규칙): 파일마다 트레이스가 읽은 구간의 합집합 (핫 바이트) 을 재고,
/// "핫 바이트 / 파일 크기" 가 높은 순으로 예산 안에 들어가는 파일을 담습니다. 안 들어가는 파일은 건너뛰고 더 작은 파일은 계속 담습니다.
/// 게임 exe·dll 은 제외 (안티치트·런처의 서명 검사를 피함). 허용 루트 밖의 파일도 제외.
public static class TierSelector
{
    public static readonly string[] ExcludedExtensions = [".exe", ".dll"];

    /// sizeOf 는 파일 크기, 읽을 수 없으면 null (그 파일은 Missing 에 기록).
    public static TierSelection Select(IEnumerable<TraceEvent> events, long budgetBytes, IReadOnlyList<string> allowedRoots, Func<string, long?> sizeOf)
    {
        var spans = new Dictionary<string, List<(long S, long E)>>(StringComparer.OrdinalIgnoreCase);
        foreach (TraceEvent e in events.Where(e => e.Length > 0))
        {
            if (!spans.TryGetValue(e.Path, out List<(long S, long E)>? list))
            {
                spans[e.Path] = list = [];
            }

            list.Add((e.Offset, e.Offset + e.Length));
        }

        var rows = new List<TierPick>();
        var missing = new List<string>();
        foreach ((string path, List<(long S, long E)> ranges) in spans)
        {
            if (Excluded(path) || !PlanGuards.Inside(path, allowedRoots, System.IO.Path.GetFullPath))
            {
                continue;
            }

            if (sizeOf(path) is not { } size)
            {
                missing.Add(path);
                continue;
            }

            if (size > 0)
            {
                rows.Add(new TierPick(path, size, Hot(ranges, size)));
            }
        }

        long total = rows.Sum(r => r.HotBytes);
        rows.Sort((a, b) => ((double)b.HotBytes / b.Size).CompareTo((double)a.HotBytes / a.Size) is var c and not 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        var chosen = new List<TierPick>();
        long moved = 0;
        foreach (TierPick r in rows)
        {
            if (moved + r.Size <= budgetBytes)
            {
                chosen.Add(r);
                moved += r.Size;
            }
        }

        return new TierSelection(chosen, moved, chosen.Sum(r => r.HotBytes), total, missing);
    }

    public static bool Excluded(string path) => ExcludedExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    // 읽은 구간을 합치고 파일 크기로 자른 총 길이.
    private static long Hot(List<(long S, long E)> ranges, long size)
    {
        long hot = 0;
        long curS = -1, curE = -1;
        foreach ((long s, long e) in ranges.OrderBy(r => r.S))
        {
            if (curE >= 0 && s <= curE)
            {
                curE = Math.Max(curE, e);
                continue;
            }

            hot += Clip(curS, curE, size);
            (curS, curE) = (s, e);
        }

        return hot + Clip(curS, curE, size);
    }

    private static long Clip(long s, long e, long size) => e < 0 ? 0 : Math.Max(0, Math.Min(e, size) - Math.Min(s, size));
}
