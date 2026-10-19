using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Media;
using Shotwise.Core.Capture;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Selection;

/// <summary>
/// Floating card shown next to the adjusted selection. All-in-One: accent size chip, then Image, Video, Scroll (direction variants
/// appear when the probe finds scrollable content), Panorama, Grab Text and Cancel - colour icons with captions. Video presets: size
/// chip, Record and Cancel. Created lazily (first time a selection is adjusted) so it never adds to the overlay's first frame.
/// </summary>
internal sealed class MiniToolbar : Grid
{
    private readonly TextBlock _size = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly Button _panoramic;
    private readonly StackPanel _scrollGroup = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _scrollV, _scrollH, _scrollBoth;

    public event Action<SelectionAction, ScrollDirection>? ActionChosen;
    public event Action? Cancelled;

    /// <param name="fullSet">true: All-in-One set; false: Record / Cancel only (video presets).</param>
    public MiniToolbar(bool fullSet = true)
    {
        Ui.EnsureStyles();
        Focusable = false;
        var surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), Child = _row };
        surface.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
        surface.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
        surface.SetResourceReference(TextElement.ForegroundProperty, "Shotwise.TextPrimaryBrush");
        surface.SetResourceReference(TextElement.FontFamilyProperty, "Shotwise.FontFamily");
        Children.Add(Ui.ShadowLayer(14));
        Children.Add(surface);

        var chip = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = _size };
        chip.SetResourceReference(Border.BackgroundProperty, "Shotwise.AccentBrush");
        _size.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.OnAccentBrush");
        _row.Children.Add(chip);

        var image = Tool("Icon.Image.Color", CS.Get("TB_Image"), CS.Get("Toolbar_CaptureImage"), () => ActionChosen?.Invoke(SelectionAction.CaptureImage, ScrollDirection.Vertical));
        var video = Tool("Icon.Video.Color", CS.Get("TB_Video"), CS.Get("Toolbar_RecordVideo"), () => ActionChosen?.Invoke(SelectionAction.RecordVideo, ScrollDirection.Vertical));
        _scrollV = Tool(Tint("Icon.Scroll.Filled", "Shotwise.Color.Success"), CS.Get("TB_Scroll"), CS.Get("Toolbar_ScrollVertical"), () => ActionChosen?.Invoke(SelectionAction.Scrolling, ScrollDirection.Vertical));
        _scrollH = Tool(Tint("Icon.FullScreen.Filled", "Shotwise.Color.Success"), CS.Get("TB_ScrollH"), CS.Get("Toolbar_ScrollHorizontal"), () => ActionChosen?.Invoke(SelectionAction.Scrolling, ScrollDirection.Horizontal));
        _scrollBoth = Tool(Tint("Icon.Resize.Filled", "Shotwise.Color.Success"), CS.Get("TB_ScrollAll"), CS.Get("Toolbar_ScrollBoth"), () => ActionChosen?.Invoke(SelectionAction.Scrolling, ScrollDirection.Both));
        _panoramic = Tool(Tint("Icon.FitScreen.Filled", "Shotwise.Color.Warning"), CS.Get("TB_Panoramic"), CS.Get("Toolbar_Panoramic"), () => ActionChosen?.Invoke(SelectionAction.Panoramic, ScrollDirection.Vertical));
        var grab = Tool("Icon.Tool.GrabText.Color", CS.Get("TB_GrabText"), CS.Get("Toolbar_GrabText"), () => ActionChosen?.Invoke(SelectionAction.GrabText, ScrollDirection.Vertical));
        var cancel = Tool("Icon.Dismiss.Regular", CS.Get("TB_Cancel"), CS.Get("Toolbar_Cancel") + " (Esc)", () => Cancelled?.Invoke());

        _scrollGroup.Children.Add(_scrollV);
        _scrollGroup.Children.Add(_scrollH);
        _scrollGroup.Children.Add(_scrollBoth);

        if (fullSet)
        {
            _row.Children.Add(image);
            _row.Children.Add(video);
            _row.Children.Add(_scrollGroup);
            _row.Children.Add(_panoramic);
            _row.Children.Add(grab);
        }
        else
        {
            _row.Children.Add(video);
        }
        _row.Children.Add(Ui.VSeparator(30));
        _row.Children.Add(cancel);
    }

    /// <summary>Gradient-tinted copy of a Filled glyph (the shared icon set has colour variants only for some tools).</summary>
    private static ImageSource? Tint(string geometryKey, string colorKey)
    {
        if (Ui.Res(geometryKey) is not Geometry geo) return null;
        var c = Ui.ColorOf(colorKey, Color.FromRgb(0x16, 0xA3, 0x4A));
        var light = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.45), (byte)(c.G + (255 - c.G) * 0.45), (byte)(c.B + (255 - c.B) * 0.45));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(light, c, 90), null, geo));
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    private static Button Tool(object? icon, string caption, string tip, Action click)
    {
        var b = new Button { Content = caption, ToolTip = tip, Focusable = false, Margin = new Thickness(1, 0, 1, 0), MinWidth = 56 }.WithStyle("Shotwise.ToolCommandButton");
        SwUi.SetIcon(b, icon);
        SwUi.SetIconSize(b, 24);
        Ui.AutomationProperties(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public void Update(int width, int height, ScrollProbe? probe, bool panoramicAllowed)
    {
        _size.Text = $"{width} × {height}";
        bool v = probe?.CanScrollVertical == true, h = probe?.CanScrollHorizontal == true;
        _scrollGroup.Visibility = v || h ? Visibility.Visible : Visibility.Collapsed;
        _scrollV.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        _scrollH.Visibility = h ? Visibility.Visible : Visibility.Collapsed;
        _scrollBoth.Visibility = v && h ? Visibility.Visible : Visibility.Collapsed;
        _panoramic.IsEnabled = panoramicAllowed;
    }
}
