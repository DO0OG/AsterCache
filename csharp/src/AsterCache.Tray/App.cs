using System.Runtime.InteropServices;
using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 트레이 아이콘과 메인 창(닫으면 트레이로)을 가진 앱. 메뉴 항목은 TrayViewModel 을 부르기만 합니다.
internal sealed partial class App : Application
{
    public static TrayViewModel Model { get; set; } = null!;

    public static ILog Log { get; set; } = null!;

    public static ManageViewModel Manage { get; set; } = null!;

    public static bool Smoke { get; set; }

    /// --smoke --screenshot <png>: 메인 창을 창 크기 (1180x760) 그대로 그려 저장하고 끝냅니다 (문서용 그림).
    public static string? ScreenshotPath { get; set; }

    public static int ScreenshotTab { get; set; }

    /// --advanced: 그림 찍을 때 게임 표의 고급 열을 켭니다.
    public static bool ScreenshotAdvanced { get; set; }

    /// --smoke --click <apply-auto|apply-language|apply-watcher>: 해당 버튼의 처리 경로를 실행하고 종료 코드로 결과를 알립니다 (0 정상, 3 실패).
    public static string? SmokeClick { get; set; }

    /// --theme light|dark: 화면 테마를 OS 설정 대신 강제합니다 (그림 찍기용). 없으면 OS 설정을 따릅니다.
    public static string? ThemeOverride { get; set; }

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
    private DateTimeOffset _lastNoticeAt = DateTimeOffset.MinValue;
    private Window[] _extras = [];
    private int _waited;
    private StatusIcons? _statusIcons;
    private StatusTone? _tone;
    private string _menuKey = string.Empty;
    private readonly List<NativeMenuItem> _statusItems = [];
    private readonly List<(string Name, NativeMenuItem Item)> _gameItems = [];

    public override void Initialize()
    {
        Styles.Add(Look.Theme());
        Look.AddResources(Resources);
        Styles.Add(Look.AppStyles());
        RequestedThemeVariant = ThemeOverride switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            Model.Refresh();
            _statusIcons = new StatusIcons(LoadBitmap("aster-tray.png") ?? MakeIcon());
            _icon = new TrayIcon { IsVisible = true }; // 아이콘은 Rebuild 가 상태 색 점을 합성해 지정
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
                _main.SelectTab(ScreenshotTab);
                _main.ShowAdvanced(ScreenshotAdvanced);
                string sample = "Sample-Game";
                _extras = [new RegisterWindow(Model, () => { }, new GameInput(sample, ["Sample.exe"], @"C:\Plans\Sample-Game.prefetch.json", @"C:\Plans\Sample-Game.plan.json", @"D:\Games\Sample", [@"D:\Games"], "Games", null, [])), ConfirmWindow.Create(L.T("Confirm_Apply", "Sample Game", "12.3 GiB")), ConfirmWindow.Create(L.T("Confirm_Remove", sample), true), new AutoToast(L.T("Toast_Prewarmed", "Sample Game"), 0, StatusTone.Ok, () => { }), new AutoToast(L.T("Toast_Discovered", "A Game With A Very Long Name That Needs More Than One Line To Show Fully"), 1, StatusTone.Busy, () => { })];
                foreach (Window w in _extras)
                {
                    w.Show();
                }
            }

            _main.Show();
            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            poll.Tick += (_, _) =>
            {
                AutoStatusFile? auto = Manage.ReloadAuto(); // 메뉴를 다시 만들기 전에 런처 이름을 먼저 읽음
                Update(Model.Refresh);
                _main.UpdateStatus();
                if (!Smoke)
                {
                    Notify(auto);
                }
            };
            poll.Start();
            StartUpdater();
            if (Smoke)
            {
                Log.Info($"smoke: tray icon visible={_icon.IsVisible} menu_items={_icon.Menu?.Items.Count} main window visible={_main.IsVisible} status={Model.StatusText}");
                if (SmokeClick is not null)
                {
                    ClickAndExit(desktop, _main, SmokeClick);
                }
                else
                {
                    var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ScreenshotPath is null ? 3 : 1) };
                    stop.Tick += (_, _) =>
                    {
                        if (ScreenshotPath is not null && (++_waited < 4 || (!_main.Idle && _waited < 90)))
                        {
                            return; // 처음 읽기 (게임마다 aster 호출) 가 끝나야 표가 채워진 그림이 나옴
                        }

                        stop.Stop();
                        if (ScreenshotPath is not null)
                        {
                            using var shot = new RenderTargetBitmap(new PixelSize((int)_main.Bounds.Width, (int)_main.Bounds.Height), new Vector(96, 96));
                            shot.Render(_main);
                            shot.Save(ScreenshotPath);
                            string[] names = ["register", "confirm", "confirm-danger", "toast-ok", "toast-found"];
                            for (int i = 0; i < _extras.Length; i++)
                            {
                                Snap(_extras[i], Path.ChangeExtension(ScreenshotPath, null) + "-" + names[i] + ".png");
                            }

                            _statusIcons.SaveSheet(Path.ChangeExtension(ScreenshotPath, null) + "-icons.png");
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

    private static void Snap(Window w, string path)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w.Bounds.Width), (int)Math.Ceiling(w.Bounds.Height)), new Vector(96, 96));
        bmp.Render(w);
        bmp.Save(path);
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TraySummary summary = TrayStatus.Summarize(Model.Service, Model.Games, Manage.Auto, Model.HasProblem);
        string message = TrayStatus.Recent(Model.LastMessage, Model.LastMessageAt, now);
        string notice = TrayStatus.Recent(_lastNotice, _lastNoticeAt, now);
        _icon!.ToolTipText = TrayStatus.Tip(summary, message, notice);
        if (_tone != summary.Tone)
        {
            _tone = summary.Tone;
            _icon.Icon = _statusIcons!.Icon(summary.Tone); // 상태가 바뀔 때만 교체 (상태별 아이콘은 캐시)
        }

        string[] lines = [.. new[] { Model.StatusText, summary.Counts, message }.Where(l => l.Length > 0)];
        string key = $"{Model.Service}|{lines.Length}|{string.Join(",", Model.Games)}|{Updater?.Status.Key}";
        if (key == _menuKey && _icon.Menu is not null)
        {
            // 항목 구성이 같으면 메뉴를 새로 만들지 않고 글만 바꿈 (열려 있는 메뉴가 5 초마다 교체되지 않게)
            for (int i = 0; i < lines.Length; i++)
            {
                _statusItems[i].Header = lines[i];
            }

            foreach ((string name, NativeMenuItem item) in _gameItems)
            {
                item.Header = GameHeader(name);
            }

            return;
        }

        _menuKey = key;
        _icon.Menu = BuildMenu(lines);
    }

    private string GameHeader(string name)
    {
        (string stage, string prewarm) = Manage.GameBrief(name);
        return TrayStatus.GameItem(Model.DisplayName(name), stage, prewarm);
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
            Toast(AutoStatusFile.Notice(e with { Game = Model.DisplayName(e.Game) }, L.Current), e.Kind == "relocated" ? StatusTone.Ok : StatusTone.Busy);
        }

        foreach (string game in AutoStatusFile.FirstRunFinished(_stageSeen, status)) // 이벤트로는 알 수 없어 단계 변화로 감지
        {
            Toast(L.T("Toast_FirstRun", Model.DisplayName(game)), StatusTone.Ok);
        }

        foreach (string game in AutoStatusFile.PrewarmFinished(_prewarmSeen, _prewarmToast, status, DateOnly.FromDateTime(DateTime.Now)))
        {
            Toast(L.T("Toast_Prewarmed", Model.DisplayName(game)), StatusTone.Ok);
        }
    }

    private void Toast(string text, StatusTone tone)
    {
        _lastNotice = text;
        _lastNoticeAt = DateTimeOffset.UtcNow;
        if (!Model.AllowToast(text))
        {
            Rebuild(); // 알림 창은 끄고 아이콘 풍선 도움말에는 남김
            return;
        }

        var toast = new AutoToast(text, _toasts.Count, tone, ShowMain);
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

    private NativeMenu BuildMenu(string[] status)
    {
        ServiceState state = Model.Service;
        _statusItems.Clear();
        _gameItems.Clear();
        var games = new NativeMenu();
        foreach (string name in Model.Games)
        {
            var one = new NativeMenu();
            one.Items.Add(Item(L.T("Menu_PrewarmStream"), () => InBackground(() => Model.StreamNow(name))));
            one.Items.Add(Item(L.T("Btn_Remove"), () => ConfirmRemove(name)));
            var game = new NativeMenuItem(GameHeader(name)) { Menu = one };
            _gameItems.Add((name, game));
            games.Items.Add(game);
        }

        if (Model.Games.Count == 0)
        {
            games.Items.Add(new NativeMenuItem(L.T("Menu_NoGames")) { IsEnabled = false });
        }

        games.Items.Add(new NativeMenuItemSeparator());
        games.Items.Add(Item(L.T("Menu_Register"), () => new RegisterWindow(Model, () => Update(Model.Refresh)).Show()));

        var menu = new NativeMenu();
        foreach (string line in status)
        {
            var item = new NativeMenuItem(line) { IsEnabled = false };
            _statusItems.Add(item);
            menu.Items.Add(item);
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Menu_Open"), ShowMain));
        menu.Items.Add(Item(L.T("Menu_Settings"), () =>
        {
            ShowMain();
            _main?.SelectTab(2);
        }));
        menu.Items.Add(new NativeMenuItem(L.T("Menu_Games")) { Menu = games });
        (string serviceKey, bool serviceEnabled) = TrayStatus.ServiceAction(state);
        menu.Items.Add(Item(L.T(serviceKey), () => InBackground(state == ServiceState.Running ? Model.StopService : Model.StartService), serviceEnabled));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Menu_OpenLogs"), () => Update(() => Model.OpenLogs())));
        AddUpdateItems(menu);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Menu_Exit"), Quit));
        return menu;
    }

    /// 메뉴에서 게임 제거: 메인 창과 같은 확인 대화상자를 거칩니다.
    private async void ConfirmRemove(string name)
    {
        ShowMain();
        if (_main is not null && await ConfirmWindow.Ask(_main, L.T("Confirm_Remove", Model.DisplayName(name)), danger: true))
        {
            Update(() => Model.RemoveGame(name));
        }
    }

    /// 진행 중인 트레이스는 저장하도록 기다린 뒤 (최대 10 초) 끝냅니다.
    private async void Quit()
    {
        StopUpdater();
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

    private static NativeMenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new NativeMenuItem(header) { IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    /// 포함 자원 Assets\<파일> 의 그림, 읽지 못하면 null.
    internal static Bitmap? LoadBitmap(string file)
    {
        try
        {
            using Stream s = AssetLoader.Open(new Uri("avares://AsterCache.Tray/Assets/" + file));
            return new Bitmap(s);
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            Log?.Warn($"image {file} not loaded: {e.GetType().Name}");
            return null;
        }
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
