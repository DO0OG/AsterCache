using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class TraceStageMarksTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KeepsFirstStartsForRegisteredExesAndGroupsStartsInsideTheSettleWindow()
    {
        TraceRecord[] records =
        [
            TraceRecord.Read(T0, 1, @"D:\Games\data.bin", 0, 1),
            TraceRecord.Start(T0, 1, 0, @"D:\Games\launcher.exe"),
            TraceRecord.Start(T0.AddSeconds(5), 2, 1, @"D:\Games\helper.exe"),
            TraceRecord.Start(T0.AddSeconds(10), 3, 1, @"D:\Games\game.exe"),
            TraceRecord.Start(T0.AddSeconds(11), 4, 1, @"D:\Games\game.exe"),
            TraceRecord.Start(T0.AddSeconds(12), 5, 1, @"D:\Games\other.exe"),
            TraceRecord.Start(T0.AddSeconds(13), 6, 1, @"D:\Games\main.exe"),
        ];

        IReadOnlyList<AsterCache.Core.Trace.StageMark> marks = TraceStageMarks.FromRecords(records, ["launcher.exe", "helper.exe", "game.exe", "main.exe"]);

        Assert.Equal([new("launcher.exe", 0), new("main.exe", 13_000)], marks);
    }

    [Fact]
    public void RepeatedExeStartsStayInOneStageAndAnUnseenFirstExeUsesTheEmptyFallback()
    {
        TraceRecord[] records =
        [
            TraceRecord.Read(T0, 1, @"D:\Games\data.bin", 0, 1),
            TraceRecord.Start(T0.AddSeconds(20), 2, 0, "first.exe"),
            TraceRecord.Start(T0.AddSeconds(21), 3, 0, "first.exe"),
            TraceRecord.Start(T0.AddSeconds(22), 4, 0, "second.exe"),
        ];

        IReadOnlyList<AsterCache.Core.Trace.StageMark> marks = TraceStageMarks.FromRecords(records, ["first.exe", "second.exe"]);

        Assert.Equal([new("", 0), new("first.exe", 20_000), new("second.exe", 22_000)], marks);
        Assert.Equal([new("", 0)], TraceStageMarks.FromRecords([TraceRecord.Read(T0, 1, "a", 0, 1)], ["first.exe"]));
    }

    [Fact]
    public void MatchesTheKernelTruncatedImageNameToItsRegisteredExe()
    {
        IReadOnlyList<AsterCache.Core.Trace.StageMark> marks = TraceStageMarks.FromRecords(
            [TraceRecord.Start(T0, 1, 0, "SomeLongGameNam")], ["SomeLongGameName.exe"]);

        Assert.Equal([new("SomeLongGameName.exe", 0)], marks);
    }
}
