using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Cli;
using AsterCache.Tests.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class DiskKindTests : IDisposable
{
    private readonly Func<string, DiskDetails> _savedDisk = StatusCommand.Disk;
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        StatusCommand.Disk = _savedDisk;
        _dir.Dispose();
    }

    [Fact]
    public void KindNamesSsdHddAndUnknown()
    {
        Assert.Equal("SSD", DiskInfo.Kind(false, null));
        Assert.Equal("SSD", DiskInfo.Kind(false, true));
        Assert.Equal("HDD (looks like SMR)", DiskInfo.Kind(true, true));
        Assert.Equal("HDD (probably CMR)", DiskInfo.Kind(true, false));
        Assert.Equal("HDD", DiskInfo.Kind(true, null));
        Assert.Equal("unknown", DiskInfo.Kind(null, true));
    }

    [Fact]
    public void TrimDescriptorParsing()
    {
        Assert.True(DiskInfo.ParseTrim([0, 0, 0, 0, 12, 0, 0, 0, 1, 0, 0, 0]));
        Assert.False(DiskInfo.ParseTrim([0, 0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(DiskInfo.ParseTrim([1, 2]));
    }

    [Fact]
    public void SystemDriveKindDoesNotThrow()
    {
        DiskDetails d = DiskInfo.Query(Path.GetPathRoot(Environment.SystemDirectory)![..2]);
        Assert.False(string.IsNullOrEmpty(DiskInfo.Kind(d.IncursSeekPenalty, d.Trim)));
    }

    [Fact]
    public void StatusListsEachGameDriveOnceWithItsKind()
    {
        string plans = _dir.Combine("plans");
        Directory.CreateDirectory(plans);
        File.WriteAllText(_dir.Combine("config.json"), PyJson.Dumps(new JObject { { "games", new List<object?> { Game("A", @"D:\Games\A", plans), Game("B", @"d:\Games\B", plans), Game("C", @"E:\C", plans) } } }));
        StatusCommand.Disk = l => l == "D:" ? new DiskDetails("Model D", true, true) : new DiskDetails(string.Empty, false);

        (int code, string text, _) = CliTests.Run("status", "--data-dir", _dir.Path, "--prewarm-status", _dir.Combine("none.json"), "--auto-status", _dir.Combine("none.json"));

        Assert.Equal(0, code);
        var disks = ((List<object?>)((JObject)JsonRead.Parse(text)!)["disks"]!).Cast<JObject>().ToList();
        Assert.Equal(2, disks.Count);
        Assert.Equal("HDD (looks like SMR)", disks[0]["kind"]);
        Assert.Equal("D:", disks[0]["drive"]);
        Assert.Equal("SSD", disks[1]["kind"]);
    }

    [Fact]
    public void LogAtStartWritesOneLinePerDrive()
    {
        var log = new ListLog();
        var calls = new List<string>();

        DiskKinds.LogAtStart([Settings(@"D:\A"), Settings(@"d:\B"), Settings(@"E:\C")], log, l =>
        {
            calls.Add(l);
            return new DiskDetails(l == "D:" ? "Model D" : string.Empty, l == "D:", l == "D:" ? false : null);
        });

        Assert.Equal(["D:", "E:"], calls);
        Assert.Equal(["Info: disk D: HDD (probably CMR) (Model D)", "Info: disk E: SSD"], log.Lines);
    }

    private static GameSettings Settings(string root) => new("g", ["g.exe"], "t", "p", root, [root], "L", 60);

    private static JObject Game(string name, string root, string plans) => new()
    {
        { "name", name }, { "exe_names", new List<object?> { name + ".exe" } }, { "trace", Path.Combine(plans, name + ".prefetch.json") }, { "plan", Path.Combine(plans, name + ".plan.json") },
        { "origin_root", root }, { "allowed_roots", new List<object?> { root } }, { "volume_label", "GAMES" },
    };
}
