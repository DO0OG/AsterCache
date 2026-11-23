using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
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

    // 스파스 파일의 할당 구간이 계획 뒤 바뀌어 보류됐으면 완료로 확정하지 않고 UnreadableRetry 뒤 다시 계획합니다 (읽지 못한 파일 보류와 같은 흐름). 보류가 없으면 거짓.
    private bool HoldSparseChanged(GameSettings g, long count)
    {
        if (count <= 0)
        {
            return false;
        }

        DateTimeOffset until = _env.Now() + UnreadableRetry;
        string text = $"{count} sparse file(s) changed allocation, planning again after {until.ToUniversalTime():HH:mm} UTC";
        _unreadableHold[g.Name] = (until, text);
        Note(g, AutoStage.WaitingIdle, text);
        _log.Warn($"{g.Name}: {count} sparse file(s) changed allocation since the plan, held, will plan again later");
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
                    SetAsideForReplan(g, "held files remain");
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

    /// 같은 점유 사유가 이 횟수째 연속되면 보류 시간을 늘리기 시작함.
    public const int OccupiedStreakGrow = 3;

    /// 늘린 보류 시간의 상한.
    public static readonly TimeSpan RecoveryHoldMax = TimeSpan.FromHours(4);

    private static readonly Regex RejectedLcn = new(@"LCN (\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static long? LcnIn(string message) => RejectedLcn.Match(message) is { Success: true } m ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    // 다른 프로그램이 존 자리를 차지해 막힌 사유인가 (실행 전 확인의 zone_not_free, 실행 중 연속 점유 중단).
    // 다른 계획의 예약과 겹친 거절 (reserved by another plan) 은 그 계획이 끝나면 풀리므로 다른 프로그램의 점유로 세지 않음.
    private static bool IsOccupiedReason(string? reason) => reason is not null
        && (reason.StartsWith("zone_not_free", StringComparison.Ordinal) && !reason.Contains("reserved by another plan", StringComparison.Ordinal) || reason == Applier.ZoneOccupied);

    /// 이 가까움 (클러스터) 안이면 "옮긴 조각 끝 바로 뒤" 로 봄 (4 KiB 클러스터에서 4 MiB).
    public const long ProgressNearClusters = 1024;

    /// 거절된 LCN 이 존의 어디였는지: 존 앞 여유 안 / 존 안에서 옮긴 마지막 조각 끝 바로 뒤 (진행 지점) / 존 안 그 밖 / 존 밖.
    /// runStart: 계획 stats 의 여유를 둔 빈 구간 시작 (없으면 null). progressEnd: 존 안에서 이미 옮긴 마지막 조각의 끝 (없으면 null). 계획·저널에 이미 있는 값만 씀.
    public static string RejectedPlace(long lcn, long zoneStart, long zoneClusters, long? runStart, long? progressEnd)
    {
        if (lcn < zoneStart)
        {
            return runStart is { } run && lcn >= run ? $"in the margin before the zone ({zoneStart - lcn} clusters before its start)" : "outside the zone";
        }

        if (lcn >= zoneStart + zoneClusters)
        {
            return "outside the zone";
        }

        return progressEnd is { } end && lcn >= end && lcn - end < ProgressNearClusters
            ? $"right after the last moved piece ({lcn - end} clusters after its end)"
            : "inside the zone, not next to the moved part";
    }

    // 점유로 막힌 자리를 한 줄 로그로: 이 로그는 점유가 다른 프로그램의 새 파일 자리인지 이동 진행 지점인지 가르는 근거. lcn 이 없으면 (실행 중 중단) 첫 실패 단계의 목표.
    private void LogRejectedPlace(GameSettings g, PlanFile plan, string? message, long? failedIdx = null)
    {
        try
        {
            if (plan.Zone.StartLcn is not { } zoneStart || message is not null && message.Contains("reserved by another plan", StringComparison.Ordinal))
            {
                return;
            }

            var cache = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
            IReadOnlyList<Extent>? Ext(string path) => cache.TryGetValue(path, out IReadOnlyList<Extent>? e) ? e : cache[path] = _env.Extents(path);
            long? progressEnd = null;
            foreach (PlanTask t in plan.Tasks.Where(t => t.TargetLcn >= zoneStart && t.TargetLcn < zoneStart + plan.ZoneLength))
            {
                long done = PlanGuards.DonePrefix(Ext(t.Path), t.Vcn, t.Clusters, t.TargetLcn);
                if (done > 0)
                {
                    progressEnd = Math.Max(progressEnd ?? 0, t.TargetLcn + done);
                }
            }

            long? lcn = message is not null ? LcnIn(message)
                : failedIdx is { } i && i >= 0 && i < plan.Tasks.Count ? plan.Tasks[(int)i].TargetLcn + PlanGuards.DonePrefix(Ext(plan.Tasks[(int)i].Path), plan.Tasks[(int)i].Vcn, plan.Tasks[(int)i].Clusters, plan.Tasks[(int)i].TargetLcn) : null;
            long? runStart = plan.Stats.TryGetValue(PlanGuards.ZoneRunStartKey, out object? rs) && rs is long r ? r : null;
            if (lcn is { } at)
            {
                _log.Info($"{g.Name}: zone rejected at LCN {at}: {RejectedPlace(at, zoneStart, plan.ZoneLength, runStart, progressEnd)} (zone {zoneStart}, moved up to {progressEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"})");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 진단 로그일 뿐: 읽지 못하면 남기지 않음
        }
    }

    /// 점유 사유가 streak 번째 연속일 때의 보류 시간: 3 번째부터 두 배씩 (30 분 → 1 시간 → 2 시간 → 4 시간 상한).
    public static TimeSpan RecoveryHoldFor(int streak) =>
        streak < OccupiedStreakGrow ? RecoveryHold : TimeSpan.FromTicks(Math.Min(RecoveryHoldMax.Ticks, RecoveryHold.Ticks << Math.Min(streak - OccupiedStreakGrow + 1, 8)));

    /// 의미 있는 양 (횟수로 세는 최소 바이트) 을 옮겼으면 점유가 연속된 횟수를 지움.
    public static RecoveryAttempt ClearOccupiedStreakIfMoved(RecoveryAttempt attempt, long movedBytes) =>
        movedBytes >= AutoStatus.CountedRelocationMinBytes ? attempt with { OccupiedStreak = 0 } : attempt;

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

        // 잔여 재계획마다 계획 SHA 가 바뀌어 위의 표식 비교가 걸리지 않으므로, 같은 점유 사유가 연속된 횟수를 따로 셈 (다른 사유가 끼면 다시 1)
        int streak = IsOccupiedReason(reason) ? (IsOccupiedReason(prior.Reason) ? prior.OccupiedStreak : 0) + 1 : 0;
        TimeSpan hold = RecoveryHoldFor(streak);
        DateTimeOffset until = _env.Now() + hold;
        _status.SetRecovery(g.Name, prior with { RejectedLcn = LcnIn(why) ?? prior.RejectedLcn, NextAllowed = until, Reason = reason, Stamp = stamp, OccupiedStreak = streak });
        Note(g, AutoStage.WaitingIdle, HoldText(until));
        _log.Warn($"{g.Name}: recovery held until {until.ToUniversalTime():u} ({FirstLine(why)})");
        if (streak >= OccupiedStreakGrow)
        {
            _log.Warn($"{g.Name}: zone occupied {streak} times in a row, another program keeps writing to this drive, hold extended to {hold.TotalMinutes:F0} min");
        }
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
