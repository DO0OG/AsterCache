using System.Diagnostics;
using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tray;
using AsterCache.Windows;
using Avalonia;
using Avalonia.Controls;
using Velopack;

// 사용법: AsterCache.Tray [--data-dir <폴더>] [--lang ko|en] [--smoke]   (기본 폴더 %ProgramData%\AsterCache)
// 언어: --lang > 환경변수 ASTER_LANG > config.json 의 language (auto 는 OS 표시 언어). 화면 문구는 시작할 때 한 번 정합니다.
// --smoke: 트레이 아이콘과 메인 창을 만들고 3초 뒤 스스로 종료합니다 (알림 없음, 로그 한 줄). 종료 코드 0 정상, 2 설정 오류.
// --smoke --screenshot <png> [--tab <0..3>]: 메인 창을 1100x720 으로 그려 PNG 로 저장합니다 (드라이브 목록은 이 PC 대신 예시 값).
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        int at = Array.IndexOf(args, "--data-dir");
        string dataDir = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : SettingsStore.DefaultDataDir;
        int lang = Array.IndexOf(args, "--lang");
        L.Current = L.Resolve(lang >= 0 && lang + 1 < args.Length ? args[lang + 1] : null, Environment.GetEnvironmentVariable("ASTER_LANG"),
            SettingsStore.ReadLanguage(Path.Combine(dataDir, "config.json")));
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
        int shot = Array.IndexOf(args, "--screenshot");
        App.ScreenshotPath = shot >= 0 && shot + 1 < args.Length ? Path.GetFullPath(args[shot + 1]) : null;
        int tab = Array.IndexOf(args, "--tab");
        App.ScreenshotTab = tab >= 0 && tab + 1 < args.Length && int.TryParse(args[tab + 1], out int t) ? t : 0;
        var config = new ConfigEditor(dataDir);
        var elevator = new UacElevator();
        var shell = new ExplorerShell();
        App.Model = new TrayViewModel(config, new ScmServiceControl(elevator), new PrefetchStreamer(), shell, log, updates,
            ScmServiceControl.FileVersionOf(Environment.ProcessPath));
        App.Manage = new ManageViewModel(App.Model, config, new AsterProcess(elevator, Elevation.IsAdmin, log), App.ScreenshotPath is null ? new SystemDrives() : new SampleDrives(), elevator, shell, log, Elevation.IsAdmin,
            ProcessSnapshot.IsRunning, string.Join(' ', args.Select(AsterProcess.Quote)));
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

/// 문서용 그림에 쓰는 예시 드라이브 (--screenshot): 이 PC 의 볼륨 이름·모델이 그림에 남지 않게 합니다.
internal sealed class SampleDrives : IDriveSource
{
    public IReadOnlyList<DriveFacts> Fixed() =>
    [
        new("C:", "Windows", "NTFS", 476L << 30, 212L << 30, "Sample NVMe SSD", false),
        new("D:", "Games", "NTFS", 2000L << 30, 880L << 30, "Sample 2TB HDD", true),
    ];
}
