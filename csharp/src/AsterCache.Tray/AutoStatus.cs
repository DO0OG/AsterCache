using System.Globalization;
using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;

namespace AsterCache.Tray;

/// 서비스가 auto_status.json 에 적은 게임 하나의 자동 단계.
public sealed record AutoGame(string Source, string? Launcher, string Stage, string? Detail, int? HitPercent = null, int? CoveragePercent = null, string? DisplayName = null, DateTimeOffset? HitAt = null);

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
                        games[kv.Key] = new AutoGame(Get(g, "source") as string ?? "auto", Get(g, "launcher") as string, stage, Get(g, "detail") as string, Get(g, "hit_percent") is long hp ? (int)Math.Clamp(hp, 0, 100) : null, Get(g, "coverage_percent") is long cp ? (int)Math.Clamp(cp, 0, 100) : null, Get(g, "display_name") as string, DateTimeOffset.TryParse(Get(g, "hit_at") as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset ha) ? ha : null);
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
            string now = PrewarmPhase(g);
            if (seen.TryGetValue(name, out string? before) && before != "warm" && now == "warm" && !(notified.TryGetValue(name, out DateOnly day) && day == today))
            {
                finished.Add(name);
                notified[name] = today;
            }

            seen[name] = now;
        }

        return finished;
    }

    /// 설치 뒤 자동 진행 집계 (발견·계획·재배치·예열·적중률 평균).
    public static AutoProgress Progress(AutoStatusFile status, string plansDir) => AutoProgress.From(status.Games.Select(kv => (kv.Key, kv.Value.Stage, kv.Value.Detail, kv.Value.HitPercent)), plansDir);

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

    // 서비스가 detail 에 적는 영어 사유 (일부) → 짧은 설명 키. 먼저 맞는 것을 씁니다.
    private static readonly (string Needle, string Key)[] Reasons =
    [
        ("auto relocate off", "Why_RelocateOff"),
        ("daily limit reached", "Why_DailyLimit"),
        ("daily write budget reached", "Why_DailyBudget"),
        ("drive recovering after relocation", "Why_DriveRecovering"),
        ("startup prewarm in progress", "Why_StartupPrewarm"),
        ("status file was unreadable", "Why_StatusHold"),
        ("write limit reached", "Why_WriteLimit"),
        ("another apply or plan switch holds the zone lock", "Why_ZoneLockBusy"),
        ("target region busy", "Why_TargetBusy"),
        ("trace grew", "Why_TraceGrew"),
        ("trace kept, planning again", "Why_TraceKept"),
        ("unchanged files kept, planning again", "Why_UnchangedKept"),
        ("game updated", "Why_GameUpdated"),
        ("first run recorded", "Why_FirstRunRecorded"),
        ("plan and journal differ", "Why_PartialPlan"),
        ("recovery stopped", "Why_RecoveryStopped"),
        ("data check failed", "Why_DataCheck"),
        ("stale plan", "Why_StalePlan"),
        ("dry-run cancelled", "Why_DryRunCancelled"),
        ("verification deferred", "Why_VerificationDeferred"),
    ];

    /// detail 안의 알려진 영어 사유를 현재 언어의 짧은 설명으로. 모르면 null (원문은 부르는 쪽이 툴팁·로그로 둠).
    public static string? ReasonText(string? detail, string lang) =>
        FindReason(detail) is { } r ? L.Get(lang, r.Key) : null;

    private static (string Needle, string Key)? FindReason(string? detail) =>
        Reasons.Where(r => (detail ?? string.Empty).Contains(r.Needle, StringComparison.Ordinal)).Select(r => ((string Needle, string Key)?)r).FirstOrDefault();

    /// 단계 이름 뒤에 붙일 부가 설명: 학습 중이면 선행 읽기 양 ("<n> MiB read ahead" 를 풀어 보임), detail 에 프로필·프리페치 재배치 표시가 있으면 "재배치됨(프로필)"·"재배치됨(프리페치)", 예열 표시가 있으면 "예열됨" (전체 폴더·엔진 프로필은 그 이름까지). 없으면 null.
    public static string? StageExtra(AutoGame g, string lang, DateTimeOffset? boot = null) =>
        StageParts(g, lang, boot) is { Count: > 0 } parts ? string.Join(", ", parts) : null;

    /// 단계 열(고급)에 보일 짧은 글: 단계 이름 + 첫 부가 설명 하나 (나머지 개수는 "+N"). 전체 문장은 StageText 로 툴팁에 둡니다.
    public static string StageShort(AutoGame g, string lang, DateTimeOffset? boot = null) =>
        StageParts(g, lang, boot) is { Count: > 0 } parts ? $"{StageName(g.Stage, lang)} · {parts[0]}{(parts.Count > 1 ? $" +{parts.Count - 1}" : string.Empty)}" : StageName(g.Stage, lang);

    /// detail 의 예열 표시: "warming" (예열 중), "warm" (예열됨), "cold" (표시 없음).
    public static string PrewarmPhase(AutoGame g) => Prewarming().IsMatch(g.Detail ?? string.Empty) ? "warming" : PrewarmTag().IsMatch(g.Detail ?? string.Empty) ? "warm" : "cold";

    private static List<string> StageParts(AutoGame g, string lang, DateTimeOffset? boot)
    {
        if (g.Stage == "learning")
        {
            Match m = ReadAhead().Match(g.Detail ?? string.Empty);
            return m.Success ? [L.Get(lang, "Stage_ReadAhead", m.Groups[1].Value)] : string.IsNullOrEmpty(g.Detail) ? [] : [g.Detail];
        }

        var parts = new List<string>();
        if (g.Stage is "waiting_idle" or "failed" or "skipped" && ReasonText(g.Detail, lang) is { } why)
        {
            parts.Add(why); // 대기·실패 사유를 맨 앞에 (완료 단계의 옛 사유는 붙이지 않음)
        }

        if ((g.Detail ?? string.Empty).Contains("relocated (profile)", StringComparison.Ordinal))
        {
            parts.Add(L.Get(lang, "Stage_RelocatedProfile")); // boot 트레이스 (가져온 프로필) 로 재배치함
        }
        else if ((g.Detail ?? string.Empty).Contains("relocated (prefetch)", StringComparison.Ordinal))
        {
            parts.Add(L.Get(lang, "Stage_RelocatedPrefetch")); // boot 트레이스 (Windows 프리페치 기록) 로 재배치함
        }

        if (TraceUnavailableTag.Match(g.Detail ?? string.Empty) is { Success: true } blocked)
        {
            parts.Add(L.Get(lang, "Stage_TraceUnavailable", blocked.Groups[1].Value, blocked.Groups[2].Value)); // 안티치트로 트레이스를 더 모을 수 없음
        }

        if (SsdTierTag.Match(g.Detail ?? string.Empty) is { Success: true } tier)
        {
            parts.Add(L.Get(lang, "Stage_SsdTier", tier.Groups[1].Value)); // 핫 파일 일부를 SSD 사본으로 두었음
        }

        Match now = Prewarming().Match(g.Detail ?? string.Empty);
        Match p = PrewarmTag().Match(g.Detail ?? string.Empty);
        if (now.Success)
        {
            parts.Add(L.Get(lang, "Stage_Prewarming", now.Groups[1].Value, now.Groups[2].Value));
        }
        else if (p.Success)
        {
            parts.Add(p.Groups[3].Success ? L.Get(lang, "Stage_PrewarmedPartial", p.Groups[3].Value, p.Groups[4].Value)
                : p.Groups[2].Success ? L.Get(lang, "Stage_PrewarmedProfile", p.Groups[2].Value)
                : p.Groups[1].Value == "full" ? L.Get(lang, "Stage_PrewarmedFull") : L.Get(lang, "Stage_Prewarmed"));
        }

        if (g.HitPercent is { } hit)
        {
            parts.Add(L.Get(lang, g.HitAt is { } at && boot is { } b && at <= b ? "Stage_HitPercentOld" : "Stage_HitPercent", hit.ToString(CultureInfo.InvariantCulture))); // 재부팅 전에 잰 값은 지금 적중률처럼 보이지 않게
        }

        if (g.CoveragePercent is { } cover)
        {
            parts.Add(L.Get(lang, "Stage_CoveragePercent", cover.ToString(CultureInfo.InvariantCulture)));
        }

        return parts;
    }

    /// 기본 화면에 남길 보류·실패 사유: 대기·실패·건너뜀의 사유 (모르는 사유는 실패·건너뜀에서만 원문 그대로)와 안티치트 트레이스 불가. 없으면 null.
    public static string? HoldReason(AutoGame g, string lang)
    {
        var parts = new List<string>();
        bool replanning = g.Stage == "discovered" && (g.Detail ?? string.Empty).StartsWith("game updated", StringComparison.Ordinal); // 업데이트 뒤 재계획 대기 (알려진 사유만 보임)
        if (g.Stage is "waiting_idle" or "failed" or "skipped" || replanning)
        {
            if (ReasonText(g.Detail, lang) is { } why)
            {
                parts.Add(why);
            }
            else if (g.Stage is "failed" or "skipped" && !string.IsNullOrEmpty(g.Detail))
            {
                parts.Add(g.Detail);
            }
        }

        if (TraceUnavailableTag.Match(g.Detail ?? string.Empty) is { Success: true } blocked)
        {
            parts.Add(L.Get(lang, "Stage_TraceUnavailable", blocked.Groups[1].Value, blocked.Groups[2].Value));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// detail 에 지금 예열 중 표시 ("prewarming x/y MiB") 가 있으면 현재 언어의 "예열 중 x/y MB", 없으면 null.
    public static string? PrewarmingText(AutoGame g, string lang) =>
        Prewarming().Match(g.Detail ?? string.Empty) is { Success: true } m ? L.Get(lang, "Stage_Prewarming", m.Groups[1].Value, m.Groups[2].Value) : null;

    /// 단계 열에 보일 글: 단계 이름 + 부가 설명.
    public static string StageText(AutoGame g, string lang, DateTimeOffset? boot = null) =>
        StageExtra(g, lang, boot) is { } extra ? $"{StageName(g.Stage, lang)} ({extra})" : StageName(g.Stage, lang);

    [GeneratedRegex(@"^(\d+) MiB read ahead$")]
    private static partial Regex ReadAhead();

    private static readonly Regex TraceUnavailableTag = new(@"trace unavailable \(([^)]*)\), (\d+) earlier run\(s\) used", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex SsdTierTag = new(@"ssd tier (\d+(?:\.\d+)?) GB", RegexOptions.None, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"prewarming (\d+)/(\d+) MiB")]
    private static partial Regex Prewarming();

    [GeneratedRegex(@"prewarmed(?: \((full|profile ([^)]*)|partial (\d+)/(\d+) MiB)\))?")]
    private static partial Regex PrewarmTag();

    /// "자동: 이름 발견 → [트레이스 수집 중]" 처럼 현재 단계까지 보이고 현재 단계는 [ ] 로 감쌉니다. 실패·건너뜀은 사유를 덧붙입니다.
    /// 첫 실행 학습 (learning) 은 트레이스 수집 자리에 들어가고 선행 읽기 양을, 예열이 끝난 대기·완료는 "예열됨" 을 덧붙입니다.
    public static string Line(string name, AutoGame g, string lang)
    {
        string[] chain = g.Stage == "learning" ? [.. Chain.Select(s => s == "tracing" ? "learning" : s)] : Chain;
        int at = Array.IndexOf(chain, g.Stage);
        if (at < 0)
        {
            string why = string.IsNullOrEmpty(g.Detail) ? string.Empty
                : FindReason(g.Detail) is { } r ? $" ({L.Get(lang, r.Key)}{(g.Detail == r.Needle ? string.Empty : " — " + g.Detail)})" // 번역 뒤에 원문의 나머지 정보 (파일 이름, 시각)를 남김
                : $" ({g.Detail})";
            return L.Get(lang, "Auto_Line", name, $"[{StageName(g.Stage, lang)}]{why}");
        }

        IEnumerable<string> parts = chain.Take(at + 1).Select((s, i) => i == at ? $"[{StageName(s, lang)}]" : StageName(s, lang));
        string extra = StageExtra(g, lang) is { } x ? $" ({x})" : string.Empty;
        return L.Get(lang, "Auto_Line", name, string.Join(" → ", parts) + extra);
    }

    private static object? Get(JObject o, string key) => o.TryGetValue(key, out object? v) ? v : null;
}
