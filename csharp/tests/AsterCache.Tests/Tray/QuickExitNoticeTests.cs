using System.Globalization;
using AsterCache.Core.I18n;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 트레이가 서비스의 실행 직후 종료 알림 (auto_status.json 의 quick_exit) 을 읽고 문장으로 만드는 일. 전역 언어를 바꾸므로 Language 묶음에서 돌립니다.
[Collection("Language")]
public sealed class QuickExitNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_quickexit_" + Guid.NewGuid().ToString("N"));

    public QuickExitNoticeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        L.Current = L.Ko;
        Directory.Delete(_dir, true);
    }

    private AutoStatusFile Load(string json)
    {
        string path = Path.Combine(_dir, "auto_status.json");
        File.WriteAllText(path, json);
        return AutoStatusFile.Load(path)!;
    }

    private const string WithNotice =
        "{\"games\":{\"cod\":{\"stage\":\"done\",\"quick_exit\":{\"count\":2,\"run_seconds\":[52,9],\"at\":\"2026-11-15T12:00:00+00:00\",\"backed_off\":\"exited\",\"new_drivers\":[\"WinDivert64.sys\"]}}},"
        + "\"events\":[{\"at\":\"2026-11-15T12:00:00+00:00\",\"game\":\"cod\",\"kind\":\"quick_exit\"}]}";

    [Fact]
    public void QuickExitNoticeIsReadAndPickedAsANewEventOnce()
    {
        AutoStatusFile file = Load(WithNotice);
        var seen = new HashSet<string>();
        DateTimeOffset since = DateTimeOffset.Parse("2026-11-15T11:00:00+00:00", CultureInfo.InvariantCulture);

        AutoGame game = file.Games["cod"];
        AutoNotice notice = Assert.Single(AutoStatusFile.NewEvents(file.Events, since, seen));
        seen.Add(notice.Key);

        Assert.Equal((2, "exited"), (game.QuickExit!.Count, game.QuickExit.BackedOff));
        Assert.Equal(["WinDivert64.sys"], game.QuickExit.NewDrivers);
        Assert.Equal("quick_exit", notice.Kind);
        Assert.Empty(AutoStatusFile.NewEvents(file.Events, since, seen));
    }

    [Fact]
    public void NoticeAndHoldReasonUseTheGameEntryInBothLanguages()
    {
        AutoStatusFile file = Load(WithNotice);
        AutoNotice notice = file.Events.Single();

        L.Current = L.Ko;
        Assert.Equal("cod 이(가) 실행 직후 꺼졌습니다(2회). 마지막 정상 실행 뒤 새로 올라온 드라이버: WinDivert64.sys. AsterCache 가 물러난 실행에서도 꺼졌습니다.", AutoStatusFile.Notice(notice, L.Ko, file.Games["cod"]));
        Assert.Contains("새 드라이버: WinDivert64.sys", AutoStatusFile.HoldReason(file.Games["cod"], L.Ko));
        L.Current = L.En;
        Assert.StartsWith("cod exited right after launch (2 times).", AutoStatusFile.Notice(notice, L.En, file.Games["cod"]), StringComparison.Ordinal);
        Assert.Equal(string.Empty, AutoStatusFile.Notice(notice, L.En, null)); // 알림이 이미 지워졌으면 빈 글
    }

    [Fact]
    public void OldStatusFileWithoutTheFieldStillLoadsAndShowsNoQuickExit()
    {
        AutoStatusFile file = Load("{\"games\":{\"cod\":{\"stage\":\"done\",\"detail\":null}},\"events\":[]}");

        Assert.Null(file.Games["cod"].QuickExit);
        Assert.Null(AutoStatusFile.HoldReason(file.Games["cod"], L.Ko));
    }

    [Fact]
    public void UnknownDriverListIsStatedAsUnknownAndNoneAsNone()
    {
        var unknown = new AutoGame("auto", null, "done", null, QuickExit: new QuickExitNote(3, "none", null));
        var none = unknown with { QuickExit = new QuickExitNote(3, "launched", []) };

        string unknownText = AutoStatusFile.Notice(new AutoNotice(DateTimeOffset.UnixEpoch, "g", "quick_exit"), L.Ko, unknown);
        string noneText = AutoStatusFile.Notice(new AutoNotice(DateTimeOffset.UnixEpoch, "g", "quick_exit"), L.Ko, none);

        Assert.Contains("드라이버: 알 수 없음.", unknownText, StringComparison.Ordinal);
        Assert.Contains("드라이버: 없음. AsterCache 가 물러난 뒤 실행됐습니다. 앞선 종료는 서비스 문제일 수도 있고, 프로그램이 스스로 다시 시작했을 수도 있습니다.", noneText, StringComparison.Ordinal);
    }
}
