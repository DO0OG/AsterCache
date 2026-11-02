using System.Runtime.InteropServices;
using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 트레이 아이콘과 메인 창(닫으면 트레이로)을 가진 앱. 메뉴 항목은 TrayViewModel 을 부르기만 합니다.
internal sealed class App : Application
{
    public static TrayViewModel Model { get; set; } = null!;

    public static ILog Log { get; set; } = null!;

    public static ManageViewModel Manage { get; set; } = null!;

    public static bool Smoke { get; set; }

    /// --smoke --screenshot <png>: 메인 창을 1100x720 으로 그려 저장하고 끝냅니다 (문서용 그림).
    public static string? ScreenshotPath { get; set; }

    public static int ScreenshotTab { get; set; }

    /// --smoke --click <apply-auto|apply-language|apply-watcher>: 해당 버튼의 처리 경로를 실행하고 종료 코드로 결과를 알립니다 (0 정상, 3 실패).
    public static string? SmokeClick { get; set; }

    private static readonly TimeSpan DoubleClick = TimeSpan.FromMilliseconds(500);

    private TrayIcon? _icon;
    private MainWindow? _main;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DateTime _lastClick = DateTime.MinValue;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly HashSet<string> _seenEvents = [];
    private readonly Dictionary<string, string> _stageSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _prewarmSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateOnly> _prewarmToast = new(StringComparer.OrdinalIgnoreCase); // 게임별로 예열 완료를 알린 날 (게임당 하루 한 번)
    private readonly List<AutoToast> _toasts = [];
    private string _lastNotice = string.Empty;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            Model.Refresh();
            _icon = new TrayIcon { Icon = LoadIcon("aster-tray.png") ?? new WindowIcon(MakeIcon()), IsVisible = true };
            Rebuild();
            _icon.Clicked += (_, _) => // 트레이 아이콘 더블클릭 (Avalonia 는 클릭만 알려 주므로 시간으로 판정)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastClick < DoubleClick)
                {
                    ShowMain();
                }

                _lastClick = now;
            };
            TrayIcon.SetIcons(this, [_icon]);
            _main = new MainWindow(Manage, Model, Quit);
            if (Smoke && ScreenshotPath is not null)
            {
                _main.Width = 1100;
                _main.Height = 720;
                _main.SelectTab(ScreenshotTab);
            }

            _main.Show();
            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            poll.Tick += (_, _) =>
            {
                Update(Model.Refresh);
                AutoStatusFile? auto = Manage.ReloadAuto();
                _main.UpdateStatus();
                if (!Smoke)
                {
                    Notify(auto);
                }
            };
            poll.Start();
            if (Smoke)
            {
                Log.Info($"smoke: tray icon visible={_icon.IsVisible} menu_items={_icon.Menu?.Items.Count} main window visible={_main.IsVisible} status={Model.StatusText}");
                if (SmokeClick is not null)
                {
                    ClickAndExit(desktop, _main, SmokeClick);
                }
                else
                {
                    var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ScreenshotPath is null ? 3 : 3.5) };
                    stop.Tick += (_, _) =>
                    {
                        stop.Stop();
                        if (ScreenshotPath is not null)
                        {
                            using var shot = new RenderTargetBitmap(new PixelSize(1100, 720), new Vector(96, 96));
                            shot.Render(_main);
                            shot.Save(ScreenshotPath);
                            Log.Info("smoke: screenshot saved");
                        }

                        _icon.IsVisible = false;
                        desktop.Shutdown(0);
                    };
                    stop.Start();
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void ClickAndExit(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main, string click)
    {
        int code;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // 창이 열린 직후의 첫 읽기가 시작되도록
            ActionResult r = await main.SmokeClick(click);
            Log.Info($"smoke: click {click} {(r.Ok ? "ok" : "failed")}: {r.Message}");
            code = r.Ok ? 0 : 3;
        }
        catch (Exception e)
        {
            // 최상위 경계: 어떤 예외든 종료 코드 3 으로 알림
            Log.Error($"smoke: click {click} threw", e);
            code = 3;
        }

        _icon!.IsVisible = false;
        desktop.Shutdown(code);
    }

    private void Rebuild()
    {
        _icon!.ToolTipText = (Model.LastMessage.Length == 0 ? Model.StatusText : Model.StatusText + "\n" + Model.LastMessage)
            + (_lastNotice.Length == 0 ? string.Empty : "\n" + _lastNotice);
        _icon.Menu = BuildMenu();
    }

    /// 트레이 시작 이후 auto_status.json 에 새로 생긴 발견·재배치를 알림 창으로 한 번씩 띄웁니다.
    private void Notify(AutoStatusFile? status)
    {
        if (status is null)
        {
            return;
        }

        foreach (AutoNotice e in AutoStatusFile.NewEvents(status.Events, _started, _seenEvents))
        {
            _seenEvents.Add(e.Key);
            Toast(AutoStatusFile.Notice(e, L.Current));
        }

        foreach (string game in AutoStatusFile.FirstRunFinished(_stageSeen, status)) // 이벤트로는 알 수 없어 단계 변화로 감지
        {
            Toast(L.T("Toast_FirstRun", game));
        }

        foreach (string game in AutoStatusFile.PrewarmFinished(_prewarmSeen, _prewarmToast, status, DateOnly.FromDateTime(DateTime.Now)))
        {
            Toast(L.T("Toast_Prewarmed", game));
        }
    }

    private void Toast(string text)
    {
        _lastNotice = text;
        var toast = new AutoToast(text, _toasts.Count, ShowMain);
        _toasts.Add(toast);
        toast.Closed += (_, _) => _toasts.Remove(toast);
        toast.Show();
        Rebuild();
    }

    private void Update(Action change)
    {
        change();
        Rebuild();
    }

    /// 느린 일 (스트림, UAC 대기) 은 백그라운드로 돌리고 끝나면 메뉴를 다시 만듭니다.
    private void InBackground(Func<ActionResult> work) =>
        Task.Run(work).ContinueWith(_ => Dispatcher.UIThread.Post(() => Update(Model.Refresh)), TaskScheduler.Default);

    private NativeMenu BuildMenu()
    {
        ServiceState state = Model.Service;
        var games = new NativeMenu();
        foreach (string name in Model.Games)
        {
            var one = new NativeMenu();
            one.Items.Add(Item(L.T("Menu_PrewarmStream"), () => InBackground(() => Model.StreamNow(name))));
            one.Items.Add(Item(L.T("Btn_Remove"), () => Update(() => Model.RemoveGame(name))));
            games.Items.Add(new NativeMenuItem(name) { Menu = one });
        }

        if (Model.Games.Count == 0)
        {
            games.Items.Add(new NativeMenuItem(L.T("Menu_NoGames")) { IsEnabled = false });
        }

        games.Items.Add(new NativeMenuItemSeparator());
        games.Items.Add(Item(L.T("Menu_Register"), () => new RegisterWindow(Model, () => Update(Model.Refresh)).Show()));

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem(Model.StatusText) { IsEnabled = false });
        menu.Items.Add(Item(L.T("Menu_Open"), ShowMain));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem(L.T("Menu_Games")) { Menu = games });
        menu.Items.Add(Item(L.T("Btn_ServiceStart"), () => InBackground(Model.StartService), state == ServiceState.Stopped));
        menu.Items.Add(Item(L.T("Btn_ServiceStop"), () => InBackground(Model.StopService), state == ServiceState.Running));
        menu.Items.Add(Item(L.T("Menu_OpenLogs"), () => Update(() => Model.OpenLogs())));
        menu.Items.Add(Item(L.T("Menu_CheckUpdate"), CheckUpdate));
        if (Model.PendingVersion is not null)
        {
            menu.Items.Add(Item(L.T("Menu_InstallUpdate", Model.PendingVersion), () => _ = Model.ApplyUpdateAsync(CancellationToken.None)));
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Menu_Exit"), Quit));
        return menu;
    }

    /// 진행 중인 트레이스는 저장하도록 기다린 뒤 (최대 10 초) 끝냅니다.
    private async void Quit()
    {
        if (_main is not null)
        {
            await _main.StopTraceAsync();
        }

        _desktop?.Shutdown(0);
    }

    private void ShowMain()
    {
        if (_main is null)
        {
            return;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
        _main.Reload();
    }

    private async void CheckUpdate()
    {
        await Model.CheckUpdateAsync();
        Update(Model.Refresh);
    }

    private static NativeMenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new NativeMenuItem(header) { IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    /// 포함 자원 Assets\<파일> 의 아이콘, 읽지 못하면 null (트레이는 MakeIcon 으로 대신함).
    public static WindowIcon? LoadIcon(string file)
    {
        try
        {
            using Stream s = AssetLoader.Open(new Uri("avares://AsterCache.Tray/Assets/" + file));
            return new WindowIcon(s);
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            Log?.Warn($"icon {file} not loaded: {e.GetType().Name}");
            return null;
        }
    }

    /// 파란 원 (32x32). 아이콘 자원을 읽지 못할 때만 쓰는 대체 아이콘으로 메모리에서 만듭니다.
    private static WriteableBitmap MakeIcon()
    {
        const int size = 32;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        byte[] px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt(((x - 15.5) * (x - 15.5)) + ((y - 15.5) * (y - 15.5)));
                if (d <= 15)
                {
                    int i = ((y * size) + x) * 4;
                    bool rim = d > 12;
                    px[i] = rim ? (byte)255 : (byte)200;
                    px[i + 1] = rim ? (byte)255 : (byte)120;
                    px[i + 2] = rim ? (byte)255 : (byte)30;
                    px[i + 3] = 255;
                }
            }
        }

        using ILockedFramebuffer fb = bmp.Lock();
        for (int y = 0; y < size; y++)
        {
            Marshal.Copy(px, y * size * 4, fb.Address + (y * fb.RowBytes), size * 4);
        }

        return bmp;
    }
}
