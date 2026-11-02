using Avalonia.Controls;
using Avalonia.Layout;

namespace AsterCache.Tray;

/// 게임 등록 입력창. 입력값 검증은 TrayViewModel.AddGame (설정 검증) 이 하고, 거부 사유를 그대로 보여줍니다.
internal sealed class RegisterWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];

    public RegisterWindow(TrayViewModel model, Action onRegistered)
    {
        Title = "AsterCache 게임 등록";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 6 };
        AddField(panel, "name", "이름 (영문·숫자·. _ -)", string.Empty);
        AddField(panel, "exe", "실행 파일 이름 (쉼표로 구분)", string.Empty);
        AddField(panel, "origin", "게임 폴더 (예: D:\\Games\\MW4)", string.Empty);
        AddField(panel, "roots", "허용 루트 (세미콜론으로 구분, 예: D:\\Games)", string.Empty);
        AddField(panel, "label", "볼륨 라벨", string.Empty);
        AddField(panel, "triggers", "예열 트리거 프로그램 (선택, 쉼표로 구분, 예: Steam.exe)", string.Empty);
        AddField(panel, "trace", "트레이스 (.prefetch.json)", Path.Combine(model.PlansDir, "<이름>.prefetch.json"));
        AddField(panel, "plan", "계획 (.json)", Path.Combine(model.PlansDir, "<이름>.plan.json"));
        var result = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var ok = new Button { Content = "등록", HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            string name = Text("name");
            ActionResult r = model.AddGame(new GameInput(
                name, Split(Text("exe"), ','), Text("trace").Replace("<이름>", name), Text("plan").Replace("<이름>", name),
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
        panel.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = initial };
        _fields[key] = box;
        panel.Children.Add(box);
    }
}
