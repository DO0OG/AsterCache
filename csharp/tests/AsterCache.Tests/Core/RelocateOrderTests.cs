using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 실행 순서 (source) 는 작업 실행 순서만 바꾸고 결과 배치·저널·재개·되돌리기는 target 과 같아야 합니다.
public sealed class RelocateOrderTests : ApplyTestBase
{
    private static ApplyOptions Exec(string order, long writeCap = 2L << 30) => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: writeCap, Order: order);

    // 그 작업 (path, vcn) 의 이동 전 첫 extent LCN
    private long SourceLcn((string Path, long Vcn, long Lcn, long N) m) =>
        ZoneAllocator.Pieces(Files[m.Path], m.Vcn, m.Vcn + m.N).First().Lcn;

    private static IEnumerable<(string Path, long Vcn, long Lcn, long N)> FirstMoves(IEnumerable<(string Path, long Vcn, long Lcn, long N)> moves) =>
        moves.GroupBy(m => (m.Path, m.Vcn)).Select(g => g.First());

    [Theory]
    [InlineData("auto", "target")]
    [InlineData("target", "target")]
    [InlineData("source", "source")]
    public void AutoResolvesToTargetAndExplicitSettingsAreKept(string setting, string expected) =>
        Assert.Equal(expected, RelocateOrder.Resolve(setting));

    [Fact]
    public void SourceOrderRunsTasksByAscendingSourceLcn()
    {
        PlanFile p = Make().Resolve(20000);

        ApplyReport rep = Apply(p, Exec(RelocateOrder.Source));

        Assert.Empty(rep.Failed);
        long[] srcs = FirstMoves(Vol.Moves).Select(SourceLcn).ToArray();
        Assert.True(srcs.Length > 1);
        Assert.Equal(srcs.Order(), srcs);
        Assert.True(Verify(p).Contiguous);
    }

    [Fact]
    public void SourceOrderGivesTheSameFinalLayoutAsTargetOrder()
    {
        PlanFile p = Make().Resolve(20000);
        var targetVol = new FakeVolume(Files.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal), Letter);
        Applier.Apply(p, targetVol, [Root], Path.Combine(Root, "t.jsonl"), Hooks(), Exec(RelocateOrder.Target));
        long[] targetSrcs = FirstMoves(targetVol.Moves).Select(SourceLcn).ToArray();

        Apply(p, Exec(RelocateOrder.Source));

        Assert.NotEqual(targetSrcs.Order(), targetSrcs); // 이 데이터에서는 두 순서가 실제로 다름
        Assert.Equal(targetVol.Moves.Order().ToList(), Vol.Moves.Order().ToList()); // 같은 조각을 같은 대상으로 옮김
        Assert.True(SameExtents(targetVol.Ext, Vol.Ext));
    }

    [Fact]
    public void SourceOrderCancelThenResumeFinishesWithoutRepeatingAChunk()
    {
        PlanFile p = Make().Resolve(20000);
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };

        ApplyReport r1 = Applier.Apply(p, Vol, [Root], JournalPath, Hooks(), Exec(RelocateOrder.Source), cts.Token);
        Vol.AfterMove = null;
        ApplyReport r2 = Apply(p, Exec(RelocateOrder.Source));

        Assert.Equal("cancelled", r1.Stopped);
        Assert.Empty(r2.Failed);
        Assert.Null(r2.Stopped);
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn)).Distinct().Count());
        Assert.True(Verify(p).Contiguous);
        Assert.Empty(r2.ShaMismatch ?? []);
    }

    [Fact]
    public void SourceOrderWriteCapStopThenResumeInTargetOrderStillCompletes()
    {
        PlanFile p = Make().Resolve(20000);

        ApplyReport r1 = Apply(p, Exec(RelocateOrder.Source, writeCap: 16 * Cb));
        ApplyReport r2 = Apply(p, Exec(RelocateOrder.Target)); // 순서를 바꿔 이어도 남은 작업은 실제 extent 로 다시 계산됨

        Assert.Equal("write_cap", r1.Stopped);
        Assert.Empty(r2.Failed);
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn)).Distinct().Count());
        Assert.True(Verify(p).Contiguous);
    }

    [Fact]
    public void SourceOrderFallsBackToTargetWhenTheZoneOverlapsCurrentExtents()
    {
        PlanFile p = Make().Resolve(1000); // 존이 F1 의 현재 extent (1000, 2000, 3000 …) 위에 놓임

        ApplyReport rep = Apply(p, new ApplyOptions(Order: RelocateOrder.Source));

        Assert.Equal("target", rep.OrderUsed);
        Assert.Equal("zone overlaps current extents", rep.OrderFallbackReason);
        var json = rep.ToJson();
        Assert.Equal("target", json["order_used"]);
        Assert.Equal("zone overlaps current extents", json["order_fallback_reason"]);
    }

    [Fact]
    public void SourceOrderIsKeptWhenTheZoneIsClearOfCurrentExtents()
    {
        ApplyReport rep = Apply(Make().Resolve(20000), new ApplyOptions(Order: RelocateOrder.Source));

        Assert.Equal("source", rep.OrderUsed);
        Assert.False(rep.ToJson().ContainsKey("order_fallback_reason"));
        Assert.False(Apply(Make().Resolve(20000), new ApplyOptions()).ToJson().ContainsKey("order_used")); // target 요청이면 필드 없음
    }

    [Fact]
    public void SourceOrderIsRevertedToTheOriginalExtents()
    {
        PlanFile p = Make().Resolve(20000);
        Apply(p, Exec(RelocateOrder.Source));

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));

        Assert.Empty(rep.Skipped);
        Assert.True(SameExtents(Vol.Ext, Files));
    }
}
