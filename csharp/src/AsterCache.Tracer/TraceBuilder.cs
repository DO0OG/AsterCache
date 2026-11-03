using AsterCache.Core.Trace;

namespace AsterCache.Tracer;

public readonly record struct BuildStats(int Reads, int Parsed, int Ignored, bool Truncated);

/// 기록 → 추적 한 번의 실행. 대상 프로세스 트리의 읽기만, 루트 접두 안의 경로만 (procmon_import.parse_procmon_csv 와 같은 규칙).
public static class TraceBuilder
{
    public const long MaxLength = 64L << 20;

    public static (List<TraceEvent> Events, BuildStats Stats) Build(IEnumerable<TraceRecord> records, IReadOnlyCollection<string> exeNames,
        string rootPrefix, int maxEvents = TraceEvents.MaxSessionEvents)
    {
        if (maxEvents < 1 || maxEvents > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "max_events invalid");
        }

        var tree = new ProcessTree(exeNames);
        string prefix = rootPrefix.Length > 0 ? NtPath.NormCase(NtPath.NormPath(rootPrefix)) : "";
        var events = new List<TraceEvent>();
        DateTime? start = null; // 첫 기록의 시각 = 세션 시작 (t_ms 의 0)
        int reads = 0;
        int ignored = 0;
        bool truncated = false;
        foreach (TraceRecord r in records)
        {
            start ??= r.Time;
            switch (r.Kind)
            {
                case RecordKind.ProcessStart:
                    tree.Start(r.Pid, r.ParentPid, r.Path);
                    break;
                case RecordKind.ProcessStop:
                    tree.Stop(r.Pid);
                    break;
                case RecordKind.Write:
                    break;
                default:
                    if (!tree.IsTarget(r.Pid))
                    {
                        break;
                    }

                    reads++;
                    bool inRoot = prefix.Length == 0 || NtPath.IsUnder(NtPath.NormCase(NtPath.NormPath(r.Path)), prefix);
                    if (!inRoot || r.Offset < 0 || r.Length <= 0 || r.Length > MaxLength)
                    {
                        ignored++;
                        break;
                    }

                    events.Add(new TraceEvent(r.Path, r.Offset, r.Length, TMs: Math.Max(0, (long)(r.Time - start.Value).TotalMilliseconds)));
                    break;
            }

            if (events.Count >= maxEvents)
            {
                truncated = true;
                break;
            }
        }

        return (events, new BuildStats(reads, events.Count, ignored, truncated));
    }

    /// 대상 프로세스 트리가 쓴 루트 안의 파일 (대소문자 무시). 읽기 트레이스와 따로 보는 이유: 쓰는 파일은 계획에서 빼기 위함.
    public static HashSet<string> WrittenPaths(IEnumerable<TraceRecord> records, IReadOnlyCollection<string> exeNames, string rootPrefix)
    {
        var tree = new ProcessTree(exeNames);
        string prefix = rootPrefix.Length > 0 ? NtPath.NormCase(NtPath.NormPath(rootPrefix)) : "";
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TraceRecord r in records)
        {
            switch (r.Kind)
            {
                case RecordKind.ProcessStart:
                    tree.Start(r.Pid, r.ParentPid, r.Path);
                    break;
                case RecordKind.ProcessStop:
                    tree.Stop(r.Pid);
                    break;
                case RecordKind.Write when r.Path.Length > 0 && tree.IsTarget(r.Pid) && (prefix.Length == 0 || NtPath.IsUnder(NtPath.NormCase(NtPath.NormPath(r.Path)), prefix)):
                    written.Add(r.Path);
                    break;
            }
        }

        return written;
    }

    /// astercache_v07.load_events 의 두 번째 절반: 경로마다 디스크의 실제 대소문자를 씁니다.
    /// 대소문자만 다른 결과만 받아들입니다 (subst·UNC·8.3 등 다른 해석은 무시). realPath 가 null (파일 없음) 이면 Python
    /// os.path.realpath 처럼 normpath 한 경로를 씁니다.
    public static List<TraceEvent> CanonicalizeCase(IEnumerable<TraceEvent> events, Func<string, string?>? realPath = null)
    {
        realPath ??= RealPath.Resolve;
        var canon = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<TraceEvent>();
        foreach (TraceEvent e in events)
        {
            if (!canon.TryGetValue(e.Path, out string? path))
            {
                string r = realPath(e.Path) ?? NtPath.NormPath(e.Path);
                path = NtPath.NormCase(NtPath.NormPath(r)) == NtPath.NormCase(NtPath.NormPath(e.Path)) ? r : e.Path;
                canon[e.Path] = path;
            }

            result.Add(e with { Path = path });
        }

        return result;
    }
}
