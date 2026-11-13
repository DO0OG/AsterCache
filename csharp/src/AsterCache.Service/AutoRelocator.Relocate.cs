using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// AutoRelocator 의 첫 재배치 실행, 실패·되돌리기 처리.
public sealed partial class AutoRelocator
{
    private void Relocate(GameSettings g, AsterSettings s, CancellationToken token)
    {
        if (AnyGameRunning(g))
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
            using IDisposable? zone = _env.Lock(g); // 계획 만들기·적용·되돌리기·보관 전체를 한 번의 잠금으로 감쌈 (안쪽 호출은 재진입): CLI 가 끼어들어 계획을 바꾸지 못함
            if (zone is null)
            {
                Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail); // 아무것도 바꾸지 않음: 다음 틱에 다시
                return;
            }

            RelocateMarked(g, s, token);
        }
        finally
        {
            _prewarm?.EndRelocation(g.Name); // 성공·실패·예외 모두 표시를 내림
        }
    }

    private void RelocateMarked(GameSettings g, AsterSettings s, CancellationToken token)
    {
        if (_extensionRecovery.TryRemove(g.Name, out _) && IsExtending(g))
        {
            Note(g, AutoStage.Relocating, null);
            _ = BlockFailedExtension(g, "resumed extension recovery", null, 0, token, newRound: true); // 새 이동보다 먼저 남은 되돌리기와 저널 보관을 마저 끝냄 (되돌리기는 이미 옮겨진 조각을 건너뛰어 다시 해도 안전)
            return;
        }

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

            bool changed = stamps.Contains(true);
            if (changed || HoldsVolatile(plan) && _volatileChecked.Add(g.Name)) // 부분 적용된 계획도 새 규칙의 가변 파일을 들고 있으면 한 번 잔여 재계획 (이미 옮긴 조각은 지킴)
            {
                _log.Info(changed ? $"{g.Name}: source files changed since the plan, planning again" : $"{g.Name}: plan holds files the game rewrites by itself, planning again"); // 없어진 파일은 새 계획에서 빠짐
                if (changed)
                {
                    DropChangedRanges(g, plan);
                }

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

            if (dry.Stopped == "cancelled")
            {
                Note(g, AutoStage.WaitingIdle, "dry-run cancelled (disk busy or game start), will retry");
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
                if (_env.HasPendingVerification(g))
                {
                    // 이동은 끝났지만 미룬 SHA 검증이 남음: 빈 작업으로 한 번 돌려 저널의 미검증 표본을 확인 (대여·재검사 규칙 동일). 끝나기 전에는 완료로 확정하지 않음. 불일치는 ShaMismatchException 으로 기존 처리를 탐.
                    ApplyReport verified;
                    try
                    {
                        plan = _env.LoadPlan(g.Plan); // 낡은 계획 객체 대신 지금 파일을 씀 (일반 경로와 같음)
                        ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
                        verified = ApplyOrDefer(g, plan, BaseApplyOptions(s, g), token);
                    }
                    catch (Exception e) when (IsTransient(e))
                    {
                        HoldTransient(g, e); // 검증 읽기 중 일시 오류: 아무것도 바꾸지 않았고 검증은 남아 있음, 실패로 기록하지 않고 곧 다시
                        return;
                    }
                    if (verified.Stopped == DataLock.BusyReason)
                    {
                        Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail);
                        return;
                    }

                    if (verified.Stopped is not null)
                    {
                        Note(g, AutoStage.WaitingIdle, "verification deferred (game start or disk busy), will retry");
                        return;
                    }
                    if (verified.MovedBytes > 0 || verified.TasksTodo != 0)
                    {
                        return; // 옮긴 조각이나 남은 할 일이 있으면 완료로 확정하지 않음: 예산 집계는 다음 틱의 일반 경로에서 처리
                    }

                }

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
                    Fail(g, $"{rep.Failed.Count} step(s) failed", null); // 데이터는 그대로: StoppedRecheck 뒤 자동 재개가 저널을 이어 남은 이동을 함
                }
            }
            else
            {
                LayoutReport layout = LayoutVerifier.Verify(_env.LoadPlan(g.Plan), _env.Extents, _env.Unmovable);
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
            if (!BlockFailedExtension(g, DataCheckDetail, e, mark, token))
            {
                Fail(g, DataCheckDetail, e);
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

    // 게임이 안 돌 때만, 이번 자동 적용이 저널에 쓴 줄 (mark 줄 뒤) 만 되돌립니다 (최선 노력). 앞선 적용의 줄은 이어하기·수동 되돌리기의 몫이라 건드리지 않음.
    // 되돌리기를 끝냈으면 참 (되돌릴 것이 없었던 경우 포함), 게임 실행·중단·실패로 못 끝냈으면 거짓.
    private bool Undo(GameSettings g, int mark, CancellationToken token, bool newRound = false)
    {
        if (AnyGameRunning(g))
        {
            _log.Warn($"{g.Name}: game is running, revert skipped (journal kept)");
            return false;
        }

        void Count(long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            if (newRound)
            {
                _status.BeginRelocation(_env.Now(), bytes);
            }
            else
            {
                _status.AddToLastRelocationBytes(bytes); // 되돌리기도 쓰기: 그 회차 집계에 합산
            }
        }

        try
        {
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "auto relocation revert", token);
            CancellationToken revertToken = lease?.Token ?? token;
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                _log.Info($"{g.Name}: revert deferred because the disk is yielded (journal kept)");
                return false;
            }

            RevertReport? r = _env.JournalRows(g) <= mark ? null : _env.Revert(g, mark, revertToken);
            Count(r?.MovedBytes ?? 0);

            bool complete = r is null || (r.Stopped is null && r.Skipped.Count == 0); // 옮기지 못한 조각이 하나라도 있으면 미완료 (저널을 보관하면 되돌릴 길이 없어짐)
            _log.Info(r is null ? $"{g.Name}: nothing to revert" : $"{g.Name}: reverted {r.Restored} piece(s), {r.Skipped.Count} skipped" + (r.Stopped is { } why ? $", stopped: {why}" : string.Empty) + (complete ? string.Empty : ", journal kept"));
            return complete;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Count(e.Data[Reverter.MovedBytesDataKey] is long moved ? moved : 0); // 이동 뒤 저널 쓰기 등에서 예외로 끝나도 이미 쓴 바이트는 집계에 넣음
            _log.Error($"{g.Name}: revert failed, journal kept", e);
            return false;
        }
    }

    private const string DataCheckDetail = "data check failed";

    private const string ExtensionRevertDetail = "extension revert incomplete";

    private const string ExtensionArchiveDetail = "extension journal could not be archived";

    // 실패 분류: 무결성 실패 (data check failed, SHA 불일치) 만 사용자 확인이 필요해 계속 멈추고, 그 밖 (이동 실패·예외·계획 실패·멈춘 복구·확장 정리 실패) 은 StoppedRecheck 마다 자동으로 다시 열림.
    private static bool IsRetriedHourly(string? detail) => detail is not null && !detail.StartsWith(DataCheckDetail, StringComparison.Ordinal);

    private static bool IsExtensionFailure(string detail)
        => detail.StartsWith(ExtensionRevertDetail, StringComparison.Ordinal) || detail.StartsWith(ExtensionArchiveDetail, StringComparison.Ordinal);

    private void Fail(GameSettings g, string detail, Exception? e)
    {
        _planFailedAt[g.Name] = _env.Now();
        _status.Set(g.Name, g.Source, null, AutoStage.Failed, detail);
        _log.Error($"{g.Name}: auto relocation stopped: {detail}", e);
    }
}
