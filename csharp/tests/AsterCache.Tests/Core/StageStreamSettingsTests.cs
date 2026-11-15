using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

/// auto.stage_stream_enabled: 선택 필드 (없으면 켜짐), 끄면 저장·읽기가 왕복하고 기본값은 파일에 쓰지 않음.
public sealed class StageStreamSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_stage_" + Guid.NewGuid().ToString("N"));

    public StageStreamSettingsTests() => Directory.CreateDirectory(_dir);

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

        Assert.True(editor.Load()!.Auto.StageStreamEnabled);
        Assert.DoesNotContain("stage_stream_enabled", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void FalseIsReadAndRoundTripsThroughTheEditor()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default with { StageStreamEnabled = false });

        Assert.Contains("\"stage_stream_enabled\": false", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
        Assert.False(editor.Load()!.Auto.StageStreamEnabled);

        editor.UpdateAuto(a => a with { Relocate = false }); // 다른 값만 바꿔도 보존
        Assert.False(editor.Load()!.Auto.StageStreamEnabled);
    }
}
