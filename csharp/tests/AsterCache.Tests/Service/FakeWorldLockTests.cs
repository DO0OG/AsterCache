using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class FakeWorldLockTests
{
    [Fact]
    public void WritesAreRejectedOutsideTheZoneLockAndAllowedInsideIt()
    {
        FakeWorld w = new();
        AsterCache.Service.AutoEnv env = w.Env();
        GameSettings g = new("g", [], "", "", @"D:g", [@"D:g"], "GAMES", 0);

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => env.Touch(@"D:\g\a.bin"));
        Assert.Contains("zone lock is not held", e.Message);
        Assert.Throws<InvalidOperationException>(() => env.MoveAside(@"D:\g\a.bin", @"D:\g\b.bin"));
        Assert.Throws<InvalidOperationException>(() => env.ArchiveJournal(g));

        using (env.Lock(g))
        using (env.Lock(g))
        {
            env.Touch(@"D:\g\a.bin");
            env.ArchiveJournal(g);
        }

        Assert.Throws<InvalidOperationException>(() => env.Touch(@"D:\g\a.bin"));
        using (w.AllowUnlockedWrites())
        {
            env.Touch(@"D:\g\a.bin");
        }
    }
}
