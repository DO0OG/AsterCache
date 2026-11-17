using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class RelocationEstimateTests
{
    private static readonly RelocationEstimateZoneStart Zone = new(@"D:\", "disk:1", 10, 1000, "argument");

    [Fact]
    public void ClassificationPlanCoveragePagingAndSequenceMatchSmallExample()
    {
        RelocationEstimateBlock[] blocks =
        [
            Block("a.bin", 0, 1, 1000, 10, 2, 1, EstimateBlockClass.Movable, [Part(100, 164)], true),
            Block("b.bin", 1, 2, 2000, 20, 1, 1, EstimateBlockClass.Movable, [Part(200, 264)], false),
            Block("c.bin", 2, 3, 3000, 30, 3, 3, EstimateBlockClass.Unmovable, [Part(264, 328)], true, "sparse"),
            Block("gone.bin", 3, 4, 4000, 40, 1, 0, EstimateBlockClass.MissingFile, [], null),
        ];
        var session = new RelocationEstimateSession("summary.json", "session-1", DateTimeOffset.UnixEpoch, blocks);

        RelocationEstimateAnalysis result = RelocationEstimate.Calculate([session], [1], [Zone], planProvided: true).Combined;

        Assert.Equal(new RelocationEstimateTotals(4, 7, 10_000, 100, 40, 5), result.Total);
        Assert.Equal(2, result.Movable.Blocks);
        Assert.Equal(new RelocationEstimateTotals(1, 3, 3000, 30, 30, 3), result.Unmovable["sparse"]);
        Assert.Equal(1, result.MissingFile.Blocks);
        Assert.Equal(2, result.PlanCoverage!.Inside.Blocks);
        Assert.Equal(4000, result.PlanCoverage.Inside.ReadBytes);
        Assert.Equal(40, result.PlanCoverage.Inside.LatencySumUs);
        Assert.Equal(1, result.PlanCoverage.Outside.Blocks);
        Assert.Equal(1, result.PlanCoverage.Unknown.Blocks);
        Assert.Equal(3, result.Scenarios[0].CurrentZoneCoverage.Outside.Blocks);
        Assert.Equal(6000, result.Scenarios[0].CurrentZoneCoverage.Outside.ReadBytes);
        Assert.Equal(60, result.Scenarios[0].CurrentZoneCoverage.Outside.LatencySumUs);
        Assert.Equal(1, result.Scenarios[0].CurrentZoneCoverage.Unknown.Blocks);
        Assert.Equal(2, result.NonPaging.Blocks);
        Assert.Equal(2, result.PagingOnly.Blocks);
        Assert.Equal(0, result.PagingUnknown.Blocks);

        Assert.Equal(36L, result.CurrentSequence.MovementBytes);
        Assert.Equal(2L, result.CurrentSequence.ContiguousRuns);
        Assert.Equal(18d, result.CurrentSequence.MedianDistanceBytes);
        Assert.Equal(36L, result.CurrentSequence.P90DistanceBytes);

        RelocationEstimateSelection selected = result.Scenarios[0].ByLatency;
        Assert.Equal(2L, selected.Selected.Blocks);
        Assert.Equal(3000L, selected.Selected.ReadBytes);
        Assert.Equal(30L, selected.Selected.LatencySumUs);
        Assert.Equal(2L, selected.MovedBlocks);
        Assert.Equal(128L, selected.BytesToMove);
        Assert.Equal(128L, selected.RequiredContiguousBytes);
        Assert.Equal(864L, selected.VirtualSequence.MovementBytes);
        Assert.Equal(2L, selected.VirtualSequence.ContiguousRuns);
        Assert.Equal(2L, result.PolicyCoverage.Candidate.Blocks);
    }

    [Fact]
    public void SelectionUsesLatencyOrRequestCountsAndMergesSessionsOnce()
    {
        var firstBlocks = new List<RelocationEstimateBlock>
        {
            Block("hot.bin", 0, 1, 10_000, 10, 100, 0, EstimateBlockClass.Movable, [Part(0, 65536)], false),
        };
        for (long block = 1; block < 17; block++)
        {
            firstBlocks.Add(Block("hot.bin", block, block + 1, 1000, 100, 1, 0, EstimateBlockClass.Movable,
                [Part(block * 65536, (block + 1) * 65536)], false));
        }

        var first = new RelocationEstimateSession("one.io.json", "session-1", DateTimeOffset.UnixEpoch, firstBlocks);
        var second = new RelocationEstimateSession("two.io.json", "session-2", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("hot.bin", 1, 1, 500, 50, 5, 0, EstimateBlockClass.Movable, [Part(65536, 131072)], false)]);
        var duplicate = new RelocationEstimateSession("duplicate.io.json", "session-2", DateTimeOffset.UnixEpoch.AddDays(2),
            [Block("hot.bin", 0, 99, 99_000, 99_000, 99, 0, EstimateBlockClass.Movable, [Part(0, 65536)], false)]);

        RelocationEstimateReport report = RelocationEstimate.Calculate([first, second, duplicate], [1], [Zone]);

        Assert.Equal(2, report.Sessions.Count);
        Assert.Equal(["session-2"], report.DuplicateSessionIds);
        Assert.Equal(17, report.Combined.Total.Blocks);
        Assert.Equal(121, report.Combined.Total.Requests);
        Assert.Equal(100, report.Combined.Total.LatencyMaxUs);
        RelocationEstimateScenario scenario = Assert.Single(report.Combined.Scenarios);
        Assert.Equal(16, scenario.ByLatency.Selected.Blocks);
        Assert.Equal(16_500, scenario.ByLatency.Selected.ReadBytes);
        Assert.Equal(10_000 + 15_500, scenario.ByRequests.Selected.ReadBytes);
    }

    [Fact]
    public void ShortFinalBlockUsesClusterRoundedOccupancyForBudgetAndVirtualGap()
    {
        RelocationEstimateBlock[] blocks =
        [
            Block("a.bin", 0, 1, 4097, 20, 2, 0, EstimateBlockClass.Movable, [Part(2_000_000, 2_008_192)], false,
                logicalBytes: 4097, occupiedBytes: 8192),
            Block("b.bin", 0, 2, 65_536, 10, 1, 0, EstimateBlockClass.Movable, [Part(3_000_000, 3_065_536)], false),
            Block("fixed.bin", 0, 3, 1, 0, 0, 0, EstimateBlockClass.Unmovable, [Part(8192, 12_288)], false, "compressed",
                logicalBytes: 4096, occupiedBytes: 4096),
        ];

        RelocationEstimateAnalysis result = RelocationEstimate.Calculate(
            [new RelocationEstimateSession("summary", "short-final", DateTimeOffset.UnixEpoch, blocks)], [1],
            [new RelocationEstimateZoneStart(@"D:\", "disk:1", 0, 0, "argument")]).Combined;

        RelocationEstimateSelection selected = result.Scenarios[0].ByLatency;
        Assert.Equal(73_728, selected.RequiredContiguousBytes);
        Assert.Equal(65_536, selected.VirtualSequence.MovementBytes);
        Assert.Equal(2, selected.VirtualSequence.Transitions);
    }

    [Fact]
    public void EveryPhysicalPieceMustBeInZoneAndOnlyOutsidePieceBytesCountAsMovement()
    {
        RelocationEstimateBlock[] blocks =
        [
            Block("fragmented.bin", 0, 1, 100, 100, 5, 0, EstimateBlockClass.Movable,
                [Part(1000, 1100), Part(2_000_000, 2_000_100)], false, occupiedBytes: 200),
            Block("inside.bin", 0, 2, 100, 50, 2, 0, EstimateBlockClass.Movable, [Part(2000, 2100)], false,
                occupiedBytes: 100),
        ];

        RelocationEstimateAnalysis result = RelocationEstimate.Calculate(
            [new RelocationEstimateSession("summary", "zone-pieces", DateTimeOffset.UnixEpoch, blocks)], [1],
            [new RelocationEstimateZoneStart(@"D:\", "disk:1", 0, 0, "argument")]).Combined;

        RelocationEstimateSelection selected = result.Scenarios[0].ByLatency;
        Assert.Equal(1, selected.BlocksAlreadyInZone);
        Assert.Equal(1, selected.MovedBlocks);
        Assert.Equal(100, selected.BytesToMove);
        Assert.Equal(1, result.Scenarios[0].CurrentZoneCoverage.Inside.Blocks);
        Assert.Equal(1, result.Scenarios[0].CurrentZoneCoverage.Outside.Blocks);
    }

    [Fact]
    public void SequenceKeepsLogicalExtentOrderAndCountsIntraBlockMovement()
    {
        RelocationEstimateBlock[] blocks =
        [
            Block("reverse.bin", 0, 1, 20, 1, 1, 0, EstimateBlockClass.Movable,
                [Part(1000, 1010), Part(100, 110)], false, occupiedBytes: 20),
            Block("next.bin", 0, 2, 10, 1, 1, 0, EstimateBlockClass.Movable, [Part(110, 120)], false, occupiedBytes: 10),
        ];

        RelocationEstimateSequence result = RelocationEstimate.Calculate(
            [new RelocationEstimateSession("summary", "reverse", DateTimeOffset.UnixEpoch, blocks)], [1], [Zone]).Combined.CurrentSequence;

        Assert.Equal(2, result.Transitions);
        Assert.Equal(910, result.MovementBytes);
        Assert.Equal(2, result.ContiguousRuns);
    }

    [Fact]
    public void SessionMergeIsInputOrderIndependentAndChangedStateDropsAllLocations()
    {
        var stable = new RelocationEstimateSession("a", "stable", DateTimeOffset.UnixEpoch,
            [Block("same.bin", 0, 1, 20, 4, 2, 0, EstimateBlockClass.Movable, [Part(100, 120)], true)]);
        var changed = new RelocationEstimateSession("b", "changed", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("same.bin", 0, 2, 30, 6, 3, 0, EstimateBlockClass.ChangedDuringSession, [Part(200, 230)], true)]);

        RelocationEstimateAnalysis forward = RelocationEstimate.Calculate([stable, changed], [1], [Zone], planProvided: true).Combined;
        RelocationEstimateAnalysis reversed = RelocationEstimate.Calculate([changed, stable], [1], [Zone], planProvided: true).Combined;

        Assert.Equal(forward.Total, reversed.Total);
        Assert.Equal(forward.ChangedDuringSession, reversed.ChangedDuringSession);
        Assert.Equal(forward.CurrentSequence, reversed.CurrentSequence);
        Assert.Equal(forward.PlanCoverage, reversed.PlanCoverage);
        Assert.Equal(forward.PolicyCoverage.Candidate, reversed.PolicyCoverage.Candidate);
        Assert.Equal(1, forward.ChangedDuringSession.Blocks);
        Assert.Equal(1, forward.CurrentSequence.BlocksWithoutLocation);
        Assert.Equal(0, forward.CurrentSequence.BlocksWithLocation);
    }

    [Fact]
    public void PolicyCandidatesKeepMovableAndExcludedTotalsSeparate()
    {
        RelocationEstimateBlock[] blocks =
        [
            Block("candidate.bin", 0, 1, 100, 10, 1, 0, EstimateBlockClass.Movable, [Part(10, 110)], true),
            Block("excluded.bin", 0, 2, 200, 20, 2, 0, EstimateBlockClass.Movable, [Part(110, 310)], true,
                policyCandidate: false, policyReason: "volatile"),
            Block("unknown.bin", 0, 3, 300, 30, 3, 0, EstimateBlockClass.Movable, [Part(310, 610)], true,
                policyCandidate: null),
        ];

        RelocationEstimateAnalysis result = RelocationEstimate.Calculate(
            [new RelocationEstimateSession("summary", "policy", DateTimeOffset.UnixEpoch, blocks)], [1], [Zone]).Combined;

        Assert.Equal(3, result.Movable.Blocks);
        Assert.Equal(1, result.PolicyCoverage.Candidate.Blocks);
        Assert.Equal(1, result.PolicyCoverage.ExcludedByReason["volatile"].Blocks);
        Assert.Equal(1, result.PolicyCoverage.Unknown.Blocks);
    }

    [Fact]
    public void CheckedZoneAndTimestampArithmeticRejectsOverflow()
    {
        var empty = new RelocationEstimateSession("summary", "overflow", DateTimeOffset.UnixEpoch, []);
        Assert.Throws<OverflowException>(() => RelocationEstimate.Calculate([empty], [long.MaxValue], [Zone]));

        var block = Block("a.bin", 0, 1, 1, 1, 1, 0, EstimateBlockClass.Movable, [Part(1, 2)], false);
        var highZone = new RelocationEstimateZoneStart(@"D:\", "disk:1", 0, long.MaxValue - 1, "argument");
        Assert.Throws<OverflowException>(() => RelocationEstimate.Calculate(
            [new RelocationEstimateSession("summary", "zone-end", DateTimeOffset.UnixEpoch, [block])], [1], [highZone]));

        var timestamp = new RelocationEstimateSession("summary", "timestamp", DateTimeOffset.MaxValue,
            [Block("a.bin", 0, long.MaxValue, 1, 1, 1, 0, EstimateBlockClass.Movable, [Part(0, 1)], false)]);
        Assert.Throws<OverflowException>(() => RelocationEstimate.Calculate([timestamp], [1], [Zone]));
    }

    private static RelocationEstimatePhysicalExtent Part(long start, long end) => new(start, end);

    private static RelocationEstimateBlock Block(string path, long number, long first, long bytes, long latency,
        long requests, long paging, EstimateBlockClass classification, IReadOnlyList<RelocationEstimatePhysicalExtent> parts,
        bool? inPlan, string? reason = null, long logicalBytes = 64, long? occupiedBytes = null,
        bool? policyCandidate = true, string? policyReason = null) =>
        new(path, number, requests, bytes, latency, latency, first, paging, @"D:\", "disk:1", logicalBytes,
            occupiedBytes ?? parts.Sum(part => part.EndByte - part.StartByte), classification, reason, parts, inPlan,
            classification == EstimateBlockClass.Movable ? policyCandidate : null, policyReason);
}
