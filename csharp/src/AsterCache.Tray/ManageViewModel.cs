using System.ComponentModel;
using System.Globalization;
using System.Text;
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
        $"{Letter} {(Label.Length == 0 ? "(이름 없음)" : Label)}\n{Kind} · {(Model.Length == 0 ? "모델 알 수 없음" : Model)} · {FileSystem}\n"
        + $"{ManageViewModel.Size(FreeBytes)} 여유 / {ManageViewModel.Size(TotalBytes)} · 게임 {Games}개";
}

/// 게임 탭 한 줄. Verdict: aster verify 의 판정 (contiguous, non-contiguous, resume-needed), 계획이 없으면 "-", 아직 확인 전이면 "?".
public sealed record GameRow(string Name, string Exe, string Origin, bool HasPlan, bool HasTrace, string Verdict, string Prewarm);

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

    public bool IsAdmin => isAdmin();

    /// 승격해서 실행할 aster.exe 의 폴더를 일반 사용자가 바꿀 수 있으면 경고 문구 (그 파일을 바꿔 치면 관리자 권한을 얻음), 안전하면 null.
    public string? ElevatedExeWarning
    {
        get
        {
            try
            {
                return (untrustedWriters ?? AclCheck.UntrustedWriters)(AppContext.BaseDirectory).Count > 0
                    ? "경고: aster.exe 가 있는 폴더를 일반 사용자가 바꿀 수 있습니다. 승격 실행 전에 관리자 전용 폴더 (예: Program Files) 로 옮겨 쓰십시오."
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public string DataDir => Path.GetDirectoryName(config.ConfigPath)!;

    public string PrewarmStatusPath => Path.Combine(DataDir, "prewarm_status.json");

    public string RawTracePath(string name) => Path.Combine(config.PlansDir, name + ".trace.json");

    /// 서비스의 재적용 저널과 같은 자리 (이어하기·되돌리기가 서비스와 맞음).
    public string JournalPath(string name) => Path.Combine(DataDir, "journals", name + ".jsonl");

    private IReadOnlyList<GameSettings> Games => tray.Settings?.Games ?? [];

    // ---- 순수 계산 (시험 대상) ----

    public static string KindOf(bool? incursSeekPenalty) => incursSeekPenalty switch { true => "HDD", false => "SSD", _ => "알 수 없음" };

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
        $"서비스 시작 시 {(p.OnServiceStart ? "예" : "아니오")} · 트리거 {(p.TriggerExes.Count == 0 ? "없음" : string.Join(", ", p.TriggerExes))} · 여유 RAM {p.MinFreeRamMib} MiB · 쿨다운 {p.CooldownMin}분";

    /// 줄 끝 N 줄 (쓰는 중인 로그도 읽도록 공유 읽기).
    public static string Tail(string path, int lines = LogLines)
    {
        if (!File.Exists(path))
        {
            return "(파일 없음: " + path + ")";
        }

        try
        {
            return string.Join(Environment.NewLine, ReadLines(path).TakeLast(lines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "(읽지 못함: " + e.GetType().Name + ")";
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
                DescribePrewarm(g.Prewarm));
        }).ToList();

    /// 서비스 상태 + 감시기 + 마지막 예열 + 게임별 배치 상태.
    public IReadOnlyList<string> StatusLines(string? letter)
    {
        var lines = new List<string> { tray.StatusText, "감시기: " + (tray.Settings?.Watcher ?? "알 수 없음"), LastPrewarm() };
        foreach (GameSettings g in Games.Where(g => letter is null || OnDrive(g, letter)))
        {
            string layout = !File.Exists(g.Plan) ? "계획 없음"
                : _status.TryGetValue(g.Name, out GameStatus? s)
                    ? $"배치 {s.Verdict} · 존 적중 {(s.ZoneHitPercent is { } p ? p.ToString("F1", CultureInfo.InvariantCulture) + "%" : "-")} · 마지막 재배치 {(s.LastRelayout is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "없음")}"
                    : "배치 확인 전 (새로 고침)";
            lines.Add($"[{g.Name}] {layout} · 예열 {PrewarmText(g.Name)}");
        }

        return lines;
    }

    /// aster status 로 예열 기록과 게임별 배치 상태를 다시 읽습니다 (게임마다 프로세스 한 번).
    public ActionResult RefreshStatus()
    {
        tray.Refresh();
        _status.Clear();
        CliResult all = cli.Run(AsterArgs.Status(PrewarmStatusPath, null, null));
        _prewarm = Get(all.Json, "prewarm") as JObject;
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

        return Report(true, $"상태를 새로 읽음 (게임 {Games.Count}개)");
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
            ? $"마지막 예열: {b.Name} {b.At.ToLocalTime():yyyy-MM-dd HH:mm} · {b.Bytes >> 20} MiB · {b.Seconds:F1} 초"
            : "마지막 예열: 기록 없음";
    }

    private string PrewarmText(string name)
    {
        object? e = Get(Get(_prewarm, "games"), name);
        return e is null ? "기록 없음" : Get(e, "result") as string is { } r && Get(e, "reason") is string why ? $"{r} ({why})" : Get(e, "result") as string ?? "?";
    }

    // ---- 게임 동작 ----

    /// 트레이스 수집 (관리자). 게임 프로세스가 뜬 뒤 사라지면, 또는 stop 이 취소되면 stop-file 로 aster 를 정상 종료시킵니다.
    /// stop-file 은 일반 사용자가 쓸 수 있는 임시 폴더에 둡니다 (계획 폴더는 관리자 전용).
    public ActionResult CollectTrace(string name, Action<string> progress, CancellationToken stop)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, $"{name}: 등록된 게임이 아님");
        }

        string raw = RawTracePath(g.Name);
        string stopFile = Path.Combine(tempDir ?? Path.GetTempPath(), $"aster_{g.Name}.stop");
        progress("트레이스 수집 중: 게임을 실행해 첫 맵까지 로딩한 뒤 종료하십시오");
        Task<CliResult> run = Task.Run(() => cli.Run(AsterArgs.Trace(g.ExeNames, g.OriginRoot, raw, stopFile), elevate: true));
        bool seen = false;
        while (!run.IsCompleted)
        {
            bool running = IsRunning(g);
            seen |= running;
            if (stop.IsCancellationRequested || (seen && !running))
            {
                progress("트레이스를 저장하는 중");
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
            return Failed(r, $"{g.Name}: 트레이스 수집");
        }

        long events = Get(r.Json, "union_events") as long? ?? 0;
        return Report(events > 0, events > 0
            ? $"{g.Name}: 트레이스 {events}건, {TraceBytes(raw) / Mib} MiB (64 KiB 블록 합집합)"
            : $"{g.Name}: 읽기 기록이 0건: 게임 폴더(원점 루트)와 실행 파일 이름을 확인하십시오");
    }

    /// 계획 만들기: 먼저 --dry-run 으로 핫 바이트를 보고 존 크기를 정한 뒤 (ZoneMib) 같은 트레이스로 계획을 씁니다 (계획 폴더는 관리자 전용이라 승격).
    public ActionResult MakePlan(string name, Action<string> progress)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, $"{name}: 등록된 게임이 아님");
        }

        string raw = RawTracePath(g.Name);
        if (!File.Exists(raw))
        {
            return Report(false, $"{g.Name}: 트레이스가 없음 (먼저 트레이스 수집)");
        }

        progress("핫 바이트 계산 중");
        CliResult probe = cli.Run(AsterArgs.Plan(raw, g.OriginRoot, ProbeZoneMib, null));
        if (!probe.Ok)
        {
            return Failed(probe, $"{g.Name}: 핫 바이트 계산");
        }

        long hot = Get(Get(probe.Json, "stats"), "hot_bytes") as long? ?? 0;
        if (hot == 0)
        {
            return Report(false, $"{g.Name}: 핫 바이트가 0: 트레이스에 게임 폴더 읽기가 없음");
        }

        long zone = ZoneMib(hot);
        progress($"핫 {hot / Mib} MiB, 존 {zone} MiB 로 계획을 만드는 중");
        CliResult made = cli.Run(AsterArgs.Plan(raw, g.OriginRoot, zone, g.Plan), elevate: true);
        if (!made.Ok)
        {
            return Failed(made, $"{g.Name}: 계획 만들기");
        }

        object? stats = Get(made.Json, "stats");
        string note = string.Equals(g.Trace, g.Plan + ".prefetch.json", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" (주의: 설정의 trace 가 {g.Plan}.prefetch.json 이 아니라 서비스가 이 계획의 읽기 순서를 쓰지 않음)";
        return Report(true, $"{g.Name}: 계획 만듦 · 핫 {(Get(stats, "hot_bytes") as long? ?? hot) / Mib} MiB · 존 {zone} MiB · 이동 {(Get(stats, "moved_bytes") as long? ?? 0) / Mib} MiB · 작업 {Get(stats, "tasks")}개{note}");
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
            return Report(false, $"{name}: 계획이 없음");
        }

        if (IsRunning(g))
        {
            return Report(false, $"{g.Name}: 게임이 실행 중이라 재배치하지 않음 (게임을 끝낸 뒤 다시 시도)");
        }

        string journal = JournalPath(g.Name);
        progress("재배치 검사 (dry-run)");
        CliResult dry = cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: false), elevate: true);
        if (!dry.Ok)
        {
            return Failed(dry, $"{g.Name}: 재배치 검사");
        }

        long todo = Get(dry.Json, "tasks_todo") as long? ?? 0;
        if (todo == 0)
        {
            return Report(true, $"{g.Name}: 옮길 작업이 없음 (이미 배치됨)");
        }

        Task<CliResult> run = Task.Run(() => cli.Run(AsterArgs.Apply(g.Plan, journal, g.AllowedRoots, g.VolumeLabel, execute: true), elevate: true));
        while (!run.Wait(_poll))
        {
            progress($"재배치 중: 저널 {JournalLines(journal)}줄 (작업 {todo}개)");
        }

        CliResult r = run.Result;
        if (!r.Ok)
        {
            return Failed(r, $"{g.Name}: 재배치");
        }

        string verdict = Get(cli.Run(AsterArgs.Verify(g.Plan)).Json, "verdict") as string ?? "?";
        long failed = Get(r.Json, "failed") is List<object?> f ? f.Count : 0;
        string stopped = Get(r.Json, "stopped") as string is { } s ? $" · 중단: {s}" : string.Empty;
        bool done = failed == 0 && stopped.Length == 0;
        return Report(done, $"{g.Name}: 재배치 끝 · 이동 {(Get(r.Json, "moved_bytes") as long? ?? 0) / Mib} MiB · 실패 {failed}건{stopped} · 배치 {verdict}"
            + (done ? $" (되돌리기 가능: 저널 {journal})" : " · 다시 실행하면 이어서 합니다"));
    }

    public ActionResult Revert(string name, Action<string> progress)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, $"{name}: 등록된 게임이 아님");
        }

        if (IsRunning(g))
        {
            return Report(false, $"{g.Name}: 게임이 실행 중이라 되돌리지 않음 (게임을 끝낸 뒤 다시 시도)");
        }

        string journal = JournalPath(g.Name);
        if (!File.Exists(journal))
        {
            return Report(false, $"{g.Name}: 되돌릴 저널이 없음");
        }

        progress("되돌리는 중");
        CliResult r = cli.Run(AsterArgs.Revert(journal, g.AllowedRoots, g.VolumeLabel), elevate: true);
        if (!r.Ok)
        {
            return Failed(r, $"{g.Name}: 되돌리기");
        }

        string stopped = Get(r.Json, "stopped") as string is { } s ? $" · 중단: {s}" : string.Empty;
        long skipped = Get(r.Json, "skipped") is List<object?> k ? k.Count : 0;
        bool done = stopped.Length == 0 && skipped == 0;
        return Report(done, $"{g.Name}: 되돌림 · 복원 {Get(r.Json, "restored")}건 · {(Get(r.Json, "moved_bytes") as long? ?? 0) / Mib} MiB · 건너뜀 {skipped}건{stopped}"
            + (done ? string.Empty : " · 다시 실행하면 이어서 합니다"));
    }

    /// 측정: aster prefetch --execute --timing 1회 (한 번 읽으면 파일 캐시가 데워지므로 같은 값을 다시 재려면 캐시를 비워야 함).
    public ActionResult Measure(string name)
    {
        if (Find(name) is not { } g)
        {
            return Report(false, $"{name}: 등록된 게임이 아님");
        }

        long? zone = PrefetchStreamer.ZoneBytes(g);
        long budget = Math.Min(g.MaxStreamMib, zone is > 0 ? (zone.Value + Mib - 1) / Mib : g.MaxStreamMib);
        CliResult r = cli.Run(AsterArgs.Timing(g.Trace, g.OriginRoot, budget));
        if (!r.Ok)
        {
            return Failed(r, $"{g.Name}: 측정");
        }

        long bytes = Get(r.Json, "bytes") as long? ?? 0;
        double seconds = Convert.ToDouble(Get(r.Json, "seconds") ?? 0.0, CultureInfo.InvariantCulture);
        object? t = Get(r.Json, "timing_ms");
        return Report(true, $"{g.Name}: 선읽기 {bytes / Mib} MiB, {seconds:F2} 초 ({(seconds > 0 ? bytes / (double)Mib / seconds : 0):F0} MB/s) · 단계(ms) 로드 {Get(t, "load_ms")}, 스케줄 {Get(t, "schedule_ms")}, 스트림 {Get(t, "stream_ms")}");
    }

    // ---- 설정·서비스 ----

    public ActionResult InstallService()
    {
        string exe = serviceExe ?? Path.Combine(AppContext.BaseDirectory, "AsterCache.Service.exe");
        if (!File.Exists(exe))
        {
            return Report(false, "서비스 실행 파일(AsterCache.Service.exe)을 찾지 못함: 설치 프로그램을 사용하십시오");
        }

        if (!File.Exists(config.ConfigPath))
        {
            return Report(false, "config.json 이 없음: 먼저 게임을 한 개 이상 등록하십시오");
        }

        if (exe.IndexOfAny(['&', '^', '%', '!']) >= 0 || DataDir.IndexOfAny(['&', '^', '%', '!']) >= 0)
        {
            return Report(false, "경로에 & ^ % ! 문자가 있어 설치하지 않음");
        }

        // 서비스는 SYSTEM 으로 이 파일을 실행하므로 일반 사용자가 바꿀 수 있는 폴더에서는 등록하지 않습니다.
        IReadOnlyList<string> writers = (untrustedWriters ?? AclCheck.UntrustedWriters)(Path.GetDirectoryName(exe)!);
        return writers.Count > 0
            ? Report(false, "서비스 실행 파일 폴더를 일반 사용자가 바꿀 수 있어 설치하지 않음 (SYSTEM 권한 상승 위험): 설치 프로그램을 사용하십시오")
            : tray.InstallService(exe);
    }

    public ActionResult RelaunchAsAdmin()
    {
        try
        {
            elevator.Start(Environment.ProcessPath!, relaunchArgs);
            return Report(true, "관리자 권한으로 다시 시작함");
        }
        catch (Win32Exception e)
        {
            return Report(false, e.NativeErrorCode == 1223 ? "관리자 승인이 취소됨" : $"다시 실행하지 못함 ({e.NativeErrorCode})");
        }
    }

    public ActionResult OpenDataFolder()
    {
        try
        {
            shell.Open(DataDir);
            return Report(true, "데이터 폴더 열음");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return Report(false, "데이터 폴더를 열지 못함");
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

    private ActionResult Failed(CliResult r, string what)
    {
        string why = Get(r.Json, "error") is string e ? e + (Get(r.Json, "detail") is string d ? ": " + d : string.Empty) : r.Output.Trim();
        return Report(false, $"{what} 실패: {(why.Length > 300 ? why[..300] + "..." : why)}");
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
