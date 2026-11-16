using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class StageStreamRuleTests
{
    private const long Block = 65536;

    [Fact]
    public void FirstStageKeepsTheHeadAndUnassignedBlocksButDropsLaterStageBlocks()
    {
        var index = new TraceStageIndex(
        [
            new TraceStage("launcher.exe", 0, [new TraceEvent("a.bin", 0, Block)]),
            new TraceStage("game.exe", 10, [new TraceEvent("a.bin", Block, Block), new TraceEvent("b.bin", 0, Block)]),
        ]);
        ReadOp[] ops = [new("a.bin", 0, 3 * Block), new("b.bin", 0, Block)];

        List<ReadOp> got = StageStreamRule.FirstStageOps(ops, Block, index)!;

        Assert.Equal([new ReadOp("a.bin", 0, Block), new ReadOp("a.bin", 2 * Block, Block)], got);
        Assert.Null(StageStreamRule.FirstStageOps(ops, Block, new TraceStageIndex([new TraceStage("launcher.exe", 0, [])])));
    }

    [Fact]
    public void StageOpsIntersectsTheCurrentStartListInStageOrderAndSkipsVolatileFiles()
    {
        var stage = new TraceStage("game.exe", 0,
        [
            new TraceEvent("a.bin", 0, 2 * Block),
            new TraceEvent("volatile.bin", 0, Block),
            new TraceEvent("missing.bin", 0, Block),
        ]);
        ReadOp[] ops = [new("A.BIN", 0, Block), new("a.bin", Block, Block), new("volatile.bin", 0, Block)];

        List<ReadOp> got = StageStreamRule.StageOps(ops, stage, new HashSet<string>(["VOLATILE.BIN"], StringComparer.Ordinal));

        Assert.Equal([new ReadOp("A.BIN", 0, 2 * Block)], got);
        Assert.Equal(Block, StageStreamRule.SkippedVolatileBytes(ops, new HashSet<string>(["VOLATILE.BIN"], StringComparer.Ordinal)));
    }

    [Fact]
    public void PriorityStartsLowRaisesOnlyAfterQuietLongEnoughAndDropsAtOnceWhenBusyAgain()
    {
        DateTimeOffset t = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        (bool low, DateTimeOffset? quiet) = StageStreamRule.NextPriority(true, null, false, t);
        Assert.True(low); // 조용해진 지 얼마 안 됨
        Assert.Equal(t, quiet);

        (low, quiet) = StageStreamRule.NextPriority(low, quiet, false, t + StageStreamRule.QuietToRaise - TimeSpan.FromMilliseconds(1));
        Assert.True(low);

        (low, quiet) = StageStreamRule.NextPriority(low, quiet, false, t + StageStreamRule.QuietToRaise);
        Assert.False(low);

        (low, quiet) = StageStreamRule.NextPriority(low, quiet, false, t + TimeSpan.FromMinutes(1));
        Assert.False(low); // 계속 조용하면 보통 유지

        (low, quiet) = StageStreamRule.NextPriority(low, quiet, true, t + TimeSpan.FromMinutes(2));
        Assert.True(low);
        Assert.Null(quiet);

        (low, _) = StageStreamRule.NextPriority(low, quiet, false, t + TimeSpan.FromMinutes(2));
        Assert.True(low); // 다시 조용해진 시각부터 새로 셈
    }

    [Fact]
    public void CooldownEvidenceYieldAndMemoryBoundariesAreDeterministic()
    {
        DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(StageStreamRule.LaterSkip(now, null));
        Assert.Equal("cooldown 1 s", StageStreamRule.LaterSkip(now, now.AddSeconds(-59.1)));
        Assert.Null(StageStreamRule.LaterSkip(now, now.AddSeconds(-60)));
        Assert.False(StageStreamRule.LoadEvidence(StageStreamRule.EvidenceBytes - 1));
        Assert.True(StageStreamRule.LoadEvidence(StageStreamRule.EvidenceBytes));
        Assert.False(StageStreamRule.WantLow(4L << 20, 1.1));
        Assert.True(StageStreamRule.WantLow(8L << 20, 2));
        Assert.False(StageStreamRule.WantLow(8L << 20, 0));
        Assert.False(StageStreamRule.MemoryStop(4160, 4096, 64L << 20));
        Assert.True(StageStreamRule.MemoryStop(4159, 4096, 64L << 20));
        Assert.True(StageStreamRule.MemoryStop(4096, 4096, 1));
    }
}
