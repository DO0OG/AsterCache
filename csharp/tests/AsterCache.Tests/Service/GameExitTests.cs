using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class GameExitTests
{
    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ExitIsReportedOnceWhenAllExeNamesDisappear()
    {
        var tracker = new GameExitTracker();
        var game = Games.Make("cod", "cod.exe", "codlauncher.exe");

        Assert.Empty(tracker.Poll([game], Names("cod.exe")));
        Assert.Empty(tracker.Poll([game], Names("codlauncher.exe"))); // 한 exe 라도 남아 있으면 실행 중
        Assert.Equal([game], tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names()));
    }

    [Fact]
    public void ExitIsHeldWhileProcessUnderGameFolderRemains()
    {
        var tracker = new GameExitTracker();
        var game = Games.Make("sp", "cod.exe");
        bool folderBusy = false;
        Func<GameSettings, bool> underRoot = _ => folderBusy;

        _ = tracker.Poll([game], Names("cod.exe"), underRoot);
        folderBusy = true;
        Assert.Empty(tracker.Poll([game], Names(), underRoot)); // exe 는 사라졌지만 폴더 아래 프로세스가 남아 있음
        folderBusy = false;
        Assert.Equal([game], tracker.Poll([game], Names(), underRoot));
    }

    [Fact]
    public void GameNeverSeenRunningIsNotReportedAndRestartsCountAgain()
    {
        var tracker = new GameExitTracker();
        var game = Games.Make("cod", "cod.exe");

        Assert.Empty(tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names("COD.EXE")));
        Assert.Single(tracker.Poll([game], Names()));
        Assert.Empty(tracker.Poll([game], Names("cod.exe")));
        Assert.Single(tracker.Poll([game], Names()));
    }

    [Fact]
    public void GamesAreTrackedIndependentlyAndRemovedGamesAreForgotten()
    {
        var tracker = new GameExitTracker();
        var a = Games.Make("a", "cod.exe");
        var b = Games.Make("b", "other.exe");

        _ = tracker.Poll([a, b], Names("cod.exe", "other.exe"));
        Assert.Equal([a], tracker.Poll([a, b], Names("other.exe")));

        _ = tracker.Poll([a, b], Names("cod.exe", "other.exe"));
        _ = tracker.Poll([a], Names("cod.exe")); // b 가 설정에서 빠짐
        Assert.DoesNotContain(b, tracker.Poll([a, b], Names()));
    }
}
