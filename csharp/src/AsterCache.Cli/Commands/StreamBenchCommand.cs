using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Prefetch.Modes;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// 스트림 방식 비교 측정 (Python bench/prefetch_methods.py 와 같은 절차). 방식을 번갈아, 시도마다 비관리자 콜드 -> 스트림 -> 버퍼드 재생.
/// 읽기만 합니다. --with-python 이면 각 방식의 Python 판 (prefetch.cli --execute) 을 같은 콜드 조건으로 끼워 넣습니다.
internal static class StreamBenchCommand
{
    public static readonly ArgOption[] Options =
    [
        new("root", Required: true),
        new("modes", Default: "qd1,pvm,ovl2,ovl3,ovl4"),
        new("repeat", Type: ArgType.Int, Default: "5"),
        new("flush-mib", Type: ArgType.Int, Default: "300"),
        new("out"),
        new("with-python", ArgKind.Flag),
        new("python", Default: "python"),
        new("python-repo"),
    ];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string root = PathText.Str(a.Text("root")!);
        long repeat = a.Int("repeat")!.Value;
        long flushMib = a.Int("flush-mib")!.Value;
        string[] names = a.Text("modes")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (repeat < 1 || flushMib < 0 || names.Length == 0)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Bench_BadNumbers"));
        }

        string trace = Path.Combine(root, "trace.json");
        var modes = names.Select(StreamBench.ParseMode).ToList();
        if (a.Flag("with-python"))
        {
            string repo = a.Text("python-repo") is { } r ? PathText.Str(r) : FindPythonRepo(a.Lang);
            modes.AddRange(names.Select(n => StreamBench.ParsePythonMode(n, a.Text("python")!, repo, trace, root)));
        }

        List<ReadOp> ops = PrefetchCommand.Schedule(trace, root, "physical", 1024, 0, 0.3).Ops;
        List<ReplayEvent> events = StreamBench.LoadEvents(trace);
        List<ReadOp> flush = ColdMaker.ColdOps(ops, (int)flushMib, FileLayout.Get);
        List<BenchRow> rows = StreamBench.Run(ops, events, flush, modes, (int)repeat, Console.Error.WriteLine);

        JObject result = Summarize(ops, flush, rows);
        if (a.Text("out") is { } outPath)
        {
            string full = PathText.Str(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(full))!);
            File.WriteAllText(full, PyJson.Dumps(result, indent: 1) + "\n", PyJson.Utf8);
        }

        return AsterCli.Emit(o, result);
    }

    /// 표: 방식별 중앙값. combined = 스트림 + 재생 (후보 (a) 의 성공 기준 값), vs_qd1 = combined 중앙값 / qd1 의 combined 중앙값.
    internal static JObject Summarize(IReadOnlyList<ReadOp> ops, IReadOnlyList<ReadOp> flush, IReadOnlyList<BenchRow> rows)
    {
        double Med(IEnumerable<BenchRow> v, Func<BenchRow, double> f) => Math.Round(StreamBench.Median(v.Select(f)), 4);
        IGrouping<string, BenchRow>[] groups = rows.GroupBy(r => r.Mode).ToArray();
        double? qd1 = groups.FirstOrDefault(g => g.Key == "qd1") is { } q ? Med(q, r => r.StreamS + r.Replay.TotalSeconds) : null;
        bool crcOk = rows.Select(r => r.Replay.Crc32).Distinct().Count() == 1;
        var table = new List<object?>();
        foreach (IGrouping<string, BenchRow> g in groups.OrderBy(g => Med(g, r => r.StreamS)))
        {
            double combined = Med(g, r => r.StreamS + r.Replay.TotalSeconds);
            table.Add(new JObject
            {
                { "mode", g.Key },
                { "stream_s", Med(g, r => r.StreamS) },
                { "min_s", g.Min(r => r.StreamS) },
                { "max_s", g.Max(r => r.StreamS) },
                { "replay_s", Med(g, r => r.Replay.TotalSeconds) },
                { "combined_s", combined },
                { "vs_qd1", qd1 is > 0 ? Math.Round(combined / qd1.Value, 3) : null },
                { "cpu_s", Med(g, r => r.CpuS) },
                { "slow_ratio", Math.Round(StreamBench.Median(g.Select(r => r.Replay.SlowRatio)), 4) },
                { "bytes", g.First().Bytes },
                { "errors", g.Sum(r => r.Errors) },
                { "n", (long)g.Count() },
            });
        }

        return new JObject
        {
            { "ops", (long)ops.Count },
            { "hot_bytes", ops.Sum(x => x.Length) },
            { "flush_bytes", flush.Sum(x => x.Length) },
            { "slow_ms", StreamBench.SlowMs },
            { "cold_method", "unbuffered_proxy" },
            { "is_admin", Elevation.IsAdmin() },
            { "crc_ok", crcOk },
            { "time", DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) },
            { "rows", rows.Select(r => (object?)RowJson(r)).ToList() },
            { "table", table },
        };
    }

    private static JObject RowJson(BenchRow r) => new()
    {
        { "rep", (long)r.Rep },
        { "mode", r.Mode },
        { "stream_s", r.StreamS },
        { "cpu_s", r.CpuS },
        { "bytes", r.Bytes },
        { "requests", r.Requests },
        { "errors", r.Errors },
        { "replay_s", Math.Round(r.Replay.TotalSeconds, 4) },
        { "replay_slow_ratio", Math.Round(r.Replay.SlowRatio, 4) },
        { "replay_crc32", (long)r.Replay.Crc32 },
        { "replay_bytes", r.Replay.Bytes },
    };

    // 현재 폴더와 실행 파일 위치에서 위로 올라가며 prefetch/cli.py 가 있는 폴더를 찾습니다.
    private static string FindPythonRepo(string lang)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "prefetch", "cli.py")))
                {
                    return d.FullName;
                }
            }
        }

        throw new ArgException(L.Get(lang, "Cli_Bench_NeedRepo"));
    }
}
