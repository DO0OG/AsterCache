using AsterCache.Core.Metrics;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class BenchmarkRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-bench-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _t0 = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    public BenchmarkRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Path_ => Path.Combine(_dir, MetricsJson.SamplesFile);

    private static GameSettings Game(string name) => new(name, [name + ".exe"], "x.json", "y.json", @"D:\Games\" + name, [@"D:\Games"], "GAMES", 60);

    [Fact]
    public void BucketsByServiceUptime()
    {
        Assert.Equal(ElapsedBucket.Sec0, BenchmarkRecorder.BucketOf(TimeSpan.FromSeconds(9)));
        Assert.Equal(ElapsedBucket.Sec10, BenchmarkRecorder.BucketOf(TimeSpan.FromSeconds(10)));
        Assert.Equal(ElapsedBucket.Sec30, BenchmarkRecorder.BucketOf(TimeSpan.FromSeconds(30)));
        Assert.Equal(ElapsedBucket.Sec60, BenchmarkRecorder.BucketOf(TimeSpan.FromSeconds(60)));
        Assert.Equal(ElapsedBucket.Min5, BenchmarkRecorder.BucketOf(TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void ReachedRunRecordsProbeMsWithoutTReady()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        var boundary = new LoadBoundary(0, [new TraceEvent(@"D:\Games\Foo\a.pak", 0, 1 << 24)], 0, 0);

        int? ms = LoadBoundaryWatch.Run(Game("Foo"), new AutoStatus(null, new ListLog()), new ListLog(), default, () => _t0.AddSeconds(5), _ => { },
            _ => boundary, _ => new ProbeResult(8, 0, 0.1), rec);

        Assert.Equal(0, ms);
        LoadSample s = Assert.Single(MetricsJson.ParseSamples(File.ReadAllText(Path_)));
        Assert.Equal("Foo", s.GameKey);
        Assert.Null(s.TReadyMs);
        Assert.Equal(0L, s.ProbeReadyMs);
        Assert.Equal(ElapsedBucket.Sec0, s.Elapsed);
        Assert.Equal(CacheState.Unknown, s.CacheState);
        Assert.Equal(Scenario.Unknown, s.Scenario);
        Assert.Equal("service_uptime", s.ElapsedBasis);
    }

    [Fact]
    public void UnreachedRunRecordsNullProbeAndServiceUptimeBucket()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0.AddSeconds(-400));
        var boundary = new LoadBoundary(0, [new TraceEvent(@"D:\Games\Foo\a.pak", 0, 1 << 24)], 0, 0);
        TimeSpan clock = TimeSpan.Zero;

        int? ms = LoadBoundaryWatch.Run(Game("Foo"), new AutoStatus(null, new ListLog()), new ListLog(), default, () => _t0 + clock, d => clock += d,
            _ => boundary, _ => new ProbeResult(8, 1, 5.0), rec);

        Assert.Null(ms);
        LoadSample s = Assert.Single(MetricsJson.ParseSamples(File.ReadAllText(Path_)));
        Assert.Null(s.ProbeReadyMs);
        Assert.Null(s.TReadyMs);
        Assert.Equal(ElapsedBucket.Min5, s.Elapsed);
    }

    [Fact]
    public void KeepsOnlyTheLatestSamples()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        for (int i = 0; i < BenchmarkRecorder.MaxSamples + 5; i++)
        {
            rec.Record("g" + i, _t0, null);
        }

        List<LoadSample> saved = MetricsJson.ParseSamples(File.ReadAllText(Path_));
        Assert.Equal(BenchmarkRecorder.MaxSamples, saved.Count);
        Assert.Equal("g5", saved[0].GameKey);
        Assert.Equal("g" + (BenchmarkRecorder.MaxSamples + 4), saved[^1].GameKey);
    }

    [Fact]
    public void BrokenFileIsIgnoredAndReplaced()
    {
        File.WriteAllText(Path_, "{not json");

        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        Assert.Equal("{not json", File.ReadAllText(Path_ + ".bad")); // 깨진 파일은 .bad 로 보관
        rec.Record("Foo", _t0, 3);

        LoadSample s = Assert.Single(MetricsJson.ParseSamples(File.ReadAllText(Path_)));
        Assert.Equal(3L, s.ProbeReadyMs);
    }

    [Fact]
    public void CancelledRoundIsNotRecorded()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        var boundary = new LoadBoundary(0, [new TraceEvent(@"D:\Games\Foo\a.pak", 0, 1 << 24)], 0, 0);
        using var stop = new CancellationTokenSource();

        int? ms = LoadBoundaryWatch.Run(Game("Foo"), new AutoStatus(null, new ListLog()), new ListLog(), stop.Token, () => _t0.AddSeconds(5), _ => stop.Cancel(),
            _ => boundary, _ => new ProbeResult(8, 1, 5.0), rec);

        Assert.Null(ms);
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void BrokenItemIsSkippedAndTheRestIsKept()
    {
        File.WriteAllText(Path_, """{"samples": [{"game_key": "Bad", "scenario": "Nope", "arm": "Baseline", "elapsed": "Sec0", "cache_state": "Unknown"}, {"game_key": "Ok", "scenario": "Unknown", "arm": "Baseline", "elapsed": "Min5", "cache_state": "Warm", "probe_ready_ms": 7}]}""");

        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        rec.Record("New", _t0, null);

        List<LoadSample> saved = MetricsJson.ParseSamples(File.ReadAllText(Path_));
        Assert.Equal(new[] { "Ok", "New" }, saved.Select(s => s.GameKey));
    }

    [Fact]
    public void RecordWithoutElapsedBasisReadsAsUnknown()
    {
        LoadSample s = Assert.Single(MetricsJson.ParseSamples("""{"samples": [{"game_key": "Foo", "scenario": "S0", "arm": "Baseline", "elapsed": "Sec0", "cache_state": "Warm"}]}"""));

        Assert.Equal("unknown", s.ElapsedBasis);
    }

    [Fact]
    public void ExistingBadBackupIsKeptAndTheNewBrokenFileGetsATimedName()
    {
        File.WriteAllText(Path_ + ".bad", "old");
        File.WriteAllText(Path_, "{not json");

        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);

        Assert.Equal("old", File.ReadAllText(Path_ + ".bad"));
        Assert.Equal("{not json", File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, MetricsJson.SamplesFile + ".bad.*"), f => f != Path_ + ".bad")));
        rec.Record("Foo", _t0, 3);
        Assert.Single(MetricsJson.ParseSamples(File.ReadAllText(Path_)));
    }

    [Fact]
    public void BrokenFileIsKeptWhenItCannotBeMovedAndNothingIsWritten()
    {
        File.WriteAllText(Path_, "{not json");
        using var hold = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read); // 읽기는 되지만 옮기기(삭제)는 막아 .bad 보관이 실패하는 상황

        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        rec.Record("Foo", _t0, 3);

        Assert.Equal("{not json", File.ReadAllText(Path_));
        Assert.False(File.Exists(Path_ + ".bad"));
    }

    [Fact]
    public void FileLockedAtStartupIsNotReplacedByLaterRecords()
    {
        File.WriteAllText(Path_, """{"samples": [{"game_key": "Ok", "scenario": "Unknown", "arm": "Baseline", "elapsed": "Min5", "cache_state": "Warm", "probe_ready_ms": 7}]}""");
        string before = File.ReadAllText(Path_);

        using (var hold = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
            rec.Record("New", _t0, null);
        }

        Assert.Equal(before, File.ReadAllText(Path_));
    }

    [Fact]
    public void CandidateArmIsRecordedAndReadBack()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        rec.Record("Foo", _t0, 3, Arm.Candidate);
        rec.Record("Foo", _t0, 4);

        List<LoadSample> saved = MetricsJson.ParseSamples(File.ReadAllText(Path_));
        Assert.Equal(new[] { Arm.Candidate, Arm.Baseline }, saved.Select(s => s.Arm));
    }

    [Fact]
    public void ResultGateRejectsOnlyAfterFiveComparableSamplesPerArmFromTheSameBuild()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        GateResult? rejected = null;
        rec.GateEvaluated = (_, result) => rejected = result;

        foreach (long ms in Enumerable.Repeat(100L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Baseline, ms));
        }

        foreach (long ms in Enumerable.Repeat(110L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Candidate, ms));
        }

        Assert.Equal(ResultGate.Reject, rejected?.Verdict);
        Assert.Equal(ResultGate.MinSamples, rejected?.BaselineUsed);
        Assert.Equal(ResultGate.MinSamples, rejected?.CandidateUsed);
    }

    [Fact]
    public void ResultGateDoesNotCompareDifferentBuildsOrProbeOnlySamples()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        bool rejected = false;
        rec.GateEvaluated = (_, _) => rejected = true;

        foreach (long ms in Enumerable.Repeat(100L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Baseline, ms));
        }

        foreach (long ms in Enumerable.Repeat(110L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-b", Arm.Candidate, ms));
        }

        rec.Record("Foo", _t0, 500, Arm.Candidate);

        Assert.False(rejected);
    }

    [Fact]
    public void ResultGateIgnoresSamplesRecordedBeforeTheLastModeChange()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        bool rejected = false;
        rec.GateEvaluated = (_, _) => rejected = true;
        rec.GateSince = () => DateTimeOffset.UtcNow.AddMinutes(1); // 모드를 방금 바꾼 것처럼 아래 표본은 모두 그 이전

        foreach (long ms in Enumerable.Repeat(100L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Baseline, ms));
        }

        foreach (long ms in Enumerable.Repeat(110L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Candidate, ms));
        }

        Assert.False(rejected);
    }

    [Fact]
    public void ResultGateUsesSamplesAfterTheLastModeChange()
    {
        var rec = new BenchmarkRecorder(Path_, new ListLog(), _t0);
        GateResult? rejected = null;
        rec.GateEvaluated = (_, result) => rejected = result;
        rec.GateSince = () => DateTimeOffset.UtcNow.AddMinutes(-1);

        foreach (long ms in Enumerable.Repeat(100L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Baseline, ms));
        }

        foreach (long ms in Enumerable.Repeat(110L, ResultGate.MinSamples))
        {
            rec.Record(Comparable("build-a", Arm.Candidate, ms));
        }

        Assert.Equal(ResultGate.Reject, rejected?.Verdict);
    }

    private static LoadSample Comparable(string build, Arm arm, long ms) => new("Foo", Scenario.S0, arm, ElapsedBucket.Min5, ms,
        GameBuild: build, CacheState: CacheState.Warm, ElapsedBasis: "service_uptime");
}
