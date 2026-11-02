using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 자동 트레이스가 쓰는 ETW 세션 하나. 시험은 가짜로 바꿉니다.
public interface IAutoTrace : IDisposable
{
    /// 세션을 멈추고 모은 기록을 돌려줍니다.
    IReadOnlyList<TraceRecord> Stop();
}

/// 자동 모드가 바깥 세계에 닿는 곳 전부 (프로세스·디스크·CPU·전원, 런처 목록, 트레이스 세션, 계획·재배치). 시험은 가짜로 바꿉니다.
public sealed class AutoEnv
{
    /// 수동 수집 (aster trace --live 의 기본 세션 이름) 과 겹치지 않는 이름.
    public const string TraceSessionName = "AsterCacheAutoTrace";
    private const int MinBatteryPercent = 50; // 배터리가 이만큼 남았을 때만 큰 디스크 쓰기를 시작함

    public required Func<DateTimeOffset> Now { get; init; }

    public required Func<IReadOnlyList<DiscoveredGame>> Scan { get; init; }

    /// 'X:' 의 볼륨 레이블 (읽지 못하면 "").
    public required Func<string, string> VolumeLabel { get; init; }

    /// 'X:' 가 HDD 인가 (true), SSD (false), 모르면 null.
    public required Func<string, bool?> IsHdd { get; init; }

    public required Func<IReadOnlyList<int>> Pids { get; init; }

    public required Func<int, string?> ImagePath { get; init; }

    /// 프로세스가 읽은 누적 바이트, 열 수 없으면 null.
    public required Func<int, long?> ReadBytes { get; init; }

    /// 읽기량을 프로세스에서 얻지 못할 때 쓰는 ETW 세션 (켜면 세션이 시작되고 Dispose 하면 꺼짐).
    public required Func<IReadWatch> StartReadWatch { get; init; }

    /// 폴더 이름들로 짐작한 안티치트 ("eac" 등), 모르면 null.
    public required Func<string, string?> AntiCheatOf { get; init; }

    /// 물리 RAM 전체 (바이트).
    public required Func<long> PhysicalRamBytes { get; init; }

    public required string WindowsDir { get; init; }

    /// 시스템(Windows) 볼륨의 드라이브 ("C:"). 자동 모드는 이 볼륨의 게임을 등록·트레이스·재배치하지 않습니다.
    public required string SystemDrive { get; init; }

    public required string ServiceDir { get; init; }

    public required Func<string, bool> FileExists { get; init; }

    /// 이름이 맞는 실행 중인 프로세스의 (PID, 읽은 누적 바이트).
    public required Func<IReadOnlyCollection<string>, IReadOnlyList<(int Pid, long ReadBytes)>> GameProcesses { get; init; }

    public required Func<IReadOnlyCollection<string>, bool> AnyRunning { get; init; }

    public required Func<IReadOnlyCollection<string>, IAutoTrace> StartTrace { get; init; }

    public required Func<IEnumerable<TraceEvent>, List<TraceEvent>> Canonicalize { get; init; }

    public required Func<string, string, IReadOnlyList<TraceEvent>> LoadTrace { get; init; }

    /// (세션, 원점 루트, 존 MiB) → 계획 (존 시작 미정).
    public required Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> MakePlan { get; init; }

    public required Action<PlanFile, IReadOnlyList<IReadOnlyList<TraceEvent>>, string> SavePlan { get; init; }

    public required Func<string, PlanFile> LoadPlan { get; init; }

    public required Func<string, string> RealPath { get; init; }

    /// 파일의 현재 [크기, 수정 시각] (계획의 지문과 같은 기준).
    public required Func<string, Fingerprint> Stat { get; init; }

    public required Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> Apply { get; init; }

    /// 게임 저널의 현재 줄 수 (저널이 없으면 0). 자동 적용이 시작 전의 줄 수를 기억해 두었다가 실패하면 그 뒤 줄만 되돌립니다.
    public required Func<GameSettings, int> JournalRows { get; init; }

    /// (게임, 건너뛸 앞쪽 저널 줄 수, 취소) → 저널의 그 뒤 줄만 되돌린 결과, 저널이 없으면 null.
    public required Func<GameSettings, int, CancellationToken, RevertReport?> Revert { get; init; }

    public required Func<string, IReadOnlyList<Extent>?> Extents { get; init; }

    /// 'X:' 디스크 처리량 (바이트/초), 읽지 못하면 null.
    public required Func<string, double?> DiskBytesPerSecond { get; init; }

    /// 직전 호출 뒤의 전체 CPU 사용률 (%), 처음이거나 읽지 못하면 null.
    public required Func<double?> CpuPercent { get; init; }

    /// 전원이 AC 이거나 배터리가 충분한가.
    public required Func<bool> PowerOk { get; init; }

    /// 자동 모드가 이 설치 루트를 건드리면 안 되는 이유, 건드려도 되면 (HDD 로 확인된 시스템 볼륨 밖) null.
    public string? DriveRefusal(string originRoot)
    {
        string letter = PlanFile.SplitDrive(originRoot).ToUpperInvariant();
        return string.Equals(letter, SystemDrive, StringComparison.OrdinalIgnoreCase) ? "system volume"
            : IsHdd(letter) switch
            {
                true => null,
                false => "not an HDD",
                null => "drive type unknown",
            };
    }

    public static AutoEnv Create(string journalDir)
    {
        var cpu = new CpuLoad();
        return new AutoEnv
        {
            Now = () => DateTimeOffset.UtcNow,
            Scan = () => LauncherScan.Scan(),
            VolumeLabel = VolumeInfo.Label,
            IsHdd = letter => DiskInfo.Query(letter).IncursSeekPenalty,
            Pids = ProcessSnapshot.Pids,
            ImagePath = ProcessSnapshot.ImagePath,
            ReadBytes = ProcessSnapshot.ReadBytes,
            StartReadWatch = () => EtwReadWatch.Start(),
            AntiCheatOf = DetectAntiCheat,
            PhysicalRamBytes = SystemMemory.TotalBytes,
            WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            SystemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant(),
            ServiceDir = AppContext.BaseDirectory,
            FileExists = File.Exists,
            GameProcesses = exes => ProcessSnapshot.ReadCounters(exes),
            AnyRunning = exes => ProcessSnapshot.IsRunning(exes),
            StartTrace = exes => new LiveAutoTrace(LiveSession.Start(exes, TraceSessionName, restartExisting: true)),
            Canonicalize = events => TraceBuilder.CanonicalizeCase(events),
            LoadTrace = (raw, origin) => TraceLoader.LoadRanges(raw, origin, RealPaths.Resolve),
            MakePlan = (sessions, origin, zoneMib) =>
            {
                int cb = FileLayout.ClusterBytes(origin) is > 0 and int c ? c : throw new PlanRejectedException("cluster size unknown");
                return PlanFile.Make(sessions, origin, zoneMib << 20, cb, FileLayout.Extents);
            },
            SavePlan = (plan, sessions, path) => PlanOutput.Save(plan, sessions, path),
            LoadPlan = PlanFile.Load,
            RealPath = RealPaths.Real,
            Stat = PlanFile.FingerprintOf,
            Apply = (g, plan, options, ct) => ReapplyEnv.Run(journalDir, g, plan, options, ct),
            JournalRows = g => ReapplyEnv.JournalRows(journalDir, g),
            Revert = (g, skipRows, ct) => ReapplyEnv.Revert(journalDir, g, skipRows, ct),
            Extents = FileLayout.Extents,
            DiskBytesPerSecond = letter => DiskActivity.BytesPerSecond(letter),
            CpuPercent = cpu.Percent,
            PowerOk = () => PowerProbe.PowerOk(MinBatteryPercent),
        };
    }

    // 발견 때와 같은 이름 열거 (LauncherScan 과 같은 깊이, 읽기 전용).
    private static string? DetectAntiCheat(string dir)
    {
        try
        {
            return AntiCheat.Detect(LauncherScan.ListNames(dir, LauncherScan.AntiCheatDepth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class LiveAutoTrace(LiveSession session) : IAutoTrace
    {
        public IReadOnlyList<TraceRecord> Stop() => session.Stop();

        public void Dispose() => session.Dispose();
    }
}
