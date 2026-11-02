using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tray;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class ManageViewModelTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_manage_" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeCli _cli = new();
    private readonly ManageService _service = new();
    private readonly ManageElevator _elevator = new();
    private readonly TrayViewModel _tray;
    private readonly ManageViewModel _vm;
    private IReadOnlyList<string> _untrusted = [];
    private Queue<bool> _running = new();

    public ManageViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _drive = Path.GetPathRoot(_dir)![..2];
        var config = new ConfigEditor(_dir);
        _tray = new TrayViewModel(config, _service, new NoStream(), new NoShell(), new NullLog(), null, "1.0.0.0");
        _vm = new ManageViewModel(_tray, config, _cli, new Drives(_drive), _elevator, new NoShell(), new NullLog(), () => false, _ => _running.Count > 0 && _running.Dequeue(),
            "--data-dir x", TimeSpan.FromMilliseconds(1), _dir, _ => _untrusted, Path.Combine(_dir, "svc", "AsterCache.Service.exe"));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Plans => Path.Combine(_dir, "plans");

    private GameInput Game(string name = "mw4") => new(
        name, ["game.exe"], Path.Combine(Plans, name + ".plan.json.prefetch.json"), Path.Combine(Plans, name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "GAMES", null, ["Steam.exe"]);

    private void Register(string name = "mw4")
    {
        Assert.True(_tray.AddGame(Game(name)).Ok);
        Directory.CreateDirectory(Plans);
    }

    private static JObject Json(string text) => (JObject)JsonRead.Parse(text)!;

    // ---- 순수 계산 ----

    [Fact]
    public void DriveListShowsKindModelAndGameCountPerVolume()
    {
        Register();
        DriveFacts hdd = new("D:", "GAMES", "NTFS", 4000L << 30, 1000L << 30, "WDC WD40", true);
        DriveFacts ssd = new(_drive, "OS", "NTFS", 500L << 30, 100L << 30, "Samsung 980", false);
        DriveFacts unknown = new("Z:", string.Empty, "exFAT", 10L << 30, 1L << 30, string.Empty, null);

        IReadOnlyList<DriveRow> rows = ManageViewModel.BuildDrives([hdd, ssd, unknown], _tray.Settings!.Games);

        Assert.Equal(["HDD", "SSD", "알 수 없음"], rows.Select(r => r.Kind));
        Assert.Equal(_drive == "D:" ? new[] { 1, 1, 0 } : new[] { 0, 1, 0 }, rows.Select(r => r.Games));
        string text = rows[0].ToString();
        Assert.Contains("D: GAMES", text);
        Assert.Contains("WDC WD40", text);
        Assert.Contains("1000.0 GiB 여유 / 3.9 TiB", text);
        Assert.Contains("(이름 없음)", rows[2].ToString());
        Assert.Contains("모델 알 수 없음", rows[2].ToString());
    }

    [Theory]
    [InlineData(0L, 16L)]
    [InlineData(1L, 16L)]
    [InlineData(14L * Mib, 16L)]
    [InlineData(15L * Mib, 32L)] // 16.5 MiB 필요
    [InlineData(100L * Mib, 112L)]
    [InlineData(1600L * Mib, 1760L)] // 정확히 1.1 배가 16 의 배수
    [InlineData((1600L * Mib) + 1, 1776L)]
    public void ZoneSizeHoldsAllHotBytesWithTenPercentRoundedUpTo16Mib(long hot, long expectedMib)
    {
        long zone = ManageViewModel.ZoneMib(hot);
        Assert.Equal(expectedMib, zone);
        Assert.True(zone * Mib * 10 >= hot * 11);
        Assert.Equal(0, zone % 16);
    }

    [Fact]
    public void InferGameUsesExeFolderAsOriginAndPlansFolderForFiles()
    {
        GameInput g = ManageViewModel.InferGame(@"D:\Games\Mech Warrior 4\MW 4.exe", "GAMES", @"C:\ProgramData\AsterCache\plans");
        Assert.Equal("MW_4", g.Name);
        Assert.Equal(["MW 4.exe"], g.ExeNames);
        Assert.Equal(@"D:\Games\Mech Warrior 4", g.OriginRoot);
        Assert.Equal([@"D:\Games\Mech Warrior 4"], g.AllowedRoots);
        Assert.Equal("GAMES", g.VolumeLabel);
        Assert.Equal(@"C:\ProgramData\AsterCache\plans\MW_4.plan.json", g.Plan);
        Assert.Equal(g.Plan + ".prefetch.json", g.Trace); // aster plan 이 계획 옆에 쓰는 이름
    }

    [Fact]
    public void AsterArgsFollowTheCliOptions()
    {
        Assert.Equal(["trace", "--live", "--exe", "a.exe", "b.exe", "--origin-root", @"D:\G", "--out", @"C:\p\x.trace.json", "--stop-file", @"C:\t\s", "--seconds", "3600"],
            AsterArgs.Trace(["a.exe", "b.exe"], @"D:\G", @"C:\p\x.trace.json", @"C:\t\s"));
        Assert.Equal(["plan", "--trace", "t", "--origin-root", "r", "--zone-mib", "112", "--out", "p"], AsterArgs.Plan("t", "r", 112, "p"));
        Assert.Equal(["plan", "--trace", "t", "--origin-root", "r", "--zone-mib", "16384", "--dry-run"], AsterArgs.Plan("t", "r", 16384, null));
        Assert.Equal(["apply", "--plan", "p", "--journal", "j", "--allowed-root", "r1", "--allowed-root", "r2", "--require-volume-label", "L", "--dry-run"],
            AsterArgs.Apply("p", "j", ["r1", "r2"], "L", execute: false));
        Assert.Equal("--execute", AsterArgs.Apply("p", "j", ["r1"], "L", execute: true)[^1]);
        Assert.Equal(["revert", "--journal", "j", "--allowed-root", "r1", "--require-volume-label", "L", "--execute"], AsterArgs.Revert("j", ["r1"], "L"));
        Assert.Equal(["verify", "--plan", "p"], AsterArgs.Verify("p"));
        Assert.Equal(["status", "--prewarm-status", "w", "--plan", "p", "--journal", "j"], AsterArgs.Status("w", "p", "j"));
        Assert.Equal(["status"], AsterArgs.Status(null, null, null));
        Assert.Equal(["prefetch", "--trace", "t", "--origin-root", "r", "--budget-mib", "112", "--execute", "--timing"], AsterArgs.Timing("t", "r", 112));
    }

    [Fact]
    public void CliResultReadsTheLastJsonLine()
    {
        Assert.True(CliResult.Parse("live {'reads': 1}\n{\"union\": \"x\", \"union_events\": 3}\n").Ok);
        Assert.False(CliResult.Parse("{\"error\":\"not_admin\"}", 3).Ok);
        Assert.False(CliResult.Parse("{\"error\":\"not_admin\"}").Ok);
        Assert.False(CliResult.Parse("aster trace: error: bad\n").Ok);
        Assert.False(CliResult.Parse("{\"ok\":1}", 1).Ok);
        Assert.False(CliResult.Parse(string.Empty).Ok);
    }

    [Fact]
    public void ArgumentsAreQuotedForTheCommandLine()
    {
        Assert.Equal("plain", AsterProcess.Quote("plain"));
        Assert.Equal("\"D:\\My Games\\x\"", AsterProcess.Quote(@"D:\My Games\x"));
        Assert.Equal("\"D:\\My Games\\\\\"", AsterProcess.Quote(@"D:\My Games\")); // 끝 역슬래시는 두 배
        Assert.Equal("\"\"", AsterProcess.Quote(string.Empty));
    }

    [Fact]
    public void TailKeepsTheLast200LinesAndHandlesMissingFile()
    {
        string log = Path.Combine(_dir, "t.log");
        File.WriteAllLines(log, Enumerable.Range(1, 300).Select(i => "line " + i));
        string[] lines = ManageViewModel.Tail(log).Split(Environment.NewLine);
        Assert.Equal(200, lines.Length);
        Assert.Equal("line 101", lines[0]);
        Assert.Equal("line 300", lines[^1]);
        Assert.Contains("파일 없음", ManageViewModel.Tail(Path.Combine(_dir, "none.log")));
    }

    [Fact]
    public void DiskDescriptorParsing()
    {
        byte[] d = new byte[64];
        BitConverter.GetBytes(40u).CopyTo(d, 12);
        BitConverter.GetBytes(48u).CopyTo(d, 16);
        System.Text.Encoding.ASCII.GetBytes("ATA \0").CopyTo(d, 40);
        System.Text.Encoding.ASCII.GetBytes("Samsung SSD 860\0").CopyTo(d, 48);
        Assert.Equal("ATA Samsung SSD 860", DiskInfo.ParseModel(d));
        Assert.Equal(string.Empty, DiskInfo.ParseModel(new byte[8]));
        Assert.True(DiskInfo.ParseSeekPenalty([0, 0, 0, 0, 12, 0, 0, 0, 1, 0, 0, 0]));
        Assert.False(DiskInfo.ParseSeekPenalty([0, 0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(DiskInfo.ParseSeekPenalty([1, 2]));
    }

    [Fact]
    public void SystemDriveQueryDoesNotThrow()
    {
        Assert.NotNull(DiskInfo.Query(_drive));
        Assert.Empty(DiskInfo.Query("9:").Model);
        Assert.NotEmpty(new SystemDrives().Fixed());
    }

    // ---- 목록과 상태 ----

    [Fact]
    public void GameRowsAndStatusTextFromAsterJson()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, "journals"));
        File.WriteAllText(_vm.JournalPath("mw4"), "{}\n");
        _cli.On("status", (args, _) => args.Contains("--plan")
            ? CliResult.Parse("{\"plan\":{\"hot_clusters\":200,\"in_zone_clusters\":150,\"verdict\":\"contiguous\"},\"journal\":{\"rows\":4}}")
            : CliResult.Parse("{\"admin\":false,\"prewarm\":{\"games\":{\"mw4\":{\"at\":\"2026-11-02T01:02:03+00:00\",\"result\":\"ok\",\"reason\":null,\"bytes\":1073741824,\"seconds\":5.5,\"mb_per_s\":186.2},"
                + "\"old\":{\"at\":\"2025-01-01T00:00:00+00:00\",\"result\":\"skipped\",\"reason\":\"low memory\",\"bytes\":null,\"seconds\":null}}}}"));

        Assert.Equal("?", _vm.GameRows(null).Single().Verdict);
        Assert.True(_vm.RefreshStatus().Ok);

        GameRow row = _vm.GameRows(null).Single();
        Assert.Equal(("mw4", "game.exe", true, false, "contiguous"), (row.Name, row.Exe, row.HasPlan, row.HasTrace, row.Verdict));
        Assert.Contains("트리거 Steam.exe", row.Prewarm);
        Assert.Contains("여유 RAM 4096 MiB", row.Prewarm);
        Assert.Empty(_vm.GameRows("Q:"));
        Assert.Single(_vm.GameRows(_drive));

        string[] lines = _vm.StatusLines(null).ToArray();
        Assert.Contains("서비스 정지됨", lines[0]);
        Assert.Equal("감시기: poll", lines[1]);
        Assert.Contains("마지막 예열: mw4", lines[2]);
        Assert.Contains("1024 MiB", lines[2]);
        Assert.Contains("5.5 초", lines[2]);
        Assert.Contains("배치 contiguous · 존 적중 75.0% · 마지막 재배치 20", lines[3]);
        Assert.Contains("예열 ok", lines[3]);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(AsterArgs.Status(null, Path.Combine(Plans, "mw4.plan.json"), _vm.JournalPath("mw4"))));
    }

    [Fact]
    public void GameWithoutPlanShowsDash()
    {
        Register();
        GameRow row = _vm.GameRows(null).Single();
        Assert.False(row.HasPlan);
        Assert.Equal("-", row.Verdict);
        Assert.Contains("계획 없음", _vm.StatusLines(null)[^1]);
        Assert.Contains("마지막 예열: 기록 없음", _vm.StatusLines(null)[2]);
    }

    // ---- 동작 ----

    [Fact]
    public void TraceCollectionStopsAfterTheGameExitsAndReportsEventsAndBytes()
    {
        Register();
        string raw = _vm.RawTracePath("mw4");
        string? stopFile = null;
        _cli.On("trace", (args, elevate) =>
        {
            Assert.True(elevate);
            stopFile = args[args.ToList().IndexOf("--stop-file") + 1];
            Assert.Equal(raw, args[args.ToList().IndexOf("--out") + 1]);
            while (!File.Exists(stopFile)) // 실제 aster 처럼 stop-file 이 생기면 끝남
            {
                Thread.Sleep(1);
            }

            File.WriteAllText(raw, "[{\"path\": \"x\", \"offset\": 0, \"length\": 65536, \"cost\": 1}]");
            return CliResult.Parse("live {'reads': 2}\n{\"union\": \"x\", \"union_events\": 1, \"per_run\": []}");
        });
        _running = new Queue<bool>([false, false, true, true, false]);
        var messages = new List<string>();

        ActionResult r = _vm.CollectTrace("mw4", messages.Add, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Contains("트레이스 1건", r.Message);
        Assert.Contains("MiB", r.Message);
        Assert.Contains("게임을 실행해 첫 맵까지 로딩한 뒤 종료하십시오", messages[0]);
        Assert.False(File.Exists(stopFile!));
    }

    [Fact]
    public void TraceCollectionCanBeStoppedByTheUser()
    {
        Register();
        _cli.On("trace", (args, _) =>
        {
            while (!File.Exists(args[args.ToList().IndexOf("--stop-file") + 1]))
            {
                Thread.Sleep(1);
            }

            return CliResult.Parse("{\"union_events\": 0}");
        });
        _running = new Queue<bool>([false]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ActionResult r = _vm.CollectTrace("mw4", _ => { }, cts.Token);
        Assert.False(r.Ok);
        Assert.Contains("0건", r.Message);
    }

    [Fact]
    public void TraceFailureIsReported()
    {
        Register();
        _cli.On("trace", (_, _) => CliResult.Parse("{\"error\":\"not_admin\"}", 3));
        ActionResult r = _vm.CollectTrace("mw4", _ => { }, new CancellationToken(true));
        Assert.False(r.Ok);
        Assert.Contains("not_admin", r.Message);
        Assert.False(_vm.CollectTrace("nope", _ => { }, CancellationToken.None).Ok);
    }

    [Fact]
    public void PlanSizesTheZoneFromTheHotBytesThenWritesElevated()
    {
        Register();
        File.WriteAllText(_vm.RawTracePath("mw4"), "[]");
        _cli.On("plan", (args, elevate) => args.Contains("--dry-run")
            ? CliResult.Parse("{\"stats\":{\"hot_bytes\":104857600,\"moved_bytes\":0}}")
            : CliResult.Parse("{\"stats\":{\"hot_bytes\":104857600,\"moved_bytes\":52428800,\"tasks\":7}}"));

        ActionResult r = _vm.MakePlan("mw4", _ => { });

        Assert.True(r.Ok);
        Assert.Contains("핫 100 MiB · 존 112 MiB · 이동 50 MiB · 작업 7개", r.Message);
        Assert.DoesNotContain("주의", r.Message);
        Assert.Equal(2, _cli.Calls.Count);
        Assert.False(_cli.Calls[0].Elevate);
        Assert.Equal("16384", _cli.Calls[0].Args[_cli.Calls[0].Args.ToList().IndexOf("--zone-mib") + 1]);
        Assert.True(_cli.Calls[1].Elevate);
        Assert.Equal(AsterArgs.Plan(_vm.RawTracePath("mw4"), Path.Combine(_dir, "Games", "mw4"), 112, Path.Combine(Plans, "mw4.plan.json")), _cli.Calls[1].Args);
    }

    [Fact]
    public void PlanNeedsATraceAndWarnsWhenConfigTracePathDiffers()
    {
        Assert.True(_tray.AddGame(Game() with { Trace = Path.Combine(Plans, "other.prefetch.json") }).Ok);
        Assert.Contains("트레이스가 없음", _vm.MakePlan("mw4", _ => { }).Message);
        Directory.CreateDirectory(Plans);
        File.WriteAllText(_vm.RawTracePath("mw4"), "[]");
        _cli.On("plan", (_, _) => CliResult.Parse("{\"stats\":{\"hot_bytes\":1048576,\"moved_bytes\":0,\"tasks\":0}}"));
        Assert.Contains("주의", _vm.MakePlan("mw4", _ => { }).Message);
    }

    [Fact]
    public void ApplyDryRunsThenExecutesElevatedWithVolumeLabelAndVerifies()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("apply", (args, _) => args.Contains("--dry-run")
            ? CliResult.Parse("{\"tasks_todo\":3,\"bytes_todo\":3145728,\"dry_run\":true}")
            : CliResult.Parse("{\"executed\":true,\"moved_bytes\":3145728,\"failed\":[],\"stopped\":null}"));
        _cli.On("verify", (_, _) => CliResult.Parse("{\"verdict\":\"contiguous\"}"));

        ActionResult r = _vm.Apply("mw4", _ => { });

        Assert.True(r.Ok);
        Assert.Contains("이동 3 MiB", r.Message);
        Assert.Contains("배치 contiguous", r.Message);
        Assert.Equal(["apply", "apply", "verify"], _cli.Calls.Select(c => c.Args[0]));
        Assert.Equal([true, true, false], _cli.Calls.Select(c => c.Elevate));
        string plan = Path.Combine(Plans, "mw4.plan.json");
        string root = Path.Combine(_dir, "Games");
        Assert.Equal(AsterArgs.Apply(plan, _vm.JournalPath("mw4"), [root], "GAMES", execute: true), _cli.Calls[1].Args);
        Assert.Contains("--require-volume-label", _cli.Calls[1].Args);
    }

    [Fact]
    public void ApplyWithNothingToMoveSkipsExecuteAndFailureStops()
    {
        Register();
        Assert.Contains("계획이 없음", _vm.Apply("mw4", _ => { }).Message);
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("apply", (_, _) => CliResult.Parse("{\"tasks_todo\":0}"));
        Assert.Contains("옮길 작업이 없음", _vm.Apply("mw4", _ => { }).Message);
        Assert.Single(_cli.Calls);

        _cli.On("apply", (_, _) => CliResult.Parse("{\"error\":\"PlanRejected\",\"detail\":\"volume label\"}", 1));
        ActionResult bad = _vm.Apply("mw4", _ => { });
        Assert.False(bad.Ok);
        Assert.Contains("PlanRejected: volume label", bad.Message);
    }

    [Fact]
    public void RevertUsesTheJournalAndNeedsIt()
    {
        Register();
        Assert.Contains("저널이 없음", _vm.Revert("mw4", _ => { }).Message);
        Directory.CreateDirectory(Path.Combine(_dir, "journals"));
        File.WriteAllText(_vm.JournalPath("mw4"), "{}\n");
        _cli.On("revert", (_, _) => CliResult.Parse("{\"executed\":true,\"restored\":3,\"moved_bytes\":3145728,\"skipped\":[]}"));
        ActionResult r = _vm.Revert("mw4", _ => { });
        Assert.True(r.Ok);
        Assert.Contains("복원 3건", r.Message);
        Assert.Equal(AsterArgs.Revert(_vm.JournalPath("mw4"), [Path.Combine(_dir, "Games")], "GAMES"), _cli.Calls.Last().Args);
        Assert.True(_cli.Calls.Last().Elevate);
    }

    [Fact]
    public void MovedBytesComeFromThePlanStats()
    {
        Register();
        Assert.Null(_vm.MovedBytes("mw4"));
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{\"stats\":{\"moved_bytes\":5242880}}");
        Assert.Equal(5L << 20, _vm.MovedBytes("mw4"));
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{\"nope\":1}");
        Assert.Null(_vm.MovedBytes("mw4"));
    }

    [Fact]
    public void MeasureShowsStreamTime()
    {
        Register();
        _cli.On("prefetch", (_, elevate) =>
        {
            Assert.False(elevate);
            return CliResult.Parse("{\"bytes\":104857600,\"seconds\":2.0,\"timing_ms\":{\"load_ms\":5.0,\"schedule_ms\":7.5,\"stream_ms\":2000.0}}");
        });
        ActionResult r = _vm.Measure("mw4");
        Assert.True(r.Ok);
        Assert.Contains("선읽기 100 MiB, 2.00 초 (50 MB/s)", r.Message);
        Assert.Contains("스트림 2000", r.Message);
        Assert.Equal("4096", _cli.Calls.Single().Args[^3]); // 계획이 없으면 max_stream_mib
    }

    [Fact]
    public void ApplyAndRevertRefuseWhileTheGameRuns()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, "journals"));
        File.WriteAllText(_vm.JournalPath("mw4"), "{}\n");
        _running = new Queue<bool>([true, true]);
        Assert.Contains("실행 중", _vm.Apply("mw4", _ => { }).Message);
        Assert.Contains("실행 중", _vm.Revert("mw4", _ => { }).Message);
        Assert.Empty(_cli.Calls);
    }

    [Fact]
    public void RevertWithSkippedStepsOrStopIsAFailureThatAsksToRunAgain()
    {
        Register();
        Directory.CreateDirectory(Path.Combine(_dir, "journals"));
        File.WriteAllText(_vm.JournalPath("mw4"), "{}\n");
        _cli.On("revert", (_, _) => CliResult.Parse("{\"executed\":true,\"restored\":1,\"moved_bytes\":1048576,\"skipped\":[{\"idx\":2,\"reason\":\"changed\"}]}"));
        ActionResult skipped = _vm.Revert("mw4", _ => { });
        Assert.False(skipped.Ok);
        Assert.Contains("다시 실행", skipped.Message);
        _cli.On("revert", (_, _) => CliResult.Parse("{\"executed\":true,\"restored\":1,\"moved_bytes\":1,\"skipped\":[],\"stopped\":\"write_cap\"}"));
        ActionResult capped = _vm.Revert("mw4", _ => { });
        Assert.False(capped.Ok);
        Assert.Contains("중단: write_cap", capped.Message);
    }

    [Fact]
    public void StderrIsShownButNotParsedAsJson()
    {
        CliResult r = CliResult.Parse("live {'reads': 2}\n{\"union\": \"x\", \"union_events\": 1}\n", 0, "tracing; press Ctrl+C to stop\n");
        Assert.True(r.Ok);
        Assert.Equal(1L, r.Json!["union_events"]);
        Assert.Contains("tracing; press Ctrl+C", r.Output);
        Assert.False(CliResult.Parse("{\"error\":\"x\"}", 1, "tracing\n").Ok);
    }

    [Fact]
    public void RealAsterRunsDirectlyWhenAdminAndThroughResultFileWhenElevated()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "aster.exe");
        string status = Path.Combine(_dir, "no&such", "p.json"); // & 가 든 무공백 경로
        var direct = new AsterProcess(_elevator, () => true, new NullLog(), exe);
        CliResult d = direct.Run(["status", "--prewarm-status", status], elevate: true);
        Assert.True(d.Ok, d.Output);
        Assert.Empty(_elevator.ListRuns);

        // 승격 경로: 가짜 승격기가 같은 인자로 CLI 를 프로세스 안에서 돌림 (--result-file 이 결과를 파일로 받음)
        _elevator.OnRun = args => AsterCache.Cli.AsterCli.Run(args, new StringWriter(), new StringWriter());
        string tempRoot = Path.Combine(_dir, "tmp");
        Directory.CreateDirectory(tempRoot);
        var elevated = new AsterProcess(_elevator, () => false, new NullLog(), exe, tempRoot);
        CliResult e = elevated.Run(["status", "--prewarm-status", status], elevate: true);

        Assert.True(e.Ok, e.Output);
        (string runExe, IReadOnlyList<string> args) = _elevator.ListRuns.Single();
        Assert.Equal(exe, runExe);
        Assert.Equal("--result-file", args[0]);
        Assert.StartsWith(tempRoot, args[1]);
        Assert.Equal(["status", "--prewarm-status", status], args.Skip(2));
        Assert.Empty(Directory.GetDirectories(tempRoot)); // 실행마다 만든 GUID 폴더는 지움
    }

    [Fact]
    public void ResultFileOptionCopiesStdout()
    {
        string file = Path.Combine(_dir, "r.txt");
        var o = new StringWriter();
        int code = AsterCache.Cli.AsterCli.Run(["--result-file", file, "status", "--prewarm-status", Path.Combine(_dir, "none.json")], o, new StringWriter());
        Assert.Equal(0, code);
        Assert.Equal(o.ToString(), File.ReadAllText(file));
        Assert.Contains("\"admin\"", File.ReadAllText(file));
    }

    // ---- 설정·서비스 ----

    [Fact]
    public void WatcherIsSavedAndValidated()
    {
        Register();
        Assert.True(_tray.SetWatcher("etw").Ok);
        Assert.Equal("etw", _tray.Settings!.Watcher);
        Assert.False(_tray.SetWatcher("fast").Ok);
        Assert.Equal("etw", _tray.Settings!.Watcher);
    }

    [Fact]
    public void ServiceInstallRefusesUnsafeFolderAndMissingConfigThenRequestsElevatedInstall()
    {
        string exe = Path.Combine(_dir, "svc", "AsterCache.Service.exe");
        Assert.Contains("찾지 못함", _vm.InstallService().Message);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty);
        Assert.Contains("config.json", _vm.InstallService().Message);

        Register();
        _untrusted = ["S-1-5-32-545"];
        ActionResult unsafeFolder = _vm.InstallService();
        Assert.False(unsafeFolder.Ok);
        Assert.Contains("일반 사용자가 바꿀 수 있어", unsafeFolder.Message);
        Assert.Empty(_service.Installs);

        _untrusted = [];
        Assert.True(_vm.InstallService().Ok);
        Assert.Equal([(exe, _dir)], _service.Installs);
        Assert.True(_tray.UninstallService().Ok);
        Assert.Equal(1, _service.Uninstalls);
    }

    [Fact]
    public void ScmInstallRunsInstallerStepsInOrderThroughCmd()
    {
        var elevator = new ManageElevator();
        var scm = new ScmServiceControl(elevator, "AsterCacheTest");
        scm.RequestInstall(@"C:\Program Files\AsterCache\AsterCache.Service.exe", @"C:\ProgramData\AsterCache");
        scm.RequestUninstall();

        Assert.All(elevator.Runs, c => Assert.EndsWith("cmd.exe", c.Exe, StringComparison.OrdinalIgnoreCase));
        string install = elevator.Runs[0].Args;
        string[] steps = ["mkdir", "/setowner *S-1-5-32-544", "/reset", "/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX", "sc.exe\" create AsterCacheTest",
            "start= auto obj= LocalSystem", "sc.exe\" start AsterCacheTest"];
        int at = -1;
        foreach (string step in steps)
        {
            int next = install.IndexOf(step, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, step);
            at = next;
        }

        Assert.Contains("binPath= \"\\\"C:\\Program Files\\AsterCache\\AsterCache.Service.exe\\\" --data-dir \\\"C:\\ProgramData\\AsterCache\\\"\"", install);
        Assert.Contains("stop AsterCacheTest", elevator.Runs[1].Args);
        Assert.Contains("delete AsterCacheTest", elevator.Runs[1].Args);
    }

    [Fact]
    public void RelaunchAsAdministratorStartsWithTheSameArguments()
    {
        Assert.True(_vm.RelaunchAsAdmin().Ok);
        Assert.Equal("--data-dir x", _elevator.Starts.Single().Args);
        _elevator.Cancel = true;
        Assert.Contains("취소", _vm.RelaunchAsAdmin().Message);
    }

    // ---- 가짜 ----

    private sealed class FakeCli : IAsterCli
    {
        private readonly Dictionary<string, Func<IReadOnlyList<string>, bool, CliResult>> _handlers = [];

        public List<(IReadOnlyList<string> Args, bool Elevate)> Calls { get; } = [];

        public void On(string command, Func<IReadOnlyList<string>, bool, CliResult> handler) => _handlers[command] = handler;

        public CliResult Run(IReadOnlyList<string> args, bool elevate = false)
        {
            lock (Calls)
            {
                Calls.Add((args, elevate));
            }

            return _handlers.TryGetValue(args[0], out Func<IReadOnlyList<string>, bool, CliResult>? h) ? h(args, elevate) : CliResult.Fail("no handler " + args[0]);
        }
    }

    private sealed class Drives(string letter) : IDriveSource
    {
        public IReadOnlyList<DriveFacts> Fixed() => [new(letter, "OS", "NTFS", 1L << 40, 1L << 39, "Test Disk", false)];
    }

    private sealed class ManageElevator : IElevator
    {
        public List<(string Exe, string Args)> Runs { get; } = [];

        public List<(string Exe, string Args)> Starts { get; } = [];

        public List<(string Exe, IReadOnlyList<string> Args)> ListRuns { get; } = [];

        public Action<IReadOnlyList<string>>? OnRun { get; set; }

        public bool Cancel { get; set; }

        public void Run(string exe, string arguments) => Runs.Add((exe, arguments));

        public void Run(string exe, IReadOnlyList<string> arguments)
        {
            ListRuns.Add((exe, arguments));
            OnRun?.Invoke(arguments);
        }

        public void Start(string exe, string arguments)
        {
            if (Cancel)
            {
                throw new System.ComponentModel.Win32Exception(1223);
            }

            Starts.Add((exe, arguments));
        }
    }

    private sealed class ManageService : IServiceControl
    {
        public List<(string Exe, string DataDir)> Installs { get; } = [];

        public int Uninstalls { get; private set; }

        public ServiceState Query() => ServiceState.Stopped;

        public string? InstalledVersion() => null;

        public void RequestStart()
        {
        }

        public void RequestStop()
        {
        }

        public void RequestInstall(string serviceExe, string dataDir) => Installs.Add((serviceExe, dataDir));

        public void RequestUninstall() => Uninstalls++;
    }

    private sealed class NoStream : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(0, 0, 0, 0);
    }

    private sealed class NoShell : IShell
    {
        public void Open(string path)
        {
        }
    }

    private sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
