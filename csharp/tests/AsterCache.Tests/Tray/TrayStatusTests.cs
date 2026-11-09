using AsterCache.Core.I18n;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 작업표시줄 상태 4종·툴팁·메뉴 글을 만드는 순수 함수. 전역 언어를 바꾸므로 Language 묶음에서 돌립니다.
[Collection("Language")]
public sealed class TrayStatusTests : IDisposable
{
    public void Dispose() => L.Current = L.Ko;

    private static AutoStatusFile Auto(params (string Name, string Stage, string? Detail)[] games) =>
        new(games.ToDictionary(g => g.Name, g => new AutoGame("auto", null, g.Stage, g.Detail), StringComparer.OrdinalIgnoreCase), []);

    private static readonly string[] Three = ["a", "b", "c"];

    [Fact]
    public void ToneFollowsTheDecidedPriorityStoppedThenAttentionThenBusyThenOk()
    {
        AutoStatusFile mixed = Auto(("a", "done", "prewarmed"), ("b", "tracing", null), ("c", "waiting_idle", "daily limit reached"));
        Assert.Equal(StatusTone.Gray, TrayStatus.Summarize(ServiceState.Stopped, Three, mixed).Tone);
        Assert.Equal(StatusTone.Gray, TrayStatus.Summarize(ServiceState.NotInstalled, Three, mixed).Tone);
        Assert.Equal(StatusTone.Gray, TrayStatus.Summarize(ServiceState.Unknown, Three, mixed).Tone);
        Assert.Equal(StatusTone.Warn, TrayStatus.Summarize(ServiceState.Running, Three, mixed).Tone); // 진행 중이어도 보류가 있으면 주의
        Assert.Equal(StatusTone.Warn, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "failed", "x"))).Tone);
        Assert.Equal(StatusTone.Warn, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "skipped", "x"))).Tone);
        Assert.Equal(StatusTone.Busy, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "relocating", null))).Tone);
        Assert.Equal(StatusTone.Busy, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "done", "prewarming 1/2 MiB"))).Tone);
        Assert.Equal(StatusTone.Ok, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "done", "prewarmed"))).Tone);
        Assert.Equal(StatusTone.Ok, TrayStatus.Summarize(ServiceState.Running, ["a"], null).Tone); // 기록이 아직 없어도 서비스가 돌면 정상
        Assert.Equal(StatusTone.Ok, TrayStatus.Summarize(ServiceState.Running, [], Auto(("old", "failed", "x"))).Tone); // 등록 게임 0 이면 옛 기록은 세지 않음
        Assert.Equal(StatusTone.Busy, TrayStatus.Summarize(ServiceState.Pending, ["a"], null).Tone);
        Assert.Equal(StatusTone.Warn, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "waiting_idle", "daily limit reached"))).Tone); // 사유 있는 대기는 보류
        Assert.Equal(StatusTone.Busy, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "waiting_idle", null))).Tone); // 사유 없는 대기는 평소 단계라 작업 중
        Assert.Equal(StatusTone.Ok, TrayStatus.Summarize(ServiceState.Running, ["a"], Auto(("a", "discovered", null))).Tone); // 발견만 된 상태는 어느 쪽도 아님
        Assert.Equal(StatusTone.Warn, TrayStatus.Summarize(ServiceState.Running, ["a"], null, problem: true).Tone);
    }

    [Fact]
    public void TooltipShowsCountsInBothLanguagesAndIgnoresGamesThatAreNotRegistered()
    {
        AutoStatusFile auto = Auto(("a", "done", "prewarmed"), ("b", "tracing", null), ("c", "waiting_idle", "daily limit reached"), ("gone", "failed", "x"));
        TraySummary s = TrayStatus.Summarize(ServiceState.Running, Three, auto);
        Assert.Equal("AsterCache: 실행 중 · 게임 3 · 예열됨 1 · 진행 1 · 보류 1", TrayStatus.Tip(s));
        Assert.Equal("실행 중 · 게임 3", TrayStatus.Summarize(ServiceState.Running, Three, null).Line);
        Assert.Equal("정지됨 · 등록된 게임 없음", TrayStatus.Summarize(ServiceState.Stopped, [], auto).Line);
        L.Current = L.En;
        Assert.Equal("AsterCache: Running · games 3 · prewarmed 1 · working 1 · on hold 1", TrayStatus.Tip(TrayStatus.Summarize(ServiceState.Running, Three, auto)));
    }

    [Fact]
    public void TooltipAddsRecentMessagesAndNeverExceedsTheWin32Limit()
    {
        TraySummary s = TrayStatus.Summarize(ServiceState.Running, Three, null);
        Assert.Equal("AsterCache: 실행 중 · 게임 3\n예열 완료: g", TrayStatus.Tip(s, string.Empty, "예열 완료: g"));
        string tip = TrayStatus.Tip(s, new string('가', 200), "두 번째");
        Assert.Equal(TrayStatus.TipMax, tip.Length);
        Assert.EndsWith("…", tip);
        Assert.StartsWith("AsterCache: 실행 중 · 게임 3\n", tip);
    }

    [Fact]
    public void MessagesExpireAfterSixtySeconds()
    {
        DateTimeOffset at = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal("끝", TrayStatus.Recent("끝", at, at.AddSeconds(60)));
        Assert.Equal(string.Empty, TrayStatus.Recent("끝", at, at.AddSeconds(61)));
        Assert.Equal(string.Empty, TrayStatus.Recent("끝", DateTimeOffset.MinValue, at));
    }

    [Fact]
    public void MenuTextsAreBuiltFromParts()
    {
        Assert.Equal("Name · 재배치 대기 · 예열됨", TrayStatus.GameItem("Name", "재배치 대기", "예열됨"));
        Assert.Equal("Name", TrayStatus.GameItem("Name", string.Empty, string.Empty));
        Assert.Equal(("Btn_ServiceStop", true), TrayStatus.ServiceAction(ServiceState.Running));
        Assert.Equal(("Btn_ServiceStart", true), TrayStatus.ServiceAction(ServiceState.Stopped));
        Assert.Equal(("Btn_ServiceStart", false), TrayStatus.ServiceAction(ServiceState.Pending));
        Assert.Equal(("Btn_ServiceStart", false), TrayStatus.ServiceAction(ServiceState.NotInstalled));
        Assert.Equal(StatusTone.Ok, TrayStatus.StageTone("done", null));
        Assert.Equal(StatusTone.Warn, TrayStatus.StageTone("failed", "x"));
        Assert.Equal(StatusTone.Warn, TrayStatus.StageTone("waiting_idle", "daily limit reached"));
        Assert.Equal(StatusTone.Busy, TrayStatus.StageTone("waiting_idle", null));
        Assert.Equal(StatusTone.Gray, TrayStatus.PrewarmTone("none"));
        Assert.Equal(StatusTone.Busy, TrayStatus.PrewarmTone("warming"));
    }

    [Fact]
    public void StageColumnShowsTheMainStateWithOneExtraAndTheCountOfTheRest()
    {
        Assert.Equal("완료", AutoStatusFile.StageShort(new AutoGame("auto", null, "done", null), "ko"));
        Assert.Equal("완료 · 재배치됨(프로필)", AutoStatusFile.StageShort(new AutoGame("auto", null, "done", "relocated (profile)"), "ko"));
        Assert.Equal("done · relocated (profile) +1", AutoStatusFile.StageShort(new AutoGame("auto", null, "done", "relocated (profile), prewarmed"), "en"));
        Assert.Equal("첫 실행: 학습 중 · 선행 읽기 512 MiB", AutoStatusFile.StageShort(new AutoGame("auto", null, "learning", "512 MiB read ahead"), "ko"));
    }

    [Fact]
    public void PrewarmReasonsAndResultsAreTranslatedAndUnknownOnesStayRaw()
    {
        Assert.Equal("여유 메모리 부족", ManageViewModel.PrewarmReasonText("low memory (available 100 MiB < zone 900 MiB + reserve 2048 MiB)"));
        Assert.Equal("재시도 대기 중 (12분 남음)", ManageViewModel.PrewarmReasonText("cooldown (12 min left)"));
        Assert.Equal("프로필 없음, 트레이스 필요", ManageViewModel.PrewarmReasonText("no profile; trace needed"));
        Assert.Equal("프로필 없음", ManageViewModel.PrewarmReasonText("no profile"));
        Assert.Equal("오류로 중단 (IOException)", ManageViewModel.PrewarmReasonText("failed: IOException"));
        Assert.Equal("something new", ManageViewModel.PrewarmReasonText("something new"));
        Assert.Equal(("성공", "건너뜀", "실패", "?"), (ManageViewModel.PrewarmResultText("ok"), ManageViewModel.PrewarmResultText("skipped"), ManageViewModel.PrewarmResultText("failed"), ManageViewModel.PrewarmResultText(null)));
        L.Current = L.En;
        Assert.Equal("not enough free memory", ManageViewModel.PrewarmReasonText("low memory (x)"));
    }
}
