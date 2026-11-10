using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 화면 오른쪽 아래에 6 초 떠 있는 작은 알림 (활성화하지 않음). 누르면 onClick 을 부르고 닫힙니다.
/// 왼쪽 색 띠 (상태 색) + 앱 이름 + 본문 (긴 이름은 두 줄까지, 더 길면 말줄임).
internal sealed class AutoToast : Window
{
    private const int ToastWidth = 360;
    private const int ToastHeight = 88;
    private const int Gap = 8;
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(6);

    public AutoToast(string text, int slot, StatusTone tone, Action onClick)
    {
        Width = ToastWidth;
        Height = ToastHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        var body = Look.Text(text, Look.Body, FontWeight.SemiBold);
        body.MaxLines = 2;
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        var stack = new StackPanel { Spacing = Look.S4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Look.S16, Look.S8), Children = { Look.Muted("AsterCache"), body } };
        var stripe = new Border { Width = 6 }.Fill(Border.BackgroundProperty, Look.Key(tone) + ".Dot");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(stack, 1);
        row.Children.Add(stripe);
        row.Children.Add(stack);
        this.Fill(BackgroundProperty, "Aster.Surface");
        Content = new Border { Child = row, BorderThickness = new Thickness(1), ClipToBounds = true }.Fill(Border.BorderBrushProperty, "Aster.Border");
        ToolTip.SetTip(this, text);
        PointerPressed += (_, _) =>
        {
            onClick();
            Close();
        };
        Opened += (_, _) =>
        {
            PixelRect area = (Screens.Primary ?? Screens.All[0]).WorkingArea;
            double scale = Screens.Primary?.Scaling ?? 1;
            int w = (int)(ToastWidth * scale);
            int h = (int)(ToastHeight * scale);
            Position = new PixelPoint(area.Right - w - Gap, area.Bottom - ((h + Gap) * (slot + 1)));
            var close = new DispatcherTimer { Interval = Visible };
            close.Tick += (_, _) =>
            {
                close.Stop();
                Close();
            };
            close.Start();
        };
    }
}
