using AsterCache.Core.Settings;
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
    public void RegisteredNamesKeepTheGameAliveAndUnregisteredHelperDoesNotExtendGrace()
    {
        var tracker = Tracker();
        var game = Games.Make("game", "body.exe");

        Assert.Empty(tracker.Poll([game], Names("body.exe")));
        Assert.Empty(tracker.Poll([game], Names("unregistered.exe")));
        _now += GameExitTracker.Grace - TimeSpan.FromMilliseconds(1);
        Assert.Empty(tracker.Poll([game], Names()));
        _now += TimeSpan.FromMilliseconds(1);

        Assert.Equal([game], tracker.Poll([game], Names("unregistered.exe")));
    }

    [Fact]
    public void UnregisteredHelperAloneDoesNotStartAnUnobservedGame()
    {
        var tracker = Tracker();
        var game = Games.Make("game", "body.exe");
        Assert.Empty(tracker.Poll([game], Names()));
        _now += GameExitTracker.Grace + TimeSpan.FromSeconds(1);
        Assert.Empty(tracker.Poll([game], Names()));
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

    [Fact]
    public void GraceNamesAreOnlyTheGamesWaitingForTheGraceAndClearWhenTheyReturnOrExit()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "cod.exe");
        var other = Games.Make("other", "other.exe");

        _ = tracker.Poll([game, other], Names("cod.exe", "other.exe"));
        Assert.Empty(tracker.GraceNames);
        _ = tracker.Poll([game, other], Names("other.exe"));
        Assert.Equal(["cod"], tracker.GraceNames);
        _ = tracker.Poll([game, other], Names("cod.exe", "other.exe")); // 유예 안에 다시 뜸
        Assert.Empty(tracker.GraceNames);

        _ = tracker.Poll([game, other], Names("other.exe"));
        _now += GameExitTracker.Grace;
        Assert.Single(tracker.Poll([game, other], Names("other.exe")));
        Assert.Empty(tracker.GraceNames); // 종료가 보고되면 유예도 끝
    }

    [Fact]
    public void GoneSinceKeepsTheFirstObservationTimeUntilTheExitIsReported()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "cod.exe");

        _ = tracker.Poll([game], Names("cod.exe"));
        Assert.Empty(tracker.GoneSince);
        DateTimeOffset first = _now;
        _ = tracker.Poll([game], Names());
        _now += TimeSpan.FromSeconds(30);
        _ = tracker.Poll([game], Names());

        Assert.Equal(first, tracker.GoneSince["cod"]); // 종료 확정 시각이 아니라 처음 본 시각
        _now += GameExitTracker.Grace;
        Assert.Single(tracker.Poll([game], Names()));
        Assert.Empty(tracker.GoneSince);
    }

    [Fact]
    public void GoneNamesIsReadOnlyAndLeavesTheExitDecisionToThePoll()
    {
        var tracker = Tracker();
        var a = Games.Make("a", "a.exe");
        var b = Games.Make("b", "b.exe");
        var never = Games.Make("never", "never.exe");

        _ = tracker.Poll([a, b, never], Names("a.exe", "b.exe"));
        IReadOnlySet<string> gone = tracker.GoneNames([a, b, never], Names("b.exe"));

        Assert.Equal(["a"], gone); // 틱 전에도 이미 사라진 a 만: 실행을 본 적 없는 never 와 떠 있는 b 는 아님
        Assert.Empty(tracker.GraceNames); // 상태를 바꾸지 않음
        Assert.Empty(tracker.Poll([a, b, never], Names("b.exe"))); // 유예는 이 Poll 에서 처음 시작
        _now += GameExitTracker.Grace;
        Assert.Equal([a], tracker.Poll([a, b, never], Names("b.exe")));
    }

    [Fact]
    public void GoneNamesExcludesTheStartingGameAndIgnoresUnregisteredProcesses()
    {
        var tracker = Tracker();
        var a = Games.Make("a", "a.exe");
        var b = Games.Make("b", "b.exe");
        tracker.ObserveStarted(a);
        tracker.ObserveStarted(b);

        Assert.Equal(["a"], tracker.GoneNames([a, b], Names("helper.exe"), except: "b"));
        Assert.Equal(["a", "b"], tracker.GoneNames([a, b], Names("helper.exe")).Order());
        Assert.Empty(tracker.GoneNames([a, b], Names("A.EXE", "b.exe")));
    }

    [Fact]
    public void StartedGameThatPollNeverSawIsUnseenAndTheObservedOneIsNot()
    {
        var tracker = Tracker();
        var brief = Games.Make("brief", "brief.exe");
        var game = Games.Make("game", "game.exe");
        var unseen = new List<GameSettings>();
        tracker.ObserveStarted(brief);
        tracker.ObserveStarted(game);

        Assert.Empty(tracker.Poll([brief, game], Names("game.exe"), unseen)); // brief 는 보이지 않음 (유예 시작), game 은 관측됨
        _now += GameExitTracker.Grace;
        Assert.Equal([brief], tracker.Poll([brief, game], Names(), unseen)); // brief 는 유예가 지나 종료, Poll 이 본 적 없음
        Assert.Equal([brief], unseen);

        unseen.Clear();
        _now += GameExitTracker.Grace;
        Assert.Equal([game], tracker.Poll([brief, game], Names(), unseen));
        Assert.Empty(unseen); // 관측된 game 은 unseen 이 아님
    }

    [Fact]
    public void HeavyWorkGraceUsesTheLongerOfExitGraceAndConfiguredRewarmDelay()
    {
        Assert.Equal(GameExitTracker.Grace, GameExitTracker.HeavyWorkGrace(0));
        Assert.Equal(TimeSpan.FromMinutes(3), GameExitTracker.HeavyWorkGrace(3));
        Assert.Equal(_now + TimeSpan.FromMinutes(3), GameExitTracker.HeavyWorkNotBeforeAt(_now, 3));
    }

    [Fact]
    public void HeavyWorkDelayKeepsTheFirstMissingTimeAfterExitIsConfirmed()
    {
        var tracker = Tracker();
        var game = Games.Make("cod", "cod.exe");
        tracker.ObserveStarted(game);
        _ = tracker.Poll([game], Names());
        DateTimeOffset goneAt = _now;

        _now += GameExitTracker.Grace;
        Assert.Single(tracker.Poll([game], Names()));

        Assert.Equal(goneAt + TimeSpan.FromMinutes(3), tracker.HeavyWorkNotBefore("cod", 3));
    }
}
