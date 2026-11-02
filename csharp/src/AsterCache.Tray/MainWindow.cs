using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using AsterCache.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 확인/취소 대화상자 (긴 작업 전에 이동 바이트·되돌리기 안내를 보여줌).
internal sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string text)
    {
        Title = "AsterCache";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var yes = new Button { Content = L.T("Btn_Ok"), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = L.T("Btn_Cancel") };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } },
            },
        };
    }

    public static Task<bool> Ask(Window owner, string text) => new ConfirmWindow(text).ShowDialog<bool>(owner);
}

/// 드라이브 관리 유틸리티처럼 한눈에 보고 조작하는 메인 창. 닫으면 숨기기만 합니다 (트레이로). 계산과 aster 호출은 ManageViewModel 이 하고 여기는 배치와 버튼 연결뿐입니다.
internal sealed class MainWindow : Window
{
    private readonly ManageViewModel _manage;
    private readonly TrayViewModel _tray;
    private readonly ListBox _drives = new() { Margin = new Thickness(0, 4, 8, 0) };
    private readonly ListBox _games = new();
    private readonly TextBox _status = ReadOnlyBox();
    private string _stages = string.Empty;
    private readonly TextBox _serviceLog = ReadOnlyBox();
    private readonly TextBox _trayLog = ReadOnlyBox();
    private readonly TextBlock _versions = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _watcher = new() { ItemsSource = new[] { "poll", "etw" }, MinWidth = 90 };
    private readonly ComboBox _language = new() { ItemsSource = new[] { L.T("Lang_Auto"), L.T("Lang_Ko"), L.T("Lang_En") }, MinWidth = 200 };
    private readonly CheckBox _autoEnabled = new() { Content = L.T("Auto_Enabled") };
    private readonly CheckBox _autoDiscover = new() { Content = L.T("Auto_Discover") };
    private readonly CheckBox _autoTrace = new() { Content = L.T("Auto_Trace") };
    private readonly CheckBox _autoRelocate = new() { Content = L.T("Auto_Relocate") };
    private readonly ComboBox _autoPrewarm = new() { ItemsSource = Enumerable.Range(0, 9).ToList(), MinWidth = 70 };
    private readonly TextBox _autoIgnore = new() { MinWidth = 320 };
    private readonly TextBox _zoneLimit = new() { MinWidth = 90 };
    private readonly TextBox _rewarmMinutes = new() { MinWidth = 70 };
    private readonly TextBlock _bar = new() { Margin = new Thickness(8, 2) };
    private readonly TextBlock _progress = new() { Margin = new Thickness(8, 2), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _busyBar = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private CancellationTokenSource? _traceStop;
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 4, 8, 0) };
    private Task? _traceRun;
    private static readonly string[] LanguageValues = ["auto", L.Ko, L.En];
    private static readonly TimeSpan TraceStopWait = TimeSpan.FromSeconds(10);

    private int _busy;
    private string? _letter;
    private bool _loading;

    public MainWindow(ManageViewModel manage, TrayViewModel tray, Action quit)
    {
        _manage = manage;
        _tray = tray;
        Title = "AsterCache";
        Icon = App.LoadIcon("aster.ico");
        Width = 1180;
        Height = 740;
        MinWidth = 860;
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.WindowClosing) // 앱 종료는 막지 않음
            {
                e.Cancel = true;
                Hide();
            }
        };
        Opened += (_, _) => Reload();

        var driveSide = new DockPanel { Margin = new Thickness(8, 8, 0, 0) };
        var driveTitle = new TextBlock { Text = L.T("Pane_Drives"), FontWeight = FontWeight.Bold };
        DockPanel.SetDock(driveTitle, Dock.Top);
        driveSide.Children.Add(driveTitle);
        driveSide.Children.Add(_drives);
        _drives.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                _letter = (_drives.SelectedItem as DriveRow)?.Letter;
                ShowRows();
            }
        };

        TabControl tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = L.T("Tab_Status"), Content = StatusTab() });
        tabs.Items.Add(new TabItem { Header = L.T("Tab_Games"), Content = GamesTab() });
        tabs.Items.Add(new TabItem { Header = L.T("Tab_Settings"), Content = SettingsTab() });
        tabs.Items.Add(new TabItem { Header = L.T("Tab_Logs"), Content = LogsTab() });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*") };
        Place(driveSide, body, 0);
        Place(tabs, body, 1);

        var footer = new StackPanel { Children = { _busyBar, _progress, _bar } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Place(Banner(quit), root, 0, true);
        Place(body, root, 1, true);
        Place(footer, root, 2, true);
        Content = root;
        UpdateStatus();
    }

    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    /// 아래 줄: 서비스 상태 (트레이의 5 초 갱신이 부릅니다).
    public void UpdateStatus()
    {
        _bar.Text = _tray.StatusText;
        if (_games.ItemsSource is null)
        {
            return;
        }

        // 단계 열은 값이 바뀐 때만 목록을 다시 채워 선택·스크롤을 지킵니다.
        IReadOnlyList<GameRow> rows = _manage.GameRows(_letter);
        string stages = string.Join("|", rows.Select(r => r.Stage));
        if (stages != _stages)
        {
            _stages = stages;
            int selected = _games.SelectedIndex;
            _games.ItemsSource = rows;
            _games.SelectedIndex = selected < rows.Count ? selected : -1;
        }

        string text = string.Join(Environment.NewLine, _manage.StatusLines(_letter));
        if (_status.Text != text)
        {
            _status.Text = text;
        }
    }

    /// 열 때마다 상태·목록·로그를 다시 읽습니다 (다른 작업 중이면 그 작업이 끝날 때 읽으므로 건너뜀).
    public void Reload()
    {
        if (Volatile.Read(ref _busy) == 0)
        {
            _ = RunAsync(L.T("Op_Loading"), () => new ActionResult(true, string.Empty));
        }
    }

    private Control Banner(Action quit)
    {
        var relaunch = new Button { Content = L.T("Btn_Relaunch"), Margin = new Thickness(12, 0, 0, 0) };
        relaunch.Click += async (_, _) =>
        {
            await StopTraceAsync();
            ActionResult r = _manage.RelaunchAsAdmin();
            _progress.Text = r.Message;
            if (r.Ok)
            {
                quit();
            }
        };
        var banner = new Border
        {
            Background = Brushes.Gold,
            Padding = new Thickness(10, 6),
            IsVisible = !_manage.IsAdmin,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = L.T("Banner_NotAdmin"), Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center },
                    relaunch,
                },
            },
        };
        var warning = new Border
        {
            Background = Brushes.OrangeRed,
            Padding = new Thickness(10, 4),
            IsVisible = _manage.ElevatedExeWarning is not null && App.ScreenshotPath is null, // 그림 찍기 (예시 모드) 에서는 숨김
            Child = new TextBlock { Text = _manage.ElevatedExeWarning, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
        };
        return new StackPanel { Children = { banner, warning } };
    }

    /// 진행 중인 트레이스가 있으면 stop-file 로 정상 종료를 요청하고 최대 10 초 기다립니다 (앱 종료·관리자 재실행 전에 부름).
    public async Task StopTraceAsync()
    {
        if (_traceRun is not { IsCompleted: false } run)
        {
            return;
        }

        _progress.Text = L.T("Msg_SavingTrace");
        _traceStop?.Cancel();
        await Task.WhenAny(run, Task.Delay(TraceStopWait));
    }

    private Control StatusTab()
    {
        var panel = new DockPanel { Margin = new Thickness(4) };
        var refresh = Btn(L.T("Btn_Refresh"), () => _ = RunAsync(L.T("Btn_Refresh"), () => new ActionResult(true, L.T("Btn_Refresh"))));
        DockPanel.SetDock(refresh, Dock.Top);
        panel.Children.Add(refresh);
        panel.Children.Add(_status);
        return panel;
    }

    private Control GamesTab()
    {
        _games.ItemTemplate = new FuncDataTemplate<GameRow>((row, _) => row is null ? new Panel() : Cells(
            [row.Name, row.Exe, row.Origin, L.T(row.HasPlan ? "Cell_Yes" : "Cell_No"), L.T(row.HasTrace ? "Cell_Yes" : "Cell_No"), row.Verdict, row.Prewarm, row.Source, row.AntiCheat, row.Stage]));
        _games.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var buttons = new WrapPanel
        {
            Children =
            {
                Btn(L.T("Btn_Register"), Register),
                Btn(L.T("Btn_Remove"), () => WithGame(g => Confirmed(L.T("Confirm_Remove", g), L.T("Btn_Remove"), () => _tray.RemoveGame(g)))),
                Btn(L.T("Btn_CollectTrace"), () => WithGame(g =>
                {
                    _traceStop = new CancellationTokenSource();
                    CancellationToken stop = _traceStop.Token;
                    return _traceRun = RunAsync(L.T("Btn_CollectTrace"), () => _manage.CollectTrace(g, Progress, stop));
                })),
                Btn(L.T("Btn_StopCollect"), () => _traceStop?.Cancel()),
                Btn(L.T("Btn_MakePlan"), () => WithGame(g => RunAsync(L.T("Btn_MakePlan"), () => _manage.MakePlan(g, Progress)))),
                Btn(L.T("Btn_Apply"), () => WithGame(Apply)),
                Btn(L.T("Btn_Revert"), () => WithGame(Revert)),
                Btn(L.T("Btn_Prewarm"), () => WithGame(g => RunAsync(L.T("Op_PrewarmStream"), () => _tray.StreamNow(g)))),
                Btn(L.T("Btn_Measure"), () => WithGame(g => RunAsync(L.T("Btn_Measure"), () => _manage.Measure(g)))),
            },
        };
        var panel = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(buttons, Dock.Top);
        var header = Cells([L.T("Col_Name"), L.T("Col_Exe"), L.T("Col_Origin"), L.T("Col_Plan"), L.T("Col_Trace"), L.T("Col_Layout"), L.T("Col_Prewarm"), L.T("Col_Source"), L.T("Col_AntiCheat"), L.T("Col_Stage")], FontWeight.Bold);
        header.Margin = new Thickness(8, 0, 0, 2);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(header);
        panel.Children.Add(_games);
        return panel;
    }

    private Control SettingsTab()
    {
        var apply = Btn(L.T("Btn_ApplyWatcher"), () => _ = RunAsync(L.T("Op_WatcherChange"), () => _tray.SetWatcher(_watcher.SelectedItem as string ?? "poll")));
        var service = new WrapPanel
        {
            Children =
            {
                Btn(L.T("Btn_ServiceInstall"), () => _ = Confirmed(L.T("Confirm_ServiceInstall"), L.T("Btn_ServiceInstall"), () => _manage.InstallService())),
                Btn(L.T("Btn_ServiceUninstall"), () => _ = Confirmed(L.T("Confirm_ServiceUninstall"), L.T("Btn_ServiceUninstall"), () => _tray.UninstallService())),
                Btn(L.T("Btn_ServiceStart"), () => _ = RunAsync(L.T("Btn_ServiceStart"), () => _tray.StartService())),
                Btn(L.T("Btn_ServiceStop"), () => _ = RunAsync(L.T("Btn_ServiceStop"), () => _tray.StopService())),
            },
        };
        var folders = new WrapPanel
        {
            Children = { Btn(L.T("Btn_OpenData"), () => _progress.Text = _manage.OpenDataFolder().Message), Btn(L.T("Btn_OpenLogs"), () => _progress.Text = _tray.OpenLogs().Message) },
        };
        var applyLanguage = Btn(L.T("Btn_ApplyLanguage"), () => _ = RunAsync(L.T("Op_LanguageChange"), () => _tray.SetLanguage(LanguageValues[Math.Max(0, _language.SelectedIndex)])));
        var applyAuto = Btn(L.T("Btn_ApplyAuto"), () => _ = RunAsync(L.T("Op_AutoChange"), () =>
        {
            ActionResult saved = _tray.SetAuto(
                _autoEnabled.IsChecked == true, _autoDiscover.IsChecked == true, _autoTrace.IsChecked == true, _autoRelocate.IsChecked == true,
                Math.Max(0, _autoPrewarm.SelectedIndex), _autoIgnore.Text ?? string.Empty);
            return saved.Ok ? _tray.SetSpeed(_zoneLimit.Text ?? string.Empty, _rewarmMinutes.Text ?? string.Empty) : saved;
        }));
        return new StackPanel
        {
            Margin = new Thickness(8),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = L.T("Set_Auto"), FontWeight = FontWeight.Bold },
                new TextBlock { Text = L.T("Set_AutoNote"), TextWrapping = TextWrapping.Wrap },
                new WrapPanel { Children = { _autoEnabled, _autoDiscover, _autoTrace, _autoRelocate } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L.T("Auto_Prewarm"), VerticalAlignment = VerticalAlignment.Center }, _autoPrewarm } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L.T("Auto_Ignore"), VerticalAlignment = VerticalAlignment.Center }, _autoIgnore } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L.T("Auto_ZoneLimit"), VerticalAlignment = VerticalAlignment.Center }, _zoneLimit } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L.T("Auto_Rewarm"), VerticalAlignment = VerticalAlignment.Center }, _rewarmMinutes } },
                new TextBlock { Text = L.T("Set_PinNote"), TextWrapping = TextWrapping.Wrap },
                applyAuto,
                new TextBlock { Text = L.T("Set_Service"), FontWeight = FontWeight.Bold },
                service,
                new TextBlock { Text = L.T("Set_Watcher"), FontWeight = FontWeight.Bold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _watcher, apply } },
                new TextBlock { Text = L.T("Set_PrewarmDefaults"), FontWeight = FontWeight.Bold },
                new TextBlock { Text = ManageViewModel.DescribePrewarm(PrewarmSettings.Default), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = L.T("Set_Language"), FontWeight = FontWeight.Bold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _language, applyLanguage } },
                new TextBlock { Text = L.T("Set_Folders"), FontWeight = FontWeight.Bold },
                folders,
                new TextBlock { Text = L.T("Set_Versions"), FontWeight = FontWeight.Bold },
                _versions,
            },
        };
    }

    private Control LogsTab()
    {
        var refresh = Btn(L.T("Btn_Refresh"), () => _ = RunAsync(L.T("Btn_Refresh"), () => new ActionResult(true, L.T("Btn_Refresh"))));
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*") };
        Place(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { refresh, new TextBlock { Text = L.T("Log_Service"), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } } }, grid, 0, true);
        Place(_serviceLog, grid, 1, true);
        var trayTitle = new TextBlock { Text = L.T("Log_Tray"), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };
        Place(trayTitle, grid, 2, true);
        Place(_trayLog, grid, 3, true);
        return grid;
    }

    private async void Register()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Pick_Title"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("Pick_Filter")) { Patterns = ["*.exe"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        string label = path.Length > 2 && path[1] == ':' ? VolumeInfo.Label(path[..2]) : string.Empty;
        await new RegisterWindow(_tray, () => { }, ManageViewModel.InferGame(path, label, _tray.PlansDir)).ShowDialog(this);
        await RunAsync(L.T("Op_Loading"), () => new ActionResult(true, L.T("Msg_GameListReloaded")));
    }

    private async Task Apply(string name)
    {
        long? moved = await Task.Run(() => _manage.MovedBytes(name));
        string text = L.T("Confirm_Apply", name, moved is { } m ? ManageViewModel.Size(m) : L.T("Confirm_ApplySizeUnknown"));
        if (await ConfirmWindow.Ask(this, text))
        {
            await RunAsync(L.T("Op_Relocate"), () => _manage.Apply(name, Progress));
        }
    }

    private async Task Revert(string name)
    {
        if (await ConfirmWindow.Ask(this, L.T("Confirm_Revert", name)))
        {
            await RunAsync(L.T("Btn_Revert"), () => _manage.Revert(name, Progress));
        }
    }

    private async Task Confirmed(string text, string what, Func<ActionResult> work)
    {
        if (await ConfirmWindow.Ask(this, text))
        {
            await RunAsync(what, work);
        }
    }

    private void Progress(string text) => Dispatcher.UIThread.Post(() => _progress.Text = text);

    private async void WithGame(Func<string, Task> act)
    {
        if (_games.SelectedItem is GameRow row)
        {
            await act(row.Name);
        }
        else
        {
            _progress.Text = L.T("Msg_SelectGame");
        }
    }

    /// 한 번에 하나만: 작업을 백그라운드로 돌리고 (진행 띠 표시), 끝나면 상태·목록·로그를 다시 읽어 그립니다.
    private async Task RunAsync(string what, Func<ActionResult> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _progress.Text = L.T("Msg_Busy");
            return;
        }

        _busyBar.IsVisible = true;
        _progress.Text = what + " ...";
        try
        {
            ActionResult r = await Task.Run(() =>
            {
                try
                {
                    return work();
                }
                catch (Exception e)
                {
                    // 창 맨 위 경계: 어떤 예외도 앱을 죽이지 않고 메시지로 보여줌 (원인은 tray.log 의 aster 줄과 함께 봄)
                    return new ActionResult(false, L.T("Msg_OpFailed", what, e.GetType().Name));
                }
            });
            _progress.Text = r.Message;

            var loaded = await Task.Run(() =>
            {
                try
                {
                    _manage.RefreshStatus();
                    return (Rows: _manage.Drives(), Service: _manage.ServiceLog(), Tray: _manage.TrayLog());
                }
                catch (Exception e)
                {
                    // 창 맨 위 경계: 상태를 못 읽어도 앱을 죽이지 않음
                    return (Rows: (IReadOnlyList<DriveRow>)[], Service: L.T("Msg_ReadFailed", e.GetType().Name), Tray: string.Empty);
                }
            });
            ShowDrives(loaded.Rows);
            _serviceLog.Text = loaded.Service;
            _serviceLog.CaretIndex = loaded.Service.Length; // 최근 줄이 보이게
            _trayLog.Text = loaded.Tray;
            _trayLog.CaretIndex = loaded.Tray.Length;
            _versions.Text = L.T("Ver_Line", _tray.TrayVersion ?? L.T("Word_Unknown"), _tray.ServiceVersion ?? L.T("Word_NotInstalled"), _tray.ServiceReinstallNeeded ? L.T("Ver_Reinstall") : string.Empty);
            AutoSettings auto = _tray.Settings?.Auto ?? AutoSettings.Default;
            _autoEnabled.IsChecked = auto.Enabled;
            _autoDiscover.IsChecked = auto.Discover;
            _autoTrace.IsChecked = auto.Trace;
            _autoRelocate.IsChecked = auto.Relocate;
            _autoPrewarm.SelectedIndex = Math.Clamp(auto.PrewarmRecent, 0, 8);
            _autoIgnore.Text = string.Join(", ", auto.IgnoreExes);
            _rewarmMinutes.Text = auto.RewarmMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _zoneLimit.Text = _tray.Settings?.MaxZoneAuto == true ? "auto" : (_tray.Settings?.MaxZoneMib ?? SettingsStore.DefaultMaxZoneMib).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _watcher.SelectedItem = _tray.Settings?.Watcher;
            _language.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, _tray.Settings?.Language));
            UpdateStatus();
        }
        finally
        {
            _busyBar.IsVisible = false;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void ShowDrives(IReadOnlyList<DriveRow> rows)
    {
        _loading = true;
        _drives.ItemsSource = new object[] { L.T("Drives_All", _tray.Games.Count) }.Concat(rows).ToList();
        _drives.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.Letter == _letter) + 1);
        _loading = false;
        ShowRows();
    }

    private void ShowRows()
    {
        IReadOnlyList<GameRow> rows = _manage.GameRows(_letter);
        _stages = string.Join("|", rows.Select(r => r.Stage));
        _games.ItemsSource = rows;
        _status.Text = string.Join(Environment.NewLine, _manage.StatusLines(_letter));
    }

    private static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    private static void Place(Control c, Grid grid, int at, bool row = false)
    {
        if (row)
        {
            Grid.SetRow(c, at);
        }
        else
        {
            Grid.SetColumn(c, at);
        }

        grid.Children.Add(c);
    }

    private static TextBox ReadOnlyBox()
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Malgun Gothic") };
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    private static Grid Cells(IReadOnlyList<string> text, FontWeight? weight = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,130,2*,50,70,110,2*,60,90,110") };
        for (int i = 0; i < text.Count; i++)
        {
            var t = new TextBlock { Text = text[i], FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }

        return g;
    }
}
