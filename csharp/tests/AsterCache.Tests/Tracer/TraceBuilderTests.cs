using AsterCache.Core.Trace;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// 대상 프로세스 트리 + 루트 접두 필터 (합성 레코드). Python 대응: procmon_import 의 접두 규칙 (D:\Games 가 D:\GamesOther 와 겹치지 않음).
public sealed class TraceBuilderTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Game = ["game.exe"];

    private static TraceRecord Start(int pid, int ppid, string image) => TraceRecord.Start(T0, pid, ppid, image);

    private static TraceRecord Read(int pid, string path, long offset = 0, long length = 4096) => TraceRecord.Read(T0, pid, path, offset, length);

    [Fact]
    public void KeepsOnlyTargetTreeInsideRootInArrivalOrder()
    {
        var records = new[]
        {
            Start(1, 0, @"C:\Windows\explorer.exe"),
            Start(100, 1, @"D:\Games\game.exe"),
            Start(200, 100, @"D:\Games\helper.exe"),   // 자식: 이름이 달라도 포함
            Start(300, 1, @"C:\Windows\notepad.exe"),  // 다른 프로세스
            Read(100, @"D:\Games\a.bin", 0, 100),
            Read(300, @"D:\Games\a.bin", 5, 100),      // 다른 프로세스: 제외
            Read(200, @"D:\Games\b.bin", 7, 200),
            Read(100, @"D:\Games\a.bin", 50, 300),
            Read(100, @"C:\Windows\x.dll"),            // 루트 밖: 제외
        };

        (List<TraceEvent> events, BuildStats stats) = TraceBuilder.Build(records, Game, @"D:\Games");

        Assert.Equal(
            [
                new TraceEvent(@"D:\Games\a.bin", 0, 100),
                new TraceEvent(@"D:\Games\b.bin", 7, 200),
                new TraceEvent(@"D:\Games\a.bin", 50, 300),
            ],
            events);
        Assert.All(events, e => Assert.Equal(8.0, e.Cost));
        Assert.Equal(new BuildStats(4, 3, 1, false), stats);
    }

    [Fact]
    public void RootPrefixDoesNotMatchSiblingFolderWithSameStart()
    {
        var records = new[]
        {
            Start(100, 0, "game.exe"),
            Read(100, @"D:\GamesOther\z.bin"),
            Read(100, @"D:\Games\z.bin"),
            Read(100, @"d:/games/Sub/../y.bin"),   // 대소문자·슬래시·.. 정규화 뒤 비교
            Read(100, @"D:\Games"),                // 루트 자체와 같은 경로도 통과 (Python canon == prefix)
        };

        (List<TraceEvent> events, _) = TraceBuilder.Build(records, Game, @"D:\Games\");

        Assert.Equal([@"D:\Games\z.bin", @"d:/games/Sub/../y.bin", @"D:\Games"], events.Select(e => e.Path));
    }

    [Fact]
    public void DescendantsOfDescendantsAreIncludedAndOrphanedChildKeepsTracking()
    {
        var records = new[]
        {
            Start(100, 1, "game.exe"),
            Start(200, 100, "launcher.exe"),
            Start(300, 200, "worker.exe"),
            TraceRecord.Stop(T0, 100),   // 부모가 먼저 끝나도 자식 트리는 계속 대상
            Start(400, 100, "late.exe"), // 끝난 부모의 pid 를 부모로 단 새 프로세스: 부모가 빠졌으므로 대상 아님
            Read(300, @"D:\Games\a.bin"),
            Read(400, @"D:\Games\b.bin"),
            Read(100, @"D:\Games\c.bin"),
        };

        (List<TraceEvent> events, _) = TraceBuilder.Build(records, Game, @"D:\Games");

        Assert.Equal([@"D:\Games\a.bin"], events.Select(e => e.Path));
    }

    [Fact]
    public void ReusedPidAfterStopIsNotATargetUnlessItStartsAsOne()
    {
        var records = new[]
        {
            Start(100, 1, @"D:\Games\GAME.EXE"), // 이름은 대소문자 무시
            Read(100, @"D:\Games\a.bin"),
            TraceRecord.Stop(T0, 100),
            Read(100, @"D:\Games\b.bin"),        // 종료 뒤 같은 pid: 제외
            Start(100, 1, @"C:\Windows\other.exe"),
            Read(100, @"D:\Games\c.bin"),        // 재사용된 pid 가 다른 프로그램: 제외
            Start(100, 1, @"C:\x\game.exe"),
            Read(100, @"D:\Games\d.bin"),        // 다시 대상 이름으로 시작: 포함
        };

        (List<TraceEvent> events, _) = TraceBuilder.Build(records, Game, @"D:\Games");

        Assert.Equal([@"D:\Games\a.bin", @"D:\Games\d.bin"], events.Select(e => e.Path));
    }

    [Fact]
    public void LongExeNameMatchesKernelsFifteenCharImageName()
    {
        var records = new[] { Start(1, 0, "SomeLongGameNam"), Read(1, @"D:\Games\a.bin") };

        (List<TraceEvent> events, _) = TraceBuilder.Build(records, ["SomeLongGameName.exe"], @"D:\Games");

        Assert.Single(events);
    }

    [Fact]
    public void InvalidRangesAreIgnoredAndMaxEventsTruncates()
    {
        var records = new[]
        {
            Start(1, 0, "game.exe"),
            Read(1, @"D:\Games\a.bin", -1, 10),
            Read(1, @"D:\Games\a.bin", 0, 0),
            Read(1, @"D:\Games\a.bin", 0, (64L << 20) + 1),
            Read(1, @"D:\Games\a.bin", 0, 64L << 20),
            Read(1, @"D:\Games\b.bin", 1, 1),
            Read(1, @"D:\Games\c.bin", 2, 2),
        };

        (List<TraceEvent> all, BuildStats stats) = TraceBuilder.Build(records, Game, @"D:\Games");
        (List<TraceEvent> two, BuildStats cut) = TraceBuilder.Build(records, Game, @"D:\Games", maxEvents: 2);

        Assert.Equal(3, all.Count);
        Assert.Equal(new BuildStats(6, 3, 3, false), stats);
        Assert.Equal([@"D:\Games\a.bin", @"D:\Games\b.bin"], two.Select(e => e.Path));
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void EmptyRootKeepsEveryPathAndBadArgumentsThrow()
    {
        var records = new[] { Start(1, 0, "game.exe"), Read(1, @"C:\Windows\x.dll") };

        (List<TraceEvent> events, _) = TraceBuilder.Build(records, Game, "");

        Assert.Single(events);
        Assert.Throws<ArgumentException>(() => TraceBuilder.Build(records, [], ""));
        Assert.Throws<ArgumentException>(() => TraceBuilder.Build(records, [@"D:\Games\"], ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceBuilder.Build(records, Game, "", 0));
    }

    [Fact]
    public void CanonicalizeCaseAppliesOnlyCaseDifferences()
    {
        var events = new[]
        {
            new TraceEvent(@"d:\games\a.bin", 0, 1),
            new TraceEvent(@"D:\Games\A.BIN", 1, 1),
            new TraceEvent(@"D:\Games\link.bin", 2, 1),
            new TraceEvent(@"D:\Games\gone.bin", 3, 1),
            new TraceEvent("D:/Games/./x/../n.bin", 4, 1),
        };
        string? Real(string p) => p.ToLowerInvariant() switch
        {
            @"d:\games\a.bin" => @"D:\Games\A.bin",
            @"d:\games\link.bin" => @"E:\Elsewhere\target.bin", // 대소문자 차이가 아닌 해석: 무시
            _ => null,                                          // 파일 없음: normpath 만 적용
        };

        List<TraceEvent> result = TraceBuilder.CanonicalizeCase(events, Real);

        Assert.Equal(
            [@"D:\Games\A.bin", @"D:\Games\A.bin", @"D:\Games\link.bin", @"D:\Games\gone.bin", @"D:\Games\n.bin"],
            result.Select(e => e.Path));
    }
}
