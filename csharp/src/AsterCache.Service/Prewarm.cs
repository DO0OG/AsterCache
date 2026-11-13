using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 사전 예열: 서비스 시작 때와 트리거 프로그램이 뜰 때, 게임의 존 순서 스트림 (기존 IGameStreamer, pvm) 을 돌려 핫 집합을 RAM 에 올립니다.
/// 결과는 service.log 와 상태 파일 (aster status 가 읽음) 에 남깁니다. 예열 중 게임이 시작돼도 스트림은 멈추지 않습니다 (같은 데이터).
public sealed partial class Prewarmer
{
    internal static readonly TimeSpan RecentWarmupWindow = TimeSpan.FromMinutes(1);
    /// 게임 시작으로 취소돼 스트림을 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string Cancelled = "cancelled";

    /// 업데이트 직후 지연으로 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string PostUpdateDelay = "post-update delay";

    /// 같은 디스크에서 재배치가 시작했거나 도는 중이라 읽기를 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string RelocationRunning = "relocation running";

    /// 게임별 정책 (games[].mode, games[].paused) 이 예열을 막아 읽지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string GamePolicySkip = "game policy";

    // 재배치 직후 예열의 이유 (AutoRelocator 가 "auto " + "after relocation" 으로 넘김): 회복 기간 고정의 조건.
    private const string AfterRelocation = "auto after relocation";

    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly string? _statusPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly WarmEstimateCache _estimates; // 예산 계산 (WarmBudget) 의 게임별 추정 재사용, 읽을 범위를 정하는 호출은 거치지 않음
    private readonly object _gate = new(); // 짧은 메모리 상태만 보호: 스트림이 도는 동안에도 LastWarmed·Warmed 가 막히지 않게
    private readonly object _run = new(); // 예열을 한 번에 하나로
    private readonly Dictionary<string, int> _activeWork = new(StringComparer.OrdinalIgnoreCase); // 설정 재적재 때 진행 중인 게임 상태를 보존, _gate 로 보호
    private readonly HashSet<string> _removedGames = new(StringComparer.OrdinalIgnoreCase); // 진행 중 작업이 끝난 뒤 임시 상태를 정리할 게임, _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastBeforeBoot = new(StringComparer.OrdinalIgnoreCase); // 부팅 전에 성공한 예열 시각 (상태 파일), 새 트레이스 판정 (LastWarmed) 만 씀, _gate 로 보호
    private sealed record PendingWarm(DateTimeOffset RetryAt, string Reason, string? TriggerExe = null); // TriggerExe: 막던 게임이 사라져 한 번 다시 시도할 트리거 exe
    private readonly Dictionary<string, PendingWarm> _pendingWarm = new(StringComparer.OrdinalIgnoreCase); // 성공하지 못한 일시 예열과 시작 상한 누락, _gate 로 보호
    private sealed class PrewarmCancelDetail
    {
        private string? _first;
        private int _memory;

        public string? First => Volatile.Read(ref _first);

        /// 처음 남은 사유가 읽는 도중 메모리 판정이었는가 (문구가 아니라 값으로 구분).
        public bool Memory => Volatile.Read(ref _memory) != 0;

        public void Set(string reason, bool memory = false)
        {
            if (Interlocked.CompareExchange(ref _first, reason, null) is null && memory)
            {
                Volatile.Write(ref _memory, 1);
            }
        }
    }
    private readonly JObject _status = new();
    private readonly PinKeeper _pins;
    private readonly Rewarmer _rewarm;
    private GameExitTracker? _exitTracker;
    private DiskReadCoordinator? _coordinator;
    private readonly Dictionary<string, long> _coldMib = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열해 RAM 에 올려 둔 양 (MiB), _gate 로 보호
    private readonly Dictionary<string, IReadOnlyList<(string Path, long Offset, int Bytes)>> _coldSamples = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열한 파일 목록에서 고른 재예열 점검 표본, _gate 로 보호
    private readonly Dictionary<string, ResidencyProbe> _residencyProbes = new(StringComparer.OrdinalIgnoreCase); // 마지막 실제 상주 표본과 계획·trace 세대, _gate 로 보호
    private readonly Dictionary<string, string> _profileMiss = new(StringComparer.OrdinalIgnoreCase); // 엔진 프로필이 없다는 판정과 그때의 설치 폴더 mtime 키 (prewarm_status.json 의 profile_miss_key), _gate 로 보호
    private readonly HashSet<string> _refusedLogged = new(StringComparer.OrdinalIgnoreCase); // 드라이브 거부를 이미 로그한 게임, _gate 로 보호
    private readonly HashSet<string> _policyLogged = new(StringComparer.OrdinalIgnoreCase); // 게임 정책으로 예열을 멈춘 것을 이미 로그한 게임 (정책이 다시 허용되면 지움), _gate 로 보호
    private readonly Dictionary<string, bool> _delayLogged = new(StringComparer.OrdinalIgnoreCase); // 업데이트 보류를 이미 로그한 게임 (값: 재계획 보류인가, 아니면 지연): 보류 종류가 바뀌면 다시 로그, _gate 로 보호
    private readonly HashSet<string> _relocHoldLogged = new(StringComparer.OrdinalIgnoreCase); // 재배치 중이라 시작 예열을 보류한 것을 이미 로그한 게임, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _skipLogged = new(StringComparer.OrdinalIgnoreCase); // 게임|사유별 마지막으로 남긴 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private readonly Dictionary<string, (string Reason, long WarmedMib, long HotMib)> _supplementNothingNew = new(StringComparer.OrdinalIgnoreCase); // 보충할 새 구간이 없다는 마지막 상태, _gate 로 보호
    private readonly Dictionary<string, (string Reason, long CapMib, long UsedMib, long AllocMib)> _pendingWarmBudgetSkipped = new(StringComparer.OrdinalIgnoreCase); // 예산 거절의 마지막 상태, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _triggerSeen = new(StringComparer.OrdinalIgnoreCase); // 트리거 exe 별 마지막 처리 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, long Bytes)> _readSample = new(StringComparer.OrdinalIgnoreCase); // 실행 중인 게임의 마지막 읽기 카운터 표본, _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _gameIdleSince = new(StringComparer.OrdinalIgnoreCase); // 실행 중인 게임이 한가해진 시각 (읽기 증가율·디스크 처리량이 낮은 상태가 이어진 시작), _gate 로 보호
    private readonly TriggerRetries _triggerRetries = new(); // 같은 디스크의 다른 게임 때문에 거절된 트리거 예열과 그 막던 게임들
    private (string Game, DateTimeOffset At)? _triggered; // 가장 최근 트리거로 예열을 시작한 게임과 시각: 보충·재예열 예산 순위에서 먼저 받음, _gate 로 보호
    private volatile AutoSettings _auto = AutoSettings.Default;
    private volatile string? _streaming; // 핫 집합 스트림이 도는 게임
    private volatile string? _coldReading; // 기록 없는 (통째·프로필) 예열의 읽기가 도는 게임: _streaming 과 달리 게임 시작 양보 신호는 받지 않음 (ct 로만 멈춤)
    private readonly HashSet<string> _relocStarted = new(StringComparer.OrdinalIgnoreCase); // 재배치가 시작한 (아직 끝나지 않은) 게임, _gate 로 보호
    private volatile bool _keepStream; // auto.keep_stream_on_game_start 가 켜진 채 그 게임이 시작돼 (같은 데이터를 동시에 읽으므로) 취소 신호에도 끝까지 읽기로 한 표시
    private volatile bool _streamGameStarted;
    private volatile CancellationTokenSource? _streamStop; // 도는 스트림을 멈추는 신호 (게임 시작이 같은 디스크 읽기를 바로 끊음)
    private volatile PrewarmCancelDetail? _streamCancelDetail;
    private volatile CancellationTokenSource? _calibrationStop;
    private volatile CancellationTokenSource? _warmStop; // WarmFiles 읽기를 멈추는 신호 (호출자 ct 와 연결, 같은 디스크 게임 시작이 끊음), 읽는 게임은 _coldReading
    private (string Game, long At)? _yield; // 게임 시작으로 스트림 양보를 요청한 게임과 시각 (Stopwatch 틱), _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _partial = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 상한으로 핫 집합의 앞부분만 읽은 게임 (읽은 양, 전체), 보충 예열이 끝나면 지움, _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _memoryPartial = new(StringComparer.OrdinalIgnoreCase); // 메모리가 모자라 핫 집합의 앞부분만 읽은 게임 (다음에 전체가 들어가는 예열이 끝나면 지움)
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _memoryRunning = new(StringComparer.OrdinalIgnoreCase); // 지금 부분 예열을 읽는 중 (스트림이 끝나는 알림에서 "부분" 표시를 붙이려고)
    private readonly Dictionary<string, DateTimeOffset> _memoryStopAt = new(StringComparer.OrdinalIgnoreCase); // 읽는 도중 메모리 판정으로 마지막에 멈춘 시각 (다시 읽기 시작하거나 끝까지 읽으면 지움), _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _startupTrim = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 예열이 상한으로 줄여 읽는 중인 게임 (Record 가 같은 상태 쓰기에 warmed_mib/hot_mib 를 넣음), _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _idleSince = new(StringComparer.OrdinalIgnoreCase); // 보충 예열을 기다리는 게임이 한가해지기 시작한 시각, _gate 로 보호
    private readonly Dictionary<string, (long WindowMib, long Bytes)> _resume = new(StringComparer.OrdinalIgnoreCase); // 끊긴 보충 예열이 그 윈도 (MaxStreamMib 로 잘린 목록) 의 스케줄 앞쪽에서 이미 읽은 바이트: 다음 보충이 이어 읽는 위치, _gate 로 보호
    private sealed record SupplementReadProgress(long Bytes, string? Inputs, GameSettings Settings);
    private sealed record SupplementRetry(DateTimeOffset RetryAt, long CapMib, long ProgressBytes, string? Inputs, GameSettings Settings, bool PlanExhausted, bool EarlyRetryUsed, bool Logged);
    private readonly Dictionary<string, SupplementReadProgress> _supplementReadProgress = new(StringComparer.OrdinalIgnoreCase); // 부분 예열에서 마지막으로 받아들인 바이트 (MiB 반올림 전), _gate 로 보호
    private readonly Dictionary<string, SupplementRetry> _supplementRetry = new(StringComparer.OrdinalIgnoreCase); // 무진전 또는 읽을 목록 소진 뒤 재시도 기한, _gate 로 보호
    private DateTimeOffset _driveObservedAt; // 지금 도는 읽기가 멈춰서 디스크 처리량을 마지막으로 잰 (또는 읽기를 시작한) 시각, _run 아래에서만 씀
    private double _yieldedSeconds; // 핫 집합 스트림이 낮은 I/O 우선순위로 읽은 시간 (초, Run 이 예열 한 회 동안 모아 done 줄에 씀), _run 아래에서만 씀

    private sealed record ProbeFileGeneration(string Path, bool Exists, long Size, long MtimeNs);
    private sealed record ProbeGeneration(ProbeFileGeneration Plan, ProbeFileGeneration Trace, ProbeFileGeneration WarmOnly);
    private sealed record ResidencyProbe(bool Warm, DateTimeOffset At, ProbeGeneration Generation);

    private sealed class GameWorkLease(Prewarmer owner, string game) : IDisposable
    {
        private Prewarmer? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndGameWork(game);
    }

    public Prewarmer(IReadOnlyList<GameSettings> games, IGameStreamer streamer, PrewarmEnv env, ILog log, string? statusPath = null, Func<DateTimeOffset>? now = null,
        DiskReadCoordinator? coordinator = null)
    {
        _games = games;
        _streamer = streamer;
        _env = env;
        _log = log;
        _statusPath = statusPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _coordinator = coordinator;
        _estimates = new WarmEstimateCache(_now);
        _pins = new PinKeeper(env, log, _now) { Coordinator = coordinator };
        _solar = new Solar(env, log, _now);
        _rewarm = new Rewarmer(this, env, log, _now);
        LoadStatus();
    }

    public void SetDiskReadCoordinator(DiskReadCoordinator coordinator) => (_coordinator, _pins.Coordinator) = (coordinator, coordinator);

    public void SetGameExitTracker(GameExitTracker tracker)
    {
        _exitTracker = tracker;
        _rewarm.SetGameExitTracker(tracker);
    }

    private bool HeavyWorkAllowed(GameSettings game) =>
        _exitTracker?.HeavyWorkAllowed(game, _games, _env.SameDisk, _auto.RewarmAfterExitMin, _now()) ?? true;

    /// 기록 없이 (통째·프로필) 예열에 성공했을 때: 게임과 방식 이름표.
    public event Action<GameSettings, string>? ColdWarmed;

    /// 예열이 끝났을 때의 실측 MB/s (드라이브 회복 판정의 캐시 적중 상한용).
    public event Action<GameSettings, double>? WarmMeasured;

    /// 핫 집합 예열의 진행 (묶음마다, ProgressEvery 에 한 번 이하): 게임, 읽은 MiB, 총 MiB.
    public event Action<GameSettings, long, long>? Progress;

    /// 핫 집합 스트림이 끝났을 때 (취소·실패 포함): 게임, 끝까지 읽었는가.
    public event Action<GameSettings, bool>? StreamEnded;
}
