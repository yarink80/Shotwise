using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Geometry;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Hud;

/// <summary>Visual family of a HUD window.</summary>
public enum HudKind
{
    /// <summary>Dark translucent pill (recording HUD, step HUD, wait HUD): always dark, in both themes.</summary>
    DarkPill,
    /// <summary>Light/dark themed floating card with elevation (Ready to record, Panoramic/Scrolling HUDs, the widget).</summary>
    Card,
    /// <summary>No chrome at all (webcam preview): the content draws itself.</summary>
    Bare,
}

/// <summary>
/// Small borderless always-on-top tool window (recording HUD, countdown, panoramic/step HUD ...). Never shown in the taskbar, never
/// captured by screenshots/recordings (display affinity), draggable by its background and placed in physical pixels. The window is
/// per-pixel transparent so the chrome can be a real pill/card with a soft shadow in the transparent margin around it.
/// </summary>
public class HudWindow : Window
{
    /// <summary>Transparent margin around the chrome that holds the shadow (DIPs).</summary>
    public static readonly Thickness DefaultShadowMargin = new(14);

    private readonly Border _chrome = new() { BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8) };
    private readonly Grid _host = new();
    private readonly Border? _shadow;

    public HudWindow(bool noActivate = true, HudKind kind = HudKind.Card)
    {
        Ui.EnsureStyles();
        Kind = kind;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = !noActivate;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        this.SetResourceReference(FontFamilyProperty, "Shotwise.FontFamily");

        switch (kind)
        {
            case HudKind.DarkPill:
                _chrome.CornerRadius = new CornerRadius(28);
                _chrome.SetResourceReference(Border.BackgroundProperty, "Shotwise.HudBrush");
                _chrome.SetResourceReference(Border.BorderBrushProperty, "Capture.HudBorderBrush");
                _chrome.SetResourceReference(UIElement.EffectProperty, "Shotwise.Elevation.Flyout");
                _chrome.SetResourceReference(TextElement.ForegroundProperty, "Shotwise.HudTextBrush");
                break;
            case HudKind.Card:
                _chrome.CornerRadius = new CornerRadius(14);
                _chrome.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
                _chrome.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
                _chrome.SetResourceReference(TextElement.ForegroundProperty, "Shotwise.TextPrimaryBrush");
                _shadow = Ui.ShadowLayer(14);
                break;
            default:
                _chrome.BorderThickness = new Thickness(0);
                _chrome.Padding = new Thickness(0);
                break;
        }
        if (_shadow is not null) _host.Children.Add(_shadow);
        _host.Children.Add(_chrome);
        ShadowMargin = kind == HudKind.Bare ? new Thickness(10) : DefaultShadowMargin;
        base.Content = _host;
        MouseLeftButtonDown += OnDragStart;
        SourceInitialized += (_, _) =>
        {
            NativeUi.ExcludeFromCapture(this);
            NativeUi.SetToolWindow(this, noActivate);
        };
    }

    public HudKind Kind { get; }

    protected Thickness ChromePadding { get => _chrome.Padding; set => _chrome.Padding = value; }

    protected CornerRadius ChromeRadius
    {
        get => _chrome.CornerRadius;
        set { _chrome.CornerRadius = value; if (_shadow is not null) _shadow.CornerRadius = value; }
    }

    /// <summary>Transparent margin around the chrome (room for the shadow). Use a smaller margin on screen edges the window docks to.</summary>
    protected Thickness ShadowMargin
    {
        get => _host.Margin;
        set { _host.Margin = value; if (_shadow is not null) _shadow.Margin = new Thickness(0); }
    }

    protected Border Chrome => _chrome;

    /// <summary>Content hosted inside the rounded chrome.</summary>
    public new UIElement? Content
    {
        get => _chrome.Child;
        set => _chrome.Child = value;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        OnDragCompleted();
    }

    /// <summary>Called after the user finished dragging the window by its background.</summary>
    protected virtual void OnDragCompleted() { }

    private static bool IsInteractive(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or TextBox or ComboBox or Slider or System.Windows.Controls.Primitives.ScrollBar) return true;
            d = (d is Visual or System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>
    /// Positions the HUD relative to a screen-pixel rectangle: below it when there is room on that monitor, otherwise above, otherwise
    /// inside at the bottom. <paramref name="workAreaPx"/> is the usable area of the monitor containing the region.
    /// </summary>
    public void PlaceNear(RectI regionPx, RectI workAreaPx, int gapPx = 12)
    {
        Show();
        UpdateLayout();
        var scale = NativeUi.ScaleOf(this);
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int top = (int)(ShadowMargin.Top * scale), bottom = (int)(ShadowMargin.Bottom * scale);
        int x = regionPx.X + (regionPx.Width - w) / 2;
        int y = regionPx.Bottom + gapPx - top;
        if (y + h - bottom > workAreaPx.Bottom)
        {
            y = regionPx.Y - h - gapPx + bottom;
            if (y + top < workAreaPx.Y) y = Math.Max(workAreaPx.Y - top, regionPx.Bottom - h - gapPx + bottom);
        }
        x = Math.Clamp(x, workAreaPx.X, Math.Max(workAreaPx.X, workAreaPx.Right - w));
        NativeUi.MoveTo(this, x, y);
    }

    public void PlaceTopCenter(RectI workAreaPx, int marginPx = 12)
    {
        Show();
        UpdateLayout();
        var scale = NativeUi.ScaleOf(this);
        int w = (int)Math.Ceiling(ActualWidth * scale);
        NativeUi.MoveTo(this, workAreaPx.X + (workAreaPx.Width - w) / 2, workAreaPx.Y + marginPx - (int)(ShadowMargin.Top * scale));
    }

    /// <summary>Closes the window from any thread.</summary>
    public void CloseSafe()
    {
        if (Dispatcher.CheckAccess()) { try { Close(); } catch (InvalidOperationException) { } }
        else Dispatcher.BeginInvoke(new Action(CloseSafe));
    }
}
