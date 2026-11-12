using AsterCache.Core.Plan;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 존이 상한까지 찬 뒤의 새 회차 블록 중 존 밖 블록을 예열 전용 목록 (warmonly) 에 덧붙일 행으로 고릅니다.
/// 재배치·계획 파일은 건드리지 않으므로 디스크 쓰기가 없고, 시작 스트림은 warmonly 를 읽지 않아 읽는 집합이 그대로입니다.
internal static class WarmOverflow
{
    /// listed: 이미 선읽기 목록에 든 행 (= 존 안 블록). stable: 가변 파일을 뺀 병합 트레이스 (병합 순서 = 최신 회차 먼저). listBytes: 이미 예열 목록에 든 바이트 (선읽기 + 예열 전용). capBytes: 캐시 예산.
    /// 목록 전체가 capBytes 를 넘지 않는 만큼만 병합 순서대로 고릅니다. 행은 블록이 이어지면 합치고, 처음 읽은 시각 (t_ms) 을 유지하며, Overflow 표시를 붙입니다 (가변 파일의 실행 앞부분 우선 규칙에서 빼기 위해).
    internal static List<TraceEvent> Select(IReadOnlyList<TraceEvent> listed, IReadOnlyList<TraceEvent> stable, long listBytes, long capBytes)
    {
        long room = capBytes - listBytes;
        var rows = new List<TraceEvent>();
        if (room < HotSet.Block)
        {
            return rows;
        }

        Dictionary<string, List<(long First, long End)>> zone = ZoneBlocks(listed);
        var seen = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        long used = 0;
        foreach (TraceEvent e in stable)
        {
            if (e.Length <= 0)
            {
                continue;
            }

            zone.TryGetValue(e.Path, out List<(long First, long End)>? inZone);
            HashSet<long>? seenBlocks = null;
            for (long b = e.Offset / HotSet.Block; b <= (e.Offset + e.Length - 1) / HotSet.Block; b++)
            {
                if (InZone(inZone, b))
                {
                    continue;
                }

                if (seenBlocks is null && !seen.TryGetValue(e.Path, out seenBlocks))
                {
                    seen[e.Path] = seenBlocks = [];
                }

                if (!seenBlocks.Add(b))
                {
                    continue;
                }

                if (used + HotSet.Block > room)
                {
                    return rows;
                }

                Append(rows, e, b);
                used += HotSet.Block;
            }
        }

        return rows;
    }

    // 경로별 목록 행의 블록 구간 [First, End) 를 정렬·병합해 둡니다.
    private static Dictionary<string, List<(long First, long End)>> ZoneBlocks(IReadOnlyList<TraceEvent> listed)
    {
        var zone = new Dictionary<string, List<(long First, long End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (TraceEvent row in listed)
        {
            if (row.Length <= 0)
            {
                continue;
            }

            if (!zone.TryGetValue(row.Path, out List<(long First, long End)>? ranges))
            {
                zone[row.Path] = ranges = [];
            }

            ranges.Add((row.Offset / HotSet.Block, (row.Offset + row.Length - 1) / HotSet.Block + 1));
        }

        foreach (List<(long First, long End)> ranges in zone.Values)
        {
            ranges.Sort();
            int last = 0;
            for (int i = 1; i < ranges.Count; i++)
            {
                if (ranges[i].First <= ranges[last].End)
                {
                    ranges[last] = (ranges[last].First, Math.Max(ranges[last].End, ranges[i].End));
                }
                else
                {
                    ranges[++last] = ranges[i];
                }
            }

            ranges.RemoveRange(last + 1, ranges.Count - last - 1);
        }

        return zone;
    }

    // 정렬·병합된 구간에서 이진 탐색: End 가 b 보다 큰 첫 구간이 b 를 덮는가.
    private static bool InZone(List<(long First, long End)>? ranges, long b)
    {
        if (ranges is null)
        {
            return false;
        }

        int low = 0;
        int high = ranges.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (ranges[mid].End <= b)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < ranges.Count && ranges[low].First <= b;
    }

    private static void Append(List<TraceEvent> rows, TraceEvent source, long block)
    {
        if (rows.Count > 0)
        {
            TraceEvent previous = rows[^1];
            if (string.Equals(previous.Path, source.Path, StringComparison.OrdinalIgnoreCase) && previous.Offset + previous.Length == block * HotSet.Block
                && previous.Length < TraceEvents.MaxBlocksPerRequest * HotSet.Block)
            {
                rows[^1] = previous with { Length = previous.Length + HotSet.Block, Cost = Math.Max(previous.Cost, source.Cost) };
                return;
            }
        }

        rows.Add(new TraceEvent(source.Path, block * HotSet.Block, HotSet.Block, source.Cost, source.TMs, Overflow: true));
    }
}
