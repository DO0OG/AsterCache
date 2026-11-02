using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

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

    /// 물리 RAM 전체 (바이트): 상주 고정은 16 GiB 이상에서만. 기본 0 (고정 안 함).
    public Func<long> PhysicalRamBytes { get; init; } = () => 0;

    /// 고정할 파일 경로 (예열 순서 목록의 파일 중 허용 루트 안인 것).
    public Func<GameSettings, IReadOnlyList<string>> PinPaths { get; init; } = _ => [];

    /// 런처 (LauncherExes) 가 하나라도 실행 중인가: 그동안은 고정하지 않음.
    public Func<bool> LauncherRunning { get; init; } = () => false;

    /// 직전 호출 뒤의 전체 CPU 사용률 (%), 처음이거나 읽지 못하면 null. 예열 유지의 유휴 판정용 (기본은 한가함).
    public Func<double?> CpuPercent { get; init; } = () => 0;

    /// 게임이 놓인 드라이브의 처리량 (바이트/초), 읽지 못하면 null. 예열 유지의 유휴 판정용 (기본은 한가함).
    public Func<GameSettings, double?> DiskBytesPerSecond { get; init; } = _ => 0;

    /// 파일들을 읽기 전용 매핑으로 고정.
    public Func<IReadOnlyList<string>, PinHandle> OpenPin { get; init; } = _ => new PinHandle(new Nothing(), 0, 0);

    /// 핫 집합 몇 곳을 읽어 잰 1 MiB 평균 읽기 시간 (ms), 읽을 수 없으면 null.
    public Func<GameSettings, double?> ProbeReadMs { get; init; } = _ => null;

    public static PrewarmEnv Create() => new()
    {
        PhysicalRamBytes = SystemMemory.TotalBytes,
        PinPaths = g => PrefetchStreamer.PinPaths(g, PrefetchStreamer.PinCapBytes(PrefetchStreamer.ZoneBytes(g), SystemMemory.TotalBytes(), SystemMemory.AvailableMib(), g.Prewarm.MinFreeRamMib)),
        LauncherRunning = () => ProcessSnapshot.IsRunning(LauncherExes.All),
        CpuPercent = new CpuLoad().Percent,
        DiskBytesPerSecond = g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)),
        OpenPin = paths =>
        {
            FilePin pin = FilePin.Open(paths);
            return new PinHandle(pin, pin.Count, pin.Skipped);
        },
        ProbeReadMs = PrefetchStreamer.ProbeReadMs,
        AvailableRamMib = SystemMemory.AvailableMib,
        GameRunning = g => ProcessSnapshot.IsRunning(g.ExeNames),
        ZoneBytes = g => PrefetchStreamer.ZoneBytes(g) is { } z ? Math.Min(z, (long)g.MaxStreamMib << 20) : null,
        TraceExists = g => File.Exists(g.Trace),
    };
}

/// 열어 둔 고정 (Dispose 하면 풂)과 고정한 파일 수, 건너뛴 파일 수.
public sealed record PinHandle(IDisposable Pin, int Count, int Skipped);

internal sealed class Nothing : IDisposable
{
    public void Dispose()
    {
    }
}

public sealed record PrewarmResult(string Game, bool Ran, string? SkipReason, StreamStats? Stats, DateTimeOffset At);

/// 사전 예열: 서비스 시작 때와 트리거 프로그램이 뜰 때, 게임의 존 순서 스트림 (기존 IGameStreamer, pvm) 을 돌려 핫 집합을 RAM 에 올립니다.
/// 결과는 service.log 와 상태 파일 (aster status 가 읽음) 에 남깁니다. 예열 중 게임이 시작돼도 스트림은 멈추지 않습니다 (같은 데이터).
public sealed class Prewarmer
{
    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly string? _statusPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly JObject _status = new();
    private readonly PinKeeper _pins;
    private readonly Rewarmer _rewarm;

    public Prewarmer(IReadOnlyList<GameSettings> games, IGameStreamer streamer, PrewarmEnv env, ILog log, string? statusPath = null, Func<DateTimeOffset>? now = null)
    {
        _games = games;
        _streamer = streamer;
        _env = env;
        _log = log;
        _statusPath = statusPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pins = new PinKeeper(env, log);
        _rewarm = new Rewarmer(this, env, log, _now);
    }

    /// 지금 고정을 들고 있는 게임 이름.
    public IReadOnlyCollection<string> Pinned => _pins.Held;

    internal IReadOnlyList<GameSettings> Games => _games;

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운·상태는 유지). pin 이 꺼졌거나 빠진 게임의 고정은 풉니다.
    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        _games = games;
        _pins.Reconcile(games);
    }

    /// 한 게임의 고정을 풉니다 (그 게임 exe 가 뜰 때, 스트림보다 먼저).
    public void ReleasePin(string game, string why) => _pins.Release(game, why);

    /// 모든 고정을 풉니다 (런처가 뜰 때, 서비스 종료).
    public void ReleasePins(string why) => _pins.ReleaseAll(why);

    public void Shutdown() => _pins.ReleaseAll("service stopping");

    /// 주기 점검 (재적용 틱마다): 가용 RAM 이 모자라면 고정 해제, 예열 유지 주기가 되면 식은 게임을 다시 예열.
    public void Tick(AutoSettings auto)
    {
        _pins.CheckMemory(_games);
        _rewarm.Tick(auto);
    }

    /// 예열에 성공한 적이 있는 게임 (최근 순, 설정에 아직 있는 것만).
    internal IReadOnlyList<GameSettings> Warmed()
    {
        lock (_gate)
        {
            return [.. _games.Where(g => _last.ContainsKey(g.Name)).OrderByDescending(g => _last[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal)];
        }
    }

    internal PrewarmResult RewarmGame(GameSettings game) => Guarded(game, "rewarm", pin: true, ignoreCooldown: true);

    /// 이 프로세스가 어느 게임의 trigger_exes 에 들어 있는가 (대소문자 무시).
    public bool IsTrigger(string exeName) => _games.Any(g => IsTrigger(g, exeName));

    public static bool IsTrigger(GameSettings g, string exeName) => g.Prewarm.TriggerExes.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로.
    public IReadOnlyList<PrewarmResult> RunServiceStart() => RunBatch(_games.Where(g => g.Prewarm.OnServiceStart), "service start");

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로. narrow 가 있으면 그 목록에서 예열할 게임만 고릅니다.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start, Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? narrow = null)
    {
        IReadOnlyList<GameSettings> hit = _games.Where(g => IsTrigger(g, start.ExeName)).ToList();
        return RunBatch(narrow?.Invoke(hit) ?? hit, $"trigger {start.ExeName}", pin: !LauncherExes.Is(start.ExeName)); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
    }

    /// 주어진 게임을 차례로 (한 게임의 실패가 나머지를 막지 않음).
    public IReadOnlyList<PrewarmResult> RunAll(IEnumerable<GameSettings> games, string why) => RunBatch(games, why);

    // 한 묶음 안에서 존 크기 합이 (가용 RAM - 여유) 를 넘으면 그 뒤 게임은 건너뜁니다 (앞쪽 = 최근 순서가 우선). 첫 게임은 Run 의 자체 판단에 맡깁니다.
    private IReadOnlyList<PrewarmResult> RunBatch(IEnumerable<GameSettings> games, string why, bool pin = true)
    {
        var results = new List<PrewarmResult>();
        long avail = _env.AvailableRamMib();
        long used = 0;
        bool cut = false;
        foreach (GameSettings g in games)
        {
            long zoneMib = ((_env.ZoneBytes(g) ?? 0) + (1 << 20) - 1) >> 20;
            if (cut || (used > 0 && used + zoneMib > avail - g.Prewarm.MinFreeRamMib))
            {
                cut = true;
                _log.Info($"{g.Name}: prewarm skipped (batch zones {used + zoneMib} MiB exceed available {avail} MiB - reserve {g.Prewarm.MinFreeRamMib} MiB) [{why}]");
                results.Add(new PrewarmResult(g.Name, false, "batch memory budget", null, _now()));
                continue;
            }

            PrewarmResult r = Guarded(g, why, pin);
            results.Add(r);
            used += r.Ran ? zoneMib : 0;
        }

        return results;
    }

    // 한 게임의 예외가 나머지 게임의 예열을 막지 않게 합니다.
    private PrewarmResult Guarded(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false)
    {
        try
        {
            return Run(game, why, pin, ignoreCooldown);
        }
        catch (Exception e)
        {
            _log.Error($"{game.Name}: prewarm failed ({why})", e);
            return new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, _now());
        }
    }

    /// 한 게임을 지금 예열 (조건을 만족할 때만). 동시에 하나만 돕니다.
    public PrewarmResult Run(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false)
    {
        lock (_gate)
        {
            DateTimeOffset now = _now();
            TimeSpan? since = !ignoreCooldown && _last.TryGetValue(game.Name, out DateTimeOffset prev) ? now - prev : null;
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
                    PrewarmResult done = Record(game, now, new PrewarmResult(game.Name, true, null, s, now), mbps);
                    if (pin)
                    {
                        _pins.TryPin(game);
                    }

                    return done;
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

    /// 물리 RAM 전체 (바이트), 읽지 못하면 0.
    public static long TotalBytes()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? (long)s.TotalPhys : 0;
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
