using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Service;

public enum ReapplyVerdict
{
    /// 계획이 한 번도 적용되지 않음 (존 시작 미정). 첫 적용은 사용자가 CLI 로 합니다.
    NotApplied,

    /// 계획 뒤 원본 파일이 바뀜 (지문 불일치). 새 계획이 필요하고 재적용은 거부됩니다.
    PlanChanged,
    Healthy,
    GameRunning,
    DiskBusy,

    /// 관리자가 아니거나 설정·계획 폴더 권한이 안전하지 않음: 상태만 기록.
    ApplyDisabled,
    Reapply,

    /// 재적용 도중 게임이 시작돼 멈춤 (저널에 재개 지점이 남음). 오류가 아니고, 게임이 끝난 뒤 다음 검사에서 이어갑니다.
    Cancelled,
    Failed,

    /// 아래는 자동 모드와 같은 게이트가 미룬 이유 (같은 디스크 게임·다른 게임 실행은 GameRunning, 디스크 바쁨은 DiskBusy 를 씀).
    Recovering,
    OnBattery,
    DailyLimit,
    NotRelocatable,
    PostUpdateDelay,
    NotIdle,

    /// 계획 파일 중 읽을 수 없는 것이 있어 (잠김·권한) 바뀌었는지 모름: 재적용을 보류하고 재계획도 하지 않습니다.
    FilesUnreadable,
}

/// 재적용 판단 입력. GameRunning·DiskBusy 는 비싸므로 필요할 때만 부르는 지연 값입니다.
/// Contiguous 는 LayoutVerifier 와 같은 판정 (null = 알 수 없음, 건강으로 봄). 존 안 비율이 아니라 이 판정이 기준인 이유: 계획이 이미 연속인 구간을 제자리에 보존하면
/// 존 안 비율이 낮아도 배치는 연속입니다 (실제 게임 계획에서 10.5% 인데 verify 는 contiguous 였음).
public sealed record ReapplyInputs(bool PlanApplied, bool FingerprintsChanged, bool? Contiguous, Func<bool> GameRunning, Func<bool> DiskBusy, bool CanApply, Func<ReapplyVerdict?>? Gate = null, bool FingerprintsUnknown = false);

public static class ReapplyPolicy
{
    public static ReapplyVerdict Decide(ReapplyInputs i)
    {
        if (!i.PlanApplied)
        {
            return ReapplyVerdict.NotApplied;
        }

        if (i.FingerprintsChanged)
        {
            return ReapplyVerdict.PlanChanged;
        }

        if (i.Contiguous is null or true)
        {
            return ReapplyVerdict.Healthy;
        }

        if (i.FingerprintsUnknown)
        {
            return ReapplyVerdict.FilesUnreadable;
        }

        if (i.Gate?.Invoke() is { } blocked)
        {
            return blocked; // 자동 재배치와 같은 게이트 (같은 디스크 다른 게임·회복·전원·일일 예산·유휴 등)
        }

        if (i.GameRunning())
        {
            return ReapplyVerdict.GameRunning;
        }

        if (i.DiskBusy())
        {
            return ReapplyVerdict.DiskBusy;
        }

        return i.CanApply ? ReapplyVerdict.Reapply : ReapplyVerdict.ApplyDisabled;
    }
}

/// 재적용 검사가 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class ReapplyEnv
{
    public required bool CanApply { get; init; }

    public required Func<GameSettings, bool> GameRunning { get; init; }

    public required Func<GameSettings, bool> DiskBusy { get; init; }

    public required Func<string, PlanFile> LoadPlan { get; init; }

    public required Func<string, IReadOnlyList<Extent>?> Extents { get; init; }

    public required Func<string, Fingerprint> Stat { get; init; }

    public required Func<string, string> RealPath { get; init; }

    public required Func<GameSettings, PlanFile, ApplyOptions?, CancellationToken, ApplyReport> Apply { get; init; }

    /// 이 게임에 SSD 보조 저장 링크가 살아 있는가: 링크가 낀 파일은 배치를 읽을 수 없고 허용 루트 밖으로 풀리므로 그동안 배치 검사를 건너뜁니다 (링크는 재배치 전·업데이트 때 되돌려짐).
    public Func<GameSettings, bool> TierActive { get; init; } = _ => false;

    /// 자동 모드와 같은 재적용 게이트 (서비스가 자동 모드를 만든 뒤 연결): 막는 이유 또는 null. 연결 전·시험 기본값은 막지 않음.
    public Func<GameSettings, ReapplyVerdict?> Gate { get; set; } = _ => null;

    /// 재적용을 시작하기 전에 예정 바이트를 일일 상한·예산 집계에 알림 (자동 재배치와 같이 실행 전에 셈).
    public Action<GameSettings, long> Started { get; set; } = (_, _) => { };

    /// 재적용이 끝난 뒤 (예외로 끝나도) 실제로 쓴 바이트를 일일 예산·회복 추적에 알림.
    public Action<GameSettings, long> Spent { get; set; } = (_, _) => { };

    /// 서비스가 정한 재적용 옵션 (회당 쓰기 상한·하루 잔여 예산 반영, 서비스가 자동 모드를 만든 뒤 연결): null 이면 OptionsFor 의 기본.
    public Func<GameSettings, ApplyOptions?> Options { get; set; } = _ => null;

    /// 배치 풀림 경고를 낼 만한 때인가 (연결 전·시험 기본값은 허용).
    public Func<GameSettings, bool> WarnDegraded { get; set; } = _ => true;

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// 실제 구현. 재적용은 관리자이고 폴더 권한이 안전할 때만 (canApply). 저널은 게임마다 journalDir\이름.jsonl 하나를 계속 씁니다 (같은 저널 규칙: 이어하기·되돌리기).
    public static ReapplyEnv Create(bool canApply, string journalDir, ILog? log = null, Func<string>? relocateOrder = null, Func<GameSettings, int>? relocateParallel = null)
    {
        ReapplyEnv env = null!;
        env = new()
        {
            CanApply = canApply,
            GameRunning = g => ProcessSnapshot.IsRunning(g.ExeNames),
            DiskBusy = g => DiskActivity.IsBusy(PlanFile.SplitDrive(g.OriginRoot)),
            LoadPlan = PlanFile.Load,
            Extents = FileLayout.Extents,
            Stat = PlanFile.FingerprintOf,
            RealPath = RealPaths.Real,
            Apply = (g, plan, injected, ct) => RunLocked(journalDir, g, plan, ChooseOptions(g, injected, relocateOrder?.Invoke() ?? RelocateOrder.Target, relocateParallel?.Invoke(g) ?? 1), ct, log), // 존을 다시 정하면 계획에 남김
            TierActive = g => TierEngine.HasActive(Path.Combine(Path.GetDirectoryName(journalDir) ?? journalDir, "tier"), g.Name),
        };
        return env;
    }

    /// 적용 (dry-run 포함) 전체를 CLI 와 같은 DataLock 안에서 실행합니다. 잠금을 못 잡으면 아무것도 옮기지 않고 Stopped = DataLock.BusyReason 인 빈 보고서를 돌려줍니다 (오류가 아니라 다음 검사에서 다시).
    /// 잠금을 잡은 뒤 디스크의 활성 계획이 넘겨받은 plan 과 다르면 (기다리는 동안 계획이 바뀜) PlanRejectedException (DataLock.PlanChangedMessage) 으로 거절해 옛 계획을 되살리지 않고,
    /// 존 예약은 잠금 안에서 다시 조회해 options.Reserved 를 덮어씁니다 (잠금 전 스냅샷을 쓰지 않음).
    public static ApplyReport RunLocked(string journalDir, GameSettings g, PlanFile plan, ApplyOptions options, CancellationToken ct, ILog? log = null) =>
        RunLocked(journalDir, g, plan, options, o => Run(journalDir, g, plan, o, ct, log));

    /// 위 RunLocked 의 실행 부분을 바꿔 끼울 수 있는 형태 (run 은 잠금 안에서 예약을 다시 채운 옵션을 받음). 시험이 씁니다.
    public static ApplyReport RunLocked(string journalDir, GameSettings g, PlanFile plan, ApplyOptions options, Func<ApplyOptions, ApplyReport> run)
    {
        using DataLock? gate = Lock(journalDir, g);
        if (gate is null)
        {
            return new ApplyReport { Stopped = DataLock.BusyReason };
        }

        if (!File.Exists(g.Plan) || PlanFile.Load(g.Plan).Sha() != plan.Sha())
        {
            throw new PlanRejectedException(DataLock.PlanChangedMessage);
        }

        return run(options with { Reserved = ReservedFor(g) });
    }

    /// 서비스가 정한 옵션 (회당 쓰기 상한 포함) 이 있으면 그것, 없으면 OptionsFor 의 기본.
    public static ApplyOptions ChooseOptions(GameSettings g, ApplyOptions? injected, string orderSetting, int parallel) =>
        injected is null ? OptionsFor(g, orderSetting, parallel) : injected with { SaveTo = g.Plan };

    /// 이 게임이 놓인 디스크 종류를 모르거나 USB 버스인가 (실패하면 모름으로 침).
    public static bool IsSlowBus(GameSettings g) => DiskInfo.Query(PlanFile.SplitDrive(g.OriginRoot)) is var d && (d.IncursSeekPenalty is null || d.BusType == DiskInfo.BusTypeUsb);

    /// 이 게임이 놓인 디스크가 SMR 로 보이는 HDD 인가 (자동 재배치와 같은 판정, 실패하면 아님).
    public static bool IsSmr(GameSettings g) => DiskInfo.Query(PlanFile.SplitDrive(g.OriginRoot)) is var d && DiskInfo.Kind(d.IncursSeekPenalty, d.Trim) == "HDD (looks like SMR)";

    /// 재적용의 동시 수: 자동 재배치와 같은 규칙 (명시값, SMR 이면 1, 느린 버스 2, 그 밖에 8). 디스크 질의는 시험이 바꿔 끼울 수 있음.
    public static int ParallelFor(AutoSettings auto, GameSettings g, Func<GameSettings, bool>? slowBus = null, Func<GameSettings, bool>? smr = null) =>
        auto.ParallelFor((slowBus ?? IsSlowBus)(g), (smr ?? IsSmr)(g));

    /// 재적용의 옵션: 실행 순서는 설정 auto.relocate_order 를 따름 (자동 재배치와 같은 규칙).
    public static ApplyOptions OptionsFor(GameSettings g, string orderSetting, int parallel = 1) =>
        new(Execute: true, SaveTo: g.Plan, Order: RelocateOrder.Resolve(orderSetting), Parallel: parallel);

    /// 레이블 확인 → 볼륨 열기 → Applier. 재적용과 자동 모드의 첫 재배치가 같은 길을 씁니다 (DryRun 은 존 시작만 계획에 저장).
    /// lowIoLog 가 있으면 이동과 SHA 표본 읽기에 낮은 I/O 우선순위 힌트를 붙이고 (서비스의 백그라운드 경로), 힌트가 실패하면 거기에 한 번 남깁니다. null 이면 보통 우선순위.
    public static ApplyReport Run(string journalDir, GameSettings g, PlanFile plan, ApplyOptions options, CancellationToken ct, ILog? lowIoLog = null)
    {
        IoPriorityHint? hint = lowIoLog is null || !options.LowIoPriority ? null : new IoPriorityHint(err => lowIoLog.Warn($"{g.Name}: low I/O priority hint not applied (Win32 error {err}), continuing at normal priority"));
        using NtfsVolume vol = OpenChecked(g);
        if (hint is not null)
        {
            vol.UseLowIoPriority(hint);
        }

        Directory.CreateDirectory(journalDir);
        return Applier.Apply(plan, vol, g.AllowedRoots, JournalPath(journalDir, g), Hooks(hint), options, ct);
    }

    /// 저널대로 되돌립니다 (skipRows 줄 뒤부터만, 게임이 뜨면 조각 사이에서 멈춤). 저널이 없으면 (아무것도 옮기지 않았음) null.
    public static RevertReport? Revert(string journalDir, GameSettings g, int skipRows = 0, CancellationToken ct = default)
    {
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return null;
        }

        using NtfsVolume vol = OpenChecked(g);
        return Reverter.Revert(journal, vol, g.AllowedRoots, Hooks(null), new ApplyOptions(Execute: true), ct, skipRows);
    }

    /// 이 게임의 계획 폴더·저널 폴더 잠금을 잡습니다 (서비스의 게임 단위 변경 구간용, 이미 이 스레드가 잡았으면 재진입). 못 잡으면 null (보류).
    public static DataLock? Lock(string journalDir, GameSettings g) => DataLock.TryAcquire(g.Plan, journalPath: JournalPath(journalDir, g));

    /// 끝난 저널을 <이름>.jsonl.<UTC 시각> 으로 옮겨 둡니다 (Applier 가 존을 옮길 때 보관하는 것과 같은 이름). 저널이 없으면 null.
    /// DataLock 안에서 실행하며 (이미 잡은 구간이면 재진입), 못 잡으면 PlanRejectedException (DataLock.BusyMessage).
    public static string? ArchiveJournal(string journalDir, GameSettings g)
    {
        using DataLock gate = Lock(journalDir, g) ?? throw new PlanRejectedException(DataLock.BusyMessage);
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return null;
        }

        string dest = PlanSwitch.ArchiveName(journal, DateTime.UtcNow); // 같은 초에 두 번 보관해도 이름이 겹치지 않음
        File.Move(journal, dest);
        return dest;
    }

    /// 같은 볼륨의 다른 게임 계획이 아직 쓸 존 범위 (계획 폴더의 활성 계획과 진행 중 교체 기록).
    public static IReadOnlyList<(long Lcn, long Clusters)> ReservedFor(GameSettings g) =>
        ZoneReservations.Collect(Path.GetDirectoryName(Path.GetFullPath(g.Plan)) ?? ".", PlanFile.SplitDrive(g.OriginRoot), g.Plan);

    /// 부분 적용된 이전 목표를 지키는 잔여 재계획 (ResidualReplan) 으로 활성 계획을 바꿉니다. sessions 가 있으면 선읽기 파일도 같이 다시 씁니다.
    /// DataLock 안에서 실행하며, 잠금을 못 잡으면 PlanRejectedException (DataLock.BusyMessage) 으로 알려 호출자의 기존 보류 처리를 탑니다.
    public static PlanFile ReplanResidual(string journalDir, GameSettings g, PlanFile candidate, IReadOnlyList<PlanFile> olds, IReadOnlyList<IReadOnlyList<TraceEvent>>? sessions)
    {
        using DataLock gate = Lock(journalDir, g) ?? throw new PlanRejectedException(DataLock.BusyMessage);
        using NtfsVolume vol = OpenChecked(g);
        Directory.CreateDirectory(journalDir);
        Action<PlanFile, string>? save = sessions is null ? null : (p, path) => PlanOutput.Save(p, sessions, path);
        return ResidualReplan.Run(candidate, g.Plan, JournalPath(journalDir, g), vol, ReservedFor(g), Hooks(null), () => DateTime.UtcNow, save, olds).Plan;
    }

    /// 중단된 계획 교체를 끝까지 진행합니다 (없으면 null). DataLock 안에서 실행하고, 못 잡으면 PlanRejectedException (DataLock.BusyMessage).
    /// 활성 계획이나 저널이 기록과 어긋나면 PlanRejectedException 으로 거절하고 기록은 그대로 둡니다.
    public static SwitchResult? ResumeSwitch(string journalDir, GameSettings g)
    {
        using DataLock gate = Lock(journalDir, g) ?? throw new PlanRejectedException(DataLock.BusyMessage);
        return PlanSwitch.Resume(g.Plan, JournalPath(journalDir, g));
    }

    /// 게임 저널 header 의 plan_sha (저널이 없거나 행이 없으면 null).
    public static string? JournalPlanSha(string journalDir, GameSettings g)
    {
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return null;
        }

        using var jr = new Journal(journal);
        return jr.Rows.FirstOrDefault(r => Equals(r["type"], "header")) is { } h && h.TryGetValue("plan_sha", out object? sha) ? sha as string : null;
    }

    /// 저널의 현재 줄 수 (없으면 0).
    public static int JournalRows(string journalDir, GameSettings g)
    {
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return 0;
        }

        using var jr = new Journal(journal);
        return jr.Rows.Count;
    }

    private static string JournalPath(string journalDir, GameSettings g) => Path.Combine(journalDir, g.Name + ".jsonl");

    private static ApplyHooks Hooks(IoPriorityHint? hint) => new()
    {
        Extents = FileLayout.Extents,
        Digest = (p, size, extra) => SampleDigest.Compute(p, size, extra, hint: hint),
        EdgeDigest = (p, size, extra) => SampleDigest.Compute(p, size, extra, span: 0, hint: hint),
        SkipReason = p => FileGuards.SkipReason(p),
        RealPath = RealPaths.Real,
    };

    private static NtfsVolume OpenChecked(GameSettings g)
    {
        string letter = PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();
        if (!string.Equals(VolumeInfo.Label(letter), g.VolumeLabel, StringComparison.OrdinalIgnoreCase))
        {
            throw new PlanRejectedException("volume label mismatch");
        }

        return NtfsVolume.Open(letter);
    }
}

/// 재적용 하나를 감싸는 취소 창. 게임 시작 알림이 Cancel 을 부르면 지금 도는 재적용만 멈추고 (한 조각을 끝낸 뒤), 돌고 있지 않으면 아무 일도 없습니다.
public sealed class ReapplyGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;

    public T Run<T>(CancellationToken stop, Func<CancellationToken, T> work)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_lock)
        {
            _current = cts;
        }

        try
        {
            return work(cts.Token);
        }
        finally
        {
            lock (_lock)
            {
                _current = null; // Cancel 이 잠금 안에서만 _current 를 건드리므로 이 뒤에 Dispose 해도 안전
            }

            cts.Dispose();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _current?.Cancel();
        }
    }
}

/// 게임 하나의 주기 검사: 지문 비교 → verify_layout (연속 여부, 존 적중률은 참고 로그) → (게임 미실행·디스크 유휴·권한 OK 이면) Applier 재적용.
public sealed class ReapplyChecker
{
    private readonly ReapplyEnv _env;
    private readonly ILog _log;
    private readonly HashSet<string> _noPlanLogged = new(StringComparer.OrdinalIgnoreCase); // "계획 없음"은 게임당 한 번만
    private readonly Dictionary<string, DateTimeOffset> _warnedAt = new(StringComparer.Ordinal); // 배치 풀림 경고를 낸 시각 (게임당 하루 한 번)
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal); // 취소로 반쯤 적용된 게임

    public ReapplyChecker(ReapplyEnv env, ILog log)
    {
        _env = env;
        _log = log;
    }

    public ReapplyVerdict Check(GameSettings game, CancellationToken cancellationToken = default)
    {
        try
        {
            PlanFile plan;
            try
            {
                plan = _env.LoadPlan(game.Plan);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                if (_noPlanLogged.Add(game.Name))
                {
                    _log.Info($"{game.Name}: no plan file yet, nothing to check"); // 아직 계획을 만들지 않은 게임은 오류가 아님
                }
                return ReapplyVerdict.NotApplied;
            }

            if (_env.TierActive(game))
            {
                _log.Info($"{game.Name}: layout check skipped, ssd tier links are active");
                return ReapplyVerdict.Healthy;
            }

            CheckPaths(plan, game, _env.RealPath);
            bool applied = plan.Zone.StartLcn is not null;
            List<bool?> stamps = applied ? [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Select(f => Compare(f, _env.Stat))] : [];
            bool changed = stamps.Contains(true);
            bool unknown = !changed && stamps.Contains(null);
            LayoutReport? layout = applied && !changed ? LayoutVerifier.Verify(plan, _env.Extents) : null;
            // 파일 배치를 못 읽은 항목이 있으면 (희소 파일 등) 판단할 수 없으므로 건강으로 봅니다: 같은 이유로 매 주기 재적용하며 0 바이트를 옮기는 일을 피함.
            bool? contiguous = layout is null || layout.HotClusters == 0 || layout.MissingFiles > 0 ? null : layout.Contiguous;
            ReapplyVerdict v = ReapplyPolicy.Decide(new ReapplyInputs(applied, changed, contiguous, () => _env.GameRunning(game), () => _env.DiskBusy(game), _env.CanApply, () => _env.Gate(game), unknown));
            _log.Info($"{game.Name}: layout check {v}" + (layout is null ? string.Empty : $" ({LayoutState(layout)}{ZoneHit(layout)})"));
            if (contiguous == false && _cancelled.Contains(game.Name))
            {
                _log.Info($"{game.Name}: partially applied (an earlier reapply was cancelled by a game start); resuming");
            }
            else if (contiguous == false)
            {
                // 설계 2.3-6: 원본이 그대로인데 배치가 풀렸으면 정기 드라이브 최적화가 되돌렸을 수 있음. 자동으로 해제하지 않고 알리기만 함 (지문이 확실하고 경고할 만한 때만, 게임당 하루 한 번).
                DateTimeOffset now = _env.Now();
                if (!unknown && _env.WarnDegraded(game) && (!_warnedAt.TryGetValue(game.Name, out DateTimeOffset last) || now - last >= TimeSpan.FromDays(1)))
                {
                    _warnedAt[game.Name] = now;
                    _log.Warn($"{game.Name}: layout degraded while source files are unchanged (cause unknown; a scheduled drive optimization or another tool may have moved files; not changed automatically)");
                }
            }

            if (v == ReapplyVerdict.Reapply)
            {
                ApplyOptions? injected = _env.Options(game); // Started 가 집계를 늘리기 전에 한 번만 계산해 Apply 에도 그대로 넘김
                long cap = (injected ?? new ApplyOptions()).WriteCap;
                long planned = Math.Min(plan.Tasks.Sum(t => t.Clusters) * plan.ClusterBytes, cap);
                _env.Started(game, planned); // 예정 바이트 (근사: 계획 전체, 회당 상한까지)
                ApplyReport rep;
                try
                {
                    rep = _env.Apply(game, plan, injected, cancellationToken);
                }
                catch (PlanRejectedException e) when (e.Message == DataLock.PlanChangedMessage)
                {
                    _env.Spent(game, 0); // 잠금을 기다리는 동안 계획이 바뀜: 아무것도 옮기지 않았으니 다음 검사에서 새 계획으로 다시
                    _log.Info($"{game.Name}: reapply deferred, the plan changed while waiting for the zone lock");
                    return ReapplyVerdict.DiskBusy;
                }
                catch
                {
                    _env.Spent(game, planned); // 예외로 끝나도 예정 바이트로 Spent 를 불러 예산에 남기고 SMR 회복 대기를 시작시킴 (과대 집계가 안전)
                    throw;
                }

                _env.Spent(game, rep.MovedBytes);

                if (rep.Stopped == DataLock.BusyReason)
                {
                    _log.Info($"{game.Name}: reapply deferred, another apply or plan switch holds the zone lock");
                    return ReapplyVerdict.DiskBusy;
                }

                if (rep.ZoneRetargeted)
                {
                    _log.Info($"{game.Name}: previous zone was taken by another file, zone moved" + (rep.JournalArchivedTo is { } a ? $", earlier journal kept as {a}" : string.Empty));
                }

                _log.Info($"{game.Name}: reapplied, moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed" + (rep.OrderUsed is { } ou ? $", order {ou}" + (rep.OrderFallbackReason is not null ? " (fallback)" : string.Empty) : string.Empty) + (rep.Stopped is { } s ? $", stopped: {s}" : string.Empty));
                if (rep.Stopped == "cancelled")
                {
                    _cancelled.Add(game.Name);
                    return ReapplyVerdict.Cancelled;
                }

                _cancelled.Remove(game.Name);
            }

            return v;
        }
        catch (ShaMismatchException e)
        {
            _log.Error($"{game.Name}: DATA CHECK FAILED after move, journal kept for revert", e);
            return ReapplyVerdict.Failed;
        }
        catch (Exception e) when (e is PlanRejectedException or NotAdminException or PyValueException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Error($"{game.Name}: layout check failed", e);
            return ReapplyVerdict.Failed;
        }
    }

    /// 계획 안의 모든 경로가 (링크를 푼 뒤) 허용 루트 안인가. 서비스는 SYSTEM 이라 계획 파일을 그대로 믿지 않습니다.
    public static void CheckPaths(PlanFile plan, GameSettings game, Func<string, string> realPath)
    {
        foreach (string p in plan.Tasks.Select(t => t.Path).Concat(plan.Order.Select(o => o.Path)).Distinct(StringComparer.Ordinal))
        {
            if (!PlanGuards.Inside(p, game.AllowedRoots, realPath))
            {
                throw new PlanRejectedException("outside_allowed_root in plan");
            }
        }
    }

    /// 계획 뒤 파일이 바뀌었는가 (크기·수정 시각, 없어짐 포함): true 바뀜, false 같음, null 읽을 수 없어 모름 (잠김·권한).
    /// 자동 재배치와 재적용 검사가 같은 기준을 씁니다: 모름은 재적용만 미루고 재계획은 하지 않습니다.
    public static bool? Compare(Fingerprint fp, Func<string, Fingerprint> stat)
    {
        try
        {
            Fingerprint now = stat(fp.Path);
            return now.Size != fp.Size || now.MtimeNs != fp.MtimeNs;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// 모름을 바뀜으로 치는 판정 (핀 해제처럼 확실할 때만 유지하는 곳).
    public static bool Changed(Fingerprint fp, Func<string, Fingerprint> stat) => Compare(fp, stat) != false;

    private static string LayoutState(LayoutReport r) =>
        r.MissingFiles > 0 ? "layout unreadable for some files" : r.Contiguous ? "contiguous" : r.ResumeNeeded ? "resume-needed" : "non-contiguous";

    // 참고용: 핫 데이터 중 존 안 클러스터 비율 (보존 구간이 있는 계획에서는 낮아도 정상).
    private static string ZoneHit(LayoutReport r) =>
        r.HotClusters > 0 && r.InZoneClusters is { } z ? $", zone hit {100.0 * z / r.HotClusters:F1}%" : string.Empty;
}
