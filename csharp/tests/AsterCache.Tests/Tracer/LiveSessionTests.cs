using AsterCache.Core;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class LiveSessionTests
{
    [Fact]
    public void StartWithoutAdminThrowsBeforeCreatingAnySession()
    {
        Assert.Throws<NotAdminException>(() => LiveSession.Start(["game.exe"], isAdmin: () => false));
    }

    [Fact]
    public void DescribeShowsSummaryThenFirstReadsOnly()
    {
        DateTime t = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var records = new List<TraceRecord>
        {
            TraceRecord.Start(t, 10, 1, @"D:\Games\game.exe"),
            TraceRecord.Read(t, 10, @"D:\Games b.bin", 4096, 65536),
            TraceRecord.Read(t, 10, @"D:\Games\c.bin", 0, 512),
            TraceRecord.Read(t, 10, @"D:\Games\d.bin", 1, 2),
        };

        string[] lines = EtlReader.Describe(records, 3, 7, 2).ToArray();

        Assert.Equal(
            [
                "etl records=4 reads=3 process_starts=1 unresolved_reads=3 events_lost=7",
                @"FileIORead pid=10 offset=4096 length=65536 path=D:\Games b.bin",
                @"FileIORead pid=10 offset=0 length=512 path=D:\Games\c.bin",
            ],
            lines);
        Assert.Single(EtlReader.Describe(records, 0, 0, 0));
    }

    [Fact]
    public void DefaultBufferIs64MB()
    {
        Assert.Equal(64, LiveSession.DefaultBufferMB);
    }

    [Fact]
    public void WaitPumpReturnsWithinLimitWhenPumpNeverStops()
    {
        var never = new TaskCompletionSource();

        DateTime start = DateTime.UtcNow;
        bool done = LiveSession.WaitPump(never.Task, TimeSpan.FromMilliseconds(100));

        Assert.False(done);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5));
        Assert.True(LiveSession.WaitPump(Task.CompletedTask, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(10, LiveSession.PumpWaitLimit.TotalSeconds);
    }

    [Fact]
    public void PumpTimeoutDiscardsTraceInsteadOfFinishingCollector()
    {
        var never = new TaskCompletionSource();
        bool finished = false;

        Assert.Throws<TraceIncompleteException>(() => LiveSession.FinishAfterPump(never.Task, TimeSpan.FromMilliseconds(50), () =>
        {
            finished = true;
            return [];
        }, null));

        Assert.False(finished);
        Assert.Empty(LiveSession.FinishAfterPump(Task.CompletedTask, TimeSpan.FromMilliseconds(50), () => [], null));
    }

    [Fact]
    public void DisposeRequestsStopAndDefersSessionCleanupUntilThePumpEnds()
    {
        var pump = new TaskCompletionSource();
        bool stopRequested = false;
        bool sessionDisposed = false;

        LiveSession.DisposeAfterStop(pump.Task, TimeSpan.FromMilliseconds(25), () => stopRequested = true, () => sessionDisposed = true, null);

        Assert.True(stopRequested);
        Assert.False(sessionDisposed);
        pump.SetResult();
        Assert.True(SpinWait.SpinUntil(() => sessionDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DisposeStillReleasesTheSessionWhenStopRequestFails()
    {
        bool sessionDisposed = false;

        LiveSession.DisposeAfterStop(Task.CompletedTask, TimeSpan.FromMilliseconds(25), () => throw new InvalidOperationException("stop failed"), () => sessionDisposed = true, null);

        Assert.True(sessionDisposed);
    }

    [Fact]
    public void EtlReaderRejectsMissingFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), "astercache-missing-" + Guid.NewGuid().ToString("N") + ".etl");

        Assert.ThrowsAny<Exception>(() => EtlReader.Read(missing, ["game.exe"], new DevicePaths([])));
    }
}
