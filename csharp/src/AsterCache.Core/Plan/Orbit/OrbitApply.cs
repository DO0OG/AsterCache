using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Plan.Orbit;

public static class OrbitApply
{
    /// 항등·무효 순열이면 입력 목록 그대로(같은 참조) 돌려줍니다. 기준선과 바이트 단위로 같아야 하기 때문입니다.
    /// 적용 여부는 호출자가 ReferenceEquals(결과, ops) 로 판정합니다(같은 참조 = 적용 안 함).
    public static List<ReadOp> Reorder(List<ReadOp> ops, OrbitSegmentSet set, IReadOnlyList<int>? order)
    {
        if (order is null || !ReferenceEquals(set.Ops, ops) || order.Count != set.Segments.Count
            || IsIdentity(order, order.Count))
        {
            return ops;
        }

        var seen = new bool[order.Count];
        foreach (var index in order)
        {
            if (index < 0 || index >= seen.Length || seen[index])
            {
                return ops;
            }

            seen[index] = true;
        }

        // Build 는 L 을 끝까지 덮어야 합니다. 끝이 어긋나면 일부 op 를 잃으므로 적용하지 않습니다.
        int covered = set.Segments.Count == 0 ? set.HeadCount : set.Segments[^1].Start + set.Segments[^1].Count;
        if (covered != ops.Count)
        {
            return ops;
        }

        var result = new List<ReadOp>(ops.Count);
        for (int i = 0; i < set.HeadCount; i++)
        {
            result.Add(ops[i]);
        }

        foreach (var index in order)
        {
            var segment = set.Segments[index];
            for (int i = 0; i < segment.Count; i++)
            {
                result.Add(ops[segment.Start + i]);
            }
        }

        return result;
    }

    public static bool IsIdentity(IReadOnlyList<int>? order, int count)
    {
        if (order is null || order.Count != count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (order[i] != i)
            {
                return false;
            }
        }

        return true;
    }
}
