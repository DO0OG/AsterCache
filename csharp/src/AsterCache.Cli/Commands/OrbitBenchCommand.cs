using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using AsterCache.Core.Json;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Cli.Commands;

/// 오프라인 벤치 (설계안 4.1). run 은 한 팔의 예열 순서를 동기 읽기로 읽고 op 마다 완료 시각을 씁니다 (A = 제품 예열 순서, B = 후보).
/// summarize 는 ABBA 로 모은 run 출력을 쌍으로 비교해 판정합니다. 게임이 돌거나 서비스가 도는 중이면 거부합니다.
/// 실제 디스크 읽기 대여 (DiskReadCoordinator) 는 서비스 쪽 클래스라 여기서는 쓰지 않습니다. 서비스 정지 (sc query 의 STOPPED) 와 등록 게임 전부의 미실행 확인으로 대신합니다.
internal static class OrbitBenchCommand
{
    /// 후보 순서 seam. 입력은 게임과 A 팔의 예열 순서입니다.
    public static Func<GameSettings, List<ReadOp>, double, OrbitRuntime.ForcedCandidate>? CandidateProvider { get; set; } = OrbitRuntime.ForceCandidateOps;
    internal static Func<string, string, string, List<ReadOp>, List<ReadOp>?, long?, double, int, Action<string, double>, OrbitRuntime.CandidateAssessment>
        RescoreAssessmentProvider
    { get; set; } = DecideForRescore;

    /// A 팔은 제품 예열의 원래 순서를 씁니다. A·B 모두 같은 틈 없는 목록 (IncumbentPrewarmOps(g, 0)) 의 순서만 비교합니다.
    internal static Func<GameSettings, List<ReadOp>?>? IncumbentProvider { get; set; } = g => PrefetchStreamer.IncumbentPrewarmOps(g, 0);

    /// 서비스 이름 (csharp/scripts/cs_service.ps1 의 기본값).
    internal const string ServiceName = "AsterCache";

    /// 서비스가 정지 상태가 아닌지 (기본: sc query 출력으로 확인). 서비스가 설치되지 않았으면 정지로 봅니다.
    internal static Func<bool> ServiceRunning { get; set; } = () => !ServiceStopped(QueryService());

    /// 표본 사이 서비스 경합 확인 (기본: ServiceController 핸들을 가볍게 조회).
    internal static Func<bool> ServiceRunningLight { get; set; } = ServiceControllerRunning;

    /// sc query 출력에서 상태 코드가 1 (STOPPED) 인지. 필드 이름은 로캘마다 현지화되므로 "숫자 + 상태 이름" 값으로 찾습니다. 상태 값이 없고 오류 1060 (설치되지 않음) 이면 정지로 보며, 그 밖은 정지가 아닌 것으로 봅니다.
    internal static bool ServiceStopped(string scOutput)
    {
        Match state = Regex.Match(scOutput, @":\s*(\d+)\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
        return state.Success ? state.Groups[1].Value == "1" : scOutput.Contains("1060", StringComparison.Ordinal);
    }

    private static string QueryService()
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["query", ServiceName])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    private static bool ServiceControllerRunning()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status != ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return true; // 조회할 수 없으면 시작 경합이 없다고 가정하지 않습니다.
        }
    }

    /// 게임이 도는지 (기본: TierCommand.Running). 벤치 시험이 TierCommand 의 seam 을 바꾸면 그 시험과 섞이므로 따로 둡니다.
    internal static Func<GameSettings, bool> GameRunning { get; set; } = g => TierCommand.Running(g);

    /// 중단 이유 (서비스가 도는 중이거나 등록 게임이 실행 중), 없으면 null. 벤치 run 과 orbit calibrate 가 같이 씁니다.
    internal static string? Blocker(IEnumerable<GameSettings> games, bool lightweightServiceCheck = false)
    {
        if (lightweightServiceCheck ? ServiceRunningLight() : ServiceRunning())
        {
            return $"the {ServiceName} service is running; stop it first";
        }

        return games.FirstOrDefault(GameRunning) is { } running ? $"the game is running: {running.Name}; quit it first" : null;
    }

    internal static void EnsureIdle(IEnumerable<GameSettings> games)
    {
        if (Blocker(games) is { } why)
        {
            throw new InvalidOperationException(why);
        }
    }

    internal static void EnsureIdleBetweenSamples(IEnumerable<GameSettings> games)
    {
        if (Blocker(games, lightweightServiceCheck: true) is { } why)
        {
            throw new InvalidOperationException(why);
        }
    }

    public static readonly ArgOption[] RunOptions =
    [
        new("game", Required: true),
        new("arm", Required: true),
        new("out", Required: true),
        new("data-dir"),
        new("lambda-per-min", Type: ArgType.Double, Default: "1.0"),
    ];

    public static readonly ArgOption[] SummarizeOptions =
    [
        new("in", ArgKind.List, Required: true),
        new("out"),
    ];

    public static readonly ArgOption[] RescoreOptions =
    [
        new("orbit", Required: true),
        new("in", ArgKind.List, Required: true),
        new("out-dir"),
        new("data-dir"),
        new("budget-ms", Type: ArgType.Int, Default: Orbit3Constants.DecisionBudgetMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string arm = a.Text("arm")!;
        if (arm is not ("A" or "B"))
        {
            throw new ArgException("--arm must be A or B");
        }

        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir);
        string plans = Path.Combine(dataDir, "plans");
        string name = a.Text("game")!;
        IReadOnlyList<GameSettings> games = SettingsStore.Load(Path.Combine(dataDir, "config.json"), plans).Games;
        GameSettings game = games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new PyValueException($"game not registered: {name}");

        EnsureIdle(games); // 등록 게임 전부와 서비스 상태를 확인

        List<ReadOp> incumbent = IncumbentProvider?.Invoke(game) ?? throw new InvalidOperationException("incumbent prewarm order is not available in this build");
        double lambdaPerMin = a.Double("lambda-per-min") ?? 1.0;
        if (!double.IsFinite(lambdaPerMin) || lambdaPerMin <= 0)
        {
            throw new ArgException("--lambda-per-min must be a positive number");
        }

        (List<ReadOp> ops, OrbitRuntime.ForcedCandidate? forced) = arm == "A" ? (incumbent, null) : CandidateOps(game, incumbent, lambdaPerMin);

        string orbit = Path.Combine(plans, game.Name + ".orbit.json");
        string? orbitText = File.Exists(orbit) ? File.ReadAllText(orbit) : null;
        List<OrbitBenchMetrics.BenchEpisode> episodes = orbitText is null ? [] : OrbitBenchMetrics.ParseEpisodes(orbitText);

        ReadTimedResult read = ReadTimed(ops);
        List<(ReadOp Op, double DoneMs)> done = read.Done;
        OrbitBenchMetrics.BenchRunMetrics m = OrbitBenchMetrics.Evaluate(done, episodes);

        var result = new JObject
        {
            { "version", 1L },
            { "arm", arm },
            { "game", game.Name },
            { "inputs", orbitText is null ? null : OrbitBenchMetrics.Fingerprint(orbitText) },
            { "errors", (long)read.Errors },
            { "short_reads", (long)read.ShortReads },
            { "lambda_source", forced?.LambdaSource },
            { "lambda_per_min", forced?.LambdaPerMin },
            { "total_ms", Ms(m.TotalMs) },
            { "bytes", m.Bytes },
            { "value_mean_ms", MsOrNull(m.ValueMeanMs) },
            { "curve_area_ms", MsOrNull(m.CurveAreaMs) },
            { "episodes", m.Episodes.Select(e => (object?)new JObject { { "t_ready_ms", MsOrNull(e.TReadyMs) }, { "blocks_missing", (long)e.MissingBlocks } }).ToList() },
            { "ops", done.Select(x => (object?)new JObject { { "path", x.Op.Path }, { "offset", x.Op.Offset }, { "length", x.Op.Length }, { "done_ms", Ms(x.DoneMs) } }).ToList() },
        };

        string outPath = Path.GetFullPath(a.Text("out")!);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, PyJson.Dumps(result, indent: 1) + "\n", PyJson.Utf8);

        return AsterCli.Emit(o, new JObject
        {
            { "arm", arm },
            { "out", outPath },
            { "ops", (long)done.Count },
            { "errors", (long)read.Errors },
            { "short_reads", (long)read.ShortReads },
            { "bytes", m.Bytes },
            { "lambda_source", forced?.LambdaSource },
            { "lambda_per_min", forced?.LambdaPerMin },
            { "total_ms", Ms(m.TotalMs) },
            { "value_mean_ms", MsOrNull(m.ValueMeanMs) },
            { "curve_area_ms", MsOrNull(m.CurveAreaMs) },
        });
    }

    public static int Summarize(ParsedArgs a, TextWriter o)
    {
        var runs = new List<OrbitBenchMetrics.RunSummary>();
        var games = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new HashSet<string?>(StringComparer.Ordinal);
        foreach (string file in a.List("in"))
        {
            var j = JsonRead.ReadFile(file) as JObject ?? throw new PyValueException($"not a run file: {file}");
            if (OrbitBenchMetrics.Field(j, "version") is not 1L)
            {
                throw new ArgumentException($"run file version must be 1: {file}");
            }

            games.Add(OrbitBenchMetrics.Field(j, "game") as string ?? throw new PyValueException($"game missing: {file}"));
            inputs.Add(OrbitBenchMetrics.Field(j, "inputs") as string);
            var episodes = new List<double?>();
            foreach (object? item in OrbitBenchMetrics.Field(j, "episodes") as List<object?> ?? throw new PyValueException($"episodes missing: {file}"))
            {
                var row = item as JObject ?? throw new PyValueException($"episode row: {file}");
                episodes.Add(OrbitBenchMetrics.Field(row, "t_ready_ms") is { } t ? Num(t) : null);
            }

            runs.Add(new OrbitBenchMetrics.RunSummary(
                OrbitBenchMetrics.Field(j, "arm") as string ?? throw new PyValueException($"arm missing: {file}"),
                Num(OrbitBenchMetrics.Field(j, "total_ms")),
                OrbitBenchMetrics.Field(j, "value_mean_ms") is { } v ? Num(v) : null,
                episodes));
        }

        if (games.Count > 1)
        {
            throw new ArgumentException("run files are from different games");
        }

        if (inputs.Count > 1)
        {
            throw new ArgumentException("run files have different orbit.json inputs");
        }

        OrbitBenchMetrics.Verdict verdict = OrbitBenchMetrics.Summarize(runs);
        var result = new JObject
        {
            { "runs", (long)runs.Count },
            { "pairs", verdict.Pairs.Select(p => (object?)new JObject { { "value_reduction", Ratio(p.ValueReduction) }, { "total_worse", Ratio(p.TotalWorse) } }).ToList() },
            { "excluded_pairs", (long)verdict.Pairs.Count(p => p.ValueReduction is null) }, // A 값이 0 이하이거나 없는 쌍, B 값이 없는 쌍도 포함
            { "value_reduction_median", Ratio(verdict.ValueReductionMedian) },
            { "pairs_reduction_ge_10pct", (long)verdict.PairsReductionAtLeast10 },
            { "sign_p", Ratio(verdict.SignP) },
            { "total_worse_median", Ratio(verdict.TotalWorseMedian) },
            { "episode_p95_worse_max_ms", MsOrNull(verdict.EpisodeP95WorseMaxMs) },
            { "flags", new JObject { { "2", verdict.Flag2 }, { "3", verdict.Flag3 }, { "4", verdict.Flag4 }, { "5", null } } },
        };

        if (a.Text("out") is { } outPath)
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, PyJson.Dumps(result, indent: 1) + "\n", PyJson.Utf8);
        }

        return AsterCli.Emit(o, result);
    }

    public static int Rescore(ParsedArgs a, TextWriter o)
    {
        string orbitPath = Path.GetFullPath(a.Text("orbit")!);
        int budgetMs = a.Int("budget-ms") is { } requestedBudget && requestedBudget is > 0 and <= 60000
            ? (int)requestedBudget
            : throw new ArgException("--budget-ms must be between 1 and 60000");
        string plansDir = Path.GetDirectoryName(orbitPath) ?? string.Empty;
        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } requestedDataDir
            ? PathText.Str(requestedDataDir)
            : Directory.GetParent(plansDir)?.FullName ?? SettingsStore.DefaultDataDir);
        string orbitText = File.ReadAllText(orbitPath);
        var orbit = JsonRead.Parse(orbitText) as JObject ?? throw new PyValueException($"not an orbit file: {orbitPath}");
        if (OrbitBenchMetrics.Field(orbit, "version") is not (OrbitBenchMetrics.LegacyOrbitVersion or OrbitBenchMetrics.OrbitVersion)
            || OrbitBenchMetrics.Field(orbit, "episodes") is not List<object?> sourceEpisodes
            || OrbitBenchMetrics.Fingerprint(orbitText) is not { } fingerprint)
        {
            throw new PyValueException($"not a supported orbit file: {orbitPath}");
        }

        List<OrbitBenchMetrics.BenchEpisode> episodes = OrbitBenchMetrics.ParseEpisodes(orbitText);
        if (episodes.Count != sourceEpisodes.Count)
        {
            throw new PyValueException($"invalid orbit episodes: {orbitPath}");
        }

        string? outDir = a.Text("out-dir") is { } dir ? Path.GetFullPath(dir) : null;
        var results = new List<(string Path, JObject Run, List<ReadOp> Ops)>();
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? gameName = null;
        foreach (string source in a.List("in"))
        {
            string inputPath = Path.GetFullPath(source);
            var run = JsonRead.ReadFile(inputPath) as JObject ?? throw new PyValueException($"not a run file: {inputPath}");
            if (OrbitBenchMetrics.Field(run, "version") is not 1L)
            {
                throw new ArgumentException($"run file version must be 1: {inputPath}");
            }

            if (OrbitBenchMetrics.Field(run, "game") is not string thisGame || thisGame.Length == 0
                || OrbitBenchMetrics.Field(run, "arm") is not ("A" or "B"))
            {
                throw new PyValueException($"game or arm missing in run file: {inputPath}");
            }

            gameName ??= thisGame;
            if (!string.Equals(gameName, thisGame, StringComparison.Ordinal))
            {
                throw new ArgumentException("run files are from different games");
            }

            if (OrbitBenchMetrics.Field(run, "ops") is not List<object?> opRows)
            {
                throw new PyValueException($"ops missing: {inputPath}");
            }

            var done = new List<(ReadOp Op, double DoneMs)>(opRows.Count);
            foreach (object? item in opRows)
            {
                if (item is not JObject op || OrbitBenchMetrics.Field(op, "path") is not string path || path.Length == 0
                    || OrbitBenchMetrics.Field(op, "offset") is not long offset || offset < 0
                    || OrbitBenchMetrics.Field(op, "length") is not long length || length <= 0 || length > long.MaxValue - offset)
                {
                    throw new PyValueException($"invalid op in run file: {inputPath}");
                }

                double doneMs = Num(OrbitBenchMetrics.Field(op, "done_ms"));
                if (!double.IsFinite(doneMs) || doneMs < 0)
                {
                    throw new PyValueException($"invalid op completion time in run file: {inputPath}");
                }

                done.Add((new ReadOp(path, offset, length), doneMs));
            }

            OrbitBenchMetrics.BenchRunMetrics metrics = OrbitBenchMetrics.Evaluate(done, episodes);
            run["inputs"] = fingerprint;
            run["value_mean_ms"] = MsOrNull(metrics.ValueMeanMs);
            run["curve_area_ms"] = MsOrNull(metrics.CurveAreaMs);
            run["episodes"] = metrics.Episodes.Select(e => (object?)new JObject
            {
                { "t_ready_ms", MsOrNull(e.TReadyMs) },
                { "blocks_missing", (long)e.MissingBlocks },
            }).ToList();

            string outputPath = outDir is null ? inputPath : Path.Combine(outDir, Path.GetFileName(inputPath));
            if (!outputs.Add(outputPath))
            {
                throw new ArgumentException($"multiple run files map to the same output path: {outputPath}");
            }

            results.Add((outputPath, run, done.Select(item => item.Op).ToList()));
        }

        int baselineIndex = results.FindIndex(result => OrbitBenchMetrics.Field(result.Run, "arm") is "A");
        List<ReadOp> incumbent = baselineIndex >= 0
            ? results[baselineIndex].Ops
            : results[0].Ops.OrderBy(op => op.Path, StringComparer.OrdinalIgnoreCase).ThenBy(op => op.Offset).ToList();
        int candidateIndex = results.FindIndex(result => OrbitBenchMetrics.Field(result.Run, "arm") is "B");
        List<ReadOp>? candidateOps = candidateIndex >= 0 ? results[candidateIndex].Ops : null;
        long? candidateHeadBytes = null;
        var candidateOrderClock = Stopwatch.StartNew();
        if (candidateOps is not null && OrbitApply.TryInferHeadBytes(incumbent, candidateOps, 256L << 20,
                out long inferredHeadBytes, out _))
        {
            candidateHeadBytes = inferredHeadBytes;
        }
        candidateOrderClock.Stop();
        double lambdaPerMin = results
            .Where(result => OrbitBenchMetrics.Field(result.Run, "arm") is "B")
            .Select(result => OrbitBenchMetrics.Field(result.Run, "lambda_per_min"))
            .Where(value => value is not null)
            .Select(value => Num(value))
            .DefaultIfEmpty(1.0)
            .First();
        if (!double.IsFinite(lambdaPerMin) || lambdaPerMin <= 0)
        {
            throw new PyValueException("invalid lambda_per_min in run file");
        }

        RescoreAssessment cold = Assess(orbitPath, dataDir, gameName!, incumbent, candidateOps, candidateHeadBytes, lambdaPerMin, budgetMs);
        RescoreAssessment warm = Assess(orbitPath, dataDir, gameName!, incumbent, candidateOps, candidateHeadBytes, lambdaPerMin, budgetMs);
        OrbitRuntime.CandidateAssessment assessment = warm.Assessment;
        foreach (var result in results)
        {
            if (OrbitBenchMetrics.Field(result.Run, "arm") is "B")
            {
                result.Run["rescore_decision"] = AssessmentJson(assessment);
            }
        }

        OrbitBenchMetrics.Verdict? measured = null;
        if (IsAbba(results))
        {
            measured = OrbitBenchMetrics.Summarize(results.Select(result => ToRunSummary(result.Run)).ToArray());
        }

        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
        }

        foreach (var result in results)
        {
            File.WriteAllText(result.Path, PyJson.Dumps(result.Run, indent: 1) + "\n", PyJson.Utf8);
        }

        return AsterCli.Emit(o, new JObject
        {
            { "runs", (long)results.Count },
            { "out_dir", outDir },
            { "outputs", results.Select(result => (object?)result.Path).ToList() },
            { "new_rule_would_adopt_b", assessment.WouldAdopt },
            { "new_rule_reason", assessment.Reason },
            { "model_worst_episode_delay_ms", MsOrNull(assessment.WorstEpisodeDelayMs) },
            { "lambda_source", assessment.LambdaSource },
            { "budget_ms", (long)budgetMs },
            { "candidate_head_bytes", candidateHeadBytes },
            { "candidate_order_reconstruction_ms", Ms(candidateOrderClock.Elapsed.TotalMilliseconds) },
            { "cold_decision", AssessmentJson(cold.Assessment) },
            { "warm_decision", AssessmentJson(warm.Assessment) },
            { "decision_timings_ms", new JObject
                {
                    { "cold_total", Ms(cold.ElapsedMs) },
                    { "cold_stages", StageTimingsJson(cold.StageTimings) },
                    { "warm_total", Ms(warm.ElapsedMs) },
                    { "warm_stages", StageTimingsJson(warm.StageTimings) },
                } },
            { "measured_episode_p95_worse_max_ms", measured is null ? null : MsOrNull(measured.EpisodeP95WorseMaxMs) },
            { "measured_flag_4", measured?.Flag4 },
        });
    }

    /// 후보 순서를 받고, A 와 같은 op 다중집합인지 확인합니다 (경로는 대소문자 무시).
    private static RescoreAssessment Assess(string orbitPath, string dataDir, string gameName, List<ReadOp> incumbent,
        List<ReadOp>? candidateOps, long? candidateHeadBytes, double lambdaPerMin, int budgetMs)
    {
        var stageTimings = new Dictionary<string, double>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        OrbitRuntime.CandidateAssessment assessment = RescoreAssessmentProvider(orbitPath, dataDir, gameName, incumbent,
            candidateOps, candidateHeadBytes, lambdaPerMin, budgetMs,
            (stage, elapsedMs) => stageTimings[stage] = stageTimings.GetValueOrDefault(stage) + elapsedMs);
        clock.Stop();
        return new RescoreAssessment(assessment, clock.Elapsed.TotalMilliseconds, stageTimings);
    }

    private static OrbitRuntime.CandidateAssessment DecideForRescore(string orbitPath, string dataDir, string gameName,
        List<ReadOp> incumbent, List<ReadOp>? candidateOps, long? candidateHeadBytes, double lambdaPerMin,
        int budgetMs, Action<string, double> reportStage)
    {
        string plansDir = Path.Combine(dataDir, "plans");
        IReadOnlyList<GameSettings> games;
        try
        {
            games = LoadRescoreGames(dataDir, plansDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException)
        {
            return new OrbitRuntime.CandidateAssessment(false, "game_settings_unavailable", null);
        }

        GameSettings? game = games.FirstOrDefault(g => string.Equals(g.Name, gameName, StringComparison.OrdinalIgnoreCase));
        return game is null
            ? new OrbitRuntime.CandidateAssessment(false, "game_not_registered", null)
            : OrbitRuntime.DecideCandidateOpsMeasured(game, incumbent, lambdaPerMin, budgetMs, reportStage,
                candidateOps, candidateHeadBytes,
                loadModelOverride: (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    OrbitModelSnapshot snapshot = OrbitModel.LoadForRescore(orbitPath);
                    token.ThrowIfCancellationRequested();
                    return snapshot;
                });
    }

    /// 재채점 데이터 사본의 설정 경로를 메모리에서만 사본 plans 폴더로 되돌립니다.
    private static IReadOnlyList<GameSettings> LoadRescoreGames(string dataDir, string plansDir)
    {
        JObject config = JsonRead.ReadFile(Path.Combine(dataDir, "config.json")) as JObject
            ?? throw new PyValueException("rescore config must be an object");
        if (config["games"] is not List<object?> games)
        {
            throw new PyValueException("rescore config games must be a list");
        }

        foreach (object? item in games)
        {
            if (item is not JObject game)
            {
                continue;
            }

            foreach (string field in new[] { "trace", "plan" })
            {
                if (game[field] is string path && Path.GetFileName(path) is { Length: > 0 } name)
                {
                    game[field] = Path.Combine(plansDir, name);
                }
            }
        }

        return SettingsStore.Parse(PyJson.Dumps(config), plansDir).Games;
    }

    private sealed record RescoreAssessment(
        OrbitRuntime.CandidateAssessment Assessment,
        double ElapsedMs,
        IReadOnlyDictionary<string, double> StageTimings);

    private static JObject StageTimingsJson(IReadOnlyDictionary<string, double> timings)
    {
        var result = new JObject();
        foreach ((string stage, double elapsedMs) in timings)
        {
            result.Add(stage, Ms(elapsedMs));
        }

        return result;
    }

    private static JObject AssessmentJson(OrbitRuntime.CandidateAssessment assessment) => new()
    {
        { "would_adopt", assessment.WouldAdopt },
        { "reason", assessment.Reason },
        { "model_worst_episode_delay_ms", MsOrNull(assessment.WorstEpisodeDelayMs) },
        { "lambda_source", assessment.LambdaSource },
        { "lambda_per_min", assessment.LambdaPerMin },
    };

    private static bool IsAbba(IReadOnlyList<(string Path, JObject Run, List<ReadOp> Ops)> results)
    {
        if (results.Count == 0 || results.Count % 4 != 0)
        {
            return false;
        }

        string[] pattern = ["A", "B", "B", "A"];
        for (int start = 0; start < results.Count; start += pattern.Length)
        {
            for (int index = 0; index < pattern.Length; index++)
            {
                if (OrbitBenchMetrics.Field(results[start + index].Run, "arm") is not string arm || arm != pattern[index])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static OrbitBenchMetrics.RunSummary ToRunSummary(JObject run)
    {
        List<object?> episodeRows = OrbitBenchMetrics.Field(run, "episodes") as List<object?>
            ?? throw new PyValueException("episodes missing in rescored run");
        double?[] episodeTimes = episodeRows.Select(row =>
        {
            if (row is JObject episode && OrbitBenchMetrics.Field(episode, "t_ready_ms") is { } value)
            {
                return (double?)Num(value);
            }

            return null;
        }).ToArray();
        return new OrbitBenchMetrics.RunSummary(
            OrbitBenchMetrics.Field(run, "arm") as string ?? throw new PyValueException("arm missing in rescored run"),
            Num(OrbitBenchMetrics.Field(run, "total_ms")),
            OrbitBenchMetrics.Field(run, "value_mean_ms") is { } valueMean ? Num(valueMean) : null,
            episodeTimes);
    }

    private static (List<ReadOp> Ops, OrbitRuntime.ForcedCandidate Forced) CandidateOps(GameSettings game, List<ReadOp> incumbent, double lambdaPerMin)
    {
        OrbitRuntime.ForcedCandidate forced = CandidateProvider?.Invoke(game, incumbent, lambdaPerMin) ?? throw new InvalidOperationException("candidate order is not available");
        List<ReadOp> candidate = forced.Ops ?? throw new InvalidOperationException($"candidate order is not available: {forced.Reason}");
        if (!candidate.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(incumbent.Select(Key).Order(StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("candidate ops differ from incumbent ops");
        }

        return (candidate, forced);
    }

    /// ReadTimed 결과: op 별 완료 시각 (열지 못한 op 는 빠짐), 열지 못한 op 수, EOF 로 짧게 읽힌 op 수.
    internal sealed record ReadTimedResult(List<(ReadOp Op, double DoneMs)> Done, int Errors, int ShortReads);

    /// 예열 순서를 동기 읽기로 읽고, op 마다 시작 이후 완료 시각 (ms) 을 돌려줍니다. 준비 시각은 읽기 반환 시점입니다.
    /// 경로별 핸들은 run 동안 유지합니다. 열 수 없는 파일의 op 는 errors 로 세고 넘어가고, EOF 에 걸린 op 는 short_reads 로 셉니다.
    internal static ReadTimedResult ReadTimed(IReadOnlyList<ReadOp> ops)
    {
        // 제품 Read 경로와 같은 미리읽기 조건
        byte[] buf = new byte[1 << 20];
        var handles = new Dictionary<string, SafeFileHandle?>(StringComparer.OrdinalIgnoreCase);
        var done = new List<(ReadOp Op, double DoneMs)>(ops.Count);
        var clock = Stopwatch.StartNew();
        int errors = 0;
        int shortReads = 0;
        try
        {
            foreach (ReadOp op in ops)
            {
                if (!handles.TryGetValue(op.Path, out SafeFileHandle? h))
                {
                    h = OpenOrNull(op.Path);
                    handles[op.Path] = h;
                }

                if (h is null)
                {
                    errors++;
                    continue;
                }

                long pos = op.Offset;
                long left = op.Length;
                while (left > 0)
                {
                    int n = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(buf.Length, left)), pos);
                    if (n == 0)
                    {
                        shortReads++;
                        break;
                    }

                    pos += n;
                    left -= n;
                }

                done.Add((op, clock.Elapsed.TotalMilliseconds));
            }
        }
        finally
        {
            foreach (SafeFileHandle? h in handles.Values)
            {
                h?.Dispose();
            }
        }

        return new ReadTimedResult(done, errors, shortReads);
    }

    private static SafeFileHandle? OpenOrNull(string path)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // | 는 Windows 경로에 쓸 수 없는 문자라 구분자로 씁니다 (문자열 정렬을 서수로 하려고 튜플 대신 문자열).
    private static string Key(ReadOp op) => $"{op.Path.ToUpperInvariant()}|{op.Offset}|{op.Length}";

    private static double Num(object? v) => v switch
    {
        double d => d,
        long l => l,
        _ => throw new PyValueException("number expected in run file"),
    };

    private static double Ms(double v) => Math.Round(v, 3);

    private static double? MsOrNull(double? v) => v is null ? null : Math.Round(v.Value, 3);

    private static double? Ratio(double? v) => v is null ? null : Math.Round(v.Value, 6);
}
