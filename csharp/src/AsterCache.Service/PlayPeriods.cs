using System.Globalization;

namespace AsterCache.Service;

/// 첫 구간 (기본 600 초) 이후 한 구간 (같은 길이) 씩의 물리 읽기 요약. 구간값은 누계의 차로 셉니다.
internal sealed record PlayPeriodSummary(int FromSeconds, int ToSeconds, long ReadBytes, ulong ReadCount, double ReadSeconds, int SlowSamples);

internal static class PlayPeriods
{
    public const int DefaultSeconds = 600;

    /// 누계 검사점 개수 상한 (기본 구간이면 6 시간): 아주 긴 세션의 기록이 끝없이 커지지 않게 함.
    public const int MaxCheckpoints = 36;

    /// 누계 검사점: 구간 길이의 배수 (P, 2P, ...).
    public static int[] Checkpoints(int periodSeconds) => [.. Enumerable.Range(1, MaxCheckpoints).Select(i => i * periodSeconds)];

    /// cumulative: 검사점마다의 누계 (오름차순, Seconds = 검사점), end: 끝났을 때의 누계 (끝나지 않았으면 null).
    /// 첫 검사점 이전은 기존 창 요약이 맡으므로 첫 구간은 [첫 검사점, 둘째 검사점) 이고, 마지막 검사점 뒤에 읽은 것이 있으면 끝까지의 구간을 더합니다.
    public static List<PlayPeriodSummary> Build(IReadOnlyList<PlayWindowSummary> cumulative, PlayWindowSummary? end)
    {
        var periods = new List<PlayPeriodSummary>();
        for (int i = 1; i < cumulative.Count; i++)
        {
            periods.Add(Between(cumulative[i - 1], cumulative[i], cumulative[i].Seconds ?? 0));
        }

        if (end is not null && cumulative.Count > 0 && cumulative[^1] is { Seconds: { } last } lastWindow && end.MeasuredAtSeconds > last)
        {
            periods.Add(Between(lastWindow, end, (int)Math.Ceiling(end.MeasuredAtSeconds)));
        }

        return periods;
    }

    private static PlayPeriodSummary Between(PlayWindowSummary from, PlayWindowSummary to, int toSeconds) =>
        new(from.Seconds ?? 0, toSeconds, to.ReadBytes - from.ReadBytes, to.ReadCount - from.ReadCount, to.ReadSeconds - from.ReadSeconds, to.SlowSamples - from.SlowSamples);

    /// 한 줄 요약 (로그): "10-20 min 512 MiB/1234 reads/12.3 s read time/5 slow samples; ...".
    public static string Format(IReadOnlyList<PlayPeriodSummary> periods) => string.Join("; ", periods.Select(p =>
        string.Create(CultureInfo.InvariantCulture, $"{p.FromSeconds / 60}-{(p.ToSeconds + 59) / 60} min {p.ReadBytes / 1_048_576d:F0} MiB/{p.ReadCount} reads/{p.ReadSeconds:F1} s read time/{p.SlowSamples} slow samples")));
}
