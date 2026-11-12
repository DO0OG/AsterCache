using AsterCache.Core.I18n;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AsterCache.Tray;

/// 게임 한 개의 설정창: 일시 중지, 서비스가 하는 일 (전체·예열만·재배치만·사용 안 함), 표시 이름. 저장은 한 번에 하고, 거부 사유는 창 안에 보입니다 (성공하면 닫힘).
internal sealed class GameOptionsWindow : Window
{
    private static readonly GameMode[] Modes = [GameMode.Inherit, GameMode.PrewarmOnly, GameMode.RelocateOnly, GameMode.Off];

    private bool _saving;

    /// 저장에 성공했으면 그 결과 (닫기·취소면 null).
    public ActionResult? Saved { get; private set; }

    public GameOptionsWindow(TrayViewModel model, GameSettings game)
    {
        Title = L.T("Game_OptionsTitle", model.DisplayName(game.Name));
        Icon = App.LoadIcon("aster.ico");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var paused = new CheckBox { Content = L.T("Game_Paused"), IsChecked = game.Paused };
        var modes = new StackPanel { Spacing = Look.S8 };
        var radios = new List<RadioButton>();
        foreach (GameMode mode in Modes)
        {
            (string name, string note) = ManageViewModel.ModeLabels(mode);
            var radio = new RadioButton { Content = name, GroupName = "mode", IsChecked = game.Mode == mode };
            radios.Add(radio);
            TextBlock hint = Look.Muted(note);
            hint.Margin = new Thickness(Look.S24 + Look.S4, 0, 0, 0);
            modes.Children.Add(new StackPanel { Children = { radio, hint } });
        }

        TextBox def = new() { Text = game.DisplayName ?? string.Empty };
        TextBox ko = new() { Text = game.DisplayNames?.GetValueOrDefault("ko") ?? string.Empty };
        TextBox en = new() { Text = game.DisplayNames?.GetValueOrDefault("en") ?? string.Empty };

        var result = Look.Text(string.Empty, Look.Body, color: Look.Danger + ".Fg");
        Border error = Look.Banner(Look.Danger, result);
        error.IsVisible = false;
        Button save = Look.Btn(L.T("Btn_Save"), () => { }, Look.ButtonKind.Primary);
        Button cancel = Look.Btn(L.T("Btn_Cancel"), Close);
        save.IsDefault = true;
        cancel.IsCancel = true;
        save.Click += async (_, _) =>
        {
            _saving = true; // 저장 중에는 닫지 못하게 해, 저장은 됐는데 목록이 갱신되지 않는 일을 막음
            save.IsEnabled = false;
            cancel.IsEnabled = false;
            GameMode mode = Modes[Math.Max(0, radios.FindIndex(r => r.IsChecked == true))];
            bool pause = paused.IsChecked == true;
            string d = def.Text ?? string.Empty, k = ko.Text ?? string.Empty, e = en.Text ?? string.Empty;
            ActionResult r;
            try
            {
                r = await Task.Run(() => model.SetGameOptions(game.Name, mode, pause, d, k, e)); // 파일 저장은 화면 스레드에서 하지 않음
            }
            catch (Exception ex)
            {
                // 창 맨 위 경계: 어떤 예외도 앱을 죽이지 않고 오류 문구로 보여주고 tray.log 에 남김
                App.Log.Error("game options failed", ex);
                r = new ActionResult(false, L.T("Msg_OpFailed", L.T("Op_GameOptions"), TrayViewModel.Brief(ex)));
            }

            _saving = false;
            result.Text = r.Message;
            error.IsVisible = !r.Ok;
            save.IsEnabled = true;
            cancel.IsEnabled = true;
            if (r.Ok)
            {
                Saved = r;
                Close();
            }
        };

        Closing += (_, ev) => ev.Cancel = _saving;

        var panel = new StackPanel { Spacing = Look.S12, Margin = new Thickness(Look.S24) };
        panel.Children.Add(Look.Text(L.T("Game_OptionsTitle", model.DisplayName(game.Name)), Look.Title, FontWeight.SemiBold));
        panel.Children.Add(paused);
        panel.Children.Add(Look.Text(L.T("Game_ModeLabel"), Look.Caption, FontWeight.SemiBold, "Aster.Muted"));
        panel.Children.Add(modes);
        panel.Children.Add(Rule());
        panel.Children.Add(Look.Muted(L.T("Game_NamesNote")));
        AddField(panel, L.T("Game_NameDefault"), def);
        AddField(panel, L.T("Game_NameKo"), ko);
        AddField(panel, L.T("Game_NameEn"), en);
        panel.Children.Add(error);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, Look.S8, 0, 0), Children = { cancel, save } });
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static Border Rule() => new Border { Height = 1, Margin = new Thickness(0, Look.S4) }.Fill(Border.BackgroundProperty, "Aster.Border");

    private static void AddField(StackPanel panel, string label, TextBox box) =>
        panel.Children.Add(new StackPanel { Spacing = Look.S4, Children = { Look.Text(label, Look.Caption, FontWeight.SemiBold, "Aster.Muted"), box } });
}
