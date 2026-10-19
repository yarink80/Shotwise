using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Selection;

/// <summary>
/// One borderless topmost window per monitor showing the frozen snapshot. Positioned in physical pixels (PerMonitorV2); all
/// pointer positions come from GetCursorPos so a drag can continue across monitors via mouse capture.
/// <para>
/// The window is a reusable host (see <see cref="OverlayWindowPool"/>): creating the HWND and its WPF render target costs ~130 ms per
/// full-monitor window on the measured host, so the window is created once, hidden between selections and gets fresh per-selection
/// content through <see cref="Attach"/>. It is DWM-cloaked while hidden and until the new content has been rendered, so a stale frame
/// from the previous selection is never composited.
/// </para>
/// <para>
/// Latency rule (FX-2): everything the first frame does not need is built later. The floating cards (options bar, mini toolbar,
/// multi-area bar) are created on first use, after the overlay was revealed, and carry their shadows on separate layers.
/// </para>
/// </summary>
internal sealed class SelectionOverlayWindow : Window
{
    private SelectionController? _ctl;
    private SelectionCanvas? _canvas;
    private Canvas? _layer;
    private MiniToolbar? _toolbar;
    private OverlayOptionsBar? _optionsBar;
    private FrameworkElement? _multiBar;
    private TextBlock? _multiCount;
    private bool _primaryHelpHost;
    private bool _revealed;

    public SelectionOverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        var dpi = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        Left = monitor.BoundsPx.X / dpi; Top = monitor.BoundsPx.Y / dpi;
        Width = monitor.BoundsPx.Width / dpi; Height = monitor.BoundsPx.Height / dpi;
        Cursor = Cursors.Cross;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            NativeUi.SetCloaked(Handle, true);
            NativeUi.SetToolWindow(this, false);
            NativeUi.ExcludeFromCapture(this);
            ApplyBounds();
        };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += (_, e) => _ctl?.KeyUp(e.Key == Key.System ? e.SystemKey : e.Key);
    }

    public MonitorInfo Monitor { get; }
    public IntPtr Handle { get; private set; }
    public bool IsAttached => _ctl is not null;

    /// <summary>Creates the HWND and render target without showing anything (pool warm-up).</summary>
    public void Prepare()
    {
        if (Handle == IntPtr.Zero) NativeUi.HandleOf(this);
    }

    /// <summary>Builds the per-selection content (frozen image, canvas) for <paramref name="ctl"/>; floating cards are created lazily.</summary>
    public void Attach(SelectionController ctl, BitmapSource frozen, BgraImage pixels)
    {
        _ctl = ctl;
        _primaryHelpHost = false;
        _revealed = false;
        _canvas = new SelectionCanvas(ctl, Monitor, frozen, pixels, () => _primaryHelpHost);
        _layer = new Canvas();
        var grid = new Grid();
        grid.Children.Add(_canvas);
        grid.Children.Add(_layer);
        _optionsBar = null; _toolbar = null; _multiBar = null; _multiCount = null;

        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.MouseRightButtonDown += (_, e) => { e.Handled = true; ctl.ClearOrCancel(); };
        Cursor = Cursors.Cross;
        Content = grid;
    }

    /// <summary>Shows the window (still cloaked) on top of everything at its exact physical bounds.</summary>
    public void ShowCloaked()
    {
        Prepare();
        NativeUi.SetCloaked(Handle, true);
        if (!IsVisible) Show();
        ApplyBounds();
    }

    /// <summary>Makes the rendered content visible.</summary>
    public void Reveal()
    {
        NativeUi.SetCloaked(Handle, false);
        _revealed = true;
    }

    /// <summary>After the first frame is visible: turn on content kept out of the first frame (magnifier loupe, options bar).</summary>
    public void EnableDeferredContent()
    {
        _canvas?.EnableMagnifier();
        EnsureOptionsBar();
    }

    /// <summary>
    /// Pool dry run only: builds and measures the floating cards once so their styles/templates and JIT are paid at idle time,
    /// then drops them again (the next real selection creates them lazily after the first frame).
    /// </summary>
    public void WarmFloatingCards()
    {
        if (_ctl is not { } ctl || _layer is null) return;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var bar = new OverlayOptionsBar(ctl);
        _layer.Children.Add(bar);
        bar.Measure(inf);
        _layer.Children.Remove(bar);
        var toolbar = new MiniToolbar(fullSet: true);
        _layer.Children.Add(toolbar);
        toolbar.Update(1, 1, null, true);
        toolbar.Measure(inf);
        _layer.Children.Remove(toolbar);
    }

    /// <summary>Hides the window and drops the per-selection content (the frozen bitmap is released).</summary>
    public void Detach()
    {
        if (Handle != IntPtr.Zero) NativeUi.SetCloaked(Handle, true);
        if (_canvas?.IsMouseCaptured == true) _canvas.ReleaseMouseCapture();
        try { Hide(); } catch (InvalidOperationException) { }
        Content = null;
        _ctl = null; _canvas = null; _layer = null; _toolbar = null; _optionsBar = null; _multiBar = null; _multiCount = null;
        _revealed = false;
    }

    public void SetHelpHost(bool primary)
    {
        _primaryHelpHost = primary;
        if (primary && _revealed) EnsureOptionsBar();
        if (_optionsBar is null) return;
        _optionsBar.Visibility = primary ? Visibility.Visible : Visibility.Collapsed;
        PositionOptionsBar();
    }

    private void EnsureOptionsBar()
    {
        if (!_primaryHelpHost || _optionsBar is not null || _ctl is not { } ctl || _layer is null) return;
        _optionsBar = new OverlayOptionsBar(ctl);
        _layer.Children.Add(_optionsBar);
        PositionOptionsBar();
        _canvas?.InvalidateVisual();
    }

    private void PositionOptionsBar()
    {
        if (_optionsBar is null || _optionsBar.Visibility != Visibility.Visible) return;
        _optionsBar.Sync();
        _optionsBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double monW = Monitor.BoundsPx.Width / NativeUi.ScaleOf(this);
        Canvas.SetLeft(_optionsBar, Math.Max(4, (monW - _optionsBar.DesiredSize.Width) / 2));
        Canvas.SetTop(_optionsBar, 14);
    }

    private void ApplyBounds() => NativeUi.PlaceTopmost(this, Monitor.BoundsPx, activate: false);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Handle != IntPtr.Zero && IsVisible) ApplyBounds();
    }

    /// <summary>Repaints and repositions floating controls after the shared state changed.</summary>
    public void Refresh()
    {
        if (_ctl is not { } ctl || _canvas is null) return;
        _canvas.InvalidateVisual();
        PositionOptionsBar();
        UpdateToolbar(ctl);
        if (!ctl.IsFinished)
        {
            var c = ctl.Cursor;
            var h = ctl.Selection is { } sel && ctl.Phase == SelectionPhase.Adjusting ? SelectionController.HitTest(sel, c, (int)(8 * NativeUi.ScaleOf(this))) : HandleId.None;
            Cursor = h switch
            {
                HandleId.TopLeft or HandleId.BottomRight => Cursors.SizeNWSE,
                HandleId.TopRight or HandleId.BottomLeft => Cursors.SizeNESW,
                HandleId.Top or HandleId.Bottom => Cursors.SizeNS,
                HandleId.Left or HandleId.Right => Cursors.SizeWE,
                HandleId.Body => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
        }
    }

    private void UpdateToolbar(SelectionController ctl)
    {
        if (ctl.Request.AdjustBeforeCommit)
        {
            bool show = false;
            if (ctl.Phase == SelectionPhase.Adjusting && ctl.Selection is { } sel && ctl.HostMonitorFor(sel) is { } host && host.DeviceName == Monitor.DeviceName && _layer is not null)
            {
                if (_toolbar is null)
                {
                    _toolbar = new MiniToolbar(fullSet: ctl.Request.IsAllInOne) { Visibility = Visibility.Collapsed };
                    _toolbar.ActionChosen += (a, d) => ctl.CommitCurrent(a, d);
                    _toolbar.Cancelled += ctl.Cancel;
                    _layer.Children.Add(_toolbar);
                }
                show = true;
                _toolbar.Update(sel.Width, sel.Height, ctl.Probe, ctl.FitsOneMonitor(sel));
                _toolbar.Visibility = Visibility.Visible;
                _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                PositionNear(_toolbar, sel);
            }
            if (!show && _toolbar is not null) _toolbar.Visibility = Visibility.Collapsed;
        }
        if (ctl.Request.Type == SelectionType.MultipleArea && _layer is not null)
        {
            bool show = ctl.Areas.Count > 0 && Monitor.BoundsPx.Contains(ctl.Cursor);
            if (show && _multiBar is null) CreateMultiBar(ctl);
            if (_multiBar is not null && _multiCount is not null)
            {
                _multiBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show)
                {
                    _multiCount.Text = CS.Format("Selection_AreasCount", ctl.Areas.Count);
                    _multiBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var s = NativeUi.ScaleOf(this);
                    Canvas.SetLeft(_multiBar, Math.Max(8, (Monitor.BoundsPx.Width / s - _multiBar.DesiredSize.Width) / 2));
                    Canvas.SetTop(_multiBar, 24);
                }
            }
        }
    }

    private void CreateMultiBar(SelectionController ctl)
    {
        Ui.EnsureStyles();
        _multiCount = Ui.Label("", 12, weight: FontWeights.SemiBold, brush: "Shotwise.OnAccentBrush");
        var chip = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = _multiCount };
        chip.SetResourceReference(Border.BackgroundProperty, "Shotwise.AccentBrush");
        var done = Ui.TextButton(CS.Get("Selection_Done"), ctl.CommitMultiple, "Icon.Checkmark.Regular", accent: true);
        var cancel = Ui.IconButton("Icon.Dismiss.Regular", CS.Get("Toolbar_Cancel"), ctl.Cancel);
        cancel.Margin = new Thickness(4, 0, 0, 0);
        var row = Ui.Row(chip, done, cancel);
        var bar = Ui.FloatingCard(row, 16, new Thickness(8, 6, 8, 6), "Shotwise.Elevation.Card");
        bar.Visibility = Visibility.Collapsed;
        bar.SetResourceReference(TextElement.FontFamilyProperty, "Shotwise.FontFamily");
        _multiBar = bar;
        _layer!.Children.Add(bar);
    }

    private void PositionNear(FrameworkElement el, RectI sel)
    {
        double s = NativeUi.ScaleOf(this);
        var mb = Monitor.BoundsPx;
        double w = el.DesiredSize.Width, h = el.DesiredSize.Height;
        double selL = (sel.X - mb.X) / s, selR = (sel.Right - mb.X) / s, selT = (sel.Y - mb.Y) / s, selB = (sel.Bottom - mb.Y) / s;
        double monW = mb.Width / s, monH = mb.Height / s;
        double x = (selL + selR) / 2 - w / 2;
        double y = selB + 10;
        if (y + h > monH - 4)
        {
            y = selT - h - 10;
            if (y < 4) y = Math.Max(4, selB - h - 12); // selection fills the screen: dock inside at the bottom
        }
        x = Math.Clamp(x, 4, Math.Max(4, monW - w - 4));
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_ctl is not { } ctl || _canvas is null) return;
        if (e.ClickCount == 2) { ctl.DoubleClick(NativeUi.CursorPx()); return; }
        _canvas.CaptureMouse();
        ctl.PointerDown(NativeUi.CursorPx(), (int)Math.Ceiling(9 * NativeUi.ScaleOf(this)));
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => _ctl?.PointerMoved(NativeUi.CursorPx());

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_canvas?.IsMouseCaptured == true) _canvas.ReleaseMouseCapture();
        _ctl?.PointerUp(NativeUi.CursorPx());
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_ctl is not { } ctl) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt) return;
        if (ctl.KeyDown(key, Keyboard.Modifiers)) e.Handled = true;
    }
}
