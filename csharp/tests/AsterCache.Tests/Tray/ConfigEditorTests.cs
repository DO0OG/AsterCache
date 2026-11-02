using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class ConfigEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_cfg_" + Guid.NewGuid().ToString("N"));

    public ConfigEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private GameInput Game(string name) => new(
        name, [name + ".exe"], Path.Combine(_dir, "plans", name + ".prefetch.json"), Path.Combine(_dir, "plans", name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "게임", 60);

    [Fact]
    public void RoundTripKeepsBytesAndKeyOrder()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.Add(Game("b"));
        byte[] two = File.ReadAllBytes(editor.ConfigPath);
        Assert.NotEqual(0xEF, two[0]); // BOM 없음
        Assert.DoesNotContain((byte)'\r', two);

        editor.Add(Game("c"));
        editor.Remove("c");
        Assert.Equal(two, File.ReadAllBytes(editor.ConfigPath)); // 추가 후 제거하면 원래 바이트

        // 읽어서 그대로 다시 쓰는 일은 없지만 (바꾸지 않으면 쓰지 않음), 한 항목을 지웠다 같은 내용으로 넣어도 바이트 같음
        editor.Remove("b");
        editor.Add(Game("b"));
        Assert.Equal(two, File.ReadAllBytes(editor.ConfigPath));
        Assert.Equal(["a", "b"], editor.Load()!.Games.Select(g => g.Name));
        Assert.Equal(60, editor.Load()!.Games[0].ReapplyMinutes);
    }

    [Fact]
    public void RejectedEditDoesNotWriteOrLeaveTempFile()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);
        Assert.Throws<SettingsException>(() => editor.Add(Game("a")));
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
        Assert.False(File.Exists(editor.ConfigPath + ".tmp"));
    }

    [Fact]
    public void RemovingTheLastGameLeavesAValidEmptyList()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.Remove("a");
        Assert.Empty(editor.Load()!.Games);
    }

    [Fact]
    public void BlockedWriteThrowsUnauthorizedAndLeavesConfigUntouched()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);
        Directory.CreateDirectory(editor.ConfigPath + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() => editor.Add(Game("b")));
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void TriggerExesAreWrittenAsPrewarmSetting()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a") with { TriggerExes = ["Steam.exe"] });
        editor.Add(Game("b") with { TriggerExes = [] });

        AsterSettings s = editor.Load()!;

        Assert.Equal(["Steam.exe"], s.Games[0].Prewarm.TriggerExes);
        Assert.Empty(s.Games[1].Prewarm.TriggerExes);
        Assert.DoesNotContain("prewarm", File.ReadAllText(editor.ConfigPath).Split("\"b.exe\"")[1], StringComparison.Ordinal); // 입력이 없으면 키를 쓰지 않음
    }

    [Fact]
    public void MissingConfigLoadsAsNull()
    {
        Assert.Null(new ConfigEditor(_dir).Load());
    }
}
