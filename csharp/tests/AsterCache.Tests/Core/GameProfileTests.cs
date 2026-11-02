using AsterCache.Core.Json;
using AsterCache.Core.Profiles;
using AsterCache.Core.Trace;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// 프로필: 상대 경로 규칙, 만들기·검증·읽기 (신뢰 경계 검사).
public sealed class GameProfileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();
    private readonly string _root;

    public GameProfileTests()
    {
        _root = _dir.Combine("Foo");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[3 << 20]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.bin"), new byte[4096]);
    }

    public void Dispose() => _dir.Dispose();

    private List<TraceEvent> Events() =>
    [
        new(Path.Combine(_root, "a.bin"), 0, 65536),
        new(Path.Combine(_root, "SUB", "B.BIN"), 0, 4096), // 대소문자만 다른 경로도 같은 파일
        new(Path.Combine(_root, "sub", "b.bin"), 0, 4096, 3.0),
        new(Path.Combine(_root, "gone.bin"), 0, 4096), // 없는 파일은 뺌
        new(_dir.Combine("outside.bin"), 0, 4096), // 루트 밖은 뺌
    ];

    [Theory]
    [InlineData("a.bin", true)]
    [InlineData("sub/dir/a b.bin", true)]
    [InlineData("sub\\a.bin", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../x.bin", false)]
    [InlineData("a/../../x.bin", false)]
    [InlineData("a\\..\\x", false)]
    [InlineData("./x.bin", false)]
    [InlineData("/abs.bin", false)]
    [InlineData("\\abs.bin", false)]
    [InlineData("C:\\x.bin", false)]
    [InlineData("C:x.bin", false)]
    [InlineData("\\\\srv\\share\\x.bin", false)]
    [InlineData("//srv/share/x.bin", false)]
    [InlineData("a//b.bin", false)]
    [InlineData("a/b.bin:stream", false)]
    [InlineData("a/NUL", false)]
    [InlineData("a/com1.txt", false)]
    [InlineData("a/b.", false)]
    [InlineData("a/b.bin\0", false)]
    public void RelativePathRule(string path, bool safe) => Assert.Equal(safe, GameProfile.IsSafeRelative(path));

    [Fact]
    public void BuildKeepsRelativePathsOnceAndDropsMissingAndOutsideRows()
    {
        GameProfile p = GameProfile.Build("Foo", ["foo.exe"], _root, Events(), [], Now);

        Assert.Equal(["a.bin", "SUB/B.BIN"], p.Files.Select(f => f.Path)); // 처음 본 대소문자로 한 번만
        Assert.Equal([3L << 20, 4096L], p.Files.Select(f => f.Size));
        Assert.All(p.Files, f => Assert.Equal(64, f.SampleSha256.Length));
        Assert.Equal(["a.bin", "SUB/B.BIN", "SUB/B.BIN"], p.RawTrace.Select(e => e.Path));
        Assert.Empty(p.PrefetchTrace);
        Assert.Equal("2026-11-02T09:30:00Z", p.Created);
        string text = System.Text.Encoding.UTF8.GetString(p.ToBytes());
        Assert.DoesNotContain(_dir.Path, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesRoundTripAndVerifyPassesOnTheSameFolder()
    {
        GameProfile p = GameProfile.Build("Foo", ["foo.exe"], _root, Events(), Events().Take(1).ToList(), Now);

        GameProfile back = GameProfile.ParseBytes(p.ToBytes());

        Assert.Equal(p.Files, back.Files);
        Assert.Equal(p.RawTrace, back.RawTrace);
        Assert.Equal(p.PrefetchTrace, back.PrefetchTrace);
        Assert.Equal(["foo.exe"], back.ExeNames);
        Assert.Empty(back.Verify(_root));
        string trace = System.Text.Encoding.UTF8.GetString(GameProfile.TraceBytes(back.PrefetchTrace, _root));
        Assert.Contains("\"path\": " + PyJson.Dumps(Path.Combine(_root, "a.bin")), trace, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyReportsMissingSizeAndSampleMismatches()
    {
        GameProfile p = GameProfile.Build("Foo", ["foo.exe"], _root, Events(), [], Now);
        using (var f = new FileStream(Path.Combine(_root, "a.bin"), FileMode.Open, FileAccess.Write))
        {
            f.Seek(3 << 19, SeekOrigin.Begin); // 가운데 표본 안
            f.WriteByte(7);
        }

        ProfileMismatch sample = Assert.Single(p.Verify(_root));
        Assert.Equal(new ProfileMismatch("a.bin", "sample", 3L << 20, 3L << 20), sample);

        File.AppendAllText(Path.Combine(_root, "sub", "b.bin"), "x");
        Assert.Equal(["sample", "size"], p.Verify(_root).Select(m => m.Reason));

        File.Delete(Path.Combine(_root, "sub", "b.bin"));
        Assert.Equal(new ProfileMismatch("SUB/B.BIN", "missing", 4096, null), p.Verify(_root)[1]);
    }

    [Fact]
    public void ParseRejectsUnknownKeysUnsafePathsAndEventsOutsideTheFileList()
    {
        JObject Good() => GameProfile.Build("Foo", ["foo.exe"], _root, Events(), [], Now).ToJson();

        JObject unknown = Good();
        unknown["engine"] = "x";
        Assert.Throws<PyValueException>(() => GameProfile.Parse(unknown));

        foreach (string bad in new[] { "../evil.bin", "C:\\evil.bin", "\\\\srv\\s\\evil.bin", "a/../evil.bin" })
        {
            JObject o = Good();
            ((JObject)((List<object?>)o["files"]!)[0]!)["path"] = bad;
            Assert.Throws<PyValueException>(() => GameProfile.Parse(o));
        }

        JObject extra = Good();
        ((List<object?>)extra["raw_trace"]!).Add(new JObject { { "path", "other.bin" }, { "offset", 0L }, { "length", 1L } });
        Assert.Throws<PyValueException>(() => GameProfile.Parse(extra));

        JObject hash = Good();
        ((JObject)((List<object?>)hash["files"]!)[0]!)["sample_sha256"] = "XYZ";
        Assert.Throws<PyValueException>(() => GameProfile.Parse(hash));

        JObject version = Good();
        version["version"] = 2L;
        Assert.Throws<PyValueException>(() => GameProfile.Parse(version));
    }

    [Fact]
    public void ParseBytesRejectsOversizedInput() =>
        Assert.Throws<PyValueException>(() => GameProfile.ParseBytes(new byte[GameProfile.MaxBytes + 1]));

    // ---- 재해석 지점 (정션·심볼릭 링크) ----

    [Fact]
    public void ReparsePointFilesAreLeftOutOfAnExportAndRejectedOnImport()
    {
        GameProfile built = GameProfile.Build("foo", ["Foo.exe"], _root, Events(), [], Now);
        Func<string, string, bool> fake = (_, rel) => rel.StartsWith("sub/", StringComparison.OrdinalIgnoreCase); // sub 폴더가 정션이라고 가정

        GameProfile exported = GameProfile.Build("foo", ["Foo.exe"], _root, Events(), [], Now, fake);
        Assert.Equal(["a.bin"], exported.Files.Select(f => f.Path));

        ProfileMismatch bad = Assert.Single(built.Verify(_root, fake));
        Assert.Equal(("SUB/B.BIN", "outside_root", null), (bad.Path, bad.Reason, bad.ActualSize));
    }

    [Fact]
    public void RealDirectoryJunctionIsDetectedOnItsOwnAndOnFilesBelowIt()
    {
        string outside = _dir.Combine("Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "x.bin"), new byte[4096]);
        string link = Path.Combine(_root, "link");
        using var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode); // 정션은 관리자 권한이 필요 없음

        Assert.True(GameProfile.HasReparsePoint(_root, "link"));
        Assert.True(GameProfile.HasReparsePoint(_root, "link/x.bin"));
        Assert.False(GameProfile.HasReparsePoint(_root, "sub/b.bin"));
        Assert.False(GameProfile.HasReparsePoint(_root, "gone/none.bin"));

        var events = new List<TraceEvent> { new(Path.Combine(_root, "link", "x.bin"), 0, 4096), new(Path.Combine(_root, "a.bin"), 0, 4096) };
        Assert.Equal(["a.bin"], GameProfile.Build("foo", ["Foo.exe"], _root, events, [], Now).Files.Select(f => f.Path));
        Directory.Delete(link); // 대상은 지우지 않고 정션만
    }
}
