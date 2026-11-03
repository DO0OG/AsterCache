using AsterCache.Core;
using Xunit;

namespace AsterCache.Tests;

public sealed class AutoProgressTests
{
    [Fact]
    public void Empty_state_counts_zero_and_has_no_line()
    {
        AutoProgress p = AutoProgress.From([]);

        Assert.Equal(new AutoProgress(0, 0, 0, 0, null), p);
        Assert.Null(p.Line("en"));
    }

    [Fact]
    public void Mixed_state_counts_each_step_and_averages_hits()
    {
        AutoProgress p = AutoProgress.From(
        [
            ("discovered", null, null),
            ("tracing", null, null),
            ("done", "relocated (profile), prewarmed", 80),
            ("done", "prewarmed (full)", 51),
            ("failed", "x", null),
        ]);

        Assert.Equal(new AutoProgress(5, 3, 2, 2, 66), p);
        Assert.Equal("Auto progress: 5 games found → 3 bootstrap planned → 2 relocated → 2 prewarmed → avg hit rate 66%", p.Line("en"));
    }
}
