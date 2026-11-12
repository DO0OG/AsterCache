using System.IO.Compression;
using System.Text;
using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 트레이 편의 기능: 설정 한 번에 저장, 마지막 플레이 요약, 진단 내보내기, 알림 끄기, 빈 목록 안내.
public sealed class TrayConvenienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_conv_" + Guid.NewGuid().ToString("N"));
    private readonly TrayViewModelTests.MemoryLog _log = new();
    private readonly TrayViewModel _tray;
    private readonly ManageViewModel _manage;

    public TrayConvenienceTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigEditor(_dir);
        _tray = new TrayViewModel(config, new TrayViewModelTests.FakeService(), new TrayViewModelTests.FakeStreamer(), new TrayViewModelTests.FakeShell(), _log, "1.2.3.0",
            Path.Combine(_dir, "tray.json"));
        _manage = new ManageViewModel(_tray, config, new NoCli(), new NoDrives(), new NoElevator(), new TrayViewModelTests.FakeShell(), _log, () => false, _ => false);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string At(string name) => Path.Combine(_dir, name);

    private static GameInput Game(string dir, string name = "mw4") => new(
        name, ["game.exe"], Path.Combine(dir, "plans", name + ".prefetch.json"), Path.Combine(dir, "plans", name + ".plan.json"),
        Path.Combine(dir, "Games", name), [Path.Combine(dir, "Games")], "GAMES");

    // ---- 설정 한 번에 저장 ----

    [Theory]
    [InlineData("big", "10", "8", "8", 2)] // 존 상한 형식 오류
    [InlineData("2048", "x", "8", "8", 2)] // 예열 유지 주기 형식 오류
    [InlineData("2048", "10", "0", "8", 2)] // SSD 예산 범위 밖
    [InlineData("2048", "10", "8", "abc", 2)] // 재배치 상한 형식 오류
    [InlineData("2048", "10", "8", "8", 99)] // 설정 검증이 거부하는 예열 개수
    public void ApplyAutoWithOneBadInputSavesNothing(string zone, string rewarm, string tier, string gib, int recent)
    {
        Assert.True(_tray.AddGame(Game(_dir)).Ok);
        string before = File.ReadAllText(At("config.json"));
        var form = new AutoForm(false, false, false, false, recent, "a.exe", zone, rewarm) { TierEnabled = true, TierBudget = tier, RelocateGib = gib };

        Assert.False(_tray.ApplyAuto(form).Ok);

        Assert.Equal(before, File.ReadAllText(At("config.json")));
    }

    [Fact]
    public void ApplyAutoWithAllValidInputsSavesEverythingInOneEdit()
    {
        Assert.True(_tray.AddGame(Game(_dir)).Ok);
        _log.Lines.Clear();
        var form = new AutoForm(false, true, false, true, 3, "a.exe, b.exe", "2048", "7") { TierEnabled = true, TierBudget = "16", RelocateGib = "4" };

        Assert.True(_tray.ApplyAuto(form).Ok);

        AsterSettings s = new ConfigEditor(_dir).Load()!;
        Assert.Equal((false, true, false, true, 3), (s.Auto.Enabled, s.Auto.Discover, s.Auto.Trace, s.Auto.Relocate, s.Auto.PrewarmRecent));
        Assert.Equal(["a.exe", "b.exe"], s.Auto.IgnoreExes);
        Assert.Equal((2048, 7), (s.MaxZoneMib, s.Auto.RewarmMinutes));
        Assert.Equal((true, 16, 4), (s.Auto.SsdTier.Enabled, s.Auto.SsdTier.BudgetGib, s.Auto.MaxRelocateGibPerDay));
        Assert.Single(_log.Lines, l => l == L.T("Msg_AutoSet")); // 예전에는 네 번에 나눠 저장해 같은 알림이 네 번 남았음
    }

    // ---- 알림 끄기 ----

    [Fact]
    public void NotificationsAreOnByDefaultAndTheExistingTrayJsonStillReads()
    {
        string path = At("tray.json");
        Assert.True(TraySettings.Load(path).NotificationsEnabled);
        File.WriteAllText(path, "{\"update_feed_url\": \"https://example.invalid/feed\"}"); // 예전 버전이 쓰던 키: 거부하지 않고 무시
        Assert.True(TraySettings.Load(path).NotificationsEnabled);
        File.WriteAllText(path, "{\"update_feed_url\": \"http://example.invalid/feed\", \"notifications\": {\"enabled\": false}}");
        Assert.False(TraySettings.Load(path).NotificationsEnabled);
        File.WriteAllText(path, "{\"notifications\": {}}");
        Assert.True(TraySettings.Load(path).NotificationsEnabled);
        File.WriteAllText(path, "{\"notifications\": {\"enabled\": false}}");
        Assert.False(TraySettings.Load(path).NotificationsEnabled);
    }

    [Theory]
    [InlineData("{\"notifications\": {\"enabled\": \"no\"}}")]
    [InlineData("{\"notifications\": {\"volume\": 1}}")]
    [InlineData("{\"notifications\": true}")]
    [InlineData("{\"toast\": true}")]
    public void BadNotificationSettingsAreRejectedLikeOtherTrayJsonErrors(string json)
    {
        File.WriteAllText(At("tray.json"), json);
        Assert.Throws<SettingsException>(() => TraySettings.Load(At("tray.json")));
    }

    [Fact]
    public void TurningNotificationsOffIsSavedKeepsOtherKeysAndSuppressesToastsButLogsThem()
    {
        File.WriteAllText(At("tray.json"), "{\"update_feed_url\": \"https://example.invalid/feed\"}");
        Assert.True(_tray.AllowToast("before"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("before", StringComparison.Ordinal));

        Assert.True(_tray.SetNotifications(false).Ok);

        Assert.False(_tray.NotificationsEnabled);
        Assert.False(_tray.AllowToast("A game was found"));
        Assert.Contains(_log.Lines, l => l.Contains("A game was found", StringComparison.Ordinal));
        TraySettings saved = TraySettings.Load(At("tray.json"));
        Assert.False(saved.NotificationsEnabled);
        Assert.Contains("https://example.invalid/feed", File.ReadAllText(At("tray.json"))); // 다른 키는 그대로 남음
        Assert.True(_tray.SetNotifications(true).Ok);
        Assert.True(_tray.AllowToast("again"));
    }

    [Fact]
    public void SavingNotificationsFailsCleanlyWhenTheFileCannotBeWritten()
    {
        File.WriteAllText(At("blocker"), "x");
        var vm = new TrayViewModel(new ConfigEditor(_dir), new TrayViewModelTests.FakeService(), new TrayViewModelTests.FakeStreamer(), new TrayViewModelTests.FakeShell(), _log,
            null, Path.Combine(_dir, "blocker", "tray.json"));

        Assert.False(vm.SetNotifications(false).Ok);
        Assert.True(vm.NotificationsEnabled);
    }

    // ---- 빈 목록 안내 ----

    [Fact]
    public void EmptyListTextDependsOnWhetherAutomaticDiscoveryIsOn()
    {
        Assert.Equal(L.T("Empty_GamesWait"), _manage.EmptyGamesText(null));
        Assert.Equal(L.T("Empty_GamesDrive"), _manage.EmptyGamesText("D:"));
        Assert.True(_tray.AddGame(Game(_dir)).Ok);
        Assert.True(_tray.SetAuto(true, false, true, true, 0, string.Empty).Ok);

        Assert.Equal(L.T("Empty_Games"), _manage.EmptyGamesText(null));
    }

    // ---- 마지막 플레이 요약 ----

    private static string Session(string startedAt, double seconds, string warm, string windows, string total = "null", string status = "complete") =>
        $$"""
        {"sessionId":"s","game":"mw4","startedAt":"{{startedAt}}","durationSeconds":{{seconds}},"status":"{{status}}","startWarm":{{warm}},"startStream":{"status":"pending"},"windows":[{{windows}}],"total":{{total}}}
        """;

    private static string Win(int? seconds, long bytes, double readSeconds, int slow) =>
        $$"""
        {"seconds":{{(seconds?.ToString() ?? "null")}},"measuredAtSeconds":31,"readBytes":{{bytes}},"readCount":10,"readSeconds":{{readSeconds}},"slowSamples":{{slow}},"sampleCount":30}
        """;

    private void WritePlay(params string[] sessions) =>
        File.WriteAllText(At("play_samples.json"), "{\"games\":{\"mw4\":[" + string.Join(",", sessions) + "]}}");

    [Fact]
    public void PlaySummaryShowsTheLatestSessionWithDiskReadStatisticsOnly()
    {
        DateTimeOffset latest = new(2026, 11, 12, 12, 3, 0, TimeSpan.Zero);
        WritePlay(
            Session("2026-11-01T01:00:00+00:00", 600, "null", Win(30, 1, 1, 0)),
            Session(latest.ToString("O"), 2520, """{"mode":"hot set","result":"ok","readMib":300,"targetMib":900}""",
                Win(30, 412L << 20, 1.84, 0) + "," + Win(120, 980L << 20, 3.2, 1), Win(null, 4L << 30, 21.04, 2)));

        PlayRecords records = PlaySummary.Load(At("play_samples.json"));
        string text = PlaySummary.Describe(records, "MW4");

        Assert.Contains(latest.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), text);
        Assert.Contains(L.T("Play_Min", 42), text);
        Assert.Contains(L.T("Play_WarmPartial"), text);
        Assert.Contains("412 MiB", text);
        Assert.Contains("980 MiB", text);
        Assert.Contains("4.0 GiB", text);
        Assert.Contains("1.8", text);
        Assert.Contains("21.0", text);
        Assert.Contains(L.T("Play_Disk", "", "", "").Split(':')[0], text);
        Assert.DoesNotContain("개선", text);
        Assert.DoesNotContain("빨라", text);
    }

    [Fact]
    public void PlaySummaryDistinguishesNoRecordFromAnUnreadableFile()
    {
        Assert.Equal(L.T("Play_None"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4"));
        WritePlay(Session("2026-11-12T01:00:00+00:00", 90, "null", string.Empty));
        Assert.Equal(L.T("Play_None"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "other"));
        File.WriteAllText(At("play_samples.json"), "{ not json");

        Assert.Equal(L.T("Play_Unreadable"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4"));
    }

    [Fact]
    public void PlaySummaryShowsMissingWindowsAndUnavailableCounters()
    {
        WritePlay(Session("2026-11-12T01:00:00+00:00", 45, """{"mode":"hot set","result":"skipped","reason":"cooldown"}""", Win(30, 5L << 20, 0.5, 0)));
        string shortPlay = PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4");
        Assert.Contains(L.T("Play_Sec", 45), shortPlay);
        Assert.Contains(L.T("Play_WarmSkipped"), shortPlay);
        Assert.Contains(L.T("Play_WinLabel", 120) + ": -", shortPlay);
        Assert.Contains(L.T("Play_WinTotal") + ": -", shortPlay);

        WritePlay(Session("2026-11-12T01:00:00+00:00", 45, "null", string.Empty, status: "unavailable"));
        Assert.Contains(L.T("Play_CounterUnavailable"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4"));
    }

    [Theory]
    [InlineData("""{"result":"ok","readMib":900,"targetMib":900}""", "Play_WarmDone")]
    [InlineData("""{"result":"ok","readMib":10,"targetMib":900}""", "Play_WarmPartial")]
    [InlineData("""{"result":"skipped","reason":"cancelled"}""", "Play_WarmPartial")]
    [InlineData("""{"result":"skipped","reason":"low memory"}""", "Play_WarmSkipped")]
    [InlineData("""{"result":"failed"}""", "Play_WarmFailed")]
    [InlineData("null", "Play_WarmUnknown")]
    public void PlaySummaryNamesTheStartPrewarmState(string warm, string key)
    {
        WritePlay(Session("2026-11-12T01:00:00+00:00", 600, warm, string.Empty));
        string head = PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4").Split('\n')[0];
        Assert.EndsWith(L.T(key), head);
    }

    [Fact]
    public void GameRowsCarryThePlaySummaryAndPickUpAChangedFile()
    {
        Assert.True(_tray.AddGame(Game(_dir)).Ok);
        _tray.Refresh();
        _manage.ReloadAuto();
        Assert.Equal(L.T("Play_None"), _manage.GameRows(null).Single().Play);

        WritePlay(Session("2026-11-12T01:00:00+00:00", 600, "null", Win(30, 2L << 20, 0.1, 0)));
        _manage.ReloadAuto();

        Assert.Contains("2 MiB", _manage.GameRows(null).Single().Play);
        File.WriteAllText(At("play_samples.json"), "garbage");
        _manage.ReloadAuto();
        Assert.Equal(L.T("Play_Unreadable"), _manage.GameRows(null).Single().Play);
    }

    // ---- 진단 내보내기 ----

    private DiagnosticsInput Input(long? max = null) => new(_dir, At("logs/tray.log"), "1.2.3.0", "1.2.3.0", "Windows test 1.0", "Hong")
    {
        MaxBytes = max ?? DiagnosticsExport.DefaultMaxBytes,
    };

    private void WriteDataFiles()
    {
        Directory.CreateDirectory(At("logs"));
        File.WriteAllText(At("config.json"), """{"games":[{"origin_root":"C:\\Users\\Hong\\Games","keep":"D:\\Games\\Keep"}],"password":"hunter2","url":"https://bob:hunter2@example.com/f?token=abc123def&x=1","k":"ghp_abcdefghijklmnopqrstuvwxyz0123456789"}""");
        File.WriteAllText(At("tray.json"), "{}");
        File.WriteAllText(At("auto_status.json"), "{\"games\":{}}");
        File.WriteAllText(At("prewarm_status.json"), "{\"games\":{}}");
        File.WriteAllText(At("play_samples.json"), "{\"games\":{}}");
        File.WriteAllLines(At("logs/service.log"), Enumerable.Range(1, 3000).Select(i => $"service line {i} C:\\Users\\Hong\\x"));
        File.WriteAllLines(At("logs/tray.log"), ["tray line 1 owner Hong", "tray line 2"]);
    }

    private static Dictionary<string, string> ReadZip(string zip)
    {
        using ZipArchive a = ZipFile.OpenRead(zip);
        return a.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        });
    }

    [Fact]
    public void DiagnosticsZipHoldsTheVersionsSettingsStatusFilesAndTheLastLogLinesWithPersonalDetailsMasked()
    {
        WriteDataFiles();
        string zip = At("out.zip");

        DiagnosticsResult r = DiagnosticsExport.Write(zip, Input());

        Assert.True(r.Ok);
        Dictionary<string, string> files = ReadZip(zip);
        Assert.Equal(["auto_status.json", "config.json", "info.txt", "logs/service.log", "logs/tray.log", "manifest.txt", "play_samples.json", "prewarm_status.json", "tray.json"], files.Keys.Order());
        Assert.Contains("tray: 1.2.3.0", files["info.txt"]);
        Assert.Contains("service: 1.2.3.0", files["info.txt"]);
        Assert.Contains("os: Windows test 1.0", files["info.txt"]);
        string[] service = files["logs/service.log"].Split(Environment.NewLine);
        Assert.Equal(2000, service.Length);
        Assert.StartsWith("service line 1001 ", service[0]);
        Assert.EndsWith("C:\\Users\\<user>\\x", service[^1]);
        string config = files["config.json"];
        Assert.Contains(@"C:\\Users\\<user>\\Games", config);
        Assert.Contains(@"D:\\Games\\Keep", config);
        Assert.Contains("https://<redacted>@example.com", config);
        Assert.Contains("token=<redacted>", config);
        Assert.DoesNotContain("hunter2", config);
        Assert.DoesNotContain("ghp_", config);
        Assert.DoesNotContain("Hong", string.Concat(files.Values));
        Assert.Contains("logs/tray.log: included", files["manifest.txt"]);
    }

    [Fact]
    public void DiagnosticsZipStaysUnderTheSizeLimitAndNamesWhatItLeftOut()
    {
        WriteDataFiles();
        File.WriteAllText(At("auto_status.json"), new string('a', 60_000)); // 남은 몫보다 큼
        File.WriteAllLines(At("logs/service.log"), Enumerable.Range(1, 1500).Select(i => $"service line {i} " + new string('x', 100)));
        const long limit = 100 * 1024;
        string zip = At("small.zip");

        DiagnosticsResult r = DiagnosticsExport.Write(zip, Input(limit));

        Assert.True(r.Ok);
        using (ZipArchive a = ZipFile.OpenRead(zip))
        {
            Assert.True(a.Entries.Sum(e => e.Length) <= limit);
        }

        Dictionary<string, string> files = ReadZip(zip);
        Assert.DoesNotContain("auto_status.json", files.Keys);
        Assert.Contains("auto_status.json: skipped, too large", files["manifest.txt"]);
        Assert.Contains("logs/service.log: included", files["manifest.txt"]);
        Assert.Contains("cut to size limit", files["manifest.txt"]);
        Assert.EndsWith("service line 1500 " + new string('x', 100), files["logs/service.log"].TrimEnd());
    }

    [Fact]
    public void DiagnosticsZipSkipsMissingAndUnreadableFilesWithTheReason()
    {
        WriteDataFiles();
        File.Delete(At("tray.json"));
        string zip = At("skip.zip");

        DiagnosticsResult r;
        using (new FileStream(At("config.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            r = DiagnosticsExport.Write(zip, Input());
        }

        Assert.True(r.Ok);
        string manifest = ReadZip(zip)["manifest.txt"];
        Assert.Contains("tray.json: skipped, missing", manifest);
        Assert.Contains("config.json: skipped, unreadable (IOException)", manifest);
        Assert.Contains(r.Entries, e => e is { Name: "config.json", Included: false });
    }

    [Fact]
    public void DiagnosticsExportReportsAFailureWhenTheTargetCannotBeWritten()
    {
        WriteDataFiles();

        DiagnosticsResult r = DiagnosticsExport.Write(At("nope/dir/out.zip"), Input());

        Assert.False(r.Ok);
        Assert.False(File.Exists(At("nope/dir/out.zip")));
    }

    [Fact]
    public void ViewModelExportsTheDiagnosticsAndReportsTheCounts()
    {
        WriteDataFiles();
        string zip = At("vm.zip");

        ActionResult r = _manage.ExportDiagnostics(zip, "Hong", "Windows test 1.0");

        Assert.True(r.Ok);
        Assert.Contains(zip, r.Message);
        Assert.Contains("tray: 1.2.3.0", ReadZip(zip)["info.txt"]);
        Assert.False(_manage.ExportDiagnostics(At("nope/dir/out.zip")).Ok);
    }

    [Theory]
    [InlineData(@"C:\Users\Hong\Documents\a.txt", @"C:\Users\<user>\Documents\a.txt")]
    [InlineData(@"c:/users/Hong/x", @"c:/users/<user>/x")]
    [InlineData(@"{""p"":""C:\\Users\\Hong\\x""}", @"{""p"":""C:\\Users\\<user>\\x""}")]
    [InlineData("user Hongkong stays", "user Hongkong stays")]
    [InlineData("git clone https://tok@github.com/x/y.git", "git clone https://<redacted>@github.com/x/y.git")]
    [InlineData("GET /a?api_key=SECRET1&b=2", "GET /a?api_key=<redacted>&b=2")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234", "Authorization: <redacted>")]
    [InlineData("sent Bearer abcdefghijklmnop1234 ok", "sent Bearer <redacted> ok")]
    [InlineData("sha 0123456789abcdef0123456789abcdef0123456789abcdef", "sha <redacted>")]
    [InlineData("plain text, D:\\Games\\Frostpunk\\x.exe 12345", "plain text, D:\\Games\\Frostpunk\\x.exe 12345")]
    public void RedactMasksProfilePathsUserNamesAndCredentialShapedValues(string input, string expected) =>
        Assert.Equal(expected, DiagnosticsExport.Redact(input, "Hong"));

    [Fact]
    public void RedactLeavesShortUserNamesToThePathRuleOnly() =>
        Assert.Equal(@"C:\Users\<user>\x and Al stays", DiagnosticsExport.Redact(@"C:\Users\Al\x and Al stays", "Al"));

    [Theory]
    [InlineData(@"c:\users\kim\x", @"c:\users\<user>\x")]
    [InlineData(@"C:\USERS\KIM\x", @"C:\USERS\<user>\x")]
    [InlineData(@"c:/users/kim/x", @"c:/users/<user>/x")]
    [InlineData(@"{""p"":""c:\\users\\kim\\x""}", @"{""p"":""c:\\users\\<user>\\x""}")]
    [InlineData(@"C:\Users\John Smith\AppData\x", @"C:\Users\<user>\AppData\x")]
    [InlineData(@"C:\Users\김 철수\AppData\x", @"C:\Users\<user>\AppData\x")]
    public void RedactPathRuleWorksWithoutTheMatchingUserNameIgnoringCaseAndKeepingSpacedNamesWhole(string input, string expected) =>
        Assert.Equal(expected, DiagnosticsExport.Redact(input, "Other"));

    [Fact]
    public void RedactMasksSpacedUserNamesEverywhereBeforeThePathRule()
    {
        Assert.Equal(@"C:\Users\<user>\AppData and <user> owns it", DiagnosticsExport.Redact(@"C:\Users\John Smith\AppData and John Smith owns it", "John Smith"));
        Assert.Equal(@"C:\Users\<user>\x, owner <user>", DiagnosticsExport.Redact(@"C:\Users\김 철수\x, owner 김 철수", "김 철수"));
    }

    [Fact]
    public void RedactMasksJsonValuesWhoseKeyNamesASecretWhateverTheLength()
    {
        string json = """{"refresh_token":"a","Client_Secret": "x y","ApiKey":"k","api_key": 12,"Authorization":"Bearer zz","DB_PASSWORD":"p\"q","name":"keep","n":1}""";

        string masked = DiagnosticsExport.Redact(json, "Other");

        Assert.Equal("""{"refresh_token":"<redacted>","Client_Secret": "<redacted>","ApiKey":"<redacted>","api_key": "<redacted>","Authorization":"<redacted>","DB_PASSWORD":"<redacted>","name":"keep","n":1}""", masked);
    }

    [Fact]
    public void PlaySummaryDoesNotThrowWhenTheStartTimeIsMissingOrOutOfRange()
    {
        File.WriteAllText(At("play_samples.json"), """{"games":{"mw4":[{"durationSeconds":90,"status":"complete","windows":[]}]}}""");
        Assert.Contains(L.T("Play_NoTime"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4"));
        WritePlay(Session("0001-01-01T00:00:00+00:00", 90, "null", string.Empty));
        Assert.Contains(L.T("Play_NoTime"), PlaySummary.Describe(PlaySummary.Load(At("play_samples.json")), "mw4"));
    }

    [Fact]
    public void DiagnosticsLeavesNoTmpFileEvenWhenAnUnexpectedExceptionEscapes()
    {
        string zip = At("boom.zip");
        var input = new DiagnosticsInput(null!, At("logs/tray.log"), "1", null, "os", "Hong");

        Assert.ThrowsAny<ArgumentException>(() => DiagnosticsExport.Write(zip, input));

        Assert.False(File.Exists(zip + ".tmp"));
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void SavingNotificationsLeavesNoTmpFileWhenTheReplaceFails()
    {
        Directory.CreateDirectory(At("tray.json")); // 파일 자리에 폴더가 있어 교체가 실패함

        Assert.False(_tray.SetNotifications(false).Ok);

        Assert.False(File.Exists(At("tray.json.tmp")));
    }

    // ---- 가짜 ----

    private sealed class NoCli : IAsterCli
    {
        public CliResult Run(IReadOnlyList<string> args, bool elevate = false) => CliResult.Fail("no cli in this test");
    }

    private sealed class NoDrives : IDriveSource
    {
        public IReadOnlyList<DriveFacts> Fixed() => [];
    }

    private sealed class NoElevator : IElevator
    {
        public void Run(string exe, string arguments)
        {
        }

        public void Run(string exe, IReadOnlyList<string> arguments)
        {
        }

        public void Start(string exe, string arguments)
        {
        }
    }
}
