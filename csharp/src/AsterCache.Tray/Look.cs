using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AsterCache.Tray;

/// 상태 색 하나의 글자·배경·점 색 (글자는 배경 위에서 4.5:1 이상, 점은 표면 위에서 3:1 이상).
internal sealed record ToneColors(Color Fg, Color Bg, Color Dot);

/// 밝은·어두운 테마 하나의 색 모음. 화면은 색을 직접 쓰지 않고 Look 의 리소스 키 (Aster.*) 로만 씁니다.
internal sealed record Palette(
    Color Bg, Color Surface, Color SurfaceAlt, Color Border, Color Text, Color Muted, Color Accent, Color OnAccent, Color AccentSoft,
    ToneColors Gray, ToneColors Warn, ToneColors Busy, ToneColors Ok, ToneColors Danger);

/// 화면 공통 모양: 색 (밝음·어두움), 글자 크기·간격·모서리 단계, 카드·구역 머리글·상태 배지·버튼·입력 스타일. 모든 창이 여기서만 가져다 씁니다.
internal static class Look
{
    // 글자 크기 단계: 제목 · 소제목 · 본문 · 보조
    public const double Title = 22;
    public const double Subtitle = 16;
    public const double Body = 14;
    public const double Caption = 12;

    // 간격 단계 (4 의 배수)
    public const double S4 = 4;
    public const double S8 = 8;
    public const double S12 = 12;
    public const double S16 = 16;
    public const double S24 = 24;

    // 모서리 반경
    public const double RadiusCard = 8;
    public const double RadiusControl = 6;
    public const double RadiusPill = 10;

    public const string Danger = "Aster.Danger";

    private static Color C(string hex) => Color.Parse(hex);

    public static readonly Palette Light = new(
        C("#F4F5F7"), C("#FFFFFF"), C("#EEF0F3"), C("#D5D9E0"), C("#1B1F27"), C("#576071"), C("#1F5FD6"), C("#FFFFFF"), C("#E1EAFB"),
        new ToneColors(C("#4A5160"), C("#E9ECF0"), C("#6B7280")),
        new ToneColors(C("#7A4B00"), C("#FFF0C7"), C("#B26B00")),
        new ToneColors(C("#1747A8"), C("#E0EAFD"), C("#2D6FE8")),
        new ToneColors(C("#14672B"), C("#E1F3E6"), C("#1A7F37")),
        new ToneColors(C("#A31919"), C("#FDE3E3"), C("#D32F2F")));

    public static readonly Palette Dark = new(
        C("#15171B"), C("#1F2228"), C("#2A2E36"), C("#3A3F4A"), C("#E8EAED"), C("#A6AEBD"), C("#6EA2FF"), C("#0B1630"), C("#24324F"),
        new ToneColors(C("#BAC0CC"), C("#2E323A"), C("#8B93A1")),
        new ToneColors(C("#F3C95E"), C("#3F3109"), C("#F0B429")),
        new ToneColors(C("#9BBEFF"), C("#1B2D52"), C("#5B97FF")),
        new ToneColors(C("#86E0A0"), C("#143822"), C("#3FB968")),
        new ToneColors(C("#FFA3A3"), C("#4A1C1C"), C("#F06565")));

    // 작업표시줄 아이콘 점: 밝은·어두운 작업표시줄 어디서나 보이는 중간 밝기
    private static readonly Dictionary<StatusTone, SolidColorBrush> IconTones = new()
    {
        [StatusTone.Gray] = new SolidColorBrush(Color.Parse("#8A8A8A")),
        [StatusTone.Warn] = new SolidColorBrush(Color.Parse("#E5A100")),
        [StatusTone.Busy] = new SolidColorBrush(Color.Parse("#2D7FF9")),
        [StatusTone.Ok] = new SolidColorBrush(Color.Parse("#2EA043")),
    };

    public static IBrush Brush(StatusTone tone) => IconTones[tone];

    /// 상태 색 모음의 리소스 키 머리 (뒤에 .Fg .Bg .Dot).
    public static string Key(StatusTone tone) => "Aster." + tone;

    /// Fluent 테마에 우리 강조색·바탕색을 넣어 기본 컨트롤 (버튼·선택·포커스) 이 같은 색 체계를 따르게 합니다.
    public static FluentTheme Theme() => new()
    {
        Palettes =
        {
            [ThemeVariant.Light] = new ColorPaletteResources { Accent = Light.Accent, RegionColor = Light.Bg, ErrorText = Light.Danger.Fg },
            [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Dark.Accent, RegionColor = Dark.Bg, ErrorText = Dark.Danger.Fg },
        },
    };

    /// 테마별 색 리소스 (Application.Resources 에 넣음). 밝기에 따라 ActualThemeVariant 가 바뀌면 연결된 속성이 따라 바뀝니다.
    public static void AddResources(IResourceDictionary resources)
    {
        resources.ThemeDictionaries[ThemeVariant.Light] = Dictionary(Light);
        resources.ThemeDictionaries[ThemeVariant.Dark] = Dictionary(Dark);
    }

    private static ResourceDictionary Dictionary(Palette p)
    {
        var d = new ResourceDictionary
        {
            ["Aster.Bg"] = new SolidColorBrush(p.Bg),
            ["Aster.Surface"] = new SolidColorBrush(p.Surface),
            ["Aster.SurfaceAlt"] = new SolidColorBrush(p.SurfaceAlt),
            ["Aster.Border"] = new SolidColorBrush(p.Border),
            ["Aster.Text"] = new SolidColorBrush(p.Text),
            ["Aster.Muted"] = new SolidColorBrush(p.Muted),
            ["Aster.Accent"] = new SolidColorBrush(p.Accent),
            ["Aster.AccentSoft"] = new SolidColorBrush(p.AccentSoft),
        };

        // Fluent 버튼의 상태별 색 (lightweight styling 키)을 우리 색으로: 보조 버튼은 카드 같은 차분한 면으로.
        d["ButtonBackground"] = new SolidColorBrush(p.SurfaceAlt);
        d["ButtonBackgroundPointerOver"] = new SolidColorBrush(p.Border);
        d["ButtonBackgroundPressed"] = new SolidColorBrush(p.Border);
        d["ButtonBorderBrush"] = new SolidColorBrush(p.Border);
        d["ButtonBorderBrushPointerOver"] = new SolidColorBrush(p.Muted);
        d["ButtonBorderBrushPressed"] = new SolidColorBrush(p.Muted);
        foreach ((string key, ToneColors t) in new[] { (Key(StatusTone.Gray), p.Gray), (Key(StatusTone.Warn), p.Warn), (Key(StatusTone.Busy), p.Busy), (Key(StatusTone.Ok), p.Ok), (Danger, p.Danger) })
        {
            d[key + ".Fg"] = new SolidColorBrush(t.Fg);
            d[key + ".Bg"] = new SolidColorBrush(t.Bg);
            d[key + ".Dot"] = new SolidColorBrush(t.Dot);
        }

        return d;
    }

    /// 앱 전체에 붙는 Style: 버튼·입력·탭·목록 항목의 여백과 모서리.
    public static Styles AppStyles() =>
    [
        new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(S16, 6)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(RadiusControl)), new Setter(Layoutable.MinHeightProperty, 34.0) },
        },
        new Style(x => x.OfType<TextBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(RadiusControl)), new Setter(Layoutable.MinHeightProperty, 34.0) },
        },
        new Style(x => x.OfType<ComboBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(RadiusControl)), new Setter(Layoutable.MinHeightProperty, 34.0) },
        },
        new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(RadiusControl)) },
        },
        new Style(x => x.OfType<TabItem>())
        {
            Setters = { new Setter(TemplatedControl.FontSizeProperty, Subtitle), new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold), new Setter(TemplatedControl.PaddingProperty, new Thickness(S12, S8)) },
        },
    ];

    /// 테마 리소스 (다크·라이트에 따라 바뀜) 를 속성에 연결합니다. 키가 없으면 속성은 기본값 그대로입니다.
    public static T Fill<T>(this T target, AvaloniaProperty property, string resourceKey) where T : Control
    {
        target.Bind(property, target.GetResourceObservable(resourceKey));
        return target;
    }

    /// 글: 크기 단계·굵기·색 (기본은 본문 글자색). 길면 줄바꿈.
    public static TextBlock Text(string text, double size = Body, FontWeight weight = FontWeight.Normal, string color = "Aster.Text", bool wrap = true) =>
        new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap }.Fill(TextBlock.ForegroundProperty, color);

    /// 보조 글 (설명·단위·힌트).
    public static TextBlock Muted(string text, double size = Caption) => Text(text, size, color: "Aster.Muted");

    /// 소제목 (구역 머리글).
    public static TextBlock Heading(string text) => Text(text, Subtitle, FontWeight.SemiBold);

    /// 카드: 표면 색 + 경계선 + 둥근 모서리. 구역 하나를 묶습니다.
    public static Border Card(Control child, Thickness? padding = null) =>
        new Border { Child = child, Padding = padding ?? new Thickness(S16), CornerRadius = new CornerRadius(RadiusCard), BorderThickness = new Thickness(1) }
            .Fill(Border.BackgroundProperty, "Aster.Surface").Fill(Border.BorderBrushProperty, "Aster.Border");

    /// 구역 카드: 소제목 + 설명 (선택) + 본문. 설정 탭 등이 씁니다.
    public static Border Section(string title, string? note, params Control[] body)
    {
        var stack = new StackPanel { Spacing = S12, Children = { Heading(title) } };
        if (!string.IsNullOrEmpty(note))
        {
            stack.Children.Add(Muted(note, Caption));
        }

        stack.Children.AddRange(body);
        return Card(stack);
    }

    /// 상태 점 (8 px).
    public static Ellipse Dot(StatusTone tone, string? key = null) =>
        new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center }.Fill(Shape.FillProperty, (key ?? Key(tone)) + ".Dot");

    /// 상태 배지 (알약): 상태 색 배경 + 색 점 + 글자. 글자는 배경 위에서 4.5:1 이상. key 를 주면 상태 4종 밖의 색 (Danger) 을 씁니다.
    public static Border Pill(StatusTone tone, string text, double size = Caption, string? key = null)
    {
        string k = key ?? Key(tone);
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }.Fill(TextBlock.ForegroundProperty, k + ".Fg");
        Grid.SetColumn(label, 1);
        var dot = Dot(tone, k);
        dot.Margin = new Thickness(0, 0, 6, 0);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(dot);
        row.Children.Add(label);
        return new Border { Child = row, Padding = new Thickness(S8, 3), CornerRadius = new CornerRadius(RadiusPill), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }
            .Fill(Border.BackgroundProperty, k + ".Bg");
    }

    /// 중립 배지 (종류 표시: SSD·HDD 등). 상태 색을 쓰지 않아 상태와 헷갈리지 않습니다.
    public static Border Badge(string text) =>
        new Border { Child = Muted(text, Caption), Padding = new Thickness(S8, 1), CornerRadius = new CornerRadius(RadiusPill), VerticalAlignment = VerticalAlignment.Center }
            .Fill(Border.BackgroundProperty, "Aster.SurfaceAlt");

    /// 안내 띠 (경고·오류): 상태 색 배경 + 글자색. content 안의 글은 Banner 글자색 (key + ".Fg") 을 쓰십시오.
    public static Border Banner(string key, Control content) =>
        new Border { Child = content, Padding = new Thickness(S16, S8), CornerRadius = new CornerRadius(RadiusControl), BorderThickness = new Thickness(1) }
            .Fill(Border.BackgroundProperty, key + ".Bg").Fill(Border.BorderBrushProperty, key + ".Dot");

    /// 통계 타일: 큰 숫자 + 단위, 이름, 짧은 설명.
    public static Border Stat(string value, string label, string note)
    {
        var stack = new StackPanel { Spacing = S4 };
        stack.Children.Add(Text(value, Title, FontWeight.SemiBold, wrap: false));
        stack.Children.Add(Text(label, Body, FontWeight.SemiBold));
        stack.Children.Add(Muted(note));
        return Card(stack, new Thickness(S12));
    }

    /// 버튼: 주 버튼 (강조색, 화면당 하나), 보조 버튼 (기본), 위험 버튼 (되돌릴 수 없는 일).
    public enum ButtonKind
    {
        Secondary,
        Primary,
        Danger,
    }

    public static Button Btn(string text, Action click, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new Button { Content = text };
        switch (kind)
        {
            case ButtonKind.Primary:
                b.Classes.Add("accent");
                break;
            case ButtonKind.Danger:
                b.Fill(TemplatedControl.ForegroundProperty, Danger + ".Fg").Fill(TemplatedControl.BorderBrushProperty, Danger + ".Dot");
                break;
        }

        b.Click += (_, _) => click();
        return b;
    }

    /// 설정 양식: 왼쪽 설명 + 오른쪽 입력 (줄마다 같은 열).
    public static Grid Form(params (string Label, Control Input)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), ColumnSpacing = S16, RowSpacing = S12 };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock label = Text(rows[i].Label, Body);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, i);
            Control input = rows[i].Input;
            input.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 1);
            grid.Children.Add(label);
            grid.Children.Add(input);
        }

        return grid;
    }

    /// 두 색의 명암비 (WCAG 상대 휘도). 본문 글자는 4.5 이상이어야 합니다.
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
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
