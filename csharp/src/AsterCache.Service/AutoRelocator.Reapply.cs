using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 업데이트 지연과 재적용 차단 판정, 재적용 옵션.
public sealed partial class AutoRelocator
{
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

    /// 업데이트 뒤 트레이스를 유지한 채 곧 다시 계획하는 상태 (KeptDetail·UnchangedKeptDetail) 인가. 다시 트레이스할 때까지 기다리는 ReplanDetail 은 거짓.
    internal static bool ReplanKept(AutoStatus status, string game) => // 보류 이유 표시로 단계가 바뀌어도 (Note) 접두가 남아 있는 동안은 참
        status.Get(game)?.Detail is { } d && (d.StartsWith(KeptDetail, StringComparison.Ordinal) || d.StartsWith(UnchangedKeptDetail, StringComparison.Ordinal));

    internal static AutoGameEntry? Replanning(AutoStatus status, string game) =>
        status.Get(game) is { Stage: AutoStage.Discovered, Detail: { } detail } e && detail.StartsWith(ReplanDetail, StringComparison.Ordinal) ? e : null;

    private static string DriveOf(GameSettings g) => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();

    // probe 모드에서 회복 추적이 있으면 디스크 회복을 확인할 때까지 (최대 recovery_max_min 분), 아니면 고정 post_update_delay_min 분을 기다립니다.
    public static string PostUpdateLine(string game, int minutes, bool probed = false) => probed
        ? $"{game}: waiting after update until the drive has recovered (up to {minutes} min)"
        : $"{game}: waiting {minutes} min after update (drive may still be reorganizing)";

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
            _log.Info(probed is null ? PostUpdateLine(g.Name, s.Auto.PostUpdateDelayMin) : PostUpdateLine(g.Name, s.Auto.RecoveryMaxMin, probed: true));
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

        if (AnyGameRunning(g))
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

    /// 정기 재적용이 존 점유 (zone_not_free) 로 거절됐을 때 자동 재배치의 잔여 재계획에 넘김: 재배치된 게임을 처음 단계로 되돌려 다음 틱이 기존 게이트·유휴 확인·dry-run 을 거쳐 잔여만 다시 계획하게 함.
    /// 넘기지 못하면 상태를 건드리지 않고 false (재적용이 기존대로 실패 처리). 트레이스가 없으면 NeedsWork 가 집지 못하는데 재적용만 막히므로 넘기지 않고, 무결성 실패로 멈춘 상태도 자동으로 풀지 않음.
    public bool HandOffZoneBlocked(GameSettings g)
    {
        AsterSettings s = _settings();
        if (!(s.Auto.Enabled && s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0) || _env.ReplanResidual is null || IsExtending(g)
            || _status.Get(g.Name) is not { RelocatedAt: not null } e || e.Stage == AutoStage.Failed && !IsRetriedHourly(e.Detail)
            || AutoTracer.PlanTracePath(g, _env.FileExists) is null)
        {
            return false;
        }

        _status.ResetForReplan(g.Name, BusyDetail);
        _log.Info($"{g.Name}: reapply blocked by zone_not_free, handing over to auto relocation to replan the remaining part");
        return true;
    }
}
