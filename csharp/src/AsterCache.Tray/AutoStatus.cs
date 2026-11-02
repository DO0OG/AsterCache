using System.Globalization;
using System.Text.RegularExpressions;
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
public sealed partial record AutoStatusFile(IReadOnlyDictionary<string, AutoGame> Games, IReadOnlyList<AutoNotice> Events)
{
    private static readonly string[] Chain = ["discovered", "tracing", "waiting_idle", "relocating", "done"];
    private static readonly string[] FirstRunStages = ["learning", "tracing"];
    private static readonly string[] AfterFirstRun = ["waiting_idle", "relocating", "done"];

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

    /// 첫 실행 (learning / tracing) 이 끝난 게임 이름: seen 에 기억한 이전 단계가 첫 실행이었고 지금은 재배치 대기 이후인 것. 트레이가 처음 본 단계는 알리지 않고 기억만 합니다. seen 은 이 함수가 갱신합니다.
    public static IReadOnlyList<string> FirstRunFinished(IDictionary<string, string> seen, AutoStatusFile status)
    {
        var finished = new List<string>();
        foreach ((string name, AutoGame g) in status.Games)
        {
            if (seen.TryGetValue(name, out string? before) && FirstRunStages.Contains(before) && AfterFirstRun.Contains(g.Stage))
            {
                finished.Add(name);
            }

            seen[name] = g.Stage;
        }

        return finished;
    }

    /// 예열이 끝난 게임 이름: seen 에 기억한 이전 상태가 예열 중 (detail 에 "prewarming x/y MiB") 이거나 예열 표시가 없었고 지금은 예열됨인 것.
    /// 트레이가 처음 본 상태는 알리지 않고 기억만 합니다. 게임당 하루 한 번 (notified: 게임 → 알린 날, 트레이 메모리). seen·notified 는 이 함수가 갱신합니다.
    public static IReadOnlyList<string> PrewarmFinished(IDictionary<string, string> seen, IDictionary<string, DateOnly> notified, AutoStatusFile status, DateOnly today)
    {
        var finished = new List<string>();
        foreach ((string name, AutoGame g) in status.Games)
        {
            string detail = g.Detail ?? string.Empty;
            string now = Prewarming().IsMatch(detail) ? "warming" : PrewarmTag().IsMatch(detail) ? "warm" : "cold";
            if (seen.TryGetValue(name, out string? before) && before != "warm" && now == "warm" && !(notified.TryGetValue(name, out DateOnly day) && day == today))
            {
                finished.Add(name);
                notified[name] = today;
            }

            seen[name] = now;
        }

        return finished;
    }

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
            "learning" => "Stage_Learning",
            _ => null,
        };
        return key is null ? stage : L.Get(lang, key);
    }

    /// 단계 이름 뒤에 붙일 부가 설명: 학습 중이면 선행 읽기 양 ("<n> MiB read ahead" 를 풀어 보임), detail 에 프로필·프리페치 재배치 표시가 있으면 "재배치됨(프로필)"·"재배치됨(프리페치)", 예열 표시가 있으면 "예열됨" (전체 폴더·엔진 프로필은 그 이름까지). 없으면 null.
    public static string? StageExtra(AutoGame g, string lang)
    {
        if (g.Stage == "learning")
        {
            Match m = ReadAhead().Match(g.Detail ?? string.Empty);
            return m.Success ? L.Get(lang, "Stage_ReadAhead", m.Groups[1].Value) : g.Detail;
        }

        var parts = new List<string>();
        if ((g.Detail ?? string.Empty).Contains("relocated (profile)", StringComparison.Ordinal))
        {
            parts.Add(L.Get(lang, "Stage_RelocatedProfile")); // boot 트레이스 (가져온 프로필) 로 재배치함
        }
        else if ((g.Detail ?? string.Empty).Contains("relocated (prefetch)", StringComparison.Ordinal))
        {
            parts.Add(L.Get(lang, "Stage_RelocatedPrefetch")); // boot 트레이스 (Windows 프리페치 기록) 로 재배치함
        }

        Match now = Prewarming().Match(g.Detail ?? string.Empty);
        Match p = PrewarmTag().Match(g.Detail ?? string.Empty);
        if (now.Success)
        {
            parts.Add(L.Get(lang, "Stage_Prewarming", now.Groups[1].Value, now.Groups[2].Value));
        }
        else if (p.Success)
        {
            parts.Add(p.Groups[2].Success ? L.Get(lang, "Stage_PrewarmedProfile", p.Groups[2].Value)
                : p.Groups[1].Value == "full" ? L.Get(lang, "Stage_PrewarmedFull") : L.Get(lang, "Stage_Prewarmed"));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// 단계 열에 보일 글: 단계 이름 + 부가 설명.
    public static string StageText(AutoGame g, string lang) =>
        StageExtra(g, lang) is { } extra ? $"{StageName(g.Stage, lang)} ({extra})" : StageName(g.Stage, lang);

    [GeneratedRegex(@"^(\d+) MiB read ahead$")]
    private static partial Regex ReadAhead();

    [GeneratedRegex(@"prewarming (\d+)/(\d+) MiB")]
    private static partial Regex Prewarming();

    [GeneratedRegex(@"prewarmed(?: \((full|profile ([^)]*))\))?")]
    private static partial Regex PrewarmTag();

    /// "자동: 이름 발견 → [트레이스 수집 중]" 처럼 현재 단계까지 보이고 현재 단계는 [ ] 로 감쌉니다. 실패·건너뜀은 사유를 덧붙입니다.
    /// 첫 실행 학습 (learning) 은 트레이스 수집 자리에 들어가고 선행 읽기 양을, 예열이 끝난 대기·완료는 "예열됨" 을 덧붙입니다.
    public static string Line(string name, AutoGame g, string lang)
    {
        string[] chain = g.Stage == "learning" ? [.. Chain.Select(s => s == "tracing" ? "learning" : s)] : Chain;
        int at = Array.IndexOf(chain, g.Stage);
        if (at < 0)
        {
            string why = string.IsNullOrEmpty(g.Detail) ? string.Empty : $" ({g.Detail})";
            return L.Get(lang, "Auto_Line", name, $"[{StageName(g.Stage, lang)}]{why}");
        }

        IEnumerable<string> parts = chain.Take(at + 1).Select((s, i) => i == at ? $"[{StageName(s, lang)}]" : StageName(s, lang));
        string extra = StageExtra(g, lang) is { } x ? $" ({x})" : string.Empty;
        return L.Get(lang, "Auto_Line", name, string.Join(" → ", parts) + extra);
    }

    private static object? Get(JObject o, string key) => o.TryGetValue(key, out object? v) ? v : null;
}
