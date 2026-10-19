using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shotwise.Core.Imaging;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Large colour swatch button (DESIGN-SYSTEM §3, item 3.6): 40 px swatch (checkerboard behind translucent colours) with a dropdown chevron and an
/// optional caption (<see cref="Label"/>, e.g. "Color" / "Fill" / "Outline"). Clicking opens the <see cref="ColorPicker"/> flyout
/// (theme colours, my colours, standard grid, opacity, hex, eyedropper, More colours...). Bind <see cref="ColorPicker.SelectedColor"/>.
/// </summary>
public class ColorSwatchButton : ColorPicker
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColorSwatchButton),
        new FrameworkPropertyMetadata(null, (d, e) => { var b = (ColorSwatchButton)d; b.Title ??= e.NewValue as string; }));
    public static readonly DependencyProperty SwatchSizeProperty = DependencyProperty.Register(nameof(SwatchSize), typeof(double), typeof(ColorSwatchButton), new FrameworkPropertyMetadata(40.0));

    static ColorSwatchButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ColorSwatchButton), new FrameworkPropertyMetadata(typeof(ColorSwatchButton)));

    public ColorSwatchButton() : this(Argb.Black) { }

    public ColorSwatchButton(Argb initial, bool allowAlpha = true, string? title = null) : base(initial, allowAlpha, title)
    {
        ClearValue(HorizontalAlignmentProperty);
        ClearValue(PaddingProperty);
    }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double SwatchSize { get => (double)GetValue(SwatchSizeProperty); set => SetValue(SwatchSizeProperty, value); }

    /// <summary>Checkerboard used behind swatches (8 px cells).</summary>
    public static Brush Checker { get; } = CheckerBrush(4);
}

/// <summary>Where the drop shadow falls (cell position in <see cref="ShadowDirectionPicker"/>).</summary>
public enum ShadowDirection { None, TopLeft, Top, TopRight, Left, Around, Right, BottomLeft, Bottom, BottomRight }

/// <summary>
/// 3x3 shadow-direction grid + "None" (DESIGN-SYSTEM §3, item 3.7). Each cell shows a small tile with the shadow falling towards that cell;
/// the centre cell is an all-around glow. Bind <see cref="Direction"/>; convert with <see cref="ToAngle"/>/<see cref="FromAngle"/>
/// (angles follow <c>ShadowStyle.AngleDegrees</c>: 0 = right, 90 = down).
/// </summary>
[TemplatePart(Name = "PART_Grid", Type = typeof(UniformGrid))]
[TemplatePart(Name = "PART_None", Type = typeof(ToggleButton))]
public class ShadowDirectionPicker : Control
{
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(nameof(Direction), typeof(ShadowDirection), typeof(ShadowDirectionPicker),
        new FrameworkPropertyMetadata(ShadowDirection.BottomRight, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ShadowDirectionPicker)d).SyncCells()));

    private static readonly ShadowDirection[] GridOrder =
    {
        ShadowDirection.TopLeft, ShadowDirection.Top, ShadowDirection.TopRight,
        ShadowDirection.Left, ShadowDirection.Around, ShadowDirection.Right,
        ShadowDirection.BottomLeft, ShadowDirection.Bottom, ShadowDirection.BottomRight,
    };

    private readonly List<ToggleButton> _cells = new();
    private ToggleButton? _none;

    static ShadowDirectionPicker()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ShadowDirectionPicker), new FrameworkPropertyMetadata(typeof(ShadowDirectionPicker)));
        FocusableProperty.OverrideMetadata(typeof(ShadowDirectionPicker), new FrameworkPropertyMetadata(false));
    }

    public ShadowDirectionPicker() => AutomationProperties.SetName(this, "Shadow direction");

    public ShadowDirection Direction { get => (ShadowDirection)GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }

    /// <summary>Shadow angle for a direction (null for None/Around, which have no offset).</summary>
    public static double? ToAngle(ShadowDirection d) => d switch
    {
        ShadowDirection.Right => 0, ShadowDirection.BottomRight => 45, ShadowDirection.Bottom => 90, ShadowDirection.BottomLeft => 135,
        ShadowDirection.Left => 180, ShadowDirection.TopLeft => 225, ShadowDirection.Top => 270, ShadowDirection.TopRight => 315,
        _ => null,
    };

    /// <summary>Nearest direction for an angle (null = None).</summary>
    public static ShadowDirection FromAngle(double? degrees)
    {
        if (degrees is not { } a) return ShadowDirection.None;
        var n = (int)Math.Round(((a % 360) + 360) % 360 / 45.0) % 8;
        return new[] { ShadowDirection.Right, ShadowDirection.BottomRight, ShadowDirection.Bottom, ShadowDirection.BottomLeft, ShadowDirection.Left, ShadowDirection.TopLeft, ShadowDirection.Top, ShadowDirection.TopRight }[n];
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _cells.Clear();
        if (GetTemplateChild("PART_Grid") is UniformGrid grid)
        {
            grid.Children.Clear();
            foreach (var dir in GridOrder)
            {
                var cell = new ToggleButton { Tag = dir, Content = CellVisual(dir), ToolTip = Describe(dir) };
                cell.SetResourceReference(StyleProperty, "Shotwise.ShadowCell");
                AutomationProperties.SetName(cell, Describe(dir));
                cell.Click += (_, _) => SetCurrentValue(DirectionProperty, dir);
                grid.Children.Add(cell);
                _cells.Add(cell);
            }
        }
        if (_none is not null) _none.Click -= OnNone;
        _none = GetTemplateChild("PART_None") as ToggleButton;
        if (_none is not null) { _none.Click += OnNone; AutomationProperties.SetName(_none, "No shadow"); }
        SyncCells();
    }

    private void OnNone(object sender, RoutedEventArgs e) => SetCurrentValue(DirectionProperty, ShadowDirection.None);

    private static string Describe(ShadowDirection d) => d == ShadowDirection.Around ? "Shadow all around" : "Shadow " + d switch
    {
        ShadowDirection.TopLeft => "top left", ShadowDirection.TopRight => "top right", ShadowDirection.BottomLeft => "bottom left",
        ShadowDirection.BottomRight => "bottom right", _ => d.ToString().ToLowerInvariant(),
    };

    private void SyncCells()
    {
        foreach (var c in _cells) c.IsChecked = (ShadowDirection)c.Tag == Direction;
        if (_none is not null) _none.IsChecked = Direction == ShadowDirection.None;
    }

    private static UIElement CellVisual(ShadowDirection dir)
    {
        var (dx, dy) = dir switch
        {
            ShadowDirection.TopLeft => (-1, -1), ShadowDirection.Top => (0, -1), ShadowDirection.TopRight => (1, -1),
            ShadowDirection.Left => (-1, 0), ShadowDirection.Right => (1, 0),
            ShadowDirection.BottomLeft => (-1, 1), ShadowDirection.Bottom => (0, 1), ShadowDirection.BottomRight => (1, 1),
            _ => (0, 0),
        };
        var root = new Grid { Width = 14, Height = 14, IsHitTestVisible = false };
        var shadow = new Border { CornerRadius = new CornerRadius(4), Opacity = 0.45 };
        shadow.SetResourceReference(Border.BackgroundProperty, "Shotwise.TextTertiaryBrush");
        shadow.RenderTransform = dir == ShadowDirection.Around
            ? new ScaleTransform(1.3, 1.3, 7, 7)
            : new TranslateTransform(dx * 2.5, dy * 2.5);
        if (dir == ShadowDirection.Around) shadow.Opacity = 0.3;
        var tile = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        tile.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
        tile.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderStrongBrush");
        root.Children.Add(shadow);
        root.Children.Add(tile);
        return root;
    }
}

/// <summary>
/// ComboBox whose items and selection box render previews (DESIGN-SYSTEM §3, item 3.8): arrow caps, dash styles, line widths...
/// Set <see cref="PreviewTemplate"/> (or ItemTemplate) to one of the <c>Shotwise.PreviewTemplate.*</c> DataTemplates or your own.
/// Uses the WPF-UI ComboBox look.
/// </summary>
public class StylePreviewComboBox : ComboBox
{
    public static readonly DependencyProperty PreviewTemplateProperty = DependencyProperty.Register(nameof(PreviewTemplate), typeof(DataTemplate), typeof(StylePreviewComboBox),
        new FrameworkPropertyMetadata(null, (d, e) => ((StylePreviewComboBox)d).ItemTemplate = (DataTemplate?)e.NewValue));

    public StylePreviewComboBox()
    {
        SetResourceReference(StyleProperty, typeof(ComboBox));
        MinWidth = 72;
    }

    public DataTemplate? PreviewTemplate { get => (DataTemplate?)GetValue(PreviewTemplateProperty); set => SetValue(PreviewTemplateProperty, value); }
}
