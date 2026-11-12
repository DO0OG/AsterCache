using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class ReadWindowTests
{
    [Fact]
    public void CountsCoverOnlyTheSpansSinceTheLastTake()
    {
        var window = new ReadWindow();
        Assert.True(window.IsOpen);
        window.Count();
        window.Count();
        Assert.Equal(2L, window.Take());
        Assert.Equal(0L, window.Take()); // 가져가면 비워짐

        window.Close();
        Assert.False(window.IsOpen);
        window.Reopen();
        Assert.True(window.IsOpen);
    }
}
