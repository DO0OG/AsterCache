using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 안 로딩 판정: 시작·끝 규칙 (design_w82 2.3), 상한, 판정 상태 (로그는 바뀔 때만 한 줄), 입력 공급 (서비스 자신의 읽기를 뺌).
public sealed class PlayPhaseTests
{
    private const long Mib = 1L << 20;

    private static LoadingSample S(double t, long driveMib = 0, int? gpu = null, bool exe = false) => new(t, driveMib * Mib, gpu, exe);

    // 초마다 같은 양을 넣어 from 초부터 to 초까지 (포함) 의 마지막 결과를 돌려줌
    private static LoadStep Feed(LoadDetector d, int from, int to, long driveMib = 0, Func<int, int?>? gpu = null)
    {
        LoadStep step = default;
        for (int t = from; t <= to; t++)
        {
            step = d.Add(S(t, driveMib, gpu?.Invoke(t)));
        }

        return step;
    }

    private static LoadDetector Loading()
    {
        var d = new LoadDetector();
        Assert.Equal(LoadStart.ExeStart, d.Add(S(0, exe: true)).Started);
        return d;
    }

    // ---- 시작 ----

    [Fact]
    public void AnExecutableStartStartsLoadingAndWithoutAnySignalTheStateIsNotLoading()
    {
        var d = new LoadDetector();

        Assert.Equal(LoadState.Unknown, d.State); // 표본이 오기 전
        Assert.Equal(LoadState.NotLoading, Feed(d, 0, 30, driveMib: 1).State);
        LoadStep start = d.Add(S(31, exe: true));
        Assert.Equal((LoadState.Loading, LoadStart.ExeStart), (start.State, start.Started));
        Assert.Null(d.Add(S(32)).Started); // 이미 로딩이라 다음 표본은 새 시작이 아님
    }

    [Fact]
    public void DriveReadsOf80MiBInFiveSecondsStartLoadingAndLessDoesNot()
    {
        var below = new LoadDetector();
        var at = new LoadDetector();

        Assert.Equal(LoadState.NotLoading, Feed(below, 0, 20, driveMib: 15).State); // 5 초에 75 MiB
        LoadStep step = Feed(at, 0, 20, driveMib: 16); // 5 초에 80 MiB

        Assert.Equal(LoadState.Loading, step.State);
        Assert.Equal(80 * Mib, step.DriveBytes);
        Assert.Equal(4.0, at.LoadingSince); // 처음 5 초가 찬 때 (초 0 ~ 4)
    }

    [Fact]
    public void GraphicsMemoryGrowthOf250MiBInTenSecondsStartsLoadingAndASlowerGrowthDoesNot()
    {
        var below = new LoadDetector();
        var at = new LoadDetector();

        Assert.Equal(LoadState.NotLoading, Feed(below, 0, 40, gpu: t => 3000 + (t * 24)).State); // 10 초에 240 MiB
        LoadStep? started = null;
        for (int t = 0; t <= 40 && started is null; t++)
        {
            LoadStep step = at.Add(S(t, gpu: 3000 + (t * 25))); // 10 초에 250 MiB
            started = step.Started is null ? null : step;
        }

        Assert.Equal((LoadStart.Graphics, 250), (started?.Started, started?.GpuRiseMib));
    }

    [Fact]
    public void AZeroOrMissingGraphicsValueNeverCountsAsGrowthSoOnlyTheDiskRuleRuns()
    {
        var d = new LoadDetector();

        Assert.Equal(LoadState.NotLoading, Feed(d, 0, 5, gpu: _ => 0).State); // 카운터가 아직 값을 주지 않음
        Assert.Equal(LoadState.NotLoading, Feed(d, 6, 20, gpu: t => t == 15 ? 4000 : 0).State); // 0 에서 4000 으로 건너뛰어도 기준이 없음
        Assert.Equal(LoadState.NotLoading, Feed(new LoadDetector(), 0, 30).State); // 그래픽 값이 아예 없는 게임
    }

    // ---- 끝 ----

    [Fact]
    public void LoadingEndsWhenReadsStayBelow40MiBAndGraphicsGrowthBelow64MiBForFifteenSeconds()
    {
        LoadDetector d = Loading();

        Assert.Equal(LoadState.Loading, Feed(d, 1, 15).State); // 조용한 상태의 첫 표본이 초 1 이므로 초 15 는 14 초째
        LoadStep end = d.Add(S(16));

        Assert.Equal((LoadState.NotLoading, LoadEnd.Quiet, 16.0), (end.State, end.Ended, end.LoadingSeconds));
    }

    [Fact]
    public void ANoisySecondRestartsTheQuietCount()
    {
        LoadDetector d = Loading();
        _ = Feed(d, 1, 10);

        _ = d.Add(S(11, driveMib: 45)); // 5 초에 45 MiB (40 MiB 이상): 조용하지 않음
        _ = Feed(d, 12, 15); // 읽기 창이 5 초라 45 MiB 가 창 안에 머무는 동안 (초 15 까지) 계속 시끄러움
        _ = Feed(d, 16, 30); // 조용한 상태가 초 16 부터 다시 셈

        Assert.Equal(LoadState.Loading, d.State);
        Assert.Equal(LoadEnd.Quiet, d.Add(S(31)).Ended);
    }

    [Fact]
    public void GraphicsGrowthOf64MiBInTenSecondsKeepsLoadingEvenWithNoReads()
    {
        LoadDetector d = Loading();

        _ = Feed(d, 1, 60, gpu: t => 3000 + (t * 7)); // 10 초에 70 MiB: 시작 문턱 (250) 아래이지만 끝 문턱 (64) 이상

        Assert.Equal(LoadState.Loading, d.State);
        Assert.Equal(LoadState.NotLoading, Feed(d, 61, 100, gpu: _ => 3500).State);
    }

    [Fact]
    public void ALoadingThatNeverGoesQuietEndsAfter240SecondsAndDoesNotStartAgainWhileTheSignalStaysHigh()
    {
        LoadDetector d = Loading();

        LoadStep before = Feed(d, 1, 239, driveMib: 30); // 5 초에 150 MiB: 계속 시끄러움
        LoadStep end = d.Add(S(240, driveMib: 30));

        Assert.Equal(LoadState.Loading, before.State);
        Assert.Equal((LoadState.NotLoading, LoadEnd.TimeLimit, 240.0), (end.State, end.Ended, end.LoadingSeconds));
        Assert.Equal(LoadState.NotLoading, Feed(d, 241, 300, driveMib: 30).State); // 상한 뒤에는 신호가 한 번 꺼질 때까지 다시 시작하지 않음
        Assert.Equal(LoadState.NotLoading, Feed(d, 301, 320).State); // 꺼짐
        Assert.Equal(LoadState.Loading, Feed(d, 321, 330, driveMib: 30).State); // 다시 켜지면 새 로딩
    }

    [Fact]
    public void AnExecutableStartDuringLoadingStartsTheTimeLimitAgainWithoutANewStartStep()
    {
        LoadDetector d = Loading();
        _ = Feed(d, 1, 200, driveMib: 30);

        LoadStep again = d.Add(S(201, driveMib: 30, exe: true));

        Assert.Equal((LoadState.Loading, null), (again.State, again.Started));
        Assert.Equal(LoadState.Loading, Feed(d, 202, 440, driveMib: 30).State); // 처음 로딩이었다면 초 240 에 끝났을 것
        LoadStep end = d.Add(S(441, driveMib: 30));
        Assert.Equal((LoadState.NotLoading, LoadEnd.TimeLimit, 240.0), (end.State, end.Ended, end.LoadingSeconds));
    }

    // ---- 판정 상태와 로그 ----

    [Fact]
    public void TheBoardLogsOnlyWhenTheStateChangesAndNamesWhatWasHeldBack()
    {
        var log = new ListLog();
        var board = new LoadPhaseBoard(log);

        Assert.Equal(LoadState.Unknown, board.StateOf("g"));
        Assert.False(board.Defers("g", "keep round")); // 표본이 없으면 미루지 않음
        _ = board.Add("g", S(0, exe: true));
        Assert.True(board.Defers("g", "keep round"));
        Assert.True(board.Defers("g", "keep round"));
        Assert.True(board.Defers("g", "hold check"));
        for (int t = 1; t <= 16; t++)
        {
            _ = board.Add("g", S(t));
        }

        Assert.False(board.Defers("g", "keep round"));
        Assert.Equal(LoadState.NotLoading, board.StateOf("G")); // 게임 이름은 대소문자를 가리지 않음
        Assert.Equal(["Info: g: loading phase started (executable started)", "Info: g: loading phase ended after 16 s (quiet for 15 s); held back: hold check, keep round"], log.Lines);
    }

    [Fact]
    public void TheStartLineSaysWhichSignalStartedTheLoading()
    {
        var log = new ListLog();
        var board = new LoadPhaseBoard(log);

        for (int t = 0; t <= 4; t++)
        {
            _ = board.Add("d", S(t, driveMib: 20));
        }

        for (int t = 0; t <= 12; t++)
        {
            _ = board.Add("g", S(t, gpu: 3000 + (t * 30)));
        }

        Assert.Equal("Info: d: loading phase started (drive read 80 MiB within 5 s)", log.Lines.ElementAt(0));
        Assert.Equal("Info: g: loading phase started (graphics memory +270 MiB within 10 s)", log.Lines.ElementAt(1));
    }

    [Fact]
    public void ForgettingAGameDuringLoadingLogsTheEndAndNoStateIsLeftToBlockTheService()
    {
        var log = new ListLog();
        var board = new LoadPhaseBoard(log);
        _ = board.Add("g", S(10, exe: true));
        _ = board.Add("g", S(31, driveMib: 100));

        board.Forget("g");
        board.Forget("g");

        Assert.Equal(LoadState.Unknown, board.StateOf("g"));
        Assert.False(board.Defers("g", "keep round"));
        Assert.Equal(["Info: g: loading phase started (executable started)", "Info: g: loading phase ended after 21 s (play ended)"], log.Lines);
    }

    // ---- 입력 공급 ----

    private static PlaySeriesRaw Raw(long serviceRead, int? gpu) =>
        new(null, null, [new ProcessUsage(100, "game.exe", 0, 0, 5 * Mib * 1000), new ProcessUsage(200, "AsterCache.Service.exe", 0, 0, serviceRead)], 200, 7000, null, null, null, gpu, string.Empty);

    private static PlayDiskCounters Disk(long readBytes) => new(1, readBytes, 0, 0, 0, 0);

    [Fact]
    public void TheServicesOwnReadsAreSubtractedFromTheDriveReadsAndTheResultNeverGoesBelowZero()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", board);

        _ = feed.Next([Disk(0)], [Disk(10 * Mib)], 1, Raw(0, null), false); // 첫 초는 서비스 읽기의 기준만 잡음
        _ = feed.Next([Disk(10 * Mib)], [Disk(110 * Mib)], 2, Raw(30 * Mib, null), false); // 드라이브 100 MiB 중 서비스 30 MiB
        _ = feed.Next([Disk(110 * Mib)], [Disk(120 * Mib)], 3, Raw(80 * Mib, null), false); // 서비스 읽기가 드라이브보다 큼 (다른 볼륨 읽기 포함): 0
        LoadStep after = board.Add("g", S(4)); // 지금까지 들어간 표본의 5 초 합은 10 + 70 + 0 = 80 MiB

        Assert.Equal(LoadState.Loading, after.State);
        Assert.Equal(80 * Mib, after.DriveBytes);
    }

    [Fact]
    public void AGameWithoutAGraphicsValueStillGetsTheDiskRuleAndAnUnreadableProcessListFallsBackToTheOwnQuery()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", board);

        LoadState state = feed.Next([Disk(0)], [Disk(200 * Mib)], 1, null, false); // 기록이 꺼진 플레이: 프로세스 목록 없이도 판정이 돎

        Assert.Equal(LoadState.Loading, state);
        Assert.NotNull(ProcessFaults.OwnReadBytes()); // 이 시험 프로세스의 읽기 바이트를 얻을 수 있음 (서비스 자신의 조회와 같은 방법)
    }
}
