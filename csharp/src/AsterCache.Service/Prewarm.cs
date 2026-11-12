using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 판단 (순수 함수): 건너뛸 이유, 없으면 null. 순서: 계획·추적 파일 → 쿨다운 → 게임 실행 중 → 가용 RAM (존 크기 + 여유).
public static class PrewarmPolicy
{
    public static string? SkipReason(long? zoneBytes, bool traceExists, bool gameRunning, long availableRamMib, int minFreeRamMib, TimeSpan? sinceLast, TimeSpan cooldown)
    {
        if (zoneBytes is null)
        {
            return "no plan file";
        }

        if (zoneBytes == 0)
        {
            return "no zone";
        }

        if (!traceExists)
        {
            return "no trace file";
        }

        if (sinceLast is { } since && since < cooldown)
        {
            return $"cooldown ({Math.Ceiling((cooldown - since).TotalMinutes)} min left)";
        }

        if (gameRunning)
        {
            return "game is running";
        }

        long zoneMib = (zoneBytes.Value + (1 << 20) - 1) >> 20;
        return !HasMemoryBudget(zoneMib, availableRamMib, minFreeRamMib)
            ? $"low memory (available {availableRamMib} MiB < zone {zoneMib} MiB + reserve {minFreeRamMib} MiB)"
            : null;
    }

    internal static bool HasMemoryBudget(long zoneMib, long availableRamMib, int minFreeRamMib) =>
        availableRamMib >= zoneMib + minFreeRamMib;
}

/// 예열이 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class PrewarmEnv
{
    public required Func<long> AvailableRamMib { get; init; }

    public required Func<GameSettings, bool> GameRunning { get; init; }

    /// 프로세스 스냅샷을 한 번 찍어 그 스냅샷으로 게임 실행을 판정하는 함수를 만듭니다 (묶음 경계 점검이 같은 디스크의 여러 게임을 봐도 스냅샷은 한 번). null 이면 GameRunning 을 그대로 부릅니다.
    public Func<Func<GameSettings, bool>>? GameRunningSnapshot { get; init; }

    /// 게임 실행 판정의 근거가 된 프로세스 설명 ("이름(pid) 경로", 없으면 null): 건너뛴 사유를 로그에 남기는 데만 씁니다. 시험 기본값은 null (실제 스냅샷을 부르지 않음).
    public Func<GameSettings, string?> DescribeBlockers { get; init; } = _ => null;

    /// 이 이름의 프로세스가 지금 떠 있는가 (트리거 프로세스 생존 확인용). 시험 기본값은 꺼짐 (실제 스냅샷을 부르지 않음).
    public Func<string, bool> ProcessRunning { get; init; } = _ => false;

    /// 두 설치 루트가 같은 물리 디스크인가 (모르면 참). 시험 기본값은 드라이브 문자 비교.
    public Func<string, string, bool> SameDisk { get; init; } = AutoEnv.SameLetter;

    /// 이번에 읽을 바이트 (계획의 존 크기를 max_stream_mib 로 자른 값), 계획 파일이 없으면 null, 존이 비어 있으면 0.
    public required Func<GameSettings, long?> ZoneBytes { get; init; }

    /// 예열할 집합 가운데 실제 게임 트레이스가 관찰한 범위의 바이트. null 이면 전체 집합을 관찰된 것으로 취급합니다.
    public Func<GameSettings, long?> ObservedBytes { get; init; } = _ => null;

    /// 게임이 실행 중일 때는 이미 캐시에 있는 출처 정보만 사용합니다 (원시 트레이스 파일을 새로 읽지 않음).
    public Func<GameSettings, long?> ObservedBytesIfCached { get; init; } = _ => null;

    public required Func<GameSettings, bool> TraceExists { get; init; }

    /// 계획을 만들 때의 유효 존 상한 (바이트), 모르면 null. 틈 메우기 예산의 상한이며 현재 존 크기와 무관합니다.
    public Func<GameSettings, long?> EffectiveZoneBytes { get; init; } = _ => null;

    /// 물리 RAM 전체 (바이트): 상주 고정은 16 GiB 이상에서만. 기본 0 (고정 안 함).
    public Func<long> PhysicalRamBytes { get; init; } = () => 0;

    /// 고정할 파일 경로 (예열 순서 목록의 파일 중 허용 루트 안인 것).
    public Func<GameSettings, IReadOnlyList<string>> PinPaths { get; init; } = _ => [];

    /// 게임 폴더 이름으로 짐작한 안티치트 ("unknown" 은 탐지 실패): 안티치트 게임은 고정하지 않음. 기본은 없음.
    public Func<GameSettings, string?> AntiCheatOf { get; init; } = _ => null;

    /// 런처 (LauncherExes) 가 하나라도 실행 중인가: 그동안은 고정하지 않음.
    public Func<bool> LauncherRunning { get; init; } = () => false;

    /// 직전 호출 뒤의 전체 CPU 사용률 (%), 처음이거나 읽지 못하면 null. 예열 유지의 유휴 판정용 (기본은 한가함).
    public Func<double?> CpuPercent { get; init; } = () => 0;

    /// 게임이 놓인 드라이브의 처리량 (바이트/초), 읽지 못하면 null. 예열 유지의 유휴 판정용 (기본은 한가함).
    public Func<GameSettings, double?> DiskBytesPerSecond { get; init; } = _ => 0;

    /// 처리량과 표본 상태를 한 번에 읽음 (null 이 고장인지 아직 모름인지 구분). 없으면 DiskBytesPerSecond 로 대신 (null = 고장).
    public Func<GameSettings, (double? Bps, DiskActivity.SampleState State)>? DiskSample { get; init; }

    /// 게임 보호 판정 (외부 디스크 부하) 용 DiskSample: 수집 정체가 60 초를 넘어도 Broken 으로 넘기지 않음. 없으면 DiskSample 을 씀.
    public Func<GameSettings, (double? Bps, DiskActivity.SampleState State)>? GameDiskSample { get; init; }

    /// 게임이 시작 뒤 읽은 누적 바이트 (등록 exe 이름 프로세스들의 합, 이름·읽기 카운터만 봄), 하나라도 읽지 못하거나 프로세스가 없으면 null. 같은 디스크의 다른 게임이 한가한지 판정하는 데만 씀 (null 은 바쁨). 시험 기본값은 null.
    public Func<GameSettings, long?> GameReadBytes { get; init; } = _ => null;

    /// 파일의 크기·수정 시각 (회복 기간 고정이 고정 중 파일 변경을 알아채는 데). 읽을 수 없으면 IOException 등. 기본은 항상 같은 값 (시험용), Create 는 진짜 값.
    public Func<string, Fingerprint> FileStamp { get; init; } = p => new Fingerprint(p, 0, 0);

    /// 파일들을 읽기 전용 매핑으로 고정.
    public Func<IReadOnlyList<string>, PinHandle> OpenPin { get; init; } = _ => new PinHandle(new Nothing(), 0, 0);

    /// 핫 집합 몇 곳을 읽어 잰 결과 (CacheProbe), 읽을 수 없으면 null.
    public Func<GameSettings, ProbeResult?> ProbeReadMs { get; init; } = _ => null;

    public Func<GameSettings, Func<bool>, ProbeResult?>? ProbeReadMsChecked { get; init; }

    /// 메모리가 모자라 앞부분만 읽은 게임의 재예열 표본: 부분 예열이 읽은 것과 같은 잘라낸 목록에서만 고름 (읽지 않은 뒤쪽은 항상 차갑기 때문). 없으면 ProbeReadMs 를 씀.
    public Func<GameSettings, ProbeResult?>? ProbePartialReadMs { get; init; }

    public Func<GameSettings, Func<bool>, ProbeResult?>? ProbePartialReadMsChecked { get; init; }

    /// 끊긴 보충 예열을 이어 읽기 전에: 부분 예열 목록 (MaxStreamMib 로 잘린 것) 의 스케줄 앞쪽 bytes 에서 고른 표본을 읽어 잰 결과. 없거나 읽을 수 없으면 null (이어 읽지 않고 처음부터).
    public Func<GameSettings, long, ProbeResult?>? ProbeDone { get; init; }

    public Func<GameSettings, long, Func<bool>, ProbeResult?>? ProbeDoneChecked { get; init; }

    /// 원시 트레이스 (자동 트레이스 결과) 나 boot 트레이스 (가져온 프로필) 가 있는가. 기록 없는 게임의 통째·프로필 예열은 이 파일과 TraceExists 가 모두 없을 때만 합니다.
    public Func<GameSettings, bool> RawTraceExists { get; init; } = _ => false;

    /// 기록 없는 게임의 예열 계획 (게임, 통째 상한 바이트 (0 = 끔), 프로필 예산 바이트 (0 = 끔), 취소). null 이면 이 기능 없음 (기본).
    public Func<GameSettings, long, long, CancellationToken, ColdPlan>? ColdPlanner { get; init; }

    /// 계획을 읽습니다 (묶음 사이에서 취소를 봄): (통계, 끝까지 읽었는가).
    public Func<IReadOnlyList<ReadOp>, CancellationToken, (StreamStats Stats, bool Complete)> ColdRead { get; init; } = (ops, ct) => FolderWarm.Read(ops, ct);

    /// 기록 없는 예열을 묶음별 진행 콜백과 함께 읽습니다. 지정되지 않으면 기존 ColdRead 를 씁니다.
    public Func<IReadOnlyList<ReadOp>, CancellationToken, Action<long, long>, (StreamStats Stats, bool Complete)>? ColdReadWithProgress { get; init; }

    /// 기록 없는 common-first 예열을 묶음별 진행과 실제 읽기 요청 알림을 함께 전달해 읽습니다.
    public Func<IReadOnlyList<ReadOp>, CancellationToken, Action<long, long>, Action<string, long, bool>, (StreamStats Stats, bool Complete)>? ColdReadWithIssue { get; init; }

    /// 파일 목록 예열 (Prewarmer.WarmFiles) 의 읽기: 파일마다 낮은 I/O 우선순위 힌트를 붙인 버퍼드 읽기 (묶음 사이에서 취소를 봄). 시험은 가짜로 바꿉니다.
    public Func<IReadOnlyList<ReadOp>, CancellationToken, (StreamStats Stats, bool Complete)> FileRead { get; init; } = FileWarm.Read;

    /// 기록 없는 예열이 건드리면 안 되는 드라이브면 이유 (시스템 SSD·SSD·판정 불가), 아니면 null. 기본은 거부 없음 (시험용), Create 는 진짜 판정.
    public Func<GameSettings, string?> ColdRefusal { get; init; } = _ => null;

    /// 부트스트랩 S2: 기록 없는 게임의 boot 트레이스를 프리페치 기록 (.pf) 으로 만들어 봅니다 (ColdRefusal 이 없을 때만 부름). 만들었으면 true: 그러면 더는 기록 없는 게임이 아니라 통째·프로필 예열 (S3) 을 하지 않습니다. 기본 false.
    public Func<GameSettings, bool> PrefetchBoot { get; init; } = _ => false;

    /// 기록 없이 예열한 게임의 표본 (경로, 오프셋, 길이) 들을 읽어 잰 결과 (CacheProbe), 읽을 수 없으면 null.
    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?> ProbeSamples { get; init; } = s => CacheProbe.Probe(s);

    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, Func<bool>, ProbeResult?>? ProbeSamplesChecked { get; init; }

    /// 파일 목록 예열 (Prewarmer.WarmFiles) 의 이미 캐시됐는가 점검용 표본 읽기: 표본 사이에서 취소를 봅니다 (취소되면 null).
    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, CancellationToken, ProbeResult?> ProbeFileSamples { get; init; } = (s, ct) => CacheProbe.Probe(s, ct: ct);

    public Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, CancellationToken, Func<bool>, ProbeResult?>? ProbeFileSamplesChecked { get; init; }

    /// 부팅 뒤 경과 시간 (ms). 서비스 시작 예열은 부팅 직후의 디스크 경쟁을 피해 BootDelay 가 지난 뒤에 시작합니다. 시험 기본값은 하루 (이미 지남).
    public Func<long> UptimeMs { get; init; } = () => (long)TimeSpan.FromDays(1).TotalMilliseconds;

    /// 서비스 시작 예열의 대기 (취소되면 일찍 돌아옴). 시험 기본값은 기다리지 않음.
    public Action<TimeSpan, CancellationToken> Sleep { get; init; } = (_, _) => { };

    /// 시작 예열이 핫 집합보다 먼저 올릴 common (여러 회차에서 반복해 읽힌) 범위: (게임, 이번 스트림의 틈 예산, 건너뛸 앞부분 바이트) → (이번 스트림이 읽을 범위 중 common 인 부분 (읽는 순서), 이번 스트림 전체 바이트 (핫 집합 스트림의 total 과 같음)). 없으면 빈 목록과 0 (기존 동작). 기본은 없음 (시험용), Create 는 진짜 구분.
    public Func<GameSettings, long, long, (IReadOnlyList<ReadOp> Ops, long Total)> CommonFirstOps { get; init; } = (_, _, _) => ([], 0);

    public Func<GameSettings, long, long, CancellationToken, (IReadOnlyList<ReadOp> Ops, long Total)>? CommonFirstOpsWithCancellation { get; init; }

    /// 핫 집합 스트림 직전에 원래 op 목록으로 장치 표본을 모읍니다.
    public Action<GameSettings, IReadOnlyList<ReadOp>, string?, DiskReadCoordinator.Lease?, CancellationToken>? DeviceCalibration { get; init; }

    /// 장치 표본 훅을 부를지 (모드가 off 면 거짓): 거짓이면 훅 인자를 만드는 메타데이터 읽기도 하지 않습니다. null 이면 훅이 있을 때 항상 부릅니다.
    public Func<bool>? DeviceCalibrationEnabled { get; init; }

    /// 스트림 노출과 같은 디스크 게임 시작을 예열 스레드에서 동기로 기록합니다.
    public Action<GameSettings, double, bool>? OrbitExposure { get; init; }

    public static Func<GameSettings, bool> GameActiveOnDisk(IReadOnlyList<GameSettings> games,
        Func<string, string, bool> sameDisk, Func<GameSettings, bool> gameRunning) => OrbitRuntime.GameActiveOnDisk(games, sameDisk, gameRunning);

    public static PrewarmEnv Create(OrbitCalibration? orbitCalibration = null, OrbitRuntime? orbitRuntime = null)
    {
        Func<GameSettings, bool> gameRunning = DispatchEnv.IsGameRunning;
        Func<string, string, bool> sameDisk = VolumeDisks.SameDisk;

        return new PrewarmEnv
        {
            GameRunning = gameRunning,
            ProcessRunning = exe => ProcessSnapshot.IsRunning([exe]),
            GameRunningSnapshot = () =>
            {
                IReadOnlyList<(int Pid, string Exe)> processes = ProcessSnapshot.Take();
                return g => DispatchEnv.IsGameRunning(g, processes);
            },
            SameDisk = sameDisk,
            DeviceCalibrationEnabled = orbitRuntime is null ? null : () => orbitRuntime.Mode != "off",
            DeviceCalibration = orbitCalibration is null ? null : (g, ops, antiCheat, lease, ct) =>
            {
                string disk = orbitCalibration.KeyOf(g);
                DeviceFit? before = orbitCalibration.Envelope(disk);
                orbitCalibration.Sample(g, ops, antiCheat, lease, ct);
                if (before is not null && orbitCalibration.Envelope(disk) is null)
                {
                    _ = orbitRuntime?.Demote("coverage_invalidated");
                }
            },
            OrbitExposure = orbitRuntime is null ? null : (g, seconds, interrupted) =>
            {
                if (orbitRuntime.Mode != "off")
                {
                    OrbitModel.NoteExposure(g, seconds, interrupted);
                }
            },
            CommonFirstOps = (g, maxBytes, skip) =>
            {
                if (TracePhases.CommonOf(g) is not { } common)
                {
                    return ([], 0); // 회차가 부족하거나 트레이스가 없음 (트레이스 금지 게임·공유 프로필만 있는 게임 포함)
                }

                (List<ReadOp> ops, _, bool fits) = PrefetchStreamer.PrewarmOps(g, maxBytes);
                List<ReadOp> stream = skip > 0 ? PrefetchStreamer.Tail(ops, skip) : ops;
                return fits ? (TracePhases.Within(common, stream), stream.Sum(o => o.Length)) : (new List<ReadOp>(), 0L);
            },
            CommonFirstOpsWithCancellation = (g, maxBytes, skip, cancellationToken) =>
            {
                if (TracePhases.CommonOf(g, cancellationToken: cancellationToken) is not { } common)
                {
                    return ([], 0);
                }

                (List<ReadOp> ops, _, bool fits) = PrefetchStreamer.PrewarmOps(g, maxBytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                List<ReadOp> stream = skip > 0 ? PrefetchStreamer.Tail(ops, skip) : ops;
                return fits ? (TracePhases.Within(common, stream, cancellationToken), stream.Sum(o => o.Length)) : (new List<ReadOp>(), 0L);
            },
            UptimeMs = () => Environment.TickCount64,
            Sleep = (t, ct) => ct.WaitHandle.WaitOne(t),
            PrefetchBoot = g => AutoTracer.BootFromPrefetch(g) > 0,
            ColdRefusal = g => DriveCapabilities.Of(g.OriginRoot, Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant(), letter => DiskInfo.Query(letter).IncursSeekPenalty).PrewarmRefusal,
            RawTraceExists = g => AutoTracer.PlanTracePath(g, File.Exists) is not null,
            ColdPlanner = (g, full, profile, ct) => PlanGuards.Inside(g.OriginRoot, g.AllowedRoots, RealPaths.Real)
                ? FolderWarm.Plan(g.OriginRoot, g.ExeNames, full, profile, EngineProfiles.Builtin, ct)
                : throw new PlanRejectedException("origin_root resolves outside allowed roots"),
            ColdReadWithProgress = (ops, ct, progress) =>
            {
                long done = 0;
                long total = ops.Sum(o => o.Length);
                return FolderWarm.Read(ops, ct, batch =>
                {
                    StreamStats stats = ZoneStreamer.Run(batch, StreamMethod.Pvm, ct: ct);
                    done += stats.Bytes;
                    progress(done, total);
                    return stats;
                });
            },
            ColdReadWithIssue = (ops, ct, progress, noteIssue) =>
            {
                long done = 0;
                long total = ops.Sum(o => o.Length);
                return FolderWarm.Read(ops, ct, batch =>
                {
                    noteIssue("common-first batch", batch.Sum(op => op.Length), true);
                    StreamStats stats = ZoneStreamer.Run(batch, StreamMethod.Pvm, ct: ct, noteIssue: noteIssue);
                    done += stats.Bytes;
                    progress(done, total);
                    return stats;
                });
            },
            PhysicalRamBytes = SystemMemory.TotalBytes,
            FileStamp = PlanFile.FingerprintOf,
            PinPaths = g => PrefetchStreamer.PinPaths(g, PrefetchStreamer.PinCapBytes(PrefetchStreamer.ZoneBytes(g), SystemMemory.TotalBytes(), SystemMemory.AvailableMib(), g.Prewarm.MinFreeRamMib)),
            AntiCheatOf = g => AutoEnv.DetectAntiCheat(g.OriginRoot),
            LauncherRunning = () => ProcessSnapshot.IsRunning(LauncherExes.All),
            CpuPercent = new CpuLoad().Percent,
            DiskBytesPerSecond = g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)),
            DiskSample = g => DiskActivity.Read(PlanFile.SplitDrive(g.OriginRoot)),
            GameDiskSample = g => DiskActivity.Read(PlanFile.SplitDrive(g.OriginRoot), keepStalled: true),
            GameReadBytes = RegisteredReadBytes,
            OpenPin = paths =>
            {
                FilePin pin = FilePin.Open(paths);
                return new PinHandle(pin, pin.Count, pin.Skipped);
            },
            ProbePartialReadMs = g => File.Exists(g.Trace) ? PrefetchStreamer.ProbePartialReadMs(g) : null,
            ProbePartialReadMsChecked = (g, canIssue) => File.Exists(g.Trace) ? PrefetchStreamer.ProbePartialReadMs(g, canIssue) : null,
            ProbeDone = (g, bytes) => File.Exists(g.Trace) ? PrefetchStreamer.ProbeScheduledHead(g, bytes) : null,
            ProbeDoneChecked = (g, bytes, canIssue) => File.Exists(g.Trace) ? PrefetchStreamer.ProbeScheduledHead(g, bytes, canIssue) : null,
            ProbeReadMs = g => File.Exists(g.Trace) ? PrefetchStreamer.ProbeReadMs(g) : null, // 기록 없이 예열한 게임은 비교할 핫 집합이 없음
            ProbeReadMsChecked = (g, canIssue) => File.Exists(g.Trace) ? PrefetchStreamer.ProbeReadMs(g, canIssue) : null,
            ProbeSamplesChecked = (samples, canIssue) => CacheProbe.Probe(samples, canIssue: canIssue),
            ProbeFileSamplesChecked = (samples, ct, canIssue) => CacheProbe.Probe(samples, ct: ct, canIssue: canIssue),
            AvailableRamMib = SystemMemory.AvailableMib,
            DescribeBlockers = DispatchEnv.DescribeBlockers,
            ZoneBytes = g => PrefetchStreamer.WarmBytes(g) is { } z ? Math.Min(z, g.StreamCapBytes(z)) : null,
            ObservedBytes = PrefetchStreamer.ObservedBytes,
            ObservedBytesIfCached = g => PrefetchStreamer.ObservedBytes(g, allowSourceRead: false),
            TraceExists = g => File.Exists(g.Trace),
            EffectiveZoneBytes = EffectiveZoneFromPlan,
        };
    }

    // 등록 exe 이름 프로세스들의 읽기 카운터 합: 하나라도 못 읽거나 하나도 없으면 null (조회 실패를 0 으로 보지 않음).
    private static long? RegisteredReadBytes(GameSettings g)
    {
        long sum = 0;
        bool any = false;
        foreach ((int pid, string exe) in ProcessSnapshot.Take())
        {
            if (pid == Environment.ProcessId || !g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProcessSnapshot.ReadBytes(pid) is not { } bytes)
            {
                return null;
            }

            (sum, any) = (sum + bytes, true);
        }

        return any ? sum : null;
    }

    // AutoRelocator 가 계획 stats 에 적는 effective_zone_mib (EffectiveZoneMib 의 결과) 를 그대로 읽습니다.
    private static long? EffectiveZoneFromPlan(GameSettings g)
    {
        try
        {
            return PlanFile.Load(g.Plan).Stats.TryGetValue("effective_zone_mib", out object? v) && v is long mib && mib > 0 ? mib << 20 : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }
}

/// 열어 둔 고정 (Dispose 하면 풂)과 고정한 파일 수, 건너뛴 파일 수.
public sealed record PinHandle(IDisposable Pin, int Count, int Skipped);

internal sealed class Nothing : IDisposable
{
    public void Dispose()
    {
    }
}

public sealed record PrewarmResult(string Game, bool Ran, string? SkipReason, StreamStats? Stats, DateTimeOffset At)
{
    /// 기록 없이 예열했을 때의 방식 이름표 ("full" 또는 "profile Unity"), 핫 집합 예열이면 null.
    public string? Mode { get; init; }

    internal string? CancelDetail { get; init; }
}

/// 사전 예열: 서비스 시작 때와 트리거 프로그램이 뜰 때, 게임의 존 순서 스트림 (기존 IGameStreamer, pvm) 을 돌려 핫 집합을 RAM 에 올립니다.
/// 결과는 service.log 와 상태 파일 (aster status 가 읽음) 에 남깁니다. 예열 중 게임이 시작돼도 스트림은 멈추지 않습니다 (같은 데이터).
public sealed class Prewarmer
{
    internal static readonly TimeSpan RecentWarmupWindow = TimeSpan.FromMinutes(1);
    /// 게임 시작으로 취소돼 스트림을 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string Cancelled = "cancelled";

    /// 업데이트 직후 지연으로 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string PostUpdateDelay = "post-update delay";

    /// 같은 디스크에서 재배치가 시작했거나 도는 중이라 읽기를 시작하지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string RelocationRunning = "relocation running";

    /// 게임별 정책 (games[].mode, games[].paused) 이 예열을 막아 읽지 않았다는 표시 (PrewarmResult.SkipReason).
    public const string GamePolicySkip = "game policy";

    // 재배치 직후 예열의 이유 (AutoRelocator 가 "auto " + "after relocation" 으로 넘김): 회복 기간 고정의 조건.
    private const string AfterRelocation = "auto after relocation";

    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly string? _statusPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new(); // 짧은 메모리 상태만 보호: 스트림이 도는 동안에도 LastWarmed·Warmed 가 막히지 않게
    private readonly object _run = new(); // 예열을 한 번에 하나로
    private readonly Dictionary<string, int> _activeWork = new(StringComparer.OrdinalIgnoreCase); // 설정 재적재 때 진행 중인 게임 상태를 보존, _gate 로 보호
    private readonly HashSet<string> _removedGames = new(StringComparer.OrdinalIgnoreCase); // 진행 중 작업이 끝난 뒤 임시 상태를 정리할 게임, _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastBeforeBoot = new(StringComparer.OrdinalIgnoreCase); // 부팅 전에 성공한 예열 시각 (상태 파일), 새 트레이스 판정 (LastWarmed) 만 씀, _gate 로 보호
    private sealed record PendingWarm(DateTimeOffset RetryAt, string Reason, string? TriggerExe = null); // TriggerExe: 막던 게임이 사라져 한 번 다시 시도할 트리거 exe
    private readonly Dictionary<string, PendingWarm> _pendingWarm = new(StringComparer.OrdinalIgnoreCase); // 성공하지 못한 일시 예열과 시작 상한 누락, _gate 로 보호
    private sealed class PrewarmCancelDetail
    {
        private string? _first;
        private int _memory;

        public string? First => Volatile.Read(ref _first);

        /// 처음 남은 사유가 읽는 도중 메모리 판정이었는가 (문구가 아니라 값으로 구분).
        public bool Memory => Volatile.Read(ref _memory) != 0;

        public void Set(string reason, bool memory = false)
        {
            if (Interlocked.CompareExchange(ref _first, reason, null) is null && memory)
            {
                Volatile.Write(ref _memory, 1);
            }
        }
    }
    private readonly JObject _status = new();
    private readonly PinKeeper _pins;
    private readonly Rewarmer _rewarm;
    private GameExitTracker? _exitTracker;
    private DiskReadCoordinator? _coordinator;
    private readonly Dictionary<string, long> _coldMib = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열해 RAM 에 올려 둔 양 (MiB), _gate 로 보호
    private readonly Dictionary<string, IReadOnlyList<(string Path, long Offset, int Bytes)>> _coldSamples = new(StringComparer.OrdinalIgnoreCase); // 기록 없이 예열한 파일 목록에서 고른 재예열 점검 표본, _gate 로 보호
    private readonly Dictionary<string, ResidencyProbe> _residencyProbes = new(StringComparer.OrdinalIgnoreCase); // 마지막 실제 상주 표본과 계획·trace 세대, _gate 로 보호
    private readonly Dictionary<string, string> _profileMiss = new(StringComparer.OrdinalIgnoreCase); // 엔진 프로필이 없다는 판정과 그때의 설치 폴더 mtime 키 (prewarm_status.json 의 profile_miss_key), _gate 로 보호
    private readonly HashSet<string> _refusedLogged = new(StringComparer.OrdinalIgnoreCase); // 드라이브 거부를 이미 로그한 게임, _gate 로 보호
    private readonly HashSet<string> _policyLogged = new(StringComparer.OrdinalIgnoreCase); // 게임 정책으로 예열을 멈춘 것을 이미 로그한 게임 (정책이 다시 허용되면 지움), _gate 로 보호
    private readonly Dictionary<string, bool> _delayLogged = new(StringComparer.OrdinalIgnoreCase); // 업데이트 보류를 이미 로그한 게임 (값: 재계획 보류인가, 아니면 지연): 보류 종류가 바뀌면 다시 로그, _gate 로 보호
    private readonly HashSet<string> _relocHoldLogged = new(StringComparer.OrdinalIgnoreCase); // 재배치 중이라 시작 예열을 보류한 것을 이미 로그한 게임, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _skipLogged = new(StringComparer.OrdinalIgnoreCase); // 게임|사유별 마지막으로 남긴 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private readonly Dictionary<string, (string Reason, long WarmedMib, long HotMib)> _supplementNothingNew = new(StringComparer.OrdinalIgnoreCase); // 보충할 새 구간이 없다는 마지막 상태, _gate 로 보호
    private readonly Dictionary<string, (string Reason, long CapMib, long UsedMib, long AllocMib)> _pendingWarmBudgetSkipped = new(StringComparer.OrdinalIgnoreCase); // 예산 거절의 마지막 상태, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> _triggerSeen = new(StringComparer.OrdinalIgnoreCase); // 트리거 exe 별 마지막 처리 시각과 그 뒤 억제한 횟수, _gate 로 보호
    private readonly Dictionary<string, (DateTimeOffset At, long Bytes)> _readSample = new(StringComparer.OrdinalIgnoreCase); // 실행 중인 게임의 마지막 읽기 카운터 표본, _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _gameIdleSince = new(StringComparer.OrdinalIgnoreCase); // 실행 중인 게임이 한가해진 시각 (읽기 증가율·디스크 처리량이 낮은 상태가 이어진 시작), _gate 로 보호
    private readonly TriggerRetries _triggerRetries = new(); // 같은 디스크의 다른 게임 때문에 거절된 트리거 예열과 그 막던 게임들
    private (string Game, DateTimeOffset At)? _triggered; // 가장 최근 트리거로 예열을 시작한 게임과 시각: 보충·재예열 예산 순위에서 먼저 받음, _gate 로 보호
    private volatile AutoSettings _auto = AutoSettings.Default;
    private volatile string? _streaming; // 핫 집합 스트림이 도는 게임
    private volatile string? _coldReading; // 기록 없는 (통째·프로필) 예열의 읽기가 도는 게임: _streaming 과 달리 게임 시작 양보 신호는 받지 않음 (ct 로만 멈춤)
    private readonly HashSet<string> _relocStarted = new(StringComparer.OrdinalIgnoreCase); // 재배치가 시작한 (아직 끝나지 않은) 게임, _gate 로 보호
    private volatile bool _keepStream; // auto.keep_stream_on_game_start 가 켜진 채 그 게임이 시작돼 (같은 데이터를 동시에 읽으므로) 취소 신호에도 끝까지 읽기로 한 표시
    private volatile bool _streamGameStarted;
    private volatile CancellationTokenSource? _streamStop; // 도는 스트림을 멈추는 신호 (게임 시작이 같은 디스크 읽기를 바로 끊음)
    private volatile PrewarmCancelDetail? _streamCancelDetail;
    private volatile CancellationTokenSource? _calibrationStop;
    private volatile CancellationTokenSource? _warmStop; // WarmFiles 읽기를 멈추는 신호 (호출자 ct 와 연결, 같은 디스크 게임 시작이 끊음), 읽는 게임은 _coldReading
    private (string Game, long At)? _yield; // 게임 시작으로 스트림 양보를 요청한 게임과 시각 (Stopwatch 틱), _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _partial = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 상한으로 핫 집합의 앞부분만 읽은 게임 (읽은 양, 전체), 보충 예열이 끝나면 지움, _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _memoryPartial = new(StringComparer.OrdinalIgnoreCase); // 메모리가 모자라 핫 집합의 앞부분만 읽은 게임 (다음에 전체가 들어가는 예열이 끝나면 지움)
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _memoryRunning = new(StringComparer.OrdinalIgnoreCase); // 지금 부분 예열을 읽는 중 (스트림이 끝나는 알림에서 "부분" 표시를 붙이려고)
    private readonly Dictionary<string, DateTimeOffset> _memoryStopAt = new(StringComparer.OrdinalIgnoreCase); // 읽는 도중 메모리 판정으로 마지막에 멈춘 시각 (다시 읽기 시작하거나 끝까지 읽으면 지움), _gate 로 보호
    private readonly Dictionary<string, (long WarmedMib, long HotMib)> _startupTrim = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 예열이 상한으로 줄여 읽는 중인 게임 (Record 가 같은 상태 쓰기에 warmed_mib/hot_mib 를 넣음), _gate 로 보호
    private readonly Dictionary<string, DateTimeOffset> _idleSince = new(StringComparer.OrdinalIgnoreCase); // 보충 예열을 기다리는 게임이 한가해지기 시작한 시각, _gate 로 보호
    private readonly Dictionary<string, (long WindowMib, long Bytes)> _resume = new(StringComparer.OrdinalIgnoreCase); // 끊긴 보충 예열이 그 윈도 (MaxStreamMib 로 잘린 목록) 의 스케줄 앞쪽에서 이미 읽은 바이트: 다음 보충이 이어 읽는 위치, _gate 로 보호
    private sealed record SupplementReadProgress(long Bytes, string? Inputs, GameSettings Settings);
    private sealed record SupplementRetry(DateTimeOffset RetryAt, long CapMib, long ProgressBytes, string? Inputs, GameSettings Settings, bool PlanExhausted, bool EarlyRetryUsed, bool Logged);
    private readonly Dictionary<string, SupplementReadProgress> _supplementReadProgress = new(StringComparer.OrdinalIgnoreCase); // 부분 예열에서 마지막으로 받아들인 바이트 (MiB 반올림 전), _gate 로 보호
    private readonly Dictionary<string, SupplementRetry> _supplementRetry = new(StringComparer.OrdinalIgnoreCase); // 무진전 또는 읽을 목록 소진 뒤 재시도 기한, _gate 로 보호
    private DateTimeOffset _driveObservedAt; // 지금 도는 읽기가 멈춰서 디스크 처리량을 마지막으로 잰 (또는 읽기를 시작한) 시각, _run 아래에서만 씀

    private sealed record ProbeFileGeneration(string Path, bool Exists, long Size, long MtimeNs);
    private sealed record ProbeGeneration(ProbeFileGeneration Plan, ProbeFileGeneration Trace, ProbeFileGeneration WarmOnly);
    private sealed record ResidencyProbe(bool Warm, DateTimeOffset At, ProbeGeneration Generation);

    private sealed class GameWorkLease(Prewarmer owner, string game) : IDisposable
    {
        private Prewarmer? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndGameWork(game);
    }

    public Prewarmer(IReadOnlyList<GameSettings> games, IGameStreamer streamer, PrewarmEnv env, ILog log, string? statusPath = null, Func<DateTimeOffset>? now = null,
        DiskReadCoordinator? coordinator = null)
    {
        _games = games;
        _streamer = streamer;
        _env = env;
        _log = log;
        _statusPath = statusPath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _coordinator = coordinator;
        _pins = new PinKeeper(env, log, _now) { Coordinator = coordinator };
        _rewarm = new Rewarmer(this, env, log, _now);
        LoadStatus();
    }

    public void SetDiskReadCoordinator(DiskReadCoordinator coordinator) => (_coordinator, _pins.Coordinator) = (coordinator, coordinator);

    public void SetGameExitTracker(GameExitTracker tracker)
    {
        _exitTracker = tracker;
        _rewarm.SetGameExitTracker(tracker);
    }

    private bool HeavyWorkAllowed(GameSettings game) =>
        _exitTracker?.HeavyWorkAllowed(game, _games, _env.SameDisk, _auto.RewarmAfterExitMin, _now()) ?? true;

    // 이전 실행의 상태 파일: 항목은 그대로 이어 쓰고, 부팅 뒤에 성공한 예열만 _last 로 되살려 서비스만 다시 시작했을 때 같은 게임을 또 읽지 않게 합니다 (부팅 전 것은 파일 캐시가 비었으므로 제외).
    private void LoadStatus()
    {
        if (_statusPath is null || !File.Exists(_statusPath))
        {
            return;
        }

        try
        {
            if (JsonRead.ReadFile(_statusPath) is not JObject root || !root.TryGetValue("games", out object? games) || games is not JObject all)
            {
                return;
            }

            DateTimeOffset boot = _now() - TimeSpan.FromMilliseconds(_env.UptimeMs());
            foreach ((string name, object? value) in all)
            {
                if (value is not JObject o)
                {
                    continue;
                }

                _status[name] = o;
                if (o.TryGetValue("profile_miss_key", out object? miss) && miss is string missKey)
                {
                    _profileMiss[name] = missKey; // 프로필 미일치 판정은 설치 폴더 mtime 이 같은 동안 다시 열거하지 않음 (재시작 뒤에도 이어짐)
                }

                DateTimeOffset? okAt = LastOkAt(o);
                if (okAt is { } earlier && earlier <= boot)
                {
                    _lastBeforeBoot[name] = earlier; // 부팅 전 예열: _last 로는 되살리지 않음 (캐시가 비었음), 새 트레이스로 다시 치지 않도록 LastWarmed 만 돌려줌
                }

                if (okAt is { } when && when > boot)
                {
                    _last[name] = when;
                    if (o.TryGetValue("warmed_mib", out object? warmed) && warmed is long w && o.TryGetValue("hot_mib", out object? hot) && hot is long h && w < h)
                    {
                        _partial[name] = (w, h); // 부분 예열 뒤 서비스만 다시 시작해도 보충이 이어짐
                        if (o.TryGetValue("partial", out object? memoryFlag) && memoryFlag is true && w > 0 && w <= int.MaxValue)
                        {
                            _memoryPartial[name] = (w, h); // 메모리가 모자라 앞부분만 읽은 상태도 이어짐 (표본 제한과 재읽기)
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn("prewarm status file not read: " + e.GetType().Name);
        }
    }

    // 마지막 성공 시각: last_ok_at 은 마지막 시도가 건너뜀·실패여도 남습니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게). 옛 파일은 result ok 의 at.
    private static DateTimeOffset? LastOkAt(JObject o)
    {
        string? text = null;
        if (o.TryGetValue("last_ok_at", out object? kept))
        {
            text = kept as string;
        }
        else if (o.TryGetValue("result", out object? result) && result as string == "ok" && o.TryGetValue("at", out object? at))
        {
            text = at as string;
        }

        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when) ? when : null;
    }

    // 이번 시도가 성공이 아니면 이전 항목의 마지막 성공 시각을 그대로 이어 씁니다.
    private string? KeptOkAt(string game)
    {
        lock (_gate)
        {
            return _status.TryGetValue(game, out object? prior) && prior is JObject po && LastOkAt(po) is { } ok ? ok.ToString("O") : null;
        }
    }

    /// 기록 없이 (통째·프로필) 예열에 성공했을 때: 게임과 방식 이름표.
    public event Action<GameSettings, string>? ColdWarmed;

    /// 예열이 끝났을 때의 실측 MB/s (드라이브 회복 판정의 캐시 적중 상한용).
    public event Action<GameSettings, double>? WarmMeasured;

    /// 핫 집합 예열의 진행 (묶음마다, ProgressEvery 에 한 번 이하): 게임, 읽은 MiB, 총 MiB.
    public event Action<GameSettings, long, long>? Progress;

    /// 핫 집합 스트림이 끝났을 때 (취소·실패 포함): 게임, 끝까지 읽었는가.
    public event Action<GameSettings, bool>? StreamEnded;

    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    internal const int GapFillReserveMib = 4096;

    /// 메모리가 모자라 예열을 줄여 읽을 때 이보다 작게 줄어들면 읽지 않고 건너뜁니다 (MiB).
    internal const int MinPartialMib = 256;

    // 최종 예산 재축소 뒤에도 짧은 메모리 변동으로 같은 전체 예열을 반복하지 않게 남길 여유 (MiB).
    internal const int FinalShrinkHeadroomMib = 256;

    // 메모리 사유로 멈춘 게임을 다시 읽기 전 최소 간격: 메모리가 계속 빠듯한 동안 같은 시도를 되풀이하지 않음.
    internal static readonly TimeSpan MemoryRetryFloor = TimeSpan.FromMinutes(2);

    // 부분 예열 창을 잡을 때 쓰는 가용 RAM (MiB): 처음 잡을 때도 계획 직후 재확인과 같은 여유를 빼서, 몇십 MiB 변동에 읽기가 통째 취소되지 않게 함.
    internal static long WindowAvailableMib(long availableRamMib) => Math.Max(0, availableRamMib - FinalShrinkHeadroomMib);

    // 재시도 성격의 예열 (대기 재시도, 재예열 점검): 메모리로 멈춘 직후 MemoryRetryFloor 안에는 다시 읽지 않음. 트리거·서비스 시작은 해당 없음.
    private static bool IsRetryReason(string why) => why is "pending warm retry" or "rewarm";

    // 실제로 읽은 양 (스트림 통계, MiB 올림) 을 설정한 상한 limit 으로 자른 값. 통계가 없으면 limit, 0 바이트면 0 (호출 쪽이 성공으로 확정하지 않음).
    // skipped: 이어 읽을 때 건너뛴 (이미 읽은) 바이트.
    internal static long ActualMib(StreamStats? stats, long limit, long skipped = 0) => stats is null ? limit : Math.Min(limit, (stats.Bytes + skipped + (1 << 20) - 1) >> 20);

    // 예열 예산 (MiB, 게임들이 나눠 씀): prewarm.max_cache_mib 가 있으면 (게임마다 적혀 있으면 가장 큰 값) 그 값을 물리 RAM 으로 자른 것, 없으면 물리 RAM / 2. 물리 RAM 을 모르면 제한 없음.
    // 존 최대 크기 (max_zone_mib auto) 의 자동값 (ZoneSize.AutoMaxMib) 과는 별개입니다.
    internal static long CacheCapMib(long physicalRamBytes, IEnumerable<GameSettings> games)
    {
        long physMib = physicalRamBytes > 0 ? physicalRamBytes >> 20 : long.MaxValue;
        long set = games.Select(g => (long)g.Prewarm.MaxCacheMib).DefaultIfEmpty(0).Max();
        return set > 0 ? Math.Min(set, physMib) : physMib == long.MaxValue ? physMib : physMib / 2;
    }

    private long PhysCapMib() => CacheCapMib(_env.PhysicalRamBytes(), _games);

    private IDisposable TrackGameWork(string game)
    {
        lock (_gate)
        {
            _activeWork[game] = _activeWork.GetValueOrDefault(game) + 1;
        }

        return new GameWorkLease(this, game);
    }

    private void EndGameWork(string game)
    {
        lock (_gate)
        {
            if (_activeWork.TryGetValue(game, out int count) && count > 1)
            {
                _activeWork[game] = count - 1;
            }
            else
            {
                _activeWork.Remove(game);
                PruneRemovedGameLocked(game);
            }
        }
    }

    private void PruneRemovedGameLocked(string game)
    {
        if (!_removedGames.Contains(game) || _activeWork.ContainsKey(game)
            || _relocStarted.Contains(game)
            || string.Equals(_streaming, game, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_coldReading, game, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _removedGames.Remove(game);
        _last.Remove(game);
        _pendingWarm.Remove(game);
        _idleSince.Remove(game);
        _profileMiss.Remove(game);
        _residencyProbes.Remove(game);
        _refusedLogged.Remove(game);
        _policyLogged.Remove(game);
        _delayLogged.Remove(game);
        _relocHoldLogged.Remove(game);
        _supplementNothingNew.Remove(game);
        _memoryStopAt.Remove(game);
        _pendingWarmBudgetSkipped.Remove(game);
        _readSample.Remove(game);
        _gameIdleSince.Remove(game);
        _coldMib.Remove(game);
        _coldSamples.Remove(game);
        _resume.Remove(game);
        _supplementReadProgress.Remove(game);
        _supplementRetry.Remove(game);
        foreach (string key in _skipLogged.Keys.Where(k => k.StartsWith(game + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _skipLogged.Remove(key);
        }

        if (_triggered is { } trigger && string.Equals(trigger.Game, game, StringComparison.OrdinalIgnoreCase))
        {
            _triggered = null;
        }

        HashSet<string> liveTriggers = new(_games.SelectMany(g => g.Prewarm.TriggerExes), StringComparer.OrdinalIgnoreCase);
        foreach (string triggerExe in _triggerSeen.Keys.Where(k => !liveTriggers.Contains(k)).ToList())
        {
            _triggerSeen.Remove(triggerExe);
        }
    }

    // 전체가 들어가지 않을 때 읽을 앞부분의 크기 (MiB): min(원래 읽을 양, 가용 RAM - 예비, 예열 예산 physCapMib). 너무 작으면 null (건너뜀). 앞쪽 = 계획 순서 = 게임이 먼저 읽는 순서.
    // roomMib: 묶음에서 남은 예산 (MiB, 단독 예열이면 제한 없음).
    internal static long? PartialCapMib(GameSettings game, long hotMib, long availableRamMib, long physCapMib, long roomMib = long.MaxValue)
    {
        long cap = Math.Min(Math.Min(Math.Min(hotMib, roomMib), availableRamMib - Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib)), physCapMib);
        return cap >= MinPartialMib && cap < hotMib && cap <= int.MaxValue ? cap : null;
    }

    // 묶음에 남은 예산 (MiB) = 예열 예산 - 이미 올린 양. 이미 올린 양이 있으면 가용 RAM - 여유 - 이미 올린 양도 넘지 않음 (첫 게임의 가용 RAM 쪽 판단은 Run 의 부분 예열이 함).
    // RunBatch (전체 예열) 와 WarmHeads (앞부분 예열) 가 같은 식을 씁니다.
    internal static long BatchRoomMib(long physCapMib, long availableRamMib, int minFreeRamMib, long heldMib)
    {
        long room = physCapMib - heldMib;
        return heldMib > 0 ? Math.Min(room, availableRamMib - minFreeRamMib - heldMib) : room;
    }

    // 묶음 예산이 모자랄 때만 순서를 바꿉니다. 순위 (입력 순서) 대로 통째 들어가는 앞 게임들과, 부분 예열이 남은 예산을 받는 첫 게임은 자리를 지키고,
    // 그 뒤 후보는 바이트당 가치 (순위 가중 1/(순위+1) ÷ 존 MiB, 존을 모르면 DefaultFullPrewarmMib) 순으로 늘어놓습니다. 모두 들어가면 입력 그대로.
    // shortcut: 점수 자체가 아니라 순위 위치를 가치로 씀 (예열 점수는 자동 모드 쪽에서 순위로만 넘어옴), 점수를 Prewarmer 에 넘기게 되면 그 값으로.
    internal static IReadOnlyList<GameSettings> OrderByValuePerByte(IReadOnlyList<GameSettings> list, Func<GameSettings, long> zoneMib, Func<GameSettings, long, long> roomMib)
    {
        long used = 0;
        int keep = 0;
        while (keep < list.Count && used + zoneMib(list[keep]) <= roomMib(list[keep], used))
        {
            used += zoneMib(list[keep++]);
        }

        if (keep < list.Count && roomMib(list[keep], used) >= MinPartialMib)
        {
            keep++; // 부분 예열이 남은 예산을 받는 게임은 순위 자리 유지
        }

        keep = Math.Max(keep, 1); // 순위 1위는 항상 자리를 지킴 (못 들어가도 뒤 후보와 섞이지 않음)
        if (list.Count - keep < 2)
        {
            return list;
        }

        return [.. list.Take(keep), .. list.Skip(keep).Select((g, n) => (G: g, V: 1.0 / (keep + n + 1) / (zoneMib(g) is > 0 and long z ? z : DefaultFullPrewarmMib))).OrderByDescending(x => x.V).Select(x => x.G)];
    }

    private long ZoneMib(GameSettings game) => ((_env.ZoneBytes(game) ?? 0) + (1 << 20) - 1) >> 20;

    private long ObservedMib(GameSettings game, long totalMib, bool allowSourceRead = true)
    {
        if (totalMib <= 0)
        {
            return totalMib;
        }

        long? observedBytes = allowSourceRead ? _env.ObservedBytes(game) : _env.ObservedBytesIfCached(game);
        if (observedBytes is not { } bytes)
        {
            return totalMib;
        }

        return Math.Min(totalMib, (bytes + (1 << 20) - 1) >> 20);
    }

    // 묶음 안 게임별 관찰분 (MiB)과, 각 게임 뒤에 남겨 둘 양: 뒤 게임들의 관찰분 합. 앞 게임의 추측분이 뒤 게임의 관찰분을 밀어내지 않게 합니다.
    // 자기 관찰분은 항상 보장되므로 목록 전체가 관찰된 (출처 표식이 없는) 게임은 이전 순위 규칙 그대로입니다. 건너뛰는 게임과 기록 없는 예열은 남기지 않습니다.
    private (long[] Observed, long[] Later) ObservedSplit(IReadOnlyList<GameSettings> list, long avail)
    {
        bool[] skips = [.. list.Select(g => SkipsInBatch(g, avail))];
        long[] observed = [.. list.Select((g, i) => skips[i] ? ZoneMib(g) : ObservedMib(g, ZoneMib(g), allowSourceRead: !DriveHasRunningGame(g)))];
        var later = new long[list.Count];
        for (int i = list.Count - 2; i >= 0; i--)
        {
            later[i] = later[i + 1] + (skips[i + 1] ? 0 : observed[i + 1]);
        }

        return (observed, later);
    }

    // 이 게임이 읽을 수 있는 양: 남은 예산에서 뒤 게임의 관찰분을 뺀 만큼 (자기 관찰분은 예산 안에서 항상 보장).
    private static long RoomKeepingLaterObserved(long room, long ownObserved, long later) => Math.Max(Math.Min(ownObserved, room), room - later);

    // 틈 메우기 포함 읽기 전체의 상한: min(유효 존 상한 (모르면 현재 존), 스트림 상한, 물리 RAM / 4, 가용 RAM - 예비). 원래 범위는 호출 쪽에서 뺍니다.
    internal static long GapFillBudgetBytes(GameSettings game, long? zoneBytes, long? effectiveZoneBytes, long physicalRamBytes, long availableRamMib)
    {
        long? capZone = effectiveZoneBytes is > 0 ? effectiveZoneBytes : zoneBytes;
        long streamCap = capZone is > 0 and long zone
            ? Math.Min(zone, game.StreamCapBytes(zone))
            : (long)game.MaxStreamMib << 20;
        long physicalCap = physicalRamBytes > 0 ? physicalRamBytes / 4 : long.MaxValue;
        long availableMib = Math.Max(0, availableRamMib - Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib));
        long availableCap = availableMib > (long.MaxValue >> 20) ? long.MaxValue : availableMib << 20;
        return Math.Min(streamCap, Math.Min(physicalCap, availableCap));
    }

    /// 서비스 시작 예열은 부팅 뒤 이만큼 지나고, 게임 드라이브가 IdleNeeded 동안 한가해진 뒤에 시작합니다 (부팅 직후 디스크 경쟁 방지).
    public static readonly TimeSpan BootDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan IdleNeeded = TimeSpan.FromSeconds(60);

    /// 트리거로 켠 게임이 보충·재예열 예산 순위에서 먼저 받는 기간 (그 뒤에는 기존 순위만).
    public static readonly TimeSpan TriggerPriorityWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan GateStep = TimeSpan.FromSeconds(5);

    /// 기록 없는 예열의 통째 상한 기본값 (MiB, full_prewarm_max_mib 가 0 일 때).
    public const int DefaultFullPrewarmMib = 1024;

    /// 이 게임을 플레이한 적이 있는가 (자동 모드의 last_played). 재예열 점검은 플레이한 게임만 봅니다. 기본은 모두 참 (서비스가 자동 상태를 보고 정함).
    public Func<string, bool> HasPlayed { get; set; } = _ => true;

    /// 다음에 실행될 가능성이 높은 순서로 게임을 돌려주는 순위 (자동 모드의 예열 점수 + 런처 트리거). 기본 null = 순위 없음: 재예열·보충 예열이 모든 대상을 처리하고 예산을 나누지 않음.
    public Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? WarmRank { get; set; }

    /// 게임 업데이트 직후의 지연 (auto.post_update_delay_min): 그 게임이 아직 기다려야 하면 남은 시간, 아니면 null. 기본은 지연 없음 (서비스가 자동 상태를 보고 정함).
    public Func<GameSettings, TimeSpan?> PostUpdateWait { get; set; } = _ => null;

    /// 업데이트를 감지해 재계획 상태가 된 때 (없으면 null): probe 모드에서 이 드라이브의 회복 판정 (Recovery) 의 기준 시각. 기본 null (고정 분만 씀).
    public Func<GameSettings, DateTimeOffset?> ReplanStart { get; set; } = _ => null;

    /// 업데이트 뒤 트레이스를 유지한 채 곧 다시 계획하는 상태인가 (바로 재계획 가능). 다음 플레이에서 다시 트레이스하는 상태는 거짓: 그 사이 새 예열을 막으면 플레이 전까지 영영 걸리므로 보류하지 않음. 기본 거짓.
    public Func<GameSettings, bool> ReplanKept { get; set; } = _ => false;

    /// 재배치 단계 (auto_status 의 relocating) 인 게임인가. 하나라도 있으면 서비스 시작 예열을 보류합니다.
    public Func<string, bool> Relocating { get; set; } = _ => false;

    /// 업데이트 지연으로 미뤄야 하는 게임이면 true (지연 한 번에 로그는 한 줄). 지연이 끝난 게임은 다음 지연 때 다시 로그를 남깁니다.
    private bool Deferred(GameSettings game)
    {
        // AutoRelocator.PostUpdateDelayed 와 같은 판정: probe 모드이고 이 드라이브에 회복 추적이 있으면 그 판정, 없으면 고정 분.
        bool? probed = _auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && Recovery is { } recovery && ReplanStart(game) is { } at
            ? recovery.WatchUpdate(PlanFile.SplitDrive(game.OriginRoot).ToUpperInvariant(), at, TimeSpan.FromMinutes(_auto.RecoveryMaxMin)) : null;
        bool delayed = probed ?? PostUpdateWait(game) is not null;
        bool replan = !delayed && ReplanKept(game); // 지연이 끝나도 곧 있을 재계획이 끝나 상태가 바뀔 때까지 (성공·실패 모두 상태가 바뀜) 옛 목록으로 새 예열을 시작하지 않음. 다시 트레이스할 때까지 기다리는 상태는 제외
        bool wait = delayed || replan;
        lock (_gate)
        {
            if (!wait)
            {
                _delayLogged.Remove(game.Name);
            }
            else if (!_delayLogged.TryGetValue(game.Name, out bool logged) || logged != replan)
            {
                _delayLogged[game.Name] = replan;
                _log.Info(replan ? $"{game.Name}: new prewarm held until the re-plan after the update finishes"
                    : probed is null ? AutoRelocator.PostUpdateLine(game.Name, _auto.PostUpdateDelayMin) : AutoRelocator.PostUpdateLine(game.Name, _auto.RecoveryMaxMin, probed: true));
            }
        }

        return wait;
    }

    /// 이 게임이나 같은 디스크의 게임을 읽는 예열 (핫 집합 스트림, 기록 없는 통째·프로필 읽기) 이 지금 도는가. 재배치가 시작하기 전에 보고 양보합니다 (같은 디스크에서 읽기와 쓰기가 겹치지 않게).
    public bool StreamingOnDisk(string game) => ReadingOnDisk(_streaming, game) || ReadingOnDisk(_coldReading, game);

    private bool ReadingOnDisk(string? reading, string game) => reading is not null && (string.Equals(reading, game, StringComparison.OrdinalIgnoreCase) || SameDisk(reading, game));

    // 이 게임과 같은 디스크의 등록 게임 이름들 (자신 포함). 디스크 조회는 잠금 밖에서 하고 잠금 안에서는 이름만 비교합니다.
    private HashSet<string> DiskMates(string game) =>
        new([game, .. _games.Where(g => !string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase) && SameDisk(g.Name, game)).Select(g => g.Name)], StringComparer.OrdinalIgnoreCase);

    /// 재배치 쪽: 같은 디스크의 예열 읽기가 없을 때만 재배치 시작 표시를 세우고 true (읽는 중이면 표시 없이 false). 확인과 표시가 _gate 아래에서 한 번에 이뤄져,
    /// 이 호출이 true 를 돌려준 뒤에는 같은 디스크의 예열이 읽기 표시를 세우지 못합니다 (TryMarkReading). 끝나면 (성공·실패·예외 모두) EndRelocation 을 불러야 합니다.
    public bool TryBeginRelocation(string game)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if ((_streaming is { } streaming && mates.Contains(streaming)) || (_coldReading is { } cold && mates.Contains(cold)))
            {
                return false;
            }

            _relocStarted.Add(game);
            return true;
        }
    }

    /// 재배치 시작 표시를 내립니다.
    public void EndRelocation(string game)
    {
        lock (_gate)
        {
            _relocStarted.Remove(game);
            PruneRemovedGameLocked(game); // 재배치 중에 설정에서 빠져 보류된 정리를 끝나는 때 마저 함
        }
    }

    // 예열 쪽: 같은 디스크에서 재배치가 시작했으면 false (표시를 세우지 않음), 아니면 mark 로 읽기 표시를 세우고 true. 확인과 표시가 _gate 아래에서 한 번에 이뤄집니다 (mark 는 필드 대입만).
    private bool TryMarkReading(string game, Action mark)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if (_relocStarted.Overlaps(mates))
            {
                return false;
            }

            foreach (string mate in mates)
            {
                _idleSince.Remove(mate);
            }

            mark();
            return true;
        }
    }

    // 묶음 경계 (게임 사이) 에서 재배치가 돌고 있으면 끝나고 디스크가 다시 한가해질 때까지 기다립니다 (시작 게이트와 같은 조건). 기다리는 중 취소되면 false.
    private bool WaitOutRelocation(GameSettings game, CancellationToken ct) => !RelocationHeld() || WaitStartupGate([game], ct);

    // 재배치 중인 게임이 있으면 (어느 게임이든) true. 보류 로그는 게임당 한 번, 재배치가 끝나면 다음에 다시 로그를 남깁니다.
    private bool RelocationHeld()
    {
        bool any = false;
        lock (_gate)
        {
            foreach (GameSettings g in _games)
            {
                if (!Relocating(g.Name))
                {
                    _relocHoldLogged.Remove(g.Name);
                    continue;
                }

                any = true;
                if (_relocHoldLogged.Add(g.Name))
                {
                    _log.Info($"{g.Name}: startup prewarm held while relocation is running");
                }
            }
        }

        return any;
    }

    /// 게임 exe 가 떴을 때 (스트림 취소보다 먼저): 도는 예열 스트림이 그 게임의 것이거나 같은 디스크의 것이면 다음 묶음 경계에서 멈추게 해 디스크를 양보하고 (같은 HDD 동시 읽기는 게임을 느리게 함),
    /// 단 auto.keep_stream_on_game_start (실험용) 가 켜져 있으면 그 게임의 스트림은 끝까지 읽습니다. 기록 없는 예열이 잡아 둔 예산은 풉니다 (게임이 돌면 RAM 이 달라지므로 다음 예산 계산에 쓰지 않음).
    public void NoteGameStart(string game)
    {
        OrbitModel.CancelInFlightGame(game);
        if (_warmStop is { } warm && _coldReading is { } warming && (string.Equals(warming, game, StringComparison.OrdinalIgnoreCase) || SameDisk(warming, game)))
        {
            try
            {
                warm.Cancel(); // 챔피언 선읽기 (WarmFiles) 도 같은 디스크 게임 시작에 바로 멈춤
            }
            catch (ObjectDisposedException)
            {
                // 읽기가 방금 끝남
            }
        }

        if (_streaming is { } reading)
        {
            bool same = string.Equals(reading, game, StringComparison.OrdinalIgnoreCase);
            bool sameDisk = same || SameDisk(reading, game);
            if (sameDisk)
            {
                _streamGameStarted = true;
                try
                {
                    _calibrationStop?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 표본 읽기가 방금 끝남
                }
            }

            if (same && _auto.KeepStreamOnGameStart)
            {
                _keepStream = true;
            }
            else if (sameDisk)
            {
                lock (_gate)
                {
                    _yield ??= (game, Stopwatch.GetTimestamp());
                }

                _streamCancelDetail?.Set(same ? "game started" : "another game started on the same disk");

                try
                {
                    _streamStop?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 스트림이 방금 끝남
                }
            }
        }

        lock (_gate)
        {
            _coldMib.Remove(game);
            _supplementRetry.Remove(game); // 플레이 뒤 재예열은 정체 기한을 넘겨서 다시 채울 수 있음
        }
    }

    // 두 게임이 같은 물리 디스크에 있는가 (env.SameDisk: 한 디스크에 볼륨이 둘이어도 같다고 봄).
    private bool SameDisk(string a, string b) =>
        _games.FirstOrDefault(g => string.Equals(g.Name, a, StringComparison.OrdinalIgnoreCase)) is { } x && _games.FirstOrDefault(g => string.Equals(g.Name, b, StringComparison.OrdinalIgnoreCase)) is { } y
        && _env.SameDisk(x.OriginRoot, y.OriginRoot);

    // 읽기 증가율이 이 값 미만이면 실행 중인 다른 게임을 한가하다고 봄 (디스크 한가함 기준 BusyBytesPerSecond 4 MiB/s 의 1/4: 상주 클라이언트의 잔 읽기는 넘기고 로딩은 넘지 않게).
    internal const double IdleGameReadBytesPerSecond = 1 << 20;

    // 이 게임이나 같은 디스크의 다른 등록 게임이 돌고 있는가 (다른 게임은 한가하지 않을 때만): 돌면 그 디스크에 새 읽기를 내지 않습니다.
    // running: 이미 찍은 스냅샷으로 판정하는 함수 (묶음 경계 점검이 한 번만 찍어 나눠 씀), 없으면 env.GameRunning.
    private bool DriveHasRunningGame(GameSettings game, Func<GameSettings, bool>? running = null) => (running ?? _env.GameRunning)(game) || BusyOthers(game, running).Count > 0;

    // 같은 디스크에서 돌고 있으면서 한가하지 않은 다른 등록 게임 (재예열 점검도 씀).
    internal List<GameSettings> BusyOthers(GameSettings game, Func<GameSettings, bool>? running = null)
    {
        running ??= _env.GameRunning;
        return [.. AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Where(x => !string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase) && running(x) && !OtherGameIdle(x))];
    }

    // 같은 디스크에서 우리 읽기가 도는 중인가: 그동안의 디스크 처리량에는 우리 읽기가 섞임 (우리 읽기량은 물리 읽기가 아니라 PVM 힌트 바이트라 빼서 보정할 수도 없음).
    private bool SelfReadingOn(GameSettings other) =>
        (_streaming ?? _coldReading) is { } reading && (string.Equals(reading, other.Name, StringComparison.OrdinalIgnoreCase) || SameDisk(reading, other.Name));

    // 읽는 도중 같은 디스크에서 다른 등록 게임이 도는 동안, 이 간격마다 읽기를 잠깐 멈추고 외부 부하를 잼.
    private static readonly TimeSpan DiskObserveEvery = TimeSpan.FromSeconds(10);

    // 멈춰 있는 관측 구간의 길이: DiskActivity 표본 하나 (1 초).
    private static readonly TimeSpan DiskObserveWindow = TimeSpan.FromSeconds(1);

    // 읽는 중에 외부 디스크 부하가 바쁨 기준 이상인가: 같은 디스크의 다른 등록 게임이 도는 동안 DiskObserveEvery 마다 다음 묶음을 내기 전에 읽기를 멈추고 (DiskObserveWindow) 처리량을 잼. 못 읽는 고장 (Broken) 이면 바쁨으로 보지 않지만 (진입 시점의 실패만 바쁨) 표본이 아직 없거나 낡았으면 (Warming·Stale) 바쁨.
    // shortcut: 처리량 표본 캐시 (2 초) 가 방금 것이면 우리 읽기가 섞일 수 있음, 관측 간격이 10 초라 보통은 새 표본이고 틀려도 양보 쪽으로만 어긋남.
    private bool ExternalLoadWhilePaused(GameSettings game, CancellationToken stop, Func<GameSettings, bool>? running = null)
    {
        running ??= _env.GameRunning;
        if (_now() - _driveObservedAt < DiskObserveEvery
            || !AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Any(x => !string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase) && running(x)))
        {
            return false;
        }

        _env.Sleep(DiskObserveWindow, stop);
        _driveObservedAt = _now();
        return !stop.IsCancellationRequested && ExternalDiskBusy(game);
    }

    private bool ExternalDiskBusy(GameSettings game)
    {
        (double? bps, DiskActivity.SampleState state) = (_env.GameDiskSample ?? _env.DiskSample)?.Invoke(game) ?? DefaultSample(_env.DiskBytesPerSecond(game));
        return DiskActivity.IsBusy(bps, state);
    }

    private static (double?, DiskActivity.SampleState) DefaultSample(double? bps) => (bps, bps is null ? DiskActivity.SampleState.Broken : DiskActivity.SampleState.Ok);

    // 읽는 도중 디스크 표본이 아직 없거나 낡았으면 (Warming·Stale) 처리량을 모르므로 다음 묶음부터 읽지 않음 (묶음 경계마다 부름: 스트림·기록 없는 예열·파일 예열 공통).
    // 고장 (Broken) 은 여기서 읽기 허용으로 넓히지도 막지도 않고 기존 동작 그대로 (정체가 60 초 넘으면 Broken 이 되는 점은 알려진 한계, 낡은 표본 (4 초) 이 먼저 멈춤).
    private bool DiskSampleUnknown(GameSettings game) => _env.DiskSample?.Invoke(game).State is DiskActivity.SampleState.Warming or DiskActivity.SampleState.Stale;

    // 실행 중인 게임이 한가한가: 읽기 카운터 증가율 (직전 표본과의 차) 이 낮고 디스크 처리량도 (우리 읽기가 없을 때) 낮은 상태가 IdleNeeded 이상 이어졌을 때만 참.
    // 호출마다 (시각, 바이트) 를 기억하며 첫 표본만 있으면 아직 한가하지 않음. 조회 실패 (null)·카운터 감소 (프로세스 교체) 는 바쁨. 표본 간격이 1 초 미만이면 1 초로 셈 (잔 읽기를 큰 비율로 보지 않게).
    private bool OtherGameIdle(GameSettings other)
    {
        long? bytes = _env.GameReadBytes(other);
        bool diskBusy = !SelfReadingOn(other) && (_env.DiskBytesPerSecond(other) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond); // 우리 읽기 중이면 처리량 판정을 하지 않음 (자기 I/O 를 외부 부하로 오인해 한가함 기록을 지우지 않게)
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (bytes is not { } total)
            {
                _readSample.Remove(other.Name);
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            if (!_readSample.TryGetValue(other.Name, out (DateTimeOffset At, long Bytes) prev))
            {
                _readSample[other.Name] = (now, total);
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            double secs = (now - prev.At).TotalSeconds;
            bool busy = diskBusy || total < prev.Bytes || (total - prev.Bytes) / Math.Max(secs, 1) >= IdleGameReadBytesPerSecond;
            if (busy || secs >= 1)
            {
                _readSample[other.Name] = (now, total);
            }

            if (busy)
            {
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            if (!_gameIdleSince.TryGetValue(other.Name, out DateTimeOffset since))
            {
                _gameIdleSince[other.Name] = since = prev.At;
            }

            return now - since >= IdleNeeded;
        }
    }

    // 재예열·보충 대상과 같은 디스크의 실행 중인 게임을 틱마다 표본으로 남김 (한가함 판정에 쓸 기록이 쌓이게), 안 도는 게임의 표본은 지움 (다음 실행의 카운터와 섞이지 않게).
    private void SampleDriveGames()
    {
        foreach (GameSettings g in AutoRelocator.SameDriveGames(_games, Warmed(), _env.SameDisk).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (_env.GameRunning(g))
            {
                OtherGameIdle(g);
                continue;
            }

            lock (_gate)
            {
                _readSample.Remove(g.Name);
                _gameIdleSince.Remove(g.Name);
            }
        }
    }

    // 읽기를 끊을 만큼 게임이 도는가. keep_stream_on_game_start 가 켜졌으면 읽던 게임 자신의 실행만 무시하고, 같은 디스크의 다른 등록 게임 (한가하지 않은 것) 은 설정과 무관하게 봅니다.
    private bool RunningBlocksRead(GameSettings game, Func<GameSettings, bool>? running = null) => !_auto.KeepStreamOnGameStart
        ? DriveHasRunningGame(game, running)
        : BusyOthers(game, running).Count > 0;

    // 건너뛴 사유 로그용: 이 디스크에서 게임이 돈다고 판정한 근거 프로세스 (없으면 빈 문자열). 건너뛰어 로그를 남길 때만 부름.
    private string BlockerNote(GameSettings game)
    {
        string blockers = string.Join("; ", AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Append(game).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(_env.DescribeBlockers).Where(b => b is not null));
        return blockers.Length == 0 ? string.Empty : $" [blocked by {blockers}]";
    }

    // 게임 시작으로 멈춘 스트림이면 양보 시간과 읽은 양을 로그에 남기고 표시를 지웁니다.
    private void LogYield(string streamGame, long bytes, DiskReadCoordinator.Lease? lease = null)
    {
        (string Game, long At)? y;
        lock (_gate)
        {
            (y, _yield) = (_yield, null);
        }

        if (y is { } v)
        {
            string activity = lease?.ReadActivityDetail() ?? "stage unknown, no issue timing available, request size unknown (metadata)";
            _log.Info($"{streamGame}: yielded to {v.Game} after {(long)Stopwatch.GetElapsedTime(v.At).TotalMilliseconds} ms, {bytes >> 20} MiB");
            _log.Info($"{streamGame}: yield activity; {activity}");
        }
    }

    /// 자동 모드 설정 (full_prewarm 등)을 바꿉니다. 시작 때와 설정을 다시 읽을 때 부름.
    public void SetAuto(AutoSettings auto) => _auto = auto;

    // 호출자가 쥔 옛 사본이 아니라 설정을 다시 읽은 최신 항목으로 정책을 봅니다 (목록에 없으면 받은 값).
    private GameSettings CurrentOf(GameSettings game) => _games.FirstOrDefault(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)) ?? game;

    /// 게임별 정책이 예열 계열 (예열·재예열·보충·시작 스트림·고정) 을 막는가. 전역 스위치·안티치트 게이트와는 따로 AND 로 봅니다.
    internal bool PolicyBlocksPrewarm(GameSettings game) => !GamePolicy.AllowsPrewarm(CurrentOf(game));

    // 막혔으면 true. 정책이 바뀔 때까지 같은 게임의 사유는 한 번만 로그합니다.
    private bool PolicyBlocked(GameSettings game, string what)
    {
        GameSettings current = CurrentOf(game);
        if (GamePolicy.AllowsPrewarm(current))
        {
            return false;
        }

        bool first;
        lock (_gate)
        {
            first = _policyLogged.Add(game.Name);
        }

        if (first)
        {
            _log.Info($"{game.Name}: {what} skipped (game policy: {PolicyText(current)})");
        }

        return true;
    }

    private static string PolicyText(GameSettings g) => g.Paused ? "paused" : GamePolicy.ModeName(g.Mode);

    /// 이 게임이 기록 없는 예열 대상인가: 기능이 켜져 있고 (자동 모드, full_prewarm 이나 engine_profiles) 원시 트레이스도 prefetch 기록도 없음.
    public bool IsCold(GameSettings game)
    {
        AutoSettings a = _auto;
        return _env.ColdPlanner is not null && a.Enabled && (a.FullPrewarm || a.EngineProfiles) && !_env.TraceExists(game) && !_env.RawTraceExists(game);
    }

    /// 재배치 직후 예열의 회복 기간 고정이 쓰는 드라이브 회복 판정. null 이면 그 고정은 꺼진 것과 같습니다.
    public IDriveRecovery? Recovery
    {
        get => _pins.Recovery;
        set => _pins.Recovery = value;
    }

    /// 지금 고정을 들고 있는 게임 이름.
    public IReadOnlyCollection<string> Pinned => _pins.Held;

    internal IReadOnlyList<GameSettings> Games => _games;

    /// 이 게임을 마지막으로 예열한 시각, 예열한 적이 없으면 null. 부팅 전에 한 예열은 원시·boot 트레이스가 있는 게임에만 돌려줍니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게).
    public DateTimeOffset? LastWarmed(string game)
    {
        DateTimeOffset? before;
        lock (_gate)
        {
            if (_last.TryGetValue(game, out DateTimeOffset at))
            {
                return at;
            }

            before = _lastBeforeBoot.TryGetValue(game, out DateTimeOffset prior) ? prior : null;
        }

        return before is { } t && _games.FirstOrDefault(g => string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase)) is { } gs && _env.RawTraceExists(gs) ? t : null;
    }

    /// 게임 시작 시 이미 메모리에 있는 마지막 예열 상태를 돌려줍니다. 데이터 파일이나 게임 디스크를 새로 읽지 않습니다.
    internal PlayWarmSnapshot? PlayStartSnapshot(string game, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (!_status.TryGetValue(game, out object? status) || status is not JObject entry)
            {
                return null;
            }

            string result = StatusString(entry, "result") ?? "unknown";
            string? reason = StatusString(entry, "reason");
            long? bytes = StatusLong(entry, "bytes");
            long? readMib = StatusLong(entry, "warmed_mib") ?? (bytes is { } read ? read >> 20 : null);
            long? targetMib = StatusLong(entry, "hot_mib");
            if (targetMib is null && result == "ok")
            {
                targetMib = readMib;
            }

            DateTimeOffset? completedAt = StatusString(entry, "at") is { } at
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
            double? secondsAgo = completedAt is { } then && startedAt >= then ? (startedAt - then).TotalSeconds : null;
            return new PlayWarmSnapshot(StatusString(entry, "mode") ?? "hot set", result, reason, readMib, targetMib, secondsAgo);
        }
    }

    private static string? StatusString(JObject entry, string key) =>
        entry.TryGetValue(key, out object? value) ? value as string : null;

    private static long? StatusLong(JObject entry, string key) =>
        entry.TryGetValue(key, out object? value) ? value switch { long number => number, int number => number, _ => null } : null;

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운·상태는 유지). pin 이 꺼졌거나 빠진 게임의 고정은 풉니다.
    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        IReadOnlyList<GameSettings> before = _games;
        _games = games;
        HashSet<string> live = new(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (GameSettings gone in before.Where(g => !live.Contains(g.Name)))
            {
                _removedGames.Add(gone.Name);
            }

            _removedGames.RemoveWhere(live.Contains);
            foreach (string stale in _resume.Keys.Concat(_supplementRetry.Keys).Concat(_supplementReadProgress.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                GameSettings? now = games.FirstOrDefault(g => string.Equals(g.Name, stale, StringComparison.OrdinalIgnoreCase));
                GameSettings? was = before.FirstOrDefault(g => string.Equals(g.Name, stale, StringComparison.OrdinalIgnoreCase));
                bool changed = now is not null && was != now;
                bool removed = now is null;
                if (changed || removed && !_activeWork.ContainsKey(stale) && !_relocStarted.Contains(stale))
                {
                    _resume.Remove(stale); // 빠진 게임, 또는 설정 (읽을 목록) 이 바뀐 게임의 이어 읽기 위치는 낡음
                    _supplementRetry.Remove(stale);
                    _supplementReadProgress.Remove(stale);
                }
            }

            foreach (string gone in _pendingWarm.Keys.Where(k => !live.Contains(k) && !_activeWork.ContainsKey(k)).ToList())
            {
                _pendingWarm.Remove(gone);
                _idleSince.Remove(gone);
                _pendingWarmBudgetSkipped.Remove(gone);
            }

            foreach (GameSettings g in games)
            {
                if (GamePolicy.AllowsPrewarm(g))
                {
                    _policyLogged.Remove(g.Name); // 다시 허용되면 부분 상태·마지막 예열 이력은 그대로라 다음 점검부터 평소처럼 이어짐
                    continue;
                }

                // 예열이 금지된 게임의 대기 상태 (대기 예열, 보충 재시도, 이어 읽기 위치) 는 낡은 채 엉뚱한 읽기를 하지 않게 정리. 이력 (_last·_partial·상태 파일) 은 보존.
                _pendingWarm.Remove(g.Name);
                _idleSince.Remove(g.Name);
                _pendingWarmBudgetSkipped.Remove(g.Name);
                _resume.Remove(g.Name);
                _supplementRetry.Remove(g.Name);
                _supplementReadProgress.Remove(g.Name);
                _supplementNothingNew.Remove(g.Name);
                if (_policyLogged.Add(g.Name))
                {
                    _log.Info($"{g.Name}: prewarm, supplement and pin stopped (game policy: {PolicyText(g)})");
                }
            }

            foreach (string gone in _removedGames.ToList())
            {
                PruneRemovedGameLocked(gone);
            }
        }

        _rewarm.SetGames(games);
        _pins.Reconcile(games);
        bool budgetWaiting;
        lock (_gate)
        {
            budgetWaiting = _pendingWarmBudgetSkipped.Count > 0;
        }

        if (budgetWaiting)
        {
            WarmBudget(); // 목록·예산이 바뀌었으면 예산으로 미뤄 둔 대기 게임의 배정 회복을 바로 판정
        }
    }

    /// 한 게임의 고정을 풉니다 (그 게임 exe 가 뜰 때, 스트림보다 먼저).
    public void ReleasePin(string game, string why) => _pins.Release(game, why);

    /// 모든 고정을 풉니다 (런처가 뜰 때). 회복 기간 고정은 남깁니다 (런처 업데이트는 파일 변경 해제로 막음).
    public void ReleasePins(string why) => _pins.ReleaseAll(why, keepRecovery: true);

    public void Shutdown() => _pins.ReleaseAll("service stopping");

    /// 주기 점검 (재적용 틱마다): 가용 RAM 이 모자라면 고정 해제, 예열 유지 주기가 되면 식은 게임을 다시 예열. ct 가 취소되면 (게임 시작) 시작하지 않은 예열은 하지 않습니다.
    public void Tick(AutoSettings auto, CancellationToken ct = default)
    {
        _pins.CheckMemory(_games);
        _pins.CheckRecovery();
        SampleDriveGames();
        SupplementTick(ct);
        _rewarm.Tick(auto, ct);
    }

    internal DateTimeOffset? PendingRetryAt(string game)
    {
        lock (_gate)
        {
            return _pendingWarm.TryGetValue(game, out PendingWarm? pending) ? pending.RetryAt : null;
        }
    }

    internal bool HasPendingWarm(string game)
    {
        lock (_gate)
        {
            return _pendingWarm.ContainsKey(game);
        }
    }

    private TimeSpan PendingWarmRetryDelay() => _auto.RewarmMinutes > 0
        ? TimeSpan.FromMinutes(_auto.RewarmMinutes)
        : AutoRelocator.RetryEvery;

    private static bool IsPermanentWarmRefusal(string? reason) => reason is "no plan file" or "no zone" or "no trace file" or "outside allowed roots" or GamePolicySkip
        || reason?.StartsWith("failed:", StringComparison.Ordinal) == true;

    private static bool IsRetryableWarmOutcome(string? reason) => reason is Cancelled or RelocationRunning or "startup byte cap"
        or "batch memory budget" or "game is running" or "drive busy" or "nothing read" or PostUpdateDelay
        || reason?.StartsWith("low memory", StringComparison.Ordinal) == true;

    private void TrackPendingWarm(GameSettings game, PrewarmResult result, DateTimeOffset at)
    {
        bool alreadyPending;
        lock (_gate)
        {
            alreadyPending = _pendingWarm.ContainsKey(game.Name);
        }

        if (result.Ran)
        {
            lock (_gate)
            {
                _pendingWarm.Remove(game.Name);
                _idleSince.Remove(game.Name);
                _pendingWarmBudgetSkipped.Remove(game.Name);
            }

            return;
        }

        string? reason = result.SkipReason;
        if (IsCold(game) || IsPermanentWarmRefusal(reason)) // 기록 없는 게임은 대기 재시도 대상이 아니고 런처 트리거·서비스 시작 때만 예열함
        {
            lock (_gate)
            {
                _pendingWarm.Remove(game.Name);
                _idleSince.Remove(game.Name);
                _pendingWarmBudgetSkipped.Remove(game.Name);
            }

            return;
        }

        if (!alreadyPending && (result.CancelDetail == "game started"
            || reason == "game is running" && _env.GameRunning(game)
            || reason == Cancelled && (_env.GameRunning(game) || _coordinator?.IsRegistered(game.Name) == true)
            || result.CancelDetail == "disk read lease refused" && _coordinator?.IsRegistered(game.Name) == true))
        {
            return; // 그 게임 자신이 실행 중일 때의 거절은 게임 종료 경로에 맡김
        }

        if (!alreadyPending && !IsRetryableWarmOutcome(reason))
        {
            return;
        }

        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game.Name, out PendingWarm? current) && (current.RetryAt > at || current.TriggerExe is not null))
            {
                return; // 재시도 기한 전에 다른 경로가 실패해도 기한을 뒤로 밀지 않음, 당겨 둔 트리거 재시도 표시도 지우지 않음
            }

            _pendingWarm[game.Name] = new(at + PendingWarmRetryDelay(), reason ?? "unsuccessful warm");
            _pendingWarmBudgetSkipped.Remove(game.Name); // 다른 사유로 새로 잡힌 기한은 예산 회복으로 앞당기지 않음
        }
    }

    /// 게임이 끝났을 때 (Worker 가 부름): 재예열 점검을 앞당깁니다.
    public void NoteGameExit(DateTimeOffset? heavyWorkNotBefore = null) => _rewarm.PullIn(_auto, heavyWorkNotBefore);

    /// 예열에 성공한 적이 있는 게임 (최근 순, 설정에 아직 있는 것만, 게임 정책이 예열을 막은 게임은 제외).
    internal IReadOnlyList<GameSettings> Warmed()
    {
        lock (_gate)
        {
            return [.. _games.Where(g => _last.ContainsKey(g.Name) && HasPlayed(g.Name) && GamePolicy.AllowsPrewarm(g)).OrderByDescending(g => _last[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal)];
        }
    }

    /// 재예열·보충 예열 대상이 게임들 합계로 캐시 예산 (기본 물리 RAM/2, prewarm.max_cache_mib) 을 넘지 않게 합니다. 관찰 범위를 우선 배정하고 미배정량은 기존 순위대로 나눕니다.
    /// 상태를 저장하지 않으므로 순위가 바뀌면 다음 점검에서 대상도 바뀝니다. 순위가 없으면 null (모두 대상).
    /// AllocMib: 들어간 게임이 셈에 쓴 양 (MiB): 재예열은 이만큼까지만 읽어야 계상과 실제 읽기가 같습니다.
    internal (HashSet<string> In, long CapMib, long UsedMib, Dictionary<string, long> AllocMib)? WarmBudget()
    {
        Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? rank = WarmRank;
        bool hasPending;
        lock (_gate)
        {
            hasPending = _pendingWarm.Count > 0;
        }

        if (rank is null && !hasPending)
        {
            return null;
        }

        long cap = PhysCapMib();
        List<GameSettings> candidates = [.. Warmed()];
        lock (_gate)
        {
            candidates.AddRange(_games.Where(g => _partial.ContainsKey(g.Name) && GamePolicy.AllowsPrewarm(g) && !candidates.Any(c => string.Equals(c.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
            candidates.AddRange(_games.Where(g => _pendingWarm.ContainsKey(g.Name) && GamePolicy.AllowsPrewarm(g) && !candidates.Any(c => string.Equals(c.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        }

        HashSet<string> names = [.. candidates.Select(g => g.Name)];
        var inBudget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alloc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long used = 0;
        string? triggered;
        lock (_gate)
        {
            triggered = _triggered is { } t && _now() - t.At < TriggerPriorityWindow ? t.Game : null;
        }

        // 방금 트리거로 켠 게임은 관찰·보충 두 단계 모두에서 먼저 배정받고, 같은 단계의 나머지는 기존 순위를 유지합니다.
        List<GameSettings> ordered = [.. (rank?.Invoke(candidates) ?? candidates).Where(g => names.Contains(g.Name)).Concat(candidates)
            .DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Name, triggered, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        var totalMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in ordered)
        {
            long total = ZoneMib(g);
            bool hasZoneList = total > 0;
            long coldHeld;
            bool pending;
            lock (_gate)
            {
                coldHeld = _coldMib.GetValueOrDefault(g.Name);
                pending = _pendingWarm.ContainsKey(g.Name);
            }

            if (total <= 0)
            {
                total = coldHeld; // 기록 없이 예열한 게임은 올려 둔 양
            }

            bool unknownPendingSize = pending && coldHeld <= 0 && total <= 0;
            if (unknownPendingSize)
            {
                total = cap;
            }

            total = Math.Min(total, cap);
            long observed = unknownPendingSize ? 0 : total <= 0 || !hasZoneList
                ? total
                : ObservedMib(g, total, allowSourceRead: !DriveHasRunningGame(g)); // 같은 디스크에서 게임이 실행 중이면 출처를 읽지 않고, 출처를 모르면 전부 관찰된 것으로 셈
            totalMib[g.Name] = total;
            observedMib[g.Name] = observed;
        }

        // 1단계: 관찰 범위는 예전과 같이 게임 단위로 통째 들어갈 때만 (순위 순). 2단계: 남은 예산을 순위대로 보충 범위에 나눔 (들어가지 못한 게임의 보충 범위는 읽지 않음).
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in ordered)
        {
            long observed = observedMib[g.Name];
            if (used + observed <= cap)
            {
                if (observed > 0 || totalMib[g.Name] <= 0) // 크기를 모르는 게임은 예전처럼 대상 (배정량 0 = 제한 없음)
                {
                    inBudget.Add(g.Name);
                    alloc[g.Name] = observed;
                    used += observed;
                }
            }
            else
            {
                rejected.Add(g.Name);
            }
        }

        foreach (GameSettings g in ordered.Where(g => !rejected.Contains(g.Name)))
        {
            long take = Math.Min(totalMib[g.Name] - observedMib[g.Name], Math.Max(0, cap - used));
            if (take <= 0)
            {
                continue;
            }

            inBudget.Add(g.Name);
            alloc[g.Name] = alloc.GetValueOrDefault(g.Name) + take;
            used += take;
        }

        // 3단계: 두 단계에서 배정되지 않은 관찰 범위도 남은 예산으로 최소 부분량을 받습니다.
        foreach (GameSettings g in ordered.Where(g => rejected.Contains(g.Name)))
        {
            long take = Math.Min(Math.Max(0, totalMib[g.Name] - alloc.GetValueOrDefault(g.Name)), Math.Max(0, cap - used));
            if (take < MinPartialMib)
            {
                continue;
            }

            inBudget.Add(g.Name);
            alloc[g.Name] = alloc.GetValueOrDefault(g.Name) + take;
            used += take;
        }

        lock (_gate)
        {
            DateTimeOffset now = _now();
            foreach (string recovered in _pendingWarmBudgetSkipped.Keys.Where(name => alloc.GetValueOrDefault(name) > 0).ToList())
            {
                _pendingWarmBudgetSkipped.Remove(recovered);
                if (_pendingWarm.TryGetValue(recovered, out PendingWarm? pending) && pending.RetryAt > now)
                {
                    _pendingWarm[recovered] = pending with { RetryAt = now }; // 배정이 회복되면 대기 기한을 앞당김
                }
            }
        }

        return (inBudget, cap, used, alloc);
    }

    internal static string WarmBudgetSkipDetail(long capMib, long usedMib) =>
        $"the cache budget of {capMib} MiB has {usedMib} MiB assigned to warm targets; no allocation of at least {MinPartialMib} MiB fits this game";

    /// 기록 없이 예열한 게임의 재예열 점검 표본 (그 게임이 지금도 기록 없는 예열 대상이고 예열한 적이 있을 때만), 아니면 null.
    internal IReadOnlyList<(string Path, long Offset, int Bytes)>? ColdSamples(GameSettings game)
    {
        lock (_gate)
        {
            return IsCold(game) && _coldSamples.TryGetValue(game.Name, out IReadOnlyList<(string, long, int)>? s) ? s : null;
        }
    }

    /// 게임 시작 때 핫 집합이 RAM 에 있는 비율 (%): 표본 몇 곳 (CacheProbe.PickSamples 규모) 을 읽어 느리지 않은 곳의 비율. 잴 표본이 없으면 null.
    public int? HitPercent(GameSettings game)
    {
        ProbeResult? p = ColdSamples(game) is { } samples ? ProbeSamplesForGame(game, samples, "hit percent") : ProbeReadMsForGame(game, "hit percent");
        return p is { Samples: > 0 } ? (p.Samples - p.Slow) * 100 / p.Samples : null;
    }

    // roomMib: WarmBudget 가 이 게임에 배정한 양 (핫 집합이 더 크면 앞쪽 이만큼만 읽음: 일반 부분 예열 경로).
    internal PrewarmResult RewarmGame(GameSettings game, CancellationToken ct = default, long? roomMib = null) => Guarded(game, "rewarm", pin: true, ignoreCooldown: true, ct, roomMib: roomMib);

    internal bool RewarmAlreadyCovered(GameSettings game)
    {
        if (string.Equals(_streaming, game.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_coldReading, game.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        DateTimeOffset now = _now();
        return LastWarmed(game.Name) is { } warmedAt && now >= warmedAt && now - warmedAt < RecentWarmupWindow;
    }

    /// 이 프로세스가 어느 게임의 trigger_exes 에 들어 있는가 (대소문자 무시).
    public bool IsTrigger(string exeName) => _games.Any(g => IsTrigger(g, exeName));

    public static bool IsTrigger(GameSettings g, string exeName) => g.Prewarm.TriggerExes.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// 로그용 예열 원인: "trigger X.exe" → "X.exe", "auto cold start" → "cold start", 그 밖은 앞의 "auto " 만 뗌.
    public static string TriggerCause(string why) => why.StartsWith("trigger ", StringComparison.Ordinal) ? why["trigger ".Length..] : why.StartsWith("auto ", StringComparison.Ordinal) ? why["auto ".Length..] : why;

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로 (started 를 주면 그 조건을 만족하는 게임만: 자동 모드의 플레이한 게임).
    /// recent (최근순) 를 주면 한 묶음으로 합칩니다: on_service_start 게임 먼저, 그다음 최근순 (중복 제거) — RAM 예산을 한 번만 씁니다.
    /// 부팅 BootDelay 뒤 + 게임 드라이브 IdleNeeded 유휴까지 기다린 다음 (그동안 ct 가 취소되면 아무것도 하지 않음), 핫 집합이 이미 파일 캐시에 있는 게임은 읽지 않고 건너뜁니다.
    /// lastPlayed (게임 이름 → 마지막 플레이) 를 주면 (자동 모드가 켜진 경우) 대상을 최근 <see cref="StartupPlayedWithin"/> 안에 플레이한 게임으로 좁혀 마지막 플레이 순 상위 auto.startup_warm_games 개만 남기고
    /// (on_service_start 게임도 같은 순위), 합계 읽기량이 auto.startup_warm_max_mib 를 넘는 부분은 읽지 않습니다 (넘는 게임은 앞부분만, 이미 다 쓴 뒤의 게임은 건너뜀). 합계 상한은 기록이 없는 게임 (통째 예열) 도 포함해 항상 적용합니다.
    public IReadOnlyList<PrewarmResult> RunServiceStart(IEnumerable<GameSettings>? recent = null, CancellationToken ct = default, Func<GameSettings, bool>? started = null,
        Func<string, DateTimeOffset?>? lastPlayed = null)
    {
        AutoSettings a = _auto;
        GameSettings[] first = _games.Where(g => g.Prewarm.OnServiceStart && (started?.Invoke(g) ?? true)).ToArray();
        IEnumerable<GameSettings> merged = first.Concat((recent ?? []).Where(g => !first.Any(f => string.Equals(f.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        merged = [.. merged.Where(g => !PolicyBlocked(g, "service-start prewarm"))]; // 정책이 예열을 막은 게임은 시작 예열 순위·개수·상한을 쓰지 않음
        if (lastPlayed is not null)
        {
            DateTimeOffset cutoff = _now() - StartupPlayedWithin;
            merged = merged.Select(g => (Game: g, At: lastPlayed(g.Name))).Where(x => x.At is { } at && at >= cutoff)
                .OrderByDescending(x => x.At).ThenBy(x => x.Game.Name, StringComparer.Ordinal).Take(a.StartupWarmGames).Select(x => x.Game);
        }

        GameSettings[] list = [.. merged];
        if (list.Length == 0 || !WaitStartupGate(list, ct))
        {
            return [];
        }

        var skipped = new List<PrewarmResult>();
        var cold = new List<GameSettings>();
        var trimmed = new Dictionary<string, (long WarmedMib, long HotMib)>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<GameSettings>();
        long availNow = _env.AvailableRamMib();
        foreach (GameSettings g in list)
        {
            if (AlreadyWarm(g) is true)
            {
                LogSkipped(g.Name, "already warm", $"{g.Name}: prewarm skipped (already warm) [service start]");
                skipped.Add(new PrewarmResult(g.Name, false, "already warm", null, _now()));
            }
            else if (_env.ZoneBytes(g) is not null && !IsCold(g) && WillSkipReason(g, availNow) is { } why)
            {
                skipped.Add(SkipAtServiceStart(g, why)); // 쿨다운 같은 사유로 어차피 건너뜀: 시작 상한을 배정하지 않고, 다른 게임을 읽는 사이 사유가 풀려도 상한 없이 읽지 않게 여기서 결과를 확정
            }
            else
            {
                candidates.Add(g);
            }
        }

        // 시작 상한도 출처별 두 단계로 나눕니다: 모든 게임의 관찰분을 먼저 (입력 순서), 남은 상한을 보충분에 씁니다. 기록 없는 게임은 전부 첫 단계에서 순서대로 셉니다.
        var totalMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in candidates)
        {
            long? zone = _env.ZoneBytes(g);
            long mib = ((zone ?? (long)DefaultFullPrewarmMib << 20) + (1 << 20) - 1) >> 20; // 기록이 없으면 통째 예열의 기본 상한만큼으로 셈
            totalMib[g.Name] = mib;
            observedMib[g.Name] = zone is null || IsCold(g) ? mib : ObservedMib(g, mib, allowSourceRead: !DriveHasRunningGame(g));
        }

        var startupAlloc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long plannedMib = 0;
        foreach (bool observedPhase in new[] { true, false })
        {
            foreach (GameSettings g in candidates)
            {
                long portion = observedPhase ? observedMib[g.Name] : totalMib[g.Name] - observedMib[g.Name];
                long remaining = Math.Max(0, a.StartupWarmMaxMib - plannedMib);
                long take = _env.ZoneBytes(g) is null && portion > remaining ? 0 : Math.Min(portion, remaining); // 기록 없는 게임은 통째로 들어갈 때만 (건너뛰면서 상한을 쓰지 않음)
                startupAlloc[g.Name] = startupAlloc.GetValueOrDefault(g.Name) + take;
                plannedMib += take;
            }
        }

        foreach (GameSettings g in candidates)
        {
            long? zone = _env.ZoneBytes(g);
            long mib = totalMib[g.Name];
            long allocation = startupAlloc[g.Name];
            if (allocation <= 0 && (mib > 0 || plannedMib >= a.StartupWarmMaxMib) || (zone is null && allocation < mib))
            {
                LogSkipped(g.Name, "startup byte cap", $"{g.Name}: prewarm skipped (the service-start cap of {a.StartupWarmMaxMib} MiB is used up) [service start]");
                PrewarmResult result = new(g.Name, false, "startup byte cap", null, _now());
                TrackPendingWarm(g, result, result.At);
                skipped.Add(result);
            }
            else if (zone is not null && allocation < mib)
            {
                // 상한을 넘는 만큼은 읽지 않음: 핫 집합의 앞부분만 (max_stream_mib 를 남은 양으로 줄인 사본)
                trimmed[g.Name] = (allocation, mib);
                cold.Add(g with { MaxStreamMib = (int)Math.Min(allocation, int.MaxValue), MaxStreamSet = true });
            }
            else
            {
                cold.Add(g);
            }
        }

        IReadOnlyList<PrewarmResult> ran;
        lock (_gate)
        {
            foreach (KeyValuePair<string, (long, long)> t in trimmed)
            {
                _startupTrim[t.Key] = t.Value;
            }
        }

        try
        {
            ran = RunBatch(cold, "service start", ct: ct, yieldToRelocation: true);
        }
        finally
        {
            lock (_gate)
            {
                _startupTrim.Clear();
            }
        }

        foreach (PrewarmResult r in ran.Where(r => r is { Ran: true, Mode: null }))
        {
            if (trimmed.TryGetValue(r.Game, out (long WarmedMib, long HotMib) t))
            {
                if (list.FirstOrDefault(g => string.Equals(g.Name, r.Game, StringComparison.OrdinalIgnoreCase)) is { } game)
                {
                    long warmedMib = ActualMib(r.Stats, t.WarmedMib);
                    NotePartial(game, warmedMib, t.HotMib, r.Stats?.Bytes ?? MibBytes(warmedMib)); // 상한으로 앞부분만 읽음 (실제 읽은 양): 유휴 때 나머지를 보충 (상태 파일에는 Record 가 이미 남김)
                }
            }
        }

        return [.. skipped, .. ran];
    }

    /// 시작 예열이 상한으로 일부만 읽은 게임이 남아 있는가 (재예열 점검이 "표본이 따뜻함" 으로 건너뛰지 않게).
    public bool SupplementPending(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game);
        }
    }

    private void NotePartial(GameSettings game, long warmedMib, long hotMib, long bytes)
    {
        string? inputs = PrewarmRun.InputStamp(game);
        lock (_gate)
        {
            _supplementNothingNew.Remove(game.Name);
            if (_memoryPartial.ContainsKey(game.Name))
            {
                return; // 메모리 상한이 더 작게 읽어 이미 실제 읽은 양으로 기록함
            }

            _partial[game.Name] = (warmedMib, hotMib);
            _resume.Remove(game.Name); // 부분 상태를 새로 정함: 옛 이어 읽기 위치는 낡음
            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            _supplementRetry.Remove(game.Name);
        }

        _log.Info($"{game.Name}: startup prewarm read {warmedMib} of {hotMib} MiB, the rest is read when the drive is idle");
    }

    // 나머지까지 읽어 부분 예열이 끝남: 메모리 상태와 상태 파일의 부분 표지를 함께 지웁니다.
    private void ClearPartial(string game, long hotMib)
    {
        lock (_gate)
        {
            _partial.Remove(game);
            _memoryPartial.Remove(game);
            _memoryRunning.Remove(game);
            _resume.Remove(game);
            _supplementNothingNew.Remove(game);
            _supplementReadProgress.Remove(game);
            _supplementRetry.Remove(game);
            _memoryStopAt.Remove(game);
        }

        PatchStatus(game, hotMib, hotMib);
    }

    private void DropPendingWarm(string game)
    {
        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending) && pending.TriggerExe is not null)
            {
                return; // 당겨 둔 트리거 재시도 표시는 보충 경로가 지우지 않음 (RetryPendingWarmTick 이 먼저 처리)
            }

            _pendingWarm.Remove(game);
            _idleSince.Remove(game);
            _pendingWarmBudgetSkipped.Remove(game);
        }
    }

    private void CompletePendingWarmSupplement(GameSettings game, StreamStats stats)
    {
        DateTimeOffset at = _now();
        lock (_gate)
        {
            if (!_pendingWarm.ContainsKey(game.Name))
            {
                return;
            }

            _last[game.Name] = at;
        }

        double mbps = stats.Seconds > 0 ? stats.Bytes / 1048576.0 / stats.Seconds : 0;
        Record(game, at, new PrewarmResult(game.Name, true, null, stats, at), mbps);
    }

    private long SupplementProgressBytes(GameSettings game, (long WarmedMib, long HotMib) partial, string? inputs)
    {
        lock (_gate)
        {
            if (_supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? progress)
                && progress.Settings == game && string.Equals(progress.Inputs, inputs, StringComparison.Ordinal))
            {
                return progress.Bytes;
            }

            _supplementReadProgress.Remove(game.Name);
            _supplementRetry.Remove(game.Name);
            long bytes = MibBytes(partial.WarmedMib);
            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            return bytes;
        }
    }

    private bool SupplementDeferred(GameSettings game, long capMib, long hotMib, long progressBytes, string? inputs)
    {
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (!_supplementRetry.TryGetValue(game.Name, out SupplementRetry? retry))
            {
                return false;
            }

            if (retry.Settings != game || retry.ProgressBytes != progressBytes || !string.Equals(retry.Inputs, inputs, StringComparison.Ordinal))
            {
                _supplementRetry.Remove(game.Name);
                return false;
            }

            if (now < retry.RetryAt)
            {
                bool memoryGrew = capMib >= retry.CapMib + MinPartialMib;
                bool fullPathBecameAvailable = retry.CapMib < hotMib && capMib >= hotMib;
                if (retry.EarlyRetryUsed || (!memoryGrew && !fullPathBecameAvailable))
                {
                    return true;
                }

                _supplementRetry[game.Name] = retry with { CapMib = capMib, EarlyRetryUsed = true };
                return false;
            }

            _supplementRetry[game.Name] = retry with { RetryAt = now + PendingWarmRetryDelay(), CapMib = capMib, EarlyRetryUsed = false };
            return false;
        }
    }

    private void DeferSupplement(GameSettings game, long capMib, long progressBytes, string? inputs, StreamStats stats, bool planEnded, long planBytes, string? reason = null)
    {
        DateTimeOffset now = _now();
        DateTimeOffset retryAt = now + PendingWarmRetryDelay();
        bool earlyRetryUsed = false;
        bool log;
        lock (_gate)
        {
            if (_supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? progress)
                && progress.Settings == game && string.Equals(progress.Inputs, inputs, StringComparison.Ordinal))
            {
                progressBytes = Math.Max(progressBytes, progress.Bytes);
            }
            else if (_partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) partial))
            {
                progressBytes = Math.Max(progressBytes, MibBytes(partial.WarmedMib));
            }

            bool sameProgress = _supplementRetry.TryGetValue(game.Name, out SupplementRetry? previous)
                && previous.Settings == game && previous.ProgressBytes == progressBytes
                && string.Equals(previous.Inputs, inputs, StringComparison.Ordinal);
            bool same = sameProgress && previous!.PlanExhausted == planEnded;
            if (sameProgress && previous is { } current && now < current.RetryAt)
            {
                retryAt = current.RetryAt;
                earlyRetryUsed = current.EarlyRetryUsed;
            }

            log = !same || previous?.Logged != true;
            _supplementRetry[game.Name] = new(retryAt, capMib, progressBytes, inputs, game, planEnded, earlyRetryUsed, true);
        }

        if (!log)
        {
            return;
        }

        if (reason is not null)
        {
            _log.Info($"{game.Name}: supplement prewarm deferred ({reason}; retry after {retryAt:O})");
        }
        else if (planEnded)
        {
            _log.Info($"{game.Name}: supplement prewarm deferred (read list ended at {MibText(planBytes)} of {capMib} MiB planned, {stats.Errors} errors; retry after {retryAt:O})");
        }
        else
        {
            _log.Info($"{game.Name}: supplement prewarm deferred (no progress: {MibText(stats.Bytes)} accepted of {MibText(stats.RequestedBytes)} requested, {stats.Errors} errors; retry after {retryAt:O})");
        }
    }

    private static bool SupplementPlanEnded(long? listBytes, StreamStats stats, long skipBytes, long capMib)
    {
        if (stats.Errors != 0 || listBytes is null && stats.RequestedBytes <= 0)
        {
            return false;
        }

        long bytes = listBytes ?? stats.RequestedBytes;
        long total = bytes > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + bytes;
        return total < MibBytes(capMib);
    }

    private static bool SupplementFullyCovered(StreamStats stats, long skipBytes, long capMib, long? listBytes = null, bool fullReadPath = false)
    {
        if (stats.Errors != 0)
        {
            return false;
        }

        if (fullReadPath)
        {
            long fullList = listBytes ?? stats.RequestedBytes;
            if (fullList <= 0)
            {
                fullList = stats.Bytes;
            }

            return fullList > 0;
        }

        long requestedTail = listBytes ?? stats.RequestedBytes;
        if (listBytes is null && stats.RequestedBytes <= 0)
        {
            requestedTail = stats.Bytes; // 구형 스트리머 통계에 요청량이 없으면 수락량으로만 완료를 확인합니다.
        }

        long requested = requestedTail > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + requestedTail;
        long accepted = stats.Bytes > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + stats.Bytes;
        long target = MibBytes(capMib);
        return requested >= target && accepted >= target;
    }

    private static long MibBytes(long mib) => mib <= 0 ? 0 : mib > long.MaxValue >> 20 ? long.MaxValue : mib << 20;

    private static string MibText(long bytes) => $"{bytes / 1048576.0:F2} MiB";

    // 보충이 끊기거나 앞부분만 읽고 끝났을 때 읽은 양을 기억합니다 (줄어들지 않음): 상태 표시와 다음 보충의 시작점. 보충 대기 중인 게임만.
    private void NoteProgress(GameSettings game, string? inputs, long warmedMib, long hotMib, long bytes)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) cur))
            {
                return;
            }

            long previousBytes = _supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? previous)
                && previous.Settings == game && string.Equals(previous.Inputs, inputs, StringComparison.Ordinal)
                ? previous.Bytes
                : MibBytes(cur.WarmedMib);
            if (bytes <= previousBytes)
            {
                return;
            }

            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            _supplementRetry.Remove(game.Name);
            _supplementNothingNew.Remove(game.Name);
            if (warmedMib > cur.WarmedMib)
            {
                _partial[game.Name] = (warmedMib, hotMib);
                if (_memoryPartial.ContainsKey(game.Name))
                {
                    _memoryPartial[game.Name] = (warmedMib, hotMib); // 재예열 점검은 읽은 앞부분에서만 표본을 고름
                }

                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        lock (_run)
        {
            lock (_gate)
            {
                if (_status.TryGetValue(game.Name, out object? entry) && entry is JObject o)
                {
                    o["warmed_mib"] = warmedMib;
                    o["hot_mib"] = hotMib;
                }
            }

            FlushStatus();
        }
    }

    // 이미 읽은 앞부분 (warmedMib) 의 표본이 식었는가: 식었으면 보충이 앞부분부터 다시 읽음. 읽지 못하면 식지 않은 것으로 봄.
    private bool FrontCold(GameSettings game, long warmedMib, CancellationToken ct, out bool refused)
    {
        refused = false;
        try
        {
            GameSettings front = game with { MaxStreamMib = (int)Math.Min(warmedMib, int.MaxValue), MaxStreamSet = true };
            return GuardedProbe(game, "supplement front sample", ct, () => (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(front),
                (_env.ProbePartialReadMsChecked ?? _env.ProbeReadMsChecked) is { } checkedProbe ? canIssue => checkedProbe(front, canIssue) : null, out refused) is { Cold: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return false;
        }
    }

    // 끊긴 보충을 이어 읽을 지점: 기억한 윈도가 지금 예산 (cap) 안이면 그 윈도를 끝내고 (읽을 목록이 같아야 앞 바이트를 건너뛸 수 있음), 그 앞부분 표본이 따뜻할 때만 건너뜀.
    // 식었거나 확인할 수 없거나 윈도가 예산을 넘으면 기억을 버리고 cap 으로 처음부터. full: 전체 핫 집합이 들어가는 경로라 cap (전체 크기) 와 같은 윈도의 기억만 씀.
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 두고 호출자가 조용히 멈춤).
    private (long WindowMib, long SkipBytes) ResumePoint(GameSettings game, long cap, CancellationToken ct, out bool refused, bool full = false)
    {
        refused = false;
        (long WindowMib, long Bytes) r;
        lock (_gate)
        {
            if (!_resume.TryGetValue(game.Name, out r))
            {
                return (cap, 0);
            }
        }

        ProbeResult? probe = null;
        if ((full ? r.WindowMib == cap : r.WindowMib <= cap) && _env.ProbeDone is { } probeDone)
        {
            try
            {
                GameSettings window = game with { MaxStreamMib = (int)r.WindowMib, MaxStreamSet = true };
                probe = GuardedProbe(game, "supplement resume sample", ct, () => probeDone(window, r.Bytes),
                    _env.ProbeDoneChecked is { } checkedDone ? canIssue => checkedDone(window, r.Bytes, canIssue) : null, out refused);
                if (refused)
                {
                    return (cap, 0);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                // 읽지 못하면 이어 읽지 않고 처음부터
            }
        }

        if (probe is { Samples: > 0, Cold: false })
        {
            return r;
        }

        lock (_gate)
        {
            _resume.Remove(game.Name);
        }

        return (cap, 0);
    }

    // 상태 파일의 그 게임 항목에 warmed_mib/hot_mib 를 남깁니다 (항목이 없으면 아무것도 안 함).
    private void PatchStatus(string game, long warmedMib, long hotMib)
    {
        lock (_run)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_status.TryGetValue(game, out object? entry) && entry is JObject o)
                {
                    o["warmed_mib"] = warmedMib;
                    o["hot_mib"] = hotMib;
                    o["partial"] = false; // 나머지까지 읽음
                    changed = true;
                }
            }

            if (changed)
            {
                FlushStatus();
            }
        }
    }

    // 보충 예열 대기 중인 게임이 IdleNeeded 동안 한가했으면 남은 구간을 읽습니다 (게임 미실행·재배치 아님·같은 디스크 게임 미실행·디스크 처리량 낮음).
    private void SupplementTick(CancellationToken ct)
    {
        string[] pending;
        lock (_gate)
        {
            pending = [.. _partial.Keys];
        }

        HashSet<string> pendingPartial = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in pending)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } g)
            {
                lock (_gate)
                {
                    _partial.Remove(name);
                    _idleSince.Remove(name);
                    _resume.Remove(name);
                    _supplementNothingNew.Remove(name);
                    _supplementReadProgress.Remove(name);
                    _supplementRetry.Remove(name);
                }

                continue;
            }

            if (!HeavyWorkAllowed(g) || PolicyBlocksPrewarm(g))
            {
                continue;
            }

            if (HasPendingWarm(name))
            {
                pendingPartial.Add(name);
            }

            SupplementWhenIdle(g, ct);
        }

        RetryPendingWarmTick(ct, pendingPartial);
    }

    private void RetryPendingWarmTick(CancellationToken ct, IReadOnlySet<string>? pendingPartial = null)
    {
        string[] pending;
        DateTimeOffset now = _now();
        lock (_gate)
        {
            pending = [.. _pendingWarm.Where(pair => pair.Value.RetryAt <= now).Select(pair => pair.Key)];
        }

        foreach (string name in pending)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            GameSettings? game = _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (game is null || IsCold(game) || !GamePolicy.AllowsPrewarm(game)) // 기록이 없어진 게임, 정책이 예열을 막은 게임은 대기에서 뺌 (기록 없는 게임은 대기 재시도 대상이 아님)
            {
                lock (_gate)
                {
                    _pendingWarm.Remove(name);
                    _idleSince.Remove(name);
                    _pendingWarmBudgetSkipped.Remove(name);
                }

                continue;
            }

            if (TryRunTriggerRetry(game, ct))
            {
                continue; // 막던 게임이 사라지면 부분 상태 게임도 보충 유휴를 기다리지 않고 트리거 재시도를 먼저 함 (게임 실행·디스크 대여 판정은 Run 이 그대로 함)
            }

            if (pendingPartial?.Contains(name) == true)
            {
                continue; // 이 틱에 보충 경로가 맡은 게임은 부분 읽기와 재시도를 겹치지 않음
            }

            RetryPendingWarmWhenIdle(game, ct);
        }
    }

    private bool RetryPendingWarmWhenIdle(GameSettings game, CancellationToken ct)
    {
        if (!PendingWarmCanRead(game, ct))
        {
            ResetIdleSinceForGame(game.Name);
            return false;
        }

        DateTimeOffset now = _now();
        if (!PendingWarmIdleElapsed(game, now))
        {
            return false;
        }

        if (PendingWarmAllocation(game) is not > 0)
        {
            return false;
        }

        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            DateTimeOffset enteredAt = _now();
            lock (_gate)
            {
                if (!_pendingWarm.TryGetValue(game.Name, out PendingWarm? pending) || pending.RetryAt > enteredAt)
                {
                    return false;
                }
            }

            if (!PendingWarmCanRead(game, ct))
            {
                ResetIdleSinceForGame(game.Name);
                return false;
            }

            if (!PendingWarmIdleElapsed(game, enteredAt))
            {
                return false;
            }

            long? allocatedMib = PendingWarmAllocation(game);
            if (allocatedMib is not > 0)
            {
                return false;
            }

            PrewarmResult result = Guarded(game, "pending warm retry", pin: false, ignoreCooldown: true, ct, roomMib: allocatedMib);
            return result.Ran;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    private IEnumerable<GameSettings> GamesActiveOnDisk(GameSettings game) => AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk)
        .Where(g => _env.GameRunning(g) || _coordinator?.IsRegistered(g.Name) == true);

    private bool AnyGameActiveOnDisk(GameSettings game) => GamesActiveOnDisk(game).Any();

    private bool PendingWarmCanRead(GameSettings game, CancellationToken ct) => !ct.IsCancellationRequested && BootSettled()
        && HeavyWorkAllowed(game) && !Deferred(game)
        && !AnyGameActiveOnDisk(game) && !_games.Any(g => Relocating(g.Name))
        && _env.DiskBytesPerSecond(game) is { } bps && bps < DiskActivity.BusyBytesPerSecond;

    private bool PendingWarmIdleElapsed(GameSettings game, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_idleSince.TryGetValue(game.Name, out DateTimeOffset since))
            {
                _idleSince[game.Name] = since = now;
            }

            return now - since >= IdleNeeded;
        }
    }

    private void ResetIdleSinceForGame(string game)
    {
        lock (_gate)
        {
            _idleSince.Remove(game);
        }
    }

    private long? PendingWarmAllocation(GameSettings game)
    {
        if (WarmBudget() is not { } budget)
        {
            return null;
        }

        long allocatedMib = budget.AllocMib.GetValueOrDefault(game.Name);
        if (!budget.In.Contains(game.Name) || allocatedMib <= 0)
        {
            DeferPendingWarmForBudget(game.Name);
            LogPendingWarmBudgetSkipped(game.Name, budget.CapMib, budget.UsedMib, allocatedMib);
            return 0;
        }

        lock (_gate)
        {
            _pendingWarmBudgetSkipped.Remove(game.Name);
        }

        return allocatedMib;
    }

    private void DeferPendingWarmForBudget(string game)
    {
        DateTimeOffset retryAt = _now() + PendingWarmRetryDelay();
        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending))
            {
                _pendingWarm[game] = pending with { RetryAt = retryAt };
            }
        }
    }

    private void LogPendingWarmBudgetSkipped(string game, long capMib, long usedMib, long allocatedMib)
    {
        var state = (Reason: "cache budget", CapMib: capMib, UsedMib: usedMib, AllocMib: allocatedMib);
        lock (_gate)
        {
            if (_pendingWarmBudgetSkipped.TryGetValue(game, out var previous) && previous == state)
            {
                return;
            }

            _pendingWarmBudgetSkipped[game] = state;
        }

        _log.Info($"{game}: pending prewarm skipped ({WarmBudgetSkipDetail(capMib, usedMib)})");
    }

    /// 보충 예열 대기 게임이 IdleNeeded 동안 연속으로 한가했을 때만 Supplement 를 부릅니다 (SupplementTick 과 재예열 점검이 같은 판정을 씀). 유휴가 막 시작됐거나 아직 모자라면 읽지 않고 false.
    public bool SupplementWhenIdle(GameSettings g, CancellationToken ct)
    {
        if (!HeavyWorkAllowed(g) || PolicyBlocksPrewarm(g))
        {
            return false;
        }

        long? allocatedMib = null;
        if (WarmBudget() is { } budget)
        {
            if (!budget.In.Contains(g.Name))
            {
                LogSkipped(g.Name, "warm budget", $"{g.Name}: supplement prewarm skipped ({WarmBudgetSkipDetail(budget.CapMib, budget.UsedMib)})");
                return false;
            }

            allocatedMib = budget.AllocMib.GetValueOrDefault(g.Name) is > 0 and long mib ? mib : null;
        }

        bool idle = !DriveHasRunningGame(g) && !_games.Any(x => Relocating(x.Name)) && _env.DiskBytesPerSecond(g) is { } bps && bps < DiskActivity.BusyBytesPerSecond;
        DateTimeOffset now = _now();
        DateTimeOffset since;
        lock (_gate)
        {
            if (!idle)
            {
                _idleSince.Remove(g.Name);
                return false;
            }

            if (!_idleSince.TryGetValue(g.Name, out since))
            {
                _idleSince[g.Name] = since = now;
            }
        }

        if (now - since < IdleNeeded)
        {
            return false;
        }

        bool done = Supplement(g, ct, allocatedMib);
        lock (_gate)
        {
            _idleSince.Remove(g.Name); // 취소돼 남았으면 다음에 다시 IdleNeeded 를 셈
        }

        return done;
    }

    /// 남은 핫 집합 구간을 읽는 보충 예열 한 번 (한 번 끝나면 다시 하지 않음). 다른 예열이 돌고 있거나, 게임·재배치가 있거나, RAM 예산 (예열 예산, 가용 RAM - 여유) 을 넘으면 읽지 않고 false.
    /// 읽는 동안 게임 시작·재배치 시작은 StreamHot 의 기존 규칙으로 멈춥니다 (멈추면 남겨 두고 다음 유휴 때 다시).
    internal bool Supplement(GameSettings game, CancellationToken ct, long? allocatedMib = null)
    {
        (long WarmedMib, long HotMib) p;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out p))
            {
                return false;
            }
        }

        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            using IDisposable work = TrackGameWork(game.Name);
            if (ct.IsCancellationRequested || !HeavyWorkAllowed(game) || PolicyBlocksPrewarm(game)
                || DriveHasRunningGame(game) || _games.Any(x => Relocating(x.Name)) || Deferred(game) || MemoryRetryTooSoon(game.Name))
            {
                return false;
            }

            long avail = _env.AvailableRamMib();
            long physCap = PhysCapMib();
            long budgetCap = allocatedMib is { } allocated ? Math.Min(p.HotMib, allocated) : p.HotMib;
            bool needsPartial = budgetCap < p.HotMib || p.HotMib > physCap || p.HotMib + game.Prewarm.MinFreeRamMib > avail;
            long? partialCap = null;
            if (needsPartial)
            {
                partialCap = PartialCapMib(game, p.HotMib, WindowAvailableMib(avail), physCap, budgetCap);
                if (partialCap is not { } cap)
                {
                    LogSkipped(game.Name, "supplement memory budget", $"{game.Name}: supplement prewarm skipped (hot set {p.HotMib} MiB exceeds the budget: min(cache budget {physCap} MiB, available {avail} MiB - reserve {game.Prewarm.MinFreeRamMib} MiB), and less than {MinPartialMib} MiB fits)");
                    return false;
                }
            }

            long plannedCapMib = partialCap ?? p.HotMib;
            string? inputStamp = PrewarmRun.InputStamp(game);
            long progressBefore = SupplementProgressBytes(game, p, inputStamp);
            if (SupplementDeferred(game, plannedCapMib, p.HotMib, progressBefore, inputStamp))
            {
                return false;
            }

            GameSettings warm = game;
            long? windowMib = null; // 전체가 예산에 안 들어갈 때 읽을 앞부분 (일반 예열의 부분 예산과 같은 식)
            long skipBytes = 0; // 끊긴 보충이 그 윈도에서 이미 읽은 바이트 (ResumePoint)
            bool refused = false; // 앞부분·이어 읽을 지점 표본이 디스크 대여 거절로 읽히지 않음: 이유를 남기지 않고 다음 유휴에 다시 (기억은 그대로)
            if (needsPartial)
            {
                long cap = partialCap.GetValueOrDefault();

                if (SupplementCoveredByCap(game, p, cap, ct, out refused))
                {
                    return false;
                }

                if (!HeavyWorkAllowed(game))
                {
                    return false;
                }

                (long resumed, skipBytes) = ResumePoint(game, cap, ct, out refused);
                if (refused)
                {
                    return false;
                }

                (windowMib, warm) = (resumed, game with { MaxStreamMib = (int)resumed, MaxStreamSet = true });
            }
            else if (!HeavyWorkAllowed(game))
            {
                return false;
            }
            else if (ResumePoint(game, p.HotMib, ct, out refused, full: true).SkipBytes is > 0 and long fullSkip)
            {
                // 전체가 들어가는 경로도 끊긴 위치에서 이어 읽음: 틈 메우기는 스케줄 앞쪽을 안정적으로 만들지 못하므로 이어 읽을 때는 틈 없이 같은 스케줄로 읽고, 끝나면 평소처럼 부분 상태를 지움.
                (skipBytes, warm) = (fullSkip, game with { MaxStreamMib = (int)Math.Min(p.HotMib, int.MaxValue), MaxStreamSet = true });
            }

            if (refused)
            {
                return false;
            }

            if (!HeavyWorkAllowed(game))
            {
                return false;
            }

            using PrewarmRun run = PrewarmRun.Begin();
            long? finalReadBytes = ReadSetBytes(warm, partial: windowMib is not null || skipBytes > 0);
            avail = _env.AvailableRamMib();
            if (finalReadBytes is > 0 and long finalReadSize)
            {
                long finalReadMib = (finalReadSize + (1 << 20) - 1) >> 20;
                long reserveMib = Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib);
                long allowedMib = Math.Min(Math.Min(physCap, allocatedMib ?? long.MaxValue), WindowAvailableMib(avail) - reserveMib);
                if (finalReadMib > allowedMib)
                {
                    if (PartialCapMib(game, finalReadMib, WindowAvailableMib(avail), physCap, budgetCap) is { } capMib)
                    {
                        if (SupplementCoveredByCap(game, p, capMib, ct, out _))
                        {
                            return false; // 줄인 뒤의 상한이 이미 읽은 양 이하: 앞부분을 다시 읽지 않고 앞쪽 "더 읽을 것 없음" 과 같은 경로로 돌아감
                        }

                        bool changedWindow = windowMib is null || windowMib.Value != capMib;
                        windowMib = capMib;
                        warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                        if (changedWindow)
                        {
                            skipBytes = 0; // 창이나 틈 메우기 방식이 바뀌면 이전 스케줄의 앞부분을 건너뛰지 않음
                            lock (_gate)
                            {
                                _resume.Remove(game.Name);
                            }
                        }

                        _log.Info($"{game.Name}: supplement memory budget fell after planning; reading the first {capMib} MiB of {p.HotMib} MiB");
                    }
                    else
                    {
                        string reason = $"low memory (available {avail} MiB, reserve {reserveMib} MiB, final set {finalReadMib} MiB; less than {MinPartialMib} MiB fits)";
                        LogSkipped(game.Name, "supplement " + reason, $"{game.Name}: supplement prewarm skipped ({reason})"); // 일반 예열의 low memory 와 다른 억제 키
                        DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, new StreamStats(0, 0, 0, 0), planEnded: false, 0, reason); // 표본 읽기 뒤의 건너뜀이 유휴마다 되풀이되지 않게 재시도 기한을 둠
                        return false;
                    }
                }
            }

            long readCapMib = windowMib ?? p.HotMib;
            long? zoneBytes = _env.ZoneBytes(game);
            (StreamStats Stats, bool Complete, string? CancelReason, bool MemoryStop)? hot;
            long? tailTotal = null; // 이번에 읽을 목록의 길이 (건너뛴 앞부분 제외): 이어 읽을 위치를 스케줄 길이로 제한하고 끝까지 읽었는지 앎
            try
            {
                if (!HeavyWorkAllowed(game))
                {
                    return false;
                }

                lock (_gate)
                {
                    _memoryStopAt.Remove(game.Name); // 다시 읽기 시작함: 이번에 메모리로 멈추면 그때 다시 기억
                }

                _log.Info($"{game.Name}: supplement prewarm of the rest of the hot set ({p.WarmedMib} of {p.HotMib} MiB read{(windowMib is { } w ? $", reading the first {w} MiB" : string.Empty)}{(skipBytes > 0 ? $", continuing after {skipBytes >> 20} MiB" : string.Empty)})");
                hot = StreamHot(warm, ct, zoneBytes, avail, windowMib is not null || skipBytes > 0, skipBytes: skipBytes, onTotal: total => tailTotal = total, settle: stats =>
                {
                    if (windowMib is { } window)
                    {
                        NoteProgress(game, inputStamp, ActualMib(stats, window, skipBytes), p.HotMib, skipBytes + stats.Bytes); // 앞부분만 읽음: 나머지는 다음 보충 때
                        lock (_gate)
                        {
                            _resume.Remove(game.Name); // 윈도를 끝까지 읽음
                        }
                    }
                    else if (SupplementFullyCovered(stats, skipBytes, p.HotMib, tailTotal, fullReadPath: windowMib is null))
                    {
                        ClearPartial(game.Name, p.HotMib); // 완료 알림보다 먼저 부분 상태를 지움
                    }
                    else
                    {
                        NoteProgress(game, inputStamp, ActualMib(stats, p.HotMib, skipBytes), p.HotMib, skipBytes + stats.Bytes);
                    }
                }, allowReorder: false, roomMib: allocatedMib ?? long.MaxValue);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Warn($"{game.Name}: supplement prewarm failed: {e.GetType().Name}");
                lock (_gate)
                {
                    _partial.Remove(game.Name); // 실패한 읽기는 되풀이하지 않음 (유휴마다 같은 실패를 반복하지 않게)
                    _resume.Remove(game.Name);
                    _supplementNothingNew.Remove(game.Name);
                    _supplementReadProgress.Remove(game.Name);
                    _supplementRetry.Remove(game.Name);
                }

                _log.Warn($"{game.Name}: supplement given up after the failure");
                return false;
            }

            if (hot is not { } r)
            {
                LogSkipped(game.Name, RelocationRunning, $"{game.Name}: supplement prewarm skipped ({RelocationRunning})");
                return false;
            }

            if (!r.Complete)
            {
                long progressBytes = skipBytes + r.Stats.Bytes;
                NoteProgress(game, inputStamp, ActualMib(r.Stats, windowMib ?? p.HotMib, skipBytes), p.HotMib, progressBytes); // 끊겨도 읽은 양은 버리지 않음: 다음 보충이 이어서 읽음
                bool reordered = _streamer.WasPrewarmReordered(game);
                // 위치는 틈을 뺀 스케줄 기준 (틈 메운 순서와 달라도 모자라게 셈: 덜 건너뛰는 쪽). 오류가 있으면 앞이 다 읽혔다고 못 하니 버리고, 목록 끝까지 읽은 뒤의 취소 (갱신 읽기 도중) 는 창 완료.
                long pos = skipBytes + Math.Max(0, r.Stats.Bytes - r.Stats.GapBytes);
                bool listAtEnd = r.Stats.Errors == 0 && tailTotal is { } t && pos >= skipBytes + t - r.Stats.GapBytes;
                bool planEndedAtEnd = listAtEnd && windowMib is not null && SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
                bool atEnd = listAtEnd && !planEndedAtEnd && (windowMib is not null || SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true));
                lock (_gate)
                {
                    if (r.Stats.Errors > 0 || atEnd || planEndedAtEnd || r.Stats.GapBytes > 0 || reordered) // 순서를 바꿨거나 틈 메우기가 섞이면 위치를 믿을 수 없어 다음에 처음부터
                    {
                        _resume.Remove(game.Name);
                    }
                    else if (pos > 0)
                    {
                        _resume[game.Name] = (windowMib ?? p.HotMib, pos); // 같은 윈도 (전체 경로면 전체 크기) 의 이 위치부터 이어 읽음
                    }
                }

                if (atEnd && windowMib is null)
                {
                    ClearPartial(game.Name, p.HotMib); // 전체를 끝까지 읽음
                    CompletePendingWarmSupplement(game, r.Stats);
                }
                else if (planEndedAtEnd)
                {
                    DeferSupplement(game, plannedCapMib, progressBytes, inputStamp, r.Stats, planEnded: true, skipBytes + (tailTotal ?? r.Stats.RequestedBytes));
                }
                else if (progressBytes <= progressBefore
                    && r.CancelReason is not ("game started" or "another game started on the same disk" or "disk read lease revoked" or "disk read lease refused" or "caller cancelled"))
                {
                    // 진전 없이 끊긴 보충도 유휴마다 같은 앞부분을 다시 읽지 않음 (게임 시작·디스크 양보로 끊긴 것은 제외: 게임이 끝나면 바로 이어야 함)
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + (tailTotal ?? r.Stats.RequestedBytes));
                }

                string reason = r.CancelReason ?? "cancelled externally";
                if (r.MemoryStop)
                {
                    lock (_gate)
                    {
                        _memoryStopAt[game.Name] = _now(); // 읽은 데까지는 NoteProgress 가 이미 남김
                    }
                }

                LogSkipped(game.Name, "supplement cancelled " + reason, () => $"{game.Name}: supplement prewarm cancelled after {r.Stats.Bytes >> 20} MiB ({reason})");
                return false;
            }

            if (r.Stats.Bytes <= 0 && skipBytes == 0
                && (windowMib is not null || !SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true)))
            {
                bool planEnded = SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
                long listBytes = tailTotal ?? r.Stats.RequestedBytes;
                DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded, skipBytes + listBytes);
                _log.Info($"{game.Name}: supplement prewarm read nothing, kept as partial");
                return false;
            }

            long progressAfter = skipBytes + r.Stats.Bytes;
            bool madeProgress = progressAfter > progressBefore;
            bool requestListEnded = windowMib is not null && SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
            long plannedListBytes = tailTotal ?? r.Stats.RequestedBytes;
            if (requestListEnded)
            {
                DeferSupplement(game, plannedCapMib, Math.Max(progressBefore, progressAfter), inputStamp, r.Stats, planEnded: true, skipBytes + plannedListBytes);
                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the plan lists {MibText(skipBytes + plannedListBytes)} within {readCapMib} MiB");
                return true;
            }

            if (windowMib is not null)
            {
                if (!madeProgress)
                {
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + plannedListBytes);
                }

                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the partial window remains pending");
                return true;
            }

            if (!SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true))
            {
                if (!madeProgress)
                {
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + plannedListBytes);
                }

                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the hot set remains partial");
                return true;
            }

            ClearPartial(game.Name, p.HotMib);
            CompletePendingWarmSupplement(game, r.Stats);
            _log.Info($"{game.Name}: supplement prewarm done: {MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.GapBytes / 1048576.0:F2} MiB gaps filled in {r.Stats.Seconds:F2} s, {r.Stats.Errors} errors");
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // cap 이 이미 읽은 양 (p.WarmedMib) 이하이면 더 읽을 것이 없는 경우를 처리하고 true: 메모리 부분 예열은 바로 대기에서 빼고, 아니면 앞부분 표본이 식지 않았을 때만 같은 처리 (식은 앞부분은 재예열 점검이 다시 읽음).
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 두고 호출자가 조용히 멈춤).
    private bool SupplementCoveredByCap(GameSettings game, (long WarmedMib, long HotMib) p, long cap, CancellationToken ct, out bool refused)
    {
        refused = false;
        if (cap > p.WarmedMib)
        {
            return false;
        }

        if (MemoryPartialPending(game.Name))
        {
            LogSupplementNothingNew(game.Name, cap, p.WarmedMib, p.HotMib);
            if (!HasMemoryStop(game.Name))
            {
                DropPendingWarm(game.Name); // 메모리로 멈춘 게임은 대기를 남겨 둠: 가용 메모리가 읽은 양 + 여유 + 예비량으로 회복되면 다음 유휴 때 창 계산이 이어 읽음
            }

            return true;
        }

        if (HeavyWorkAllowed(game) && FrontCold(game, p.WarmedMib, ct, out refused))
        {
            return false;
        }

        if (!refused && HeavyWorkAllowed(game))
        {
            LogSupplementNothingNew(game.Name, cap, p.WarmedMib, p.HotMib);
            DropPendingWarm(game.Name); // 들어가는 만큼은 이미 읽었으니 더 읽을 것이 없고, 남기면 회복 보류만 계속 걸림
        }

        return true;
    }

    /// 서비스 시작 예열 대상이 되는 마지막 플레이의 최대 경과.
    public static readonly TimeSpan StartupPlayedWithin = TimeSpan.FromDays(14);

    /// 서비스 시작 예열을 시작해도 되는 시점인가 (부팅 BootDelay 경과). 자동 모드의 기록 없는 예열 (cold start) 도 같은 조건을 봅니다.
    public bool BootSettled() => _env.UptimeMs() >= BootDelay.TotalMilliseconds;

    /// 부팅 뒤 경과 시간 (ms, PrewarmEnv.UptimeMs). 부팅 조용 구간 판정이 같은 시계를 씁니다.
    public long UptimeMs() => _env.UptimeMs();

    // 부팅 5 분 경과 + 게임 드라이브 60 초 유휴를 기다립니다. 취소되면 false. 대상 드라이브 중 하나라도 처리량을 못 읽으면 (null) 유휴를 확인할 수 없으므로 읽지 않고 보류합니다.
    private bool WaitStartupGate(IReadOnlyList<GameSettings> games, CancellationToken ct)
    {
        TimeSpan idle = TimeSpan.Zero;
        bool logged = false;
        bool heldLogged = false;
        while (!ct.IsCancellationRequested)
        {
            if (RelocationHeld())
            {
                idle = TimeSpan.Zero; // 재배치가 끝난 뒤 유휴를 처음부터 다시 셉니다
                _env.Sleep(GateStep, ct);
                continue;
            }

            double?[] rates = [.. games.Select(g => _env.DiskBytesPerSecond(g))];
            bool settled = BootSettled();
            if (settled && rates.Any(r => r is null))
            {
                // 디스크 유휴를 확인할 수 없음: 읽지 않고 계측값이 생길 때까지 (또는 취소될 때까지) 보류
                idle = TimeSpan.Zero;
                if (!heldLogged)
                {
                    heldLogged = true;
                    _log.Info("service start prewarm held: disk activity cannot be measured");
                }

                _env.Sleep(GateStep, ct);
                continue;
            }

            bool busy = !settled || rates.Any(r => r >= DiskActivity.BusyBytesPerSecond);
            idle = busy ? TimeSpan.Zero : idle + GateStep;
            if (idle >= IdleNeeded)
            {
                return true;
            }

            if (!logged)
            {
                logged = true;
                _log.Info($"service start prewarm waits for {BootDelay.TotalMinutes:F0} min after boot and {IdleNeeded.TotalSeconds:F0} s of idle disk");
            }

            _env.Sleep(GateStep, ct);
        }

        return false;
    }

    // 핫 집합 몇 곳을 읽어 이미 파일 캐시에 있으면 (CacheProbe) 다시 읽지 않습니다. 표본을 못 읽으면 모름 (null) 입니다.
    internal bool? AlreadyWarm(GameSettings g)
    {
        ProbeGeneration? before = ProbeGenerationOf(g);
        ProbeResult? p;
        try
        {
            p = ColdSamples(g) is { } samples ? ProbeSamplesForGame(g, samples, "already warm") : ProbeReadMsForGame(g, "already warm");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return null;
        }

        if (p is not { Samples: > 0 })
        {
            return null;
        }

        bool warm = !p.Cold;
        ProbeGeneration? after = ProbeGenerationOf(g);
        lock (_gate)
        {
            if (before is not null && before == after)
            {
                _residencyProbes[g.Name] = new ResidencyProbe(warm, _now(), before);
            }
            else
            {
                _residencyProbes.Remove(g.Name);
            }
        }

        return warm;
    }

    /// 같은 계획·trace 세대에서 얻은 마지막 실제 warm 표본 시각. 세대가 다르거나 확인할 수 없으면 기록을 버립니다.
    internal DateTimeOffset? RecentWarmProbeAt(GameSettings g)
    {
        ProbeGeneration? current = ProbeGenerationOf(g);
        lock (_gate)
        {
            if (!_residencyProbes.TryGetValue(g.Name, out ResidencyProbe? probe))
            {
                return null;
            }

            if (current is null || current != probe.Generation)
            {
                _residencyProbes.Remove(g.Name);
                return null;
            }

            return probe.Warm ? probe.At : null;
        }
    }

    private ProbeGeneration? ProbeGenerationOf(GameSettings game)
    {
        try
        {
            ProbeFileGeneration FileGeneration(string path)
            {
                if (!File.Exists(path))
                {
                    return new ProbeFileGeneration(path, false, 0, 0);
                }

                Fingerprint stamp = _env.FileStamp(path);
                return new ProbeFileGeneration(path, true, stamp.Size, stamp.MtimeNs);
            }

            return new ProbeGeneration(FileGeneration(game.Plan), FileGeneration(game.Trace), FileGeneration(PlanOutput.WarmOnlyPath(game.Trace)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void InvalidateResidencyProbe(string game)
    {
        lock (_gate)
        {
            _residencyProbes.Remove(game);
        }
    }

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로. narrow 가 있으면 그 목록에서 예열할 게임만 고릅니다.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start, Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? narrow = null, CancellationToken ct = default)
    {
        if (DuplicateTrigger(start.ExeName))
        {
            return [];
        }

        IReadOnlyList<GameSettings> hit = _games.Where(g => IsTrigger(g, start.ExeName) && !PolicyBlocked(g, $"trigger prewarm ({start.ExeName})")).ToList();
        IReadOnlyList<GameSettings> chosen = [.. narrow?.Invoke(hit) ?? hit];
        if (chosen.Count > 0)
        {
            GameSettings first = WarmRank?.Invoke(chosen).FirstOrDefault() ?? chosen[0]; // 같은 트리거를 여러 게임이 쓰면 기존 순위로 한 게임을 고름
            lock (_gate)
            {
                _triggered = (first.Name, _now());
            }
        }

        IReadOnlyList<PrewarmResult> results = RunBatch(chosen, $"trigger {start.ExeName}", pin: !LauncherExes.Is(start.ExeName), ct: ct, trigger: true); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
        WaitForBlockingGames(results, start.ExeName);
        return results;
    }

    // 같은 디스크의 다른 게임이 실행 중이라 거절된 트리거 예열은, 그 게임들이 사라진 것을 처음 본 때 한 번 다시 시도하도록 적어 둡니다 (NoteGamesGone).
    private void WaitForBlockingGames(IReadOnlyList<PrewarmResult> results, string triggerExe)
    {
        foreach (PrewarmResult r in results.Where(r => !r.Ran && (r.SkipReason == "game is running" || r.CancelDetail == "disk read lease refused")))
        {
            if (_games.FirstOrDefault(g => string.Equals(g.Name, r.Game, StringComparison.OrdinalIgnoreCase)) is { } game && !_env.GameRunning(game))
            {
                _triggerRetries.Wait(game.Name, triggerExe, GamesActiveOnDisk(game).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), _now());
            }
        }
    }

    /// 종료 유예에 들어간 (프로세스가 사라진 것을 처음 본) 게임 이름들 (Worker 가 틱마다 부름): 그 게임 때문에 거절됐고 트리거 프로세스가 아직 살아 있는 예열의 재시도를 지금으로 당깁니다.
    public void NoteGamesGone(IReadOnlySet<string> gone)
    {
        foreach ((string game, string exe) in _triggerRetries.NoteGone(gone, BlockerActive, _env.ProcessRunning, _now()))
        {
            lock (_gate)
            {
                if (_pendingWarm.TryGetValue(game, out PendingWarm? pending))
                {
                    _pendingWarm[game] = pending with { RetryAt = _now(), TriggerExe = exe };
                }
            }
        }
    }

    // 막던 게임이 아직 실행 중이거나 양보 등록돼 있는가 (설정에서 빠진 게임은 아님).
    private bool BlockerActive(string name) =>
        _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } game && (_env.GameRunning(game) || _coordinator?.IsRegistered(name) == true);

    // 당겨진 트리거 재시도를 트리거 경로와 같은 조건 (유휴·종료 유예 게이트 없음) 으로 한 번 돌립니다. 다른 예열이 돌고 있으면 표시를 남겨 다음 틱으로 미룸.
    private bool TryRunTriggerRetry(GameSettings game, CancellationToken ct)
    {
        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            if (TakeTriggerRetry(game.Name) is not { } exe)
            {
                return false;
            }

            _log.Info($"{game.Name}: retrying the refused trigger prewarm ({exe}) now that the blocking game is gone");
            _ = RunBatch([game], $"trigger {exe}", pin: !LauncherExes.Is(exe), ct: ct, trigger: true);
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 당겨진 트리거 재시도를 한 번만 꺼냅니다 (표시를 지우므로 다시 거절돼도 되풀이하지 않음).
    private string? TakeTriggerRetry(string game)
    {
        lock (_gate)
        {
            if (!_pendingWarm.TryGetValue(game, out PendingWarm? pending) || pending.TriggerExe is not { } exe)
            {
                return null;
            }

            _pendingWarm[game] = pending with { TriggerExe = null };
            return exe;
        }
    }

    // 같은 exe 의 트리거는 SkipLogEvery 안에 한 번만 처리하고 나머지는 세어 두었다가, 다음에 처리하는 때 한 줄로 남깁니다.
    private bool DuplicateTrigger(string exe)
    {
        int suppressed;
        lock (_gate)
        {
            DateTimeOffset now = _now();
            if (_triggerSeen.TryGetValue(exe, out (DateTimeOffset At, int Count) last) && now - last.At < SkipLogEvery)
            {
                _triggerSeen[exe] = last with { Count = last.Count + 1 };
                return true;
            }

            suppressed = last.Count;
            _triggerSeen[exe] = (now, 0);
        }

        if (suppressed > 0)
        {
            _log.Info($"{exe}: trigger handled again ({suppressed} duplicate triggers suppressed)");
        }

        return false;
    }

    /// 주어진 게임을 차례로 (한 게임의 실패가 나머지를 막지 않음).
    public IReadOnlyList<PrewarmResult> RunAll(IEnumerable<GameSettings> games, string why) => RunBatch(games, why);

    /// 게임 설치 폴더 (origin_root) 아래의 지정한 파일들을 순서대로 파일 캐시로 읽습니다 (챔피언 선택 선읽기 같은 임의 목록). 읽은 바이트를 돌려주고, 읽지 않았으면 0.
    /// 경로는 정규화한 뒤 origin_root 아래의 것만 (".."·다른 드라이브·루트 밖 링크는 건너뜀), 이미 파일 캐시에 있는 파일은 예산을 쓰지 않고 건너뜁니다.
    /// 예산은 budgetBytes 와 공유 RAM 예산 (예열 예산, 가용 RAM - 여유 - 이미 올린 양) 중 작은 쪽. 다른 예열이 돌고 있거나 같은 디스크에서 재배치가 시작했으면 읽지 않습니다. ct 로 멈춥니다 (예외를 내지 않음).
    public long WarmFiles(GameSettings g, IReadOnlyList<string> paths, long budgetBytes, CancellationToken ct)
    {
        if (budgetBytes <= 0 || paths.Count == 0 || ct.IsCancellationRequested)
        {
            return 0;
        }

        if (PolicyBlocked(g, "warm files"))
        {
            return 0;
        }

        if (!PlanGuards.Inside(g.OriginRoot, g.AllowedRoots, RealPaths.Real))
        {
            LogSkipped(g.Name, "outside allowed roots", $"{g.Name}: warm files skipped (origin_root resolves outside allowed roots)");
            return 0;
        }

        if (DriveHasRunningGame(g))
        {
            LogSkipped(g.Name, "game running", () => $"{g.Name}: warm files skipped (a game is running on this drive){BlockerNote(g)}");
            return 0;
        }

        var ops = new List<ReadOp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            if (FileWarm.Allowed(g.OriginRoot, path, RealPaths.Real) is not { } full || !seen.Add(full))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(full);
                ReadOp op = new(full, 0, file.Exists ? file.Length : 0);
                if (op.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    ops.Add(op);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 열 수 없는 파일은 건너뜀
            }
        }

        if (ops.Count == 0)
        {
            return 0;
        }

        if (!Monitor.TryEnter(_run))
        {
            LogSkipped(g.Name, "another prewarm", $"{g.Name}: warm files skipped (another prewarm is running)");
            return 0;
        }

        try
        {
            using IDisposable work = TrackGameWork(g.Name);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); // 같은 디스크 게임 시작 (NoteGameStart) 이 끊을 수 있게 호출자 ct 와 연결
            if (!TryMarkReading(g.Name, () =>
            {
                (_coldReading, _warmStop) = (g.Name, stop);
                _driveObservedAt = _now();
            }))
            {
                LogSkipped(g.Name, RelocationRunning, $"{g.Name}: warm files skipped ({RelocationRunning})");
                return 0;
            }

            try
            {
                // 표본 읽기도 읽기 표시 안에서 (재배치가 시작했으면 위에서 이미 막힘). 표시를 세운 뒤 게임이 떴으면 NoteGameStart 와 겹쳐도 놓치지 않게 다시 봄.
                if (DriveHasRunningGame(g))
                {
                    LogSkipped(g.Name, "game running", () => $"{g.Name}: warm files skipped (a game is running on this drive){BlockerNote(g)}");
                    return 0;
                }

                var uncached = new List<ReadOp>();
                foreach (ReadOp op in ops)
                {
                    bool? cached = FileCached(g, op, stop.Token);
                    if (cached is null)
                    {
                        return 0;
                    }

                    if (!cached.Value)
                    {
                        uncached.Add(op);
                    }
                }

                ops = uncached;
                long held = ColdMib(null);
                long physMib = PhysCapMib();
                long ramMib = Math.Min(_env.AvailableRamMib() - g.Prewarm.MinFreeRamMib - held, physMib - held);
                long budget = Math.Min(budgetBytes, Math.Max(0, ramMib) << 20);
                ops = PrefetchStreamer.Head(ops, budget);
                if (ops.Count == 0 || stop.IsCancellationRequested)
                {
                    return 0;
                }

                InvalidateResidencyProbe(g.Name);
                (StreamStats s, bool complete) = ReadFileBatches(g, ops, stop);
                _log.Info($"{g.Name}: warm files {(complete ? "done" : "cancelled")}: {s.Bytes >> 20} MiB of {ops.Sum(o => o.Length) >> 20} MiB ({ops.Count} files) in {s.Seconds:F2} s, {s.Errors} errors");
                return s.Bytes;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                _log.Warn($"{g.Name}: warm files stopped: {e.GetType().Name}");
                return 0;
            }
            finally
            {
                (_coldReading, _warmStop) = (null, null);
            }
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 파일 목록을 FolderWarm.BatchBytes 묶음으로 읽되 묶음 경계마다 공통 실행 판정을 다시 봅니다 (시작 알림을 놓친 등록 exe 도 잡도록). 판정이 참이면 취소합니다.
    private (StreamStats Stats, bool Complete) ReadFileBatches(GameSettings g, List<ReadOp> ops, CancellationTokenSource stop)
    {
        StreamStats sum = new(0, 0, 0, 0);
        int at = 0;
        long taken = 0; // ops[at] 에서 이미 묶음에 넣은 길이 (큰 파일은 묶음 경계에서 나눔)
        while (at < ops.Count)
        {
            if (RunningBlocksRead(g) || ExternalLoadWhilePaused(g, stop.Token) || DiskSampleUnknown(g))
            {
                stop.Cancel();
            }

            if (stop.IsCancellationRequested)
            {
                return (sum with { Aborted = true }, false);
            }

            List<ReadOp> batch = [];
            long size = 0;
            while (at < ops.Count && size < FolderWarm.BatchBytes)
            {
                ReadOp op = ops[at];
                long take = Math.Min(op.Length - taken, FolderWarm.BatchBytes - size);
                batch.Add(op with { Offset = op.Offset + taken, Length = take });
                size += take;
                taken += take;
                if (taken >= op.Length)
                {
                    (at, taken) = (at + 1, 0);
                }
            }

            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "warm files", stop.Token);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return (sum with { Aborted = true }, false);
            }

            (StreamStats s, bool complete) = _env.FileRead(batch, lease?.Token ?? stop.Token);
            sum = sum with
            {
                Bytes = sum.Bytes + s.Bytes,
                Requests = sum.Requests + s.Requests,
                Errors = sum.Errors + s.Errors,
                Seconds = sum.Seconds + s.Seconds,
                Aborted = sum.Aborted || s.Aborted,
                RequestedBytes = sum.RequestedBytes + s.RequestedBytes,
                HintAcceptedBytes = sum.HintAcceptedBytes + s.HintAcceptedBytes,
            };
            if (!complete)
            {
                return (sum, false);
            }
        }

        // 마지막 묶음을 다 읽은 뒤의 취소·중단은 완료로 치지 않음
        return (sum, !stop.IsCancellationRequested && !sum.Aborted);
    }

    // 파일 한 개의 표본 (큰 파일은 조각마다 고르게) 이 모두 파일 캐시에 있으면 true. 표본을 못 읽거나 취소되면 false.
    private (ProbeResult? Result, bool Aborted) ProbeFileSamples(DiskReadCoordinator.Lease? lease, IReadOnlyList<(string Path, long Offset, int Bytes)> samples, CancellationToken ct)
    {
        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return (null, true);
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return (_env.ProbeFileSamples(samples, ct), false);
        }

        if (_env.ProbeFileSamplesChecked is { } checkedProbe)
        {
            ProbeResult? result = checkedProbe(samples, lease?.Token ?? ct, CanIssue);
            return CanIssue() ? (result, false) : (null, true);
        }

        var results = new List<ProbeResult>();
        CancellationToken probeToken = lease?.Token ?? ct;
        foreach (var sample in samples)
        {
            if (!CanIssue())
            {
                return (null, true);
            }

            if (_env.ProbeFileSamples([sample], probeToken) is { } result)
            {
                results.Add(result);
            }
        }

        return CanIssue() ? (CombineProbes(results), false) : (null, true);
    }

    internal ProbeResult? ProbeSamplesForGame(GameSettings game, IReadOnlyList<(string Path, long Offset, int Bytes)> samples, string purpose, CancellationToken ct = default)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return null;
        }

        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return null;
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return _env.ProbeSamples(samples);
        }

        if (_env.ProbeSamplesChecked is { } checkedProbe)
        {
            ProbeResult? result = checkedProbe(samples, CanIssue);
            return CanIssue() ? result : null;
        }

        var results = new List<ProbeResult>();
        foreach (var sample in samples)
        {
            if (!CanIssue())
            {
                return null;
            }

            if (_env.ProbeSamples([sample]) is { } result)
            {
                results.Add(result);
            }
        }

        return CanIssue() ? CombineProbes(results) : null;
    }

    // 단발 표본 읽기를 디스크 대여 안에서: 거절·무효·취소면 읽지 않고 null (refused = true: 읽기 실패와 구분), 발행 직전 재검사를 받는 읽기가 있으면 그것을 씁니다.
    private ProbeResult? GuardedProbe(GameSettings game, string purpose, CancellationToken ct, Func<ProbeResult?> plain, Func<Func<bool>, ProbeResult?>? checkedProbe, out bool refused)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        refused = !CanIssue();
        if (refused)
        {
            return null;
        }

        ProbeResult? result = checkedProbe is null || (_coordinator is null && !ct.CanBeCanceled) ? plain() : checkedProbe(CanIssue);
        refused = !CanIssue();
        return refused ? null : result;
    }

    private ProbeResult? ProbeReadMsForGame(GameSettings game, string purpose, CancellationToken ct = default, bool partial = false)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return null;
        }

        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return null;
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return partial ? (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(game) : _env.ProbeReadMs(game);
        }

        Func<GameSettings, Func<bool>, ProbeResult?>? checkedProbe = partial ? _env.ProbePartialReadMsChecked ?? _env.ProbeReadMsChecked : _env.ProbeReadMsChecked;
        ProbeResult? result = checkedProbe is null
            ? partial ? (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(game) : _env.ProbeReadMs(game)
            : checkedProbe(game, CanIssue);
        return CanIssue() ? result : null;
    }

    private static ProbeResult? CombineProbes(IReadOnlyList<ProbeResult> results)
    {
        int samples = results.Sum(result => result.Samples);
        return samples == 0 ? null : new ProbeResult(samples, results.Sum(result => result.Slow), results.Sum(result => result.AverageMs * result.Samples) / samples);
    }

    private bool? FileCached(GameSettings game, ReadOp op, CancellationToken ct)
    {
        try
        {
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "warm file cache probe", ct);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return null;
            }

            (ProbeResult? probe, bool aborted) = ProbeFileSamples(lease, CacheProbe.PickSamples(FileWarm.Pieces(op)), ct);
            return aborted ? null : probe is { Samples: > 0, Cold: false };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// 같은 게임·같은 사유의 "prewarm skipped" 를 남기는 최소 간격 (트리거 프로그램이 연달아 떠도 줄이 쌓이지 않게).
    public static readonly TimeSpan SkipLogEvery = TimeSpan.FromSeconds(60);

    // 간격 안에 같은 줄이 또 나오면 남기지 않고 세어 두었다가, 다음에 남기는 줄에 "(suppressed N)" 로 붙입니다.
    private void LogSkipped(string game, string reason, string line) => LogSkipped(game, reason, () => line);

    // line 은 실제로 남길 때만 만듭니다 (억제되는 틱에는 프로세스 스냅샷 같은 비싼 일을 하지 않음).
    private void LogSkipped(string game, string reason, Func<string> line)
    {
        int suppressed = 0;
        lock (_gate)
        {
            string key = SkipLogKey(game, reason);
            DateTimeOffset now = _now();
            if (_skipLogged.TryGetValue(key, out (DateTimeOffset At, int Count) last))
            {
                if (now - last.At < SkipLogEvery)
                {
                    _skipLogged[key] = last with { Count = last.Count + 1 };
                    return;
                }

                suppressed = last.Count;
            }

            _skipLogged[key] = (now, 0);
        }

        string text = line();
        _log.Info(suppressed > 0 ? $"{text} (suppressed {suppressed})" : text);
    }

    internal static string SkipLogKey(string game, string reason) => game + "|" + StableSkipReason(reason);

    private static string StableSkipReason(string reason)
    {
        if (reason.StartsWith("supplement cancelled ", StringComparison.Ordinal))
        {
            return "supplement-cancelled|" + StableSkipReason(reason[21..]);
        }

        if (reason.StartsWith("prewarm cancelled ", StringComparison.Ordinal))
        {
            return "prewarm-cancelled|" + StableSkipReason(reason[18..]);
        }

        if (reason.StartsWith("head cancelled ", StringComparison.Ordinal))
        {
            return "head-cancelled|" + StableSkipReason(reason[15..]);
        }

        if (reason.StartsWith("supplement low memory", StringComparison.Ordinal))
        {
            return "supplement-low-memory";
        }

        if (reason.StartsWith("low memory", StringComparison.Ordinal))
        {
            return "low-memory";
        }

        if (reason.StartsWith("memory reserve:", StringComparison.Ordinal))
        {
            return "memory-reserve";
        }

        if (reason.StartsWith("memory budget:", StringComparison.Ordinal))
        {
            return "memory-budget";
        }

        return reason.StartsWith("cooldown (", StringComparison.Ordinal) ? "cooldown" : reason;
    }

    private void LogSupplementNothingNew(string game, long capMib, long warmedMib, long hotMib)
    {
        var state = (Reason: "supplement nothing new", WarmedMib: warmedMib, HotMib: hotMib); // cap 은 가용 메모리에 따라 계속 흔들리므로 키에 넣지 않음
        lock (_gate)
        {
            if (_supplementNothingNew.TryGetValue(game, out var previous) && previous == state)
            {
                return;
            }

            _supplementNothingNew[game] = state;
        }

        _log.Info($"{game}: supplement prewarm skipped (the {capMib} MiB that fits is already read: {warmedMib} of {hotMib} MiB)");
    }

    // 한 묶음의 예산 = min(예열 예산, 가용 RAM - 여유). 존 크기 합이 예산을 넘는 게임만 건너뛰고, 뒤 게임은 남은 예산으로 따로 판단합니다 (앞쪽 = 최근 순서가 우선).
    // 서비스 시작·런처 시작이 같은 규칙입니다. 첫 게임의 가용 RAM 쪽 판단은 Run 의 자체 판단에 맡깁니다.
    // yieldToRelocation: 게임 사이마다 재배치가 시작됐는지 다시 보고, 돌고 있으면 끝날 때까지 기다린 뒤 이어서 읽습니다 (서비스 시작 예열: 게이트를 지난 직후나 예열 도중 시작된 재배치와 겹치지 않게).
    private IReadOnlyList<PrewarmResult> RunBatch(IEnumerable<GameSettings> games, string why, bool pin = true, CancellationToken ct = default, bool yieldToRelocation = false, bool trigger = false)
    {
        var results = new List<PrewarmResult>();
        IReadOnlyList<GameSettings> list = games as IReadOnlyList<GameSettings> ?? [.. games];
        IReadOnlyList<GameSettings> given = list; // 결과는 받은 순서로 돌려줌 (읽는 순서만 예산에 따라 바뀔 수 있음)
        if (list.Any(g => PolicyBlocksPrewarm(g)))
        {
            // 게임 정책이 예열을 막은 게임은 예산 계산과 읽기에서 빼고 결과만 남김
            foreach (GameSettings denied in list.Where(g => PolicyBlocked(g, $"prewarm [{why}]")))
            {
                results.Add(new PrewarmResult(denied.Name, false, GamePolicySkip, null, _now()));
            }

            list = [.. list.Where(g => !PolicyBlocksPrewarm(g))];
        }

        long cold = list.Count > 1 ? ColdMib(null) : 0; // 기록 없는 예열 양: 순위 모의 계산과 앞부분 예열이 한 번만 읽어 같이 씀
        if (list.Count > 2)
        {
            long availNow = _env.AvailableRamMib();
            long physNow = PhysCapMib();
            list = OrderByValuePerByte(list, g => SkipsInBatch(g, availNow) ? 0 : ((_env.ZoneBytes(g) ?? 0) + (1 << 20) - 1) >> 20, (g, used) => BatchRoomMib(physNow, availNow, g.Prewarm.MinFreeRamMib, cold + used));
        }

        long avail = _env.AvailableRamMib();
        long physCap = PhysCapMib(); // MiB, 예열 예산
        (long[] observed, long[] later) = ObservedSplit(list, avail);
        WarmHeads(list, ct, yieldToRelocation, cold, observed, later, trigger);
        long used = 0; // 핫 집합 예열의 MiB (기록 없는 예열은 _coldMib 가 셈)
        for (int i = 0; i < list.Count; i++)
        {
            GameSettings g = list[i];
            if (yieldToRelocation && !WaitOutRelocation(g, ct))
            {
                PrewarmResult cancelled = new(g.Name, false, Cancelled, null, _now());
                TrackPendingWarm(g, cancelled, cancelled.At); // 기다리는 동안 취소됨 (읽지 않았으므로 상태 파일에는 기록하지 않음)
                results.Add(cancelled);
                continue;
            }

            if (Deferred(g))
            {
                results.Add(new PrewarmResult(g.Name, false, PostUpdateDelay, null, _now()));
                continue;
            }

            long zoneMib = ZoneMib(g);
            long held = used + ColdMib(null); // 이미 올린 양 (기록 없는 예열 포함): 기록 없는 예열이 먼저 돌아도 새 사용량이 바로 셈에 들어감
            long room = RoomKeepingLaterObserved(BatchRoomMib(physCap, avail, g.Prewarm.MinFreeRamMib, held), observed[i], later[i]); // 묶음에 남은 예산: 전체가 들어가지 않으면 들어가는 만큼만 읽음 (부분 예열)
            bool fits = zoneMib <= room;
            if (!fits && room < MinPartialMib)
            {
                LogSkipped(g.Name, "batch memory budget", $"{g.Name}: prewarm skipped (batch zones {held + zoneMib} MiB exceed the budget: min(cache budget {physCap} MiB, available {avail} MiB - reserve {g.Prewarm.MinFreeRamMib} MiB)) [{why}]");
                PrewarmResult skipped = new(g.Name, false, "batch memory budget", null, _now());
                TrackPendingWarm(g, skipped, skipped.At);
                results.Add(skipped);
                continue;
            }

            PrewarmResult r = Guarded(g, why, pin, ct: ct, usedMib: used, roomMib: fits ? null : room, trigger: trigger);
            results.Add(r);
            used += r.Ran && r.Mode is null ? r.Stats is { Bytes: > 0 } st ? (st.Bytes + (1 << 20) - 1) >> 20 : zoneMib : 0; // 예산에서는 설정한 상한이 아니라 실제로 읽은 양을 뺌 (통계가 없으면 존 크기)
        }

        if (ReferenceEquals(given, list))
        {
            return results;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal); // 받은 순서 (같은 이름이면 처음 것)
        for (int i = 0; i < given.Count; i++)
        {
            rank.TryAdd(given[i].Name, i);
        }

        return [.. results.OrderBy(r => rank.GetValueOrDefault(r.Game, -1))];
    }

    // 모의 계산에서 실제로 건너뛸 게임은 예산을 차지하지 않음: 업데이트 지연·기록 없는 예열·그 밖의 건너뜀 사유 (메모리 부족은 부분 예열이 읽으니 제외).
    private bool SkipsInBatch(GameSettings g, long avail) => WillSkipReason(g, avail) is not null || IsCold(g);

    // Run 이 메모리 부족 외의 사유로 건너뛸 게임의 사유 (없으면 null): 업데이트 지연 포함.
    private string? WillSkipReason(GameSettings g, long avail)
    {
        TimeSpan? since = LastWarmed(g.Name) is { } prev ? _now() - prev : null;
        string? skip = PrewarmPolicy.SkipReason(_env.ZoneBytes(g), _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin));
        if (skip is not null && !skip.StartsWith("low memory", StringComparison.Ordinal))
        {
            return skip;
        }

        return Deferred(g) ? PostUpdateDelay : null;
    }

    // 서비스 시작 분류에서 건너뛰기로 한 게임의 결과를 Run 과 같은 방식으로 남김 (업데이트 지연은 기록 없이 대기만).
    private PrewarmResult SkipAtServiceStart(GameSettings g, string reason)
    {
        DateTimeOffset now = _now();
        PrewarmResult result = new(g.Name, false, reason, null, now);
        if (reason == PostUpdateDelay)
        {
            TrackPendingWarm(g, result, now);
            return result;
        }

        LogSkipped(g.Name, reason, () => $"{g.Name}: prewarm skipped ({reason}) [service start]{(reason == "game is running" ? BlockerNote(g) : string.Empty)}");
        return Record(g, now, result, 0);
    }

    // 묶음에 게임이 둘 이상이면 각 게임의 로딩 앞부분 (tier0) 을 최근 순서로 먼저 모두 올리고, 그 뒤 게임별 전체 (tier1 포함) 를 읽습니다 (앞부분은 이미 올라와 있어 다시 읽어도 빠름).
    // 올릴 게임은 RunBatch 와 같은 예산과 Run 과 같은 건너뜀 조건으로 고릅니다. 취소는 앞부분 묶음 사이에서 보고, 읽던 게임이 시작되면 끝까지 읽습니다 (StreamHot 과 같음).
    private void WarmHeads(IReadOnlyList<GameSettings> games, CancellationToken ct, bool yieldToRelocation, long planned, long[] observed, long[] later, bool allowWhileExiting)
    {
        if (games.Count < 2)
        {
            return;
        }

        long avail = _env.AvailableRamMib();
        long physCap = PhysCapMib();
        for (int i = 0; i < games.Count; i++)
        {
            GameSettings g = games[i];
            if (ct.IsCancellationRequested || (yieldToRelocation && !WaitOutRelocation(g, ct)))
            {
                return;
            }

            if (Deferred(g))
            {
                continue;
            }

            long zoneMib = ZoneMib(g);
            long room = RoomKeepingLaterObserved(BatchRoomMib(physCap, avail, g.Prewarm.MinFreeRamMib, planned), observed[i], later[i]); // RunBatch 와 같은 남은 예산
            long? hotBytes = _env.ZoneBytes(g);
            TimeSpan? since = LastWarmed(g.Name) is { } prev ? _now() - prev : null;
            string? skip = PrewarmPolicy.SkipReason(hotBytes, _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin));
            if (skip?.StartsWith("low memory", StringComparison.Ordinal) == true && ReadSetBytes(g) is { } readBytes && readBytes < hotBytes)
            {
                skip = PrewarmPolicy.SkipReason(readBytes, _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin)); // Run 과 같은 재판단: 읽을 집합이 들어가면 진행
                hotBytes = readBytes;
            }

            bool lowMemory = skip?.StartsWith("low memory", StringComparison.Ordinal) == true;
            long hotMib = ((hotBytes ?? 0) + (1 << 20) - 1) >> 20; // 실제로 읽을 양 (RunBatch 의 Run 과 같은 기준)
            long take = hotMib; // 이 게임이 예산에서 가져갈 양 (RunBatch 가 실제로 읽을 양과 같게)
            GameSettings head = g;
            if (lowMemory || hotMib > room)
            {
                // 전체가 안 들어가도 Run 의 부분 예열이 읽으면 앞부분도 올림 (RunBatch 와 같은 판단, 상한은 Run 과 같게 전체 입장 때는 남은 예산을 씀)
                if (PartialCapMib(g, hotMib, WindowAvailableMib(avail), physCap, zoneMib <= room ? long.MaxValue : room) is not { } cap)
                {
                    continue; // 이 게임만 건너뜀: 뒤 게임은 남은 예산으로 RunBatch 와 같이 따로 판단
                }

                take = cap;
                head = g with { MaxStreamMib = (int)cap, MaxStreamSet = true }; // 앞부분 읽기도 상한을 지킴 (tier0 가 cap 보다 커도)
                skip = lowMemory ? null : skip;
            }

            if (IsCold(g) || skip is not null)
            {
                continue;
            }

            planned += take;
            lock (_run)
            {
                if (_games.Any(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase) && GamePolicy.AllowsPrewarm(x)))
                {
                    StreamHead(head, ct, allowWhileExiting); // 대기 중 등록이 해제됐거나 정책이 예열을 막은 게임은 앞부분도 읽지 않음
                }
            }
        }
    }

    private void StreamHead(GameSettings game, CancellationToken ct, bool allowWhileExiting)
    {
        using IDisposable work = TrackGameWork(game.Name);
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "warm heads", ct, preserveForGameRoot: _auto.KeepStreamOnGameStart ? game.OriginRoot : null, preserveGameKey: game.Name, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return;
        }

        using var stop = new CancellationTokenSource();
        if (!TryMarkReading(game.Name, () =>
        {
            (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
            _yield = null;
            _driveObservedAt = _now();
        }))
        {
            return; // 같은 디스크에서 재배치가 시작함: 앞부분은 읽지 않음 (전체 예열 Run 이 같은 이유로 건너뜀을 남김)
        }

        InvalidateResidencyProbe(game.Name);
        try
        {
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(stop.Cancel) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() =>
            {
                if (!_keepStream)
                {
                    stop.Cancel();
                }
            });
            lease?.NoteReadIssue("head schedule", 0, false);
            var cancelDetail = new PrewarmCancelDetail();
            _streamer.StreamHeadCoordinated(game, stop.Token, (done, total) => CheckPrewarmBatch(game, stop, done, total,
                    detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default),
                () => !stop.IsCancellationRequested && (lease?.IsValid ?? true),
                (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            if (stop.IsCancellationRequested)
            {
                LogSkipped(game.Name, "head cancelled " + (cancelDetail.First ?? "cancelled externally"),
                    $"{game.Name}: load head prewarm cancelled ({cancelDetail.First ?? "cancelled externally"})");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Info($"{game.Name}: load head prewarm cancelled (planning)"); // 계획 도중 대여가 취소됨: 서비스 종료가 아니면 RunBatch 를 계속함
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: load head prewarm failed: {e.GetType().Name}"); // 전체 예열(Run)이 같은 오류를 판단해 기록함
        }
        finally
        {
            (_streaming, _streamStop) = (null, null);
            LogYield(game.Name, 0, lease);
        }
    }

    // 미플레이 게임은 예산에도 표본 읽기에도 넣지 않습니다 (플레이한 게임이 우선, 미플레이 표본을 HDD 에서 읽지 않음).
    // 기록 없이 예열한 게임들이 지금도 RAM 에 있다고 보는 양 (MiB): 기록한 양이되, 표본을 다시 읽어 식었으면 (CacheProbe) 0 으로 바꿉니다. 영구히 예산을 막지 않게.
    // 표본을 읽을 수 없으면 (null) 기록한 양을 그대로 씁니다. 표본 읽기는 잠금 밖에서.
    private long ColdMib(string? except)
    {
        List<(string Name, IReadOnlyList<(string Path, long Offset, int Bytes)>? Samples)> held;
        lock (_gate)
        {
            held = [.. _coldMib.Where(kv => kv.Value > 0 && HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Select(kv => (kv.Key, _coldSamples.GetValueOrDefault(kv.Key)))];
        }

        foreach ((string name, var samples) in held)
        {
            GameSettings? gm = _games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            bool busy = gm is not null && (DriveHasRunningGame(gm) || PolicyBlocksPrewarm(gm)); // 같은 디스크에서 게임이 도는 동안과 정책이 예열을 막은 게임은 표본을 읽지 않음
            if (!busy && gm is not null && samples is not null && ProbeSamplesForGame(gm, samples, "warm state sample") is { Cold: true })
            {
                lock (_gate)
                {
                    if (_coldMib.ContainsKey(name))
                    {
                        _coldMib[name] = 0;
                    }
                }

                _log.Info($"{name}: cold prewarm no longer counts against the budget (its samples left the file cache)");
            }
        }

        lock (_gate)
        {
            return _coldMib.Where(kv => HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
        }
    }

    // 한 게임의 예외가 나머지 게임의 예열을 막지 않게 합니다.
    internal PrewarmResult Guarded(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0, long? roomMib = null, bool trigger = false)
    {
        try
        {
            return Run(game, why, pin, ignoreCooldown, ct, usedMib, roomMib, trigger);
        }
        catch (Exception e)
        {
            _log.Error($"{game.Name}: prewarm failed ({why})", e);
            PrewarmResult failed = new(game.Name, false, "failed: " + e.GetType().Name, null, _now());
            TrackPendingWarm(game, failed, failed.At);
            return failed;
        }
    }

    /// 한 게임을 지금 예열 (조건을 만족할 때만). 동시에 하나만 돕니다.
    /// ct 는 스트림을 시작하기 전 (차례를 기다린 뒤) 과 핫 집합 스트림의 묶음 사이에서 확인합니다. 단 지금 읽는 그 게임이 시작되면 (NoteGameStart) 이 스트림도 다음 묶음 경계에서 멈춥니다 (keep_stream_on_game_start 가 켜졌을 때만 끝까지 읽음).
    /// usedMib: 같은 묶음에서 앞서 올린 핫 집합의 양 (기록 없는 예열의 예산에서 뺌). roomMib: 묶음에 남은 예산 (MiB), 핫 집합이 더 크면 들어가는 만큼만 읽음 (부분 예열).
    public PrewarmResult Run(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0, long? roomMib = null, bool trigger = false)
    {
        lock (_run)
        {
            using IDisposable work = TrackGameWork(game.Name);
            DateTimeOffset now = _now();
            if (ct.IsCancellationRequested)
            {
                string detail = _env.GameRunning(game) ? "game started"
                    : _coordinator?.IsRegistered(game.Name) == true ? "disk read lease refused" : "caller cancelled";
                LogSkipped(game.Name, "prewarm cancelled " + detail, $"{game.Name}: prewarm cancelled ({why}) after 0 MiB ({detail})");
                PrewarmResult cancelled = new(game.Name, false, Cancelled, null, now) { CancelDetail = detail };
                TrackPendingWarm(game, cancelled, now);
                return cancelled;
            }

            if (!_games.Any(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return new PrewarmResult(game.Name, false, "game removed", null, now); // 대기 중에 등록이 해제됨: 읽지 않음
            }

            if (IsRetryReason(why) && MemoryRetryTooSoon(game.Name))
            {
                LogSkipped(game.Name, "memory retry floor", $"{game.Name}: prewarm skipped (stopped by memory less than {MemoryRetryFloor.TotalMinutes:F0} min ago) [{why}]");
                return new PrewarmResult(game.Name, false, "memory retry floor", null, now); // 대기 기한은 그대로 둠
            }

            if (PolicyBlocked(game, $"prewarm [{why}]"))
            {
                PrewarmResult blocked = new(game.Name, false, GamePolicySkip, null, now); // 상태 파일 이력은 건드리지 않음
                TrackPendingWarm(game, blocked, now);
                return blocked;
            }

            if (string.Equals(why, "rewarm", StringComparison.Ordinal) && RewarmAlreadyCovered(game))
            {
                PrewarmResult covered = new(game.Name, false, "prewarm already running or recently completed", null, now);
                TrackPendingWarm(game, covered, now);
                return covered;
            }

            if (Deferred(game))
            {
                PrewarmResult deferred = new(game.Name, false, PostUpdateDelay, null, now);
                TrackPendingWarm(game, deferred, now);
                return deferred;
            }

            if (IsCold(game) && !(_env.ColdRefusal(game) is null && _env.PrefetchBoot(game))) // S2 가 먼저: boot 트레이스가 생기면 더는 기록 없는 게임이 아님
            {
                return RunCold(game, why, now, ct, usedMib, trigger);
            }

            TimeSpan? since = !ignoreCooldown && LastWarmed(game.Name) is { } prev ? now - prev : null;
            long? zoneBytes = _env.ZoneBytes(game);
            long availableRamMib = _env.AvailableRamMib();
            string? Skip(long? bytes) => PrewarmPolicy.SkipReason(bytes, _env.TraceExists(game), DriveHasRunningGame(game), availableRamMib,
                game.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(game.Prewarm.CooldownMin));
            string? skip = Skip(zoneBytes);
            long? hotBytes = zoneBytes; // 읽을 핫 집합의 크기 (실제 읽을 집합을 알면 그것)
            if (skip?.StartsWith("low memory", StringComparison.Ordinal) == true && ReadSetBytes(game) is { } readBytes && readBytes < zoneBytes)
            {
                skip = Skip(readBytes); // 존 크기가 아니라 실제 읽을 집합 + 예비가 들어가면 진행
                hotBytes = readBytes;
            }

            GameSettings warm = game;
            (long WarmedMib, long HotMib)? memoryPartial = null;
            long originalHotMib = 0;
            if (hotBytes is > 0 and long hotRead)
            {
                long hotMib = (hotRead + (1 << 20) - 1) >> 20;
                long originalMib = hotMib;
                lock (_gate)
                {
                    if (_startupTrim.TryGetValue(game.Name, out (long WarmedMib, long HotMib) trim))
                    {
                        originalMib = trim.HotMib;
                    }
                }

                originalHotMib = originalMib;
                bool lowMemory = skip?.StartsWith("low memory", StringComparison.Ordinal) == true;
                bool overBudget = skip is null && roomMib is { } room && hotMib > room;
                if (lowMemory || overBudget)
                {
                    if (PartialCapMib(game, hotMib, WindowAvailableMib(availableRamMib), PhysCapMib(), roomMib ?? long.MaxValue) is { } capMib)
                    {
                        // 전체가 들어가지 않음: 앞쪽부터 들어가는 만큼만 읽음 (max_stream_mib 를 줄인 사본). 틈 메우기는 생략.
                        // 전체 크기는 원래 값을 유지: 서비스 시작 상한으로 이미 줄인 사본이면 그 원래 크기.
                        memoryPartial = (capMib, originalMib);
                        warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                        skip = null;
                    }
                    else if (overBudget)
                    {
                        skip = "batch memory budget";
                    }
                }
            }

            if (skip is null)
            {
                using PrewarmRun run = PrewarmRun.Begin(); // 이 예열 한 회 동안만 계획을 재사용하고 단계 시간을 잼
                long? finalReadBytes = ReadSetBytes(warm, partial: memoryPartial is not null);
                availableRamMib = _env.AvailableRamMib();
                if (finalReadBytes is > 0 and long finalReadSize)
                {
                    long finalReadMib = (finalReadSize + (1 << 20) - 1) >> 20;
                    long reserveMib = Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib);
                    long allowedMib = Math.Min(Math.Min(PhysCapMib(), roomMib ?? long.MaxValue), WindowAvailableMib(availableRamMib) - reserveMib); // 전체가 들어가는지도 창과 같은 여유를 둠: 경계에서 0 MiB 정지가 되풀이되지 않게
                    if (finalReadMib > allowedMib)
                    {
                        if (PartialCapMib(game, finalReadMib, WindowAvailableMib(availableRamMib), PhysCapMib(), roomMib ?? long.MaxValue) is { } capMib)
                        {
                            originalHotMib = Math.Max(originalHotMib, finalReadMib);
                            memoryPartial = (capMib, originalHotMib);
                            warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                            _log.Info($"{game.Name}: prewarm memory budget fell after planning; reading the first {capMib} MiB of {originalHotMib} MiB");
                        }
                        else
                        {
                            string reason = $"low memory (available {availableRamMib} MiB, reserve {reserveMib} MiB, final set {finalReadMib} MiB; less than {MinPartialMib} MiB fits)";
                            LogSkipped(game.Name, reason, $"{game.Name}: prewarm skipped ({reason}) [{why}]");
                            return Record(game, now, new PrewarmResult(game.Name, false, reason, null, now), 0);
                        }
                    }
                }

                if (warm.MaxStreamSet && !DriveHasRunningGame(game))
                {
                    _ = _env.ObservedBytes(game); // 줄여 읽기 전에 출처 정보를 캐시에 올림 (스케줄 단계는 캐시만 씀)
                }

                _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                if (memoryPartial is { } running)
                {
                    lock (_gate)
                    {
                        _memoryRunning[game.Name] = running;
                    }
                }

                // 스트림이 끝났다는 알림이 나가기 전에 부분 예열 상태를 확정합니다 (자동 상태가 옛 상태를 보고 표시를 만들지 않게). 읽은 양은 설정한 상한이 아니라 실제 스트림 통계.
                void Settle(StreamStats stats)
                {
                    string? inputs = PrewarmRun.InputStamp(game);
                    lock (_gate)
                    {
                        _memoryRunning.Remove(game.Name);
                        _memoryStopAt.Remove(game.Name);
                        if (stats.Bytes <= 0)
                        {
                            return; // 아무것도 읽지 못함: 성공이 아니므로 부분 상태도 만들지 않음
                        }

                        _supplementNothingNew.Remove(game.Name);
                        _resume.Remove(game.Name); // 일반 예열이 부분 상태를 교체함: 보충의 옛 이어 읽기 위치는 낡음

                        if (memoryPartial is { } planned)
                        {
                            SetMemoryPartialLocked(game, stats, planned.WarmedMib, planned.HotMib, inputs);
                        }
                        else
                        {
                            _memoryPartial.Remove(game.Name); // 메모리 때문에 줄이지 않은 예열이 끝남
                            if (_games.FirstOrDefault(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase))?.MaxStreamMib == game.MaxStreamMib)
                            {
                                _partial.Remove(game.Name); // 설정 상한 그대로 읽었으면 남은 구간이 없음 (서비스 시작 상한으로 줄인 사본은 다름)
                                _supplementReadProgress.Remove(game.Name);
                                _supplementRetry.Remove(game.Name);
                            }
                        }
                    }
                }

                try
                {
                    if (StreamHot(warm, ct, zoneBytes, availableRamMib, memoryPartial is not null, Settle,
                        allowReorder: memoryPartial is null && !HasPartialState(game.Name), roomMib: roomMib ?? long.MaxValue, allowWhileExiting: trigger) is not { } hot)
                    {
                        LogSkipped(game.Name, RelocationRunning, $"{game.Name}: prewarm skipped ({RelocationRunning}) [{why}]");
                        return Record(game, now, new PrewarmResult(game.Name, false, RelocationRunning, null, now), 0);
                    }

                    (StreamStats s, bool complete, string? cancelReason, bool memoryStop) = hot;
                    if (!complete)
                    {
                        string reason = cancelReason ?? "cancelled externally";
                        long? keptMib = memoryStop ? NoteMemoryStop(game, s, memoryPartial) : null; // Record 가 상태 파일에 쓰기 전에 읽은 데까지를 부분 상태로 확정
                        LogSkipped(game.Name, "prewarm cancelled " + reason, () => $"{game.Name}: prewarm cancelled ({why}) after {s.Bytes >> 20} MiB ({reason})");
                        if (keptMib is { } kept)
                        {
                            _log.Info($"{game.Name}: prewarm stopped by memory; kept the {kept} of {memoryPartial?.HotMib} MiB read as partial");
                        }

                        PrewarmResult stopped = Record(game, now, new PrewarmResult(game.Name, false, Cancelled, s, now) { CancelDetail = reason }, 0);
                        if (memoryStop && s.Bytes > 0)
                        {
                            ExpediteMemoryRetry(game.Name); // 읽은 게 없는 정지 (0 MiB) 는 기존 재시도 주기를 유지
                        }

                        return stopped;
                    }

                    if (s.Bytes <= 0)
                    {
                        _log.Info($"{game.Name}: prewarm read nothing ({why}), not recorded as done");
                        return Record(game, now, new PrewarmResult(game.Name, false, "nothing read", s, now), 0);
                    }

                    DateTimeOffset warmedAt = _now();
                    lock (_gate)
                    {
                        _last[game.Name] = warmedAt;
                        _coldMib.Remove(game.Name); // 기록이 생겨 핫 집합으로 바뀜
                        _coldSamples.Remove(game.Name);
                    }

                    double mbps = s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0;
                    string kind = memoryPartial is { } part ? $"partial {ActualMib(s, part.WarmedMib)}/{part.HotMib} MiB, the rest does not fit in memory: " : string.Empty;
                    _log.Info($"{game.Name}: prewarm done ({why}): {kind}{s.Bytes >> 20} MiB requested, {s.GapBytes / 1048576.0:F2} MiB gaps filled in {s.Seconds:F2} s = {mbps:F0} hint MB/s, {s.Requests} requests, {s.Errors} errors, room {(roomMib is { } roomLimit ? roomLimit + " MiB" : "unlimited")}, available {_env.AvailableRamMib()} MiB");
                    _log.Info($"{game.Name}: prewarm phases ({why}): {run.Summary()}");
                    PrewarmResult done = Record(game, warmedAt, new PrewarmResult(game.Name, true, null, s, warmedAt), mbps);
                    WarmMeasured?.Invoke(game, mbps);
                    if (pin && !ct.IsCancellationRequested && _games.Any(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pins.TryPin(game, why == AfterRelocation);
                    }

                    return done;
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    skip = "no trace file";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
                {
                    _log.Error($"{game.Name}: prewarm failed ({why})", e);
                    return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
                }
                finally
                {
                    lock (_gate)
                    {
                        _memoryRunning.Remove(game.Name);
                    }
                }
            }

            LogSkipped(game.Name, skip, () => $"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
            return Record(game, now, new PrewarmResult(game.Name, false, skip, null, now), 0);
        }
    }

    // 메모리 부분 예열 상태를 실제 읽은 양으로 확정 (_gate 안에서 부름): 완전 예열로 기록하지 않고, 나머지는 메모리가 날 때 보충 예열이 읽고, 재예열 점검은 읽은 앞부분만 잼.
    private void SetMemoryPartialLocked(GameSettings game, StreamStats stats, long windowMib, long hotMib, string? inputs)
    {
        (long WarmedMib, long HotMib) actual = (ActualMib(stats, windowMib), hotMib);
        (_memoryPartial[game.Name], _partial[game.Name]) = (actual, actual);
        _supplementReadProgress[game.Name] = new(stats.Bytes, inputs, game);
        _supplementRetry.Remove(game.Name);
    }

    // 읽는 도중 메모리 판정으로 멈춘 예열: 멈춘 시각을 기억하고, 줄여 읽던 창 (plan) 이 틈·오류·순서 바꿈 없이 읽은 만큼을 부분 예열로 남김. 남긴 양 (MiB), 안 남겼으면 null.
    // 전체 계획 경로 (plan 없음) 와 0 MiB 는 남기지 않음: 읽은 곳이 목록 앞쪽과 같다고 보장할 수 없음 (보충의 NoteProgress 규칙과 같게).
    // shortcut: 위치는 창 스케줄 기준의 근사 (접근 순서 head 를 넘는 구간은 물리 순서라 목록 앞쪽과 다를 수 있음), 어긋난 구간은 재예열 점검의 표본이 식은 것으로 잡아 다시 읽음.
    private long? NoteMemoryStop(GameSettings game, StreamStats stats, (long WarmedMib, long HotMib)? plan)
    {
        bool positional = plan is not null && stats.Bytes > 0 && stats.GapBytes == 0 && stats.Errors == 0 && !_streamer.WasPrewarmReordered(game);
        string? inputs = positional ? PrewarmRun.InputStamp(game) : null;
        lock (_gate)
        {
            _memoryStopAt[game.Name] = _now();
            if (!positional)
            {
                return null;
            }

            (long windowMib, long hotMib) = plan!.Value;
            long warmedMib = ActualMib(stats, windowMib);
            if (warmedMib >= hotMib)
            {
                return null;
            }

            _supplementNothingNew.Remove(game.Name);
            _resume.Remove(game.Name); // 부분 상태를 새로 정함: 보충의 옛 이어 읽기 위치는 낡음
            SetMemoryPartialLocked(game, stats, windowMib, hotMib, inputs);
            return warmedMib;
        }
    }

    // 메모리로 멈춘 게임의 재시도 대기를 한 주기 대신 MemoryRetryFloor 뒤로 당김 (기한을 뒤로 밀지는 않음). 메모리가 회복됐는지는 다시 읽을 때 창 계산이 확인함.
    private void ExpediteMemoryRetry(string game)
    {
        lock (_gate)
        {
            DateTimeOffset soon = _now() + MemoryRetryFloor;
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending) && pending.RetryAt > soon)
            {
                _pendingWarm[game] = pending with { RetryAt = soon };
            }
        }
    }

    private bool HasMemoryStop(string game)
    {
        lock (_gate)
        {
            return _memoryStopAt.ContainsKey(game);
        }
    }

    // 메모리로 멈춘 지 MemoryRetryFloor 가 안 지났는가.
    private bool MemoryRetryTooSoon(string game)
    {
        lock (_gate)
        {
            return _memoryStopAt.TryGetValue(game, out DateTimeOffset at) && _now() - at < MemoryRetryFloor;
        }
    }

    // 실제 읽기 집합 크기. 부분 예열은 스트림처럼 마지막 범위를 자르고, 조회하지 못하면 null입니다.
    private long? ReadSetBytes(GameSettings game, bool partial = false)
    {
        try
        {
            return _streamer.PrewarmReadBytes(game, partial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return null;
        }
    }

    private void CheckPrewarmBatch(GameSettings game, CancellationTokenSource stop, long done, long total, bool reserveFinalSet = false,
        PrewarmCancelDetail? detail = null, CancellationToken callerToken = default, CancellationToken leaseToken = default)
    {
        if (stop.IsCancellationRequested)
        {
            string cancellation = leaseToken.IsCancellationRequested ? "disk read lease revoked"
                : callerToken.IsCancellationRequested ? "caller cancelled"
                : _streamGameStarted ? "game started on the same disk"
                : "cancelled externally";
            detail?.Set(cancellation);
            return;
        }

        long remaining = Math.Max(0, total - done);
        long remainingMib = (remaining + ((1 << 20) - 1)) >> 20;
        long availableRamMib = _env.AvailableRamMib();
        long finalSetMib = (total + ((1 << 20) - 1)) >> 20;
        bool finalSetExceedsReserve = reserveFinalSet &&
            (availableRamMib < GapFillReserveMib || finalSetMib > availableRamMib - GapFillReserveMib);
        // keep_stream_on_game_start 가 켜졌으면 읽던 게임 자신의 실행만으로는 취소하지 않음 (같은 디스크의 다른 게임 실행은 항상 봄). 메모리 검사는 그대로.
        string? reason = null;
        bool memoryReason = false;
        Func<GameSettings, bool>? snapshot = null;
        bool running(GameSettings g) => (snapshot ??= _env.GameRunningSnapshot?.Invoke() ?? _env.GameRunning)(g); // 처음 필요할 때 한 번만 찍어 묶음 안의 판정이 나눠 씀
        if (RunningBlocksRead(game, running))
        {
            reason = running(game) ? "game started" : "another game is running on the same disk";
        }
        else if (PolicyBlocksPrewarm(game))
        {
            reason = "game policy"; // 읽는 도중 정책이 바뀌면 다음 묶음 경계에서 멈춤
        }
        else if (remaining > 0 && ExternalLoadWhilePaused(game, stop.Token, running))
        {
            reason = "disk busy after pause";
        }
        else if (remaining > 0 && _env.DiskSample?.Invoke(game) is { } sample
            && sample.State is DiskActivity.SampleState.Warming or DiskActivity.SampleState.Stale)
        {
            reason = "disk sample " + sample.State.ToString().ToLowerInvariant();
        }
        else if (finalSetExceedsReserve)
        {
            reason = $"memory reserve: available {availableRamMib} MiB, final set {finalSetMib} MiB + reserve {GapFillReserveMib} MiB";
            memoryReason = true;
        }
        else if (!PrewarmPolicy.HasMemoryBudget(remainingMib, availableRamMib, game.Prewarm.MinFreeRamMib))
        {
            reason = $"memory budget: available {availableRamMib} MiB, remaining {remainingMib} MiB + reserve {game.Prewarm.MinFreeRamMib} MiB";
            memoryReason = true;
        }

        if (reason is not null)
        {
            detail?.Set(reason, memoryReason);
            stop.Cancel();
        }
    }

    // 핫 집합 스트림 한 번. 게임이 시작되면 (NoteGameStart) 다음 묶음 경계에서 멈춥니다. 단 keep_stream_on_game_start 가 켜졌고 그 게임의 스트림이면 ct 가 취소돼도 끝까지 읽습니다.
    // 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    // settle: 끝까지 읽었을 때 StreamEnded 알림보다 먼저 부름 (읽은 통계를 받아 부분 예열 상태를 확정하거나 지움).
    // skipBytes: 읽기 목록 앞쪽 이만큼은 이미 읽었으니 건너뜀 (끊긴 보충을 이어 읽음, 틈 메우기 없는 경로에서만). onTotal: 이번에 읽을 목록의 길이 (건너뛴 앞부분 제외).
    private (StreamStats Stats, bool Complete, string? CancelReason, bool MemoryStop)? StreamHot(GameSettings game, CancellationToken ct, long? zoneBytes, long availableRamMib, bool partial = false, Action<StreamStats>? settle = null, long skipBytes = 0, Action<long>? onTotal = null, bool allowReorder = true, long roomMib = long.MaxValue, bool allowWhileExiting = false)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "prewarm stream", ct, preserveForGameRoot: _auto.KeepStreamOnGameStart ? game.OriginRoot : null, preserveGameKey: game.Name, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false, "disk read lease refused", false);
        }

        using var stop = new CancellationTokenSource();
        using var calibration = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var cancelDetail = new PrewarmCancelDetail();
        if (!TryMarkReading(game.Name, () =>
        {
            _streamGameStarted = false;
            _calibrationStop = calibration;
            (_keepStream, _streaming, _streamStop, _streamCancelDetail) = (false, game.Name, stop, cancelDetail);
            _yield = null;
            _driveObservedAt = _now();
        }))
        {
            return null;
        }

        InvalidateResidencyProbe(game.Name);
        using PrewarmRun run = PrewarmRun.Begin(); // 보충 등 바깥 회가 없는 경로도 이 스트림 한 번 동안은 계획을 재사용
        DateTimeOffset? last = null;
        bool complete = false;
        StreamStats? finalStats = null;
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(() =>
            {
                cancelDetail.Set("disk read lease revoked");
                stop.Cancel();
            }) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() =>
            {
                if (!_keepStream)
                {
                    cancelDetail.Set("caller cancelled");
                    stop.Cancel();
                }
            });
            lease?.NoteReadIssue("prewarm planning", 0, false);
            long maxBytes = partial ? 0 : GapFillBudgetBytes(game, zoneBytes, _env.EffectiveZoneBytes(game), _env.PhysicalRamBytes(), availableRamMib); // 부분 예열은 틈 메우기 예산 0
            if (!partial)
            {
                long allocationMib = Math.Min(PhysCapMib(), roomMib);
                long allocationBytes = allocationMib > (long.MaxValue >> 20) ? long.MaxValue : allocationMib << 20;
                maxBytes = Math.Min(maxBytes, allocationBytes);
            }
            if (_env.DeviceCalibration is not null && (_env.DeviceCalibrationEnabled?.Invoke() ?? true)) // 훅이 없거나 off 면 기준선과 같은 호출만 남김
            {
                using var calibrationTimer = PrewarmRun.Time(PrewarmPhase.Calibration);
                IReadOnlyList<ReadOp> calibrationOps = _streamer is PrefetchStreamer ? PrefetchStreamer.IncumbentPrewarmOps(game, maxBytes, calibration.Token) : Array.Empty<ReadOp>();
                calibration.Token.ThrowIfCancellationRequested();
                lease?.NoteReadIssue("device calibration", calibrationOps.Sum(op => op.Length), true);
                CalibrateDevice(game, calibrationOps, _env.AntiCheatOf(game), lease, calibration.Token);
            }

            calibration.Token.ThrowIfCancellationRequested();
            using (PrewarmRun.Time(PrewarmPhase.Common))
            {
                WarmCommonFirst(game, stop, maxBytes, skipBytes, lease, cancelDetail, ct);
            }

            calibration.Token.ThrowIfCancellationRequested();
            (StreamStats, bool) r = _streamer.StreamPrewarmCoordinated(game, stop.Token, (done, total) =>
            {
                onTotal?.Invoke(total);
                CheckPrewarmBatch(game, stop, done, total, reserveFinalSet: true, detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default);
                if (done == 0)
                {
                    return;
                }

                DateTimeOffset at = _now();
                if (last is null || at - last >= ProgressEvery)
                {
                    last = at;
                    Progress?.Invoke(game, done >> 20, (total + (1 << 20) - 1) >> 20);
                }
            }, maxBytes, skipBytes, () => !stop.IsCancellationRequested && (lease?.IsValid ?? true), allowReorder,
                (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            complete = r.Item2 && !stop.IsCancellationRequested;
            finalStats = r.Item1;
            LogYield(game.Name, r.Item1.Bytes, lease);
            return (r.Item1, complete, cancelDetail.First, cancelDetail.Memory);
        }
        catch (OperationCanceledException) when (calibration.IsCancellationRequested)
        {
            StreamStats cancelled = new(0, 0, 0, 0, Aborted: true);
            finalStats = cancelled;
            LogYield(game.Name, 0, lease);
            return (cancelled, false, cancelDetail.First ?? (lease?.Token.IsCancellationRequested == true ? "disk read lease revoked" : ct.IsCancellationRequested ? "caller cancelled" : "cancelled externally"), cancelDetail.Memory);
        }
        finally
        {
            bool interrupted = !complete && _streamGameStarted;
            _calibrationStop = null;
            (_streaming, _streamStop, _streamCancelDetail) = (null, null, null);
            // shortcut: 스트림 PDH 는 순차 탐침과 비교할 수 없어 교차 확인을 배선하지 않음, 실측 이탈이 확인되면 끝까지 읽은 스트림의 실제 읽기 바이트/시간으로 다시 연결
            try
            {
                _env.OrbitExposure?.Invoke(game, Stopwatch.GetElapsedTime(startedAt).TotalSeconds, interrupted);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{game.Name}: orbit exposure update skipped: {e.GetType().Name}");
            }

            if (complete && finalStats is not null)
            {
                settle?.Invoke(finalStats);
            }

            StreamEnded?.Invoke(game, complete && (finalStats is { Bytes: > 0 } || skipBytes > 0)); // 아무것도 읽지 못했으면 예열 완료로 알리지 않음 (이어 읽기의 0 바이트는 앞에서 이미 읽은 것)
        }
    }

    // 장치 표본 훅: 실패해도 스트림은 그대로 진행합니다 (취소는 아래 스트림이 판단).
    private void CalibrateDevice(GameSettings game, IReadOnlyList<ReadOp> ops, string? antiCheat, DiskReadCoordinator.Lease? lease, CancellationToken ct)
    {
        try
        {
            _env.DeviceCalibration?.Invoke(game, ops, antiCheat, lease, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 취소 뒤에는 다음 읽기를 발행하지 않습니다.
        }
        catch (Exception e)
        {
            _log.Warn($"{game.Name}: device calibration skipped: {e.GetType().Name}");
        }
    }

    // 모드·맵을 모르는 시작 예열: 여러 회차에서 반복해 읽힌 common 을 핫 집합 스트림보다 먼저 올립니다 (common 이 없으면 아무것도 안 해 기존 동작 그대로).
    // 이번 스트림이 읽을 범위의 부분집합만 읽어 RAM 예산은 그대로이고, map_initial (일부 회차에서만 읽힌 나머지) 은 기존 스트림이 이어 읽어 이득이 줄지 않습니다 (common 은 이미 올라와 있어 다시 읽어도 빠름).
    // 디스크 대여·중단 신호는 StreamHot 의 것을 그대로 씁니다. 실패하면 건너뛰고 스트림이 같은 오류를 판단합니다.
    // 최종 예약 검사는 common 을 읽기 전에 이번 스트림 전체 양으로 합니다 (거절이면 common 도 읽지 않음).
    private void WarmCommonFirst(GameSettings game, CancellationTokenSource stop, long maxBytes, long skipBytes, DiskReadCoordinator.Lease? lease,
        PrewarmCancelDetail cancelDetail, CancellationToken callerToken)
    {
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            (IReadOnlyList<ReadOp> ops, long streamTotal) = _env.CommonFirstOpsWithCancellation is { } checkedOps
                ? checkedOps(game, maxBytes, skipBytes, stop.Token)
                : _env.CommonFirstOps(game, maxBytes, skipBytes);
            if (stop.IsCancellationRequested || ops.Count == 0)
            {
                return;
            }

            CheckPrewarmBatch(game, stop, 0, streamTotal, reserveFinalSet: true, detail: cancelDetail, callerToken: callerToken, leaseToken: lease?.Token ?? default);
            if (stop.IsCancellationRequested || lease is { IsValid: false })
            {
                if (lease is { IsValid: false })
                {
                    cancelDetail.Set("disk read lease revoked");
                    stop.Cancel();
                }

                return;
            }

            long total = ops.Sum(o => o.Length);
            Action<long, long> progress = (done, planned) =>
            {
                CheckPrewarmBatch(game, stop, done, planned, detail: cancelDetail, callerToken: callerToken, leaseToken: lease?.Token ?? default);
                if (lease is { IsValid: false })
                {
                    cancelDetail.Set("disk read lease revoked");
                    stop.Cancel();
                }
            };
            (StreamStats Stats, bool Complete) read;
            if (_env.ColdReadWithIssue is { } withIssue)
            {
                read = withIssue(ops, stop.Token, progress, (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            }
            else if (_env.ColdReadWithProgress is { } withProgress)
            {
                lease?.NoteReadIssue("common-first batch", total, true);
                read = withProgress(ops, stop.Token, progress);
            }
            else
            {
                lease?.NoteReadIssue("common-first read", total, true);
                read = _env.ColdRead(ops, stop.Token);
            }
            _log.Info($"{game.Name}: common-first prewarm {read.Stats.Bytes >> 20}/{total >> 20} MiB" + (read.Complete ? string.Empty : " (stopped)"));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // 스트림 취소는 공통 구간 계산·읽기를 끝내고 호출부가 핫 스트림을 취소 처리하게 둡니다.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: common-first prewarm skipped: {e.GetType().Name}");
        }
    }

    // 기록 (원시 트레이스·prefetch 기록) 이 없는 게임: 설치 폴더가 예산 이하면 통째로, 크면 엔진 프로필로. 쿨다운은 항상 지킵니다 (같은 폴더를 연달아 두 번 읽지 않게): 재예열도 쿨다운 안이면 다음 점검으로 미룸.
    // 순서: 드라이브 거부 (시스템 SSD·SSD·판정 불가) → 게임 실행 중 → 쿨다운 → 디스크 유휴 → RAM 예산 → 폴더 열거 (비싼 일은 마지막).
    private PrewarmResult RunCold(GameSettings game, string why, DateTimeOffset now, CancellationToken ct, long usedMib, bool trigger)
    {
        AutoSettings a = _auto;
        TimeSpan cooldown = TimeSpan.FromMinutes(game.Prewarm.CooldownMin);
        if (_env.ColdRefusal(game) is { } refused)
        {
            bool first;
            lock (_gate)
            {
                first = _refusedLogged.Add(game.Name);
            }

            if (first)
            {
                _log.Info($"{game.Name}: prewarm without a record skipped ({refused}) [{why}]");
            }

            return new PrewarmResult(game.Name, false, refused, null, now); // 상태 파일에도 쓰지 않음: 기록 없는 예열 대상이 아닌 드라이브
        }

        string? skip = null;
        if (DriveHasRunningGame(game))
        {
            skip = "game is running";
        }
        else if (LastWarmed(game.Name) is { } prev && now - prev < cooldown)
        {
            skip = $"cooldown ({Math.Ceiling((cooldown - (now - prev)).TotalMinutes)} min left)";
        }
        else if (_env.DiskBytesPerSecond(game) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
        {
            skip = "drive busy";
        }

        long held = usedMib + ColdMib(game.Name);
        long avail = _env.AvailableRamMib();
        long availBudget = avail - game.Prewarm.MinFreeRamMib - held; // MiB
        long ram = Math.Min(availBudget, PhysCapMib() - held); // min(예열 예산, 가용 RAM - 여유) - 이미 올린 양
        long fullMib = !a.FullPrewarm ? 0 : Math.Min(a.FullPrewarmMaxMib > 0 ? a.FullPrewarmMaxMib : DefaultFullPrewarmMib, availBudget);
        long profileMib = !a.EngineProfiles ? 0 : a.FullPrewarm && a.FullPrewarmMaxMib > 0 ? Math.Min(ram, fullMib) : ram;
        if (skip is null && Math.Max(fullMib, profileMib) <= 0)
        {
            skip = $"low memory (available {avail} MiB, reserve {game.Prewarm.MinFreeRamMib} MiB, already held {held} MiB)";
        }

        StreamStats? stats = null;
        string? label = null;
        string? cancelReason = null;
        List<(string Path, long Offset, int Bytes)>? samples = null;
        if (skip is null)
        {
            try
            {
                string key = Directory.GetLastWriteTimeUtc(game.OriginRoot).Ticks.ToString(); // shortcut: 설치 폴더 자신의 mtime 만 봄 (하위 변경은 놓칠 수 있음), 폴더 mtime 이 바뀌면 다시 열거
                ColdPlan? plan = ProfileMissed(game.Name, key) ? null : _env.ColdPlanner!(game, Math.Max(0, fullMib) << 20, Math.Max(0, profileMib) << 20, ct);
                if (plan is null)
                {
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip == FolderWarm.NoEngineProfile)
                {
                    RememberProfileMiss(game.Name, key);
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip is not null)
                {
                    skip = plan.Skip;
                }
                else
                {
                    ForgetProfileMiss(game.Name);
                    _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                    if (ColdReadMarked(game, plan.Ops, ct, trigger) is not { } cold)
                    {
                        skip = RelocationRunning;
                    }
                    else
                    {
                        (stats, label, skip, cancelReason) = (cold.Stats, plan.Label, cold.Complete && !ct.IsCancellationRequested ? null : Cancelled, cold.CancelReason);
                        samples = CacheProbe.PickSamples(plan.Ops);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                skip = Cancelled;
                cancelReason = ct.IsCancellationRequested ? "caller cancelled" : "planning cancelled";
            }
            catch (DirectoryNotFoundException)
            {
                skip = "install folder not found";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Error($"{game.Name}: prewarm failed ({why})", e);
                return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
            }
        }

        if (skip is not null || stats is null)
        {
            if (skip == Cancelled)
            {
                string reason = cancelReason ?? "cancelled externally";
                long afterMib = stats is { } cancelledStats ? cancelledStats.Bytes >> 20 : 0;
                LogSkipped(game.Name, "prewarm cancelled " + reason, $"{game.Name}: prewarm cancelled ({why}) after {afterMib} MiB ({reason})");
            }
            else
            {
                _log.Info($"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
            }

            return Record(game, now, new PrewarmResult(game.Name, false, skip, stats, now), 0);
        }

        DateTimeOffset warmedAt = _now();
        lock (_gate)
        {
            _last[game.Name] = warmedAt;
            _coldMib[game.Name] = (stats.Bytes + (1 << 20) - 1) >> 20;
            _coldSamples[game.Name] = samples!;
        }

        double mbps = stats.Seconds > 0 ? stats.Bytes / 1048576.0 / stats.Seconds : 0;
        _log.Info($"{game.Name}: prewarm done ({why}, {label}): {stats.Bytes >> 20} MiB requested in {stats.Seconds:F2} s = {mbps:F0} hint MB/s, {stats.Requests} requests, {stats.Errors} errors");
        PrewarmResult done = Record(game, warmedAt, new PrewarmResult(game.Name, true, null, stats, warmedAt) { Mode = label }, mbps);
        WarmMeasured?.Invoke(game, mbps);
        ColdWarmed?.Invoke(game, label!);
        return done;
    }

    // 기록 없는 읽기 한 번: 읽는 동안 _coldReading 표시를 세우고 (예외·취소로 끝나도 내림), 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    private (StreamStats Stats, bool Complete, string? CancelReason)? ColdReadMarked(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken ct, bool allowWhileExiting)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "cold prewarm", ct, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false, "disk read lease refused");
        }

        if (!TryMarkReading(game.Name, () => _coldReading = game.Name))
        {
            return null;
        }

        InvalidateResidencyProbe(game.Name);
        try
        {
            var cancelDetail = new PrewarmCancelDetail();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? ct);
            long total = ops.Sum(o => o.Length);
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(() => cancelDetail.Set("disk read lease revoked")) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() => cancelDetail.Set("caller cancelled"));
            Action<long, long> progress = (done, planned) => CheckPrewarmBatch(game, stop, done, planned,
                detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default);
            (StreamStats Stats, bool Complete) read = _env.ColdReadWithProgress is { } withProgress
                ? withProgress(ops, stop.Token, progress)
                : _env.ColdRead(ops, stop.Token);
            if (_env.ColdReadWithProgress is null)
            {
                progress(read.Stats.Bytes, total);
            }

            return stop.IsCancellationRequested
                ? (read.Stats with { Aborted = true }, false, cancelDetail.First ?? "cancelled externally")
                : (read.Stats, read.Complete, cancelDetail.First);
        }
        finally
        {
            _coldReading = null;
        }
    }

    private bool ProfileMissed(string game, string key)
    {
        lock (_gate)
        {
            return _profileMiss.TryGetValue(game, out string? known) && known == key;
        }
    }

    private void RememberProfileMiss(string game, string key)
    {
        lock (_gate)
        {
            _profileMiss[game] = key;
        }
    }

    private void ForgetProfileMiss(string game)
    {
        lock (_gate)
        {
            _profileMiss.Remove(game);
        }
    }

    // 안티치트 게임은 기록 (트레이스) 을 만들지 않으므로 "trace needed" 라고 하지 않습니다.
    private string ProfileSkip(GameSettings game) => AntiCheat.ForbidsTrace(game.AntiCheat) || AntiCheat.ForbidsTrace(_env.AntiCheatOf(game)) ? "no profile" : "no profile; trace needed";

    private PrewarmResult Record(GameSettings game, DateTimeOffset now, PrewarmResult r, double mbps)
    {
        TrackPendingWarm(game, r, now);
        string? okAt = r.Ran ? now.ToString("O") : KeptOkAt(game.Name);
        JObject entry = new()
        {
            { "at", now.ToString("O") },
            { "result", r.Ran ? "ok" : r.SkipReason!.StartsWith("failed", StringComparison.Ordinal) ? "failed" : "skipped" },
            { "reason", r.SkipReason },
            { "bytes", r.Stats?.Bytes },
            { "hint_accepted_bytes", r.Stats?.HintAcceptedBytes },
            { "seconds", r.Stats?.Seconds },
            { "hint_mb_per_s", r.Stats is null ? null : Math.Round(mbps, 1) },
            { "last_ok_at", okAt },
        };
        lock (_gate)
        {
            if (_profileMiss.TryGetValue(game.Name, out string? miss))
            {
                entry["profile_miss_key"] = miss;
            }
        }
        if (r.Ran && r.Mode is null && _streamer.LoadHeadBytes(game) is { } head)
        {
            entry["head_mib"] = head >> 20; // 로딩 앞부분 (관리 창 상태 탭)
        }

        if (r.Mode is not null)
        {
            entry["mode"] = r.Mode;
        }

        lock (_gate)
        {
            if (r.Ran && r.Mode is null && _startupTrim.TryGetValue(game.Name, out (long WarmedMib, long HotMib) trim) && game.MaxStreamMib == trim.WarmedMib)
            {
                entry["warmed_mib"] = ActualMib(r.Stats, trim.WarmedMib); // 서비스 시작 상한으로 앞부분만 읽음 (실제 읽은 양) (보충 예열이 끝나면 둘이 같아짐)
                entry["hot_mib"] = trim.HotMib;
            }
        }

        lock (_gate)
        {
            if (r.Mode is null && _partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) part))
            {
                // 부분 예열 상태는 건너뜀·취소·실패 기록이 덮어쓰지 않음 (재시작 뒤에도 보충·표본 제한이 이어지게). 지우는 것은 완전 성공이나 보충 성공뿐.
                bool memory = _memoryPartial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) mem);
                if (memory)
                {
                    (part, entry["partial"]) = (mem, true); // 메모리가 모자라 앞부분만 읽음
                }

                if (!r.Ran || memory)
                {
                    entry["warmed_mib"] = part.WarmedMib;
                    entry["hot_mib"] = part.HotMib;
                }
            }

            _status[game.Name] = entry;
        }

        FlushStatus();
        return r;
    }

    /// 메모리가 모자라 앞부분만 읽은 게임이면 그 앞부분만 읽도록 줄인 사본 (재예열 점검의 표본을 읽은 구간에서만 고르게), 아니면 그대로.
    internal GameSettings WarmedPart(GameSettings g)
    {
        lock (_gate)
        {
            return _memoryPartial.TryGetValue(g.Name, out (long WarmedMib, long HotMib) mem) && _partial.ContainsKey(g.Name) ? g with { MaxStreamMib = (int)mem.WarmedMib, MaxStreamSet = true } : g;
        }
    }

    /// 부분 예열 중이거나 부분 예열로 끝난 게임의 (읽은 양, 전체) MiB, 아니면 null (상태 표시용).
    internal (long WarmedMib, long HotMib)? PartialInfo(string game)
    {
        lock (_gate)
        {
            if (_memoryRunning.TryGetValue(game, out (long WarmedMib, long HotMib) running))
            {
                return running;
            }

            return _memoryPartial.TryGetValue(game, out (long WarmedMib, long HotMib) mem) && _partial.ContainsKey(game) ? mem : null;
        }
    }

    /// 재예열 점검의 표본 측정: 부분 예열 상태면 읽은 앞부분의 잘라낸 목록에서만, 아니면 평소대로.
    internal ProbeResult? ProbeFront(GameSettings g, CancellationToken ct = default) =>
        MemoryPartialPending(g.Name) ? ProbeReadMsForGame(WarmedPart(g), "rewarm sample", ct, partial: true) : ProbeReadMsForGame(g, "rewarm sample", ct);

    internal bool MemoryPartialPending(string game)
    {
        lock (_gate)
        {
            return _memoryPartial.ContainsKey(game) && _partial.ContainsKey(game);
        }
    }

    private bool HasPartialState(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game) || _memoryPartial.ContainsKey(game) || _resume.ContainsKey(game);
        }
    }

    private void FlushStatus()
    {
        if (_statusPath is not null)
        {
            try
            {
                string tmp = _statusPath + ".tmp";
                string contents;
                lock (_gate)
                {
                    contents = PyJson.Dumps(new JObject { { "games", _status } }, indent: 2, ensureAscii: false) + "\n";
                }

                DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(contents));
                File.Move(tmp, _statusPath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("prewarm status file not written: " + e.GetType().Name);
            }
        }
    }
}

/// 가용 물리 메모리 (여유 + 대기 목록, GlobalMemoryStatusEx).
public static partial class SystemMemory
{
    public static long AvailableMib()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailablePhys >> 20) : 0;
    }

    /// 물리 RAM 전체 (바이트), 읽지 못하면 0.
    public static long TotalBytes()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? (long)s.TotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailablePhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
