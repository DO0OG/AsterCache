using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

/// auto.stream_idle_only: 선택 필드 (없으면 켜짐), 끄면 저장·읽기가 왕복하고 기본값은 파일에 쓰지 않음.
public sealed class StreamIdleOnlySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_idle_" + Guid.NewGuid().ToString("N"));

    public StreamIdleOnlySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private ConfigEditor Editor()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(new GameInput("a", ["a.exe"], Path.Combine(_dir, "plans", "a.prefetch.json"), Path.Combine(_dir, "plans", "a.plan.json"),
            Path.Combine(_dir, "Games", "a"), [Path.Combine(_dir, "Games")], "게임"));
        return editor;
    }

    [Fact]
    public void MissingKeyMeansEnabledAndTheDefaultIsNotWritten()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default);

        Assert.True(editor.Load()!.Auto.StreamIdleOnly);
        Assert.DoesNotContain("stream_idle_only", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void FalseIsReadAndRoundTripsThroughTheEditor()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default with { StreamIdleOnly = false });

        Assert.Contains("\"stream_idle_only\": false", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
        Assert.False(editor.Load()!.Auto.StreamIdleOnly);

        editor.UpdateAuto(a => a with { Relocate = false });
        Assert.False(editor.Load()!.Auto.StreamIdleOnly);
    }

    [Fact]
    public void ANonBooleanValueIsRejected()
    {
        Assert.Throws<SettingsException>(() => SettingsStore.Parse("""{"games":[],"auto":{"stream_idle_only":"false"}}""", _dir));
    }
}
