using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class ServiceAndUpdateTests
{
    private const string NoSuchService = "AsterCacheNoSuchService";

    [Fact]
    public void UninstalledServiceReportsNotInstalled()
    {
        Assert.Equal(ServiceState.NotInstalled, new ScmServiceControl(new RecordingElevator(), NoSuchService).Query());
    }

    [Theory]
    [InlineData(@"""C:\Program Files\AsterCache\AsterCache.Service.exe"" --data-dir ""C:\ProgramData\AsterCache""", @"C:\Program Files\AsterCache\AsterCache.Service.exe")]
    [InlineData(@"C:\Aster\AsterCache.Service.exe --data-dir C:\d", @"C:\Aster\AsterCache.Service.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ImagePathYieldsTheServiceExecutable(string? imagePath, string? expected)
    {
        Assert.Equal(expected, ScmServiceControl.ImagePathExe(imagePath));
    }

    [Fact]
    public void UninstalledServiceHasNoInstalledVersion()
    {
        Assert.Null(new ScmServiceControl(new RecordingElevator(), NoSuchService).InstalledVersion());
    }

    [Fact]
    public void StartAndStopUseScThroughTheElevatorOnly()
    {
        var elevator = new RecordingElevator();
        var scm = new ScmServiceControl(elevator, NoSuchService);
        scm.RequestStart();
        scm.RequestStop();
        Assert.Equal(2, elevator.Calls.Count);
        Assert.All(elevator.Calls, c => Assert.EndsWith("sc.exe", c.Exe, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["start " + NoSuchService, "stop " + NoSuchService], elevator.Calls.Select(c => c.Args));
    }

    [Fact]
    public void TraySettingsDefaultsToDisabledAndRequiresHttps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_trayjson_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "tray.json");
            Assert.False(TraySettings.Load(path).UpdateEnabled);
            File.WriteAllText(path, "{\"update_feed_url\": \"https://example.invalid/feed\"}");
            Assert.True(TraySettings.Load(path).UpdateEnabled);
            File.WriteAllText(path, "{\"update_feed_url\": \"http://example.invalid/feed\"}");
            Assert.Throws<SettingsException>(() => TraySettings.Load(path));
            File.WriteAllText(path, "{\"other\": 1}");
            Assert.Throws<SettingsException>(() => TraySettings.Load(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task UpdateSkippedWhenNotInstalledAndReportedWhenAvailable()
    {
        var manager = new FakeManager { Installed = false, Version = "2.0.0" };
        var service = new UpdateService(manager, new NullLog());
        Assert.Null(await service.CheckAsync());
        Assert.Equal(0, manager.Checks);

        manager.Installed = true;
        Assert.Equal("2.0.0", await service.CheckAsync());
        manager.Throw = true;
        Assert.Null(await service.CheckAsync()); // 확인 실패는 삼키고 null

        manager.Throw = false;
        await service.DownloadAndApplyAsync(_ => { }, CancellationToken.None);
        Assert.Equal(["download", "apply"], manager.Steps);
    }

    private sealed class RecordingElevator : IElevator
    {
        public List<(string Exe, string Args)> Calls { get; } = [];

        public void Run(string exe, string arguments) => Calls.Add((exe, arguments));
    }

    private sealed class FakeManager : IUpdateManager
    {
        public bool Installed { get; set; }

        public string? Version { get; set; }

        public bool Throw { get; set; }

        public int Checks { get; private set; }

        public List<string> Steps { get; } = [];

        public bool IsInstalled => Installed;

        public Task<string?> CheckForUpdatesAsync()
        {
            Checks++;
            return Throw ? throw new InvalidOperationException("feed down") : Task.FromResult(Version);
        }

        public Task DownloadUpdatesAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Steps.Add("download");
            return Task.CompletedTask;
        }

        public void ApplyUpdatesAndRestart() => Steps.Add("apply");
    }

    private sealed class NullLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
