using System.Globalization;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;

namespace AsterCache.Tray;

/// 서비스가 auto_status.json 에 적은 게임 하나의 자동 단계.
public sealed record AutoGame(string Source, string? Launcher, string Stage, string? Detail);

/// 알림 대상 사건 (kind: discovered, relocated).
public sealed record AutoNotice(DateTimeOffset At, string Game, string Kind)
{
    public string Key => $"{At.UtcTicks}|{Game}|{Kind}";
}

/// auto_status.json 읽기 전용 보기 (없거나 손상되면 Load 가 null). 단계 사슬·알림 선택은 화면과 무관한 순수 함수입니다.
public sealed record AutoStatusFile(IReadOnlyDictionary<string, AutoGame> Games, IReadOnlyList<AutoNotice> Events)
{
    private static readonly string[] Chain = ["discovered", "tracing", "waiting_idle", "relocating", "done"];

    public static AutoStatusFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonRead.ReadFile(path) is not JObject root)
            {
                return null;
            }

            var games = new Dictionary<string, AutoGame>(StringComparer.OrdinalIgnoreCase);
            if (Get(root, "games") is JObject gs)
            {
                foreach (KeyValuePair<string, object?> kv in gs)
                {
                    if (kv.Value is JObject g && Get(g, "stage") is string stage)
                    {
                        games[kv.Key] = new AutoGame(Get(g, "source") as string ?? "auto", Get(g, "launcher") as string, stage, Get(g, "detail") as string);
                    }
                }
            }

            var events = new List<AutoNotice>();
            if (Get(root, "events") is List<object?> es)
            {
                foreach (object? e in es)
                {
                    if (e is JObject o && Get(o, "game") is string game && Get(o, "kind") is string kind
                        && DateTimeOffset.TryParse(Get(o, "at") as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
                    {
                        events.Add(new AutoNotice(at, game, kind));
                    }
                }
            }

            return new AutoStatusFile(games, events);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// since 이후에 생겼고 seen 에 없는 discovered / relocated 사건만 (순서 유지). seen 은 부르는 쪽이 채웁니다.
    public static IReadOnlyList<AutoNotice> NewEvents(IEnumerable<AutoNotice> events, DateTimeOffset since, ISet<string> seen) =>
        events.Where(e => e.Kind is "discovered" or "relocated" && e.At >= since && !seen.Contains(e.Key)).ToList();

    public static string Notice(AutoNotice e, string lang) => L.Get(lang, e.Kind == "relocated" ? "Toast_Relocated" : "Toast_Discovered", e.Game);

    public static string StageName(string stage, string lang)
    {
        string? key = stage switch
        {
            "discovered" => "Stage_Discovered",
            "tracing" => "Stage_Tracing",
            "waiting_idle" => "Stage_WaitingIdle",
            "relocating" => "Stage_Relocating",
            "done" => "Stage_Done",
            "failed" => "Stage_Failed",
            "skipped" => "Stage_Skipped",
            _ => null,
        };
        return key is null ? stage : L.Get(lang, key);
    }

    /// "자동: 이름 발견 → [트레이스 수집 중]" 처럼 현재 단계까지 보이고 현재 단계는 [ ] 로 감쌉니다. 실패·건너뜀은 사유를 덧붙입니다.
    public static string Line(string name, AutoGame g, string lang)
    {
        int at = Array.IndexOf(Chain, g.Stage);
        if (at < 0)
        {
            string why = string.IsNullOrEmpty(g.Detail) ? string.Empty : $" ({g.Detail})";
            return L.Get(lang, "Auto_Line", name, $"[{StageName(g.Stage, lang)}]{why}");
        }

        IEnumerable<string> parts = Chain.Take(at + 1).Select((s, i) => i == at ? $"[{StageName(s, lang)}]" : StageName(s, lang));
        return L.Get(lang, "Auto_Line", name, string.Join(" → ", parts));
    }

    private static object? Get(JObject o, string key) => o.TryGetValue(key, out object? v) ? v : null;
}
