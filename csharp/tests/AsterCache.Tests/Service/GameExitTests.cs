using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class GameExitTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    private GameExitTracker Tracker() => new(() => _now);

    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ExitIsReportedOnceWhenAllExeNamesStayGoneThroughTheGrace()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "cod.exe", "codlauncher.exe");

        Assert.Empty(tracker.Poll([game], Names("cod.exe")));
        Assert.Empty(tracker.Poll([game], Names("codlauncher.exe"))); // 한 exe 라도 남아 있으면 실행 중
        Assert.Empty(tracker.Poll([game], Names())); // 처음 사라진 틱: 유예 시작
        _now += GameExitTracker.Grace;
        Assert.Equal([game], tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names()));
    }

    [Fact]
    public void UnregisteredProcessUnderGameFolderNeverDelaysTheExit()
    {
        var tracker = Tracker();
        var game = Games.Make("sp", "cod.exe");

        _ = tracker.Poll([game], Names("cod.exe"));
        Assert.Empty(tracker.Poll([game], Names("bootstrapper.exe"))); // 폴더 아래 미등록 상주 프로세스는 이름 목록에 있어도 등록 이름이 아님
        _now += GameExitTracker.Grace;
        Assert.Equal([game], tracker.Poll([game], Names("bootstrapper.exe")));
    }

    [Fact]
    public void LauncherToBodyGapWithinTheGraceIsNotAnExit()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "launcher.exe", "body.exe");

        _ = tracker.Poll([game], Names("launcher.exe"));
        Assert.Empty(tracker.Poll([game], Names())); // 런처는 끝났고 본체는 아직
        _now += TimeSpan.FromSeconds(30);
        Assert.Empty(tracker.Poll([game], Names("body.exe"))); // 유예 안에 다시 뜸
        Assert.Empty(tracker.Poll([game], Names("body.exe")));
        Assert.Empty(tracker.Poll([game], Names())); // 다시 사라지면 유예를 처음부터
        _now += TimeSpan.FromSeconds(30);
        Assert.Empty(tracker.Poll([game], Names()));
        _now += TimeSpan.FromSeconds(30);
        Assert.Single(tracker.Poll([game], Names()));
    }

    [Fact]
    public void GameNeverSeenRunningIsNotReportedAndRestartsCountAgain()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "cod.exe");

        Assert.Empty(tracker.Poll([game], Names()));
        _now += GameExitTracker.Grace;
        Assert.Empty(tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names("COD.EXE")));
        Assert.Empty(tracker.Poll([game], Names()));
        _now += GameExitTracker.Grace;
        Assert.Single(tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names("cod.exe")));
        Assert.Empty(tracker.Poll([game], Names()));
        _now += GameExitTracker.Grace;
        Assert.Single(tracker.Poll([game], Names()));
    }

    [Fact]
    public void GamesAreTrackedIndependentlyAndRemovedGamesAreForgotten()
    {
        var tracker = Tracker();
        var a = Games.Make("a", "cod.exe");
        var b = Games.Make("b", "other.exe");

        _ = tracker.Poll([a, b], Names("cod.exe", "other.exe"));
        Assert.Empty(tracker.Poll([a, b], Names("other.exe")));
        _now += GameExitTracker.Grace;
        Assert.Equal([a], tracker.Poll([a, b], Names("other.exe")));

        _ = tracker.Poll([a, b], Names("cod.exe", "other.exe"));
        _ = tracker.Poll([a], Names("cod.exe")); // b 가 설정에서 빠짐
        _ = tracker.Poll([a, b], Names());
        _now += GameExitTracker.Grace;
        Assert.DoesNotContain(b, tracker.Poll([a, b], Names()));
    }
}
