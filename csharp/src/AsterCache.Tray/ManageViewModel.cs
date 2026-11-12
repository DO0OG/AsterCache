using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Metrics;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Windows;

namespace AsterCache.Tray;

/// 고정 볼륨 한 개의 사실 (시험은 가짜 목록을 줍니다). IncursSeekPenalty: true HDD, false SSD, null 알 수 없음.
public sealed record DriveFacts(string Letter, string Label, string FileSystem, long TotalBytes, long FreeBytes, string Model, bool? IncursSeekPenalty);

public interface IDriveSource
{
    IReadOnlyList<DriveFacts> Fixed();
}

public sealed class SystemDrives : IDriveSource
{
    public IReadOnlyList<DriveFacts> Fixed()
    {
        var list = new List<DriveFacts>();
        foreach (DriveInfo d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            try
            {
                if (d.IsReady)
                {
                    string letter = d.Name[..2];
                    DiskDetails disk = DiskInfo.Query(letter);
                    list.Add(new DriveFacts(letter, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, disk.Model, disk.IncursSeekPenalty));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 읽을 수 없는 볼륨은 목록에서 뺌
            }
        }

        return list;
    }
}

/// 왼쪽 드라이브 목록 한 줄. ToString 이 목록에 보이는 글입니다.
public sealed record DriveRow(string Letter, string Label, string FileSystem, long TotalBytes, long FreeBytes, string Model, string Kind, int Games)
{
    public override string ToString() =>
        $"{Letter} {(Label.Length == 0 ? L.T("Drive_NoName") : Label)}\n{Kind} · {(Model.Length == 0 ? L.T("Drive_ModelUnknown") : Model)} · {FileSystem}\n"
        + L.T("Drive_Line", ManageViewModel.Size(FreeBytes), ManageViewModel.Size(TotalBytes), Games);
}

/// 게임 탭 한 줄. 기본 열은 DisplayName·State (자동 단계)·PrewarmState (PrewarmKind: ready 이번 부팅 뒤 예열, partial 앞부분만, history 재부팅 전 과거 기록, none 실측 안 됨, warming 예열 중)·Reason·AntiCheat, 나머지는 고급 열. Verdict: aster verify 의 판정 (contiguous, non-contiguous, resume-needed), 계획이 없으면 "-", 아직 확인 전이면 "?".
public sealed record GameRow(string Name, string Exe, string Origin, bool HasPlan, bool HasTrace, string Verdict, string Prewarm, string Source, string Stage, string AntiCheat, string PrewarmFull, string DisplayName = "", string VerdictRaw = "", string StageRaw = "", string State = "", string PrewarmState = "", string PrewarmKind = "", string Reason = "", string Gate = "", string StageFull = "", StatusTone StateTone = StatusTone.Gray, StatusTone PrewarmTone = StatusTone.Gray, string Detail = "", string AutoLine = "");

/// 게임 하나의 배치 상태 (aster status --plan 결과와 저널 파일 시각).
public sealed record GameStatus(string Verdict, double? ZoneHitPercent, DateTime? LastRelayout);

/// 관리 창이 보여주고 부르는 모든 것. 로직은 새로 만들지 않고 aster.exe (IAsterCli) 의 JSON 과 TrayViewModel·ConfigEditor 를 씁니다.
/// 오래 걸리는 메서드는 동기이며, 창이 백그라운드 스레드에서 부릅니다.
public sealed class ManageViewModel(
    TrayViewModel tray, ConfigEditor config, IAsterCli cli, IDriveSource drives, IElevator elevator, IShell shell, ILog log, Func<bool> isAdmin,
    Func<IEnumerable<string>, bool> isRunning, string relaunchArgs = "", TimeSpan? poll = null, string? tempDir = null,
    Func<string, IReadOnlyList<string>>? untrustedWriters = null, string? serviceExe = null, DateTimeOffset? bootAt = null)
{
    public const int LogLines = 200;
    private const long Mib = 1L << 20;
    private const long ProbeZoneMib = 16384;
    private const int ZoneRoundMib = 16;

    // 배치 판정은 백그라운드 스레드가 채우고 화면 스레드가 읽으므로 동시 사전. 마지막으로 안 판정을 계속 보여 주고, 계획·저널 수정 시각이 같으면 다시 묻지 않습니다.
    private readonly ConcurrentDictionary<string, GameStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Plan, DateTime Journal, DateTime At)> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _generation = new(StringComparer.OrdinalIgnoreCase); // 게임별: 배치를 바꾸는 동작이 끝날 때마다 올림
    private readonly FileMetadataCache<AutoStatusFile> _autoCache = new();
    private readonly FileMetadataCache<IReadOnlyList<LoadSample>> _samplesCache = new();
    private readonly object _reloadGate = new();
    private static readonly TimeSpan LayoutMaxAge = TimeSpan.FromMinutes(30); // 파일이 그대로여도 게임 업데이트 등으로 배치가 바뀔 수 있어 이 시간이 지나면 다시 확인
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(500);
    private JObject? _prewarm;
    private AutoStatusFile? _auto;
    private IReadOnlyList<LoadSample> _samples = [];

    public bool IsAdmin => isAdmin();

    /// 승격해서 실행할 aster.exe 의 폴더를 일반 사용자가 바꿀 수 있으면 경고 문구 (그 파일을 바꿔 치면 관리자 권한을 얻음), 안전하면 null.
    public string? ElevatedExeWarning
    {
        get
        {
            try
            {
                return (untrustedWriters ?? AclCheck.UntrustedWriters)(AppContext.BaseDirectory).Count > 0
                    ? L.T("Warn_ExeFolder")
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public string DataDir => Path.GetDirectoryName(config.ConfigPath)!;

    public string AutoStatusPath => Path.Combine(DataDir, "auto_status.json");

    public string PrewarmStatusPath => Path.Combine(DataDir, "prewarm_status.json");

    public string RawTracePath(string name) => Path.Combine(config.PlansDir, name + ".trace.json");

    /// 서비스의 재적용 저널과 같은 자리 (이어하기·되돌리기가 서비스와 맞음).
    public string JournalPath(string name) => Path.Combine(DataDir, "journals", name + ".jsonl");

    private IReadOnlyList<GameSettings> Games => tray.Settings?.Games ?? [];

    // ---- 순수 계산 (시험 대상) ----

    public static string KindOf(bool? incursSeekPenalty) => incursSeekPenalty switch { true => "HDD", false => "SSD", _ => L.T("Kind_Unknown") };

    public static string Size(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):F1} TiB" : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GiB" : $"{bytes >> 20} MiB";

    /// 핫 바이트 전체를 담는 존 크기 (MiB): 1.1 배를 16 MiB 단위로 올림, 최소 16.
    public static long ZoneMib(long hotBytes)
    {
        long padded = ((hotBytes * 11) + 9) / 10;
        long mib = (padded + Mib - 1) / Mib;
        return Math.Max(ZoneRoundMib, (mib + ZoneRoundMib - 1) / ZoneRoundMib * ZoneRoundMib);
    }

    /// exe 파일 하나로 등록 입력을 채웁니다: 원점 루트·허용 루트는 exe 폴더, 이름은 파일 이름에서 (영문·숫자·. _ - 만).
    /// 트레이스는 aster plan 이 계획 옆에 쓰는 이름 (<계획>.prefetch.json) 으로 잡습니다.
    public static GameInput InferGame(string exePath, string volumeLabel, string plansDir)
    {
        string folder = Path.GetDirectoryName(exePath)!.TrimEnd('\\');
        string stem = new(Path.GetFileNameWithoutExtension(exePath).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        string name = stem.Length == 0 ? "game" : stem[..Math.Min(stem.Length, 64)];
        string plan = Path.Combine(plansDir, name + ".plan.json");
        return new GameInput(name, [Path.GetFileName(exePath)], plan + ".prefetch.json", plan, folder, [folder], volumeLabel[..Math.Min(volumeLabel.Length, 32)]);
    }

    /// 로딩 표본 기록을 읽습니다. 없으면 빈 목록 (기록 없음으로 보입니다). 읽기 입출력 오류는 호출부에 던져 직전 값을 유지하게 합니다.
    public static IReadOnlyList<LoadSample> LoadSamples(string path)
    {
        try
        {
            return File.Exists(path) ? MetricsJson.ParseSamples(ReadText(path)) : [];
        }
        catch (PyValueException)
        {
            return [];
        }
    }

    /// 게임 하나의 성과 판정 한 줄 (V2-09, 읽기 전용). 기록이 없으면 기록 없음, 그 밖에는 ResultGate 의 판정과 사유. 자동 기본값·실행 경로는 이 값을 보지 않습니다.
    public static string GateText(IReadOnlyList<LoadSample> samples, string game)
    {
        List<LoadSample> mine = samples.Where(s => s.GameKey == game).ToList();
        if (mine.Count == 0)
        {
            return L.T("Gate_NoRecord");
        }

        GateResult r = ResultGate.Evaluate(mine.Where(s => s.Arm == Arm.Baseline).ToList(), mine.Where(s => s.Arm == Arm.Candidate).ToList());
        string why = GateReason(r);
        return r.Verdict == ResultGate.Reject ? L.T("Gate_Reject", why) : L.T("Gate_Unverified", why);
    }

    /// 사유 코드를 현재 언어의 문구로 (로그·JSON 의 한국어 Reason 은 쓰지 않습니다).
    private static string GateReason(GateResult r) => r.ReasonCode switch
    {
        "mixed_conditions" => L.T("Gate_Why_mixed_conditions"),
        "mixed_arms" => L.T("Gate_Why_mixed_arms"),
        "too_few_samples" => L.T("Gate_Why_too_few_samples", r.BaselineUsed, r.CandidateUsed, ResultGate.MinSamples),
        "worse_p50" => L.T("Gate_Why_worse_median"),
        "worse_p95" => L.T("Gate_Why_worse_tail"),
        "worse_p50_p95" => L.T("Gate_Why_worse_both"),
        "not_worse_no_ci" => L.T("Gate_Why_not_worse_no_ci"),
        _ => r.Reason,
    };

    public static IReadOnlyList<DriveRow> BuildDrives(IEnumerable<DriveFacts> facts, IReadOnlyList<GameSettings> games) =>
        facts.Select(d => new DriveRow(d.Letter, d.Label, d.FileSystem, d.TotalBytes, d.FreeBytes, d.Model, KindOf(d.IncursSeekPenalty),
            games.Count(g => OnDrive(g, d.Letter)))).ToList();

    public static bool OnDrive(GameSettings game, string letter) => game.OriginRoot.StartsWith(letter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string DescribePrewarm(PrewarmSettings p) =>
        L.T("Prewarm_Line", L.T(p.OnServiceStart ? "Word_Yes" : "Word_No"), p.TriggerExes.Count == 0 ? L.T("Word_None") : string.Join(", ", p.TriggerExes), p.MinFreeRamMib, p.CooldownMin);

    public static string DescribePrewarmShort(PrewarmSettings p) =>
        L.T("Prewarm_Short", L.T(p.OnServiceStart ? "Word_Yes" : "Word_No"), p.TriggerExes.Count == 0 ? L.T("Word_None") : string.Join(", ", p.TriggerExes));

    /// 줄 끝 N 줄 (쓰는 중인 로그도 읽도록 공유 읽기).
    public static string Tail(string path, int lines = LogLines)
    {
        if (!File.Exists(path))
        {
            return L.T("Msg_FileMissing", path);
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return string.Join(Environment.NewLine, ReadTail(reader, lines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return L.T("Msg_ReadFailed", e.GetType().Name);
        }
    }

    internal static Queue<string> ReadTail(TextReader reader, int lines)
    {
        var tail = new Queue<string>(Math.Max(0, lines));
        while (reader.ReadLine() is { } line)
        {
            if (lines <= 0)
            {
                continue;
            }

            if (tail.Count == lines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return tail;
    }

    private static List<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---- 목록 ----

    public IReadOnlyList<DriveRow> Drives() => BuildDrives(drives.Fixed(), Games);

    /// letter 가 null 이면 모든 드라이브.
    public IReadOnlyList<GameRow> GameRows(string? letter) =>
        Games.Where(g => letter is null || OnDrive(g, letter)).Select(g =>
        {
            bool plan = File.Exists(g.Plan);
            string verdict = plan ? _status.TryGetValue(g.Name, out GameStatus? s) ? s.Verdict : "?" : "-";
            AutoGame? a = _auto?.Games.GetValueOrDefault(g.Name);
            (string kind, string warm) = PrewarmState(g.Name, a);
            return new GameRow(g.Name, string.Join(", ", g.ExeNames), g.OriginRoot, plan, File.Exists(g.Trace), VerdictText(verdict),
                DescribePrewarmShort(g.Prewarm), L.T(g.Source == "auto" ? "Source_Auto" : "Source_Manual"),
                a is null ? "-" : AutoStatusFile.StageShort(a, L.Current, Boot), g.AntiCheat ?? "-", DescribePrewarm(g.Prewarm), tray.DisplayName(g.Name), verdict, a?.Detail ?? string.Empty,
                a is null ? "-" : AutoStatusFile.StageName(a.Stage, L.Current), warm, kind, HoldReasons(g.Name, a, kind, verdict), GateText(_samples, g.Name),
                a is null ? "-" : AutoStatusFile.StageText(a, L.Current, Boot), a is null ? StatusTone.Gray : TrayStatus.StageTone(a.Stage, a.Detail), TrayStatus.PrewarmTone(kind),
                L.T("Layout_Detail", LayoutText(g), PrewarmText(g.Name)), a is null ? string.Empty : AutoStatusFile.Line(tray.DisplayName(g.Name), a, L.Current));
        }).ToList();

    /// 마지막으로 읽은 auto_status.json (아직 못 읽었거나 없으면 null). 트레이 아이콘·툴팁이 5 초마다 씁니다.
    public AutoStatusFile? Auto => _auto;

    /// 트레이 메뉴의 게임 항목용 짧은 상태: 자동 단계 이름과, 예열 중·예열됨·부분 예열일 때만 짧은 예열 말 (진행량은 넣지 않아 메뉴 글이 자주 바뀌지 않음, 없으면 빈 글).
    public (string Stage, string Prewarm) GameBrief(string name)
    {
        AutoGame? a = _auto?.Games.GetValueOrDefault(name);
        (string kind, _) = PrewarmState(name, a);
        return (a is null ? string.Empty : AutoStatusFile.StageName(a.Stage, L.Current), kind switch { "ready" => L.T("Stage_Prewarmed"), "warming" => L.T("Tray_Warming"), "partial" => L.T("Tray_WarmPartial"), _ => string.Empty });
    }

    private DateTimeOffset Boot => bootAt ?? DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// 예열 상태 한 가지로: 지금 예열 중, 이번 부팅 뒤 성공 (전부 / 앞부분만), 재부팅 전 기록 (지금 캐시에는 없음), 기록 없음 (실측 안 됨). 서로 다른 문구로 보입니다.
    private (string Kind, string Text) PrewarmState(string name, AutoGame? a)
    {
        if (a is not null && AutoStatusFile.PrewarmingText(a, L.Current) is { } now)
        {
            return ("warming", now);
        }

        object? e = Get(Get(_prewarm, "games"), name);
        string? okText = Get(e, "last_ok_at") as string ?? (Get(e, "result") as string == "ok" ? Get(e, "at") as string : null); // 마지막 시도가 건너뜀·실패여도 마지막 성공 시각이 남음
        if (!DateTimeOffset.TryParse(okText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset ok))
        {
            return ("none", L.T("State_NotMeasured"));
        }

        if (ok <= Boot)
        {
            return ("history", L.T("State_History")); // 부팅하면 파일 캐시가 비므로 예전 성공은 지금 준비 상태가 아님
        }

        return Get(e, "warmed_mib") is long w && Get(e, "hot_mib") is long h && w < h ? ("partial", L.T("State_Partial", w, h)) : ("ready", L.T("State_Ready"));
    }

    // 기본 화면에 남길 사유: 자동 단계의 대기·실패 사유, 예열이 준비 상태가 아닌 까닭 (마지막 시도의 건너뜀·실패), 이어 옮기기 필요·확인 오류.
    private string HoldReasons(string name, AutoGame? a, string prewarmKind, string verdict)
    {
        var why = new List<string>();
        if (a is not null && AutoStatusFile.HoldReason(a, L.Current) is { } stage)
        {
            why.Add(stage);
        }

        object? e = Get(Get(_prewarm, "games"), name);
        if (Get(e, "result") is "skipped" or "failed" && Get(e, "reason") is string reason && prewarmKind != "ready")
        {
            why.Add(L.T("Reason_PrewarmLast", PrewarmResultText(Get(e, "result") as string), PrewarmReasonText(reason)));
        }

        if (verdict is "resume-needed" or "error")
        {
            why.Add(L.T("Reason_Layout", VerdictText(verdict)));
        }

        return string.Join(", ", why);
    }

    // 서비스가 prewarm_status.json 의 reason 에 적는 영어 사유 (앞부분) → 짧은 설명 키. 긴 접두어를 먼저 둡니다.
    private static readonly (string Prefix, string Key)[] PrewarmReasons =
    [
        ("no zone", "Pwhy_NoZone"), ("no trace file", "Pwhy_NoTrace"), ("game is running", "Pwhy_GameRunning"), ("low memory", "Pwhy_LowMemory"),
        ("already warm", "Pwhy_AlreadyWarm"), ("startup byte cap", "Pwhy_StartupCap"), ("cancelled", "Pwhy_Cancelled"), ("post-update delay", "Pwhy_PostUpdate"),
        ("relocation running", "Pwhy_Relocating"), ("batch memory budget", "Pwhy_BatchBudget"), ("prewarm already running", "Pwhy_AlreadyRunning"),
        ("nothing read", "Pwhy_NothingRead"), ("no profile; trace needed", "Pwhy_NoProfileTrace"), ("no profile", "Pwhy_NoProfile"), ("drive busy", "Pwhy_DriveBusy"),
    ];

    private static readonly Regex CooldownReason = new(@"^cooldown \((\d+) min left\)", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// 예열 사유 영어 원문을 현재 언어의 짧은 설명으로 (모르는 사유는 원문 그대로).
    public static string PrewarmReasonText(string reason)
    {
        if (CooldownReason.Match(reason) is { Success: true } m)
        {
            return L.T("Pwhy_Cooldown", m.Groups[1].Value);
        }

        if (reason.StartsWith("failed: ", StringComparison.Ordinal))
        {
            return L.T("Pwhy_Failed", reason[8..]);
        }

        foreach ((string prefix, string key) in PrewarmReasons)
        {
            if (reason.StartsWith(prefix, StringComparison.Ordinal))
            {
                return L.T(key);
            }
        }

        return reason;
    }

    /// 예열 결과(ok·skipped·failed)를 현재 언어의 말로 (모르는 값은 원문, 없으면 ?).
    public static string PrewarmResultText(string? result) => result switch
    {
        "ok" => L.T("Word_ResultOk"),
        "skipped" => L.T("Stage_Skipped"),
        "failed" => L.T("Stage_Failed"),
        null => "?",
        _ => result,
    };

    /// aster verify 판정을 현재 언어의 짧은 말로 (모르는 값은 원문 그대로).
    public static string VerdictText(string verdict) => verdict switch
    {
        "contiguous" => L.T("Verdict_Contiguous"),
        "non-contiguous" => L.T("Verdict_NonContiguous"),
        "resume-needed" => L.T("Verdict_ResumeNeeded"),
        "error" => L.T("Verdict_Error"),
        _ => verdict,
    };

    /// 서비스 상태 + 감시기 + 마지막 예열 + 게임별 배치 상태.
    public IReadOnlyList<string> StatusLines(string? letter)
    {
        (string watcher, string lastPrewarm) = StatusInfo();
        var lines = new List<string> { tray.StatusText, watcher, lastPrewarm };
        if (Progress() is { } progress && progress.Line(L.Current) is { } progressLine)
        {
            lines.Add(progressLine);
        }

        foreach (GameSettings g in Games.Where(g => letter is null || OnDrive(g, letter)))
        {
            string layout = LayoutText(g);
            lines.Add(L.T("Layout_Game", tray.DisplayName(g.Name), layout, PrewarmText(g.Name)));
            if (_auto?.Games.TryGetValue(g.Name, out AutoGame? auto) == true)
            {
                lines.Add("  " + AutoStatusFile.Line(tray.DisplayName(g.Name), auto, L.Current));
            }
        }

        return lines;
    }

    /// 상태 탭 머리의 두 줄: 감시기 방식과 마지막 예열.
    public (string Watcher, string LastPrewarm) StatusInfo() => (L.T("Status_Watcher", tray.Settings?.Watcher ?? L.T("Word_Unknown")), LastPrewarm());

    /// 자동 진행 집계 (아직 auto_status.json 을 못 읽었으면 null).
    public AutoProgress? Progress() => _auto is null ? null : AutoStatusFile.Progress(_auto, config.PlansDir);

    private string LayoutText(GameSettings g) =>
        !File.Exists(g.Plan) ? L.T("Layout_NoPlan")
            : _status.TryGetValue(g.Name, out GameStatus? s)
                ? L.T("Layout_Line", VerdictText(s.Verdict), s.ZoneHitPercent is { } p ? p.ToString("F1", CultureInfo.InvariantCulture) + "%" : "-", s.LastRelayout is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : L.T("Word_Never"))
                : L.T(LayoutChecking ? "Layout_Checking" : "Layout_Unchecked");

    /// auto_status.json 만 다시 읽습니다 (aster 프로세스 없음, 5 초 주기용). 읽은 결과를 돌려줍니다.
    public AutoStatusFile? ReloadAuto()
    {
        lock (_reloadGate)
        {
            _auto = _autoCache.Get(AutoStatusPath, () => AutoStatusFile.Load(AutoStatusPath), cacheNull: false);
            tray.LauncherNames = _auto?.Games.Where(kv => kv.Value.DisplayName is not null).ToDictionary(kv => kv.Key, kv => kv.Value.DisplayName!, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
            string samplesPath = Path.Combine(DataDir, MetricsJson.SamplesFile);
            try
            {
                _samples = _samplesCache.Get(samplesPath, () => LoadSamples(samplesPath)) ?? [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 서비스가 쓰는 중이거나 잠겨 못 읽으면 직전 값을 그대로 둡니다
            }

            return _auto;
        }
    }

    /// 게임별 배치 판정을 채우는 중이면 true (목록은 마지막 판정 또는 "확인 중" 으로 먼저 보임).
    public volatile bool LayoutChecking;

    /// 배치를 바꾸는 동작 (계획·재배치·되돌리기·가져오기) 을 감쌉니다: 끝나면 그 게임의 판정 재사용을 끊고, 동작 전에 시작한 조회 결과는 버립니다 (RefreshLayouts).
    public ActionResult ChangesLayout(string name, Func<ActionResult> work)
    {
        try
        {
            return work();
        }
        finally
        {
            _generation.AddOrUpdate(name, 1, (_, v) => v + 1);
            _checked.TryRemove(name, out _);
        }
    }

    /// 서비스 상태와 예열 기록, auto_status.json 만 다시 읽습니다 (계획 없는 가벼운 aster status 한 번, 게임별 배치 조회 없음).
    public ActionResult RefreshOverview()
    {
        tray.Refresh();
        CliResult all = cli.Run(AsterArgs.Status(PrewarmStatusPath, AutoStatusPath, null, null));
        _prewarm = Get(all.Json, "prewarm") as JObject;
        ReloadAuto();
        return Report(true, L.T("Msg_StatusRefreshed", Games.Count));
    }

    /// 예열 기록과 게임별 배치 상태를 모두 다시 읽습니다 (RefreshOverview + RefreshLayouts, 동기).
    public ActionResult RefreshStatus()
    {
        ActionResult r = RefreshOverview();
        RefreshLayouts();
        return r;
    }

    /// 계획이 있는 게임의 배치 판정을 aster status --plan 으로 차례로 채웁니다 (디스크의 파일 배치를 조회하므로 느릴 수 있음: 창 쪽이 백그라운드로 부름).
    /// 계획·저널 수정 시각이 그대로면 마지막 판정을 쓰고 조회하지 않으며, 등록된 게임이 실행 중이면 조회를 모두 미룹니다 (프로세스 목록만 봄, 게임 프로세스는 열지 않음).
    /// 판정 하나가 바뀔 때마다 changed 를 부릅니다.
    public void RefreshLayouts(Action? changed = null)
    {
        LayoutChecking = true;
        try
        {
            foreach (GameSettings g in Games.Where(g => File.Exists(g.Plan)).ToList())
            {
                string journal = JournalPath(g.Name);
                bool hasJournal = File.Exists(journal);
                DateTime plan = File.GetLastWriteTimeUtc(g.Plan);
                DateTime written = hasJournal ? File.GetLastWriteTimeUtc(journal) : default;
                if (_checked.TryGetValue(g.Name, out var seen) && seen.Plan == plan && seen.Journal == written && DateTime.UtcNow - seen.At < LayoutMaxAge && _status.ContainsKey(g.Name))
                {
                    continue;
                }

                if (AnyGameRunning())
                {
                    log.Info("layout check deferred: a registered game is running");
                    return;
                }

                int generation = _generation.GetValueOrDefault(g.Name);
                CliResult r = cli.Run(AsterArgs.Status(PrewarmStatusPath, AutoStatusPath, g.Plan, hasJournal ? journal : null));
                if (_generation.GetValueOrDefault(g.Name) != generation)
                {
                    continue; // 조회 도중 배치를 바꾸는 동작이 끝남: 옛 결과로 덮지 않고 다음 읽기에서 다시 물음
                }

                object? p = Get(r.Json, "plan");
                long? hot = Get(p, "hot_clusters") as long?;
                long? inZone = Get(p, "in_zone_clusters") as long?;
                string verdict = Get(p, "verdict") as string ?? "error";
                _status[g.Name] = new GameStatus(verdict, hot > 0 && inZone is not null ? inZone * 100.0 / hot : null, hasJournal ? File.GetLastWriteTime(journal) : null);
                if (verdict != "error")
                {
                    _checked[g.Name] = (plan, written, DateTime.UtcNow); // 오류는 기억하지 않아 다음에 다시 물음
                }

                changed?.Invoke();
            }
        }
        finally
        {
            LayoutChecking = false;
        }
    }

    private bool AnyGameRunning()
    {
        try
        {
            return isRunning(Games.SelectMany(g => g.ExeNames).ToList());
        }
        catch (Win32Exception)
        {
            return true; // 프로세스 목록을 못 읽으면 실행 중으로 보고 미룸
        }
    }

    private string LastPrewarm()
    {
        (string Name, DateTimeOffset At, long Bytes, double Seconds)? best = null;
        if (Get(_prewarm, "games") is JObject games)
        {
            foreach (KeyValuePair<string, object?> kv in games)
            {
                if (Get(kv.Value, "result") as string == "ok" && DateTimeOffset.TryParse(Get(kv.Value, "at") as string, CultureInfo.InvariantCulture, out DateTimeOffset at)
                    && (best is null || at > best.Value.At))
                {
                    best = (kv.Key, at, Get(kv.Value, "bytes") as long? ?? 0, Convert.ToDouble(Get(kv.Value, "seconds") ?? 0.0, CultureInfo.InvariantCulture));
                }
            }
        }

        return best is { } b
            ? L.T("Prewarm_Last", tray.DisplayName(b.Name), b.At.ToLocalTime(), b.Bytes >> 20, b.Seconds)
            : L.T("Prewarm_LastNone");
    }

    private string PrewarmText(string name)
    {
        object? e = Get(Get(_prewarm, "games"), name);
        string text = e is null ? L.T("State_NotMeasured") : Get(e, "result") as string is { } r && Get(e, "reason") is string why ? $"{PrewarmResultText(r)} ({PrewarmReasonText(why)})" : PrewarmResultText(Get(e, "result") as string);
        if (Get(e, "partial") is true && Get(e, "warmed_mib") is long warmed && Get(e, "hot_mib") is long hot)
        {
            text = $"{text} · {L.T("Status_PrewarmPartial", warmed, hot)}"; // 메모리가 모자라 앞부분만 읽음: 완료로 보이지 않게
        }

        text = Get(e, "head_mib") is long head ? $"{text} · {L.T("Status_LoadHead", head)}" : text;
        return PrewarmState(name, null) is ("history", { } past) ? $"{text} · {past}" : text; // 재부팅 전 성공은 현재 준비 상태처럼 보이지 않게
    }

    // ---- 게임 동작 ----

    /// 트레이스 수집 (관리자). 게임 프로세스가 뜬 뒤 사라지면, 또는 stop 이 취소되면 stop-file 로 aster 를 정상 종료시킵니다.
    /// stop-file 은 일반 사용자가 쓸 수 있는 임시 폴더에 둡니다 (계획 폴더는 관리자 전용).
    public ActionResult CollectTrace(string name, Action<string> progress, CancellationToken stop)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        string raw = RawTracePath(g.Name);
        string stopFile = Path.Combine(tempDir ?? Path.GetTempPath(), $"aster_{g.Name}.stop");
        progress(L.T("Trace_Prompt"));
        Task<CliResult> run = Task.Run(() => cli.Run(AsterArgs.Trace(g.ExeNames, g.OriginRoot, raw, stopFile), elevate: true));
        bool seen = false;
        while (!run.IsCompleted)
        {
            bool running = IsRunning(g);
            seen |= running;
            if (stop.IsCancellationRequested || (seen && !running))
            {
                progress(L.T("Trace_Saving"));
                while (!run.Wait(_poll)) // aster 는 시작할 때 stop-file 을 지우므로 끝날 때까지 계속 만듭니다
                {
                    File.WriteAllText(stopFile, string.Empty);
                }

                break;
            }

            run.Wait(_poll);
        }

        File.Delete(stopFile);
        CliResult r = run.Result;
        if (!r.Ok)
        {
            return Failed(r, "Fail_Trace", g.Name);
        }

        long events = Get(r.Json, "union_events") as long? ?? 0;
        return Report(events > 0, events > 0
            ? L.T("Trace_Done", tray.DisplayName(g.Name), events, TraceBytes(raw) / Mib)
            : L.T("Trace_None", tray.DisplayName(g.Name)));
    }

    /// 계획 만들기: 먼저 --dry-run 으로 핫 바이트를 보고 존 크기를 정한 뒤 (ZoneMib) 같은 트레이스로 계획을 씁니다 (계획 폴더는 관리자 전용이라 승격).
    public ActionResult MakePlan(string name, Action<string> progress)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        string raw = RawTracePath(g.Name);
        if (!File.Exists(raw))
        {
            raw = Path.Combine(config.PlansDir, g.Name + GameProfile.BootTraceSuffix); // 가져온 프로필만 있으면 그것으로
        }

        if (!File.Exists(raw))
        {
            return Report(false, L.T("Plan_NoTrace", tray.DisplayName(g.Name)));
        }

        progress(L.T("Plan_Probing"));
        CliResult probe = cli.Run(AsterArgs.Plan(raw, g.OriginRoot, ProbeZoneMib, null));
        if (!probe.Ok)
        {
            return Failed(probe, "Fail_Probe", g.Name);
        }

        long hot = Get(Get(probe.Json, "stats"), "hot_bytes") as long? ?? 0;
        if (hot == 0)
        {
            return Report(false, L.T("Plan_NoHot", tray.DisplayName(g.Name)));
        }

        long zone = ZoneMib(hot);
        progress(L.T("Plan_Making", hot / Mib, zone));
        CliResult made = cli.Run(AsterArgs.Plan(raw, g.OriginRoot, zone, g.Plan, JournalPath(g.Name)), elevate: true); // 저널에 행이 있으면 CLI 가 활성 계획을 덮어쓰지 않고 잔여 재계획
        if (!made.Ok)
        {
            return Failed(made, "Fail_Plan", g.Name);
        }

        object? stats = Get(made.Json, "stats");
        string note = string.Equals(g.Trace, g.Plan + ".prefetch.json", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : L.T("Plan_Note", g.Plan);
        return Report(true, L.T("Plan_Done", tray.DisplayName(g.Name), (Get(stats, "hot_bytes") as long? ?? hot) / Mib, zone, (Get(stats, "moved_bytes") as long? ?? 0) / Mib, Get(stats, "tasks"), note));
    }

    /// 확인창에 보일 이동 바이트 (계획 파일의 stats), 계획이 없거나 읽지 못하면 null.
    public long? MovedBytes(string name)
    {
        try
        {
            return Find(name) is { } g && File.Exists(g.Plan) && Get(((JObject)JsonRead.ReadFile(g.Plan)!)["stats"], "moved_bytes") is long moved ? moved : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or InvalidCastException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// 재배치 실행: 승격 apply --dry-run (존 시작을 정해 계획에 저장) 뒤 --execute. 진행률은 저널 줄 수. 끝나면 verify.
    /// 저장된 계획과 저널이 서로 다른 계획이면 (dry-run 이 "journal belongs to a different plan" 으로 거절) 활성 계획으로 잔여 재계획을 한 번 하고 dry-run 부터 한 번만 다시 시도합니다.
    public ActionResult Apply(string name, Action<string> progress)
    {
        if (Find(name) is not { } g || !File.Exists(g.Plan))
        {
            return Report(false, L.T("Err_NoPlan", tray.DisplayName(name)));
        }

        if (IsRunning(g))
        {
            return Report(false, L.T("Err_GameRunningApply", tray.DisplayName(g.Name)));
        }

        string journal = JournalPath(g.Name);
        progress(L.T("Apply_Dry"));
        CliResult dry = cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: false), elevate: true);
        if (!dry.Ok && Get(dry.Json, "error") as string == "PlanRejected" && Get(dry.Json, "detail") as string is { } why
            && why.Contains("journal belongs to a different plan", StringComparison.Ordinal))
        {
            progress(L.T("Apply_JournalOtherPlan", tray.DisplayName(g.Name)));
            CliResult again = cli.Run(AsterArgs.Replan(g.Plan, journal), elevate: true);
            if (!again.Ok)
            {
                return Failed(again, "Fail_Plan", g.Name);
            }

            progress(L.T("Apply_Dry"));
            dry = cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: false), elevate: true);
        }

        if (!dry.Ok && Get(dry.Json, "error") as string == "PlanRejected" && Get(dry.Json, "detail") as string is { } blocked
            && blocked.StartsWith("zone_not_free", StringComparison.Ordinal))
        {
            // 점유·예약 충돌은 이 검사의 오류가 아니라 계획한 대상 공간을 지금 못 쓴다는 뜻: 아무것도 옮기지 않았고 서비스가 남은 부분의 새 위치를 찾음 (미실행으로 반환)
            string overlap = blocked.Contains("is reserved by another plan", StringComparison.Ordinal) ? L.T("Dry_DeferredReserved") : string.Empty;
            return Report(false, L.T("Dry_Deferred", tray.DisplayName(g.Name), overlap, blocked.Length > 300 ? blocked[..300] + "..." : blocked));
        }

        if (!dry.Ok)
        {
            return Failed(dry, "Fail_Dry", g.Name);
        }

        long todo = Get(dry.Json, "tasks_todo") as long? ?? 0;
        if (todo == 0)
        {
            return Report(true, L.T("Apply_Nothing", tray.DisplayName(g.Name)));
        }

        Task<CliResult> run = Task.Run(() => cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: true), elevate: true));
        while (!run.Wait(_poll))
        {
            progress(L.T("Apply_Progress", JournalLines(journal), todo));
        }

        CliResult r = run.Result;
        if (!r.Ok)
        {
            return Failed(r, "Fail_Apply", g.Name);
        }

        string verdict = Get(cli.Run(AsterArgs.Verify(g.Plan)).Json, "verdict") as string ?? "?";
        long failed = Get(r.Json, "failed") is List<object?> f ? f.Count : 0;
        string stopped = Get(r.Json, "stopped") as string is { } s ? L.T("Apply_Stopped", s) : string.Empty;
        bool done = failed == 0 && stopped.Length == 0;
        return Report(done, L.T("Apply_Done", tray.DisplayName(g.Name), (Get(r.Json, "moved_bytes") as long? ?? 0) / Mib, failed, stopped, verdict)
            + (done ? L.T("Apply_Undoable", journal) : L.T("Apply_Resume")));
    }

    public ActionResult Revert(string name, Action<string> progress)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        if (IsRunning(g))
        {
            return Report(false, L.T("Err_GameRunningRevert", tray.DisplayName(g.Name)));
        }

        string journal = JournalPath(g.Name);
        if (!File.Exists(journal))
        {
            return Report(false, L.T("Revert_NoJournal", tray.DisplayName(g.Name)));
        }

        progress(L.T("Revert_Running"));
        CliResult r = cli.Run(AsterArgs.Revert(journal, g.AllowedRoots, g.VolumeLabel), elevate: true);
        if (!r.Ok)
        {
            return Failed(r, "Fail_Revert", g.Name);
        }

        string stopped = Get(r.Json, "stopped") as string is { } s ? L.T("Apply_Stopped", s) : string.Empty;
        long skipped = Get(r.Json, "skipped") is List<object?> k ? k.Count : 0;
        bool done = stopped.Length == 0 && skipped == 0;
        return Report(done, L.T("Revert_Done", tray.DisplayName(g.Name), Get(r.Json, "restored"), (Get(r.Json, "moved_bytes") as long? ?? 0) / Mib, skipped, stopped)
            + (done ? string.Empty : L.T("Apply_Resume")));
    }

    /// 측정: aster prefetch --execute --timing 1회 (한 번 읽으면 파일 캐시가 데워지므로 같은 값을 다시 재려면 캐시를 비워야 함).
    public ActionResult Measure(string name)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        long? zone = PrefetchStreamer.ZoneBytes(g);
        long budget = zone is > 0 ? (Math.Min(g.StreamCapBytes(zone.Value), zone.Value) + Mib - 1) / Mib : g.MaxStreamMib;
        CliResult r = cli.Run(AsterArgs.Timing(g.Trace, g.OriginRoot, budget));
        if (!r.Ok)
        {
            return Failed(r, "Fail_Measure", g.Name);
        }

        long bytes = Get(r.Json, "bytes") as long? ?? 0;
        double seconds = Convert.ToDouble(Get(r.Json, "seconds") ?? 0.0, CultureInfo.InvariantCulture);
        object? t = Get(r.Json, "timing_ms");
        return Report(true, L.T("Measure_Done", tray.DisplayName(g.Name), bytes / Mib, seconds, seconds > 0 ? bytes / (double)Mib / seconds : 0, Get(t, "load_ms"), Get(t, "schedule_ms"), Get(t, "stream_ms")));
    }

    /// 프로필 내보내기: aster profile export (plans 폴더를 읽으므로 승격). 파일은 사용자가 고른 .json 또는 .zip.
    public ActionResult ExportProfile(string name, string file)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        CliResult r = cli.Run(AsterArgs.ProfileExport(g.Name, file, DataDir), elevate: true);
        return r.Ok
            ? Report(true, L.T("Profile_Exported", tray.DisplayName(g.Name), Get(r.Json, "files"), Get(r.Json, "raw_events"), Get(r.Json, "prefetch_events"), file))
            : Failed(r, "Fail_ProfileExport", g.Name);
    }

    /// 프로필 가져오기: aster profile import (plans 폴더에 쓰므로 승격). 파일이 하나라도 다르면 아무것도 쓰지 않고 다른 파일을 알려 줍니다.
    public ActionResult ImportProfile(string name, string file)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        CliResult r = cli.Run(AsterArgs.ProfileImport(g.Name, file, DataDir), elevate: true);
        if (Get(r.Json, "error") as string == "profile_mismatch")
        {
            string first = Get(r.Json, "mismatches") is List<object?> { Count: > 0 } list ? Get(list[0], "path") as string ?? "?" : "?";
            return Report(false, L.T("Profile_Mismatch", tray.DisplayName(g.Name), Get(r.Json, "mismatch_count"), first));
        }

        return r.Ok
            ? Report(true, L.T("Profile_Imported", tray.DisplayName(g.Name), Get(r.Json, "files"), Get(r.Json, "raw_events"), Get(r.Json, "prefetch_events")))
            : Failed(r, "Fail_ProfileImport", g.Name);
    }

    // ---- 설정·서비스 ----

    public ActionResult InstallService()
    {
        string exe = serviceExe ?? Path.Combine(AppContext.BaseDirectory, "AsterCache.Service.exe");
        if (!File.Exists(exe))
        {
            return Report(false, L.T("Err_ServiceExeMissing"));
        }

        if (!File.Exists(config.ConfigPath))
        {
            return Report(false, L.T("Err_NoConfig"));
        }

        if (exe.IndexOfAny(['&', '^', '%', '!']) >= 0 || DataDir.IndexOfAny(['&', '^', '%', '!']) >= 0)
        {
            return Report(false, L.T("Err_BadPathChars"));
        }

        // 서비스는 SYSTEM 으로 이 파일을 실행하므로 일반 사용자가 바꿀 수 있는 폴더에서는 등록하지 않습니다.
        IReadOnlyList<string> writers = (untrustedWriters ?? AclCheck.UntrustedWriters)(Path.GetDirectoryName(exe)!);
        return writers.Count > 0
            ? Report(false, L.T("Err_ExeFolderWritable"))
            : tray.InstallService(exe);
    }

    public ActionResult RelaunchAsAdmin()
    {
        try
        {
            elevator.Start(Environment.ProcessPath!, relaunchArgs);
            return Report(true, L.T("Msg_Relaunched"));
        }
        catch (Win32Exception e)
        {
            return Report(false, e.NativeErrorCode == 1223 ? L.T("Err_UacCancelled") : L.T("Err_Relaunch", e.NativeErrorCode));
        }
    }

    public ActionResult OpenDataFolder()
    {
        try
        {
            shell.Open(DataDir);
            return Report(true, L.T("Msg_DataOpened"));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return Report(false, L.T("Err_DataOpen"));
        }
    }

    public string ServiceLog() => Tail(Path.Combine(DataDir, "logs", "service.log"));

    /// 트레이 로그는 데이터 폴더에 못 쓰면 사용자별 폴더에 있으므로 더 최근에 쓴 쪽을 읽습니다.
    public string TrayLog()
    {
        string[] candidates =
        [
            Path.Combine(DataDir, "logs", "tray.log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsterCache", "logs", "tray.log"),
        ];
        return Tail(candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? candidates[0]);
    }

    // ---- 내부 ----

    private GameSettings? Find(string name) => Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsRunning(GameSettings g)
    {
        try
        {
            return isRunning(g.ExeNames);
        }
        catch (Win32Exception)
        {
            return true; // 프로세스 목록을 못 읽으면 끝났다고 보지 않음: 사용자가 수집 중지로 끝냄
        }
    }

    private long JournalLines(string journal)
    {
        try
        {
            return File.Exists(journal) ? ReadLines(journal).Count : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long TraceBytes(string path)
    {
        try
        {
            return JsonRead.ReadFile(path) is List<object?> rows ? rows.Sum(r => TraceEvents.FromJson(r).Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return 0;
        }
    }

    private static object? Get(object? o, string key) => o is JObject j && j.TryGetValue(key, out object? v) ? v : null;

    private ActionResult Failed(CliResult r, string failKey, string name)
    {
        string why = Get(r.Json, "error") is string e ? e + (Get(r.Json, "detail") is string d ? ": " + d : string.Empty) : r.Output.Trim();
        string hint = Get(r.Json, "error") as string == "PlanRejected" && Get(r.Json, "detail") as string is { } reason ? " " + PlanRejectedHint(reason) : string.Empty;
        return Report(false, L.T(failKey, tray.DisplayName(name), why.Length > 300 ? why[..300] + "..." : why) + hint);
    }

    /// PlanRejected 사유를 안내 문구로 바꿉니다 (영어 원문은 호출자가 메시지에 그대로 둡니다). zone_not_free 는 존 뒤 공간이 다른 파일 소유라는 뜻입니다.
    public static string PlanRejectedHint(string reason) =>
        reason.StartsWith("zone_not_free", StringComparison.Ordinal) ? L.T("Plan_ZoneNotFree") : L.T("Plan_Rejected", reason);

    private ActionResult Report(bool ok, string message)
    {
        log.Info(message);
        return new ActionResult(ok, message);
    }
}

/// aster 명령줄 조립 (옵션 이름은 Cli/Commands 의 ArgOption 과 같음).
public static class AsterArgs
{
    /// 트레이스 수집 상한 (초): 멈추는 길이 모두 막혀도 세션이 영원히 남지 않게 함.
    public const string MaxTraceSeconds = "3600";

    /// 예열·자동 상태 경로는 항상 넘깁니다 (없으면 aster 가 %ProgramData% 를 읽음).
    public static string[] Status(string prewarmStatus, string autoStatus, string? plan, string? journal) =>
    [
        "status",
        "--prewarm-status", prewarmStatus,
        "--auto-status", autoStatus,
        .. plan is null ? [] : new[] { "--plan", plan },
        .. journal is null ? [] : new[] { "--journal", journal },
    ];

    public static string[] Trace(IEnumerable<string> exes, string originRoot, string output, string stopFile) =>
        ["trace", "--live", "--exe", .. exes, "--origin-root", originRoot, "--out", output, "--stop-file", stopFile, "--seconds", MaxTraceSeconds];

    /// output 이 null 이면 --dry-run (아무것도 쓰지 않고 통계만). journal 을 주면 그 저널에 행이 있을 때 활성 계획을 덮어쓰지 않고 잔여 재계획.
    public static string[] Plan(string trace, string originRoot, long zoneMib, string? output, string? journal = null) =>
        ["plan", "--trace", trace, "--origin-root", originRoot, "--zone-mib", zoneMib.ToString(CultureInfo.InvariantCulture), .. output is null ? ["--dry-run"] : new[] { "--out", output },
            .. output is null || journal is null ? [] : new[] { "--journal", journal }];

    /// 트레이스 없이 활성 계획의 Order 를 후보로 잔여 재계획 (이미 옮긴 범위는 고정, 저널은 보관).
    public static string[] Replan(string plan, string journal) => ["plan", "--from-plan", plan, "--out", plan, "--journal", journal];

    public static string[] Apply(string plan, string journal, IEnumerable<string> allowedRoots, string label, bool execute) =>
        ["apply", "--plan", plan, "--journal", journal, .. Roots(allowedRoots), "--require-volume-label", label, execute ? "--execute" : "--dry-run"];

    public static string[] Revert(string journal, IEnumerable<string> allowedRoots, string label) =>
        ["revert", "--journal", journal, .. Roots(allowedRoots), "--require-volume-label", label, "--execute"];

    public static string[] Verify(string plan) => ["verify", "--plan", plan];

    public static string[] ProfileExport(string game, string output, string dataDir) => ["profile", "export", "--game", game, "--out", output, "--data-dir", dataDir];

    public static string[] ProfileImport(string game, string file, string dataDir) => ["profile", "import", "--file", file, "--game", game, "--data-dir", dataDir, "--json"];

    public static string[] Timing(string trace, string originRoot, long budgetMib) =>
        ["prefetch", "--trace", trace, "--origin-root", originRoot, "--budget-mib", budgetMib.ToString(CultureInfo.InvariantCulture), "--execute", "--timing"];

    private static IEnumerable<string> Roots(IEnumerable<string> roots) => roots.SelectMany(r => new[] { "--allowed-root", r });
}
