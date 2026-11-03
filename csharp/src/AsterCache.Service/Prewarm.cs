using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
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

    /// 두 설치 루트가 같은 물리 디스크인가 (모르면 참). 시험 기본값은 드라이브 문자 비교.
    public Func<string, string, bool> SameDisk { get; init; } = AutoEnv.SameLetter;

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

    /// 파일의 크기·수정 시각 (회복 기간 고정이 고정 중 파일 변경을 알아채는 데). 읽을 수 없으면 IOException 등. 기본은 항상 같은 값 (시험용), Create 는 진짜 값.
    public Func<string, Fingerprint> FileStamp { get; init; } = p => new Fingerprint(p, 0, 0);

    /// 파일들을 읽기 전용 매핑으로 고정.
    public Func<IReadOnlyList<string>, PinHandle> OpenPin { get; init; } = _ => new PinHandle(new Nothing(), 0, 0);

    /// 핫 집합 몇 곳을 읽어 잰 결과 (CacheProbe), 읽을 수 없으면 null.
    public Func<GameSettings, ProbeResult?> ProbeReadMs { get; init; } = _ => null;

    /// 원시 트레이스 (자동 트레이스 결과) 나 boot 트레이스 (가져온 프로필) 가 있는가. 기록 없는 게임의 통째·프로필 예열은 이 파일과 TraceExists 가 모두 없을 때만 합니다.
    public Func<GameSettings, bool> RawTraceExists { get; init; } = _ => false;

    /// 기록 없는 게임의 예열 계획 (게임, 통째 상한 바이트 (0 = 끔), 프로필 예산 바이트 (0 = 끔), 취소). null 이면 이 기능 없음 (기본).
    public Func<GameSettings, long, long, CancellationToken, ColdPlan>? ColdPlanner { get; init; }

    /// 계획을 읽습니다 (묶음 사이에서 취소를 봄): (통계, 끝까지 읽었는가).
    public Func<IReadOnlyList<ReadOp>, CancellationToken, (StreamStats Stats, bool Complete)> ColdRead { get; init; } = (ops, ct) => FolderWarm.Read(ops, ct);

    /// 기록 없는 예열이 건드리면 안 되는 드라이브면 이유 (시스템 SSD·SSD·판정 불가), 아니면 null. 기본은 거부 없음 (시험용), Create 는 진짜 판정.
    public Func<GameSettings, string?> ColdRefusal { get; init; } = _ => null;

    /// 부트스트랩 S2: 기록 없는 게임의 boot 트레이스를 프리페치 기록 (.pf) 으로 만들어 봅니다 (ColdRefusal 이 없을 때만 부름). 만들었으면 true: 그러면 더는 기록 없는 게임이 아니라 통째·프로필 예열 (S3) 을 하지 않습니다. 기본 false.
    public Func<GameSettings, bool> PrefetchBoot { get; init; } = _ => false;

    /// 기록 없이 예열한 게임의 표본 (경로, 오프셋, 길이) 들을 읽어 잰 결과 (CacheProbe), 읽을 수 없으면 null.
    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?> ProbeSamples { get; init; } = s => CacheProbe.Probe(s);

    public static PrewarmEnv Create() => new()
    {
        SameDisk = VolumeDisks.SameDisk,
        PrefetchBoot = g => AutoTracer.BootFromPrefetch(g) > 0,
        ColdRefusal = g => DriveCapabilities.Of(g.OriginRoot, Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant(), letter => DiskInfo.Query(letter).IncursSeekPenalty).PrewarmRefusal,
        RawTraceExists = g => AutoTracer.PlanTracePath(g, File.Exists) is not null,
        ColdPlanner = (g, full, profile, ct) => PlanGuards.Inside(g.OriginRoot, g.AllowedRoots, RealPaths.Real)
            ? FolderWarm.Plan(g.OriginRoot, g.ExeNames, full, profile, EngineProfiles.Builtin, ct)
            : throw new PlanRejectedException("origin_root resolves outside allowed roots"),
        PhysicalRamBytes = SystemMemory.TotalBytes,
        FileStamp = PlanFile.FingerprintOf,
        PinPaths = g => PrefetchStreamer.PinPaths(g, PrefetchStreamer.PinCapBytes(PrefetchStreamer.ZoneBytes(g), SystemMemory.TotalBytes(), SystemMemory.AvailableMib(), g.Prewarm.MinFreeRamMib)),
        LauncherRunning = () => ProcessSnapshot.IsRunning(LauncherExes.All),
        CpuPercent = new CpuLoad().Percent,
        DiskBytesPerSecond = g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)),
        OpenPin = paths =>
        {
            FilePin pin = FilePin.Open(paths);
            return new PinHandle(pin, pin.Count, pin.Skipped);
        },
        ProbeReadMs = g => File.Exists(g.Trace) ? PrefetchStreamer.ProbeReadMs(g) : null, // 기록 없이 예열한 게임은 비교할 핫 집합이 없음
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

public sealed record PrewarmResult(string Game, bool Ran, string? SkipReason, StreamStats? Stats, DateTimeOffset At)
{
    /// 기록 없이 예열했을 때의 방식 이름표 ("full" 또는 "profile Unity"), 핫 집합 예열이면 null.
    public string? Mode { get; init; }
}

/// 사전 예열: 서비스 시작 때와 트리거 프로그램이 뜰 때, 게임의 존 순서 스트림 (기존 IGameStreamer, pvm) 을 돌려 핫 집합을 RAM 에 올립니다.
/// 결과는 service.log 와 상태 파일 (aster status 가 읽음) 에 남깁니다. 예열 중 게임이 시작돼도 스트림은 멈추지 않습니다 (같은 데이터).
public sealed class Prewarmer
{
    /// 게임 시작으로 취소돼 스트림을 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string Cancelled = "cancelled";

    /// 업데이트 직후 지연으로 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string PostUpdateDelay = "post-update delay";

    // 재배치 직후 예열의 이유 (AutoRelocator 가 "auto " + "after relocation" 으로 넘김): 회복 기간 고정의 조건.
    private const string AfterRelocation = "auto after relocation";

    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly string? _statusPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new(); // _last 만 보호 (짧게): 스트림이 도는 동안에도 LastWarmed·Warmed 가 막히지 않게
    private readonly object _run = new(); // 예열을 한 번에 하나로
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly JObject _status = new();
    private readonly PinKeeper _pins;
    private readonly Rewarmer _rewarm;
    private readonly Dictionary<string, long> _coldMib = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열해 RAM 에 올려 둔 양 (MiB), _gate 로 보호
    private readonly Dictionary<string, IReadOnlyList<(string Path, long Offset, int Bytes)>> _coldSamples = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열한 파일 목록에서 고른 재예열 점검 표본, _gate 로 보호
    private readonly HashSet<string> _refusedLogged = new(StringComparer.OrdinalIgnoreCase); // 드라이브 거부를 이미 로그한 게임, _gate 로 보호
    private readonly HashSet<string> _delayLogged = new(StringComparer.OrdinalIgnoreCase); // 업데이트 지연을 이미 로그한 게임, _gate 로 보호
    private volatile AutoSettings _auto = AutoSettings.Default;
    private volatile string? _streaming; // 핫 집합 스트림이 도는 게임
    private volatile bool _keepStream; // auto.keep_stream_on_game_start 가 켜진 채 그 게임이 시작돼 (같은 데이터를 동시에 읽으므로) 취소 신호에도 끝까지 읽기로 한 표시
    private volatile CancellationTokenSource? _streamStop; // 도는 스트림을 멈추는 신호 (게임 시작이 같은 디스크 읽기를 바로 끊음)
    private (string Game, long At)? _yield; // 게임 시작으로 스트림 양보를 요청한 게임과 시각 (Stopwatch 틱), _gate 로 보호

    public Prewarmer(IReadOnlyList<GameSettings> games, IGameStreamer streamer, PrewarmEnv env, ILog log, string? statusPath = null, Func<DateTimeOffset>? now = null)
    {
        _games = games;
        _streamer = streamer;
        _env = env;
        _log = log;
        _statusPath = statusPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pins = new PinKeeper(env, log, _now);
        _rewarm = new Rewarmer(this, env, log, _now);
    }

    /// 기록 없이 (통째·프로필) 예열에 성공했을 때: 게임과 방식 이름표.
    public event Action<GameSettings, string>? ColdWarmed;

    /// 예열이 끝났을 때의 실측 MB/s (드라이브 회복 판정의 캐시 적중 상한용).
    public event Action<GameSettings, double>? WarmMeasured;

    /// 핫 집합 예열의 진행 (묶음마다, ProgressEvery 에 한 번 이하): 게임, 읽은 MiB, 총 MiB.
    public event Action<GameSettings, long, long>? Progress;

    /// 핫 집합 스트림이 끝났을 때 (취소·실패 포함): 게임, 끝까지 읽었는가.
    public event Action<GameSettings, bool>? StreamEnded;

    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    /// 게임 업데이트 직후의 지연 (auto.post_update_delay_min): 그 게임이 아직 기다려야 하면 남은 시간, 아니면 null. 기본은 지연 없음 (서비스가 자동 상태를 보고 정함).
    public Func<GameSettings, TimeSpan?> PostUpdateWait { get; set; } = _ => null;

    /// 업데이트를 감지해 재계획 상태가 된 때 (없으면 null): probe 모드에서 이 드라이브의 회복 판정 (Recovery) 의 기준 시각. 기본 null (고정 분만 씀).
    public Func<GameSettings, DateTimeOffset?> ReplanStart { get; set; } = _ => null;

    /// 업데이트 지연으로 미뤄야 하는 게임이면 true (지연 한 번에 로그는 한 줄). 지연이 끝난 게임은 다음 지연 때 다시 로그를 남깁니다.
    private bool Deferred(GameSettings game)
    {
        // AutoRelocator.PostUpdateDelayed 와 같은 판정: probe 모드이고 이 드라이브에 회복 추적이 있으면 그 판정, 없으면 고정 분.
        bool? probed = _auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && _auto.PostUpdateDelayMin > 0 && Recovery is { } recovery && ReplanStart(game) is { } at
            ? recovery.WatchUpdate(PlanFile.SplitDrive(game.OriginRoot).ToUpperInvariant(), at, TimeSpan.FromMinutes(_auto.PostUpdateDelayMin * 4)) : null;
        bool wait = probed ?? PostUpdateWait(game) is not null;
        lock (_gate)
        {
            if (!wait)
            {
                _delayLogged.Remove(game.Name);
            }
            else if (_delayLogged.Add(game.Name))
            {
                _log.Info(AutoRelocator.PostUpdateLine(game.Name, _auto.PostUpdateDelayMin));
            }
        }

        return wait;
    }

    /// 게임 exe 가 떴을 때 (스트림 취소보다 먼저): 도는 예열 스트림이 그 게임의 것이거나 같은 디스크의 것이면 다음 묶음 경계에서 멈추게 해 디스크를 양보하고 (같은 HDD 동시 읽기는 게임을 느리게 함),
    /// 단 auto.keep_stream_on_game_start (실험용) 가 켜져 있으면 그 게임의 스트림은 끝까지 읽습니다. 기록 없는 예열이 잡아 둔 예산은 풉니다 (게임이 돌면 RAM 이 달라지므로 다음 예산 계산에 쓰지 않음).
    public void NoteGameStart(string game)
    {
        if (_streaming is { } reading)
        {
            bool same = string.Equals(reading, game, StringComparison.OrdinalIgnoreCase);
            if (same && _auto.KeepStreamOnGameStart)
            {
                _keepStream = true;
            }
            else if (same || SameDisk(reading, game))
            {
                lock (_gate)
                {
                    _yield ??= (game, Stopwatch.GetTimestamp());
                }

                try
                {
                    _streamStop?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 스트림이 방금 끝남
                }
            }
        }

        lock (_gate)
        {
            _coldMib.Remove(game);
        }
    }

    // 두 게임이 같은 물리 디스크에 있는가 (env.SameDisk: 한 디스크에 볼륨이 둘이어도 같다고 봄).
    private bool SameDisk(string a, string b) =>
        _games.FirstOrDefault(g => string.Equals(g.Name, a, StringComparison.OrdinalIgnoreCase)) is { } x && _games.FirstOrDefault(g => string.Equals(g.Name, b, StringComparison.OrdinalIgnoreCase)) is { } y
        && _env.SameDisk(x.OriginRoot, y.OriginRoot);

    // 이 게임이나 같은 드라이브의 등록 게임이 돌고 있는가: 돌면 그 디스크에 새 읽기를 내지 않습니다.
    private bool DriveHasRunningGame(GameSettings game) => _env.GameRunning(game) || AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Any(_env.GameRunning);

    // 게임 시작으로 멈춘 스트림이면 양보 시간과 읽은 양을 로그에 남기고 표시를 지웁니다.
    private void LogYield(string streamGame, long bytes)
    {
        (string Game, long At)? y;
        lock (_gate)
        {
            (y, _yield) = (_yield, null);
        }

        if (y is { } v)
        {
            _log.Info($"{streamGame}: yielded to {v.Game} after {(long)Stopwatch.GetElapsedTime(v.At).TotalMilliseconds} ms, {bytes >> 20} MiB");
        }
    }

    /// 자동 모드 설정 (full_prewarm 등)을 바꿉니다. 시작 때와 설정을 다시 읽을 때 부름.
    public void SetAuto(AutoSettings auto) => _auto = auto;

    /// 이 게임이 기록 없는 예열 대상인가: 기능이 켜져 있고 (자동 모드, full_prewarm 이나 engine_profiles) 원시 트레이스도 prefetch 기록도 없음.
    public bool IsCold(GameSettings game)
    {
        AutoSettings a = _auto;
        return _env.ColdPlanner is not null && a.Enabled && (a.FullPrewarm || a.EngineProfiles) && !_env.TraceExists(game) && !_env.RawTraceExists(game);
    }

    /// 재배치 직후 예열의 회복 기간 고정이 쓰는 드라이브 회복 판정. null 이면 그 고정은 꺼진 것과 같습니다.
    public IDriveRecovery? Recovery
    {
        get => _pins.Recovery;
        set => _pins.Recovery = value;
    }

    /// 지금 고정을 들고 있는 게임 이름.
    public IReadOnlyCollection<string> Pinned => _pins.Held;

    internal IReadOnlyList<GameSettings> Games => _games;

    /// 이 게임을 마지막으로 예열한 시각, 예열한 적이 없으면 null.
    public DateTimeOffset? LastWarmed(string game)
    {
        lock (_gate)
        {
            return _last.TryGetValue(game, out DateTimeOffset at) ? at : null;
        }
    }

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운·상태는 유지). pin 이 꺼졌거나 빠진 게임의 고정은 풉니다.
    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        _games = games;
        lock (_gate)
        {
            foreach (string gone in _coldMib.Keys.Where(k => !games.Any(g => string.Equals(g.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _coldMib.Remove(gone);
                _coldSamples.Remove(gone);
                _refusedLogged.Remove(gone);
            }
        }

        _pins.Reconcile(games);
    }

    /// 한 게임의 고정을 풉니다 (그 게임 exe 가 뜰 때, 스트림보다 먼저).
    public void ReleasePin(string game, string why) => _pins.Release(game, why);

    /// 모든 고정을 풉니다 (런처가 뜰 때). 회복 기간 고정은 남깁니다 (런처 업데이트는 파일 변경 해제로 막음).
    public void ReleasePins(string why) => _pins.ReleaseAll(why, keepRecovery: true);

    public void Shutdown() => _pins.ReleaseAll("service stopping");

    /// 주기 점검 (재적용 틱마다): 가용 RAM 이 모자라면 고정 해제, 예열 유지 주기가 되면 식은 게임을 다시 예열. ct 가 취소되면 (게임 시작) 시작하지 않은 예열은 하지 않습니다.
    public void Tick(AutoSettings auto, CancellationToken ct = default)
    {
        _pins.CheckMemory(_games);
        _pins.CheckRecovery();
        _rewarm.Tick(auto, ct);
    }

    /// 예열에 성공한 적이 있는 게임 (최근 순, 설정에 아직 있는 것만).
    internal IReadOnlyList<GameSettings> Warmed()
    {
        lock (_gate)
        {
            return [.. _games.Where(g => _last.ContainsKey(g.Name)).OrderByDescending(g => _last[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal)];
        }
    }

    /// 기록 없이 예열한 게임의 재예열 점검 표본 (그 게임이 지금도 기록 없는 예열 대상이고 예열한 적이 있을 때만), 아니면 null.
    internal IReadOnlyList<(string Path, long Offset, int Bytes)>? ColdSamples(GameSettings game)
    {
        lock (_gate)
        {
            return IsCold(game) && _coldSamples.TryGetValue(game.Name, out IReadOnlyList<(string, long, int)>? s) ? s : null;
        }
    }

    /// 게임 시작 때 핫 집합이 RAM 에 있는 비율 (%): 표본 몇 곳 (CacheProbe.PickSamples 규모) 을 읽어 느리지 않은 곳의 비율. 잴 표본이 없으면 null.
    public int? HitPercent(GameSettings game)
    {
        ProbeResult? p = ColdSamples(game) is { } samples ? _env.ProbeSamples(samples) : _env.ProbeReadMs(game);
        return p is { Samples: > 0 } ? (p.Samples - p.Slow) * 100 / p.Samples : null;
    }

    internal PrewarmResult RewarmGame(GameSettings game, CancellationToken ct = default) => Guarded(game, "rewarm", pin: true, ignoreCooldown: true, ct);

    /// 이 프로세스가 어느 게임의 trigger_exes 에 들어 있는가 (대소문자 무시).
    public bool IsTrigger(string exeName) => _games.Any(g => IsTrigger(g, exeName));

    public static bool IsTrigger(GameSettings g, string exeName) => g.Prewarm.TriggerExes.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// 로그용 예열 원인: "trigger X.exe" → "X.exe", "auto cold start" → "cold start", 그 밖은 앞의 "auto " 만 뗌.
    public static string TriggerCause(string why) => why.StartsWith("trigger ", StringComparison.Ordinal) ? why["trigger ".Length..] : why.StartsWith("auto ", StringComparison.Ordinal) ? why["auto ".Length..] : why;

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로.
    /// recent (최근순) 를 주면 한 묶음으로 합칩니다: on_service_start 게임 먼저, 그다음 최근순 (중복 제거) — RAM 예산을 한 번만 씁니다.
    public IReadOnlyList<PrewarmResult> RunServiceStart(IEnumerable<GameSettings>? recent = null, CancellationToken ct = default)
    {
        GameSettings[] first = _games.Where(g => g.Prewarm.OnServiceStart).ToArray();
        IEnumerable<GameSettings> rest = (recent ?? []).Where(g => !first.Any(f => string.Equals(f.Name, g.Name, StringComparison.OrdinalIgnoreCase)));
        return RunBatch(first.Concat(rest), "service start", ct: ct);
    }

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로. narrow 가 있으면 그 목록에서 예열할 게임만 고릅니다.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start, Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? narrow = null, CancellationToken ct = default)
    {
        IReadOnlyList<GameSettings> hit = _games.Where(g => IsTrigger(g, start.ExeName)).ToList();
        return RunBatch(narrow?.Invoke(hit) ?? hit, $"trigger {start.ExeName}", pin: !LauncherExes.Is(start.ExeName), ct: ct); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
    }

    /// 주어진 게임을 차례로 (한 게임의 실패가 나머지를 막지 않음).
    public IReadOnlyList<PrewarmResult> RunAll(IEnumerable<GameSettings> games, string why) => RunBatch(games, why);

    // 한 묶음의 예산 = min(물리 RAM / 4, 가용 RAM - 여유). 존 크기 합이 예산을 넘으면 그 뒤 게임은 건너뜁니다 (앞쪽 = 최근 순서가 우선).
    // 서비스 시작·런처 시작이 같은 규칙입니다. 첫 게임의 가용 RAM 쪽 판단은 Run 의 자체 판단에 맡깁니다.
    private IReadOnlyList<PrewarmResult> RunBatch(IEnumerable<GameSettings> games, string why, bool pin = true, CancellationToken ct = default)
    {
        var results = new List<PrewarmResult>();
        IReadOnlyList<GameSettings> list = games as IReadOnlyList<GameSettings> ?? [.. games];
        WarmHeads(list, ct);
        long avail = _env.AvailableRamMib();
        long physCap = _env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue; // MiB, 물리 RAM 의 1/4
        long used = 0; // 핫 집합 예열의 MiB (기록 없는 예열은 _coldMib 가 셈)
        bool cut = false;
        foreach (GameSettings g in list)
        {
            if (Deferred(g))
            {
                results.Add(new PrewarmResult(g.Name, false, PostUpdateDelay, null, _now()));
                continue;
            }

            long zoneMib = ((_env.ZoneBytes(g) ?? 0) + (1 << 20) - 1) >> 20;
            long held = used + ColdMib(null); // 이미 올린 양 (기록 없는 예열 포함)
            if (cut || held + zoneMib > physCap || (held > 0 && held + zoneMib > avail - g.Prewarm.MinFreeRamMib))
            {
                cut = true;
                _log.Info($"{g.Name}: prewarm skipped (batch zones {held + zoneMib} MiB exceed the budget: min(1/4 of RAM, available {avail} MiB - reserve {g.Prewarm.MinFreeRamMib} MiB)) [{why}]");
                results.Add(new PrewarmResult(g.Name, false, "batch memory budget", null, _now()));
                continue;
            }

            PrewarmResult r = Guarded(g, why, pin, ct: ct, usedMib: used);
            results.Add(r);
            used += r.Ran && r.Mode is null ? zoneMib : 0;
        }

        return results;
    }

    // 묶음에 게임이 둘 이상이면 각 게임의 로딩 앞부분 (tier0) 을 최근 순서로 먼저 모두 올리고, 그 뒤 게임별 전체 (tier1 포함) 를 읽습니다 (앞부분은 이미 올라와 있어 다시 읽어도 빠름).
    // 올릴 게임은 RunBatch 와 같은 예산과 Run 과 같은 건너뜀 조건으로 고릅니다. 취소는 앞부분 묶음 사이에서 보고, 읽던 게임이 시작되면 끝까지 읽습니다 (StreamHot 과 같음).
    private void WarmHeads(IReadOnlyList<GameSettings> games, CancellationToken ct)
    {
        if (games.Count < 2)
        {
            return;
        }

        long avail = _env.AvailableRamMib();
        long physCap = _env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue;
        long planned = ColdMib(null);
        foreach (GameSettings g in games)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (Deferred(g))
            {
                continue;
            }

            long zoneMib = ((_env.ZoneBytes(g) ?? 0) + (1 << 20) - 1) >> 20;
            if (planned + zoneMib > physCap || (planned > 0 && planned + zoneMib > avail - g.Prewarm.MinFreeRamMib))
            {
                return; // 이 게임부터 RunBatch 도 건너뜀
            }

            TimeSpan? since = LastWarmed(g.Name) is { } prev ? _now() - prev : null;
            if (IsCold(g) || PrewarmPolicy.SkipReason(_env.ZoneBytes(g), _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin)) is not null)
            {
                continue;
            }

            planned += zoneMib;
            lock (_run)
            {
                StreamHead(g, ct);
            }
        }
    }

    private void StreamHead(GameSettings game, CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
        lock (_gate)
        {
            _yield = null;
        }

        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            if (!_keepStream)
            {
                stop.Cancel();
            }
        });
        try
        {
            _streamer.StreamHead(game, stop.Token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: load head prewarm failed: {e.GetType().Name}"); // 전체 예열(Run)이 같은 오류를 판단해 기록함
        }
        finally
        {
            (_streaming, _streamStop) = (null, null);
            LogYield(game.Name, 0);
        }
    }

    // 기록 없이 예열한 게임들이 지금도 RAM 에 있다고 보는 양 (MiB): 기록한 양이되, 표본을 다시 읽어 식었으면 (CacheProbe) 0 으로 바꿉니다. 영구히 예산을 막지 않게.
    // 표본을 읽을 수 없으면 (null) 기록한 양을 그대로 씁니다. 표본 읽기는 잠금 밖에서.
    private long ColdMib(string? except)
    {
        List<(string Name, IReadOnlyList<(string Path, long Offset, int Bytes)>? Samples)> held;
        lock (_gate)
        {
            held = [.. _coldMib.Where(kv => kv.Value > 0 && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Select(kv => (kv.Key, _coldSamples.GetValueOrDefault(kv.Key)))];
        }

        foreach ((string name, var samples) in held)
        {
            bool busy = _games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } gm && DriveHasRunningGame(gm); // 같은 디스크에서 게임이 도는 동안은 표본을 읽지 않음
            if (!busy && samples is not null && _env.ProbeSamples(samples) is { Cold: true })
            {
                lock (_gate)
                {
                    if (_coldMib.ContainsKey(name))
                    {
                        _coldMib[name] = 0;
                    }
                }

                _log.Info($"{name}: cold prewarm no longer counts against the budget (its samples left the file cache)");
            }
        }

        lock (_gate)
        {
            return _coldMib.Where(kv => !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
        }
    }

    // 한 게임의 예외가 나머지 게임의 예열을 막지 않게 합니다.
    internal PrewarmResult Guarded(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0)
    {
        try
        {
            return Run(game, why, pin, ignoreCooldown, ct, usedMib);
        }
        catch (Exception e)
        {
            _log.Error($"{game.Name}: prewarm failed ({why})", e);
            return new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, _now());
        }
    }

    /// 한 게임을 지금 예열 (조건을 만족할 때만). 동시에 하나만 돕니다.
    /// ct 는 스트림을 시작하기 전 (차례를 기다린 뒤) 과 핫 집합 스트림의 묶음 사이에서 확인합니다. 단 지금 읽는 그 게임이 시작되면 (NoteGameStart) 이 스트림도 다음 묶음 경계에서 멈춥니다 (keep_stream_on_game_start 가 켜졌을 때만 끝까지 읽음).
    /// usedMib: 같은 묶음에서 앞서 올린 핫 집합의 양 (기록 없는 예열의 예산에서 뺌).
    public PrewarmResult Run(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0)
    {
        lock (_run)
        {
            DateTimeOffset now = _now();
            if (ct.IsCancellationRequested)
            {
                _log.Info($"{game.Name}: prewarm cancelled ({why})");
                return new PrewarmResult(game.Name, false, Cancelled, null, now);
            }

            if (Deferred(game))
            {
                return new PrewarmResult(game.Name, false, PostUpdateDelay, null, now);
            }

            if (IsCold(game) && !(_env.ColdRefusal(game) is null && _env.PrefetchBoot(game))) // S2 가 먼저: boot 트레이스가 생기면 더는 기록 없는 게임이 아님
            {
                return RunCold(game, why, now, ct, usedMib);
            }

            TimeSpan? since = !ignoreCooldown && LastWarmed(game.Name) is { } prev ? now - prev : null;
            string? skip = PrewarmPolicy.SkipReason(_env.ZoneBytes(game), _env.TraceExists(game), DriveHasRunningGame(game), _env.AvailableRamMib(),
                game.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(game.Prewarm.CooldownMin));
            if (skip is null)
            {
                _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                try
                {
                    (StreamStats s, bool complete) = StreamHot(game, ct);
                    if (!complete)
                    {
                        _log.Info($"{game.Name}: prewarm cancelled ({why}) after {s.Bytes >> 20} MiB");
                        return Record(game, now, new PrewarmResult(game.Name, false, Cancelled, s, now), 0);
                    }

                    lock (_gate)
                    {
                        _last[game.Name] = now;
                        _coldMib.Remove(game.Name); // 기록이 생겨 핫 집합으로 바뀜
                        _coldSamples.Remove(game.Name);
                    }

                    double mbps = s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0;
                    _log.Info($"{game.Name}: prewarm done ({why}): {s.Bytes >> 20} MiB in {s.Seconds:F2} s = {mbps:F0} hint MB/s, {s.Requests} requests, {s.Errors} errors");
                    PrewarmResult done = Record(game, now, new PrewarmResult(game.Name, true, null, s, now), mbps);
                    WarmMeasured?.Invoke(game, mbps);
                    if (pin && !ct.IsCancellationRequested)
                    {
                        _pins.TryPin(game, why == AfterRelocation);
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

    // 핫 집합 스트림 한 번. 게임이 시작되면 (NoteGameStart) 다음 묶음 경계에서 멈춥니다. 단 keep_stream_on_game_start 가 켜졌고 그 게임의 스트림이면 ct 가 취소돼도 끝까지 읽습니다.
    private (StreamStats Stats, bool Complete) StreamHot(GameSettings game, CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
        lock (_gate)
        {
            _yield = null;
        }

        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            if (!_keepStream)
            {
                stop.Cancel();
            }
        });
        DateTimeOffset? last = null;
        bool complete = false;
        try
        {
            (StreamStats, bool) r = _streamer.Stream(game, stop.Token, (done, total) =>
            {
                DateTimeOffset at = _now();
                if (last is null || at - last >= ProgressEvery)
                {
                    last = at;
                    Progress?.Invoke(game, done >> 20, (total + (1 << 20) - 1) >> 20);
                }
            });
            complete = r.Item2;
            LogYield(game.Name, r.Item1.Bytes);
            return r;
        }
        finally
        {
            (_streaming, _streamStop) = (null, null);
            StreamEnded?.Invoke(game, complete);
        }
    }

    // 기록 (원시 트레이스·prefetch 기록) 이 없는 게임: 설치 폴더가 예산 이하면 통째로, 크면 엔진 프로필로. 쿨다운은 항상 지킵니다 (같은 폴더를 연달아 두 번 읽지 않게): 재예열도 쿨다운 안이면 다음 점검으로 미룸.
    // 순서: 드라이브 거부 (시스템 SSD·SSD·판정 불가) → 게임 실행 중 → 쿨다운 → 디스크 유휴 → RAM 예산 → 폴더 열거 (비싼 일은 마지막).
    private PrewarmResult RunCold(GameSettings game, string why, DateTimeOffset now, CancellationToken ct, long usedMib)
    {
        AutoSettings a = _auto;
        TimeSpan cooldown = TimeSpan.FromMinutes(game.Prewarm.CooldownMin);
        if (_env.ColdRefusal(game) is { } refused)
        {
            bool first;
            lock (_gate)
            {
                first = _refusedLogged.Add(game.Name);
            }

            if (first)
            {
                _log.Info($"{game.Name}: prewarm without a record skipped ({refused}) [{why}]");
            }

            return new PrewarmResult(game.Name, false, refused, null, now); // 상태 파일에도 쓰지 않음: 기록 없는 예열 대상이 아닌 드라이브
        }

        string? skip = null;
        if (DriveHasRunningGame(game))
        {
            skip = "game is running";
        }
        else if (LastWarmed(game.Name) is { } prev && now - prev < cooldown)
        {
            skip = $"cooldown ({Math.Ceiling((cooldown - (now - prev)).TotalMinutes)} min left)";
        }
        else if (_env.DiskBytesPerSecond(game) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
        {
            skip = "drive busy";
        }

        long held = usedMib + ColdMib(game.Name);
        long avail = _env.AvailableRamMib();
        long availBudget = avail - game.Prewarm.MinFreeRamMib - held; // MiB
        long ram = Math.Min(availBudget, (_env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue) - held); // min(물리 RAM / 4, 가용 RAM - 여유) - 이미 올린 양
        long fullMib = !a.FullPrewarm ? 0 : a.FullPrewarmMaxMib > 0 ? Math.Min(a.FullPrewarmMaxMib, availBudget) : ram;
        long profileMib = !a.EngineProfiles ? 0 : a.FullPrewarm && a.FullPrewarmMaxMib > 0 ? Math.Min(ram, fullMib) : ram;
        if (skip is null && Math.Max(fullMib, profileMib) <= 0)
        {
            skip = $"low memory (available {avail} MiB, reserve {game.Prewarm.MinFreeRamMib} MiB, already held {held} MiB)";
        }

        StreamStats? stats = null;
        string? label = null;
        List<(string Path, long Offset, int Bytes)>? samples = null;
        if (skip is null)
        {
            try
            {
                ColdPlan plan = _env.ColdPlanner!(game, Math.Max(0, fullMib) << 20, Math.Max(0, profileMib) << 20, ct);
                if (plan.Skip is not null)
                {
                    skip = plan.Skip;
                }
                else
                {
                    _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                    (StreamStats s, bool complete) = _env.ColdRead(plan.Ops, ct);
                    (stats, label, skip) = (s, plan.Label, complete ? null : Cancelled);
                    samples = CacheProbe.PickSamples(plan.Ops);
                }
            }
            catch (OperationCanceledException)
            {
                skip = Cancelled;
            }
            catch (DirectoryNotFoundException)
            {
                skip = "install folder not found";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Error($"{game.Name}: prewarm failed ({why})", e);
                return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
            }
        }

        if (skip is not null || stats is null)
        {
            _log.Info($"{game.Name}: prewarm {(skip == Cancelled ? "cancelled" : $"skipped ({skip})")} [{why}]");
            return Record(game, now, new PrewarmResult(game.Name, false, skip, stats, now), 0);
        }

        lock (_gate)
        {
            _last[game.Name] = now;
            _coldMib[game.Name] = (stats.Bytes + (1 << 20) - 1) >> 20;
            _coldSamples[game.Name] = samples!;
        }

        double mbps = stats.Seconds > 0 ? stats.Bytes / 1048576.0 / stats.Seconds : 0;
        _log.Info($"{game.Name}: prewarm done ({why}, {label}): {stats.Bytes >> 20} MiB in {stats.Seconds:F2} s = {mbps:F0} hint MB/s, {stats.Requests} requests, {stats.Errors} errors");
        PrewarmResult done = Record(game, now, new PrewarmResult(game.Name, true, null, stats, now) { Mode = label }, mbps);
        WarmMeasured?.Invoke(game, mbps);
        ColdWarmed?.Invoke(game, label!);
        return done;
    }

    private PrewarmResult Record(GameSettings game, DateTimeOffset now, PrewarmResult r, double mbps)
    {
        _status[game.Name] = new JObject
        {
            { "at", now.ToString("O") },
            { "result", r.Ran ? "ok" : r.SkipReason!.StartsWith("failed", StringComparison.Ordinal) ? "failed" : "skipped" },
            { "reason", r.SkipReason },
            { "bytes", r.Stats?.Bytes },
            { "hint_accepted_bytes", r.Stats?.HintAcceptedBytes },
            { "seconds", r.Stats?.Seconds },
            { "hint_mb_per_s", r.Stats is null ? null : Math.Round(mbps, 1) },
        };
        if (r.Ran && r.Mode is null && _streamer.LoadHeadBytes(game) is { } head)
        {
            ((JObject)_status[game.Name]!)["head_mib"] = head >> 20; // 로딩 앞부분 (관리 창 상태 탭)
        }

        if (r.Mode is not null)
        {
            ((JObject)_status[game.Name]!)["mode"] = r.Mode;
        }

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
