using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace AsterCache.Tray;

/// 화면 공통 모양: 글꼴 크기 단계, 공용 Style, 상태 색 4종 (다크·라이트 어느 쪽에서도 보이는 중간 밝기), 테마 리소스 연결.
internal static class Look
{
    public const double Small = 12;
    public const double Title = 15;

    private static readonly Dictionary<StatusTone, SolidColorBrush> Tones = new()
    {
        [StatusTone.Gray] = new SolidColorBrush(Color.Parse("#8A8A8A")),
        [StatusTone.Warn] = new SolidColorBrush(Color.Parse("#E5A100")),
        [StatusTone.Busy] = new SolidColorBrush(Color.Parse("#2D7FF9")),
        [StatusTone.Ok] = new SolidColorBrush(Color.Parse("#2EA043")),
    };

    public static IBrush Brush(StatusTone tone) => Tones[tone];

    /// 앱 전체에 붙는 Style: 버튼·목록 항목 모서리와 여백, 제목(title)·보조 글(label) 클래스.
    public static Styles AppStyles() =>
    [
        new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 5)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)) },
        },
        new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)) },
        },
        new Style(x => x.OfType<TextBlock>().Class("title"))
        {
            Setters = { new Setter(TextBlock.FontSizeProperty, Title), new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold), new Setter(Avalonia.Layout.Layoutable.MarginProperty, new Thickness(0, 8, 0, 0)) },
        },
        new Style(x => x.OfType<TextBlock>().Class("label"))
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.8) },
        },
    ];

    /// 테마 리소스(다크·라이트에 따라 바뀜)를 속성에 연결합니다. 키가 없으면 속성은 기본값 그대로입니다.
    public static void Themed(Control target, AvaloniaProperty property, string resourceKey) =>
        target.Bind(property, target.GetResourceObservable(resourceKey));

    /// 색 점 + 글자 (상태 칩). 글이 길면 점 옆에서 줄바꿈됩니다.
    public static Grid Chip(StatusTone tone, TextBlock text)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Brush(tone), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        var chip = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        chip.Children.Add(dot);
        chip.Children.Add(text);
        return chip;
    }
}

/// 작업표시줄(알림 영역) 아이콘: aster-tray.png 위에 오른쪽 아래 상태 색 점을 합성해 상태마다 한 번만 만들어 둡니다 (HICON 을 매번 새로 만들지 않게).
internal sealed class StatusIcons(IImage baseImage)
{
    private const int Px = 32;
    private readonly Dictionary<StatusTone, RenderTargetBitmap> _bitmaps = [];
    private readonly Dictionary<StatusTone, WindowIcon> _icons = [];

    public RenderTargetBitmap Bitmap(StatusTone tone)
    {
        if (!_bitmaps.TryGetValue(tone, out RenderTargetBitmap? bmp))
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = Look.Brush(tone), Stroke = Brushes.White, StrokeThickness = 2 };
            Canvas.SetLeft(dot, Px - 16);
            Canvas.SetTop(dot, Px - 16);
            var canvas = new Canvas { Width = Px, Height = Px, Children = { new Image { Source = baseImage, Width = Px, Height = Px }, dot } };
            canvas.Measure(new Size(Px, Px));
            canvas.Arrange(new Rect(0, 0, Px, Px));
            bmp = new RenderTargetBitmap(new PixelSize(Px, Px), new Vector(96, 96));
            bmp.Render(canvas);
            _bitmaps[tone] = bmp;
        }

        return bmp;
    }

    public WindowIcon Icon(StatusTone tone)
    {
        if (!_icons.TryGetValue(tone, out WindowIcon? icon))
        {
            icon = new WindowIcon(Bitmap(tone));
            _icons[tone] = icon;
        }

        return icon;
    }

    /// --smoke --screenshot 용: 상태 4종 아이콘을 밝은·어두운 바탕에서 실제 16 px 로 줄인 것과 그 확대 (최근접 보간) 를 PNG 로 저장합니다.
    public void SaveSheet(string path)
    {
        var rows = new StackPanel { Spacing = 8 };
        foreach (string back in new[] { "#F3F3F3", "#202020" })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Background = new SolidColorBrush(Color.Parse(back)) };
            foreach (StatusTone tone in Enum.GetValues<StatusTone>())
            {
                var small = new Image { Source = Bitmap(tone), Width = 16, Height = 16 };
                RenderOptions.SetBitmapInterpolationMode(small, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                small.Measure(new Size(16, 16));
                small.Arrange(new Rect(0, 0, 16, 16));
                var actual = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96)); // 실제 16 px 로 래스터라이즈한 결과
                actual.Render(small);
                var zoom = new Image { Source = actual, Width = 96, Height = 96 };
                RenderOptions.SetBitmapInterpolationMode(zoom, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                row.Children.Add(zoom);
                row.Children.Add(new Image { Source = actual, Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            }

            rows.Children.Add(row);
        }

        rows.Measure(Size.Infinity);
        rows.Arrange(new Rect(rows.DesiredSize));
        using var sheet = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(rows.DesiredSize.Width), (int)Math.Ceiling(rows.DesiredSize.Height)), new Vector(96, 96));
        sheet.Render(rows);
        sheet.Save(path);
    }
}
