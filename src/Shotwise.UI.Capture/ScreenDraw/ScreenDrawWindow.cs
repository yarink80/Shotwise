using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Capture.ScreenDraw;

/// <summary>
/// Transparent topmost overlay over the recorded monitor. Click-through while inactive (drawings stay visible and keep fading); in
/// draw mode it captures the mouse for Arrow (A), Square (S) and Step (1) drawing. The overlay is excluded from capture because the
/// same <see cref="ScreenDrawModel"/> is composited into the video by the recorder (<see cref="Core.Media.IFrameOverlay"/>).
/// </summary>
public sealed class ScreenDrawWindow : Window
{
    private readonly ScreenDrawModel _model;
    private readonly RectI _areaPx;
    private readonly DrawSurface _surface;
    private readonly FrameworkElement _toolbar;
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Func<Argb, Task<Argb?>>? _pickColor;
    private readonly Action<Argb, int>? _persist;
    private DrawTool _tool = DrawTool.Arrow;
    private readonly Dictionary<DrawTool, ToolButton> _toolButtons = new();
    private readonly List<(Argb Color, ToggleButton Button)> _swatches = new();

    public bool IsDrawing { get; private set; }
    public event Action<bool>? ModeChanged;

    public ScreenDrawWindow(ScreenDrawModel model, RectI monitorBoundsPx, Func<Argb, Task<Argb?>>? pickColor, Action<Argb, int>? persist)
    {
        Ui.EnsureStyles();
        _model = model;
        _areaPx = monitorBoundsPx;
        _pickColor = pickColor;
        _persist = persist;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        this.SetResourceReference(FontFamilyProperty, "Shotwise.FontFamily");
        _surface = new DrawSurface(model, monitorBoundsPx);
        _toolbar = BuildToolbar();
        _toolbar.Visibility = Visibility.Collapsed;
        _toolbar.HorizontalAlignment = HorizontalAlignment.Center;
        _toolbar.VerticalAlignment = VerticalAlignment.Top;
        _toolbar.Margin = new Thickness(0, 16, 0, 0);
        var grid = new Grid();
        grid.Children.Add(_surface);
        grid.Children.Add(_toolbar);
        Content = grid;

        SourceInitialized += (_, _) =>
        {
            NativeUi.SetToolWindow(this, true);
            NativeUi.ExcludeFromCapture(this);
            NativeUi.SetClickThrough(this, true);
            NativeUi.PlaceTopmost(this, _areaPx);
        };
        _surface.MouseLeftButtonDown += OnDown;
        _surface.MouseMove += OnMove;
        _surface.MouseLeftButtonUp += OnUp;
        _surface.MouseRightButtonDown += (_, e) => { e.Handled = true; _model.Undo(); _surface.InvalidateVisual(); };
        PreviewKeyDown += OnKey;
        _fade.Tick += (_, _) => _surface.InvalidateVisual();
        Loaded += (_, _) => _fade.Start();
        Closed += (_, _) => _fade.Stop();
        SelectTool(DrawTool.Arrow);
        SyncSwatches();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        NativeUi.PlaceTopmost(this, _areaPx);
    }

    public void Open()
    {
        Show();
        NativeUi.PlaceTopmost(this, _areaPx);
    }

    /// <summary>Enters or leaves draw mode (the enter/exit hotkey).</summary>
    public void SetDrawing(bool on)
    {
        if (on == IsDrawing) return;
        IsDrawing = on;
        NativeUi.SetClickThrough(this, !on);
        Background = on ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : Brushes.Transparent; // alpha 1: catches the mouse, visually transparent
        _toolbar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _surface.Cursor = on ? Cursors.Cross : Cursors.Arrow;
        if (on)
        {
            Activate();
            NativeUi.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Focus();
        }
        ModeChanged?.Invoke(on);
    }

    private void SelectTool(DrawTool t)
    {
        _tool = t;
        foreach (var (tool, b) in _toolButtons) b.IsChecked = tool == t;
    }

    private void SyncSwatches()
    {
        foreach (var (color, b) in _swatches) b.IsChecked = color.Value == _model.Color.Value;
    }

    /// <summary>Draw-mode toolbar: a floating card with colour tool buttons, a round colour palette, fade timing and undo / clear / exit.</summary>
    private FrameworkElement BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ToolButton ToolBtn(DrawTool t, string icon, string caption, string tip)
        {
            var b = new ToolButton { Icon = icon, Content = caption, ToolTip = tip, GroupName = "screen-draw-tool", Focusable = false, MinWidth = 52 };
            b.Click += (_, _) => SelectTool(t);
            _toolButtons[t] = b;
            return b;
        }
        row.Children.Add(ToolBtn(DrawTool.Arrow, "Icon.Tool.Arrow.Color", CS.Get("Draw_Arrow"), CS.Get("Draw_Arrow") + " (A)"));
        row.Children.Add(ToolBtn(DrawTool.Square, "Icon.Tool.Shape.Color", CS.Get("Draw_Square"), CS.Get("Draw_Square") + " (S)"));
        row.Children.Add(ToolBtn(DrawTool.Step, "Icon.Tool.Step.Color", CS.Get("Draw_Step"), CS.Get("Draw_Step") + " (1)"));
        row.Children.Add(Ui.VSeparator(30));

        var palette = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new uint[] { 0xFFE53935, 0xFFFB8C00, 0xFFFDD835, 0xFF43A047, 0xFF1E88E5, 0xFF000000, 0xFFFFFFFF })
        {
            var color = new Argb(c);
            var sw = new ToggleButton { Background = color.ToBrush(), ToolTip = color.ToRgbHex(), Focusable = false }.WithStyle("Capture.Swatch");
            Ui.AutomationProperties(sw, color.ToRgbHex());
            sw.Click += (_, _) => SetColor(color);
            _swatches.Add((color, sw));
            palette.Children.Add(sw);
        }
        if (_pickColor is not null)
        {
            var more = Ui.IconButton("Icon.Color.Regular", CS.Get("Draw_MoreColors"), async () => { var c = await _pickColor(_model.Color); if (c is { } v) SetColor(v); }, 18);
            more.Focusable = false;
            palette.Children.Add(more);
        }
        row.Children.Add(palette);
        row.Children.Add(Ui.VSeparator(30));

        var fadeIcon = Ui.Icon("Icon.Timer.Regular", 18, "Shotwise.TextSecondaryBrush");
        fadeIcon.ToolTip = CS.Get("Draw_FadeAfter");
        var fade = Ui.Combo(new[] { (0, CS.Get("Draw_FadeNever")), (2, "2 s"), (3, "3 s"), (5, "5 s"), (10, "10 s"), (30, "30 s") }, (int)_model.FadeSeconds, v =>
        {
            _model.FadeSeconds = v;
            _persist?.Invoke(_model.Color, v);
        }, 84);
        fade.Margin = new Thickness(6, 0, 0, 0);
        fade.Focusable = false;
        fade.ToolTip = CS.Get("Draw_FadeAfter");
        Ui.AutomationProperties(fade, CS.Get("Draw_FadeAfter"));
        row.Children.Add(fadeIcon);
        row.Children.Add(fade);
        row.Children.Add(Ui.VSeparator(30));
        var undo = Ui.IconButton("Icon.Undo.Regular", CS.Get("Draw_Undo") + " (Ctrl+Z)", () => { _model.Undo(); _surface.InvalidateVisual(); }, 20);
        var clear = Ui.IconButton("Icon.Delete.Regular", CS.Get("Draw_Clear") + " (Del)", () => { _model.Clear(); _surface.InvalidateVisual(); }, 20);
        var exit = Ui.IconButton("Icon.Dismiss.Regular", CS.Get("Draw_Exit") + " (Esc)", () => SetDrawing(false), 20);
        foreach (var b in new[] { undo, clear, exit }) b.Focusable = false;
        row.Children.Add(undo);
        row.Children.Add(clear);
        row.Children.Add(exit);

        var card = Ui.FloatingCard(row, 16, new Thickness(8, 6, 8, 6));
        card.SetResourceReference(TextElement.FontFamilyProperty, "Shotwise.FontFamily");
        card.SetResourceReference(TextElement.ForegroundProperty, "Shotwise.TextPrimaryBrush");
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    private void SetColor(Argb c)
    {
        _model.Color = c;
        SyncSwatches();
        _persist?.Invoke(c, (int)_model.FadeSeconds);
    }

    private PointI? _start;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsDrawing) return;
        e.Handled = true;
        var p = NativeUi.CursorPx();
        if (_tool == DrawTool.Step)
        {
            _model.Add(new DrawItem(DrawTool.Step, p.ToPoint2(), p.ToPoint2(), _model.Color, _model.NowMs, _model.NextStepNumber()));
            _surface.InvalidateVisual();
            return;
        }
        _start = p;
        _surface.Preview = new DrawItem(_tool, p.ToPoint2(), p.ToPoint2(), _model.Color, _model.NowMs, 0);
        _surface.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } s || _surface.Preview is null) return;
        var p = NativeUi.CursorPx();
        var b = p.ToPoint2();
        // Shift constrains arrows to 45-degree steps and squares to a square.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            var dx = b.X - s.X; var dy = b.Y - s.Y;
            if (_tool == DrawTool.Square) { var m = Math.Max(Math.Abs(dx), Math.Abs(dy)); b = new Point2(s.X + Math.Sign(dx) * m, s.Y + Math.Sign(dy) * m); }
            else
            {
                var ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                var len = Math.Sqrt(dx * dx + dy * dy);
                b = new Point2(s.X + Math.Cos(ang) * len, s.Y + Math.Sin(ang) * len);
            }
        }
        _surface.Preview = _surface.Preview with { B = b };
        _surface.InvalidateVisual();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        e.Handled = true;
        _surface.ReleaseMouseCapture();
        var item = _surface.Preview;
        _surface.Preview = null;
        _start = null;
        if (item is not null && (Math.Abs(item.B.X - item.A.X) > 3 || Math.Abs(item.B.Y - item.A.Y) > 3))
            _model.Add(item with { CreatedMs = _model.NowMs });
        _surface.InvalidateVisual();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!IsDrawing) return;
        switch (e.Key)
        {
            case Key.A: SelectTool(DrawTool.Arrow); e.Handled = true; break;
            case Key.S: SelectTool(DrawTool.Square); e.Handled = true; break;
            case Key.D1 or Key.NumPad1: SelectTool(DrawTool.Step); e.Handled = true; break;
            case Key.Z when (Keyboard.Modifiers & ModifierKeys.Control) != 0: _model.Undo(); _surface.InvalidateVisual(); e.Handled = true; break;
            case Key.Delete: _model.Clear(); _surface.InvalidateVisual(); e.Handled = true; break;
            case Key.Escape: SetDrawing(false); e.Handled = true; break;
        }
    }

    /// <summary>Renders the model's items (plus the item being drawn) in physical pixels.</summary>
    private sealed class DrawSurface : FrameworkElement
    {
        private readonly ScreenDrawModel _model;
        private readonly RectI _origin;
        public DrawItem? Preview { get; set; }

        public DrawSurface(ScreenDrawModel model, RectI origin)
        {
            _model = model; _origin = origin;
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            dc.PushTransform(new ScaleTransform(1 / s, 1 / s));
            // Hit-test surface: nearly transparent so draw mode receives mouse input.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, _origin.Width, _origin.Height));
            var now = _model.NowMs;
            foreach (var it in _model.Snapshot()) DrawItem(dc, it, _model.OpacityOf(it, now));
            if (Preview is { } p) DrawItem(dc, p, 1);
            dc.Pop();
        }

        private void DrawItem(DrawingContext dc, DrawItem it, double opacity)
        {
            if (opacity <= 0) return;
            var c = it.Color.WithOpacity(opacity).ToColor();
            var brush = new SolidColorBrush(c);
            var pen = new Pen(brush, ScreenDrawModel.BaseStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            Point P(Point2 q) => new(q.X - _origin.X, q.Y - _origin.Y);
            var a = P(it.A); var b = P(it.B);
            switch (it.Tool)
            {
                case DrawTool.Arrow:
                {
                    dc.DrawLine(pen, a, b);
                    var (l, r) = ScreenDrawModel.ArrowHead(it.A, it.B, 22);
                    dc.DrawLine(pen, b, P(l));
                    dc.DrawLine(pen, b, P(r));
                    break;
                }
                case DrawTool.Square:
                    dc.DrawRoundedRectangle(null, pen, new Rect(a, b), 3, 3);
                    break;
                case DrawTool.Step:
                {
                    double d = ScreenDrawModel.StepDiameter;
                    dc.DrawEllipse(brush, null, a, d / 2, d / 2);
                    var ft = new FormattedText(it.StepNumber.ToString(), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 18, new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255)), 1.0);
                    dc.DrawText(ft, new Point(a.X - ft.Width / 2, a.Y - ft.Height / 2));
                    break;
                }
            }
        }
    }
}
