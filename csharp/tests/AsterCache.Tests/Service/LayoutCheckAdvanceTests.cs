using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 예약 드라이브 최적화가 끝난 직후 배치 검사를 앞당기는 규칙: 통째 배치 계획의 게임만, 끝 시각이 바뀐 틱에서 한 번.
public sealed class LayoutCheckAdvanceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 3, 0, 0, TimeSpan.Zero);
    private readonly ListLog _log = new();
    private readonly LayoutCheckAdvance _advance = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_adv_" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = T0;
    private long _ms;
    private bool _running;

    public LayoutCheckAdvanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private DriveOptimization Optimization() => new(() => _running, _log, () => _now, () => _ms);

    private void Elapse(TimeSpan span)
    {
        _now += span;
        _ms += (long)span.TotalMilliseconds;
    }

    // 최적화 한 번이 끝난 것으로 만듦 (끝 시각 = 지금).
    private void OneRun(DriveOptimization o)
    {
        _running = true;
        Elapse(TimeSpan.FromSeconds(30));
        o.Refresh();
        _running = false;
        Elapse(TimeSpan.FromMinutes(5));
        o.Refresh();
    }

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    [Fact]
    public void TheDueTimeOfAWholePlanGameMovesToNowOnlyOncePerFinishedRun()
    {
        DriveOptimization o = Optimization();
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["a"] = T0 + TimeSpan.FromHours(10) };
        Assert.Equal(0, _advance.Run(o, due, [Game("a")], _ => true, _now, _log)); // 아직 끝난 실행을 본 적 없음

        OneRun(o);

        Assert.Equal(1, _advance.Run(o, due, [Game("a")], _ => true, _now, _log));
        Assert.Equal(_now, due["a"]);
        Assert.Contains(_log.Lines, l => l.Contains("layout check brought forward for 1 game(s)", StringComparison.Ordinal));

        due["a"] = _now + TimeSpan.FromHours(10);
        Assert.Equal(0, _advance.Run(o, due, [Game("a")], _ => true, _now, _log)); // 같은 끝 시각은 다시 앞당기지 않음
        Assert.True(due["a"] > _now);

        OneRun(o);

        Assert.Equal(1, _advance.Run(o, due, [Game("a")], _ => true, _now, _log)); // 다음 실행이 끝나면 다시
    }

    [Fact]
    public void ARangePlanGameIsNeverBroughtForward()
    {
        DriveOptimization o = Optimization();
        OneRun(o);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["a"] = _now + TimeSpan.FromHours(10), ["b"] = _now + TimeSpan.FromHours(10) };

        int moved = _advance.Run(o, due, [Game("a"), Game("b")], g => g.Name == "b", _now, _log);

        Assert.Equal(1, moved);
        Assert.True(due["a"] > _now);
        Assert.Equal(_now, due["b"]);
    }

    [Fact]
    public void AGameWhoseCheckIsAlreadyDueIsLeftAlone()
    {
        DriveOptimization o = Optimization();
        OneRun(o);
        DateTimeOffset earlier = _now - TimeSpan.FromMinutes(1);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["a"] = earlier };

        Assert.Equal(0, _advance.Run(o, due, [Game("a")], _ => true, _now, _log));
        Assert.Equal(earlier, due["a"]);
    }

    [Fact]
    public void WithoutAnOptimizationDetectorNothingMoves()
    {
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { ["a"] = T0 + TimeSpan.FromHours(1) };

        Assert.Equal(0, _advance.Run(null, due, [Game("a")], _ => true, _now, _log));
    }

    private GameSettings WithPlan(string name, PlanFile? plan)
    {
        string path = Path.Combine(_dir, name + ".plan.json");
        plan?.Save(path);
        return Game(name) with { Plan = path };
    }

    private static PlanFile Plan(bool whole, long? start)
    {
        var stats = new AsterCache.Core.Json.JObject();
        if (whole)
        {
            stats.Add(WholeFileLayout.RuleKey, WholeFileLayout.RuleVersion);
            stats.Add(WholeFileLayout.WholeFilesKey, new List<object?>());
        }

        return new PlanFile(PlanFile.CurrentVersion, @"D:\g", "D:", 4096, 65536, new PlanZone(start, 0), [], [], [], stats);
    }

    [Fact]
    public void OnlyAnAppliedWholePlanCountsAsAWholePlanGame()
    {
        Assert.True(LayoutCheckAdvance.UsesWholeLayout(WithPlan("whole", Plan(true, 100))));
        Assert.False(LayoutCheckAdvance.UsesWholeLayout(WithPlan("ranges", Plan(false, 100))));
        Assert.False(LayoutCheckAdvance.UsesWholeLayout(WithPlan("pending", Plan(true, null)))); // zone 이 아직 없으면 적용되지 않은 계획
        Assert.False(LayoutCheckAdvance.UsesWholeLayout(WithPlan("missing", null)));
    }
}
