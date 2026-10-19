using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Shotwise.UI.Common.Controls;

/// <summary>
/// Renders a Shotwise icon resource (docs/DESIGN-SYSTEM.md "Icons"): a <see cref="Geometry"/> (<c>Icon.X.Regular</c> /
/// <c>Icon.X.Filled</c>, drawn in a 24-unit box and filled with the inherited <see cref="Foreground"/>), an
/// <see cref="ImageSource"/> (<c>Icon.X.Color</c> DrawingImage or a bitmap) or a resource key string ("Icon.Undo.Regular").
/// </summary>
public class SwIcon : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(object), typeof(SwIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(SwIcon),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SwIcon),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Design box of the generated geometries (tools/icon-import normalises every glyph to 24 units).</summary>
    public const double DesignSize = 24;

    static SwIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(SwIcon), new FrameworkPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(SwIcon), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Geometry, ImageSource or resource key (string).</summary>
    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Rendered edge length in DIPs (16 small, 20 chrome, 24 tools).</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>True when <see cref="Source"/> resolves to something drawable.</summary>
    public bool HasIcon => Resolve() is not null;

    private object? Resolve() => Source switch
    {
        string key when key.Length > 0 => TryFindResource(key),
        Geometry or ImageSource => Source,
        _ => null,
    };

    protected override Size MeasureOverride(Size availableSize) => Resolve() is null ? new Size(0, 0) : new Size(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Size;
        switch (Resolve())
        {
            case Geometry g:
                dc.PushTransform(new ScaleTransform(size / DesignSize, size / DesignSize));
                dc.DrawGeometry(Foreground, null, g);
                dc.Pop();
                break;
            case ImageSource img:
                dc.DrawImage(img, new Rect(0, 0, size, size));
                break;
        }
    }
}

/// <summary>Attached properties used by the Shotwise button styles (<c>sw:SwUi.Icon</c>, <c>sw:SwUi.CornerRadius</c>, ...).</summary>
public static class SwUi
{
    /// <summary>Icon shown by Shotwise button styles (Geometry / ImageSource / resource key).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(object), typeof(SwUi), new FrameworkPropertyMetadata(null));
    public static object? GetIcon(DependencyObject d) => d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, object? value) => d.SetValue(IconProperty, value);

    /// <summary>Icon size override for Shotwise button styles.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached("IconSize", typeof(double), typeof(SwUi), new FrameworkPropertyMetadata(16.0));
    public static double GetIconSize(DependencyObject d) => (double)d.GetValue(IconSizeProperty);
    public static void SetIconSize(DependencyObject d, double value) => d.SetValue(IconSizeProperty, value);

    /// <summary>Corner radius override for Shotwise templates that honour it.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(SwUi), new FrameworkPropertyMetadata(new CornerRadius(6)));
    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    /// <summary>Keyboard shortcut text shown in tooltips / captions (e.g. "Ctrl+Z").</summary>
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.RegisterAttached("Shortcut", typeof(string), typeof(SwUi), new FrameworkPropertyMetadata(null));
    public static string? GetShortcut(DependencyObject d) => (string?)d.GetValue(ShortcutProperty);
    public static void SetShortcut(DependencyObject d, string? value) => d.SetValue(ShortcutProperty, value);
}
