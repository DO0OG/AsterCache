using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임별 실행 직후 종료 연속 횟수, 한 번 물러남, 드라이버 단서, 알림 (가짜 시계·가짜 드라이버 목록·메모리 상태 파일).
public sealed class QuickExitGuardTests
{
    private const string Game = "cod";
    private readonly ListLog _log = new();
    private readonly List<(string Game, QuickExitRecord Record)> _notices = [];
    private readonly List<string> _cleared = [];
    private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private IReadOnlyList<KernelDriver>? _drivers = [Driver("ntfs.sys")];
    private string? _state;

    private static KernelDriver Driver(string name) => new(name, @"C:\Drivers\" + name);

    private QuickExitGuard Make() => new(new QuickExitEnv
    {
        Now = () => _now,
        QuickSeconds = () => 90,
        Drivers = () => _drivers,
        Background = action =>
        {
            action();
            return Task.CompletedTask;
        },
        ReadState = () => _state,
        WriteState = text => _state = text,
        Notify = (game, record) => _notices.Add((game, record)),
        ClearNotice = _cleared.Add,
    }, _log);

    // 세션 하나: 시작 → seconds 뒤 종료. (물러났는가, 짧은 종료로 셌는가)
    private (bool BackOff, bool Quick) Run(QuickExitGuard guard, int seconds)
    {
        bool backOff = guard.BeginSession(Game);
        _now = _now.AddSeconds(seconds);
        return (backOff, guard.EndSession(Game));
    }

    private bool Logged(string text) => _log.Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void FirstQuickExitOnlyCountsAndLogsOneInfoLine()
    {
        QuickExitGuard guard = Make();

        (bool backOff, bool quick) = Run(guard, 52);

        Assert.False(backOff);
        Assert.True(quick);
        Assert.Contains($"Info: {Game}: exited quickly (52s)", _log.Lines);
        Assert.Empty(_notices);
    }

    [Fact]
    public void RestartWithinTenMinutesBacksOffOnceAndTheThirdRunIsNormal()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _now = _now.AddMinutes(2);

        bool second = guard.BeginSession(Game);
        Assert.True(second);
        Assert.True(guard.IsBackingOff(Game));
        Assert.True(Logged("cod: backing off for this run (quick exit 1 in a row, last 52s, 2 min ago): no start stream, memory yield or trace"));
        _now = _now.AddSeconds(9);
        Assert.True(guard.EndSession(Game)); // 물러난 실행도 짧게 끝남
        _now = _now.AddMinutes(1);

        (bool third, _) = Run(guard, 20);

        Assert.False(third); // 연속당 한 번만
        Assert.False(guard.IsBackingOff(Game));
    }

    [Fact]
    public void WouldBackOffAnswersWithoutOpeningASessionOrUsingTheOneBackOffPerStreak()
    {
        QuickExitGuard guard = Make();
        Assert.False(guard.WouldBackOff(Game)); // 기록이 없음
        _ = Run(guard, 52);
        _now = _now.AddMinutes(1);

        Assert.True(guard.WouldBackOff(Game));
        Assert.True(guard.WouldBackOff(Game)); // 몇 번을 물어도 소비되지 않음
        Assert.False(guard.IsBackingOff(Game)); // 세션은 열리지 않음
        Assert.True(guard.BeginSession(Game)); // 실제 시작이 여전히 물러남
        _now = _now.AddMinutes(11);
        Assert.False(guard.WouldBackOff("other"));
    }

    [Fact]
    public void StartedAgainSplitsOnThePreviousRunButStoresTheNewStartForTheNextCheck()
    {
        QuickExitGuard guard = Make();
        DateTimeOffset start = _now;
        _ = guard.BeginSession(Game, "a.exe", 1, start);

        Assert.False(guard.StartedAgain(Game, "a.exe", 2, start.AddSeconds(2), start.AddSeconds(5), allowSplit: true, out bool continued)); // 앞 실행 2초: 잇고 새 pid 시작은 5초에 저장
        Assert.True(continued);
        Assert.True(guard.StartedAgain(Game, "a.exe", 3, start.AddSeconds(11), start.AddSeconds(12), allowSplit: true, out continued)); // 앞 실행 6초 (5초에서 11초): 나눔
        Assert.False(continued);
    }

    [Fact]
    public void RestartAfterTenMinutesRunsAsUsual()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _now = _now.AddMinutes(11);

        Assert.False(guard.BeginSession(Game));
        Assert.False(guard.IsBackingOff(Game));
    }

    [Fact]
    public void LaterRunsOfTheSameSessionKeepTheBackOffDecisionWithoutANewOne()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _now = _now.AddMinutes(1);
        Assert.True(guard.BeginSession(Game));

        Assert.True(guard.IsBackingOff(Game)); // 같은 세션의 후속 exe 는 새 세션이 아니라 물음만 함
        Assert.False(guard.IsBackingOff("other"));
    }

    [Fact]
    public void BackedOffRunThatExitsQuicklyIsRecordedAsExitingToo()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _now = _now.AddMinutes(1);

        (bool backOff, bool quick) = Run(guard, 9);

        Assert.True(backOff);
        Assert.True(quick);
        (_, QuickExitRecord record) = Assert.Single(_notices);
        Assert.Equal((2, BackedOffResult.Exited), (record.Count, record.BackedOff));
        Assert.Equal([52, 9], record.RunSeconds);
        Assert.True(Logged("quick exit 2 in a row (runs 52s, 9s); backed-off run: exited too;"));
    }

    [Fact]
    public void BackedOffRunThatSurvivesIsWarnedAndTheNoticeStaysUntilANormalRun()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _cleared.Clear();
        _now = _now.AddMinutes(1);

        (bool backOff, bool quick) = Run(guard, 400);

        Assert.True(backOff);
        Assert.False(quick);
        Assert.Contains($"Warn: {Game}: launched after the service backed off (400s); the earlier quick exit(s) may involve the service, or the program may restart itself", _log.Lines);
        Assert.Equal(BackedOffResult.Launched, Assert.Single(_notices).Record.BackedOff);
        Assert.Empty(_cleared);

        _ = Run(guard, 600); // 물러나지 않은 정상 세션

        Assert.Equal([Game], _cleared);
    }

    [Fact]
    public void RunsBetweenTheLimitsNeitherCountNorReset()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _now = _now.AddMinutes(11);

        (bool backOff, bool quick) = Run(guard, 150);
        _now = _now.AddMinutes(11);

        Assert.False(backOff);
        Assert.False(quick); // 90초~5분은 세지도 되돌리지도 않음
        Assert.False(guard.BeginSession(Game));
        _now = _now.AddSeconds(30);
        Assert.True(guard.EndSession(Game));
        Assert.True(Logged("quick exit 2 in a row (runs 52s, 30s)"));
    }

    [Fact]
    public void SecondQuickExitFillsTheNoticeAndANormalRunClearsIt()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _ = Run(guard, 9);

        (string game, QuickExitRecord record) = Assert.Single(_notices);
        Assert.Equal((Game, 2), (game, record.Count));
        Assert.Single(_log.Lines, l => l.StartsWith("Warn: cod: quick exit 2 in a row", StringComparison.Ordinal));

        _ = Run(guard, 600);

        Assert.Equal([Game], _cleared.Distinct());
        Assert.False(guard.BeginSession(Game)); // 연속 횟수도 0
    }

    [Fact]
    public void OnlyDriversThatAppearedSinceTheLastNormalRunAreReported()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 600); // 정상 실행: ntfs.sys 만 올라와 있음
        _drivers = [Driver("ntfs.sys"), Driver("WinDivert64.sys")];
        _ = Run(guard, 52);
        _ = Run(guard, 9);

        QuickExitRecord record = Assert.Single(_notices).Record;

        Assert.Equal(["WinDivert64.sys"], record.NewDrivers);
        Assert.True(Logged(@"new kernel drivers since the last normal run: WinDivert64.sys (C:\Drivers\WinDivert64.sys)"));
    }

    [Fact]
    public void WithoutANormalRunOrADriverListTheCluesAreUnknownButTheRestWorks()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        _ = Run(guard, 9);
        Assert.Null(Assert.Single(_notices).Record.NewDrivers);
        Assert.True(Logged("new kernel drivers since the last normal run: unknown (no normal run recorded)"));

        _ = Run(guard, 600); // 목록 조회가 실패한 채로 정상 실행
        _drivers = null;
        _ = Run(guard, 52);
        _ = Run(guard, 9);

        Assert.True(Logged("new kernel drivers since the last normal run: unavailable"));
    }

    [Fact]
    public void CountAndLastNormalDriversSurviveAServiceRestart()
    {
        QuickExitGuard first = Make();
        _ = Run(first, 600);
        _ = Run(first, 52);
        _drivers = [Driver("ntfs.sys"), Driver("new.sys")];

        QuickExitGuard restarted = Make(); // 같은 상태 파일로 다시 만듦
        _ = Run(restarted, 9);

        QuickExitRecord record = Assert.Single(_notices).Record;
        Assert.Equal(2, record.Count);
        Assert.Equal(["new.sys"], record.NewDrivers);
    }

    [Fact]
    public void BrokenStateFileStartsEmpty()
    {
        _state = "{not json";

        QuickExitGuard guard = Make();

        Assert.False(guard.BeginSession(Game));
    }

    [Fact]
    public void LateExitFromBeforeTheOpenSessionDoesNotCloseIt()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 52);
        DateTimeOffset oldExit = _now;
        _now = _now.AddMinutes(1);
        Assert.True(guard.BeginSession(Game, "cod.exe", 2, _now));

        Assert.False(guard.EndSession(Game, oldExit));
        Assert.True(guard.IsBackingOff(Game));
    }

    [Fact]
    public void ExitDriverLookupFailureDoesNotFallBackToTheStartSnapshot()
    {
        QuickExitGuard guard = Make();
        _ = Run(guard, 600); // 기준 실행에 ntfs.sys 만 기록

        _now = _now.AddMinutes(1);
        Assert.False(guard.BeginSession(Game, "cod.exe", 2, _now));
        _now = _now.AddSeconds(52);
        Assert.True(guard.EndSession(Game, _now));

        _now = _now.AddMinutes(1);
        _drivers = [Driver("ntfs.sys"), Driver("new.sys")];
        Assert.True(guard.BeginSession(Game, "cod.exe", 3, _now)); // 새 세션의 시작 목록에 new.sys 가 있음
        _drivers = null; // 종료 시 조회 실패: 시작 목록으로 대체하면 안 됨
        _now = _now.AddSeconds(9);
        Assert.True(guard.EndSession(Game, _now));

        QuickExitRecord notice = Assert.Single(_notices).Record;
        Assert.Null(notice.NewDrivers);
        Assert.True(Logged("new kernel drivers since the last normal run: unavailable"));
    }

    [Fact]
    public async Task DriverEnumerationDoesNotHoldTheGuardLock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var env = new QuickExitEnv
        {
            Now = () => _now,
            Drivers = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return _drivers;
            },
        };
        QuickExitGuard guard = new(env, _log);

        _ = guard.BeginSession(Game);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> backingOff = Task.Run(() => guard.IsBackingOff(Game));
        Assert.False(await backingOff.WaitAsync(TimeSpan.FromSeconds(1)));
        release.TrySetResult();
    }
}
