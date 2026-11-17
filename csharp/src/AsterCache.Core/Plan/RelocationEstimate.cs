namespace AsterCache.Core.Plan;

public enum EstimateBlockClass
{
    Movable,
    Unmovable,
    ChangedDuringSession,
    MissingFile,
    MissingSection,
    LayoutUnavailable,
}

public sealed record RelocationEstimatePhysicalExtent(long StartByte, long EndByte);

/// <summary>한 물리 읽기 집계의 파일별 64 KiB 블록 행과 현재 배치 정보입니다.</summary>
public sealed record RelocationEstimateBlock(
    string Path,
    long BlockNumber,
    long Requests,
    long Bytes,
    long LatencySumUs,
    long LatencyMaxUs,
    long FirstObservedUs,
    long PagingRequests,
    string VolumeKey,
    string? DeviceKey,
    long LogicalBytes,
    long OccupiedBytes,
    EstimateBlockClass Classification,
    string? UnmovableReason,
    IReadOnlyList<RelocationEstimatePhysicalExtent> CurrentExtents,
    bool? InPlan,
    bool? PolicyCandidate,
    string? PolicyExclusionReason);

public sealed record RelocationEstimateSession(
    string SourcePath,
    string SessionId,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<RelocationEstimateBlock> Blocks,
    bool Truncated = false,
    long EventsLost = 0,
    long BlockTableDropped = 0);

public sealed record RelocationEstimateZoneStart(
    string VolumeKey,
    string? DeviceKey,
    long StartLcn,
    long? StartByte,
    string Source,
    long? PlanZoneClusters = null);

public sealed record RelocationEstimateTotals(
    long Blocks,
    long Requests,
    long ReadBytes,
    long LatencySumUs,
    long LatencyMaxUs,
    long PagingRequests);

public sealed record RelocationEstimateSequence(
    long BlocksWithLocation,
    long BlocksWithoutLocation,
    long Transitions,
    long MovementBytes,
    long ContiguousRuns,
    double? MedianDistanceBytes,
    long? P90DistanceBytes,
    long? MaximumDistanceBytes);

public sealed record RelocationEstimatePlanCoverage(
    RelocationEstimateTotals Inside,
    RelocationEstimateTotals Outside,
    RelocationEstimateTotals Unknown);

public sealed record RelocationEstimatePolicyCoverage(
    RelocationEstimateTotals Candidate,
    IReadOnlyDictionary<string, RelocationEstimateTotals> ExcludedByReason,
    RelocationEstimateTotals Unknown);

public sealed record RelocationEstimateSelection(
    string Criterion,
    long ZoneMib,
    RelocationEstimateTotals Selected,
    double? SelectedReadBytesRatio,
    double? SelectedLatencyRatio,
    long MovedBlocks,
    long BytesToMove,
    long BlocksAlreadyInZone,
    long RequiredContiguousBytes,
    RelocationEstimateSequence VirtualSequence);

public sealed record RelocationEstimateScenario(
    long ZoneMib,
    RelocationEstimatePlanCoverage CurrentZoneCoverage,
    RelocationEstimateSelection ByLatency,
    RelocationEstimateSelection ByRequests);

public sealed record RelocationEstimateAnalysis(
    RelocationEstimateTotals Total,
    RelocationEstimateTotals Movable,
    IReadOnlyDictionary<string, RelocationEstimateTotals> Unmovable,
    RelocationEstimateTotals ChangedDuringSession,
    RelocationEstimateTotals MissingFile,
    RelocationEstimateTotals MissingSection,
    IReadOnlyDictionary<string, RelocationEstimateTotals> LayoutUnavailable,
    RelocationEstimatePlanCoverage? PlanCoverage,
    RelocationEstimatePolicyCoverage PolicyCoverage,
    RelocationEstimateTotals NonPaging,
    RelocationEstimateTotals PagingOnly,
    RelocationEstimateTotals PagingUnknown,
    RelocationEstimateSequence CurrentSequence,
    IReadOnlyList<RelocationEstimateScenario> Scenarios);

public sealed record RelocationEstimateSessionResult(
    string SourcePath,
    string SessionId,
    bool Truncated,
    long EventsLost,
    long BlockTableDropped,
    RelocationEstimateAnalysis Analysis);

public sealed record RelocationEstimateReport(
    IReadOnlyList<RelocationEstimateSessionResult> Sessions,
    IReadOnlyList<string> DuplicateSessionIds,
    RelocationEstimateAnalysis Combined);

/// <summary>물리 집계의 파일 배치·분류 자료에서 추정치를 계산합니다. 파일 시스템에 접근하지 않습니다.</summary>
public static class RelocationEstimate
{
    public const long BlockSizeBytes = 64 * 1024;
    private const long Mib = 1024 * 1024;

    private sealed record BlockKey(string Path, long BlockNumber);

    public static RelocationEstimateReport Calculate(
        IReadOnlyList<RelocationEstimateSession> sessions,
        IReadOnlyList<long> zoneMib,
        IReadOnlyList<RelocationEstimateZoneStart> zoneStarts,
        bool planProvided = false)
    {
        if (zoneMib.Any(size => size <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(zoneMib));
        }

        var unique = new List<RelocationEstimateSession>();
        var duplicates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RelocationEstimateSession session in sessions)
        {
            if (!seen.Add(session.SessionId))
            {
                duplicates.Add(session.SessionId);
                continue;
            }

            unique.Add(session);
        }

        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts = zoneStarts
            .GroupBy(start => start.VolumeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var results = new List<RelocationEstimateSessionResult>(unique.Count);
        foreach (RelocationEstimateSession session in unique)
        {
            List<RelocationEstimateBlock> blocks = Normalize(session.Blocks);
            results.Add(new RelocationEstimateSessionResult(session.SourcePath, session.SessionId, session.Truncated,
                session.EventsLost, session.BlockTableDropped, Analyze(blocks, zoneMib, starts, planProvided)));
        }

        List<RelocationEstimateBlock> combined = Merge(unique);
        return new RelocationEstimateReport(results, duplicates, Analyze(combined, zoneMib, starts, planProvided));
    }

    private static RelocationEstimateAnalysis Analyze(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        IReadOnlyList<long> zoneMib,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts,
        bool planProvided)
    {
        RelocationEstimateTotals total = Totals(blocks);
        RelocationEstimateBlock[] movableBlocks = [.. blocks.Where(block => block.Classification == EstimateBlockClass.Movable)];
        RelocationEstimateTotals movable = Totals(movableBlocks);
        Dictionary<string, RelocationEstimateTotals> unmovable = GroupTotals(blocks.Where(block => block.Classification == EstimateBlockClass.Unmovable),
            block => block.UnmovableReason ?? "unknown");
        RelocationEstimateTotals changed = Totals(blocks.Where(block => block.Classification == EstimateBlockClass.ChangedDuringSession));
        RelocationEstimateTotals missingFile = Totals(blocks.Where(block => block.Classification == EstimateBlockClass.MissingFile));
        RelocationEstimateTotals missingSection = Totals(blocks.Where(block => block.Classification == EstimateBlockClass.MissingSection));
        Dictionary<string, RelocationEstimateTotals> unavailable = GroupTotals(blocks.Where(block => block.Classification == EstimateBlockClass.LayoutUnavailable),
            block => block.UnmovableReason ?? "layout_unavailable");

        RelocationEstimatePlanCoverage? coverage = planProvided
            ? new RelocationEstimatePlanCoverage(
                Totals(blocks.Where(block => block.InPlan == true)),
                Totals(blocks.Where(block => block.InPlan == false)),
                Totals(blocks.Where(block => block.InPlan is null)))
            : null;

        RelocationEstimatePolicyCoverage policy = new(
            Totals(movableBlocks.Where(block => block.PolicyCandidate == true)),
            GroupTotals(movableBlocks.Where(block => block.PolicyCandidate == false), block => block.PolicyExclusionReason ?? "unknown"),
            Totals(movableBlocks.Where(block => block.PolicyCandidate is null)));

        RelocationEstimateTotals nonPaging = Totals(blocks.Where(block => block.Requests > block.PagingRequests));
        RelocationEstimateTotals pagingOnly = Totals(blocks.Where(block => block.Requests > 0 && block.Requests == block.PagingRequests));
        RelocationEstimateTotals pagingUnknown = Totals(blocks.Where(block => block.Requests <= 0 || block.PagingRequests > block.Requests));
        RelocationEstimateSequence current = Sequence(blocks);
        var scenarios = new List<RelocationEstimateScenario>(zoneMib.Count);
        foreach (long size in zoneMib)
        {
            scenarios.Add(new RelocationEstimateScenario(size,
                CurrentZoneCoverage(blocks, size, starts),
                Select(blocks, total, size, "latency", starts),
                Select(blocks, total, size, "requests", starts)));
        }

        return new RelocationEstimateAnalysis(total, movable, unmovable, changed, missingFile, missingSection, unavailable,
            coverage, policy, nonPaging, pagingOnly, pagingUnknown, current, scenarios);
    }

    private static RelocationEstimatePlanCoverage CurrentZoneCoverage(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        long zoneMib,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts)
    {
        long zoneBytes = checked(zoneMib * Mib);
        var inside = new List<RelocationEstimateBlock>();
        var outside = new List<RelocationEstimateBlock>();
        var unknown = new List<RelocationEstimateBlock>();
        foreach (RelocationEstimateBlock block in blocks)
        {
            if (!TryZoneBounds(block.VolumeKey, zoneBytes, starts, out long zoneStart, out long zoneEnd)
                || block.CurrentExtents.Count == 0 || block.DeviceKey is null)
            {
                unknown.Add(block);
            }
            else if (IsInZone(block.CurrentExtents, zoneStart, zoneEnd))
            {
                inside.Add(block);
            }
            else
            {
                outside.Add(block);
            }
        }

        return new RelocationEstimatePlanCoverage(Totals(inside), Totals(outside), Totals(unknown));
    }

    private static RelocationEstimateSelection Select(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        RelocationEstimateTotals total,
        long zoneMib,
        string criterion,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts)
    {
        long zoneBytes = checked(zoneMib * Mib);
        var selected = new HashSet<BlockKey>(new BlockKeyComparer());
        foreach (IGrouping<string, RelocationEstimateBlock> volume in blocks
            .Where(block => block.Classification == EstimateBlockClass.Movable)
            .GroupBy(block => block.VolumeKey, StringComparer.OrdinalIgnoreCase))
        {
            IOrderedEnumerable<RelocationEstimateBlock> candidates = criterion == "latency"
                ? volume.OrderByDescending(block => block.LatencySumUs)
                : volume.OrderByDescending(block => block.Requests);
            long used = 0;
            foreach (RelocationEstimateBlock block in candidates.ThenBy(block => block.FirstObservedUs)
                .ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                long bytes = Math.Max(0, block.OccupiedBytes);
                if (bytes == 0 || checked(used + bytes) > zoneBytes)
                {
                    continue;
                }

                selected.Add(new BlockKey(block.Path, block.BlockNumber));
                used = checked(used + bytes);
            }
        }

        List<RelocationEstimateBlock> chosen = blocks.Where(block => selected.Contains(new BlockKey(block.Path, block.BlockNumber))).ToList();
        RelocationEstimateTotals selectedTotals = Totals(chosen);
        long required = 0, toMove = 0, alreadyInZone = 0, movedBlocks = 0;
        foreach (RelocationEstimateBlock block in chosen)
        {
            required = checked(required + block.OccupiedBytes);
            if (TryZoneBounds(block.VolumeKey, zoneBytes, starts, out long zoneStart, out long zoneEnd)
                && block.CurrentExtents.Count > 0 && IsInZone(block.CurrentExtents, zoneStart, zoneEnd))
            {
                alreadyInZone++;
                continue;
            }

            movedBlocks++;
            long moveZoneStart = starts.TryGetValue(block.VolumeKey, out RelocationEstimateZoneStart? start) ? start.StartByte ?? 0 : 0;
            long moveZoneEnd = checked(moveZoneStart + zoneBytes);
            toMove = checked(toMove + (block.CurrentExtents.Count == 0
                ? block.OccupiedBytes
                : BytesOutsideZone(block.CurrentExtents, moveZoneStart, moveZoneEnd)));
        }

        var virtualBlocks = new List<RelocationEstimateBlock>(blocks.Count);
        foreach (IGrouping<string, RelocationEstimateBlock> volume in blocks.GroupBy(block => block.VolumeKey, StringComparer.OrdinalIgnoreCase))
        {
            long zoneStart = starts.TryGetValue(volume.Key, out RelocationEstimateZoneStart? start) ? start.StartByte ?? 0 : 0;
            long offset = 0;
            foreach (RelocationEstimateBlock block in volume.Where(block => selected.Contains(new BlockKey(block.Path, block.BlockNumber)))
                .OrderBy(block => block.FirstObservedUs).ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                long end = checked(zoneStart + offset + block.OccupiedBytes);
                virtualBlocks.Add(block with { CurrentExtents = [new RelocationEstimatePhysicalExtent(checked(zoneStart + offset), end)] });
                offset = checked(offset + block.OccupiedBytes);
            }

            virtualBlocks.AddRange(volume.Where(block => !selected.Contains(new BlockKey(block.Path, block.BlockNumber))));
        }

        return new RelocationEstimateSelection(criterion, zoneMib, selectedTotals,
            Ratio(selectedTotals.ReadBytes, total.ReadBytes), Ratio(selectedTotals.LatencySumUs, total.LatencySumUs),
            movedBlocks, toMove, alreadyInZone, required, Sequence(virtualBlocks));
    }

    private static bool TryZoneBounds(string volumeKey, long zoneBytes,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts, out long zoneStart, out long zoneEnd)
    {
        if (!starts.TryGetValue(volumeKey, out RelocationEstimateZoneStart? start) || start.StartByte is not { } byteStart)
        {
            zoneStart = 0;
            zoneEnd = 0;
            return false;
        }

        zoneStart = byteStart;
        zoneEnd = checked(zoneStart + zoneBytes);
        return true;
    }

    private static bool IsInZone(IReadOnlyList<RelocationEstimatePhysicalExtent> extents, long zoneStart, long zoneEnd) =>
        extents.Count > 0 && extents.All(extent => extent.StartByte >= zoneStart && extent.EndByte <= zoneEnd);

    private static long BytesOutsideZone(IReadOnlyList<RelocationEstimatePhysicalExtent> extents, long zoneStart, long zoneEnd)
    {
        long outside = 0;
        foreach (RelocationEstimatePhysicalExtent extent in extents)
        {
            long length = Math.Max(0, checked(extent.EndByte - extent.StartByte));
            long insideStart = Math.Max(extent.StartByte, zoneStart);
            long insideEnd = Math.Min(extent.EndByte, zoneEnd);
            long inside = Math.Max(0, checked(insideEnd - insideStart));
            outside = checked(outside + length - inside);
        }

        return outside;
    }

    private static RelocationEstimateSequence Sequence(IReadOnlyList<RelocationEstimateBlock> blocks)
    {
        var distances = new List<long>();
        long withLocation = 0, withoutLocation = 0, runs = 0;
        foreach (RelocationEstimateBlock block in blocks)
        {
            if (block.CurrentExtents.Count == 0 || block.DeviceKey is null)
            {
                withoutLocation++;
            }
            else
            {
                withLocation++;
            }
        }

        foreach (IGrouping<string, RelocationEstimateBlock> device in blocks.Where(block => block.DeviceKey is not null)
            .GroupBy(block => block.DeviceKey!, StringComparer.OrdinalIgnoreCase))
        {
            long? previousEnd = null;
            foreach (RelocationEstimateBlock block in device.OrderBy(block => block.FirstObservedUs)
                .ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                if (block.CurrentExtents.Count == 0)
                {
                    previousEnd = null;
                    continue;
                }

                foreach (RelocationEstimatePhysicalExtent extent in block.CurrentExtents)
                {
                    if (previousEnd is not { } priorEnd)
                    {
                        runs++;
                        previousEnd = extent.EndByte;
                        continue;
                    }

                    long distance = Math.Abs(checked(extent.StartByte - priorEnd));
                    distances.Add(distance);
                    if (distance != 0)
                    {
                        runs++;
                    }

                    previousEnd = extent.EndByte;
                }
            }
        }

        long movement = 0;
        foreach (long distance in distances)
        {
            movement = checked(movement + distance);
        }

        if (distances.Count == 0)
        {
            return new RelocationEstimateSequence(withLocation, withoutLocation, 0, 0, runs, null, null, null);
        }

        distances.Sort();
        double median = distances.Count % 2 == 1
            ? distances[distances.Count / 2]
            : distances[distances.Count / 2 - 1] / 2d + distances[distances.Count / 2] / 2d;
        int p90Index = Math.Max(0, (int)Math.Ceiling(distances.Count * 0.9) - 1);
        return new RelocationEstimateSequence(withLocation, withoutLocation, distances.Count, movement, runs,
            median, distances[p90Index], distances[^1]);
    }

    private static List<RelocationEstimateBlock> Merge(IReadOnlyList<RelocationEstimateSession> sessions)
    {
        var grouped = new Dictionary<BlockKey, List<(RelocationEstimateSession Session, RelocationEstimateBlock Block, long FirstObservedUs)>>(new BlockKeyComparer());
        foreach (RelocationEstimateSession session in sessions.OrderBy(item => item.ObservedAtUtc.ToUniversalTime())
            .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.SourcePath, StringComparer.Ordinal))
        {
            long sessionStartUs = session.ObservedAtUtc.ToUniversalTime().Ticks / 10;
            foreach (RelocationEstimateBlock block in Normalize(session.Blocks))
            {
                var key = new BlockKey(block.Path, block.BlockNumber);
                if (!grouped.TryGetValue(key, out List<(RelocationEstimateSession, RelocationEstimateBlock, long)>? values))
                {
                    grouped[key] = values = [];
                }

                values.Add((session, block, checked(sessionStartUs + block.FirstObservedUs)));
            }
        }

        return grouped.Values.Select(MergeBlocks)
            .OrderBy(block => block.FirstObservedUs).ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(block => block.BlockNumber).ToList();
    }

    private static RelocationEstimateBlock MergeBlocks(IEnumerable<(RelocationEstimateSession Session, RelocationEstimateBlock Block, long FirstObservedUs)> entries)
    {
        var values = entries.OrderBy(item => item.Session.ObservedAtUtc.ToUniversalTime())
            .ThenBy(item => item.Session.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Session.SourcePath, StringComparer.Ordinal).ThenBy(item => item.Block.Path, StringComparer.Ordinal)
            .ThenBy(item => PhysicalKey(item.Block), StringComparer.Ordinal).ToArray();
        RelocationEstimateBlock first = values[0].Block;
        EstimateBlockClass classification = values.Select(item => item.Block.Classification).Aggregate(MergeClass);
        bool changed = values.Any(item => item.Block.Classification == EstimateBlockClass.ChangedDuringSession);
        RelocationEstimateBlock location = values.Select(item => item.Block)
            .Where(block => block.CurrentExtents.Count > 0)
            .OrderByDescending(block => block.CurrentExtents.Count > 0)
            .ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(block => block.CurrentExtents[0].StartByte)
            .ThenBy(PhysicalKey, StringComparer.Ordinal)
            .FirstOrDefault() ?? first;

        string? reason = values.Where(item => item.Block.Classification == classification)
            .Select(item => item.Block.UnmovableReason).Where(value => value is not null)
            .Order(StringComparer.Ordinal).FirstOrDefault();
        string? policyReason = values.Select(item => item.Block.PolicyExclusionReason).Where(value => value is not null)
            .Order(StringComparer.Ordinal).FirstOrDefault();
        bool? policyCandidate = values.Any(item => item.Block.PolicyCandidate == false) ? false
            : values.All(item => item.Block.PolicyCandidate == true) ? true
            : null;

        long requests = 0, bytes = 0, latency = 0, paging = 0;
        long latencyMax = 0, firstObserved = long.MaxValue;
        foreach (var item in values)
        {
            requests = checked(requests + item.Block.Requests);
            bytes = checked(bytes + item.Block.Bytes);
            latency = checked(latency + item.Block.LatencySumUs);
            paging = checked(paging + item.Block.PagingRequests);
            latencyMax = Math.Max(latencyMax, item.Block.LatencyMaxUs);
            firstObserved = Math.Min(firstObserved, item.FirstObservedUs);
        }

        bool? inPlan = SameValue(values.Select(item => item.Block.InPlan));
        return location with
        {
            Path = first.Path,
            Requests = requests,
            Bytes = bytes,
            LatencySumUs = latency,
            LatencyMaxUs = latencyMax,
            FirstObservedUs = firstObserved,
            PagingRequests = paging,
            LogicalBytes = location.LogicalBytes,
            OccupiedBytes = location.OccupiedBytes,
            Classification = classification,
            UnmovableReason = reason,
            CurrentExtents = changed ? [] : location.CurrentExtents,
            InPlan = inPlan,
            PolicyCandidate = policyCandidate,
            PolicyExclusionReason = policyReason,
        };
    }

    private static bool? SameValue(IEnumerable<bool?> values)
    {
        bool?[] all = [.. values];
        return all.Length > 0 && all.All(value => value == all[0]) ? all[0] : null;
    }

    private static EstimateBlockClass MergeClass(EstimateBlockClass left, EstimateBlockClass right)
    {
        static int Rank(EstimateBlockClass value) => value switch
        {
            EstimateBlockClass.MissingFile => 6,
            EstimateBlockClass.ChangedDuringSession => 5,
            EstimateBlockClass.MissingSection => 4,
            EstimateBlockClass.Unmovable => 3,
            EstimateBlockClass.LayoutUnavailable => 2,
            _ => 1,
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static List<RelocationEstimateBlock> Normalize(IReadOnlyList<RelocationEstimateBlock> blocks)
    {
        return blocks.GroupBy(block => new BlockKey(block.Path, block.BlockNumber), new BlockKeyComparer())
            .Select(group =>
            {
                RelocationEstimateBlock[] values = group.OrderBy(block => block.Path, StringComparer.Ordinal)
                    .ThenBy(block => block.CurrentExtents.FirstOrDefault()?.StartByte ?? long.MaxValue)
                    .ThenBy(PhysicalKey, StringComparer.Ordinal).ToArray();
                return MergeBlocks(values.Select(block => (new RelocationEstimateSession(string.Empty, string.Empty, DateTimeOffset.UnixEpoch, []), block, block.FirstObservedUs)));
            })
            .OrderBy(block => block.FirstObservedUs).ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(block => block.BlockNumber).ToList();
    }

    private static string PhysicalKey(RelocationEstimateBlock block) =>
        string.Join(";", block.CurrentExtents.Select(extent => $"{extent.StartByte}:{extent.EndByte}"));

    private static RelocationEstimateTotals Totals(IEnumerable<RelocationEstimateBlock> blocks)
    {
        long count = 0, requests = 0, bytes = 0, latency = 0, max = 0, paging = 0;
        foreach (RelocationEstimateBlock block in blocks)
        {
            count++;
            requests = checked(requests + block.Requests);
            bytes = checked(bytes + block.Bytes);
            latency = checked(latency + block.LatencySumUs);
            max = Math.Max(max, block.LatencyMaxUs);
            paging = checked(paging + block.PagingRequests);
        }

        return new RelocationEstimateTotals(count, requests, bytes, latency, max, paging);
    }

    private static Dictionary<string, RelocationEstimateTotals> GroupTotals(IEnumerable<RelocationEstimateBlock> blocks, Func<RelocationEstimateBlock, string> key) =>
        blocks.GroupBy(key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => Totals(group), StringComparer.Ordinal);

    private static double? Ratio(long part, long whole) => whole == 0 ? null : part / (double)whole;

    private sealed class BlockKeyComparer : IEqualityComparer<BlockKey>
    {
        public bool Equals(BlockKey? x, BlockKey? y) => x is not null && y is not null && x.BlockNumber == y.BlockNumber
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(BlockKey obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.BlockNumber);
    }
}
