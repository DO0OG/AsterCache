using System.Globalization;
using AsterCache.Core.I18n;

namespace AsterCache.Core;

/// 설치 뒤 자동 진행 집계 (auto_status.json 의 게임별 단계 → 발견·계획·재배치·예열·적중률 평균). 새 수집 없이 이미 있는 값만 셉니다.
public sealed record AutoProgress(int Discovered, int Planned, int Relocated, int Prewarmed, int? HitAverage)
{
    /// 계획 M = 계획 파일 (plansDir\<이름>.plan.json, 자동 등록과 같은 이름 규칙) 이 실제로 있는 게임, 재배치 K = done, 예열 P = detail 에 "prewarmed" 표시. 적중률은 값이 있는 게임만의 평균 (없으면 null).
    public static AutoProgress From(IEnumerable<(string Name, string Stage, string? Detail, int? HitPercent)> games, string plansDir)
    {
        List<(string Name, string Stage, string? Detail, int? HitPercent)> all = [.. games];
        List<int> hits = [.. all.Where(g => g.HitPercent.HasValue).Select(g => g.HitPercent!.Value)];
        return new AutoProgress(
            all.Count,
            all.Count(g => File.Exists(Path.Combine(plansDir, g.Name + ".plan.json"))),
            all.Count(g => g.Stage == "done"),
            all.Count(g => g.Detail?.Contains("prewarmed", StringComparison.Ordinal) == true && !g.Detail.Contains("prewarmed (partial", StringComparison.Ordinal)), // 부분 예열은 예열 완료로 세지 않음
            hits.Count == 0 ? null : (int)Math.Round(hits.Average(), MidpointRounding.AwayFromZero));
    }

    /// "자동 진행: 발견 N 게임 → …" 한 줄 (게임이 없으면 null).
    public string? Line(string lang)
    {
        if (Discovered == 0)
        {
            return null;
        }

        string line = L.Get(lang, "Auto_Progress", Discovered, Planned, Relocated, Prewarmed);
        return HitAverage is { } h ? line + L.Get(lang, "Auto_ProgressHit", h.ToString(CultureInfo.InvariantCulture)) : line;
    }
}
