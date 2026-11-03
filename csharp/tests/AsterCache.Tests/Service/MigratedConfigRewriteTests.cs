using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// beta.3 형식 설정을 시작 때 새 키로 한 번 다시 쓰는지 (임시 폴더에서만 실행).
public sealed class MigratedConfigRewriteTests : IDisposable
{
    private const string OldConfig = """{"games":[],"auto":{"prewarm_recent":1,"idle_minutes":3}}""";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_migrate_" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public MigratedConfigRewriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private AsterSettings Start(ILog log) =>
        Worker.ForApply(SettingsStore.Load(ConfigPath, Path.Combine(_dir, "plans")), canApply: true, log, new ConfigEditor(_dir));

    [Fact]
    public void FirstStartRewritesTheOldKeysOnceAndSecondStartIsSilent()
    {
        File.WriteAllText(ConfigPath, OldConfig);
        var first = new ListLog();

        Start(first);

        Assert.Single(first.Lines, l => l.Contains("beta.3"));
        Assert.Contains(first.Lines, l => l.Contains("rewrote the file with the new keys"));
        string text = File.ReadAllText(ConfigPath);
        Assert.Contains("\"idle_seconds\": 30", text);
        Assert.Contains("\"prewarm_recent\": 0", text);

        var second = new ListLog();
        AsterSettings now = Start(second);
        Assert.False(now.Auto.Migrated);
        Assert.Equal(TimeSpan.FromSeconds(30), now.Auto.IdleWindow);
        Assert.DoesNotContain(second.Lines, l => l.Contains("beta.3"));
    }

    [Fact]
    public void FailedRewriteLogsOneWarningAndStartupContinues()
    {
        File.WriteAllText(ConfigPath, OldConfig);
        Directory.CreateDirectory(ConfigPath + ".lock"); // 잠금 파일 자리를 디렉터리로 막아 쓰기를 실패시킴
        var log = new ListLog();

        AsterSettings s = Start(log);

        Assert.True(s.Auto.Migrated);
        Assert.Single(log.Lines, l => l.StartsWith("Warn: ") && l.Contains("could not be rewritten"));
        Assert.Equal(OldConfig, File.ReadAllText(ConfigPath));
    }
}
