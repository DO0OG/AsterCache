using System.Globalization;

namespace AsterCache.Service;

/// 첫 구간 (기본 600 초) 이후 한 구간 (같은 길이) 씩의 물리 읽기 요약. 구간값은 누계의 차로 셉니다.
/// 프레임 값은 같은 구간의 프레임 수와 50 ms·100 ms 를 넘은 간격 수, 그리고 그 구간 안만의 가장 긴 간격 (ms) 입니다 (프레임을 재지 않았으면 0).
internal sealed record PlayPeriodSummary(int FromSeconds, int ToSeconds, long ReadBytes, ulong ReadCount, double ReadSeconds, int SlowSamples,
    long FrameCount = 0, double MaxFrameMs = 0, int Over50Ms = 0, int Over100Ms = 0, int Pauses = 0, double MaxPauseMs = 0);

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
        new(from.Seconds ?? 0, toSeconds, to.ReadBytes - from.ReadBytes, to.ReadCount - from.ReadCount, to.ReadSeconds - from.ReadSeconds, to.SlowSamples - from.SlowSamples,
            to.FrameCount - from.FrameCount, to.PeriodMaxFrameMs, to.Over50Ms - from.Over50Ms, to.Over100Ms - from.Over100Ms, to.Pauses - from.Pauses, to.PeriodMaxPauseMs);

    /// 한 줄 요약 (로그): "10-20 min 512 MiB/1234 reads/12.3 s read time/5 slow samples, frames 70000, longest 83 ms, over 50 ms 12, over 100 ms 3; ...".
    /// frameStatus 가 null 이면 프레임 부분을 쓰지 않고, "ok" 가 아니면 프레임을 재지 못한 이유 한 단어를 씁니다.
    /// stoppedAt (초) 이 있으면 프레임 측정이 그 시각에 멈춘 것이라, 그 안에 끝난 구간은 잰 값을 그대로 씁니다.
    public static string Format(IReadOnlyList<PlayPeriodSummary> periods, string? frameStatus = null, double? stoppedAt = null) => string.Join("; ", periods.Select(p =>
        string.Create(CultureInfo.InvariantCulture, $"{p.FromSeconds / 60}-{(p.ToSeconds + 59) / 60} min {p.ReadBytes / 1_048_576d:F0} MiB/{p.ReadCount} reads/{p.ReadSeconds:F1} s read time/{p.SlowSamples} slow samples")
            + (frameStatus is null ? string.Empty : ", " + FrameText(frameStatus == "blocked" && p.ToSeconds <= stoppedAt ? "ok" : frameStatus, p.FrameCount, p.MaxFrameMs, p.Over50Ms, p.Over100Ms, p.Pauses, p.MaxPauseMs))));

    /// 프레임 한 줄 요약 (로그 공통): 잰 경우 "frames N, longest M ms, over 50 ms K, over 100 ms L, pauses P (longest X ms)" (pauses 는 1 초 이상 걸린 간격 수와 그 가장 긴 것, 없으면 괄호 생략), 못 잰 경우 "frames <이유 한 단어>".
    public static string FrameText(string status, long frames, double maxMs, int over50, int over100, int pauses, double maxPauseMs) => status == "ok"
        ? string.Create(CultureInfo.InvariantCulture, $"frames {frames}, longest {maxMs:F0} ms, over 50 ms {over50}, over 100 ms {over100}, pauses {pauses}{(pauses > 0 ? $" (longest {maxPauseMs:F0} ms)" : string.Empty)}")
        : "frames " + status;
}
