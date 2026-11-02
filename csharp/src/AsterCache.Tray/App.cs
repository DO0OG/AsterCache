using System.Runtime.InteropServices;
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

    private static readonly TimeSpan DoubleClick = TimeSpan.FromMilliseconds(500);

    private TrayIcon? _icon;
    private MainWindow? _main;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DateTime _lastClick = DateTime.MinValue;

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
                _main.UpdateStatus();
            };
            poll.Start();
            if (Smoke)
            {
                Log.Info($"smoke: tray icon visible={_icon.IsVisible} menu_items={_icon.Menu?.Items.Count} main window visible={_main.IsVisible} status={Model.StatusText}");
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

        base.OnFrameworkInitializationCompleted();
    }

    private void Rebuild()
    {
        _icon!.ToolTipText = Model.LastMessage.Length == 0 ? Model.StatusText : Model.StatusText + "\n" + Model.LastMessage;
        _icon.Menu = BuildMenu();
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
            one.Items.Add(Item("지금 예열 (스트림)", () => InBackground(() => Model.StreamNow(name))));
            one.Items.Add(Item("제거", () => Update(() => Model.RemoveGame(name))));
            games.Items.Add(new NativeMenuItem(name) { Menu = one });
        }

        if (Model.Games.Count == 0)
        {
            games.Items.Add(new NativeMenuItem("(등록된 게임 없음)") { IsEnabled = false });
        }

        games.Items.Add(new NativeMenuItemSeparator());
        games.Items.Add(Item("게임 등록...", () => new RegisterWindow(Model, () => Update(Model.Refresh)).Show()));

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem(Model.StatusText) { IsEnabled = false });
        menu.Items.Add(Item("열기", ShowMain));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("게임") { Menu = games });
        menu.Items.Add(Item("서비스 시작", () => InBackground(Model.StartService), state == ServiceState.Stopped));
        menu.Items.Add(Item("서비스 정지", () => InBackground(Model.StopService), state == ServiceState.Running));
        menu.Items.Add(Item("로그 열기", () => Update(() => Model.OpenLogs())));
        menu.Items.Add(Item("업데이트 확인", CheckUpdate));
        if (Model.PendingVersion is not null)
        {
            menu.Items.Add(Item("업데이트 설치 (" + Model.PendingVersion + ")", () => _ = Model.ApplyUpdateAsync(CancellationToken.None)));
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("종료", Quit));
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
