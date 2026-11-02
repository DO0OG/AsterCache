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

    public List<(int Pid, long ReadBytes)> Procs { get; set; } = [];

    public bool Running { get; set; }

    public FakeTrace? Session { get; set; }

    public Exception? StartTraceThrows { get; set; }

    public int TraceStarts { get; private set; }

    public double? Disk { get; set; } = 0;

    public double? Cpu { get; set; } = 1;

    public bool Power { get; set; } = true;

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> MakePlan { get; set; } = (_, _, _) => throw new InvalidOperationException("MakePlan");

    public List<long> PlanZones { get; } = [];

    public Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> Apply { get; set; } = (_, _, _, _) => throw new InvalidOperationException("Apply");

    public Func<string, Fingerprint> Stat { get; set; } = PlanFile.FingerprintOf;

    public Func<GameSettings, int> JournalRows { get; set; } = _ => 0;

    public Func<GameSettings, int, CancellationToken, RevertReport?> Revert { get; set; } = (_, _, _) => throw new InvalidOperationException("Revert");

    public Func<string, IReadOnlyList<Extent>?> Extents { get; set; } = _ => throw new InvalidOperationException("Extents");

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>> LoadSessions { get; set; } = () => [[new TraceEvent(@"D:\g\a.bin", 0, 65536)]];

    public AutoEnv Env() => new()
    {
        Now = () => Now,
        Scan = () => Found,
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
        GameProcesses = _ => Procs,
        AnyRunning = _ => Running,
        StartTrace = _ =>
        {
            TraceStarts++;
            return StartTraceThrows is null ? Session ?? throw new InvalidOperationException("Session") : throw StartTraceThrows;
        },
        Canonicalize = e => e.ToList(),
        LoadTrace = (_, _) => LoadSessions()[0],
        MakePlan = (sessions, origin, zoneMib) =>
        {
            PlanZones.Add(zoneMib);
            return MakePlan(sessions, origin, zoneMib);
        },
        SavePlan = (plan, sessions, path) => PlanOutput.Save(plan, sessions, path),
        LoadPlan = PlanFile.Load,
        RealPath = Path.GetFullPath,
        Apply = (g, plan, options, ct) => Apply(g, plan, options, ct),
        Stat = p => Stat(p),
        JournalRows = g => JournalRows(g),
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
