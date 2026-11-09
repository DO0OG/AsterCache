using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AsterCache.Tray;

/// 화면 오른쪽 아래에 6 초 떠 있는 작은 알림 (활성화하지 않음). 누르면 onClick 을 부르고 닫힙니다.
internal sealed class AutoToast : Window
{
    private const int ToastWidth = 340;
    private const int ToastHeight = 72;
    private const int Gap = 8;
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(6);

    public AutoToast(string text, int slot, Action onClick)
    {
        Width = ToastWidth;
        Height = ToastHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var card = new Border { BorderBrush = Look.Brush(StatusTone.Busy), BorderThickness = new Thickness(1, 1, 1, 1), Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Child = body };
        Look.Themed(card, Border.BackgroundProperty, "SystemControlBackgroundAltHighBrush"); // 다크·라이트를 따르는 테마 색
        Look.Themed(body, TextBlock.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        Content = card;
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
