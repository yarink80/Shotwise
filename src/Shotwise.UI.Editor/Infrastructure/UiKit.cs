using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Shotwise design-token keys used by the editor (always applied with SetResourceReference = DynamicResource).</summary>
internal static class Tok
{
    public const string Surface0 = "Shotwise.Surface0Brush", Surface1 = "Shotwise.Surface1Brush", Surface2 = "Shotwise.Surface2Brush", Surface3 = "Shotwise.Surface3Brush";
    public const string Canvas = "Shotwise.CanvasBrush", BorderSubtle = "Shotwise.BorderSubtleBrush", BorderStrong = "Shotwise.BorderStrongBrush";
    public const string Text = "Shotwise.TextPrimaryBrush", Text2 = "Shotwise.TextSecondaryBrush", Text3 = "Shotwise.TextTertiaryBrush", TextDisabled = "Shotwise.TextDisabledBrush";
    public const string Accent = "Shotwise.AccentBrush", AccentText = "Shotwise.AccentTextBrush", AccentSubtle = "Shotwise.AccentSubtleBrush", OnAccent = "Shotwise.OnAccentBrush";
    public const string AccentGradient = "Shotwise.AccentGradientBrush";
    public const string Capture = "Shotwise.CaptureSolidBrush", Success = "Shotwise.SuccessBrush", Warning = "Shotwise.WarningBrush", Danger = "Shotwise.DangerBrush";
    public const string Hud = "Shotwise.HudBrush", HudText = "Shotwise.HudTextBrush", Scrim = "Shotwise.ScrimBrush";
    public const string ElevationCard = "Shotwise.Elevation.Card", ElevationFlyout = "Shotwise.Elevation.Flyout", ElevationRaised = "Shotwise.Elevation.Raised";
    public const string FontFamily = "Shotwise.FontFamily";
}

/// <summary>Small helpers that build the (code-defined) editor UI from the Shotwise design system components (UI.Common, D1).</summary>
internal static class UiKit
{
    /// <summary>Set by the host: opens the colour dialog (IDialogService.ShowColorPicker).</summary>
    public static Func<Argb, bool, string?, Task<Argb?>>? ColorPicker { get; set; }

    /// <summary>
    /// Root panel for a code-built tree. The design-system templates bind <c>{Binding (sw:SwUi.Icon), RelativeSource=TemplatedParent}</c>;
    /// WPF resolves that prefixed path through the XAML namespace context inherited from a XAML-parsed ancestor, so a purely
    /// code-built window loses every attached icon (PathError). Parsing the (empty) root from XAML with the <c>sw</c> prefix
    /// gives the whole subtree that context.
    /// </summary>
    public static T XamlRoot<T>() where T : Panel
        => (T)System.Windows.Markup.XamlReader.Parse($"<{typeof(T).Name} xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:sw='clr-namespace:Shotwise.UI.Common.Controls;assembly=Shotwise.UI.Common' />");

    public static T Themed<T>(this T el, DependencyProperty dp, string resourceKey) where T : FrameworkElement
    {
        el.SetResourceReference(dp, resourceKey);
        return el;
    }

    public static T Styled<T>(this T el, string styleKey) where T : FrameworkElement
    {
        el.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return el;
    }

    /// <summary>Icon source for a key; "key@deg" returns the glyph rotated by deg (chevrons used as arrows).</summary>
    public static object IconSource(string key)
    {
        var at = key.IndexOf('@');
        if (at < 0) return key;
        var baseKey = key[..at];
        var angle = double.Parse(key[(at + 1)..], CultureInfo.InvariantCulture);
        if (Application.Current?.TryFindResource(baseKey) is not Geometry g) return baseKey;
        var group = new GeometryGroup { Transform = new RotateTransform(angle, SwIcon.DesignSize / 2, SwIcon.DesignSize / 2) };
        group.Children.Add(g);
        group.Freeze();
        return group;
    }

    /// <summary>Glyph element (Regular/Filled geometry follows the inherited foreground; Color icons keep their colours).</summary>
    public static SwIcon Glyph(string key, double size = 16)
        => new() { Source = IconSource(key), Size = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };

    /// <summary>32 px round icon-only button (Shotwise.IconButton) with tooltip + automation name.</summary>
    public static Button IconButton(string glyph, string tooltip, Action onClick, double size = 16)
    {
        var b = new Button { ToolTip = tooltip }.Styled("Shotwise.IconButton");
        SwUi.SetIcon(b, IconSource(glyph));
        SwUi.SetIconSize(b, size + 1);
        AutomationProperties.SetName(b, StripShortcut(tooltip));
        b.Click += (_, _) => onClick();
        return b;
    }

    private static string StripShortcut(string tip)
    {
        var i = tip.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? tip[..i] : tip;
    }

    /// <summary>Secondary (outlined) text button — the default for panel actions.</summary>
    public static Button TextButton(string text, Action onClick, string? tooltip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 6) }.Styled("Shotwise.SecondaryButton");
        if (tooltip is not null) b.ToolTip = tooltip;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Secondary button with a leading icon.</summary>
    public static Button GlyphTextButton(string glyph, string text, Action onClick, string? tooltip = null)
    {
        var b = TextButton(text, onClick, tooltip);
        SwUi.SetIcon(b, IconSource(glyph));
        return b;
    }

    /// <summary>Accent (primary) button — one per region.</summary>
    public static Button PrimaryButton(string text, string? glyph, Action onClick, string? tooltip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 6, 6) }.Styled("Shotwise.PrimaryButton");
        if (glyph is not null) SwUi.SetIcon(b, IconSource(glyph));
        if (tooltip is not null) b.ToolTip = tooltip;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Borderless (ghost) button with optional icon.</summary>
    public static Button GhostButton(string text, string? glyph, Action onClick, string? tooltip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0) }.Styled("Shotwise.GhostButton");
        if (glyph is not null) SwUi.SetIcon(b, IconSource(glyph));
        if (tooltip is not null) b.ToolTip = tooltip;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Ghost toggle button (checked = accent pill), e.g. Effects | Properties, Recent.</summary>
    public static ToggleButton GhostToggle(string text, string? glyph, string? tooltip = null)
    {
        var t = new ToggleButton { Content = text }.Styled("Shotwise.GhostToggleButton");
        if (glyph is not null) SwUi.SetIcon(t, IconSource(glyph));
        if (tooltip is not null) t.ToolTip = tooltip;
        AutomationProperties.SetName(t, text);
        return t;
    }

    public static TextBlock Label(string text, bool secondary = true)
    {
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = secondary ? 12 : 13 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, secondary ? Tok.Text2 : Tok.Text);
        return tb;
    }

    /// <summary>Hint/caption text (wraps, tertiary colour).</summary>
    public static TextBlock Hint(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        return tb;
    }

    /// <summary>Sub-section caption inside a panel ("OUTLINE", "ENDS"): 11 px semibold overline.</summary>
    public static TextBlock Header(string text)
    {
        var tb = new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), Margin = new Thickness(0, 16, 0, 6) }.Styled("Shotwise.Text.Overline");
        return tb;
    }

    /// <summary>Label | control row (PropertyRow); a <see cref="SliderRow"/> is returned labelled (it has its own label column).</summary>
    public static FrameworkElement Row(string label, UIElement control, double labelWidth = 84)
    {
        if (control is SliderRow slider)
        {
            slider.Label = label;
            slider.LabelWidth = labelWidth;
            return slider;
        }
        return new PropertyRow { Label = label, LabelWidth = labelWidth, Content = control };
    }

    public static ComboBox Combo<T>(IEnumerable<(string Text, T Value)> items, T selected, Action<T> changed) where T : notnull
    {
        var list = items.ToList();
        var cb = new ComboBox { MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13 };
        foreach (var (text, value) in list) cb.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        SelectCombo(cb, selected);
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem { Tag: T v }) changed(v);
        };
        return cb;
    }

    public static void SelectCombo<T>(ComboBox cb, T value) where T : notnull
    {
        for (var i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboBoxItem { Tag: T v } && EqualityComparer<T>.Default.Equals(v, value)) { cb.SelectedIndex = i; return; }
        cb.SelectedIndex = -1;
    }

    /// <summary>On/off option as a ToggleSwitchRow (label left, switch right).</summary>
    public static ToggleSwitchRow Check(string text, bool value, Action<bool> changed)
    {
        var c = new ToggleSwitchRow { Content = text, IsChecked = value, Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(-8, 1, -4, 1), MinHeight = 34 };
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    /// <summary>Small toggle (formatting B/I/U/S, alignment) — ghost toggle look, or an icon toggle when <paramref name="isGlyph"/>.</summary>
    public static ToggleButton Toggle(string glyphOrText, string tooltip, bool value, Action<bool> changed, bool isGlyph = false)
    {
        ToggleButton t;
        if (isGlyph)
        {
            t = new ToggleButton().Styled("Shotwise.IconButton");
            SwUi.SetIcon(t, IconSource(glyphOrText));
        }
        else
        {
            t = new ToggleButton { Content = glyphOrText, MinWidth = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0) }.Styled("Shotwise.GhostToggleButton");
            switch (glyphOrText)
            {
                case "B": t.FontWeight = FontWeights.Bold; break;
                case "I": t.FontStyle = FontStyles.Italic; break;
                case "U": t.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }; break;
                case "S": t.Content = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }; break;
            }
        }
        t.ToolTip = tooltip;
        t.IsChecked = value;
        AutomationProperties.SetName(t, tooltip);
        t.Click += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    /// <summary>Thin horizontal divider (Shotwise.Divider).</summary>
    public static Border Divider(double vertical = 8) => new Border { Margin = new Thickness(0, vertical, 0, vertical) }.Styled("Shotwise.Divider");

    /// <summary>Vertical divider for bars.</summary>
    public static Border VDivider() => new Border().Styled("Shotwise.VDivider");

    /// <summary>Neutral pill chip with text.</summary>
    public static Border Chip(string text, bool accent = false)
        => new Border { Child = new TextBlock { Text = text } }.Styled(accent ? "Shotwise.AccentChip" : "Shotwise.Chip");

    public static string FormatSize(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        };

    public static string FormatDuration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    /// <summary>Short m:ss duration for badges.</summary>
    public static string FormatShortDuration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Transparency checkerboard (behind thumbnails of images with alpha) — the design system's swatch checker.</summary>
    public static Brush CheckerBrush(int cell = 6) => Shotwise.UI.Common.Controls.ColorPicker.CheckerBrush(cell);

    /// <summary>A Surface1 card with radius 10 and a soft shadow on a separate layer (text stays crisp).</summary>
    public static Card Card(UIElement content, Thickness? padding = null, int elevation = 1)
        => new() { Content = content, Padding = padding ?? new Thickness(16), Elevation = elevation };

    /// <summary>Executes <paramref name="action"/> inside a try/catch that reports failures through <paramref name="report"/>.</summary>
    public static async void Fire(Func<Task> action, Action<Exception> report)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { report(ex); }
    }
}

/// <summary>
/// Colour swatch for panels: the design-system <see cref="ColorSwatchButton"/> (big swatch + chevron, optional caption) whose
/// flyout offers theme colours, standard palette, My colours, opacity, hex and "More colours…" (full dialog, CONTRACT A28).
/// </summary>
internal sealed class ColorSwatch : ColorSwatchButton
{
    /// <summary>Set by the host: theme colours of the active style theme and the user's "My colours".</summary>
    public static Func<IReadOnlyList<Argb>>? ThemeColorsProvider { get; set; }
    public static Func<IReadOnlyList<Argb>>? MyColorsProvider { get; set; }

    public ColorSwatch(Argb initial, bool allowAlpha = true, string? title = null, string? caption = null, double swatchSize = 22) : base(initial, allowAlpha, title)
    {
        Label = caption;
        SwatchSize = swatchSize;
        HorizontalAlignment = caption is null ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        MoreColors = UiKit.ColorPicker;
        PreviewMouseLeftButtonDown += (_, _) => Refresh();
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) Refresh(); };
    }

    private void Refresh()
    {
        ThemeColors = ThemeColorsProvider?.Invoke() ?? Array.Empty<Argb>();
        MyColors = MyColorsProvider?.Invoke() ?? Array.Empty<Argb>();
        MoreColors ??= UiKit.ColorPicker;
    }
}

/// <summary>
/// Labelled slider + numeric box (design-system <see cref="SliderWithValue"/>); raises <see cref="Changed"/> with a merge key that
/// is stable during one drag / one keyboard session so a drag becomes a single undo step.
/// </summary>
internal sealed class SliderRow : SliderWithValue
{
    private string _mergeKey = Guid.NewGuid().ToString("N");
    private bool _silent;

    /// <param name="format">.NET-style format used before the redesign ("0", "0.#", "0'%'"): decimals and a quoted unit are derived from it.</param>
    public SliderRow(double min, double max, double value, string format = "0", double smallChange = 1)
    {
        Minimum = min;
        Maximum = max;
        SmallChange = smallChange;
        LargeChange = smallChange * 5;
        var (decimals, unit) = ParseFormat(format);
        Decimals = decimals;
        Unit = unit;
        _silent = true;
        try { base.Value = Math.Clamp(value, min, max); }
        finally { _silent = false; }
        PreviewMouseLeftButtonDown += (_, _) => _mergeKey = Guid.NewGuid().ToString("N");
        PreviewGotKeyboardFocus += (_, e) => { if (e.OldFocus is not DependencyObject d || !IsAncestorOf(d)) _mergeKey = Guid.NewGuid().ToString("N"); };
    }

    public event Action<double, string>? Changed;

    /// <summary>Sets the value without raising <see cref="Changed"/>.</summary>
    public new double Value
    {
        get => base.Value;
        set
        {
            _silent = true;
            try { base.Value = Math.Clamp(value, Minimum, Maximum); }
            finally { _silent = false; }
        }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (!_silent) Changed?.Invoke(newValue, _mergeKey);
    }

    private static (int Decimals, string? Unit) ParseFormat(string format)
    {
        string? unit = null;
        var q = format.IndexOf('\'');
        var core = format;
        if (q >= 0)
        {
            var end = format.IndexOf('\'', q + 1);
            unit = end > q ? format[(q + 1)..end] : null;
            core = format[..q];
        }
        var dot = core.IndexOf('.');
        var decimals = dot < 0 ? 0 : core[(dot + 1)..].Count(c => c is '0' or '#');
        return (decimals, unit);
    }
}
