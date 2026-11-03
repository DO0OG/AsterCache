using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Cli.Commands;

/// 읽기 기록 -> relayout/prefetch 가 읽는 trace JSON. --csv 는 astercache_v07.py trace 와 같은 출력,
/// --etl 은 .etl 파일 (--dump-first N 은 변환된 읽기 처음 N 건을 진단용으로 먼저 찍음), --live 는 실시간 ETW 세션 (관리자, --seconds 또는 --stop-file 로 종료) 에서 --exe 프로세스 트리의 읽기만 모읍니다.
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
        new("dump-first", Type: ArgType.Int),
        new("stop-file"),
    ];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        int modes = (a.Given("csv") ? 1 : 0) + (a.Given("etl") ? 1 : 0) + (a.Flag("live") ? 1 : 0);
        if (modes != 1)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Trace_ExactlyOne"));
        }

        string prefix = a.Text("prefix") ?? a.Text("origin-root") ?? throw new ArgException(L.Get(a.Lang, "Cli_Trace_NeedPrefix"));
        IReadOnlyList<string> exes = a.List("exe");
        if (!a.Given("csv") && exes.Count == 0)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Trace_NeedExe"));
        }

        if (a.Given("dump-first") && !a.Given("etl"))
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Trace_DumpNeedsEtl"));
        }

        if (a.Given("stop-file") && !a.Flag("live"))
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Trace_StopNeedsLive"));
        }

        if (a.Flag("live") && AntiCheatGuard.RefuseTrace(o, prefix, exes) is { } refused)
        {
            return refused;
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
                (List<TraceRecord> records, int unresolved, long lost) = EtlReader.ReadWithStats(file, exes);
                if (a.Int("dump-first") is { } dump)
                {
                    foreach (string line in EtlReader.Describe(records, unresolved, lost, (int)Math.Min(dump, int.MaxValue)))
                    {
                        o.WriteLine(line);
                    }
                }

                sessions.Add(Build(o, Path.GetFileName(file), records, exes, prefix, lost));
            }
        }
        else
        {
            (List<TraceRecord> live, long liveLost) = CaptureLive(exes, a.Int("seconds"), a.Text("stop-file"), a.Lang);
            sessions.Add(Build(o, "live", live, exes, prefix, liveLost));
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

    // eventsLost: .etl / 실시간 세션이 버퍼 부족으로 잃은 이벤트 수. 0 이 아니면 읽기가 빠졌을 수 있습니다 (trace JSON 형식은 그대로 두고 이 줄에만 남깁니다).
    private static List<TraceEvent> Build(TextWriter o, string name, List<TraceRecord> records, IReadOnlyList<string> exes, string prefix, long? eventsLost = null)
    {
        (List<TraceEvent> events, BuildStats st) = TraceBuilder.Build(records, exes, prefix);
        o.WriteLine($"{name} {{'reads': {st.Reads}, 'parsed': {st.Parsed}, 'ignored': {st.Ignored}, 'truncated': {(st.Truncated ? "True" : "False")}{(eventsLost is { } lost ? $", 'events_lost': {lost}" : string.Empty)}}}");
        return TraceBuilder.CanonicalizeCase(events);
    }

    // 세션은 관리자만 열 수 있습니다 (NotAdminException, 아무것도 만들기 전에). Ctrl+C, (터미널이면) Enter, 또는 --seconds 로 멈춥니다.
    // --stop-file: 그 파일이 생기면 멈춥니다 (스크립트가 프로세스를 강제 종료하지 않고 출력을 끝까지 쓰게 하는 길).
    private static (List<TraceRecord> Records, long EventsLost) CaptureLive(IReadOnlyList<string> exes, long? seconds, string? stopFile, string lang)
    {
        if (stopFile is not null)
        {
            File.Delete(stopFile); // 이전 실행이 남긴 파일 때문에 바로 멈추지 않도록
        }

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
            Console.Error.WriteLine(L.Get(lang, Console.IsInputRedirected ? "Cli_Trace_Tracing" : "Cli_Trace_TracingEnter"));
            if (!Console.IsInputRedirected)
            {
                _ = Task.Run(() =>
                {
                    Console.In.ReadLine();
                    stop.Set();
                });
            }

            if (stopFile is not null)
            {
                _ = Task.Run(() =>
                {
                    while (!stop.IsSet && !File.Exists(stopFile))
                    {
                        Thread.Sleep(200);
                    }

                    stop.Set();
                });
            }

            stop.Wait(seconds is { } s ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        List<TraceRecord> records = session.Stop();
        return (records, session.EventsLost);
    }
}
