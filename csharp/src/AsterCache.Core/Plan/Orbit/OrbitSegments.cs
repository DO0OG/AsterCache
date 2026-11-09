using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Plan.Orbit;

/// 파일의 연속 논리 블록 범위 하나입니다.
public sealed record OrbitBlockRange(string Path, long StartBlock, long Count);

/// L 안의 연속 구간 하나. Position 은 구간 첫 op 앞까지의 누적 바이트(고정 접두 포함).
public sealed record OrbitSegment(int Start, int Count, long Bytes, long Position, int InnerGaps);

public sealed class OrbitSegmentSet
{
    public OrbitSegmentSet(IReadOnlyList<ReadOp> ops, int headCount, long headBytes, IReadOnlyList<OrbitSegment> segments)
    {
        Ops = ops;
        HeadCount = headCount;
        HeadBytes = headBytes;
        Segments = segments;
    }

    public IReadOnlyList<ReadOp> Ops { get; }

    public int HeadCount { get; }

    public long HeadBytes { get; }

    public IReadOnlyList<OrbitSegment> Segments { get; }
}

public static class OrbitSegments
{
    public static OrbitSegmentSet Build(IReadOnlyList<ReadOp> ops, long headBytes, long segmentBytes = Orbit3Constants.SegmentBytes)
    {
        long total = 0;
        foreach (var op in ops)
        {
            total += Math.Max(0, op.Length);
        }

        // 경계를 걸친 op 도 접두에 넣습니다.
        int headCount = 0;
        long head = 0;
        while (headBytes > 0 && headCount < ops.Count && head < headBytes)
        {
            head += Math.Max(0, ops[headCount].Length);
            headCount++;
        }

        var segments = new List<OrbitSegment>();
        long tail = total - head;
        if (tail > 0 && segmentBytes > 0)
        {
            long k = Math.Clamp((tail + segmentBytes - 1) / segmentBytes, 1, Orbit3Constants.MaxTailSegments);
            long acc = 0;
            long cut = 1;
            int start = headCount;
            long segBytes = 0;
            long position = head;
            int gaps = 0;
            for (int i = headCount; i < ops.Count; i++)
            {
                var op = ops[i];
                if (i > start && (!string.Equals(ops[i - 1].Path, op.Path, StringComparison.Ordinal)
                    || op.Offset != ops[i - 1].Offset + ops[i - 1].Length))
                {
                    gaps++;
                }

                long len = Math.Max(0, op.Length);
                acc += len;
                segBytes += len;
                if (acc * k >= cut * tail)
                {
                    segments.Add(new OrbitSegment(start, i + 1 - start, segBytes, position, gaps));
                    while (cut <= k && acc * k >= cut * tail)
                    {
                        cut++;
                    }

                    start = i + 1;
                    position = head + acc;
                    segBytes = 0;
                    gaps = 0;
                }
            }

            // 남은 영 길이 op 는 마지막 세그먼트에 붙여 L 을 빈틈없이 덮고, 그 불연속도 셉니다.
            if (start < ops.Count && segments.Count > 0)
            {
                var last = segments[^1];
                int tailGaps = 0;
                for (int i = start; i < ops.Count; i++)
                {
                    if (!string.Equals(ops[i - 1].Path, ops[i].Path, StringComparison.Ordinal)
                        || ops[i].Offset != ops[i - 1].Offset + ops[i - 1].Length)
                    {
                        tailGaps++;
                    }
                }

                segments[^1] = last with { Count = ops.Count - last.Start, InnerGaps = last.InnerGaps + tailGaps };
            }
        }

        return new OrbitSegmentSet(ops, headCount, head, segments);
    }
}
