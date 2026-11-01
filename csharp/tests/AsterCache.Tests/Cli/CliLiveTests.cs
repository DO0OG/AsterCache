using System.Diagnostics;
using AsterCache.Core.Json;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Cli;

/// 같은 인자로 Python 참조 CLI 와 C# CLI 를 돌려 표준 출력을 바이트로 비교합니다. D:\AsterBench 의 메타데이터만 읽습니다.
/// 기본 실행에서는 제외되고 `--filter "Category=Live"` 로 따로 돌립니다 (Python 과 D:\AsterBench 가 있어야 함).
[Trait("Category", "Live")]
public sealed class CliLiveTests
{
    private const string Bench = @"D:\AsterBench";
    private const string BenchTrace = @"D:\AsterBench\trace.json";

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "relayout", "cli.py")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("Python reference implementation (relayout/cli.py) not found above the test binaries");
    }

    private static string Python(params string[] args)
    {
        var info = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = RepoRoot() };
        info.ArgumentList.Add("-B");
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process p = Process.Start(info)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    private static void AssertSame(string[] pythonArgs, params string[] asterArgs)
    {
        (_, string cs, _) = CliTests.Run(asterArgs);
        Assert.Equal(Python(pythonArgs), cs);
    }

    [Fact]
    public void PlanDryRunMatchesPython()
    {
        string[] tail = ["--trace", BenchTrace, "--origin-root", Bench, "--zone-mib", "300", "--dry-run"];
        AssertSame(["-m", "relayout.cli", "plan", .. tail], ["plan", .. tail]);
    }

    [Fact]
    public void PlanOutFilesMatchPythonBytes()
    {
        using var py = new TempDir();
        using var cs = new TempDir();
        string[] tail = ["--trace", BenchTrace, "--origin-root", Bench, "--zone-mib", "300", "--out"];
        string[] pyArgs = ["-m", "relayout.cli", "plan", .. tail, py.Combine("plan.json")];
        string[] csArgs = ["plan", .. tail, cs.Combine("plan.json")];
        Python(pyArgs);
        CliTests.Run(csArgs);

        foreach (string name in new[] { "plan.json", "plan.json.prefetch.json" })
        {
            Assert.True(File.ReadAllBytes(py.Combine(name)).AsSpan().SequenceEqual(File.ReadAllBytes(cs.Combine(name))), name);
        }
    }

    [Fact]
    public void VerifyE3PlanMatchesPython()
    {
        string plan = Path.Combine(AppContext.BaseDirectory, "fixtures", "e3", "plan.json");
        AssertSame(["-m", "relayout.cli", "verify", "--plan", plan], "verify", "--plan", plan);
    }

    [Theory]
    [InlineData("physical", "0.3")]
    [InlineData("path", "2")]
    [InlineData("none", "0.3")]
    public void PrefetchDryRunMatchesPython(string strategy, string ratio)
    {
        string[] tail = ["--trace", BenchTrace, "--origin-root", Bench, "--strategy", strategy, "--full-file-ratio", ratio, "--budget-mib", "100"];
        AssertSame(["-m", "prefetch.cli", .. tail], ["prefetch", .. tail]);
    }

    /// 자기 종료하는 cmd 를 게임으로 시작합니다 (사용자 앱은 건드리지 않음). 스트림은 임시 폴더의 작은 파일만 읽습니다.
    [Fact]
    public void LaunchStartsStreamAndGame()
    {
        using var dir = new TempDir();
        string file = dir.Combine("a.bin");
        File.WriteAllBytes(file, new byte[1 << 20]);
        string trace = dir.Combine("trace.json");
        File.WriteAllText(trace, PyJson.Dumps(new List<object?> { new JObject { { "path", file }, { "offset", 0L }, { "length", 65536L }, { "cost", 8.0 } } }));

        (int code, string text, _) = CliTests.Run("launch", "--trace", trace, "--origin-root", dir.Path, "--game", "cmd", "--game-arg", "/c", "--game-arg", "exit");

        Assert.Equal(0, code);
        JObject json = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(true, json["prefetch"]);
        Assert.Equal(65536L, json["bytes"]);
        Assert.Equal(0L, json["errors"]);
        Assert.IsType<long>(json["game_pid"]);
    }
}
