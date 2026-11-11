using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Core.Volume;
using AsterCache.Service;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Tests.Service;

/// Stopwatch 틱을 가짜 시계에서 따옴: 시계를 건너뛰면 틱도 같이 가서, 시계만 튀는 시험이 아니면 벽시계 판정과 같아집니다.
internal static class FakeClock
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Func<long> Ticks(Func<DateTimeOffset> now) => () => (long)((decimal)(now() - Epoch).Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond);
}

internal sealed class FakeTrace : IAutoTrace
{
    public List<TraceRecord> Records { get; } = [];

    public Exception? StopFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<TraceRecord> Stop()
    {
        Stopped = true;
        if (StopFailure is { } failure)
        {
            throw failure;
        }

        return Records;
    }

    public void Dispose()
    {
        if (DisposeFailure is { } failure)
        {
            throw failure;
        }

        Disposed = true;
    }
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

    /// SMR 로 보이는 HDD 인가 (기본: 아님).
    public Func<string, bool> IsSmr { get; set; } = _ => false;

    public List<int> Pids { get; set; } = [];

    public Dictionary<int, string> Images { get; } = [];

    public Dictionary<int, long> Reads { get; } = [];

    /// ReadBytes (IoCounters) 를 연 PID 기록: 열 수 없는 PID 는 Reads 에 없음 (ACCESS_DENIED 와 같음).
    public List<int> ReadOpens { get; } = [];

    /// 건너뜀 사유 로그에 남는 막고 있는 프로세스 설명 (기본: 없음, 실제 스냅샷을 뜨지 않음).
    public Func<GameSettings, string?> DescribeBlockers { get; set; } = _ => null;

    public Dictionary<string, string?> AntiCheats { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long PhysicalRam { get; set; } = 16L << 30;

    public string WindowsDir { get; set; } = @"C:\Windows";

    public string SystemDrive { get; set; } = "C:";

    public string ServiceDir { get; set; } = @"C:\Program Files\AsterCache";

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// 파일의 마지막 수정 시각: 원시 트레이스가 마지막 예열보다 새로운지 보는 시험이 채웁니다.
    public Dictionary<string, DateTimeOffset> FileTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// run 파일의 읽기량 (기본: 모름 = 유효 회차로 셈).
    public Func<string, long?> RunReadBytes { get; set; } = _ => null;

    /// 원시 트레이스의 병합 결과 지문 (기본: 읽을 수 없음).
    public Func<string, string?> TraceFingerprint { get; set; } = _ => null;

    public List<(int Pid, long ReadBytes)> Procs { get; set; } = [];

    public bool Running { get; set; }

    /// 이 exe 이름이 도는 중인 것으로 침 (Running 은 이름과 상관없이 모두).
    public HashSet<string> RunningExes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// AnyRunning 을 부를 때마다 (넘긴 이름 목록과 함께) 불리는 관찰용 후크.
    public Action<IReadOnlyCollection<string>>? OnAnyRunning { get; set; }

    /// 폴더가 있는가 (거짓이면 드라이브가 분리된 것과 같음).
    public Func<string, bool> DirectoryExists { get; set; } = _ => true;

    /// 휴리스틱 후보의 설치 폴더가 게임처럼 보이는가 (기본: 항상 참).
    public Func<string, bool> LooksLikeGame { get; set; } = _ => true;

    /// 설치 폴더 → 그 아래 exe 와 크기 (제거 목록 exe 재평가, exe 지문).
    public Dictionary<string, List<(string Path, long Size)>> ExeFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeTrace? Session { get; set; }

    public Func<int, FakeTrace>? StartTraceFactory { get; set; }

    public Exception? StartTraceThrows { get; set; }

    public int TraceStarts { get; private set; }

    /// 정규화 (경로 해석·stat) 를 부른 횟수.
    public int CanonicalizeCalls { get; private set; }

    /// 트레이스 정규화 도중 불리는 관찰용 후크 (경쟁 시험용).
    public Action? OnCanonicalize { get; set; }

    /// 마지막 StartTrace 에 넘어온 선행 읽기 알림 (학습이 꺼졌으면 null).
    public LiveListener? Listener { get; private set; }

    public double? Disk { get; set; } = 0;

    public List<TimeSpan> WaitDurations { get; } = [];

    public Func<TimeSpan, CancellationToken, bool>? WaitOverride { get; set; }

    private bool Wait(TimeSpan duration, CancellationToken token)
    {
        WaitDurations.Add(duration);
        if (WaitOverride is not null)
        {
            return WaitOverride(duration, token);
        }

        if (token.IsCancellationRequested)
        {
            return false;
        }

        Now += duration;
        return true;
    }

    /// 디스크 처리량을 읽을 때마다 부르는 훅 (유휴 판정과 재배치 시작 사이에 일이 끼어드는 경쟁을 만들 때).
    public Action? OnDiskSample { get; set; }

    /// 회복 표본: (드라이브, LCN) → MB/s, 못 재면 null (기본).
    public Func<string, long, ProbeRound, double?> ProbeMbps { get; set; } = (_, _, _) => null;

    public Func<string, long> ProbeStepLcn { get; set; } = _ => 0;

    /// 회복 기준 구간 찾기: (드라이브, 존 시작 LCN) → (존 할당 여부, 기준 LCN), 모르면 null (기본).
    public Func<string, long, (bool ZoneAllocated, long? ReferenceLcn)?> LocateProbeSpots { get; set; } = (_, _) => null;

    public double? Cpu { get; set; } = 1;

    public bool Power { get; set; } = true;

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> MakePlan { get; set; } = (_, _, _) => throw new InvalidOperationException("MakePlan");

    public List<long> PlanZones { get; } = [];

    /// 존 시작을 지정한 계획 (업데이트 뒤 재계획의 이전 존 시작 시도). 시험이 필요하면 바꿔 끼움.
    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, long, long, PlanFile> MakePlanAt { get; set; } = (_, _, _, _, _) => throw new InvalidOperationException("MakePlanAt");

    public List<long> PlanStarts { get; } = [];

    /// 프리페치 기록 부트스트랩 (S2) 의 가짜: 시험이 boot 트레이스를 만든 것처럼 Files 를 바꾸고 만든 범위 수를 돌려줍니다 (기본 0 = 기록 없음).
    public Func<GameSettings, int> PrefetchBoot { get; set; } = _ => 0;

    /// PrefetchBoot 를 부른 게임 이름들.
    public List<string> PrefetchCalls { get; } = [];

    /// boot 트레이스 파일의 "source" 표시 (프리페치 기록으로 만든 것이면 "prefetch").
    public Dictionary<string, string> TraceSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// 트레이스에서 알게 된 가변 파일 표시 (경로 → 사유).
    public Dictionary<string, string> Volatile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<GameSettings, string?> ArchiveJournal { get; set; } = _ => null;

    /// 게임 단위 잠금 (기본: 항상 잡힘). 못 잡는 상황이나 잠금을 기다리는 동안 계획이 바뀌는 상황은 시험이 바꿔 끼움.
    public Func<GameSettings, IDisposable?> Lock { get; set; } = _ => new FakeGate();

    private readonly ThreadLocal<int> _held = new();

    /// 운영 DataLock.RequireHeld 와 같은 규칙: 이 스레드에서 Lock 이 돌려준 게이트가 열려 있을 때만 쓰기가 허용됩니다.
    private void RequireHeld(string what)
    {
        if (_held.Value <= 0)
        {
            throw new InvalidOperationException($"zone lock is not held ({what})");
        }
    }

    /// 시험 준비 코드가 잠금 없이 env 의 쓰기를 직접 부를 때만 씁니다 (운영 코드 경로에는 쓰지 않음).
    public IDisposable AllowUnlockedWrites()
    {
        _held.Value++;
        return new FakeGate(() => _held.Value--);
    }

    /// MoveAside 로 옮긴 (원본, 새 이름) 기록. 파일 집합 (Files) 도 따라 바뀝니다.
    public List<(string From, string To)> Moved { get; } = [];

    /// 트레이스 파일에 나오는 경로 (기본: LoadSessions 의 경로). 시험이 읽기 실패를 만들 때 바꿈.
    public Func<string, IReadOnlyCollection<string>>? TracePaths { get; set; }

    /// 트레이스를 읽는 가짜의 대체 (기본: LoadSessions 의 첫 세션 그대로). 시험이 진짜 읽기처럼 없는 파일 행 제외·길이 0 행 예외를 흉내 낼 때 바꿈.
    public Func<IReadOnlyList<TraceEvent>>? LoadTraceOverride { get; set; }

    /// 업데이트로 바뀐 파일의 범위를 트레이스·예열 목록에서 빼는 가짜 (기본: 아무 일도 하지 않음). 불린 인자는 시험이 기록하고, 필요하면 Sessions 와 파일 시각을 바꿈.
    public Action<GameSettings, IReadOnlySet<string>> DropChanged { get; set; } = (_, _) => { };

    /// true 면 MoveAside 가 (파일이 잠긴 것처럼) IOException 으로 실패합니다.
    public bool MoveFails { get; set; }

    public Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> Apply { get; set; } = (_, _, _, _) => throw new InvalidOperationException("Apply");

    public Func<string, Fingerprint> Stat { get; set; } = PlanFile.FingerprintOf;

    public Func<string, DateTimeOffset?> ChangeTime { get; set; } = _ => DateTimeOffset.MinValue;

    public Func<GameSettings, int> JournalRows { get; set; } = _ => 0;

    /// 같은 볼륨의 다른 게임 계획이 예약한 존 범위 (기본: 없음).
    public Func<GameSettings, IReadOnlyList<(long Lcn, long Clusters)>> Reserved { get; set; } = _ => [];

    /// 잔여 재계획으로 활성 계획 바꾸기 (기본: 연결 안 함 = 기존 흐름).
    public Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<TraceEvent>>?, PlanFile>? ReplanResidual { get; set; }

    public Func<GameSettings, SwitchResult?> ResumeSwitch { get; set; } = _ => null;

    public Func<GameSettings, string?> JournalPlanSha { get; set; } = _ => null;

    public Func<GameSettings, bool> HasPendingVerification { get; set; } = _ => false;

    public Func<GameSettings, int, CancellationToken, RevertReport?> Revert { get; set; } = (_, _, _) => throw new InvalidOperationException("Revert");

    public Func<string, IReadOnlyList<Extent>?> Extents { get; set; } = _ => throw new InvalidOperationException("Extents");

    /// 볼륨 할당 비트맵: 존 확장 계획이 덧붙일 구간이 비었는지 미리 보는 데 씀. 기본은 읽지 못함 (null).
    public Func<string, VolumeBitmap?> Bitmap { get; set; } = _ => null;

    /// 트레이스 금지 게임의 엔진 프로필 구간 (게임, 예산 -> 우선순위 순 이벤트). 기본은 없음.
    public Func<GameSettings, long, IReadOnlyList<TraceEvent>> ProfileEvents { get; set; } = (_, _) => [];

    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>> LoadSessions { get; set; } = () => [[new TraceEvent(@"D:\g\a.bin", 0, 65536)]];

    public Func<string, string, bool> SameDisk { get; set; } = AutoEnv.SameLetter;

    /// 있으면 Now 에서 따온 틱 대신 이 틱을 씀 (시스템 시계가 튀어도 세션 경과가 유지되는지 보는 시험).
    public Func<long>? Ticks { get; set; }

    public AutoEnv Env() => new()
    {
        Now = () => Now,
        Ticks = Ticks ?? FakeClock.Ticks(() => Now),
        Wait = Wait,
        SameDisk = (a, b) => SameDisk(a, b),
        Scan = () => Skipped.Count == 0 ? Found : new ScanResult([.. Found], [.. Skipped]),
        VolumeLabel = l => Labels.GetValueOrDefault(l, "GAMES"),
        IsHdd = l => Hdd.TryGetValue(l, out bool? v) ? v : true,
        IsSmr = l => IsSmr(l),
        Pids = () => Pids,
        ImagePath = p => Images.GetValueOrDefault(p),
        ReadBytes = p =>
        {
            ReadOpens.Add(p);
            return Reads.TryGetValue(p, out long v) ? v : null;
        },
        AntiCheatOf = d => AntiCheats.GetValueOrDefault(d),
        DescribeBlockers = g => DescribeBlockers(g),
        PhysicalRamBytes = () => PhysicalRam,
        WindowsDir = WindowsDir,
        SystemDrive = SystemDrive,
        ServiceDir = ServiceDir,
        FileExists = p => Files.Contains(p) || File.Exists(p),
        FileWriteTime = p => FileTimes.TryGetValue(p, out DateTimeOffset t) ? t : null,
        RunReadBytes = p => RunReadBytes(p),
        TraceFingerprint = p => TraceFingerprint(p),
        GameProcesses = _ => Procs,
        AnyRunning = exes =>
        {
            OnAnyRunning?.Invoke(exes);
            return Running || exes.Any(RunningExes.Contains);
        },
        DirectoryExists = p => DirectoryExists(p),
        LooksLikeGame = p => LooksLikeGame(p),
        ExeFiles = p => ExeFiles.TryGetValue(p, out List<(string Path, long Size)>? list) ? list : [],
        ReadText = p => Texts.GetValueOrDefault(p),
        StartTrace = (_, listener) =>
        {
            TraceStarts++;
            Listener = listener;
            return StartTraceThrows is null ? StartTraceFactory?.Invoke(TraceStarts) ?? Session ?? throw new InvalidOperationException("Session") : throw StartTraceThrows;
        },
        Canonicalize = e =>
        {
            CanonicalizeCalls++;
            OnCanonicalize?.Invoke();
            return e.ToList();
        },
        PrefetchBoot = g =>
        {
            PrefetchCalls.Add(g.Name);
            return PrefetchBoot(g);
        },
        TraceSource = p => TraceSources.GetValueOrDefault(p),
        LoadTrace = (_, _) => LoadTraceOverride is { } load ? load() : LoadSessions()[0],
        ProfileEvents = (g, budget) => ProfileEvents(g, budget),
        LoadVolatile = _ => Volatile,
        MakePlan = (sessions, origin, zoneMib) =>
        {
            PlanZones.Add(zoneMib);
            return MakePlan(sessions, origin, zoneMib);
        },
        MakePlanAt = (sessions, origin, zoneMib, start, used) =>
        {
            PlanStarts.Add(start);
            return MakePlanAt(sessions, origin, zoneMib, start, used);
        },
        SavePlan = (plan, sessions, path) =>
        {
            RequireHeld($"SavePlan {path}");
            PlanOutput.Save(plan, sessions, path);
        },
        LoadPlan = PlanFile.Load,
        RealPath = Path.GetFullPath,
        Apply = (g, plan, options, ct) => Apply(g, plan, options, ct),
        Stat = p => Stat(p),
        ChangeTime = p => ChangeTime(p),
        JournalRows = g => JournalRows(g),
        Lock = g =>
        {
            IDisposable? gate = Lock(g);
            if (gate is null)
            {
                return null;
            }

            _held.Value++;
            return new FakeGate(() =>
            {
                _held.Value--;
                gate.Dispose();
            });
        },
        Reserved = g => Reserved(g),
        ReplanResidual = ReplanResidual,
        ResumeSwitch = g => ResumeSwitch(g),
        JournalPlanSha = g => JournalPlanSha(g),
        HasPendingVerification = g => HasPendingVerification(g),
        ArchiveJournal = g =>
        {
            RequireHeld($"ArchiveJournal {g.Name}");
            return ArchiveJournal(g);
        },
        MoveAside = (src, dst) =>
        {
            RequireHeld($"MoveAside {src}");
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
        TracePaths = p => TracePaths is { } paths ? paths(p) : [.. LoadSessions().SelectMany(s => s).Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase)],
        TraceRows = _ => LoadSessions()[0],
        DropChanged = (g, paths) =>
        {
            RequireHeld($"DropChanged {g.Name}");
            DropChanged(g, paths);
        },
        Touch = p =>
        {
            RequireHeld($"Touch {p}");
            FileTimes[p] = Now;
        },
        Revert = (g, skipRows, ct) => Revert(g, skipRows, ct),
        Extents = p => Extents(p),
        Bitmap = d => Bitmap(d),
        DiskBytesPerSecond = _ =>
        {
            OnDiskSample?.Invoke();
            return Disk;
        },
        ProbeMbps = (drive, lcn, round) => ProbeMbps(drive, lcn, round),
        ProbeStepLcn = drive => ProbeStepLcn(drive),
        LocateProbeSpots = (drive, lcn, _) => LocateProbeSpots(drive, lcn),
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
        Status = new AutoStatus(Path.Combine(Dir, "auto_status.json"), Log, () => World.Now, ticks: FakeClock.Ticks(() => World.Now));
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

    public void Dispose()
    {
        OrbitModel.StopUnder(Dir);
        Directory.Delete(Dir, true);
    }
}
