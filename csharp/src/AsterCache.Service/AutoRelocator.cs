using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 자동 계획·첫 재배치. 30 초 재적용 틱 안에서 (같은 스레드라 이동이 겹치지 않음) 원시 트레이스가 있고 아직 재배치하지 않은 게임을
/// 최근에 한 순서로 하나씩: 계획 (Tray 의 계획 만들기와 같은 2 단계) → 유휴·일일 상한 확인 → 존 시작 정하기 → 이동 → 확인.
/// 안전장치는 재적용과 같습니다: 저널, SHA 표본 검증, 게임 시작 때 취소 (ReapplyGate), 볼륨 레이블 확인, 허용 루트 검사.
/// 예열은 틱 스레드에서 돌리지 않고 따로 (한 번에 하나) 돌립니다: 틱은 예열을 요청만 하고 바로 돌아오며, 게임이 뜨면 CancelWarm 이 그 작업을 취소합니다.
public sealed partial class AutoRelocator
{
    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);
    public const string RelocateOff = "auto relocate off";
    public const string DailyLimit = "daily limit reached";
    public const string DailyBudget = "daily write budget reached";
    public const string DriveRecovering = "drive recovering after relocation";
    public const string StartupPrewarmBusy = "startup prewarm in progress";

    /// 예열이 끝났다는 표시 (auto_status 의 detail). 관리 창이 "예열됨" 으로 보입니다.
    public const string Prewarmed = "prewarmed";

    /// 기록 없이 예열한 게임의 표시: "prewarmed (full)" (폴더 통째) 또는 "prewarmed (profile Unity)" (엔진 프로필).
    public static string PrewarmedBy(string label) => $"{Prewarmed} ({label})";

    private const string ColdWhy = "cold start";

    /// PDH 2초 갱신 주기와 1초 표본 구간의 위상을 지나 자기 읽기가 빠진 표본을 받습니다.
    private static readonly TimeSpan BaselineBusyObservation = TimeSpan.FromMilliseconds(3_100);
    private static readonly TimeSpan BaselineBusyPoll = TimeSpan.FromMilliseconds(100);

    /// boot 트레이스 (가져온 프로필) 로 만든 계획의 stats.source, 그 계획으로 재배치를 마친 게임의 detail 표시.
    public const string PlanSourceBoot = "boot";
    public const string RelocatedProfile = "relocated (profile)";

    /// 프리페치 기록 (.pf) 으로 만든 boot 트레이스 (부트스트랩 S2) 로 만든 계획의 stats.source, 그 계획으로 재배치를 마친 게임의 detail 표시.
    public const string PlanSourcePrefetch = PrefetchFiles.BootSource;
    public const string RelocatedPrefetch = "relocated (prefetch)";

    /// 프리페치 계획은 트레이스가 참조한 범위의 앞뒤로 이만큼을 더해 존에 모읍니다 (파일 크기 상한 없음).
    public const long PrefetchPadBytes = 64L << 20;
    private const string FirstRunDetail = "first run recorded, planning again";
    private const string BusyDetail = "target region busy, planning again";

    /// 존 확장 재계획 (병합한 트레이스가 핫 집합을 키웠을 때): 이전 계획을 옆으로 치운 파일 (이 파일이 있는 동안이 확장 중), 그동안의 detail 표시. ReplanDetail 접두는 쓰지 않음 (업데이트 지연 판정과 섞이지 않게).
    public const string ExtendSuffix = ".extend";
    private const string EffectiveZoneKey = "effective_zone_mib"; // 계획 stats: 계획을 만들 때의 유효 존 상한 (MiB)
    internal const string PlannedAtKey = "planned_at"; // 계획 stats: 계획을 만든 시각 (UTC 유닉스 초). 다시 저장하거나 Touch 해도 그대로: 업데이트 뒤 지문 없는 파일의 변경 판정 기준
    private const string MergePolicyKey = "merge_policy"; // 계획 stats: 계획을 만들 때의 트레이스 병합 정책 (AutoTracer.MergePolicy)
    private const string FingerprintKey = "trace_fingerprint"; // 계획 stats: 계획을 만들 때의 원시 트레이스 지문 (블록 집합·순서)
    private const string StableFingerprintKey = "relocation_trace_fingerprint";
    private const string ExtendDetail = "trace grew, extending zone";
    private const string SupplementKey = "profile_supplement_mib"; // 계획 stats: 트레이스 금지 게임의 계획에 엔진 프로필에서 보탠 양 (MiB)
    private const string SupplementCheckedKey = "profile_supplement_checked"; // 계획 stats: 프로필 보충을 이미 따져 본 계획 (보충이 없거나 작았음)
    private const string SupplementReplanKey = "profile_supplement_replan"; // 이전 계획 (.extend) 의 stats: 프로필 보충 재계획 중 (뒤 공간이 쓰이면 새 존 대신 보류)

    /// 읽을 수 없는 (잘린) 계획을 옆으로 치운 파일의 접미사.
    public const string BadSuffix = ".bad";

    /// 업데이트 뒤 재계획 검사 간격: 배치까지 끝낸 게임마다 이 주기로만 계획의 지문을 봅니다 (ReapplyChecker 의 주기보다 짧게: 게임을 다시 켜기 전에 잡으려고).
    public static readonly TimeSpan StaleCheckEvery = TimeSpan.FromMinutes(10);

    public const string StaleSuffix = ".stale";

    /// 이번 업데이트의 원본 보관 이름: 이전 업데이트의 .stale 이 이미 있어 그것을 덮지 않고 따로 둘 때 (.stale.partial 은 정리가 실패한 뒤의 정리본이라 의미가 다름).
    public const string UpdateSuffix = ".stale.update";

    /// 지문 검사의 대표 파일: 게임 exe 와 계획에서 가장 큰 파일 몇 개. 이 중 하나라도 바뀌었을 때만 전체 지문을 비교합니다.
    private const int LargestSampled = 3;
    internal const string ReplanDetail = "game updated, will trace again on next run";

    /// 트레이스 금지 게임의 업데이트: 트레이스를 유지한 채 다시 계획하는 동안의 detail (ReplanDetail 접두라 업데이트 뒤 지연 규칙도 같이 지킴).
    private const string KeptDetail = ReplanDetail + "; trace kept, planning again";

    /// 일부 파일만 바뀐 업데이트: 바뀐 파일의 범위만 빼고 나머지 학습으로 다시 계획하는 동안의 detail (ReplanDetail 접두라 업데이트 뒤 지연 규칙도 같이 지킴).
    private const string PartialSuffix = ".partial";

    private const string UnchangedKeptDetail = ReplanDetail + "; unchanged files kept, planning again";

    /// 바뀐 파일이 계획 핫 바이트의 이 비율 이상이면 일부만 빼지 않고 전체를 치웁니다 (절반 이상이 바뀐 큰 패치는 남은 학습이 새 버전을 대표하지 못해 재학습이 맞음).
    internal const double PartialMaxChangedShare = 0.5;

    private const string BootPlanSuffix = ".boot";

    /// 새 계획이 실패해 낡은 계획을 되살린 게임의 detail 표시 (done 상태는 유지: 예열·스트림은 계속).
    public const string StalePlanDetail = "stale plan";
    private static readonly Regex PrewarmedTag = new(@"(, )?prewarmed( \([^)]*\))?", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex ProgressTag = new(@"(, )?prewarming \d+/\d+ MiB", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TraceTag = new(@"(, )?trace (\d+/\d+( \(coverage \d+%\))?|unavailable \([^)]*\), \d+ earlier run\(s\) used)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TierTag = new(@"(, )?ssd tier [\d.]+ GB", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly BlockerNotes _blockers; // 게임 실행 중이라 건너뛸 때 막고 있는 프로세스를 남김 (사유가 바뀔 때만)
    private readonly IdleTracker _idle = new();
    private DateTimeOffset? _waitSince; // 틱 스레드에서만 씀: 유휴가 아닌 채로 기다리기 시작한 때
    private bool _waitNoted; // 틱 스레드에서만 씀: 이번 대기 구간에 긴 대기를 이미 남겼는지
    private bool _prewarmHeld; // 틱 스레드에서만 씀: 예열 스트림 때문에 재배치를 미루는 중 (로그는 시작 때 한 번)
    private bool _hoursHeld; // 틱 스레드에서만 씀: auto.relocate_hours 시간대 밖이라 새 재배치를 미루는 중 (로그는 상태가 바뀔 때만)
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(5); // 기록 없는 예열이 "drive busy" 로 밀렸을 때 다시 시도하는 간격 (틱마다 30 초가 아님)
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(6); // 프리페치·콜드 예열 재시도 간격 (발견 주기와 별개)
    private static readonly TimeSpan LongWait = TimeSpan.FromHours(6);
    private readonly Prewarmer? _prewarm;
    private readonly DriveRecovery _recovery;
    private DiskReadCoordinator? _coordinator;
    private readonly AutoTier? _tier; // SSD 보조 저장 (선택 기능): 환경이 엔진을 주지 않으면 (시험) 없음
    private GameExitTracker? _exitTracker;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coldTried = new(StringComparer.Ordinal); // 이번 서비스 실행 중 기록 없는 예열을 요청한 게임과 그 시각 (영구 건너뜀은 발견 주기마다 다시 시도)
    private (bool, int, bool) _coldKey; // 틱 스레드에서만 씀: 설정이 바뀌면 _coldTried 를 비움
    private readonly Dictionary<string, DateTimeOffset> _staleChecked = new(StringComparer.Ordinal); // 틱 스레드에서만 씀
    private readonly HashSet<string> _delayLogged = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 업데이트 지연을 이미 로그한 게임
    private readonly Dictionary<string, DateTimeOffset> _prefetchTried = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 프리페치 기록으로 boot 를 만들어 본 게임과 그 시각
    private readonly HashSet<string> _supplementChecked = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 프로필 보충 재계획을 이미 따져 본 게임 (서비스 실행당 한 번)
    private readonly HashSet<string> _volatileChecked = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 옛 계획의 가변 파일을 이미 따져 본 게임 (서비스 실행당 한 번)
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 이전 존 시작에 맞춰 계획을 만든 게임
    private readonly ConcurrentDictionary<string, byte> _smrBudgetLogged = new(StringComparer.Ordinal); // 명시한 하루 쓰기 예산이 SMR 기본값보다 큰 게임 (한 번만 로그)
    private readonly Dictionary<string, (bool Auto, int Mib)> _limitSeen = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 존 상한 설정 (auto 여부·값) 을 이미 따져 본 게임과 그때의 설정 (설정이 바뀌거나 서비스를 다시 시작하기 전에는 계획을 다시 읽지 않음)
    private readonly HashSet<string> _warmRestorePending = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 잔여 재계획 뒤 예열 목록 복원에 실패한 게임 (복원에 성공할 때까지 존 확장 점검 앞에서 다시 시도)
    private readonly Dictionary<string, DateTimeOffset> _extTried = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 존 확장을 이미 따져 본 (또는 막힌) 게임과 그때의 트레이스 수정 시각 (다음 플레이가 쌓여 트레이스가 바뀔 때까지 다시 보지 않음)
    private readonly ConcurrentDictionary<string, byte> _extensionRecovery = new(StringComparer.Ordinal); // 확장 되돌리기·저널 보관에 실패했다가 다시 열려 그 복구부터 해야 하는 게임 (서비스를 다시 시작하면 사라짐)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _planFailedAt = new(StringComparer.Ordinal); // 계획 (또는 재배치) 이 마지막으로 실패한 시각: 그 뒤 트레이스가 새로 쓰이기 전에는 같은 실패를 되풀이하지 않음
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Text)> _transientHold = new(StringComparer.Ordinal); // 잠금 같은 일시 오류로 잔여 재계획을 못 한 게임과 다시 시도할 시각 (같은 서비스 실행 안에서 자동 재시도, 실패로 기록하지 않음)
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Text)> _unreadableHold = new(StringComparer.Ordinal); // 읽지 못한 파일이 계획에 보류로 남아 완료로 확정하지 못한 게임과 다시 계획할 시각
    private readonly ConcurrentDictionary<string, DateTimeOffset> _traceWarmTried = new(StringComparer.Ordinal); // 게임별로 예열을 시도한 트레이스 파일 시각
    private readonly ConcurrentDictionary<string, byte> _removedGameCaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warmLock = new(); // 아래 예열 큐 상태
    private readonly object _noteLock = new(); // 대기 표시 갱신과 예열 완료 표시가 서로의 detail 을 덮지 않게
    private readonly Queue<(GameSettings Game, string Why)> _warmQueue = new();
    private readonly ConcurrentDictionary<string, string> _progress = new(StringComparer.Ordinal); // 핫 집합을 읽는 중인 게임의 "prewarming x/y MiB" 표시, _noteLock 으로 갱신
    private CancellationTokenSource _warmCts = new();
    private Task _warmTask = Task.CompletedTask;
    private bool _warmRunning;
    private bool _recovered; // 틱 스레드에서만 씀: 중단된 재계획 복구는 서비스 실행당 첫 틱에 한 번
    private string? _warming;

    public AutoRelocator(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings, Prewarmer? prewarm = null, AutoTier? tier = null)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
        _blockers = new BlockerNotes(env, log);
        _prewarm = prewarm;
        _recovery = new DriveRecovery(env, log, status); // 재시작 때 저장된 회복 추적을 되살림
        _tier = tier ?? (env.Tier is { } engine ? new AutoTier(env, status, log, engine) : null);
    }

    /// 쓰기(재배치) 뒤 드라이브 회복 상태: 회복 전에는 같은 드라이브의 다음 재배치·재계획을 시작하지 않습니다 (예열은 막지 않음).
    public IDriveRecovery Recovery => _recovery;

    public void SetGameExitTracker(GameExitTracker tracker) => _exitTracker = tracker;

    public void SettingsReloaded(IEnumerable<GameSettings> removed)
    {
        foreach (GameSettings game in removed)
        {
            _removedGameCaches.TryAdd(game.Name, 0);
        }
    }

    private bool HeavyWorkAllowed(GameSettings target)
    {
        AsterSettings settings = _settings();
        return _exitTracker?.HeavyWorkAllowed(target, settings.Games, _env.SameDisk, settings.Auto.RewarmAfterExitMin, _env.Now()) ?? true;
    }

    public void SetDiskReadCoordinator(DiskReadCoordinator coordinator)
    {
        _coordinator = coordinator;
        _recovery.SetDiskReadCoordinator(coordinator);
    }
}
