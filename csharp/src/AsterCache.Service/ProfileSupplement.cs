using AsterCache.Core.Profiles;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 트레이스를 만들 수 없는 게임 (안티치트) 의 계획 입력 보충: 트레이스가 덮지 못한 엔진 프로필 구간을 트레이스 뒤에 우선순위 순으로 더합니다 (트레이스 ∪ 프로필).
public static class ProfileSupplement
{
    /// 보충 구간을 쪼개는 이벤트 크기: 한 이벤트의 길이 상한 (TraceEvents.MaxLength) 안.
    public const long PieceBytes = 16L << 20;

    /// trace 의 고유 바이트(파일별 구간 합집합)가 capBytes 보다 작으면, profile(예산) 이 주는 우선순위 순 구간 중 trace 가 덮지 않은 부분을 capBytes 까지 trace 뒤에 더합니다.
    /// profile 에 넘기는 예산은 capBytes + trace 고유 바이트 (겹치는 만큼 빼도 capBytes 어치가 남도록). 보충이 없으면 trace 를 그대로 돌려줍니다. 경로 비교는 대소문자 무시.
    public static (IReadOnlyList<TraceEvent> Events, long SupplementBytes) Union(IReadOnlyList<TraceEvent> trace, long capBytes, Func<long, IReadOnlyList<TraceEvent>> profile)
    {
        var covered = new Dictionary<string, RangeSet>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, TraceEvent> byPath in trace.Where(e => e.Length > 0).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            covered[byPath.Key] = RangeSet.From(byPath.Select(e => (e.Offset, e.Offset + e.Length)));
        }

        long coveredBytes = covered.Values.Sum(r => r.Bytes);
        long room = capBytes - coveredBytes;
        if (room <= 0)
        {
            return (trace, 0);
        }

        var extra = new List<TraceEvent>();
        long taken = 0;
        foreach (TraceEvent p in profile(capBytes + coveredBytes).Where(p => p.Length > 0))
        {
            if (!covered.TryGetValue(p.Path, out RangeSet? set))
            {
                covered[p.Path] = set = new RangeSet();
            }

            foreach ((long start, long end) in set.Gaps(p.Offset, p.Offset + p.Length).ToList())
            {
                long len = Math.Min(end - start, room - taken);
                for (long at = start; at < start + len; at += PieceBytes)
                {
                    extra.Add(new TraceEvent(p.Path, at, Math.Min(PieceBytes, start + len - at), p.Cost));
                }

                set.Add(start, start + len);
                taken += len;
                if (taken >= room)
                {
                    return (taken == 0 ? trace : [.. trace, .. extra], taken);
                }
            }
        }

        return (taken == 0 ? trace : [.. trace, .. extra], taken);
    }

    /// 게임 설치 폴더의 엔진 프로필 선택 (FolderWarm.Plan 의 프로필 경로) 을 이벤트로: 우선순위 순, 경로는 링크를 푼 실제 경로. 프로필이 없거나 읽을 수 없으면 빈 목록.
    public static IReadOnlyList<TraceEvent> EventsFor(string originRoot, IReadOnlyCollection<string> exeNames, long budgetBytes, Func<string, string?> resolve)
    {
        try
        {
            ColdPlan plan = FolderWarm.Plan(originRoot, exeNames, 0, budgetBytes, EngineProfiles.Builtin);
            return [.. plan.Ops.Select(o => new TraceEvent(resolve(o.Path) ?? o.Path, o.Offset, o.Length))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // 겹치지 않는 [시작, 끝) 구간들 (시작 순).
    private sealed class RangeSet
    {
        private readonly List<(long S, long E)> _r = [];

        public long Bytes => _r.Sum(x => x.E - x.S);

        public static RangeSet From(IEnumerable<(long S, long E)> ranges)
        {
            var set = new RangeSet();
            foreach ((long s, long e) in ranges.OrderBy(x => x.S))
            {
                if (set._r.Count > 0 && s <= set._r[^1].E)
                {
                    set._r[^1] = (set._r[^1].S, Math.Max(set._r[^1].E, e));
                }
                else
                {
                    set._r.Add((s, e));
                }
            }

            return set;
        }

        public void Add(long s, long e)
        {
            if (e <= s)
            {
                return;
            }

            var keep = new List<(long, long)>();
            foreach ((long rs, long re) in _r)
            {
                if (re < s || rs > e)
                {
                    keep.Add((rs, re));
                }
                else
                {
                    (s, e) = (Math.Min(s, rs), Math.Max(e, re));
                }
            }

            keep.Add((s, e));
            _r.Clear();
            _r.AddRange(keep.OrderBy(x => x.Item1));
        }

        // [s, e) 중 덮이지 않은 구간들 (시작 순).
        public IEnumerable<(long S, long E)> Gaps(long s, long e)
        {
            long at = s;
            foreach ((long rs, long re) in _r)
            {
                if (re <= at)
                {
                    continue;
                }

                if (rs >= e)
                {
                    break;
                }

                if (rs > at)
                {
                    yield return (at, rs);
                }

                at = Math.Max(at, re);
            }

            if (at < e)
            {
                yield return (at, e);
            }
        }
    }
}
