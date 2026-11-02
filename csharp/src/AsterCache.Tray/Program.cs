using System.Diagnostics;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tray;
using Avalonia;
using Avalonia.Controls;
using Velopack;

// 사용법: AsterCache.Tray [--data-dir <폴더>] [--smoke]   (기본 폴더 %ProgramData%\AsterCache)
// --smoke: 트레이 아이콘을 만들고 3초 뒤 스스로 종료합니다 (창·알림 없음, 로그 한 줄). 종료 코드 0 정상, 2 설정 오류.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        int at = Array.IndexOf(args, "--data-dir");
        string dataDir = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : SettingsStore.DefaultDataDir;
        ILog log = OpenLog(dataDir);
        TraySettings tray;
        try
        {
            tray = TraySettings.Load(Path.Combine(dataDir, "tray.json"));
        }
        catch (SettingsException e)
        {
            log.Error("cannot start: " + e.Message);
            return 2;
        }

        UpdateService? updates = tray.UpdateEnabled ? new UpdateService(new VelopackUpdateManager(tray.UpdateFeedUrl!), log) : null;
        App.Log = log;
        App.Smoke = args.Contains("--smoke");
        App.Model = new TrayViewModel(new ConfigEditor(dataDir), new ScmServiceControl(new UacElevator()), new PrefetchStreamer(), new ExplorerShell(), log, updates,
            ScmServiceControl.FileVersionOf(Environment.ProcessPath));
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// 데이터 폴더의 logs 에 씁니다. 일반 사용자가 쓸 수 없으면 사용자별 폴더로 대신합니다.
    private static FileLog OpenLog(string dataDir)
    {
        try
        {
            string path = Path.Combine(dataDir, "logs", "tray.log");
            var log = new FileLog(path);
            File.AppendAllText(path, string.Empty); // 데이터 폴더는 일반 사용자 쓰기 금지: FileLog 는 쓰기 실패를 삼키므로 여기서 미리 확인
            return log;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return new FileLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsterCache", "logs", "tray.log"));
        }
    }
}

internal sealed class ExplorerShell : IShell
{
    public void Open(string path) => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
}
