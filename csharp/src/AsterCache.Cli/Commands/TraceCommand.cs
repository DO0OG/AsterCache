using AsterCache.Core.Json;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Cli.Commands;

/// 읽기 기록 -> relayout/prefetch 가 읽는 trace JSON. --csv 는 astercache_v07.py trace 와 같은 출력,
/// --etl 은 .etl 파일, --live 는 실시간 ETW 세션 (관리자) 에서 --exe 프로세스 트리의 읽기만 모읍니다.
internal static class TraceCommand
{
    public static readonly ArgOption[] Options =
    [
        new("csv", ArgKind.List),
        new("etl", ArgKind.List),
        new("live", ArgKind.Flag),
        new("exe", ArgKind.List),
        new("prefix"),
        new("origin-root"),
        new("out", Required: true),
        new("seconds", Type: ArgType.Int),
    ];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        int modes = (a.Given("csv") ? 1 : 0) + (a.Given("etl") ? 1 : 0) + (a.Flag("live") ? 1 : 0);
        if (modes != 1)
        {
            throw new ArgException("exactly one of --csv, --etl, --live is required");
        }

        string prefix = a.Text("prefix") ?? a.Text("origin-root") ?? throw new ArgException("--prefix (or --origin-root) is required");
        IReadOnlyList<string> exes = a.List("exe");
        if (!a.Given("csv") && exes.Count == 0)
        {
            throw new ArgException("--exe is required with --etl and --live");
        }

        var sessions = new List<List<TraceEvent>>();
        if (a.Given("csv"))
        {
            foreach (string file in a.List("csv"))
            {
                (List<TraceEvent> events, ProcmonStats st) = ProcmonCsv.LoadEvents(file, prefix);
                sessions.Add(events);
                o.WriteLine($"{Path.GetFileName(file)} {{'rows': {st.Rows}, 'read_success_rows': {st.ReadSuccessRows}, 'parsed': {st.Parsed}, 'ignored': {st.Ignored}}}");
            }
        }
        else if (a.Given("etl"))
        {
            foreach (string file in a.List("etl"))
            {
                sessions.Add(Build(o, Path.GetFileName(file), EtlReader.Read(file, exes), exes, prefix));
            }
        }
        else
        {
            sessions.Add(Build(o, "live", CaptureLive(exes, a.Int("seconds")), exes, prefix));
        }

        string outPath = PathText.Str(a.Text("out")!);
        (List<string> runs, int count) = TraceWriter.Write(sessions, outPath);
        o.WriteLine(PyJson.Dumps(new JObject
        {
            { "union", outPath },
            { "union_events", (long)count },
            { "per_run", runs.Cast<object?>().ToList() },
        }));
        return 0;
    }

    private static List<TraceEvent> Build(TextWriter o, string name, List<TraceRecord> records, IReadOnlyList<string> exes, string prefix)
    {
        (List<TraceEvent> events, BuildStats st) = TraceBuilder.Build(records, exes, prefix);
        o.WriteLine($"{name} {{'reads': {st.Reads}, 'parsed': {st.Parsed}, 'ignored': {st.Ignored}, 'truncated': {(st.Truncated ? "True" : "False")}}}");
        return TraceBuilder.CanonicalizeCase(events);
    }

    // 세션은 관리자만 열 수 있습니다 (NotAdminException, 아무것도 만들기 전에). Ctrl+C, (터미널이면) Enter, 또는 --seconds 로 멈춥니다.
    private static List<TraceRecord> CaptureLive(IReadOnlyList<string> exes, long? seconds)
    {
        using LiveSession session = LiveSession.Start(exes);
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            Console.Error.WriteLine("tracing; press Ctrl+C" + (Console.IsInputRedirected ? "" : " or Enter") + " to stop");
            if (!Console.IsInputRedirected)
            {
                _ = Task.Run(() =>
                {
                    Console.In.ReadLine();
                    stop.Set();
                });
            }

            stop.Wait(seconds is { } s ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        return session.Stop();
    }
}
