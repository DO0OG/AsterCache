using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class IoSummarySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_io_setting_" + Guid.NewGuid().ToString("N"));

    public IoSummarySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void IoSummarySwitchIsAcceptedByAutoSettings()
    {
        AsterSettings settings = SettingsStore.Parse("""{"games":[],"auto":{"io_summary_enabled":false}}""", _dir);

        Assert.False(settings.Auto.IoSummaryEnabled);
        Assert.True(SettingsStore.Parse("""{"games":[]}""", _dir).Auto.IoSummaryEnabled);
        Assert.Throws<SettingsException>(() => SettingsStore.Parse("""{"games":[],"auto":{"io_summary_enabled":"false"}}""", _dir));
    }

    [Fact]
    public void DefaultIsOmittedAndFalseRoundTripsWithoutBeingLostByAnotherEdit()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default);

        Assert.True(editor.Load()!.Auto.IoSummaryEnabled);
        Assert.DoesNotContain("io_summary_enabled", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);

        editor.SetIoSummaryEnabled(false);
        Assert.Contains("\"io_summary_enabled\": false", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
        Assert.False(editor.Load()!.Auto.IoSummaryEnabled);
        editor.UpdateAuto(auto => auto with { Relocate = false });
        Assert.False(editor.Load()!.Auto.IoSummaryEnabled);

        editor.SetIoSummaryEnabled(true);
        Assert.DoesNotContain("io_summary_enabled", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

    private ConfigEditor Editor()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(new GameInput("a", ["a.exe"], Path.Combine(_dir, "plans", "a.prefetch.json"), Path.Combine(_dir, "plans", "a.plan.json"),
            Path.Combine(_dir, "Games", "a"), [Path.Combine(_dir, "Games")], "게임"));
        return editor;
    }
}
