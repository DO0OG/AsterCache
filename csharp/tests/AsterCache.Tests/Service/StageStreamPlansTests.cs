using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 시작 목록과 단계 색인의 교집합으로 단계 스트림의 목록을 고르는 순수 함수.
public sealed class StageStreamPlansTests
{
    private const long Mib = 1L << 20;
    private static readonly IReadOnlySet<string> NoVolatile = new HashSet<string>();

    private static TraceStageIndex Index(string firstExe = "cod.exe") => new(
    [
        new TraceStage(firstExe, 0, [new TraceEvent("a.bin", 0, Mib)]),
        new TraceStage("sp26-cod.exe", 5000, [new TraceEvent("a.bin", Mib, Mib), new TraceEvent("v.bin", 0, Mib)]),
    ]);

    private static readonly ReadOp[] Ops = [new("a.bin", 0, 4 * Mib), new("v.bin", 0, 2 * Mib)];

    [Fact]
    public void FirstPlanWithoutTwoStagesIsNull()
    {
        Assert.Null(StageStreamPlans.First(Ops, 0, null, NoVolatile));
        Assert.Null(StageStreamPlans.First(Ops, 0, new TraceStageIndex([new TraceStage("cod.exe", 0, [])]), NoVolatile));
    }

    [Fact]
    public void FirstPlanKeepsTheHeadAndUnassignedBlocksAndAheadIsOnlyTheNextStageInsideTheStartList()
    {
        StageFirstPlan plan = StageStreamPlans.First(Ops, 0, Index(), new HashSet<string>(["v.bin"]))!;

        Assert.Equal("sp26-cod.exe", plan.AheadExe);
        Assert.Equal([new ReadOp("a.bin", Mib, Mib)], plan.AheadOps); // 가변 파일 (v.bin) 은 빠짐
        Assert.Equal([new ReadOp("a.bin", 0, Mib), new ReadOp("a.bin", 2 * Mib, 2 * Mib), new ReadOp("v.bin", Mib, Mib)], plan.Ops); // v.bin 앞 1 MiB 는 다음 단계 몫
        Assert.Equal(Mib, plan.VolatileSkippedBytes); // 다음 단계의 v.bin 1 MiB 만 (시작 목록의 v.bin 2 MiB 전체가 아님)
        Assert.All(plan.AheadOps, ahead => Assert.Contains(Ops, op => op.Path == ahead.Path && op.Offset <= ahead.Offset && op.Offset + op.Length >= ahead.Offset + ahead.Length)); // 시작 목록의 부분집합
    }

    [Fact]
    public void LaterPlanUsesItsStageAndTheFirstStageNameMeansStageZero()
    {
        StageLaterPlan second = StageStreamPlans.Later(Ops, Index(), "SP26-COD.EXE", NoVolatile);
        StageLaterPlan first = StageStreamPlans.Later(Ops, Index(), "cod.exe", NoVolatile);

        Assert.True(second.HasStage);
        Assert.Equal([new ReadOp("a.bin", Mib, Mib), new ReadOp("v.bin", 0, Mib)], second.Ops);
        Assert.Equal(Mib, StageStreamPlans.Later(Ops, Index(), "sp26-cod.exe", new HashSet<string>(["v.bin"])).VolatileSkippedBytes);
        Assert.True(first.HasStage);
        Assert.Equal([new ReadOp("a.bin", 0, Mib)], first.Ops);
    }

    [Fact]
    public void LaterPlanForAnUnknownExeOrAnUnnamedFirstStageIsTheWholeStartListWithoutVolatileFiles()
    {
        var vol = new HashSet<string>(["v.bin"], StringComparer.OrdinalIgnoreCase);

        StageLaterPlan unknown = StageStreamPlans.Later(Ops, Index(), "crash.exe", vol);
        StageLaterPlan unnamed = StageStreamPlans.Later(Ops, Index(""), "cod.exe", NoVolatile);
        StageLaterPlan none = StageStreamPlans.Later(Ops, null, "cod.exe", NoVolatile);

        Assert.False(unknown.HasStage);
        Assert.Equal([new ReadOp("a.bin", 0, 4 * Mib)], unknown.Ops);
        Assert.Equal(2 * Mib, unknown.VolatileSkippedBytes);
        Assert.False(unnamed.HasStage);
        Assert.False(none.HasStage);
        Assert.Equal(Ops, none.Ops);
    }

    [Fact]
    public void SplitCutsBigOpsIntoPiecesInOrderWithoutChangingTheBytesAndLeavesSmallOnesAlone()
    {
        ReadOp big = new("big.bin", 100, 80 * Mib);
        ReadOp small = new("small.bin", 0, 5 * Mib);

        List<ReadOp> pieces = StageStreamPlans.Split([small, big]);

        Assert.Equal([small, new ReadOp("big.bin", 100, 32 * Mib), new ReadOp("big.bin", 100 + 32 * Mib, 32 * Mib), new ReadOp("big.bin", 100 + 64 * Mib, 16 * Mib)], pieces);
        Assert.All(pieces, piece => Assert.True(piece.Length <= StageStreamPlans.PieceBytes));
    }
}
