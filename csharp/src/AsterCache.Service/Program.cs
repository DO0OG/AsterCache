using AsterCache.Core.Logging;
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
    settings = SettingsStore.Load(configPath, plansDir);
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
ReapplyEnv reapplyEnv = ReapplyEnv.Create(canApply, journalDir, log, () => settings.Auto.RelocateOrder, g => settings.Auto.ParallelFor(ReapplyEnv.IsSlowBus(g)));
var checker = new ReapplyChecker(reapplyEnv, log);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
if (!console)
{
    builder.Services.AddWindowsService(o => o.ServiceName = "AsterCache");
}

var streamer = new PrefetchStreamer();
var prewarm = new Prewarmer(settings.Games, streamer, PrewarmEnv.Create(), log, canApply ? Path.Combine(dataDir, "prewarm_status.json") : null); // 데이터 폴더를 믿을 수 없으면 (SYSTEM 이 일반 사용자가 쓸 수 있는 곳에 쓰지 않도록) 상태 파일을 쓰지 않음
// 자동 모드는 설정·이동을 쓰므로 canApply 일 때만 (상태 파일도 같은 이유로)
var auto = canApply ? new AutoMode(new ConfigEditor(dataDir), AutoEnv.Create(journalDir, log), new AutoStatus(Path.Combine(dataDir, "auto_status.json"), log), log, settings, prewarm) : null;
if (auto is not null)
{
    reapplyEnv.Gate = auto.ReapplyBlock; // 정기 재적용도 자동 재배치와 같은 게이트를 지남
    reapplyEnv.Spent = auto.ReapplySpent;
}

var reloader = new SettingsReloader(() => SettingsReloader.StampOf(configPath), LoadChecked, s => Worker.ChooseWatchers(s, admin), log, configStamp);
builder.Services.AddHostedService(_ => new Worker(settings, log, primary, fallback, streamer, checker, prewarm: prewarm, auto: auto, reload: reloader));
await builder.Build().RunAsync();
return 0;

// 다시 읽을 때도 시작 때와 같은 권한 검사를 합니다 (그 사이 데이터 폴더·설정 파일을 일반 사용자가 쓸 수 있게 바뀌었으면 옛 설정 유지).
AsterSettings LoadChecked()
{
    if (canApply && (AclCheck.UntrustedFileWriters(configPath).Count > 0 || AclCheck.UntrustedWriters(dataDir).Count > 0 || AclCheck.UntrustedWriters(plansDir).Count > 0))
    {
        throw new SettingsException("config: data folder or file is writable by non-admin principals");
    }

    AsterSettings loaded = SettingsStore.Load(configPath, plansDir);
    if (canApply && loaded.Auto.Migrated)
    {
        Worker.RewriteMigrated(new ConfigEditor(dataDir), log);
    }

    return canApply || !loaded.Auto.Enabled ? loaded : loaded with { Auto = loaded.Auto with { Enabled = false } }; // 시작 때와 같이 자동 모드 끔
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
