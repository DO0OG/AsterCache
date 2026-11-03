using AsterCache.Core;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests;

public sealed class AutoProgressTests
{
    [Fact]
    public void Empty_state_counts_zero_and_has_no_line()
    {
        using var dir = new TempDir();

        AutoProgress p = AutoProgress.From([], dir.Path);

        Assert.Equal(new AutoProgress(0, 0, 0, 0, null), p);
        Assert.Null(p.Line("en"));
    }

    [Fact]
    public void Planned_counts_only_games_that_have_a_plan_file()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("c.plan.json"), "{}");

        AutoProgress p = AutoProgress.From(
        [
            ("a", "discovered", null, null),
            ("b", "tracing", null, null),
            ("c", "done", null, null),
        ], dir.Path);

        Assert.Equal(1, p.Planned);
    }

    [Fact]
    public void Mixed_state_counts_each_step_and_averages_hits()
    {
        using var dir = new TempDir();
        foreach (string name in new[] { "c", "d", "e" })
        {
            File.WriteAllText(dir.Combine(name + ".plan.json"), "{}");
        }

        AutoProgress p = AutoProgress.From(
        [
            ("a", "discovered", null, null),
            ("b", "tracing", null, null),
            ("c", "done", "relocated (profile), prewarmed", 80),
            ("d", "done", "prewarmed (full)", 51),
            ("e", "failed", "x", null),
        ], dir.Path);

        Assert.Equal(new AutoProgress(5, 3, 2, 2, 66), p);
        Assert.Equal("Auto progress: 5 games found → 3 bootstrap planned → 2 relocated → 2 prewarmed → avg hit rate 66%", p.Line("en"));
    }
}
