using System.Text.Json.Nodes;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 부팅 뒤 콜드 비용 진단 (BootProbe), 업데이트 뒤 지연 (post_update_delay_min) 의 판정·설정·예열 연결.
public sealed class BootProbeTests : IDisposable
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_bootprobe_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly List<string> _opened = [];
    private TimeSpan _clock;

    public BootProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "auto_status.json");

    private static long Ms(int minutes, int seconds) => (minutes * 60L + seconds) * 1000;

    private static AsterSettings Settings(params GameSettings[] games) => new(50, games) { Auto = AutoSettings.Default };

    private AutoStatus Status() => new(StatusPath, _log, () => T0);

    private static IReadOnlyList<string> Files(GameSettings g, int n) => [.. Enumerable.Range(0, n).Select(i => $@"{g.OriginRoot}\f{i}.bin")];

    // 열 때마다 가짜 시계가 5 ms 가고 failing 에 든 경로는 열기에 실패함.
    private AutoBootProbe? Run(AsterSettings s, AutoStatus status, long uptimeMs, int files, params string[] failing) =>
        BootProbe.Run(s, status, _log, T0, uptimeMs, g => Files(g, files), p =>
        {
            _opened.Add(p);
            _clock += TimeSpan.FromMilliseconds(5);
            return !failing.Contains(p);
        }, () => _clock);

    private AutoStatus WithPlayed(params (string Name, int HoursAgo)[] played)
    {
        AutoStatus status = Status();
        foreach ((string name, int hoursAgo) in played)
        {
            status.MarkPlayed(name, "auto", AutoStage.Done, T0.AddHours(-hoursAgo));
        }

        return status;
    }

    [Theory]
    [InlineData(9, 59, true)]
    [InlineData(10, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(300, 0, false)]
    public void ProbesOnlyWhenTheSystemHasBeenUpUnderTenMinutes(int minutes, int seconds, bool probes)
    {
        GameSettings g = Games.Make("a", "a.exe");

        AutoBootProbe? probe = Run(Settings(g), WithPlayed(("a", 1)), Ms(minutes, seconds), 10);

        Assert.Equal(probes, probe is not null);
        Assert.Equal(probes ? 10 : 0, _opened.Count);
    }

    [Fact]
    public void NoRecentGameMeansNoProbe()
    {
        AutoBootProbe? probe = Run(Settings(Games.Make("a", "a.exe")), Status(), 1000, 10);

        Assert.Null(probe);
        Assert.Empty(_opened);
    }

    [Fact]
    public void AutoModeOffMeansNoProbe()
    {
        AsterSettings s = Settings(Games.Make("a", "a.exe")) with { Auto = AutoSettings.Default with { Enabled = false } };

        Assert.Null(Run(s, WithPlayed(("a", 1)), 1000, 10));
        Assert.Empty(_opened);
    }

    [Fact]
    public void OnlyTheFirstTwoHundredFilesAreOpened()
    {
        GameSettings g = Games.Make("a", "a.exe");

        AutoBootProbe? probe = Run(Settings(g), WithPlayed(("a", 1)), 1000, 300);

        Assert.Equal(200, _opened.Count);
        Assert.Equal(Files(g, 200), _opened);
        Assert.Equal(200, probe!.Files);
    }

    [Fact]
    public void ShorterListsAreOpenedWhole()
    {
        AutoBootProbe? probe = Run(Settings(Games.Make("a", "a.exe")), WithPlayed(("a", 1)), 1000, 37);

        Assert.Equal(37, probe!.Files);
        Assert.Equal(37, _opened.Count);
    }

    [Fact]
    public void FailedOpensAreLeftOutOfTheCountAndTheAverage()
    {
        GameSettings g = Games.Make("a", "a.exe");

        AutoBootProbe? probe = Run(Settings(g), WithPlayed(("a", 1)), 1000, 10, failing: [Files(g, 10)[0], Files(g, 10)[1]]);

        Assert.Equal(10, _opened.Count); // 열어 보기는 모두 함
        Assert.Equal(8, probe!.Files);
        Assert.Equal(5.0, probe.OpenMsPerFile); // 성공한 열기의 평균 (실패한 둘은 시간에도 넣지 않음)
    }

    [Fact]
    public void NothingOpenedLeavesNoRecord()
    {
        GameSettings g = Games.Make("a", "a.exe");
        AutoStatus status = WithPlayed(("a", 1));

        Assert.Null(Run(Settings(g), status, 1000, 3, failing: Files(g, 3).ToArray()));
        Assert.Null(status.Get("a")!.BootProbe);
    }

    [Fact]
    public void ProbesTheMostRecentlyPlayedGameOnly()
    {
        GameSettings a = Games.Make("a", "a.exe");
        GameSettings b = Games.Make("b", "b.exe");
        AutoStatus status = WithPlayed(("a", 5), ("b", 2));

        Run(Settings(a, b), status, 1000, 4);

        Assert.All(_opened, p => Assert.StartsWith(b.OriginRoot, p, StringComparison.Ordinal));
        Assert.NotNull(status.Get("b")!.BootProbe);
        Assert.Null(status.Get("a")!.BootProbe);
    }

    [Fact]
    public void RecordIsWrittenToAutoStatusInTheAgreedShapeAndKeepsTheOtherKeys()
    {
        GameSettings g = Games.Make("a", "a.exe");
        AutoStatus status = WithPlayed(("a", 1));
        status.RecordHit("a", 80, T0);

        Run(Settings(g), status, Ms(2, 0), 10);

        var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["a"]!;
        Assert.Equal(["source", "launcher", "stage", "detail", "at", "last_played", "relocated_at", "plays", "hit_percent", "hit_at", "boot_probe"], game.Select(kv => kv.Key));
        var probe = (JObject)game["boot_probe"]!;
        Assert.Equal(["at", "files", "open_ms_per_file"], probe.Select(kv => kv.Key));
        Assert.Equal(("2026-11-03T09:00:00.0000000+00:00", 10L, 5.0), (probe["at"], probe["files"], probe["open_ms_per_file"]));
        Assert.Equal(80L, game["hit_percent"]);
        Assert.Contains(_log.Lines, l => l.Contains("a: boot probe opened 10 files, 5.00 ms per file", StringComparison.Ordinal));

        AutoGameEntry again = Status().Get("a")!;
        Assert.Equal(new AutoBootProbe(T0, 10, 5.0), again.BootProbe);
    }

    [Fact]
    public void RecordIsAbsentFromEntriesThatWereNeverProbed()
    {
        AutoStatus status = WithPlayed(("a", 1));

        var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["a"]!;
        Assert.DoesNotContain("boot_probe", game.Select(kv => kv.Key));
        Assert.Null(status.Get("a")!.BootProbe);
    }

    [Fact]
    public void TracePathsListsFirstFilesOnceWithoutTouchingThem()
    {
        string root = Path.Combine(_dir, "game");
        string trace = Path.Combine(_dir, "a.prefetch.json");
        var rows = new JsonArray();
        for (int i = 0; i < 300; i++)
        {
            rows.Add(new JsonObject { ["path"] = Path.Combine(root, $"f{i}.bin"), ["offset"] = 0, ["length"] = 4096 });
            rows.Add(new JsonObject { ["path"] = Path.Combine(root, $"f{i}.bin"), ["offset"] = 4096, ["length"] = 4096 }); // 같은 파일의 다음 행
        }

        File.WriteAllText(trace, rows.ToJsonString());
        GameSettings g = Games.Make("a", "a.exe") with { Trace = trace, OriginRoot = root, AllowedRoots = [root] };

        IReadOnlyList<string> paths = BootProbe.TracePaths(g);

        Assert.Equal(200, paths.Count);
        Assert.Equal(Path.Combine(root, "f0.bin"), paths[0]);
        Assert.Equal(Path.Combine(root, "f199.bin"), paths[199]);
        Assert.False(Directory.Exists(root)); // 파일이 없어도 경로를 돌려줌: 크기를 재거나 열지 않음
        Assert.Empty(BootProbe.TracePaths(g with { Trace = Path.Combine(_dir, "missing.json") }));
    }

    [Fact]
    public void OpenCloseOpensAnExistingFileEvenWhileOthersHoldItAndReportsMissingOnes()
    {
        string file = Path.Combine(_dir, "meta.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        using FileStream hold = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(BootProbe.OpenClose(file));
        Assert.False(BootProbe.OpenClose(Path.Combine(_dir, "none.bin")));
    }

    // ---- 업데이트 뒤 지연 ----

    private static AutoSettings Delay(int minutes) => AutoSettings.Default with { PostUpdateDelayMin = minutes };

    private AutoStatus Updated() // 업데이트를 감지해 재계획 상태로 돌아간 게임: stage discovered, at = T0
    {
        AutoStatus status = WithPlayed(("a", 1));
        status.Complete("a", "auto", T0.AddHours(-1), counted: false, null);
        status.ResetForReplan("a", "game updated, will trace again on next run");
        return status;
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 1)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    public void WaitEndsExactlyAfterTheConfiguredMinutes(int sinceMinutes, int leftMinutes)
    {
        TimeSpan? wait = AutoRelocator.PostUpdateWait(Updated(), Delay(10), "a", T0.AddMinutes(sinceMinutes));

        Assert.Equal(leftMinutes == 0 ? null : TimeSpan.FromMinutes(leftMinutes), wait);
    }

    [Fact]
    public void ZeroMeansNoWaitAndOtherStatesNeverWait()
    {
        AutoStatus status = Updated();
        Assert.Null(AutoRelocator.PostUpdateWait(status, Delay(0), "a", T0));
        Assert.Null(AutoRelocator.PostUpdateWait(status, Delay(10), "unknown", T0));

        status.Set("a", "auto", null, AutoStage.WaitingIdle, null); // 새 트레이스가 끝나 다음 단계로 감
        Assert.Null(AutoRelocator.PostUpdateWait(status, Delay(10), "a", T0));
    }

    [Fact]
    public void ProgressTagsAppendedToTheDetailDoNotEndTheWait()
    {
        AutoStatus status = Updated();
        status.Set("a", "auto", null, AutoStage.Discovered, "game updated, will trace again on next run, prewarming 1/5 MiB");

        Assert.NotNull(AutoRelocator.PostUpdateWait(status, Delay(10), "a", T0.AddMinutes(1)));
    }

    [Fact]
    public void WaitSurvivesAServiceRestart()
    {
        Updated();

        AutoStatus restarted = Status(); // 파일에서 다시 읽음

        Assert.Equal(TimeSpan.FromMinutes(7), AutoRelocator.PostUpdateWait(restarted, Delay(10), "a", T0.AddMinutes(3)));
    }

    [Fact]
    public void FirstRunRecordedReplanIsNotDelayed()
    {
        AutoStatus status = WithPlayed(("a", 1));
        status.Complete("a", "auto", T0, counted: false, null);
        status.ResetForReplan("a", "first run recorded, planning again");

        Assert.Null(AutoRelocator.PostUpdateWait(status, Delay(10), "a", T0));
    }

    [Fact]
    public void PrewarmerSkipsWhileWaitingAndLogsOncePerDelay()
    {
        var streamer = new FakeStreamer();
        GameSettings g = Games.Make("a", "a.exe");
        var prewarm = new Prewarmer([g], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
        }, _log, null, () => T0);
        prewarm.SetAuto(Delay(10));
        TimeSpan? wait = TimeSpan.FromMinutes(5);
        prewarm.PostUpdateWait = _ => wait;

        PrewarmResult first = prewarm.Run(g, "service start");
        PrewarmResult second = prewarm.Run(g, "trigger");
        IReadOnlyList<PrewarmResult> batch = prewarm.RunServiceStart();

        Assert.Equal((false, Prewarmer.PostUpdateDelay), (first.Ran, first.SkipReason));
        Assert.Equal(Prewarmer.PostUpdateDelay, second.SkipReason);
        Assert.Equal(Prewarmer.PostUpdateDelay, Assert.Single(batch).SkipReason);
        Assert.Empty(streamer.Calls);
        Assert.Single(_log.Lines, l => l.Contains("a: waiting 10 min after update (drive may still be reorganizing)", StringComparison.Ordinal));

        wait = null; // 지연이 끝남
        PrewarmResult done = prewarm.Run(g, "service start");

        Assert.True(done.Ran);
        Assert.Single(streamer.Calls);
    }

    // ---- 설정 ----

    private static AutoSettings ParseAuto(JsonObject auto)
    {
        var root = new JsonObject { ["games"] = new JsonArray(), ["auto"] = auto };
        return SettingsStore.Parse(root.ToJsonString(), Plans).Auto;
    }

    [Fact]
    public void PostUpdateDelayDefaultsToFifteenAndZeroIsAccepted()
    {
        Assert.Equal(15, AutoSettings.Default.PostUpdateDelayMin);
        Assert.Equal(15, ParseAuto([]).PostUpdateDelayMin);
        Assert.Equal(0, ParseAuto(new JsonObject { ["post_update_delay_min"] = 0 }).PostUpdateDelayMin);
        Assert.Equal(25, ParseAuto(new JsonObject { ["post_update_delay_min"] = 25 }).PostUpdateDelayMin);
        Assert.Equal(1440, ParseAuto(new JsonObject { ["post_update_delay_min"] = 1440 }).PostUpdateDelayMin);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    [InlineData(1.5)]
    [InlineData("10")]
    [InlineData(true)]
    public void PostUpdateDelayRejectsOutOfRangeAndNonIntegers(object value)
    {
        JsonNode? node = JsonValue.Create(value);
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["post_update_delay_min"] = node }));

        Assert.Contains("auto.post_update_delay_min", e.Message, StringComparison.Ordinal);
    }
}
