using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AsterCache.Tray;

/// 게임 등록 입력창. 입력값 검증은 TrayViewModel.AddGame (설정 검증) 이 하고, 거부 사유를 그대로 보여줍니다.
internal sealed class RegisterWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];

    /// prefill: 관리 창이 exe 파일에서 추론한 값 (모든 칸은 그대로 고칠 수 있음).
    public RegisterWindow(TrayViewModel model, Action onRegistered, GameInput? prefill = null)
    {
        Title = L.T("Reg_Title");
        Icon = App.LoadIcon("aster.ico");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        string placeholder = L.T("Reg_NamePlaceholder");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 4 };
        AddField(panel, "name", L.T("Reg_Name"), prefill?.Name ?? string.Empty);
        AddField(panel, "exe", L.T("Reg_Exe"), string.Join(", ", prefill?.ExeNames ?? []));
        AddField(panel, "origin", L.T("Reg_Origin"), prefill?.OriginRoot ?? string.Empty);
        AddField(panel, "roots", L.T("Reg_Roots"), string.Join("; ", prefill?.AllowedRoots ?? []));
        AddField(panel, "label", L.T("Reg_Label"), prefill?.VolumeLabel ?? string.Empty);
        AddField(panel, "triggers", L.T("Reg_Triggers"), string.Empty);
        AddField(panel, "trace", L.T("Reg_Trace"), prefill?.Trace ?? Path.Combine(model.PlansDir, placeholder + ".prefetch.json"));
        AddField(panel, "plan", L.T("Reg_Plan"), prefill?.Plan ?? Path.Combine(model.PlansDir, placeholder + ".plan.json"));
        var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var ok = new Button { Content = L.T("Btn_Register"), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            string name = Text("name");
            ActionResult r = model.AddGame(new GameInput(
                name, Split(Text("exe"), ','), Text("trace").Replace(placeholder, name), Text("plan").Replace(placeholder, name),
                Text("origin"), Split(Text("roots"), ';'), Text("label"), null, Split(Text("triggers"), ',')));
            result.Text = r.Message;
            if (r.Ok)
            {
                onRegistered();
                Close();
            }
        };
        panel.Children.Add(result);
        panel.Children.Add(ok);
        Content = panel;
    }

    private string Text(string key) => (_fields[key].Text ?? string.Empty).Trim();

    private static List<string> Split(string text, char sep) =>
        text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void AddField(StackPanel panel, string key, string label, string initial)
    {
        panel.Children.Add(new TextBlock { Text = label, Classes = { "label" }, Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        var box = new TextBox { Text = initial };
        _fields[key] = box;
        panel.Children.Add(box);
    }
}
