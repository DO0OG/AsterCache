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
    string? PolicyExclusionReason,
    double Weight = 1.0);

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
    RelocationEstimateSequence VirtualSequence,
    RelocationEstimateModelMetrics Model,
    double ModelPositionReductionMs,
    double? RealizedGainMs = null);

public sealed record RelocationEstimateScenario(
    long ZoneMib,
    RelocationEstimatePlanCoverage CurrentZoneCoverage,
    RelocationEstimateSelection ByLatency,
    RelocationEstimateSelection ByRequests,
    RelocationEstimateSelection ByLinkGain);

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
    IReadOnlyList<RelocationEstimateScenario> Scenarios,
    RelocationEstimateSegmentStats Segments,
    RelocationEstimateModelMetrics CurrentModel);

public sealed record RelocationEstimateSessionResult(
    string SourcePath,
    string SessionId,
    bool Truncated,
    long EventsLost,
    long BlockTableDropped,
    RelocationEstimateAnalysis Analysis);

/// <summary>평가용 플레이 하나를 선정마다 가상 배치로 놓았을 때의 모형 지표입니다. 이 플레이는 선정에 쓰이지 않았습니다.</summary>
public sealed record RelocationEstimateEvaluatedSelection(RelocationEstimateModelMetrics Model, RelocationEstimateLinkChange Change);

public sealed record RelocationEstimateEvaluationZone(
    long ZoneMib,
    RelocationEstimateEvaluatedSelection ByLatency,
    RelocationEstimateEvaluatedSelection ByRequests,
    RelocationEstimateEvaluatedSelection ByLinkGain);

public sealed record RelocationEstimateEvaluation(
    string SourcePath,
    string SessionId,
    RelocationEstimateTotals Total,
    RelocationEstimateTotals UnseenInTraining,
    RelocationEstimateModelMetrics Current,
    IReadOnlyList<RelocationEstimateEvaluationZone> Zones);

public sealed record RelocationEstimateReport(
    IReadOnlyList<RelocationEstimateSessionResult> Sessions,
    IReadOnlyList<string> DuplicateSessionIds,
    RelocationEstimateAnalysis Combined,
    RelocationEstimateModel Model,
    long DiskBytes,
    IReadOnlyList<RelocationEstimateEvaluation> Evaluations,
    IReadOnlyList<string> DuplicateEvaluationSessionIds);

/// <summary>물리 집계의 파일 배치·분류 자료에서 추정치를 계산합니다. 파일 시스템에 접근하지 않습니다.</summary>
public static partial class RelocationEstimate
{
    public const long BlockSizeBytes = 64 * 1024;
    private const long Mib = 1024 * 1024;
    public const double DefaultSessionDecay = 0.7;

    private sealed record BlockKey(string Path, long BlockNumber);

    public static RelocationEstimateReport Calculate(
        IReadOnlyList<RelocationEstimateSession> sessions,
        IReadOnlyList<long> zoneMib,
        IReadOnlyList<RelocationEstimateZoneStart> zoneStarts,
        bool planProvided = false,
        RelocationEstimateModel? model = null,
        double sessionDecay = DefaultSessionDecay,
        IReadOnlyList<RelocationEstimateSession>? evaluationSessions = null)
    {
        if (zoneMib.Any(size => size <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(zoneMib));
        }

        if (!(sessionDecay > 0 && sessionDecay <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionDecay));
        }

        model ??= RelocationEstimateModel.Default;
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

        var evaluation = new List<RelocationEstimateSession>();
        var duplicateEvaluation = new List<string>();
        var evaluationSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RelocationEstimateSession session in evaluationSessions ?? [])
        {
            if (seen.Contains(session.SessionId))
            {
                throw new ArgumentException("an evaluation session_id is also a training session_id: " + session.SessionId, nameof(evaluationSessions));
            }

            if (!evaluationSeen.Add(session.SessionId))
            {
                duplicateEvaluation.Add(session.SessionId);
                continue;
            }

            evaluation.Add(session);
        }

        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts = zoneStarts
            .GroupBy(start => start.VolumeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        List<RelocationEstimateBlock> combined = Merge(unique, SessionWeights(unique, sessionDecay));
        List<RelocationEstimateBlock> line = RecencyLine(unique, combined);
        // 디스크 크기를 모르면 읽기 구간 사이 이음 거리의 최댓값을 씁니다.
        long diskBytes = model.DiskBytes is > 0
            ? model.DiskBytes.Value
            : Math.Max(1, RelocationEstimateSegments.MaximumLinkDistance(RelocationEstimateSegments.Build(line, _ => true)));
        var results = new List<RelocationEstimateSessionResult>(unique.Count);
        foreach (RelocationEstimateSession session in unique)
        {
            List<RelocationEstimateBlock> blocks = Normalize(session.Blocks);
            results.Add(new RelocationEstimateSessionResult(session.SourcePath, session.SessionId, session.Truncated,
                session.EventsLost, session.BlockTableDropped, Analyze(blocks, blocks, zoneMib, starts, planProvided, model, diskBytes, null)));
        }

        var placements = new Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>>();
        RelocationEstimateAnalysis combinedAnalysis = Analyze(combined, line, zoneMib, starts, planProvided, model, diskBytes, placements);
        List<RelocationEstimateEvaluation> evaluations = Evaluate(evaluation, combined, zoneMib, placements, model, diskBytes);
        return new RelocationEstimateReport(results, duplicates, combinedAnalysis, model, diskBytes, evaluations, duplicateEvaluation);
    }

    /// <summary>플레이 가중치는 관측 시각이 최근인 것부터 1, decay, decay^2 … 입니다.</summary>
    private static Dictionary<RelocationEstimateSession, double> SessionWeights(IReadOnlyList<RelocationEstimateSession> sessions, double decay)
    {
        var weights = new Dictionary<RelocationEstimateSession, double>(ReferenceEqualityComparer.Instance);
        double weight = 1;
        foreach (RelocationEstimateSession session in RecentFirst(sessions))
        {
            weights[session] = weight;
            weight *= decay;
        }

        return weights;
    }

    private static IEnumerable<RelocationEstimateSession> RecentFirst(IReadOnlyList<RelocationEstimateSession> sessions) =>
        sessions.OrderByDescending(item => item.ObservedAtUtc.ToUniversalTime())
            .ThenBy(item => item.SourcePath, StringComparer.Ordinal).ThenBy(item => item.SessionId, StringComparer.Ordinal);

    /// <summary>최근 플레이부터 이어 붙이고 이미 나온 블록은 건너뛴 한 줄의 순서입니다. FirstObservedUs 는 이 줄에서의 순번으로 바꿉니다.</summary>
    private static List<RelocationEstimateBlock> RecencyLine(IReadOnlyList<RelocationEstimateSession> sessions, IReadOnlyList<RelocationEstimateBlock> merged)
    {
        var byKey = merged.ToDictionary(block => new BlockKey(block.Path, block.BlockNumber), new BlockKeyComparer());
        var seen = new HashSet<BlockKey>(new BlockKeyComparer());
        var line = new List<RelocationEstimateBlock>(merged.Count);
        foreach (RelocationEstimateSession session in RecentFirst(sessions))
        {
            foreach (RelocationEstimateBlock block in Normalize(session.Blocks))
            {
                var key = new BlockKey(block.Path, block.BlockNumber);
                if (seen.Add(key))
                {
                    line.Add(byKey[key] with { FirstObservedUs = line.Count });
                }
            }
        }

        return line;
    }

    private static List<RelocationEstimateEvaluation> Evaluate(
        IReadOnlyList<RelocationEstimateSession> sessions,
        IReadOnlyList<RelocationEstimateBlock> training,
        IReadOnlyList<long> zoneMib,
        Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>> placements,
        RelocationEstimateModel model,
        long diskBytes)
    {
        var trainingKeys = new HashSet<BlockKey>(training.Select(block => new BlockKey(block.Path, block.BlockNumber)), new BlockKeyComparer());
        var results = new List<RelocationEstimateEvaluation>(sessions.Count);
        foreach (RelocationEstimateSession session in sessions.OrderBy(item => item.ObservedAtUtc.ToUniversalTime())
            .ThenBy(item => item.SourcePath, StringComparer.Ordinal).ThenBy(item => item.SessionId, StringComparer.Ordinal))
        {
            List<RelocationEstimateBlock> blocks = Normalize(session.Blocks);
            RelocationEstimateEvaluatedSelection Placed(long zone, string criterion) => new(
                ModelMetrics(
                    [.. blocks.Select(block => placements[(zone, criterion)].TryGetValue(new BlockKey(block.Path, block.BlockNumber), out RelocationEstimatePhysicalExtent? extent)
                        ? block with { CurrentExtents = [extent] }
                        : block)], model, diskBytes),
                LinkChange(blocks, placements[(zone, criterion)], model, diskBytes));
            results.Add(new RelocationEstimateEvaluation(session.SourcePath, session.SessionId, Totals(blocks),
                Totals(blocks.Where(block => !trainingKeys.Contains(new BlockKey(block.Path, block.BlockNumber)))),
                ModelMetrics(blocks, model, diskBytes),
                [.. zoneMib.Select(size => new RelocationEstimateEvaluationZone(size, Placed(size, "latency"), Placed(size, "requests"), Placed(size, "link_gain")))]));
        }

        return results;
    }

    private static RelocationEstimateModelMetrics ModelMetrics(IReadOnlyList<RelocationEstimateBlock> ordered, RelocationEstimateModel model, long diskBytes) =>
        RelocationEstimateSegments.Metrics(RelocationEstimateSegments.Build(ordered, _ => true), model, diskBytes);

    private static RelocationEstimateAnalysis Analyze(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        IReadOnlyList<RelocationEstimateBlock> line,
        IReadOnlyList<long> zoneMib,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts,
        bool planProvided,
        RelocationEstimateModel model,
        long diskBytes,
        Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>>? placements)
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
        RelocationEstimateModelMetrics currentModel = ModelMetrics(line, model, diskBytes);
        var scenarios = new List<RelocationEstimateScenario>(zoneMib.Count);
        foreach (long size in zoneMib)
        {
            scenarios.Add(new RelocationEstimateScenario(size,
                CurrentZoneCoverage(blocks, size, starts),
                Select(blocks, line, total, size, "latency", starts, model, diskBytes, currentModel, placements),
                Select(blocks, line, total, size, "requests", starts, model, diskBytes, currentModel, placements),
                SelectByLinkGain(blocks, line, total, size, starts, model, diskBytes, currentModel, placements)));
        }

        return new RelocationEstimateAnalysis(total, movable, unmovable, changed, missingFile, missingSection, unavailable,
            coverage, policy, nonPaging, pagingOnly, pagingUnknown, current, scenarios,
            RelocationEstimateSegments.Stats(RelocationEstimateSegments.Build(line, _ => true)), currentModel);
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
        IReadOnlyList<RelocationEstimateBlock> line,
        RelocationEstimateTotals total,
        long zoneMib,
        string criterion,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts,
        RelocationEstimateModel model,
        long diskBytes,
        RelocationEstimateModelMetrics currentModel,
        Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>>? placements)
    {
        long zoneBytes = checked(zoneMib * Mib);
        var selected = new HashSet<BlockKey>(new BlockKeyComparer());
        foreach (IGrouping<string, RelocationEstimateBlock> volume in blocks
            .Where(IsSelectable)
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

        return Describe(blocks, line, total, zoneMib, criterion, selected, starts, model, diskBytes, currentModel, placements, null);
    }

    /// <summary>고른 블록 집합의 기존 지표, 가상 배치의 순서 지표, 모형 지표를 계산합니다.</summary>
    private static RelocationEstimateSelection Describe(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        IReadOnlyList<RelocationEstimateBlock> line,
        RelocationEstimateTotals total,
        long zoneMib,
        string criterion,
        HashSet<BlockKey> selected,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts,
        RelocationEstimateModel model,
        long diskBytes,
        RelocationEstimateModelMetrics currentModel,
        Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>>? placements,
        double? realizedGainMs)
    {
        long zoneBytes = checked(zoneMib * Mib);
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

        (List<RelocationEstimateBlock> virtualBlocks, Dictionary<BlockKey, RelocationEstimatePhysicalExtent> map) = Place(blocks, selected, zoneBytes, starts);
        (List<RelocationEstimateBlock> lineVirtual, Dictionary<BlockKey, RelocationEstimatePhysicalExtent> lineMap) = ReferenceEquals(blocks, line)
            ? (virtualBlocks, map)
            : Place(line, selected, zoneBytes, starts);
        if (placements is not null)
        {
            // 평가는 선정에 쓴 순서(최근 순 한 줄)로 놓은 구역 위치를 씁니다.
            placements[(zoneMib, criterion)] = lineMap;
        }

        RelocationEstimateModelMetrics virtualModel = ModelMetrics(lineVirtual, model, diskBytes);
        return new RelocationEstimateSelection(criterion, zoneMib, selectedTotals,
            Ratio(selectedTotals.ReadBytes, total.ReadBytes), Ratio(selectedTotals.LatencySumUs, total.LatencySumUs),
            movedBlocks, toMove, alreadyInZone, required, Sequence(virtualBlocks),
            virtualModel, currentModel.PositionMs - virtualModel.PositionMs, realizedGainMs);
    }

    /// <summary>고른 블록을 읽힌 순서대로 볼륨별 구역 시작부터 붙여 놓은 가상 배치입니다. 고른 블록의 가상 위치도 함께 돌려줍니다.</summary>
    private static (List<RelocationEstimateBlock> Blocks, Dictionary<BlockKey, RelocationEstimatePhysicalExtent> Map) Place(
        IReadOnlyList<RelocationEstimateBlock> ordered,
        HashSet<BlockKey> selected,
        long zoneBytes,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts)
    {
        var virtualBlocks = new List<RelocationEstimateBlock>(ordered.Count);
        var map = new Dictionary<BlockKey, RelocationEstimatePhysicalExtent>(new BlockKeyComparer());
        foreach (IGrouping<string, RelocationEstimateBlock> volume in ordered.GroupBy(block => block.VolumeKey, StringComparer.OrdinalIgnoreCase))
        {
            long zoneStart = starts.TryGetValue(volume.Key, out RelocationEstimateZoneStart? start) ? start.StartByte ?? 0 : 0;
            long offset = 0;
            foreach (RelocationEstimateBlock block in volume.Where(block => selected.Contains(new BlockKey(block.Path, block.BlockNumber)))
                .OrderBy(block => block.FirstObservedUs).ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                long end = checked(zoneStart + offset + block.OccupiedBytes);
                var extent = new RelocationEstimatePhysicalExtent(checked(zoneStart + offset), end);
                map[new BlockKey(block.Path, block.BlockNumber)] = extent;
                virtualBlocks.Add(block with { CurrentExtents = [extent] });
                offset = checked(offset + block.OccupiedBytes);
            }

            virtualBlocks.AddRange(volume.Where(block => !selected.Contains(new BlockKey(block.Path, block.BlockNumber))));
        }

        return (virtualBlocks, map);
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

    private static List<RelocationEstimateBlock> Merge(IReadOnlyList<RelocationEstimateSession> sessions,
        IReadOnlyDictionary<RelocationEstimateSession, double> weights)
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

        // 블록의 가중치는 그 블록이 나온 플레이들의 가중치 합입니다.
        return grouped.Values.Select(entries => MergeBlocks(entries) with { Weight = entries.Sum(entry => weights[entry.Session]) })
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
