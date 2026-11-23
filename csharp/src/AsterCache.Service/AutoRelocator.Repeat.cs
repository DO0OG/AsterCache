using System.Security.Cryptography;
using System.Text;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 원본이 바뀌지 않은 재적용이 되풀이될 때 게임별로 이동을 쉬게 합니다.
public sealed partial class AutoRelocator
{
    private const int ReapplyRepeatLimit = 3;
    private static readonly TimeSpan ReapplyRepeatWindow = TimeSpan.FromDays(7);
    internal const string ReapplyRepeatDetail = "reapply paused after 3 unchanged-source rounds in 7 days";

    /// 조사 사례에서 하루에도 여러 번 같은 배치를 되풀이한 것을 막기 위한 기준입니다.
    public void NoteReapplyMoved(GameSettings g, long movedBytes)
    {
        _ = RecordReapplyRepeatMove(g, movedBytes, handoffOnly: false);
        _ = ReapplyRepeatHeld(g, _settings().Auto);
    }

    private bool ReapplyRepeatHeld(GameSettings g, AutoSettings auto)
    {
        ReapplyLimitRecord? record = _status.ReapplyLimit(g.Name);
        if (record is not null && record.Layout != GamePolicy.RelocateLayout(g, auto))
        {
            _status.ClearReapplyLimit(g.Name);
            record = null;
        }

        bool held = record is not null && _status.ReapplyRoundsWithin(g.Name, ReapplyRepeatWindow) >= ReapplyRepeatLimit;
        if (_status.Get(g.Name) is { } entry)
        {
            bool shown = entry.Detail?.StartsWith(ReapplyRepeatDetail, StringComparison.Ordinal) == true; // 예열 진행 표시가 뒤에 붙어도 같은 표시로 봄
            if (held && !shown)
            {
                _ = _status.Set(g.Name, g.Source, null, entry.Stage, ReapplyRepeatDetail); // 단계는 그대로 두고 사유만 보임
            }
            else if (!held && shown)
            {
                _ = _status.Set(g.Name, g.Source, null, entry.Stage, null);
            }
        }

        return held;
    }

    private bool RecordReapplyRepeatMove(GameSettings g, long movedBytes, bool handoffOnly)
    {
        (int count, bool reachedLimit) = _status.RecordReapplyRound(g.Name, movedBytes, ReapplyRepeatWindow, ReapplyRepeatLimit, handoffOnly);
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
