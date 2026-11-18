using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 이전 형식 계획 처리: 스파스 파일의 할당 기준값이 없는 계획에 기준값을 더해 잔여 재계획으로 한 번 바꿉니다.
public sealed partial class AutoRelocator
{
    // 스파스 파일의 할당 기준값이 없는 이전 형식 계획은 스파스 작업을 적용에서 건너뜁니다. 서비스 실행당 한 번 따져, 기준값과 표지를 더한 후보를 잔여 재계획 (ReplanResidual) 으로 교체합니다:
    // 이미 놓인 조각은 제자리 (고정 작업), 놓이지 않은 스파스 할당 구간은 다른 게임 계획의 예약을 피한 새 빈 구간에 접근 순서대로 이어 붙음 (옛 목표 자리는 이미 다른 데이터가 차지함).
    // Plan () 을 부르지 않고 상태 (_status), .extend, .stale, 저널 보관을 직접 건드리지 않습니다: 이동은 이후 정기 재적용이 일일 예산·회당 상한·게임 실행 중 미루기를 지키며 합니다.
    private void ReplanLegacySparse(AsterSettings s)
    {
        if (!(s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0) || _env.ReplanResidual is null)
        {
            return;
        }

        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsRelocate(g) || _status.Get(g.Name) is not { Stage: AutoStage.Done, RelocatedAt: not null } entry
                || entry.Detail?.StartsWith(StalePlanDetail, StringComparison.Ordinal) == true || _planFailedAt.ContainsKey(g.Name)
                || !_env.FileExists(g.Plan) || IsExtending(g) || _env.FileExists(g.Plan + StaleSuffix) || _env.FileExists(g.Plan + PlanSwitch.RecordSuffix)
                || _tier?.IsActive(g.Name) == true || _recovery.IsRelocationHeld(DriveOf(g)) || RecoveryHeldUntil(g) is not null || AnyGameRunning(g)
                || _sparseRetryAt.TryGetValue(g.Name, out DateTimeOffset retryAt) && _env.Now() < retryAt || !_sparseChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || plan.SparseAllocations.Count > 0 || plan.Stats.ContainsKey(SparseAllocation.RuleKey))
                {
                    continue; // 새 규칙으로 만든 계획 (또는 존이 아직 없는 계획) 은 대상이 아님
                }

                if (!_env.DirectoryExists(plan.OriginRoot))
                {
                    _ = _sparseChecked.Remove(g.Name);
                    _sparseRetryAt[g.Name] = _env.Now() + TransientRetry; // 원본 폴더가 없음 (드라이브가 빠짐 등): 틱마다 계획을 다시 읽지 않고 잠시 쉼
                    continue;
                }

                List<bool?> stamps = [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Select(f => ReapplyChecker.Compare(f, _env.Stat))];
                if (stamps.Contains(true))
                {
                    continue; // 원본이 바뀜: 업데이트 다시 계획이 맡음
                }

                if (stamps.Contains(null))
                {
                    _ = _sparseChecked.Remove(g.Name); // 읽지 못해 모름: 잠시 쉰 뒤 다시 (지문을 틱마다 다시 읽지 않음)
                    _sparseRetryAt[g.Name] = _env.Now() + TransientRetry;
                    continue;
                }

                if (SparseAllocation.WithMissingBaselines(plan, _env.IsSparse, _env.Extents) is not { } candidate)
                {
                    continue;
                }

                using IDisposable? gate = _env.Lock(g);
                if (gate is null || !PlanUnchanged(g, plan))
                {
                    _ = _sparseChecked.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시
                    continue;
                }

                PlanFile built = _env.ReplanResidual(g, candidate, ResidualOlds(g), null);
                LogLegacySparse(g, candidate.SparseAllocations.Count, built);
                LogResidualPlacement(g, built);
            }
            catch (PlanRejectedException e) when (e.Message.StartsWith(NoFreeRun, StringComparison.Ordinal))
            {
                // 표시를 그대로 둠: 빈 구간 부족은 곧 풀리지 않고 시도마다 볼륨 비트맵을 읽으므로 이번 서비스 실행에서는 다시 시도하지 않음
                _log.Info($"{g.Name}: legacy plan not replaced, {e.Message}; will try again on the next service start");
            }
            catch (PlanRejectedException e) when (IsTemporaryRejection(e))
            {
                _ = _sparseChecked.Remove(g.Name); // 잠금 busy·계획 변경·미검증 파일 읽기 불가 등 일시 사유: 다시 시도
                if (!IsLockBusy(e) && !e.Message.StartsWith(PlanChangedWhileWaiting, StringComparison.Ordinal))
                {
                    _sparseRetryAt[g.Name] = _env.Now() + ResourceRetry; // 읽기 불가는 곧 풀리지 않고 표본을 매번 읽게 되므로 잠시 쉼 (잠금·계획 변경은 다음 틱)
                }
            }
            catch (Exception e) when (IsTransient(e))
            {
                _ = _sparseChecked.Remove(g.Name); // 아무것도 바꾸지 않은 읽기·잠금 실패: 잠시 쉰 뒤 다시 (선례: ReplanRemainder)
                _sparseRetryAt[g.Name] = _env.Now() + TransientRetry;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: sparse baseline replan failed ({e.GetType().Name}: {FirstLine(e.Message)})");
            }
        }
    }

    // 교체 결과 한 줄. 교체가 목표를 바꾸므로 이어지는 배치 검사가 "layout degraded" 경고를 낼 수 있고, 그것은 원본 변경이 아니라 이 교체 때문에 다시 적용으로 판정된 것입니다.
    private void LogLegacySparse(GameSettings g, int baselines, PlanFile built)
    {
        long start = built.Zone.StartLcn ?? 0, length = built.Zone.Clusters;
        long sparseMoving = built.Tasks.Where(t => built.SparseAllocations.ContainsKey(t.Path) && length > 0 && t.TargetLcn >= start && t.TargetLcn < start + length).Sum(t => t.Clusters);
        int held = HeldFiles(built);
        string place = length > 0 ? $"new run starts at LCN {start} ({length} clusters)" : "nothing left to move";
        _log.Info($"{g.Name}: legacy plan replaced, {baselines} sparse file(s) gained an allocation baseline, {sparseMoving} sparse cluster(s) to move, {place}, {held} file(s) held; "
            + "the next layout check may warn \"layout degraded\" because this replacement changed the targets, and will then judge the plan as needing apply again");
    }
}
