using System.Globalization;
using System.Text.Json;
using AsterCache.Core.I18n;

namespace AsterCache.Tray;

/// 플레이 기록 한 구간. 물리 디스크의 읽기 통계이며 게임의 로딩 시간이나 프레임 끊김을 잰 값이 아닙니다.
internal sealed record PlayWindowStat(int? Seconds, long ReadBytes, double ReadSeconds, int SlowSamples);

internal sealed record PlayWarmStat(string Result, string? Reason, long? ReadMib, long? TargetMib);

internal sealed record PlayStat(DateTimeOffset? StartedAt, double DurationSeconds, string Status, PlayWarmStat? Warm, IReadOnlyList<PlayWindowStat> Windows, PlayWindowStat? Total);

/// play_samples.json 에서 읽은 게임별 가장 최근 플레이. Unreadable 이면 파일이 있는데 해석하지 못함 (기록 없음과 구별).
internal sealed record PlayRecords(IReadOnlyDictionary<string, PlayStat> Last, bool Unreadable)
{
    public static PlayRecords Empty { get; } = new(new Dictionary<string, PlayStat>(), false);
}

/// 서비스가 쓰는 play_samples.json 을 읽어 게임별 마지막 플레이를 짧은 글로 만듭니다 (읽기 전용).
internal static class PlaySummary
{
    public const string FileName = "play_samples.json";
    private const int FirstWindow = 30;
    private const int SecondWindow = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class Document
    {
        public Dictionary<string, List<Record?>?>? Games { get; set; }
    }

    private sealed class Record
    {
        public DateTimeOffset? StartedAt { get; set; }
        public double DurationSeconds { get; set; }
        public string? Status { get; set; }
        public Warm? StartWarm { get; set; }
        public List<Window?>? Windows { get; set; }
        public Window? Total { get; set; }
    }

    private sealed class Warm
    {
        public string? Result { get; set; }
        public string? Reason { get; set; }
        public long? ReadMib { get; set; }
        public long? TargetMib { get; set; }
    }

    private sealed class Window
    {
        public int? Seconds { get; set; }
        public long ReadBytes { get; set; }
        public double ReadSeconds { get; set; }
        public int SlowSamples { get; set; }
    }

    /// 파일이 없으면 빈 기록, 해석하지 못하면 Unreadable. 읽기 입출력 오류는 호출부에 던져 직전 값을 유지하게 합니다.
    public static PlayRecords Load(string path)
    {
        if (!File.Exists(path))
        {
            return PlayRecords.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Document doc = JsonSerializer.Deserialize<Document>(stream, JsonOptions) ?? new Document();
            var last = new Dictionary<string, PlayStat>(StringComparer.OrdinalIgnoreCase);
            foreach ((string game, List<Record?>? records) in doc.Games ?? [])
            {
                if (records?.OfType<Record>().MaxBy(r => r.StartedAt) is { } r)
                {
                    last[game] = new PlayStat(r.StartedAt, r.DurationSeconds, r.Status ?? string.Empty,
                        r.StartWarm is { } w ? new PlayWarmStat(w.Result ?? string.Empty, w.Reason, w.ReadMib, w.TargetMib) : null,
                        (r.Windows ?? []).OfType<Window>().Select(Stat).ToList(), r.Total is { } t ? Stat(t) : null);
                }
            }

            return new PlayRecords(last, false);
        }
        catch (JsonException)
        {
            return new PlayRecords(PlayRecords.Empty.Last, true);
        }
    }

    private static PlayWindowStat Stat(Window w) => new(w.Seconds, w.ReadBytes, w.ReadSeconds, w.SlowSamples);

    /// 게임 하나의 마지막 플레이 요약 (두 줄). 기록이 없거나 파일을 못 읽으면 그 사실만 적습니다. 평가 문구는 넣지 않습니다.
    public static string Describe(PlayRecords records, string game)
    {
        if (records.Unreadable)
        {
            return L.T("Play_Unreadable");
        }

        if (!records.Last.TryGetValue(game, out PlayStat? p))
        {
            return L.T("Play_None");
        }

        string when = LocalText(p.StartedAt);
        string head = L.T("Play_Head", when, Duration(p), WarmText(p.Warm));
        if (p.Status == "unavailable")
        {
            return head + "\n" + L.T("Play_CounterUnavailable");
        }

        PlayWindowStat? first = p.Windows.FirstOrDefault(w => w.Seconds == FirstWindow);
        PlayWindowStat? second = p.Windows.FirstOrDefault(w => w.Seconds == SecondWindow);
        return head + "\n" + L.T("Play_Disk", WindowText(L.T("Play_WinLabel", FirstWindow), first), WindowText(L.T("Play_WinLabel", SecondWindow), second), WindowText(L.T("Play_WinTotal"), p.Total));
    }

    // 시각이 없거나 범위 밖이면 (기본값의 ToLocalTime 은 UTC 보다 늦은 시간대에서 예외) 예외 없이 "시각 없음"
    private static string LocalText(DateTimeOffset? at)
    {
        try
        {
            return at is { Year: > 1 } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : L.T("Play_NoTime");
        }
        catch (ArgumentOutOfRangeException)
        {
            return L.T("Play_NoTime");
        }
    }

    private static string Duration(PlayStat p) =>
        p.Status == "recording" ? L.T("Play_NoEnd") : p.DurationSeconds < 60 ? L.T("Play_Sec", (int)p.DurationSeconds) : L.T("Play_Min", (int)Math.Round(p.DurationSeconds / 60, MidpointRounding.AwayFromZero));

    private static string WarmText(PlayWarmStat? warm) => warm switch
    {
        null => L.T("Play_WarmUnknown"),
        { Reason: "cancelled" } => L.T("Play_WarmPartial"), // 예열이 도중에 취소됨 (서비스 Prewarmer.Cancelled)
        { Result: "ok" } w when w.TargetMib is { } target && w.ReadMib is { } read && read < target => L.T("Play_WarmPartial"),
        { Result: "ok" } => L.T("Play_WarmDone"),
        { Result: "skipped" } => L.T("Play_WarmSkipped"),
        { Result: "failed" } => L.T("Play_WarmFailed"),
        _ => L.T("Play_WarmUnknown"),
    };

    private static string WindowText(string label, PlayWindowStat? w) =>
        w is null
            ? label + ": -"
            : L.T("Play_Window", label, ManageViewModel.Size(w.ReadBytes), w.ReadSeconds.ToString("F1", CultureInfo.InvariantCulture), w.SlowSamples);
}
