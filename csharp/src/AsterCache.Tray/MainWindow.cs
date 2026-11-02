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
        var yes = new Button { Content = "확인", Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "취소" };
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
    private readonly TextBox _serviceLog = ReadOnlyBox();
    private readonly TextBox _trayLog = ReadOnlyBox();
    private readonly TextBlock _versions = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _watcher = new() { ItemsSource = new[] { "poll", "etw" }, MinWidth = 90 };
    private readonly TextBlock _bar = new() { Margin = new Thickness(8, 2) };
    private readonly TextBlock _progress = new() { Margin = new Thickness(8, 2), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _busyBar = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private CancellationTokenSource? _traceStop;
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 4, 8, 0) };
    private Task? _traceRun;
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
        var driveTitle = new TextBlock { Text = "드라이브", FontWeight = FontWeight.Bold };
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
        tabs.Items.Add(new TabItem { Header = "상태", Content = StatusTab() });
        tabs.Items.Add(new TabItem { Header = "게임", Content = GamesTab() });
        tabs.Items.Add(new TabItem { Header = "설정", Content = SettingsTab() });
        tabs.Items.Add(new TabItem { Header = "로그", Content = LogsTab() });

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
    public void UpdateStatus() => _bar.Text = _tray.StatusText;

    /// 열 때마다 상태·목록·로그를 다시 읽습니다 (다른 작업 중이면 그 작업이 끝날 때 읽으므로 건너뜀).
    public void Reload()
    {
        if (Volatile.Read(ref _busy) == 0)
        {
            _ = RunAsync("불러오는 중", () => new ActionResult(true, string.Empty));
        }
    }

    private Control Banner(Action quit)
    {
        var relaunch = new Button { Content = "관리자로 다시 실행", Margin = new Thickness(12, 0, 0, 0) };
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
                    new TextBlock { Text = "관리자 권한 없음: 트레이스·재배치·서비스 제어는 관리자 재실행 필요", Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center },
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

        _progress.Text = "트레이스를 저장하는 중 (최대 10초)";
        _traceStop?.Cancel();
        await Task.WhenAny(run, Task.Delay(TraceStopWait));
    }

    private Control StatusTab()
    {
        var panel = new DockPanel { Margin = new Thickness(4) };
        var refresh = Btn("새로 고침", () => _ = RunAsync("새로 고침", () => new ActionResult(true, "새로 고침")));
        DockPanel.SetDock(refresh, Dock.Top);
        panel.Children.Add(refresh);
        panel.Children.Add(_status);
        return panel;
    }

    private Control GamesTab()
    {
        _games.ItemTemplate = new FuncDataTemplate<GameRow>((row, _) => row is null ? new Panel() : Cells(
            [row.Name, row.Exe, row.Origin, row.HasPlan ? "있음" : "없음", row.HasTrace ? "있음" : "없음", row.Verdict, row.Prewarm]));
        _games.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var buttons = new WrapPanel
        {
            Children =
            {
                Btn("등록", Register),
                Btn("제거", () => WithGame(g => Confirmed($"{g} 를 설정에서 제거합니다 (파일은 그대로).", "제거", () => _tray.RemoveGame(g)))),
                Btn("트레이스 수집", () => WithGame(g =>
                {
                    _traceStop = new CancellationTokenSource();
                    CancellationToken stop = _traceStop.Token;
                    return _traceRun = RunAsync("트레이스 수집", () => _manage.CollectTrace(g, Progress, stop));
                })),
                Btn("수집 중지", () => _traceStop?.Cancel()),
                Btn("계획 만들기", () => WithGame(g => RunAsync("계획 만들기", () => _manage.MakePlan(g, Progress)))),
                Btn("재배치 실행", () => WithGame(Apply)),
                Btn("되돌리기", () => WithGame(Revert)),
                Btn("지금 예열", () => WithGame(g => RunAsync("예열 스트림", () => _tray.StreamNow(g)))),
                Btn("측정", () => WithGame(g => RunAsync("측정", () => _manage.Measure(g)))),
            },
        };
        var panel = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(buttons, Dock.Top);
        var header = Cells(["이름", "실행 파일", "원점 루트", "계획", "트레이스", "배치", "예열 설정"], FontWeight.Bold);
        header.Margin = new Thickness(8, 0, 0, 2);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(header);
        panel.Children.Add(_games);
        return panel;
    }

    private Control SettingsTab()
    {
        var apply = Btn("감시기 적용", () => _ = RunAsync("감시기 변경", () => _tray.SetWatcher(_watcher.SelectedItem as string ?? "poll")));
        var service = new WrapPanel
        {
            Children =
            {
                Btn("서비스 설치", () => _ = Confirmed("서비스를 설치합니다 (LocalSystem, 자동 시작, 데이터 폴더 권한 설정). 계속할까요?", "서비스 설치", () => _manage.InstallService())),
                Btn("서비스 제거", () => _ = Confirmed("서비스를 정지하고 제거합니다 (데이터 폴더는 남김). 계속할까요?", "서비스 제거", () => _tray.UninstallService())),
                Btn("서비스 시작", () => _ = RunAsync("서비스 시작", () => _tray.StartService())),
                Btn("서비스 정지", () => _ = RunAsync("서비스 정지", () => _tray.StopService())),
            },
        };
        var folders = new WrapPanel
        {
            Children = { Btn("데이터 폴더 열기", () => _progress.Text = _manage.OpenDataFolder().Message), Btn("로그 폴더 열기", () => _progress.Text = _tray.OpenLogs().Message) },
        };
        return new StackPanel
        {
            Margin = new Thickness(8),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "서비스", FontWeight = FontWeight.Bold },
                service,
                new TextBlock { Text = "프로세스 감시기 (서비스를 다시 시작해야 적용, etw 는 관리자 필요)", FontWeight = FontWeight.Bold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _watcher, apply } },
                new TextBlock { Text = "예열 기본값 (게임 설정에 prewarm 을 적지 않았을 때)", FontWeight = FontWeight.Bold },
                new TextBlock { Text = ManageViewModel.DescribePrewarm(PrewarmSettings.Default), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "폴더", FontWeight = FontWeight.Bold },
                folders,
                new TextBlock { Text = "버전", FontWeight = FontWeight.Bold },
                _versions,
            },
        };
    }

    private Control LogsTab()
    {
        var refresh = Btn("새로 고침", () => _ = RunAsync("새로 고침", () => new ActionResult(true, "새로 고침")));
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*") };
        Place(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { refresh, new TextBlock { Text = "service.log (끝 200줄)", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center } } }, grid, 0, true);
        Place(_serviceLog, grid, 1, true);
        var trayTitle = new TextBlock { Text = "tray.log (끝 200줄: aster 실행 결과 포함)", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };
        Place(trayTitle, grid, 2, true);
        Place(_trayLog, grid, 3, true);
        return grid;
    }

    private async void Register()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "게임 실행 파일 선택",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("실행 파일") { Patterns = ["*.exe"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        string label = path.Length > 2 && path[1] == ':' ? VolumeInfo.Label(path[..2]) : string.Empty;
        await new RegisterWindow(_tray, () => { }, ManageViewModel.InferGame(path, label, _tray.PlansDir)).ShowDialog(this);
        await RunAsync("불러오는 중", () => new ActionResult(true, "게임 목록을 새로 읽음 (서비스를 다시 시작해야 새 게임을 감시)"));
    }

    private async Task Apply(string name)
    {
        long? moved = await Task.Run(() => _manage.MovedBytes(name));
        string text = $"{name}: 계획대로 게임 파일을 디스크 존으로 옮깁니다 (이동 {(moved is { } m ? ManageViewModel.Size(m) : "크기 알 수 없음")}). "
            + "저널이 남아 '되돌리기'로 원래 자리로 복원할 수 있고, 게임이 실행 중이면 멈추십시오. "
            + "한 번에 쓰는 양은 2 GiB (--write-cap-mib) 로 제한되어 큰 이동은 여러 번 실행해야 할 수 있습니다. 계속할까요?";
        if (await ConfirmWindow.Ask(this, text))
        {
            await RunAsync("재배치", () => _manage.Apply(name, Progress));
        }
    }

    private async Task Revert(string name)
    {
        if (await ConfirmWindow.Ask(this, $"{name}: 저널을 따라 옮긴 파일을 원래 자리로 되돌립니다. 한 번에 쓰는 양은 2 GiB (--write-cap-mib) 로 제한되어 큰 이동은 여러 번 실행해야 할 수 있습니다. 계속할까요?"))
        {
            await RunAsync("되돌리기", () => _manage.Revert(name, Progress));
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
            _progress.Text = "먼저 게임 목록에서 게임을 선택하십시오";
        }
    }

    /// 한 번에 하나만: 작업을 백그라운드로 돌리고 (진행 띠 표시), 끝나면 상태·목록·로그를 다시 읽어 그립니다.
    private async Task RunAsync(string what, Func<ActionResult> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _progress.Text = "다른 작업이 진행 중입니다";
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
                    return new ActionResult(false, $"{what} 실패 ({e.GetType().Name})");
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
                    return (Rows: (IReadOnlyList<DriveRow>)[], Service: $"(읽지 못함: {e.GetType().Name})", Tray: string.Empty);
                }
            });
            ShowDrives(loaded.Rows);
            _serviceLog.Text = loaded.Service;
            _serviceLog.CaretIndex = loaded.Service.Length; // 최근 줄이 보이게
            _trayLog.Text = loaded.Tray;
            _trayLog.CaretIndex = loaded.Tray.Length;
            _versions.Text = $"트레이 {_tray.TrayVersion ?? "알 수 없음"} · 서비스 {_tray.ServiceVersion ?? "미설치"}{(_tray.ServiceReinstallNeeded ? " (서비스 재설치 필요)" : string.Empty)}";
            _watcher.SelectedItem = _tray.Settings?.Watcher;
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
        _drives.ItemsSource = new object[] { $"전체 드라이브 (게임 {_tray.Games.Count}개)" }.Concat(rows).ToList();
        _drives.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.Letter == _letter) + 1);
        _loading = false;
        ShowRows();
    }

    private void ShowRows()
    {
        _games.ItemsSource = _manage.GameRows(_letter);
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
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,130,2*,50,70,110,2*") };
        for (int i = 0; i < text.Count; i++)
        {
            var t = new TextBlock { Text = text[i], FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }

        return g;
    }
}
