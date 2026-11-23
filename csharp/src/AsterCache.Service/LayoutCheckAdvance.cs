using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 예약 드라이브 최적화가 끝난 직후 배치 검사를 앞당깁니다. 통째 배치 계획이 적용된 게임에 한해서만: 최적화가 한 조각 파일은 옮기지 않는다는 가정을 끝난 직후의 검사로 바로 확인하고,
/// 흐트러졌으면 재적용합니다. 범위 방식 계획은 최적화가 되돌리는 배치라 앞당기면 같은 하루 한도를 되풀이해 쓰므로 앞당기지 않습니다.
/// 앞당기는 것은 검사 시점뿐입니다: 당긴 검사도 게임 실행·디스크 바쁨·최적화 중·하루 한도 같은 기존 조건을 모두 지납니다. 어느 드라이브가 최적화됐는지는 알 수 없어 등록 게임 전부가 대상입니다.
internal sealed class LayoutCheckAdvance
{
    private DateTimeOffset? _endSeen;

    /// 마지막으로 본 최적화 끝 시각이 바뀌었으면 통째 계획 게임의 기한 (due) 을 지금으로 당기고 그 수를 돌려줍니다 (없으면 0).
    public int Run(DriveOptimization? optimization, Dictionary<string, DateTimeOffset> due, IEnumerable<GameSettings> games, Func<GameSettings, bool> wholePlan, DateTimeOffset now, ILog log)
    {
        if (optimization?.LastEnded is not { } ended || ended == _endSeen)
        {
            return 0;
        }

        _endSeen = ended;
        int moved = 0;
        foreach (GameSettings g in games)
        {
            if (due.TryGetValue(g.Name, out DateTimeOffset at) && at > now && wholePlan(g))
            {
                due[g.Name] = now;
                moved++;
            }
        }

        if (moved > 0)
        {
            log.Info($"drive optimization ended at {DriveOptimization.Stamp(ended)}, layout check brought forward for {moved} game(s)");
        }

        return moved;
    }

    /// 활성 계획이 통째 배치 계획이고 zone 이 정해져 (적용됨) 있는가. 계획을 읽지 못하면 거짓.
    public static bool UsesWholeLayout(GameSettings g)
    {
        try
        {
            PlanFile plan = PlanFile.Load(g.Plan);
            return plan.Zone.StartLcn is not null && WholeFileLayout.WholeFiles(plan) is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AsterCache.Core.Json.PyValueException)
        {
            return false;
        }
    }
}
