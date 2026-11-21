namespace AsterCache.Windows;

/// 파일 안의 바이트 구간 집합 ([시작, 끝) 을 겹치지 않게 합쳐 시작 순으로 둔 목록) 의 계산: 이미 붙든 구간과 빠진 것으로 기억한 구간을 다룹니다.
public static class RangeSets
{
    /// [from, to) 에서 set 에 든 구간을 뺀 나머지 (앞에서부터).
    public static List<(long From, long To)> Uncovered(List<(long Start, long End)> set, long from, long to)
    {
        List<(long, long)> result = [];
        long cursor = from;
        for (int i = FirstEndingAfter(set, from); i < set.Count && set[i].Start < to && cursor < to; i++)
        {
            if (set[i].Start > cursor)
            {
                result.Add((cursor, set[i].Start));
            }

            cursor = Math.Max(cursor, set[i].End);
        }

        if (cursor < to)
        {
            result.Add((cursor, to));
        }

        return result;
    }

    /// [from, to) 를 set 에 더합니다 (맞닿거나 겹치는 구간과 합침).
    public static void Add(List<(long Start, long End)> set, long from, long to)
    {
        if (to <= from)
        {
            return;
        }

        int first = FirstEndingAfter(set, from - 1); // 끝이 from 과 맞닿는 구간부터
        int last = first;
        long start = from;
        long end = to;
        while (last < set.Count && set[last].Start <= to)
        {
            (start, end) = (Math.Min(start, set[last].Start), Math.Max(end, set[last].End));
            last++;
        }

        set.RemoveRange(first, last - first);
        set.Insert(first, (start, end));
    }

    /// [from, to) 를 set 에서 뺍니다 (걸친 구간은 남는 쪽만 남김).
    public static void Remove(List<(long Start, long End)> set, long from, long to)
    {
        if (to <= from)
        {
            return;
        }

        int first = FirstEndingAfter(set, from);
        List<(long, long)> kept = [];
        int i = first;
        for (; i < set.Count && set[i].Start < to; i++)
        {
            if (set[i].Start < from)
            {
                kept.Add((set[i].Start, from));
            }

            if (set[i].End > to)
            {
                kept.Add((to, set[i].End));
            }
        }

        set.RemoveRange(first, i - first);
        set.InsertRange(first, kept);
    }

    // 끝이 value 보다 큰 첫 구간의 위치 (구간이 합쳐 정렬돼 있어 끝도 정렬됨).
    private static int FirstEndingAfter(List<(long Start, long End)> set, long value)
    {
        int low = 0;
        int high = set.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (set[mid].End > value)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}
