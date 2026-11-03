using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Tracer;

namespace AsterCache.Cli.Commands;

/// 대상 프로세스가 본 실시간 세션. 시험은 가짜로 바꿉니다.
internal interface ILearnSession : IDisposable
{
    /// 대상 프로세스 트리가 지금 살아 있는가 / 한 번이라도 봤는가.
    bool TargetsActive { get; }

    bool SawTarget { get; }

    long EventsLost { get; }

    /// 읽기 이름 해석 집계 (Stop 뒤에 읽음).
    ReadNameStats NameStats { get; }

    IReadOnlyList<TraceRecord> Stop();
}

/// 이 명령이 바깥 세계에 닿는 곳 (세션, 파일 읽기, RAM, 폴링 간격).
internal sealed class LearnDeps
{
    public Func<IReadOnlyCollection<string>, LiveListener, ILearnSession> Open { get; init; } = (exes, listener) => new LiveLearnSession(
        LiveSession.Start(exes, SessionName, listener: listener, flushInterval: FlushInterval));

    public Func<IReadAheadIo> OpenIo { get; init; } = () => new FileReadAheadIo();

    public Func<long> AvailableRamMib { get; init; } = LearnMemory.AvailableMib;

    public Func<long> PhysicalRamBytes { get; init; } = LearnMemory.TotalBytes;

    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(200);

    /// 수동 수집 (aster trace --live) 과 서비스의 자동 트레이스 세션 이름과 겹치지 않는 이름.
    public const string SessionName = "AsterCacheLearn";

    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    private sealed class LiveLearnSession(LiveSession session) : ILearnSession
    {
        public bool TargetsActive => session.TargetsActive;

        public bool SawTarget => session.SawTarget;

        public long EventsLost => session.EventsLost;

        public ReadNameStats NameStats => session.NameStats;

        public IReadOnlyList<TraceRecord> Stop() => session.Stop();

        public void Dispose() => session.Dispose();
    }
}

internal static partial class LearnMemory
{
    public static long AvailableMib()
    {
        var s = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailablePhys >> 20) : 0;
    }

    public static long TotalBytes()
    {
        var s = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        return GlobalMemoryStatusEx(ref s) ? (long)s.TotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailablePhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref Status status);
}

/// aster learn: 관리자 콘솔에서 게임을 하는 동안 읽기를 실시간으로 따라가며 뒤쪽을 미리 읽어 파일 캐시에 올립니다 (서비스의 첫 실행 학습과 같은 엔진).
/// 대상 exe 가 뜨면 (이미 떠 있으면 바로) 따라가고, 프로세스가 모두 끝나거나 --seconds 가 지나거나 Ctrl+C 를 누르면 멈춥니다. --out 이면 같은 읽기를 trace.json 으로도 저장합니다.
internal static class LearnCommand
{
    public static readonly ArgOption[] Options =
    [
        new("exe", ArgKind.List, Required: true),
        new("root", Required: true),
        new("seconds", Type: ArgType.Int),
        new("window-mib", Type: ArgType.Int),
        new("max-mib", Type: ArgType.Int),
        new("gap-ms", Type: ArgType.Int),
        new("seq-runs", Type: ArgType.Int),
        new("mode", Choices: [LearnSettings.StreamMode, LearnSettings.GapMode, LearnSettings.BoundedMode, LearnSettings.AdaptiveMode], Default: LearnSettings.StreamMode),
        new("saturation-mbps", Type: ArgType.Int),
        new("out"),
        new("json", ArgKind.Flag),
    ];

    private static readonly long DefaultWindowMib = LearnSettings.Default.WindowMib;
    private static readonly long DefaultMinFreeRamMib = PrewarmSettings.Default.MinFreeRamMib;

    /// 시험이 바꿔 끼우는 바깥 세계.
    internal static LearnDeps Deps { get; set; } = new();

    public static int Run(ParsedArgs a, TextWriter o)
    {
        long window = a.Int("window-mib") ?? DefaultWindowMib;
        long? maxMib = a.Int("max-mib");
        long? seconds = a.Int("seconds");
        if (window < 1 || maxMib is < 1 || seconds is < 1)
        {
            throw new ArgException("--window-mib, --max-mib and --seconds must be 1 or more");
        }

        long gapMs = a.Int("gap-ms") ?? ReadAhead.DefaultGapMs;
        long seqRuns = a.Int("seq-runs") ?? ReadAhead.DefaultSeqRuns;
        if (gapMs < 0 || seqRuns is < 1 or > int.MaxValue)
        {
            throw new ArgException("--gap-ms must be 0 or more and --seq-runs must be 1 or more");
        }

        long saturationMbps = a.Int("saturation-mbps") ?? LearnSettings.DefaultSaturationMbps;
        if (saturationMbps is < 1 or > 10000)
        {
            throw new ArgException("--saturation-mbps must be between 1 and 10000");
        }

        LearnDeps deps = Deps;
        IReadOnlyList<string> exes = a.List("exe");
        string root = PathText.Str(a.Text("root")!);
        if (AntiCheatGuard.RefuseTrace(o, root, exes) is { } refused)
        {
            return refused;
        }

        string? outPath = a.Text("out") is { } p ? PathText.Str(p) : null;
        long maxBytes = ReadAhead.ResolveMaxBytes((int)Math.Min(maxMib ?? 0, int.MaxValue), deps.PhysicalRamBytes());
        string mode = a.Text("mode")!;
        using var engine = new ReadAhead(new ReadAheadOptions([root], window << 20, maxBytes, DefaultMinFreeRamMib, deps.AvailableRamMib, GapMs: gapMs, SeqRuns: (int)seqRuns, Stream: mode != LearnSettings.GapMode, Bounded: mode is LearnSettings.BoundedMode or LearnSettings.AdaptiveMode, Adaptive: mode == LearnSettings.AdaptiveMode, SaturationMbps: (int)saturationMbps), deps.OpenIo());

        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += onCancel;
        IReadOnlyList<TraceRecord> records;
        long lost;
        ReadNameStats names;
        try
        {
            using ILearnSession session = deps.Open(exes, new LiveListener(engine.OnRead, engine.OnOpen, engine.OnLateRead)); // 관리자가 아니면 NotAdminException (세션을 만들기 전에)
            engine.Start();
            Console.Error.WriteLine(L.Get(a.Lang, "Cli_Trace_Tracing"));
            var clock = Stopwatch.StartNew();
            while (!stop.Wait(deps.Poll) && !(seconds is { } s && clock.Elapsed >= TimeSpan.FromSeconds(s)) && !(session.SawTarget && !session.TargetsActive))
            {
            }

            records = session.Stop();
            lost = session.EventsLost;
            names = session.NameStats;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        engine.Dispose();
        ReadAheadStats st = engine.Stats;
        long? traceEvents = outPath is null ? null : SaveTrace(records, exes, root, outPath);
        var result = new JObject
        {
            { "read_ahead_bytes", st.ReadAheadBytes },
            { "game_read_bytes", st.GameReadBytes },
            { "hit_ratio", Math.Round(st.HitRatio, 4) },
            { "events", st.Events },
            { "events_lost", lost },
            { "resolved_reads", names.Resolved }, // 대상 프로세스 트리의 모든 읽기 기준 (root 밖 포함)
            { "late_reads", names.Late },
            { "unresolved_reads", names.Unresolved },
            { "sequential_files", (long)st.SequentialFiles },
            { "hit_bytes", st.HitBytes },
            { "gap_count", st.GapCount },
            { "yielded_requests", st.YieldedRequests },
            { "read_ahead_while_idle_bytes", st.ReadAheadWhileIdleBytes },
            { "saturated_ms", st.SaturatedMs },
            { "paused_requests", st.PausedRequests },
        };
        if (outPath is not null)
        {
            result.Add("out", outPath);
            result.Add("trace_events", traceEvents);
        }

        if (a.Flag("json"))
        {
            return AsterCli.Emit(o, result);
        }

        o.WriteLine(Summary(st, lost));
        return 0;
    }

    /// 한 줄 요약 (MiB 단위, 적중 추정은 %).
    internal static string Summary(ReadAheadStats st, long eventsLost) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"learn: read ahead {st.ReadAheadBytes >> 20} MiB, game read {st.GameReadBytes >> 20} MiB, hit {st.HitRatio * 100:F1}%, events {st.Events}{(eventsLost > 0 ? $", events lost {eventsLost}" : string.Empty)}");

    private static long SaveTrace(IReadOnlyList<TraceRecord> records, IReadOnlyList<string> exes, string root, string outPath)
    {
        (List<TraceEvent> events, _) = TraceBuilder.Build(records, exes, root);
        List<TraceEvent> canonical = TraceBuilder.CanonicalizeCase(events);
        TraceWriter.Write([canonical], outPath);
        return canonical.Count;
    }
}
