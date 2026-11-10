using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Metrics;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 사용법: AsterCache.Service [--console [--watch-test <초>]] [--data-dir <폴더>]   (기본 폴더 %ProgramData%\AsterCache: config.json, plans\, journals\, logs\)
// 종료 코드: 0 정상, 2 설정 오류.
bool console = args.Contains("--console");
int at = Array.IndexOf(args, "--data-dir");
string dataDir = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : SettingsStore.DefaultDataDir;
string plansDir = Path.Combine(dataDir, "plans");
string configPath = Path.Combine(dataDir, "config.json");

ILog log;
AsterSettings settings;
(long Length, long Ticks)? configStamp = SettingsReloader.StampOf(configPath); // 읽기 직전에 재서 그 사이의 변경도 다시 읽게 함
try
{
    ILog file = new FileLog(Path.Combine(dataDir, "logs", "service.log"));
    log = console ? new TeeLog(file) : file;
    settings = AntiCheat.WithRiotRule(SettingsStore.Load(configPath, plansDir), LauncherScan.IsVanguardInstalled);
}
catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("AsterCache.Service: cannot start: " + FileLog.Scrub(e.Message));
    return 2;
}

int watchAt = Array.IndexOf(args, "--watch-test");
if (console && watchAt >= 0)
{
    // 진단: 서비스를 띄우지 않고 감시기만 돌려 본다 (ETW 가 시작 이벤트를 못 받는 원인을 가르는 용도)
    int seconds = watchAt + 1 < args.Length && int.TryParse(args[watchAt + 1], out int n) && n is > 0 and <= 3600 ? n : 30;
    return await WatchTest.RunAsync(settings.Games.SelectMany(g => g.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), seconds, Console.Out);
}

bool admin = Elevation.IsAdmin();
bool canApply = Worker.CanApply(admin, [dataDir, plansDir], log, [configPath]);
settings = Worker.ForApply(settings, canApply, log, canApply ? new ConfigEditor(dataDir) : null);
(IProcessWatcher primary, IProcessWatcher? fallback) = Worker.ChooseWatchers(settings, admin);
string journalDir = Path.Combine(dataDir, "journals");
ReapplyEnv reapplyEnv = ReapplyEnv.Create(canApply, journalDir, log, () => settings.Auto.RelocateOrder, g => ReapplyEnv.ParallelFor(settings.Auto, g));
var checker = new ReapplyChecker(reapplyEnv, log);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
if (!console)
{
    builder.Services.AddWindowsService(o => o.ServiceName = "AsterCache");
}

var streamer = new PrefetchStreamer();
DiskActivity.Start(); // 디스크 처리량 수집을 미리 켬 (첫 호출이 표본 없이 null 을 받는 구간을 줄임)
PrewarmEnv baseEnv = PrewarmEnv.Create();
AutoStatus? orbitStatus = canApply ? new AutoStatus(Path.Combine(dataDir, "auto_status.json"), log) : null;
var coordinator = new DiskReadCoordinator(VolumeDisks.Shared, log); // 서비스와 같은 대여 장부: 장치 표본이 게임 실행·종료 유예를 같은 등록으로 판단함
// 설정을 다시 읽어도 최신 등록 목록으로 판정 (등록된 같은 디스크 게임이 돌거나 종료 유예 중이면 참)
Func<GameSettings, bool> gameActiveOnDisk = g => PrewarmEnv.GameActiveOnDisk(settings.Games, baseEnv.SameDisk, other => baseEnv.GameRunning(other) || coordinator.IsRegistered(other.Name))(g);
OrbitCalibration? orbitCalibration = canApply
    ? new OrbitCalibration(dataDir, log, OrbitCalibration.UnbufferedRead, () => DateTimeOffset.UtcNow, DiskActivity.IsBusy, gameActiveOnDisk, Random.Shared, diskKey: OrbitCalibration.PhysicalDiskKey)
    : null;
OrbitRuntime orbitRuntime = new(dataDir, orbitCalibration, orbitStatus, log, enabled: canApply);
streamer.Reorder = canApply ? orbitRuntime.Reorder : null; // 스트림 중지 신호를 받는 오버로드
PrewarmEnv prewarmEnv = canApply ? PrewarmEnv.Create(orbitCalibration, orbitRuntime) : baseEnv;
var prewarm = new Prewarmer(settings.Games, streamer, prewarmEnv, log, canApply ? Path.Combine(dataDir, "prewarm_status.json") : null); // 데이터 폴더를 믿을 수 없으면 (SYSTEM 이 일반 사용자가 쓸 수 있는 곳에 쓰지 않도록) 상태 파일을 쓰지 않음
var playRecorder = new PlayRecorder(canApply ? Path.Combine(dataDir, "play_samples.json") : null, log);
LoadBoundaryWatch.OrbitApplied = canApply ? streamer.WasPrewarmReordered : _ => false;
LoadBoundaryWatch.Recorder = canApply ? new BenchmarkRecorder(Path.Combine(dataDir, MetricsJson.SamplesFile), log) : null; // 로딩 표본 기록 (상태 파일과 같은 이유로 canApply 일 때만)
if (LoadBoundaryWatch.Recorder is { } recorder)
{
    // ProbeReadyMs 는 대리 지표이므로 고정 시드 bootstrap 신뢰구간에서 유의한 악화가 확인될 때만 모드 변경 이후 표본으로 강등
    recorder.GateSince = () => orbitRuntime.ModeChangedAt;
    recorder.GateEvaluated = (gameName, result) =>
    {
        if (result.Verdict == ResultGate.Reject)
        {
            _ = orbitRuntime.Demote("result_gate_reject");
        }
    };
}
// 자동 모드는 설정·이동을 쓰므로 canApply 일 때만 (상태 파일도 같은 이유로)
var auto = canApply ? new AutoMode(new ConfigEditor(dataDir), AutoEnv.Create(journalDir, log), orbitStatus!, log, settings, prewarm) : null;
if (auto is not null)
{
    reapplyEnv.Gate = auto.ReapplyBlock; // 정기 재적용도 자동 재배치와 같은 게이트를 지남
    reapplyEnv.Started = auto.ReapplyStarted;
    reapplyEnv.Spent = auto.ReapplySpent;
    reapplyEnv.Options = auto.ReapplyOptions;
    reapplyEnv.WarnDegraded = auto.ReapplyWarnAllowed;
}

if (auto is not null)
{
    auto.BoundaryLease = LoadBoundaryWatch.LeaseFrom(coordinator);
}

var reloader = new SettingsReloader(() => SettingsReloader.StampOf(configPath), LoadChecked, s => Worker.ChooseWatchers(s, admin), log, configStamp);
builder.Services.AddHostedService(_ => new Worker(settings, log, primary, fallback, streamer, checker, prewarm: prewarm, auto: auto, reload: reloader, dispatchEnv: DispatchEnv.Create(), coordinator: coordinator, playRecorder: playRecorder));
await builder.Build().RunAsync();
DiskActivity.StopSampling();
return 0;

// 다시 읽을 때도 시작 때와 같은 권한 검사를 합니다 (그 사이 데이터 폴더·설정 파일을 일반 사용자가 쓸 수 있게 바뀌었으면 옛 설정 유지).
AsterSettings LoadChecked()
{
    if (canApply && (AclCheck.UntrustedFileWriters(configPath).Count > 0 || AclCheck.UntrustedWriters(dataDir).Count > 0 || AclCheck.UntrustedWriters(plansDir).Count > 0))
    {
        throw new SettingsException("config: data folder or file is writable by non-admin principals");
    }

    AsterSettings loaded = AntiCheat.WithRiotRule(SettingsStore.Load(configPath, plansDir), LauncherScan.IsVanguardInstalled);
    if (canApply && loaded.Auto.Migrated)
    {
        Worker.RewriteMigrated(new ConfigEditor(dataDir), log);
    }

    settings = canApply || !loaded.Auto.Enabled ? loaded : loaded with { Auto = loaded.Auto with { Enabled = false } }; // 시작 때와 같이 자동 모드 끔. 재적용 옵션 공급자가 최신 설정을 보도록 지역 변수도 갱신
    return settings;
}

/// 콘솔 모드: 파일 로그와 같은 줄을 화면에도 냅니다.
internal sealed class TeeLog(ILog inner) : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        inner.Write(level, message, exception);
        Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {FileLog.Scrub(message)}" + (exception is null ? string.Empty : $" ({exception.GetType().Name})"));
    }
}
