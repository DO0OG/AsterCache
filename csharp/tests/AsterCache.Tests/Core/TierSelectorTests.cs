using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// SSD 보조 저장의 파일 선택 규칙 (측정용 tier_plan.py 와 같음) 과 사본 폴더 선택.
public sealed class TierSelectorTests
{
    private const long Mib = 1L << 20;
    private static readonly string[] Roots = [@"D:\Games"];

    private static TraceEvent E(string path, long offset, long length) => new(path, offset, length);

    private static TierSelection Pick(IEnumerable<TraceEvent> events, long budget, Dictionary<string, long> sizes) =>
        TierSelector.Select(events, budget, Roots, p => sizes.TryGetValue(p, out long s) ? s : null);

    [Fact]
    public void RanksByHotBytesOverSizeAndFillsTheBudgetSkippingWhatDoesNotFit()
    {
        var sizes = new Dictionary<string, long>
        {
            [@"D:\Games\g\a.bin"] = 100 * Mib,
            [@"D:\Games\g\b.bin"] = 10 * Mib,
            [@"D:\Games\g\c.bin"] = 50 * Mib,
            [@"D:\Games\g\d.bin"] = 20 * Mib,
        };
        TraceEvent[] events =
        [
            E(@"D:\Games\g\a.bin", 0, 90 * Mib), E(@"D:\Games\g\b.bin", 0, 5 * Mib), E(@"D:\Games\g\c.bin", 0, 50 * Mib), E(@"D:\Games\g\d.bin", 0, 1 * Mib),
        ];

        TierSelection s = Pick(events, 80 * Mib, sizes);

        Assert.Equal([@"D:\Games\g\c.bin", @"D:\Games\g\b.bin", @"D:\Games\g\d.bin"], s.Files.Select(f => f.Path)); // a 는 비율 0.9 로 둘째지만 100 MiB 라 안 들어감
        Assert.Equal(80 * Mib, s.MovedBytes);
        Assert.Equal(56 * Mib, s.CoveredHotBytes);
        Assert.Equal(146 * Mib, s.TotalHotBytes);
        Assert.Empty(s.Missing);
    }

    [Fact]
    public void OverlappingAndRepeatedReadsCountOnceAndHotIsClippedToTheFileSize()
    {
        var sizes = new Dictionary<string, long> { [@"D:\Games\g\a.bin"] = 100 };
        TierSelection s = Pick([E(@"D:\Games\g\a.bin", 0, 10), E(@"D:\Games\g\a.bin", 5, 10), E(@"D:\Games\g\a.bin", 5, 10), E(@"D:\Games\g\a.bin", 40, 5), E(@"D:\Games\g\a.bin", 95, 50)], 1000, sizes);

        TierPick a = Assert.Single(s.Files);
        Assert.Equal(15 + 5 + 5, a.HotBytes); // [0,15) + [40,45) + [95,100)
    }

    [Fact]
    public void ExecutablesAndLibrariesAreNeverPickedEvenWithTheBestRatio()
    {
        var sizes = new Dictionary<string, long> { [@"D:\Games\g\game.exe"] = 100, [@"D:\Games\g\core.DLL"] = 100, [@"D:\Games\g\data.pak"] = 100 };
        TierSelection s = Pick([E(@"D:\Games\g\game.exe", 0, 100), E(@"D:\Games\g\core.DLL", 0, 100), E(@"D:\Games\g\data.pak", 0, 10)], 1000, sizes);

        Assert.Equal([@"D:\Games\g\data.pak"], s.Files.Select(f => f.Path));
        Assert.True(TierSelector.Excluded(@"D:\x\Launcher.EXE"));
        Assert.False(TierSelector.Excluded(@"D:\x\a.exe.bak"));
    }

    [Fact]
    public void FilesOutsideTheAllowedRootsAreIgnoredAndUnreadableSizesAreReported()
    {
        var sizes = new Dictionary<string, long> { [@"D:\Games\g\a.bin"] = 100, [@"E:\Other\b.bin"] = 100 };
        TierSelection s = Pick([E(@"D:\Games\g\a.bin", 0, 50), E(@"E:\Other\b.bin", 0, 100), E(@"D:\Games\g\gone.bin", 0, 10)], 1000, sizes);

        Assert.Equal([@"D:\Games\g\a.bin"], s.Files.Select(f => f.Path));
        Assert.Equal([@"D:\Games\g\gone.bin"], s.Missing);
    }

    [Fact]
    public void EqualRatiosBreakTiesByPathAndAZeroBudgetPicksNothing()
    {
        var sizes = new Dictionary<string, long> { [@"D:\Games\g\b.bin"] = 100, [@"D:\Games\g\a.bin"] = 100, [@"D:\Games\g\z.bin"] = 0 };
        TraceEvent[] events = [E(@"D:\Games\g\b.bin", 0, 50), E(@"D:\Games\g\a.bin", 0, 50), E(@"D:\Games\g\z.bin", 0, 50)];

        Assert.Equal([@"D:\Games\g\a.bin", @"D:\Games\g\b.bin"], Pick(events, 1000, sizes).Files.Select(f => f.Path));
        Assert.Empty(Pick(events, 0, sizes).Files);
    }

    private static TierRootChoice Root(SsdTierSettings s, string system, Dictionary<string, bool?> hdd, Dictionary<string, long> free) =>
        TierRoot.Resolve(s, system, free.Keys, d => hdd.GetValueOrDefault(d), d => free.GetValueOrDefault(d));

    [Fact]
    public void AutoRootUsesTheSystemDriveWhenItIsAnSsd()
    {
        TierRootChoice c = Root(SsdTierSettings.Default, "C:", new() { ["C:"] = false, ["E:"] = false }, new() { ["C:"] = 10, ["E:"] = 999 });

        Assert.Equal(@"C:\AsterCache\tier", c.Root);
    }

    [Fact]
    public void AutoRootFallsBackToTheOtherSsdWithTheMostFreeSpaceElseSkips()
    {
        var hdd = new Dictionary<string, bool?> { ["C:"] = true, ["D:"] = true, ["E:"] = false, ["F:"] = false, ["G:"] = null };
        var free = new Dictionary<string, long> { ["C:"] = 999, ["D:"] = 999, ["E:"] = 10, ["F:"] = 50, ["G:"] = 9999 };

        Assert.Equal(@"F:\AsterCache\tier", Root(SsdTierSettings.Default, "C:", hdd, free).Root);

        TierRootChoice none = Root(SsdTierSettings.Default, "C:", new() { ["C:"] = true, ["D:"] = true }, new() { ["C:"] = 1, ["D:"] = 1 });
        Assert.Null(none.Root);
        Assert.Equal("no SSD found", none.Why);
    }

    [Fact]
    public void CustomRootNeedsAnSsdOtherwiseItIsSkippedWithAReason()
    {
        var s = new SsdTierSettings(true, 8, @"E:\Tier");

        Assert.Equal(@"E:\Tier", Root(s, "C:", new() { ["E:"] = false }, new()).Root);
        Assert.Equal("custom root is not on an SSD", Root(s, "C:", new() { ["E:"] = true }, new()).Why);
        Assert.Equal("custom root drive type unknown", Root(s, "C:", new() { ["E:"] = null }, new()).Why);
    }

    [Fact]
    public void DestPathMirrorsTheOriginalPathUnderTheGameFolder()
    {
        Assert.Equal(@"C:\AsterCache\tier\Foo\Games\Foo\data\a.bin", TierEngine.DestPath(@"C:\AsterCache\tier", "Foo", @"D:\Games\Foo\data\a.bin"));
        Assert.True(TierEngine.IsUnder(@"C:\AsterCache\tier\Foo\x.bin", @"C:\AsterCache\tier"));
        Assert.False(TierEngine.IsUnder(@"C:\AsterCache\tier2\x.bin", @"C:\AsterCache\tier"));
        Assert.False(TierEngine.IsUnder(@"C:\AsterCache\tier\..\x.bin", @"C:\AsterCache\tier"));
    }
}
