using AsterCache.Core.Trace;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class TraceWriterTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "tracer", name);

    /// Python astercache_v07 trace 가 같은 CSV 두 개로 만든 파일과 바이트가 같음 (CRLF, 비ASCII 원문, 합집합 64 KiB 블록).
    [Fact]
    public void BytesMatchPythonWriteTraces()
    {
        const string Root = @"Q:\AsterGame\";
        IReadOnlyList<TraceEvent>[] sessions =
        [
            ProcmonCsv.LoadEvents(Fixture("procmon_run1.csv"), Root).Events,
            ProcmonCsv.LoadEvents(Fixture("procmon_run2.csv"), Root).Events,
        ];
        string dir = Directory.CreateTempSubdirectory("astercache-tracer-").FullName;
        try
        {
            string trace = Path.Combine(dir, "trace.json");

            (List<string> runs, int count) = TraceWriter.Write(sessions, trace);

            Assert.Equal(1035, count);
            Assert.Equal(2, runs.Count);
            foreach (string name in new[] { "trace.json", "trace.run1.json", "trace.run2.json" })
            {
                Assert.True(File.ReadAllBytes(Fixture("expected/" + name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, name))), name);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RunPathFollowsPathlibStemAndSuffix()
    {
        Assert.Equal(@"C:\t\trace.run1.json", TraceWriter.RunPath(@"C:\t\trace.json", 1));
        Assert.Equal(@"C:\t\a.tar.run12.gz", TraceWriter.RunPath(@"C:\t\a.tar.gz", 12));
        Assert.Equal(@"C:\t\trace.run2", TraceWriter.RunPath(@"C:\t\trace", 2));
        Assert.Equal(@"C:\t\.hidden.run1", TraceWriter.RunPath(@"C:\t\.hidden", 1));
        Assert.Equal(@"C:\t\x..run1", TraceWriter.RunPath(@"C:\t\x.", 1)); // 끝 점은 확장자가 아님
        Assert.Equal("trace.run1.json", TraceWriter.RunPath("trace.json", 1));
    }

    [Fact]
    public void UnionKeepsTheTimeOfTheFirstReadOfEachBlock()
    {
        IReadOnlyList<TraceEvent>[] sessions =
        [
            [new TraceEvent("p", 0, 65_536 * 2, 8, 100), new TraceEvent("p", 65_536, 65_536 * 2, 8, 900)], // 블록 1 은 100 ms 에 처음, 블록 2 는 900 ms
            [new TraceEvent("q", 0, 1)],                                                                      // t_ms 없는 행은 없는 채로
        ];

        Assert.Equal([100L, 100L, 900L, null], TraceWriter.Union(sessions).Select(e => e.TMs));
    }

    [Fact]
    public void UnionKeepsFirstSeenBlockOrderAndCostPerEvent()
    {
        IReadOnlyList<TraceEvent>[] sessions =
        [
            [new TraceEvent("p", 65_530, 20, 3.5), new TraceEvent("p", 0, 1, 9)],  // 블록 0 과 1 에 걸침; 블록 0 은 이미 본 것
            [new TraceEvent("q", 131_072, 65_536), new TraceEvent("p", 65_536, 65_536 * 2, 1)],
        ];

        List<TraceEvent> union = TraceWriter.Union(sessions);

        Assert.Equal(
            [
                new TraceEvent("p", 0, 65_536, 3.5),
                new TraceEvent("p", 65_536, 65_536, 3.5),
                new TraceEvent("q", 131_072, 65_536, 8.0),
                new TraceEvent("p", 131_072, 65_536, 1),
            ],
            union);
    }

    [Fact]
    public void EmptySessionWritesEmptyListWithCrlf()
    {
        string dir = Directory.CreateTempSubdirectory("astercache-tracer-").FullName;
        try
        {
            string trace = Path.Combine(dir, "t.json");

            TraceWriter.Write([[]], trace);

            Assert.Equal("[]\r\n"u8.ToArray(), File.ReadAllBytes(trace));
            Assert.Equal("[]\r\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(dir, "t.run1.json")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
