using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Infrastructure;

/// <summary>
/// Code-side control factory for the capture UI. Everything is built from the Shotwise design system (UI.Common tokens, icons and
/// component styles, resolved through resource references so the Light/Dark switch applies at runtime) plus the few capture-local
/// styles in <c>Themes/CaptureStyles.xaml</c>.
/// </summary>
internal static class Ui
{
    private static readonly Uri StylesUri = new("pack://application:,,,/Shotwise.UI.Capture;component/Themes/CaptureStyles.xaml", UriKind.Absolute);

    private static bool _primed;

    /// <summary>Merges the capture-local styles into the application resources once (idempotent; UI thread).</summary>
    public static void EnsureStyles()
    {
        var app = Application.Current;
        if (app is null) return;
        if (!_primed)
        {
            // Binding paths like (sw:SwUi.Icon) inside the shared templates only resolve once the xmlns "urn:shotwise-ui" mapping has been
            // used by the XAML parser in this process; windows built purely in code would otherwise get PathError bindings (no icons).
            _primed = true;
            // (parsing one element that uses the attached property from XAML registers it with the parser's mapper)
            try
            {
                var probe = (StackPanel)System.Windows.Markup.XamlReader.Parse(
                    "<StackPanel xmlns:sw=\"urn:shotwise-ui\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                    "<Button Style=\"{DynamicResource Shotwise.IconButton}\" sw:SwUi.Icon=\"Icon.Reset.Regular\"/>" +
                    "<ToggleButton Style=\"{DynamicResource Shotwise.IconButton}\" sw:SwUi.Icon=\"Icon.Reset.Regular\"/>" +
                    "<ToggleButton Style=\"{DynamicResource Shotwise.GhostToggleButton}\" sw:SwUi.Icon=\"Icon.Reset.Regular\"/>" +
                    "<Button Style=\"{DynamicResource Shotwise.GhostButton}\" sw:SwUi.Icon=\"Icon.Reset.Regular\"/>" +
                    "<Button Style=\"{DynamicResource Shotwise.ToolCommandButton}\" sw:SwUi.Icon=\"Icon.Reset.Regular\"/></StackPanel>");
                probe.Measure(new Size(100, 400));
            }
            catch (Exception) { /* best effort */ }
        }
        if (app.Resources.Contains("Capture.HudButton")) return;
        try { app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri }); }
        catch (Exception) { /* a missing style only degrades the look; never fail a capture over it */ }
    }

    public static object? Res(string key) => Application.Current?.TryFindResource(key);

    public static T WithStyle<T>(this T e, string styleKey) where T : FrameworkElement
    {
        EnsureStyles();
        e.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return e;
    }

    public static T Bg<T>(this T e, string brushKey) where T : Control { e.SetResourceReference(Control.BackgroundProperty, brushKey); return e; }

    /// <summary>Icon from the shared icon set by resource key (Icon.X.Regular / .Filled / .Color). Regular/Filled follow Foreground.</summary>
    public static SwIcon Icon(string key, double size = 16, string? brush = null)
    {
        var i = new SwIcon { Source = key, Size = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (brush is not null) i.SetResourceReference(SwIcon.ForegroundProperty, brush);
        return i;
    }

    public static TextBlock Label(string text, double size = 12, bool secondary = false, FontWeight? weight = null, bool wrap = false, string? brush = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Shotwise.FontFamily");
        if (weight is { } w) t.FontWeight = w;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush ?? (secondary ? "Shotwise.TextSecondaryBrush" : "Shotwise.TextPrimaryBrush"));
        return t;
    }

    /// <summary>Small section title ("Selection", "Presets"): 11 px semibold, secondary text.</summary>
    public static TextBlock Header(string text)
    {
        var t = Label(text, 11, secondary: true, weight: FontWeights.SemiBold);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextTertiaryBrush");
        return t;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Button IconButton(string iconKey, string tooltip, Action onClick, double size = 18, string style = "Shotwise.IconButton")
    {
        var b = new Button { ToolTip = tooltip };
        b.WithStyle(style);
        SwUi.SetIcon(b, iconKey);
        SwUi.SetIconSize(b, size);
        AutomationProperties(b, tooltip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Text button; <paramref name="accent"/> = primary (one per surface), otherwise secondary. <paramref name="ghost"/> = borderless.</summary>
    public static Button TextButton(string text, Action onClick, string? iconKey = null, bool accent = false, bool ghost = false)
    {
        var b = new Button { Content = text };
        b.WithStyle(accent ? "Shotwise.PrimaryButton" : ghost ? "Shotwise.GhostButton" : "Shotwise.SecondaryButton");
        if (iconKey is not null) SwUi.SetIcon(b, iconKey);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Pill-shaped coral action (Record).</summary>
    public static Button RecordButton(string text, Action onClick, string iconKey = "Icon.Record.Filled")
    {
        var b = new Button { Content = text };
        b.WithStyle("Capture.RecordButton");
        SwUi.SetIcon(b, iconKey);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Small value chip button (opens a flyout / menu).</summary>
    public static Button ChipButton(string text, Action onClick, string? tooltip = null, double maxWidth = 150)
    {
        var tb = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = maxWidth, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Content = tb, ToolTip = tooltip ?? text };
        b.WithStyle("Capture.ChipButton");
        AutomationProperties(b, tooltip ?? text);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static void AutomationProperties(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

    public static Card Card(UIElement child, Thickness? padding = null, int elevation = 0, double radius = 10)
        => new() { Content = child, Padding = padding ?? new Thickness(12), Elevation = elevation, CornerRadius = new CornerRadius(radius) };

    public static Border Separator()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
        b.SetResourceReference(Border.BackgroundProperty, "Shotwise.BorderSubtleBrush");
        return b;
    }

    public static Border VSeparator(double height = 22, string brush = "Shotwise.BorderSubtleBrush")
    {
        var b = new Border { Width = 1, Height = height, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, brush);
        return b;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Text)> items, T selected, Action<T> changed, double minWidth = 140)
    {
        var list = items.ToList();
        var c = new ComboBox { MinWidth = minWidth, MinHeight = 32, DisplayMemberPath = "Text", SelectedValuePath = "Value", ItemsSource = list.Select(i => new ComboItem<T>(i.Value, i.Text)).ToList() };
        c.SelectedValue = selected;
        c.SelectionChanged += (_, _) => { if (c.SelectedValue is T v) changed(v); };
        return c;
    }

    public sealed record ComboItem<T>(T Value, string Text);

    /// <summary>On/off row with a switch (design-system <c>ToggleSwitchRow</c>).</summary>
    public static ToggleSwitchRow Switch(string text, bool value, Action<bool> changed, string? iconKey = null, string? description = null)
    {
        var s = new ToggleSwitchRow { Content = text, IsChecked = value, Description = description, MinHeight = 38 };
        if (iconKey is not null) s.Icon = iconKey;
        s.Click += (_, _) => changed(s.IsChecked == true);
        return s;
    }

    public static Brush BrushOf(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    public static Brush DynamicBrush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static Color ColorOf(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Color c ? c : fallback;

    public static void OnEscape(UIElement e, Action action)
        => e.PreviewKeyDown += (_, a) => { if (a.Key == Key.Escape) { a.Handled = true; action(); } };

    /// <summary>Soft elevation shadow behind a floating surface (separate layer so text above stays crisp).</summary>
    public static Border ShadowLayer(double radius, string effectKey = "Shotwise.Elevation.Flyout")
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), IsHitTestVisible = false };
        b.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
        b.SetResourceReference(UIElement.EffectProperty, effectKey);
        return b;
    }

    /// <summary>Floating card surface (Surface1, border, radius) with a shadow layer behind it. Returns the outer grid.</summary>
    public static Grid FloatingCard(UIElement content, double radius = 12, Thickness? padding = null, string effectKey = "Shotwise.Elevation.Flyout")
    {
        var surface = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(6), Child = content };
        surface.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
        surface.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
        var g = new Grid();
        g.Children.Add(ShadowLayer(radius, effectKey));
        g.Children.Add(surface);
        return g;
    }
}
