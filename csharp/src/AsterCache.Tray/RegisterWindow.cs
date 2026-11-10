using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AsterCache.Tray;

/// 게임 등록 입력창. 입력값 검증은 TrayViewModel.AddGame (설정 검증) 이 하고, 거부 사유를 그대로 보여줍니다.
/// 칸은 위에서 아래로 "무엇 (이름·실행 파일)" → "어디 (폴더·허용 루트·볼륨 라벨)" → "고급 (트리거·트레이스·계획)", 버튼은 오른쪽 아래 [취소] [등록].
internal sealed class RegisterWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];

    /// prefill: 관리 창이 exe 파일에서 추론한 값 (모든 칸은 그대로 고칠 수 있음).
    public RegisterWindow(TrayViewModel model, Action onRegistered, GameInput? prefill = null)
    {
        Title = L.T("Reg_Title");
        Icon = App.LoadIcon("aster.ico");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        string placeholder = L.T("Reg_NamePlaceholder");
        var panel = new StackPanel { Spacing = Look.S12, Margin = new Thickness(Look.S24) };
        panel.Children.Add(Look.Text(L.T("Reg_Title"), Look.Title, FontWeight.SemiBold));
        AddField(panel, "name", L.T("Reg_Name"), prefill?.Name ?? string.Empty);
        AddField(panel, "exe", L.T("Reg_Exe"), string.Join(", ", prefill?.ExeNames ?? []));
        panel.Children.Add(Rule());
        AddField(panel, "origin", L.T("Reg_Origin"), prefill?.OriginRoot ?? string.Empty);
        AddField(panel, "roots", L.T("Reg_Roots"), string.Join("; ", prefill?.AllowedRoots ?? []));
        AddField(panel, "label", L.T("Reg_Label"), prefill?.VolumeLabel ?? string.Empty);
        panel.Children.Add(Rule());
        AddField(panel, "triggers", L.T("Reg_Triggers"), string.Empty);
        AddField(panel, "trace", L.T("Reg_Trace"), prefill?.Trace ?? Path.Combine(model.PlansDir, placeholder + ".prefetch.json"));
        AddField(panel, "plan", L.T("Reg_Plan"), prefill?.Plan ?? Path.Combine(model.PlansDir, placeholder + ".plan.json"));

        // 거부 사유는 빨간 띠로 (검증을 통과하면 닫히므로 이 띠는 실패 때만 보임)
        var result = Look.Text(string.Empty, Look.Body, color: Look.Danger + ".Fg");
        Border error = Look.Banner(Look.Danger, result);
        error.IsVisible = false;
        Button ok = Look.Btn(L.T("Btn_Register"), () => { }, Look.ButtonKind.Primary);
        Button cancel = Look.Btn(L.T("Btn_Cancel"), Close);
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            string name = Text("name");
            ActionResult r = model.AddGame(new GameInput(
                name, Split(Text("exe"), ','), Text("trace").Replace(placeholder, name), Text("plan").Replace(placeholder, name),
                Text("origin"), Split(Text("roots"), ';'), Text("label"), null, Split(Text("triggers"), ',')));
            result.Text = r.Message;
            error.IsVisible = !r.Ok;
            if (r.Ok)
            {
                onRegistered();
                Close();
            }
        };
        panel.Children.Add(error);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.S8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, Look.S8, 0, 0), Children = { cancel, ok } });
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private string Text(string key) => (_fields[key].Text ?? string.Empty).Trim();

    private static List<string> Split(string text, char sep) =>
        text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static Border Rule() => new Border { Height = 1, Margin = new Thickness(0, Look.S4) }.Fill(Border.BackgroundProperty, "Aster.Border");

    /// 설명 (보조 글) 은 칸 위에, 입력은 한 줄 가득.
    private void AddField(StackPanel panel, string key, string label, string initial)
    {
        var box = new TextBox { Text = initial };
        _fields[key] = box;
        panel.Children.Add(new StackPanel { Spacing = Look.S4, Children = { Look.Text(label, Look.Caption, FontWeight.SemiBold, "Aster.Muted"), box } });
    }
}
