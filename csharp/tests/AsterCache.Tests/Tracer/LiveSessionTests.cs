using AsterCache.Core;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class LiveSessionTests
{
    [Fact]
    public void StartWithoutAdminThrowsBeforeCreatingAnySession()
    {
        Assert.Throws<NotAdminException>(() => LiveSession.Start(["game.exe"], isAdmin: () => false));
    }

    [Fact]
    public void EtlReaderRejectsMissingFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), "astercache-missing-" + Guid.NewGuid().ToString("N") + ".etl");

        Assert.ThrowsAny<Exception>(() => EtlReader.Read(missing, ["game.exe"], new DevicePaths([])));
    }
}
