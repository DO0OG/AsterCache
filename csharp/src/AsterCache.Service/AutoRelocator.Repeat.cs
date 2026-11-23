using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 원본이 바뀌지 않았는데 서비스가 다시 놓은 회차가 되풀이될 때 게임별로 이동을 쉬게 합니다.
/// 세는 곳: 정기 재적용이 바이트를 옮기고 돌아온 때, 그리고 원본이 그대로인데 이미 놓였던 조각이 제자리를 잃어 일어난 잔여 재계획 (존 점유로 정기 재적용이 넘긴 것 포함) 뒤의 첫 이동.
/// 처음 배치, 회당 쓰기 상한으로 나뉜 이어 옮기기, 새 계획의 이행은 세지 않습니다.
public sealed partial class AutoRelocator
{
    // 조사 사례 (한 게임에서 12일 동안 같은 조각을 다시 옮기며 하루에도 여러 번 되풀이) 에서 정한 기준: 7일 안에 3번이면 그 게임의 재배치를 쉼.
    private const int ReapplyRepeatLimit = 3;
    private static readonly TimeSpan ReapplyRepeatWindow = TimeSpan.FromDays(7);
    internal const string ReapplyRepeatDetail = "reapply paused after 3 unchanged-source rounds in 7 days";
    private static readonly Regex RepeatTag = new("(, )?" + Regex.Escape(ReapplyRepeatDetail), RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly Dictionary<string, DateTimeOffset> _repeatSourceChecked = new(StringComparer.Ordinal); // 틱 스레드에서만 씀

    public void NoteReapplyMoved(GameSettings g, long movedBytes, bool partial)
    {
        _ = RecordReapplyRepeatMove(g, movedBytes, handoffOnly: false, partial);
        _ = ReapplyRepeatHeld(g, _settings().Auto);
    }

    public void NoteReapplyHealthy(GameSettings g) => _status.ClearReapplySplit(g.Name);

    /// 쉬는 중인가 (상태는 바꾸지 않음): 기록의 배치 방식이 지금과 같고 7일 안에 3회 이상.
    private bool ReapplyPaused(GameSettings g, AutoSettings auto) =>
        _status.ReapplyLimit(g.Name) is { } record && record.Layout == GamePolicy.RelocateLayout(g, auto) && _status.ReapplyRoundsWithin(g.Name, ReapplyRepeatWindow) >= ReapplyRepeatLimit;

    /// 쉬는 중인가. 배치 방식 설정이 바뀌었으면 기록을 지우고, 게임 상세에 쉬는 사유를 붙이거나 뗍니다 (기존 상세는 그대로 둠).
    private bool ReapplyRepeatHeld(GameSettings g, AutoSettings auto)
    {
        if (_status.ReapplyLimit(g.Name) is { } record && record.Layout != GamePolicy.RelocateLayout(g, auto))
        {
            _status.ClearReapplyLimit(g.Name);
        }

        bool held = ReapplyPaused(g, auto);
        if (_status.Get(g.Name) is { Stage: AutoStage.Done or AutoStage.WaitingIdle } entry)
        {
            string? want = held ? WithRepeatTag(entry.Detail) : WithoutRepeatTag(entry.Detail);
            if (want != entry.Detail)
            {
                _ = _status.Set(g.Name, g.Source, null, entry.Stage, want);
            }
        }

        return held;
    }

    private static string WithRepeatTag(string? detail)
    {
        string rest = WithoutRepeatTag(detail) ?? string.Empty;
        return rest.Length == 0 ? ReapplyRepeatDetail : rest + ", " + ReapplyRepeatDetail;
    }

    private static string? WithoutRepeatTag(string? detail)
    {
        string? rest = detail is null ? null : RepeatTag.Replace(detail, string.Empty).Trim(' ', ',');
        return string.IsNullOrEmpty(rest) ? null : rest;
    }

    /// 쉬는 게임이 원본이 바뀌어 풀리는가: 틱이 건너뛰기 전에 StaleCheckEvery 마다 계획의 지문 목록을 지금 파일과 대조합니다 (재배치 단계의 게임은 업데이트 재계획이 보지 않음).
    /// 바뀌었으면 기록을 지우고 평소 흐름 (원본 변경을 보고 다시 계획) 으로 보냅니다. 반환: 더 이상 쉬지 않는가.
    private bool RepeatSourceReleased(GameSettings g, AutoSettings auto)
    {
        DateTimeOffset now = _env.Now();
        if (AnyGameRunning(g) || !_env.FileExists(g.Plan) || _repeatSourceChecked.TryGetValue(g.Name, out DateTimeOffset at) && now - at < StaleCheckEvery)
        {
            return false;
        }

        _repeatSourceChecked[g.Name] = now;
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            _status.EnsureReapplyLimit(g.Name, ReapplySourceKey(plan), GamePolicy.RelocateLayout(g, auto)); // 계획이 다른 원본의 것이면 기록이 새로 시작됨
            if (plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Select(f => ReapplyChecker.Compare(f, _env.Stat)).Contains(true))
            {
                _status.ClearReapplyLimit(g.Name);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false; // 읽지 못하면 바뀌었는지 모름: 계속 쉼
        }

        return !ReapplyRepeatHeld(g, auto);
    }

    /// 원본이 그대로인 채 계획을 다시 만들었음 (잔여 재계획·치워 둔 계획의 재계획): 기록은 두고, 이미 놓였던 조각이 제자리를 잃었으면 (이미 되풀이가 센 적이 있거나 잔여 계획의 고정분이 줄었음) 그 뒤 첫 이동을 한 회차로 셉니다.
    /// 처음 배치 중의 재계획은 고정분이 줄지 않고 센 회차도 없어 세지 않습니다.
    private void NoteReapplyReplan(GameSettings g, PlanFile? active, PlanFile? built)
    {
        AutoSettings auto = _settings().Auto;
        if (active is not null)
        {
            _status.EnsureReapplyLimit(g.Name, ReapplySourceKey(active), GamePolicy.RelocateLayout(g, auto));
        }

        bool lost = _status.ReapplyRoundsWithin(g.Name, ReapplyRepeatWindow) > 0 || _status.ReapplyLimit(g.Name) is { HandoffPending: true }
            || active is not null && built is not null && FixedClusters(active) is { } before && FixedClusters(built) is { } after && after < before;
        _status.NoteReapplyReplan(g.Name, lost);
    }

    private static long? FixedClusters(PlanFile plan) => plan.Stats.TryGetValue("fixed_clusters", out object? v) && v is long l ? l : null;

    private bool RecordReapplyRepeatMove(GameSettings g, long movedBytes, bool handoffOnly, bool partial = false)
    {
        (int count, bool reachedLimit) = _status.RecordReapplyRound(g.Name, movedBytes, ReapplyRepeatWindow, ReapplyRepeatLimit, handoffOnly, partial);
        if (reachedLimit)
        {
            _log.Warn($"{g.Name}: reapply paused after {count} unchanged-source rounds in {ReapplyRepeatWindow.TotalDays:0} days; automatic relocation resumes when source files change and a new plan is made, the layout setting changes, or fewer than {ReapplyRepeatLimit} rounds remain within {ReapplyRepeatWindow.TotalDays:0} days");
        }

        return reachedLimit;
    }

    private static string ReapplySourceKey(PlanFile plan)
    {
        var source = new StringBuilder();
        foreach (Fingerprint f in plan.Fingerprints.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            source.Append(f.Path.Length).Append(':').Append(f.Path).Append(':').Append(f.Size).Append(':').Append(f.MtimeNs).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }
}
