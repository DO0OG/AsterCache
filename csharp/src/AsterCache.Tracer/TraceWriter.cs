using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Tracer;

/// astercache_v07.write_traces: 실행별 `<stem>.run<N><suffix>` (접근 순서 그대로) 와 합집합 `out` (64 KiB 블록마다 한 번, 처음 본 순서, cost 유지).
/// 바이트는 Python write_text(json.dumps(events, ensure_ascii=False) + '\n') 와 같습니다: UTF-8 원문, 줄바꿈 CRLF.
public static class TraceWriter
{
    public const long Block = 65536;

    public static (List<string> Runs, int UnionCount) Write(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, string outPath)
    {
        var runs = new List<string>();
        for (int i = 0; i < sessions.Count; i++)
        {
            string f = RunPath(outPath, i + 1);
            WriteFile(f, sessions[i]);
            runs.Add(f);
        }

        var union = Union(sessions);
        WriteFile(outPath, union);
        return (runs, union.Count);
    }

    public static List<TraceEvent> Union(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions)
    {
        var seen = new HashSet<(string, long)>();
        var union = new List<TraceEvent>();
        foreach (IReadOnlyList<TraceEvent> events in sessions)
        {
            foreach (TraceEvent e in events)
            {
                for (long blk = e.Offset / Block; blk <= (e.Offset + e.Length - 1) / Block; blk++)
                {
                    if (seen.Add((e.Path, blk)))
                    {
                        union.Add(new TraceEvent(e.Path, blk * Block, Block, e.Cost, e.TMs)); // 블록을 처음 읽은 시각
                    }
                }
            }
        }

        return union;
    }

    /// out.with_name('%s.run%d%s' % (out.stem, i, out.suffix)). stem·suffix 는 pathlib 규칙 (맨 앞 점과 끝 점은 확장자가 아님).
    public static string RunPath(string outPath, int index)
    {
        string name = Path.GetFileName(outPath);
        int dot = name.LastIndexOf('.');
        bool hasSuffix = dot > 0 && dot < name.Length - 1;
        string stem = hasSuffix ? name[..dot] : name;
        string suffix = hasSuffix ? name[dot..] : "";
        return Path.Combine(Path.GetDirectoryName(outPath) ?? "", $"{stem}.run{index}{suffix}");
    }

    private static void WriteFile(string path, IEnumerable<TraceEvent> events)
    {
        string text = PyJson.Dumps(events.Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
        File.WriteAllBytes(path, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
    }
}
