using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Service;

/// 트레이스 금지 게임 (안티치트) 의 계획은 트레이스가 덮지 못한 엔진 프로필 구간을 보충으로 더하고, 허용 게임은 그대로.
public sealed partial class AutoRelocatorTests
{
    private const long OneMib = 1L << 20;

    private List<IReadOnlyList<IReadOnlyList<TraceEvent>>> CaptureSessions()
    {
        var seen = new List<IReadOnlyList<IReadOnlyList<TraceEvent>>>();
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> inner = _w.MakePlan;
        _w.MakePlan = (sessions, origin, zoneMib) =>
        {
            seen.Add(sessions);
            return inner(sessions, origin, zoneMib);
        };
        return seen;
    }

    private static long UniqueBytes(IEnumerable<TraceEvent> events) =>
        events.GroupBy(e => e.Path).Sum(g => g.OrderBy(e => e.Offset).Aggregate((End: 0L, Sum: 0L), (a, e) =>
        {
            long start = Math.Max(a.End, e.Offset);
            long end = e.Offset + e.Length;
            return end > start ? (end, a.Sum + end - start) : a;
        }).Sum);

    [Fact]
    public void TraceForbiddenGamePlanAddsTheProfileRangesTheTraceMissed()
    {
        UseAntiCheat("vanguard");
        ReportFullMoves();
        var seen = CaptureSessions();
        var asked = new List<long>();
        _w.ProfileEvents = (_, budget) =>
        {
            asked.Add(budget);
            return [new TraceEvent(F2, 0, OneMib), new TraceEvent(F1, 0, OneMib)]; // 파일 전체 (일부는 트레이스가 이미 덮음)
        };
        long traceBytes = UniqueBytes(Sessions[0]);

        Tick();

        Assert.True(File.Exists(_game.Plan));
        IReadOnlyList<TraceEvent> planned = seen[0][0];
        Assert.Equal(2 * OneMib, UniqueBytes(planned)); // 트레이스 ∪ 프로필 = 두 파일 전체
        Assert.Equal(Sessions[0], planned.Take(Sessions[0].Count)); // 트레이스가 앞
        Assert.Equal(2 * OneMib - traceBytes, UniqueBytes(planned) - traceBytes);
        Assert.Equal((2 * OneMib - traceBytes + OneMib - 1) / OneMib, (long)PlanFile.Load(_game.Plan).Stats["profile_supplement_mib"]!);
        Assert.All(asked, b => Assert.True(b >= traceBytes));
    }

    [Fact]
    public void ZoneCapKeepsTheSupplementInsideIt()
    {
        UseAntiCheat("vanguard");
        _settings = _settings with { MaxZoneMib = 1, MaxZoneAuto = false }; // 존 상한 1 MiB
        ReportFullMoves();
        var seen = CaptureSessions();
        _w.ProfileEvents = (_, _) => [new TraceEvent(F2, 0, OneMib), new TraceEvent(F1, 0, OneMib)];

        Tick();

        Assert.Equal(OneMib, UniqueBytes(seen[0][0])); // 트레이스 320 KiB + 보충은 상한 1 MiB 까지
        Assert.True(seen[0][0].Count > Sessions[0].Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ricochet")] // 리코셰는 트레이스를 허용
    public void TraceAllowedGamesAreUnchanged(string? kind)
    {
        UseAntiCheat(kind);
        ReportFullMoves();
        var seen = CaptureSessions();
        int calls = 0;
        _w.ProfileEvents = (_, _) =>
        {
            calls++;
            return [new TraceEvent(F2, 0, OneMib)];
        };

        Tick();

        Assert.Equal(0, calls);
        Assert.Equal(Sessions[0], seen[0][0]);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("profile_supplement_mib"));
    }

    [Fact]
    public void ForbiddenGameWithoutAProfileKeepsTheTraceOnlyPlan()
    {
        UseAntiCheat("eac");
        ReportFullMoves();
        var seen = CaptureSessions();

        Tick(); // ProfileEvents 기본값: 빈 목록

        Assert.Equal(Sessions[0], seen[0][0]);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("profile_supplement_mib"));
    }
}
