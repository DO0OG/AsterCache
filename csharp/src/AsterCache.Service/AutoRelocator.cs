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

    /// 프리페치 계획은 이 크기 이하의 파일만 통째로 존에 모읍니다 (더 큰 파일은 옮기지 않고 예열 때만 읽음).
    public const long PrefetchMaxFileBytes = 64L << 20;
    private const string FirstRunDetail = "first run recorded, planning again";
    private const string BusyDetail = "target region busy, planning again";

    /// 존 확장 재계획 (병합한 트레이스가 핫 집합을 키웠을 때): 이전 계획을 옆으로 치운 파일 (이 파일이 있는 동안이 확장 중), 그동안의 detail 표시. ReplanDetail 접두는 쓰지 않음 (업데이트 지연 판정과 섞이지 않게).
    public const string ExtendSuffix = ".extend";
    private const string ExtendDetail = "trace grew, extending zone";

    /// 업데이트 뒤 재계획 검사 간격: 배치까지 끝낸 게임마다 이 주기로만 계획의 지문을 봅니다 (ReapplyChecker 의 주기보다 짧게: 게임을 다시 켜기 전에 잡으려고).
    public static readonly TimeSpan StaleCheckEvery = TimeSpan.FromMinutes(10);

    public const string StaleSuffix = ".stale";

    /// 지문 검사의 대표 파일: 게임 exe 와 계획에서 가장 큰 파일 몇 개. 이 중 하나라도 바뀌었을 때만 전체 지문을 비교합니다.
    private const int LargestSampled = 3;
    private const string ReplanDetail = "game updated, will trace again on next run";
    private static readonly Regex PrewarmedTag = new(@"(, )?prewarmed( \([^)]*\))?", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex ProgressTag = new(@"(, )?prewarming \d+/\d+ MiB", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TraceTag = new(@"(, )?trace \d+/\d+", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex TierTag = new(@"(, )?ssd tier [\d.]+ GB", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly IdleTracker _idle = new();
    private DateTimeOffset? _waitSince; // 틱 스레드에서만 씀: 유휴가 아닌 채로 기다리기 시작한 때
    private bool _waitNoted; // 틱 스레드에서만 씀: 이번 대기 구간에 긴 대기를 이미 남겼는지
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
    private readonly Dictionary<string, long> _needBytes = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 하루 쓰기 예산 때문에 보류한 게임의 마지막 이동 예정 바이트
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 이전 존 시작에 맞춰 계획을 만든 게임
    private readonly Dictionary<string, DateTimeOffset> _extTried = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 존 확장을 이미 따져 본 (또는 막힌) 게임과 그때의 트레이스 수정 시각 (다음 플레이가 쌓여 트레이스가 바뀔 때까지 다시 보지 않음)
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
        PrefetchBootstrap(s);
        WarmCold(s, warms);
        bool relocateOn = s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0;
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        List<GameSettings> work = s.Games.Where(g => !PostUpdateDelayed(g, s) && NeedsWork(g, relocateOn))
            .OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        DateTimeOffset? held = relocateOn ? _status.RelocationHeldUntil() : null; // 상태 파일을 못 읽어 막는 중
        bool limitReached = relocateOn && (held is not null || _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay); // Note 보다 먼저 정해 같은 상태 로그가 오락가락하며 쌓이지 않게
        long budget = (long)s.Auto.MaxRelocateGibPerDay << 30; // 0 = 무제한
        long used = budget > 0 ? _status.RelocationBytesWithin(DailyWindow) : 0;
        var ready = new List<GameSettings>();
        foreach (GameSettings g in work)
        {
            if (_recovery.IsRecovering(DriveOf(g)))
            {
                WarmNewTrace(g, warms); // 예열은 막지 않음
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, DriveRecovering));
                continue;
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

            bool budgetHeld = budget > 0 && used > 0 && used + _needBytes.GetValueOrDefault(g.Name, 1) > budget; // 아직 모르면 1 바이트라도 남아야 함, 그날 첫 이동은 한 번 이동량이 상한보다 커도 허용
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
        if (ShowsProgress(stage) && AutoTracer.RunCount(g, _env.FileExists) is > 0 and int runs && runs < _settings().Auto.TraceRuns) // 플레이를 더 모으는 중: "trace 2/3"
        {
            rest = rest is null ? $"trace {runs}/{_settings().Auto.TraceRuns}" : rest + $", trace {runs}/{_settings().Auto.TraceRuns}";
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
        _ = _env.ArchiveJournal(g);
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

                // boot 트레이스로 배치한 게임이 첫 실행을 기록했으면 (원시 트레이스가 생김) 그 기록으로 계획을 바꿉니다: 원시 트레이스는 치우지 않음.
                bool recorded = IsBootPlan(plan) && _env.FileExists(AutoTracer.RawTracePath(g));
                if (!recorded && !ReplanStale(g, plan))
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "game updated"); // 링크는 새 재배치 전에 되돌림 (심볼릭 링크가 낀 계획은 재적용·재배치가 거부함)
                _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                string trace = AutoTracer.RawTracePath(g);
                if (!recorded && _env.FileExists(trace))
                {
                    for (int run = AutoTracer.RunCount(g, _env.FileExists); run >= 1; run--)
                    {
                        string runFile = TraceWriter.RunPath(trace, run); // 플레이 수는 run 파일로 세므로 옛 버전의 run 파일이 남지 않게 함
                        if (_env.FileExists(runFile))
                        {
                            _env.MoveAside(runFile, runFile + StaleSuffix);
                        }
                    }

                    _env.MoveAside(trace, trace + StaleSuffix);
                }

                _env.MoveAside(g.Plan, g.Plan + StaleSuffix);
                _status.ResetForReplan(g.Name, recorded ? FirstRunDetail : ReplanDetail);
                _log.Info(recorded
                    ? $"{g.Name}: first run recorded, profile plan set aside as {StaleSuffix}, will plan again from the recording (same zone first)"
                    : $"{g.Name}: source files changed since the plan (game update), trace and plan set aside as {StaleSuffix}, will trace and plan again on the next run");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: update check failed, will retry ({e.GetType().Name})");
            }
        }
    }

    // 드라이브가 분리됐거나 (폴더 없음) 파일을 읽을 수 없으면 (잠김 등) "변경" 이 아니라 건너뜀: 다음 검사에 다시 봅니다. 대표 파일 (게임 exe, 가장 큰 파일 몇 개) 이
    // 바뀐 경우에만 전체 지문을 비교합니다 (shortcut: 대표가 아닌 파일만 바뀐 업데이트는 대표가 바뀔 때까지 못 잡음, 재배치 직전 검사가 잡음).
    private bool ReplanStale(GameSettings g, PlanFile plan)
    {
        IEnumerable<Fingerprint> sample = plan.Fingerprints.Where(f => g.ExeNames.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))
            .Concat(plan.Fingerprints.OrderByDescending(f => f.Size).Take(LargestSampled));
        return sample.Any(Differs) && plan.Fingerprints.Any(Differs);
    }

    // 지문과 다른가. 파일이 없어짐 (업데이트가 지움) 은 바뀜, 그 밖의 읽기 실패 (잠김·권한) 는 판단 불가라 같은 것으로 침.
    private bool Differs(Fingerprint fp)
    {
        try
        {
            Fingerprint now = _env.Stat(fp.Path);
            return now.Size != fp.Size || now.MtimeNs != fp.MtimeNs;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 재계획 도중 서비스가 꺼진 경우 (계획은 .stale 로 옮겼는데 상태를 되돌리기 전): 배치됨 표시는 남았고 계획은 없고 .stale 만 있으면 상태를 마저 되돌립니다.
    private void RecoverReplan(AsterSettings s)
    {
        if (_recovered)
        {
            return;
        }

        _recovered = true;
        foreach (GameSettings g in s.Games.Where(g => _status.Get(g.Name)?.RelocatedAt is not null && !_env.FileExists(g.Plan) && (_env.FileExists(g.Plan + StaleSuffix) || IsExtending(g))))
        {
            _status.ResetForReplan(g.Name, IsExtending(g) ? ExtendDetail : ReplanDetail);
            _log.Info($"{g.Name}: an earlier replan was interrupted, state reset to trace and plan again");
        }
    }

    // 확장 중인가: 확장 재계획이 이전 계획을 .extend 로 치운 뒤 끝나거나 막히기 전까지 (파일이라 서비스를 다시 시작해도 이어짐).
    private bool IsExtending(GameSettings g) => _env.FileExists(g.Plan + ExtendSuffix);

    // 존 확장 재계획: 배치까지 끝낸 게임의 병합 트레이스 (플레이를 더 모아 합집합이 된 trace.json) 가 계획보다 새롭고, 핫 집합이 TraceMergeMinMib 이상 커졌으면
    // 이전 계획을 .extend 로 치우고 처음 단계로 되돌립니다. 이후는 일반 흐름 (계획 → 재배치): 같은 존 시작에 맞춰 계획하므로 이미 존에 있는 블록은 제자리이고 새 블록만 존 뒤에 붙으며,
    // 유휴·일일 상한·바이트 예산·회복·전원·같은 디스크 게임 같은 안전장치를 그대로 지납니다. 존 뒤가 비어 있지 않은 등 확장할 수 없으면 BlockExtension 이 이전 계획을 되살립니다.
    private void ReplanExtended(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            string raw = AutoTracer.RawTracePath(g);
            if (_status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || _env.FileWriteTime(raw) is not { } traced || _env.FileWriteTime(g.Plan) is not { } planned || traced <= planned
                || _extTried.TryGetValue(g.Name, out DateTimeOffset seen) && seen >= traced
                || AutoTracer.RunCount(g, _env.FileExists) < 2)
            {
                continue;
            }

            _extTried[g.Name] = traced;
            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || IsBootPlan(plan) || !plan.Stats.TryGetValue("hot_blocks", out object? hb) || hb is not long hotBlocks || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue; // boot 계획은 첫 기록 재계획 (ReplanChanged) 의 몫
                }

                long cap = MaxZoneMib(s, g) << 20;
                long merged = Math.Min(DistinctBlocks(_env.LoadTrace(raw, g.OriginRoot)) * HotSet.Block, cap);
                long grew = merged - hotBlocks * HotSet.Block;
                if (grew < (long)s.Auto.TraceMergeMinMib << 20)
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "trace grew"); // 링크는 새 재배치 전에 되돌림
                _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                _status.ResetForReplan(g.Name, ExtendDetail);
                _log.Info($"{g.Name}: merged trace holds {grew >> 20} MiB more than the plan (run {AutoTracer.RunCount(g, _env.FileExists)}), plan set aside as {ExtendSuffix}, will extend the zone");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: zone extension check failed, will retry on the next trace ({e.GetType().Name})");
            }
        }
    }

    private long MaxZoneMib(AsterSettings s, GameSettings g) => s.MaxZoneAuto ? ZoneSize.AutoMaxMib(_env.PhysicalRamBytes(), SlowBus(g)) : s.MaxZoneMib;

    private bool SlowBus(GameSettings g)
    {
        try { return _env.IsSlowBus(DriveOf(g)); }
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
        _ = _seeded.Remove(g.Name);
        _status.Complete(g.Name, g.Source, _env.Now(), counted: false, null);
        _log.Info($"{g.Name}: zone extension blocked: {reason}");
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
        if (_env.AnyRunning([.. SameDriveGames(s.Games, [g], _env.SameDisk).SelectMany(x => x.ExeNames).Concat(g.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)]))
        {
            return ReapplyVerdict.GameRunning;
        }

        if (_recovery.IsRecovering(DriveOf(g)))
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

        long budget = (long)s.Auto.MaxRelocateGibPerDay << 30; // 0 = 무제한
        bool countHeld = _status.RelocationHeldUntil() is not null || (s.Auto.MaxRelocationsPerDay > 0 && _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay);
        bool bytesHeld = budget > 0 && _status.RelocationBytesWithin(DailyWindow) is > 0 and long used && used >= budget;
        return countHeld || bytesHeld ? ReapplyVerdict.DailyLimit : null;
    }

    /// 정기 재적용이 끝났음 (예외로 끝났으면 0 바이트): 일일 상한·예산 집계에 시작으로 넣고, 쓴 바이트가 있으면 자동 재배치와 같이 드라이브 회복 추적을 시작합니다.
    public void NoteReapplied(GameSettings g, long movedBytes)
    {
        _status.BeginRelocation(_env.Now(), movedBytes);
        BeginRecovery(g, _settings().Auto, movedBytes, null);
    }

    private bool NeedsWork(GameSettings g, bool relocateOn)
    {
        AutoGameEntry? e = _status.Get(g.Name);
        bool stageOpen = e is null || e.Stage is not (AutoStage.Done or AutoStage.Failed)
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
            IReadOnlyList<TraceEvent> trace = _env.LoadTrace(tracePath, g.OriginRoot);
            IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = [prefetch ? WholeFilesByLcn(trace) : trace];
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

            long? start = reuseZone && _env.MakePlanAt is not null ? PreviousZoneStart(g) : null;
            if (extending && start is null)
            {
                BlockExtension(g, "previous zone start unknown");
                return false;
            }

            PlanFile plan;
            try
            {
                plan = start is { } lcn ? _env.MakePlanAt!(sessions, g.OriginRoot, zoneMib, lcn) : _env.MakePlan(sessions, g.OriginRoot, zoneMib);
            }
            catch (PyValueException e) when (start is not null && !extending)
            {
                // 존 안의 핫 파일이 접근 순서와 반대로 놓이면 (zone overlaps hot data in a cycle) 그 자리에서는 계획할 수 없음: 확장이 아니면 새 존을 찾음
                _log.Info($"{g.Name}: previous zone cannot be planned in place ({FirstLine(e.Message)}), looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (start is not null && ZoneBlockedByOwnData(plan))
            {
                if (extending)
                {
                    BlockExtension(g, "the region after the zone holds data the plan does not move");
                    return false;
                }

                _log.Info($"{g.Name}: previous zone holds data this plan does not move, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            _ = start is null ? _seeded.Remove(g.Name) : _seeded.Add(g.Name);
            if (boot)
            {
                plan.Stats.Add("source", prefetch ? PlanSourcePrefetch : PlanSourceBoot);
            }

            _env.SavePlan(plan, sessions, g.Plan);
            _log.Info($"{g.Name}: auto plan made, hot {hot >> 20} MiB, zone {zoneMib} MiB");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlanFailed(g, "plan failed: " + e.GetType().Name, e);
            return false;
        }
    }

    // 확장 중에 계획이 실패하면 실패 상태로 두지 않고 이전 계획을 되살립니다.
    private void PlanFailed(GameSettings g, string detail, Exception? e)
    {
        if (IsExtending(g))
        {
            BlockExtension(g, e is null ? detail : $"{detail}: {FirstLine(e.Message)}"); // 예외 형식만으로는 원인을 알 수 없음
        }
        else
        {
            Fail(g, detail, e);
        }
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

    // 프리페치 계획의 입력: 트레이스에 나온 파일 중 크기가 PrefetchMaxFileBytes 이하인 것을 통째로, 물리 순서 (첫 extent 의 LCN, 오름차순) 로.
    // shortcut: 프리페치 순서가 실제 접근 순서와 얼마나 맞는지 아직 재지 않아서 (τ 검증 전) 접근 순서를 따르지 않고 물리 순서로 보수적으로 정렬함, 검증되면 프리페치 순서로 바꿈.
    private List<TraceEvent> WholeFilesByLcn(IReadOnlyList<TraceEvent> trace)
    {
        var rows = new List<(string Path, long Size, long Lcn)>();
        foreach (string path in trace.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            long size = _env.Stat(path).Size;
            if (size > 0 && size <= PrefetchMaxFileBytes && _env.Extents(path) is { Count: > 0 } extents)
            {
                rows.Add((path, size, extents.Min(x => x.Lcn)));
            }
        }

        return rows.OrderBy(r => r.Lcn).ThenBy(r => r.Path, StringComparer.Ordinal).Select(r => new TraceEvent(r.Path, 0, r.Size)).ToList();
    }

    // 같은 존을 다시 쓰는 계획의 목표 자리에 이 계획이 옮기지 않는 자기 파일 데이터 (예: 이전 계획이 존에 모은, 이번 기록에서는 핫이 아닌 부분) 가 있는가.
    // 그런 목표로는 이동이 늘 실패하므로 (사용 중인 클러스터) 새 존을 찾아야 합니다. 핫 범위(Order) 밖의 조각이 존의 쓰는 구간과 겹치는지 봅니다.
    private bool ZoneBlockedByOwnData(PlanFile plan)
    {
        if (plan.Zone.StartLcn is not { } zoneStart || !plan.Stats.TryGetValue("zone_used_clusters", out object? u) || u is not long used)
        {
            return false;
        }

        foreach (IGrouping<string, PlanRange> file in plan.Order.GroupBy(o => o.Path, StringComparer.Ordinal))
        {
            if (_env.Extents(file.Key) is not { } extents)
            {
                continue;
            }

            List<(long A, long B)> hot = [.. file.Select(o => (A: o.Vcn, B: o.Vcn + o.Clusters)).OrderBy(r => r.A)];
            foreach (Extent e in extents.Where(e => e.Lcn < zoneStart + used && zoneStart < e.Lcn + e.Clusters))
            {
                long cur = e.Vcn + Math.Max(0, zoneStart - e.Lcn); // 존과 겹치는 VCN 구간 [cur, end)
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

    private long? PreviousZoneStart(GameSettings g)
    {
        try
        {
            string previous = IsExtending(g) ? g.Plan + ExtendSuffix : g.Plan + StaleSuffix; // 확장 중이면 확장 전 계획의 존 시작
            return _env.FileExists(previous) ? _env.LoadPlan(previous).Zone.StartLcn : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // 읽을 수 없는 옛 계획은 힌트일 뿐
        }
    }

    private void Relocate(GameSettings g, AsterSettings s, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            Note(g, AutoStage.WaitingIdle, "game is running");
            return;
        }

        _tier?.RevertGame(g.Name, "relocation starting");
        Note(g, AutoStage.Relocating, null);
        _log.Info($"{g.Name}: auto relocation starting");
        VolumeDisks.Shared.Forget(PlanFile.SplitDrive(g.OriginRoot)); // 재배치 직전 매핑 재조회 (드라이브 문자 재할당 반영)
        int mark = int.MaxValue; // 이번 적용 전의 저널 줄 수 (모르면 아무것도 되돌리지 않음)
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            if (plan.Fingerprints.Any(f => ReapplyChecker.Changed(f, _env.Stat)))
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

            var dryRun = new ApplyOptions(DryRun: true, SaveTo: g.Plan);
            ApplyReport dry;
            try
            {
                dry = _env.Apply(g, plan, dryRun, token); // 존 시작을 정해 계획에 저장 (이어하기를 위해 남김)
            }
            catch (PlanRejectedException e) when (extending)
            {
                BlockExtension(g, e.Message); // 존 뒤가 비어 있지 않음 (zone_not_free) 등: 새 존을 찾지 않음
                return;
            }
            catch (PlanRejectedException e) when (_seeded.Contains(g.Name) && e.Message.StartsWith("zone_not_free", StringComparison.Ordinal))
            {
                _log.Info($"{g.Name}: previous zone is not free, looking for a new one");
                if (!Plan(g, s, reuseZone: false))
                {
                    return;
                }

                plan = _env.LoadPlan(g.Plan);
                ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
                dry = _env.Apply(g, plan, dryRun, token);
            }

            _ = _seeded.Remove(g.Name);
            if (dry.ZoneRetargeted)
            {
                _log.Info($"{g.Name}: previous zone was taken by another file, zone moved" + (dry.JournalArchivedTo is { } a ? $", earlier journal kept as {a}" : string.Empty));
            }

            mark = _env.JournalRows(g); // 저널을 옮겨 보관했을 수 있으므로 계획을 저장한 뒤의 줄 수
            if (dry.TasksTodo == 0)
            {
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
            string order = RelocateOrder.Resolve(s.Auto.RelocateOrder);
            var run = new ApplyOptions(Execute: true, ChunkBytes: (long)s.Auto.RelocateChunkMib << 20, LowIoPriority: s.Auto.RelocateIoPriority == AutoSettings.IoLow, Verify: s.Auto.RelocateVerify, Order: order, Parallel: s.Auto.ParallelFor(_env.IsSlowBus(DriveOf(g))));
            long bytes = Math.Min(dry.BytesTodo, run.WriteCap); // 한 번의 적용은 WriteCap 까지만 쓰고 나머지는 이어하기가 다시 셈
            if (_status.RelocationHeldUntil() is { } held)
            {
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, AutoStatus.HoldDetail(held)));
                return;
            }

            long budget = (long)s.Auto.MaxRelocateGibPerDay << 30;
            long usedToday = budget > 0 ? _status.RelocationBytesWithin(DailyWindow) : 0;
            if (extending && budget > 0 && usedToday == 0 && bytes > budget)
            {
                BlockExtension(g, "the extension is larger than the daily write budget"); // 확장분은 첫 이동 예외를 받지 않으므로 영원히 대기하게 됨
                return;
            }

            if ((usedToday > 0 || extending) && usedToday + bytes > budget && budget > 0) // 빈 창의 첫 이동은 상한보다 커도 허용 (아니면 영원히 대기), 존 확장은 예외 없음
            {
                _needBytes[g.Name] = bytes;
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, DailyBudget));
                return;
            }

            _ = _needBytes.Remove(g.Name);
            (DriveBaseline? baseline, bool interrupted) = s.Auto.PostRelocateRecovery == AutoSettings.RecoveryProbe ? MeasureBaseline(g, plan, token) : (null, false);
            if (interrupted)
            {
                Note(g, AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"); // 기준 없이 강행하지 않음: 이번 틱에는 쓰지 않음
                return;
            }

            _status.BeginRelocation(_env.Now(), bytes); // 하루 상한은 끝이 아니라 시작 (이어하기 포함) 으로 셈
            ApplyReport rep = _env.Apply(g, plan, run, token);
            _log.Info($"{g.Name}: auto relocation moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed, order {rep.OrderUsed ?? order}" + (rep.OrderFallbackReason is not null ? " (fallback)" : string.Empty) + (rep.Stopped is { } why ? $", stopped: {why}" : string.Empty) + rep.Timings.Summary);
            BeginRecovery(g, s.Auto, rep.MovedBytes, baseline);
            if (rep.Stopped == "cancelled")
            {
                Note(g, AutoStage.WaitingIdle, "cancelled by game start"); // 저널이 재개 지점이고 이어하기는 기존 재적용 검사가 합니다
            }
            else if (rep.Stopped == Applier.ZoneOccupied)
            {
                SetAsideForReplan(g); // 존이 막혔으니 계획을 낡은 것으로 치워 다음 틱에 같은 트레이스로 (새 존을 찾아) 다시 계획
            }
            else if (rep.Stopped is not null)
            {
                Note(g, AutoStage.WaitingIdle, "write limit reached, will continue");
            }
            else if (rep.Failed.Count > 0)
            {
                Fail(g, $"{rep.Failed.Count} step(s) failed", null); // 데이터는 그대로이고 남은 이동은 기존 재적용 검사가 이어감
            }
            else
            {
                LayoutReport layout = LayoutVerifier.Verify(_env.LoadPlan(g.Plan), _env.Extents);
                _log.Info($"{g.Name}: auto relocation verified: " + (layout.Contiguous ? "contiguous" : layout.ResumeNeeded ? "resume-needed" : "non-contiguous"));
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
            Fail(g, "data check failed", e);
            Undo(g, mark, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fail(g, "relocation failed: " + e.GetType().Name, e);
            Undo(g, mark, token);
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

    // 쓴 바이트가 있으면 회복 추적을 시작: probe 는 기준이 있을 때 최대 recovery_max_min 분, 기준이 없거나 fixed 면 post_update_delay_min 분 고정, off 는 기다리지 않음.
    private void BeginRecovery(GameSettings g, AutoSettings auto, long movedBytes, DriveBaseline? baseline)
    {
        if (movedBytes <= 0 || auto.PostRelocateRecovery == AutoSettings.RecoveryOff)
        {
            return;
        }

        bool probe = auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && baseline is not null;
        _recovery.Begin(DriveOf(g), probe ? baseline : null, TimeSpan.FromMinutes(probe ? auto.RecoveryMaxMin : auto.PostUpdateDelayMin));
    }

    // 게임이 안 돌 때만, 이번 자동 적용이 저널에 쓴 줄 (mark 줄 뒤) 만 되돌립니다 (최선 노력). 앞선 적용의 줄은 이어하기·수동 되돌리기의 몫이라 건드리지 않음.
    private void Undo(GameSettings g, int mark, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            _log.Warn($"{g.Name}: game is running, revert skipped (journal kept)");
            return;
        }

        try
        {
            RevertReport? r = _env.JournalRows(g) <= mark ? null : _env.Revert(g, mark, token);
            _log.Info(r is null ? $"{g.Name}: nothing to revert" : $"{g.Name}: reverted {r.Restored} piece(s), {r.Skipped.Count} skipped" + (r.Stopped is { } why ? $", stopped: {why}, journal kept" : string.Empty));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: revert failed, journal kept", e);
        }
    }

    private void Fail(GameSettings g, string detail, Exception? e)
    {
        _status.Set(g.Name, g.Source, null, AutoStage.Failed, detail);
        _log.Error($"{g.Name}: auto relocation stopped: {detail}", e);
    }
}
