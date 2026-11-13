using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Core;

/// 사다리 순서: 예열 순서의 역순, 이어진 범위 합치기 (순서 보존), 빈 목록.
public sealed class LadderOrderTests
{
    [Fact]
    public void ReversesTheReadOrderSoTheFirstReadIsTouchedLast()
    {
        List<ReadOp> ladder = LadderOrder.Reverse([new ReadOp("a", 0, 10), new ReadOp("b", 0, 10), new ReadOp("a", 100, 10)]);

        Assert.Equal([new ReadOp("a", 100, 10), new ReadOp("b", 0, 10), new ReadOp("a", 0, 10)], ladder);
    }

    [Fact]
    public void MergesOnlyWhenTheNextTouchedRangeStartsWhereThePreviousEnds()
    {
        // 읽은 순서 c(20~30), b(10~20), a(0~10) 를 역순으로 만지면 a -> b -> c: 주소가 이어지고 순서도 주소 순서라 합쳐도 순서가 같음
        Assert.Equal([new ReadOp("f", 0, 30)], LadderOrder.Reverse([new ReadOp("f", 20, 10), new ReadOp("f", 10, 10), new ReadOp("f", 0, 10)]));
        // 읽은 순서 a, b, c (주소 오름차순) 를 역순으로 만지면 c -> b -> a: 합치면 b 가 c 보다 먼저 만져져 순서가 뒤집히므로 합치지 않음
        Assert.Equal(3, LadderOrder.Reverse([new ReadOp("f", 0, 10), new ReadOp("f", 10, 10), new ReadOp("f", 20, 10)]).Count);
    }

    [Fact]
    public void DoesNotMergeAcrossFilesOrGapsAndKeepsPathCaseInsensitive()
    {
        List<ReadOp> ladder = LadderOrder.Reverse([new ReadOp("A", 10, 10), new ReadOp("b", 10, 10), new ReadOp("a", 0, 10), new ReadOp("a", 30, 5)]);

        Assert.Equal([new ReadOp("a", 30, 5), new ReadOp("a", 0, 10), new ReadOp("b", 10, 10), new ReadOp("A", 10, 10)], ladder);
        Assert.Equal([new ReadOp("A", 0, 20)], LadderOrder.Reverse([new ReadOp("a", 10, 10), new ReadOp("A", 0, 10)])); // 대소문자만 다른 같은 파일
    }

    [Fact]
    public void EmptyListAndZeroLengthRangesGiveNothing()
    {
        Assert.Empty(LadderOrder.Reverse([]));
        Assert.Empty(LadderOrder.Reverse([new ReadOp("a", 0, 0), new ReadOp("a", 5, -1)]));
    }
}
