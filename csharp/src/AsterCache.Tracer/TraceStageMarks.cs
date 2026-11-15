using AsterCache.Core.Trace;

namespace AsterCache.Tracer;

/// 자동 추적 기록의 등록 프로세스 시작을 단계 표지로 변환합니다.
public static class TraceStageMarks
{
    public static IReadOnlyList<StageMark> FromRecords(IEnumerable<TraceRecord> records, IReadOnlyCollection<string> exeNames, long settleMs = 10_000)
    {
        if (settleMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settleMs));
        }

        var registered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string exe in exeNames)
        {
            string name = FileName(exe);
            if (name.Length > 0)
            {
                registered.TryAdd(name, name);
            }
        }

        DateTime? start = null;
        var firstStarts = new List<StageMark>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TraceRecord record in records)
        {
            start ??= record.Time;
            if (record.Kind != RecordKind.ProcessStart)
            {
                continue;
            }

            string image = FileName(record.Path);
            string? exe = registered.TryGetValue(image, out string? exact)
                ? exact
                : image.Length == 15 ? registered.Values.FirstOrDefault(name => name.StartsWith(image, StringComparison.OrdinalIgnoreCase)) : null;
            if (start is not { } began || exe is null || !seen.Add(exe))
            {
                continue;
            }

            long tMs = Math.Max(0, (long)(record.Time - began).TotalMilliseconds);
            firstStarts.Add(new StageMark(exe, tMs));
        }

        StageMark? first = firstStarts.Where(m => m.TMs <= settleMs).MinBy(m => m.TMs);
        var result = new List<StageMark> { first is null ? new StageMark("", 0) : new StageMark(first.Exe, 0) };
        result.AddRange(firstStarts.Where(m => m.TMs > settleMs).OrderBy(m => m.TMs));
        return result;
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
