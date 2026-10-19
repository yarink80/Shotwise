using System.Globalization;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor.Tools;

/// <summary>Fill: click flood-fills flattened pixels; Alt+click samples a colour (eyedropper); right-click sets the canvas colour.</summary>
public sealed class FillTool : ToolBase
{
    private bool _alt;

    public FillTool() : base(ToolKind.Fill) { }

    public override CursorHint Cursor => _alt ? CursorHint.Eyedropper : CursorHint.Fill;
    public override bool ShowsBusyCursor => true;

    public override void PointerMove(PointerInput e) => _alt = e.Alt;

    public override void PointerDown(PointerInput e)
    {
        _alt = e.Alt;
        var o = S.ToolOptions.Fill;
        switch (e.Button)
        {
            case PointerButton.Right:
                S.SetCanvasColor(o.Transparent ? Argb.Transparent : o.Color);
                break;
            case PointerButton.Left when e.Alt:
                S.SampleColor(e.Position);
                break;
            case PointerButton.Left:
                _ = S.FillAtAsync(e.Position);
                break;
        }
    }
}

/// <summary>Eraser: drag erases flattened pixels (width / antialias / to canvas colour or transparent); right-click sets the canvas colour.</summary>
public sealed class EraserTool : ToolBase
{
    private readonly List<Point2> _stroke = new();
    private bool _dragging;
    private Point2? _hover;
    private double _zoom = 1;

    public EraserTool() : base(ToolKind.Eraser) { }

    public override CursorHint Cursor => CursorHint.Eraser;
    public override bool ShowsBusyCursor => true;

    /// <summary>Points of the stroke in progress.</summary>
    public IReadOnlyList<Point2> CurrentStroke => _stroke;

    public override void PointerDown(PointerInput e)
    {
        _zoom = e.EffectiveZoom;
        if (e.Button == PointerButton.Right)
        {
            var o = S.ToolOptions.Fill;
            S.SetCanvasColor(o.Transparent ? Argb.Transparent : o.Color);
            return;
        }
        if (e.Button != PointerButton.Left) return;
        _stroke.Clear();
        _stroke.Add(e.Position);
        _dragging = true;
        S.Invalidate();
    }

    public override void PointerMove(PointerInput e)
    {
        _zoom = e.EffectiveZoom;
        _hover = e.Position;
        if (_dragging && e.Position.DistanceTo(_stroke[^1]) * _zoom >= 1)
        {
            _stroke.Add(e.Shift ? GeometryMath.SnapAngle(_stroke[0], e.Position) : e.Position);
            if (e.Shift && _stroke.Count > 2) _stroke.RemoveRange(1, _stroke.Count - 2);
        }
        S.Invalidate();
    }

    public override void PointerUp(PointerInput e)
    {
        if (!_dragging) return;
        _dragging = false;
        var pts = _stroke.ToList();
        _stroke.Clear();
        _ = S.EraseAsync(pts);
    }

    public override void PointerLeave()
    {
        _hover = null;
        Session?.Invalidate();
    }

    public override void Cancel()
    {
        _dragging = false;
        _stroke.Clear();
        Session?.Invalidate();
    }

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var w = S.ToolOptions.Eraser.Width;
        var list = new List<OverlayPrimitive>();
        if (_stroke.Count > 0)
            list.Add(new OverlayPath(_stroke.Count == 1 ? new[] { _stroke[0], _stroke[0] } : _stroke.ToArray(), false) { Style = new OverlayStyle(new Argb(0x80FFFFFF), Math.Max(1, w * zoom)) });
        if (_hover is { } h)
            list.Add(new OverlayEllipse(new Rect2(h.X - w / 2, h.Y - w / 2, w, w)) { Style = OverlayStyle.Hover });
        return list;
    }
}

/// <summary>
/// Crop: a frame (initially the whole canvas) with 8 handles; drag inside moves it, drag outside (or on an untouched
/// full frame) draws a new one, aspect lock from <see cref="CropToolOptions.AspectRatio"/> (Shift = keep current aspect).
/// Enter / <see cref="Apply"/> crops, Esc / <see cref="Reset"/> resets the frame; <see cref="EditorSession.ResetCrop"/>
/// restores a non-destructive crop.
/// </summary>
public sealed class CropTool : ToolBase
{
    private enum Drag { None, New, Move, Handle }

    private Rect2 _rect;
    private Drag _drag;
    private HandleKind _handle;
    private Point2 _down;
    private Rect2 _start;
    private double _zoom = 1;
    private CursorHint _hoverCursor = CursorHint.Crosshair;

    public CropTool() : base(ToolKind.Crop) { }

    /// <summary>Current crop frame (canvas pixels).</summary>
    public Rect2 CropRect => _rect;

    /// <summary>True when the frame differs from the canvas (Apply would crop).</summary>
    public bool HasPendingCrop => Session is not null && _rect != S.ActivePage.CanvasRect && !_rect.IsEmpty;

    public override CursorHint Cursor => _drag switch
    {
        Drag.Move => CursorHint.Move,
        Drag.Handle => SelectionHandles.CursorFor(_handle, 0),
        Drag.New => CursorHint.Crosshair,
        _ => _hoverCursor,
    };

    public override void Activate(EditorSession s)
    {
        base.Activate(s);
        _rect = s.ActivePage.CanvasRect;
    }

    /// <summary>Sets the frame programmatically (floaty W/H fields); clipped to the canvas.</summary>
    public void SetCropRect(Rect2 r)
    {
        var c = r.Normalize().Intersect(S.ActivePage.CanvasRect);
        if (c.Width < 1 || c.Height < 1) return;
        _rect = c;
        S.Invalidate();
    }

    public void Apply()
    {
        if (!HasPendingCrop) return;
        S.Crop(_rect);
        _rect = S.ActivePage.CanvasRect;
        S.Invalidate();
    }

    public void Reset()
    {
        _rect = S.ActivePage.CanvasRect;
        _drag = Drag.None;
        S.Invalidate();
    }

    public override void OnDocumentChanged()
    {
        if (Session is not null && _drag == Drag.None) _rect = _rect.Intersect(S.ActivePage.CanvasRect) is { IsEmpty: false } r ? r : S.ActivePage.CanvasRect;
    }

    private IEnumerable<(HandleKind Kind, Point2 Pos)> Handles()
    {
        foreach (var k in new[] { HandleKind.TopLeft, HandleKind.Top, HandleKind.TopRight, HandleKind.Right, HandleKind.BottomRight, HandleKind.Bottom, HandleKind.BottomLeft, HandleKind.Left })
            yield return (k, SelectionHandles.BoxPoint(_rect, k));
    }

    private HandleKind? HandleAt(Point2 p, double zoom)
    {
        var r = SelectionHandles.ScreenHitRadius / zoom;
        foreach (var (k, pos) in Handles()) if (pos.DistanceTo(p) <= r) return k;
        return null;
    }

    public override void PointerDown(PointerInput e)
    {
        if (e.Button != PointerButton.Left) return;
        _zoom = e.EffectiveZoom;
        _down = e.Position;
        _start = _rect;
        if (HandleAt(e.Position, _zoom) is { } h) { _drag = Drag.Handle; _handle = h; return; }
        var full = _rect == S.ActivePage.CanvasRect;
        _drag = !full && _rect.Contains(e.Position) ? Drag.Move : Drag.New;
    }

    public override void PointerMove(PointerInput e)
    {
        _zoom = e.EffectiveZoom;
        var canvas = S.ActivePage.CanvasRect;
        var p = new Point2(Math.Clamp(e.Position.X, canvas.Left, canvas.Right), Math.Clamp(e.Position.Y, canvas.Top, canvas.Bottom));
        switch (_drag)
        {
            case Drag.None:
                _hoverCursor = HandleAt(e.Position, _zoom) is { } h ? SelectionHandles.CursorFor(h, 0) : _rect.Contains(e.Position) && _rect != canvas ? CursorHint.Move : CursorHint.Crosshair;
                return;
            case Drag.New:
                {
                    if (_down.DistanceTo(e.Position) * _zoom < SelectionManipulator.DragThreshold) return;
                    var aspect = S.ToolOptions.Crop.AspectRatio ?? 0;
                    var q = aspect > 0 ? GeometryMath.ConstrainAspect(_down, p, aspect) : e.Shift ? GeometryMath.ConstrainSquare(_down, p) : p;
                    _rect = Rect2.FromPoints(_down, q).Intersect(canvas);
                    break;
                }
            case Drag.Move:
                {
                    var dx = Math.Clamp(e.Position.X - _down.X, canvas.Left - _start.Left, canvas.Right - _start.Right);
                    var dy = Math.Clamp(e.Position.Y - _down.Y, canvas.Top - _start.Top, canvas.Bottom - _start.Bottom);
                    _rect = _start.Offset(dx, dy);
                    break;
                }
            case Drag.Handle:
                _rect = ResizeFrame(_start, _handle, p, e.Shift ? _start.Width / Math.Max(1, _start.Height) : S.ToolOptions.Crop.AspectRatio ?? 0).Intersect(canvas);
                break;
        }
        S.Invalidate();
    }

    internal static Rect2 ResizeFrame(Rect2 start, HandleKind k, Point2 p, double aspect)
    {
        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        var left = k is HandleKind.TopLeft or HandleKind.Left or HandleKind.BottomLeft;
        var right = k is HandleKind.TopRight or HandleKind.Right or HandleKind.BottomRight;
        var top = k is HandleKind.TopLeft or HandleKind.Top or HandleKind.TopRight;
        var bottom = k is HandleKind.BottomLeft or HandleKind.Bottom or HandleKind.BottomRight;
        if (left) l = Math.Min(p.X, r - 1);
        if (right) r = Math.Max(p.X, l + 1);
        if (top) t = Math.Min(p.Y, b - 1);
        if (bottom) b = Math.Max(p.Y, t + 1);
        if (aspect > 0 && SelectionHandles.IsCorner(k))
        {
            var w = r - l;
            var h = b - t;
            if (w / h > aspect) w = h * aspect; else h = w / aspect;
            if (left) l = r - w; else r = l + w;
            if (top) t = b - h; else b = t + h;
        }
        return Rect2.FromLTRB(Math.Round(l), Math.Round(t), Math.Round(r), Math.Round(b));
    }

    public override void PointerUp(PointerInput e)
    {
        if (_drag == Drag.None) return;
        PointerMove(e);
        if (_drag == Drag.New && (_rect.Width < 1 || _rect.Height < 1)) _rect = _start;
        _drag = Drag.None;
        S.Invalidate();
    }

    public override void DoubleClick(PointerInput e)
    {
        if (_rect.Contains(e.Position)) Apply();
    }

    public override void KeyDown(KeyInput e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                Apply();
                e.Handled = true;
                break;
            case VirtualKey.Escape when HasPendingCrop || _drag != Drag.None:
                Reset();
                e.Handled = true;
                break;
        }
    }

    public override void Cancel()
    {
        if (Session is not null) Reset();
    }

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var c = S.ActivePage.CanvasRect;
        var r = _rect;
        var dim = new Argb(0x99000000);
        var list = new List<OverlayPrimitive>();
        if (r.Top > c.Top) list.Add(new OverlayImageTint(Rect2.FromLTRB(c.Left, c.Top, c.Right, r.Top), dim));
        if (r.Bottom < c.Bottom) list.Add(new OverlayImageTint(Rect2.FromLTRB(c.Left, r.Bottom, c.Right, c.Bottom), dim));
        if (r.Left > c.Left) list.Add(new OverlayImageTint(Rect2.FromLTRB(c.Left, r.Top, r.Left, r.Bottom), dim));
        if (r.Right < c.Right) list.Add(new OverlayImageTint(Rect2.FromLTRB(r.Right, r.Top, c.Right, r.Bottom), dim));
        list.Add(new OverlayRect(r) { Style = OverlayStyle.Crop });
        // rule-of-thirds guides
        for (var i = 1; i <= 2; i++)
        {
            list.Add(new OverlayLine(new Point2(r.X + r.Width * i / 3, r.Top), new Point2(r.X + r.Width * i / 3, r.Bottom)) { Style = OverlayStyle.Crop with { Stroke = new Argb(0x80FFFFFF) } });
            list.Add(new OverlayLine(new Point2(r.Left, r.Y + r.Height * i / 3), new Point2(r.Right, r.Y + r.Height * i / 3)) { Style = OverlayStyle.Crop with { Stroke = new Argb(0x80FFFFFF) } });
        }
        foreach (var (k, pos) in Handles()) list.Add(new OverlayHandle(k.ToString(), pos, k) { Style = OverlayStyle.Handle });
        list.Add(new OverlayText(new Point2(r.X, r.Y - 20 / zoom), string.Create(CultureInfo.InvariantCulture, $"{Math.Round(r.Width)} × {Math.Round(r.Height)}")));
        return list;
    }
}

/// <summary>Cut Out: drag across the canvas to mark the strip (orientation + edge style from <see cref="CutOutToolOptions"/>); release removes it.</summary>
public sealed class CutOutTool : ToolBase
{
    private bool _dragging;
    private double _a;
    private double _b;
    private double? _hover;

    public CutOutTool() : base(ToolKind.CutOut) { }

    private bool Horizontal => S.ToolOptions.CutOut.Orientation == CutOutOrientation.Horizontal;

    public override CursorHint Cursor => Session is null ? CursorHint.Crosshair : Horizontal ? CursorHint.SizeNS : CursorHint.SizeWE;

    /// <summary>Band being marked (start, end) along the cut axis, or null.</summary>
    public (double Start, double End)? Band => _dragging ? (Math.Min(_a, _b), Math.Max(_a, _b)) : null;

    private double Axis(Point2 p) => Horizontal ? p.Y : p.X;

    public override void PointerDown(PointerInput e)
    {
        if (e.Button != PointerButton.Left) return;
        _a = _b = Clamp(Axis(e.Position));
        _dragging = true;
        S.Invalidate();
    }

    private double Clamp(double v)
    {
        var size = S.ActivePage.CanvasSize;
        return Math.Round(Math.Clamp(v, 0, Horizontal ? size.Height : size.Width));
    }

    public override void PointerMove(PointerInput e)
    {
        _hover = Clamp(Axis(e.Position));
        if (_dragging) _b = _hover.Value;
        S.Invalidate();
    }

    public override void PointerUp(PointerInput e)
    {
        if (!_dragging) return;
        _b = Clamp(Axis(e.Position));
        _dragging = false;
        var (s, en) = (Math.Min(_a, _b), Math.Max(_a, _b));
        if (en - s >= 1)
        {
            var o = S.ToolOptions.CutOut;
            S.CutOut(o.Orientation, s, en, o.Edge);
        }
        S.Invalidate();
    }

    public override void PointerLeave()
    {
        _hover = null;
        Session?.Invalidate();
    }

    public override void Cancel()
    {
        _dragging = false;
        Session?.Invalidate();
    }

    public override void KeyDown(KeyInput e)
    {
        if (e.Key == VirtualKey.Escape && _dragging) { Cancel(); e.Handled = true; }
    }

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var size = S.ActivePage.CanvasSize;
        var list = new List<OverlayPrimitive>();
        Point2 From(double v) => Horizontal ? new Point2(0, v) : new Point2(v, 0);
        Point2 To(double v) => Horizontal ? new Point2(size.Width, v) : new Point2(v, size.Height);
        if (Band is { } band)
        {
            var r = Horizontal ? new Rect2(0, band.Start, size.Width, band.End - band.Start) : new Rect2(band.Start, 0, band.End - band.Start, size.Height);
            list.Add(new OverlayImageTint(r, new Argb(0x66E91E63)));
            list.Add(new OverlayLine(From(band.Start), To(band.Start)) { Style = OverlayStyle.Guide });
            list.Add(new OverlayLine(From(band.End), To(band.End)) { Style = OverlayStyle.Guide });
            list.Add(new OverlayText(Horizontal ? new Point2(4 / zoom, band.End + 4 / zoom) : new Point2(band.End + 4 / zoom, 4 / zoom), string.Create(CultureInfo.InvariantCulture, $"{Math.Round(band.End - band.Start)} px")));
        }
        else if (_hover is { } h)
        {
            list.Add(new OverlayLine(From(h), To(h)) { Style = OverlayStyle.Hover with { Stroke = OverlayStyle.Guide.Stroke } });
        }
        return list;
    }
}

/// <summary>Grab Text / Edit Text: drag a region (click = whole page or pixel selection) → OCR result, or editable text objects in Edit Text mode.</summary>
public sealed class GrabTextTool : ToolBase
{
    private bool _dragging;
    private Point2 _down;
    private Rect2 _rect;
    private double _zoom = 1;

    public GrabTextTool() : base(ToolKind.GrabText) { }

    public override CursorHint Cursor => CursorHint.Crosshair;
    public override bool ShowsBusyCursor => true;

    public override void PointerDown(PointerInput e)
    {
        if (e.Button != PointerButton.Left) return;
        _zoom = e.EffectiveZoom;
        _dragging = true;
        _down = e.Position;
        _rect = new Rect2(_down.X, _down.Y, 0, 0);
    }

    public override void PointerMove(PointerInput e)
    {
        if (!_dragging) return;
        _rect = Rect2.FromPoints(_down, e.Position).Intersect(S.ActivePage.CanvasRect);
        S.Invalidate();
    }

    public override void PointerUp(PointerInput e)
    {
        if (!_dragging) return;
        _dragging = false;
        _rect = Rect2.FromPoints(_down, e.Position).Intersect(S.ActivePage.CanvasRect);
        Rect2? region = _down.DistanceTo(e.Position) * _zoom >= SelectionManipulator.DragThreshold && !_rect.IsEmpty ? _rect : null;
        if (S.ToolOptions.GrabText.EditText) _ = S.EditTextAsync(region);
        else _ = S.GrabTextAsync(region);
        S.Invalidate();
    }

    public override void Cancel()
    {
        _dragging = false;
        Session?.Invalidate();
    }

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
        => _dragging && !_rect.IsEmpty ? new OverlayPrimitive[] { new OverlayRect(_rect) { Style = OverlayStyle.Marquee } } : Array.Empty<OverlayPrimitive>();
}
