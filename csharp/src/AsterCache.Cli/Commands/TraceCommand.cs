using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using AsterCache.Windows;

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
        new("io-summary-root", ArgKind.Many),
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

        if (a.Given("io-summary-root") && !a.Flag("live"))
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Trace_IoSummaryNeedsLive"));
        }

        if (a.Flag("live"))
        {
            foreach (string root in new[] { prefix }.Concat(a.List("io-summary-root")))
            {
                if (AntiCheatGuard.RefuseTrace(o, root, exes) is { } refused)
                {
                    return refused;
                }
            }
        }

        var sessions = new List<List<TraceEvent>>();
        IoSummary? ioSummary = null;
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
            IReadOnlyList<string> ioSummaryRoots = a.List("io-summary-root");
            (List<TraceRecord> live, long liveLost, IoRequestSnapshot? requests) = CaptureLive(exes, a.Int("seconds"), a.Text("stop-file"), a.Lang,
                ioSummaryRoots.Count == 0 ? null : ioSummaryRoots);
            ioSummary = ConvertIoSummary(requests, ioSummaryRoots, o); // 세션을 닫은 뒤 게임 폴더 파일 배치로 (파일, 블록) 에 잇기
            sessions.Add(Build(o, "live", live, exes, prefix, liveLost));
        }

        string outPath = PathText.Str(a.Text("out")!);
        TraceStageIndex.ClearSidecars(outPath); // 옛 원시 기록과 짝이 맞지 않는 단계 색인·집계·회차 표지·시도 횟수를 먼저 지움
        (List<string> runs, int count) = TraceWriter.Write(sessions, outPath);
        if (ioSummary is { } summary)
        {
            SaveIoSummary(outPath, summary, o);
        }

        o.WriteLine(PyJson.Dumps(new JObject
        {
            { "union", outPath },
            { "union_events", (long)count },
            { "per_run", runs.Cast<object?>().ToList() },
        }));
        return 0;
    }

    // 변환은 부가 정보: 실패해도 받은 원시 기록은 그대로 저장하도록 경고 한 줄만 남기고 null (서비스와 같은 방식)
    internal static IoSummary? ConvertIoSummary(IoRequestSnapshot? requests, IReadOnlyList<string> roots, TextWriter output,
        Func<IoRequestSnapshot, IReadOnlyList<string>, CancellationToken, IoSummary>? scan = null)
    {
        if (requests is null)
        {
            return null;
        }

        try
        {
            return (scan ?? ((snapshot, folders, ct) => IoLayoutScan.Build(snapshot, folders, ct)))(requests, roots, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            output.WriteLine($"I/O summary not built ({e.GetType().Name})");
            return null;
        }
    }

    internal static void SaveIoSummary(string outPath, IoSummary summary, TextWriter output)
    {
        IoSummaryStore.WriteForRun(outPath, summary);
        output.WriteLine(summary.FormatLogLine());
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
    private static (List<TraceRecord> Records, long EventsLost, IoRequestSnapshot? IoRequests) CaptureLive(IReadOnlyList<string> exes, long? seconds, string? stopFile,
        string lang, IReadOnlyCollection<string>? ioSummaryRoots)
    {
        if (stopFile is not null)
        {
            File.Delete(stopFile); // 이전 실행이 남긴 파일 때문에 바로 멈추지 않도록
        }

        using LiveSession session = LiveSession.Start(exes, ioSummaryRoots: ioSummaryRoots);
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
        return (records, session.EventsLost, session.IoRequests);
    }
}
