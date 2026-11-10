using AsterCache.Core.I18n;
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
        Assert.Equal(["plan", "--trace", "t", "--origin-root", "r", "--zone-mib", "112", "--out", "p", "--journal", "j"], AsterArgs.Plan("t", "r", 112, "p", "j"));
        Assert.DoesNotContain("--journal", AsterArgs.Plan("t", "r", 112, null, "j")); // dry-run 은 저널을 보지 않음
        Assert.Equal(["plan", "--from-plan", "p", "--out", "p", "--journal", "j"], AsterArgs.Replan("p", "j"));
        Assert.Equal(["apply", "--plan", "p", "--journal", "j", "--allowed-root", "r1", "--allowed-root", "r2", "--require-volume-label", "L", "--dry-run"],
            AsterArgs.Apply("p", "j", ["r1", "r2"], "L", execute: false));
        Assert.Equal("--execute", AsterArgs.Apply("p", "j", ["r1"], "L", execute: true)[^1]);
        Assert.Equal(["revert", "--journal", "j", "--allowed-root", "r1", "--require-volume-label", "L", "--execute"], AsterArgs.Revert("j", ["r1"], "L"));
        Assert.Equal(["verify", "--plan", "p"], AsterArgs.Verify("p"));
        Assert.Equal(["status", "--prewarm-status", "w", "--auto-status", "a", "--plan", "p", "--journal", "j"], AsterArgs.Status("w", "a", "p", "j"));
        Assert.Equal(["status", "--prewarm-status", "w", "--auto-status", "a"], AsterArgs.Status("w", "a", null, null));
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
            : CliResult.Parse("{\"admin\":false,\"prewarm\":{\"games\":{\"mw4\":{\"at\":\"2026-11-02T01:02:03+00:00\",\"result\":\"ok\",\"reason\":null,\"bytes\":1073741824,\"seconds\":5.5,\"mb_per_s\":186.2,\"head_mib\":5},"
                + "\"old\":{\"at\":\"2025-01-01T00:00:00+00:00\",\"result\":\"skipped\",\"reason\":\"low memory\",\"bytes\":null,\"seconds\":null}}}}"));

        Assert.Equal("?", _vm.GameRows(null).Single().Verdict);
        Assert.True(_vm.RefreshStatus().Ok);

        GameRow row = _vm.GameRows(null).Single();
        Assert.Equal(("mw4", "game.exe", true, false, "연속", "contiguous"), (row.Name, row.Exe, row.HasPlan, row.HasTrace, row.Verdict, row.VerdictRaw));
        Assert.Equal("시작 예 · Steam.exe", row.Prewarm);
        Assert.Contains("여유 RAM 4096 MiB", row.PrewarmFull);
        Assert.Empty(_vm.GameRows("Q:"));
        Assert.Single(_vm.GameRows(_drive));

        string[] lines = _vm.StatusLines(null).ToArray();
        Assert.Contains("서비스 정지됨", lines[0]);
        Assert.Equal("감시기: poll", lines[1]);
        Assert.Contains("마지막 예열: mw4", lines[2]);
        Assert.Contains("1024 MiB", lines[2]);
        Assert.Contains("5.5 초", lines[2]);
        Assert.Contains("배치 연속 · 존 적중 75.0% · 마지막 재배치 20", lines[3]);
        Assert.Contains("예열 성공 · 로딩 앞부분 5 MiB", lines[3]);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(AsterArgs.Status(_vm.PrewarmStatusPath, _vm.AutoStatusPath, Path.Combine(Plans, "mw4.plan.json"), _vm.JournalPath("mw4"))));
    }

    [Fact]
    public void PartialPrewarmIsShownAsPartialAndNotAsPlainOk()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("status", (args, _) => args.Contains("--plan")
            ? CliResult.Parse("{\"plan\":{\"hot_clusters\":200,\"in_zone_clusters\":150,\"verdict\":\"contiguous\"}}")
            : CliResult.Parse("{\"admin\":false,\"prewarm\":{\"games\":{\"mw4\":{\"at\":\"2026-11-02T01:02:03+00:00\",\"result\":\"ok\",\"reason\":null,\"bytes\":1073741824,\"seconds\":5.5,\"warmed_mib\":1904,\"hot_mib\":8000,\"partial\":true}}}}"));

        Assert.True(_vm.RefreshStatus().Ok);

        Assert.Contains("예열 성공 · 부분 1904/8000 MiB", _vm.StatusLines(null).ToArray()[3]);
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

    [Fact]
    public void RefreshStatusPassesTheDataDirStatusFilesSoAsterNeverReadsProgramData()
    {
        Register();
        _cli.On("status", (_, _) => CliResult.Parse("{}"));
        Assert.True(_vm.RefreshStatus().Ok);
        Assert.Equal(AsterArgs.Status(Path.Combine(_dir, "prewarm_status.json"), Path.Combine(_dir, "auto_status.json"), null, null), _cli.Calls.Single().Args);
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
    public void TraceOfAnAntiCheatGameShowsTheCliRefusalOnScreen()
    {
        Register();
        _cli.On("trace", (_, _) => CliResult.Parse("{\"error\":\"anti_cheat\",\"detail\":\"anti-cheat protected game; tracing disabled (vanguard)\"}", 1));

        ActionResult r = _vm.CollectTrace("mw4", _ => { }, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("anti_cheat: anti-cheat protected game; tracing disabled (vanguard)", r.Message, StringComparison.Ordinal);
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
        Assert.Equal(AsterArgs.Plan(_vm.RawTracePath("mw4"), Path.Combine(_dir, "Games", "mw4"), 112, Path.Combine(Plans, "mw4.plan.json"), _vm.JournalPath("mw4")), _cli.Calls[1].Args);
        Assert.DoesNotContain("--journal", _cli.Calls[0].Args);
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
    public void PlanRejectedReasonMapsToAZoneNotFreeOrGenericExplanation()
    {
        Assert.Equal(L.T("Plan_ZoneNotFree"), ManageViewModel.PlanRejectedHint("zone_not_free: LCN 265872734 is in use by another file"));
        Assert.Equal(L.T("Plan_Rejected", "no free run of 5 clusters"), ManageViewModel.PlanRejectedHint("no free run of 5 clusters"));
        Assert.Contains("다른 파일", L.Get("ko", "Plan_ZoneNotFree"), StringComparison.Ordinal);
        Assert.Contains("another file", L.Get("en", "Plan_ZoneNotFree"), StringComparison.Ordinal);
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

    private const string OtherPlanReply = "{\"error\":\"PlanRejected\",\"detail\":\"journal belongs to a different plan\"}";

    [Fact]
    public void ApplyReplansTheRestOnceWhenTheJournalBelongsToAnotherPlanThenRetries()
    {
        Register();
        string plan = Path.Combine(Plans, "mw4.plan.json");
        File.WriteAllText(plan, "{}");
        int dryRuns = 0;
        _cli.On("apply", (args, _) => args.Contains("--dry-run")
            ? ++dryRuns == 1 ? CliResult.Parse(OtherPlanReply, 1) : CliResult.Parse("{\"tasks_todo\":2,\"dry_run\":true}")
            : CliResult.Parse("{\"executed\":true,\"moved_bytes\":2097152,\"failed\":[],\"stopped\":null}"));
        _cli.On("plan", (_, _) => CliResult.Parse("{\"stats\":{\"fixed_clusters\":5}}"));
        _cli.On("verify", (_, _) => CliResult.Parse("{\"verdict\":\"contiguous\"}"));
        var shown = new List<string>();

        ActionResult r = _vm.Apply("mw4", shown.Add);

        Assert.True(r.Ok);
        Assert.Equal(["apply", "plan", "apply", "apply", "verify"], _cli.Calls.Select(c => c.Args[0]));
        Assert.Equal(AsterArgs.Replan(plan, _vm.JournalPath("mw4")), _cli.Calls[1].Args);
        Assert.True(_cli.Calls[1].Elevate);
        Assert.Contains(L.T("Apply_JournalOtherPlan", "mw4"), shown);
        Assert.Contains("이미 옮긴 부분은 유지하고 남은 부분만 다시 계획합니다", L.Get("ko", "Apply_JournalOtherPlan"), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDoesNotReplanASecondTimeWhenTheRetryIsRejectedAgain()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("apply", (_, _) => CliResult.Parse(OtherPlanReply, 1));
        _cli.On("plan", (_, _) => CliResult.Parse("{\"stats\":{}}"));

        ActionResult r = _vm.Apply("mw4", _ => { });

        Assert.False(r.Ok);
        Assert.Equal(["apply", "plan", "apply"], _cli.Calls.Select(c => c.Args[0]));
        Assert.Contains("journal belongs to a different plan", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyStopsWhenTheResidualReplanFails()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("apply", (_, _) => CliResult.Parse(OtherPlanReply, 1));
        _cli.On("plan", (_, _) => CliResult.Parse("{\"error\":\"PlanRejected\",\"detail\":\"no free run of 9 clusters\"}", 1));

        ActionResult r = _vm.Apply("mw4", _ => { });

        Assert.False(r.Ok);
        Assert.Equal(["apply", "plan"], _cli.Calls.Select(c => c.Args[0]));
        Assert.Contains("no free run of 9 clusters", r.Message, StringComparison.Ordinal);
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

    private ActionResult DryRunRejected(string detail)
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("apply", (_, _) => CliResult.Parse("{\"error\":\"PlanRejected\",\"detail\":\"" + detail + "\"}", 1));
        ActionResult r = _vm.Apply("mw4", _ => { });
        Assert.Single(_cli.Calls); // 미실행: 실행 단계로 가지 않음
        Assert.False(r.Ok);
        return r;
    }

    [Fact]
    public void DryRunZoneNotFreeIsReportedAsDeferredNotAsAFailure()
    {
        ActionResult r = DryRunRejected("zone_not_free: LCN 82964732 is in use by another file");

        Assert.Contains("재배치 보류: 계획한 대상 공간을 지금 쓸 수 없습니다. 이번 검사에서는 파일을 옮기지 않았습니다.", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("실패", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("겹칩니다", r.Message, StringComparison.Ordinal);
        Assert.Contains("zone_not_free: LCN 82964732", r.Message, StringComparison.Ordinal);
        Assert.Contains("Relocation deferred: the planned destination is currently unavailable. No files were moved during this check.", L.Get("en", "Dry_Deferred"), StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunReservedByAnotherPlanAddsTheOverlapNote()
    {
        ActionResult r = DryRunRejected("zone_not_free: LCN 100 is reserved by another plan");

        Assert.Contains("재배치 보류", r.Message, StringComparison.Ordinal);
        Assert.Contains("다른 AsterCache 계획이 쓸 공간과 겹칩니다.", r.Message, StringComparison.Ordinal);
        Assert.Contains("overlaps space assigned to another AsterCache plan", L.Get("en", "Dry_DeferredReserved"), StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunOtherPlanRejectionsKeepTheFailureWording()
    {
        ActionResult r = DryRunRejected("volume label");

        Assert.Contains("재배치 검사 실패", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("재배치 보류", r.Message, StringComparison.Ordinal);
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
    public void MeasureBudgetFollowsTheStreamCapForTheZone()
    {
        Register();
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "fresh", "plan.json"));
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), fixture.Replace("\"clusters\": 76800", "\"clusters\": 2097152")); // 4096 바이트 클러스터 x 2097152 = 8192 MiB
        _cli.On("prefetch", (_, _) => CliResult.Parse("{\"bytes\":0,\"seconds\":1.0,\"timing_ms\":{}}"));

        Assert.True(_vm.Measure("mw4").Ok);
        Assert.Equal("8192", _cli.Calls.Single().Args[^3]); // max_stream_mib 미명시: max(4096, 존 8192 MiB)

        string config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"volume_label\"", "\"max_stream_mib\": 100, \"volume_label\""));
        var tray = new TrayViewModel(new ConfigEditor(_dir), _service, new NoStream(), new NoShell(), new NullLog(), null, "1.0.0.0");
        tray.Refresh();
        var vm = new ManageViewModel(tray, new ConfigEditor(_dir), _cli, new Drives(_drive), _elevator, new NoShell(), new NullLog(), () => false, _ => false,
            "--data-dir x", TimeSpan.FromMilliseconds(1), _dir, _ => _untrusted, Path.Combine(_dir, "svc", "AsterCache.Service.exe"));
        _cli.Calls.Clear();

        ActionResult r = vm.Measure("mw4");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("100", _cli.Calls.Single().Args[^3]); // max_stream_mib 명시: 명시값 그대로 (존보다 작음)
    }

    [Fact]
    public void ExportAndImportProfileRunAsterProfileElevatedAndReportTheResult()
    {
        Register();
        string file = Path.Combine(_dir, "mw4.json");
        _cli.On("profile", (args, elevate) =>
        {
            Assert.True(elevate);
            return args[1] == "export"
                ? CliResult.Parse("{\"game\":\"mw4\",\"files\":2,\"raw_events\":5,\"prefetch_events\":3}")
                : CliResult.Parse("{\"imported\":false,\"error\":\"profile_mismatch\",\"mismatch_count\":2,\"mismatches\":[{\"path\":\"a.bin\",\"reason\":\"size\",\"expected_size\":1,\"actual_size\":2}]}", 1);
        });

        ActionResult export = _vm.ExportProfile("mw4", file);
        Assert.True(export.Ok);
        Assert.Contains("파일 2개", export.Message);
        Assert.Equal(["profile", "export", "--game", "mw4", "--out", file, "--data-dir", _dir], _cli.Calls[0].Args);

        ActionResult import = _vm.ImportProfile("mw4", file);
        Assert.False(import.Ok);
        Assert.Contains("일치하지 않는 파일 2개", import.Message);
        Assert.Contains("a.bin", import.Message);
        Assert.Equal(["profile", "import", "--file", file, "--game", "mw4", "--data-dir", _dir, "--json"], _cli.Calls[1].Args);

        _cli.On("profile", (_, _) => CliResult.Parse("{\"imported\":true,\"game\":\"mw4\",\"files\":2,\"raw_events\":5,\"prefetch_events\":3}"));
        Assert.True(_vm.ImportProfile("mw4", file).Ok);
        Assert.False(_vm.ExportProfile("nope", file).Ok);
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

    // ---- 자동 모드 ----

    private void WriteAuto(string json) => File.WriteAllText(Path.Combine(_dir, "auto_status.json"), json);

    [Fact]
    public void AutoColumnsAndStageChainShowUpToTheCurrentStage()
    {
        Register("mw4");
        Assert.True(_tray.AddGame(Game("frost") with { ExeNames = ["frost.exe"], TriggerExes = null }).Ok);
        _cli.On("status", (_, _) => CliResult.Parse("{}"));
        WriteAuto("{\"games\":{\"frost\":{\"source\":\"auto\",\"launcher\":\"steam\",\"stage\":\"tracing\",\"detail\":null,\"at\":\"2026-11-02T01:00:00+00:00\"}}}");
        Assert.True(_vm.RefreshStatus().Ok);

        GameRow frost = _vm.GameRows(null).Single(r => r.Name == "frost");
        GameRow mw4 = _vm.GameRows(null).Single(r => r.Name == "mw4");
        Assert.Equal(("수동", "-"), (mw4.Source, mw4.Stage));
        Assert.Equal("트레이스 수집 중", frost.Stage);
        string[] lines = _vm.StatusLines(null).ToArray();
        Assert.Contains("  자동: frost 발견 → [트레이스 수집 중]", lines);
        Assert.DoesNotContain(lines, l => l.Contains("자동: mw4"));
    }

    [Fact]
    public void ReloadAutoAloneUpdatesStageWithoutAsterProcess()
    {
        Assert.True(_tray.AddGame(Game("frost") with { ExeNames = ["frost.exe"], TriggerExes = null }).Ok);
        WriteAuto("{\"games\":{\"frost\":{\"source\":\"auto\",\"launcher\":\"steam\",\"stage\":\"tracing\",\"detail\":null,\"at\":\"2026-11-02T01:00:00+00:00\"}}}");
        Assert.NotNull(_vm.ReloadAuto());
        Assert.Equal("트레이스 수집 중", _vm.GameRows(null).Single().Stage);
        WriteAuto("{\"games\":{\"frost\":{\"source\":\"auto\",\"launcher\":\"steam\",\"stage\":\"done\",\"detail\":null,\"at\":\"2026-11-02T01:05:00+00:00\"}}}");
        _vm.ReloadAuto();
        Assert.Equal("완료", _vm.GameRows(null).Single().Stage);
        Assert.Contains("  자동: frost 발견 → 트레이스 수집 중 → 재배치 자동 진행 대기 → 재배치 중 → [완료]", _vm.StatusLines(null));
    }

    // ---- 기본 화면 가독성 (상태 구분, 기본 열과 고급 열) ----

    private static readonly DateTimeOffset BootAt = DateTimeOffset.Parse("2026-11-08T00:00:00+00:00");

    // 기준 부팅 시각을 준 관리 뷰모델에 게임 이름들을 등록하고 예열·자동 상태 파일을 읽힙니다.
    private ManageViewModel ReadinessVm(string prewarmGames, string autoGames, params string[] names)
    {
        foreach (string n in names)
        {
            Assert.True(_tray.AddGame(Game(n) with { ExeNames = [n + ".exe"], TriggerExes = null }).Ok);
        }

        _cli.On("status", (_, _) => CliResult.Parse("{\"prewarm\":{\"games\":{" + prewarmGames + "}}}"));
        WriteAuto("{\"games\":{" + autoGames + "}}");
        var vm = new ManageViewModel(_tray, new ConfigEditor(_dir), _cli, new Drives(_drive), _elevator, new NoShell(), new NullLog(), () => false, _ => false,
            "--data-dir x", TimeSpan.FromMilliseconds(1), _dir, _ => _untrusted, Path.Combine(_dir, "svc", "AsterCache.Service.exe"), BootAt);
        Assert.True(vm.RefreshStatus().Ok);
        return vm;
    }

    [Fact]
    public void PartialPastAndNotMeasuredPrewarmAreShownWithDifferentWords()
    {
        ManageViewModel vm = ReadinessVm(
            "\"ready\":{\"at\":\"2026-11-08T02:00:00+00:00\",\"result\":\"ok\",\"last_ok_at\":\"2026-11-08T02:00:00+00:00\"},"
            + "\"part\":{\"at\":\"2026-11-08T02:00:00+00:00\",\"result\":\"ok\",\"last_ok_at\":\"2026-11-08T02:00:00+00:00\",\"warmed_mib\":1904,\"hot_mib\":8000,\"partial\":true},"
            + "\"past\":{\"at\":\"2026-11-07T20:00:00+00:00\",\"result\":\"ok\",\"last_ok_at\":\"2026-11-07T20:00:00+00:00\"},"
            + "\"stale\":{\"at\":\"2026-11-08T03:00:00+00:00\",\"result\":\"skipped\",\"reason\":\"low memory\",\"last_ok_at\":\"2026-11-07T20:00:00+00:00\"}",
            string.Empty, "ready", "part", "past", "stale", "none");

        IReadOnlyList<GameRow> rows = vm.GameRows(null);
        GameRow Row(string n) => rows.Single(r => r.Name == n);
        Assert.Equal(["ready", "partial", "history", "history", "none"], new[] { "ready", "part", "past", "stale", "none" }.Select(n => Row(n).PrewarmKind));
        Assert.Equal("예열됨 (이번 부팅)", Row("ready").PrewarmState);
        Assert.Contains("부분 예열 1904/8000 MiB", Row("part").PrewarmState);
        Assert.Contains("과거 기록", Row("past").PrewarmState);
        Assert.Contains("지금은 아님", Row("past").PrewarmState);
        Assert.Equal("실측 안 됨", Row("none").PrewarmState);
        Assert.Equal(4, new[] { "ready", "part", "past", "none" }.Select(n => Row(n).PrewarmState).Distinct().Count());
        Assert.Equal("예열 건너뜀: 여유 메모리 부족", Row("stale").Reason); // 마지막 시도가 건너뜀이면 왜 준비 상태가 아닌지 기본 화면에 남음
        Assert.Equal(string.Empty, Row("ready").Reason);

        string line = vm.StatusLines(null).Single(l => l.StartsWith("[past]", StringComparison.Ordinal));
        Assert.Contains("과거 기록", line); // 상태 탭도 재부팅 전 성공을 현재처럼 보이지 않게
        Assert.Contains("실측 안 됨", vm.StatusLines(null).Single(l => l.StartsWith("[none]", StringComparison.Ordinal)));
    }

    [Fact]
    public void BasicColumnsKeepHoldReasonsAndAdvancedColumnsKeepTheTechnicalDetail()
    {
        ManageViewModel vm = ReadinessVm(
            string.Empty,
            "\"wait\":{\"source\":\"auto\",\"stage\":\"waiting_idle\",\"detail\":\"daily limit reached, trace unavailable (vanguard), 2 earlier run(s) used\",\"hit_percent\":61,\"coverage_percent\":88,\"hit_at\":\"2026-11-07T20:00:00+00:00\"},"
            + "\"bad\":{\"source\":\"auto\",\"stage\":\"failed\",\"detail\":\"disk exploded\",\"hit_percent\":40,\"hit_at\":\"2026-11-08T05:00:00+00:00\"}",
            "wait", "bad");

        GameRow wait = vm.GameRows(null).Single(r => r.Name == "wait");
        GameRow bad = vm.GameRows(null).Single(r => r.Name == "bad");
        Assert.Equal("재배치 자동 진행 대기", wait.State); // 기본 열은 짧은 단계 이름만
        Assert.Contains("오늘 재배치 횟수 다 씀", wait.Reason);
        Assert.Contains("트레이스 불가 (vanguard)", wait.Reason); // 안티치트 상태는 기본 화면에 남음
        Assert.DoesNotContain("%", wait.State + wait.PrewarmState + wait.Reason);
        Assert.Equal("재배치 자동 진행 대기 · 오늘 재배치 횟수 다 씀 +3", wait.Stage); // 고급 열은 주 상태 + 부가 하나, 나머지는 개수
        Assert.Contains("계획 핫셋/읽은 양 88%", wait.StageFull); // 점수·비율은 고급 열의 단계 글에
        Assert.Contains("과거 기록: 예전 회차 적중 61%", wait.StageFull); // 재부팅 전에 잰 적중률은 과거 기록으로 표시
        Assert.Equal("실패", bad.State);
        Assert.Equal("disk exploded", bad.Reason); // 모르는 실패 사유도 원문 그대로 기본 화면에
        Assert.Contains("예열 직후 적중 40%", bad.StageFull);
        Assert.DoesNotContain("과거 기록", bad.StageFull);
    }

    [Fact]
    public void ResumeNeededLayoutStaysInTheBasicReasonAndNewKeysExistInBothLanguages()
    {
        Register();
        File.WriteAllText(Path.Combine(Plans, "mw4.plan.json"), "{}");
        _cli.On("status", (args, _) => args.Contains("--plan")
            ? CliResult.Parse("{\"plan\":{\"hot_clusters\":200,\"in_zone_clusters\":150,\"verdict\":\"resume-needed\"}}")
            : CliResult.Parse("{}"));
        Assert.True(_vm.RefreshStatus().Ok);

        Assert.Equal("배치 자동으로 이어 옮김", _vm.GameRows(null).Single().Reason);
        foreach (string key in new[] { "Col_State", "Col_PrewarmState", "Col_Reason", "Adv_Show", "State_Ready", "State_Partial", "State_History", "State_NotMeasured", "Reason_PrewarmLast", "Reason_Layout", "Stage_HitPercentOld" })
        {
            Assert.False(string.IsNullOrWhiteSpace(L.Get("ko", key)));
            Assert.False(string.IsNullOrWhiteSpace(L.Get("en", key)));
            Assert.NotEqual(L.Get("ko", key), L.Get("en", key));
        }
    }

    [Fact]
    public void AntiCheatColumnShowsTheDetectedKindOrDash()
    {
        Assert.True(_tray.AddGame(Game("a") with { AntiCheat = "eac" }).Ok);
        Assert.True(_tray.AddGame(Game("b") with { ExeNames = ["b.exe"], TriggerExes = null }).Ok);

        Assert.Equal("eac", _vm.GameRows(null).Single(r => r.Name == "a").AntiCheat);
        Assert.Equal("-", _vm.GameRows(null).Single(r => r.Name == "b").AntiCheat);
        Assert.Equal(("안티치트", "Anti-cheat"), (L.Get("ko", "Col_AntiCheat"), L.Get("en", "Col_AntiCheat")));
    }

    [Fact]
    public void AutoSourceColumnReadsTheRegisteredSource()
    {
        Assert.True(_tray.AddGame(Game("a") with { Source = "auto" }).Ok);
        Assert.Equal("자동", _vm.GameRows(null).Single().Source);
    }

    [Fact]
    public void AutoLineEnglishDoneAndFailedAndSkipped()
    {
        var done = new AutoGame("auto", "gog", "done", null);
        Assert.Equal("Auto: g discovered → collecting trace → waiting for automatic relocation → relocating → [done]", AutoStatusFile.Line("g", done, "en"));
        Assert.Equal("자동: g 발견 → 트레이스 수집 중 → 재배치 자동 진행 대기 → 재배치 중 → [완료]", AutoStatusFile.Line("g", done, "ko"));
        Assert.Equal("자동: g [실패] (no trace)", AutoStatusFile.Line("g", done with { Stage = "failed", Detail = "no trace" }, "ko"));
        Assert.Equal("Auto: g [skipped] (too small)", AutoStatusFile.Line("g", done with { Stage = "skipped", Detail = "too small" }, "en"));
        Assert.Equal("Auto: g [skipped]", AutoStatusFile.Line("g", done with { Stage = "skipped" }, "en"));
        Assert.Equal("Auto: g [weird]", AutoStatusFile.Line("g", done with { Stage = "weird" }, "en"));
    }

    [Fact]
    public void LearningStageShowsInPlaceOfTracingWithTheReadAheadAmount()
    {
        var learning = new AutoGame("auto", "gog", "learning", "512 MiB read ahead");

        Assert.Equal("자동: g 발견 → [첫 실행: 학습 중] (선행 읽기 512 MiB)", AutoStatusFile.Line("g", learning, "ko"));
        Assert.Equal("Auto: g discovered → [first run: learning] (512 MiB read ahead)", AutoStatusFile.Line("g", learning, "en"));
        Assert.Equal("첫 실행: 학습 중 (선행 읽기 512 MiB)", AutoStatusFile.StageText(learning, "ko"));
        Assert.Equal("자동: g 발견 → [첫 실행: 학습 중]", AutoStatusFile.Line("g", learning with { Detail = null }, "ko"));
        Assert.Equal("first run: learning (something else)", AutoStatusFile.StageText(learning with { Detail = "something else" }, "en")); // 모르는 형식은 그대로
    }

    [Fact]
    public void PrewarmedDetailAddsPrewarmedToAnyStage()
    {
        var waiting = new AutoGame("auto", "gog", "waiting_idle", "prewarmed");

        Assert.Equal("재배치 자동 진행 대기 (예열됨)", AutoStatusFile.StageText(waiting, "ko"));
        Assert.Equal("done (prewarmed)", AutoStatusFile.StageText(waiting with { Stage = "done" }, "en"));
        Assert.Equal("완료 (예열됨)", AutoStatusFile.StageText(waiting with { Stage = "done" }, "ko"));
        Assert.Equal("자동: g 발견 → 트레이스 수집 중 → [재배치 자동 진행 대기] (예열됨)", AutoStatusFile.Line("g", waiting, "ko"));
        Assert.Equal("재배치 자동 진행 대기 (오늘 재배치 횟수 다 씀, 예열됨)", AutoStatusFile.StageText(waiting with { Detail = "daily limit reached, prewarmed" }, "ko"));
        Assert.Equal("재배치 자동 진행 대기 (오늘 재배치 횟수 다 씀)", AutoStatusFile.StageText(waiting with { Detail = "daily limit reached" }, "ko"));
        Assert.Equal("실패 (예열됨)", AutoStatusFile.StageText(waiting with { Stage = "failed" }, "ko"));
    }

    [Fact]
    public void RelocatedProfileDetailShowsRelocatedProfileAlongsideThePrewarmTag()
    {
        var done = new AutoGame("auto", null, "done", "relocated (profile)");

        Assert.Equal("완료 (재배치됨(프로필))", AutoStatusFile.StageText(done, "ko"));
        Assert.Equal("done (relocated (profile))", AutoStatusFile.StageText(done, "en"));
        Assert.Equal("완료 (재배치됨(프로필), 예열됨)", AutoStatusFile.StageText(done with { Detail = "relocated (profile), prewarmed" }, "ko"));
        Assert.Equal("done (relocated (profile), prewarming 3/8 MiB)", AutoStatusFile.StageText(done with { Detail = "relocated (profile), prewarming 3/8 MiB" }, "en"));
    }

    [Fact]
    public void RelocatedPrefetchDetailShowsRelocatedPrefetchAlongsideThePrewarmTag()
    {
        var done = new AutoGame("auto", null, "done", "relocated (prefetch)");

        Assert.Equal("완료 (재배치됨(프리페치))", AutoStatusFile.StageText(done, "ko"));
        Assert.Equal("done (relocated (prefetch))", AutoStatusFile.StageText(done, "en"));
        Assert.Equal("완료 (재배치됨(프리페치), 예열됨)", AutoStatusFile.StageText(done with { Detail = "relocated (prefetch), prewarmed" }, "ko"));
        Assert.Equal("done (relocated (prefetch), prewarming 3/8 MiB)", AutoStatusFile.StageText(done with { Detail = "relocated (prefetch), prewarming 3/8 MiB" }, "en"));
    }

    [Fact]
    public void LateTraceWindowDetailIsLocalizedInBothLanguages()
    {
        var tracing = new AutoGame("auto", null, "tracing", "late window 2/6");

        Assert.Equal("트레이스 수집 중 (후반 창 2/6)", AutoStatusFile.StageText(tracing, "ko"));
        Assert.Equal("collecting trace (late window 2/6)", AutoStatusFile.StageText(tracing, "en"));
    }

    [Fact]
    public void PrewarmedDetailShowsFullFolderOrProfileName()
    {
        var discovered = new AutoGame("manual", null, "discovered", "prewarmed (profile Unity)");

        Assert.Equal("발견 (예열됨(프로필 Unity))", AutoStatusFile.StageText(discovered, "ko"));
        Assert.Equal("discovered (prewarmed (profile Unity))", AutoStatusFile.StageText(discovered, "en"));
        Assert.Equal("발견 (예열됨(전체))", AutoStatusFile.StageText(discovered with { Detail = "prewarmed (full)" }, "ko"));
        Assert.Equal("discovered (prewarmed (full folder))", AutoStatusFile.StageText(discovered with { Detail = "prewarmed (full)" }, "en"));
        Assert.Equal("발견 (예열됨(프로필 Unity+Source))", AutoStatusFile.StageText(discovered with { Detail = "prewarmed (profile Unity+Source)" }, "ko"));
        Assert.Equal("done (prewarmed (full folder))", AutoStatusFile.StageText(discovered with { Stage = "done", Detail = "daily limit reached, prewarmed (full)" }, "en"));
    }

    [Fact]
    public void PrewarmingDetailShowsTheProgressInStageColumnAndLine()
    {
        var waiting = new AutoGame("auto", "gog", "waiting_idle", "daily limit reached, prewarming 128/512 MiB");

        Assert.Equal("재배치 자동 진행 대기 (오늘 재배치 횟수 다 씀, 예열 중 128/512 MiB)", AutoStatusFile.StageText(waiting, "ko"));
        Assert.Equal("waiting for automatic relocation (daily relocation count used up, prewarming 128/512 MiB)", AutoStatusFile.StageText(waiting, "en"));
        Assert.Equal("자동: g 발견 → 트레이스 수집 중 → [재배치 자동 진행 대기] (오늘 재배치 횟수 다 씀, 예열 중 128/512 MiB)", AutoStatusFile.Line("g", waiting, "ko"));
        Assert.Equal("done (prewarming 1/2 MiB)", AutoStatusFile.StageText(waiting with { Stage = "done", Detail = "prewarming 1/2 MiB" }, "en"));
    }

    [Fact]
    public void PrewarmFinishedFiresOnTheTransitionOncePerGamePerDay()
    {
        var seen = new Dictionary<string, string>();
        var told = new Dictionary<string, DateOnly>();
        var day1 = new DateOnly(2026, 11, 2);
        AutoStatusFile Status(string? detail) => new(new Dictionary<string, AutoGame> { ["g"] = new("auto", null, "waiting_idle", detail) }, []);

        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed"), day1)); // 트레이를 켰을 때 이미 예열돼 있던 것은 알리지 않음
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarming 64/512 MiB"), day1));
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarming 256/512 MiB"), day1));
        Assert.Equal(["g"], AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed"), day1));
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed"), day1)); // 한 번만
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarming 64/512 MiB"), day1));
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed"), day1)); // 같은 날 두 번째 예열은 알리지 않음
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarming 64/512 MiB"), day1.AddDays(1)));
        Assert.Equal(["g"], AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed"), day1.AddDays(1))); // 다음 날은 다시
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status("prewarming 64/512 MiB"), day1.AddDays(1)));
        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status(null), day1.AddDays(1))); // 취소돼 표시만 사라지면 알리지 않음
    }

    [Fact]
    public void PrewarmFinishedAlsoFiresWhenTheTrayNeverSawTheProgress()
    {
        var seen = new Dictionary<string, string>();
        var told = new Dictionary<string, DateOnly>();
        AutoStatusFile Status(string? detail) => new(new Dictionary<string, AutoGame> { ["g"] = new("auto", null, "discovered", detail) }, []);

        Assert.Empty(AutoStatusFile.PrewarmFinished(seen, told, Status(null), new DateOnly(2026, 11, 2)));
        Assert.Equal(["g"], AutoStatusFile.PrewarmFinished(seen, told, Status("prewarmed (full)"), new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void GameRowStageColumnShowsTheExtra()
    {
        Register();
        WriteAuto("""{"games": {"mw4": {"source": "auto", "stage": "waiting_idle", "detail": "prewarmed", "at": "2026-11-02T00:00:00Z"}}, "events": []}""");
        _vm.ReloadAuto();

        Assert.Contains("(예열됨)", _vm.GameRows(null).Single().StageFull, StringComparison.Ordinal);
        Assert.Equal("재배치 자동 진행 대기 · 예열됨", _vm.GameRows(null).Single().Stage);
    }

    [Fact]
    public void FirstRunFinishedFiresOnlyOnTheTransitionTheTrayWatched()
    {
        AutoStatusFile Status(string stage) => new(new Dictionary<string, AutoGame> { ["g"] = new("auto", null, stage, null) }, []);
        var seen = new Dictionary<string, string>();

        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("waiting_idle"))); // 트레이를 켰을 때 이미 끝나 있던 것은 알리지 않음
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("learning")));
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("learning")));
        Assert.Equal(["g"], AutoStatusFile.FirstRunFinished(seen, Status("waiting_idle")));
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("waiting_idle"))); // 한 번만
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("tracing")));
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("failed"))); // 실패는 알리지 않음
        Assert.Empty(AutoStatusFile.FirstRunFinished(seen, Status("tracing")));
        Assert.Equal(["g"], AutoStatusFile.FirstRunFinished(seen, Status("done")));
        Assert.Equal("다음 실행부터 빨라집니다: g", L.Get("ko", "Toast_FirstRun", "g"));
        Assert.Equal("Faster from the next launch: g", L.Get("en", "Toast_FirstRun", "g"));
    }

    [Fact]
    public void MissingOrCorruptAutoStatusMeansNoLines()
    {
        Register();
        _cli.On("status", (_, _) => CliResult.Parse("{}"));
        _vm.RefreshStatus();
        Assert.Equal("-", _vm.GameRows(null).Single().Stage);

        WriteAuto("{not json");
        _vm.RefreshStatus();
        Assert.Equal("-", _vm.GameRows(null).Single().Stage);
        Assert.DoesNotContain(_vm.StatusLines(null), l => l.Contains("자동:"));
        Assert.Null(AutoStatusFile.Load(Path.Combine(_dir, "auto_status.json")));
    }

    [Fact]
    public void NotificationsPickOnlyNewDiscoveredAndRelocatedEventsAfterStart()
    {
        WriteAuto("{\"events\":[{\"at\":\"2026-11-02T00:59:00+00:00\",\"game\":\"old\",\"kind\":\"discovered\"},"
            + "{\"at\":\"2026-11-02T01:01:00+00:00\",\"game\":\"a\",\"kind\":\"discovered\"},"
            + "{\"at\":\"2026-11-02T01:02:00+00:00\",\"game\":\"a\",\"kind\":\"relocated\"},"
            + "{\"at\":\"2026-11-02T01:03:00+00:00\",\"game\":\"a\",\"kind\":\"other\"}]}");
        AutoStatusFile file = AutoStatusFile.Load(Path.Combine(_dir, "auto_status.json"))!;
        var since = DateTimeOffset.Parse("2026-11-02T01:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var seen = new HashSet<string>();

        IReadOnlyList<AutoNotice> first = AutoStatusFile.NewEvents(file.Events, since, seen);
        foreach (AutoNotice e in first)
        {
            seen.Add(e.Key);
        }

        Assert.Equal(["discovered", "relocated"], first.Select(e => e.Kind));
        Assert.Empty(AutoStatusFile.NewEvents(file.Events, since, seen));
        Assert.Equal("새 게임을 찾았습니다: a", AutoStatusFile.Notice(first[0], "ko"));
        Assert.Equal("Relocation finished: a", AutoStatusFile.Notice(first[1], "en"));
    }

    [Fact]
    public void SetAutoRoundTripKeepsTheOtherAutoValues()
    {
        Register();
        AutoSettings before = _tray.Settings!.Auto;

        Assert.True(_tray.SetAuto(false, true, false, true, 5, " a.exe, ,b.exe ").Ok);

        AutoSettings after = new ConfigEditor(_dir).Load()!.Auto;
        Assert.Equal((false, true, false, true, 5), (after.Enabled, after.Discover, after.Trace, after.Relocate, after.PrewarmRecent));
        Assert.Equal(["a.exe", "b.exe"], after.IgnoreExes);
        Assert.Equal((before.IdleMinutes, before.TraceCapSeconds, before.TraceCapMib, before.MaxRelocationsPerDay), (after.IdleMinutes, after.TraceCapSeconds, after.TraceCapMib, after.MaxRelocationsPerDay));
        Assert.False(_tray.SetAuto(true, true, true, true, 33, string.Empty).Ok); // 범위 밖은 검증이 거부
        Assert.Equal(5, new ConfigEditor(_dir).Load()!.Auto.PrewarmRecent);
    }

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
