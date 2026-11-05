using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
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

    /// 게임 폴더 이름으로 짐작한 안티치트 ("unknown" 은 탐지 실패): 안티치트 게임은 고정하지 않음. 기본은 없음.
    public Func<GameSettings, string?> AntiCheatOf { get; init; } = _ => null;

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

    /// 파일 목록 예열 (Prewarmer.WarmFiles) 의 읽기: 파일마다 낮은 I/O 우선순위 힌트를 붙인 버퍼드 읽기 (묶음 사이에서 취소를 봄). 시험은 가짜로 바꿉니다.
    public Func<IReadOnlyList<ReadOp>, CancellationToken, (StreamStats Stats, bool Complete)> FileRead { get; init; } = FileWarm.Read;

    /// 기록 없는 예열이 건드리면 안 되는 드라이브면 이유 (시스템 SSD·SSD·판정 불가), 아니면 null. 기본은 거부 없음 (시험용), Create 는 진짜 판정.
    public Func<GameSettings, string?> ColdRefusal { get; init; } = _ => null;

    /// 부트스트랩 S2: 기록 없는 게임의 boot 트레이스를 프리페치 기록 (.pf) 으로 만들어 봅니다 (ColdRefusal 이 없을 때만 부름). 만들었으면 true: 그러면 더는 기록 없는 게임이 아니라 통째·프로필 예열 (S3) 을 하지 않습니다. 기본 false.
    public Func<GameSettings, bool> PrefetchBoot { get; init; } = _ => false;

    /// 기록 없이 예열한 게임의 표본 (경로, 오프셋, 길이) 들을 읽어 잰 결과 (CacheProbe), 읽을 수 없으면 null.
    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?> ProbeSamples { get; init; } = s => CacheProbe.Probe(s);

    /// 파일 목록 예열 (Prewarmer.WarmFiles) 의 이미 캐시됐는가 점검용 표본 읽기: 표본 사이에서 취소를 봅니다 (취소되면 null).
    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, CancellationToken, ProbeResult?> ProbeFileSamples { get; init; } = (s, ct) => CacheProbe.Probe(s, ct: ct);

    /// 부팅 뒤 경과 시간 (ms). 서비스 시작 예열은 부팅 직후의 디스크 경쟁을 피해 BootDelay 가 지난 뒤에 시작합니다. 시험 기본값은 하루 (이미 지남).
    public Func<long> UptimeMs { get; init; } = () => (long)TimeSpan.FromDays(1).TotalMilliseconds;

    /// 서비스 시작 예열의 대기 (취소되면 일찍 돌아옴). 시험 기본값은 기다리지 않음.
    public Action<TimeSpan, CancellationToken> Sleep { get; init; } = (_, _) => { };

    public static PrewarmEnv Create() => new()
    {
        UptimeMs = () => Environment.TickCount64,
        Sleep = (t, ct) => ct.WaitHandle.WaitOne(t),
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
        AntiCheatOf = g => AutoEnv.DetectAntiCheat(g.OriginRoot),
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
        ZoneBytes = g => PrefetchStreamer.ZoneBytes(g) is { } z ? Math.Min(z, g.StreamCapBytes(z)) : null,
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

    /// 같은 디스크에서 재배치가 시작했거나 도는 중이라 읽기를 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string RelocationRunning = "relocation running";

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
    private readonly Dictionary<string, DateTimeOffset> _lastBeforeBoot = new(StringComparer.OrdinalIgnoreCase); // 부팅 전에 성공한 예열 시각 (상태 파일), 새 트레이스 판정 (LastWarmed) 만 씀, _gate 로 보호
    private readonly JObject _status = new();
    private readonly PinKeeper _pins;
    private readonly Rewarmer _rewarm;
    private readonly Dictionary<string, long> _coldMib = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열해 RAM 에 올려 둔 양 (MiB), _gate 로 보호
    private readonly Dictionary<string, IReadOnlyList<(string Path, long Offset, int Bytes)>> _coldSamples = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열한 파일 목록에서 고른 재예열 점검 표본, _gate 로 보호
    private readonly Dictionary<string, string> _profileMiss = new(StringComparer.OrdinalIgnoreCase); // 엔진 프로필이 없다는 판정과 그때의 설치 폴더 mtime 키 (prewarm_status.json 의 profile_miss_key), _gate 로 보호
    private readonly HashSet<string> _refusedLogged = new(StringComparer.OrdinalIgnoreCase); // 드라이브 거부를 이미 로그한 게임, _gate 로 보호
    private readonly HashSet<string> _delayLogged = new(StringComparer.OrdinalIgnoreCase); // 업데이트 지연을 이미 로그한 게임, _gate 로 보호
    private readonly HashSet<string> _relocHoldLogged = new(StringComparer.OrdinalIgnoreCase); // 재배치 중이라 시작 예열을 보류한 것을 이미 로그한 게임, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _skipLogged = new(StringComparer.OrdinalIgnoreCase); // 게임|사유별 마지막으로 남긴 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _triggerSeen = new(StringComparer.OrdinalIgnoreCase); // 트리거 exe 별 마지막 처리 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private volatile AutoSettings _auto = AutoSettings.Default;
    private volatile string? _streaming; // 핫 집합 스트림이 도는 게임
    private volatile string? _coldReading; // 기록 없는 (통째·프로필) 예열의 읽기가 도는 게임: _streaming 과 달리 게임 시작 양보 신호는 받지 않음 (ct 로만 멈춤)
    private readonly HashSet<string> _relocStarted = new(StringComparer.OrdinalIgnoreCase); // 재배치가 시작한 (아직 끝나지 않은) 게임, _gate 로 보호
    private volatile bool _keepStream; // auto.keep_stream_on_game_start 가 켜진 채 그 게임이 시작돼 (같은 데이터를 동시에 읽으므로) 취소 신호에도 끝까지 읽기로 한 표시
    private volatile CancellationTokenSource? _streamStop; // 도는 스트림을 멈추는 신호 (게임 시작이 같은 디스크 읽기를 바로 끊음)
    private volatile CancellationTokenSource? _warmStop; // WarmFiles 읽기를 멈추는 신호 (호출자 ct 와 연결, 같은 디스크 게임 시작이 끊음), 읽는 게임은 _coldReading
    private (string Game, long At)? _yield; // 게임 시작으로 스트림 양보를 요청한 게임과 시각 (Stopwatch 틱), _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _partial = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 상한으로 핫 집합의 앞부분만 읽은 게임 (읽은 양, 전체), 보충 예열이 끝나면 지움, _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _startupTrim = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 예열이 상한으로 줄여 읽는 중인 게임 (Record 가 같은 상태 쓰기에 warmed_mib/hot_mib 를 넣음), _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _idleSince = new(StringComparer.OrdinalIgnoreCase); // 보충 예열을 기다리는 게임이 한가해지기 시작한 시각, _gate 로 보호

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
        LoadStatus();
    }

    // 이전 실행의 상태 파일: 항목은 그대로 이어 쓰고, 부팅 뒤에 성공한 예열만 _last 로 되살려 서비스만 다시 시작했을 때 같은 게임을 또 읽지 않게 합니다 (부팅 전 것은 파일 캐시가 비었으므로 제외).
    private void LoadStatus()
    {
        if (_statusPath is null || !File.Exists(_statusPath))
        {
            return;
        }

        try
        {
            if (JsonRead.ReadFile(_statusPath) is not JObject root || !root.TryGetValue("games", out object? games) || games is not JObject all)
            {
                return;
            }

            DateTimeOffset boot = _now() - TimeSpan.FromMilliseconds(_env.UptimeMs());
            foreach ((string name, object? value) in all)
            {
                if (value is not JObject o)
                {
                    continue;
                }

                _status[name] = o;
                if (o.TryGetValue("profile_miss_key", out object? miss) && miss is string missKey)
                {
                    _profileMiss[name] = missKey; // 프로필 미일치 판정은 설치 폴더 mtime 이 같은 동안 다시 열거하지 않음 (재시작 뒤에도 이어짐)
                }

                DateTimeOffset? okAt = LastOkAt(o);
                if (okAt is { } earlier && earlier <= boot)
                {
                    _lastBeforeBoot[name] = earlier; // 부팅 전 예열: _last 로는 되살리지 않음 (캐시가 비었음), 새 트레이스로 다시 치지 않도록 LastWarmed 만 돌려줌
                }

                if (okAt is { } when && when > boot)
                {
                    _last[name] = when;
                    if (o.TryGetValue("warmed_mib", out object? warmed) && warmed is long w && o.TryGetValue("hot_mib", out object? hot) && hot is long h && w < h)
                    {
                        _partial[name] = (w, h); // 부분 예열 뒤 서비스만 다시 시작해도 보충이 이어짐
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn("prewarm status file not read: " + e.GetType().Name);
        }
    }

    // 마지막 성공 시각: last_ok_at 은 마지막 시도가 건너뜀·실패여도 남습니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게). 옛 파일은 result ok 의 at.
    private static DateTimeOffset? LastOkAt(JObject o)
    {
        string? text = null;
        if (o.TryGetValue("last_ok_at", out object? kept))
        {
            text = kept as string;
        }
        else if (o.TryGetValue("result", out object? result) && result as string == "ok" && o.TryGetValue("at", out object? at))
        {
            text = at as string;
        }

        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when) ? when : null;
    }

    // 이번 시도가 성공이 아니면 이전 항목의 마지막 성공 시각을 그대로 이어 씁니다.
    private string? KeptOkAt(string game) => _status.TryGetValue(game, out object? prior) && prior is JObject po && LastOkAt(po) is { } ok ? ok.ToString("O") : null;

    /// 기록 없이 (통째·프로필) 예열에 성공했을 때: 게임과 방식 이름표.
    public event Action<GameSettings, string>? ColdWarmed;

    /// 예열이 끝났을 때의 실측 MB/s (드라이브 회복 판정의 캐시 적중 상한용).
    public event Action<GameSettings, double>? WarmMeasured;

    /// 핫 집합 예열의 진행 (묶음마다, ProgressEvery 에 한 번 이하): 게임, 읽은 MiB, 총 MiB.
    public event Action<GameSettings, long, long>? Progress;

    /// 핫 집합 스트림이 끝났을 때 (취소·실패 포함): 게임, 끝까지 읽었는가.
    public event Action<GameSettings, bool>? StreamEnded;

    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    /// 서비스 시작 예열은 부팅 뒤 이만큼 지나고, 게임 드라이브가 IdleNeeded 동안 한가해진 뒤에 시작합니다 (부팅 직후 디스크 경쟁 방지).
    public static readonly TimeSpan BootDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan IdleNeeded = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GateStep = TimeSpan.FromSeconds(5);

    /// 기록 없는 예열의 통째 상한 기본값 (MiB, full_prewarm_max_mib 가 0 일 때).
    public const int DefaultFullPrewarmMib = 1024;

    /// 이 게임을 플레이한 적이 있는가 (자동 모드의 last_played). 재예열 점검은 플레이한 게임만 봅니다. 기본은 모두 참 (서비스가 자동 상태를 보고 정함).
    public Func<string, bool> HasPlayed { get; set; } = _ => true;

    /// 게임 업데이트 직후의 지연 (auto.post_update_delay_min): 그 게임이 아직 기다려야 하면 남은 시간, 아니면 null. 기본은 지연 없음 (서비스가 자동 상태를 보고 정함).
    public Func<GameSettings, TimeSpan?> PostUpdateWait { get; set; } = _ => null;

    /// 업데이트를 감지해 재계획 상태가 된 때 (없으면 null): probe 모드에서 이 드라이브의 회복 판정 (Recovery) 의 기준 시각. 기본 null (고정 분만 씀).
    public Func<GameSettings, DateTimeOffset?> ReplanStart { get; set; } = _ => null;

    /// 재배치 단계 (auto_status 의 relocating) 인 게임인가. 하나라도 있으면 서비스 시작 예열을 보류합니다.
    public Func<string, bool> Relocating { get; set; } = _ => false;

    /// 업데이트 지연으로 미뤄야 하는 게임이면 true (지연 한 번에 로그는 한 줄). 지연이 끝난 게임은 다음 지연 때 다시 로그를 남깁니다.
    private bool Deferred(GameSettings game)
    {
        // AutoRelocator.PostUpdateDelayed 와 같은 판정: probe 모드이고 이 드라이브에 회복 추적이 있으면 그 판정, 없으면 고정 분.
        bool? probed = _auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && Recovery is { } recovery && ReplanStart(game) is { } at
            ? recovery.WatchUpdate(PlanFile.SplitDrive(game.OriginRoot).ToUpperInvariant(), at, TimeSpan.FromMinutes(_auto.RecoveryMaxMin)) : null;
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

    /// 이 게임이나 같은 디스크의 게임을 읽는 예열 (핫 집합 스트림, 기록 없는 통째·프로필 읽기) 이 지금 도는가. 재배치가 시작하기 전에 보고 양보합니다 (같은 디스크에서 읽기와 쓰기가 겹치지 않게).
    public bool StreamingOnDisk(string game) => ReadingOnDisk(_streaming, game) || ReadingOnDisk(_coldReading, game);

    private bool ReadingOnDisk(string? reading, string game) => reading is not null && (string.Equals(reading, game, StringComparison.OrdinalIgnoreCase) || SameDisk(reading, game));

    // 이 게임과 같은 디스크의 등록 게임 이름들 (자신 포함). 디스크 조회는 잠금 밖에서 하고 잠금 안에서는 이름만 비교합니다.
    private HashSet<string> DiskMates(string game) =>
        new([game, .. _games.Where(g => !string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase) && SameDisk(g.Name, game)).Select(g => g.Name)], StringComparer.OrdinalIgnoreCase);

    /// 재배치 쪽: 같은 디스크의 예열 읽기가 없을 때만 재배치 시작 표시를 세우고 true (읽는 중이면 표시 없이 false). 확인과 표시가 _gate 아래에서 한 번에 이뤄져,
    /// 이 호출이 true 를 돌려준 뒤에는 같은 디스크의 예열이 읽기 표시를 세우지 못합니다 (TryMarkReading). 끝나면 (성공·실패·예외 모두) EndRelocation 을 불러야 합니다.
    public bool TryBeginRelocation(string game)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if ((_streaming is { } streaming && mates.Contains(streaming)) || (_coldReading is { } cold && mates.Contains(cold)))
            {
                return false;
            }

            _relocStarted.Add(game);
            return true;
        }
    }

    /// 재배치 시작 표시를 내립니다.
    public void EndRelocation(string game)
    {
        lock (_gate)
        {
            _relocStarted.Remove(game);
        }
    }

    // 예열 쪽: 같은 디스크에서 재배치가 시작했으면 false (표시를 세우지 않음), 아니면 mark 로 읽기 표시를 세우고 true. 확인과 표시가 _gate 아래에서 한 번에 이뤄집니다 (mark 는 필드 대입만).
    private bool TryMarkReading(string game, Action mark)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if (_relocStarted.Overlaps(mates))
            {
                return false;
            }

            mark();
            return true;
        }
    }

    // 묶음 경계 (게임 사이) 에서 재배치가 돌고 있으면 끝나고 디스크가 다시 한가해질 때까지 기다립니다 (시작 게이트와 같은 조건). 기다리는 중 취소되면 false.
    private bool WaitOutRelocation(GameSettings game, CancellationToken ct) => !RelocationHeld() || WaitStartupGate([game], ct);

    // 재배치 중인 게임이 있으면 (어느 게임이든) true. 보류 로그는 게임당 한 번, 재배치가 끝나면 다음에 다시 로그를 남깁니다.
    private bool RelocationHeld()
    {
        bool any = false;
        lock (_gate)
        {
            foreach (GameSettings g in _games)
            {
                if (!Relocating(g.Name))
                {
                    _relocHoldLogged.Remove(g.Name);
                    continue;
                }

                any = true;
                if (_relocHoldLogged.Add(g.Name))
                {
                    _log.Info($"{g.Name}: startup prewarm held while relocation is running");
                }
            }
        }

        return any;
    }

    /// 게임 exe 가 떴을 때 (스트림 취소보다 먼저): 도는 예열 스트림이 그 게임의 것이거나 같은 디스크의 것이면 다음 묶음 경계에서 멈추게 해 디스크를 양보하고 (같은 HDD 동시 읽기는 게임을 느리게 함),
    /// 단 auto.keep_stream_on_game_start (실험용) 가 켜져 있으면 그 게임의 스트림은 끝까지 읽습니다. 기록 없는 예열이 잡아 둔 예산은 풉니다 (게임이 돌면 RAM 이 달라지므로 다음 예산 계산에 쓰지 않음).
    public void NoteGameStart(string game)
    {
        if (_warmStop is { } warm && _coldReading is { } warming && (string.Equals(warming, game, StringComparison.OrdinalIgnoreCase) || SameDisk(warming, game)))
        {
            try
            {
                warm.Cancel(); // 챔피언 선읽기 (WarmFiles) 도 같은 디스크 게임 시작에 바로 멈춤
            }
            catch (ObjectDisposedException)
            {
                // 읽기가 방금 끝남
            }
        }

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

    /// 이 게임을 마지막으로 예열한 시각, 예열한 적이 없으면 null. 부팅 전에 한 예열은 원시·boot 트레이스가 있는 게임에만 돌려줍니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게).
    public DateTimeOffset? LastWarmed(string game)
    {
        DateTimeOffset? before;
        lock (_gate)
        {
            if (_last.TryGetValue(game, out DateTimeOffset at))
            {
                return at;
            }

            before = _lastBeforeBoot.TryGetValue(game, out DateTimeOffset prior) ? prior : null;
        }

        return before is { } t && _games.FirstOrDefault(g => string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase)) is { } gs && _env.RawTraceExists(gs) ? t : null;
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
        SupplementTick(ct);
        _rewarm.Tick(auto, ct);
    }

    /// 게임이 끝났을 때 (Worker 가 부름): 재예열 점검을 앞당깁니다.
    public void NoteGameExit() => _rewarm.PullIn(_auto);

    /// 예열에 성공한 적이 있는 게임 (최근 순, 설정에 아직 있는 것만).
    internal IReadOnlyList<GameSettings> Warmed()
    {
        lock (_gate)
        {
            return [.. _games.Where(g => _last.ContainsKey(g.Name) && HasPlayed(g.Name)).OrderByDescending(g => _last[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal)];
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

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로 (started 를 주면 그 조건을 만족하는 게임만: 자동 모드의 플레이한 게임).
    /// recent (최근순) 를 주면 한 묶음으로 합칩니다: on_service_start 게임 먼저, 그다음 최근순 (중복 제거) — RAM 예산을 한 번만 씁니다.
    /// 부팅 BootDelay 뒤 + 게임 드라이브 IdleNeeded 유휴까지 기다린 다음 (그동안 ct 가 취소되면 아무것도 하지 않음), 핫 집합이 이미 파일 캐시에 있는 게임은 읽지 않고 건너뜁니다.
    /// lastPlayed (게임 이름 → 마지막 플레이) 를 주면 (자동 모드가 켜진 경우) 대상을 최근 <see cref="StartupPlayedWithin"/> 안에 플레이한 게임으로 좁혀 마지막 플레이 순 상위 auto.startup_warm_games 개만 남기고
    /// (on_service_start 게임도 같은 순위), 합계 읽기량이 auto.startup_warm_max_mib 를 넘는 부분은 읽지 않습니다 (넘는 게임은 앞부분만, 이미 다 쓴 뒤의 게임은 건너뜀). 합계 상한은 기록이 없는 게임 (통째 예열) 도 포함해 항상 적용합니다.
    public IReadOnlyList<PrewarmResult> RunServiceStart(IEnumerable<GameSettings>? recent = null, CancellationToken ct = default, Func<GameSettings, bool>? started = null,
        Func<string, DateTimeOffset?>? lastPlayed = null)
    {
        AutoSettings a = _auto;
        GameSettings[] first = _games.Where(g => g.Prewarm.OnServiceStart && (started?.Invoke(g) ?? true)).ToArray();
        IEnumerable<GameSettings> merged = first.Concat((recent ?? []).Where(g => !first.Any(f => string.Equals(f.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        if (lastPlayed is not null)
        {
            DateTimeOffset cutoff = _now() - StartupPlayedWithin;
            merged = merged.Select(g => (Game: g, At: lastPlayed(g.Name))).Where(x => x.At is { } at && at >= cutoff)
                .OrderByDescending(x => x.At).ThenBy(x => x.Game.Name, StringComparer.Ordinal).Take(a.StartupWarmGames).Select(x => x.Game);
        }

        GameSettings[] list = [.. merged];
        if (list.Length == 0 || !WaitStartupGate(list, ct))
        {
            return [];
        }

        var skipped = new List<PrewarmResult>();
        var cold = new List<GameSettings>();
        var trimmed = new Dictionary<string, (long WarmedMib, long HotMib)>(StringComparer.OrdinalIgnoreCase);
        long plannedMib = 0;
        foreach (GameSettings g in list)
        {
            long? zone = _env.ZoneBytes(g);
            long mib = ((zone ?? (long)DefaultFullPrewarmMib << 20) + (1 << 20) - 1) >> 20; // 기록이 없으면 통째 예열의 기본 상한만큼으로 셈
            long room = a.StartupWarmMaxMib - plannedMib;
            if (AlreadyWarm(g))
            {
                LogSkipped(g.Name, "already warm", $"{g.Name}: prewarm skipped (already warm) [service start]");
                skipped.Add(new PrewarmResult(g.Name, false, "already warm", null, _now()));
            }
            else if (room <= 0 || (zone is null && mib > room))
            {
                LogSkipped(g.Name, "startup byte cap", $"{g.Name}: prewarm skipped (the service-start cap of {a.StartupWarmMaxMib} MiB is used up) [service start]");
                skipped.Add(new PrewarmResult(g.Name, false, "startup byte cap", null, _now()));
            }
            else
            {
                // 상한을 넘는 만큼은 읽지 않음: 핫 집합의 앞부분만 (max_stream_mib 를 남은 양으로 줄인 사본)
                plannedMib += Math.Min(mib, room);
                if (zone is not null && mib > room)
                {
                    trimmed[g.Name] = (room, mib);
                }

                cold.Add(zone is not null && mib > room ? g with { MaxStreamMib = (int)room, MaxStreamSet = true } : g);
            }
        }

        IReadOnlyList<PrewarmResult> ran;
        lock (_gate)
        {
            foreach (KeyValuePair<string, (long, long)> t in trimmed)
            {
                _startupTrim[t.Key] = t.Value;
            }
        }

        try
        {
            ran = RunBatch(cold, "service start", ct: ct, yieldToRelocation: true);
        }
        finally
        {
            lock (_gate)
            {
                _startupTrim.Clear();
            }
        }

        foreach (PrewarmResult r in ran.Where(r => r is { Ran: true, Mode: null }))
        {
            if (trimmed.TryGetValue(r.Game, out (long WarmedMib, long HotMib) t))
            {
                NotePartial(r.Game, t.WarmedMib, t.HotMib); // 상한으로 앞부분만 읽음: 유휴 때 나머지를 보충 (상태 파일에는 Record 가 이미 남김)
            }
        }

        return [.. skipped, .. ran];
    }

    /// 시작 예열이 상한으로 일부만 읽은 게임이 남아 있는가 (재예열 점검이 "표본이 따뜻함" 으로 건너뛰지 않게).
    public bool SupplementPending(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game);
        }
    }

    private void NotePartial(string game, long warmedMib, long hotMib)
    {
        lock (_gate)
        {
            _partial[game] = (warmedMib, hotMib);
        }

        _log.Info($"{game}: startup prewarm read {warmedMib} of {hotMib} MiB, the rest is read when the drive is idle");
    }

    // 상태 파일의 그 게임 항목에 warmed_mib/hot_mib 를 남깁니다 (항목이 없으면 아무것도 안 함).
    private void PatchStatus(string game, long warmedMib, long hotMib)
    {
        lock (_run)
        {
            if (_status.TryGetValue(game, out object? entry) && entry is JObject o)
            {
                o["warmed_mib"] = warmedMib;
                o["hot_mib"] = hotMib;
                FlushStatus();
            }
        }
    }

    // 보충 예열 대기 중인 게임이 IdleNeeded 동안 한가했으면 남은 구간을 읽습니다 (게임 미실행·재배치 아님·같은 디스크 게임 미실행·디스크 처리량 낮음).
    private void SupplementTick(CancellationToken ct)
    {
        string[] pending;
        lock (_gate)
        {
            pending = [.. _partial.Keys];
        }

        foreach (string name in pending)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } g)
            {
                lock (_gate)
                {
                    _partial.Remove(name);
                    _idleSince.Remove(name);
                }

                continue;
            }

            SupplementWhenIdle(g, ct);
        }
    }

    /// 보충 예열 대기 게임이 IdleNeeded 동안 연속으로 한가했을 때만 Supplement 를 부릅니다 (SupplementTick 과 재예열 점검이 같은 판정을 씀). 유휴가 막 시작됐거나 아직 모자라면 읽지 않고 false.
    public bool SupplementWhenIdle(GameSettings g, CancellationToken ct)
    {
        bool idle = !DriveHasRunningGame(g) && !_games.Any(x => Relocating(x.Name)) && _env.DiskBytesPerSecond(g) is { } bps && bps < DiskActivity.BusyBytesPerSecond;
        DateTimeOffset now = _now();
        DateTimeOffset since;
        lock (_gate)
        {
            if (!idle)
            {
                _idleSince.Remove(g.Name);
                return false;
            }

            if (!_idleSince.TryGetValue(g.Name, out since))
            {
                _idleSince[g.Name] = since = now;
            }
        }

        if (now - since < IdleNeeded)
        {
            return false;
        }

        bool done = Supplement(g, ct);
        lock (_gate)
        {
            _idleSince.Remove(g.Name); // 취소돼 남았으면 다음에 다시 IdleNeeded 를 셈
        }

        return done;
    }

    /// 남은 핫 집합 구간을 읽는 보충 예열 한 번 (한 번 끝나면 다시 하지 않음). 다른 예열이 돌고 있거나, 게임·재배치가 있거나, RAM 예산 (물리 RAM/4, 가용 RAM - 여유) 을 넘으면 읽지 않고 false.
    /// 읽는 동안 게임 시작·재배치 시작은 StreamHot 의 기존 규칙으로 멈춥니다 (멈추면 남겨 두고 다음 유휴 때 다시).
    internal bool Supplement(GameSettings game, CancellationToken ct)
    {
        (long WarmedMib, long HotMib) p;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out p))
            {
                return false;
            }
        }

        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            if (ct.IsCancellationRequested || DriveHasRunningGame(game) || _games.Any(x => Relocating(x.Name)) || Deferred(game))
            {
                return false;
            }

            long avail = _env.AvailableRamMib();
            long physCap = _env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue;
            if (p.HotMib > physCap || p.HotMib + game.Prewarm.MinFreeRamMib > avail)
            {
                LogSkipped(game.Name, "supplement memory budget", $"{game.Name}: supplement prewarm skipped (hot set {p.HotMib} MiB exceeds the budget: min(1/4 of RAM, available {avail} MiB - reserve {game.Prewarm.MinFreeRamMib} MiB))");
                return false;
            }

            _log.Info($"{game.Name}: supplement prewarm of the rest of the hot set ({p.WarmedMib} of {p.HotMib} MiB read)");
            (StreamStats Stats, bool Complete)? hot;
            try
            {
                hot = StreamHot(game, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Warn($"{game.Name}: supplement prewarm failed: {e.GetType().Name}");
                lock (_gate)
                {
                    _partial.Remove(game.Name); // 실패한 읽기는 되풀이하지 않음 (유휴마다 같은 실패를 반복하지 않게)
                }

                _log.Warn($"{game.Name}: supplement given up after the failure");
                return false;
            }

            if (hot is not { } r)
            {
                LogSkipped(game.Name, RelocationRunning, $"{game.Name}: supplement prewarm skipped ({RelocationRunning})");
                return false;
            }

            if (!r.Complete)
            {
                _log.Info($"{game.Name}: supplement prewarm cancelled after {r.Stats.Bytes >> 20} MiB");
                return false;
            }

            lock (_gate)
            {
                _partial.Remove(game.Name);
            }

            PatchStatus(game.Name, p.HotMib, p.HotMib);
            _log.Info($"{game.Name}: supplement prewarm done: {r.Stats.Bytes >> 20} MiB requested in {r.Stats.Seconds:F2} s");
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    /// 서비스 시작 예열 대상이 되는 마지막 플레이의 최대 경과.
    public static readonly TimeSpan StartupPlayedWithin = TimeSpan.FromDays(14);

    /// 서비스 시작 예열을 시작해도 되는 시점인가 (부팅 BootDelay 경과). 자동 모드의 기록 없는 예열 (cold start) 도 같은 조건을 봅니다.
    public bool BootSettled() => _env.UptimeMs() >= BootDelay.TotalMilliseconds;

    /// 부팅 뒤 경과 시간 (ms, PrewarmEnv.UptimeMs). 부팅 조용 구간 판정이 같은 시계를 씁니다.
    public long UptimeMs() => _env.UptimeMs();

    // 부팅 5 분 경과 + 게임 드라이브 60 초 유휴를 기다립니다. 취소되면 false. 대상 드라이브 중 하나라도 처리량을 못 읽으면 (null) 유휴를 확인할 수 없으므로 읽지 않고 보류합니다.
    private bool WaitStartupGate(IReadOnlyList<GameSettings> games, CancellationToken ct)
    {
        TimeSpan idle = TimeSpan.Zero;
        bool logged = false;
        bool heldLogged = false;
        while (!ct.IsCancellationRequested)
        {
            if (RelocationHeld())
            {
                idle = TimeSpan.Zero; // 재배치가 끝난 뒤 유휴를 처음부터 다시 셉니다
                _env.Sleep(GateStep, ct);
                continue;
            }

            double?[] rates = [.. games.Select(g => _env.DiskBytesPerSecond(g))];
            bool settled = BootSettled();
            if (settled && rates.Any(r => r is null))
            {
                // 디스크 유휴를 확인할 수 없음: 읽지 않고 계측값이 생길 때까지 (또는 취소될 때까지) 보류
                idle = TimeSpan.Zero;
                if (!heldLogged)
                {
                    heldLogged = true;
                    _log.Info("service start prewarm held: disk activity cannot be measured");
                }

                _env.Sleep(GateStep, ct);
                continue;
            }

            bool busy = !settled || rates.Any(r => r >= DiskActivity.BusyBytesPerSecond);
            idle = busy ? TimeSpan.Zero : idle + GateStep;
            if (idle >= IdleNeeded)
            {
                return true;
            }

            if (!logged)
            {
                logged = true;
                _log.Info($"service start prewarm waits for {BootDelay.TotalMinutes:F0} min after boot and {IdleNeeded.TotalSeconds:F0} s of idle disk");
            }

            _env.Sleep(GateStep, ct);
        }

        return false;
    }

    // 핫 집합 몇 곳을 읽어 이미 파일 캐시에 있으면 (CacheProbe) 다시 읽지 않습니다. 표본을 못 읽으면 (null) 예열합니다.
    internal bool AlreadyWarm(GameSettings g)
    {
        try
        {
            ProbeResult? p = ColdSamples(g) is { } samples ? _env.ProbeSamples(samples) : _env.ProbeReadMs(g);
            return p is { Samples: > 0, Cold: false };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return false;
        }
    }

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로. narrow 가 있으면 그 목록에서 예열할 게임만 고릅니다.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start, Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? narrow = null, CancellationToken ct = default)
    {
        if (DuplicateTrigger(start.ExeName))
        {
            return [];
        }

        IReadOnlyList<GameSettings> hit = _games.Where(g => IsTrigger(g, start.ExeName)).ToList();
        return RunBatch(narrow?.Invoke(hit) ?? hit, $"trigger {start.ExeName}", pin: !LauncherExes.Is(start.ExeName), ct: ct); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
    }

    // 같은 exe 의 트리거는 SkipLogEvery 안에 한 번만 처리하고 나머지는 세어 두었다가, 다음에 처리하는 때 한 줄로 남깁니다.
    private bool DuplicateTrigger(string exe)
    {
        int suppressed;
        lock (_gate)
        {
            DateTimeOffset now = _now();
            if (_triggerSeen.TryGetValue(exe, out (DateTimeOffset At, int Count) last) && now - last.At < SkipLogEvery)
            {
                _triggerSeen[exe] = last with { Count = last.Count + 1 };
                return true;
            }

            suppressed = last.Count;
            _triggerSeen[exe] = (now, 0);
        }

        if (suppressed > 0)
        {
            _log.Info($"{exe}: trigger handled again ({suppressed} duplicate triggers suppressed)");
        }

        return false;
    }

    /// 주어진 게임을 차례로 (한 게임의 실패가 나머지를 막지 않음).
    public IReadOnlyList<PrewarmResult> RunAll(IEnumerable<GameSettings> games, string why) => RunBatch(games, why);

    /// 게임 설치 폴더 (origin_root) 아래의 지정한 파일들을 순서대로 파일 캐시로 읽습니다 (챔피언 선택 선읽기 같은 임의 목록). 읽은 바이트를 돌려주고, 읽지 않았으면 0.
    /// 경로는 정규화한 뒤 origin_root 아래의 것만 (".."·다른 드라이브·루트 밖 링크는 건너뜀), 이미 파일 캐시에 있는 파일은 예산을 쓰지 않고 건너뜁니다.
    /// 예산은 budgetBytes 와 공유 RAM 예산 (물리 RAM/4, 가용 RAM - 여유 - 이미 올린 양) 중 작은 쪽. 다른 예열이 돌고 있거나 같은 디스크에서 재배치가 시작했으면 읽지 않습니다. ct 로 멈춥니다 (예외를 내지 않음).
    public long WarmFiles(GameSettings g, IReadOnlyList<string> paths, long budgetBytes, CancellationToken ct)
    {
        if (budgetBytes <= 0 || paths.Count == 0 || ct.IsCancellationRequested)
        {
            return 0;
        }

        if (!PlanGuards.Inside(g.OriginRoot, g.AllowedRoots, RealPaths.Real))
        {
            LogSkipped(g.Name, "outside allowed roots", $"{g.Name}: warm files skipped (origin_root resolves outside allowed roots)");
            return 0;
        }

        if (DriveHasRunningGame(g))
        {
            LogSkipped(g.Name, "game running", $"{g.Name}: warm files skipped (a game is running on this drive)");
            return 0;
        }

        var ops = new List<ReadOp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            if (FileWarm.Allowed(g.OriginRoot, path, RealPaths.Real) is not { } full || !seen.Add(full))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(full);
                ReadOp op = new(full, 0, file.Exists ? file.Length : 0);
                if (op.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    ops.Add(op);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 열 수 없는 파일은 건너뜀
            }
        }

        if (ops.Count == 0)
        {
            return 0;
        }

        if (!Monitor.TryEnter(_run))
        {
            LogSkipped(g.Name, "another prewarm", $"{g.Name}: warm files skipped (another prewarm is running)");
            return 0;
        }

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); // 같은 디스크 게임 시작 (NoteGameStart) 이 끊을 수 있게 호출자 ct 와 연결
            if (!TryMarkReading(g.Name, () => (_coldReading, _warmStop) = (g.Name, stop)))
            {
                LogSkipped(g.Name, RelocationRunning, $"{g.Name}: warm files skipped ({RelocationRunning})");
                return 0;
            }

            try
            {
                // 표본 읽기도 읽기 표시 안에서 (재배치가 시작했으면 위에서 이미 막힘). 표시를 세운 뒤 게임이 떴으면 NoteGameStart 와 겹쳐도 놓치지 않게 다시 봄.
                if (DriveHasRunningGame(g))
                {
                    LogSkipped(g.Name, "game running", $"{g.Name}: warm files skipped (a game is running on this drive)");
                    return 0;
                }

                ops = [.. ops.Where(op => !FileCached(op, stop.Token))];
                long held = ColdMib(null);
                long physMib = _env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue;
                long ramMib = Math.Min(_env.AvailableRamMib() - g.Prewarm.MinFreeRamMib - held, physMib - held);
                long budget = Math.Min(budgetBytes, Math.Max(0, ramMib) << 20);
                ops = PrefetchStreamer.Head(ops, budget);
                if (ops.Count == 0 || stop.IsCancellationRequested)
                {
                    return 0;
                }

                (StreamStats s, bool complete) = _env.FileRead(ops, stop.Token);
                _log.Info($"{g.Name}: warm files {(complete ? "done" : "cancelled")}: {s.Bytes >> 20} MiB of {ops.Sum(o => o.Length) >> 20} MiB ({ops.Count} files) in {s.Seconds:F2} s, {s.Errors} errors");
                return s.Bytes;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                _log.Warn($"{g.Name}: warm files stopped: {e.GetType().Name}");
                return 0;
            }
            finally
            {
                (_coldReading, _warmStop) = (null, null);
            }
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 파일 한 개의 표본 (큰 파일은 조각마다 고르게) 이 모두 파일 캐시에 있으면 true. 표본을 못 읽거나 취소되면 false.
    private bool FileCached(ReadOp op, CancellationToken ct)
    {
        try
        {
            return _env.ProbeFileSamples(CacheProbe.PickSamples(FileWarm.Pieces(op)), ct) is { Samples: > 0, Cold: false };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// 같은 게임·같은 사유의 "prewarm skipped" 를 남기는 최소 간격 (트리거 프로그램이 연달아 떠도 줄이 쌓이지 않게).
    public static readonly TimeSpan SkipLogEvery = TimeSpan.FromSeconds(60);

    // 간격 안에 같은 줄이 또 나오면 남기지 않고 세어 두었다가, 다음에 남기는 줄에 "(suppressed N)" 로 붙입니다.
    private void LogSkipped(string game, string reason, string line)
    {
        int suppressed = 0;
        lock (_gate)
        {
            string key = game + "|" + reason;
            DateTimeOffset now = _now();
            if (_skipLogged.TryGetValue(key, out (DateTimeOffset At, int Count) last))
            {
                if (now - last.At < SkipLogEvery)
                {
                    _skipLogged[key] = last with { Count = last.Count + 1 };
                    return;
                }

                suppressed = last.Count;
            }

            _skipLogged[key] = (now, 0);
        }

        _log.Info(suppressed > 0 ? $"{line} (suppressed {suppressed})" : line);
    }

    // 한 묶음의 예산 = min(물리 RAM / 4, 가용 RAM - 여유). 존 크기 합이 예산을 넘으면 그 뒤 게임은 건너뜁니다 (앞쪽 = 최근 순서가 우선).
    // 서비스 시작·런처 시작이 같은 규칙입니다. 첫 게임의 가용 RAM 쪽 판단은 Run 의 자체 판단에 맡깁니다.
    // yieldToRelocation: 게임 사이마다 재배치가 시작됐는지 다시 보고, 돌고 있으면 끝날 때까지 기다린 뒤 이어서 읽습니다 (서비스 시작 예열: 게이트를 지난 직후나 예열 도중 시작된 재배치와 겹치지 않게).
    private IReadOnlyList<PrewarmResult> RunBatch(IEnumerable<GameSettings> games, string why, bool pin = true, CancellationToken ct = default, bool yieldToRelocation = false)
    {
        var results = new List<PrewarmResult>();
        IReadOnlyList<GameSettings> list = games as IReadOnlyList<GameSettings> ?? [.. games];
        WarmHeads(list, ct, yieldToRelocation);
        long avail = _env.AvailableRamMib();
        long physCap = _env.PhysicalRamBytes() is > 0 and long phys ? phys >> 22 : long.MaxValue; // MiB, 물리 RAM 의 1/4
        long used = 0; // 핫 집합 예열의 MiB (기록 없는 예열은 _coldMib 가 셈)
        bool cut = false;
        foreach (GameSettings g in list)
        {
            if (yieldToRelocation && !WaitOutRelocation(g, ct))
            {
                results.Add(new PrewarmResult(g.Name, false, Cancelled, null, _now())); // 기다리는 동안 취소됨 (읽지 않았으므로 기록하지 않음)
                continue;
            }

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
                LogSkipped(g.Name, "batch memory budget", $"{g.Name}: prewarm skipped (batch zones {held + zoneMib} MiB exceed the budget: min(1/4 of RAM, available {avail} MiB - reserve {g.Prewarm.MinFreeRamMib} MiB)) [{why}]");
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
    private void WarmHeads(IReadOnlyList<GameSettings> games, CancellationToken ct, bool yieldToRelocation)
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
            if (ct.IsCancellationRequested || (yieldToRelocation && !WaitOutRelocation(g, ct)))
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
        if (!TryMarkReading(game.Name, () =>
        {
            (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
            _yield = null;
        }))
        {
            return; // 같은 디스크에서 재배치가 시작함: 앞부분은 읽지 않음 (전체 예열 Run 이 같은 이유로 건너뜀을 남김)
        }

        try
        {
            using CancellationTokenRegistration reg = ct.Register(() => // 표시를 세운 뒤의 모든 예외에서 finally 가 표시를 내리도록 try 안
            {
                if (!_keepStream)
                {
                    stop.Cancel();
                }
            });
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

    // 미플레이 게임은 예산에도 표본 읽기에도 넣지 않습니다 (플레이한 게임이 우선, 미플레이 표본을 HDD 에서 읽지 않음).
    // 기록 없이 예열한 게임들이 지금도 RAM 에 있다고 보는 양 (MiB): 기록한 양이되, 표본을 다시 읽어 식었으면 (CacheProbe) 0 으로 바꿉니다. 영구히 예산을 막지 않게.
    // 표본을 읽을 수 없으면 (null) 기록한 양을 그대로 씁니다. 표본 읽기는 잠금 밖에서.
    private long ColdMib(string? except)
    {
        List<(string Name, IReadOnlyList<(string Path, long Offset, int Bytes)>? Samples)> held;
        lock (_gate)
        {
            held = [.. _coldMib.Where(kv => kv.Value > 0 && HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Select(kv => (kv.Key, _coldSamples.GetValueOrDefault(kv.Key)))];
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
            return _coldMib.Where(kv => HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
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
                    if (StreamHot(game, ct) is not { } hot)
                    {
                        LogSkipped(game.Name, RelocationRunning, $"{game.Name}: prewarm skipped ({RelocationRunning}) [{why}]");
                        return Record(game, now, new PrewarmResult(game.Name, false, RelocationRunning, null, now), 0);
                    }

                    (StreamStats s, bool complete) = hot;
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
                        if (_games.FirstOrDefault(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase))?.MaxStreamMib == game.MaxStreamMib)
                        {
                            _partial.Remove(game.Name); // 설정 상한 그대로 읽었으면 남은 구간이 없음 (서비스 시작 상한으로 줄인 사본은 다름)
                        }
                    }

                    double mbps = s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0;
                    _log.Info($"{game.Name}: prewarm done ({why}): {s.Bytes >> 20} MiB requested in {s.Seconds:F2} s = {mbps:F0} hint MB/s, {s.Requests} requests, {s.Errors} errors");
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

            LogSkipped(game.Name, skip, $"{game.Name}: prewarm skipped ({skip}) [{why}]");
            return Record(game, now, new PrewarmResult(game.Name, false, skip, null, now), 0);
        }
    }

    // 핫 집합 스트림 한 번. 게임이 시작되면 (NoteGameStart) 다음 묶음 경계에서 멈춥니다. 단 keep_stream_on_game_start 가 켜졌고 그 게임의 스트림이면 ct 가 취소돼도 끝까지 읽습니다.
    // 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    private (StreamStats Stats, bool Complete)? StreamHot(GameSettings game, CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        if (!TryMarkReading(game.Name, () =>
        {
            (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
            _yield = null;
        }))
        {
            return null;
        }

        DateTimeOffset? last = null;
        bool complete = false;
        try
        {
            using CancellationTokenRegistration reg = ct.Register(() => // 표시를 세운 뒤의 모든 예외에서 finally 가 표시를 내리도록 try 안
            {
                if (!_keepStream)
                {
                    stop.Cancel();
                }
            });
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
        long fullMib = !a.FullPrewarm ? 0 : Math.Min(a.FullPrewarmMaxMib > 0 ? a.FullPrewarmMaxMib : DefaultFullPrewarmMib, availBudget);
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
                string key = Directory.GetLastWriteTimeUtc(game.OriginRoot).Ticks.ToString(); // shortcut: 설치 폴더 자신의 mtime 만 봄 (하위 변경은 놓칠 수 있음), 폴더 mtime 이 바뀌면 다시 열거
                ColdPlan? plan = ProfileMissed(game.Name, key) ? null : _env.ColdPlanner!(game, Math.Max(0, fullMib) << 20, Math.Max(0, profileMib) << 20, ct);
                if (plan is null)
                {
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip == FolderWarm.NoEngineProfile)
                {
                    RememberProfileMiss(game.Name, key);
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip is not null)
                {
                    skip = plan.Skip;
                }
                else
                {
                    ForgetProfileMiss(game.Name);
                    _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                    if (ColdReadMarked(game, plan.Ops, ct) is not { } cold)
                    {
                        skip = RelocationRunning;
                    }
                    else
                    {
                        (stats, label, skip) = (cold.Stats, plan.Label, cold.Complete ? null : Cancelled);
                        samples = CacheProbe.PickSamples(plan.Ops);
                    }
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
        _log.Info($"{game.Name}: prewarm done ({why}, {label}): {stats.Bytes >> 20} MiB requested in {stats.Seconds:F2} s = {mbps:F0} hint MB/s, {stats.Requests} requests, {stats.Errors} errors");
        PrewarmResult done = Record(game, now, new PrewarmResult(game.Name, true, null, stats, now) { Mode = label }, mbps);
        WarmMeasured?.Invoke(game, mbps);
        ColdWarmed?.Invoke(game, label!);
        return done;
    }

    // 기록 없는 읽기 한 번: 읽는 동안 _coldReading 표시를 세우고 (예외·취소로 끝나도 내림), 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    private (StreamStats Stats, bool Complete)? ColdReadMarked(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken ct)
    {
        if (!TryMarkReading(game.Name, () => _coldReading = game.Name))
        {
            return null;
        }

        try
        {
            return _env.ColdRead(ops, ct);
        }
        finally
        {
            _coldReading = null;
        }
    }

    private bool ProfileMissed(string game, string key)
    {
        lock (_gate)
        {
            return _profileMiss.TryGetValue(game, out string? known) && known == key;
        }
    }

    private void RememberProfileMiss(string game, string key)
    {
        lock (_gate)
        {
            _profileMiss[game] = key;
        }
    }

    private void ForgetProfileMiss(string game)
    {
        lock (_gate)
        {
            _profileMiss.Remove(game);
        }
    }

    // 안티치트 게임은 기록 (트레이스) 을 만들지 않으므로 "trace needed" 라고 하지 않습니다.
    private string ProfileSkip(GameSettings game) => AntiCheat.ForbidsTrace(game.AntiCheat) || AntiCheat.ForbidsTrace(_env.AntiCheatOf(game)) ? "no profile" : "no profile; trace needed";

    private PrewarmResult Record(GameSettings game, DateTimeOffset now, PrewarmResult r, double mbps)
    {
        string? okAt = r.Ran ? now.ToString("O") : KeptOkAt(game.Name);
        _status[game.Name] = new JObject
        {
            { "at", now.ToString("O") },
            { "result", r.Ran ? "ok" : r.SkipReason!.StartsWith("failed", StringComparison.Ordinal) ? "failed" : "skipped" },
            { "reason", r.SkipReason },
            { "bytes", r.Stats?.Bytes },
            { "hint_accepted_bytes", r.Stats?.HintAcceptedBytes },
            { "seconds", r.Stats?.Seconds },
            { "hint_mb_per_s", r.Stats is null ? null : Math.Round(mbps, 1) },
            { "last_ok_at", okAt },
        };
        lock (_gate)
        {
            if (_profileMiss.TryGetValue(game.Name, out string? miss))
            {
                ((JObject)_status[game.Name]!)["profile_miss_key"] = miss;
            }
        }
        if (r.Ran && r.Mode is null && _streamer.LoadHeadBytes(game) is { } head)
        {
            ((JObject)_status[game.Name]!)["head_mib"] = head >> 20; // 로딩 앞부분 (관리 창 상태 탭)
        }

        if (r.Mode is not null)
        {
            ((JObject)_status[game.Name]!)["mode"] = r.Mode;
        }

        lock (_gate)
        {
            if (r.Ran && r.Mode is null && _startupTrim.TryGetValue(game.Name, out (long WarmedMib, long HotMib) trim) && game.MaxStreamMib == trim.WarmedMib)
            {
                ((JObject)_status[game.Name]!)["warmed_mib"] = trim.WarmedMib; // 서비스 시작 상한으로 앞부분만 읽음 (보충 예열이 끝나면 둘이 같아짐)
                ((JObject)_status[game.Name]!)["hot_mib"] = trim.HotMib;
            }
        }

        FlushStatus();
        return r;
    }

    private void FlushStatus()
    {
        if (_statusPath is not null)
        {
            try
            {
                string tmp = _statusPath + ".tmp";
                DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(new JObject { { "games", _status } }, indent: 2, ensureAscii: false) + "\n"));
                File.Move(tmp, _statusPath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("prewarm status file not written: " + e.GetType().Name);
            }
        }
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
