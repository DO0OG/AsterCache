using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class TraceStagesTests
{
    private const long Block = 65536;

    [Fact]
    public void AssignsEachBlockToTheLastProcessStartBeforeItsFirstReadAndMergesAdjacentBlocks()
    {
        var index = TraceStageIndex.Build(
        [
            (
                (IReadOnlyList<TraceEvent>)[
                    new TraceEvent("a.bin", 2 * Block, Block, TMs: 5),
                    new TraceEvent("a.bin", 0, Block, TMs: 11),
                    new TraceEvent("a.bin", Block, Block, TMs: 12),
                    new TraceEvent("unmarked.bin", 0, Block),
                ],
                (IReadOnlyList<StageMark>)[new StageMark("launcher.exe", 0), new StageMark("game.exe", 10)]
            ),
        ]);

        Assert.NotNull(index);
        Assert.Equal(["launcher.exe", "game.exe"], index.Stages.Select(x => x.Exe));
        Assert.Equal([new TraceEvent("a.bin", 2 * Block, Block)], index.Stages[0].Ranges);
        Assert.Equal([new TraceEvent("a.bin", 0, 2 * Block)], index.Stages[1].Ranges);
        Assert.Equal(1, index.StageOf("GAME.EXE"));
        Assert.Null(index.StageOf("launcher.exe"));
    }

    [Fact]
    public void NewerRunsWinForSharedBlocksAndAnOlderRunsFirstMarkIsTheFirstStage()
    {
        var index = TraceStageIndex.Build(
        [
            (
                (IReadOnlyList<TraceEvent>)[new TraceEvent("same.bin", 0, Block, TMs: 20)],
                (IReadOnlyList<StageMark>)[new StageMark("new.exe", 0), new StageMark("last.exe", 10)]
            ),
            (
                (IReadOnlyList<TraceEvent>)[new TraceEvent("same.bin", 0, Block, TMs: 20), new TraceEvent("same.bin", Block, Block, TMs: 20), new TraceEvent("old.bin", 0, Block, TMs: 5)],
                (IReadOnlyList<StageMark>)[new StageMark("old.exe", 0), new StageMark("new.exe", 10)]
            ),
        ]);

        Assert.NotNull(index);
        Assert.Equal(["new.exe", "last.exe"], index.Stages.Select(x => x.Exe)); // 옛 run 의 첫 exe 이름은 새 단계가 되지 않음
        Assert.Equal([new TraceEvent("same.bin", 0, Block)], index.Stages[1].Ranges); // 최신 run 의 같은 블록 배정이 남음
        Assert.Equal([new TraceEvent("old.bin", 0, Block), new TraceEvent("same.bin", Block, Block)], index.Stages[0].Ranges);
    }

    [Fact]
    public void LaterMarksOfOlderRunsThatTheLatestRunLacksAreAppendedByTime()
    {
        var index = TraceStageIndex.Build(
        [
            (
                (IReadOnlyList<TraceEvent>)[new TraceEvent("a.bin", 0, Block, TMs: 1)],
                (IReadOnlyList<StageMark>)[new StageMark("", 0)]
            ),
            (
                (IReadOnlyList<TraceEvent>)[new TraceEvent("a.bin", 0, Block, TMs: 1), new TraceEvent("b.bin", 0, Block, TMs: 50)],
                (IReadOnlyList<StageMark>)[new StageMark("first.exe", 0), new StageMark("main.exe", 40)]
            ),
        ]);

        Assert.NotNull(index);
        Assert.Equal(["", "main.exe"], index.Stages.Select(x => x.Exe));
        Assert.Equal(1, index.StageOf("main.exe"));
        Assert.Equal([new TraceEvent("b.bin", 0, Block)], index.Stages[1].Ranges);
    }

    [Fact]
    public void RunsWithoutMarksAndEventsWithoutTimesDoNotCreateStageRanges()
    {
        Assert.Null(TraceStageIndex.Build([(new TraceEvent[] { new("a.bin", 0, Block, TMs: 0) }, Array.Empty<StageMark>())]));

        var index = TraceStageIndex.Build(
        [
            (
                (IReadOnlyList<TraceEvent>)[new TraceEvent("a.bin", 0, Block), new TraceEvent("b.bin", 0, Block, TMs: 1)],
                (IReadOnlyList<StageMark>)[new StageMark("game.exe", 0)]
            ),
        ]);

        Assert.NotNull(index);
        Assert.Equal([new TraceEvent("b.bin", 0, Block)], index.Stages[0].Ranges);
    }

    [Fact]
    public void StageMarksAndIndexRoundTripAndMissingFilesReturnNull()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string run = Path.Combine(dir, "game.trace.run1.json");
            string raw = Path.Combine(dir, "game.trace.json");
            var marks = (IReadOnlyList<StageMark>)[new("game.exe", 0), new("main.exe", 1234)];
            TraceStageIndex.WriteMarks(run, marks);
            TraceStageIndex.Write(TraceStageIndex.IndexPath(raw), new TraceStageIndex(
            [
                new TraceStage("game.exe", 0, [new TraceEvent("a.bin", 0, Block)]),
                new TraceStage("main.exe", 1234, [new TraceEvent("b.bin", Block, Block)]),
            ]));

            Assert.EndsWith("game.trace.run1.json.stages.json", TraceStageIndex.RunMarksPath(run), StringComparison.Ordinal);
            Assert.Equal(marks, TraceStageIndex.ReadMarks(run));
            Assert.Equal(["game.exe", "main.exe"], TraceStageIndex.Read(TraceStageIndex.IndexPath(raw))!.Stages.Select(x => x.Exe));
            Assert.Equal(new TraceEvent("b.bin", Block, Block), TraceStageIndex.Read(TraceStageIndex.IndexPath(raw))!.Stages[1].Ranges[0]);
            Assert.Null(TraceStageIndex.ReadMarks(Path.Combine(dir, "missing.run1.json")));
            Assert.Null(TraceStageIndex.Read(Path.Combine(dir, "missing.trace.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RuntimeLoadBoundaryExposesItsEvidenceThreshold()
    {
        Assert.Equal(64L << 20, RuntimeLoadBoundary.MinimumPeakBytes);
    }
}
