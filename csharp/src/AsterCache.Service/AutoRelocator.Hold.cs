using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 계획 거절·일시 오류 보류와 재시도 시각 관리.
public sealed partial class AutoRelocator
{
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
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "auto relocation", token);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return new ApplyReport { Stopped = "cancelled" };
            }

            ApplyOptions coordinated = options with { CanIssue = () => lease?.IsValid ?? !token.IsCancellationRequested };
            return _env.Apply(g, plan, coordinated, lease?.Token ?? token);
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
                using IDisposable? gate = _env.Lock(g);
                if (gate is null)
                {
                    DateTimeOffset until = _env.Now() + TransientRetry;
                    _unreadableHold[g.Name] = (until, ZoneLockBusyDetail); // 보류를 이어서 다시 시도
                    Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail);
                    return false;
                }

                if (_env.FileExists(g.Plan))
                {
                    SetAsideForReplan(g);
                }
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
}
