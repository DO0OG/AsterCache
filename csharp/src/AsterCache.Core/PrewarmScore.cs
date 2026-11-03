namespace AsterCache.Core;

/// 예열 순서 점수: 최근성 (마지막 플레이로부터 일수의 지수 감쇠, 반감기 7 일) + 습관 (지금과 같은 요일·±2 시간에 한 플레이의 비율).
public static class PrewarmScore
{
    public const double HalfLifeDays = 7;
    public const int HabitWindowMinutes = 120;

    /// plays 는 플레이 시각들 (순서 무관). 이력이 비었으면 0. 이력이 하나뿐이면 습관은 0 이라 점수는 최근성만입니다 (최근순과 같은 결과).
    /// 요일·시각은 now 의 오프셋 기준입니다 (호출하는 쪽이 now 를 지역 시각으로 넘김). shortcut: ±2 시간은 자정을 넘어 이어 보지 않음, 필요해지면 원형 거리로.
    public static double Compute(IReadOnlyList<DateTimeOffset> plays, DateTimeOffset now)
    {
        if (plays.Count == 0)
        {
            return 0;
        }

        double days = Math.Max(0, (now - plays.Max()).TotalDays);
        double recency = Math.Pow(2, -days / HalfLifeDays);
        if (plays.Count < 2)
        {
            return recency;
        }

        int near = plays.Select(p => p.ToOffset(now.Offset)).Count(p => p.DayOfWeek == now.DayOfWeek
            && Math.Abs((p.TimeOfDay - now.TimeOfDay).TotalMinutes) <= HabitWindowMinutes);
        return recency + (double)near / plays.Count;
    }
}
