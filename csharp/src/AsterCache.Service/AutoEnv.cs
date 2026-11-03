using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using AsterCache.Windows;
using Microsoft.Win32;

namespace AsterCache.Service;

/// 자동 트레이스가 쓰는 ETW 세션 하나. 시험은 가짜로 바꿉니다.
public interface IAutoTrace : IDisposable
{
    /// 세션을 멈추고 모은 기록을 돌려줍니다.
    IReadOnlyList<TraceRecord> Stop();
}

/// 드라이브에서 자동 모드가 할 수 있는 일. 시스템 볼륨이 회전 디스크면 읽기 전용 경로 (발견·트레이스·예열) 만 허용하고 재배치·SSD 보조 저장은 거절합니다.
/// 시스템 볼륨이 SSD 이거나 종류를 모르면 전부 거절, 시스템 볼륨 밖은 HDD 로 확인된 때만 전부 허용합니다.
public sealed record DriveCapabilities(bool CanDiscover, bool CanPrewarm, bool CanRelocate, bool CanTier, bool IsSystem, bool IsRotational)
{
    /// 시스템 HDD 게임의 auto_status detail.
    public const string PrewarmOnly = "prewarm only (system drive)";

    /// 재배치를 거절하는 이유, 재배치해도 되면 null.
    public string? Reason { get; init; }

    /// 예열을 거절하는 이유, 예열해도 되면 null.
    public string? PrewarmRefusal => CanPrewarm ? null : Reason;

    public static DriveCapabilities Of(string originRoot, string systemDrive, Func<string, bool?> isHdd)
    {
        string letter = PlanFile.SplitDrive(originRoot).ToUpperInvariant();
        bool system = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase);
        bool? hdd = isHdd(letter);
        bool rotational = hdd == true; // 발견·트레이스·예열은 HDD 로 확인된 드라이브에서만 (시스템 볼륨이어도), 재배치·티어는 시스템 볼륨 밖에서만
        bool all = rotational && !system;
        string? reason = all ? null
            : system ? (rotational ? PrewarmOnly : "system volume")
            : hdd is false ? "not an HDD" : "drive type unknown";
        return new DriveCapabilities(rotational, rotational, all, all, system, rotational) { Reason = reason };
    }
}

/// 자동 모드가 바깥 세계에 닿는 곳 전부 (프로세스·디스크·CPU·전원, 런처 목록, 트레이스 세션, 계획·재배치). 시험은 가짜로 바꿉니다.
public sealed class AutoEnv
{
    /// 수동 수집 (aster trace --live 의 기본 세션 이름) 과 겹치지 않는 이름.
    public const string TraceSessionName = "AsterCacheAutoTrace";
    private const int MinBatteryPercent = 50; // 배터리가 이만큼 남았을 때만 큰 디스크 쓰기를 시작함

    public required Func<DateTimeOffset> Now { get; init; }

    /// Stopwatch 틱: 세션 안 경과 판정 (회복 대기) 이 시스템 시계가 튀어도 흔들리지 않게 씀. 시험은 가짜.
    public Func<long> Ticks { get; init; } = System.Diagnostics.Stopwatch.GetTimestamp;

    public required Func<IReadOnlyList<DiscoveredGame>> Scan { get; init; }

    /// 폴더가 게임처럼 보이는가 (휴리스틱 등록 전 확인: 큰 파일이나 런처 매니페스트 흔적). 시험 기본값은 항상 참.
    public Func<string, bool> LooksLikeGame { get; init; } = _ => true;

    /// 설치 폴더 아래의 exe 와 크기 (제거 목록 exe 재평가, exe 지문). 읽을 수 없으면 빈 목록, 시험 기본값도 빈 목록.
    public Func<string, IReadOnlyList<(string Path, long Size)>> ExeFiles { get; init; } = _ => [];

    /// 'X:' 의 볼륨 레이블 (읽지 못하면 "").
    public required Func<string, string> VolumeLabel { get; init; }

    /// 'X:' 가 HDD 인가 (true), SSD (false), 모르면 null.
    public required Func<string, bool?> IsHdd { get; init; }

    /// 'X:' 의 디스크 종류를 모르거나 USB 버스인가 (자동 재배치 동시 수 규칙). 시험 기본값은 참.
    public Func<string, bool> IsSlowBus { get; init; } = _ => true;

    /// 'X:' 가 SMR 로 보이는 HDD 인가 (TRIM 을 받는 HDD 는 추정일 뿐). 시험 기본값은 거짓.
    public Func<string, bool> IsSmr { get; init; } = _ => false;

    public required Func<IReadOnlyList<int>> Pids { get; init; }

    public required Func<int, string?> ImagePath { get; init; }

    /// 프로세스가 읽은 누적 바이트, 열 수 없으면 null.
    public required Func<int, long?> ReadBytes { get; init; }

    /// 읽기량을 프로세스에서 얻지 못할 때 쓰는 ETW 세션 (켜면 세션이 시작되고 Dispose 하면 꺼짐).
    public required Func<IReadWatch> StartReadWatch { get; init; }

    /// 폴더 이름들로 짐작한 안티치트 ("eac" 등), 모르면 null.
    public required Func<string, string?> AntiCheatOf { get; init; }

    /// 이 게임 폴더 아래에서 도는 프로세스의 PID (게임 exe 가 자식 프로세스를 띄우는 경우를 포함, 이 서비스는 제외). 시험 기본값은 없음.
    public Func<GameSettings, IReadOnlyList<int>> RootPids { get; init; } = _ => [];

    /// 물리 RAM 전체 (바이트).
    public required Func<long> PhysicalRamBytes { get; init; }

    public required string WindowsDir { get; init; }

    /// 시스템(Windows) 볼륨의 드라이브 ("C:"). 자동 모드는 이 볼륨의 게임을 등록·트레이스·재배치하지 않습니다.
    public required string SystemDrive { get; init; }

    public required string ServiceDir { get; init; }

    public required Func<string, bool> FileExists { get; init; }

    /// 폴더가 있는가 (드라이브가 분리되면 거짓). 시험 기본값은 항상 참.
    public Func<string, bool> DirectoryExists { get; init; } = _ => true;

    /// 파일의 마지막 수정 시각 (UTC), 없으면 null. 원시 트레이스가 마지막 예열보다 새로운지 보는 데 씁니다.
    public Func<string, DateTimeOffset?> FileWriteTime { get; init; } = _ => null;

    /// 이름이 맞는 실행 중인 프로세스의 (PID, 읽은 누적 바이트).
    public required Func<IReadOnlyCollection<string>, IReadOnlyList<(int Pid, long ReadBytes)>> GameProcesses { get; init; }

    public required Func<IReadOnlyCollection<string>, bool> AnyRunning { get; init; }

    /// (게임 exe 이름들, 선행 읽기 알림 또는 null) → ETW 세션. 알림이 있으면 읽기·열기를 실시간으로 알리고 세션 버퍼를 짧은 간격으로 내려 보냅니다.
    public required Func<IReadOnlyCollection<string>, LiveListener?, IAutoTrace> StartTrace { get; init; }

    /// 부트스트랩 S2: 프리페치 기록 (.pf) 으로 이 게임의 boot 트레이스를 만듭니다. 만든 범위 수, 못 만들면 0 (이미 있거나 기록 없음). 시험 기본값은 0.
    public Func<GameSettings, int> PrefetchBoot { get; init; } = _ => 0;

    /// boot 트레이스 파일의 "source" 표시 (프리페치 기록으로 만든 것이면 "prefetch"), 없으면 null. 시험 기본값은 null.
    public Func<string, string?> TraceSource { get; init; } = _ => null;

    public required Func<IEnumerable<TraceEvent>, List<TraceEvent>> Canonicalize { get; init; }

    public required Func<string, string, IReadOnlyList<TraceEvent>> LoadTrace { get; init; }

    /// 트레이스에서 알게 된 가변 파일 (경로 → 사유) 표시 파일을 읽음, 없으면 빈 목록. 시험 기본값은 빈 목록.
    public Func<string, IReadOnlyDictionary<string, string>> LoadVolatile { get; init; } = _ => new Dictionary<string, string>();

    /// (세션, 원점 루트, 존 MiB) → 계획 (존 시작 미정).
    public required Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> MakePlan { get; init; }

    /// (세션, 원점 루트, 존 MiB, 존 시작 LCN) → 그 자리에 존을 두고 만든 계획 (제자리·겹침을 계산에 넣음). 없으면 이전 존 시작을 쓰지 않고 MakePlan 만 씁니다.
    public Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, long, PlanFile>? MakePlanAt { get; init; }

    public required Action<PlanFile, IReadOnlyList<IReadOnlyList<TraceEvent>>, string> SavePlan { get; init; }

    public required Func<string, PlanFile> LoadPlan { get; init; }

    public required Func<string, string> RealPath { get; init; }

    /// 파일의 현재 [크기, 수정 시각] (계획의 지문과 같은 기준).
    public required Func<string, Fingerprint> Stat { get; init; }

    public required Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> Apply { get; init; }

    /// 게임 저널의 현재 줄 수 (저널이 없으면 0). 자동 적용이 시작 전의 줄 수를 기억해 두었다가 실패하면 그 뒤 줄만 되돌립니다.
    public required Func<GameSettings, int> JournalRows { get; init; }

    /// 끝난 게임 저널을 옆으로 옮겨 보관하고 그 경로를 돌려줍니다 (저널이 없으면 null). 새 계획은 다른 저널 header 를 쓰기 때문입니다.
    public Func<GameSettings, string?> ArchiveJournal { get; init; } = _ => null;

    /// (원본, 새 이름) 파일 이동, 이미 있으면 덮어씀. 낡은 트레이스·계획을 .stale 로 치우는 데 씁니다.
    public Action<string, string> MoveAside { get; init; } = (_, _) => { };

    /// (게임, 건너뛸 앞쪽 저널 줄 수, 취소) → 저널의 그 뒤 줄만 되돌린 결과, 저널이 없으면 null.
    public required Func<GameSettings, int, CancellationToken, RevertReport?> Revert { get; init; }

    public required Func<string, IReadOnlyList<Extent>?> Extents { get; init; }

    /// 'X:' 디스크 처리량 (바이트/초), 읽지 못하면 null.
    public required Func<string, double?> DiskBytesPerSecond { get; init; }

    /// ('X:', LCN, 회차 세션) → 그 자리의 비버퍼드 순차 읽기 속도 (MB/s, 서로 떨어진 3 구간의 중앙값), 못 재면 null. 읽기량·시간·취소는 세션이 정함 (회차 192 MiB·10 s, 존 + 참조면 구간당 32 MiB). 서비스 전용, 시험은 가짜 (기본: 못 잼).
    public Func<string, long, ProbeRound, double?> ProbeMbps { get; init; } = (_, _, _) => null;

    /// 'X:' → 표본 한 칸 (64 MiB) 의 LCN 수. 회복 표본이 이만큼씩 옮겨 읽음 (0 이면 회전 없음, 시험 기본값).
    public Func<string, long> ProbeStepLcn { get; init; } = _ => 0;

    /// ('X:', 존 시작 LCN) → (그 존 앞쪽이 이미 할당됐는가, 존에서 4 GiB 뒤에 가장 가까운 할당된 기준 구간의 LCN), 비트맵을 못 읽으면 null. 시험 기본값은 null.
    public Func<string, long, CancellationToken, (bool ZoneAllocated, long? ReferenceLcn)?> LocateProbeSpots { get; init; } = (_, _, _) => null;

    /// 두 드라이브 ('X:' 또는 그 아래 경로) 가 같은 물리 디스크인가. 모르면 참 (보수적). 시험 기본값은 드라이브 문자 비교.
    public Func<string, string, bool> SameDisk { get; init; } = SameLetter;

    /// SameDisk 의 시험·기본 구현: 드라이브 문자가 같은가.
    public static bool SameLetter(string a, string b) => string.Equals(PlanFile.SplitDrive(a), PlanFile.SplitDrive(b), StringComparison.OrdinalIgnoreCase);

    /// 'X:' 와 같은 물리 디스크 (SameDisk) 에 설치된 등록 게임이 지금 실행 중인가. 설정을 아는 AutoMode 가 연결하며, 연결 전·시험 기본값은 null (게임 없음으로 봄).
    public Func<string, bool>? GameRunningOnDisk { get; set; }

    /// 직전 호출 뒤의 전체 CPU 사용률 (%), 처음이거나 읽지 못하면 null.
    public required Func<double?> CpuPercent { get; init; }

    /// 전원이 AC 이거나 배터리가 충분한가.
    public required Func<bool> PowerOk { get; init; }

    /// SSD 보조 저장 (선택 기능) 의 엔진. 시험 기본값은 null (건드리지 않음).
    public TierEngine? Tier { get; init; }

    /// 자동 모드가 이 설치 루트에 할 수 있는 일 (HDD 로 확인된 시스템 볼륨 밖은 전부, 회전 디스크인 시스템 볼륨은 발견·예열만).
    public DriveCapabilities Capabilities(string originRoot) => DriveCapabilities.Of(originRoot, SystemDrive, IsHdd);

    /// log 가 있으면 재배치의 이동·검증 읽기에 낮은 I/O 우선순위 힌트를 붙입니다 (자동 모드는 사용자 PC 를 덜 방해해야 함).
    public static AutoEnv Create(string journalDir, ILog? log = null)
    {
        var cpu = new CpuLoad();
        return new AutoEnv
        {
            Now = () => DateTimeOffset.UtcNow,
            Scan = () => LauncherScan.Scan(),
            LooksLikeGame = dir => LauncherScan.LooksLikeGame(dir),
            ExeFiles = dir => LauncherScan.InstalledExes(dir),
            VolumeLabel = VolumeInfo.Label,
            IsHdd = letter => DiskInfo.Query(letter).IncursSeekPenalty,
            SameDisk = VolumeDisks.SameDisk,
            IsSlowBus = letter => DiskInfo.Query(letter) is var d && (d.IncursSeekPenalty is null || d.BusType == DiskInfo.BusTypeUsb),
            IsSmr = letter => DiskInfo.Query(letter) is var d && DiskInfo.Kind(d.IncursSeekPenalty, d.Trim) == "HDD (looks like SMR)",
            Pids = ProcessSnapshot.Pids,
            ImagePath = ProcessSnapshot.ImagePath,
            ReadBytes = ProcessSnapshot.ReadBytes,
            StartReadWatch = () => EtwReadWatch.Start(),
            AntiCheatOf = DetectAntiCheat,
            RootPids = g => [.. ProcessSnapshot.Take().Where(p => p.Pid != Environment.ProcessId && DispatchEnv.UnderRoot(g, ProcessSnapshot.ImagePath(p.Pid))).Select(p => p.Pid)],
            PhysicalRamBytes = SystemMemory.TotalBytes,
            WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            SystemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant(),
            ServiceDir = AppContext.BaseDirectory,
            FileExists = File.Exists,
            DirectoryExists = Directory.Exists,
            FileWriteTime = path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null,
            GameProcesses = exes => ProcessSnapshot.ReadCounters(exes),
            AnyRunning = exes => ProcessSnapshot.IsRunning(exes),
            StartTrace = (exes, listener) => new LiveAutoTrace(LiveSession.Start(
                exes, TraceSessionName, restartExisting: true, listener: listener, flushInterval: listener is null ? null : EtwProcessWatcher.FlushInterval, log: log), log),
            Canonicalize = events => TraceBuilder.CanonicalizeCase(events),
            PrefetchBoot = g => AutoTracer.BootFromPrefetch(g),
            TraceSource = AutoTracer.TraceSourceOf,
            LoadTrace = (raw, origin) => TraceLoader.LoadRanges(raw, origin, RealPaths.Resolve),
            LoadVolatile = AutoTracer.ReadVolatile,
            MakePlan = (sessions, origin, zoneMib) => MakePlanAtLcn(sessions, origin, zoneMib, null),
            MakePlanAt = (sessions, origin, zoneMib, start) => MakePlanAtLcn(sessions, origin, zoneMib, start),
            SavePlan = (plan, sessions, path) => PlanOutput.Save(plan, sessions, path),
            LoadPlan = PlanFile.Load,
            RealPath = RealPaths.Real,
            Stat = PlanFile.FingerprintOf,
            Apply = (g, plan, options, ct) => ReapplyEnv.Run(journalDir, g, plan, options, ct, log),
            JournalRows = g => ReapplyEnv.JournalRows(journalDir, g),
            ArchiveJournal = g => ReapplyEnv.ArchiveJournal(journalDir, g),
            MoveAside = (src, dst) => File.Move(src, dst, true),
            Revert = (g, skipRows, ct) => ReapplyEnv.Revert(journalDir, g, skipRows, ct),
            Extents = FileLayout.Extents,
            DiskBytesPerSecond = letter => DiskActivity.BytesPerSecond(letter),
            ProbeMbps = DiskProbe.Mbps,
            ProbeStepLcn = DiskProbe.StepLcn,
            LocateProbeSpots = (letter, zone, ct) => DiskProbe.Locate(letter, zone, ct, () => DiskActivity.IsBusy(letter)),
            CpuPercent = cpu.Percent,
            PowerOk = () => PowerProbe.PowerOk(MinBatteryPercent),
            Tier = log is null ? null : new TierEngine(new TierFs(new IoPriorityHint(err => log.Warn($"ssd tier: low I/O priority hint not applied (Win32 error {err})"))), log, Path.Combine(Path.GetDirectoryName(journalDir) ?? journalDir, "tier")),
        };
    }

    private static PlanFile MakePlanAtLcn(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, string origin, long zoneMib, long? start)
    {
        int cb = FileLayout.ClusterBytes(origin) is > 0 and int c ? c : throw new PlanRejectedException("cluster size unknown");
        return PlanFile.Make(sessions, origin, zoneMib << 20, cb, FileLayout.Extents, zoneStartLcn: start);
    }

    /// 탐지를 못 한 결과 (I/O 실패): 없음이 아니라 모름. 티어는 거절하고 캐시하지 않으며, 등록에는 저장하지 않습니다.
    public const string AntiCheatUnknown = "unknown";

    /// 등록에 저장해도 되는 탐지 결과 (모름은 null).
    public static string? KnownAntiCheat(string? found) => found == AntiCheatUnknown ? null : found;

    /// 이 시스템에 Vanguard (vgk/vgc 서비스 또는 Riot Vanguard 폴더) 가 있는가.
    internal static bool IsVanguardInstalled()
    {
        foreach (string service in new[] { "vgk", "vgc" })
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
            if (key is not null)
            {
                return true;
            }
        }

        return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Vanguard"));
    }

    // 발견 때와 같은 이름 열거 (LauncherScan 과 같은 깊이, 읽기 전용).
    internal static string? DetectAntiCheat(string dir)
    {
        try
        {
            return AntiCheat.Detect(LauncherScan.ListNames(dir, LauncherScan.AntiCheatDepth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AntiCheatUnknown;
        }
    }

    private sealed class LiveAutoTrace(LiveSession session, ILog? log) : IAutoTrace
    {
        public IReadOnlyList<TraceRecord> Stop()
        {
            List<TraceRecord> records = session.Stop();
            log?.Info($"trace session stopped: {session.EventsLost} events lost");
            return records;
        }

        public void Dispose() => session.Dispose();
    }
}
