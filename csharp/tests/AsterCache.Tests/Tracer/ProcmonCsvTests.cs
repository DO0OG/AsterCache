using System.Text;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// procmon_import.parse_procmon_csv / astercache_v07.load_events 포팅 시험과 tests/test_game_trace.py 대응.
/// 픽스처 CSV 와 expected/* 는 Python 참조 구현이 같은 CSV 로 만든 것 (프로세스 모니터 실제 열 배치, 쉼표 천 단위, BOM, QUOTE_ALL).
public sealed class ProcmonCsvTests
{
    private const string Root = @"Q:\AsterGame\"; // 없는 드라이브: 실제 경로 해석이 일어나지 않음

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "tracer", name);

    [Fact]
    public void ParseCountsRowsLikePython()
    {
        (List<TraceEvent> e1, ProcmonStats s1) = ProcmonCsv.Parse(Fixture("procmon_run1.csv"), Root);
        (List<TraceEvent> e2, ProcmonStats s2) = ProcmonCsv.Parse(Fixture("procmon_run2.csv"), Root);
        (List<TraceEvent> e3, ProcmonStats s3) = ProcmonCsv.Parse(Fixture("procmon_plain.csv"), Root);

        // Python: {'rows': 13, 'read_success_rows': 11, 'parsed': 6, 'ignored': 7}
        Assert.Equal(new ProcmonStats(13, 11, 6, 7), s1);
        Assert.Equal(new ProcmonStats(4, 4, 4, 0), s2);
        Assert.Equal(new ProcmonStats(2, 2, 2, 0), s3);
        Assert.Equal(
            [
                new TraceEvent(@"Q:\AsterGame\A.bin", 1000, 4096),
                new TraceEvent(@"Q:\AsterGame\b.bin", 70000, 65536),
                new TraceEvent(@"Q:\AsterGame\data\한글.pak", 0x10000, 0x2000),  // 16진수
                new TraceEvent(@"Q:\AsterGame\c.bin", 5, 6),                     // 소문자 readfile / success
                new TraceEvent(@"Q:\AsterGame\c.bin", 9, 64L << 20),             // 길이 상한 경계 포함
                new TraceEvent("q:/astergame/./sub/../d.bin", 131072, 65536),    // 천 단위 쉼표
            ],
            e1);
        Assert.Equal(4, e2.Count);
        Assert.Equal(
            [new TraceEvent(@"Q:\AsterGame\p.bin", 4096, 8192), new TraceEvent("Q:\\AsterGame\\q \"x\".bin", 0, 1)],
            e3);
    }

    [Fact]
    public void PrefixIsOptionalAndRespectsFolderBoundary()
    {
        (List<TraceEvent> all, _) = ProcmonCsv.Parse(Fixture("procmon_run1.csv"));
        (List<TraceEvent> under, _) = ProcmonCsv.Parse(Fixture("procmon_run1.csv"), @"Q:\AsterGame");

        Assert.Contains(all, e => e.Path.StartsWith(@"C:\Windows", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Path.StartsWith(@"Q:\AsterGameOther", StringComparison.Ordinal));
        Assert.Equal(6, under.Count);
        Assert.DoesNotContain(under, e => e.Path.StartsWith(@"Q:\AsterGameOther", StringComparison.Ordinal));
    }

    [Fact]
    public void MaxEventsStopsEarlyAndInvalidLimitThrows()
    {
        (List<TraceEvent> two, ProcmonStats stats) = ProcmonCsv.Parse(Fixture("procmon_run1.csv"), Root, maxEvents: 2);

        Assert.Equal(2, two.Count);
        Assert.Equal(2, stats.Parsed);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcmonCsv.Parse(Fixture("procmon_run1.csv"), Root, maxEvents: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcmonCsv.Parse(Fixture("procmon_run1.csv"), Root, maxEvents: 10_000_001));
    }

    [Fact]
    public void MissingRequiredColumnsAreRejected()
    {
        using var dir = new Scratch();
        string noDetail = dir.Write("bad.csv", "Operation,Path,Result\nReadFile,x,SUCCESS\n");
        string empty = dir.Write("empty.csv", "");

        Assert.Throws<FormatException>(() => ProcmonCsv.Parse(noDetail));
        Assert.Throws<FormatException>(() => ProcmonCsv.Parse(empty));
    }

    [Fact]
    public void QuotedFieldsMayHoldCommasQuotesAndNewlines()
    {
        using var dir = new Scratch();
        string csv = dir.Write("q.csv", "Operation,Path,Result,Detail\r\n\"ReadFile\",\"Q:\\a,b\\\"\"c\"\".bin\",SUCCESS,\"Offset: 1,\r\nLength: 2\"\r\nReadFile,Q:\\z,SUCCESS,Offset: 3 Length: 4");

        (List<TraceEvent> events, ProcmonStats stats) = ProcmonCsv.Parse(csv);

        // 두 번째 행은 Offset: 3 다음에 Length: 가 이어 붙어 있어도 정규식이 찾습니다 (Python re.search 와 같음).
        Assert.Equal([new TraceEvent("Q:\\a,b\\\"c\".bin", 1, 2), new TraceEvent(@"Q:\z", 3, 4)], events);
        Assert.Equal(2, stats.Rows);
    }

    [Fact]
    public void LoadEventsGivesRealOnDiskLetterCase()
    {
        using var dir = new Scratch();
        string real = Path.Combine(dir.Path, "Game", "Data", "A.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllBytes(real, new byte[100]);
        string spelled = real.ToLowerInvariant();
        string csv = dir.Write("run.csv", "Operation,Path,Result,Detail\n"
            + $"ReadFile,{spelled},SUCCESS,\"Offset: 0, Length: 10\"\n"
            + $"ReadFile,{real},SUCCESS,\"Offset: 10, Length: 10\"\n");

        (List<TraceEvent> events, _) = ProcmonCsv.LoadEvents(csv, Path.Combine(dir.Path, "Game"));

        Assert.Equal([real, real], events.Select(e => e.Path));
    }

    /// tests/test_game_trace.py: 실행별 + 합집합 추적, 대소문자 변형 병합, prefetch/relayout 로더가 그대로 읽음.
    [Fact]
    public void TraceUnionPerRunAndLoaderAcceptsIt()
    {
        using var dir = new Scratch();
        string root = Path.Combine(dir.Path, "Game");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "A.bin");
        string b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, new byte[400_000]);
        File.WriteAllBytes(b, new byte[200_000]);
        string spelled = a.ToLowerInvariant();
        string other = @"C:\Windows\x.dll";
        string[] csvs =
        [
            dir.Write("run1.csv", ProcmonText([Row(a, 1_000, 4_096), Row(b, 70_000, 65_536), Row(b, 0, 4_096, "END OF FILE"), Row(other, 0, 4_096)])),
            dir.Write("run2.csv", ProcmonText([Row(spelled, 1_000, 4_096), Row(a, 200_000, 100_000)])),
        ];

        List<IReadOnlyList<TraceEvent>> sessions = csvs.Select(c => (IReadOnlyList<TraceEvent>)ProcmonCsv.LoadEvents(c, root + Path.DirectorySeparatorChar).Events).ToList();
        string trace = Path.Combine(dir.Path, "trace.json");
        (List<string> runs, int count) = TraceWriter.Write(sessions, trace);

        Assert.Equal([Path.Combine(dir.Path, "trace.run1.json"), Path.Combine(dir.Path, "trace.run2.json")], runs);
        Assert.Equal([(1_000L, 4_096L), (70_000L, 65_536L)], sessions[0].Select(e => (e.Offset, e.Length)));
        List<TraceEvent> union = TraceLoader.LoadRanges(trace, root);
        Assert.Equal(count, union.Count);
        var keys = union.Select(e => (Path.GetFileName(e.Path), e.Offset / 65536)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count()); // 블록당 하나, 대소문자 변형 병합
        Assert.Equal([("b.bin", 1L), ("b.bin", 2L)], keys.Where(k => k.Item1 == "b.bin").OrderBy(k => k.Item2));
        Assert.Equal([("A.bin", 0L), ("A.bin", 3L), ("A.bin", 4L)], keys.Where(k => k.Item1 == "A.bin").OrderBy(k => k.Item2));
        Assert.All(union.Take(2), e => Assert.Equal(65536, e.Length));
        Assert.All(union, e => Assert.Equal(a.Equals(e.Path, StringComparison.OrdinalIgnoreCase) ? a : b, e.Path));
    }

    private static string[] Row(string path, long off, long n, string result = "SUCCESS") =>
    [
        "1:00:00.0000001 PM", "game.exe", "42", "ReadFile", path, result,
        $"Offset: {off:N0}, Length: {n:N0}, I/O Flags: Non-cached, Paging I/O, Synchronous Paging I/O, Priority: Normal",
    ];

    private static string ProcmonText(string[][] rows)
    {
        string Line(IEnumerable<string> f) => string.Join(',', f.Select(x => "\"" + x.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        string[] header = ["Time of Day", "Process Name", "PID", "Operation", "Path", "Result", "Detail"];
        return Line(header) + "\r\n" + string.Concat(rows.Select(r => Line(r) + "\r\n"));
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("astercache-tracer-").FullName;

        public string Write(string name, string text)
        {
            string p = System.IO.Path.Combine(Path, name);
            File.WriteAllText(p, text, new UTF8Encoding(true));
            return p;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // 임시 폴더 정리 실패는 시험 결과와 무관.
            }
        }
    }
}
