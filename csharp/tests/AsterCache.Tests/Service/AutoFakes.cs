using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Tests.Service;

internal sealed class FakeTrace : IAutoTrace
{
    public List<TraceRecord> Records { get; } = [];

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<TraceRecord> Stop()
    {
        Stopped = true;
        return Records;
    }

    public void Dispose() => Disposed = true;
}

/// 휴리스틱의 ETW 대체 공급자 가짜: 시험이 읽기량과 이미지 경로를 채웁니다.
internal sealed class FakeReadWatch : IReadWatch
{
    public Dictionary<int, long> Reads { get; } = [];

    public Dictionary<int, string> Images { get; } = [];

    public bool Disposed { get; private set; }

    public long ReadBytes(int pid) => Reads.GetValueOrDefault(pid);

    public string? ImagePath(int pid) => Images.GetValueOrDefault(pid);

    public void Dispose() => Disposed = true;
}

/// 자동 모드가 닿는 바깥 세계의 가짜 (시계, 런처 목록, 프로세스, 디스크·CPU·전원, 트레이스 세션). 계획·이동은 시험이 필요한 만큼만 바꿔 끼웁니다.
internal sealed class FakeWorld
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public List<DiscoveredGame> Found { get; } = [];

    /// 발견이 런처형이라 등록하지 않았다고 알리는 폴더 (비어 있으면 평범한 목록).
    public List<SkippedFolder> Skipped { get; } = [];

    public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool?> Hdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<int> Pids { get; set; } = [];

    public Dictionary<int, string> Images { get; } = [];

    public Dictionary<int, long> Reads { get; } = [];

    /// ReadBytes (IoCounters) 를 연 PID 기록: 열 수 없는 PID 는 Reads 에 없음 (ACCESS_DENIED 와 같음).
    public List<int> ReadOpens { get; } = [];

    public Dictionary<string, string?> AntiCheats { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long PhysicalRam { get; set; } = 16L << 30;

    public FakeReadWatch? Watch { get; set; }

    public int WatchStarts { get; private set; }

    public string WindowsDir { get; set; } = @"C:\Windows";

    public string SystemDrive { get; set; } = "C:";

    public string ServiceDir { get; set; } = @"C:\Program Files\AsterCache";

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// 파일의 마지막 수정 시각: 원시 트레이스가 마지막 예열보다 새로운지 보는 시험이 채웁니다.
    public Dictionary<string, DateTimeOffset> FileTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(int Pid, long ReadBytes)> Procs { get; set; } = [];

    public bool Running { get; set; }

    /// 이 exe 이름이 도는 중인 것으로 침 (Running 은 이름과 상관없이 모두).
    public HashSet<string> RunningExes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// 폴더가 있는가 (거짓이면 드라이브가 분리된 것과 같음).
    public Func<string, bool> DirectoryExists { get; set; } = _ => true;

    public FakeTrace? Session { get; set; }

    public Exception? StartTraceThrows { get; set; }

    public int TraceStarts { get; private set; }

    /// 마지막 StartTrace 에 넘어온 선행 읽기 알림 (학습이 꺼졌으면 null).
    public LiveListener? Listener { get; private set; }

    public double? Disk { get; set; } = 0;

    public double? Cpu { get; set; } = 1;

    public bool Power { get; set; } = true;

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> MakePlan { get; set; } = (_, _, _) => throw new InvalidOperationException("MakePlan");

    public List<long> PlanZones { get; } = [];

    /// 존 시작을 지정한 계획 (업데이트 뒤 재계획의 이전 존 시작 시도). 시험이 필요하면 바꿔 끼움.
    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, long, PlanFile> MakePlanAt { get; set; } = (_, _, _, _) => throw new InvalidOperationException("MakePlanAt");

    public List<long> PlanStarts { get; } = [];

    public Func<GameSettings, string?> ArchiveJournal { get; set; } = _ => null;

    /// MoveAside 로 옮긴 (원본, 새 이름) 기록. 파일 집합 (Files) 도 따라 바뀝니다.
    public List<(string From, string To)> Moved { get; } = [];

    /// true 면 MoveAside 가 (파일이 잠긴 것처럼) IOException 으로 실패합니다.
    public bool MoveFails { get; set; }

    public Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> Apply { get; set; } = (_, _, _, _) => throw new InvalidOperationException("Apply");

    public Func<string, Fingerprint> Stat { get; set; } = PlanFile.FingerprintOf;

    public Func<GameSettings, int> JournalRows { get; set; } = _ => 0;

    public Func<GameSettings, int, CancellationToken, RevertReport?> Revert { get; set; } = (_, _, _) => throw new InvalidOperationException("Revert");

    public Func<string, IReadOnlyList<Extent>?> Extents { get; set; } = _ => throw new InvalidOperationException("Extents");

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>> LoadSessions { get; set; } = () => [[new TraceEvent(@"D:\g\a.bin", 0, 65536)]];

    public AutoEnv Env() => new()
    {
        Now = () => Now,
        Scan = () => Skipped.Count == 0 ? Found : new ScanResult([.. Found], [.. Skipped]),
        VolumeLabel = l => Labels.GetValueOrDefault(l, "GAMES"),
        IsHdd = l => Hdd.TryGetValue(l, out bool? v) ? v : true,
        Pids = () => Pids,
        ImagePath = p => Images.GetValueOrDefault(p),
        ReadBytes = p =>
        {
            ReadOpens.Add(p);
            return Reads.TryGetValue(p, out long v) ? v : null;
        },
        StartReadWatch = () =>
        {
            WatchStarts++;
            return Watch ?? throw new InvalidOperationException("Watch");
        },
        AntiCheatOf = d => AntiCheats.GetValueOrDefault(d),
        PhysicalRamBytes = () => PhysicalRam,
        WindowsDir = WindowsDir,
        SystemDrive = SystemDrive,
        ServiceDir = ServiceDir,
        FileExists = p => Files.Contains(p) || File.Exists(p),
        FileWriteTime = p => FileTimes.TryGetValue(p, out DateTimeOffset t) ? t : null,
        GameProcesses = _ => Procs,
        AnyRunning = exes => Running || exes.Any(RunningExes.Contains),
        DirectoryExists = p => DirectoryExists(p),
        StartTrace = (_, listener) =>
        {
            TraceStarts++;
            Listener = listener;
            return StartTraceThrows is null ? Session ?? throw new InvalidOperationException("Session") : throw StartTraceThrows;
        },
        Canonicalize = e => e.ToList(),
        LoadTrace = (_, _) => LoadSessions()[0],
        MakePlan = (sessions, origin, zoneMib) =>
        {
            PlanZones.Add(zoneMib);
            return MakePlan(sessions, origin, zoneMib);
        },
        MakePlanAt = (sessions, origin, zoneMib, start) =>
        {
            PlanStarts.Add(start);
            return MakePlanAt(sessions, origin, zoneMib, start);
        },
        SavePlan = (plan, sessions, path) => PlanOutput.Save(plan, sessions, path),
        LoadPlan = PlanFile.Load,
        RealPath = Path.GetFullPath,
        Apply = (g, plan, options, ct) => Apply(g, plan, options, ct),
        Stat = p => Stat(p),
        JournalRows = g => JournalRows(g),
        ArchiveJournal = g => ArchiveJournal(g),
        MoveAside = (src, dst) =>
        {
            if (MoveFails)
            {
                throw new IOException("locked");
            }

            Moved.Add((src, dst));
            Files.Remove(src);
            Files.Add(dst);
            if (File.Exists(src))
            {
                File.Move(src, dst, true);
            }
        },
        Revert = (g, skipRows, ct) => Revert(g, skipRows, ct),
        Extents = p => Extents(p),
        DiskBytesPerSecond = _ => Disk,
        CpuPercent = () => Cpu,
        PowerOk = () => Power,
    };
}

/// 임시 데이터 폴더 + 실제 ConfigEditor + 가짜 세계: 등록·상태 시험의 바탕.
internal sealed class AutoRig : IDisposable
{
    public AutoRig()
    {
        Dir = Path.Combine(Path.GetTempPath(), "aster_autorig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Editor = new ConfigEditor(Dir);
        Status = new AutoStatus(Path.Combine(Dir, "auto_status.json"), Log, () => World.Now);
        Settings = new AsterSettings(50, []);
    }

    public string Dir { get; }

    public ConfigEditor Editor { get; }

    public FakeWorld World { get; } = new();

    public ListLog Log { get; } = new();

    public AutoStatus Status { get; }

    /// 서비스가 지금 들고 있는 설정 (Reload 로 디스크의 config.json 과 맞춤).
    public AsterSettings Settings { get; set; }

    public AsterSettings Reload() => Settings = Editor.Load() ?? new AsterSettings(50, []);

    public AutoRegistry Registry() => new(Editor, World.Env(), Status, Log, () => Settings);

    public GameInput Input(string name, string exe, string root, string? source = null, IReadOnlyList<string>? triggers = null) => new(
        name, [exe], Path.Combine(Dir, "plans", name + ".plan.json.prefetch.json"), Path.Combine(Dir, "plans", name + ".plan.json"), root, [root], "GAMES", null, triggers)
    {
        Source = source,
    };

    public void Add(string name, string exe, string root, string? source = null, IReadOnlyList<string>? triggers = null)
    {
        Editor.Add(Input(name, exe, root, source, triggers));
        Reload();
    }

    public void Dispose() => Directory.Delete(Dir, true);
}
