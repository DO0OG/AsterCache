using AsterCache.Cli;
using AsterCache.Core.Json;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Cli;

public sealed class CliTests
{
    internal static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = AsterCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static JObject Json(string text) => (JObject)JsonRead.Parse(text)!;

    /// 임시 폴더에 파일 둘과, 그 안의 범위를 읽은 트레이스 한 개를 만듭니다. 반환: 루트, 트레이스 경로.
    private static (string Root, string Trace) MakeTree(TempDir dir)
    {
        string root = dir.Combine("root");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "a.bin");
        string b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, new byte[3 << 20]);
        File.WriteAllBytes(b, new byte[1 << 20]);
        var rows = new List<object?>
        {
            new JObject { { "path", a }, { "offset", 0L }, { "length", 131072L }, { "cost", 8.0 } },
            new JObject { { "path", a }, { "offset", 1L << 20 }, { "length", 65536L }, { "cost", 8.0 } },
            new JObject { { "path", b }, { "offset", 65536L }, { "length", 65536L }, { "cost", 8.0 } },
        };
        string trace = dir.Combine("trace.json");
        File.WriteAllText(trace, PyJson.Dumps(rows));
        return (root, trace);
    }

    [Fact]
    public void PlanThenVerifyOnTempTree()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);
        string plan = dir.Combine("plan.json");

        (int code, string text, _) = Run("plan", "--trace", trace, "--origin-root", root, "--zone-mib", "1", "--out", plan);

        Assert.Equal(0, code);
        Assert.Equal(["dry_run", "out", "prefetch_trace", "min_sessions", "sessions", "cluster_bytes", "zone", "stats"], Json(text).Select(kv => kv.Key));
        Assert.True(File.Exists(plan));
        Assert.True(File.Exists(plan + ".prefetch.json"));
        (int vcode, string vtext, _) = Run("verify", "--plan", plan);
        Assert.Equal(0, vcode);
        Assert.Contains(Json(vtext)["verdict"], new object?[] { "contiguous", "non-contiguous" }); // 아직 적용 전이라 어느 쪽이든 볼륨 배치에 달림
    }

    [Fact]
    public void PlanDryRunWritesNothing()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);

        (int code, string text, _) = Run("plan", "--trace", trace, "--origin-root", root, "--zone-mib", "1", "--dry-run");

        Assert.Equal(0, code);
        Assert.Equal(true, Json(text)["dry_run"]);
        Assert.Null(Json(text)["out"]);
        Assert.Equal(2, Directory.GetFileSystemEntries(dir.Path).Length); // root, trace.json 만
    }

    [Fact]
    public void PlanWithoutOutIsError()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);

        (int code, string text, _) = Run("plan", "--trace", trace, "--origin-root", root, "--zone-mib", "1");

        Assert.Equal(1, code);
        Assert.Equal("""{"error":"ValueError","detail":"--out is required unless --dry-run"}""" + Environment.NewLine, text);
    }

    [Fact]
    public void PrefetchDryRunListsOps()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);

        (int code, string text, _) = Run("prefetch", "--trace", trace, "--origin-root", root);

        Assert.Equal(0, code);
        JObject json = Json(text);
        Assert.Equal(true, json["dry_run"]);
        Assert.Equal(262144L, json["bytes"]);
        Assert.Equal(3L, json["requests"]);
    }

    [Theory]
    [InlineData("pvm")]
    [InlineData("read")]
    public void PrefetchExecuteReadsTheRanges(string method)
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);

        (int code, string text, _) = Run("prefetch", "--trace", trace, "--origin-root", root, "--execute", "--method", method);

        Assert.Equal(0, code);
        JObject json = Json(text);
        Assert.Equal(262144L, json["bytes"]);
        Assert.Equal(0L, json["errors"]);
        Assert.Equal(method, json["method"]);
    }

    [Fact]
    public void PrefetchTakesTraceAndRootFromEnvironment()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);
        string? oldTrace = Environment.GetEnvironmentVariable("ASTER_TRACE");
        string? oldRoot = Environment.GetEnvironmentVariable("ASTER_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ASTER_TRACE", trace);
            Environment.SetEnvironmentVariable("ASTER_ROOT", root);

            (int code, string text, _) = Run("prefetch");

            Assert.Equal(0, code);
            Assert.Equal(3L, Json(text)["requests"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASTER_TRACE", oldTrace);
            Environment.SetEnvironmentVariable("ASTER_ROOT", oldRoot);
        }
    }

    [Fact]
    public void TraceCsvBytesMatchPython()
    {
        using var dir = new TempDir();
        string trace = dir.Combine("trace.json");

        (int code, string text, _) = Run("trace", "--csv", Fixture("tracer/procmon_run1.csv"), Fixture("tracer/procmon_run2.csv"),
            "--prefix", @"Q:\AsterGame\", "--out", trace);

        Assert.Equal(0, code);
        string[] lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal("procmon_run1.csv {'rows': 13, 'read_success_rows': 11, 'parsed': 6, 'ignored': 7}", lines[0]);
        Assert.Equal("procmon_run2.csv {'rows': 4, 'read_success_rows': 4, 'parsed': 4, 'ignored': 0}", lines[1]);
        Assert.Equal($$"""{"union": {{PyJson.Dumps(trace)}}, "union_events": 1035, "per_run": [{{PyJson.Dumps(dir.Combine("trace.run1.json"))}}, {{PyJson.Dumps(dir.Combine("trace.run2.json"))}}]}""", lines[2]);
        foreach (string name in new[] { "trace.json", "trace.run1.json", "trace.run2.json" })
        {
            Assert.True(File.ReadAllBytes(Fixture("tracer/expected/" + name)).AsSpan().SequenceEqual(File.ReadAllBytes(dir.Combine(name))), name);
        }
    }

    [Fact]
    public void TraceNeedsExactlyOneSource()
    {
        (int code, _, string err) = Run("--lang", "en", "trace", "--out", "x.json");
        Assert.Equal(2, code);
        Assert.Contains("exactly one of --csv, --etl, --live", err, StringComparison.Ordinal);

        Assert.Equal(2, Run("trace", "--etl", "a.etl", "--prefix", @"D:\", "--out", "x.json").Code);
    }

    [Fact]
    public void VolumeCommandsNeedAdministrator()
    {
        if (Elevation.IsAdmin())
        {
            return; // 관리자로 실행 중이면 실제 볼륨을 열게 되므로 이 확인은 하지 않음
        }

        string plan = Fixture("e3/plan.json");
        string journal = Fixture("e3/journal.jsonl");
        string notAdmin = """{"error":"not_admin"}""" + Environment.NewLine;
        Assert.Equal((3, notAdmin), Pair(Run("probe", "--volume", "D:")));
        Assert.Equal((3, notAdmin), Pair(Run("apply", "--plan", plan, "--allowed-root", @"D:\AsterBench", "--execute")));
        Assert.Equal((3, notAdmin), Pair(Run("revert", "--journal", journal, "--allowed-root", @"D:\AsterBench")));
        Assert.Equal((3, notAdmin), Pair(Run("trace", "--live", "--exe", "game.exe", "--prefix", @"D:\", "--out", "x.json")));
    }

    [Fact]
    public void ApplyRejectsExecuteWithDryRunBeforeAnythingElse()
    {
        (int code, string text, _) = Run("apply", "--plan", "missing.json", "--allowed-root", @"D:\x", "--execute", "--dry-run");

        Assert.Equal(1, code);
        Assert.Equal("""{"error":"ValueError","detail":"--execute and --dry-run are exclusive"}""" + Environment.NewLine, text);
    }

    [Theory]
    [InlineData("plan", "--trace", "t.json", "--origin-root", "R", "--zone-mib", "0", "--dry-run")]
    [InlineData("plan", "--trace", "t.json", "--origin-root", "R", "--zone-mib", "5", "--min-sessions", "-1")]
    [InlineData("apply", "--plan", "p.json", "--allowed-root", "R", "--chunk-mib", "-4")]
    [InlineData("revert", "--journal", "j.jsonl", "--allowed-root", "R", "--write-cap-mib", "0")]
    public void NonPositiveNumbersAreInvalidArgument(params string[] args)
    {
        (int code, string text, _) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal("""{"error":"invalid_argument"}""" + Environment.NewLine, text);
    }

    [Theory]
    [InlineData("plan", "--trace", "t.json", "--zone-mib", "5")]
    [InlineData("plan", "--trace", "t.json", "--origin-root", "R", "--zone-mib", "abc")]
    [InlineData("verify", "--plan", "p.json", "--bogus")]
    [InlineData("verify")]
    [InlineData("prewarm")]
    [InlineData("trace", "--csv", "x.csv", "--prefix", "D:/Games", "--out", "o.json", "--dump-first", "20")] // --dump-first 는 --etl 에서만
    [InlineData("trace", "--etl", "x.etl", "--exe", "a.exe", "--prefix", "D:/Games", "--out", "o.json", "--stop-file", "s")] // --stop-file 은 --live 에서만
    [InlineData("prefetch", "--strategy", "zzz")]
    [InlineData("prefetch", "--trace", "t.json", "--origin-root", "R", "--threads", "9")]
    [InlineData("no-such-command")]
    public void BadArgumentsExitWithCodeTwo(params string[] args)
    {
        (int code, string text, string err) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, text);
        Assert.NotEqual(string.Empty, err);
    }

    [Fact]
    public void BareDriveLetterIsTheDriveRootNotTheCurrentDirectory()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)![..2]; // "C:"

        Assert.Equal(drive + @"\", RealPaths.Real(drive));
        Assert.Equal(drive + @"\", RealPaths.Resolve(drive));
        Assert.Equal(@"Q:\", RealPaths.Real("Q:")); // 없는 드라이브도 루트로만 정규화
        Assert.True(AsterCache.Core.Apply.PlanGuards.Inside(Environment.SystemDirectory, [drive], RealPaths.Real));
        Assert.False(AsterCache.Core.Apply.PlanGuards.Inside(Environment.SystemDirectory, ["Q:"], RealPaths.Real));
    }

    [Fact]
    public void NoArgumentsExitWithCodeTwo() => Assert.Equal(2, Run().Code);

    [Fact]
    public void MissingFilesReportPythonStyleError()
    {
        (int code, string text, _) = Run("verify", "--plan", "no-such-plan.json");

        Assert.Equal(1, code);
        Assert.Equal("""{"error":"FileNotFoundError","detail":"[Errno 2] No such file or directory: 'no-such-plan.json'"}""" + Environment.NewLine, text);
    }

    [Fact]
    public void BrokenPlanIsValueError()
    {
        (int code, string text, _) = Run("verify", "--plan", Fixture("e3/trace.json"));

        Assert.Equal(1, code);
        Assert.Equal("""{"error":"ValueError","detail":"malformed plan"}""" + Environment.NewLine, text);
    }

    [Fact]
    public void StatusSummarizesJournal()
    {
        (int code, string text, _) = Run("status", "--journal", Fixture("e3/journal.jsonl"));

        Assert.Equal(0, code);
        JObject journal = (JObject)Json(text)["journal"]!;
        Assert.Equal(1048L, journal["steps"]);
        Assert.Equal(1048L, journal["done"]);
        Assert.Equal(true, journal["ended"]);
    }

    [Fact]
    public void StatusShowsTheLastPrewarmResultFromTheServiceFile()
    {
        using var dir = new TempDir();
        string file = dir.Combine("prewarm_status.json");
        File.WriteAllText(file, "{\"games\": {\"mw4\": {\"at\": \"2026-11-02T01:00:00.0000000+00:00\", \"result\": \"ok\", \"bytes\": 1610612736, \"seconds\": 4.2, \"mb_per_s\": 365.7}}}");

        (int code, string text, _) = Run("status", "--prewarm-status", file);
        (int missingCode, string missing, _) = Run("status", "--prewarm-status", dir.Combine("none.json"));

        Assert.Equal(0, code);
        var mw4 = (JObject)((JObject)((JObject)Json(text)["prewarm"]!)["games"]!)["mw4"]!;
        Assert.Equal("ok", mw4["result"]);
        Assert.Equal(0, missingCode);
        Assert.Null(Json(missing)["prewarm"]);
    }

    [Fact]
    public void StatusShowsAutoGamesAndRelocationsOrNull()
    {
        using var dir = new TempDir();
        string file = dir.Combine("auto_status.json");
        File.WriteAllText(file, "{\"updated\": \"2026-11-02T01:00:00+00:00\", \"relocations\": [\"2026-11-02T00:30:00+00:00\"], \"games\": {\"g\": {\"stage\": \"done\"}}, \"events\": []}");
        string bad = dir.Combine("bad.json");
        File.WriteAllText(bad, "{oops");

        (int code, string text, _) = Run("status", "--prewarm-status", dir.Combine("none.json"), "--auto-status", file);
        (_, string missing, _) = Run("status", "--prewarm-status", dir.Combine("none.json"), "--auto-status", dir.Combine("none.json"));
        (_, string corrupt, _) = Run("status", "--prewarm-status", dir.Combine("none.json"), "--auto-status", bad);

        Assert.Equal(0, code);
        JObject auto = (JObject)Json(text)["auto"]!;
        Assert.Equal(["games", "relocations"], auto.Select(kv => kv.Key));
        Assert.Equal("done", ((JObject)((JObject)auto["games"]!)["g"]!)["stage"]);
        Assert.Equal("auto", Json(text).Last().Key);
        Assert.Null(Json(missing)["auto"]);
        Assert.Null(Json(corrupt)["auto"]);
    }

    private static (int, string) Pair((int Code, string Out, string Err) r) => (r.Code, r.Out);
}
