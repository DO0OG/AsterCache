using AsterCache.Core.I18n;

namespace AsterCache.Tray;

/// 상태 색 4종 (작업표시줄 아이콘 점과 게임 표 칩이 같은 팔레트). 글자는 항상 같이 보입니다.
public enum StatusTone
{
    Gray, // 서비스 정지·미설치·알 수 없음, 또는 상태 없음
    Warn, // 실패·건너뜀·재배치 대기(보류)
    Busy, // 트레이스·학습·재배치·예열 진행
    Ok,
}

/// 작업표시줄 한 줄 요약: Head (서비스 상태 · 게임 수) 와 Counts (예열됨·진행·보류, 0 은 생략).
public sealed record TraySummary(StatusTone Tone, string Head, string Counts)
{
    public string Line => Counts.Length == 0 ? Head : Head + " · " + Counts;
}

/// 트레이 아이콘·툴팁·메뉴가 쓰는 상태 계산 (화면과 무관한 순수 함수). 입력은 서비스 상태와 auto_status.json 뿐입니다.
public static class TrayStatus
{
    public const int TipMax = 127; // Win32 NOTIFYICONDATA.szTip 은 NUL 포함 128 자

    public static readonly TimeSpan MessageLife = TimeSpan.FromSeconds(60);

    /// registered: 설정에 등록된 게임 키. auto_status.json 에 남은 옛 게임은 세지 않습니다. problem: 설정 오류·서비스 재설치 필요.
    public static TraySummary Summarize(ServiceState service, IReadOnlyCollection<string> registered, AutoStatusFile? auto, bool problem = false)
    {
        string head = ServiceWord(service) + " · " + (registered.Count == 0 ? L.T("Tray_NoGames") : L.T("Tray_Games", registered.Count));
        if (service != ServiceState.Running && service != ServiceState.Pending)
        {
            return new TraySummary(StatusTone.Gray, head, string.Empty); // 멈춘 서비스의 단계 기록은 지금 상태가 아니므로 세지 않음
        }

        int warmed = 0, busy = 0, hold = 0;
        foreach (string name in registered)
        {
            if (auto is null || !auto.Games.TryGetValue(name, out AutoGame? g))
            {
                continue;
            }

            string phase = AutoStatusFile.PrewarmPhase(g);
            warmed += phase == "warm" ? 1 : 0;
            StatusTone stage = StageTone(g.Stage, g.Detail);
            hold += stage == StatusTone.Warn ? 1 : 0;
            busy += stage == StatusTone.Busy || phase == "warming" ? 1 : 0; // discovered 는 진행·보류 어느 쪽도 아님 (발견만 된 상태, 곧 트레이스 단계로 넘어감)
        }

        var counts = new List<string>();
        AddCount(counts, "Tray_Warmed", warmed);
        AddCount(counts, "Tray_Busy", busy);
        AddCount(counts, "Tray_Hold", hold);
        if (problem)
        {
            counts.Add(L.T("Tray_Problem"));
        }

        StatusTone tone = hold > 0 || problem ? StatusTone.Warn : busy > 0 || service == ServiceState.Pending ? StatusTone.Busy : StatusTone.Ok;
        return new TraySummary(tone, head, string.Join(" · ", counts));
    }

    /// 툴팁: 요약 한 줄 + 최근 메시지(있으면) 줄들. 127 자를 넘으면 잘라 "…" 를 붙입니다.
    public static string Tip(TraySummary summary, params string[] recent)
    {
        string text = string.Join("\n", recent.Where(r => r.Length > 0).Prepend("AsterCache: " + summary.Line));
        return text.Length <= TipMax ? text : text[..(TipMax - 1)] + "…";
    }

    /// 메시지가 만료 시간 (60 초) 안에 생겼을 때만 돌려줍니다 (오래된 결과가 툴팁에 남지 않게).
    public static string Recent(string text, DateTimeOffset at, DateTimeOffset now) => now - at <= MessageLife ? text : string.Empty;

    /// 메뉴의 게임 항목: "이름 · 단계 · 예열" (비어 있는 부분은 생략).
    public static string GameItem(string display, string stage, string prewarm) => string.Join(" · ", new[] { display, stage, prewarm }.Where(p => p.Length > 0));

    /// 상태 색의 짧은 말 (화면 머리의 배지 글자: 색만으로 구분하지 않음).
    public static string ToneWord(StatusTone tone) => L.T(tone switch { StatusTone.Ok => "Tone_Ok", StatusTone.Busy => "Tone_Busy", StatusTone.Warn => "Tone_Warn", _ => "Tone_Gray" });

    /// 서비스 시작/정지를 한 메뉴 항목으로: 문구 키와 눌러도 되는지. 전환 중·미설치·알 수 없음은 시작 문구를 끈 채로 보입니다.
    public static (string Key, bool Enabled) ServiceAction(ServiceState service) => service switch
    {
        ServiceState.Running => ("Btn_ServiceStop", true),
        ServiceState.Stopped => ("Btn_ServiceStart", true),
        _ => ("Btn_ServiceStart", false),
    };

    /// 게임 표 칩과 작업표시줄 집계가 같이 쓰는 단계 색. 재배치 대기는 사유가 있을 때 (일일 한도·점유 등) 만 보류(노랑), 사유 없는 대기는 평소 거치는 단계라 작업 중(파랑).
    public static StatusTone StageTone(string stage, string? detail) => stage switch
    {
        "done" => StatusTone.Ok,
        "tracing" or "learning" or "relocating" => StatusTone.Busy,
        "waiting_idle" => AutoStatusFile.ReasonText(detail, L.Current) is null ? StatusTone.Busy : StatusTone.Warn,
        "failed" or "skipped" => StatusTone.Warn,
        _ => StatusTone.Gray,
    };

    /// 예열 칩의 색: PrewarmKind 기준 (ready 전부, warming 진행, partial 일부만, 그 밖은 회색).
    public static StatusTone PrewarmTone(string kind) => kind switch
    {
        "ready" => StatusTone.Ok,
        "warming" => StatusTone.Busy,
        "partial" => StatusTone.Warn,
        _ => StatusTone.Gray,
    };

    private static void AddCount(List<string> counts, string key, int n)
    {
        if (n > 0)
        {
            counts.Add(L.T(key, n));
        }
    }

    private static string ServiceWord(ServiceState s) => s switch
    {
        ServiceState.Running => L.T("Tray_Running"),
        ServiceState.Stopped => L.T("Tray_Stopped"),
        ServiceState.NotInstalled => L.T("Tray_NotInstalled"),
        ServiceState.Pending => L.T("Tray_Pending"),
        _ => L.T("Tray_Unknown"),
    };
}
