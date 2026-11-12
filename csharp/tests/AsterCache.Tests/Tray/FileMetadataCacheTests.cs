using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class FileMetadataCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-file-cache-" + Guid.NewGuid().ToString("N"));

    public FileMetadataCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ReusesTheValueUntilFileLengthOrLastWriteTimeChanges()
    {
        string path = Path.Combine(_dir, "snapshot.json");
        File.WriteAllText(path, "one");
        var cache = new FileMetadataCache<string>();
        int reads = 0;
        string? Read() => cache.Get(path, () => "read " + ++reads);

        Assert.Equal("read 1", Read());
        Assert.Equal("read 1", Read());
        Assert.Equal(1, reads);

        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("read 2", Read());
        File.WriteAllText(path, "longer");
        Assert.Equal("read 3", Read());
        Assert.Equal(3, reads);
    }

    [Fact]
    public void ReusesMissingFileResultUntilTheFileAppears()
    {
        string path = Path.Combine(_dir, "later.json");
        var cache = new FileMetadataCache<object>();
        int reads = 0;
        object? Read() => cache.Get(path, () =>
        {
            reads++;
            return File.Exists(path) ? new object() : null;
        });

        Assert.Null(Read());
        Assert.Null(Read());
        Assert.Equal(1, reads);

        File.WriteAllText(path, "now present");
        Assert.NotNull(Read());
        Assert.Equal(2, reads);
    }
}
