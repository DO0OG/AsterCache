using System.Diagnostics;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 스트림 묶음의 메모리 여유 판정: 묶음 + 여분 이상일 때만 통과, 조회 실패는 여유 없음, 조회는 초당 4 회 이하.
public sealed class StreamHeadroomTests
{
    private const long Mib = 1L << 20;

    private long _now;
    private int _calls;
    private MemoryLists? _lists = new(1000 * Mib, 0);

    private StreamHeadroom Create() => new(() =>
    {
        _calls++;
        return _lists;
    }, () => _now);

    private static long Ms(int ms) => (long)(ms / 1000d * Stopwatch.Frequency);

    [Fact]
    public void TheBatchPassesOnlyWhenTheHeadroomCoversItPlusTheSpare()
    {
        StreamHeadroom headroom = Create();

        Assert.False(headroom.Short(1000 * Mib - StreamHeadroom.SpareBytes)); // 딱 맞음
        _now += Ms(300);
        Assert.True(headroom.Short(1000 * Mib - StreamHeadroom.SpareBytes + 1));
        Assert.Equal(256L << 20, StreamHeadroom.SpareBytes);
    }

    [Fact]
    public void AFailedLookupCountsAsNoHeadroomAndIsRemembered()
    {
        _lists = null;
        StreamHeadroom headroom = Create();

        Assert.True(headroom.Short(1));
        Assert.True(headroom.LookupFailed);
        Assert.Null(headroom.MinBytes);

        _lists = new MemoryLists(1000 * Mib, 0);
        _now += Ms(300);
        Assert.False(headroom.Short(1));
        Assert.False(headroom.LookupFailed);
    }

    [Fact]
    public void ALookupThatThrowsIsAFailedLookup()
    {
        var headroom = new StreamHeadroom(() => throw new InvalidOperationException(), () => _now);

        Assert.True(headroom.Short(1));
        Assert.True(headroom.LookupFailed);
    }

    [Fact]
    public void TheLookupIsReusedWithin250MillisecondsAndTheMinimumFollowsFreshLookups()
    {
        StreamHeadroom headroom = Create();
        _ = headroom.Short(1);
        _lists = new MemoryLists(400 * Mib, 0);
        _now += Ms(200);
        _ = headroom.Short(1);
        Assert.Equal(1, _calls); // 낡지 않은 결과를 다시 씀

        _now += Ms(60);
        _ = headroom.Short(1);

        Assert.Equal(2, _calls);
        Assert.Equal(400 * Mib, headroom.MinBytes);

        _lists = new MemoryLists(900 * Mib, 0);
        _now += Ms(300);
        _ = headroom.Short(1);
        Assert.Equal(400 * Mib, headroom.MinBytes); // 더 큰 값은 최소를 바꾸지 않음
    }

    [Fact]
    public void HeldBatchesAndSecondsAccumulate()
    {
        StreamHeadroom headroom = Create();

        headroom.NoteHeld(1.5);
        headroom.NoteHeld(2.0);

        Assert.Equal((2L, 3.5), (headroom.HeldBatches, headroom.HeldSeconds));
    }
}
