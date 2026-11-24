using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 업데이트 보관본 (.stale, .stale.update) 의 보존 규칙: 트레이스 보관본 가운데 기한이 지난 것만 지우고 계획 보관본과 현재 파일은 건드리지 않습니다.
public sealed class TraceArchivesTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_archives_" + Guid.NewGuid().ToString("N"));

    public TraceArchivesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Make(string name, int daysOld)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, Cutoff.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void OldTraceArchiveCopiesAreRemovedAndEverythingElseStays()
    {
        string oldStale = Make("foo.trace.json.stale", 3);
        string oldUpdate = Make("foo.trace.json.stale.update", 3);
        string oldRun = Make("foo.trace.run7.json.stale", 20);
        string oldIo = Make("foo.trace.run7.json.io.json.stale", 20);
        string recent = Make("foo.trace.run8.json.stale", -2); // 기한 안
        string planArchive = Make("foo.plan.json.stale", 90); // 계획 보관본은 잔여 재계획이 읽음
        string current = Make("foo.trace.json", 90);
        string partial = Make("foo.trace.json.stale.partial", 90); // 정리가 실패한 뒤의 정리본: 뜻이 달라 건드리지 않음
        string other = Make("bar.trace.json.stale", 90); // 다른 게임

        (int files, long bytes) = TraceArchives.Prune(Path.Combine(_dir, "foo.trace.json"), Cutoff);

        Assert.Equal((4, 4096L), (files, bytes));
        Assert.All(new[] { oldStale, oldUpdate, oldRun, oldIo }, p => Assert.False(File.Exists(p)));
        Assert.All(new[] { recent, planArchive, current, partial, other }, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void AMissingFolderRemovesNothing()
    {
        Assert.Equal((0, 0L), TraceArchives.Prune(Path.Combine(_dir, "none", "foo.trace.json"), Cutoff));
    }
}
