using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Theming;

namespace Shotwise.UI.Common.Gallery;

/// <summary>
/// Living style gallery: every Shotwise component in one scrollable page (docs/DESIGN-SYSTEM.md). Requires the Shotwise resources in
/// the application (SharedResources.xaml or <see cref="StyleGallery.EnsureResources"/>). Sample artwork below is drawn in code.
/// </summary>
public partial class StyleGalleryView : UserControl
{
    public StyleGalleryView()
    {
        InitializeComponent();
        (ShotwiseTheme.Current == EffectiveTheme.Dark ? DarkThemeItem : LightThemeItem).IsChecked = true;

        ThemeCombo.ItemsSource = new[] { "Starter", "Bold", "Soft pastel" };
        ColorSwatch.Color = Argb.FromRgb(0xEE, 0x3B, 0x2B);
        ColorSwatch.ThemeColors = new[] { Argb.FromRgb(0xEE, 0x3B, 0x2B), Argb.FromRgb(0x2E, 0xB8, 0x5C), Argb.FromRgb(0x21, 0x60, 0xE8), Argb.FromRgb(0xF2, 0xB2, 0x00) };
        FillSwatch.Color = Argb.FromArgb(0x66, 0xFF, 0xD2, 0x57);

        var caps = new Geometry[]
        {
            Geometry.Parse("M0 5H30V7H0Z M30 0L42 6L30 12Z"),
            Geometry.Parse("M0 5H34V7H0Z M34 2A4 4 0 1 1 34 10A4 4 0 1 1 34 2Z"),
            Geometry.Parse("M0 5H40V7H0Z"),
        };
        StartCap.ItemsSource = new[] { caps[2], caps[1], caps[0] };
        StartCap.SelectedIndex = 0;
        EndCap.ItemsSource = caps;
        EndCap.SelectedIndex = 0;
        Dash.ItemsSource = new[] { new DoubleCollection { 1, 0 }, new DoubleCollection { 3, 1.5 }, new DoubleCollection { 1, 1 } };
        Dash.SelectedIndex = 0;

        var hues = new[] { Color.FromRgb(0xEE, 0x3B, 0x2B), Color.FromRgb(0x2E, 0xB8, 0x5C), Color.FromRgb(0x21, 0x8C, 0xF0), Color.FromRgb(0xF2, 0xB2, 0x00) };
        var tiles = new List<QuickStyleTile>();
        for (var row = 0; row < 3; row++)
            foreach (var hue in hues)
                tiles.Add(new QuickStyleTile { Thumbnail = ArrowThumb(hue, row), IsFavorite = tiles.Count is 0 or 6, Title = $"Arrow style {tiles.Count + 1}" });
        QuickStyles.ItemsSource = tiles;

        Tray.ItemsSource = new[]
        {
            new TrayItem { Thumbnail = ScreenThumb(Color.FromRgb(0x5B, 0x4C, 0xF5), false), Title = "Capture 1" },
            new TrayItem { Thumbnail = ScreenThumb(Color.FromRgb(0x0C, 0x8A, 0x7D), false), Title = "Capture 2", IsPinned = true },
            new TrayItem { Thumbnail = ScreenThumb(Color.FromRgb(0x1B, 0x1C, 0x22), true), Title = "Recording", Duration = "0:42" },
            new TrayItem { Thumbnail = ScreenThumb(Color.FromRgb(0xE8, 0x59, 0x0C), false), Title = "Capture 4", IsModified = true },
        };

        foreach (var key in IconKeys(".Color").Where(k => k != "Icon.Brand.Color"))
            ColorIcons.Children.Add(IconCell(key, 28, colored: true));
        foreach (var key in IconKeys(".Regular"))
            RegularIcons.Children.Add(IconCell(key, 20, colored: false));
        BuildTokens();
        ShotwiseTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => ShotwiseTheme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => BuildTokens();

    private void OnLightChecked(object sender, RoutedEventArgs e) { if (IsLoaded) StyleGallery.ApplyTheme(EffectiveTheme.Light); }
    private void OnDarkChecked(object sender, RoutedEventArgs e) { if (IsLoaded) StyleGallery.ApplyTheme(EffectiveTheme.Dark); }

    private static IEnumerable<string> IconKeys(string suffix)
    {
        var icons = new ResourceDictionary { Source = new Uri("pack://application:,,,/Shotwise.UI.Common;component/Themes/Icons.xaml", UriKind.Absolute) };
        var keys = icons.Keys.OfType<string>().Concat(icons.MergedDictionaries.SelectMany(d => d.Keys.OfType<string>()));
        return keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal))
                   .OrderBy(k => k.StartsWith("Icon.Tool.", StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal);
    }

    private static FrameworkElement IconCell(string key, double size, bool colored)
    {
        var name = key["Icon.".Length..];
        name = name[..name.LastIndexOf('.')].Replace("Tool.", "");
        var icon = new SwIcon { Size = size, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(SwIcon.SourceProperty, key);
        if (!colored) icon.SetResourceReference(SwIcon.ForegroundProperty, "Shotwise.TextSecondaryBrush");
        var label = new TextBlock { Text = name, FontSize = 10, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextTertiaryBrush");
        var panel = new StackPanel { Width = colored ? 82 : 74, Margin = new Thickness(0, 6, 0, 6), ToolTip = key };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        return panel;
    }

    private void BuildTokens()
    {
        Tokens.Children.Clear();
        var dict = ShotwiseTheme.CreateTokens(ShotwiseTheme.Current);
        foreach (var key in dict.Keys.OfType<string>().Where(k => k.EndsWith("Brush", StringComparison.Ordinal)).OrderBy(Order).ThenBy(k => k, StringComparer.Ordinal))
        {
            var swatch = new Border { Height = 34, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            swatch.SetResourceReference(Border.BackgroundProperty, key);
            swatch.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
            var checker = new Border { Height = 34, CornerRadius = new CornerRadius(6), Background = ColorPicker.CheckerBrush(4) };
            var grid = new Grid();
            grid.Children.Add(checker);
            grid.Children.Add(swatch);
            var name = new TextBlock { Text = key["Shotwise.".Length..^"Brush".Length], FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextPrimaryBrush");
            var hex = new TextBlock { Text = dict[key] is SolidColorBrush s ? s.Color.ToString() : "gradient", FontSize = 10, Margin = new Thickness(1, 1, 0, 0) };
            hex.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextTertiaryBrush");
            var cell = new StackPanel { Width = 128, Margin = new Thickness(4, 4, 8, 8) };
            cell.Children.Add(grid);
            cell.Children.Add(name);
            cell.Children.Add(hex);
            Tokens.Children.Add(cell);
        }

        static int Order(string k) => k.Contains("Accent") ? 0 : k.Contains("Capture") ? 1 : k.Contains("Surface") || k.Contains("Canvas") ? 2 : k.Contains("Text") ? 3 : k.Contains("Border") ? 4 : 5;
    }

    /// <summary>Quick-style sample: an arrow in a hue (row 0 solid + shadow, row 1 thick, row 2 dashed with round end).</summary>
    private static ImageSource ArrowThumb(Color hue, int row)
    {
        var brush = new SolidColorBrush(hue);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 48, 48))));
        var thickness = row == 1 ? 6.5 : 4.5;
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat };
        if (row == 2) pen.DashStyle = new DashStyle(new[] { 1.2, 1.1 }, 0);
        Point from = new(40, 9), to = new(15, 34);
        var dir = new Vector(to.X - from.X, to.Y - from.Y); dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);
        var head = row == 2
            ? (Geometry)new EllipseGeometry(to, 5.5, 5.5)
            : new PathGeometry(new[] { new PathFigure(to + dir * 6, new PathSegment[] { new LineSegment(to - dir * 9 + normal * 9, true), new LineSegment(to - dir * 9 - normal * 9, true) }, true) });
        var shaftEnd = row == 2 ? to : to - dir * 6;
        if (row != 2)
        {
            var shadow = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
            var sp = new Pen(shadow, thickness) { StartLineCap = PenLineCap.Round };
            var off = new Vector(2, 2.5);
            g.Children.Add(new GeometryDrawing(null, sp, new LineGeometry(from + off, shaftEnd + off)));
            var sh = head.Clone(); sh.Transform = new TranslateTransform(off.X, off.Y);
            g.Children.Add(new GeometryDrawing(shadow, null, sh));
        }
        g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(from, shaftEnd)));
        g.Children.Add(new GeometryDrawing(brush, null, head));
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    /// <summary>Tray sample: a stylised window screenshot (or a dark video frame with a play glyph).</summary>
    private static ImageSource ScreenThumb(Color accent, bool video)
    {
        var g = new DrawingGroup();
        var bg = video ? Color.FromRgb(0x1B, 0x1C, 0x22) : Color.FromRgb(0xF4, 0xF5, 0xF9);
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, 96, 72))));
        if (video)
        {
            g.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(0x3C, 0x2F, 0xC7), Color.FromRgb(0xFF, 0x4D, 0x5E), 30), null, new RectangleGeometry(new Rect(8, 8, 80, 56), 4, 4)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, Geometry.Parse("M43 27L55 36L43 45Z")));
        }
        else
        {
            g.Children.Add(new GeometryDrawing(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xE0, 0xEA)), 1), new RectangleGeometry(new Rect(8, 8, 80, 58), 4, 4)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(accent), null, new RectangleGeometry(new Rect(8, 8, 80, 10), 4, 4)));
            var grey = new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xDA));
            g.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(14, 24, 40, 4), 2, 2)));
            g.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(14, 32, 56, 4), 2, 2)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)), null, new RectangleGeometry(new Rect(14, 42, 30, 18), 3, 3)));
            g.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(50, 44, 30, 4), 2, 2)));
            g.Children.Add(new GeometryDrawing(grey, null, new RectangleGeometry(new Rect(50, 52, 22, 4), 2, 2)));
        }
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }
}
