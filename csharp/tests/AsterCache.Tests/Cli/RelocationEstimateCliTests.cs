using System.Runtime.InteropServices;
using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Cli;

public sealed class RelocationEstimateCliTests : IDisposable
{
    private const string TrainId = "0123456789abcdef0123456789abcdef";
    private const string EvalId = "fedcba9876543210fedcba9876543210";
    private const string SameId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly TempDir _dir = new();
    private readonly Func<IReadOnlyList<(int Pid, string Exe)>> _savedProcesses = RelocationEstimateCommand.RunningProcesses;
    private readonly Func<string> _savedConfigPath = RelocationEstimateCommand.ConfigPath;
    private readonly Func<string, RelocationEstimateCommand.FileLayoutSnapshot> _savedLayoutReader = RelocationEstimateCommand.LayoutReader;
    private readonly Func<string, AsterSettings> _savedSettingsReader = RelocationEstimateCommand.SettingsReader;
    private readonly Func<string, string, bool> _savedSameDisk = RelocationEstimateCommand.SameDisk;
    private readonly Func<string, string, RelocationEstimateCommand.DeviceCalibrationValues?> _savedCalibrationReader = RelocationEstimateCommand.CalibrationReader;
    private readonly (Func<string, string?> Folder, Func<bool> Vanguard, Func<string, string?> Registered) _savedGuard =
        (AntiCheatGuard.FolderKind, AntiCheatGuard.VanguardInstalled, AntiCheatGuard.RegisteredKind);

    public RelocationEstimateCliTests()
    {
        RelocationEstimateCommand.RunningProcesses = () => [];
        RelocationEstimateCommand.ConfigPath = () => _dir.Combine("config.json");
        RelocationEstimateCommand.SettingsReader = path => AntiCheatGuard.Load(path, Path.Combine(Path.GetDirectoryName(path)!, "plans"));
        RelocationEstimateCommand.SameDisk = (_, _) => true;
        RelocationEstimateCommand.CalibrationReader = (_, _) => null;
        AntiCheatGuard.FolderKind = _ => null;
        AntiCheatGuard.VanguardInstalled = () => false;
        AntiCheatGuard.RegisteredKind = _ => null;
    }

    public void Dispose()
    {
        RelocationEstimateCommand.RunningProcesses = _savedProcesses;
        RelocationEstimateCommand.ConfigPath = _savedConfigPath;
        RelocationEstimateCommand.LayoutReader = _savedLayoutReader;
        RelocationEstimateCommand.SettingsReader = _savedSettingsReader;
        RelocationEstimateCommand.SameDisk = _savedSameDisk;
        RelocationEstimateCommand.CalibrationReader = _savedCalibrationReader;
        (AntiCheatGuard.FolderKind, AntiCheatGuard.VanguardInstalled, AntiCheatGuard.RegisteredKind) = _savedGuard;
        _dir.Dispose();
    }

    [Fact]
    public void EstimateSkipsInvalidMarkedAndMissingSummaries()
    {
        string invalid = _dir.Combine("bad.io.json");
        string missing = _dir.Combine("missing.io.json");
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        File.WriteAllText(invalid, "not a summary");
        IoSummaryStore.MarkInvalid(invalid);

        (int code, string output, string error) = CliTests.Run("plan", "estimate", "--io-summary", invalid,
            "--io-summary", missing, "--origin-root", root);

        Assert.Equal(1, code);
        JObject json = (JObject)JsonRead.Parse(output)!;
        Assert.Equal("no_valid_io_summaries", json["error"]);
        List<object?> inputs = (List<object?>)json["inputs"]!;
        Assert.Equal("invalid_marker", ((JObject)inputs[0]!)["status"]);
        Assert.Equal("missing_summary", ((JObject)inputs[1]!)["status"]);
        Assert.Contains("invalid marker exists", error, StringComparison.Ordinal);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Contains("policy exclusions are not reflected", (List<object?>)json["notes"]!);
    }

    [Fact]
    public void EstimateHelpShowsItsRequiredRootAndPlacementOptions()
    {
        (int code, string output, _) = CliTests.Run("plan", "estimate", "--help");

        Assert.Equal(0, code);
        Assert.Contains("--io-summary", output, StringComparison.Ordinal);
        Assert.Contains("--origin-root", output, StringComparison.Ordinal);
        Assert.Contains("--zone-start-lcn", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginRootIsRequiredAndExistingPlanCommandRoutesStillWork()
    {
        (int missingRootCode, _, string missingRootError) = CliTests.Run("plan", "estimate", "--io-summary", "summary.json");
        (int planCode, string planHelp, _) = CliTests.Run("plan", "--help");
        (int profileCode, string profileHelp, _) = CliTests.Run("profile", "export", "--help");

        Assert.Equal(2, missingRootCode);
        Assert.Contains("origin-root", missingRootError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, planCode);
        Assert.Contains("--trace", planHelp, StringComparison.Ordinal);
        Assert.Equal(0, profileCode);
        Assert.Contains("profile export", profileHelp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateSeparatesPlanScopeFromCurrentZoneAndSkipsFilesOutsideRoot()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string fragmented = Path.Combine(root, "fragmented.bin");
        string outside = Path.Combine(_dir.Combine("other"), "outside.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        string summaryPath = _dir.Combine("reads.io.json");
        string planPath = _dir.Combine("plan.json");
        WriteSummary(summaryPath, [new(fragmented), new(outside)], [Row(0, 1), Row(1, 1)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [fragmented] = Layout(65_536, [new Extent(0, 20, 4), new Extent(8, 5, 8)]),
        });
        Plan(root, fragmented, planPath, new JObject { { VolatileFiles.RuleKey, VolatileFiles.RuleVersion } });

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root,
            "--plan", planPath, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject json = (JObject)JsonRead.Parse(output)!;
        Assert.Equal(1L, json["files_outside_origin_root"]);
        Assert.Equal(256L, ((JObject)((List<object?>)json["zone_starts"]!)[0]!)["plan_zone_clusters"]);
        Assert.Contains(((List<object?>)json["notes"]!), note => note is string text && text.Contains("fixed target positions", StringComparison.Ordinal));
        JObject combined = (JObject)json["combined"]!;
        Assert.Equal(1L, ((JObject)combined["total"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)((JObject)combined["plan_coverage"]!)["inside"]!)["blocks"]);
        JObject scenario = (JObject)((List<object?>)((JObject)combined["sequence"]!)["zones"]!)[0]!;
        JObject zoneCoverage = (JObject)scenario["current_zone_coverage"]!;
        Assert.Equal(1L, ((JObject)zoneCoverage["outside"]!)["blocks"]);
        JObject byLatency = (JObject)scenario["by_latency"]!;
        Assert.Equal(1L, byLatency["moved_blocks"]);
        Assert.Equal(20_480L, byLatency["bytes_to_move"]);
        Assert.Equal(49_152L, byLatency["required_contiguous_bytes"]);
        JObject sequence = (JObject)((JObject)combined["sequence"]!)["current"]!;
        Assert.Equal(77_824L, sequence["movement_bytes"]);
    }

    [Fact]
    public void EstimateClassifiesLengthFailuresAndSectionsBeyondEofSeparately()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string unreadable = Path.Combine(root, "unreadable.bin");
        string pastEof = Path.Combine(root, "past-eof.bin");
        string extentsUnavailable = Path.Combine(root, "extents-unavailable.bin");
        string reparsePath = Path.Combine(root, "reparse-path.bin");
        string unsupported = Path.Combine(root, "unsupported.bin");
        string shortFinal = Path.Combine(root, "short-final.bin");
        string coordinateOverflow = Path.Combine(root, "coordinate-overflow.bin");
        string summaryPath = _dir.Combine("failure.io.json");
        WriteSummary(summaryPath, [new(unreadable), new(pastEof), new(extentsUnavailable), new(reparsePath), new(unsupported), new(shortFinal), new(coordinateOverflow)],
            [Row(0, 1), Row(1, 2), Row(2, 3), Row(3, 4), Row(4, 5), Row(5, 6), Row(6, 7)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [unreadable] = new(true, false, 0, null, null, null, null, "file_metadata_unavailable"),
            [pastEof] = Layout(0, []),
            [extentsUnavailable] = new(true, true, 65_536, null, null, 4096, [new DiskExtent(0, 0, 8_000_000)], "extent_query_failed"),
            [reparsePath] = new(true, false, 0, null, null, null, null, "reparse_point_path"),
            [unsupported] = new(true, true, 4096, null, null, 4096, [new DiskExtent(0, 0, 8_000_000)], "unsupported_file_system"),
            [shortFinal] = Layout(4097, [new Extent(0, 10, 2)]),
            [coordinateOverflow] = Layout(4096, [new Extent(0, long.MaxValue, 1)]),
        });

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject combined = (JObject)((JObject)JsonRead.Parse(output)!)["combined"]!;
        JObject classification = (JObject)combined["classification"]!;
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["file_metadata_unavailable"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["extent_query_failed"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["reparse_point_path"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["unsupported_file_system"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["layout_coordinate_overflow"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)classification["missing_section"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)classification["movable"]!)["blocks"]);
        JObject scenario = (JObject)((List<object?>)((JObject)combined["sequence"]!)["zones"]!)[0]!;
        Assert.Equal(8192L, ((JObject)scenario["by_latency"]!)["required_contiguous_bytes"]);

        // 첫 파일이 조회 실패여도 뒤 파일의 볼륨 정보와 구역 시작이 정상으로 나와야 합니다.
        JObject current = (JObject)((JObject)combined["sequence"]!)["current"]!;
        Assert.Equal(1L, current["blocks_with_location"]);
        Assert.Equal(6L, current["blocks_without_location"]);
        JObject zoneStart = (JObject)((List<object?>)((JObject)JsonRead.Parse(output)!)["zone_starts"]!)[0]!;
        Assert.Equal(0L, zoneStart["disk_start_byte"]);
        Assert.Equal(1L, ((JObject)((JObject)scenario["current_zone_coverage"]!)["inside"]!)["blocks"]);
    }

    [Fact]
    public void VolumeInfoComesFromTheFirstValidLookupWhenTheFirstFileIsMissing()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string gone = Path.Combine(root, "gone.bin");
        string present = Path.Combine(root, "present.bin");
        string summaryPath = _dir.Combine("first-missing.io.json");
        WriteSummary(summaryPath, [new(gone), new(present)], [Row(0, 1), Row(1, 2)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [gone] = new(false, false, 0, null, null, null, null, "missing_file"),
            [present] = Layout(4096, [new Extent(0, 11, 1)]),
        });

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root,
            "--zone-start-lcn", "10", "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject json = (JObject)JsonRead.Parse(output)!;
        JObject zoneStart = (JObject)((List<object?>)json["zone_starts"]!)[0]!;
        Assert.Equal(40_960L, zoneStart["disk_start_byte"]);
        Assert.Equal("disk:0", zoneStart["device"]);
        JObject combined = (JObject)json["combined"]!;
        JObject current = (JObject)((JObject)combined["sequence"]!)["current"]!;
        Assert.Equal(1L, current["blocks_with_location"]);
        Assert.Equal(1L, current["blocks_without_location"]);
        JObject scenario = (JObject)((List<object?>)((JObject)combined["sequence"]!)["zones"]!)[0]!;
        Assert.Equal(1L, ((JObject)((JObject)scenario["current_zone_coverage"]!)["inside"]!)["blocks"]);
        JObject byLatency = (JObject)scenario["by_latency"]!;
        Assert.Equal(1L, byLatency["blocks_already_in_zone"]);
        Assert.Equal(0L, byLatency["bytes_to_move"]);
    }

    [Fact]
    public void MissingOriginRootStopsWithArgumentError()
    {
        (int code, _, string error) = CliTests.Run("plan", "estimate", "--io-summary", "summary.json",
            "--origin-root", _dir.Combine("no-such-folder"));

        Assert.Equal(2, code);
        Assert.Contains("origin-root does not exist", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterLargerThanBlockIsLayoutUnavailable()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string big = Path.Combine(root, "big-cluster.bin");
        string summaryPath = _dir.Combine("big-cluster.io.json");
        WriteSummary(summaryPath, [new(big)], [Row(0, 1)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [big] = new(true, true, 65_536, null, [new Extent(0, 5, 1)], 131_072, [new DiskExtent(0, 0, 8_000_000)], null),
        });

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject classification = (JObject)((JObject)((JObject)JsonRead.Parse(output)!)["combined"]!)["classification"]!;
        Assert.Equal(1L, ((JObject)((JObject)classification["layout_unavailable"]!)["cluster_larger_than_block"]!)["blocks"]);
        Assert.Equal(0L, ((JObject)classification["movable"]!)["blocks"]);
    }

    [Fact]
    public void EstimateUsesTheExplicitRootForAntiCheatAndRunningGameGates()
    {
        string root = _dir.Combine("selected-game");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a.bin");
        string summaryPath = _dir.Combine("gate.io.json");
        WriteSummary(summaryPath, [new(file)], [Row(0, 1)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = Layout(4096, [new Extent(0, 5, 1)]),
        });

        string? seenRoot = null;
        AntiCheatGuard.FolderKind = value => { seenRoot = value; return "eac"; };
        (int antiCheatCode, string antiCheat, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root);
        Assert.Equal(1, antiCheatCode);
        Assert.Contains("anti_cheat", antiCheat, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(root), seenRoot);

        AntiCheatGuard.FolderKind = _ => null;
        string config = RelocationEstimateCommand.ConfigPath();
        File.WriteAllText(config, "settings are supplied by the test reader");
        RelocationEstimateCommand.SettingsReader = _ => Settings(root);
        RelocationEstimateCommand.RunningProcesses = () => [(123, "foo.exe")];
        string? checkedRoot = null;
        RelocationEstimateCommand.SameDisk = (target, registered) =>
        {
            checkedRoot = target;
            return string.Equals(target, registered, StringComparison.OrdinalIgnoreCase);
        };

        (int runningCode, string running, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root);

        Assert.Equal(1, runningCode);
        Assert.Contains("game_running", running, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(root), checkedRoot);
    }

    [Fact]
    public void SettingsReadFailureStopsWithIndeterminateError()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string summaryPath = _dir.Combine("settings.io.json");
        WriteSummary(summaryPath, [new(Path.Combine(root, "a.bin"))], [Row(0, 1)]);
        File.WriteAllText(RelocationEstimateCommand.ConfigPath(), "{");

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root);

        Assert.Equal(1, code);
        Assert.Equal("settings_unavailable", ((JObject)JsonRead.Parse(output)!)["error"]);
    }

    [Fact]
    public void PolicyUsesTraceMarksAndFallsBackToPlanExclusions()
    {
        string root = _dir.Combine("game");
        string plans = _dir.Combine("plans");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(plans);
        string marked = Path.Combine(root, "marked.bin");
        string named = Path.Combine(root, "cache_data.bin");
        string summaryPath = _dir.Combine("policy.io.json");
        WriteSummary(summaryPath, [new(marked), new(named)], [Row(0, 1), Row(1, 2)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [marked] = Layout(4096, [new Extent(0, 5, 1)]),
            [named] = Layout(4096, [new Extent(0, 6, 1)]),
        });
        GameSettings game = Game(root, plans);
        File.WriteAllText(AutoTracer.VolatilePath(game), PyJson.Dumps(new JObject { { marked, "write_activity" } }));
        string config = RelocationEstimateCommand.ConfigPath();
        File.WriteAllText(config, "settings are supplied by the test reader");
        RelocationEstimateCommand.SettingsReader = _ => new AsterSettings(SettingsStore.DefaultPollMs, [game]);

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject json = (JObject)JsonRead.Parse(output)!;
        Assert.Equal("trace_marks", json["policy_exclusion_source"]);
        JObject policy = (JObject)((JObject)json["combined"]!)["current_policy"]!;
        Assert.Equal(0L, ((JObject)policy["candidate"]!)["blocks"]);
        JObject reasons = (JObject)policy["excluded_by_reason"]!;
        Assert.Equal(1L, ((JObject)reasons["write_activity"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)reasons[VolatileFiles.NameRule]!)["blocks"]);

        RelocationEstimateCommand.ConfigPath = () => _dir.Combine("absent-config.json");
        Plan(root, marked, _dir.Combine("excluded.plan.json"), new JObject
        {
            { VolatileFiles.RuleKey, VolatileFiles.RuleVersion },
            { VolatileFiles.StatsKey, new JObject { { marked, "plan_exclusion" } } },
        });
        (int fallbackCode, string fallbackOutput, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath,
            "--origin-root", root, "--plan", _dir.Combine("excluded.plan.json"), "--zone-mib", "1");
        Assert.Equal(0, fallbackCode);
        JObject fallback = (JObject)JsonRead.Parse(fallbackOutput)!;
        Assert.Equal("plan", fallback["policy_exclusion_source"]);
        JObject fallbackPolicy = (JObject)((JObject)fallback["combined"]!)["current_policy"]!;
        JObject fallbackReasons = (JObject)fallbackPolicy["excluded_by_reason"]!;
        Assert.Equal(1L, ((JObject)fallbackReasons["plan_exclusion"]!)["blocks"]);
    }

    [Fact]
    public void EvaluatePlayIsReportedSeparatelyAndModelSourcesAreListed()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a.bin");
        string train = _dir.Combine("train.io.json");
        string evaluate = _dir.Combine("eval.io.json");
        WriteSummary(train, [new(file)], [BlockRow(0, 0, 1), BlockRow(0, 1, 2)], TrainId);
        WriteSummary(evaluate, [new(file)], [BlockRow(0, 0, 1), BlockRow(0, 1, 2), BlockRow(0, 2, 3)], EvalId);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = Layout(4 * 65_536, [new Extent(0, 100, 16), new Extent(16, 5000, 16), new Extent(32, 9000, 16), new Extent(48, 13_000, 16)]),
        });

        (int code, string output, string error) = CliTests.Run("plan", "estimate", "--io-summary", train, "--evaluate-io-summary", evaluate,
            "--origin-root", root, "--zone-mib", "1,2", "--position-base-ms", "7", "--position-full-ms", "0");

        Assert.Equal(0, code);
        JObject json = (JObject)JsonRead.Parse(output)!;
        JObject model = (JObject)json["link_model"]!;
        Assert.Equal(7d, model["position_base_ms"]);
        Assert.Equal(0d, model["position_full_ms"]);
        Assert.Equal(150d, model["transfer_mbps"]);
        JObject sources = (JObject)model["sources"]!;
        Assert.Equal("arguments", sources["position_base_ms"]);
        Assert.Equal("arguments", sources["position_full_ms"]);
        Assert.Equal("defaults", sources["transfer_mbps"]);
        List<object?> evaluation = (List<object?>)json["evaluation"]!;
        JObject played = (JObject)Assert.Single(evaluation)!;
        Assert.Equal(EvalId, played["session_id"]);
        Assert.Equal(1L, ((JObject)played["unseen_in_training"]!)["blocks"]);
        Assert.Equal(2, ((List<object?>)played["zones"]!).Count);
        JObject combined = (JObject)json["combined"]!;
        Assert.Equal(2L, ((JObject)combined["total"]!)["blocks"]); // 평가용 플레이는 학습 합계에 들지 않습니다
        JObject scenario = (JObject)((List<object?>)((JObject)combined["sequence"]!)["zones"]!)[0]!;
        Assert.NotNull(((JObject)scenario["by_link_gain"]!)["model"]);
        Assert.Equal("movable_not_policy_excluded", ((JObject)scenario["by_latency"]!)["candidate_set"]);
        Assert.NotNull(((JObject)scenario["by_link_gain"]!)["model_position_reduction_ms"]);
        JObject firstZone = (JObject)((List<object?>)played["zones"]!)[0]!;
        JObject linkChange = (JObject)((JObject)firstZone["by_link_gain"]!)["link_change"]!;
        Assert.NotNull(((JObject)linkChange["removed"]!)["before_model_position_ms"]);
        Assert.NotNull(linkChange["crossing_zone"]);
        Assert.NotNull(linkChange["inside_zone"]);
        Assert.Contains(((List<object?>)json["notes"]!), note => note is string text && text.Contains("evaluation plays are not used for selection", StringComparison.Ordinal));
        Assert.Contains("Zone 1 MiB link_gain", error, StringComparison.Ordinal);
        Assert.Contains("Evaluation eval.io.json", error, StringComparison.Ordinal);
        List<object?> inputs = (List<object?>)json["inputs"]!;
        Assert.Equal(["train", "evaluate"], inputs.Select(input => ((JObject)input!)["role"]).ToArray());
    }

    [Fact]
    public void CalibrationThroughputIsUsedOnlyWhenNoArgumentIsGiven()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a.bin");
        string summary = _dir.Combine("only.io.json");
        WriteSummary(summary, [new(file)], [Row(0, 1)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = Layout(65_536, [new Extent(0, 100, 16)]),
        });
        RelocationEstimateCommand.CalibrationReader = (_, _) => new RelocationEstimateCommand.DeviceCalibrationValues(180, 9);

        (_, string fromCalibration, _) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--zone-mib", "1");
        (_, string fromArgument, _) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--zone-mib", "1",
            "--transfer-mbps", "90");

        JObject calibrated = (JObject)((JObject)JsonRead.Parse(fromCalibration)!)["link_model"]!;
        Assert.Equal(180d, calibrated["transfer_mbps"]);
        Assert.Equal("calibration", ((JObject)calibrated["sources"]!)["transfer_mbps"]);
        Assert.Equal(9d, ((JObject)calibrated["calibration"]!)["seek_hat_ms"]);
        JObject given = (JObject)((JObject)JsonRead.Parse(fromArgument)!)["link_model"]!;
        Assert.Equal(90d, given["transfer_mbps"]);
        Assert.Equal("arguments", ((JObject)given["sources"]!)["transfer_mbps"]);
        Assert.Null(given["calibration"]);
    }

    [Fact]
    public void EvaluatePlayWithTheSameSessionIdAsTrainingIsAnArgumentError()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a.bin");
        string train = _dir.Combine("train.io.json");
        string evaluate = _dir.Combine("eval.io.json");
        WriteSummary(train, [new(file)], [Row(0, 1)], SameId);
        WriteSummary(evaluate, [new(file)], [Row(0, 1)], SameId);

        (int code, _, string error) = CliTests.Run("plan", "estimate", "--io-summary", train, "--evaluate-io-summary", evaluate, "--origin-root", root);

        Assert.Equal(2, code);
        Assert.Contains("evaluate-io-summary", error, StringComparison.Ordinal);
        Assert.Contains(SameId, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelArgumentsAreValidatedAndOutputRepeatsExactly()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "a.bin");
        string summary = _dir.Combine("repeat.io.json");
        WriteSummary(summary, [new(file)], [BlockRow(0, 0, 1), BlockRow(0, 1, 2)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = Layout(2 * 65_536, [new Extent(0, 100, 16), new Extent(16, 9000, 16)]),
        });

        (int negative, _, string negativeError) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--transfer-mbps", "0");
        (int decay, _, string decayError) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--session-decay", "1.5");
        (_, string first, _) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--zone-mib", "1,2");
        (_, string second, _) = CliTests.Run("plan", "estimate", "--io-summary", summary, "--origin-root", root, "--zone-mib", "1,2");

        Assert.Equal(2, negative);
        Assert.Contains("transfer-mbps", negativeError, StringComparison.Ordinal);
        Assert.Equal(2, decay);
        Assert.Contains("session-decay", decayError, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BlockInsideASparseHoleHasNoLocationAndAddsNothingToMovedBytesOrLinks()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string sparse = Path.Combine(root, "sparse.bin");
        string summaryPath = _dir.Combine("hole.io.json");
        // 블록 0 과 2 는 구역(처음 256 클러스터) 밖에 할당돼 있고 블록 1 은 구멍입니다(읽혔어도 할당이 없음).
        WriteSummary(summaryPath, [new(sparse)], [BlockRow(0, 0, 1), BlockRow(0, 1, 2), BlockRow(0, 2, 3)]);
        SetLayouts(new Dictionary<string, RelocationEstimateCommand.FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [sparse] = Layout(3 * 65_536, [new Extent(0, 1000, 16), new Extent(32, 2000, 16)]),
        });

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject combined = (JObject)((JObject)JsonRead.Parse(output)!)["combined"]!;
        JObject classification = (JObject)combined["classification"]!;
        Assert.Equal(2L, ((JObject)classification["movable"]!)["blocks"]);
        Assert.Equal(1L, ((JObject)classification["missing_section"]!)["blocks"]);
        JObject current = (JObject)((JObject)combined["sequence"]!)["current"]!;
        Assert.Equal(2L, current["blocks_with_location"]);
        Assert.Equal(1L, current["blocks_without_location"]);
        // 구멍 블록이 줄을 끊으므로 앞뒤 블록 사이에 이음이 생기지 않습니다.
        Assert.Equal(0L, ((JObject)((JObject)combined["sequence"]!)["current_model"]!)["links"]);
        JObject scenario = (JObject)((List<object?>)((JObject)combined["sequence"]!)["zones"]!)[0]!;
        JObject byLatency = (JObject)scenario["by_latency"]!;
        Assert.Equal(2L, byLatency["moved_blocks"]);
        Assert.Equal(131_072L, byLatency["bytes_to_move"]);
        Assert.Equal(131_072L, byLatency["required_contiguous_bytes"]);
        Assert.Contains(((List<object?>)((JObject)JsonRead.Parse(output)!)["notes"]!), note => note is string text && text.Contains("16-cluster", StringComparison.Ordinal));
    }

    // 스파스 속성만 있는 파일은 옮길 수 있음, 압축이 함께 있으면 옮길 수 없음으로 분류하는지 실제 속성으로 확인합니다(NTFS 임시 폴더).
    [Theory]
    [InlineData(false, 2L, 0L)]
    [InlineData(true, 0L, 2L)]
    public void SparseAttributeAloneIsMovableButSparseWithCompressionIsNot(bool compressed, long movable, long unmovable)
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "sparse.bin");
        const int block = 65_536;
        using (SafeFileHandle h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough))
        {
            if (compressed)
            {
                SetCompression(h);
            }

            Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE
            RandomAccess.Write(h, new byte[block], 0);
            RandomAccess.Write(h, new byte[block], 2L * block);
            RandomAccess.SetLength(h, 3L * block);
            RandomAccess.FlushToDisk(h);
        }

        string summaryPath = _dir.Combine("attr.io.json");
        WriteSummary(summaryPath, [new(path)], [BlockRow(0, 0, 1), BlockRow(0, 2, 3)]);

        (int code, string output, _) = CliTests.Run("plan", "estimate", "--io-summary", summaryPath, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(0, code);
        JObject classification = (JObject)((JObject)((JObject)JsonRead.Parse(output)!)["combined"]!)["classification"]!;
        Assert.Equal(movable, ((JObject)classification["movable"]!)["blocks"]);
        JObject reasons = (JObject)classification["unmovable_by_reason"]!;
        Assert.Equal(unmovable == 0 ? 0 : 1, reasons.Count);
        if (unmovable > 0)
        {
            Assert.Equal(unmovable, ((JObject)reasons["sparse"]!)["blocks"]); // 스파스와 압축이 함께 있으면 기존 이유 그대로입니다
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private static void SetCompression(SafeFileHandle handle)
    {
        nint input = Marshal.AllocHGlobal(sizeof(ushort));
        try
        {
            Marshal.WriteInt16(input, 1); // COMPRESSION_FORMAT_DEFAULT
            Assert.True(DeviceIoControl(handle, 0x9C040, input, sizeof(ushort), 0, 0, out _, 0)); // FSCTL_SET_COMPRESSION
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    private void SetLayouts(IReadOnlyDictionary<string, RelocationEstimateCommand.FileLayoutSnapshot> layouts) =>
        RelocationEstimateCommand.LayoutReader = path => layouts.TryGetValue(path, out RelocationEstimateCommand.FileLayoutSnapshot? layout)
            ? layout
            : throw new InvalidOperationException($"No layout fixture for {path}");

    private static RelocationEstimateCommand.FileLayoutSnapshot Layout(long length, IReadOnlyList<Extent> extents) =>
        new(true, true, length, null, extents, 4096, [new DiskExtent(0, 0, 8_000_000)], null);

    private static IoSummaryRow Row(int file, long first) => new(file, 0, 1, 4096, 10, 10, first, 0);

    private static IoSummaryRow BlockRow(int file, long block, long first) => new(file, block, 1, 4096, 10, 10, first, 0);

    private static void WriteSummary(string path, IReadOnlyList<IoSummaryFile> files, IReadOnlyList<IoSummaryRow> rows, string? sessionId = null)
    {
        IoClassTotals linked = new(rows.Count, rows.Sum(row => row.Bytes), rows.Sum(row => row.LatencySumUs));
        var quality = new IoSummaryQuality(linked, rows.Count == 0 ? 0 : rows.Max(row => row.LatencyMaxUs), linked,
            default, default, default, default, 0, 0, 0);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IoSummaryStore.Write(path, new IoSummary(now, now.AddSeconds(1), [new IoSummaryWindow(0, 1)], quality, files, rows,
            sessionId ?? Guid.NewGuid().ToString("N")));
    }

    private static AsterSettings Settings(string root) =>
        new(SettingsStore.DefaultPollMs, [Game(root, Path.Combine(Path.GetDirectoryName(root)!, "plans"))]);

    private static GameSettings Game(string root, string plans) => new("Foo", ["foo.exe"], Path.Combine(plans, "Foo.trace.json"),
        Path.Combine(plans, "Foo.plan.json"), root, [root], "GAMES", 60);

    private static void Plan(string root, string orderedPath, string path, JObject stats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string volume = Path.GetPathRoot(root)!.TrimEnd('\\', '/');
        var plan = new PlanFile(PlanFile.CurrentVersion, root, volume, 4096, 65_536,
            new PlanZone(10, 256), [], [new PlanRange(orderedPath, 0, 16)], [], stats);
        plan.Save(path);
    }
}
