using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Plan.Orbit;

public static class OrbitApply
{
    private sealed class ReadOpPathComparer : IEqualityComparer<ReadOp>
    {
        public bool Equals(ReadOp left, ReadOp right) => left.Offset == right.Offset && left.Length == right.Length
            && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOp operation) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(operation.Path), operation.Offset, operation.Length);
    }

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

    /// JSON run 의 op 순서를 incumbent 세그먼트 순열로 복원합니다. 각 세그먼트 안의 op 순서는 바뀌지 않아야 합니다.
    internal static bool TryGetOrder(OrbitSegmentSet set, IReadOnlyList<ReadOp> orderedOps, out int[] order, out string reason)
    {
        order = [];
        reason = string.Empty;
        if (set is null || orderedOps is null || orderedOps.Count != set.Ops.Count
            || set.HeadCount < 0 || set.HeadCount > set.Ops.Count)
        {
            reason = "length";
            return false;
        }

        var comparer = new ReadOpPathComparer();
        for (int index = 0; index < set.HeadCount; index++)
        {
            if (!comparer.Equals(set.Ops[index], orderedOps[index]))
            {
                reason = "head";
                return false;
            }
        }

        var seenSegments = new bool[set.Segments.Count];
        for (int segmentIndex = 0; segmentIndex < set.Segments.Count; segmentIndex++)
        {
            OrbitSegment segment = set.Segments[segmentIndex];
            if (segment.Start < set.HeadCount || segment.Count <= 0 || segment.Start > set.Ops.Count - segment.Count
                || segmentIndex > 0 && set.Segments[segmentIndex - 1].Start + set.Segments[segmentIndex - 1].Count != segment.Start)
            {
                reason = "segment_range";
                return false;
            }
        }

        var result = new List<int>(set.Segments.Count);
        int orderedIndex = set.HeadCount;
        while (orderedIndex < orderedOps.Count)
        {
            int matchedSegment = -1;
            for (int segmentIndex = 0; segmentIndex < set.Segments.Count; segmentIndex++)
            {
                OrbitSegment segment = set.Segments[segmentIndex];
                if (seenSegments[segmentIndex] || !comparer.Equals(set.Ops[segment.Start], orderedOps[orderedIndex])
                    || segment.Count > orderedOps.Count - orderedIndex)
                {
                    continue;
                }

                bool matches = true;
                for (int offset = 1; offset < segment.Count; offset++)
                {
                    if (!comparer.Equals(set.Ops[segment.Start + offset], orderedOps[orderedIndex + offset]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    matchedSegment = segmentIndex;
                    break;
                }
            }

            if (matchedSegment < 0)
            {
                reason = $"segment_sequence_{orderedIndex}_{result.Count}";
                return false;
            }

            seenSegments[matchedSegment] = true;
            result.Add(matchedSegment);
            orderedIndex += set.Segments[matchedSegment].Count;
        }

        if (result.Count != set.Segments.Count || seenSegments.Any(seen => !seen)
            || set.Segments.Count > 0 && set.Segments[^1].Start + set.Segments[^1].Count != set.Ops.Count)
        {
            reason = "segment_missing";
            return false;
        }

        order = [.. result];
        return true;
    }

    /// Trace 원본이 빠진 재채점 사본에서는 B 의 보존 접두와 세그먼트 순열로 head 크기를 복원합니다.
    internal static bool TryInferHeadBytes(IReadOnlyList<ReadOp> incumbent, IReadOnlyList<ReadOp> candidate,
        long defaultHeadBytes, out long inferredHeadBytes, out string reason)
    {
        inferredHeadBytes = defaultHeadBytes;
        reason = string.Empty;
        if (incumbent.Count != candidate.Count)
        {
            reason = "length";
            return false;
        }

        var comparer = new ReadOpPathComparer();
        int commonPrefix = 0;
        while (commonPrefix < incumbent.Count && comparer.Equals(incumbent[commonPrefix], candidate[commonPrefix]))
        {
            commonPrefix++;
        }

        OrbitSegmentSet defaultSet = OrbitSegments.Build(incumbent, defaultHeadBytes);
        if (TryGetOrder(defaultSet, candidate, out _, out _))
        {
            return true;
        }

        if (commonPrefix == incumbent.Count)
        {
            return false;
        }

        var prefixBytes = new long[commonPrefix + 1];
        for (int index = 0; index < commonPrefix; index++)
        {
            prefixBytes[index + 1] = checked(prefixBytes[index] + Math.Max(0, incumbent[index].Length));
        }

        long? matchedHeadBytes = null;
        for (int headCount = commonPrefix; headCount >= 0; headCount--)
        {
            long headBytes = prefixBytes[headCount];
            if (headBytes == defaultHeadBytes)
            {
                continue;
            }

            OrbitSegmentSet set = OrbitSegments.Build(incumbent, headBytes);
            if (set.Segments.Count < 2 || !TryGetOrder(set, candidate, out _, out _))
            {
                continue;
            }

            if (matchedHeadBytes is not null)
            {
                reason = "ambiguous_head";
                return false;
            }

            matchedHeadBytes = headBytes;
        }

        if (matchedHeadBytes is not { } match)
        {
            reason = "no_head_match";
            return false;
        }

        inferredHeadBytes = match;
        return true;
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
