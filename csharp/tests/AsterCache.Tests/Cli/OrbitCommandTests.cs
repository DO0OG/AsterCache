using AsterCache.Cli;
using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Service;
using AsterCache.Tests.Service;
using Xunit;

namespace AsterCache.Tests.Cli;

public sealed class OrbitCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_orbit_cli_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string> _savedDirectory = OrbitCommand.DataDirectory;

    public OrbitCommandTests()
    {
        Directory.CreateDirectory(_dir);
        OrbitCommand.DataDirectory = () => _dir;
    }

    public void Dispose()
    {
        OrbitCommand.DataDirectory = _savedDirectory;
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("shadow")]
    [InlineData("active")]
    public void OrbitModeCliWritesAndReadsBackTheSelectedMode(string mode)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = AsterCli.Run(["--lang", "en", "orbit", "mode", mode], stdout, stderr);

        Assert.Equal(0, code);
        Assert.Empty(stderr.ToString());
        Assert.Equal(mode, ((JObject)JsonRead.Parse(stdout.ToString())!)["mode"]);
        Assert.Equal(mode, new OrbitRuntime(_dir, null, null, new ListLog()).Mode);
    }

    [Fact]
    public void OrbitModeCliRejectsUnknownValues()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = AsterCli.Run(["--lang", "en", "orbit", "mode", "unknown"], stdout, stderr);

        Assert.Equal(2, code);
        Assert.Empty(stdout.ToString());
        Assert.Contains("off|shadow|active", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--disk", "DD")]
    [InlineData("--disk", "1")]
    [InlineData("--samples", "0")]
    [InlineData("--samples", "201")]
    public void OrbitCalibrateRejectsInvalidDiskAndSampleCounts(string option, string value)
    {
        string[] args = option == "--disk" ? ["orbit", "calibrate", "--disk", value] : ["orbit", "calibrate", "--disk", "D", "--samples", value];
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = AsterCli.Run(["--lang", "en", .. args], stdout, stderr);

        Assert.Equal(2, code);
        Assert.Empty(stdout.ToString());
    }
}
