using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 유휴 판정: 모든 조건이 필요한 시간 동안 끊김 없이 참이어야 유휴입니다. 하나라도 거짓이면 처음부터 다시 잽니다.
public sealed class IdleTracker
{
    /// 이 이상 CPU 를 쓰는 중이면 다른 일을 하는 PC 로 봄 (백그라운드 작업 정도는 허용). 예열·재배치 판정에서는 쓰지 않음.
    public const double CpuBusyPercent = 20;

    private DateTimeOffset? _since;

    public bool Sample(DateTimeOffset now, bool quiet, TimeSpan needed)
    {
        if (!quiet)
        {
            _since = null;
            return false;
        }

        _since ??= now;
        return now - _since.Value >= needed;
    }

    public void Reset() => _since = null;
}

/// 자동 계획·첫 재배치. 30 초 재적용 틱 안에서 (같은 스레드라 이동이 겹치지 않음) 원시 트레이스가 있고 아직 재배치하지 않은 게임을
/// 최근에 한 순서로 하나씩: 계획 (Tray 의 계획 만들기와 같은 2 단계) → 유휴·일일 상한 확인 → 존 시작 정하기 → 이동 → 확인.
/// 안전장치는 재적용과 같습니다: 저널, SHA 표본 검증, 게임 시작 때 취소 (ReapplyGate), 볼륨 레이블 확인, 허용 루트 검사.
/// 예열은 틱 스레드에서 돌리지 않고 따로 (한 번에 하나) 돌립니다: 틱은 예열을 요청만 하고 바로 돌아오며, 게임이 뜨면 CancelWarm 이 그 작업을 취소합니다.
public sealed class AutoRelocator
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
    private const string MergePolicyKey = "merge_policy"; // 계획 stats: 계획을 만들 때의 트레이스 병합 정책 (AutoTracer.MergePolicy)
    private const string FingerprintKey = "trace_fingerprint"; // 계획 stats: 계획을 만들 때의 원시 트레이스 지문 (블록 집합·순서)
    private const string ExtendDetail = "trace grew, extending zone";
    private const string SupplementKey = "profile_supplement_mib"; // 계획 stats: 트레이스 금지 게임의 계획에 엔진 프로필에서 보탠 양 (MiB)
    private const string SupplementCheckedKey = "profile_supplement_checked"; // 계획 stats: 프로필 보충을 이미 따져 본 계획 (보충이 없거나 작았음)
    private const string SupplementReplanKey = "profile_supplement_replan"; // 이전 계획 (.extend) 의 stats: 프로필 보충 재계획 중 (뒤 공간이 쓰이면 새 존 대신 보류)

    /// 읽을 수 없는 (잘린) 계획을 옆으로 치운 파일의 접미사.
    public const string BadSuffix = ".bad";

    /// 업데이트 뒤 재계획 검사 간격: 배치까지 끝낸 게임마다 이 주기로만 계획의 지문을 봅니다 (ReapplyChecker 의 주기보다 짧게: 게임을 다시 켜기 전에 잡으려고).
    public static readonly TimeSpan StaleCheckEvery = TimeSpan.FromMinutes(10);

    public const string StaleSuffix = ".stale";

    /// 지문 검사의 대표 파일: 게임 exe 와 계획에서 가장 큰 파일 몇 개. 이 중 하나라도 바뀌었을 때만 전체 지문을 비교합니다.
    private const int LargestSampled = 3;
    private const string ReplanDetail = "game updated, will trace again on next run";

    /// 트레이스 금지 게임의 업데이트: 트레이스를 유지한 채 다시 계획하는 동안의 detail (ReplanDetail 접두라 업데이트 뒤 지연 규칙도 같이 지킴).
    private const string KeptDetail = ReplanDetail + "; trace kept, planning again";
    private const string BootPlanSuffix = ".boot";

    /// 새 계획이 실패해 낡은 계획을 되살린 게임의 detail 표시 (done 상태는 유지: 예열·스트림은 계속).
    public const string StalePlanDetail = "stale plan";
    private static readonly Regex PrewarmedTag = new(@"(, )?prewarmed( \([^)]*\))?", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex ProgressTag = new(@"(, )?prewarming \d+/\d+ MiB", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TraceTag = new(@"(, )?trace \d+/\d+( \(coverage \d+%\))?", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TierTag = new(@"(, )?ssd tier [\d.]+ GB", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly IdleTracker _idle = new();
    private DateTimeOffset? _waitSince; // 틱 스레드에서만 씀: 유휴가 아닌 채로 기다리기 시작한 때
    private bool _waitNoted; // 틱 스레드에서만 씀: 이번 대기 구간에 긴 대기를 이미 남겼는지
    private bool _prewarmHeld; // 틱 스레드에서만 씀: 예열 스트림 때문에 재배치를 미루는 중 (로그는 시작 때 한 번)
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(5); // 기록 없는 예열이 "drive busy" 로 밀렸을 때 다시 시도하는 간격 (틱마다 30 초가 아님)
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(6); // 프리페치·콜드 예열 재시도 간격 (발견 주기와 별개)
    private static readonly TimeSpan LongWait = TimeSpan.FromHours(6);
    private readonly Prewarmer? _prewarm;
    private readonly DriveRecovery _recovery;
    private readonly AutoTier? _tier; // SSD 보조 저장 (선택 기능): 환경이 엔진을 주지 않으면 (시험) 없음
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
    private readonly Dictionary<string, DateTimeOffset> _extTried = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 존 확장을 이미 따져 본 (또는 막힌) 게임과 그때의 트레이스 수정 시각 (다음 플레이가 쌓여 트레이스가 바뀔 때까지 다시 보지 않음)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _planFailedAt = new(StringComparer.Ordinal); // 계획 (또는 재배치) 이 마지막으로 실패한 시각: 그 뒤 트레이스가 새로 쓰이기 전에는 같은 실패를 되풀이하지 않음
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Text)> _transientHold = new(StringComparer.Ordinal); // 잠금 같은 일시 오류로 잔여 재계획을 못 한 게임과 다시 시도할 시각 (같은 서비스 실행 안에서 자동 재시도, 실패로 기록하지 않음)
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Text)> _unreadableHold = new(StringComparer.Ordinal); // 읽지 못한 파일이 계획에 보류로 남아 완료로 확정하지 못한 게임과 다시 계획할 시각
    private readonly ConcurrentDictionary<string, DateTimeOffset> _traceWarmTried = new(StringComparer.Ordinal); // 게임별로 예열을 시도한 트레이스 파일 시각
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
        _prewarm = prewarm;
        _recovery = new DriveRecovery(env, log, status); // 재시작 때 저장된 회복 추적을 되살림
        _tier = tier ?? (env.Tier is { } engine ? new AutoTier(env, status, log, engine) : null);
    }

    /// 쓰기(재배치) 뒤 드라이브 회복 상태: 회복 전에는 같은 드라이브의 다음 재배치·재계획을 시작하지 않습니다 (예열은 막지 않음).
    public IDriveRecovery Recovery => _recovery;

    /// 게임이 떴을 때 (감시기 스레드에서 바로): 돌고 있거나 기다리는 예열을 취소합니다. 핫 집합 스트림은 묶음 경계에서 멈춥니다 (그 게임을 읽던 스트림은 Prewarmer.NoteGameStart 가 끝까지 읽게 함).
    public void CancelWarm()
    {
        lock (_warmLock)
        {
            _warmQueue.ToList().ForEach(q => _ = _traceWarmTried.TryRemove(q.Game.Name, out _) | _coldTried.TryRemove(q.Game.Name, out _)); // 줄 서 있던 트레이스 예열은 게임이 끝난 뒤 다시 요청되게
            _warmQueue.Clear();
            _warmCts.Cancel();
            _warmCts = new CancellationTokenSource();
        }
    }

    /// 지금 이후 시작하는 예열 작업의 취소 신호 (CancelWarm 이 취소하고 새로 바꿈).
    public CancellationToken WarmToken
    {
        get
        {
            lock (_warmLock)
            {
                return _warmCts.Token;
            }
        }
    }

    /// 요청된 예열이 모두 끝나면 완료되는 작업 (시험용).
    public Task WaitWarmAsync()
    {
        lock (_warmLock)
        {
            return _warmTask;
        }
    }

    public void Tick(ReapplyGate gate, CancellationToken ct)
    {
        var warms = new List<(GameSettings Game, string Why)>(); // 틱 끝에서 요청 (재배치를 예열보다 먼저, 예열이 틱을 늦추지 않게)
        try
        {
            TickCore(gate, ct, warms);
            TierTick(gate, ct);
        }
        finally
        {
            warms.ForEach(w => QueueWarm(w.Game, w.Why));
        }
    }

    // SSD 보조 저장: 재배치가 끝난 (또는 재배치가 꺼진) 게임을 유휴일 때 적용하고, 저널을 유지하고, 꺼졌으면 되돌립니다. 상태 열 표시도 새로 붙입니다.
    private void TierTick(ReapplyGate gate, CancellationToken ct)
    {
        if (_tier is null)
        {
            return;
        }

        AsterSettings s = _settings();
        _tier.Tick(s, s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0, gate, ct);
        foreach (GameSettings g in s.Games)
        {
            lock (_noteLock)
            {
                Retag(g);
            }
        }
    }

    private void TickCore(ReapplyGate gate, CancellationToken ct, List<(GameSettings Game, string Why)> warms)
    {
        AsterSettings s = _settings();
        _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함 (재배치 유휴 판정에는 쓰지 않음)
        if (!s.Auto.Enabled)
        {
            return;
        }

        _recovery.Poll();
        RecoverReplan(s);
        ReplanChanged(s);
        ReplanExtended(s);
        ReplanLegacyVolatile(s);
        ReplanProfileSupplement(s);
        PrefetchBootstrap(s);
        WarmCold(s, warms);
        bool relocateOn = s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0;
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        List<GameSettings> work = s.Games.Where(g => !PostUpdateDelayed(g, s) && NeedsWork(g, relocateOn))
            .OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        DateTimeOffset? held = relocateOn ? _status.RelocationHeldUntil() : null; // 상태 파일을 못 읽어 막는 중
        bool limitReached = relocateOn && (held is not null || _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay); // Note 보다 먼저 정해 같은 상태 로그가 오락가락하며 쌓이지 않게
        long used = _status.RelocationBytesWithin(DailyWindow);
        var ready = new List<GameSettings>();
        foreach (GameSettings g in work)
        {
            if (_recovery.IsRelocationHeld(DriveOf(g)))
            {
                WarmNewTrace(g, warms); // 예열은 막지 않음
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, DriveRecovering));
                continue;
            }

            if (RecoveryHeldUntil(g) is { } heldUntil)
            {
                WarmNewTrace(g, warms); // 예열은 막지 않음
                Note(g, AutoStage.WaitingIdle, HoldText(heldUntil)); // 점유 충돌 보류: 재시작해도 풀리지 않음 (auto_status 에 저장)
                continue;
            }

            if (_transientHold.TryGetValue(g.Name, out (DateTimeOffset Until, string Text) transient))
            {
                if (transient.Until > _env.Now())
                {
                    WarmNewTrace(g, warms);
                    Note(g, AutoStage.WaitingIdle, transient.Text);
                    continue;
                }

                _ = _transientHold.TryRemove(g.Name, out _);
            }

            if (_unreadableHold.TryGetValue(g.Name, out (DateTimeOffset Until, string Text) unreadable))
            {
                if (unreadable.Until > _env.Now())
                {
                    WarmNewTrace(g, warms);
                    Note(g, AutoStage.WaitingIdle, unreadable.Text);
                    continue;
                }

                _ = _unreadableHold.TryRemove(g.Name, out _);
                if (!AnyGameRunning(g) && !RetryUnreadable(g))
                {
                    continue;
                }
            }

            if (!_env.FileExists(g.Plan) && (AnyGameRunning(g) || !Plan(g, s)))
            {
                continue; // 게임이 도는 중이면 디스크를 건드리지 않고, 계획이 실패했으면 failed 로 기록됨
            }

            WarmNewTrace(g, warms); // 재배치를 못 하거나 기다리는 동안에도 트레이스가 있으면 바로 체감할 수 있게
            if (!_env.Capabilities(g.OriginRoot).CanRelocate)
            {
                Note(g, AutoStage.Skipped, DriveCapabilities.PrewarmOnly); // 시스템 HDD: 읽기 전용 예열만, 재배치 없음
                continue;
            }

            if (!relocateOn)
            {
                Note(g, AutoStage.Skipped, RelocateOff);
                continue;
            }

            long budget = BudgetBytes(s, g); // 0 = 무제한
            bool budgetHeld = budget > 0 && used >= budget; // 1 바이트라도 남아야 함: 남은 만큼만 쓰도록 적용의 쓰기 상한을 줄이므로 첫 이동 예외는 없음
            lock (_noteLock)
            {
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, held is { } until ? AutoStatus.HoldDetail(until) : limitReached ? DailyLimit : budgetHeld ? DailyBudget : null));
            }

            if (!budgetHeld)
            {
                ready.Add(g);
            }
        }

        if (ready.Count == 0 || limitReached)
        {
            _idle.Reset();
            ResetWait();
            return;
        }

        string? busy = BusyReason(s, ready);
        NoteWait(busy);
        if (!_idle.Sample(_env.Now(), busy is null, s.Auto.IdleWindow))
        {
            return;
        }

        GameSettings next = ready[0];
        if (IsWarming(next.Name))
        {
            return; // 그 게임의 예열이 읽는 중이면 이동은 다음 틱에 (유휴 조건은 그대로 이어짐)
        }

        gate.Run(ct, token =>
        {
            Relocate(next, s, token);
            return true;
        });
        _idle.Reset(); // 다음 재배치도 다시 유휴를 기다림
        Warm(next, "after relocation", warms); // 끝났든 보류·실패했든 트레이스가 있으면 그 범위로 바로 예열 (새 배치가 RAM 에 오르게)
    }

    // 부트스트랩 S2: 원시 트레이스도 boot 트레이스도 (S1: 공유 프로필이 만든 것 포함) 없는 등록 게임은 Windows 프리페치 기록 (.pf) 으로 boot 트레이스를 만듭니다.
    // 못 만들면 (프리페처 꺼짐, 맞는 기록 없음) 바로 아래 WarmCold (S3) 가 폴더 통째·엔진 프로필로 예열합니다. 재배치 대상 (HDD) 에서 게임이 돌지 않을 때만, 게임마다 발견 주기에 한 번 시도합니다.
    private void PrefetchBootstrap(AsterSettings s)
    {
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        foreach (GameSettings g in s.Games.OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            if (AutoTracer.PlanTracePath(g, _env.FileExists) is not null || _prefetchTried.TryGetValue(g.Name, out DateTimeOffset at) && _env.Now() - at < RetryEvery
                || !_env.Capabilities(g.OriginRoot).CanPrewarm || AnyGameRunning(g))
            {
                continue;
            }

            _prefetchTried[g.Name] = _env.Now();
            try
            {
                if (_env.PrefetchBoot(g) is > 0 and int n)
                {
                    _log.Info($"{g.Name}: boot trace made from the prefetch record ({n} ranges)");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: prefetch record could not be used ({e.GetType().Name})");
            }
        }
    }

    // 기록 (원시 트레이스·prefetch 기록) 이 없는 등록 게임은 첫 실행 없이 폴더 통째·엔진 프로필로 예열합니다 (게임당 서비스 실행 중 한 번: 일시적 이유로 건너뛰면 다음 틱에 다시).
    // 자동 모드의 재배치 대상 (HDD) 에서만 합니다. 서비스 시작·런처 트리거 때의 예열은 Prewarmer 가 직접 합니다.
    private void WarmCold(AsterSettings s, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is null || !_prewarm.BootSettled()) // 부팅 직후에는 디스크 경쟁을 피해 미룸
        {
            return;
        }

        (bool, int, bool) key = (s.Auto.FullPrewarm, s.Auto.FullPrewarmMaxMib, s.Auto.EngineProfiles);
        if (key != _coldKey)
        {
            _coldKey = key;
            _coldTried.Clear();
        }

        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        foreach (GameSettings g in AutoMode.Recent(s.Games, played, s.Auto.PrewarmRecent)) // 플레이한 적 있는 게임만 (prewarm_recent 안, 미플레이는 첫 실행 때 트리거 예열만)
        {
            if ((!_coldTried.TryGetValue(g.Name, out DateTimeOffset triedAt) || _env.Now() - triedAt >= RetryEvery) && !PostUpdateDelayed(g, s) && _prewarm.IsCold(g) && _prewarm.LastWarmed(g.Name) is null && _env.Capabilities(g.OriginRoot).CanPrewarm && !AnyGameRunning(g))
            {
                _coldTried[g.Name] = _env.Now();
                warms.Add((g, ColdWhy));
            }
        }
    }

    /// 예열 실측 MB/s (Prewarmer.WarmMeasured): 진단 로그용일 뿐 회복 판정에는 쓰이지 않음.
    public void NoteWarmMbps(GameSettings g, double mbps) => _recovery.NoteWarmMbps(DriveOf(g), mbps);

    /// 기록 없이 예열에 성공했을 때 (Prewarmer.ColdWarmed): auto_status 의 detail 을 "prewarmed (full)" 같은 표시로. 실패·건너뜀·재배치 중인 게임은 그대로 둡니다.
    public void NoteColdWarmed(GameSettings g, string label)
    {
        lock (_noteLock)
        {
            AutoGameEntry? e = _status.Get(g.Name);
            if (e is null ? g.Source != "auto" : e.Stage is AutoStage.Failed or AutoStage.Skipped or AutoStage.Relocating or AutoStage.Done)
            {
                return;
            }

            Note(g, e?.Stage ?? AutoStage.Discovered, WithPrewarmed(e?.Detail, PrewarmedBy(label)));
        }
    }

    /// 핫 집합 예열의 진행 (읽은 MiB, 총 MiB): 단계는 그대로 두고 detail 에 "prewarming x/y MiB" 를 붙입니다 (로그에는 남기지 않음).
    public void NoteProgress(GameSettings g, long doneMib, long totalMib)
    {
        lock (_noteLock)
        {
            _progress[g.Name] = $"prewarming {doneMib}/{totalMib} MiB";
            Retag(g);
        }
    }

    /// 핫 집합 스트림이 끝남: 진행 표시를 지우고, 끝까지 읽었으면 "prewarmed" 를 바로 붙입니다 (재배치 대기·완료 단계만, WarmNow 와 같음).
    public void NoteStreamEnded(GameSettings g, bool complete)
    {
        lock (_noteLock)
        {
            _progress.TryRemove(g.Name, out _);
            AutoGameEntry? e = _status.Get(g.Name);
            if (complete && e?.Stage is AutoStage.Done or AutoStage.WaitingIdle)
            {
                Note(g, e.Stage, WithPrewarmed(DoneDetail(e)));
            }
            else
            {
                Retag(g);
            }
        }
    }

    // 진행 표시가 보일 수 있는 단계: 학습 중 (선행 읽기 양 표시)·트레이스·재배치·실패·건너뜀의 detail 은 건드리지 않음.
    private static bool ShowsProgress(string stage) => stage is AutoStage.Discovered or AutoStage.WaitingIdle or AutoStage.Done;

    private void Retag(GameSettings g)
    {
        if (_status.Get(g.Name) is { } e && ShowsProgress(e.Stage))
        {
            _status.Set(g.Name, g.Source, null, e.Stage, Tagged(g, e.Stage, e.Detail));
        }
    }

    // detail 에서 옛 진행 표시를 지우고, 지금 진행 중이면 새 표시를 붙입니다.
    private string? Tagged(GameSettings g, string stage, string? detail)
    {
        string game = g.Name;
        string? rest = detail is null ? null : TraceTag.Replace(TierTag.Replace(ProgressTag.Replace(detail, string.Empty), string.Empty), string.Empty).Trim(' ', ',');
        rest = string.IsNullOrEmpty(rest) ? null : rest;
        if (ShowsProgress(stage) && AutoTracer.NextTrace(g, _settings().Auto, _env, _status) is { Valid: > 0, Wanted: true } next) // 플레이를 더 모으는 중: "trace 2/3", 추가 수집이면 "trace 4/8 (coverage 22%)"
        {
            string traceTag = $"trace {next.Valid}/{next.Limit}" + (next.Valid >= _settings().Auto.TraceRuns && next.Coverage is { } cov ? $" (coverage {cov}%)" : string.Empty);
            rest = rest is null ? traceTag : rest + ", " + traceTag;
        }

        if (ShowsProgress(stage) && _tier?.Tag(game) is { } tier)
        {
            rest = rest is null ? tier : rest + ", " + tier;
        }

        return ShowsProgress(stage) && _progress.TryGetValue(game, out string? tag) ? (rest is null ? tag : rest + ", " + tag) : rest;
    }

    // 원시 트레이스가 아직 예열된 적 없거나 마지막 예열보다 새로우면 한 번 예열합니다 (같은 트레이스로는 한 번만 시도: 메모리가 모자라 건너뛰어도 30 초마다 다시 부르지 않음).
    private void WarmNewTrace(GameSettings g, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is null || AutoTracer.PlanTracePath(g, _env.FileExists) is not { } tracePath || _env.FileWriteTime(tracePath) is not { } traced
            || _traceWarmTried.TryGetValue(g.Name, out DateTimeOffset tried) && tried >= traced
            || _prewarm.LastWarmed(g.Name) is { } last && last >= traced
            || AnyGameRunning(g))
        {
            return;
        }

        _traceWarmTried[g.Name] = traced;
        Warm(g, "new trace", warms);
    }

    // g 와 같은 드라이브에 설치된 등록 게임 (g 포함) 이 도는 중인지: 다른 드라이브의 게임은 g 의 디스크 읽기와 겹치지 않으므로 보지 않습니다.
    private bool AnyGameRunning(GameSettings g) =>
        _env.AnyRunning([.. SameDriveGames(_settings().Games, [g], _env.SameDisk).SelectMany(x => x.ExeNames).Concat(g.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)]);

    // targets 와 같은 물리 디스크 (sameDisk 판정) 에 설치된 games.
    public static IEnumerable<GameSettings> SameDriveGames(IEnumerable<GameSettings> games, IEnumerable<GameSettings> targets, Func<string, string, bool> sameDisk)
    {
        List<string> roots = [.. targets.Select(t => t.OriginRoot)];
        return games.Where(x => roots.Any(r => sameDisk(r, x.OriginRoot)));
    }

    // 게임이 도는 중이면 디스크를 건드리지 않습니다. 쿨다운은 무시합니다 (트레이스나 배치가 새로 바뀐 뒤의 예열이므로).
    private void Warm(GameSettings g, string why, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is not null && !AnyGameRunning(g))
        {
            warms.Add((g, why));
        }
    }

    private bool IsWarming(string game)
    {
        lock (_warmLock)
        {
            return _warming == game || _warmQueue.Any(q => q.Game.Name == game);
        }
    }

    // 같은 게임이 이미 줄에 있으면 다시 넣지 않음. 작업이 없으면 하나를 띄워 줄이 빌 때까지 차례로 돌립니다.
    private void QueueWarm(GameSettings g, string why)
    {
        lock (_warmLock)
        {
            if (_warmQueue.Any(q => q.Game.Name == g.Name))
            {
                return;
            }

            _warmQueue.Enqueue((g, why));
            if (!_warmRunning)
            {
                _warmRunning = true;
                _warmTask = Task.Run(DrainWarm, CancellationToken.None);
            }
        }
    }

    private void DrainWarm()
    {
        while (true)
        {
            GameSettings g;
            string why;
            CancellationToken token;
            lock (_warmLock)
            {
                if (!_warmQueue.TryDequeue(out (GameSettings Game, string Why) next))
                {
                    (_warmRunning, _warming) = (false, null);
                    return;
                }

                (g, why, _warming, token) = (next.Game, next.Why, next.Game.Name, _warmCts.Token);
            }

            try
            {
                WarmNow(g, why, token);
            }
            catch (Exception e)
            {
                _log.Error($"{g.Name}: auto prewarm failed", e);
            }
        }
    }

    // 게임이 돌면 디스크를 건드리지 않습니다. 쿨다운은 무시합니다 (트레이스나 배치가 새로 바뀐 뒤의 예열이므로). 취소·중단된 트레이스 예열은 다음 틱에 다시 시도합니다.
    private void WarmNow(GameSettings g, string why, CancellationToken token)
    {
        if (token.IsCancellationRequested || AnyGameRunning(g))
        {
            _traceWarmTried.TryRemove(g.Name, out _);
            _coldTried.TryRemove(g.Name, out _);
            return;
        }

        PrewarmResult r = _prewarm!.Guarded(g, "auto " + why, pin: true, ignoreCooldown: true, token);
        if (why == ColdWhy)
        {
            // 일시적인 이유 (메모리 부족, 게임 실행 중, 취소) 로 못 했으면 다음 틱에 다시, 디스크 바쁨은 BusyRetry 뒤에. 폴더가 크고 맞는 엔진이 없는 것 같은 결과는 다시 열거하지 않음.
            if (!r.Ran && r.SkipReason == "drive busy")
            {
                _coldTried[g.Name] = _env.Now() - RetryEvery + BusyRetry;
            }
            else if (!r.Ran && r.SkipReason is { } why2 && (why2 == Prewarmer.Cancelled || why2 == "game is running" || why2.StartsWith("low memory", StringComparison.Ordinal)
                || why2.StartsWith("cooldown", StringComparison.Ordinal) || why2.StartsWith("failed", StringComparison.Ordinal)))
            {
                _coldTried.TryRemove(g.Name, out _);
            }
        }
        else if (r.SkipReason == Prewarmer.Cancelled)
        {
            _traceWarmTried.TryRemove(g.Name, out _);
        }
        else if (r.Ran)
        {
            lock (_noteLock)
            {
                AutoGameEntry? e = _status.Get(g.Name);
                if (e?.Stage is AutoStage.Done or AutoStage.WaitingIdle)
                {
                    Note(g, e.Stage, WithPrewarmed(DoneDetail(e)));
                }
            }
        }
    }

    private string? WaitDetail(GameSettings g, string? d)
    {
        return _prewarm?.LastWarmed(g.Name) is null ? d : WithPrewarmed(d);
    }

    // 이미 있는 "prewarmed" 표시 (기록 없는 예열의 "(full)" 포함) 는 새 표시로 바꿉니다.
    private static string WithPrewarmed(string? detail, string tag = Prewarmed)
    {
        string rest = detail is null ? string.Empty : PrewarmedTag.Replace(detail, string.Empty).Trim(' ', ',');
        return rest.Length == 0 ? tag : rest + ", " + tag;
    }

    // 상태가 바뀌었을 때만 로그에 남깁니다 (30 초마다 같은 줄이 쌓이지 않게).
    private void Note(GameSettings g, string stage, string? detail)
    {
        detail = Tagged(g, stage, detail);
        if (_status.Set(g.Name, g.Source, null, stage, detail))
        {
            _log.Info($"{g.Name}: auto {stage}" + (detail is null ? string.Empty : $" ({detail})"));
        }
    }

    // 재배치 도중 목표 존이 막혀 멈춘 경우: 업데이트 재계획과 같은 방식으로 저널·계획을 옆으로 치우고 (트레이스는 그대로) 처음 단계로 되돌립니다.
    private void SetAsideForReplan(GameSettings g)
    {
        if (_env.ReplanResidual is null || _env.JournalRows(g) == 0)
        {
            _ = _env.ArchiveJournal(g); // 이미 옮긴 기록이 없거나 잔여 재계획이 없으면 처음부터 다시 계획
        }
        // 저널에 행이 있으면 그대로 둠: 다음 Plan 이 저널과 .stale 로 잔여 재계획하고 교체 때 저널을 보관 (보관부터 하면 이미 옮긴 데이터를 다시 옮김)
        _env.MoveAside(g.Plan, g.Plan + StaleSuffix);
        _status.ResetForReplan(g.Name, BusyDetail);
        _log.Info($"{g.Name}: zone region appears occupied, plan set aside as {StaleSuffix}, will plan again");
    }

    // 업데이트 뒤 재계획: 배치까지 끝낸 게임의 원본이 계획 뒤 바뀌었으면 (ReapplyChecker 의 PlanChanged 와 같은 기준) 낡은 트레이스·계획을 .stale 로 치우고
    // 처음 단계로 되돌립니다. 다음 실행에서 다시 트레이스·학습하고 새 계획으로 다시 배치합니다 (이전 존 시작을 먼저 시도). 일일 상한과 다른 안전장치는 그대로.
    // 마지막에 옮기는 계획이 남아 있어야 중간에 실패해도 다음 검사가 이어서 처리합니다.
    private void ReplanChanged(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            if (_status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || AnyGameRunning(g)
                || _staleChecked.TryGetValue(g.Name, out DateTimeOffset at) && _env.Now() - at < StaleCheckEvery)
            {
                continue;
            }

            _staleChecked[g.Name] = _env.Now();
            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue;
                }

                // 새 트레이스로도 계획이 실패해 낡은 계획을 되살린 게임: 같은 트레이스로는 같은 실패라 그 뒤 트레이스가 새로 쓰였을 때만 다시 계획합니다 (그 트레이스는 새 버전의 것이라 치우지 않음).
                bool retry = false;
                if (_planFailedAt.TryGetValue(g.Name, out DateTimeOffset failed))
                {
                    if (_env.FileWriteTime(AutoTracer.RawTracePath(g)) is not { } written || written <= failed)
                    {
                        continue;
                    }

                    retry = true;
                    _ = _planFailedAt.TryRemove(g.Name, out _);
                }
                else if (_status.Get(g.Name)?.Detail?.StartsWith(StalePlanDetail, StringComparison.Ordinal) == true)
                {
                    retry = true; // 되살린 낡은 계획을 재시작 뒤 보면 같은 취급: 트레이스를 치우지 않고 그 트레이스로 다시 계획 (실패하면 다시 되살리고 실패 시각이 남음)
                }

                // boot 트레이스로 배치한 게임이 첫 실행을 기록했으면 (원시 트레이스가 생김) 그 기록으로 계획을 바꿉니다: 원시 트레이스는 치우지 않음.
                bool recorded = retry || IsBootPlan(plan) && _env.FileExists(AutoTracer.RawTracePath(g));
                if (!recorded && !ReplanStale(g, plan))
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "game updated"); // 링크는 새 재배치 전에 되돌림 (심볼릭 링크가 낀 계획은 재적용·재배치가 거부함)
                _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                string trace = AutoTracer.RawTracePath(g);
                bool keep = !recorded && _env.TraceBlocker(g) is not null && TraceHasUsableBlocks(trace, g); // 다시 트레이스할 수 없는 게임은 옛 트레이스를 유지 (없어진 파일·줄어든 범위는 읽을 때 걸러짐)
                if (!recorded && !keep && _env.FileExists(trace))
                {
                    for (int run = AutoTracer.LastRunNumber(g, _env.FileExists); run >= 1; run--)
                    {
                        string runFile = TraceWriter.RunPath(trace, run); // 플레이 수는 run 파일로 세므로 옛 버전의 run 파일이 남지 않게 함
                        if (_env.FileExists(runFile))
                        {
                            _env.MoveAside(runFile, runFile + StaleSuffix);
                        }
                    }

                    _env.MoveAside(trace, trace + StaleSuffix);
                    string marks = AutoTracer.VolatilePath(g); // 옛 트레이스에서 알게 된 가변 파일 표시도 같이 치움
                    if (_env.FileExists(marks))
                    {
                        _env.MoveAside(marks, marks + StaleSuffix);
                    }
                }

                _env.MoveAside(g.Plan, g.Plan + StaleSuffix);
                _status.ResetForReplan(g.Name, recorded ? FirstRunDetail : keep ? KeptDetail : ReplanDetail);
                _log.Info(retry
                    ? $"{g.Name}: trace updated after a failed replan, stale plan set aside as {StaleSuffix}, will plan again from it"
                    : recorded
                        ? $"{g.Name}: first run recorded, profile plan set aside as {StaleSuffix}, will plan again from the recording (same zone first)"
                        : keep
                            ? $"{g.Name}: game updated; trace kept (tracing is not allowed for this game), re-planning"
                            : $"{g.Name}: source files changed since the plan (game update), trace and plan set aside as {StaleSuffix}, will trace and plan again on the next run");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: update check failed, will retry ({e.GetType().Name})");
            }
        }
    }

    // 트레이스를 읽었을 때 (없는 파일·범위 밖 행은 걸러지고 줄어든 파일은 새 크기로 잘림) 남는 블록이 있는가. 읽을 수 없어도 거짓: 유지하지 않고 기존대로 치움.
    private bool TraceHasUsableBlocks(string trace, GameSettings g)
    {
        try
        {
            return _env.FileExists(trace) && _env.LoadTrace(trace, g.OriginRoot).Count > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    // 서비스 시작 때 한 번: 트레이스 금지 게임인데 업데이트 재계획이 트레이스를 .stale 로 치워 사장시켰으면 (다시 트레이스할 수 없어 영영 재계획 불가) 되살립니다.
    // 그 사이 만들어진 boot 계획 (프리페치·프로필) 은 옆으로 치워 이미 옮긴 존 자리에 맞춰 다시 계획하게 합니다: 이전 .stale 계획이 있으면 그 존 시작을 지키려고 .boot 로, 없으면 .stale 로.
    // 순서: 계획 → 상태 → 트레이스. 마지막 단계 (원시 트레이스 복원) 전에 꺼져도 조건이 그대로라 다음 시작에 이어집니다.
    // 반환: 트레이스를 되살려 다시 계획하게 둔 게임 이름 (호출자가 같은 회차의 이전 계획 복원에서 뺌).
    private HashSet<string> RestoreStaleTraces(AsterSettings s)
    {
        var restored = new HashSet<string>(StringComparer.Ordinal);
        foreach (GameSettings g in s.Games)
        {
            string raw = AutoTracer.RawTracePath(g);
            if (_env.FileExists(raw) || !_env.FileExists(raw + StaleSuffix) || _env.TraceBlocker(g) is null)
            {
                continue;
            }

            try
            {
                if (!TraceHasUsableBlocks(raw + StaleSuffix, g))
                {
                    continue; // 파일이 다 없어졌거나 읽을 수 없는 옛 트레이스는 되살려도 계획할 수 없음
                }

                if (_env.FileExists(g.Plan))
                {
                    if (!IsBootPlan(_env.LoadPlan(g.Plan)))
                    {
                        continue; // 이미 트레이스로 만든 계획이 있음
                    }

                    _tier?.RevertGame(g.Name, "trace restored");
                    _ = _env.ArchiveJournal(g);
                    _env.MoveAside(g.Plan, g.Plan + (_env.FileExists(g.Plan + StaleSuffix) ? BootPlanSuffix : StaleSuffix));
                }

                _status.ResetForReplan(g.Name, FirstRunDetail); // 계획이 없던 게임도 다시 계획 상태로 (이전 계획 복원으로 done 이 되지 않게)
                for (int run = 1; run <= 32; run++)
                {
                    string runFile = TraceWriter.RunPath(raw, run);
                    if (_env.FileExists(runFile + StaleSuffix))
                    {
                        _env.MoveAside(runFile + StaleSuffix, runFile);
                    }
                }

                string marks = AutoTracer.VolatilePath(g);
                if (_env.FileExists(marks + StaleSuffix) && !_env.FileExists(marks))
                {
                    _env.MoveAside(marks + StaleSuffix, marks);
                }

                _env.MoveAside(raw + StaleSuffix, raw);
                _ = restored.Add(g.Name);
                _log.Info($"{g.Name}: trace set aside by an earlier update restored (tracing is not allowed for this game), re-planning");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: stale trace could not be restored ({e.GetType().Name})");
            }
        }

        return restored;
    }

    // 드라이브가 분리됐거나 (폴더 없음) 파일을 읽을 수 없으면 (잠김 등) "변경" 이 아니라 건너뜀: 다음 검사에 다시 봅니다. 대표 파일 (게임 exe, 가장 큰 파일 몇 개) 이
    // 바뀐 경우에만 전체 지문을 비교합니다 (shortcut: 대표가 아닌 파일만 바뀐 업데이트는 대표가 바뀔 때까지 못 잡음, 재배치 직전 검사가 잡음).
    private bool ReplanStale(GameSettings g, PlanFile plan)
    {
        List<Fingerprint> prints = [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path))]; // 게임이 스스로 다시 쓰는 파일 (캐시·설정·로그·세이브) 은 업데이트 증거가 아님
        IEnumerable<Fingerprint> sample = prints.Where(f => g.ExeNames.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))
            .Concat(prints.OrderByDescending(f => f.Size).Take(LargestSampled));
        return sample.Any(Differs) && prints.Any(Differs);
    }

    // 지문과 다른가. 파일이 없어짐 (업데이트가 지움) 은 바뀜, 그 밖의 읽기 실패 (잠김·권한) 는 판단 불가라 재계획하지 않음 (재적용 검사와 같은 기준).
    private bool Differs(Fingerprint fp) => ReapplyChecker.Compare(fp, _env.Stat) == true;

    // 재계획 도중 서비스가 꺼진 경우 (계획은 .stale 로 옮겼는데 상태를 되돌리기 전): 배치됨 표시는 남았고 계획은 없고 .stale 만 있으면 상태를 마저 되돌립니다.
    private void RecoverReplan(AsterSettings s)
    {
        if (_recovered)
        {
            RetryPartialRecovery(s);
            return;
        }

        _recovered = true;
        foreach (GameSettings g in s.Games)
        {
            ResumeSwitch(g); // 중단된 계획 교체를 먼저 끝냄 (모든 게임: 계획·저널 파일만 만지므로 게임이 돌아도 됨)
        }

        // 계획이 있어도 쓰다가 꺼져 잘렸으면 (읽을 수 없음) .bad 로 치워 없는 것으로 다룹니다: 되살릴 이전 계획 (.stale·.extend) 이 있는 게임만.
        foreach (GameSettings g in s.Games.Where(g => _env.FileExists(g.Plan) && (_env.FileExists(g.Plan + StaleSuffix) || IsExtending(g))))
        {
            try
            {
                _ = _env.LoadPlan(g.Plan);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                try
                {
                    _env.MoveAside(g.Plan, g.Plan + BadSuffix);
                    _log.Warn($"{g.Name}: plan unreadable ({e.GetType().Name}), set aside as {BadSuffix}");
                }
                catch (Exception m) when (m is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"{g.Name}: unreadable plan could not be set aside ({m.GetType().Name})");
                }
            }
        }

        // 새 존 전환 중 꺼짐: Plan 은 새 존 계획 (Zone.StartLcn 없음) 을 저장한 뒤에 .extend 를 .stale 로 치웁니다. 확장 계획은 MakePlanAt 로 존 시작이 항상 들어 있으므로
        // (BlockExtension 으로 떨어지지 않는 확장 계획은 Zone.StartLcn 이 있음), .extend 가 있는데 저장된 계획에 존 시작이 없으면 전환이 끝났고 치우기만 남은 것입니다.
        // 저널 보관은 Plan 이 저장 전에 이미 했으므로 여기서 하지 않음. 이미 .stale 이 있으면 Plan 과 같이 덮어씀.
        foreach (GameSettings g in s.Games.Where(g => _env.FileExists(g.Plan) && IsExtending(g)))
        {
            try
            {
                if (_env.LoadPlan(g.Plan).Zone.StartLcn is null)
                {
                    _env.MoveAside(g.Plan + ExtendSuffix, g.Plan + StaleSuffix);
                    _log.Info($"{g.Name}: zone switch to a new zone was interrupted, finished (previous plan kept as {StaleSuffix})");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: interrupted zone switch could not be finished ({e.GetType().Name})");
            }
        }

        HashSet<string> restored = RestoreStaleTraces(s); // 손상 계획을 치운 뒤에야 부트 계획 읽기가 실패하지 않아 복원이 건너뛰어지지 않음

        // 계획이 없고 .stale 이나 .extend 만 있는 failed 게임 (재계획·확장이 실패한 채 꺼짐) 또는 잘린 계획을 .bad 로 치운 게임 (상태 무관, 치운 직후 꺼져도 파일로 알 수 있음; 업데이트 뒤 재계획 대기 중이거나 되살린 트레이스로 다시 계획하는 중 (FirstRunDetail) 이면 제외): 이전 계획을 되살려 done 으로 돌립니다.
        foreach (GameSettings g in s.Games.Where(g => (_status.Get(g.Name) is { Stage: AutoStage.Failed, RelocatedAt: null } || _env.FileExists(g.Plan + BadSuffix) && Replanning(_status, g.Name) is null && _status.Get(g.Name) is not { Stage: AutoStage.Discovered, Detail: FirstRunDetail }) && !_env.FileExists(g.Plan) && !restored.Contains(g.Name) && (_env.FileExists(g.Plan + StaleSuffix) || IsExtending(g))))
        {
            if (IsExtending(g))
            {
                BlockExtension(g, "failed before the service restarted");
            }
            else
            {
                _ = RestoreStalePlan(g, "failed before the service restarted");
            }
        }

        foreach (GameSettings g in s.Games.Where(g => _status.Get(g.Name)?.RelocatedAt is not null && !_env.FileExists(g.Plan) && !restored.Contains(g.Name) && (_env.FileExists(g.Plan + StaleSuffix) || IsExtending(g))))
        {
            _status.ResetForReplan(g.Name, IsExtending(g) ? ExtendDetail : ReplanDetail);
            _log.Info($"{g.Name}: an earlier replan was interrupted, state reset to trace and plan again");
        }

        foreach (GameSettings g in s.Games.Where(g => !_recoverRetry.Contains(g.Name)))
        {
            RegisterPartial(g); // 활성 계획과 저널 header 대조: 어긋난 게임은 다음 틱의 일반 흐름에서 잔여 재계획
        }
    }

    private readonly Dictionary<string, DateTimeOffset> _recoverNextTry = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 배치를 읽지 못해 재시도 목록에 남은 게임의 다음 확인 시각 (매 틱 모든 파일을 다시 읽지 않게)
    private readonly HashSet<string> _recoverRetry = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 시작 복구가 파일을 읽지 못한 게임 (복구 완료로 치지 않고 다음 틱에 다시)

    private const string PartialDetail = "plan and journal differ, will replan the remaining part";

    private void ResumeSwitch(GameSettings g)
    {
        try
        {
            if (_env.ResumeSwitch(g) is { } r)
            {
                _log.Info($"{g.Name}: interrupted plan switch finished" + (r.ArchivedJournal is { } a ? $", earlier journal kept as {a}" : string.Empty));
            }

            _ = _recoverRetry.Remove(g.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or PyValueException)
        {
            _ = _recoverRetry.Add(g.Name);
            _log.Warn($"{g.Name}: interrupted plan switch could not be finished, will retry ({e.GetType().Name})");
        }
        catch (PlanRejectedException e)
        {
            // 잠금 busy 는 다음 틱에, 세대 거절 (활성 계획·저널이 기록과 어긋남) 은 BusyRetry 뒤에 다시: 그 게임에 한정해 재시도 목록에 올리고 뒤 게임의 복구는 계속함
            _ = _recoverRetry.Add(g.Name);
            if (!IsLockBusy(e))
            {
                _recoverNextTry[g.Name] = _env.Now() + BusyRetry;
            }

            _log.Warn($"{g.Name}: interrupted plan switch could not be finished, will retry ({FirstLine(e.Message)})");
        }
    }

    // 활성 계획의 SHA 가 저널 header 와 다르면 복구 대기로 등록: 배치됨으로 보이던 게임은 처음 단계로 돌려 일반 재배치 흐름 (유휴·게임 실행·SMR 예산 게이트) 을 타게 함.
    // 파일을 읽지 못하면 복구 완료로 치지 않고 다음 틱에 다시 봅니다.
    private void RegisterPartial(GameSettings g)
    {
        try
        {
            PlanFile? plan = _env.FileExists(g.Plan) ? _env.LoadPlan(g.Plan) : null;
            if (plan is null || _env.JournalPlanSha(g) is not { } journalSha || journalSha == plan.Sha())
            {
                _ = _recoverRetry.Remove(g.Name);
                return;
            }

            if (_status.Get(g.Name) is { RelocatedAt: not null })
            {
                switch (LayoutInPlace(plan))
                {
                    case false:
                        _status.ResetForReplan(g.Name, PartialDetail); // 배치됨으로 보이지만 계획의 배치가 아님: 이미 제자리인 게임은 건드리지 않음
                        break;
                    case null: // 읽지 못한 파일이 있어 배치를 모름: 이미 배치됨으로 치지 않고 재시도 목록에 남김
                        _ = _recoverRetry.Add(g.Name);
                        _recoverNextTry[g.Name] = _env.Now() + BusyRetry;
                        _log.Warn($"{g.Name}: layout of the active plan could not be read at startup, will retry");
                        return;
                }
            }

            _log.Warn($"{g.Name}: active plan and journal belong to different plans, the remaining part will be replanned");
            _ = _recoverRetry.Remove(g.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or PyValueException)
        {
            _ = _recoverRetry.Add(g.Name);
            _log.Warn($"{g.Name}: plan or journal could not be read at startup, will retry ({e.GetType().Name})");
        }
    }

    // 계획의 배치가 이미 이뤄졌는가. 읽지 못한 파일이 있거나 조회가 실패하면 모름 (null): 이미 배치됨으로 치지도, 재계획하지도 않고 호출자가 다시 확인합니다.
    private bool? LayoutInPlace(PlanFile plan)
    {
        try
        {
            LayoutReport r = LayoutVerifier.Verify(plan, _env.Extents);
            return r.MissingFiles > 0 ? null : r.Contiguous;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private void RetryPartialRecovery(AsterSettings s)
    {
        foreach (GameSettings g in s.Games.Where(g => _recoverRetry.Contains(g.Name) && !(_recoverNextTry.TryGetValue(g.Name, out DateTimeOffset next) && next > _env.Now())))
        {
            _ = _recoverNextTry.Remove(g.Name);
            ResumeSwitch(g);
            if (!_recoverRetry.Contains(g.Name))
            {
                RegisterPartial(g);
            }
        }
    }

    private const string WriteLimitDetail = "write limit reached, will continue";

    // 확장 중인가: 확장 재계획이 이전 계획을 .extend 로 치운 뒤 끝나거나 막히기 전까지 (파일이라 서비스를 다시 시작해도 이어짐).
    private bool IsExtending(GameSettings g) => _env.FileExists(g.Plan + ExtendSuffix);

    // 프로필 보충 재계획인가: 이전 계획 (.extend) 에 표지가 있음. 보충은 이미 옮긴 존을 지키려는 것이라 뒤 공간이 쓰이면 새 존으로 가지 않고 보류합니다.
    private bool IsSupplementExtension(GameSettings g)
    {
        try
        {
            return IsExtending(g) && _env.LoadPlan(g.Plan + ExtendSuffix).Stats.TryGetValue(SupplementReplanKey, out object? marked) && marked is 1L;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return false;
        }
    }

    // 존 확장 재계획: 배치까지 끝낸 게임의 병합 트레이스 (플레이를 더 모아 합집합이 된 trace.json) 가 계획보다 새롭고, 핫 집합이 TraceMergeMinMib 이상 커졌으면
    // 이전 계획을 .extend 로 치우고 처음 단계로 되돌립니다. 이후는 일반 흐름 (계획 → 재배치): 같은 존 시작에 맞춰 계획하므로 이미 존에 있는 블록은 제자리이고 새 블록만 존 뒤에 붙으며,
    // 유휴·일일 상한·바이트 예산·회복·전원·같은 디스크 게임 같은 안전장치를 그대로 지납니다. 존 뒤가 비어 있지 않은 등 확장할 수 없으면 BlockExtension 이 이전 계획을 되살립니다.
    private void ReplanExtended(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            string raw = AutoTracer.RawTracePath(g);
            if (_status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || _env.FileWriteTime(raw) is not { } traced || _env.FileWriteTime(g.Plan) is not { } planned)
            {
                continue;
            }

            // 새 트레이스: 계획보다 새롭고 아직 따져 보지 않았고 두 번째 이후 플레이가 병합된 것. 존 상한 설정이 바뀌었거나 처음 보는 게임도 한 번 따져 봄 (상한을 올려도 트레이스는 안 바뀌므로).
            bool newTrace = traced > planned && !(_extTried.TryGetValue(g.Name, out DateTimeOffset seen) && seen >= traced) && AutoTracer.RunCount(g, _env.FileExists) >= 2;
            (bool, int) limitKey = (s.MaxZoneAuto, s.MaxZoneMib);
            bool limitDue = !_limitSeen.TryGetValue(g.Name, out (bool, int) seenKey) || seenKey != limitKey;
            if (!newTrace && !limitDue)
            {
                continue;
            }

            _limitSeen[g.Name] = limitKey;
            if (newTrace)
            {
                _extTried[g.Name] = traced;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || IsBootPlan(plan) || !plan.Stats.TryGetValue("hot_blocks", out object? hb) || hb is not long hotBlocks || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue; // boot 계획은 첫 기록 재계획 (ReplanChanged) 의 몫
                }

                long limit = MaxZoneMib(s, g);
                bool metaSame = plan.Stats.TryGetValue(EffectiveZoneKey, out object? ez) && ez is long zoneMib && zoneMib == limit
                    && plan.Stats.TryGetValue(MergePolicyKey, out object? mp) && mp as string == AutoTracer.MergePolicy;
                if (!newTrace && metaSame)
                {
                    continue; // 상한·병합 정책이 계획을 만들 때와 같음: 반복하지 않음
                }

                string? stored = plan.Stats.TryGetValue(FingerprintKey, out object? fo) ? fo as string : null;
                string? fingerprint = _env.TraceFingerprint(raw);
                long cap = limit << 20;
                long merged = Math.Min(DistinctBlocks(_env.LoadTrace(raw, g.OriginRoot)) * HotSet.Block, cap);
                long grew = merged - hotBlocks * HotSet.Block;
                bool reordered = newTrace && stored is not null && fingerprint is not null && fingerprint != stored; // 크기가 그대로여도 블록 집합·순서가 바뀜
                bool shrink = hotBlocks * HotSet.Block > cap; // 상한을 낮춰 계획의 핫셋이 새 상한을 넘음: 메타만 적지 않고 같은 존 시작으로 줄여 다시 계획
                if (grew >= (long)s.Auto.TraceMergeMinMib << 20 || reordered || shrink)
                {
                    _tier?.RevertGame(g.Name, "trace grew"); // 링크는 새 재배치 전에 되돌림
                    _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                    _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                    _status.ResetForReplan(g.Name, ExtendDetail);
                    _log.Info(shrink
                        ? $"{g.Name}: plan holds {hotBlocks * HotSet.Block >> 20} MiB, above the zone limit {limit} MiB, plan set aside as {ExtendSuffix}, will plan again within the limit (same zone)"
                        : grew >= (long)s.Auto.TraceMergeMinMib << 20
                        ? $"{g.Name}: merged trace holds {grew >> 20} MiB more than the plan (run {AutoTracer.RunCount(g, _env.FileExists)}, zone limit {limit} MiB), plan set aside as {ExtendSuffix}, will extend the zone"
                        : $"{g.Name}: merged trace blocks or order changed since the plan (run {AutoTracer.RunCount(g, _env.FileExists)}), plan set aside as {ExtendSuffix}, will extend the zone");
                    continue;
                }

                // 다시 계획할 일은 아님: 옛 계획 (메타 없음) 이거나 상한이 바뀌었으면 지금 값을 계획에 적어 둠 (지문은 없을 때만: 옛 계획은 처음 한 번 기록만 하고 재계획하지 않음)
                bool stamp = !metaSame || stored is null && fingerprint is not null;
                if (stamp)
                {
                    plan.Stats[EffectiveZoneKey] = limit;
                    plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
                    if (stored is null && fingerprint is not null)
                    {
                        plan.Stats[FingerprintKey] = fingerprint;
                    }

                    plan.Save(g.Plan);
                    _log.Info($"{g.Name}: plan notes recorded (zone limit {limit} MiB, merge {AutoTracer.MergePolicy}), no replan needed");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: zone extension check failed, will retry on the next trace ({e.GetType().Name})");
            }
        }
    }

    // 가변 파일을 빼기 전에 만든 옛 계획 (stats 에 excluded_files 키가 없고 핫 순서에 가변 파일이 듦): 게임이 그 파일을 다시 써서 존이 흐트러져 보이므로
    // 서비스 실행당 한 번 가변 파일을 뺀 계획으로 다시 만듭니다. 확장 재계획과 같은 길 (.extend): 같은 존 시작에 맞추고, 유휴·일일 상한·바이트 예산 같은 재배치 게이트를 그대로 지납니다.
    private void ReplanLegacyVolatile(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            if (_status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || AutoTracer.PlanTracePath(g, _env.FileExists) is null || !_volatileChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || plan.Stats.ContainsKey(VolatileFiles.StatsKey) || !plan.Order.Any(o => VolatileFiles.Ignored(o.Path)))
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "volatile files left out"); // 링크는 새 재배치 전에 되돌림
                _ = _env.ArchiveJournal(g);
                _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                _status.ResetForReplan(g.Name, ExtendDetail);
                _log.Info($"{g.Name}: plan holds files the game rewrites by itself, plan set aside as {ExtendSuffix}, will plan again without them (same zone)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: volatile file check failed ({e.GetType().Name})");
            }
        }
    }

    // 트레이스 금지 게임은 트레이스를 다시 만들 수 없어, 프로필 보충 (Plan) 이 들어가기 전에 만든 계획은 스스로 다시 계획될 계기가 없습니다. 서비스 실행당 한 번 따져,
    // 계획에 보충 표지가 없고 트레이스가 덮지 못한 프로필 구간이 trace_merge_min_mib 이상이면 존 확장과 같은 길 (.extend) 로 다시 계획합니다: 같은 존 시작에 맞추고 이미 옮긴 블록은 제자리,
    // 유휴·일일 상한·바이트 예산 같은 재배치 게이트를 그대로 지납니다. 더 작으면 계획에 표지만 남깁니다. 표지는 재계획 직전에 이전 계획에도 적어, 확장이 막혀 되살려도 되풀이하지 않습니다.
    private void ReplanProfileSupplement(AsterSettings s)
    {
        if (!(s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0))
        {
            return;
        }

        foreach (GameSettings g in s.Games)
        {
            if (_status.Get(g.Name) is not { Stage: AutoStage.Done, RelocatedAt: not null } || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || !_supplementChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                if (_env.TraceBlocker(g) is not { } cheat || cheat == AutoEnv.AntiCheatUnknown || AutoTracer.PlanTracePath(g, _env.FileExists) is not { } tracePath)
                {
                    continue; // 탐지 실패는 확정이 아님: 이 실행에서는 건드리지 않음
                }

                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || plan.Stats.ContainsKey(SupplementKey) || plan.Stats.ContainsKey(SupplementCheckedKey) || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue;
                }

                (IReadOnlyList<TraceEvent> trace, _) = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot));
                (_, long bytes) = ProfileSupplement.Union(trace, MaxZoneMib(s, g) << 20, budget => _env.ProfileEvents(g, budget));
                plan.Stats[SupplementCheckedKey] = 1L;
                bool replan = bytes >= (long)s.Auto.TraceMergeMinMib << 20;
                if (replan)
                {
                    plan.Stats[SupplementReplanKey] = 1L; // 확장이 막혀 되살리면 NoteBlocked 가 0 으로 돌림
                }

                plan.Save(g.Plan);
                if (!replan)
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "engine profile added"); // 링크는 새 재배치 전에 되돌림
                _ = _env.ArchiveJournal(g);
                _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                _status.ResetForReplan(g.Name, ExtendDetail);
                _log.Info($"{g.Name}: plan lacks {(bytes + (1 << 20) - 1) >> 20} MiB of the engine profile (tracing is not allowed for this game), plan set aside as {ExtendSuffix}, will plan again with it (same zone)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: engine profile supplement check failed ({e.GetType().Name})");
            }
        }
    }

    // 하루 쓰기 예산 (바이트, 0 = 무제한): 명시하지 않았으면 SMR 로 보이는 디스크는 더 낮은 기본값.
    private long BudgetBytes(AsterSettings s, GameSettings g)
    {
        if (s.Auto is { MaxRelocateGibPerDaySet: true } a && a.MaxRelocateGibPerDay > AutoSettings.SmrMaxRelocateGibPerDay && IsSmr(g) && _smrBudgetLogged.TryAdd(g.Name, 0))
        {
            _log.Info($"{g.Name}: max_relocate_gib_per_day {a.MaxRelocateGibPerDay} is above the SMR default {AutoSettings.SmrMaxRelocateGibPerDay}; using the explicit value");
        }

        return (long)(s.Auto.MaxRelocateGibPerDaySet ? s.Auto.MaxRelocateGibPerDay : s.Auto.MaxRelocateGibFor(IsSmr(g))) << 30;
    }

    private bool IsSmr(GameSettings g)
    {
        try { return _env.IsSmr(DriveOf(g)); }
        catch (Exception) { return false; } // 질의 실패는 SMR 아님으로 본다
    }

    private long MaxZoneMib(AsterSettings s, GameSettings g) => EffectiveZoneMib(s, g, _env);

    /// 유효 존 상한 (MiB): max_zone_mib 가 auto 면 RAM·디스크 종류에 맞춘 값, 아니면 설정값. 트레이스 읽기 캡의 기본값도 이 값을 따릅니다.
    public static long EffectiveZoneMib(AsterSettings s, GameSettings g, AutoEnv env) => s.MaxZoneAuto ? ZoneSize.AutoMaxMib(env.PhysicalRamBytes(), SlowBus(g, env)) : s.MaxZoneMib;

    private static bool SlowBus(GameSettings g, AutoEnv env)
    {
        try { return env.IsSlowBus(DriveOf(g)); }
        catch (Exception) { return true; } // 질의 실패는 종류 미확인으로 본다
    }

    // 트레이스의 고유 (경로, 64 KiB 블록) 수.
    private static long DistinctBlocks(IEnumerable<TraceEvent> events)
    {
        var seen = new HashSet<(string, long)>();
        foreach (TraceEvent e in events)
        {
            for (long b = e.Offset / HotSet.Block; b <= (e.Offset + e.Length - 1) / HotSet.Block; b++)
            {
                seen.Add((e.Path, b));
            }
        }

        return seen.Count;
    }

    // 확장할 수 없음 (존 뒤가 비어 있지 않음, 기존 존이 제자리가 아님, 계획 실패 등): 전체 재배치로 떨어지지 않고 이전 계획을 되살려 끝난 상태로 둡니다.
    // 같은 트레이스로는 다시 시도하지 않음 (_extTried). 저널은 확장을 시작할 때 이미 옆으로 보관됐으므로 되살린 계획은 새 저널로 이어집니다.
    private void BlockExtension(GameSettings g, string reason)
    {
        _env.MoveAside(g.Plan + ExtendSuffix, g.Plan); // 새 계획이 이미 만들어졌으면 덮어씀
        NoteBlocked(g);
        _env.Touch(g.Plan); // 옛 수정 시각이면 재시작마다 트레이스가 계획보다 새롭다고 보고 확장을 되풀이함
        _ = _seeded.Remove(g.Name);
        _status.Complete(g.Name, g.Source, _env.Now(), counted: false, null);
        _log.Info($"{g.Name}: zone extension blocked: {reason}");
    }

    // 막힌 확장이 서비스를 다시 시작할 때마다 되풀이되지 않게, 되살린 계획에 지금의 존 상한·병합 정책을 적어 둡니다 (새 트레이스가 쌓이면 다시 따져 봄). 적지 못해도 막힘 처리는 그대로.
    private void NoteBlocked(GameSettings g)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            plan.Stats[EffectiveZoneKey] = MaxZoneMib(_settings(), g);
            plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
            if (plan.Stats.ContainsKey(SupplementReplanKey))
            {
                plan.Stats[SupplementReplanKey] = 0L; // 되살린 계획이 이후 일반 확장에서 보충 재계획으로 오인되지 않게
            }

            plan.Save(g.Plan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 계획 파일을 읽거나 쓸 수 없음: 다음 시작에 한 번 더 따져 볼 뿐
        }
    }

    // 확장 중 재배치가 실패함 (이동 실패·데이터 검사 실패·예외): 실패로 두지 않고 쓴 줄을 되돌린 뒤 이전 계획을 되살립니다. 확장 중이 아니면 거짓.
    private bool BlockFailedExtension(GameSettings g, string detail, Exception? e, int mark, CancellationToken token)
    {
        if (!IsExtending(g))
        {
            return false;
        }

        _log.Warn($"{g.Name}: zone extension failed ({detail}{(e is null ? string.Empty : ": " + e.GetType().Name)})");
        if (!Undo(g, mark, token))
        {
            Fail(g, $"extension revert incomplete ({detail})", e); // 되돌리기를 끝내지 못했으니 저널을 두고 실패로: 다음 실행이 되돌리기부터 다시 함
            return true;
        }

        try
        {
            _ = _env.ArchiveJournal(g); // 확장 계획의 저널이 남으면 복원한 이전 계획의 다음 적용이 다른 계획의 저널이라며 거부하므로 옆으로 보관
        }
        catch (Exception a) when (a is IOException or UnauthorizedAccessException)
        {
            Fail(g, $"extension journal could not be archived ({a.GetType().Name}; {detail})", a); // 보관 못 한 채 이전 계획을 되살리면 다음 적용이 거절되므로 실패로 두고 다시 시도
            return true;
        }

        BlockExtension(g, detail);
        return true;
    }

    // 확장 재배치가 끝남: 이전 계획을 일반 .stale 자리로 옮겨 확장 중 표시를 지우고, 늘어난 크기를 남깁니다.
    private void FinishExtension(GameSettings g, PlanFile plan)
    {
        string old = g.Plan + ExtendSuffix;
        long Used(PlanFile p) => p.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long clusters ? clusters : 0;
        long grew = 0;
        try
        {
            grew = (Used(plan) - Used(_env.LoadPlan(old))) * plan.ClusterBytes >> 20;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{g.Name}: previous plan unreadable, zone growth not logged ({e.GetType().Name})");
        }

        _env.MoveAside(old, g.Plan + StaleSuffix);
        _log.Info($"{g.Name}: zone extended by {grew} MiB (run {AutoTracer.RunCount(g, _env.FileExists)})");
    }

    /// 업데이트를 감지해 재계획 상태 (discovered, detail 이 ReplanDetail) 로 되돌린 게임이 아직 기다려야 하면 남은 시간, 아니면 null.
    /// 기준 시각은 그 단계에 들어간 때 (auto_status 의 at) 라 서비스를 다시 시작해도 이어집니다. post_update_delay_min 이 0 이면 지연 없음.
    public static TimeSpan? PostUpdateWait(AutoStatus status, AutoSettings auto, string game, DateTimeOffset now)
    {
        if (auto.PostUpdateDelayMin <= 0 || Replanning(status, game) is not { } e)
        {
            return null;
        }

        TimeSpan left = e.At + TimeSpan.FromMinutes(auto.PostUpdateDelayMin) - now;
        return left > TimeSpan.Zero ? left : null;
    }

    internal static AutoGameEntry? Replanning(AutoStatus status, string game) =>
        status.Get(game) is { Stage: AutoStage.Discovered, Detail: { } detail } e && detail.StartsWith(ReplanDetail, StringComparison.Ordinal) ? e : null;

    private static string DriveOf(GameSettings g) => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();

    public static string PostUpdateLine(string game, int minutes) => $"{game}: waiting {minutes} min after update (drive may still be reorganizing)";

    // 틱 스레드에서: 지연 중이면 true (지연 한 번에 로그는 한 줄, 끝나면 다음 지연 때 다시).
    private bool PostUpdateDelayed(GameSettings g, AsterSettings s)
    {
        // probe 모드이고 이 드라이브에 쓰기 전 기준값이 있으면 같은 회복 판정을, 없으면 (업데이트는 우리가 쓴 게 아님) 고정 분을 씀.
        bool? probed = s.Auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && Replanning(_status, g.Name) is { } e
            ? _recovery.WatchUpdate(DriveOf(g), e.At, TimeSpan.FromMinutes(s.Auto.RecoveryMaxMin)) : null;
        if (!(probed ?? PostUpdateWait(_status, s.Auto, g.Name, _env.Now()) is not null))
        {
            _delayLogged.Remove(g.Name);
            return false;
        }

        if (_delayLogged.Add(g.Name))
        {
            _log.Info(PostUpdateLine(g.Name, s.Auto.PostUpdateDelayMin));
        }

        return true;
    }

    /// 이 안에 다시 검사되지 않으면 유휴 연속 기록을 버립니다 (재적용 검사는 몇 분마다라 연속을 보장하지 못함: 같은 막힘 없이 두 번 연달아 통과해야 유휴).
    private static readonly TimeSpan ReapplyIdleGap = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, (IdleTracker Tracker, DateTimeOffset Last)> _reapplyIdle = new(StringComparer.Ordinal); // 재적용 틱 스레드에서만 씀

    /// 정기 재적용이 자동 재배치와 같은 게이트를 지나는지: 막는 이유 (같은 디스크 등록 게임 실행, 회복 중, 시스템 HDD, 업데이트 직후, 배터리, 디스크 바쁨, 일일 상한·예산, 유휴 시간) 또는 null.
    /// 재적용 루프 스레드에서만 부릅니다 (재배치 틱과 같은 스레드). 하루 예산은 이동 예정 바이트를 모르므로 한 바이트라도 남아야 통과 (그날 첫 이동은 허용).
    public ReapplyVerdict? ReapplyBlock(GameSettings g)
    {
        AsterSettings s = _settings();
        DateTimeOffset now = _env.Now();
        if (!_reapplyIdle.TryGetValue(g.Name, out (IdleTracker Tracker, DateTimeOffset Last) idle) || now - idle.Last > ReapplyIdleGap)
        {
            idle = (new IdleTracker(), now);
        }

        _reapplyIdle[g.Name] = (idle.Tracker, now);
        ReapplyVerdict? why = ReapplyBlockedBy(g, s);
        if (why is not null)
        {
            idle.Tracker.Reset();
            return why;
        }

        return idle.Tracker.Sample(now, true, s.Auto.IdleWindow) ? null : ReapplyVerdict.NotIdle;
    }

    private ReapplyVerdict? ReapplyBlockedBy(GameSettings g, AsterSettings s)
    {
        // 확장 중이거나 자동 재배치를 끝내지 못한 게임의 계획에는 Zone.StartLcn 이 있어 적용됨으로 보이므로 막음 (항목 없음·Skipped 는 제외, 자동 재배치를 끈 설정이면 남은 상태가 있어도 기다릴 일이 없어 제외).
        if (IsExtending(g) || (s.Auto.Enabled && s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0 && _status.Get(g.Name) is { RelocatedAt: null, Stage: not AutoStage.Skipped }))
        {
            return ReapplyVerdict.NotApplied;
        }

        if (_env.AnyRunning([.. SameDriveGames(s.Games, [g], _env.SameDisk).SelectMany(x => x.ExeNames).Concat(g.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)]))
        {
            return ReapplyVerdict.GameRunning;
        }

        if (_recovery.IsRelocationHeld(DriveOf(g)))
        {
            return ReapplyVerdict.Recovering;
        }

        if (!_env.Capabilities(g.OriginRoot).CanRelocate)
        {
            return ReapplyVerdict.NotRelocatable;
        }

        if (PostUpdateDelayed(g, s))
        {
            return ReapplyVerdict.PostUpdateDelay;
        }

        if (!_env.PowerOk())
        {
            return ReapplyVerdict.OnBattery;
        }

        if (_env.DiskBytesPerSecond(DriveOf(g)) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
        {
            return ReapplyVerdict.DiskBusy;
        }

        long budget = BudgetBytes(s, g); // 0 = 무제한
        bool countHeld = _status.RelocationHeldUntil() is not null || (s.Auto.MaxRelocationsPerDay > 0 && _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay);
        bool bytesHeld = budget > 0 && _status.RelocationBytesWithin(DailyWindow) is > 0 and long used && used >= budget;
        return countHeld || bytesHeld ? ReapplyVerdict.DailyLimit : null;
    }

    /// 정기 재적용이 쓸 옵션: 자동 재배치와 같은 회당 쓰기 상한·청크·검증·우선순위에 하루 남은 예산을 더해 제한합니다.
    public ApplyOptions ReapplyOptionsFor(GameSettings g)
    {
        AsterSettings s = _settings();
        ApplyOptions run = BaseApplyOptions(s, g);
        long budget = BudgetBytes(s, g); // 0 = 무제한
        run = run with { SaveTo = g.Plan };
        return budget > 0 ? run with { WriteCap = Math.Max(0, Math.Min(run.WriteCap, budget - _status.RelocationBytesWithin(DailyWindow))) } : run;
    }

    private ApplyOptions BaseApplyOptions(AsterSettings s, GameSettings g)
    {
        bool smr = IsSmr(g);
        return new ApplyOptions(Execute: true, ChunkBytes: (long)s.Auto.RelocateChunkMib << 20, LowIoPriority: s.Auto.RelocateIoPriority == AutoSettings.IoLow, Verify: s.Auto.RelocateVerify, Order: RelocateOrder.Resolve(s.Auto.RelocateOrder), Parallel: s.Auto.ParallelFor(_env.IsSlowBus(DriveOf(g)), smr),
            WriteCap: AutoSettings.WriteCapFor(smr), Reserved: _env.Reserved(g));
    }

    /// 정기 재적용이 시작됨: 자동 재배치처럼 실행 전에 예정 바이트로 일일 상한·예산 집계에 넣습니다.
    public void NoteReapplyStarted(GameSettings g, long plannedBytes) => _status.BeginRelocation(_env.Now(), plannedBytes);

    /// 정기 재적용이 끝났음 (예외로 끝났으면 0 바이트): 집계를 실제 이동량으로 바로잡고, 쓴 바이트가 있으면 자동 재배치와 같이 드라이브 회복 추적을 시작합니다.
    public void NoteReapplied(GameSettings g, long movedBytes)
    {
        _status.SetLastRelocationBytes(movedBytes);
        BeginRecovery(g, _settings().Auto, movedBytes, null);
    }

    /// 배치 풀림 경고를 낼 만한지: 재배치를 끝낸 적이 있고, 확장 중이 아니며, 직전 실행이 쓰기 상한으로 멈춘 부분 적용이 아닐 때.
    public bool LayoutWarnAllowed(GameSettings g)
        => _status.Get(g.Name) is { RelocatedAt: not null } e && !IsExtending(g) && e.Detail != WriteLimitDetail;

    private bool NeedsWork(GameSettings g, bool relocateOn)
    {
        AutoGameEntry? e = _status.Get(g.Name);
        // 실패한 게임은 서비스 실행당 한 번 다시 시도하고 (저장된 실패 시각 e.At 은 트레이스가 먼저 쓰여 있으면 영영 못 넘으므로 쓰지 않음), 그 뒤로는 트레이스가 새로 쓰였을 때만 다시 시도합니다.
        // 이번 실행의 실패 시각은 Fail 이 남김 (같은 트레이스로는 되풀이하지 않음).
        DateTimeOffset failedAt = _planFailedAt.GetValueOrDefault(g.Name, DateTimeOffset.MinValue);
        bool reopened = e is { Stage: AutoStage.Failed, RelocatedAt: null }
            && (AutoTracer.PlanTracePath(g, _env.FileExists) is { } latest && _env.FileWriteTime(latest) is { } written && written > failedAt
                || e.Detail?.StartsWith(RecoveryStoppedDetail, StringComparison.Ordinal) == true && _env.Now() - failedAt >= StoppedRecheck); // 같은 사유로 멈춘 복구는 새 트레이스가 없어도 (트레이스 금지 게임) StoppedRecheck 마다 다시 평가
        bool stageOpen = reopened || e is null || e.Stage is not (AutoStage.Done or AutoStage.Failed)
            && !(e.Stage == AutoStage.Skipped && !relocateOn)
            && e.RelocatedAt is null;
        if (!stageOpen || AutoTracer.PlanTracePath(g, _env.FileExists) is null)
        {
            return false;
        }

        if (_env.Capabilities(g.OriginRoot) is { CanPrewarm: false } caps) // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (수동 등록 게임도 같음). 시스템 HDD 는 계획·예열까지만 (TickCore 가 재배치 앞에서 거름)
        {
            Note(g, AutoStage.Skipped, caps.Reason!);
            return false;
        }

        return true;
    }

    // 유휴가 아닌 이유 (유휴면 null): 재배치 대상 드라이브의 등록 게임이 돌거나, 배터리이거나, 대상 드라이브에 디스크 활동이 있음.
    // CPU 사용률은 보지 않습니다: HDD 이동은 Low I/O 우선순위로 대상 드라이브에만 영향을 주므로 백그라운드 작업이 있는 PC 도 재배치해야 합니다.
    private string? BusyReason(AsterSettings s, List<GameSettings> ready)
    {
        if (_env.AnyRunning([.. SameDriveGames(s.Games, ready, _env.SameDisk).SelectMany(g => g.ExeNames)]))
        {
            return "game running";
        }

        if (!_env.PowerOk())
        {
            return "on battery";
        }

        bool prewarming = _prewarm is { } warm && ready.Any(g => warm.StreamingOnDisk(g.Name)); // 같은 디스크의 예열 스트림과 재배치 쓰기가 겹치지 않게
        if (prewarming != _prewarmHeld)
        {
            _prewarmHeld = prewarming;
            if (prewarming)
            {
                _log.Info($"auto relocation waits: {StartupPrewarmBusy}"); // 한 번만: 읽는 동안 틱마다 남기지 않음
            }
        }

        if (prewarming)
        {
            return StartupPrewarmBusy;
        }

        bool diskBusy = ready.Select(g => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()).Distinct()
            .Any(letter => _env.DiskBytesPerSecond(letter) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond);
        return diskBusy ? "disk busy" : null;
    }

    // 유휴가 아닌 채로 6 시간을 넘게 기다리면 그때 거짓이던 이유를 한 번 남깁니다. 유휴가 되면 다시 셉니다.
    private void NoteWait(string? busy)
    {
        if (busy is null)
        {
            ResetWait();
            return;
        }

        DateTimeOffset now = _env.Now();
        _waitSince ??= now;
        if (!_waitNoted && now - _waitSince.Value >= LongWait)
        {
            _waitNoted = true;
            _log.Info($"auto waiting for idle for {(int)(now - _waitSince.Value).TotalHours} h ({busy})");
        }
    }

    private void ResetWait()
    {
        _waitSince = null;
        _waitNoted = false;
    }

    // 계획 만들기: 탐색용 큰 존으로 핫 바이트를 재고, 그 크기에 맞는 존으로 다시 계획해 계획과 선읽기 파일을 저장합니다.
    // reuseZone: 업데이트 전 계획 (.stale) 의 존 시작이 있으면 그 자리에 맞춰 계획합니다 (비어 있지 않으면 Relocate 가 reuseZone 없이 다시 부름).
    private bool Plan(GameSettings g, AsterSettings s, bool reuseZone = true)
    {
        Note(g, AutoStage.Relocating, "planning");
        try
        {
            string tracePath = AutoTracer.PlanTracePath(g, _env.FileExists) ?? AutoTracer.RawTracePath(g);
            bool boot = tracePath == AutoTracer.BootTracePath(g);
            bool prefetch = boot && _env.TraceSource(tracePath) == PlanSourcePrefetch;
            (IReadOnlyList<TraceEvent> trace, JObject excluded) = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot));
            long supplementBytes = 0;
            string? cheat = _env.TraceBlocker(g);
            if (cheat is not null)
            {
                (trace, supplementBytes) = ProfileSupplement.Union(trace, MaxZoneMib(s, g) << 20, budget => _env.ProfileEvents(g, budget)); // 트레이스를 만들 수 없는 게임: 트레이스가 덮지 못한 엔진 프로필 구간을 존 상한 안에서 보충
            }

            IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = [prefetch ? RangesByLcn(trace) : trace];
            PlanFile probe = _env.MakePlan(sessions, g.OriginRoot, ZoneSize.ProbeMib);
            long hot = probe.Stats.TryGetValue("hot_bytes", out object? h) && h is long bytes ? bytes : 0;
            if (hot == 0)
            {
                PlanFailed(g, "no hot data", null);
                return false;
            }

            long zoneMib = Math.Min(ZoneSize.ZoneMib(hot), MaxZoneMib(s, g));
            bool extending = IsExtending(g);
            if (extending && hot > zoneMib << 20)
            {
                _log.Info($"{g.Name}: zone extension overflow {(hot >> 20) - zoneMib} MiB dropped (max zone {zoneMib} MiB), the latest blocks of the merged trace are left out");
            }

            bool followResidual = _env.ReplanResidual is not null && PreviousIsResidual(g); // 이전 계획 (.extend 또는 .stale) 이 잔여 계획이면 단일 존 접두부로 가지 않고 모든 이전 task 를 승계
            (long? start, long usedClusters) = reuseZone && !followResidual && _env.MakePlanAt is not null ? PreviousZone(g) : (null, 0);
            if (extending && start is null && reuseZone && !followResidual)
            {
                BlockExtension(g, "previous zone start unknown");
                return false;
            }

            PlanFile plan;
            try
            {
                plan = start is { } lcn ? _env.MakePlanAt!(sessions, g.OriginRoot, zoneMib, lcn, usedClusters) : _env.MakePlan(sessions, g.OriginRoot, zoneMib);
            }
            catch (PyValueException e) when (start is not null && !extending)
            {
                // 존 안의 핫 파일이 접근 순서와 반대로 놓이면 (zone overlaps hot data in a cycle) 그 자리에서는 계획할 수 없음: 확장이 아니면 새 존을 찾음
                _log.Info($"{g.Name}: previous zone cannot be planned in place ({FirstLine(e.Message)}), looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (start is not null && ZoneBlockedByOwnData(plan, extending ? usedClusters : 0))
            {
                if (extending)
                {
                    BlockExtension(g, "the region after the zone holds data the plan does not move");
                    return false;
                }

                _log.Info($"{g.Name}: previous zone holds data this plan does not move, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (start is not null && ZoneReserved(g, plan, extending ? usedClusters : 0))
            {
                if (extending)
                {
                    BlockExtension(g, "the region after the zone is reserved by another game plan");
                    return false;
                }

                _log.Info($"{g.Name}: previous zone overlaps another game plan, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (extending && start is not null && ExtensionRegionInUse(g, plan))
            {
                if (IsSupplementExtension(g))
                {
                    BlockExtension(g, "the region after the zone is in use, engine profile not added"); // 이미 옮긴 블록을 다시 옮기는 전체 재배치로 가지 않음
                    return false;
                }

                _log.Info($"{g.Name}: the region after the zone is in use, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            _ = start is null ? _seeded.Remove(g.Name) : _seeded.Add(g.Name);
            if (boot)
            {
                plan.Stats.Add("source", prefetch ? PlanSourcePrefetch : PlanSourceBoot);
            }

            if (!boot)
            {
                plan.Stats[EffectiveZoneKey] = MaxZoneMib(s, g);
                plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
                if (_env.TraceFingerprint(tracePath) is { } fp)
                {
                    plan.Stats[FingerprintKey] = fp;
                }
            }

            if (supplementBytes > 0)
            {
                long supplementMib = (supplementBytes + (1 << 20) - 1) >> 20;
                plan.Stats[SupplementKey] = supplementMib;
                _log.Info($"{g.Name}: engine profile added {supplementMib} MiB the trace did not cover");
            }
            else if (cheat is not null && cheat != AutoEnv.AntiCheatUnknown)
            {
                plan.Stats[SupplementCheckedKey] = 1L; // 보충할 것이 없었음: 나중에 다시 따지지 않게 표지
            }

            if (excluded.Count > 0)
            {
                plan.Stats.Add(VolatileFiles.StatsKey, excluded);
                _log.Info($"{g.Name}: {excluded.Count} volatile file(s) left out of the plan");
            }

            if (extending && start is null && !followResidual)
            {
                _ = _env.ArchiveJournal(g); // 새 존 계획은 확장 계획의 저널과 맞지 않음
            }

            if ((followResidual || !extending && _env.JournalRows(g) > 0) && _env.ReplanResidual is { } residual)
            {
                // 저널에 행이 있음 (부분 적용) 이거나 이전 계획이 잔여 계획인 확장: 활성 계획을 새 계획으로 바로 덮지 않고, 이미 옮긴 조각을 지키는 잔여 재계획으로 저널 보관과 함께 바꿈
                plan = InheritHeld(g, plan); // 아직 읽지 못하는 보류 파일은 후보에서 빠졌어도 계속 보류로 남김
                try
                {
                    _ = residual(g, plan, ResidualOlds(g), sessions);
                }
                catch (PlanRejectedException e) when (IsTemporaryRejection(e))
                {
                    HoldTemporary(g, e); // 아무것도 바꾸지 않았음: 일시 보류 뒤 같은 입력으로 다시
                    return false;
                }
                catch (PlanRejectedException e) when (followResidual && extending)
                {
                    BlockExtension(g, "residual replan rejected: " + FirstLine(e.Message)); // 이미 옮긴 조각을 다시 옮기는 계획으로 가지 않고 이전 계획을 되살림
                    return false;
                }
                catch (PlanRejectedException e)
                {
                    HoldRecovery(g, e.Message);
                    return false;
                }
                catch (Exception e) when (IsTransient(e))
                {
                    HoldTransient(g, e); // 저널 잠금·읽기 실패: 아무것도 바꾸지 않았음, 곧 다시
                    return false;
                }

                _ = _seeded.Remove(g.Name);
                _log.Info($"{g.Name}: partially applied plan replanned, the parts already moved are kept (earlier journal archived)");
            }
            else
            {
                _env.SavePlan(plan, sessions, g.Plan);
            }

            if (extending && start is null)
            {
                _env.MoveAside(g.Plan + ExtendSuffix, g.Plan + StaleSuffix); // 마지막에 치움: 앞에서 실패하면 확장 중 표시가 남아 BlockExtension 이 이전 계획을 되살림
            }

            _log.Info($"{g.Name}: auto plan made, hot {hot >> 20} MiB, zone {zoneMib} MiB");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlanFailed(g, "plan failed: " + e.GetType().Name, e);
            return false;
        }
    }

    // 가변 파일 (이름 규칙, 트레이스에서 run 사이에 바뀌었거나 쓰기로 열렸다고 표시된 파일) 을 계획 입력에서 뺍니다. 뺀 파일과 사유는 계획 stats 에 남김.
    private (IReadOnlyList<TraceEvent> Kept, JObject Excluded) DropVolatile(GameSettings g, IReadOnlyList<TraceEvent> trace)
    {
        IReadOnlyDictionary<string, string> marked = _env.LoadVolatile(AutoTracer.VolatilePath(g));
        var excluded = new JObject();
        foreach (string path in trace.Select(t => t.Path).Distinct(StringComparer.Ordinal))
        {
            if ((VolatileFiles.NameReason(path) ?? (marked.TryGetValue(path, out string? why) ? why : null)) is { } reason)
            {
                excluded.Add(path, reason);
            }
        }

        return excluded.Count == 0 ? (trace, excluded) : ([.. trace.Where(t => !excluded.ContainsKey(t.Path))], excluded);
    }

    // 이전 계획들 (옛 계획과 읽히는 활성 계획) 에 보류로 남은 파일 중 지금도 extent 를 읽지 못하는 파일의 핫 범위와 지문을 새 후보에 승계합니다:
    // 후보 생성은 읽지 못하는 파일을 Order 에서 빼므로, 그대로 두면 잔여 재계획이 그 파일을 보지 못해 held_files·held_targets (이미 옮긴 조각의 이전 목표) 가 사라짐.
    // 조회가 성공한 파일은 승계하지 않음 (후보에 없으면 트레이스가 더는 원하지 않는 파일, 있으면 보류가 아니라 정상 처리).
    private PlanFile InheritHeld(GameSettings g, PlanFile candidate)
    {
        List<PlanFile> previous = ResidualOlds(g);
        try
        {
            if (_env.FileExists(g.Plan))
            {
                previous.Add(_env.LoadPlan(g.Plan));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 읽히지 않는 활성 계획은 승계 근거에서만 빠짐
        }

        var have = candidate.Order.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
        var order = candidate.Order.ToList();
        var prints = candidate.Fingerprints.ToList();
        foreach (PlanFile old in previous.Where(p => p.Stats.TryGetValue(ResidualPlanner.HeldKey, out object? h) && h is JObject))
        {
            foreach (string path in ((JObject)old.Stats[ResidualPlanner.HeldKey]!).Select(kv => kv.Key).Where(p => !have.Contains(p) && _env.Extents(p) is null))
            {
                order.AddRange(old.Order.Where(o => o.Path == path));
                prints.AddRange(old.Fingerprints.Where(f => f.Path == path));
                _ = have.Add(path);
            }
        }

        return order.Count == candidate.Order.Count ? candidate : candidate with { Order = order, Fingerprints = prints };
    }

    // 확장 중에 계획이 실패하면 실패 상태로 두지 않고 이전 계획을 되살립니다. 확장이 아니어도 낡은 계획 (.stale) 이 있으면 같은 규칙: 되살리고 done (stale plan) 을 유지합니다.
    private void PlanFailed(GameSettings g, string detail, Exception? e)
    {
        if (IsExtending(g))
        {
            BlockExtension(g, e is null ? detail : $"{detail}: {FirstLine(e.Message)}"); // 예외 형식만으로는 원인을 알 수 없음
        }
        else if (!RestoreStalePlan(g, e is null ? detail : $"{detail}: {FirstLine(e.Message)}"))
        {
            Fail(g, detail, e);
        }
    }

    // 활성 계획을 읽어 보류 파일이 있으면 그 계획, 없거나 읽지 못하면 null.
    private PlanFile? LoadHeldPlan(GameSettings g)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            return HeldFiles(plan) > 0 ? plan : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private bool RestoreStalePlan(GameSettings g, string why)
    {
        string stale = g.Plan + StaleSuffix;
        if (_env.FileExists(g.Plan) || !_env.FileExists(stale))
        {
            return false;
        }

        try
        {
            _env.MoveAside(stale, g.Plan);
        }
        catch (Exception m) when (m is IOException or UnauthorizedAccessException)
        {
            return false; // 되살리지 못하면 기존처럼 실패로 기록
        }

        _planFailedAt[g.Name] = _env.Now();
        _ = _seeded.Remove(g.Name);
        if (LoadHeldPlan(g) is { } held && HoldUnreadable(g, held))
        {
            _log.Warn($"{g.Name}: replan failed ({why}), previous plan restored with held files, not marked done");
            return true; // 읽지 못한 파일이 보류로 남은 미완료 계획은 완료로 복원하지 않음: UnreadableRetry 뒤 다시 계획
        }

        _status.Complete(g.Name, g.Source, _env.Now(), counted: false, StalePlanDetail);
        _log.Warn($"{g.Name}: replan failed ({why}), previous plan restored, state stays done ({StalePlanDetail})");
        return true;
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    // boot 트레이스 (가져온 프로필 또는 프리페치 기록) 로 만든 계획인가.
    private static bool IsBootPlan(PlanFile plan) => plan.Stats.TryGetValue("source", out object? v) && v is PlanSourceBoot or PlanSourcePrefetch;

    // 재배치를 마친 게임의 표시: 프리페치 기록이면 "relocated (prefetch)", 프로필이면 "relocated (profile)", 원시 트레이스면 없음.
    private static string? RelocatedTag(PlanFile plan) =>
        !IsBootPlan(plan) ? null : plan.Stats["source"] is PlanSourcePrefetch ? RelocatedPrefetch : RelocatedProfile;

    // 완료 단계의 detail 은 예열 표시를 새로 붙일 때 비우되, 프로필·프리페치 기록으로 재배치했다는 표시는 남깁니다.
    private static string? DoneDetail(AutoGameEntry e) => e.Stage != AutoStage.Done ? e.Detail
        : e.Detail?.Contains(RelocatedPrefetch, StringComparison.Ordinal) == true ? RelocatedPrefetch
        : e.Detail?.Contains(RelocatedProfile, StringComparison.Ordinal) == true ? RelocatedProfile : null;

    // 프리페치 계획의 입력: 트레이스가 참조한 범위의 앞뒤로 PrefetchPadBytes 를 더해 (겹치면 합치고 파일 크기로 자름), 파일의 물리 순서 (첫 extent 의 LCN, 오름차순) 와 오프셋 순으로.
    // shortcut: 프리페치 순서가 실제 접근 순서와 얼마나 맞는지 아직 재지 않아서 (τ 검증 전) 접근 순서를 따르지 않고 물리 순서로 보수적으로 정렬함, 검증되면 프리페치 순서로 바꿈.
    private List<TraceEvent> RangesByLcn(IReadOnlyList<TraceEvent> trace)
    {
        var rows = new List<(string Path, long Lcn, long Start, long End)>();
        foreach (IGrouping<string, TraceEvent> file in trace.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            long size = _env.Stat(file.Key).Size;
            if (size <= 0 || _env.Extents(file.Key) is not { Count: > 0 } extents)
            {
                continue;
            }

            long lcn = extents.Min(x => x.Lcn);
            (long Start, long End)? open = null;
            foreach ((long Start, long End) r in file.Select(e => (Start: Math.Max(0, e.Offset - PrefetchPadBytes), End: Math.Min(size, e.Offset + e.Length + PrefetchPadBytes))).Where(r => r.Start < r.End).OrderBy(r => r.Start))
            {
                if (open is { } o && r.Start <= o.End)
                {
                    open = (o.Start, Math.Max(o.End, r.End));
                    continue;
                }

                if (open is { } done)
                {
                    rows.Add((file.Key, lcn, done.Start, done.End));
                }

                open = r;
            }

            if (open is { } last)
            {
                rows.Add((file.Key, lcn, last.Start, last.End));
            }
        }

        return rows.OrderBy(r => r.Lcn).ThenBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Start).SelectMany(r => Chunks(r.Path, r.Start, r.End)).ToList();
    }

    // 트레이스 이벤트 한 개의 길이는 TraceEvents.MaxLength 이하여야 하므로 큰 범위는 그 길이로 잘라 이어 붙입니다.
    private static IEnumerable<TraceEvent> Chunks(string path, long start, long end)
    {
        for (long at = start; at < end; at += TraceEvents.MaxLength)
        {
            yield return new TraceEvent(path, at, Math.Min(TraceEvents.MaxLength, end - at));
        }
    }

    // 확장 계획이 덧붙일 구간 (옮길 목표) 이 다른 파일에 쓰이고 있는가: 적용 때의 zone_not_free 검사를 미리 합니다.
    // 비트맵을 못 읽거나 판정에 실패하면 거짓: 확장을 시도하고, 적용 때 zone_not_free 로 거부되면 Relocate 가 새 존으로 다시 계획합니다.
    private bool ExtensionRegionInUse(GameSettings g, PlanFile plan)
    {
        try
        {
            if (_env.Bitmap(DriveOf(g)) is { } bitmap)
            {
                PlanGuards.CheckZoneFree(plan, bitmap, _env.Extents);
            }

            return false;
        }
        catch (PlanRejectedException e) when (IsZoneNotFree(e))
        {
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    private const string RecoveryStoppedDetail = "recovery stopped, rejected again for the same reason";

    private static bool IsZoneNotFree(PlanRejectedException e) => e.Message.StartsWith("zone_not_free", StringComparison.Ordinal);

    private const string PlanChangedWhileWaiting = "plan changed while waiting for the zone lock";

    private const string ZoneLockBusyDetail = "another apply or plan switch holds the zone lock, will retry";

    private static bool IsLockBusy(PlanRejectedException e) => e.Message.StartsWith(DataLock.BusyMessage, StringComparison.Ordinal);

    // 시간이 지나거나 환경이 바뀌면 풀리는 거절: 잠금 busy, 잠금 대기 중 계획 변경, 미검증 파일 읽기 불가, 빈 구간 부족. 검증 오류 ("unverified journal entry changed" 등) 와 달리 실패로 멈추지 않고 일시 보류 뒤 다시 시도합니다.
    private static bool IsTemporaryRejection(PlanRejectedException e) =>
        IsLockBusy(e) || e.Message.StartsWith(PlanChangedWhileWaiting, StringComparison.Ordinal)
        || e.Message.StartsWith(Applier.UnverifiedUnreadable, StringComparison.Ordinal) || e.Message.StartsWith(NoFreeRun, StringComparison.Ordinal);

    private const string NoFreeRun = "no free run of";

    /// 빈 구간이 모자라 다시 시도하기까지 쉬는 시간 (읽기 불가·잠금보다 길게: 비트맵과 미검증 표본을 매번 다시 읽지 않게).
    public static readonly TimeSpan ResourceRetry = TimeSpan.FromMinutes(10);

    /// 재개 조건 없이 멈춘 복구 (같은 사유 거절의 반복) 를 다시 평가하기까지 쉬는 시간.
    public static readonly TimeSpan StoppedRecheck = TimeSpan.FromHours(1);

    // 서비스 적용 함수: 잠금을 못 잡으면 busy 보고서 (Stopped = zone_lock_busy) 이고, 잠금을 기다리는 사이 활성 계획이 바뀌어 거절돼도 같은 보고서로 돌려줍니다: 둘 다 실패가 아니라 다음 틱에 다시.
    private ApplyReport ApplyOrDefer(GameSettings g, PlanFile plan, ApplyOptions options, CancellationToken token)
    {
        try
        {
            return _env.Apply(g, plan, options, token);
        }
        catch (PlanRejectedException e) when (e.Message.StartsWith(PlanChangedWhileWaiting, StringComparison.Ordinal))
        {
            return new ApplyReport { Stopped = DataLock.BusyReason };
        }
    }

    private void HoldTemporary(GameSettings g, PlanRejectedException e)
    {
        DateTimeOffset until = _env.Now() + (e.Message.StartsWith(NoFreeRun, StringComparison.Ordinal) ? ResourceRetry : TransientRetry);
        string text = $"temporarily blocked ({FirstLine(e.Message)}), retrying after {until.ToUniversalTime():HH:mm} UTC";
        _transientHold[g.Name] = (until, text);
        Note(g, AutoStage.WaitingIdle, text);
        _log.Warn($"{g.Name}: {FirstLine(e.Message)}; nothing was changed, will retry");
    }

    // 잠금·권한·일시 읽기 실패처럼 시간이 지나면 풀릴 수 있는 오류 (계획 거절과 달리 다시 시도하면 됨).
    private static bool IsTransient(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException or PyValueException;

    /// 일시 오류 뒤 다시 시도하기까지 쉬는 시간.
    public static readonly TimeSpan TransientRetry = TimeSpan.FromMinutes(2);

    /// 읽지 못한 파일이 보류로 남은 계획을 다시 계획해 보기까지 쉬는 시간.
    public static readonly TimeSpan UnreadableRetry = TimeSpan.FromHours(1);

    private void HoldTransient(GameSettings g, Exception e)
    {
        DateTimeOffset until = _env.Now() + TransientRetry;
        string text = $"temporary {e.GetType().Name}, retrying after {until.ToUniversalTime():HH:mm} UTC";
        _transientHold[g.Name] = (until, text);
        Note(g, AutoStage.WaitingIdle, text);
        _log.Warn($"{g.Name}: {FirstLine(e.Message)} ({e.GetType().Name}); nothing was changed, will retry");
    }

    // 계획에 보류로 남은 (task 가 없는) 파일 수.
    private static int HeldFiles(PlanFile plan) => plan.Stats.TryGetValue(ResidualPlanner.HeldKey, out object? h) && h is JObject held ? held.Count : 0;

    // 읽지 못한 파일이 보류로 남았으면 완료로 확정하지 않고 UnreadableRetry 뒤 다시 계획합니다 (그때 읽히면 task 가 생김). 보류가 없으면 거짓.
    private bool HoldUnreadable(GameSettings g, PlanFile plan)
    {
        int count = HeldFiles(plan);
        if (count == 0)
        {
            return false;
        }

        DateTimeOffset until = _env.Now() + UnreadableRetry;
        string text = $"{count} file(s) unreadable, planning again after {until.ToUniversalTime():HH:mm} UTC";
        _unreadableHold[g.Name] = (until, text);
        Note(g, AutoStage.WaitingIdle, text);
        _log.Warn($"{g.Name}: {count} file(s) could not be read, the layout is not confirmed, will plan again later");
        return true;
    }

    // 보류가 끝나 계획을 낡은 것으로 치우고 다시 계획 (이미 옮긴 조각은 잔여 재계획이 지킴). 계획을 치우지 못하면 거짓 (일시 오류로 보류 다시).
    private bool RetryUnreadable(GameSettings g)
    {
        try
        {
            if (_env.FileExists(g.Plan))
            {
                SetAsideForReplan(g);
            }

            return true;
        }
        catch (Exception e) when (IsTransient(e))
        {
            HoldTransient(g, e);
            return false;
        }
    }

    // 이전 계획 (확장 중이면 .extend, 아니면 .stale) 이 잔여 계획인가: 잔여 재계획이 만든 표지가 있거나, 존 밖에 고정된 task 가 있음.
    private bool PreviousIsResidual(GameSettings g)
    {
        try
        {
            string previous = g.Plan + (IsExtending(g) ? ExtendSuffix : StaleSuffix);
            if (!_env.FileExists(previous))
            {
                return false;
            }

            PlanFile old = _env.LoadPlan(previous);
            return old.Stats.TryGetValue("residual", out object? r) && r is 1L
                || old.Zone.StartLcn is { } zone && old.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long used && old.Tasks.Any(t => t.TargetLcn < zone || t.TargetLcn + t.Clusters > zone + used);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false; // 읽을 수 없는 옛 계획은 힌트일 뿐
        }
    }

    private static bool IsJournalMismatch(PlanRejectedException e) => e.Message.StartsWith("journal belongs to a different plan", StringComparison.Ordinal);

    /// 점유 충돌 뒤 잔여 재계획이 다시 막혔을 때 쉬는 시간 (재시작해도 풀리지 않음).
    public static readonly TimeSpan RecoveryHold = TimeSpan.FromMinutes(30);

    private static readonly Regex RejectedLcn = new(@"LCN (\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static long? LcnIn(string message) => RejectedLcn.Match(message) is { Success: true } m ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private DateTimeOffset? RecoveryHeldUntil(GameSettings g) => _status.Recovery(g.Name) is { NextAllowed: { } until } && until > _env.Now() ? until : null;

    private static string HoldText(DateTimeOffset until) => $"zone occupied by another file, retrying after {until.ToUniversalTime():HH:mm} UTC";

    // 복구를 RecoveryHold 동안 보류: 거절된 LCN 과 이번 주기의 재계획 수는 그대로 남기고 다음 허용 시각만 정함.
    private void HoldRecovery(GameSettings g, string why)
    {
        RecoveryAttempt prior = _status.Recovery(g.Name) ?? new RecoveryAttempt(null, null, 0, null);
        string reason = FirstLine(why);
        string stamp = RecoveryStamp(g);
        if (prior.NextAllowed is not null && prior.Reason == reason && prior.Stamp == stamp)
        {
            // 보류가 끝난 뒤 같은 계획·저널·트레이스로 같은 사유로 다시 거절됨: 30 분마다 되풀이하지 않고 멈춤 (입력이 바뀌면 다시 평가)
            Fail(g, RecoveryStoppedDetail + ": " + reason, null);
            return;
        }

        DateTimeOffset until = _env.Now() + RecoveryHold;
        _status.SetRecovery(g.Name, prior with { RejectedLcn = LcnIn(why) ?? prior.RejectedLcn, NextAllowed = until, Reason = reason, Stamp = stamp });
        Note(g, AutoStage.WaitingIdle, HoldText(until));
        _log.Warn($"{g.Name}: recovery held until {until.ToUniversalTime():u} ({FirstLine(why)})");
    }

    // 거절된 입력의 표식: 활성 계획 SHA, 저널 header SHA·줄 수, 트레이스 수정 시각. 읽을 수 없는 부분은 "?" (그 사이 읽히게 되면 표식이 달라져 다시 평가).
    private string RecoveryStamp(GameSettings g)
    {
        string plan = "?", journal = "?", trace = "?";
        try
        {
            plan = _env.LoadPlan(g.Plan).Sha();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 표식에서 "?" 로 남김
        }

        try
        {
            journal = $"{_env.JournalPlanSha(g)}:{_env.JournalRows(g)}";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 표식에서 "?" 로 남김
        }

        if (AutoTracer.PlanTracePath(g, _env.FileExists) is { } path && _env.FileWriteTime(path) is { } written)
        {
            trace = written.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"{plan}|{journal}|{trace}";
    }

    // 점유 충돌·저널 불일치의 첫 복구: 활성 계획을 후보로 잔여 재계획 (옮긴 조각은 지킴, 저널은 보관). 이미 한 번 썼거나 보류 중이면 하지 않음.
    // 반환: 활성 계획이 새 계획으로 바뀌었는가 (거짓이면 보류했거나 재계획이 거절됨: 호출자는 이번 틱을 끝냄).
    private bool ReplanRemainder(GameSettings g, PlanFile active, PlanRejectedException cause)
    {
        RecoveryAttempt prior = _status.Recovery(g.Name) ?? new RecoveryAttempt(null, null, 0, null);
        if (prior.NextAllowed is { } until && until > _env.Now())
        {
            Note(g, AutoStage.WaitingIdle, HoldText(until));
            return false;
        }

        if (prior.NextAllowed is null && prior.Replans >= 1)
        {
            HoldRecovery(g, cause.Message); // 잔여 재계획 뒤에 다시 막힘
            return false;
        }

        _log.Info($"{g.Name}: {FirstLine(cause.Message)}; replanning only the remaining part (moved parts kept)");
        try
        {
            PlanFile built = _env.ReplanResidual!(g, active, ResidualOlds(g), SessionsFor(g, active));
            _status.SetRecovery(g.Name, new RecoveryAttempt(built.Sha(), LcnIn(cause.Message), 1, null, prior.Reason, prior.Stamp));
            _ = _seeded.Remove(g.Name);
            return true;
        }
        catch (PlanRejectedException e) when (IsTemporaryRejection(e))
        {
            HoldTemporary(g, e); // 잠금 busy·읽기 불가·빈 구간 부족: 검증 오류가 아니라 일시 사유 (재계획 횟수에 세지 않고 곧 다시)
            return false;
        }
        catch (PlanRejectedException e)
        {
            HoldRecovery(g, e.Message); // 저널의 미검증 항목이 바뀌었음 (게임 업데이트 등): 같은 사유로 되풀이되면 멈추되 StoppedRecheck 마다 다시 평가
            return false;
        }
        catch (Exception e) when (IsTransient(e))
        {
            HoldTransient(g, e); // 저널 잠금·읽기 실패: 아무것도 바꾸지 않았음. 실패로 기록하지 않고 같은 서비스 실행 안에서 다시 시도
            return false;
        }
    }

    // 잔여 재계획이 .prefetch.json 도 다시 쓰도록 하는 세션: 트레이스를 읽을 수 있으면 계획할 때와 같은 입력, 없으면 계획의 핫 범위 (Order) 자체.
    private IReadOnlyList<IReadOnlyList<TraceEvent>> SessionsFor(GameSettings g, PlanFile plan)
    {
        try
        {
            if (AutoTracer.PlanTracePath(g, _env.FileExists) is { } tracePath)
            {
                IReadOnlyList<TraceEvent> trace = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot)).Kept;
                bool prefetch = tracePath == AutoTracer.BootTracePath(g) && _env.TraceSource(tracePath) == PlanSourcePrefetch;
                return [prefetch ? RangesByLcn(trace) : trace];
            }
        }
        catch (Exception e) when (IsTransient(e) || e is PlanRejectedException)
        {
            // 트레이스를 읽지 못하면 계획의 Order 로 선읽기 파일을 만듦
        }

        return [[.. plan.Order.SelectMany(o => Chunks(o.Path, o.Vcn * plan.ClusterBytes, (o.Vcn + o.Clusters) * plan.ClusterBytes))]];
    }

    // 이전 목표를 알려 주는 옛 계획들 (읽히는 것만, .stale·.boot·.extend). 현재 활성 계획은 ResidualReplan 이 스스로 더함.
    private List<PlanFile> ResidualOlds(GameSettings g)
    {
        var olds = new List<PlanFile>();
        foreach (string suffix in new[] { StaleSuffix, BootPlanSuffix, ExtendSuffix })
        {
            try
            {
                if (_env.FileExists(g.Plan + suffix))
                {
                    olds.Add(_env.LoadPlan(g.Plan + suffix));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 읽을 수 없는 옛 계획은 힌트일 뿐
            }
        }

        return olds;
    }

    // 계획의 존 (확장이면 이미 쓰던 앞부분 pinned 제외) 이 다른 게임 계획이 예약한 범위와 겹치는가.
    private bool ZoneReserved(GameSettings g, PlanFile plan, long pinned)
    {
        if (plan.Zone.StartLcn is not { } zoneStart || !plan.Stats.TryGetValue("zone_used_clusters", out object? u) || u is not long used)
        {
            return false;
        }

        return _env.Reserved(g).Any(r => r.Lcn < zoneStart + used && zoneStart + pinned < r.Lcn + r.Clusters);
    }

    // 같은 존을 다시 쓰는 계획의 목표 자리에 이 계획이 옮기지 않는 자기 파일 데이터 (예: 이전 계획이 존에 모은, 이번 기록에서는 핫이 아닌 부분) 가 있는가.
    // 그런 목표로는 이동이 늘 실패하므로 (사용 중인 클러스터) 새 존을 찾아야 합니다. 핫 범위(Order) 밖의 조각이 존의 쓰는 구간과 겹치는지 봅니다.
    // pinned: 확장 중이면 존 앞의 이만큼 (이전 계획이 쓰던 고정 구간, 이동하지 않음) 은 보지 않습니다.
    private bool ZoneBlockedByOwnData(PlanFile plan, long pinned)
    {
        if (plan.Zone.StartLcn is not { } zoneStart || !plan.Stats.TryGetValue("zone_used_clusters", out object? u) || u is not long used)
        {
            return false;
        }

        long from = zoneStart + pinned;
        foreach (IGrouping<string, PlanRange> file in plan.Order.GroupBy(o => o.Path, StringComparer.Ordinal))
        {
            if (_env.Extents(file.Key) is not { } extents)
            {
                continue;
            }

            List<(long A, long B)> hot = [.. file.Select(o => (A: o.Vcn, B: o.Vcn + o.Clusters)).OrderBy(r => r.A)];
            foreach (Extent e in extents.Where(e => e.Lcn < zoneStart + used && from < e.Lcn + e.Clusters))
            {
                long cur = e.Vcn + Math.Max(0, from - e.Lcn); // 존과 겹치는 VCN 구간 [cur, end)
                long end = e.Vcn + Math.Min(e.Clusters, zoneStart + used - e.Lcn);
                foreach ((long a, long b) in hot.Where(r => r.B > cur && r.A < end))
                {
                    if (a > cur)
                    {
                        return true;
                    }

                    cur = Math.Max(cur, b);
                }

                if (cur < end)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // 이전 계획의 존 시작과 이미 쓰던 클러스터 수 (새 계획이 그 구간의 조각을 제자리에 고정함).
    private (long? Start, long Used) PreviousZone(GameSettings g)
    {
        try
        {
            string previous = IsExtending(g) ? g.Plan + ExtendSuffix : g.Plan + StaleSuffix; // 확장 중이면 확장 전 계획의 존 시작
            if (!_env.FileExists(previous))
            {
                return (null, 0);
            }

            PlanFile old = _env.LoadPlan(previous);
            return (old.Zone.StartLcn, old.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long used ? used : 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, 0); // 읽을 수 없는 옛 계획은 힌트일 뿐
        }
    }

    private void Relocate(GameSettings g, AsterSettings s, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            Note(g, AutoStage.WaitingIdle, "game is running");
            return;
        }

        if (_prewarm is { } warm && !warm.TryBeginRelocation(g.Name)) // 예열 읽기가 없음을 확인하면서 재배치 시작 표시를 세움 (한 번에): 이후 같은 디스크의 예열은 읽기를 시작하지 못함
        {
            Note(g, AutoStage.WaitingIdle, WaitDetail(g, StartupPrewarmBusy)); // 유휴 판정 뒤에 예열이 읽기 시작했음: 쓰기를 시작하지 않고 다음 틱에
            return;
        }

        try
        {
            RelocateMarked(g, s, token);
        }
        finally
        {
            _prewarm?.EndRelocation(g.Name); // 성공·실패·예외 모두 표시를 내림
        }
    }

    private void RelocateMarked(GameSettings g, AsterSettings s, CancellationToken token)
    {
        _tier?.RevertGame(g.Name, "relocation starting");
        Note(g, AutoStage.Relocating, null);
        _log.Info($"{g.Name}: auto relocation starting");
        VolumeDisks.Shared.Forget(PlanFile.SplitDrive(g.OriginRoot)); // 재배치 직전 매핑 재조회 (드라이브 문자 재할당 반영)
        int mark = int.MaxValue; // 이번 적용 전의 저널 줄 수 (모르면 아무것도 되돌리지 않음)
        long pendingRecovery = 0; // 쓰기를 시작했으나 BeginRecovery 를 못 지난 예정 바이트: 예외로 빠져도 SMR 회복 대기를 시작하려고
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            List<bool?> stamps = [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Select(f => ReapplyChecker.Compare(f, _env.Stat))];
            if (!stamps.Contains(true) && stamps.Contains(null))
            {
                Note(g, AutoStage.WaitingIdle, "plan files unreadable, will retry"); // 잠김·권한으로 바뀌었는지 모름: 재적용만 미루고 재계획은 하지 않음
                return;
            }

            if (stamps.Contains(true))
            {
                _log.Info($"{g.Name}: source files changed since the plan, planning again"); // 없어진 파일은 새 계획에서 빠짐
                if (!Plan(g, s))
                {
                    return;
                }

                plan = _env.LoadPlan(g.Plan);
                ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            }

            bool extending = IsExtending(g);
            if (extending && plan.Stats.TryGetValue("in_place_bytes", out object? inPlace) && inPlace is 0L)
            {
                BlockExtension(g, "the existing zone is no longer in place"); // 존을 옮겨 다시 잡게 되므로 확장이 아니라 전체 재배치가 됨
                return;
            }

            var dryRun = new ApplyOptions(DryRun: true, SaveTo: g.Plan, Reserved: _env.Reserved(g));
            ApplyReport dry;
            try
            {
                if (!extending && _env.JournalPlanSha(g) is { } journalSha && journalSha != plan.Sha())
                {
                    throw new PlanRejectedException("journal belongs to a different plan"); // 존이 정해진 계획은 dry-run 이 저널을 보지 않으므로 여기서 미리 (실행 때 거절되기 전에)
                }

                dry = ApplyOrDefer(g, plan, dryRun, token); // 존 시작을 정해 계획에 저장 (이어하기를 위해 남김)
            }
            catch (PlanRejectedException e) when (extending && (!IsZoneNotFree(e) || IsSupplementExtension(g)))
            {
                BlockExtension(g, e.Message); // 존 뒤가 비어 있는 것과 무관한 거부 (프로필 보충은 비어 있지 않아도): 새 존을 찾지 않음
                return;
            }
            catch (PlanRejectedException e) when (!extending && _env.ReplanResidual is not null
                && (IsJournalMismatch(e) || IsZoneNotFree(e) && !(_seeded.Contains(g.Name) && _env.JournalRows(g) == 0)))
            {
                // 부분 적용된 계획이 막혔거나 (점유) 저널이 다른 계획의 것: 이미 옮긴 조각은 지키고 남은 부분만 다시 계획
                if (!ReplanRemainder(g, plan, e))
                {
                    return;
                }

                extending = false;
                plan = _env.LoadPlan(g.Plan);
                ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
                try
                {
                    dry = ApplyOrDefer(g, plan, dryRun, token);
                }
                catch (PlanRejectedException again) when (IsZoneNotFree(again) || IsJournalMismatch(again))
                {
                    HoldRecovery(g, again.Message); // 잔여 재계획 뒤에도 막힘: 30 분 보류
                    return;
                }
            }
            catch (PlanRejectedException e) when ((extending || _seeded.Contains(g.Name)) && IsZoneNotFree(e))
            {
                _log.Info(extending ? $"{g.Name}: the region after the zone is in use, looking for a new one" : $"{g.Name}: previous zone is not free, looking for a new one");
                if (!Plan(g, s, reuseZone: false))
                {
                    return;
                }

                extending = IsExtending(g); // 새 존 계획으로 바뀌었으면 확장 중이 아님 (.extend 는 Plan 이 .stale 로 치움)
                plan = _env.LoadPlan(g.Plan);
                ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
                dry = ApplyOrDefer(g, plan, dryRun, token);
            }

            if (dry.Stopped == DataLock.BusyReason)
            {
                Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail); // 잠금을 못 잡은 dry-run 은 아무것도 보지 못한 빈 보고서: "이미 제자리" 로 확정하지 않고 다음 틱에 다시
                return;
            }

            _ = _seeded.Remove(g.Name);
            if (dry.ZoneRetargeted)
            {
                _log.Info($"{g.Name}: previous zone was taken by another file, zone moved" + (dry.JournalArchivedTo is { } a ? $", earlier journal kept as {a}" : string.Empty));
            }

            mark = _env.JournalRows(g); // 저널을 옮겨 보관했을 수 있으므로 계획을 저장한 뒤의 줄 수
            if (dry.TasksTodo == 0)
            {
                if (HoldUnreadable(g, plan))
                {
                    return; // 읽지 못한 파일이 보류로 남음: 배치됨으로 확정하지 않음
                }

                _status.ClearRecovery(g.Name);
                _status.Complete(g.Name, g.Source, _env.Now(), counted: false, "already in place");
                if (extending)
                {
                    FinishExtension(g, plan);
                }

                _log.Info($"{g.Name}: auto relocation not needed, layout already in place");
                return;
            }

            plan = _env.LoadPlan(g.Plan);
            ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            ApplyOptions run = BaseApplyOptions(s, g);
            string order = run.Order;
            if (_status.RelocationHeldUntil() is { } held)
            {
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, AutoStatus.HoldDetail(held)));
                return;
            }

            long budget = BudgetBytes(s, g); // 0 = 무제한
            if (budget > 0)
            {
                long left = budget - _status.RelocationBytesWithin(DailyWindow);
                if (left <= 0)
                {
                    Note(g, AutoStage.WaitingIdle, WaitDetail(g, DailyBudget));
                    return;
                }

                run = run with { WriteCap = Math.Min(run.WriteCap, left) }; // 한 번의 적용은 회당 상한과 하루 남은 양 중 작은 쪽까지만 쓰고 나머지는 이어하기가 다시 셈
            }

            long bytes = Math.Min(dry.BytesTodo, run.WriteCap);
            (DriveBaseline? baseline, bool interrupted) = s.Auto.PostRelocateRecovery == AutoSettings.RecoveryProbe ? MeasureBaseline(g, plan, token) : (null, false);
            if (interrupted)
            {
                Note(g, AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"); // 기준 없이 강행하지 않음: 이번 틱에는 쓰지 않음
                return;
            }

            _status.BeginRelocation(_env.Now(), bytes); // 하루 상한은 끝이 아니라 시작 (이어하기 포함) 으로 셈
            pendingRecovery = bytes;
            ApplyReport rep = ApplyOrDefer(g, plan, run, token);
            _status.SetLastRelocationBytes(rep.MovedBytes); // 시작 때 넣은 예정 바이트 대신 실제로 옮긴 양
            _log.Info($"{g.Name}: auto relocation moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed, order {rep.OrderUsed ?? order}" + (rep.OrderFallbackReason is not null ? " (fallback)" : string.Empty) + (rep.Stopped is { } why ? $", stopped: {why}" : string.Empty) + rep.Timings.Summary);
            pendingRecovery = 0;
            BeginRecovery(g, s.Auto, rep.MovedBytes, baseline);
            if (rep.Stopped == DataLock.BusyReason)
            {
                Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail); // 아무것도 옮기지 않음 (SetLastRelocationBytes(0) 이 일일 횟수 기록도 지움): 다음 틱에 다시
            }
            else if (rep.Stopped == "cancelled")
            {
                Note(g, AutoStage.WaitingIdle, "cancelled by game start"); // 저널이 재개 지점이고 이어하기는 기존 재적용 검사가 합니다
            }
            else if (rep.Stopped == Applier.ZoneOccupied)
            {
                if (_status.Recovery(g.Name) is { Replans: >= 1 })
                {
                    HoldRecovery(g, rep.Stopped); // 잔여 재계획을 이미 썼음: 처음부터 다시 계획하지 않고 보류
                    return;
                }

                SetAsideForReplan(g); // 존이 막혔으니 계획을 낡은 것으로 치워 다음 틱에 같은 트레이스로 (새 존을 찾아) 다시 계획
            }
            else if (rep.Stopped is not null)
            {
                Note(g, AutoStage.WaitingIdle, WriteLimitDetail);
            }
            else if (rep.Failed.Count > 0)
            {
                if (!BlockFailedExtension(g, $"{rep.Failed.Count} step(s) failed", null, mark, token))
                {
                    Fail(g, $"{rep.Failed.Count} step(s) failed", null); // 데이터는 그대로이고 남은 이동은 기존 재적용 검사가 이어감
                }
            }
            else
            {
                LayoutReport layout = LayoutVerifier.Verify(_env.LoadPlan(g.Plan), _env.Extents);
                _log.Info($"{g.Name}: auto relocation verified: " + (layout.Contiguous ? "contiguous" : layout.ResumeNeeded ? "resume-needed" : "non-contiguous"));
                if (HoldUnreadable(g, plan))
                {
                    return; // 옮길 것은 옮겼으나 읽지 못한 파일이 보류로 남음: 완료로 확정하지 않음
                }

                _status.ClearRecovery(g.Name);
                _status.Complete(g.Name, g.Source, _env.Now(), counted: true, RelocatedTag(plan));
                _log.Info($"{g.Name}: auto relocation done");
                if (extending)
                {
                    FinishExtension(g, plan);
                }
            }
        }
        catch (ShaMismatchException e)
        {
            BeginRecovery(g, s.Auto, pendingRecovery, null); // 예외로 끝나도 옮기던 중이었을 수 있으니 예정 바이트로 보수적으로 회복 대기
            if (!BlockFailedExtension(g, "data check failed", e, mark, token))
            {
                Fail(g, "data check failed", e);
                Undo(g, mark, token);
            }
        }
        catch (PlanRejectedException e) when (IsTemporaryRejection(e))
        {
            if (pendingRecovery > 0)
            {
                _status.SetLastRelocationBytes(0); // 시작은 셌지만 아무것도 옮기기 전에 거절됨: 일일 횟수·예산에서 뺌
            }

            HoldTemporary(g, e); // 실패로 기록하지 않음: 빈 구간 부족·읽기 불가는 시간이 지나면 풀릴 수 있어 일시 보류 뒤 다시
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            BeginRecovery(g, s.Auto, pendingRecovery, null);
            if (!BlockFailedExtension(g, "relocation failed: " + e.GetType().Name, e, mark, token))
            {
                Fail(g, "relocation failed: " + e.GetType().Name, e);
                Undo(g, mark, token);
            }
        }
    }

    // 쓰기 직전 기준 속도: 대상 존 (이미 할당돼 있을 때만: 빈 영역은 SMR 이 0 을 즉시 돌려 과대) 과 존에서 4 GiB 뒤의 할당된 기준 구간.
    // 존·참조는 한 회차 (192 MiB·10 s, token 취소) 를 나눠 쓰고 그 사이에 디스크 활동을 다시 봅니다. 못 재면 Baseline null (고정 분으로 떨어짐), 게임 시작·바쁨으로 끊기면 Interrupted (이번 틱에는 재배치하지 않음).
    private (DriveBaseline? Baseline, bool Interrupted) MeasureBaseline(GameSettings g, PlanFile plan, CancellationToken token)
    {
        try
        {
            string drive = DriveOf(g);
            if (plan.Zone.StartLcn is not { } zone || _env.LocateProbeSpots(drive, zone, token) is not { } spots || (!spots.ZoneAllocated && spots.ReferenceLcn is null))
            {
                return (null, token.IsCancellationRequested); // 찾는 중 게임이 떠 취소됐으면 기준 없이 강행하지 않음
            }

            var round = new ProbeRound(token, (spots.ZoneAllocated ? 1 : 0) + (spots.ReferenceLcn is null ? 0 : 1));
            if (token.IsCancellationRequested || DriveRecovery.Busy(_env, drive))
            {
                return (null, true);
            }

            ProbeSpot? zoneSpot = spots.ZoneAllocated ? Spot(drive, zone, round) : null;
            if (token.IsCancellationRequested || (spots.ZoneAllocated && spots.ReferenceLcn is not null && zoneSpot is not null && DriveRecovery.Busy(_env, drive)))
            {
                return (null, true);
            }

            ProbeSpot? refSpot = spots.ReferenceLcn is { } r ? Spot(drive, r, round) : null;
            if (token.IsCancellationRequested)
            {
                return (null, true);
            }

            return (zoneSpot is null && refSpot is null ? null : new DriveBaseline(zoneSpot, refSpot), false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{g.Name}: baseline read speed not measured ({e.GetType().Name})");
            return (null, false);
        }
    }

    private ProbeSpot? Spot(string drive, long lcn, ProbeRound round) => _env.ProbeMbps(drive, lcn, round) is > 0 and double mbps ? new ProbeSpot(lcn, mbps) : null;

    // 쓴 바이트가 있으면 회복 추적을 시작: probe 는 기준이 있을 때 최대 recovery_max_min 분 (그때까지 회복을 확인하지 못하면 recovery_hard_max_min 분까지 재배치만 계속 보류), 기준이 없거나 fixed 면 post_update_delay_min 분 고정, off 는 기다리지 않음.
    private void BeginRecovery(GameSettings g, AutoSettings auto, long movedBytes, DriveBaseline? baseline)
    {
        if (movedBytes <= 0 || auto.PostRelocateRecovery == AutoSettings.RecoveryOff)
        {
            return;
        }

        bool probe = auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && baseline is not null;
        _recovery.Begin(DriveOf(g), probe ? baseline : null, TimeSpan.FromMinutes(probe ? auto.RecoveryMaxMin : auto.PostUpdateDelayMin), TimeSpan.FromMinutes(auto.RecoveryHardMaxMin));
    }

    // 게임이 안 돌 때만, 이번 자동 적용이 저널에 쓴 줄 (mark 줄 뒤) 만 되돌립니다 (최선 노력). 앞선 적용의 줄은 이어하기·수동 되돌리기의 몫이라 건드리지 않음.
    // 되돌리기를 끝냈으면 참 (되돌릴 것이 없었던 경우 포함), 게임 실행·중단·실패로 못 끝냈으면 거짓.
    private bool Undo(GameSettings g, int mark, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            _log.Warn($"{g.Name}: game is running, revert skipped (journal kept)");
            return false;
        }

        try
        {
            RevertReport? r = _env.JournalRows(g) <= mark ? null : _env.Revert(g, mark, token);
            bool complete = r is null || (r.Stopped is null && r.Skipped.Count == 0); // 옮기지 못한 조각이 하나라도 있으면 미완료 (저널을 보관하면 되돌릴 길이 없어짐)
            _log.Info(r is null ? $"{g.Name}: nothing to revert" : $"{g.Name}: reverted {r.Restored} piece(s), {r.Skipped.Count} skipped" + (r.Stopped is { } why ? $", stopped: {why}" : string.Empty) + (complete ? string.Empty : ", journal kept"));
            return complete;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: revert failed, journal kept", e);
            return false;
        }
    }

    private void Fail(GameSettings g, string detail, Exception? e)
    {
        _planFailedAt[g.Name] = _env.Now();
        _status.Set(g.Name, g.Source, null, AutoStage.Failed, detail);
        _log.Error($"{g.Name}: auto relocation stopped: {detail}", e);
    }
}
