using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class GameProcessGateTests
{
    private static readonly GameSettings Game = Games.Make("game", "game.exe") with { OriginRoot = @"D:\Games\game" };

    [Fact]
    public void UnregisteredProcessUnderInstallRootCountsAsGameRunning()
    {
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "helper.exe")];

        bool running = DispatchEnv.IsGameRunning(Game, processes, _ => @"D:\Games\game\bin\helper.exe");

        Assert.True(running);
        Assert.False(DispatchEnv.IsGameRunning(Game, processes, _ => @"D:\Games\other\helper.exe"));
    }

    [Fact]
    public void DescribeBlockersListsAtMostThreeGameProcessesWithoutTheServiceItself()
    {
        IReadOnlyList<(int Pid, string Exe)> processes =
            [(1, "game.exe"), (Environment.ProcessId, "svc.exe"), (2, "helper.exe"), (3, "other.exe"), (4, "a.exe"), (5, "b.exe")];
        Func<int, string?> image = pid => pid switch
        {
            1 => throw new System.ComponentModel.Win32Exception(), // 경로 조회 실패: 경로 없이
            2 or 4 or 5 => @"D:\Games\game\" + pid + ".exe",
            _ => @"D:\Games\other\x.exe",
        };

        string? text = DispatchEnv.DescribeBlockers(Game, processes, image);

        Assert.Equal(@"game.exe(1); helper.exe(2) D:\Games\game\2.exe; a.exe(4) D:\Games\game\4.exe", text);
        Assert.Null(DispatchEnv.DescribeBlockers(Game, [(3, "other.exe")], image));
    }

    [Fact]
    public void RegisteredExeStillCountsWhenItsImagePathCannotBeRead()
    {
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "GAME.EXE")];
        bool lookedUpImage = false;

        bool running = DispatchEnv.IsGameRunning(Game, processes, _ =>
        {
            lookedUpImage = true;
            throw new System.ComponentModel.Win32Exception();
        });

        Assert.True(running);
        Assert.False(lookedUpImage);
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
    public void PrewarmWaitsForAnUnregisteredProcessToExitThenResumes()
    {
        bool helperRunning = true;
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "helper.exe")];
        var streamer = new CountingStreamer();
        var prewarm = new Prewarmer([Game], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = g => helperRunning && DispatchEnv.IsGameRunning(g, processes, _ => @"D:\Games\game\bin\helper.exe"),
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, new ListLog());

        PrewarmResult blocked = prewarm.Run(Game, "test");

        Assert.False(blocked.Ran);
        Assert.Equal("game is running", blocked.SkipReason);
        Assert.Equal(0, streamer.Calls);

        helperRunning = false;
        PrewarmResult resumed = prewarm.Run(Game, "test");

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
