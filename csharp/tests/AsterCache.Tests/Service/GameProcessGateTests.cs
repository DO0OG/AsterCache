using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class GameProcessGateTests
{
    private static readonly GameSettings Game = Games.Make("game", "game.exe") with { OriginRoot = @"D:\Games\game" };
    private static readonly GameSettings TwoNames = Games.Make("game", "game.exe", "body.exe") with { OriginRoot = @"D:\Games\game" }; // 런처 + 자동 학습이 더한 본체 이름

    [Fact]
    public void OnlyRegisteredExeNamesCountAsGameRunning()
    {
        Assert.False(DispatchEnv.IsGameRunning(Game, [(1234, "helper.exe")])); // 설치 폴더 아래 미등록 프로세스는 세지 않음
        Assert.True(DispatchEnv.IsGameRunning(Game, [(1234, "helper.exe"), (2345, "GAME.EXE")]));
        Assert.False(DispatchEnv.IsGameRunning(Game, [(Environment.ProcessId, "game.exe")])); // 이 서비스 자신은 제외
    }

    [Fact]
    public void SecondRegisteredNameKeepsTheGameRunningAfterTheLauncherExits()
    {
        Assert.True(DispatchEnv.IsGameRunning(TwoNames, [(1, "game.exe"), (2, "body.exe")]));
        Assert.True(DispatchEnv.IsGameRunning(TwoNames, [(2, "body.exe")])); // 런처 종료, 본체만 남음
        Assert.False(DispatchEnv.IsGameRunning(TwoNames, [])); // 본체 종료: 재개
    }

    [Fact]
    public void ResidentProcessUnderAnotherGamesRootDoesNotBlockGamesOnTheSameDisk()
    {
        GameSettings wall = Games.Make("wall", "widgetui.exe");
        GameSettings other = Games.Make("other", "other.exe");
        IReadOnlyList<(int Pid, string Exe)> processes = [(90, "widget32.exe")]; // 부팅 때부터 상주, 등록 exe 는 실행 중이 아님
        var streamer = new CountingStreamer();
        var prewarm = new Prewarmer([wall, other], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => DispatchEnv.IsGameRunning(g, processes),
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, new ListLog());

        PrewarmResult result = prewarm.Run(other, "test");

        Assert.True(result.Ran);
        Assert.Equal(1, streamer.Calls);
        Assert.Null(DispatchEnv.DescribeBlockers(wall, processes, _ => @"D:\Games\wall\widget32.exe"));
    }

    [Fact]
    public void DescribeBlockersListsAtMostThreeRegisteredProcessesWithoutTheServiceItself()
    {
        GameSettings game = Games.Make("game", "game.exe", "a.exe", "b.exe", "c.exe") with { OriginRoot = @"D:\Games\game" };
        IReadOnlyList<(int Pid, string Exe)> processes =
            [(1, "game.exe"), (Environment.ProcessId, "a.exe"), (2, "helper.exe"), (3, "other.exe"), (4, "a.exe"), (5, "b.exe"), (6, "c.exe")];
        Func<int, string?> image = pid => pid switch
        {
            1 => throw new System.ComponentModel.Win32Exception(), // 경로 조회 실패: 경로 없이
            2 or 4 or 5 or 6 => @"D:\Games\game\" + pid + ".exe",
            _ => @"D:\Games\other\x.exe",
        };

        string? text = DispatchEnv.DescribeBlockers(game, processes, image);

        Assert.Equal(@"game.exe(1); a.exe(4) D:\Games\game\4.exe; b.exe(5) D:\Games\game\5.exe", text); // 폴더 아래 미등록 helper.exe 는 나열하지 않음
        Assert.Null(DispatchEnv.DescribeBlockers(game, [(2, "helper.exe"), (3, "other.exe")], image));
    }

    [Fact]
    public void RootPidsUseTheSharedInstallRootCheckAndIgnoreUnreadableImages()
    {
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "helper.exe"), (2345, "game.exe")];

        IReadOnlyList<int> rooted = DispatchEnv.RootPids(Game, processes, pid => pid == 1234
            ? @"D:\Games\game\bin\helper.exe"
            : throw new System.ComponentModel.Win32Exception());

        Assert.Equal([1234], rooted);
    }

    [Fact]
    public void PrewarmWaitsForTheSecondRegisteredExeToExitThenResumes()
    {
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "body.exe")]; // 런처는 끝났고 학습으로 더한 본체 이름만 남음
        var streamer = new CountingStreamer();
        var prewarm = new Prewarmer([TwoNames], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => DispatchEnv.IsGameRunning(g, processes),
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, new ListLog());

        PrewarmResult blocked = prewarm.Run(TwoNames, "test");

        Assert.False(blocked.Ran);
        Assert.Equal("game is running", blocked.SkipReason);
        Assert.Equal(0, streamer.Calls);

        processes = [(2345, "helper.exe")]; // 본체 종료, 폴더 아래 미등록 프로세스만 남음
        PrewarmResult resumed = prewarm.Run(TwoNames, "test");

        Assert.True(resumed.Ran);
        Assert.Equal(1, streamer.Calls);
    }

    private sealed class CountingStreamer : IGameStreamer
    {
        public int Calls { get; private set; }

        public StreamStats Stream(GameSettings game)
        {
            Calls++;
            return new StreamStats(1024, 1, 0, 0.01);
        }
    }
}
