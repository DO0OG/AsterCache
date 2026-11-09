using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 게임 표시 이름 (내부 키는 그대로, 화면만 바뀜)과 상태 문구 매핑. 전역 언어를 바꾸므로 Language 묶음에서 돌립니다.
[Collection("Language")]
public sealed class DisplayNameAndReasonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_disp_" + Guid.NewGuid().ToString("N"));

    public DisplayNameAndReasonTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        L.Current = L.Ko;
        Directory.Delete(_dir, true);
    }

    private GameInput Input(string name) => new(
        name, [name + ".exe"], Path.Combine(_dir, "plans", name + ".prefetch.json"), Path.Combine(_dir, "plans", name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "GAMES");

    private static TrayViewModel Tray(ConfigEditor editor) =>
        new(editor, new TrayViewModelTests.FakeService(), new TrayViewModelTests.FakeStreamer(), new TrayViewModelTests.FakeShell(), new TrayViewModelTests.MemoryLog());

    [Fact]
    public void DisplayNameOrderIsLanguageNameThenLauncherNameThenKeyWithSpaces()
    {
        var plain = new GameSettings("Call_of_Duty", ["a.exe"], "t", "p", @"C:\g", [@"C:\g"], "L", 60);
        GameSettings launcher = plain with { DisplayName = "Call of Duty: Modern Warfare" };
        GameSettings local = launcher with { DisplayNames = new Dictionary<string, string> { ["ko"] = "콜 오브 듀티" } };

        Assert.Equal("Call of Duty", plain.DisplayFor("ko"));
        Assert.Equal("Call of Duty: Modern Warfare", launcher.DisplayFor("en"));
        Assert.Equal("콜 오브 듀티", local.DisplayFor("ko"));
        Assert.Equal("Call of Duty: Modern Warfare", local.DisplayFor("en")); // en 항목이 없으면 런처 이름
        Assert.Equal("Call_of_Duty", local.Name); // 내부 키 불변
    }

    [Fact]
    public void ViewModelShowsLauncherNameOrSpacedKeyAndKeepsTheInternalKeys()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Input("LeagueOfLegens"));
        editor.Add(Input("FC_ONLINE_by_EA_SPORTS"));
        TrayViewModel vm = Tray(editor);
        vm.Refresh();
        vm.LauncherNames = new Dictionary<string, string> { ["LeagueOfLegens"] = "League of Legends" }; // 서비스가 상태 파일에 남긴 이름

        Assert.Equal("League of Legends", vm.DisplayName("leagueoflegens"));
        Assert.Equal("FC ONLINE by EA SPORTS", vm.DisplayName("FC_ONLINE_by_EA_SPORTS"));
        Assert.Equal("Not Registered", vm.DisplayName("Not_Registered"));
        Assert.Equal(["LeagueOfLegens", "FC_ONLINE_by_EA_SPORTS"], vm.Games);
    }

    [Fact]
    public void LanguageSwitchChangesTheShownNameOnly()
    {
        string plans = Path.Combine(_dir, "plans").Replace('\\', '/');
        string root = Path.Combine(_dir, "Games").Replace('\\', '/');
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$$"""
            {"games": [{"name": "Valorant_KR", "exe_names": ["v.exe"], "trace": "{{{plans}}}/v.prefetch.json", "plan": "{{{plans}}}/v.plan.json",
              "origin_root": "{{{root}}}/v", "allowed_roots": ["{{{root}}}"], "volume_label": "G",
              "display_name": "Valorant", "display_names": {"ko": "발로란트", "en": "VALORANT"}}]}
            """);
        TrayViewModel vm = Tray(new ConfigEditor(_dir));
        vm.Refresh();

        L.Current = L.Ko;
        Assert.Equal("발로란트", vm.DisplayName("Valorant_KR"));
        L.Current = L.En;
        Assert.Equal("VALORANT", vm.DisplayName("Valorant_KR"));
        Assert.Equal("Valorant_KR", vm.Games[0]);
    }

    [Fact]
    public void DisplayKeysAreOptionalAndValidated()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Input("Plain"));
        editor.Add(Input("Shown"));
        Assert.DoesNotContain("display_name", File.ReadAllText(editor.ConfigPath));
        string text = File.ReadAllText(editor.ConfigPath).Replace("\"name\": \"Shown\",", "\"name\": \"Shown\", \"display_name\": \"Shown Name\",");
        File.WriteAllText(editor.ConfigPath, text);
        Assert.Equal("Shown Name", editor.Load()!.Games[1].DisplayName);

        foreach (string bad in new[]
        {
            "\"display_name\": \"\"", "\"display_name\": 5", "\"display_name\": \"   \"", "\"display_name\": \"a\u200Bb\"",
            "\"display_names\": {\"fr\": \"x\"}", "\"display_names\": {\"ko\": \"a\\nb\"}", "\"display_names\": []",
        })
        {
            string changed = text.Replace("\"display_name\": \"Shown Name\"", bad);
            Assert.NotEqual(text, changed);
            File.WriteAllText(editor.ConfigPath, changed);
            Assert.Contains("display_name", Assert.Throws<SettingsException>(() => editor.Load()).Message, StringComparison.Ordinal); // 문법 오류가 아니라 표시 이름 규칙에 걸려야 함
        }
    }

    [Theory]
    [InlineData("daily write budget reached", "오늘 쓰기 한도 다 씀")]
    [InlineData("drive recovering after relocation", "드라이브 안정화 중")]
    [InlineData("prewarmed, daily limit reached", "오늘 재배치 횟수 다 씀")]
    public void KnownEnglishReasonsBecomeShortKoreanText(string detail, string expected)
    {
        var game = new AutoGame("auto", null, "waiting_idle", detail);

        Assert.Equal(expected, AutoStatusFile.ReasonText(detail, "ko"));
        Assert.Contains(expected, AutoStatusFile.StageText(game, "ko"));
        Assert.DoesNotContain("relocation", AutoStatusFile.StageText(game, "ko"));
        Assert.StartsWith("waiting to relocate (", AutoStatusFile.StageText(game, "en"));
    }

    [Fact]
    public void NewHoldReasonsShowShortTextInKoAndEnAndUnknownStaysVerbatim()
    {
        var replan = new AutoGame("auto", null, "discovered", "game updated, will trace again on next run");
        Assert.Equal("게임 갱신됨, 다음 실행에서 다시 수집", AutoStatusFile.HoldReason(replan, "ko"));
        Assert.Equal("game updated, tracing again next run", AutoStatusFile.HoldReason(replan, "en"));
        Assert.Null(AutoStatusFile.HoldReason(new AutoGame("auto", null, "discovered", "something new"), "ko"));

        var dry = new AutoGame("auto", null, "waiting_idle", "dry-run cancelled (disk busy or game start), will retry");
        Assert.Equal("시험 실행 취소 (디스크 사용 중이거나 게임 시작), 다시 시도", AutoStatusFile.HoldReason(dry, "ko"));
        Assert.Equal("dry run cancelled (disk busy or game start), will retry", AutoStatusFile.HoldReason(dry, "en"));

        var verify = new AutoGame("auto", null, "waiting_idle", "verification deferred (game start or disk busy), will retry");
        Assert.Equal("확인 미룸 (게임 시작 또는 디스크 사용 중), 다시 시도", AutoStatusFile.HoldReason(verify, "ko"));
        Assert.Equal("verification deferred (game start or disk busy), will retry", AutoStatusFile.HoldReason(verify, "en"));
        Assert.Null(AutoStatusFile.HoldReason(new AutoGame("auto", null, "waiting_idle", "something new"), "ko")); // 대기 중 모르는 사유는 원문을 보이지 않음 (기존 동작)
    }

    [Fact]
    public void KeptTraceReplanReasonsAreNotShownAsTraceAgain()
    {
        var kept = new AutoGame("auto", null, "discovered", "game updated, will trace again on next run; trace kept, planning again");
        Assert.Equal("게임 갱신됨, 학습 유지하고 다시 계획 중", AutoStatusFile.HoldReason(kept, "ko"));
        Assert.Equal("game updated, learning kept, replanning", AutoStatusFile.HoldReason(kept, "en"));

        var unchanged = new AutoGame("auto", null, "discovered", "game updated, will trace again on next run; unchanged files kept, planning again");
        Assert.Equal("게임 갱신됨, 바뀌지 않은 파일 학습 유지하고 다시 계획 중", AutoStatusFile.HoldReason(unchanged, "ko"));
        Assert.Equal("game updated, unchanged files kept, replanning", AutoStatusFile.HoldReason(unchanged, "en"));
    }

    [Fact]
    public void UnknownReasonsStayVerbatimAndVerdictsAreMapped()
    {
        Assert.Null(AutoStatusFile.ReasonText("something new", "ko"));
        Assert.Equal("자동: G [실패] (something new)", AutoStatusFile.Line("G", new AutoGame("auto", null, "failed", "something new"), "ko"));
        Assert.Equal("자동: G [실패] (데이터 검사 실패 — data check failed: boom)", AutoStatusFile.Line("G", new AutoGame("auto", null, "failed", "data check failed: boom"), "ko"));
        Assert.Equal("자동: G [실패] (데이터 검사 실패)", AutoStatusFile.Line("G", new AutoGame("auto", null, "failed", "data check failed"), "ko"));
        Assert.Equal("완료", AutoStatusFile.StageText(new AutoGame("auto", null, "done", "daily limit reached"), "ko")); // 완료 단계에는 사유를 붙이지 않음
        L.Current = L.En;
        Assert.Equal("needs resuming", ManageViewModel.VerdictText("resume-needed"));
        Assert.Equal("verification error", ManageViewModel.VerdictText("error"));
        L.Current = L.Ko;
        Assert.Equal("연속", ManageViewModel.VerdictText("contiguous"));
        Assert.Equal("확인 오류", ManageViewModel.VerdictText("error"));
        Assert.Equal("?", ManageViewModel.VerdictText("?"));
    }
}
