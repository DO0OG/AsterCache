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

ILog log;
AsterSettings settings;
try
{
    ILog file = new FileLog(Path.Combine(dataDir, "logs", "service.log"));
    log = console ? new TeeLog(file) : file;
    settings = SettingsStore.Load(Path.Combine(dataDir, "config.json"), plansDir);
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
bool canApply = Worker.CanApply(admin, [dataDir, plansDir], log, [Path.Combine(dataDir, "config.json")]);
(IProcessWatcher primary, IProcessWatcher? fallback) = Worker.ChooseWatchers(settings, admin);
var checker = new ReapplyChecker(ReapplyEnv.Create(canApply, Path.Combine(dataDir, "journals")), log);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
if (!console)
{
    builder.Services.AddWindowsService(o => o.ServiceName = "AsterCache");
}

builder.Services.AddHostedService(_ => new Worker(settings, log, primary, fallback, new PrefetchStreamer(), checker));
await builder.Build().RunAsync();
return 0;

/// 콘솔 모드: 파일 로그와 같은 줄을 화면에도 냅니다.
internal sealed class TeeLog(ILog inner) : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        inner.Write(level, message, exception);
        Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {FileLog.Scrub(message)}" + (exception is null ? string.Empty : $" ({exception.GetType().Name})"));
    }
}
