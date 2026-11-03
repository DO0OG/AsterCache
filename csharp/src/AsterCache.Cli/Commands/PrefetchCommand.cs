using System.Diagnostics;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// prefetch/cli.py. 계획이나 트레이스의 핫 범위를 스케줄해 --execute 면 파일 캐시로 읽어 들입니다. 쓰기 없음.
internal static class PrefetchCommand
{
    public static readonly string[] Methods = ["read", "pvm", "pvm-file", "overlapped", "mmap-touch", "ovl2", "ovl3", "ovl4"];

    public static readonly ArgOption[] Options =
    [
        new("plan"),
        new("trace"),
        new("origin-root"),
        new("budget-mib", Type: ArgType.Int, Default: "1024"),
        new("strategy", Choices: ["physical", "path", "none"], Default: "physical"),
        new("read-mbps", Type: ArgType.Double, Default: "100.0"),
        new("seek-ms", Type: ArgType.Double, Default: "12.0"),
        new("gap-kib", Type: ArgType.Int, Default: "0"),
        new("full-file-ratio", Type: ArgType.Double, Default: "0.3"),
        new("threads", Type: ArgType.Int, Default: "1"),
        new("chunk-kib", Type: ArgType.Int, Default: "1024"),
        new("no-reuse", ArgKind.Flag),
        new("seq-hint", ArgKind.Flag),
        new("method", Choices: Methods, Default: "pvm"),
        new("execute", ArgKind.Flag),
        new("timing", ArgKind.Flag),
    ];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string? trace = a.Text("plan") ?? a.Text("trace") ?? EnvOrNull("ASTER_TRACE");
        string? root = a.Text("origin-root") ?? EnvOrNull("ASTER_ROOT");
        if (trace is null || root is null)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Prefetch_NeedArgs"));
        }

        string method = a.Text("method")!;
        long threads = a.Int("threads")!.Value;
        if (a.Int("budget-mib")! < 1 || a.Int("gap-kib")! < 0 || a.Double("read-mbps")! <= 0 || a.Double("seek-ms")! < 0
            || threads < 1 || threads > (method == "overlapped" ? 64 : 8) || a.Int("chunk-kib")! < 4)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Prefetch_BadNumbers"));
        }

        StageTimes? times = a.Flag("timing") ? new StageTimes() : null;
        var total = Stopwatch.StartNew();
        (List<ReadOp> ops, ScheduleInfo info) = Schedule(PathText.Str(trace), PathText.Str(root), a.Text("strategy")!, a.Int("budget-mib")!.Value,
            a.Int("gap-kib")!.Value, a.Double("full-file-ratio")!.Value, times);
        JObject json = InfoJson(a.Text("strategy")!, a.Flag("seq-hint"), info);
        if (a.Flag("execute"))
        {
            StreamMethod m = method switch
            {
                "pvm" => StreamMethod.Pvm,
                "ovl2" or "ovl3" or "ovl4" or "overlapped" => StreamMethod.Overlapped,
                "read" when threads == 1 && !a.Flag("no-reuse") && !a.Flag("seq-hint") => StreamMethod.Read,
                _ => throw new NotSupportedException("this method/option combination is not available in the C# build: use --method pvm/ovl2/ovl3/ovl4, or --method read without --threads/--no-reuse/--seq-hint"),
            };
            StreamStats s = ZoneStreamer.Run(ops, m, checked((int)a.Int("chunk-kib")!.Value * 1024),
                method.StartsWith("ovl", StringComparison.Ordinal) ? method[3] - '0' : (int)threads);
            json["fallback"] = false; // Python 은 읽기 쪽 fallback (SEQUENTIAL_SCAN 열기 실패) 값으로 덮어씀
            json.Add("bytes", s.Bytes);
            json.Add("hint_accepted_bytes", s.HintAcceptedBytes);
            json.Add("requests", s.Requests);
            json.Add("errors", s.Errors);
            json.Add("seconds", s.Seconds);
            json.Add("method", method);
            times?.Add("stream_ms", s.Seconds * 1000);
        }
        else
        {
            json.Add("dry_run", true);
            json.Add("bytes", ops.Sum(op => op.Length));
            json.Add("requests", (long)ops.Count);
            json.Add("ops", ops.Select(op => (object?)new List<object?> { op.Path, op.Offset, op.Length }).ToList());
        }

        if (times is not null)
        {
            times.Add("total_in_command_ms", total.Elapsed.TotalMilliseconds);
            json.Add("timing_ms", times.ToJson());
        }

        return AsterCli.Emit(o, json);
    }

    /// 트레이스나 계획을 읽어 (루트 안의 파일만, 파일 크기로 자름) 스케줄합니다. launch 도 씁니다.
    internal static (List<ReadOp> Ops, ScheduleInfo Info) Schedule(string trace, string root, string strategy, long budgetMib, long gapKib, double fullFileRatio,
        StageTimes? times = null)
    {
        Func<string, string?> resolve = times is null ? RealPaths.Resolve : p => times.Time("resolve", () => RealPaths.Resolve(p));
        Func<string, FileLayoutInfo?> layout = times is null ? FileLayout.Get : p => times.Time("layout", () => FileLayout.Get(p));
        Func<string, long> size = times is null ? p => new FileInfo(p).Length : p => times.Time("size", () => new FileInfo(p).Length);
        var sw = Stopwatch.StartNew();
        List<ReadRange> ranges = TraceLoader.LoadRanges(trace, root, resolve).Select(e => new ReadRange(e.Path, e.Offset, e.Length)).ToList();
        times?.Add("load_ms", sw.Elapsed.TotalMilliseconds);
        sw.Restart();
        var result = Scheduler.Schedule(ranges, strategy, budgetMib * Scheduler.Mib, gapKib * 1024, layout, size, fullFileRatio);
        times?.Add("schedule_ms", sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// --timing: 단계별 시간 (load = 추적 읽기 + 경로 해석, schedule = 스케줄 + 배치 조회, stream = 읽기) 과 안쪽 호출의 합계·횟수.
    internal sealed class StageTimes
    {
        private readonly OrderedDictionary<string, double> _ms = [];
        private readonly Dictionary<string, long> _calls = [];

        public void Add(string key, double ms) => _ms[key] = _ms.GetValueOrDefault(key) + ms;

        public T Time<T>(string key, Func<T> work)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                return work();
            }
            finally
            {
                Add(key + "_ms", Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                _calls[key + "_calls"] = _calls.GetValueOrDefault(key + "_calls") + 1;
            }
        }

        public JObject ToJson()
        {
            var j = new JObject();
            foreach ((string k, double v) in _ms)
            {
                j.Add(k, Math.Round(v, 1));
            }

            foreach ((string k, long v) in _calls)
            {
                j.Add(k, v);
            }

            return j;
        }
    }

    internal static JObject InfoJson(string strategy, bool seqHint, ScheduleInfo info) => new()
    {
        { "strategy", strategy },
        { "seq_hint", seqHint },
        { "fallback", info.Fallback },
        { "layout_ok_files", (long)info.LayoutOkFiles },
        { "layout_files", (long)info.LayoutFiles },
        { "full_files", (long)info.FullFiles },
        { "gap_bytes", info.GapBytes },
    };

    internal static string? EnvOrNull(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
