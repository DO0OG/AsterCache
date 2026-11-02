using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 예열 판단 (순수 함수): 건너뛸 이유, 없으면 null. 순서: 계획·추적 파일 → 쿨다운 → 게임 실행 중 → 가용 RAM (존 크기 + 여유).
public static class PrewarmPolicy
{
    public static string? SkipReason(long? zoneBytes, bool traceExists, bool gameRunning, long availableRamMib, int minFreeRamMib, TimeSpan? sinceLast, TimeSpan cooldown)
    {
        if (zoneBytes is null)
        {
            return "no plan file";
        }

        if (zoneBytes == 0)
        {
            return "no zone";
        }

        if (!traceExists)
        {
            return "no trace file";
        }

        if (sinceLast is { } since && since < cooldown)
        {
            return $"cooldown ({Math.Ceiling((cooldown - since).TotalMinutes)} min left)";
        }

        if (gameRunning)
        {
            return "game is running";
        }

        long zoneMib = (zoneBytes.Value + (1 << 20) - 1) >> 20;
        return availableRamMib < zoneMib + minFreeRamMib
            ? $"low memory (available {availableRamMib} MiB < zone {zoneMib} MiB + reserve {minFreeRamMib} MiB)"
            : null;
    }
}

/// 예열이 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class PrewarmEnv
{
    public required Func<long> AvailableRamMib { get; init; }

    public required Func<GameSettings, bool> GameRunning { get; init; }

    /// 이번에 읽을 바이트 (계획의 존 크기를 max_stream_mib 로 자른 값), 계획 파일이 없으면 null, 존이 비어 있으면 0.
    public required Func<GameSettings, long?> ZoneBytes { get; init; }

    public required Func<GameSettings, bool> TraceExists { get; init; }

    public static PrewarmEnv Create() => new()
    {
        AvailableRamMib = SystemMemory.AvailableMib,
        GameRunning = g => ProcessSnapshot.IsRunning(g.ExeNames),
        ZoneBytes = g => PrefetchStreamer.ZoneBytes(g) is { } z ? Math.Min(z, (long)g.MaxStreamMib << 20) : null,
        TraceExists = g => File.Exists(g.Trace),
    };
}

public sealed record PrewarmResult(string Game, bool Ran, string? SkipReason, StreamStats? Stats, DateTimeOffset At);

/// 사전 예열: 서비스 시작 때와 트리거 프로그램이 뜰 때, 게임의 존 순서 스트림 (기존 IGameStreamer, pvm) 을 돌려 핫 집합을 RAM 에 올립니다.
/// 결과는 service.log 와 상태 파일 (aster status 가 읽음) 에 남깁니다. 예열 중 게임이 시작돼도 스트림은 멈추지 않습니다 (같은 데이터).
public sealed class Prewarmer
{
    private readonly IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly string? _statusPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly JObject _status = new();

    public Prewarmer(IReadOnlyList<GameSettings> games, IGameStreamer streamer, PrewarmEnv env, ILog log, string? statusPath = null, Func<DateTimeOffset>? now = null)
    {
        _games = games;
        _streamer = streamer;
        _env = env;
        _log = log;
        _statusPath = statusPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// 이 프로세스가 어느 게임의 trigger_exes 에 들어 있는가 (대소문자 무시).
    public bool IsTrigger(string exeName) => _games.Any(g => IsTrigger(g, exeName));

    public static bool IsTrigger(GameSettings g, string exeName) => g.Prewarm.TriggerExes.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로.
    public IReadOnlyList<PrewarmResult> RunServiceStart() =>
        _games.Where(g => g.Prewarm.OnServiceStart).Select(g => Guarded(g, "service start")).ToList();

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start) =>
        _games.Where(g => IsTrigger(g, start.ExeName)).Select(g => Guarded(g, $"trigger {start.ExeName}")).ToList();

    // 한 게임의 예외가 나머지 게임의 예열을 막지 않게 합니다.
    private PrewarmResult Guarded(GameSettings game, string why)
    {
        try
        {
            return Run(game, why);
        }
        catch (Exception e)
        {
            _log.Error($"{game.Name}: prewarm failed ({why})", e);
            return new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, _now());
        }
    }

    /// 한 게임을 지금 예열 (조건을 만족할 때만). 동시에 하나만 돕니다.
    public PrewarmResult Run(GameSettings game, string why)
    {
        lock (_gate)
        {
            DateTimeOffset now = _now();
            TimeSpan? since = _last.TryGetValue(game.Name, out DateTimeOffset prev) ? now - prev : null;
            string? skip = PrewarmPolicy.SkipReason(_env.ZoneBytes(game), _env.TraceExists(game), _env.GameRunning(game), _env.AvailableRamMib(),
                game.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(game.Prewarm.CooldownMin));
            if (skip is null)
            {
                try
                {
                    StreamStats s = _streamer.Stream(game);
                    _last[game.Name] = now;
                    double mbps = s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0;
                    _log.Info($"{game.Name}: prewarm done ({why}): {s.Bytes >> 20} MiB in {s.Seconds:F2} s = {mbps:F0} MB/s, {s.Requests} requests, {s.Errors} errors");
                    return Record(game, now, new PrewarmResult(game.Name, true, null, s, now), mbps);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    skip = "no trace file";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
                {
                    _log.Error($"{game.Name}: prewarm failed ({why})", e);
                    return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
                }
            }

            _log.Info($"{game.Name}: prewarm skipped ({skip}) [{why}]");
            return Record(game, now, new PrewarmResult(game.Name, false, skip, null, now), 0);
        }
    }

    private PrewarmResult Record(GameSettings game, DateTimeOffset now, PrewarmResult r, double mbps)
    {
        _status[game.Name] = new JObject
        {
            { "at", now.ToString("O") },
            { "result", r.Ran ? "ok" : r.SkipReason!.StartsWith("failed", StringComparison.Ordinal) ? "failed" : "skipped" },
            { "reason", r.SkipReason },
            { "bytes", r.Stats?.Bytes },
            { "seconds", r.Stats?.Seconds },
            { "mb_per_s", r.Stats is null ? null : Math.Round(mbps, 1) },
        };
        if (_statusPath is not null)
        {
            try
            {
                string tmp = _statusPath + ".tmp";
                File.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(new JObject { { "games", _status } }, indent: 2, ensureAscii: false) + "\n"));
                File.Move(tmp, _statusPath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("prewarm status file not written: " + e.GetType().Name);
            }
        }

        return r;
    }
}

/// 가용 물리 메모리 (여유 + 대기 목록, GlobalMemoryStatusEx).
public static partial class SystemMemory
{
    public static long AvailableMib()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailablePhys >> 20) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
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
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
