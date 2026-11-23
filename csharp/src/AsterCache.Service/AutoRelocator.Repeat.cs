using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 되풀이 감지: 범위 방식 배치를 드라이브 최적화 같은 다른 도구가 되돌려, 서비스가 같은 조각을 다시 zone 으로 옮기는 일이 이어지면 그 게임의 범위 방식 재배치를 쉼.
/// 쉬는 동안에도 예열은 그대로입니다. 계획 파일이 바뀌거나 (원본 변경으로 다시 계획, 방식을 바꿔 다시 계획) 원본 파일이 바뀌면 풀립니다. 상태는 auto_status.json 의 replan_attempts 에 남아 서비스를 다시 시작해도 이어짐.
public sealed partial class AutoRelocator
{
    private const string RestDetail = "relocation paused: a drive optimization or another tool keeps moving pieces back, and the same pieces were moved into the zone again; set layout to whole_file to move them again";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _restChecked = new(StringComparer.Ordinal); // 쉬는 게임의 원본 변경 확인 시각

    // 한 회차의 이동이 끝났을 때: 이번에 옮긴 조각이 앞선 저널에서 이미 옮긴 조각과 얼마나 같은지 보고, 되풀이 회차가 이어지면 쉼. mark 는 그 회차 전의 저널 줄 수.
    // 통째 배치 계획은 되돌려지지 않는 형태라 보지 않음. 진단 성격이라 읽기에 실패해도 재배치 흐름은 그대로.
    internal void NoteRepeatRound(GameSettings g, PlanFile plan, int mark, long movedBytes)
    {
        if (_env.JournalRounds is not { } rounds || plan.Stats.ContainsKey(WholeFileLayout.RuleKey) || movedBytes <= 0)
        {
            return;
        }

        try
        {
            (IReadOnlyList<AsterCache.Core.Json.JObject> current, IReadOnlyList<AsterCache.Core.Json.JObject>? previous) = rounds(g);
            (long repeated, long moved) = previous is null ? (0L, 0L) : MoveRepeat.Measure(current, mark, previous, plan.ClusterBytes);
            RecoveryAttempt prior = _status.Recovery(g.Name) ?? new RecoveryAttempt(null, null, 0, null);
            int count = MoveRepeat.IsRepeat(repeated, moved) ? prior.RepeatRounds + 1 : 0;
            if (count > 0)
            {
                _log.Info($"{g.Name}: {repeated} of {moved} bytes moved this round were already moved into the zone by an earlier journal ({count} round(s) in a row)");
            }

            if (count >= MoveRepeat.RoundsToRest && _env.FileWriteTime(g.Plan) is { } written)
            {
                _status.SetRecovery(g.Name, prior with { RepeatRounds = count, RestStamp = written.UtcTicks });
                Note(g, AutoStage.WaitingIdle, RestDetail);
                _log.Warn($"{g.Name}: {RestDetail}; resumes when the source files change");
            }
            else if (count != prior.RepeatRounds)
            {
                _status.SetRecovery(g.Name, prior with { RepeatRounds = count });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or AsterCache.Core.Json.PyValueException)
        {
            _log.Warn($"{g.Name}: earlier journal could not be compared ({e.GetType().Name}), repeat check skipped");
        }
    }

    // 이 게임의 범위 방식 재배치를 쉬는 중인가. 계획 파일이 바뀌었거나 방식이 통째 배치로 바뀌었거나 원본 파일이 바뀌었으면 풀고 거짓 (원본 변경은 StaleCheckEvery 마다만 확인).
    private bool RestHeld(GameSettings g, AsterSettings s)
    {
        if (_status.Recovery(g.Name) is not { RestStamp: { } stamp } attempt)
        {
            return false;
        }

        string? lifted = null;
        if (WholeLayout(g, s))
        {
            lifted = "layout is now whole_file";
        }
        else if (_env.FileWriteTime(g.Plan) is not { } written || written.UtcTicks != stamp)
        {
            lifted = "the plan changed";
        }
        else if ((!_restChecked.TryGetValue(g.Name, out DateTimeOffset at) || _env.Now() - at >= StaleCheckEvery) && SourceChanged(g))
        {
            lifted = "source files changed";
        }

        _restChecked[g.Name] = _env.Now();
        if (lifted is null)
        {
            return true;
        }

        _status.SetRecovery(g.Name, attempt with { RepeatRounds = 0, RestStamp = null });
        _log.Info($"{g.Name}: relocation resumes: {lifted}");
        return false;
    }

    private bool SourceChanged(GameSettings g)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            return plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Any(f => ReapplyChecker.Compare(f, _env.Stat) == true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or AsterCache.Core.Json.PyValueException)
        {
            return false; // 읽을 수 없으면 다음 확인 때 다시
        }
    }
}
