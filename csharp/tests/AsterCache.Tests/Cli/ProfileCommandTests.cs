using System.IO.Compression;
using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Core.Profiles;
using AsterCache.Core.Trace;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Cli;

/// aster profile export / import: 경로 없는 내보내기, 같은 폴더 왕복, 파일이 다르면 거부, 경로 조작·크기 상한 거부.
public sealed class ProfileCommandTests : IDisposable
{
    private readonly Func<bool> _savedAdmin = ProfileCommand.IsAdmin;
    private readonly TempDir _dir = new();
    private readonly string _data;
    private readonly string _plans;
    private readonly string _root;
    private readonly string _raw;
    private readonly string _boot;
    private readonly string _prefetch;

    public ProfileCommandTests()
    {
        ProfileCommand.IsAdmin = () => true;
        _data = _dir.Combine("data");
        _plans = Path.Combine(_data, "plans");
        _root = Path.Combine(_data, "Games", "Foo");
        Directory.CreateDirectory(_plans);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        byte[] big = new byte[3 << 20];
        Random.Shared.NextBytes(big);
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), big);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_data, "outside.bin"), new byte[4096]);
        _raw = Path.Combine(_plans, "Foo.trace.json");
        _boot = Path.Combine(_plans, "Foo" + GameProfile.BootTraceSuffix);
        _prefetch = Path.Combine(_plans, "Foo.plan.json.prefetch.json");
        List<TraceEvent> events =
        [
            new(Path.Combine(_root, "a.bin"), 0, 65536),
            new(Path.Combine(_root, "a.bin"), 1 << 20, 65536),
            new(Path.Combine(_root, "sub", "b.bin"), 0, 4096, 3.0),
            new(Path.Combine(_data, "outside.bin"), 0, 4096),
        ];
        string text = PyJson.Dumps(events.Select(e => (object?)e.ToJson()).ToList(), ensureAscii: false);
        File.WriteAllText(_raw, text);
        File.WriteAllText(_prefetch, text);
        var config = new JObject
        {
            {
                "games", new List<object?>
                {
                    new JObject
                    {
                        { "name", "Foo" },
                        { "exe_names", new List<object?> { "foo.exe" } },
                        { "trace", _prefetch },
                        { "plan", Path.Combine(_plans, "Foo.plan.json") },
                        { "origin_root", _root },
                        { "allowed_roots", new List<object?> { _root } },
                        { "volume_label", "GAMES" },
                    },
                }
            },
        };
        File.WriteAllText(Path.Combine(_data, "config.json"), PyJson.Dumps(config));
    }

    public void Dispose()
    {
        ProfileCommand.IsAdmin = _savedAdmin;
        _dir.Dispose();
    }

    private static (int Code, JObject Json) Run(params string[] args)
    {
        (int code, string text, _) = CliTests.Run(args);
        return (code, (JObject)JsonRead.Parse(text.Trim().Split('\n')[^1])!);
    }

    private (int Code, JObject Json) Export(string file) => Run("profile", "export", "--game", "Foo", "--out", file, "--data-dir", _data);

    private (int Code, JObject Json) Import(string file, string game = "Foo") => Run("profile", "import", "--file", file, "--game", game, "--data-dir", _data, "--json");

    private List<(string, long, long)> Rows(string path) => TraceLoader.LoadRanges(path, _root).Select(e => (e.Path, e.Offset, e.Length)).ToList();

    private void DeleteTraces()
    {
        File.Delete(_raw);
        File.Delete(_prefetch);
    }

    [Fact]
    public void ExportHasNoAbsolutePathsOrUserNameAndKeepsFileSizesAndHashes()
    {
        string file = _dir.Combine("foo.json");

        (int code, JObject result) = Export(file);

        Assert.Equal(0, code);
        Assert.Equal(2L, result["files"]);
        string text = File.ReadAllText(file);
        Assert.DoesNotContain(_dir.Path, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", text, StringComparison.Ordinal);
        if (Environment.UserName.Length >= 4)
        {
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        }

        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(["format", "version", "created", "game", "exe_names", "files", "raw_trace", "prefetch_trace"], o.Select(kv => kv.Key));
        Assert.Equal("astercache-profile", o["format"]);
        Assert.Equal(1L, o["version"]);
        var files = ((List<object?>)o["files"]!).Cast<JObject>().ToList();
        Assert.Equal(["a.bin", "sub/b.bin"], files.Select(f => f["path"]));
        Assert.Equal([3L << 20, 4096L], files.Select(f => f["size"]));
        Assert.All(files, f => Assert.Equal(64, ((string)f["sample_sha256"]!).Length));
        Assert.Equal(3, ((List<object?>)o["raw_trace"]!).Count);
        Assert.Equal(3, ((List<object?>)o["prefetch_trace"]!).Count);
    }

    [Theory]
    [InlineData("foo.json")]
    [InlineData("foo.zip")]
    public void ExportThenImportOnTheSameFolderRestoresBothTraces(string name)
    {
        string file = _dir.Combine(name);
        List<(string, long, long)> expected = Rows(_raw);
        Assert.Equal(3, expected.Count); // 루트 밖 행은 읽을 때 이미 빠짐
        Assert.Equal(0, Export(file).Code);
        DeleteTraces();

        (int code, JObject result) = Import(file);

        Assert.Equal(0, code);
        Assert.Equal(true, result["imported"]);
        Assert.Equal(expected, Rows(_boot)); // 가져온 기록은 boot 이름: 원시 trace 가 생기면 첫 실행 기록이 건너뛰어지므로
        Assert.False(File.Exists(_raw));
        Assert.Equal(expected, Rows(_prefetch));
        Assert.False(File.Exists(Path.Combine(_plans, "Foo.plan.json")));
        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            using ZipArchive zip = ZipFile.OpenRead(file);
            Assert.Equal(ProfileCommand.ZipEntry, Assert.Single(zip.Entries).FullName);
        }
    }

    [Fact]
    public void ExportOfAGameWithOnlyAnImportedProfileUsesItsBootTrace()
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);
        DeleteTraces();
        Assert.Equal(0, Import(file).Code);
        string again = _dir.Combine("again.json");

        (int code, JObject result) = Export(again);

        Assert.Equal(0, code);
        Assert.Equal(3L, result["raw_events"]);
    }

    [Fact]
    public void ChangedMissingOrResizedFilesAreRejectedAndNothingIsWritten()
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);
        DeleteTraces();
        using (var f = new FileStream(Path.Combine(_root, "a.bin"), FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(3 << 19, SeekOrigin.Begin);
            int old = f.ReadByte();
            f.Seek(3 << 19, SeekOrigin.Begin);
            f.WriteByte((byte)(old ^ 0xFF));
        }

        File.AppendAllText(Path.Combine(_root, "sub", "b.bin"), "x");

        (int code, JObject result) = Import(file);

        Assert.Equal(1, code);
        Assert.Equal("profile_mismatch", result["error"]);
        var list = ((List<object?>)result["mismatches"]!).Cast<JObject>().ToList();
        Assert.Equal(["a.bin", "sub/b.bin"], list.Select(m => m["path"]));
        Assert.Equal(["sample", "size"], list.Select(m => m["reason"]));
        Assert.Equal(4096L, list[1]["expected_size"]);
        Assert.Equal(4097L, list[1]["actual_size"]);
        Assert.False(File.Exists(_raw));
        Assert.False(File.Exists(_prefetch));

        File.Delete(Path.Combine(_root, "sub", "b.bin"));
        Assert.Equal("missing", ((JObject)((List<object?>)Import(file).Json["mismatches"]!)[1]!)["reason"]);
    }

    [Fact]
    public void TextImportListsMismatchesAndNeedsNoAdminToCheck()
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);
        File.Delete(Path.Combine(_root, "a.bin"));
        ProfileCommand.IsAdmin = () => false;

        (int code, string text, _) = CliTests.Run("profile", "import", "--file", file, "--game", "Foo", "--data-dir", _data);

        Assert.Equal(1, code);
        Assert.Contains("a.bin\tmissing\t3145728\t-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportNeedsAdministratorBeforeWriting()
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);
        DeleteTraces();
        ProfileCommand.IsAdmin = () => false;

        (int code, JObject result) = Import(file);

        Assert.Equal(3, code);
        Assert.Equal("not_admin", result["error"]);
        Assert.False(File.Exists(_raw));
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("..\\evil.bin")]
    [InlineData("C:\\Windows\\evil.bin")]
    [InlineData("\\\\server\\share\\evil.bin")]
    [InlineData("/evil.bin")]
    [InlineData("sub/../../evil.bin")]
    public void PathTraversalInTheProfileIsRejected(string path)
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);
        DeleteTraces();
        var o = (JObject)JsonRead.ReadFile(file)!;
        ((JObject)((List<object?>)o["files"]!)[0]!)["path"] = path;
        File.WriteAllText(file, PyJson.Dumps(o, ensureAscii: false));

        (int code, JObject result) = Import(file);

        Assert.Equal(1, code);
        Assert.Equal("ValueError", result["error"]);
        Assert.False(File.Exists(_raw));
        Assert.False(File.Exists(Path.Combine(_data, "evil.bin")));
    }

    [Fact]
    public void ProfilesOverTheSizeLimitAreRejectedAsJsonAndAsZip()
    {
        string json = _dir.Combine("big.json");
        using (var f = new FileStream(json, FileMode.Create))
        {
            f.SetLength(GameProfile.MaxBytes + 1);
        }

        (int code, JObject result) = Import(json);
        Assert.Equal(1, code);
        Assert.Contains("too large", (string)result["detail"]!, StringComparison.Ordinal);

        string zip = _dir.Combine("bomb.zip");
        using (var z = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
        using (Stream s = z.CreateEntry(ProfileCommand.ZipEntry).Open())
        {
            for (int i = 0; i < 65; i++)
            {
                s.Write(new byte[1 << 20]);
            }
        }

        (code, result) = Import(zip);
        Assert.Equal(1, code);
        Assert.Contains("too large", (string)result["detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownGameAndBadOutExtensionFail()
    {
        string file = _dir.Combine("foo.json");
        Assert.Equal(0, Export(file).Code);

        Assert.Equal(1, Import(file, "Nope").Code);
        (int code, _, string err) = CliTests.Run("profile", "export", "--game", "Foo", "--out", _dir.Combine("x.txt"), "--data-dir", _data);
        Assert.Equal(2, code);
        Assert.Contains("--out", err, StringComparison.Ordinal);
    }
}
