using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 저널 (F8), Python 저널 이어받기, SHA 표본. Python 의 test_truncated_last_journal_line_*, test_moved_ranges_are_hashed,
/// test_unbuffered_read_failure_* 에 대응.
public sealed class JournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_journal_" + Guid.NewGuid().ToString("N"));

    public JournalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Tmp(string name) => Path.Combine(_dir, name);

    private static List<(JObject Row, string Line)> F8Rows()
    {
        using JsonDocument doc = PlanFixtures.Doc("synthetic/journal_rows.json");
        return doc.RootElement.EnumerateArray()
            .Select(e => ((JObject)JsonRead.Parse(e.GetProperty("row").GetRawText())!, e.GetProperty("line").GetString()!)).ToList();
    }

    // ---- 잘린 꼬리 복구는 원본을 지우지 않는다

    private static string WriteTruncatedJournal(string path)
    {
        string good = PyJson.Dumps(new JObject { { "type", "header" } }) + "\r\n" + PyJson.Dumps(new JObject { { "type", "step" }, { "idx", 0L } }) + "\r\n";
        File.WriteAllText(path, good + "{\"type\": \"step\", \"idx\"");
        return good;
    }

    [Fact]
    public void TailRecoveryFailureLeavesTheOriginalJournalIntact()
    {
        string path = Tmp("trunc.jsonl");
        WriteTruncatedJournal(path);
        byte[] original = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".tmp"); // 교체용 임시 파일을 만들 수 없게 해 복구 도중 실패를 흉내

        using (var jr = new Journal(path))
        {
            Assert.ThrowsAny<Exception>(() => jr.Add(new JObject { { "type", "end" } }));
        }

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void TailRecoveryKeepsTheOriginalAsBakAndReplacesTheJournal()
    {
        string path = Tmp("trunc2.jsonl");
        string good = WriteTruncatedJournal(path);
        byte[] original = File.ReadAllBytes(path);

        using (var jr = new Journal(path))
        {
            jr.Add(new JObject { { "type", "end" } });
        }

        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(3, new Journal(path).Rows.Count);
        Assert.StartsWith(good, File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---- F8

    [Fact]
    public void F8_RowsSerializeLikePython()
    {
        foreach ((JObject row, string line) in F8Rows())
        {
            PlanFixtures.With(line, () => Assert.Equal(line, PyJson.Dumps(row)));
        }
    }

    [Fact]
    public void F8_JournalWritesPythonBytes()
    {
        List<(JObject Row, string Line)> rows = F8Rows();
        using (var jr = new Journal(Tmp("j.jsonl")))
        {
            foreach ((JObject row, _) in rows)
            {
                jr.Add(row);
            }
        }

        byte[] bytes = File.ReadAllBytes(Tmp("j.jsonl"));

        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => r.Line + "\r\n"))), bytes);
        Assert.NotEqual(0xEF, bytes[0]); // BOM 없음
        var reread = new Journal(Tmp("j.jsonl"));
        Assert.Equal(rows.Count, reread.Rows.Count);
        Assert.Equal(rows.Select(r => PyJson.Dumps(r.Row)), reread.Rows.Select(r => PyJson.Dumps(r)));
    }

    [Fact]
    public void F8_ReadsE3JournalAndReconstructsEveryStepState()
    {
        var jr = new Journal(PlanFixtures.PathOf("e3/journal.jsonl"));

        Assert.Equal(2162, jr.Rows.Count);
        Assert.NotNull(jr.First("header", "plan_sha", "7d64ea23fe801a163474da54bbb63ce1c5523dc107c4cccec5806319eff74424"));
        for (long i = 0; i < 1048; i++)
        {
            Assert.Equal("done", jr.LastState(i));
        }

        Assert.Null(jr.LastState(1048));
        JObject end = jr.Rows[^1];
        Assert.Equal("end", end["type"]);
        Assert.Equal(243531776L, end["written_bytes"]);
        Assert.Equal("cb3920ad00f5b3e5b8d71cf45597eb7c97c01a79882db493fd7d0c0cbd54014d", jr.First("file", "path", @"D:\AsterBench\data\f008.bin")!["sha_before"]);
    }

    [Fact]
    public void F8_E3JournalRewrittenByCSharpIsByteIdentical()
    {
        byte[] original = PlanFixtures.Bytes("e3/journal.jsonl");
        using (var src = new Journal(PlanFixtures.PathOf("e3/journal.jsonl")))
        using (var dst = new Journal(Tmp("copy.jsonl")))
        {
            foreach (JObject r in src.Rows)
            {
                dst.Add(r);
            }
        }

        Assert.Equal(original, File.ReadAllBytes(Tmp("copy.jsonl")));
    }

    [Fact]
    public void F8_TruncatedLastLineIsDroppedAndAppendsStayValid()
    {
        byte[] cut = PlanFixtures.Bytes("synthetic/journal_truncated.jsonl");
        File.WriteAllBytes(Tmp("j.jsonl"), cut);

        using (var jr = new Journal(Tmp("j.jsonl")))
        {
            Assert.Equal(50, jr.Rows.Count);
            jr.Add(new JObject { { "type", "end" }, { "stopped", null }, { "written_bytes", 0L }, { "sha_mismatch", new List<object?>() } });
        }

        var again = new Journal(Tmp("j.jsonl"));
        Assert.Equal(51, again.Rows.Count);
        Assert.Equal("end", again.Rows[^1]["type"]);
        byte[] now = File.ReadAllBytes(Tmp("j.jsonl"));
        int keep = cut.Length - Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(cut).Split("\r\n")[^1]); // 온전한 50줄까지
        Assert.Equal(cut[..keep], now[..keep]);
    }

    [Fact]
    public void TruncatedLastLine_PythonCase()
    {
        File.WriteAllText(Tmp("j.jsonl"), "{\"type\": \"header\", \"plan_sha\": \"x\"}\n{\"type\": \"step\", \"idx\": 0, \"sta");
        using (var j = new Journal(Tmp("j.jsonl")))
        {
            Assert.Equal(["header"], j.Rows.Select(r => (string)r["type"]!));
            j.Add(new JObject { { "type", "end" } });
        }

        Assert.Equal(["header", "end"], new Journal(Tmp("j.jsonl")).Rows.Select(r => (string)r["type"]!));
    }

    [Fact]
    public void DamageInTheMiddleIsNotSilentlySkipped()
    {
        File.WriteAllText(Tmp("j.jsonl"), "{\"type\": \"header\"\n{\"type\": \"end\"}\n");

        Assert.Throws<PyValueException>(() => new Journal(Tmp("j.jsonl")));
    }

    [Fact]
    public void CompleteLastRowWithoutNewlineDoesNotGlueNextRow()
    {
        File.WriteAllText(Tmp("j.jsonl"), "{\"type\": \"header\", \"plan_sha\": \"x\"}\r\n{\"type\": \"file\", \"path\": \"a\", \"sha_before\": null}");
        using (var j = new Journal(Tmp("j.jsonl")))
        {
            Assert.Equal(2, j.Rows.Count);
            j.Add(new JObject { { "type", "end" } });
        }

        Assert.Equal(3, new Journal(Tmp("j.jsonl")).Rows.Count);
    }

    [Fact]
    public void BlankLinesAreIgnoredAndMissingFileIsEmpty()
    {
        Assert.Empty(new Journal(Tmp("nothing.jsonl")).Rows);
        File.WriteAllText(Tmp("j.jsonl"), "\r\n{\"type\": \"end\"}\r\n   \r\n");

        Assert.Single(new Journal(Tmp("j.jsonl")).Rows);
    }

    [Fact]
    public void LastStateTakesTheLatestAndIgnoresRowsWithoutState()
    {
        using var j = new Journal(Tmp("j.jsonl"));
        j.Add(new JObject { { "type", "step" }, { "idx", 4L }, { "state", "planned" } });
        j.Add(new JObject { { "type", "step" }, { "idx", 4L }, { "state", "failed" }, { "reason", "x" } });
        j.Add(new JObject { { "type", "step" }, { "idx", 4L }, { "state", "done" }, { "reason", null } });
        j.Add(new JObject { { "type", "file" }, { "idx", 4L }, { "state", "other" } });

        Assert.Equal("done", j.LastState(4));
        Assert.Null(j.LastState(5));
    }

    // ---- Python 저널 이어받기: e3 저널 + e3 계획 → 할 일 0, 이동 0

    [Fact]
    public void E3_PythonJournalResumesWithNothingToDo()
    {
        File.Copy(PlanFixtures.PathOf("e3/journal.jsonl"), Tmp("j.jsonl"));
        byte[] original = File.ReadAllBytes(Tmp("j.jsonl"));
        PlanFile plan = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));
        var after = plan.Tasks.GroupBy(t => t.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Vcn).Select(t => new Extent(t.Vcn, t.TargetLcn, t.Clusters)).ToList(), StringComparer.Ordinal);
        using var vol = new FakeVolume(after, "D:");
        var prints = plan.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
        ApplyHooks hooks = new()
        {
            Extents = p => vol.Ext.TryGetValue(p, out List<Extent>? e) ? e : null,
            Digest = (p, size, extra) => throw new InvalidOperationException("nothing should be hashed: " + p),
            SkipReason = _ => null,
            RealPath = p => p,
            Stat = p => prints[p],
        };

        ApplyReport rep = Applier.Apply(plan, vol, [@"D:\AsterBench"], Tmp("j.jsonl"), hooks, new ApplyOptions(Execute: true));

        Assert.Equal((1048, 0, 0), ((int)rep.TasksTotal, (int)rep.TasksTodo, vol.Moves.Count));
        Assert.Empty(rep.Failed);
        Assert.Equal(0, rep.ShaChecked);
        Assert.Equal(95135032L, rep.ZoneStartLcn);
        byte[] now = File.ReadAllBytes(Tmp("j.jsonl"));
        Assert.Equal(original, now[..original.Length]); // 기존 행은 그대로, end 행 하나만 덧붙음
        var jr = new Journal(Tmp("j.jsonl"));
        Assert.Equal(2163, jr.Rows.Count);
        Assert.Equal(1, jr.Rows.Count(r => Equals(r["type"], "header")));
    }

    [Fact]
    public void E3_PlanShaMatchesPythonJournalHeader()
    {
        PlanFile plan = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));

        Assert.NotNull(new Journal(PlanFixtures.PathOf("e3/journal.jsonl")).First("header", "plan_sha", plan.Sha()));
    }

    // ---- SHA 표본

    private static byte[] Pattern(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)((i * 7) + 3);
        }

        return data;
    }

    [Fact]
    public void SampleHasher_MatchesPythonSampleDigest()
    {
        int size = (5 << 20) + 12345;
        File.WriteAllBytes(Tmp("a.bin"), Pattern(size));
        (long, long)[] extra = [((2 << 20) + (512 << 10) - 100, 200), (size - 10, 100), (-5, 10), (size, 10), (7, 0)];

        Assert.Equal("0b015199ede914f773e27470450a2c0193cebddd099b8789aa6f246349b0bd5e", SampleHasher.Compute(Tmp("a.bin"), size, extra, null));
        Assert.Equal("75a7c3ab1735d66838b5654c080d22054249ae64ab91756a52c84e4d7188fbd6", SampleHasher.Compute(Tmp("a.bin"), size, null, null));
        File.WriteAllBytes(Tmp("s.bin"), Pattern(1000));
        Assert.Equal("a4e8ffd03b6e5f8e83fc1cf12f96d270e71b4e35fcd2ac9c06301b1008f49de7", SampleHasher.Compute(Tmp("s.bin"), 1000, [(900, 500)], null));
    }

    [Fact]
    public void SampleHasher_MovedRangesAreHashed()
    {
        byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8 << 20);
        File.WriteAllBytes(Tmp("f.bin"), data);
        int hole = (2 << 20) + (1 << 19); // 처음·가운데·끝 1 MiB 표본 밖
        (long, long)[] extra = [(hole - 100, 200)];
        string plain = SampleHasher.Compute(Tmp("f.bin"), data.Length, null, null);
        string ranged = SampleHasher.Compute(Tmp("f.bin"), data.Length, extra, null);

        data[hole] ^= 1;
        File.WriteAllBytes(Tmp("f.bin"), data);

        Assert.Equal(plain, SampleHasher.Compute(Tmp("f.bin"), data.Length, null, null)); // 옮긴 범위 없이는 안 보임
        Assert.NotEqual(ranged, SampleHasher.Compute(Tmp("f.bin"), data.Length, extra, null));
    }

    [Fact]
    public void SampleHasher_UnbufferedFailureFallsBackToBufferedRead()
    {
        int size = 3000;
        File.WriteAllBytes(Tmp("f.bin"), Pattern(size));
        string buffered = SampleHasher.Compute(Tmp("f.bin"), size, null, null);

        Assert.Equal(buffered, SampleHasher.Compute(Tmp("f.bin"), size, null, (p, o, n) => throw new IOException("denied")));
        Assert.Equal(buffered, SampleHasher.Compute(Tmp("f.bin"), size, null, (p, o, n) => throw new Win32Exception(5)));
        var seen = new List<(long, long)>();
        string viaFn = SampleHasher.Compute(Tmp("f.bin"), size, null, (p, o, n) =>
        {
            seen.Add((o, n));
            return File.ReadAllBytes(p)[(int)o..(int)(o + n)];
        }, span: 1000);
        Assert.Equal(SampleHasher.Compute(Tmp("f.bin"), size, null, null, span: 1000), viaFn);
        Assert.Equal([(0L, 1000L), (1000L, 1000L), (2000L, 1000L)], seen.Distinct().OrderBy(x => x.Item1).Take(3).ToList());
    }

    [Fact]
    public void SampleHasher_UnexpectedExceptionIsNotSwallowed()
    {
        File.WriteAllBytes(Tmp("f.bin"), Pattern(100));

        Assert.Throws<InvalidOperationException>(() => SampleHasher.Compute(Tmp("f.bin"), 100, null, (p, o, n) => throw new InvalidOperationException("bug")));
    }

    [Fact]
    public void MovedRangesAreFirstAndLastSpanOfEveryTask()
    {
        var plan = new PlanFile(1, @"D:\r", "D:", 4096, 65536, new PlanZone(0, 100),
            [new PlanTask("a", 10, 3, 0), new PlanTask("a", 100, 100, 3), new PlanTask("b", 0, 1, 103)], [], [], new JObject());

        Dictionary<string, List<(long Offset, long Length)>> mr = Applier.MovedRanges(plan);

        Assert.Equal(2 * plan.Tasks.Count, mr.Values.Sum(v => v.Count));
        Assert.All(mr.Values.SelectMany(v => v), r => Assert.True(r.Length <= Applier.TaskSpan));
        Assert.Equal([(10L * 4096, 3L * 4096), (10L * 4096, 3L * 4096), (100L * 4096, Applier.TaskSpan), (200L * 4096 - Applier.TaskSpan, Applier.TaskSpan)], mr["a"]);
        Assert.Equal([(0L, 4096L), (0L, 4096L)], mr["b"]);
    }
}
