using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// AutoRelocator 의 중단된 재계획·재배치 복구와 남은 부분 다시 계획.
public sealed partial class AutoRelocator
{
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
            using IDisposable? gate = _env.Lock(g);
            if (gate is null)
            {
                _recoverBusy = true;
                continue;
            }

            if (!_env.FileExists(g.Plan))
            {
                continue; // 기다리는 동안 계획이 없어짐
            }

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
            using IDisposable? gate = _env.Lock(g);
            if (gate is null)
            {
                _recoverBusy = true;
                continue;
            }

            try
            {
                if (_env.FileExists(g.Plan) && IsExtending(g) && _env.LoadPlan(g.Plan).Zone.StartLcn is null)
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
            using IDisposable? gate = _env.Lock(g);
            if (gate is null)
            {
                _recoverBusy = true;
                continue;
            }

            if (_env.FileExists(g.Plan))
            {
                continue; // 기다리는 동안 계획이 생김: 되살리지 않음
            }

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

        if (_recoverBusy)
        {
            _recoverBusy = false;
            _recovered = false; // 잠금을 못 잡아 건너뛴 게임이 있음: 다음 틱에 시작 복구를 처음부터 다시 (각 단계는 조건을 보고 하므로 반복해도 안전)
        }
    }

    private bool _recoverBusy; // 틱 스레드에서만 씀: 이번 시작 복구에서 잠금을 못 잡아 건너뛴 게임이 있음

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
            LayoutReport r = LayoutVerifier.Verify(plan, _env.Extents, _env.Unmovable);
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
        using IDisposable? gate = _env.Lock(g); // 활성 계획을 후보로 쓰므로 잠금 안에서 디스크의 활성 계획이 후보와 같은지 대조 (호출자가 이미 잡았으면 재진입)
        try
        {
            if (gate is null)
            {
                throw new PlanRejectedException(DataLock.BusyMessage);
            }

            if (!PlanUnchanged(g, active))
            {
                throw new PlanRejectedException(DataLock.PlanChangedMessage); // 기다리는 동안 활성 계획이 바뀜: 옛 계획으로 되돌리지 않고 일시 보류 뒤 새 활성 계획으로 다시
            }

            PlanFile built = _env.ReplanResidual!(g, active, ResidualOlds(g), SessionsFor(g, active));
            LogResidualPlacement(g, built);
            _status.SetRecovery(g.Name, new RecoveryAttempt(built.Sha(), LcnIn(cause.Message), 1, null, prior.Reason, prior.Stamp));
            _ = _seeded.Remove(g.Name);
            _ = RestoreWarmList(g);
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

    // 부분 적용 계획의 예열 목록: 계획 Order 가 아니라 병합 트레이스 기준 핫셋 (유효 존·스트림 상한 이내) 에 가변 파일 범위를 더해 씀. 반환: 핫셋 MiB.
    private long SavePartialWarm(GameSettings g, long limit, IReadOnlyList<TraceEvent> stable, IReadOnlyList<TraceEvent> warmOnly)
    {
        long cap = limit << 20;
        long budget = Math.Min(cap, g.StreamCapBytes(cap));
        List<TraceEvent> hot = PlanFile.PrefetchHotSetTrace([stable], budget);
        long hotBytes = hot.Sum(r => r.Length);
        PlanOutput.SavePrefetch(g.Trace, hot, hotBytes >= budget ? WithZoneOverflow(g, hot, stable, warmOnly) : warmOnly); // 핫셋이 예산까지 찼으면 못 든 블록도 예열 전용 목록에 (복원 재시도에서도 덧붙임이 사라지지 않게)
        return hotBytes >> 20;
    }

    // 잔여 재계획의 PlanOutput.Save 가 예열 목록을 계획 Order 만으로 덮으므로, 병합 트레이스 기준 목록 (확대한 범위와 가변 범위) 으로 다시 씀. 실패해도 이미 바뀐 계획은 그대로 둠.
    private bool RestoreWarmList(GameSettings g)
    {
        try
        {
            if (AutoTracer.PlanTracePath(g, _env.FileExists) is not { } tracePath || tracePath != AutoTracer.RawTracePath(g))
            {
                return true;
            }

            (IReadOnlyList<TraceEvent> stable, _, IReadOnlyList<TraceEvent> warmOnly) = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot));
            _ = SavePartialWarm(g, MaxZoneMib(_settings(), g), stable, warmOnly); // 재배치 완료 여부와 무관하게 병합 트레이스 핫셋 (확대한 범위) 을 보존
            return true;
        }
        catch (Exception e) when (IsTransient(e) || e is PlanRejectedException)
        {
            if (_warmRestorePending.Add(g.Name)) // 다음 틱부터 RetryWarmRestore 가 다시 씀 (경고는 처음 한 번만)
            {
                _log.Warn($"{g.Name}: prewarm list not restored after the remainder replan ({e.GetType().Name}), will retry");
            }

            return false;
        }
    }

    // 복원 대기 게임의 예열 목록을 잠금 안에서 다시 씀. 성공하면 대기에서 뺌, 실패하면 대기를 유지해 다음 틱에 다시.
    private void RetryWarmRestore(GameSettings g)
    {
        try
        {
            using IDisposable? gate = _env.Lock(g);
            if (gate is not null && RestoreWarmList(g))
            {
                _ = _warmRestorePending.Remove(g.Name);
                _log.Info($"{g.Name}: prewarm list restored on retry after the remainder replan");
            }
        }
        catch (Exception e) when (IsTransient(e) || e is PlanRejectedException)
        {
            // 계획을 읽지 못함: 대기를 유지해 다음 틱에 다시
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
}
