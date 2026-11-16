using AsterCache.Core.Trace;
using AsterCache.Tests.Windows;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Core;

/// 수동 추적·learn 이 원시 추적을 다시 쓸 때 단계 색인·I/O 집계 곁 파일도 함께 정리합니다.
public sealed class StageSidecarTests
{
    private const long Block = 65536;

    private static TraceStageIndex Index() => new([new TraceStage("a.exe", 0, [new TraceEvent("a.bin", 0, Block)]), new TraceStage("b.exe", 10, [new TraceEvent("b.bin", 0, Block)])]);

    [Fact]
    public void IndexIsIgnoredOnceTheMergedTraceItWasBuiltFromChanges()
    {
        using var dir = new TempDir();
        string raw = dir.Combine("g.trace.json");
        File.WriteAllText(raw, "[]");
        string path = TraceStageIndex.IndexPath(raw);
        TraceStageIndex.Write(path, Index());
        Assert.NotNull(TraceStageIndex.Read(path));

        File.AppendAllText(raw, " "); // 원시 추적이 다시 쓰였는데 낡은 색인을 못 지운 경우

        Assert.Null(TraceStageIndex.Read(path));
    }

    [Fact]
    public void IndexWrittenWithoutAMergedTraceNextToItStillReads()
    {
        using var dir = new TempDir();
        string path = TraceStageIndex.IndexPath(dir.Combine("none.trace.json"));
        TraceStageIndex.Write(path, Index());

        Assert.Equal(2, TraceStageIndex.Read(path)!.Stages.Count);
    }

    [Fact]
    public void ClearSidecarsRemovesTheIndexTheAttemptCountAndEveryRunMarkOfThatTraceOnly()
    {
        using var dir = new TempDir();
        string raw = dir.Combine("g.trace.json");
        File.WriteAllText(raw, "[]");
        TraceStageIndex.Write(TraceStageIndex.IndexPath(raw), Index());
        TraceStageIndex.WriteTried(raw, 2);
        IoSummaryStore.WriteTried(raw, 2);
        string[] marks = [dir.Combine("g.trace.run1.json.stages.json"), dir.Combine("g.trace.run12.json.stages.json")];
        string[] io = [dir.Combine("g.trace.run1.json.io.json"), dir.Combine("g.trace.run12.json.io.json")];
        string rawIo = IoSummaryStore.RunPath(raw);
        string standalone = IoSummaryStore.StandalonePath(raw, 1);
        string[] keep = [dir.Combine("other.trace.run1.json.stages.json"), dir.Combine("other.trace.run1.json.io.json"), dir.Combine("g.trace.run1.json"), dir.Combine("g.trace.json.stages.json.bak")];
        foreach (string file in marks.Concat(io).Append(rawIo).Append(standalone).Concat(keep))
        {
            File.WriteAllText(file, "[]");
        }

        TraceStageIndex.ClearSidecars(raw);

        Assert.All(marks.Concat(io).Append(rawIo).Append(standalone).Append(TraceStageIndex.IndexPath(raw)).Append(TraceStageIndex.TriedPath(raw)).Append(IoSummaryStore.TriedPath(raw)), file => Assert.False(File.Exists(file), file));
        Assert.All(keep.Append(raw), file => Assert.True(File.Exists(file), file));
        Assert.Equal(0, TraceStageIndex.ReadTried(raw));
    }

    [Fact]
    public void ClearSidecarsResolvesRelativeOutputPathsAgainstTheCurrentDirectory()
    {
        using var dir = new TempDir();
        string raw = dir.Combine("relative.trace.json");
        File.WriteAllText(raw, "[]");
        string relativeRaw = Path.GetRelativePath(Environment.CurrentDirectory, raw);
        string runSidecar = IoSummaryStore.RunPath(TraceWriter.RunPath(raw, 1));
        string standalone = IoSummaryStore.StandalonePath(raw, 1);
        File.WriteAllText(runSidecar, "{}");
        File.WriteAllText(standalone, "{}");

        TraceStageIndex.ClearSidecars(relativeRaw);

        Assert.False(File.Exists(runSidecar));
        Assert.False(File.Exists(standalone));
        Assert.True(File.Exists(raw));
    }

    [Fact]
    public void ClearSidecarsTreatsAPathWithoutADirectoryAsTheCurrentDirectory()
    {
        string name = $"relative-{Guid.NewGuid():N}.trace.json"; // 디렉터리가 없는 상대 경로: 현재 디렉터리 (시험 출력 폴더) 의 파일
        string raw = Path.Combine(Environment.CurrentDirectory, name);
        string[] sidecars = [IoSummaryStore.RunPath(raw), IoSummaryStore.StandalonePath(raw, 1), IoSummaryStore.TriedPath(raw), TraceStageIndex.IndexPath(raw)];
        try
        {
            File.WriteAllText(raw, "[]");
            foreach (string file in sidecars)
            {
                File.WriteAllText(file, "{}");
            }

            TraceStageIndex.ClearSidecars(name);

            Assert.All(sidecars, file => Assert.False(File.Exists(file), file));
            Assert.True(File.Exists(raw));
        }
        finally
        {
            foreach (string file in sidecars.Append(raw))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void ALockedIoSummaryFileDoesNotStopTheStageIndexCleanup()
    {
        using var dir = new TempDir();
        string raw = dir.Combine("g.trace.json");
        File.WriteAllText(raw, "[]");
        TraceStageIndex.Write(TraceStageIndex.IndexPath(raw), Index());
        TraceStageIndex.WriteTried(raw, 2);
        string runIo = IoSummaryStore.RunPath(raw);
        File.WriteAllText(runIo, "{}");
        using var hold = new FileStream(runIo, FileMode.Open, FileAccess.Read, FileShare.None);

        TraceStageIndex.ClearSidecars(raw);

        Assert.False(File.Exists(TraceStageIndex.IndexPath(raw)));
        Assert.Equal(0, TraceStageIndex.ReadTried(raw));
        Assert.True(File.Exists(runIo)); // 못 지운 집계는 원시 지문이 달라 읽을 때 무효
    }

    [Fact]
    public void AttemptCountRoundTripsAndBadTextCountsAsZero()
    {
        using var dir = new TempDir();
        string raw = dir.Combine("g.trace.json");
        Assert.Equal(0, TraceStageIndex.ReadTried(raw));

        TraceStageIndex.WriteTried(raw, 2);
        Assert.Equal(2, TraceStageIndex.ReadTried(raw));

        File.WriteAllText(TraceStageIndex.TriedPath(raw), "many");
        Assert.Equal(0, TraceStageIndex.ReadTried(raw));
    }
}
