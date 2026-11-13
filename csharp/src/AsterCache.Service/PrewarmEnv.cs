using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

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

    /// 읽기를 멈춘 구간만 직접 잰 처리량과 상태 (게임, settle: 멈춘 직후 진행 중이던 읽기가 빠지길 기다림, window: 이 동안만 잼). 공유 표본 (DiskSample) 은 우리 읽기가 섞인 창일 수 있어 양보 관측은 이것을 씁니다.
    /// 없으면 (시험 기본값) 멈춤 전체 (settle + window) 를 한 번의 Sleep 으로 기다린 뒤 DiskSample 을 읽는 기존 방식.
    public Func<GameSettings, Action, Action, (double? Bps, DiskActivity.SampleState State)>? DiskWindowSample { get; init; }

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

    /// 현재 스레드의 읽기 I/O 우선순위를 낮음 (true) 또는 보통 (false) 으로 바꿈, 바꾸지 못하면 false. 핫 집합 스트림이 다른 읽기에 양보할 때 읽는 스레드에서 부릅니다. 시험 기본값은 항상 성공 (실제 호출 없음).
    public Func<bool, bool> SetReadIoLow { get; init; } = _ => true;

    /// 트리거로 예열이 실제로 시작되기 직전 (가용 메모리 첫 읽기보다 앞) 에 부름: 배경 프로세스의 메모리 양보를 시작하고 가용 메모리가 가라앉을 때까지 기다립니다 (이유, 취소). 기본은 아무것도 안 함.
    public Action<string, CancellationToken> YieldBackground { get; init; } = (_, _) => { };

    public static Func<GameSettings, bool> GameActiveOnDisk(IReadOnlyList<GameSettings> games,
        Func<string, string, bool> sameDisk, Func<GameSettings, bool> gameRunning) => OrbitRuntime.GameActiveOnDisk(games, sameDisk, gameRunning);

    public static PrewarmEnv Create(OrbitCalibration? orbitCalibration = null, OrbitRuntime? orbitRuntime = null, BackgroundYield? backgroundYield = null)
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
            SetReadIoLow = low => ThreadIoPriority.TrySet(low ? IoPriority.Low : IoPriority.Normal),
            YieldBackground = backgroundYield is null ? (_, _) => { } : (why, ct) => backgroundYield.Begin(why, settle: true, ct),
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
            DiskWindowSample = (g, settle, window) => DiskActivity.MeasureWindow(PlanFile.SplitDrive(g.OriginRoot), settle, window),
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
