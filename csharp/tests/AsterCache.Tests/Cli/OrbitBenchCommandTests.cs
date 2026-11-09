using AsterCache.Cli;
using AsterCache.Cli.Commands;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Service;
using AsterCache.Tests.Windows;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Cli;

/// aster orbit-bench run|summarize: 인자 오류, 거부 조건 (게임 실행·서비스 실행), 후보 순서 seam 오류, 실행 파일과 판정 파일의 왕복.
/// 게임 폴더는 임시 폴더의 가짜 파일이고, 데이터 폴더도 임시 폴더입니다 (ProgramData 와 실제 게임은 건드리지 않음).
public sealed class OrbitBenchCommandTests : IDisposable
{
    private readonly Func<GameSettings, bool> _savedRunning = OrbitBenchCommand.GameRunning;
    private readonly Func<bool> _savedService = OrbitBenchCommand.ServiceRunning;
    private readonly Func<GameSettings, List<ReadOp>?>? _savedIncumbent = OrbitBenchCommand.IncumbentProvider;
    private readonly Func<GameSettings, List<ReadOp>, double, OrbitRuntime.ForcedCandidate>? _savedCandidate = OrbitBenchCommand.CandidateProvider;
    private readonly Func<TimeSpan, CancellationToken, bool> _savedWait = OrbitCommand.Wait;
    private readonly Func<string, Func<GameSettings, bool>, OrbitCalibration> _savedFactory = OrbitCommand.CalibrationFactory;
    private readonly TempDir _dir = new();
    private readonly string _data;
    private readonly string _plans;
    private readonly string _root;
    private readonly string _file;
    private readonly List<ReadOp> _ops;

    public OrbitBenchCommandTests()
    {
        _data = _dir.Combine("data");
        _plans = Path.Combine(_data, "plans");
        _root = _dir.Combine("Games");
        Directory.CreateDirectory(_plans);
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(_file, new byte[3 << 20]);
        _ops = [new ReadOp(_file, 0, 1 << 20), new ReadOp(_file, 1 << 20, 1 << 20), new ReadOp(_file, 2 << 20, 1 << 20)];
        WriteConfig();

        OrbitBenchCommand.GameRunning = _ => false;
        OrbitBenchCommand.ServiceRunning = () => false;
        OrbitBenchCommand.IncumbentProvider = _ => _ops;
        OrbitBenchCommand.CandidateProvider = (_, inc, _) => new OrbitRuntime.ForcedCandidate([.. inc.Skip(1), inc[0]], "forced_candidate", "model", 0.5); // 같은 op 를 회전한 후보
    }

    public void Dispose()
    {
        OrbitBenchCommand.GameRunning = _savedRunning;
        OrbitBenchCommand.ServiceRunning = _savedService;
        OrbitBenchCommand.IncumbentProvider = _savedIncumbent;
        OrbitBenchCommand.CandidateProvider = _savedCandidate;
        OrbitCommand.Wait = _savedWait;
        OrbitCommand.CalibrationFactory = _savedFactory;
        _dir.Dispose();
    }

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = AsterCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private JObject GameJson(string name, string? antiCheat = null)
    {
        var game = new JObject
        {
            { "name", name }, { "exe_names", new List<object?> { name.ToLowerInvariant() + ".exe" } }, { "trace", Path.Combine(_plans, name + ".prefetch.json") }, { "plan", Path.Combine(_plans, name + ".plan.json") },
            { "origin_root", _root }, { "allowed_roots", new List<object?> { _root } }, { "volume_label", "GAMES" },
        };
        if (antiCheat is not null)
        {
            game.Add("anticheat", antiCheat);
        }

        return game;
    }

    private void WriteConfig(params JObject[] extra)
    {
        List<object?> games = [GameJson("Foo"), .. extra];
        var config = new JObject { { "games", games } };
        File.WriteAllText(Path.Combine(_data, "config.json"), PyJson.Dumps(config));
    }

    private string Out(string name) => Path.Combine(_dir.Path, name);

    private (int Code, string Out, string Err) Run(string arm, string outFile) =>
        Cli("orbit-bench", "run", "--game", "Foo", "--arm", arm, "--out", outFile, "--data-dir", _data);

    private static string WriteRun(string path, string arm, string game = "Foo", double? value = null, string? inputs = null)
    {
        var run = new JObject { { "version", 1L }, { "arm", arm }, { "game", game }, { "total_ms", 100.0 }, { "value_mean_ms", value }, { "inputs", inputs }, { "episodes", new List<object?>() } };
        File.WriteAllText(path, PyJson.Dumps(run));
        return path;
    }

    [Fact]
    public void ReadTimedGivesOneNondecreasingCompletionTimePerOp()
    {
        List<(ReadOp Op, double DoneMs)> done = OrbitBenchCommand.ReadTimed(_ops).Done;

        Assert.Equal(_ops, done.Select(d => d.Op));
        Assert.True(done.Zip(done.Skip(1)).All(p => p.Second.DoneMs >= p.First.DoneMs));
    }

    [Fact]
    public void ShortFileEndsItsOpAsShortReadAndTheRunStillFinishes()
    {
        string small = Path.Combine(_root, "small.bin");
        File.WriteAllBytes(small, new byte[100]);
        OrbitBenchCommand.IncumbentProvider = _ => [new ReadOp(small, 0, 1 << 20), .. _ops];
        string outFile = Out("short.json");

        (int code, _, _) = Run("A", outFile);

        Assert.Equal(0, code);
        Assert.True((long)((JObject)JsonRead.ReadFile(outFile)!)["short_reads"]! >= 1);
    }

    [Fact]
    public void UnopenableFileCountsAsErrorAndIsSkipped()
    {
        OrbitBenchCommand.ReadTimedResult r = OrbitBenchCommand.ReadTimed([new ReadOp(Path.Combine(_root, "missing.bin"), 0, 1 << 20), _ops[0]]);

        Assert.Equal(1, r.Errors);
        Assert.Single(r.Done);
    }

    [Fact]
    public void RunOnCandidateWritesEveryOpInCandidateOrder()
    {
        string outFile = Out("b1.json");

        (int code, string text, _) = Run("B", outFile);

        Assert.Equal(0, code);
        Assert.Equal(3L, ((JObject)JsonRead.Parse(text)!)["ops"]);
        var file = (JObject)JsonRead.ReadFile(outFile)!;
        Assert.Equal("B", file["arm"]);
        var ops = (List<object?>)file["ops"]!;
        Assert.Equal(3, ops.Count);
        Assert.Equal(1L << 20, ((JObject)ops[0]!)["offset"]); // 회전 후보의 첫 op 는 원래 두 번째
        Assert.Empty((List<object?>)file["episodes"]!); // orbit.json 이 없으면 에피소드 지표는 비움
    }

    [Fact]
    public void BrokenOrbitFileGivesNoEpisodes()
    {
        File.WriteAllText(Path.Combine(_plans, "Foo.orbit.json"), "{not json");
        string outFile = Out("a1.json");

        Assert.Equal(0, Run("A", outFile).Code);

        Assert.Empty((List<object?>)((JObject)JsonRead.ReadFile(outFile)!)["episodes"]!);
    }

    [Fact]
    public void ValidOrbitFileGivesReadyTimeForEachEpisode()
    {
        var orbit = new JObject
        {
            { "version", 1L },
            { "inputs", "fp1" },
            { "episodes", new List<object?> { new JObject
                {
                    { "blocks", new List<object?> { new List<object?> { _file, 0L }, new List<object?> { _file, 1L } } },
                    { "runs_seen", 1L }, { "runs_total", 2L }, { "d_ms", 5.0 },
                } } },
        };
        File.WriteAllText(Path.Combine(_plans, "Foo.orbit.json"), PyJson.Dumps(orbit));
        string outFile = Out("a2.json");

        Assert.Equal(0, Run("A", outFile).Code);

        var episodes = (List<object?>)((JObject)JsonRead.ReadFile(outFile)!)["episodes"]!;
        var first = (JObject)episodes[0]!;
        Assert.Single(episodes);
        Assert.Equal("fp1", ((JObject)JsonRead.ReadFile(outFile)!)["inputs"]);
        Assert.NotNull(first["t_ready_ms"]);
        Assert.Equal(0L, first["blocks_missing"]);
    }

    [Fact]
    public void RescoreRecomputesRunMetricsFromOperationsAndOrbitEpisodes()
    {
        const long block = OrbitBenchMetrics.BlockBytes;
        string orbitPath = Out("Foo.orbit.json");
        var orbit = new JObject
        {
            { "version", 2L },
            { "inputs", "fp-v2" },
            { "files", new List<object?> { _file } },
            { "episodes", new List<object?> { new JObject
                {
                    { "ranges", new List<object?> { new List<object?> { 0L, 1L, 2L } } },
                    { "runs_seen", 3L }, { "runs_total", 6L }, { "d_ms", 5.0 },
                } } },
        };
        File.WriteAllText(orbitPath, PyJson.Dumps(orbit));

        string runPath = Out("run.json");
        var run = new JObject
        {
            { "version", 1L }, { "arm", "B" }, { "game", "Foo" }, { "total_ms", 80.0 },
            { "inputs", null }, { "value_mean_ms", null }, { "curve_area_ms", null },
            { "episodes", new List<object?>() },
            { "ops", new List<object?>
                {
                    new JObject { { "path", _file }, { "offset", 0L }, { "length", block }, { "done_ms", 80.0 } },
                    new JObject { { "path", _file }, { "offset", block }, { "length", block }, { "done_ms", 20.0 } },
                    new JObject { { "path", _file }, { "offset", 2 * block }, { "length", block }, { "done_ms", 60.0 } },
                } },
        };
        File.WriteAllText(runPath, PyJson.Dumps(run));

        (int code, _, string error) = Cli("orbit-bench", "rescore", "--orbit", orbitPath, "--in", runPath);

        Assert.Equal(0, code);
        Assert.Empty(error);
        var rescored = (JObject)JsonRead.ReadFile(runPath)!;
        Assert.Equal("fp-v2", rescored["inputs"]);
        Assert.Equal(60.0, rescored["value_mean_ms"]);
        Assert.Equal(20.0, rescored["curve_area_ms"]);
        var episode = Assert.Single((List<object?>)rescored["episodes"]!) as JObject;
        Assert.Equal(60.0, episode!["t_ready_ms"]);
        Assert.Equal(0L, episode["blocks_missing"]);
    }

    [Fact]
    public void MissingCandidateOrderStopsWithAnError()
    {
        OrbitBenchCommand.CandidateProvider = null;

        (int code, string text, _) = Run("B", Out("b.json"));

        Assert.Equal(1, code);
        Assert.Contains("candidate order is not available", text);
    }

    [Fact]
    public void CandidateWithDifferentOpsIsRejected()
    {
        OrbitBenchCommand.CandidateProvider = (_, inc, _) => new OrbitRuntime.ForcedCandidate(inc.Skip(1).ToList(), "forced_candidate");

        (int code, string text, _) = Run("B", Out("b.json"));

        Assert.Equal(1, code);
        Assert.Contains("differ from incumbent", text);
    }

    [Fact]
    public void GameRunningRefuses()
    {
        OrbitBenchCommand.GameRunning = _ => true;

        (int code, string text, _) = Run("A", Out("a.json"));

        Assert.Equal(1, code);
        Assert.Contains("the game is running", text);
    }

    [Fact]
    public void RunningServiceRefuses()
    {
        OrbitBenchCommand.ServiceRunning = () => true;

        (int code, string text, _) = Run("A", Out("a.json"));

        Assert.Equal(1, code);
        Assert.Contains("service is running", text);
    }

    [Fact]
    public void ArmOtherThanAOrBIsAnArgumentError()
    {
        (int code, _, _) = Run("C", Out("c.json"));

        Assert.Equal(2, code);
    }

    [Fact]
    public void RunThenSummarizeRoundTripsAbbaRuns()
    {
        var files = new List<string>();
        foreach (string arm in new[] { "A", "B", "B", "A" })
        {
            string f = Out("run_" + files.Count + "_" + arm + ".json");
            Assert.Equal(0, Run(arm, f).Code);
            files.Add(f);
        }

        string[] args = ["orbit-bench", "summarize", "--in", .. files];
        (int code, string text, _) = Cli(args);

        Assert.Equal(0, code);
        var json = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(4L, json["runs"]);
        Assert.Equal(2, ((List<object?>)json["pairs"]!).Count);
        var flags = (JObject)json["flags"]!; // 실제 읽기 시간은 회차마다 달라 판정 값은 고정하지 않음, 에피소드가 없으면 플래그 4 는 null
        Assert.Equal(4, flags.Count);
        Assert.Null(flags["4"]);
        Assert.Null(flags["5"]);
        Assert.Equal(2L, json["excluded_pairs"]); // 값이 없어 두 쌍 모두 비교에서 뺌
    }

    [Fact]
    public void SummarizeExcludesPairsWhoseAValueIsNotPositive()
    {
        string[] files = [WriteRun(Out("e1.json"), "A", value: 0), WriteRun(Out("e2.json"), "B", value: 50), WriteRun(Out("e3.json"), "B", value: 50), WriteRun(Out("e4.json"), "A", value: 100)];
        string[] args = ["orbit-bench", "summarize", "--in", .. files];

        (int code, string text, _) = Cli(args);

        Assert.Equal(0, code);
        var json = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(2, ((List<object?>)json["pairs"]!).Count);
        Assert.Equal(1L, json["excluded_pairs"]);
    }

    [Fact]
    public void SummarizeRejectsRunsOfDifferentGamesOrOtherVersions()
    {
        string[] mixed = [WriteRun(Out("g1.json"), "A"), WriteRun(Out("g2.json"), "B", "Bar"), WriteRun(Out("g3.json"), "B"), WriteRun(Out("g4.json"), "A")];
        string[] args = ["orbit-bench", "summarize", "--in", .. mixed];

        (int code, string text, _) = Cli(args);

        Assert.Equal(1, code);
        Assert.Contains("different games", text);

        string old = Out("v2.json");
        File.WriteAllText(old, PyJson.Dumps(new JObject { { "version", 2L }, { "arm", "A" }, { "game", "Foo" }, { "total_ms", 100.0 }, { "value_mean_ms", null }, { "episodes", new List<object?>() } }));
        string[] oldArgs = ["orbit-bench", "summarize", "--in", old];

        (code, text, _) = Cli(oldArgs);

        Assert.Equal(1, code);
        Assert.Contains("version must be 1", text);
    }

    [Fact]
    public void SummarizeRejectsRunsWithDifferentOrbitInputs()
    {
        string[] files = [WriteRun(Out("i1.json"), "A", inputs: "x"), WriteRun(Out("i2.json"), "B", inputs: "x"), WriteRun(Out("i3.json"), "B", inputs: "y"), WriteRun(Out("i4.json"), "A", inputs: "x")];
        string[] args = ["orbit-bench", "summarize", "--in", .. files];

        (int code, string text, _) = Cli(args);

        Assert.Equal(1, code);
        Assert.Contains("different orbit.json inputs", text);
    }

    [Fact]
    public void SummarizeRejectsCountThatIsNotAMultipleOfFour()
    {
        string[] files = [WriteRun(Out("x1.json"), "A"), WriteRun(Out("x2.json"), "B"), WriteRun(Out("x3.json"), "B")];
        string[] args = ["orbit-bench", "summarize", "--in", .. files];

        (int code, string text, _) = Cli(args);

        Assert.Equal(1, code);
        Assert.Contains("multiple of 4", text);
    }

    // 기준 크기 (64 MiB) 를 넘는 가짜 run 파일. 실제 게임 파일은 읽지 않고 같은 블록 집합을 매 회차 반복합니다.
    private void WriteRunFiles(int count)
    {
        string raw = Path.Combine(_plans, "Foo.trace.json");
        for (int n = 1; n <= count; n++)
        {
            TraceWriter.WriteFile(TraceWriter.RunPath(raw, n), [new TraceEvent(_file, 0, 70L << 20, TMs: 100)]);
        }
    }

    private (int Code, string Out, string Err) Model() => Cli("orbit", "model", "--game", "Foo", "--data-dir", _data);

    [Fact]
    public void OrbitModelBuildsOrbitJsonFromThreeRunFilesAndPrintsOneSummaryLine()
    {
        WriteRunFiles(3);

        (int code, string text, _) = Model();

        Assert.Equal(0, code);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var json = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(1L, json["episodes"]);
        Assert.Equal(3L, json["runs"]);
        Assert.Equal(0.0, json["unmatched"]);
        Assert.True(File.Exists(Path.Combine(_plans, "Foo.orbit.json")));
        Assert.Single((List<object?>)((JObject)JsonRead.ReadFile(Path.Combine(_plans, "Foo.orbit.json"))!)["episodes"]!);
    }

    [Fact]
    public void OrbitModelWritesNothingWhileTheServiceOrAnyRegisteredGameRuns()
    {
        WriteRunFiles(3);
        WriteConfig(GameJson("Bar"));
        OrbitBenchCommand.ServiceRunning = () => true;

        (int code, string text, _) = Model();

        Assert.Equal(1, code);
        Assert.Contains("service is running", text);

        OrbitBenchCommand.ServiceRunning = () => false;
        OrbitBenchCommand.GameRunning = g => g.Name == "Bar";

        (code, text, _) = Model();

        Assert.Equal(1, code);
        Assert.Contains("the game is running: Bar", text);
        Assert.Empty(Directory.GetFiles(_plans, "*.orbit.json*")); // 임시 파일도 남기지 않음
    }

    [Fact]
    public void OrbitModelWithoutRunFilesFailsAndWritesNothing()
    {
        (int code, string text, _) = Model();

        Assert.Equal(1, code);
        Assert.Contains("orbit model was not built", text);
        Assert.Empty(Directory.GetFiles(_plans, "*.orbit.json*"));
    }

    [Fact]
    public void BenchBRunRecordsTheLambdaSourceAndValueAndAIsNull()
    {
        double? seen = null;
        OrbitBenchCommand.CandidateProvider = (_, inc, lambda) =>
        {
            seen = lambda;
            return new OrbitRuntime.ForcedCandidate([.. inc.Skip(1), inc[0]], "forced_candidate", "bench_override", lambda);
        };
        string b = Out("lb.json");
        string a = Out("la.json");

        (int code, string text, _) = Cli("orbit-bench", "run", "--game", "Foo", "--arm", "B", "--out", b, "--data-dir", _data, "--lambda-per-min", "2.5");

        Assert.Equal(0, code);
        Assert.Equal(2.5, seen);
        var file = (JObject)JsonRead.ReadFile(b)!;
        Assert.Equal("bench_override", file["lambda_source"]);
        Assert.Equal(2.5, file["lambda_per_min"]);
        Assert.Equal("bench_override", ((JObject)JsonRead.Parse(text)!)["lambda_source"]);
        Assert.Equal(0, Run("A", a).Code);
        Assert.Null(((JObject)JsonRead.ReadFile(a)!)["lambda_source"]);
    }

    [Fact]
    public void BenchBRunDefaultsToOneInterruptionPerMinute()
    {
        double? seen = null;
        OrbitBenchCommand.CandidateProvider = (_, inc, lambda) =>
        {
            seen = lambda;
            return new OrbitRuntime.ForcedCandidate([.. inc], "forced_candidate", "bench_override", lambda);
        };

        Assert.Equal(0, Run("B", Out("ld.json")).Code);

        Assert.Equal(1.0, seen);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositiveLambdaPerMinIsAnArgumentError(string value)
    {
        (int code, _, _) = Cli("orbit-bench", "run", "--game", "Foo", "--arm", "B", "--out", Out("ln.json"), "--data-dir", _data, "--lambda-per-min=" + value);

        Assert.Equal(2, code);
    }

    [Fact]
    public void CandidateWithoutOpsStopsWithTheReason()
    {
        OrbitBenchCommand.CandidateProvider = (_, _, _) => new OrbitRuntime.ForcedCandidate(null, "no_episode_model");

        (int code, string text, _) = Run("B", Out("b.json"));

        Assert.Equal(1, code);
        Assert.Contains("candidate order is not available: no_episode_model", text);
    }

    private string DriveLetter => OrbitCalibration.DiskKey(_root)[..1];

    // 가짜 읽기기와 가짜 대기: 실제 디스크 읽기도 10 초 대기도 하지 않음
    private (List<(string Path, long Offset, int Length)> Reads, List<TimeSpan> Waits) FakeCalibration()
    {
        var reads = new List<(string Path, long Offset, int Length)>();
        var waits = new List<TimeSpan>();
        OrbitBenchCommand.IncumbentProvider = _ => [new ReadOp(_file, 0, 64L << 30)];
        OrbitCommand.Wait = (span, _) =>
        {
            waits.Add(span);
            return true;
        };
        OrbitCommand.CalibrationFactory = (dataDir, active) => new OrbitCalibration(dataDir, new ListLog(), (path, offset, length, _) =>
        {
            reads.Add((path, offset, length));
            return TimeSpan.FromMilliseconds(10);
        }, () => new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), _ => false, active, new Random(1), _ => CapabilityOutcome.Allow, _ => "fake");
        return (reads, waits);
    }

    private (int Code, string Out, string Err) Calibrate(params string[] extra) =>
        Cli(["orbit", "calibrate", "--disk", DriveLetter.ToLowerInvariant(), "--data-dir", _data, .. extra]);

    private JObject FakeDisk() => (JObject)((JObject)((JObject)JsonRead.ReadFile(Path.Combine(_data, OrbitCalibration.FileName))!)["disks"]!)["fake"]!;

    [Fact]
    public void CalibrateTakesTheRequestedSamplesTenSecondsApartAndSavesThemAsBootstrap()
    {
        var (reads, waits) = FakeCalibration();

        (int code, string text, _) = Calibrate("--samples", "3");

        Assert.Equal(0, code);
        Assert.Equal(3 * 15, reads.Count);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], waits); // 첫 표본 앞에는 대기 없음
        Assert.Equal(3 + 1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // 표본마다 한 줄 + 요약
        var seek = (List<object?>)FakeDisk()["seek"]!;
        Assert.Equal(3, seek.Count);
        Assert.All(seek, s => Assert.Equal("bootstrap", ((JObject)s!)["source"]));
    }

    [Fact]
    public void CalibrateStopsAtTheDefaultCapOnceTheEnvelopeExists()
    {
        var (_, waits) = FakeCalibration();

        (int code, string text, _) = Calibrate();

        Assert.Equal(0, code);
        Assert.Equal(36, waits.Count); // 가짜 보정기는 표본 37 개째에 장치 구간이 생겨 상한 60 전에 멈춤
        Assert.Equal(37, ((List<object?>)FakeDisk()["seek"]!).Count);
        Assert.Contains("\"calibrated\":true,\"seek_n\":37,\"thr_n\":37", text);
    }

    [Fact]
    public void CalibrateReportsNotCalibratedWhenTheCapComesFirst()
    {
        FakeCalibration();

        (int code, string text, _) = Calibrate("--samples", "3");

        Assert.Equal(0, code);
        Assert.Contains("\"calibrated\":false,\"seek_n\":3,\"thr_n\":3", text);
    }

    [Fact]
    public void CalibrateWithARunningServiceReadsNothingAndFails()
    {
        var (reads, _) = FakeCalibration();
        OrbitBenchCommand.ServiceRunning = () => true;

        (int code, string text, _) = Calibrate("--samples", "2");

        Assert.Equal(1, code);
        Assert.Contains("service is running", text);
        Assert.Empty(reads);
    }

    [Fact]
    public void CalibrateWithAnyRegisteredGameRunningReadsNothingAndNamesIt()
    {
        var (reads, _) = FakeCalibration();
        WriteConfig(GameJson("Bar"));
        OrbitBenchCommand.GameRunning = g => g.Name == "Bar"; // 등록된 게임 전부를 확인

        (int code, string text, _) = Calibrate("--samples", "2");

        Assert.Equal(1, code);
        Assert.Contains("the game is running: Bar", text);
        Assert.Empty(reads);
    }

    [Fact]
    public void CalibrateSkipsAGameWithoutAPrewarmListAndSamplesTheOthers()
    {
        var (reads, _) = FakeCalibration();
        WriteConfig(GameJson("Bar"));
        OrbitBenchCommand.IncumbentProvider = g => g.Name == "Bar"
            ? throw new FileNotFoundException("no plan yet", Path.Combine(_plans, "Bar.prefetch.json"))
            : [new ReadOp(_file, 0, 64L << 30)];

        (int code, string text, _) = Calibrate("--samples", "2");

        Assert.Equal(0, code);
        Assert.Equal(2 * 15, reads.Count); // Bar 를 건너뛰고 Foo 만 두 번 표본
        Assert.DoesNotContain("FileNotFound", text);
    }

    [Fact]
    public void CalibrateChecksAgainBeforeEverySampleAndStopsWhenAGameStarts()
    {
        var (reads, waits) = FakeCalibration();
        OrbitCommand.Wait = (span, _) =>
        {
            waits.Add(span);
            OrbitBenchCommand.GameRunning = _ => true; // 첫 표본 뒤 게임이 시작됨
            return true;
        };

        (int code, string text, _) = Calibrate("--samples", "5");

        Assert.Equal(1, code);
        Assert.Contains("the game is running: Foo", text);
        Assert.Equal(15, reads.Count); // 첫 표본만 읽음
    }

    [Fact]
    public void CalibrateNamesTheReasonWhenASampleReadsNothing()
    {
        FakeCalibration();
        OrbitBenchCommand.IncumbentProvider = _ => [new ReadOp(_file, 0, 64L << 20)]; // 최소 거리 안이라 두 번째 표본부터 읽을 곳이 없음

        (_, string text, _) = Calibrate("--samples", "2");

        Assert.Contains("\"bytes\":0,\"reason\":\"no_far_position\"", text);
    }

    [Fact]
    public void CalibrateKeepsTheSamplesTakenBeforeACancel()
    {
        var (reads, _) = FakeCalibration();
        OrbitCommand.Wait = (_, _) => false; // 대기 중 취소

        (int code, string text, _) = Calibrate("--samples", "5");

        Assert.Equal(1, code);
        Assert.Contains("\"cancelled\":true", text);
        Assert.Equal(15, reads.Count);
        Assert.Single((List<object?>)FakeDisk()["seek"]!);
    }

    [Fact]
    public void CalibrateLeavesOutGamesTheAntiCheatGateBlocks()
    {
        var (reads, _) = FakeCalibration();
        File.WriteAllText(Path.Combine(_data, "config.json"), PyJson.Dumps(new JObject { { "games", new List<object?> { GameJson("Foo", "eac") } } }));

        (int code, string text, _) = Calibrate("--samples", "2");

        Assert.Equal(1, code);
        Assert.Contains("allows calibration reads", text);
        Assert.Empty(reads);
    }

    [Fact]
    public void BenchRunRefusesWhenAnotherRegisteredGameIsRunning()
    {
        WriteConfig(GameJson("Bar"));
        OrbitBenchCommand.GameRunning = g => g.Name == "Bar";

        (int code, string text, _) = Run("A", Out("a.json"));

        Assert.Equal(1, code);
        Assert.Contains("the game is running: Bar", text);
    }

    [Theory]
    [InlineData("SERVICE_NAME: AsterCache\n        TYPE               : 10  WIN32_OWN_PROCESS\n        STATE              : 1  STOPPED\n", true)]
    [InlineData("SERVICE_NAME: AsterCache\n        STATE              : 4  RUNNING\n", false)]
    [InlineData("SERVICE_NAME: AsterCache\n        상태               : 4  RUNNING\n                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\n", false)]
    [InlineData("SERVICE_NAME: AsterCache\n        상태               : 1  STOPPED\n", true)]
    [InlineData("SERVICE_NAME: AsterCache\n        STATE              : 3  STOP_PENDING\n", false)]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\n\nThe specified service does not exist as an installed service.\n", true)]
    [InlineData("", false)]
    [InlineData("[SC] OpenService FAILED 5: Access is denied.\n", false)]
    public void ServiceStoppedReadsTheScQueryState(string output, bool stopped) => Assert.Equal(stopped, OrbitBenchCommand.ServiceStopped(output));
}
