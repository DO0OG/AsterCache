using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class ProcessCounterDetailsTests
{
    [Fact]
    public void DetailedReadCountersRetainExeNamesAndSkipUnregisteredOrUnreadableProcesses()
    {
        IReadOnlyList<(int Pid, string ExeName, long ReadBytes)> counters = ProcessSnapshot.SelectReadCountersWithExeNames(
            [(10, "launcher.exe"), (11, "game.exe"), (12, "helper.exe")],
            ["LAUNCHER.EXE", "game.exe"],
            pid => pid == 11 ? null : pid * 100L);

        Assert.Equal([(10, "launcher.exe", 1000L)], counters);
    }
}
