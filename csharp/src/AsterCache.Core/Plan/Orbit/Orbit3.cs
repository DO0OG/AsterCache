using System.Diagnostics;
using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Plan.Orbit;

public sealed record Orbit3Device(
    double SeekMs,
    double BytesPerMs,
    double SeekMin,
    double SeekMax,
    double ThroughputMin,
    double ThroughputMax);

public sealed record Orbit3Episode(
    IReadOnlyList<(int Segment, long Bytes)> Parts,
    double PLow,
    double PHat,
    double PHigh);

public sealed record Orbit3BlockMapping(
    IReadOnlyList<(int Segment, long Bytes)> Parts,
    long HeadBytes,
    long Blocks,
    long Unmatched);

public sealed record Orbit3Input(
    OrbitSegmentSet Set,
    IReadOnlyList<Orbit3Episode> Episodes,
    double LambdaLow,
    double LambdaHat,
    double LambdaHigh,
    Orbit3Device? Device,
    double ModelConfidence);

public sealed record Orbit3Decision(
    IReadOnlyList<int>? Order,
    string Reason,
    double? DLow,
    double NominalGain,
    int Candidates,
    long ElapsedMicros,
    double? WorstEpisodeDelayMs = null);

public static class Orbit3
{
    private const int DevicePartitions = 4;
    private const int BeamWidth = 8;
    private const int BeamExpansionLimit = 360;
    private const int EpisodeLimit = 96;
    private const int EpisodeSafetyLimit = 4096;
    private const long BlockBytes = 64L << 10;

    private sealed record EpisodeData((int Segment, double Weight)[] Parts, double PLow, double PHat, double PHigh,
        double TotalBytes, int LastSegment, int SourceIndex);

    private sealed record BlockOperation(long Start, long End, int Segment, bool IsHead, int Priority);

    private sealed record IncumbentCondition(
        double CandidateSeek,
        double CandidateThroughput,
        double IncumbentSeek,
        double IncumbentThroughput,
        double[] IncumbentStarts,
        double[] IncumbentMatchedEnds);

    private static readonly IComparer<BlockOperation> PriorityComparer =
        Comparer<BlockOperation>.Create((left, right) => left.Priority.CompareTo(right.Priority));

    private sealed record PathOperationIndex(
        BlockOperation[] Operations,
        long[] PrefixMaxEnds,
        BlockOperation[] UnmappedOperations,
        long[] UnmappedPrefixMaxEnds);

    private sealed record Candidate(int[] Order, double NominalGain, double DLow, double WorstEpisodeDelayMs, bool EpisodeRegression);

    private sealed record BeamState(int[] Prefix, bool[] Used, double Score);

    private struct CompensatedSum
    {
        private double _sum;
        private double _correction;

        public void Add(double value)
        {
            double adjusted = value - _correction;
            double next = _sum + adjusted;
            _correction = (next - _sum) - adjusted;
            _sum = next;
        }

        public readonly double Value => _sum;
    }

    public static Orbit3Decision Decide(Orbit3Input input, CancellationToken cancel = default) =>
        DecideCore(input, cancel, false, null, Orbit3Constants.DecisionBudgetMs, null);

    /// 안전 하한과 관계없이 벤치 후보로 쓸 명목 순서를 고릅니다.
    public static Orbit3Decision DecideCandidate(Orbit3Input input, CancellationToken cancel = default) =>
        DecideCore(input, cancel, true, null, Orbit3Constants.DecisionBudgetMs, null);

    internal static Orbit3Decision DecideCandidateMeasured(Orbit3Input input, CancellationToken cancel, Action<string, double> reportStage) =>
        DecideCore(input, cancel, true, reportStage, Orbit3Constants.DecisionBudgetMs, null);

    internal static Orbit3Decision DecideMeasured(Orbit3Input input, CancellationToken cancel, int decisionBudgetMs,
        Action<string, double> reportStage) => DecideCore(input, cancel, false, reportStage, decisionBudgetMs, null);

    internal static Orbit3Decision DecideOrderMeasured(Orbit3Input input, IReadOnlyList<int> order,
        CancellationToken cancel, int decisionBudgetMs, Action<string, double> reportStage) =>
        DecideCore(input, cancel, false, reportStage, decisionBudgetMs, [.. order]);

    private static Orbit3Decision DecideCore(Orbit3Input input, CancellationToken cancel, bool forceCandidate,
        Action<string, double>? reportStage, int decisionBudgetMs, int[]? explicitOrder)
    {
        long started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(decisionBudgetMs);
        CancellationToken token = budget.Token;

        try
        {
            token.ThrowIfCancellationRequested();
            if (input?.Episodes is { Count: > EpisodeSafetyLimit })
            {
                return Result(null, "too_many_episodes", null, 0, 0, started);
            }

            long prepareStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
            bool valid = TryPrepare(input, token, out OrbitSegmentSet? set, out Orbit3Device? device,
                out EpisodeData[]? candidateEpisodes, out EpisodeData[]? episodes);
            if (reportStage is not null)
            {
                reportStage("input_preparation", Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds);
            }
            EnsureWithinBudget(token, started, decisionBudgetMs);
            if (!valid)
            {
                return Result(null, "invalid_input", null, 0, 0, started);
            }

            if (episodes!.Length == 0)
            {
                return Result(null, "no_episodes", 0, 0, 0, started);
            }

            int segmentCount = set!.Segments.Count;
            if (explicitOrder is not null && !IsPermutation(explicitOrder, segmentCount))
            {
                return Result(null, "invalid_candidate_order", null, 0, 0, started);
            }

            int[] incumbent = Enumerable.Range(0, segmentCount).ToArray();
            var orders = new List<int[]>();
            long candidateStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
            if (explicitOrder is not null)
            {
                orders.Add(explicitOrder);
            }
            else
            {
                AddCandidate(orders, DensityOrder(set, candidateEpisodes!, device!, token));
                AddCandidate(orders, EpisodeOrder(set, candidateEpisodes!, device!, token));
                foreach (int[] order in BeamOrders(set, candidateEpisodes!, input!.LambdaHat, device!, token))
                {
                    AddCandidate(orders, order);
                }
            }
            if (reportStage is not null)
            {
                reportStage("candidate_generation", Stopwatch.GetElapsedTime(candidateStarted).TotalMilliseconds);
            }

            EnsureWithinBudget(token, started, decisionBudgetMs);
            long incumbentStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
            IncumbentCondition[] incumbentConditions = ComputeIncumbentConditions(set!, device!, token);
            if (reportStage is not null)
            {
                reportStage("incumbent_condition_setup", Stopwatch.GetElapsedTime(incumbentStarted).TotalMilliseconds);
            }

            var evaluated = new List<Candidate>(orders.Count);
            double? bestBound = null;
            double? worstRejectedEpisodeDelay = null;
            double? worstEvaluatedEpisodeDelay = null;
            bool regressionRejected = false;
            double nominalTotalMs = 0;
            double lowerBoundTotalMs = 0;
            foreach (int[] order in orders)
            {
                EnsureWithinBudget(token, started, decisionBudgetMs);
                long nominalStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
                double nominalGain = NominalValue(set, episodes, order, input!.LambdaHat, device!, token)
                    - NominalValue(set, episodes, incumbent, input.LambdaHat, device!, token);
                if (reportStage is not null)
                {
                    nominalTotalMs += Stopwatch.GetElapsedTime(nominalStarted).TotalMilliseconds;
                }

                long lowerBoundStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
                double dLow = LowerBound(set, episodes!, order, input.LambdaLow, input.LambdaHigh, device!, incumbentConditions, token,
                    out double worstEpisodeDelayMs, out bool episodeRegression, reportStage);
                if (reportStage is not null)
                {
                    lowerBoundTotalMs += Stopwatch.GetElapsedTime(lowerBoundStarted).TotalMilliseconds;
                }
                EnsureWithinBudget(token, started, decisionBudgetMs);
                if (!double.IsFinite(nominalGain) || !double.IsFinite(dLow))
                {
                    throw new ArithmeticException("계산 결과가 유한하지 않습니다.");
                }

                evaluated.Add(new Candidate(order, nominalGain, dLow, worstEpisodeDelayMs, episodeRegression));
                worstEvaluatedEpisodeDelay = Math.Max(worstEvaluatedEpisodeDelay ?? double.NegativeInfinity, worstEpisodeDelayMs);
                if (bestBound is null || dLow > bestBound.Value)
                {
                    bestBound = dLow;
                }
            }

            if (reportStage is not null)
            {
                reportStage("nominal_evaluation", nominalTotalMs);
                reportStage("d_low_evaluation", lowerBoundTotalMs);
            }

            Candidate? best = null;
            foreach (Candidate candidate in evaluated)
            {
                if (explicitOrder is not null && candidate.EpisodeRegression)
                {
                    regressionRejected = true;
                    worstRejectedEpisodeDelay = candidate.WorstEpisodeDelayMs;
                    continue;
                }

                // 사건이 0회이면 LambdaLow=0이라 D_low<=0이 되어 기준 순서를 유지합니다.
                if (!forceCandidate && (candidate.DLow <= 0 || candidate.NominalGain <= 0))
                {
                    continue;
                }

                if (!forceCandidate && candidate.EpisodeRegression)
                {
                    regressionRejected = true;
                    worstRejectedEpisodeDelay = Math.Max(worstRejectedEpisodeDelay ?? double.NegativeInfinity, candidate.WorstEpisodeDelayMs);
                    continue;
                }

                if (best is null || IsBetter(candidate, best))
                {
                    best = candidate;
                }
            }

            if (best is null)
            {
                EnsureWithinBudget(token, started, decisionBudgetMs);
                return Result(null, regressionRejected ? "episode_regression" : "no_safe_gain", bestBound, 0,
                    evaluated.Count, started, worstRejectedEpisodeDelay
                        ?? (explicitOrder is null ? null : worstEvaluatedEpisodeDelay));
            }

            EnsureWithinBudget(token, started, decisionBudgetMs);
            return Result(Array.AsReadOnly(best.Order), forceCandidate ? "forced_candidate" : "safe_gain", best.DLow,
                best.NominalGain, evaluated.Count, started, best.WorstEpisodeDelayMs);
        }
        catch (OperationCanceledException)
        {
            string reason = cancel.IsCancellationRequested ? "cancelled" : "timeout";
            return Result(null, reason, null, 0, 0, started);
        }
        catch (Exception)
        {
            string reason = IsBudgetExpired(started, decisionBudgetMs) ? "timeout" : "calculation_error";
            return Result(null, reason, null, 0, 0, started);
        }
    }

    public static (IReadOnlyList<(int Segment, long Bytes)> Parts, long HeadBytes, int Unmatched) MapBlocks(
        OrbitSegmentSet set,
        IEnumerable<(string Path, long Block)> blocks,
        CancellationToken cancel = default)
    {
        if (set is null || blocks is null)
        {
            return (Array.Empty<(int Segment, long Bytes)>(), 0, 0);
        }

        bool hasKnownCount = blocks.TryGetNonEnumeratedCount(out int knownCount);
        if (cancel.IsCancellationRequested && hasKnownCount)
        {
            return (Array.Empty<(int Segment, long Bytes)>(), 0, knownCount);
        }

        var allBlocks = new List<(string Path, long Block)>();
        foreach ((string path, long block) in blocks)
        {
            allBlocks.Add((path, block));
            if ((allBlocks.Count & 255) == 0 && cancel.IsCancellationRequested && hasKnownCount)
            {
                return (Array.Empty<(int Segment, long Bytes)>(), 0, knownCount);
            }
        }

        if (cancel.IsCancellationRequested)
        {
            return (Array.Empty<(int Segment, long Bytes)>(), 0, allBlocks.Count);
        }

        Dictionary<string, PathOperationIndex> pathIndexes;
        try
        {
            pathIndexes = BuildPathIndexes(set, cancel);
        }
        catch (OperationCanceledException)
        {
            return (Array.Empty<(int Segment, long Bytes)>(), 0, allBlocks.Count);
        }

        var segmentBytes = new long[set.Segments.Count];
        var seen = new HashSet<(string Path, long Block)>();
        long headBytes = 0;
        int unmatched = 0;

        foreach ((string path, long block) in allBlocks)
        {
            if (cancel.IsCancellationRequested)
            {
                return (Array.Empty<(int Segment, long Bytes)>(), 0, allBlocks.Count);
            }

            if (!seen.Add((path, block)))
            {
                continue;
            }

            if (string.IsNullOrEmpty(path) || block < 0 || block > long.MaxValue / BlockBytes)
            {
                unmatched++;
                continue;
            }

            long blockStart = block * BlockBytes;
            long blockEnd = blockStart > long.MaxValue - BlockBytes ? long.MaxValue : blockStart + BlockBytes;
            if (!pathIndexes.TryGetValue(path, out PathOperationIndex? pathIndex))
            {
                unmatched++;
                continue;
            }

            bool mapped = false;
            int first = FirstPossibleOverlap(pathIndex.PrefixMaxEnds, blockStart);
            for (int operationIndex = first; operationIndex < pathIndex.Operations.Length; operationIndex++)
            {
                BlockOperation operation = pathIndex.Operations[operationIndex];
                if (operation.Start >= blockEnd)
                {
                    break;
                }

                if (operation.End <= blockStart)
                {
                    continue;
                }

                long overlap = Math.Min(blockEnd, operation.End) - Math.Max(blockStart, operation.Start);
                if (overlap <= 0)
                {
                    continue;
                }

                mapped = true;
                if (operation.IsHead)
                {
                    headBytes = checked(headBytes + overlap);
                }
                else if (operation.Segment >= 0)
                {
                    segmentBytes[operation.Segment] = checked(segmentBytes[operation.Segment] + overlap);
                }
            }

            bool hasUnmappedOperation = HasOverlap(pathIndex.UnmappedOperations, pathIndex.UnmappedPrefixMaxEnds, blockStart, blockEnd);
            if (!mapped || hasUnmappedOperation)
            {
                unmatched++;
            }
        }

        var parts = segmentBytes
            .Select((bytes, segment) => (Segment: segment, Bytes: bytes))
            .Where(part => part.Bytes > 0)
            .ToArray();
        return (Array.AsReadOnly(parts), headBytes, unmatched);
    }

    public static IReadOnlyList<Orbit3BlockMapping> MapBlockRanges(
        OrbitSegmentSet set,
        IReadOnlyList<IReadOnlyList<OrbitBlockRange>> episodes,
        CancellationToken cancel = default) => MapBlockRangesCore(set, episodes, cancel, null);

    internal static IReadOnlyList<Orbit3BlockMapping> MapBlockRangesMeasured(
        OrbitSegmentSet set,
        IReadOnlyList<IReadOnlyList<OrbitBlockRange>> episodes,
        CancellationToken cancel,
        Action<string, double> reportStage) => MapBlockRangesCore(set, episodes, cancel, reportStage);

    private static IReadOnlyList<Orbit3BlockMapping> MapBlockRangesCore(
        OrbitSegmentSet set,
        IReadOnlyList<IReadOnlyList<OrbitBlockRange>> episodes,
        CancellationToken cancel,
        Action<string, double>? reportStage)
    {
        if (set is null || episodes is null)
        {
            return Array.Empty<Orbit3BlockMapping>();
        }

        cancel.ThrowIfCancellationRequested();
        Dictionary<string, PathOperationIndex> pathIndexes = BuildPathIndexes(set, cancel, reportStage);

        long mappingStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
        var results = new List<Orbit3BlockMapping>(episodes.Count);
        foreach (IReadOnlyList<OrbitBlockRange> ranges in episodes)
        {
            cancel.ThrowIfCancellationRequested();
            var segmentBytes = new long[set.Segments.Count];
            var covered = new List<(long Start, long End)>();
            var unmapped = new List<(long Start, long End)>();
            long headBytes = 0;
            long totalBlocks = 0;
            long notMatched = 0;

            foreach (OrbitBlockRange range in ranges)
            {
                cancel.ThrowIfCancellationRequested();
                if (range.Count <= 0)
                {
                    continue;
                }

                totalBlocks = checked(totalBlocks + range.Count);
                long maxStartBlock = long.MaxValue / BlockBytes;
                if (string.IsNullOrEmpty(range.Path) || range.StartBlock < 0 || range.StartBlock > maxStartBlock
                    || range.Count > maxStartBlock - range.StartBlock + 1)
                {
                    notMatched = checked(notMatched + range.Count);
                    continue;
                }

                long blockEnd = range.StartBlock + range.Count;
                long blockStartBytes = range.StartBlock * BlockBytes;
                long blockEndBytes = blockEnd > maxStartBlock ? long.MaxValue : blockEnd * BlockBytes;
                if (!pathIndexes.TryGetValue(range.Path, out PathOperationIndex? pathIndex))
                {
                    notMatched = checked(notMatched + range.Count);
                    continue;
                }

                covered.Clear();
                unmapped.Clear();
                int first = FirstPossibleOverlap(pathIndex.PrefixMaxEnds, blockStartBytes);
                for (int operationIndex = first; operationIndex < pathIndex.Operations.Length; operationIndex++)
                {
                    if ((operationIndex & 255) == 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                    }

                    BlockOperation operation = pathIndex.Operations[operationIndex];
                    if (operation.Start >= blockEndBytes)
                    {
                        break;
                    }

                    long overlap = Math.Min(blockEndBytes, operation.End) - Math.Max(blockStartBytes, operation.Start);
                    if (overlap <= 0)
                    {
                        continue;
                    }

                    long firstBlock = Math.Max(range.StartBlock, operation.Start / BlockBytes);
                    long lastBlockExclusive = Math.Min(blockEnd, (operation.End - 1) / BlockBytes + 1);
                    if (operation.IsHead)
                    {
                        headBytes = checked(headBytes + overlap);
                        AddMergedCoverage(covered, firstBlock, lastBlockExclusive);
                    }
                    else
                    {
                        segmentBytes[operation.Segment] = checked(segmentBytes[operation.Segment] + overlap);
                        AddMergedCoverage(covered, firstBlock, lastBlockExclusive);
                    }
                }

                int firstUnmapped = FirstPossibleOverlap(pathIndex.UnmappedPrefixMaxEnds, blockStartBytes);
                for (int operationIndex = firstUnmapped; operationIndex < pathIndex.UnmappedOperations.Length; operationIndex++)
                {
                    if ((operationIndex & 255) == 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                    }

                    BlockOperation operation = pathIndex.UnmappedOperations[operationIndex];
                    if (operation.Start >= blockEndBytes)
                    {
                        break;
                    }

                    long firstBlock = Math.Max(range.StartBlock, operation.Start / BlockBytes);
                    long lastBlockExclusive = Math.Min(blockEnd, (operation.End - 1) / BlockBytes + 1);
                    AddMergedCoverage(unmapped, firstBlock, lastBlockExclusive);
                }

                long mappedBlocks = CoverageCount(covered);
                if (unmapped.Count > 0)
                {
                    mappedBlocks -= CoverageIntersection(covered, unmapped);
                }

                notMatched = checked(notMatched + range.Count - mappedBlocks);
            }

            var parts = segmentBytes
                .Select((bytes, segment) => (Segment: segment, Bytes: bytes))
                .Where(part => part.Bytes > 0)
                .ToArray();
            results.Add(new Orbit3BlockMapping(Array.AsReadOnly(parts), headBytes, totalBlocks, notMatched));
        }

        if (reportStage is not null)
        {
            reportStage("episode_block_mapping", Stopwatch.GetElapsedTime(mappingStarted).TotalMilliseconds);
        }

        return results;
    }

    private static Dictionary<string, PathOperationIndex> BuildPathIndexes(
        OrbitSegmentSet set,
        CancellationToken cancel,
        Action<string, double>? reportStage = null)
    {
        var operationSegments = new int[set.Ops.Count];
        Array.Fill(operationSegments, -1);
        for (int segmentIndex = 0; segmentIndex < set.Segments.Count; segmentIndex++)
        {
            cancel.ThrowIfCancellationRequested();
            OrbitSegment segment = set.Segments[segmentIndex];
            if (segment.Start < 0 || segment.Count <= 0 || segment.Start >= set.Ops.Count)
            {
                continue;
            }

            int end = segment.Start + Math.Min(segment.Count, set.Ops.Count - segment.Start);
            for (int operationIndex = segment.Start; operationIndex < end; operationIndex++)
            {
                operationSegments[operationIndex] = segmentIndex;
            }
        }

        var grouped = new Dictionary<string, List<BlockOperation>>(StringComparer.Ordinal);
        for (int operationIndex = 0; operationIndex < set.Ops.Count; operationIndex++)
        {
            if ((operationIndex & 255) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }

            ReadOp operation = set.Ops[operationIndex];
            if (string.IsNullOrEmpty(operation.Path) || operation.Offset < 0 || operation.Length <= 0)
            {
                continue;
            }

            long end = operation.Offset > long.MaxValue - operation.Length
                ? long.MaxValue
                : operation.Offset + operation.Length;
            if (end <= operation.Offset)
            {
                continue;
            }

            bool isHead = operationIndex < set.HeadCount;
            int segment = isHead ? -1 : operationSegments[operationIndex];
            if (!grouped.TryGetValue(operation.Path, out List<BlockOperation>? pathOperations))
            {
                pathOperations = [];
                grouped.Add(operation.Path, pathOperations);
            }

            pathOperations.Add(new BlockOperation(operation.Offset, end, segment, isHead, operationIndex));
        }

        var indexes = new Dictionary<string, PathOperationIndex>(StringComparer.Ordinal);
        long uniqueCoverageStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
        foreach ((string path, List<BlockOperation> operations) in grouped)
        {
            cancel.ThrowIfCancellationRequested();
            BlockOperation[] mapped = UniqueCoverage(operations, mapped: true, cancel);
            BlockOperation[] unmapped = UniqueCoverage(operations, mapped: false, cancel);
            (BlockOperation[] sortedMapped, long[] mappedEnds) = MakeIndex(mapped);
            (BlockOperation[] sortedUnmapped, long[] unmappedEnds) = MakeIndex(unmapped);
            indexes.Add(path, new PathOperationIndex(sortedMapped, mappedEnds, sortedUnmapped, unmappedEnds));
        }

        if (reportStage is not null)
        {
            reportStage("unique_coverage", Stopwatch.GetElapsedTime(uniqueCoverageStarted).TotalMilliseconds);
        }

        return indexes;
    }

    /// 겹친 범위는 입력 op 순서에서 먼저 나온 유효 op에 한 번만 배정합니다. head 는 처음부터 그 순서에 있어 우선 보호됩니다.
    private static BlockOperation[] UniqueCoverage(List<BlockOperation> source, bool mapped, CancellationToken cancel)
    {
        int operationCount = 0;
        foreach (BlockOperation operation in source)
        {
            if (mapped ? operation.IsHead || operation.Segment >= 0 : !operation.IsHead && operation.Segment < 0)
            {
                operationCount++;
            }
        }

        if (operationCount == 0)
        {
            return [];
        }

        var events = new List<(long Position, bool Starting, BlockOperation Operation)>(operationCount * 2);
        foreach (BlockOperation operation in source)
        {
            if (!(mapped ? operation.IsHead || operation.Segment >= 0 : !operation.IsHead && operation.Segment < 0))
            {
                continue;
            }

            cancel.ThrowIfCancellationRequested();
            events.Add((operation.Start, true, operation));
            events.Add((operation.End, false, operation));
        }

        events.Sort((left, right) => left.Position.CompareTo(right.Position));
        var active = new SortedSet<BlockOperation>(PriorityComparer);
        var result = new List<BlockOperation>(operationCount);
        long previous = events[0].Position;
        int index = 0;
        while (index < events.Count)
        {
            if ((index & 255) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }

            long position = events[index].Position;
            if (position > previous && active.Count > 0)
            {
                BlockOperation owner = active.Min!;
                AppendUnique(result, previous, position, owner);
            }

            int groupEnd = index + 1;
            while (groupEnd < events.Count && events[groupEnd].Position == position)
            {
                groupEnd++;
            }

            for (int eventIndex = index; eventIndex < groupEnd; eventIndex++)
            {
                if (!events[eventIndex].Starting)
                {
                    _ = active.Remove(events[eventIndex].Operation);
                }
            }

            for (int eventIndex = index; eventIndex < groupEnd; eventIndex++)
            {
                if (events[eventIndex].Starting)
                {
                    _ = active.Add(events[eventIndex].Operation);
                }
            }

            previous = position;
            index = groupEnd;
        }

        return [.. result];
    }

    private static void AppendUnique(List<BlockOperation> ranges, long start, long end, BlockOperation owner)
    {
        if (ranges.Count > 0 && ranges[^1].End == start
            && ranges[^1].Segment == owner.Segment && ranges[^1].IsHead == owner.IsHead)
        {
            ranges[^1] = ranges[^1] with { End = end };
        }
        else
        {
            ranges.Add(owner with { Start = start, End = end });
        }
    }

    private static (BlockOperation[] Operations, long[] PrefixMaxEnds) MakeIndex(BlockOperation[] operations)
    {
        // UniqueCoverage sweep already emits disjoint ranges in ascending start order.
        var prefixMaxEnds = new long[operations.Length];
        long maxEnd = long.MinValue;
        for (int index = 0; index < operations.Length; index++)
        {
            maxEnd = Math.Max(maxEnd, operations[index].End);
            prefixMaxEnds[index] = maxEnd;
        }

        return (operations, prefixMaxEnds);
    }

    private static bool HasOverlap(BlockOperation[] operations, long[] prefixMaxEnds, long start, long end)
    {
        int first = FirstPossibleOverlap(prefixMaxEnds, start);
        return first < operations.Length && operations[first].Start < end;
    }

    private static void AddMergedCoverage(List<(long Start, long End)> ranges, long start, long end)
    {
        if (end <= start)
        {
            return;
        }

        if (ranges.Count == 0 || start > ranges[^1].End)
        {
            ranges.Add((start, end));
            return;
        }

        if (end > ranges[^1].End)
        {
            ranges[^1] = (ranges[^1].Start, end);
        }
    }

    private static long CoverageCount(IReadOnlyList<(long Start, long End)> ranges)
    {
        long count = 0;
        foreach ((long start, long end) in ranges)
        {
            count = checked(count + end - start);
        }

        return count;
    }

    private static long CoverageIntersection(IReadOnlyList<(long Start, long End)> left, IReadOnlyList<(long Start, long End)> right)
    {
        long count = 0;
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            (long leftStart, long leftEnd) = left[leftIndex];
            (long rightStart, long rightEnd) = right[rightIndex];
            count = checked(count + Math.Max(0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart)));
            if (leftEnd <= rightEnd)
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return count;
    }

    private static int FirstPossibleOverlap(long[] prefixMaxEnds, long blockStart)
    {
        int low = 0;
        int high = prefixMaxEnds.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (prefixMaxEnds[middle] <= blockStart)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    internal static double MinimumExponentialDifference(double lambdaLow, double lambdaHigh, double candidateTime, double incumbentTime)
    {
        double minimum = ExponentialDifference(lambdaLow, candidateTime, incumbentTime);
        minimum = Math.Min(minimum, ExponentialDifference(lambdaHigh, candidateTime, incumbentTime));

        if (candidateTime > 0 && incumbentTime > 0 && candidateTime != incumbentTime)
        {
            double difference = candidateTime - incumbentTime;
            double stationary = Math.Abs(difference) <= Math.Max(candidateTime, incumbentTime) * 1e-8
                ? 1 / (candidateTime * 0.5 + incumbentTime * 0.5)
                : (Math.Log(candidateTime) - Math.Log(incumbentTime)) / difference;
            if (double.IsFinite(stationary) && stationary >= lambdaLow && stationary <= lambdaHigh)
            {
                minimum = Math.Min(minimum, ExponentialDifference(stationary, candidateTime, incumbentTime));
            }
        }

        return LowerRounded(minimum);
    }

    private static bool TryPrepare(
        Orbit3Input? input,
        CancellationToken token,
        out OrbitSegmentSet? set,
        out Orbit3Device? device,
        out EpisodeData[]? candidateEpisodes,
        out EpisodeData[]? episodes)
    {
        set = null;
        device = null;
        candidateEpisodes = null;
        episodes = null;
        if (input is null || input.Set is null || input.Episodes is null || input.Device is null)
        {
            return false;
        }

        OrbitSegmentSet candidateSet = input.Set;
        Orbit3Device candidateDevice = input.Device;
        IReadOnlyList<OrbitSegment>? candidateSegments = candidateSet.Segments;
        IReadOnlyList<ReadOp>? candidateOps = candidateSet.Ops;
        if (candidateSegments is null || candidateOps is null)
        {
            return false;
        }

        int segmentCount = candidateSegments.Count;
        if (segmentCount is < 2 or > Orbit3Constants.MaxTailSegments
            || candidateSet.HeadCount < 0 || candidateSet.HeadCount > candidateOps.Count
            || candidateSet.HeadBytes < 0
            || input.Episodes.Count == 0
            || !AllFinite(input.LambdaLow, input.LambdaHat, input.LambdaHigh, input.ModelConfidence,
                candidateDevice.SeekMs, candidateDevice.BytesPerMs, candidateDevice.SeekMin,
                candidateDevice.SeekMax, candidateDevice.ThroughputMin, candidateDevice.ThroughputMax)
            || input.LambdaLow < 0 || input.LambdaHat < input.LambdaLow || input.LambdaHigh < input.LambdaHat
            || input.ModelConfidence < Orbit3Constants.MinModelConfidence || input.ModelConfidence > 1
            || candidateDevice.SeekMs < 0 || candidateDevice.BytesPerMs <= 0
            || candidateDevice.SeekMin is < 0 or > 1
            || candidateDevice.SeekMax < 1 || candidateDevice.SeekMax > Orbit3Constants.MaxEnvelopeRatio
            || candidateDevice.ThroughputMin is <= 0 or > 1
            || candidateDevice.ThroughputMax < 1 || candidateDevice.ThroughputMax > Orbit3Constants.MaxEnvelopeRatio)
        {
            return false;
        }

        int previousEnd = candidateSet.HeadCount;
        foreach (OrbitSegment segment in candidateSegments)
        {
            token.ThrowIfCancellationRequested();
            if (segment is null || segment.Start < previousEnd || segment.Start < candidateSet.HeadCount
                || segment.Count <= 0 || segment.Start > candidateOps.Count - segment.Count
                || segment.Bytes < 0 || segment.InnerGaps < 0)
            {
                return false;
            }

            previousEnd = segment.Start + segment.Count;
        }

        var allEpisodes = new List<EpisodeData>(input.Episodes.Count);
        for (int episodeIndex = 0; episodeIndex < input.Episodes.Count; episodeIndex++)
        {
            token.ThrowIfCancellationRequested();
            Orbit3Episode? episode = input.Episodes[episodeIndex];
            if (episode is null || episode.Parts is null
                || !AllFinite(episode.PLow, episode.PHat, episode.PHigh)
                || episode.PLow < 0 || episode.PHigh > 1
                || episode.PLow > episode.PHat || episode.PHat > episode.PHigh)
            {
                return false;
            }

            var partBytes = new Dictionary<int, double>();
            foreach ((int segment, long bytes) in episode.Parts)
            {
                token.ThrowIfCancellationRequested();
                if (segment < 0 || segment >= segmentCount || bytes < 0)
                {
                    return false;
                }

                if (bytes == 0)
                {
                    continue;
                }

                partBytes[segment] = partBytes.GetValueOrDefault(segment) + bytes;
            }

            if (partBytes.Count == 0)
            {
                continue;
            }

            double totalBytes = partBytes.Values.Sum();
            if (!(totalBytes > 0) || !double.IsFinite(totalBytes))
            {
                return false;
            }

            var parts = partBytes
                .OrderBy(part => part.Key)
                .Select(part => (Segment: part.Key, Weight: part.Value / totalBytes))
                .ToArray();
            allEpisodes.Add(new EpisodeData(parts, episode.PLow, episode.PHat, episode.PHigh, totalBytes,
                parts[^1].Segment, episodeIndex));
        }

        episodes = allEpisodes.OrderBy(item => item.SourceIndex).ToArray();
        candidateEpisodes = episodes.Length <= EpisodeLimit
            ? episodes
            : episodes.OrderByDescending(item => item.PHat * item.TotalBytes)
                .ThenBy(item => item.SourceIndex)
                .Take(EpisodeLimit)
                .OrderBy(item => item.SourceIndex)
                .ToArray();
        set = candidateSet;
        device = candidateDevice;
        return true;
    }

    private static int[] DensityOrder(OrbitSegmentSet set, EpisodeData[] episodes, Orbit3Device device, CancellationToken token)
    {
        int count = set.Segments.Count;
        var order = new List<int>(count);
        var remaining = new bool[count];
        Array.Fill(remaining, true);
        int previous = -1;

        for (int position = 0; position < count; position++)
        {
            token.ThrowIfCancellationRequested();
            int best = -1;
            double bestDensity = double.NegativeInfinity;
            for (int segment = 0; segment < count; segment++)
            {
                if (!remaining[segment])
                {
                    continue;
                }

                CompensatedSum value = default;
                foreach (EpisodeData episode in episodes)
                {
                    foreach ((int partSegment, double weight) in episode.Parts)
                    {
                        if (partSegment == segment)
                        {
                            value.Add(episode.PHat * episode.TotalBytes * weight);
                        }
                    }
                }

                double cost = SegmentCost(set.Segments[segment], segment, previous, device, 1, 1);
                double density = cost > 0 ? value.Value / cost : value.Value;
                if (best < 0 || density > bestDensity || density == bestDensity && segment < best)
                {
                    best = segment;
                    bestDensity = density;
                }
            }

            order.Add(best);
            remaining[best] = false;
            previous = best;
        }

        return order.ToArray();
    }

    private static int[] EpisodeOrder(OrbitSegmentSet set, EpisodeData[] episodes, Orbit3Device device, CancellationToken token)
    {
        int count = set.Segments.Count;
        var chosen = new bool[count];
        var order = new List<int>(count);
        var episodeRanks = new List<(EpisodeData Episode, double Ratio)>();
        foreach (EpisodeData episode in episodes)
        {
            token.ThrowIfCancellationRequested();
            int[] required = episode.Parts.Select(part => part.Segment).Order().ToArray();
            double cost = OrderCost(set, required, device);
            episodeRanks.Add((episode, cost > 0 ? episode.PHat / cost : episode.PHat));
        }

        foreach ((EpisodeData episode, _) in episodeRanks
            .OrderByDescending(item => item.Ratio)
            .ThenBy(item => item.Episode.SourceIndex))
        {
            foreach ((int segment, _) in episode.Parts)
            {
                if (!chosen[segment])
                {
                    chosen[segment] = true;
                    order.Add(segment);
                }
            }
        }

        for (int segment = 0; segment < count; segment++)
        {
            if (!chosen[segment])
            {
                order.Add(segment);
            }
        }

        return order.ToArray();
    }

    private static IEnumerable<int[]> BeamOrders(
        OrbitSegmentSet set,
        EpisodeData[] episodes,
        double lambda,
        Orbit3Device device,
        CancellationToken token)
    {
        int count = set.Segments.Count;
        var beam = new List<BeamState> { new([], new bool[count], 0) };
        int expansions = 0;

        while (beam.Count > 0 && expansions < BeamExpansionLimit)
        {
            token.ThrowIfCancellationRequested();
            var next = new List<BeamState>();
            foreach (BeamState state in beam)
            {
                for (int segment = 0; segment < count && expansions < BeamExpansionLimit; segment++)
                {
                    if (state.Used[segment])
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    expansions++;
                    int[] prefix = [.. state.Prefix, segment];
                    var used = (bool[])state.Used.Clone();
                    used[segment] = true;
                    int[] completed = CompleteOrder(prefix, used);
                    double score = NominalValue(set, episodes, completed, lambda, device, token);
                    next.Add(new BeamState(prefix, used, score));
                }
            }

            if (next.Count == 0)
            {
                break;
            }

            next.Sort(CompareBeamStates);
            beam = next.Take(BeamWidth).ToList();
            if (beam[0].Prefix.Length == count || expansions >= BeamExpansionLimit)
            {
                break;
            }
        }

        foreach (BeamState state in beam)
        {
            yield return CompleteOrder(state.Prefix, state.Used);
        }
    }

    private static int CompareBeamStates(BeamState left, BeamState right)
    {
        int scoreOrder = right.Score.CompareTo(left.Score);
        return scoreOrder != 0 ? scoreOrder : CompareOrders(left.Prefix, right.Prefix);
    }

    private static int[] CompleteOrder(int[] prefix, bool[] used)
    {
        var order = new int[used.Length];
        prefix.CopyTo(order, 0);
        int at = prefix.Length;
        for (int segment = 0; segment < used.Length; segment++)
        {
            if (!used[segment])
            {
                order[at++] = segment;
            }
        }

        return order;
    }

    private static void AddCandidate(List<int[]> candidates, int[] order)
    {
        if (OrbitApply.IsIdentity(order, order.Length)
            || candidates.Any(existing => existing.AsSpan().SequenceEqual(order)))
        {
            return;
        }

        candidates.Add(order);
    }

    private static bool IsPermutation(IReadOnlyList<int> order, int count)
    {
        if (order.Count != count)
        {
            return false;
        }

        var seen = new bool[count];
        foreach (int segment in order)
        {
            if (segment < 0 || segment >= count || seen[segment])
            {
                return false;
            }

            seen[segment] = true;
        }

        return true;
    }

    private static double NominalValue(
        OrbitSegmentSet set,
        EpisodeData[] episodes,
        int[] order,
        double lambda,
        Orbit3Device device,
        CancellationToken token)
    {
        double headTime = HeadTime(set, device, 1, 1);
        (double[] starts, double[] ends) = SegmentTimes(set, order, headTime, device, 1, 1, token);
        CompensatedSum total = default;
        foreach (EpisodeData episode in episodes)
        {
            token.ThrowIfCancellationRequested();
            CompensatedSum partial = default;
            double last = 0;
            foreach ((int segment, double weight) in episode.Parts)
            {
                partial.Add(weight * Math.Exp(-lambda * ends[segment]));
                last = Math.Max(last, ends[segment]);
            }

            double average = partial.Value;
            double complete = Math.Exp(-lambda * last);
            total.Add(episode.PHat * (0.5 * average + 0.5 * complete));
        }

        return total.Value;
    }

    private static IncumbentCondition[] ComputeIncumbentConditions(OrbitSegmentSet set, Orbit3Device device, CancellationToken token)
    {
        double[] seekEdges = PartitionEdges(device.SeekMin, device.SeekMax);
        double[] throughputEdges = PartitionEdges(device.ThroughputMin, device.ThroughputMax);
        int[] incumbentOrder = Enumerable.Range(0, set.Segments.Count).ToArray();
        var result = new IncumbentCondition[DevicePartitions * DevicePartitions];

        for (int seekBox = 0; seekBox < DevicePartitions; seekBox++)
        {
            for (int throughputBox = 0; throughputBox < DevicePartitions; throughputBox++)
            {
                token.ThrowIfCancellationRequested();
                double candidateSeek = seekEdges[seekBox + 1];
                double candidateThroughput = throughputEdges[throughputBox];
                double incumbentSeek = seekEdges[seekBox];
                double incumbentThroughput = throughputEdges[throughputBox + 1];
                double matchedHead = HeadTime(set, device, candidateSeek, candidateThroughput);
                (_, double[] matchedEnds) = SegmentTimes(set, incumbentOrder, matchedHead, device,
                    candidateSeek, candidateThroughput, token);
                double incumbentHead = HeadTime(set, device, incumbentSeek, incumbentThroughput);
                (double[] incumbentStarts, _) = SegmentTimes(set, incumbentOrder, incumbentHead, device,
                    incumbentSeek, incumbentThroughput, token);
                result[seekBox * DevicePartitions + throughputBox] = new IncumbentCondition(
                    candidateSeek, candidateThroughput, incumbentSeek, incumbentThroughput, incumbentStarts, matchedEnds);
            }
        }

        return result;
    }

    private static double LowerBound(
        OrbitSegmentSet set,
        EpisodeData[] episodes,
        int[] candidateOrder,
        double lambdaLow,
        double lambdaHigh,
        Orbit3Device device,
        IncumbentCondition[] incumbentConditions,
        CancellationToken token,
        out double worstEpisodeDelayMs,
        out bool episodeRegression,
        Action<string, double>? reportStage)
    {
        int[] candidateLastSegments = LastRequiredSegments(episodes, candidateOrder, set.Segments.Count, token);
        double lower = double.PositiveInfinity;
        long episodeConstraintTicks = 0;
        worstEpisodeDelayMs = double.NegativeInfinity;
        episodeRegression = false;

        foreach (IncumbentCondition condition in incumbentConditions)
        {
            token.ThrowIfCancellationRequested();
            double candidateHead = HeadTime(set, device, condition.CandidateSeek, condition.CandidateThroughput);
            (_, double[] candidateEnds) = SegmentTimes(set, candidateOrder, candidateHead, device,
                condition.CandidateSeek, condition.CandidateThroughput, token);

            CompensatedSum episodeSum = default;
            for (int episodeIndex = 0; episodeIndex < episodes.Length; episodeIndex++)
            {
                EpisodeData episode = episodes[episodeIndex];
                token.ThrowIfCancellationRequested();
                CompensatedSum partial = default;
                foreach ((int segment, double weight) in episode.Parts)
                {
                    partial.Add(LowerRounded(weight * MinimumExponentialDifference(
                        lambdaLow, lambdaHigh, candidateEnds[segment], condition.IncumbentStarts[segment])));
                }

                double candidateLast = candidateEnds[candidateLastSegments[episodeIndex]];
                double incumbentLast = condition.IncumbentStarts[episode.LastSegment];
                double incumbentReady = condition.IncumbentMatchedEnds[episode.LastSegment];
                double complete = MinimumExponentialDifference(lambdaLow, lambdaHigh, candidateLast, incumbentLast);
                double episodeDelta = Math.Min(partial.Value, complete);
                double probability = episodeDelta < 0 ? episode.PHigh : episode.PLow;
                episodeSum.Add(LowerRounded(probability * episodeDelta));

                long constraintStarted = reportStage is null ? 0 : Stopwatch.GetTimestamp();
                double delay = candidateLast - incumbentReady;
                worstEpisodeDelayMs = Math.Max(worstEpisodeDelayMs, delay);
                double allowance = Math.Max(Orbit3Constants.EpisodeRegressionSlackMs,
                    incumbentReady * Orbit3Constants.EpisodeRegressionFraction);
                episodeRegression |= delay > allowance;
                if (reportStage is not null)
                {
                    episodeConstraintTicks += Stopwatch.GetTimestamp() - constraintStarted;
                }
            }

            lower = Math.Min(lower, episodeSum.Value);
        }

        if (!double.IsFinite(worstEpisodeDelayMs))
        {
            worstEpisodeDelayMs = 0;
        }

        if (reportStage is not null)
        {
            reportStage("episode_constraint", episodeConstraintTicks * 1000d / Stopwatch.Frequency);
        }

        return LowerRounded(lower);
    }

    private static int[] LastRequiredSegments(EpisodeData[] episodes, int[] order, int segmentCount, CancellationToken token)
    {
        var positionBySegment = new int[segmentCount];
        for (int position = 0; position < order.Length; position++)
        {
            positionBySegment[order[position]] = position;
        }

        var lastSegments = new int[episodes.Length];
        for (int episodeIndex = 0; episodeIndex < episodes.Length; episodeIndex++)
        {
            token.ThrowIfCancellationRequested();
            EpisodeData episode = episodes[episodeIndex];
            int lastSegment = episode.Parts[0].Segment;
            int lastPosition = positionBySegment[lastSegment];
            foreach ((int segment, _) in episode.Parts.AsSpan(1))
            {
                if (positionBySegment[segment] > lastPosition)
                {
                    lastSegment = segment;
                    lastPosition = positionBySegment[segment];
                }
            }

            lastSegments[episodeIndex] = lastSegment;
        }

        return lastSegments;
    }

    private static double[] PartitionEdges(double low, double high)
    {
        if (low == high)
        {
            return Enumerable.Repeat(low, DevicePartitions + 1).ToArray();
        }

        var edges = new double[DevicePartitions + 1];
        edges[0] = low;
        edges[^1] = high;
        if (low == 0)
        {
            for (int index = 1; index < DevicePartitions; index++)
            {
                edges[index] = high * Math.Pow(1e-6, (double)(DevicePartitions - index) / DevicePartitions);
            }
        }
        else
        {
            double logLow = Math.Log(low);
            double logSpan = Math.Log(high) - logLow;
            for (int index = 1; index < DevicePartitions; index++)
            {
                edges[index] = Math.Exp(logLow + logSpan * index / DevicePartitions);
            }
        }

        return edges;
    }

    private static (double[] Starts, double[] Ends) SegmentTimes(
        OrbitSegmentSet set,
        int[] order,
        double headTime,
        Orbit3Device device,
        double seekFactor,
        double throughputFactor,
        CancellationToken token)
    {
        var starts = new double[set.Segments.Count];
        var ends = new double[set.Segments.Count];
        double time = headTime;
        int previous = -1;
        foreach (int segmentIndex in order)
        {
            token.ThrowIfCancellationRequested();
            OrbitSegment segment = set.Segments[segmentIndex];
            starts[segmentIndex] = time;
            time += SegmentCost(segment, segmentIndex, previous, device, seekFactor, throughputFactor);
            if (!double.IsFinite(time))
            {
                throw new ArithmeticException("세그먼트 시각이 유한하지 않습니다.");
            }

            ends[segmentIndex] = time;
            previous = segmentIndex;
        }

        return (starts, ends);
    }

    private static double SegmentCost(OrbitSegment segment, int index, int previous, Orbit3Device device, double seekFactor, double throughputFactor)
    {
        double seekCount = segment.InnerGaps + (previous == index - 1 ? 0 : 1);
        return segment.Bytes / (device.BytesPerMs * throughputFactor)
            + seekCount * device.SeekMs * seekFactor;
    }

    private static double HeadTime(OrbitSegmentSet set, Orbit3Device device, double seekFactor, double throughputFactor)
    {
        int gaps = 0;
        for (int index = 1; index < set.HeadCount; index++)
        {
            if (!Adjacent(set.Ops[index - 1], set.Ops[index]))
            {
                gaps++;
            }
        }

        return set.HeadBytes / (device.BytesPerMs * throughputFactor) + gaps * device.SeekMs * seekFactor;
    }

    private static double OrderCost(OrbitSegmentSet set, int[] order, Orbit3Device device)
    {
        double cost = 0;
        int previous = -1;
        foreach (int segment in order)
        {
            cost += SegmentCost(set.Segments[segment], segment, previous, device, 1, 1);
            previous = segment;
        }

        return cost;
    }

    private static double ExponentialDifference(double lambda, double candidateTime, double incumbentTime)
    {
        double candidate = Math.Exp(-lambda * candidateTime);
        double incumbent = Math.Exp(-lambda * incumbentTime);
        if (candidate != 0)
        {
            candidate = Math.BitDecrement(candidate);
        }

        incumbent = Math.BitIncrement(incumbent);
        return LowerRounded(candidate - incumbent);
    }

    private static double LowerRounded(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        return Math.BitDecrement(value);
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.DLow != current.DLow)
        {
            return candidate.DLow > current.DLow;
        }

        if (candidate.NominalGain != current.NominalGain)
        {
            return candidate.NominalGain > current.NominalGain;
        }

        return CompareOrders(candidate.Order, current.Order) < 0;
    }

    private static int CompareOrders(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int index = 0; index < count; index++)
        {
            int comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static bool Adjacent(ReadOp previous, ReadOp current)
    {
        if (!string.Equals(previous.Path, current.Path, StringComparison.Ordinal) || previous.Length < 0
            || previous.Offset > long.MaxValue - previous.Length)
        {
            return false;
        }

        return current.Offset == previous.Offset + previous.Length;
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    private static void EnsureWithinBudget(CancellationToken token, long started, int decisionBudgetMs)
    {
        token.ThrowIfCancellationRequested();
        if (IsBudgetExpired(started, decisionBudgetMs))
        {
            throw new OperationCanceledException();
        }
    }

    private static bool IsBudgetExpired(long started, int decisionBudgetMs)
    {
        long budgetTicks = (long)decisionBudgetMs * Stopwatch.Frequency / 1000;
        return Stopwatch.GetTimestamp() - started >= budgetTicks;
    }

    private static Orbit3Decision Result(
        IReadOnlyList<int>? order,
        string reason,
        double? dLow,
        double nominalGain,
        int candidates,
        long started,
        double? worstEpisodeDelayMs = null)
    {
        double elapsed = (Stopwatch.GetTimestamp() - started) * 1_000_000d / Stopwatch.Frequency;
        long elapsedMicros = elapsed >= long.MaxValue ? long.MaxValue : Math.Max(0, (long)elapsed);
        return new Orbit3Decision(order, reason, dLow, nominalGain, candidates, elapsedMicros, worstEpisodeDelayMs);
    }
}
