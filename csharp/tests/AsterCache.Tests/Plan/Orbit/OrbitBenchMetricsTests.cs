using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Plan.Orbit;

/// 오프라인 벤치 지표 (설계안 4.2·4.3). 손으로 계산한 값을 고정하고, orbit.json 깨짐과 ABBA 짝짓기 오류를 봅니다.
public sealed class OrbitBenchMetricsTests
{
    private const long Block = OrbitBenchMetrics.BlockBytes;
    private const string File1 = @"D:\bench\f.bin";

    private static OrbitBenchMetrics.BenchEpisode Ep(double weight, params (string Path, long Block)[] blocks) => new(blocks, weight);

    private static OrbitBenchMetrics.RunSummary Run(string arm, double total, double? value, double? episode = 50) =>
        new(arm, total, value, new double?[] { episode });

    [Fact]
    public void ReadyTimeIsMaxOfCoveringOpsAndValueWeightedMeanAndCurveAreaAreExact()
    {
        var done = new List<(ReadOp Op, double DoneMs)>
        {
            (new ReadOp(File1, 0, Block), 40),
            (new ReadOp(File1, Block, Block), 80),
            (new ReadOp(File1, 2 * Block, Block), 100),
        };
        var episodes = new List<OrbitBenchMetrics.BenchEpisode>
        {
            Ep(0.5, (File1, 0)),
            Ep(0.25, (File1, 1)),
            Ep(0.25, (File1, 2)),
        };

        OrbitBenchMetrics.BenchRunMetrics m = OrbitBenchMetrics.Evaluate(done, episodes);

        Assert.Equal(100, m.TotalMs);
        Assert.Equal(3 * Block, m.Bytes);
        Assert.Equal(new double[] { 40, 80, 100 }, m.Episodes.Select(e => e.TReadyMs!.Value));
        Assert.Equal(65, m.ValueMeanMs!.Value, 9); // 0.5*40 + 0.25*80 + 0.25*100
        Assert.Equal(35, m.CurveAreaMs!.Value, 9); // 0.5*(100-40) + 0.25*(100-80) + 0.25*0
    }

    [Fact]
    public void ReadyTimeMaxesOverAllOpsThatCoverABlockAndPathCaseDoesNotMatter()
    {
        // 블록 0 은 두 op 가 덮음 (완료 20, 30): 마지막 완료 시각 30
        var done = new List<(ReadOp Op, double DoneMs)>
        {
            (new ReadOp(File1, 0, 1000), 20),
            (new ReadOp(File1, 500, 1000), 30),
        };
        OrbitBenchMetrics.BenchRunMetrics m = OrbitBenchMetrics.Evaluate(done, [Ep(1.0, (@"d:\BENCH\F.BIN", 0))]);

        Assert.Equal(30, m.Episodes[0].TReadyMs!.Value, 9);
    }

    [Fact]
    public void BlocksNotCoveredByAnyOpAreIgnoredAndCounted()
    {
        var done = new List<(ReadOp Op, double DoneMs)> { (new ReadOp(File1, 0, Block), 30) };

        OrbitBenchMetrics.BenchRunMetrics m = OrbitBenchMetrics.Evaluate(done, [Ep(1.0, (File1, 0), (File1, 5))]);

        Assert.Equal(30, m.Episodes[0].TReadyMs!.Value, 9);
        Assert.Equal(1, m.Episodes[0].MissingBlocks);
    }

    [Fact]
    public void EpisodeWithNoCoveredBlockHasNoReadyTimeAndLeavesTheMean()
    {
        var done = new List<(ReadOp Op, double DoneMs)> { (new ReadOp(File1, 0, Block), 10) };

        OrbitBenchMetrics.BenchRunMetrics m = OrbitBenchMetrics.Evaluate(done, [Ep(0.5, (File1, 0)), Ep(0.5, (@"D:\bench\g.bin", 0))]);

        Assert.Null(m.Episodes[1].TReadyMs);
        Assert.Equal(10, m.ValueMeanMs!.Value, 9);
        Assert.Equal(0, m.CurveAreaMs!.Value, 9); // 준비 시각이 총시간과 같아 면적 0
    }

    [Fact]
    public void SignTestNineOfTenIsElevenOver1024()
    {
        Assert.Equal(11.0 / 1024, OrbitBenchMetrics.SignTestP(10, 9), 12);
        Assert.Equal(1.0, OrbitBenchMetrics.SignTestP(10, 0), 12);
    }

    [Fact]
    public void ValidVersionOneGivesWeightsAndBlocks()
    {
        const string json = """{"version": 1, "inputs": "x", "episodes": [{"blocks": [["D:\\bench\\f.bin", 3], ["D:\\bench\\f.bin", 4]], "runs_seen": 2, "runs_total": 8, "d_ms": 12.5}]}""";

        OrbitBenchMetrics.BenchEpisode e = Assert.Single(OrbitBenchMetrics.ParseEpisodes(json));

        Assert.Equal(0.25, e.Weight);
        Assert.Equal(new (string, long)[] { (@"D:\bench\f.bin", 3), (@"D:\bench\f.bin", 4) }, e.Blocks);
    }

    [Fact]
    public void ValidVersionTwoExpandsFileRangesAndKeepsWeight()
    {
        const string json = """{"version": 2, "inputs": "x", "files": ["D:\\bench\\f.bin", "D:\\bench\\g.bin"], "episodes": [{"ranges": [[0, 3, 2], [1, 7, 1]], "runs_seen": 2, "runs_total": 8, "d_ms": 12.5}]}""";

        OrbitBenchMetrics.BenchEpisode e = Assert.Single(OrbitBenchMetrics.ParseEpisodes(json));

        Assert.Equal(0.25, e.Weight);
        Assert.Equal(new (string, long)[] { (@"D:\bench\f.bin", 3), (@"D:\bench\f.bin", 4), (@"D:\bench\g.bin", 7) }, e.Blocks);
        Assert.Equal("x", OrbitBenchMetrics.Fingerprint(json));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"version": 2, "episodes": []}""")]
    [InlineData("""{"episodes": []}""")]
    [InlineData("""{"version": 1}""")]
    [InlineData("""{"version": 1, "episodes": [{"blocks": [["D:\\bench\\f.bin"]], "runs_seen": 1, "runs_total": 2}]}""")]
    [InlineData("""{"version": 1, "episodes": [{"blocks": [], "runs_seen": 1, "runs_total": 0}]}""")]
    public void BrokenOrOtherVersionGivesNoEpisodes(string json) => Assert.Empty(OrbitBenchMetrics.ParseEpisodes(json));

    [Fact]
    public void AbbaPairsEachGroupAsAAndBOnBothHalves()
    {
        var runs = new[] { Run("A", 100, 50), Run("B", 100, 40), Run("B", 100, 45), Run("A", 100, 60) };

        List<(OrbitBenchMetrics.RunSummary A, OrbitBenchMetrics.RunSummary B)> pairs = OrbitBenchMetrics.PairAbba(runs);

        Assert.Equal(2, pairs.Count);
        Assert.Same(runs[0], pairs[0].A);
        Assert.Same(runs[1], pairs[0].B);
        Assert.Same(runs[3], pairs[1].A);
        Assert.Same(runs[2], pairs[1].B);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public void RunCountThatIsNotAPositiveMultipleOfFourIsRejected(int count)
    {
        var runs = Enumerable.Range(0, count).Select(_ => Run("A", 100, 50)).ToList();

        Assert.Throws<ArgumentException>(() => OrbitBenchMetrics.PairAbba(runs));
    }

    [Theory]
    [InlineData("A", "A", "B", "B")]
    [InlineData("A", "B", "A", "B")]
    public void OrderOtherThanAbbaIsRejected(string first, string second, string third, string fourth)
    {
        var runs = new[] { Run(first, 100, 50), Run(second, 100, 40), Run(third, 100, 40), Run(fourth, 100, 50) };

        Assert.Throws<ArgumentException>(() => OrbitBenchMetrics.PairAbba(runs));
    }

    [Fact]
    public void TenPairsWithNineImprovedPassFlagsTwoAndThree()
    {
        var runs = new List<OrbitBenchMetrics.RunSummary>();
        for (int g = 0; g < 5; g++)
        {
            // 첫 묶음의 첫 쌍만 악화 (B 값 110 > A 값 100)
            runs.Add(Run("A", 100, 100));
            runs.Add(Run("B", 100, g == 0 ? 110 : 80));
            runs.Add(Run("B", 100, 80));
            runs.Add(Run("A", 100, 100));
        }

        OrbitBenchMetrics.Verdict v = OrbitBenchMetrics.Summarize(runs);

        Assert.Equal(10, v.Pairs.Count);
        Assert.Equal(0.2, v.ValueReductionMedian!.Value, 9);
        Assert.Equal(9, v.PairsReductionAtLeast10);
        Assert.Equal(11.0 / 1024, v.SignP!.Value, 12);
        Assert.Equal(0, v.TotalWorseMedian!.Value, 9);
        Assert.Equal(0, v.EpisodeP95WorseMaxMs!.Value, 9);
        Assert.True(v.Flag2);
        Assert.True(v.Flag3);
        Assert.True(v.Flag4);
    }

    [Fact]
    public void ExcludedPairBlocksFlagTwoEvenWhenEveryValidPairImproves()
    {
        var runs = new List<OrbitBenchMetrics.RunSummary>();
        for (int g = 0; g < 3; g++)
        {
            // 첫 묶음의 A 값이 0 이면 그 쌍은 제외됨 (남은 5쌍은 모두 20% 단축, 부호 검정 p = 1/32)
            runs.Add(Run("A", 100, g == 0 ? 0 : 100));
            runs.Add(Run("B", 100, 80));
            runs.Add(Run("B", 100, 80));
            runs.Add(Run("A", 100, 100));
        }

        OrbitBenchMetrics.Verdict v = OrbitBenchMetrics.Summarize(runs);

        Assert.Equal(6, v.Pairs.Count);
        Assert.Equal(1, v.Pairs.Count(p => p.ValueReduction is null));
        Assert.Equal(5, v.PairsReductionAtLeast10);
        Assert.Equal(1.0 / 32, v.SignP!.Value, 12);
        Assert.False(v.Flag2);
    }

    [Fact]
    public void EpisodeSlowdownOver200msFailsFlagFour()
    {
        var runs = new List<OrbitBenchMetrics.RunSummary>();
        for (int g = 0; g < 2; g++)
        {
            runs.Add(Run("A", 100, 100, 50));
            runs.Add(Run("B", 100, 90, 300)); // 에피소드 준비 시각이 250 ms 늦음
            runs.Add(Run("B", 100, 90, 300));
            runs.Add(Run("A", 100, 100, 50));
        }

        OrbitBenchMetrics.Verdict v = OrbitBenchMetrics.Summarize(runs);

        Assert.Equal(250, v.EpisodeP95WorseMaxMs!.Value, 9);
        Assert.False(v.Flag4);
    }
}
