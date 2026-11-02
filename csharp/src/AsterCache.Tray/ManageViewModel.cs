using System.ComponentModel;
using System.Globalization;
using System.Text;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
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

/// 게임 탭 한 줄. Verdict: aster verify 의 판정 (contiguous, non-contiguous, resume-needed), 계획이 없으면 "-", 아직 확인 전이면 "?".
public sealed record GameRow(string Name, string Exe, string Origin, bool HasPlan, bool HasTrace, string Verdict, string Prewarm, string Source, string Stage, string AntiCheat);

/// 게임 하나의 배치 상태 (aster status --plan 결과와 저널 파일 시각).
public sealed record GameStatus(string Verdict, double? ZoneHitPercent, DateTime? LastRelayout);

/// 관리 창이 보여주고 부르는 모든 것. 로직은 새로 만들지 않고 aster.exe (IAsterCli) 의 JSON 과 TrayViewModel·ConfigEditor 를 씁니다.
/// 오래 걸리는 메서드는 동기이며, 창이 백그라운드 스레드에서 부릅니다.
public sealed class ManageViewModel(
    TrayViewModel tray, ConfigEditor config, IAsterCli cli, IDriveSource drives, IElevator elevator, IShell shell, ILog log, Func<bool> isAdmin,
    Func<IEnumerable<string>, bool> isRunning, string relaunchArgs = "", TimeSpan? poll = null, string? tempDir = null,
    Func<string, IReadOnlyList<string>>? untrustedWriters = null, string? serviceExe = null)
{
    public const int LogLines = 200;
    private const long Mib = 1L << 20;
    private const long ProbeZoneMib = 16384;
    private const int ZoneRoundMib = 16;

    private readonly Dictionary<string, GameStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(500);
    private JObject? _prewarm;
    private AutoStatusFile? _auto;

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

    public static IReadOnlyList<DriveRow> BuildDrives(IEnumerable<DriveFacts> facts, IReadOnlyList<GameSettings> games) =>
        facts.Select(d => new DriveRow(d.Letter, d.Label, d.FileSystem, d.TotalBytes, d.FreeBytes, d.Model, KindOf(d.IncursSeekPenalty),
            games.Count(g => OnDrive(g, d.Letter)))).ToList();

    public static bool OnDrive(GameSettings game, string letter) => game.OriginRoot.StartsWith(letter.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string DescribePrewarm(PrewarmSettings p) =>
        L.T("Prewarm_Line", L.T(p.OnServiceStart ? "Word_Yes" : "Word_No"), p.TriggerExes.Count == 0 ? L.T("Word_None") : string.Join(", ", p.TriggerExes), p.MinFreeRamMib, p.CooldownMin);

    /// 줄 끝 N 줄 (쓰는 중인 로그도 읽도록 공유 읽기).
    public static string Tail(string path, int lines = LogLines)
    {
        if (!File.Exists(path))
        {
            return L.T("Msg_FileMissing", path);
        }

        try
        {
            return string.Join(Environment.NewLine, ReadLines(path).TakeLast(lines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return L.T("Msg_ReadFailed", e.GetType().Name);
        }
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

    // ---- 목록 ----

    public IReadOnlyList<DriveRow> Drives() => BuildDrives(drives.Fixed(), Games);

    /// letter 가 null 이면 모든 드라이브.
    public IReadOnlyList<GameRow> GameRows(string? letter) =>
        Games.Where(g => letter is null || OnDrive(g, letter)).Select(g =>
        {
            bool plan = File.Exists(g.Plan);
            return new GameRow(g.Name, string.Join(", ", g.ExeNames), g.OriginRoot, plan, File.Exists(g.Trace), plan ? _status.TryGetValue(g.Name, out GameStatus? s) ? s.Verdict : "?" : "-",
                DescribePrewarm(g.Prewarm), L.T(g.Source == "auto" ? "Source_Auto" : "Source_Manual"),
                _auto?.Games.TryGetValue(g.Name, out AutoGame? a) == true ? AutoStatusFile.StageName(a.Stage, L.Current) : "-", g.AntiCheat ?? "-");
        }).ToList();

    /// 서비스 상태 + 감시기 + 마지막 예열 + 게임별 배치 상태.
    public IReadOnlyList<string> StatusLines(string? letter)
    {
        var lines = new List<string> { tray.StatusText, L.T("Status_Watcher", tray.Settings?.Watcher ?? L.T("Word_Unknown")), LastPrewarm() };
        foreach (GameSettings g in Games.Where(g => letter is null || OnDrive(g, letter)))
        {
            string layout = !File.Exists(g.Plan) ? L.T("Layout_NoPlan")
                : _status.TryGetValue(g.Name, out GameStatus? s)
                    ? L.T("Layout_Line", s.Verdict, s.ZoneHitPercent is { } p ? p.ToString("F1", CultureInfo.InvariantCulture) + "%" : "-", s.LastRelayout is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : L.T("Word_Never"))
                    : L.T("Layout_Unchecked");
            lines.Add(L.T("Layout_Game", g.Name, layout, PrewarmText(g.Name)));
            if (_auto?.Games.TryGetValue(g.Name, out AutoGame? auto) == true)
            {
                lines.Add("  " + AutoStatusFile.Line(g.Name, auto, L.Current));
            }
        }

        return lines;
    }

    /// auto_status.json 만 다시 읽습니다 (aster 프로세스 없음, 5 초 주기용). 읽은 결과를 돌려줍니다.
    public AutoStatusFile? ReloadAuto() => _auto = AutoStatusFile.Load(AutoStatusPath);

    /// aster status 로 예열 기록과 게임별 배치 상태를 다시 읽습니다 (게임마다 프로세스 한 번).
    public ActionResult RefreshStatus()
    {
        tray.Refresh();
        _status.Clear();
        CliResult all = cli.Run(AsterArgs.Status(PrewarmStatusPath, null, null));
        _prewarm = Get(all.Json, "prewarm") as JObject;
        ReloadAuto();
        foreach (GameSettings g in Games.Where(g => File.Exists(g.Plan)))
        {
            string journal = JournalPath(g.Name);
            bool hasJournal = File.Exists(journal);
            CliResult r = cli.Run(AsterArgs.Status(null, g.Plan, hasJournal ? journal : null));
            object? plan = Get(r.Json, "plan");
            long? hot = Get(plan, "hot_clusters") as long?;
            long? inZone = Get(plan, "in_zone_clusters") as long?;
            _status[g.Name] = new GameStatus(Get(plan, "verdict") as string ?? "error", hot > 0 && inZone is not null ? inZone * 100.0 / hot : null,
                hasJournal ? File.GetLastWriteTime(journal) : null);
        }

        return Report(true, L.T("Msg_StatusRefreshed", Games.Count));
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
            ? L.T("Prewarm_Last", b.Name, b.At.ToLocalTime(), b.Bytes >> 20, b.Seconds)
            : L.T("Prewarm_LastNone");
    }

    private string PrewarmText(string name)
    {
        object? e = Get(Get(_prewarm, "games"), name);
        return e is null ? L.T("Word_NoRecord") : Get(e, "result") as string is { } r && Get(e, "reason") is string why ? $"{r} ({why})" : Get(e, "result") as string ?? "?";
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
            ? L.T("Trace_Done", g.Name, events, TraceBytes(raw) / Mib)
            : L.T("Trace_None", g.Name));
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
            return Report(false, L.T("Plan_NoTrace", g.Name));
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
            return Report(false, L.T("Plan_NoHot", g.Name));
        }

        long zone = ZoneMib(hot);
        progress(L.T("Plan_Making", hot / Mib, zone));
        CliResult made = cli.Run(AsterArgs.Plan(raw, g.OriginRoot, zone, g.Plan), elevate: true);
        if (!made.Ok)
        {
            return Failed(made, "Fail_Plan", g.Name);
        }

        object? stats = Get(made.Json, "stats");
        string note = string.Equals(g.Trace, g.Plan + ".prefetch.json", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : L.T("Plan_Note", g.Plan);
        return Report(true, L.T("Plan_Done", g.Name, (Get(stats, "hot_bytes") as long? ?? hot) / Mib, zone, (Get(stats, "moved_bytes") as long? ?? 0) / Mib, Get(stats, "tasks"), note));
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
    public ActionResult Apply(string name, Action<string> progress)
    {
        if (Find(name) is not { } g || !File.Exists(g.Plan))
        {
            return Report(false, L.T("Err_NoPlan", name));
        }

        if (IsRunning(g))
        {
            return Report(false, L.T("Err_GameRunningApply", g.Name));
        }

        string journal = JournalPath(g.Name);
        progress(L.T("Apply_Dry"));
        CliResult dry = cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: false), elevate: true);
        if (!dry.Ok)
        {
            return Failed(dry, "Fail_Dry", g.Name);
        }

        long todo = Get(dry.Json, "tasks_todo") as long? ?? 0;
        if (todo == 0)
        {
            return Report(true, L.T("Apply_Nothing", g.Name));
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
        return Report(done, L.T("Apply_Done", g.Name, (Get(r.Json, "moved_bytes") as long? ?? 0) / Mib, failed, stopped, verdict)
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
            return Report(false, L.T("Err_GameRunningRevert", g.Name));
        }

        string journal = JournalPath(g.Name);
        if (!File.Exists(journal))
        {
            return Report(false, L.T("Revert_NoJournal", g.Name));
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
        return Report(done, L.T("Revert_Done", g.Name, Get(r.Json, "restored"), (Get(r.Json, "moved_bytes") as long? ?? 0) / Mib, skipped, stopped)
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
        long budget = Math.Min(g.MaxStreamMib, zone is > 0 ? (zone.Value + Mib - 1) / Mib : g.MaxStreamMib);
        CliResult r = cli.Run(AsterArgs.Timing(g.Trace, g.OriginRoot, budget));
        if (!r.Ok)
        {
            return Failed(r, "Fail_Measure", g.Name);
        }

        long bytes = Get(r.Json, "bytes") as long? ?? 0;
        double seconds = Convert.ToDouble(Get(r.Json, "seconds") ?? 0.0, CultureInfo.InvariantCulture);
        object? t = Get(r.Json, "timing_ms");
        return Report(true, L.T("Measure_Done", g.Name, bytes / Mib, seconds, seconds > 0 ? bytes / (double)Mib / seconds : 0, Get(t, "load_ms"), Get(t, "schedule_ms"), Get(t, "stream_ms")));
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
        return Report(false, L.T(failKey, name, why.Length > 300 ? why[..300] + "..." : why));
    }

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

    public static string[] Status(string? prewarmStatus, string? plan, string? journal) =>
    [
        "status",
        .. prewarmStatus is null ? [] : new[] { "--prewarm-status", prewarmStatus },
        .. plan is null ? [] : new[] { "--plan", plan },
        .. journal is null ? [] : new[] { "--journal", journal },
    ];

    public static string[] Trace(IEnumerable<string> exes, string originRoot, string output, string stopFile) =>
        ["trace", "--live", "--exe", .. exes, "--origin-root", originRoot, "--out", output, "--stop-file", stopFile, "--seconds", MaxTraceSeconds];

    /// output 이 null 이면 --dry-run (아무것도 쓰지 않고 통계만).
    public static string[] Plan(string trace, string originRoot, long zoneMib, string? output) =>
        ["plan", "--trace", trace, "--origin-root", originRoot, "--zone-mib", zoneMib.ToString(CultureInfo.InvariantCulture), .. output is null ? ["--dry-run"] : new[] { "--out", output }];

    public static string[] Apply(string plan, string journal, IEnumerable<string> allowedRoots, string label, bool execute) =>
        ["apply", "--plan", plan, "--journal", journal, .. Roots(allowedRoots), "--require-volume-label", label, execute ? "--execute" : "--dry-run"];

    public static string[] Revert(string journal, IEnumerable<string> allowedRoots, string label) =>
        ["revert", "--journal", journal, .. Roots(allowedRoots), "--require-volume-label", label, "--execute"];

    public static string[] Verify(string plan) => ["verify", "--plan", plan];

    public static string[] Timing(string trace, string originRoot, long budgetMib) =>
        ["prefetch", "--trace", trace, "--origin-root", originRoot, "--budget-mib", budgetMib.ToString(CultureInfo.InvariantCulture), "--execute", "--timing"];

    private static IEnumerable<string> Roots(IEnumerable<string> roots) => roots.SelectMany(r => new[] { "--allowed-root", r });
}
