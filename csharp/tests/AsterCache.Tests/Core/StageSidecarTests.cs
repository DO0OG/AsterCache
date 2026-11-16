using AsterCache.Core.Trace;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// 단계 곁 파일의 짝맞춤: 색인은 만들어진 병합 추적의 표식을 담아 낡으면 무시되고, 수동 추적·learn 이 쓴 뒤에는 곁 파일을 지웁니다.
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
        string[] marks = [dir.Combine("g.trace.run1.json.stages.json"), dir.Combine("g.trace.run12.json.stages.json")];
        string[] keep = [dir.Combine("other.trace.run1.json.stages.json"), dir.Combine("g.trace.run1.json"), dir.Combine("g.trace.json.stages.json.bak")];
        foreach (string file in marks.Concat(keep))
        {
            File.WriteAllText(file, "[]");
        }

        TraceStageIndex.ClearSidecars(raw);

        Assert.All(marks.Append(TraceStageIndex.IndexPath(raw)).Append(TraceStageIndex.TriedPath(raw)), file => Assert.False(File.Exists(file), file));
        Assert.All(keep.Append(raw), file => Assert.True(File.Exists(file), file));
        Assert.Equal(0, TraceStageIndex.ReadTried(raw));
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
