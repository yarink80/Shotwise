using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Media;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Video;

/// <summary>
/// Timeline: ruler, rounded thumbnail strip, trim handles (green start / red end), cut ranges, selection range and an accent
/// playhead. Dragging the ruler scrubs; dragging the strip creates a selection; dragging a trim handle trims; right-click a cut
/// to restore it. Colours come from the design tokens (re-rendered when the theme swaps them).
/// </summary>
internal sealed class VideoTimeline : FrameworkElement
{
    private const double Pad = 18;
    private const double RulerHeight = 22;

    private enum Drag { None, Scrub, Select, TrimStart, TrimEnd, SelStart, SelEnd }

    private static DependencyProperty Token(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(VideoTimeline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AccentProperty = Token("AccentToken");
    private static readonly DependencyProperty AccentSubtleProperty = Token("AccentSubtleToken");
    private static readonly DependencyProperty TextProperty = Token("TextToken");
    private static readonly DependencyProperty TrackProperty = Token("TrackToken");
    private static readonly DependencyProperty SuccessProperty = Token("SuccessToken");
    private static readonly DependencyProperty DangerProperty = Token("DangerToken");
    private static readonly DependencyProperty OnAccentProperty = Token("OnAccentToken");
    private static readonly DependencyProperty ScrimProperty = Token("ScrimToken");

    private IReadOnlyList<ImageSource> _thumbs = Array.Empty<ImageSource>();
    private VideoEditList? _edl;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _position;
    private TimeRange? _selection;
    private TimeRange? _trimPreview;
    private Drag _drag;
    private TimeSpan _anchor;

    public VideoTimeline()
    {
        MinHeight = 104;
        Height = 104;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        SetResourceReference(AccentProperty, "Shotwise.AccentBrush");
        SetResourceReference(AccentSubtleProperty, "Shotwise.AccentSubtleHoverBrush");
        SetResourceReference(TextProperty, "Shotwise.TextSecondaryBrush");
        SetResourceReference(TrackProperty, "Shotwise.Surface3Brush");
        SetResourceReference(SuccessProperty, "Shotwise.SuccessBrush");
        SetResourceReference(DangerProperty, "Shotwise.DangerBrush");
        SetResourceReference(OnAccentProperty, "Shotwise.OnAccentBrush");
        SetResourceReference(ScrimProperty, "Shotwise.ScrimBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, ES.T("Video_Timeline", "Timeline"));
    }

    private Brush B(DependencyProperty p) => (Brush)GetValue(p);

    public event Action<TimeSpan>? Scrubbed;
    public event Action<TimeRange?>? SelectionChanged;
    public event Action<TimeRange>? TrimCommitted;
    public event Action<TimeRange>? CutRestoreRequested;

    public TimeRange? Selection
    {
        get => _selection;
        set { _selection = value; InvalidateVisual(); }
    }

    public TimeSpan Position
    {
        get => _position;
        set { _position = value; InvalidateVisual(); }
    }

    public void SetMedia(TimeSpan duration, IReadOnlyList<ImageSource> thumbs, VideoEditList edl)
    {
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _thumbs = thumbs;
        if (_edl is not null) _edl.Changed -= OnEdl;
        _edl = edl;
        _edl.Changed += OnEdl;
        InvalidateVisual();
    }

    public void SetThumbs(IReadOnlyList<ImageSource> thumbs)
    {
        _thumbs = thumbs;
        InvalidateVisual();
    }

    private void OnEdl(object? sender, EventArgs e) => Dispatcher.BeginInvoke(InvalidateVisual);

    private double TrackWidth => Math.Max(10, ActualWidth - 2 * Pad);
    private double X(TimeSpan t) => Pad + t.TotalSeconds / _duration.TotalSeconds * TrackWidth;
    private TimeSpan T(double x) => TimeSpan.FromSeconds(Math.Clamp((x - Pad) / TrackWidth, 0, 1) * _duration.TotalSeconds);

    private TimeRange Trim => _trimPreview ?? _edl?.Trim ?? new TimeRange(TimeSpan.Zero, _duration);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var fg = B(TextProperty);
        var accent = B(AccentProperty);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var track = new Rect(Pad, RulerHeight + 6, TrackWidth, h - RulerHeight - 16);
        // ruler ticks + labels
        var seconds = _duration.TotalSeconds;
        var step = seconds switch { <= 10 => 1.0, <= 60 => 5.0, <= 300 => 30.0, <= 1800 => 120.0, _ => 600.0 };
        var minor = step / 5;
        var pen = new Pen(fg, 1);
        var minorPen = new Pen(fg, 1) { DashStyle = DashStyles.Solid };
        for (var i = 0; i * minor <= seconds + 0.001; i++)
        {
            var s = i * minor;
            var x = Math.Round(X(TimeSpan.FromSeconds(s))) + 0.5;
            var major = i % 5 == 0;
            dc.PushOpacity(major ? 0.9 : 0.35);
            dc.DrawLine(major ? pen : minorPen, new Point(x, RulerHeight - (major ? 7 : 4)), new Point(x, RulerHeight));
            dc.Pop();
            if (major)
            {
                var ft = new FormattedText(UiKit.FormatShortDuration(TimeSpan.FromSeconds(Math.Round(s))), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5, fg, dpi);
                if (x + 3 + ft.Width < w - 2) dc.DrawText(ft, new Point(x + 3, 2));
            }
        }

        // thumbnails strip (rounded)
        dc.PushClip(new RectangleGeometry(track, 8, 8));
        dc.DrawRectangle(B(TrackProperty), null, track);
        if (_thumbs.Count > 0)
        {
            var tw = track.Width / _thumbs.Count;
            for (var i = 0; i < _thumbs.Count; i++) dc.DrawImage(_thumbs[i], new Rect(track.X + i * tw, track.Y, tw + 0.5, track.Height));
        }

        // outside trim → dim
        var trim = Trim;
        var dim = B(ScrimProperty);
        dc.DrawRectangle(dim, null, new Rect(track.X, track.Y, Math.Max(0, X(trim.Start) - track.X), track.Height));
        dc.DrawRectangle(dim, null, new Rect(X(trim.End), track.Y, Math.Max(0, track.Right - X(trim.End)), track.Height));
        // cuts → danger hatch
        if (_edl is not null)
        {
            var cutBrush = B(DangerProperty).Clone();
            cutBrush.Opacity = 0.55;
            var hatch = new Pen(B(OnAccentProperty), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            foreach (var c in _edl.Cuts)
            {
                var r = new Rect(X(c.Start), track.Y, Math.Max(2, X(c.End) - X(c.Start)), track.Height);
                dc.DrawRectangle(cutBrush, null, r);
                dc.DrawLine(hatch, r.TopLeft, r.BottomRight);
            }
        }
        // selection
        if (_selection is { IsEmpty: false } sel)
        {
            var r = new Rect(X(sel.Start), track.Y, Math.Max(2, X(sel.End) - X(sel.Start)), track.Height);
            dc.DrawRectangle(B(AccentSubtleProperty), null, r);
            dc.DrawRectangle(null, new Pen(accent, 2), new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)));
        }
        dc.Pop();
        // frame around the strip
        dc.DrawRoundedRectangle(null, new Pen(B(TrackProperty), 1), track, 8, 8);

        // trim handles: green start, red end
        DrawHandle(dc, X(trim.Start), track, B(SuccessProperty), left: true);
        DrawHandle(dc, X(trim.End), track, B(DangerProperty), left: false);

        // playhead (accent): line + rounded head on the ruler
        var px = Math.Round(X(_position)) + 0.5;
        dc.DrawLine(new Pen(accent, 2), new Point(px, RulerHeight - 2), new Point(px, track.Bottom + 4));
        var head = new Rect(px - 6, RulerHeight - 12, 12, 12);
        dc.DrawGeometry(accent, null, Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M {head.Left},{head.Top + 3} Q {head.Left},{head.Top} {head.Left + 3},{head.Top} L {head.Right - 3},{head.Top} Q {head.Right},{head.Top} {head.Right},{head.Top + 3} L {head.Right},{head.Top + 6} L {px},{head.Bottom + 2} L {head.Left},{head.Top + 6} Z")));
    }

    private void DrawHandle(DrawingContext dc, double x, Rect track, Brush brush, bool left)
    {
        var r = new Rect(left ? x - 10 : x, track.Y - 3, 10, track.Height + 6);
        dc.DrawRoundedRectangle(brush, null, r, 4, 4);
        var pen = new Pen(B(OnAccentProperty), 1.5);
        dc.DrawLine(pen, new Point(r.X + 3.5, r.Y + r.Height / 2 - 7), new Point(r.X + 3.5, r.Y + r.Height / 2 + 7));
        dc.DrawLine(pen, new Point(r.X + 6.5, r.Y + r.Height / 2 - 7), new Point(r.X + 6.5, r.Y + r.Height / 2 + 7));
    }

    private Drag HitTest(Point p)
    {
        var trim = Trim;
        if (p.Y <= RulerHeight + 2) return Drag.Scrub;
        if (_selection is { IsEmpty: false } sel)
        {
            if (Math.Abs(p.X - X(sel.Start)) <= 5) return Drag.SelStart;
            if (Math.Abs(p.X - X(sel.End)) <= 5) return Drag.SelEnd;
        }
        if (p.X >= X(trim.Start) - 11 && p.X <= X(trim.Start) + 1) return Drag.TrimStart;
        if (p.X >= X(trim.End) - 1 && p.X <= X(trim.End) + 11) return Drag.TrimEnd;
        return Drag.Select;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _drag = HitTest(p);
        _anchor = T(p.X);
        CaptureMouse();
        if (_drag == Drag.Scrub) Scrubbed?.Invoke(_anchor);
        if (_drag == Drag.Select) { _selection = null; SelectionChanged?.Invoke(null); }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            var hit = HitTest(p);
            Cursor = hit is Drag.TrimStart or Drag.TrimEnd or Drag.SelStart or Drag.SelEnd ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        var t = T(p.X);
        switch (_drag)
        {
            case Drag.Scrub:
                Scrubbed?.Invoke(t);
                break;
            case Drag.Select:
                if (Math.Abs(t.TotalSeconds - _anchor.TotalSeconds) > _duration.TotalSeconds / TrackWidth * 3)
                {
                    _selection = t >= _anchor ? new TimeRange(_anchor, t) : new TimeRange(t, _anchor);
                    SelectionChanged?.Invoke(_selection);
                }
                break;
            case Drag.SelStart when _selection is { } s:
                _selection = new TimeRange(t < s.End ? t : s.End, s.End);
                SelectionChanged?.Invoke(_selection);
                break;
            case Drag.SelEnd when _selection is { } s:
                _selection = new TimeRange(s.Start, t > s.Start ? t : s.Start);
                SelectionChanged?.Invoke(_selection);
                break;
            case Drag.TrimStart when _edl is not null:
                _trimPreview = new TimeRange(t < _edl.Trim.End - TimeSpan.FromMilliseconds(100) ? t : _edl.Trim.End - TimeSpan.FromMilliseconds(100), _edl.Trim.End);
                break;
            case Drag.TrimEnd when _edl is not null:
                _trimPreview = new TimeRange(_edl.Trim.Start, t > _edl.Trim.Start + TimeSpan.FromMilliseconds(100) ? t : _edl.Trim.Start + TimeSpan.FromMilliseconds(100));
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (_drag == Drag.Select && (_selection is null || _selection.IsEmpty)) Scrubbed?.Invoke(T(p.X));
        if (_drag is Drag.TrimStart or Drag.TrimEnd && _trimPreview is { } trim)
        {
            _trimPreview = null;
            TrimCommitted?.Invoke(trim);
        }
        _drag = Drag.None;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_edl is null) return;
        var t = T(e.GetPosition(this).X);
        var cut = _edl.Cuts.FirstOrDefault(c => c.Contains(t));
        if (cut is null) return;
        var menu = new ContextMenu { PlacementTarget = this };
        var item = new MenuItem { Header = ES.T("Video_RestoreCut", "Restore this cut"), Icon = UiKit.Glyph(Icons.Glyph.Undo, 16) };
        item.Click += (_, _) => CutRestoreRequested?.Invoke(cut);
        menu.Items.Add(item);
        menu.IsOpen = true;
    }
}
