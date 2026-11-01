using System.Diagnostics;
using System.Text.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// F9: 실제 D:\AsterBench\data 를 Python 참조 구현 결과와 비교합니다. 비관리자로 실행 가능하고 읽는 양은 수 MB 입니다.
/// 기본 실행에서는 제외되고 `--filter "Category=Live"` 로 따로 돌립니다 (Python 과 D:\AsterBench 가 있어야 함).
[Trait("Category", "Live")]
public sealed class LiveInteropTests
{
    private const string DataDir = @"D:\AsterBench\data";
    private const int Span = 256 << 10; // 파일마다 표본 3 x 256 KiB + 추가 범위 하나

    private const string PythonProbe = @"
import json, os, sys
sys.path.insert(0, sys.argv[1])
from prefetch.layout import cluster_size, file_extents
from relayout.mover import sample_digest
req = json.load(sys.stdin)
out = {'extents': {}, 'cluster': {}, 'digests': {}}
for p in req['files']:
    e = file_extents(p)
    out['extents'][p] = None if e is None else [list(x) for x in e]
    out['cluster'][p] = cluster_size(p)
for p in req['digest_files']:
    out['digests'][p] = sample_digest(p, os.path.getsize(p), [tuple(x) for x in req['extra']], req['span'])
print(json.dumps(out))
";

    private static readonly (long, long)[] Extra = { (100_000, 200_000) };

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "prefetch", "layout.py")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("Python reference implementation (prefetch/layout.py) not found above the test binaries");
    }

    private static JsonElement AskPython(string[] files, string[] digestFiles)
    {
        string script = Path.Combine(Path.GetTempPath(), "astercache_probe_" + Environment.ProcessId + ".py");
        File.WriteAllText(script, PythonProbe);
        try
        {
            var psi = new ProcessStartInfo("python") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "-B", "-I", script, RepoRoot() })
            {
                psi.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(psi)!;
            proc.StandardInput.Write(JsonSerializer.Serialize(new { files, digest_files = digestFiles, extra = Extra.Select(x => new[] { x.Item1, x.Item2 }), span = Span }));
            proc.StandardInput.Close();
            Task<string> err = proc.StandardError.ReadToEndAsync();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, err.Result);
            return JsonDocument.Parse(stdout).RootElement;
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string[] DataFiles() => Directory.GetFiles(DataDir, "*.bin").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void ExtentsAndClusterBytesMatchPythonForEveryDataFile()
    {
        string[] files = DataFiles();
        Assert.Equal(64, files.Length);

        JsonElement py = AskPython(files, Array.Empty<string>());

        foreach (string f in files)
        {
            JsonElement expected = py.GetProperty("extents").GetProperty(f);
            IReadOnlyList<Extent>? actual = FileLayout.Extents(f);
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(
                    expected.EnumerateArray().Select(e => new Extent(e[0].GetInt64(), e[1].GetInt64(), e[2].GetInt64())).ToList(),
                    actual);
            }

            JsonElement cs = py.GetProperty("cluster").GetProperty(f);
            Assert.Equal(cs.ValueKind == JsonValueKind.Null ? null : cs.GetInt32(), FileLayout.ClusterBytes(f));
        }
    }

    [Fact]
    public void SampleDigestMatchesPythonOnSmallRanges()
    {
        string[] files = DataFiles().Take(3).ToArray();

        JsonElement py = AskPython(Array.Empty<string>(), files);

        foreach (string f in files)
        {
            Assert.Equal(py.GetProperty("digests").GetProperty(f).GetString(), SampleDigest.Compute(f, new FileInfo(f).Length, Extra, Span));
        }
    }

    [Theory]
    [InlineData(StreamMethod.Pvm)]
    [InlineData(StreamMethod.Read)]
    public void StreamsASmallRealRangeWithoutErrors(StreamMethod method)
    {
        string f = DataFiles()[0];
        var ops = new[] { new ReadOp(f, 0, 1 << 20), new ReadOp(f, 8 << 20, 2 << 20) };

        StreamStats r = ZoneStreamer.Run(ops, method);

        Assert.Equal((3L << 20, 2L, 0L), (r.Bytes, r.Requests, r.Errors));
    }
}
