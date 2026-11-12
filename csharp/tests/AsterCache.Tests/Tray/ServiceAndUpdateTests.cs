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

    private sealed class RecordingElevator : IElevator
    {
        public List<(string Exe, string Args)> Calls { get; } = [];

        public void Run(string exe, string arguments) => Calls.Add((exe, arguments));

        public void Run(string exe, IReadOnlyList<string> arguments) => Calls.Add((exe, string.Join(' ', arguments)));

        public void Start(string exe, string arguments) => Calls.Add((exe, arguments));
    }
}
