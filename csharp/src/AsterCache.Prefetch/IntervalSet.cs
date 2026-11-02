namespace AsterCache.Prefetch;

/// 겹치지 않는 [시작, 끝) 구간들의 집합 (시작 순). 선행 읽기가 파일별로 "이미 맡은 구간", "아직 안 읽은 구간", "읽기를 끝낸 구간" 을 셉니다.
/// shortcut: 선형 삽입 목록, 구간이 수만 개로 늘어나는 파일이 나오면 균형 트리로 바꿈.
internal sealed class IntervalSet
{
    private readonly List<(long Start, long End)> _a = [];

    public int Count => _a.Count;

    public (long Start, long End)? First => _a.Count == 0 ? null : _a[0];

    public void Clear() => _a.Clear();

    public void Add(long start, long end)
    {
        if (end <= start)
        {
            return;
        }

        int i = Find(start - 1); // 끝이 start 이상인 첫 구간 (맞닿은 것도 합침)
        int j = i;
        while (j < _a.Count && _a[j].Start <= end)
        {
            start = Math.Min(start, _a[j].Start);
            end = Math.Max(end, _a[j].End);
            j++;
        }

        _a.RemoveRange(i, j - i);
        _a.Insert(i, (start, end));
    }

    public void Remove(long start, long end)
    {
        if (end <= start)
        {
            return;
        }

        int i = Find(start);
        int j = i;
        var keep = new List<(long, long)>(2);
        while (j < _a.Count && _a[j].Start < end)
        {
            if (_a[j].Start < start)
            {
                keep.Add((_a[j].Start, start));
            }

            if (_a[j].End > end)
            {
                keep.Add((end, _a[j].End));
            }

            j++;
        }

        _a.RemoveRange(i, j - i);
        _a.InsertRange(i, keep);
    }

    /// [start, end) 와 겹치는 바이트 수.
    public long Overlap(long start, long end)
    {
        long sum = 0;
        for (int i = Find(start); i < _a.Count && _a[i].Start < end; i++)
        {
            sum += Math.Min(_a[i].End, end) - Math.Max(_a[i].Start, start);
        }

        return sum;
    }

    /// [start, end) 중 집합에 없는 구간들 (오름차순).
    public List<(long Start, long End)> Gaps(long start, long end)
    {
        var gaps = new List<(long, long)>();
        long cursor = start;
        for (int i = Find(start); i < _a.Count && _a[i].Start < end; i++)
        {
            if (_a[i].Start > cursor)
            {
                gaps.Add((cursor, _a[i].Start));
            }

            cursor = Math.Max(cursor, _a[i].End);
        }

        if (cursor < end)
        {
            gaps.Add((cursor, end));
        }

        return gaps;
    }

    // 끝이 x 보다 큰 첫 구간의 위치 (없으면 Count).
    private int Find(long x)
    {
        int lo = 0, hi = _a.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_a[mid].End > x)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}
