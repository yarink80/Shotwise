using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Inline colour picker (CONTRACT A28): a swatch button that opens a drop-down with theme colours, a standard palette,
/// "My colours", transparent, an opacity slider and a hex box, plus "More colours…" for the full colour dialog
/// (<see cref="MoreColors"/>, typically <c>IDialogService.ShowColorPicker</c>). Fast colour changes without a modal dialog.
/// </summary>
public class ColorPicker : Button
{
    /// <summary>Standard palette (our own selection): greys, then saturated hues, then light tints.</summary>
    public static IReadOnlyList<Argb> StandardColors { get; } = new[]
    {
        0xFF000000u, 0xFF404040u, 0xFF808080u, 0xFFBFBFBFu, 0xFFFFFFFFu, 0xFF7F1D1Du, 0xFF78350Fu, 0xFF14532Du,
        0xFFE11D48u, 0xFFF97316u, 0xFFFACC15u, 0xFF22C55Eu, 0xFF14B8A6u, 0xFF0EA5E9u, 0xFF2563EBu, 0xFF7C3AEDu,
        0xFFFDA4AFu, 0xFFFED7AAu, 0xFFFEF08Au, 0xFFBBF7D0u, 0xFF99F6E4u, 0xFFBAE6FDu, 0xFFBFDBFEu, 0xFFDDD6FEu,
    }.Select(v => new Argb(v)).ToArray();

    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), Width = 40, Height = 18 };
    private readonly Popup _popup;
    private readonly WrapPanel _theme = new() { Width = 8 * 26 };
    private readonly WrapPanel _mine = new() { Width = 8 * 26 };
    private readonly TextBlock _themeHeader, _mineHeader;
    private readonly TextBox _hex = new() { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 255, Width = 150, SmallChange = 1, LargeChange = 16, IsMoveToPointEnabled = true };
    private readonly StackPanel _opacityRow;
    private Argb _color;
    private bool _allowAlpha = true, _syncing;

    public ColorPicker() : this(Argb.Black) { }

    public ColorPicker(Argb initial, bool allowAlpha = true, string? title = null)
    {
        _color = initial;
        Title = title;
        var host = new Grid { Width = 40, Height = 18 };
        host.Children.Add(new Border { Background = CheckerBrush(4), CornerRadius = new CornerRadius(3) });
        host.Children.Add(_fill);
        Content = host;
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel { Margin = new Thickness(10) };
        _themeHeader = Header("Theme colours");
        stack.Children.Add(_themeHeader);
        stack.Children.Add(_theme);
        stack.Children.Add(Header("Standard colours"));
        var standard = new WrapPanel { Width = 8 * 26 };
        foreach (var c in StandardColors) standard.Children.Add(Swatch(c));
        stack.Children.Add(standard);
        _mineHeader = Header("My colours");
        stack.Children.Add(_mineHeader);
        stack.Children.Add(_mine);

        _opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _opacityRow.Children.Add(new TextBlock { Text = "Opacity", Width = 56, VerticalAlignment = VerticalAlignment.Center });
        _opacityRow.Children.Add(_opacity);
        AutomationProperties.SetName(_opacity, "Opacity");
        _opacity.ValueChanged += (_, e) => { if (!_syncing) Commit(Argb.FromArgb((byte)Math.Round(e.NewValue), _color.R, _color.G, _color.B), close: false); };
        stack.Children.Add(_opacityRow);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(_hex, "Hex colour");
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyHex(); e.Handled = true; } };
        hexRow.Children.Add(_hex);
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => ApplyHex();
        hexRow.Children.Add(apply);
        stack.Children.Add(hexRow);

        var buttons = new WrapPanel { Width = 8 * 26, Margin = new Thickness(0, 8, 0, 0) };
        var transparent = new Button { Content = "No colour", Margin = new Thickness(0, 0, 6, 0) };
        transparent.Click += (_, _) => Commit(Argb.Transparent, close: true);
        buttons.Children.Add(transparent);
        var more = new Button { Content = "More colours…" };
        more.Click += async (_, _) =>
        {
            _popup!.IsOpen = false;
            var dialog = MoreColors ?? DefaultMoreColors;
            if (dialog is null) return;
            if (await dialog(_color, _allowAlpha, Title) is { } picked) Commit(picked, close: true);
        };
        buttons.Children.Add(more);
        _eyedropperButton = new Button { Content = "Pick from screen", Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
        _eyedropperButton.Click += async (_, _) =>
        {
            _popup!.IsOpen = false;
            var pick = Eyedropper ?? DefaultEyedropper;
            if (pick is null) return;
            await Task.Delay(150); // let the drop-down disappear so it is not part of the screen snapshot
            if (await pick() is { } sampled) Commit(_allowAlpha && _color.A is > 0 and < 255 ? Argb.FromArgb(_color.A, sampled.R, sampled.G, sampled.B) : Argb.FromRgb(sampled.R, sampled.G, sampled.B), close: true);
        };
        buttons.Children.Add(_eyedropperButton);
        stack.Children.Add(buttons);
        _transparentButton = transparent;

        var border = new Border { Child = stack, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        _popupBorder = border;
        _popupShadow = new Border { CornerRadius = new CornerRadius(10) };
        var popupRoot = new Grid { Margin = new Thickness(12, 4, 12, 16) }; // room for the flyout shadow
        popupRoot.Children.Add(_popupShadow);
        popupRoot.Children.Add(border);
        _popup = new Popup { Child = popupRoot, PlacementTarget = this, Placement = PlacementMode.Bottom, HorizontalOffset = -12, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        ApplyPopupTheme();
        Click += (_, _) => Open();

        AllowAlpha = allowAlpha;
        Update();
    }

    private readonly Button _transparentButton;
    private readonly Button _eyedropperButton;
    private readonly Border _popupBorder;
    private readonly Border _popupShadow;
    private bool _syncingSelected;

    /// <summary>Brush of the current colour (read-only; used by restyled templates such as <see cref="ColorSwatchButton"/>).</summary>
    public static readonly DependencyPropertyKey SwatchBrushPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SwatchBrush), typeof(Brush), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Brushes.Black));
    public static readonly DependencyProperty SwatchBrushProperty = SwatchBrushPropertyKey.DependencyProperty;
    public Brush SwatchBrush => (Brush)GetValue(SwatchBrushProperty);

    /// <summary>Bindable mirror of <see cref="Color"/> (two-way by default) for MVVM use.</summary>
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(Argb), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Argb.Black, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) =>
        {
            var p = (ColorPicker)d;
            if (p._syncingSelected) return;
            p._color = (Argb)e.NewValue;
            p.Update();
        }));
    public Argb SelectedColor { get => (Argb)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }

    /// <summary>Uses the Shotwise tokens (Surface1 + flyout elevation) when they are loaded, else the WPF-UI brushes.</summary>
    private void ApplyPopupTheme()
    {
        var shotwise = TryFindResource("Shotwise.Surface1Brush") is not null;
        _popupBorder.SetResourceReference(Border.BackgroundProperty, shotwise ? "Shotwise.Surface1Brush" : "ApplicationBackgroundBrush");
        _popupBorder.SetResourceReference(Border.BorderBrushProperty, shotwise ? "Shotwise.BorderSubtleBrush" : "ControlElevationBorderBrush");
        _popupShadow.SetResourceReference(Border.BackgroundProperty, shotwise ? "Shotwise.Surface1Brush" : "ApplicationBackgroundBrush");
        if (shotwise) _popupShadow.SetResourceReference(EffectProperty, "Shotwise.Elevation.Flyout");
    }

    /// <summary>Raised after the user picked a colour (not when <see cref="Color"/> is set in code).</summary>
    public event Action<Argb>? ColorChanged;

    /// <summary>Opens the full colour dialog: (initial, allowAlpha, title) → picked colour or null.</summary>
    public Func<Argb, bool, string?, Task<Argb?>>? MoreColors { get; set; }

    /// <summary>Screen eyedropper for this picker (returns the sampled colour or null when cancelled); falls back to <see cref="DefaultEyedropper"/>.</summary>
    public Func<Task<Argb?>>? Eyedropper { get; set; }

    /// <summary>Application-wide eyedropper used by every picker without its own <see cref="Eyedropper"/> (registered by UI.Dialogs).</summary>
    public static Func<Task<Argb?>>? DefaultEyedropper { get; set; }

    /// <summary>Application-wide "More colours…" handler (initial, allowAlpha, title) for pickers without <see cref="MoreColors"/>.</summary>
    public static Func<Argb, bool, string?, Task<Argb?>>? DefaultMoreColors { get; set; }

    /// <summary>Theme colours shown first (e.g. the active style theme's colours).</summary>
    public IReadOnlyList<Argb> ThemeColors { get; set; } = Array.Empty<Argb>();

    /// <summary>User colours (Editor preference "My colours").</summary>
    public IReadOnlyList<Argb> MyColors { get; set; } = Array.Empty<Argb>();

    public string? Title { get; set; }

    public bool AllowAlpha
    {
        get => _allowAlpha;
        set { _allowAlpha = value; _opacityRow.Visibility = _transparentButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    public Argb Color
    {
        get => _color;
        set { _color = value; Update(); }
    }

    private void Open()
    {
        ApplyPopupTheme();
        Fill(_theme, _themeHeader, ThemeColors);
        Fill(_mine, _mineHeader, MyColors);
        _syncing = true;
        _eyedropperButton.Visibility = Eyedropper is not null || DefaultEyedropper is not null ? Visibility.Visible : Visibility.Collapsed;
        _opacity.Value = _color.A;
        _hex.Text = _color.A == 255 ? _color.ToRgbHex() : _color.ToHex();
        _syncing = false;
        _popup.IsOpen = true;
    }

    private void Fill(WrapPanel panel, TextBlock header, IReadOnlyList<Argb> colors)
    {
        panel.Children.Clear();
        foreach (var c in colors.Distinct().Take(16)) panel.Children.Add(Swatch(c));
        header.Visibility = panel.Visibility = colors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyHex()
    {
        if (TryParse(_hex.Text, out var c)) Commit(_allowAlpha ? c : Argb.FromArgb(255, c.R, c.G, c.B), close: true);
        else _hex.SelectAll();
    }

    private static bool TryParse(string text, out Argb color)
    {
        try { color = Argb.Parse(text.Trim().StartsWith('#') ? text.Trim() : "#" + text.Trim()); return true; }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { color = default; return false; }
    }

    private void Commit(Argb c, bool close)
    {
        _color = c;
        Update();
        if (close) _popup.IsOpen = false;
        ColorChanged?.Invoke(c);
    }

    private Button Swatch(Argb c)
    {
        var b = new Button
        {
            Width = 22, Height = 22, Margin = new Thickness(2), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
            Content = new Border { Background = c.ToBrush(), CornerRadius = new CornerRadius(3), Width = 18, Height = 18 },
            ToolTip = c.A == 255 ? c.ToRgbHex() : c.ToHex(),
        };
        AutomationProperties.SetName(b, "Colour " + c.ToHex());
        if (TryFindResource("Shotwise.SwatchCell") is Style cell) b.Style = cell;
        // Keep the current opacity when picking a palette colour (e.g. a 50 % highlighter stays translucent).
        b.Click += (_, _) => Commit(_allowAlpha && _color.A is > 0 and < 255 ? Argb.FromArgb(_color.A, c.R, c.G, c.B) : c, close: true);
        return b;
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(2, 6, 0, 2), FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private void Update()
    {
        _fill.Background = _color.ToBrush();
        SetValue(SwatchBrushPropertyKey, _fill.Background);
        _syncingSelected = true;
        try { SetCurrentValue(SelectedColorProperty, _color); }
        finally { _syncingSelected = false; }
        ToolTip = _color.A == 255 ? _color.ToRgbHex() : _color.ToHex();
        AutomationProperties.SetName(this, (Title ?? "Colour") + " " + _color.ToHex());
    }

    /// <summary>Checkerboard brush that shows transparency behind colour swatches.</summary>
    public static Brush CheckerBrush(double cell)
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2))));
        var dark = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, cell, cell))));
        g.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(cell, cell, cell, cell))));
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, cell * 2, cell * 2), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
