using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 트레이스 ∪ 엔진 프로필: 트레이스가 덮지 못한 프로필 구간만, 존 상한 안에서, 프로필 우선순위 순으로.
public sealed class ProfileSupplementTests
{
    private const long Mib = 1L << 20;

    private static TraceEvent Ev(string path, long startMib, long mib) => new(path, startMib * Mib, mib * Mib);

    private static long Unique(IEnumerable<TraceEvent> events) =>
        events.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Sum(g => g.OrderBy(e => e.Offset).Aggregate((End: 0L, Sum: 0L), (a, e) =>
        {
            long start = Math.Max(a.End, e.Offset);
            long end = e.Offset + e.Length;
            return end > start ? (end, a.Sum + end - start) : a;
        }).Sum);

    [Fact]
    public void AddsOnlyTheProfileRangesTheTraceDoesNotCoverInPriorityOrder()
    {
        TraceEvent[] trace = [Ev(@"D:\g\Global.wad", 10, 10), Ev(@"D:\g\Global.wad", 15, 10), Ev(@"D:\g\Other.wad", 0, 1)]; // Global 10..25 MiB 를 덮음
        TraceEvent[] profile = [Ev(@"D:\g\Global.wad", 0, 40), Ev(@"D:\g\UI.wad", 0, 8), Ev(@"D:\g\Other.wad", 0, 1)];

        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union(trace, 1000 * Mib, _ => profile);

        TraceEvent[] extra = [.. events.Skip(trace.Length)];
        Assert.Equal((10 + 15 + 8) * Mib, added); // Global 0..10 과 25..40, UI 전체. Other 는 이미 덮임
        Assert.Equal(added, Unique(events) - Unique(trace));
        string[] order = [.. extra.Select(e => e.Path)];
        Assert.Equal([@"D:\g\Global.wad", @"D:\g\UI.wad"], order.Distinct()); // 프로필 순서: Global 먼저, 그다음 UI
        Assert.True(Array.LastIndexOf(order, @"D:\g\Global.wad") < Array.IndexOf(order, @"D:\g\UI.wad"));
        Assert.Equal(trace, events.Take(trace.Length)); // 트레이스 순서 그대로 앞에
        Assert.All(extra, e => Assert.InRange(e.Length, 1, ProfileSupplement.PieceBytes)); // 이벤트 길이 상한
    }

    [Fact]
    public void ZoneCapLimitsTheSupplementAndKeepsTheHighestPriorityFirst()
    {
        TraceEvent[] trace = [Ev("a", 0, 3)];
        TraceEvent[] profile = [Ev("p1", 0, 5), Ev("p2", 0, 100)];

        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union(trace, 10 * Mib, _ => profile);

        Assert.Equal(7 * Mib, added); // 상한 10 - 트레이스 3
        Assert.Equal(10 * Mib, Unique(events));
        Assert.Equal(5 * Mib, events.Where(e => e.Path == "p1").Sum(e => e.Length)); // 우선순위가 높은 p1 은 전부
        Assert.Equal(2 * Mib, events.Where(e => e.Path == "p2").Sum(e => e.Length));
    }

    [Fact]
    public void TraceAtOrOverTheCapGetsNoSupplementAndSkipsTheProfile()
    {
        TraceEvent[] trace = [Ev("a", 0, 20)];
        bool asked = false;

        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union(trace, 20 * Mib, _ =>
        {
            asked = true;
            return [Ev("p", 0, 5)];
        });

        Assert.Equal(0, added);
        Assert.Same(trace, events);
        Assert.False(asked);
    }

    [Fact]
    public void ProfileBudgetIsCapPlusTheTraceBytesAndEmptyProfileChangesNothing()
    {
        TraceEvent[] trace = [Ev("a", 0, 3), Ev("a", 1, 3)]; // 고유 4 MiB
        long asked = -1;

        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union(trace, 10 * Mib, b =>
        {
            asked = b;
            return [];
        });

        Assert.Equal(14 * Mib, asked);
        Assert.Equal((0, trace.Length), (added, events.Count));
        Assert.Same(trace, events);
    }

    [Fact]
    public void PathsAreComparedIgnoringCaseAndRepeatedProfilePicksAreNotCountedTwice()
    {
        TraceEvent[] trace = [Ev(@"D:\G\A.wad", 0, 4)];
        TraceEvent[] profile = [Ev(@"d:\g\a.wad", 0, 6), Ev(@"d:\g\a.wad", 2, 6), Ev(@"D:\G\B.wad", 0, 2), Ev(@"D:\G\B.wad", 0, 2)];

        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union(trace, 100 * Mib, _ => profile);

        Assert.Equal((4 + 2) * Mib, added); // a: 4..8 (두 번째 픽의 6..8 은 첫 픽이 이미 덮음 -> 4..8 만), b: 2
        Assert.Equal(added, Unique(events) - Unique(trace));
    }

    [Fact]
    public void BigGapsAreSplitIntoBoundedEvents()
    {
        (IReadOnlyList<TraceEvent> events, long added) = ProfileSupplement.Union([], 1000 * Mib, _ => [Ev("big", 0, 100)]);

        Assert.Equal(100 * Mib, added);
        Assert.Equal(7, events.Count); // 16 MiB x 6 + 4 MiB
        Assert.Equal(Enumerable.Range(0, 7).Select(i => i * ProfileSupplement.PieceBytes), events.Select(e => e.Offset)); // 이어지는 오프셋
    }
}
