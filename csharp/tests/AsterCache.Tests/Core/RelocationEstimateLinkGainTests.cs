using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class RelocationEstimateLinkGainTests
{
    private static readonly RelocationEstimateZoneStart Zone = new(@"D:\", "disk:1", 0, 0, "argument");

    // 위치 잡는 시간이 거리와 무관하게 10 ms, 전송률 100 MB/s(= 100,000 바이트/ms) 인 손계산용 모형입니다.
    private static readonly RelocationEstimateModel Flat = new(10, 0, 100, 10_000);

    [Fact]
    public void ReadSegmentsJoinAdjacentBlocksAndSplitInsideFragmentedBlocks()
    {
        // A0 [0,100] 과 A1 [100,200] 은 이어지고, B [500,600] 과 C 첫 조각 [600,650] 도 이어집니다.
        // C 는 [650,900] 을 건너뛰어 [900,950] 로 끊기고(블록 안 끊김), D [950,1000] 이 그 뒤에 이어집니다.
        RelocationEstimateBlock[] blocks =
        [
            Block("a", 0, 0, 0, 100),
            Block("a", 1, 1, 100, 200),
            Block("b", 0, 2, 500, 600),
            Block("c", 0, 3, [(600, 650), (900, 950)]),
            Block("d", 0, 4, 950, 1000),
        ];

        List<ReadSegment> segments = RelocationEstimateSegments.Build(blocks, _ => true);

        Assert.Equal(3, segments.Count);
        Assert.Equal([(0L, 200L), (500L, 650L), (900L, 1000L)], segments.Select(s => (s.StartByte, s.EndByte)).ToArray());
        Assert.Equal([false, true, true], segments.Select(s => s.LinkedFromPrevious).ToArray());
        Assert.Equal([false, false, true], segments.Select(s => s.BreakInsideBlock).ToArray());
        // 조각이 끊긴 블록 c 는 두 구간에 물리적으로 들어 있어 두 구간 모두에서 셉니다(이중 계산은 이음 평균에서만 뺌).
        Assert.Equal([2, 2, 2], segments.Select(s => s.BlockCount).ToArray());
        Assert.Equal([2d, 2d, 2d], segments.Select(s => s.WeightSum).ToArray());
        Assert.Equal(2, segments[2].Blocks.Count);

        // 이음은 200→500(300), 650→900(250) 두 개이고 합쳐 20 ms, 전송은 450 바이트입니다.
        RelocationEstimateModelMetrics metrics = RelocationEstimateSegments.Metrics(segments, Flat, 10_000);
        Assert.Equal(3, metrics.Segments);
        Assert.Equal(2, metrics.Links);
        Assert.Equal(20d, metrics.PositionMs, 9);
        Assert.Equal(450 / 100_000d, metrics.TransferMs, 12);
        Assert.Equal(275d, metrics.MedianLinkDistanceBytes);
        Assert.Equal(300L, metrics.P90LinkDistanceBytes);
        Assert.Equal(300L, RelocationEstimateSegments.MaximumLinkDistance(segments));
    }

    [Fact]
    public void BlockWithoutLocationBreaksTheChainAndUnselectableBlockMarksItsSegment()
    {
        RelocationEstimateBlock unknown = Block("u", 0, 1, 0, 0) with { CurrentExtents = [] };
        RelocationEstimateBlock[] blocks =
        [
            Block("a", 0, 0, 0, 100),
            unknown,
            Block("b", 0, 2, 100, 200),
            Block("c", 0, 3, 300, 400, policy: false),
        ];

        List<ReadSegment> segments = RelocationEstimateSegments.Build(blocks, block => block.PolicyCandidate != false);

        // a 와 b 는 디스크에서는 붙어 있지만 사이의 블록 위치를 몰라 이음이 끊겨 서로 다른 구간입니다.
        Assert.Equal(3, segments.Count);
        Assert.False(segments[1].LinkedFromPrevious);
        Assert.True(segments[2].LinkedFromPrevious);
        Assert.Equal([true, true, false], segments.Select(s => s.Selectable).ToArray());
    }

    [Fact]
    public void PositionTimeFollowsBasePlusSquareRootOfDistanceFraction()
    {
        var model = new RelocationEstimateModel(6, 12, 150, 1000);

        Assert.Equal(0d, model.PositionMs(0, 1000));
        Assert.Equal(12d, model.PositionMs(250, 1000), 9); // 6 + 12 * sqrt(0.25)
        Assert.Equal(18d, model.PositionMs(1000, 1000), 9);
        Assert.Equal(18d, model.PositionMs(5000, 1000), 9); // 디스크 크기보다 길면 디스크 크기로 자릅니다
        Assert.Equal(6 + 12 * Math.Sqrt(0.1), model.PositionMs(100, 1000), 9);
        Assert.Equal(1_500_000 / 150_000d, model.TransferMs(1_500_000), 9);
    }

    [Fact]
    public void SolverPicksDifferentRunsForDifferentBudgetsAndHandlesLargeAndTinyBudgets()
    {
        // 두 묶음이 서로 이어지지 않습니다. 앞 묶음은 U0-U1 이득 100(바이트당 5), 뒤 묶음은 U2-U3 이득 60(바이트당 3), 단위마다 10 바이트.
        LinkUnit[] units = [Unit(10), Unit(10, linkGain: 100), Unit(10), Unit(10, linkGain: 60)];

        Assert.Equal([false, false, false, false], LinkGainSolver.Solve(units, 5).Selected); // 가장 작은 단위보다 작은 예산
        Assert.Equal([false, false, false, false], LinkGainSolver.Solve(units, 9).Selected);
        Assert.Equal([true, true, false, false], LinkGainSolver.Solve(units, 20).Selected); // 이득 100
        Assert.Equal([true, true, false, false], LinkGainSolver.Solve(units, 30).Selected); // 남는 10 바이트로는 뒤 묶음을 못 넣음
        Assert.Equal([true, true, true, true], LinkGainSolver.Solve(units, 40).Selected); // 이득 160
        Assert.Equal([true, true, true, true], LinkGainSolver.Solve(units, 1000).Selected); // 예산이 전체보다 큰 예
        Assert.Equal(160d, LinkGainSolver.Gain(units, LinkGainSolver.Solve(units, 40).Selected));
    }

    [Fact]
    public void SolverMayLeaveBudgetUnusedWhenGainsGrowWithLongerRunsAndTheGapStaysWithinTheStatedBound()
    {
        // U0-U1 100, U1-U2 30, U2-U3 90: 묶음이 길수록 바이트당 이득이 커서 최적 집합이 λ 풀이의 볼록 껍질 밖에 있습니다.
        // 예산 30 의 전수 최적은 {U0,U1,U2}=130 이지만 λ 풀이는 아무것도 고르지 않습니다(예산 안에서 가장 큰 집합).
        // 상한: 전수 최적 - λ 풀이 이득 <= λ × (예산 - 고른 바이트), 여기서 λ = 5.5 (220 / 40).
        LinkUnit[] units = [Unit(10), Unit(10, linkGain: 100), Unit(10, linkGain: 30), Unit(10, linkGain: 90)];

        LinkGainSolution solution = LinkGainSolver.Solve(units, 30);

        Assert.Equal([false, false, false, false], solution.Selected);
        Assert.Equal(5.5, solution.Lambda, 6);
        Assert.Equal(130d, Exhaustive(units, 30));
        Assert.True(130d - 0d <= solution.Lambda * 30 + 1e-6);
    }

    [Fact]
    public void SolverNeverRealizesLinksNextToAnUnselectableUnit()
    {
        // U2 는 고를 수 없고, 양옆 이음(1000)은 실현되지 않습니다.
        LinkUnit[] units = [Unit(10), Unit(10, linkGain: 50), Unit(10, linkGain: 1000, selectable: false), Unit(10, linkGain: 1000), Unit(10, linkGain: 40)];

        LinkGainSolution all = LinkGainSolver.Solve(units, 1000);
        Assert.Equal([true, true, false, true, true], all.Selected);
        Assert.Equal(90d, LinkGainSolver.Gain(units, all.Selected));

        LinkGainSolution twenty = LinkGainSolver.Solve(units, 20);
        Assert.Equal([true, true, false, false, false], twenty.Selected);
        Assert.Equal(50d, LinkGainSolver.Gain(units, twenty.Selected));
    }

    [Fact]
    public void SolverStaysCloseToTheExhaustiveOptimumOnSmallRandomInputs()
    {
        var random = new Random(20261118);
        int compared = 0, searched = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            int n = random.Next(1, 11);
            var units = new LinkUnit[n];
            for (int i = 0; i < n; i++)
            {
                bool linked = i > 0 && random.NextDouble() > 0.2;
                units[i] = new LinkUnit
                {
                    Bytes = random.Next(1, 21),
                    Selectable = random.NextDouble() > 0.2,
                    LinkedFromPrevious = linked,
                    LinkGain = linked ? random.Next(1, 101) : 0,
                    SelfGain = random.NextDouble() < 0.15 ? random.Next(1, 30) : 0,
                };
            }

            long selectableBytes = units.Where(unit => unit.Selectable).Sum(unit => unit.Bytes);
            foreach (long budget in new long[] { 1, selectableBytes / 3, selectableBytes / 2, Math.Max(1, selectableBytes - 1), selectableBytes })
            {
                LinkGainSolution solution = LinkGainSolver.Solve(units, budget);
                double optimum = Exhaustive(units, budget);
                double gain = Gain(units, solution.Selected);
                long bytes = units.Where((_, i) => solution.Selected[i]).Sum(unit => unit.Bytes);

                Assert.True(bytes <= budget);
                Assert.True(gain <= optimum + 1e-9);
                if (solution.Searched)
                {
                    // 이분 탐색이 실제로 좁혀졌는지: 위 끝의 집합은 예산 안, 아래 끝의 집합은 예산 밖, 간격은 48회 뒤 (처음 위 끝)/2^48 입니다.
                    Assert.Equal(solution.Selected, LinkGainSolver.Best(units, solution.Lambda));
                    Assert.True(solution.LowerLambda == 0 || LinkGainSolver.Bytes(units, LinkGainSolver.Best(units, solution.LowerLambda)) > budget);
                    Assert.True(solution.Lambda - solution.LowerLambda <= solution.InitialUpper / Math.Pow(2, LinkGainSolver.Iterations) * (1 + 1e-9));
                    searched++;
                }

                // 명시한 상한: λ 에서 최적인 집합의 값에 대해 (전수 최적 - λ 풀이 이득) <= λ × (예산 - 고른 바이트) 입니다.
                // 예산이 후보 전체 이상이면 후보 전체를 골라 차이가 없습니다.
                double bound = selectableBytes <= budget ? 0 : solution.Lambda * (budget - bytes);
                Assert.True(optimum - gain <= bound + 1e-6, $"trial {trial} budget {budget}: optimum {optimum}, solver {gain}, bound {bound}");
                compared++;
            }
        }

        Assert.Equal(1500, compared);
        Assert.True(searched > 600, $"only {searched} of {compared} cases needed the lambda search");
    }

    [Fact]
    public void LinkGainWeightsPlaysByRecencyAndAveragesBothSides()
    {
        // 새 플레이에는 X, Y 가 있고 옛 플레이에는 X 만 있습니다. 가중치 X = 1 + 0.7, Y = 1, 이음 가중치는 평균 1.35 입니다.
        var recent = new RelocationEstimateSession("new.io.json", "new", DateTimeOffset.UnixEpoch.AddDays(2),
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);
        var older = new RelocationEstimateSession("old.io.json", "old", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("x", 0, 0, 1000, 1100)]);

        RelocationEstimateSelection selection = Calculate([older, recent], [1]).Combined.Scenarios[0].ByLinkGain;

        Assert.Equal(1.35 * 10, selection.RealizedGainMs!.Value, 9);
        Assert.Equal(2, selection.Selected.Blocks);
        // 고른 둘은 구역 시작부터 붙어 하나의 읽기 구간이 됩니다.
        Assert.Equal(1, selection.Model.Segments);
        Assert.Equal(0d, selection.Model.PositionMs);
    }

    [Fact]
    public void SessionDecayChangesTheLinkWeight()
    {
        var recent = new RelocationEstimateSession("new.io.json", "new", DateTimeOffset.UnixEpoch.AddDays(2),
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);
        var older = new RelocationEstimateSession("old.io.json", "old", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("x", 0, 0, 1000, 1100)]);

        RelocationEstimateSelection selection = RelocationEstimate.Calculate([older, recent], [1], [Zone], false, Flat, 0.5)
            .Combined.Scenarios[0].ByLinkGain;

        Assert.Equal((1.5 + 1) / 2 * 10, selection.RealizedGainMs!.Value, 9);
    }

    [Fact]
    public void UnselectableSegmentBetweenTwoBlocksRealizesNoGain()
    {
        // 가운데 블록은 정책상 제외라 고를 수 없습니다. 양쪽 이음의 이득은 실현되지 않고 읽기 구간은 줄지 않습니다.
        var session = new RelocationEstimateSession("a.io.json", "a", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("p", 0, 1, 3000, 3100, policy: false), Block("y", 0, 2, 5000, 5100)]);

        RelocationEstimateSelection selection = Calculate([session], [1]).Combined.Scenarios[0].ByLinkGain;

        Assert.Equal(0d, selection.RealizedGainMs!.Value);
        Assert.Equal(2, selection.Selected.Blocks);
        Assert.Equal(3, selection.Model.Segments);
        Assert.Equal(2, selection.Model.Links);
    }

    [Fact]
    public void EvaluationPlayIsPlacedBySelectionAndCountsBlocksUnseenInTraining()
    {
        var training = new RelocationEstimateSession("train.io.json", "train", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);
        var evaluation = new RelocationEstimateSession("eval.io.json", "eval", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100), Block("z", 0, 2, 9000, 9100)]);

        RelocationEstimateReport report = Calculate([training], [1], [evaluation]);

        RelocationEstimateEvaluation result = Assert.Single(report.Evaluations);
        Assert.Equal("eval", result.SessionId);
        Assert.Equal(3, result.Total.Blocks);
        Assert.Equal(1, result.UnseenInTraining.Blocks);
        Assert.Equal(1000, result.UnseenInTraining.ReadBytes);
        // 지금 배치: x→y 3900, y→z 3900 이라 구간 3개, 이음 2개, 20 ms.
        Assert.Equal(3, result.Current.Segments);
        Assert.Equal(20d, result.Current.PositionMs, 9);
        // 학습에서 x, y 를 구역으로 옮겼으면 둘이 붙어 구간 2개(x+y, z), 이음 1개(10 ms)입니다.
        RelocationEstimateEvaluationZone zone = Assert.Single(result.Zones);
        Assert.Equal(2, zone.ByLinkGain.Model.Segments);
        Assert.Equal(1, zone.ByLinkGain.Model.Links);
        Assert.Equal(10d, zone.ByLinkGain.Model.PositionMs, 9);
        // 없어진 이음은 x→y 하나(지금 10 ms), 구역 밖 z 로 가는 y→z 는 구역을 오가는 이음으로 그대로 10 ms 입니다.
        RelocationEstimateLinkChange change = zone.ByLinkGain.Change;
        Assert.Equal(new RelocationEstimateLinkGroup(1, 0, 10), change.Removed);
        Assert.Equal(new RelocationEstimateLinkGroup(1, 10, 10), change.Crossing);
        Assert.Equal(new RelocationEstimateLinkGroup(0, 0, 0), change.Inside);
        Assert.Equal(zone.ByLinkGain.Model.PositionMs - result.Current.PositionMs,
            change.Crossing.Ms - change.Crossing.BeforeMs + change.Inside.Ms - change.Inside.BeforeMs - change.Removed.BeforeMs, 9);
        // 평가용 플레이는 선정에 쓰이지 않습니다: 학습 쪽 합계에는 x, y 만 있습니다.
        Assert.Equal(2, report.Combined.Total.Blocks);
    }

    [Fact]
    public void EvaluationPlayReadInAnotherOrderLeavesNonZeroJoinsInsideTheZone()
    {
        var training = new RelocationEstimateSession("train.io.json", "train", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);
        var evaluation = new RelocationEstimateSession("eval.io.json", "eval", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("y", 0, 0, 5000, 5100), Block("x", 0, 1, 1000, 1100)]);

        RelocationEstimateEvaluationZone zone = Assert.Single(Assert.Single(Calculate([training], [1], [evaluation]).Evaluations).Zones);

        // 구역에는 x [0,100], y [100,200] 로 놓입니다. y 다음 x 를 읽으면 200 → 0 으로 0 이 아닌 이음이 구역 안에 생깁니다.
        Assert.Equal(new RelocationEstimateLinkGroup(0, 0, 0), zone.ByLinkGain.Change.Removed);
        Assert.Equal(new RelocationEstimateLinkGroup(0, 0, 0), zone.ByLinkGain.Change.Crossing);
        Assert.Equal(new RelocationEstimateLinkGroup(1, 10, 10), zone.ByLinkGain.Change.Inside);
        Assert.Equal(2, zone.ByLinkGain.Model.Segments);
    }

    [Fact]
    public void EvaluationUsesTheRecencyLineOrderThatSelectionUsed()
    {
        // 최근 플레이는 x, y 순, 옛 플레이는 y, x, z 순입니다. 선정이 쓰는 한 줄은 x, y, z 이고 시간순은 y, x, z 입니다.
        var recent = new RelocationEstimateSession("new.io.json", "new", DateTimeOffset.UnixEpoch.AddDays(2),
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);
        var older = new RelocationEstimateSession("old.io.json", "old", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("y", 0, 0, 5000, 5100), Block("x", 0, 1, 1000, 1100), Block("z", 0, 2, 9000, 9100)]);
        var evaluation = new RelocationEstimateSession("eval.io.json", "eval", DateTimeOffset.UnixEpoch.AddDays(3),
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100), Block("z", 0, 2, 9000, 9100)]);

        RelocationEstimateEvaluationZone zone = Assert.Single(Assert.Single(Calculate([older, recent], [1], [evaluation]).Evaluations).Zones);

        // 구역에 x, y, z 순으로 붙으면 x, y, z 를 읽는 평가 플레이는 읽기 구간 하나입니다(시간순으로 붙이면 3 개가 됩니다).
        Assert.Equal(1, zone.ByLinkGain.Model.Segments);
        Assert.Equal(1, zone.ByLatency.Model.Segments);
        Assert.Equal(1, zone.ByRequests.Model.Segments);
    }

    [Fact]
    public void BlockBrokenIntoThreePiecesKeepsFiniteWeightsAndMovesAsOneUnit()
    {
        // 한 블록의 조각 셋이 서로 떨어져 있으면 가운데 구간에는 센 블록이 없습니다. 가중치가 NaN 이 되면 안 됩니다.
        var session = new RelocationEstimateSession("a.io.json", "a", DateTimeOffset.UnixEpoch,
            [Block("w", 0, 0, [(1000, 1100), (3000, 3100), (5000, 5100)])]);

        RelocationEstimateSelection selection = Calculate([session], [1]).Combined.Scenarios[0].ByLinkGain;

        Assert.Equal(1, selection.Selected.Blocks);
        Assert.Equal(20d, selection.RealizedGainMs!.Value, 9); // 조각 사이 이음 둘, 가중치 1
        Assert.Equal(1, selection.Model.Segments);
    }

    [Fact]
    public void BrokenBlockCountsInBothSegmentsAndIsDeductedOnlyFromItsInsideLink()
    {
        // a [900,1000) 은 x 의 첫 조각 [1000,1100) 과 이어져 구간 하나가 되고, x 의 둘째 조각 [3000,3100) 은 블록 안 끊김 구간,
        // c [5000,5100) 이 그 뒤에 이어집니다. 감쇠 0.5 에서 가중치는 a = 1, c = 1, x = 1 + 0.5 (옛 플레이에도 있음).
        // 구간1 {a, x} 의 합 2.5·수 2, 구간2 {x} 의 합 1.5·수 1. 블록 안 이음 구간1→구간2 는 x 의 이중 계산을 빼 (2.5 + 1.5 - 1.5) / (2 + 1 - 1) = 1.25,
        // 구간2→구간3 은 x 가 구간2 에 들어 있으므로 (1.5 + 1) / 2 = 1.25.
        // 모든 이음이 10 ms 라 이득은 12.5 + 12.5 = 25 입니다(x 를 구간2 에서 빼면 둘째 이음이 1 * 10 이 되어 22.5).
        var recent = new RelocationEstimateSession("new.io.json", "new", DateTimeOffset.UnixEpoch.AddDays(2),
            [Block("a", 0, 0, 900, 1000), Block("x", 0, 1, [(1000, 1100), (3000, 3100)]), Block("c", 0, 2, 5000, 5100)]);
        var older = new RelocationEstimateSession("old.io.json", "old", DateTimeOffset.UnixEpoch.AddDays(1),
            [Block("x", 0, 1, [(1000, 1100), (3000, 3100)])]);

        RelocationEstimateSelection selection = RelocationEstimate.Calculate([older, recent], [1], [Zone], false, Flat, 0.5)
            .Combined.Scenarios[0].ByLinkGain;

        Assert.Equal(3, selection.Selected.Blocks);
        Assert.Equal(25d, selection.RealizedGainMs!.Value, 9);
    }

    [Fact]
    public void AllThreeCriteriaSkipPolicyExcludedBlocks()
    {
        var session = new RelocationEstimateSession("a.io.json", "a", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("p", 0, 1, 3000, 3100, policy: false), Block("y", 0, 2, 5000, 5100)]);

        RelocationEstimateScenario scenario = Calculate([session], [1]).Combined.Scenarios[0];

        Assert.Equal(2, scenario.ByLatency.Selected.Blocks);
        Assert.Equal(2, scenario.ByRequests.Selected.Blocks);
        Assert.Equal(2, scenario.ByLinkGain.Selected.Blocks);
    }

    [Fact]
    public void ModelPositionReductionIsTheRealChangeAndDiffersFromRealizedGain()
    {
        // t(d) = 10 + 10 * sqrt(d / 10000). x→y 400 (12 ms), y→p 1100. 구역에 x, y 가 붙으면 y 의 끝이 200 이 되어 y→p 는 2500 (15 ms).
        var model = new RelocationEstimateModel(10, 10, 100, 10_000);
        var session = new RelocationEstimateSession("a.io.json", "a", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 1500, 1600), Block("p", 0, 2, 2700, 2800, policy: false)]);

        RelocationEstimateSelection selection = RelocationEstimate.Calculate([session], [1], [Zone], false, model)
            .Combined.Scenarios[0].ByLinkGain;

        double expectedReduction = model.PositionMs(400, 10_000) + model.PositionMs(1100, 10_000) - model.PositionMs(2500, 10_000);
        Assert.Equal(model.PositionMs(400, 10_000), selection.RealizedGainMs!.Value, 9); // 0 으로 만든 이음의 지금 시간
        Assert.Equal(expectedReduction, selection.ModelPositionReductionMs, 9); // 실제 모형 감소
        Assert.NotEqual(selection.RealizedGainMs!.Value, selection.ModelPositionReductionMs);
    }

    [Fact]
    public void EvaluationSessionIdEqualToTrainingIdIsRejected()
    {
        var training = new RelocationEstimateSession("train.io.json", "same", DateTimeOffset.UnixEpoch, [Block("x", 0, 0, 1000, 1100)]);
        var evaluation = new RelocationEstimateSession("eval.io.json", "SAME", DateTimeOffset.UnixEpoch.AddDays(1), [Block("x", 0, 0, 1000, 1100)]);

        Assert.Throws<ArgumentException>(() => Calculate([training], [1], [evaluation]));
    }

    [Fact]
    public void ResultsDoNotDependOnInputOrderAndRepeatExactly()
    {
        RelocationEstimateSession[] sessions =
        [
            new("a.io.json", "a", DateTimeOffset.UnixEpoch.AddDays(3), [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100), Block("z", 0, 2, 9000, 9100)]),
            new("b.io.json", "b", DateTimeOffset.UnixEpoch.AddDays(2), [Block("y", 0, 0, 5000, 5100), Block("w", 0, 1, 2000, 2100), Block("x", 0, 2, 1000, 1100)]),
            new("c.io.json", "c", DateTimeOffset.UnixEpoch.AddDays(1), [Block("w", 0, 0, 2000, 2100), Block("z", 0, 1, 9000, 9100)]),
        ];
        var evaluation = new RelocationEstimateSession("e.io.json", "e", DateTimeOffset.UnixEpoch.AddDays(4),
            [Block("z", 0, 0, 9000, 9100), Block("x", 0, 1, 1000, 1100), Block("q", 0, 2, 7000, 7100)]);

        RelocationEstimateReport first = Calculate(sessions, [1], [evaluation]);
        RelocationEstimateReport again = Calculate(sessions, [1], [evaluation]);
        RelocationEstimateReport reversed = Calculate([.. sessions.Reverse()], [1], [evaluation]);

        foreach (RelocationEstimateReport other in new[] { again, reversed })
        {
            RelocationEstimateSelection expected = first.Combined.Scenarios[0].ByLinkGain;
            RelocationEstimateSelection actual = other.Combined.Scenarios[0].ByLinkGain;
            Assert.Equal(expected.Selected, actual.Selected);
            Assert.Equal(expected.Model, actual.Model);
            Assert.Equal(expected.RealizedGainMs, actual.RealizedGainMs);
            Assert.Equal(first.Combined.CurrentModel, other.Combined.CurrentModel);
            Assert.Equal(first.Evaluations[0].Zones[0].ByLinkGain, other.Evaluations[0].Zones[0].ByLinkGain);
            Assert.Equal(first.Evaluations[0].Current, other.Evaluations[0].Current);
        }
    }

    [Fact]
    public void ExistingCriteriaKeepTheirBlockChoiceAndGainModelMetrics()
    {
        var session = new RelocationEstimateSession("a.io.json", "a", DateTimeOffset.UnixEpoch,
            [Block("x", 0, 0, 1000, 1100), Block("y", 0, 1, 5000, 5100)]);

        RelocationEstimateScenario scenario = Calculate([session], [1]).Combined.Scenarios[0];

        Assert.Equal(2, scenario.ByLatency.Selected.Blocks);
        Assert.Equal(2, scenario.ByRequests.Selected.Blocks);
        Assert.Equal(1, scenario.ByLatency.Model.Segments);
        Assert.Null(scenario.ByLatency.RealizedGainMs);
    }

    private static RelocationEstimateReport Calculate(IReadOnlyList<RelocationEstimateSession> sessions, long[] zones,
        IReadOnlyList<RelocationEstimateSession>? evaluation = null) =>
        RelocationEstimate.Calculate(sessions, zones, [Zone], false, Flat, RelocationEstimate.DefaultSessionDecay, evaluation);

    private static LinkUnit Unit(long bytes, double linkGain = 0, bool selectable = true) => new()
    {
        Bytes = bytes,
        Selectable = selectable,
        LinkedFromPrevious = linkGain > 0,
        LinkGain = linkGain,
    };

    // 모든 부분집합을 나열해 예산 이하에서 실현되는 이득이 가장 큰 값을 구합니다(고를 수 없는 단위는 넣지 않음).
    private static double Exhaustive(LinkUnit[] units, long budget)
    {
        double best = 0;
        for (int mask = 0; mask < 1 << units.Length; mask++)
        {
            bool[] selected = [.. Enumerable.Range(0, units.Length).Select(i => (mask >> i & 1) == 1)];
            if (selected.Where((on, i) => on && !units[i].Selectable).Any()
                || units.Where((_, i) => selected[i]).Sum(unit => unit.Bytes) > budget)
            {
                continue;
            }

            best = Math.Max(best, Gain(units, selected));
        }

        return best;
    }

    private static double Gain(LinkUnit[] units, bool[] selected)
    {
        double gain = 0;
        for (int i = 0; i < units.Length; i++)
        {
            if (selected[i])
            {
                gain += units[i].SelfGain + (i > 0 && selected[i - 1] && units[i].LinkedFromPrevious ? units[i].LinkGain : 0);
            }
        }

        return gain;
    }

    private static RelocationEstimateBlock Block(string path, long number, long first, long start, long end, bool policy = true) =>
        Block(path, number, first, [(start, end)], policy);

    private static RelocationEstimateBlock Block(string path, long number, long first, (long Start, long End)[] pieces, bool policy = true) =>
        new(path, number, 1, 1000, 10, 10, first, 0, @"D:\", "disk:1", 64, pieces.Sum(piece => piece.End - piece.Start),
            EstimateBlockClass.Movable, null, [.. pieces.Select(piece => new RelocationEstimatePhysicalExtent(piece.Start, piece.End))],
            false, policy, null);
}
