using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Tray.Update;
using AsterCache.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 확인/취소 대화상자 (긴 작업 전에 이동 바이트·되돌리기 안내를 보여줌). 버튼은 오른쪽 아래에 [취소] [확인] 순서 (Enter = 확인, Esc = 취소).
internal sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string text, bool danger)
    {
        Title = "AsterCache";
        Icon = App.LoadIcon("aster.ico");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button yes = Look.Btn(L.T("Btn_Ok"), () => Close(true), danger ? Look.ButtonKind.Danger : Look.ButtonKind.Primary);
        Button no = Look.Btn(L.T("Btn_Cancel"), () => Close(false));
        yes.IsDefault = true;
        no.IsCancel = true;
        Content = new StackPanel
        {
            Margin = new Thickness(Look.S24),
            Spacing = Look.S24,
            Children =
            {
                Look.Text(text),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
            },
        };
    }

    public static ConfirmWindow Create(string text, bool danger = false) => new(text, danger);

    public static Task<bool> Ask(Window owner, string text, bool danger = false) => new ConfirmWindow(text, danger).ShowDialog<bool>(owner);
}

/// 드라이브 관리 유틸리티처럼 한눈에 보고 조작하는 메인 창. 닫으면 숨기기만 합니다 (트레이로). 계산과 aster 호출은 ManageViewModel 이 하고 여기는 배치와 버튼 연결뿐입니다.
/// 위에서 아래로: 머리 (전체 상태 배지) · 안내 띠 · 드라이브 목록 + 탭 4개 · 아래 줄 (마지막 작업 결과).
internal sealed class MainWindow : Window
{
    private readonly ManageViewModel _manage;
    private readonly TrayViewModel _tray;
    private readonly ListBox _drives = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly ListBox _games = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _gamesEmpty = Look.Muted(string.Empty, Look.Body);
    private readonly StackPanel _statusHost = new() { Spacing = Look.S12 };
    private string _statusSig = string.Empty;
    private string _stages = string.Empty;
    private readonly CheckBox _advanced = new() { Content = L.T("Adv_Show") }; // 펼침 상태는 저장하지 않음 (config.json 에 새 키 없음)
    private readonly ContentControl _header = new();
    private readonly TextBox _serviceLog = ReadOnlyBox();
    private readonly TextBox _trayLog = ReadOnlyBox();
    private readonly TextBlock _versions = Look.Text(string.Empty);
    private readonly CheckBox _notifications = new() { Content = L.T("Notify_Enabled"), IsChecked = true };
    private static readonly UpdateMode[] UpdateModes = [UpdateMode.Auto, UpdateMode.Notify, UpdateMode.Off];
    private readonly ComboBox _updateMode = new() { ItemsSource = new[] { L.T("Update_ModeAuto"), L.T("Update_ModeNotify"), L.T("Update_ModeOff") }, Width = 200 };
    private readonly CheckBox _updatePrerelease = new() { Content = L.T("Update_Prerelease") };
    private readonly TextBlock _updateInfo = Look.Muted(string.Empty);
    private Button? _checkUpdateNow;
    private readonly ComboBox _watcher = new() { ItemsSource = new[] { L.T("Watcher_Poll"), "etw" }, Width = 200 };
    private readonly ComboBox _language = new() { ItemsSource = new[] { L.T("Lang_Auto"), L.T("Lang_Ko"), L.T("Lang_En") }, Width = 200 };
    private readonly CheckBox _autoEnabled = new() { Content = L.T("Auto_Enabled") };
    private readonly CheckBox _autoDiscover = new() { Content = L.T("Auto_Discover") };
    private readonly CheckBox _autoTrace = new() { Content = L.T("Auto_Trace") };
    private readonly CheckBox _autoRelocate = new() { Content = L.T("Auto_Relocate") };
    private readonly CheckBox _tierEnabled = new() { Content = L.T("Auto_SsdTier") };
    private readonly TextBox _tierBudget = new() { Width = 160 };
    private readonly TextBox _relocateGib = new() { Width = 160 };
    private readonly ComboBox _autoPrewarm = new() { ItemsSource = Enumerable.Range(-1, 34).Select(n => n == -1 ? L.T("Auto_PrewarmOff") : n == 0 ? L.T("Auto_PrewarmAuto") : n.ToString(CultureInfo.InvariantCulture)).ToList(), Width = 240 };
    private readonly TextBox _autoIgnore = new() { Width = 360 };
    private readonly TextBox _zoneLimit = new() { Width = 160 };
    private readonly TextBox _rewarmMinutes = new() { Width = 160 };
    private readonly CheckBox _advancedShow = new() { Content = L.T("Set_AdvancedShow") }; // 펼침 상태는 저장하지 않음 (config.json 에 새 키 없음)
    private readonly ScrollViewer _settingsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Look.S12, 0) };
    // 마지막으로 화면을 채운 값: 적용할 때 이 값과 같은 칸은 사용자가 건드리지 않은 것이라 파일 값을 그대로 둠 (그 사이 다른 곳에서 바뀐 값을 되돌리지 않게)
    private bool _shownStartStream;
    private string _shownHours = "0|0|0";
    private string?[] _shownAdv = new string?[7];
    private readonly CheckBox _startStream = new() { Content = L.T("Auto_StartStream") };
    private readonly ComboBox _hoursMode = new() { Width = 240 };
    private readonly ComboBox _hoursFrom = new() { ItemsSource = Hours, Width = 100, SelectedIndex = 0 };
    private readonly ComboBox _hoursTo = new() { ItemsSource = Hours, Width = 100, SelectedIndex = 0 };
    private readonly StackPanel _hoursRange = new() { Orientation = Orientation.Horizontal, Spacing = Look.S8, IsVisible = false };
    private readonly TextBox _advCache = new() { Width = 160 };
    private readonly TextBox _advStartupGames = new() { Width = 160 };
    private readonly TextBox _advStartupMax = new() { Width = 160 };
    private readonly TextBox _advRewarmExit = new() { Width = 160 };
    private readonly TextBox _advMaxRelocations = new() { Width = 160 };
    private readonly TextBox _advIdle = new() { Width = 160 };
    private readonly TextBox _advStartCap = new() { Width = 160 };
    private readonly ContentControl _headPill = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _headLine = Look.Text(string.Empty, Look.Subtitle, FontWeight.SemiBold);
    private readonly TextBlock _headProblem = Look.Text(string.Empty, Look.Caption, color: Look.Danger + ".Fg");
    private readonly TextBlock _progress = Look.Text(string.Empty, Look.Body);
    private readonly ProgressBar _busyBar = new() { IsIndeterminate = true, IsVisible = false, Height = 3, MinHeight = 3 };
    private Border? _footer;
    private CancellationTokenSource? _traceStop;
    private readonly TabControl _tabs = new();
    private Task? _traceRun;
    private static readonly List<string> Hours = Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ":00").ToList();
    private static readonly string[] LanguageValues = ["auto", L.Ko, L.En];
    private static readonly string[] WatcherValues = ["poll", "etw"]; // 저장 값 (화면 라벨과 분리)
    private static readonly TimeSpan TraceStopWait = TimeSpan.FromSeconds(10);

    private int _busy;
    private int _layoutBusy; // 게임별 배치 판정을 백그라운드로 채우는 중 (창 작업과는 별개라 _busy 를 잡지 않음)
    private int _layoutAgain; // 판정 조회 요청이 있음: 도는 중에 온 요청은 버리지 않고 끝난 뒤 한 번 더 돌림
    private string? _letter;
    private bool _loading;
    private bool _loaded; // 처음 읽기가 끝나기 전에는 "불러오는 중" 만 보임
    private StatusTone? _headTone;

    public MainWindow(ManageViewModel manage, TrayViewModel tray, Action quit)
    {
        _manage = manage;
        _tray = tray;
        Title = "AsterCache";
        Icon = App.LoadIcon("aster.ico");
        Width = 1180;
        Height = 760;
        MinWidth = 1000;
        MinHeight = 620;
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.WindowClosing) // 앱 종료는 막지 않음
            {
                e.Cancel = true;
                Hide();
            }
        };
        Opened += (_, _) => Reload();

        var driveSide = new DockPanel();
        var driveTitle = Look.Heading(L.T("Pane_Drives"));
        driveTitle.Margin = new Thickness(Look.S4, 0, 0, Look.S8);
        DockPanel.SetDock(driveTitle, Dock.Top);
        driveSide.Children.Add(driveTitle);
        driveSide.Children.Add(_drives);
        _drives.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch { DriveRow d => DriveCard(d), string s => Look.Text(s, Look.Body, FontWeight.SemiBold), _ => new Panel() });
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

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), ColumnSpacing = Look.S16, Margin = new Thickness(Look.S16, Look.S8, Look.S16, Look.S16) };
        Place(Look.Card(driveSide, new Thickness(Look.S12)), body, 0);
        Place(tabs, body, 1);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Place(Head(), root, 0, true);
        Place(Banner(quit), root, 1, true);
        Place(body, root, 2, true);
        Place(Footer(), root, 3, true);
        Content = root;
        ShowLoading();
        UpdateHeader();
    }

    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    public bool Idle => Volatile.Read(ref _busy) == 0 && Volatile.Read(ref _layoutBusy) == 0;

    public void ShowAdvanced(bool on)
    {
        _advanced.IsChecked = on;
        _advancedShow.IsChecked = on;
        if (on)
        {
            Dispatcher.UIThread.Post(() => _settingsScroll.ScrollToEnd(), DispatcherPriority.Background); // 그림 찍기용: 접었다 펼친 고급 구역이 보이게 맨 아래로
        }
    }

    /// 위 머리와 (처음 읽기가 끝난 뒤) 목록·상태 탭 (트레이의 5 초 갱신이 부릅니다).
    public void UpdateStatus()
    {
        UpdateHeader();
        if (!_loaded)
        {
            return;
        }

        // 단계 열은 값이 바뀐 때만 목록을 다시 채워 선택·스크롤을 지킵니다.
        IReadOnlyList<GameRow> rows = _manage.GameRows(_letter);
        string stages = Signature(rows);
        if (stages != _stages)
        {
            _stages = stages;
            int selected = _games.SelectedIndex;
            _games.ItemsSource = rows;
            _games.SelectedIndex = selected < rows.Count ? selected : -1;
        }

        ShowEmpty(rows.Count);
        RenderStatus(rows);
    }

    /// 열 때마다 상태·목록·로그를 다시 읽습니다 (다른 작업 중이면 그 작업이 끝날 때 읽으므로 건너뜀).
    public void Reload()
    {
        if (Volatile.Read(ref _busy) == 0)
        {
            _ = RunAsync(L.T("Op_Loading"), () => new ActionResult(true, string.Empty));
        }
    }

    // ---- 공통 틀 ----

    private Control Head()
    {
        var logo = new Image { Source = App.LoadBitmap("aster-tray.png"), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        var name = Look.Text("AsterCache", Look.Title, FontWeight.SemiBold, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S12, Children = { logo, name } };
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S12, HorizontalAlignment = HorizontalAlignment.Right, Children = { _headPill, _headLine } };
        _headProblem.HorizontalAlignment = HorizontalAlignment.Right;
        var right = new StackPanel { Spacing = Look.S4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { summary, _headProblem } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = Look.S24 };
        Place(brand, row, 0);
        Place(right, row, 1);
        return new Border { Child = row, Padding = new Thickness(Look.S16, Look.S12), BorderThickness = new Thickness(0, 0, 0, 1) }
            .Fill(Border.BackgroundProperty, "Aster.Surface").Fill(Border.BorderBrushProperty, "Aster.Border");
    }

    /// 머리: 전체 상태 배지 (색 + 글자) 와 한 줄 요약. 설정 오류·재설치 필요는 아래 줄에 빨갛게.
    private void UpdateHeader()
    {
        TraySummary sum = TrayStatus.Summarize(_tray.Service, _tray.Games, _manage.Auto, _tray.HasProblem);
        if (_headTone != sum.Tone || _headPill.Content is null)
        {
            _headTone = sum.Tone;
            _headPill.Content = Look.Pill(sum.Tone, TrayStatus.ToneWord(sum.Tone), Look.Body);
        }

        _headLine.Text = sum.Line;
        _headProblem.Text = _tray.HasProblem ? _tray.StatusText : string.Empty;
        _headProblem.IsVisible = _headProblem.Text.Length > 0;
    }

    private Control Footer()
    {
        _progress.Margin = new Thickness(Look.S16, Look.S8);
        _progress.TextTrimming = TextTrimming.CharacterEllipsis;
        _progress.MaxLines = 2;
        _progress.Text = string.Empty;
        _footer = new Border { Child = new StackPanel { Children = { _busyBar, _progress } }, BorderThickness = new Thickness(0, 1, 0, 0), IsVisible = false }
            .Fill(Border.BackgroundProperty, "Aster.Surface").Fill(Border.BorderBrushProperty, "Aster.Border");
        return _footer;
    }

    /// 아래 줄에 마지막 작업 결과를 보입니다 (실패는 빨간 글자).
    private void Say(string text, bool ok = true)
    {
        _progress.Text = text;
        _progress.Fill(TextBlock.ForegroundProperty, ok ? "Aster.Text" : Look.Danger + ".Fg");
        ShowFooter();
    }

    /// 아래 줄은 보일 글이나 진행 띠가 있을 때만 자리를 차지합니다.
    private void ShowFooter()
    {
        if (_footer is not null)
        {
            _footer.IsVisible = _busyBar.IsVisible || !string.IsNullOrEmpty(_progress.Text);
        }
    }

    private Control Banner(Action quit)
    {
        var relaunch = Look.Btn(L.T("Btn_Relaunch"), () => { }, Look.ButtonKind.Primary);
        relaunch.Click += async (_, _) =>
        {
            await StopTraceAsync();
            ActionResult r = _manage.RelaunchAsAdmin();
            Say(r.Message, r.Ok);
            if (r.Ok)
            {
                quit();
            }
        };
        var admin = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(relaunch, Dock.Right);
        relaunch.Margin = new Thickness(Look.S16, 0, 0, 0);
        admin.Children.Add(relaunch);
        admin.Children.Add(Look.Text(L.T("Banner_NotAdmin"), Look.Body, color: Look.Key(StatusTone.Warn) + ".Fg"));
        Border notAdmin = Look.Banner(Look.Key(StatusTone.Warn), admin);
        notAdmin.IsVisible = !_manage.IsAdmin;
        Border exe = Look.Banner(Look.Danger, Look.Text(_manage.ElevatedExeWarning ?? string.Empty, Look.Body, color: Look.Danger + ".Fg"));
        exe.IsVisible = _manage.ElevatedExeWarning is not null && App.ScreenshotPath is null; // 그림 찍기 (예시 모드) 에서는 숨김
        return new StackPanel { Spacing = Look.S8, Margin = new Thickness(Look.S16, Look.S12, Look.S16, 0), Children = { notAdmin, exe } };
    }

    /// 드라이브 한 칸: 문자·이름 + 종류 배지, 모델·파일 시스템, 사용량 막대, 여유 공간과 게임 수.
    private static Control DriveCard(DriveRow d)
    {
        double used = d.TotalBytes > 0 ? 100.0 * (d.TotalBytes - d.FreeBytes) / d.TotalBytes : 0;
        bool low = d.TotalBytes > 0 && d.FreeBytes * 10 < d.TotalBytes; // 여유 10% 미만이면 주의색
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = used, Height = 6, MinHeight = 6, ShowProgressText = false, CornerRadius = new CornerRadius(3) }
            .Fill(RangeBase.ForegroundProperty, low ? Look.Key(StatusTone.Warn) + ".Dot" : "Aster.Accent");
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = Look.S8 };
        TextBlock name = Look.Text($"{d.Letter} {(d.Label.Length == 0 ? L.T("Drive_NoName") : d.Label)}", Look.Body, FontWeight.SemiBold, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Border kind = Look.Badge(d.Kind);
        Grid.SetColumn(kind, 1);
        title.Children.Add(name);
        title.Children.Add(kind);
        var card = new StackPanel { Spacing = Look.S4, Margin = new Thickness(0, Look.S4) };
        card.Children.Add(title);
        card.Children.Add(Look.Muted($"{(d.Model.Length == 0 ? L.T("Drive_ModelUnknown") : d.Model)} · {d.FileSystem}"));
        card.Children.Add(bar);
        card.Children.Add(Look.Muted(L.T("Drive_Line", ManageViewModel.Size(d.FreeBytes), ManageViewModel.Size(d.TotalBytes), d.Games)));
        return card;
    }

    /// 진행 중인 트레이스가 있으면 stop-file 로 정상 종료를 요청하고 최대 10 초 기다립니다 (앱 종료·관리자 재실행 전에 부름).
    public async Task StopTraceAsync()
    {
        if (_traceRun is not { IsCompleted: false } run)
        {
            return;
        }

        Say(L.T("Msg_SavingTrace"));
        _traceStop?.Cancel();
        await Task.WhenAny(run, Task.Delay(TraceStopWait));
    }

    private static Control Page(Control content) => new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Look.S12, 0) };

    private static Control TabBody(Control content)
    {
        content.Margin = new Thickness(0, Look.S12, 0, 0);
        return content;
    }

    // ---- 상태 탭 ----

    private Control StatusTab()
    {
        var refresh = Look.Btn(L.T("Btn_Refresh"), () => _ = RunAsync(L.T("Btn_Refresh"), () => new ActionResult(true, L.T("Btn_Refresh"))));
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Top);
        refresh.Margin = new Thickness(0, 0, Look.S12, Look.S8);
        panel.Children.Add(refresh);
        panel.Children.Add(Page(_statusHost));
        return TabBody(panel);
    }

    private void ShowLoading()
    {
        _statusSig = "loading";
        _statusHost.Children.Clear();
        _statusHost.Children.Add(Look.Card(Look.Muted(L.T("Op_Loading") + " ...", Look.Body)));
        _gamesEmpty.Text = L.T("Op_Loading") + " ...";
        _gamesEmpty.IsVisible = true;
    }

    private void ShowEmpty(int rows)
    {
        _gamesEmpty.Text = !_loaded ? L.T("Op_Loading") + " ..." : _manage.EmptyGamesText(_letter);
        _gamesEmpty.IsVisible = rows == 0;
    }

    /// 상태 탭: ① 지금 상태 (배지·요약·감시기·마지막 예열) ② 자동 진행 숫자 타일 ③ 게임별 카드 (확인이 필요한 것부터). 내용이 바뀐 때만 다시 그립니다 (스크롤 유지).
    private void RenderStatus(IReadOnlyList<GameRow> rows)
    {
        TraySummary sum = TrayStatus.Summarize(_tray.Service, _tray.Games, _manage.Auto, _tray.HasProblem);
        (string watcher, string lastPrewarm) = _manage.StatusInfo();
        AutoProgress? progress = _manage.Progress() is { Discovered: > 0 } p ? p : null;
        string sig = string.Join("\n", new[] { sum.Tone.ToString(), sum.Line, _tray.StatusText, watcher, lastPrewarm, progress is null ? string.Empty : progress.Line(L.Current) ?? string.Empty, _letter ?? string.Empty }
            .Concat(rows.Select(r => string.Join("|", r.Name, r.State, r.StateTone, r.PrewarmState, r.PrewarmTone, r.Reason, r.Detail, r.AutoLine, r.Play, r.Policy)))
            .Append(_manage.EmptyGamesText(_letter)));
        if (sig == _statusSig)
        {
            return;
        }

        _statusSig = sig;
        _statusHost.Children.Clear();

        var now = new StackPanel { Spacing = Look.S8 };
        var headline = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S12, Children = { Look.Pill(sum.Tone, TrayStatus.ToneWord(sum.Tone), Look.Body), Look.Text(sum.Line, Look.Subtitle, FontWeight.SemiBold) } };
        now.Children.Add(headline);
        if (_tray.HasProblem)
        {
            now.Children.Add(Look.Banner(Look.Danger, Look.Text(_tray.StatusText, Look.Body, color: Look.Danger + ".Fg")));
        }

        now.Children.Add(Look.Muted(watcher));
        now.Children.Add(Look.Muted(lastPrewarm));
        _statusHost.Children.Add(Look.Card(now));

        if (progress is not null)
        {
            _statusHost.Children.Add(Look.Heading(L.T("Sec_Progress")));
            var tiles = new Grid { ColumnSpacing = Look.S12 }; // 별(*) 열이라 남는 폭을 균등 분할하고 넘치지 않음
            var stats = new List<Control>
            {
                Look.Stat(L.T("Unit_Count", progress.Discovered), L.T("Stat_Found"), L.T("Stat_FoundNote")),
                Look.Stat(L.T("Unit_Count", progress.Planned), L.T("Stat_Planned"), L.T("Stat_PlannedNote")),
                Look.Stat(L.T("Unit_Count", progress.Relocated), L.T("Stat_Relocated"), L.T("Stat_RelocatedNote")),
                Look.Stat(L.T("Unit_Count", progress.Prewarmed), L.T("Stat_Prewarmed"), L.T("Stat_PrewarmedNote")),
            };
            if (progress.HitAverage is { } hit)
            {
                stats.Add(Look.Stat(hit.ToString(CultureInfo.InvariantCulture) + "%", L.T("Stat_Hit"), L.T("Stat_HitNote")));
            }

            for (int i = 0; i < stats.Count; i++)
            {
                tiles.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 0 });
                Place(stats[i], tiles, i);
            }

            _statusHost.Children.Add(tiles);
        }

        _statusHost.Children.Add(Look.Heading(L.T("Sec_Games")));
        if (rows.Count == 0)
        {
            _statusHost.Children.Add(Look.Card(Look.Muted(_manage.EmptyGamesText(_letter), Look.Body)));
        }

        foreach (GameRow r in rows.OrderBy(r => Rank(r.StateTone)).ThenBy(r => r.DisplayName.Length == 0 ? r.Name : r.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _statusHost.Children.Add(GameCard(r));
        }
    }

    private static int Rank(StatusTone tone) => tone switch { StatusTone.Warn => 0, StatusTone.Busy => 1, StatusTone.Gray => 2, _ => 3 };

    /// 게임 카드: 이름 + 상태 배지 둘 (자동 단계·예열), 배치·예열 설명, 보류 이유, 자동 단계 흐름.
    private static Control GameCard(GameRow r)
    {
        string display = r.DisplayName.Length == 0 ? r.Name : r.DisplayName;
        var name = Look.Text(display, Look.Body, FontWeight.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        var pills = new WrapPanel { ItemSpacing = Look.S8, LineSpacing = Look.S4, HorizontalAlignment = HorizontalAlignment.Right, Children = { Look.Pill(r.StateTone, r.State), Look.Pill(r.PrewarmTone, r.PrewarmState) } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = Look.S12 };
        Place(name, top, 0);
        Place(pills, top, 1);
        var stack = new StackPanel { Spacing = Look.S4, Children = { top, Look.Muted(r.Detail) } };
        if (r.Reason.Length > 0)
        {
            stack.Children.Add(Look.Text($"{L.T("Col_Reason")}: {r.Reason}", Look.Caption, color: Look.Key(r.StateTone == StatusTone.Gray ? StatusTone.Warn : r.StateTone) + ".Fg"));
        }

        if (r.Policy.Length > 0)
        {
            stack.Children.Add(Look.Text(r.Policy, Look.Caption, FontWeight.SemiBold, Look.Key(StatusTone.Warn) + ".Fg"));
        }

        if (r.AutoLine.Length > 0)
        {
            stack.Children.Add(Look.Muted(r.AutoLine));
        }

        if (r.Play.Length > 0)
        {
            stack.Children.Add(Look.Muted(r.Play));
        }

        Border card = Look.Card(stack, new Thickness(Look.S16, Look.S12));
        ToolTip.SetTip(card, r.Name);
        return card;
    }

    // ---- 게임 탭 ----

    private Control GamesTab()
    {
        _games.ItemTemplate = new FuncDataTemplate<GameRow>((row, _) => row is null ? new Panel() : Cells(
            [row.DisplayName.Length == 0 ? row.Name : row.DisplayName, row.State, row.PrewarmState, row.Reason, row.AntiCheat, row.Exe, row.Origin, L.T(row.HasPlan ? "Cell_Yes" : "Cell_No"), L.T(row.HasTrace ? "Cell_Yes" : "Cell_No"), row.Verdict, row.Prewarm, row.Source, row.Stage, row.Gate],
            policy: row.Policy,
            tips: [row.Name, null, null, null, null, null, null, null, null, row.VerdictRaw.Length == 0 ? null : row.VerdictRaw, row.PrewarmFull, null, row.StageRaw.Length == 0 ? row.StageFull : row.StageFull + "\n" + row.StageRaw, null],
            tones: [null, row.StateTone, row.PrewarmTone], advanced: _advanced.IsChecked == true));
        _games.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(TemplatedControl.PaddingProperty, RowPadding) },
        });

        // 버튼 순서: 등록 | 수집 | 배치 | 예열·측정 | 프로필 | 제거 (위험은 맨 끝, 빨간 글자). 묶음 단위로 줄바꿈됩니다.
        var buttons = new WrapPanel { ItemSpacing = Look.S16, LineSpacing = Look.S8 };
        buttons.Children.Add(Group(Look.Btn(L.T("Btn_Register"), Register, Look.ButtonKind.Primary)));
        buttons.Children.Add(Group(
            Look.Btn(L.T("Btn_CollectTrace"), () => WithGame(g =>
            {
                _traceStop = new CancellationTokenSource();
                CancellationToken stop = _traceStop.Token;
                return _traceRun = RunAsync(L.T("Btn_CollectTrace"), () => _manage.CollectTrace(g, Progress, stop));
            })),
            Look.Btn(L.T("Btn_StopCollect"), () => _traceStop?.Cancel())));
        buttons.Children.Add(Group(
            Look.Btn(L.T("Btn_MakePlan"), () => WithGame(g => RunAsync(L.T("Btn_MakePlan"), () => _manage.ChangesLayout(g, () => _manage.MakePlan(g, Progress))))),
            Look.Btn(L.T("Btn_Apply"), () => WithGame(Apply)),
            Look.Btn(L.T("Btn_Revert"), () => WithGame(Revert))));
        buttons.Children.Add(Group(
            Look.Btn(L.T("Btn_Prewarm"), () => WithGame(g => RunAsync(L.T("Op_PrewarmStream"), () => _tray.StreamNow(g)))),
            Look.Btn(L.T("Btn_Measure"), () => WithGame(g => RunAsync(L.T("Btn_Measure"), () => _manage.Measure(g))))));
        buttons.Children.Add(Group(
            Look.Btn(L.T("Btn_ExportProfile"), () => WithGame(ExportProfile)),
            Look.Btn(L.T("Btn_ImportProfile"), () => WithGame(ImportProfile))));
        buttons.Children.Add(Group(Look.Btn(L.T("Btn_GameOptions"), () => WithGame(GameOptions))));
        buttons.Children.Add(Group(Look.Btn(L.T("Btn_Remove"), () => WithGame(g => Confirmed(L.T("Confirm_Remove", g), L.T("Btn_Remove"), () => _tray.RemoveGame(g), danger: true)), Look.ButtonKind.Danger)));

        var hint = Look.Muted(L.T("Msg_SelectGame"));
        hint.VerticalAlignment = VerticalAlignment.Center;
        _advanced.HorizontalAlignment = HorizontalAlignment.Right;
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = Look.S12 };
        Place(hint, options, 0);
        Place(_advanced, options, 1);

        var table = new DockPanel();
        Grid.SetIsSharedSizeScope(table, true); // 헤더와 행이 열 폭을 함께 정함
        ShowHeader();
        DockPanel.SetDock(_header, Dock.Top);
        table.Children.Add(_header);
        table.Children.Add(new Grid { Children = { _games, _gamesEmpty } });
        _gamesEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _gamesEmpty.VerticalAlignment = VerticalAlignment.Top;
        _gamesEmpty.Margin = new Thickness(Look.S24);
        _gamesEmpty.TextAlignment = TextAlignment.Center;
        _gamesEmpty.IsHitTestVisible = false;
        // 표: 기본 열은 창 폭에 맞춰 줄바꿈하고, 고급 열을 켜면 가로로 밀어 볼 수 있습니다.
        var scroller = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _advanced.IsCheckedChanged += (_, _) =>
        {
            scroller.HorizontalScrollBarVisibility = _advanced.IsChecked == true ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            int selected = _games.SelectedIndex;
            ShowHeader();
            ShowRows();
            _games.SelectedIndex = selected < _games.ItemCount ? selected : -1; // 토글은 행 수를 바꾸지 않으므로 선택을 지킴
        };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(options, Dock.Top);
        buttons.Margin = new Thickness(0, 0, 0, Look.S12);
        options.Margin = new Thickness(0, 0, 0, Look.S8);
        panel.Children.Add(buttons);
        panel.Children.Add(options);
        panel.Children.Add(Look.Card(scroller, new Thickness(0)));
        return TabBody(panel);
    }

    private static StackPanel Group(params Control[] buttons)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8 };
        group.Children.AddRange(buttons);
        return group;
    }

    private void ShowHeader()
    {
        Grid header = Cells([L.T("Col_Name"), L.T("Col_State"), L.T("Col_PrewarmState"), L.T("Col_Reason"), L.T("Col_AntiCheat"), L.T("Col_Exe"), L.T("Col_Origin"), L.T("Col_Plan"), L.T("Col_Trace"), L.T("Col_Layout"), L.T("Col_Prewarm"), L.T("Col_Source"), L.T("Col_Stage"), L.T("Col_Gate")], header: true, advanced: _advanced.IsChecked == true);
        _header.Content = new Border { Child = header, Padding = new Thickness(RowPadding.Left, Look.S8, RowPadding.Right, Look.S8), BorderThickness = new Thickness(0, 0, 0, 1) }
            .Fill(Border.BackgroundProperty, "Aster.SurfaceAlt").Fill(Border.BorderBrushProperty, "Aster.Border");
    }

    // ---- 설정 탭 ----

    private Control SettingsTab()
    {
        Button apply = Look.Btn(L.T("Btn_ApplyWatcher"), () => _ = ApplyWatcher(), Look.ButtonKind.Primary);
        Button applyLanguage = Look.Btn(L.T("Btn_ApplyLanguage"), () => _ = ApplyLanguage(), Look.ButtonKind.Primary);
        Button applyAuto = Look.Btn(L.T("Btn_ApplyAuto"), () => _ = ApplyAuto(), Look.ButtonKind.Primary);
        Button applyNotifications = Look.Btn(L.T("Btn_ApplyNotifications"), () => _ = ApplyNotifications(), Look.ButtonKind.Primary);
        Button applyUpdate = Look.Btn(L.T("Btn_ApplyUpdate"), () => _ = ApplyUpdate(), Look.ButtonKind.Primary);
        _checkUpdateNow = Look.Btn(L.T("Btn_CheckUpdateNow"), () => _ = App.CheckUpdateNowAsync());
        _checkUpdateNow.IsEnabled = App.Updater is not null;
        applyAuto.HorizontalAlignment = HorizontalAlignment.Right;
        var checks = new WrapPanel { ItemSpacing = Look.S24, LineSpacing = Look.S8, Children = { _autoEnabled, _autoDiscover, _autoTrace, _autoRelocate, _startStream, _tierEnabled } };
        Grid auto = Look.Form(
            (L.T("Auto_Prewarm"), _autoPrewarm), (L.T("Auto_Ignore"), _autoIgnore), (L.T("Auto_ZoneLimit"), _zoneLimit), (L.T("Auto_Rewarm"), _rewarmMinutes),
            (L.T("Auto_RelocateGib"), _relocateGib), (L.T("Auto_RelocateHours"), RelocateHoursInput()), (L.T("Auto_SsdTierBudget"), _tierBudget));
        Button applyAdvanced = Look.Btn(L.T("Btn_ApplyAdvanced"), () => _ = ApplyAuto(), Look.ButtonKind.Primary);
        Grid advancedForm = NotedForm(
            (L.T("Adv_CacheMib"), _advCache, "Adv_CacheMibNote"), (L.T("Adv_StartupGames"), _advStartupGames, "Adv_StartupGamesNote"),
            (L.T("Adv_StartupMax"), _advStartupMax, "Adv_StartupMaxNote"), (L.T("Adv_RewarmAfterExit"), _advRewarmExit, "Adv_RewarmAfterExitNote"),
            (L.T("Adv_MaxRelocations"), _advMaxRelocations, "Adv_MaxRelocationsNote"), (L.T("Adv_IdleSeconds"), _advIdle, "Adv_IdleSecondsNote"),
            (L.T("Adv_StartStreamCap"), _advStartCap, "Adv_StartStreamCapNote"));
        var advancedBody = new StackPanel { Spacing = Look.S12, IsVisible = false, Children = { Look.Muted(L.T("Set_AdvancedNote")), advancedForm, applyAdvanced } };
        _advancedShow.IsCheckedChanged += (_, _) => advancedBody.IsVisible = _advancedShow.IsChecked == true;
        Border advanced = Look.Section(L.T("Set_Advanced"), null, _advancedShow, advancedBody);

        // 서비스: 시작·정지 | 설치·제거 (되돌리기 어려운 제거는 빨간 글자)
        var service = new WrapPanel { ItemSpacing = Look.S16, LineSpacing = Look.S8 };
        service.Children.Add(Group(
            Look.Btn(L.T("Btn_ServiceStart"), () => _ = RunAsync(L.T("Btn_ServiceStart"), () => _tray.StartService())),
            Look.Btn(L.T("Btn_ServiceStop"), () => _ = RunAsync(L.T("Btn_ServiceStop"), () => _tray.StopService()))));
        service.Children.Add(Group(
            Look.Btn(L.T("Btn_ServiceInstall"), () => _ = Confirmed(L.T("Confirm_ServiceInstall"), L.T("Btn_ServiceInstall"), () => _manage.InstallService())),
            Look.Btn(L.T("Btn_ServiceUninstall"), () => _ = Confirmed(L.T("Confirm_ServiceUninstall"), L.T("Btn_ServiceUninstall"), () => _tray.UninstallService(), danger: true), Look.ButtonKind.Danger)));
        var folders = new WrapPanel
        {
            ItemSpacing = Look.S8,
            LineSpacing = Look.S8,
            Children = { Look.Btn(L.T("Btn_OpenData"), () => Say(_manage.OpenDataFolder().Message)), Look.Btn(L.T("Btn_OpenLogs"), () => Say(_tray.OpenLogs().Message)) },
        };
        _settingsScroll.Content = new StackPanel
        {
            Spacing = Look.S12,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Look.Section(L.T("Set_Auto"), L.T("Set_AutoNote"), checks, auto, Look.Muted(L.T("Set_PinNote")), applyAuto),
                Look.Section(L.T("Set_Service"), null, service),
                Look.Section(L.T("Set_Watcher"), null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, Children = { _watcher, apply } }, Look.Muted(L.T("Set_WatcherPoll") + Environment.NewLine + L.T("Set_WatcherEtw"))),
                Look.Section(L.T("Set_PrewarmDefaults"), null, Look.Text(ManageViewModel.DescribePrewarm(PrewarmSettings.Default))),
                Look.Section(L.T("Set_Language"), null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, Children = { _language, applyLanguage } }),
                Look.Section(L.T("Set_Notifications"), L.T("Set_NotificationsNote"), _notifications, applyNotifications),
                Look.Section(L.T("Set_Update"), L.T("Set_UpdateNote"), new WrapPanel { ItemSpacing = Look.S24, LineSpacing = Look.S8, Children = { _updateMode, _updatePrerelease } }, _updateInfo,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, Children = { applyUpdate, _checkUpdateNow } }),
                Look.Section(L.T("Set_Folders"), null, folders),
                Look.Section(L.T("Set_Versions"), null, _versions),
                advanced,
            },
        };
        return TabBody(_settingsScroll);
    }

    // ---- 로그 탭 ----

    private Control LogsTab()
    {
        var refresh = Look.Btn(L.T("Btn_Refresh"), () => _ = RunAsync(L.T("Btn_Refresh"), () => new ActionResult(true, L.T("Btn_Refresh"))));
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*"), RowSpacing = Look.S8 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var serviceTitle = Look.Heading(L.T("Log_Service"));
        serviceTitle.VerticalAlignment = VerticalAlignment.Center;
        Place(serviceTitle, top, 0);
        Place(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, Children = { Look.Btn(L.T("Btn_ExportDiag"), () => _ = ExportDiagnostics()), refresh } }, top, 1);
        Place(top, grid, 0, true);
        Place(_serviceLog, grid, 1, true);
        Place(Look.Heading(L.T("Log_Tray")), grid, 2, true);
        Place(_trayLog, grid, 3, true);
        return TabBody(grid);
    }

    // 설정 탭 적용: 컨트롤 값은 UI 스레드에서 지금 읽고, 배경 작업(RunAsync)에는 값만 넘깁니다 (배경 스레드에서 컨트롤을 읽으면 Avalonia 가 InvalidOperationException 을 냄).
    private Task<ActionResult> ApplyAuto()
    {
        var form = new AutoForm(
            _autoEnabled.IsChecked == true, _autoDiscover.IsChecked == true, _autoTrace.IsChecked == true, _autoRelocate.IsChecked == true,
            Math.Max(0, _autoPrewarm.SelectedIndex) - 1, _autoIgnore.Text ?? string.Empty, _zoneLimit.Text ?? string.Empty, _rewarmMinutes.Text ?? string.Empty)
        {
            TierEnabled = _tierEnabled.IsChecked == true,
            TierBudget = _tierBudget.Text ?? string.Empty,
            RelocateGib = _relocateGib.Text ?? string.Empty,
            StartStream = (_startStream.IsChecked == true) == _shownStartStream ? null : _startStream.IsChecked == true,
            RelocateHours = HoursKey() == _shownHours ? null : _hoursMode.SelectedIndex switch { 0 => [], 1 => TrayViewModel.HoursBetween(_hoursFrom.SelectedIndex, _hoursTo.SelectedIndex), _ => null },
            Advanced = new AdvancedForm(Changed(0, _advCache), Changed(1, _advStartupGames), Changed(2, _advStartupMax), Changed(3, _advRewarmExit), Changed(4, _advMaxRelocations), Changed(5, _advIdle), Changed(6, _advStartCap)),
        };
        if (form.RelocateHours is { Count: 0 } && _hoursMode.SelectedIndex == 1 && _hoursFrom.SelectedIndex == _hoursTo.SelectedIndex)
        {
            Say(L.T("Err_HoursSame"), false);
            return Task.FromResult(new ActionResult(false, L.T("Err_HoursSame")));
        }

        return RunAsync(L.T("Op_AutoChange"), () => _tray.ApplyAuto(form));
    }

    private string HoursKey() => $"{_hoursMode.SelectedIndex}|{_hoursFrom.SelectedIndex}|{_hoursTo.SelectedIndex}";

    // 고급 칸의 글: 채운 값과 같으면 null (건드리지 않음).
    private string? Changed(int at, TextBox box) => (box.Text ?? string.Empty) == _shownAdv[at] ? null : box.Text ?? string.Empty;

    // 재배치 시간대 입력: 언제든 / 시간 지정 (시작 ~ 끝) 한 구간. 파일에 한 구간으로 안 되는 값이 있으면 세 번째 항목으로 보존만 합니다.
    private Control RelocateHoursInput()
    {
        _hoursMode.ItemsSource = new[] { L.T("Hours_Anytime"), L.T("Hours_Range") };
        _hoursMode.SelectedIndex = 0;
        _hoursMode.SelectionChanged += (_, _) =>
        {
            _hoursRange.IsVisible = _hoursMode.SelectedIndex == 1;
            if (_hoursMode.SelectedIndex == 1 && _hoursFrom.SelectedIndex == _hoursTo.SelectedIndex)
            {
                _hoursFrom.SelectedIndex = 2; // 처음 고를 때의 기본 구간 (서로 달라야 바로 저장할 수 있음)
                _hoursTo.SelectedIndex = 7;
            }
        };
        _hoursRange.Children.Add(_hoursFrom);
        _hoursRange.Children.Add(Look.Text("~", Look.Body));
        _hoursRange.Children.Add(_hoursTo);
        return new StackPanel
        {
            Spacing = Look.S4,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, Children = { _hoursMode, _hoursRange } }, Look.Muted(L.T("Auto_RelocateHoursNote")) },
        };
    }

    // Look.Form 처럼 왼쪽 설명 + 오른쪽 입력인데, 설명 아래에 한 줄 설명 (보조 글) 을 붙입니다.
    private static Grid NotedForm(params (string Label, TextBox Box, string NoteKey)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), ColumnSpacing = Look.S16, RowSpacing = Look.S12 };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new StackPanel { Spacing = Look.S4, Children = { Look.Text(rows[i].Label, Look.Body), Look.Muted(L.T(rows[i].NoteKey)) } };
            Grid.SetRow(label, i);
            rows[i].Box.HorizontalAlignment = HorizontalAlignment.Left;
            rows[i].Box.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(rows[i].Box, i);
            Grid.SetColumn(rows[i].Box, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[i].Box);
        }

        return grid;
    }

    // 파일의 재배치 시간대를 화면에 맞춤 (UI 스레드에서 부름).
    private void ShowRelocateHours(IReadOnlyList<int> hours)
    {
        (int From, int To)? range = TrayViewModel.RangeOf(hours);
        bool custom = hours.Count > 0 && range is null; // 한 구간으로 안 되는 값: 화면에서 건드리지 않으면 그대로 둠
        _hoursMode.ItemsSource = custom ? new[] { L.T("Hours_Anytime"), L.T("Hours_Range"), L.T("Hours_Custom") } : new[] { L.T("Hours_Anytime"), L.T("Hours_Range") };
        _hoursMode.SelectedIndex = custom ? 2 : range is null ? 0 : 1;
        if (range is { } r)
        {
            _hoursFrom.SelectedIndex = r.From;
            _hoursTo.SelectedIndex = r.To;
        }
    }

    private Task<ActionResult> ApplyLanguage()
    {
        string language = LanguageValues[Math.Max(0, _language.SelectedIndex)];
        return RunAsync(L.T("Op_LanguageChange"), () => _tray.SetLanguage(language));
    }

    private Task<ActionResult> ApplyNotifications()
    {
        bool on = _notifications.IsChecked == true;
        return RunAsync(L.T("Op_NotificationsChange"), () => _tray.SetNotifications(on));
    }

    private Task<ActionResult> ApplyUpdate()
    {
        UpdateMode mode = UpdateModes[Math.Max(0, _updateMode.SelectedIndex)];
        bool prerelease = _updatePrerelease.IsChecked == true;
        return RunAsync(L.T("Op_UpdateChange"), () =>
        {
            ActionResult r = _tray.SetUpdate(mode, prerelease);
            if (r.Ok)
            {
                _ = App.ReevaluateUpdateAsync(); // 저장한 방식을 다음 확인 주기까지 기다리지 않고 바로 반영
            }

            return r;
        });
    }

    /// 설정 탭의 업데이트 구역에 현재 버전, 마지막 확인 시각, 현재 상태를 보입니다 (UI 스레드에서 부름).
    public void RefreshUpdateInfo()
    {
        AutoUpdater? updater = App.Updater;
        string current = UpdateEnvironment.CurrentVersion(typeof(App).Assembly)?.Core ?? L.T("Word_Unknown");
        string last = updater?.LastChecked is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : L.T("Update_NeverChecked");
        string state = updater is null ? L.T("Update_Unavailable") : updater.Status.State == UpdateState.Idle ? L.T("Update_NoStatus") : updater.Status.Text();
        _updateInfo.Text = L.T("Update_Info", current, last, state);
        if (_checkUpdateNow is not null)
        {
            _checkUpdateNow.IsEnabled = updater is not null && updater.Status.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing);
        }
    }

    private async Task ExportDiagnostics()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Pick_DiagSave"),
            SuggestedFileName = $"AsterCache-diagnostics-{DateTime.Now:yyyyMMdd}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType(L.T("Pick_DiagFilter")) { Patterns = ["*.zip"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        string target = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path : path + ".zip";
        await RunAsync(L.T("Btn_ExportDiag"), () => _manage.ExportDiagnostics(target));
    }

    private Task<ActionResult> ApplyWatcher()
    {
        string watcher = WatcherValues[Math.Max(0, _watcher.SelectedIndex)];
        return RunAsync(L.T("Op_WatcherChange"), () => _tray.SetWatcher(watcher));
    }

    /// --smoke --click <apply-auto|apply-language|apply-watcher|apply-update>: 해당 버튼의 처리 경로를 실행하고 결과를 돌려줍니다 (열 때 읽기가 끝나기를 기다린 뒤).
    public async Task<ActionResult> SmokeClick(string name)
    {
        for (int i = 0; i < 100 && Volatile.Read(ref _busy) != 0; i++)
        {
            await Task.Delay(100);
        }

        return name switch
        {
            "apply-auto" => await ApplyAuto(),
            "apply-language" => await ApplyLanguage(),
            "apply-watcher" => await ApplyWatcher(),
            "apply-update" => await ApplyUpdate(),
            _ => new ActionResult(false, "unknown click: " + name),
        };
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

    private async Task GameOptions(string name)
    {
        if (_tray.Settings?.Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } game)
        {
            Say(L.T("Err_NotRegistered", name), false);
            return;
        }

        var window = new GameOptionsWindow(_tray, game);
        await window.ShowDialog(this);
        if (window.Saved is { } saved)
        {
            await RunAsync(L.T("Op_GameOptions"), () => saved); // 저장은 창이 끝냈으니 목록·상태만 다시 읽고 결과를 아래 줄에 보임
        }
    }

    private async Task Apply(string name)
    {
        long? moved = await Task.Run(() => _manage.MovedBytes(name));
        string text = L.T("Confirm_Apply", _tray.DisplayName(name), moved is { } m ? ManageViewModel.Size(m) : L.T("Confirm_ApplySizeUnknown"));
        if (await ConfirmWindow.Ask(this, text))
        {
            await RunAsync(L.T("Op_Relocate"), () => _manage.ChangesLayout(name, () => _manage.Apply(name, Progress)));
        }
    }

    private async Task Revert(string name)
    {
        if (await ConfirmWindow.Ask(this, L.T("Confirm_Revert", _tray.DisplayName(name)), danger: true))
        {
            await RunAsync(L.T("Btn_Revert"), () => _manage.ChangesLayout(name, () => _manage.Revert(name, Progress)));
        }
    }

    private async Task ExportProfile(string name)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Pick_ProfileSave"),
            SuggestedFileName = name + ".aster-profile.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType(L.T("Pick_ProfileFilter")) { Patterns = ["*.json", "*.zip"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        string target = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json";
        await RunAsync(L.T("Btn_ExportProfile"), () => _manage.ExportProfile(name, target));
    }

    private async Task ImportProfile(string name)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Pick_ProfileOpen"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("Pick_ProfileFilter")) { Patterns = ["*.json", "*.zip"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await RunAsync(L.T("Btn_ImportProfile"), () => _manage.ChangesLayout(name, () => _manage.ImportProfile(name, path)));
    }

    private async Task Confirmed(string text, string what, Func<ActionResult> work, bool danger = false)
    {
        if (await ConfirmWindow.Ask(this, text, danger))
        {
            await RunAsync(what, work);
        }
    }

    private void Progress(string text) => Dispatcher.UIThread.Post(() => Say(text));

    private async void WithGame(Func<string, Task> act)
    {
        if (_games.SelectedItem is GameRow row)
        {
            await act(row.Name);
        }
        else
        {
            Say(L.T("Msg_SelectGame"), false);
        }
    }

    /// 한 번에 하나만: 작업을 백그라운드로 돌리고 (진행 띠 표시), 끝나면 상태·목록·로그를 다시 읽어 그립니다.
    private async Task<ActionResult> RunAsync(string what, Func<ActionResult> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Say(L.T("Msg_Busy"), false);
            return new ActionResult(false, L.T("Msg_Busy"));
        }

        _busyBar.IsVisible = true;
        Say(what + " ..."); // 아래 줄도 같이 보임
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
                    // 창 맨 위 경계: 어떤 예외도 앱을 죽이지 않고 메시지로 보여주고 tray.log 에 남김
                    App.Log.Error(what + " failed", e);
                    return new ActionResult(false, L.T("Msg_OpFailed", what, TrayViewModel.Brief(e)));
                }
            });
            Say(r.Message, r.Ok);

            var loaded = await Task.Run(() =>
            {
                try
                {
                    _manage.RefreshOverview(); // 게임별 배치 판정은 아래 RefreshLayouts 가 따로 채움: 목록은 이것만으로 바로 보임
                    return (Rows: _manage.Drives(), Service: _manage.ServiceLog(), Tray: _manage.TrayLog());
                }
                catch (Exception e)
                {
                    // 창 맨 위 경계: 상태를 못 읽어도 앱을 죽이지 않음
                    App.Log.Error("status reload failed", e);
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
            _autoPrewarm.SelectedIndex = Math.Clamp(auto.PrewarmRecent, -1, 32) + 1;
            _autoIgnore.Text = string.Join(", ", auto.IgnoreExes);
            _rewarmMinutes.Text = auto.RewarmMinutes.ToString(CultureInfo.InvariantCulture);
            _tierEnabled.IsChecked = auto.SsdTier.Enabled;
            _tierBudget.Text = auto.SsdTier.BudgetGib.ToString(CultureInfo.InvariantCulture);
            _relocateGib.Text = auto.MaxRelocateGibPerDay.ToString(CultureInfo.InvariantCulture);
            _startStream.IsChecked = auto.StartStreamEnabled;
            ShowRelocateHours(auto.RelocateHours);
            _advCache.Text = TrayViewModel.CacheMib(_tray.Settings).ToString(CultureInfo.InvariantCulture);
            _advStartupGames.Text = auto.StartupWarmGames.ToString(CultureInfo.InvariantCulture);
            _advStartupMax.Text = auto.StartupWarmMaxMib.ToString(CultureInfo.InvariantCulture);
            _advRewarmExit.Text = auto.RewarmAfterExitMin.ToString(CultureInfo.InvariantCulture);
            _advMaxRelocations.Text = auto.MaxRelocationsPerDay.ToString(CultureInfo.InvariantCulture);
            _advIdle.Text = ((int)auto.IdleWindow.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            _advStartCap.Text = auto.StartStreamCapSeconds.ToString(CultureInfo.InvariantCulture);
            _shownStartStream = _startStream.IsChecked == true;
            _shownHours = HoursKey();
            _shownAdv = [_advCache.Text, _advStartupGames.Text, _advStartupMax.Text, _advRewarmExit.Text, _advMaxRelocations.Text, _advIdle.Text, _advStartCap.Text];
            _zoneLimit.Text = _tray.Settings?.MaxZoneAuto == true ? "auto" : (_tray.Settings?.MaxZoneMib ?? SettingsStore.DefaultMaxZoneMib).ToString(CultureInfo.InvariantCulture);
            _watcher.SelectedIndex = Math.Max(0, Array.IndexOf(WatcherValues, _tray.Settings?.Watcher));
            _language.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, _tray.Settings?.Language));
            _notifications.IsChecked = _tray.NotificationsEnabled;
            UpdateOptions update = UpdateOptions.From(_tray.Settings); // 설정을 못 읽었으면 실제 동작과 같은 안전한 값 (알림만)
            _updateMode.SelectedIndex = Math.Max(0, Array.IndexOf(UpdateModes, update.Mode));
            _updatePrerelease.IsChecked = update.IncludePrerelease;
            RefreshUpdateInfo();
            UpdateStatus();
            RefreshLayoutsInBackground();
            return r;
        }
        finally
        {
            _busyBar.IsVisible = false;
            ShowFooter();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// 배치 판정 (디스크 조회) 을 창을 막지 않고 채웁니다: 판정이 하나 올 때마다 목록과 상태 탭을 다시 그립니다. 이미 도는 중이면 그 조회가 끝난 뒤 한 번 더 돕니다.
    private void RefreshLayoutsInBackground()
    {
        Volatile.Write(ref _layoutAgain, 1);
        if (Interlocked.CompareExchange(ref _layoutBusy, 1, 0) != 0)
        {
            return; // 도는 쪽이 요청 표시를 보고 다시 돎
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _layoutAgain, 0) == 1)
                {
                    _manage.RefreshLayouts(() => Dispatcher.UIThread.Post(UpdateStatus));
                }
            }
            catch (Exception e)
            {
                // 창 맨 위 경계: 배치를 못 읽어도 앱을 죽이지 않음
                App.Log.Error("layout reload failed", e);
            }
            finally
            {
                Interlocked.Exchange(ref _layoutBusy, 0);
                Dispatcher.UIThread.Post(UpdateStatus);
                if (Volatile.Read(ref _layoutAgain) == 1)
                {
                    RefreshLayoutsInBackground(); // 끝나는 순간에 온 요청
                }
            }
        });
    }

    private void ShowDrives(IReadOnlyList<DriveRow> rows)
    {
        _loading = true;
        _loaded = true;
        _drives.ItemsSource = new object[] { L.T("Drives_All", _tray.Games.Count) }.Concat(rows).ToList();
        _drives.SelectedIndex = Math.Max(0, rows.ToList().FindIndex(r => r.Letter == _letter) + 1);
        _loading = false;
        ShowRows();
    }

    private static string Signature(IEnumerable<GameRow> rows) => string.Join("|", rows.Select(r => r.Stage + "/" + r.State + "/" + r.PrewarmState + "/" + r.Reason + "/" + r.VerdictRaw + "/" + r.Policy));

    private void ShowRows()
    {
        IReadOnlyList<GameRow> rows = _manage.GameRows(_letter);
        _stages = Signature(rows);
        _games.ItemsSource = rows;
        ShowEmpty(rows.Count);
        RenderStatus(rows);
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
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Malgun Gothic"), FontSize = Look.Caption + 1 };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        return box;
    }

    // 게임 표의 열. 앞 다섯 개 (이름·상태·예열 상태·보류 이유·안티치트) 가 기본 열이고 Advanced 열은 "고급 열 보기" 를 켠 때만 보입니다.
    // 이름·exe·원점·예열 설정·단계는 안 내용만큼 (최대 Max) 이고 헤더와 모든 행이 SharedSizeGroup 으로 폭을 함께 정합니다. 짧은 열은 고정 폭, 보류 이유는 남는 폭.
    private sealed record Col(string? Group, double Fixed, double Max, int Lines, bool Advanced = false, double Min = 0);

    private static readonly Col[] GameCols =
    [
        new("name", 0, 170, 2, false, 150), new(null, 130, 0, 2), new(null, 160, 0, 3), new(null, 0, 360, 3), new(null, 72, 0, 1),
        new("exe", 0, 120, 1, true, 90), new("origin", 0, 130, 3, true, 100), new(null, 44, 0, 1, true), new(null, 64, 0, 1, true),
        new(null, 84, 0, 2, true), new("prewarm", 0, 110, 3, true, 90), new(null, 64, 0, 1, true), new("stage", 0, 220, 3, true, 120), new(null, 0, 240, 2, true),
    ];

    private static readonly Thickness RowPadding = new(Look.S8, Look.S8);

    private static Grid Cells(IReadOnlyList<string> text, bool header = false, IReadOnlyList<string?>? tips = null, IReadOnlyList<StatusTone?>? tones = null, bool advanced = false, string policy = "")
    {
        var g = new Grid { ColumnSpacing = Look.S8 };
        foreach (Col c in GameCols.Where(c => advanced || !c.Advanced))
        {
            g.ColumnDefinitions.Add(c.Group is not null ? new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = c.Group, MinWidth = c.Min }
                : c.Fixed > 0 ? new ColumnDefinition(c.Fixed, GridUnitType.Pixel) : new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 120 });
        }

        int at = 0;
        for (int i = 0; i < text.Count; i++)
        {
            Col c = GameCols[i];
            if (c.Advanced && !advanced)
            {
                continue;
            }

            Control cell;
            if (!header && tones is not null && i < tones.Count && tones[i] is { } tone)
            {
                cell = Look.Pill(tone, text[i]); // 상태 배지: 색 + 점 + 글자 (색만으로 구분하지 않음)
            }
            else
            {
                TextBlock t = header ? Look.Text(text[i], Look.Caption, FontWeight.SemiBold, "Aster.Muted") : Look.Text(text[i], i == 0 ? Look.Body : Look.Caption, i == 0 ? FontWeight.SemiBold : FontWeight.Normal);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.TextWrapping = c.Lines > 1 && !header ? TextWrapping.Wrap : TextWrapping.NoWrap;
                t.MaxLines = header ? 1 : c.Lines;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                if (c.Max > 0)
                {
                    t.MaxWidth = c.Max;
                }

                cell = i == 0 && policy.Length > 0 ? new StackPanel { Spacing = Look.S4, Children = { t, Look.Text(policy, Look.Caption, color: Look.Key(StatusTone.Warn) + ".Fg") } } : t;
            }

            if (!header)
            {
                ToolTip.SetTip(cell, tips?[i] ?? text[i]);
            }

            cell.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cell, at++);
            g.Children.Add(cell);
        }

        return g;
    }
}
