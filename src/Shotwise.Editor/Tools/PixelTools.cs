using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor.Tools;

/// <summary>How a new pixel selection combines with the existing one (Shift = add, Alt = subtract).</summary>
public enum SelectionCombine { Replace, Add, Subtract }

/// <summary>
/// Shared pixel-selection behaviour: dragging inside the selection lifts the pixels (live preview) and drops them on
/// release (Ctrl = copy); arrow keys move the pixels 1 px (Shift 10 px); Delete clears them (transparent or auto-fill).
/// </summary>
public abstract class PixelToolBase : ToolBase
{
    private bool _moving;
    private bool _copy;
    private Point2 _moveDown;
    private Point2 _moveCurrent;
    private BgraImage? _floating;
    private RectI _floatingRect;

    protected PixelToolBase(ToolKind kind) : base(kind) { }

    public override bool ShowsBusyCursor => true;

    protected bool IsMovingPixels => _moving;

    protected static SelectionCombine CombineFor(PointerInput e) => e.Alt ? SelectionCombine.Subtract : e.Shift ? SelectionCombine.Add : SelectionCombine.Replace;

    /// <summary>True when the press starts a pixel move (inside the current selection, no add/subtract modifier).</summary>
    protected bool TryBeginPixelMove(PointerInput e)
    {
        if (e.Shift || e.Alt || S.Selection.PixelMask is not { } mask || S.Selection.PixelBounds is not { } pb) return false;
        if (!mask.Contains((int)Math.Floor(e.Position.X), (int)Math.Floor(e.Position.Y))) return false;
        _moving = true;
        _copy = e.Control;
        _moveDown = _moveCurrent = e.Position;
        _floatingRect = pb.ToRectIOutward();
        var baseRaster = RasterOps.ComposeBase(S.Document, S.ActivePage);
        _floating = RasterOps.ExtractMasked(baseRaster, mask, _floatingRect);
        _floatingRect = _floatingRect.Intersect(baseRaster.Bounds);
        return true;
    }

    protected void UpdatePixelMove(PointerInput e)
    {
        _moveCurrent = e.Shift ? AxisLock(_moveDown, e.Position) : e.Position;
        S.Invalidate();
    }

    private static Point2 AxisLock(Point2 a, Point2 p) => Math.Abs(p.X - a.X) >= Math.Abs(p.Y - a.Y) ? new Point2(p.X, a.Y) : new Point2(a.X, p.Y);

    protected void EndPixelMove(PointerInput e)
    {
        UpdatePixelMove(e);
        _moving = false;
        _floating = null;
        var dx = (int)Math.Round(_moveCurrent.X - _moveDown.X);
        var dy = (int)Math.Round(_moveCurrent.Y - _moveDown.Y);
        if (dx != 0 || dy != 0 || _copy) _ = S.MovePixelsAsync(dx, dy, _copy);
        S.Invalidate();
    }

    protected void CancelPixelMove()
    {
        _moving = false;
        _floating = null;
        Session?.Invalidate();
    }

    /// <summary>Combines <paramref name="mask"/> with the current selection and applies it.</summary>
    protected void ApplySelection(Mask mask, SelectionCombine mode, PixelSelectionShape shape, IReadOnlyList<Point2>? outline)
    {
        var current = S.Selection.PixelMask;
        Mask result = mode switch
        {
            SelectionCombine.Add when current is not null => current.Union(mask),
            SelectionCombine.Subtract when current is not null => current.Subtract(mask),
            SelectionCombine.Subtract => new Mask(mask.Width, mask.Height),
            _ => mask,
        };
        S.SetPixelSelection(result, mode == SelectionCombine.Replace ? shape : PixelSelectionShape.Freehand, mode == SelectionCombine.Replace ? outline : null);
    }

    public override void KeyDown(KeyInput e)
    {
        if (S.Selection.HasPixelSelection && !e.Control && !e.Alt && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = e.Shift ? 10 : 1;
            var (dx, dy) = e.Key switch { VirtualKey.Left => (-step, 0), VirtualKey.Right => (step, 0), VirtualKey.Up => (0, -step), _ => (0, step) };
            _ = S.MovePixelsAsync(dx, dy, copy: false);
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape && _moving)
        {
            CancelPixelMove();
            e.Handled = true;
        }
    }

    protected IEnumerable<OverlayPrimitive> MoveOverlay()
    {
        if (!_moving || _floating is null) yield break;
        var dx = Math.Round(_moveCurrent.X - _moveDown.X);
        var dy = Math.Round(_moveCurrent.Y - _moveDown.Y);
        var dest = new Rect2(_floatingRect.X + dx, _floatingRect.Y + dy, _floating.Width, _floating.Height);
        if (!_copy) yield return new OverlayImageTint(_floatingRect.ToRect2(), new Argb(0x66FFFFFF), S.Selection.PixelMask);
        yield return new OverlayImage(_floating, dest, 0.9);
        yield return new OverlayRect(dest) { Style = OverlayStyle.Marquee };
    }

    protected CursorHint PixelCursor(PointerInput? hover)
    {
        if (_moving) return _copy ? CursorHint.HandGrab : CursorHint.Move;
        if (hover is { } h && S.Selection.PixelMask is { } m && m.Contains((int)Math.Floor(h.Position.X), (int)Math.Floor(h.Position.Y)) && !h.Shift && !h.Alt) return CursorHint.Move;
        return Kind == ToolKind.MagicWand ? CursorHint.MagicWand : CursorHint.Crosshair;
    }
}

/// <summary>
/// Selection tool: rectangle / ellipse / polygon (click vertices, double-click/Enter closes) / freehand pixel
/// selection; Shift adds, Alt subtracts; "Snap to object" snaps rectangle edges to object and canvas edges.
/// Move / copy / cut / delete via <see cref="PixelToolBase"/> and the session commands.
/// </summary>
public sealed class SelectionTool : PixelToolBase
{
    private bool _dragging;
    private Point2 _down;
    private Point2 _current;
    private readonly List<Point2> _points = new();
    private bool _polygonActive;
    private SelectionCombine _combine;
    private double _zoom = 1;
    private PointerInput? _hover;
    private IReadOnlyList<OverlayPrimitive> _guides = Array.Empty<OverlayPrimitive>();

    public SelectionTool() : base(ToolKind.Selection) { }

    private PixelSelectionShape Shape => S.ToolOptions.Selection.Shape;

    public override CursorHint Cursor => Session is null ? CursorHint.Crosshair : PixelCursor(_hover);

    public override void PointerDown(PointerInput e)
    {
        if (e.Button != PointerButton.Left) return;
        _zoom = e.EffectiveZoom;
        if (_polygonActive)
        {
            var p = e.Position;
            if (_points.Count >= 3 && p.DistanceTo(_points[0]) * _zoom <= SelectionHandles.ScreenHitRadius) { ClosePolygon(); return; }
            _points.Add(p);
            S.Invalidate();
            return;
        }
        if (TryBeginPixelMove(e)) return;
        _combine = CombineFor(e);
        _down = _current = SnapIfNeeded(e.Position);
        _points.Clear();
        if (Shape == PixelSelectionShape.Polygon)
        {
            _polygonActive = true;
            _points.Add(e.Position);
            S.Invalidate();
            return;
        }
        _dragging = true;
        if (Shape == PixelSelectionShape.Freehand) _points.Add(e.Position);
    }

    private Point2 SnapIfNeeded(Point2 p)
    {
        _guides = Array.Empty<OverlayPrimitive>();
        if (!S.ToolOptions.Selection.SnapToObject || Shape is not (PixelSelectionShape.Rectangle or PixelSelectionShape.Ellipse)) return p;
        var r = SnapEngine.SnapPoint(p, S.ActivePage.CanvasRect, S.ActivePage.AllAnnotations().Select(a => a.RotatedBounds).Concat(S.SmartComponents.Select(c => c.Bounds.ToRect2())), SelectionManipulator.SnapThreshold / _zoom);
        _guides = r.Guides.Select(g => (OverlayPrimitive)new OverlayLine(g.From, g.To) { Style = OverlayStyle.Guide }).ToList();
        return p.Offset(r.Dx, r.Dy);
    }

    public override void PointerMove(PointerInput e)
    {
        _zoom = e.EffectiveZoom;
        _hover = e;
        if (IsMovingPixels) { UpdatePixelMove(e); return; }
        if (_polygonActive) { _current = e.Position; S.Invalidate(); return; }
        if (!_dragging) return;
        if (Shape == PixelSelectionShape.Freehand)
        {
            if (e.Position.DistanceTo(_points[^1]) * _zoom >= 1) _points.Add(e.Position);
            _current = e.Position;
        }
        else
        {
            var p = SnapIfNeeded(e.Position);
            _current = e.Shift && _combine == SelectionCombine.Replace ? GeometryMath.ConstrainSquare(_down, p) : p;
        }
        S.Invalidate();
    }

    public override void PointerUp(PointerInput e)
    {
        if (IsMovingPixels) { EndPixelMove(e); return; }
        if (!_dragging) return;
        _dragging = false;
        _guides = Array.Empty<OverlayPrimitive>();
        if (_down.DistanceTo(e.Position) * _zoom < SelectionManipulator.DragThreshold)
        {
            if (_combine == SelectionCombine.Replace) S.Selection.ClearPixelSelection();
            S.Invalidate();
            return;
        }
        if (Shape == PixelSelectionShape.Freehand)
        {
            if (_points.Count < 3) return;
            var pts = _points.ToList();
            ApplySelection(S.CreateSelectionMask(PixelSelectionShape.Freehand, Rect2.FromPoints(pts), pts), _combine, PixelSelectionShape.Freehand, pts);
            _points.Clear();
            return;
        }
        var rect = Rect2.FromPoints(_down, _current).Intersect(S.ActivePage.CanvasRect);
        if (rect.Width < 1 || rect.Height < 1) return;
        var shape = Shape == PixelSelectionShape.Ellipse ? PixelSelectionShape.Ellipse : PixelSelectionShape.Rectangle;
        IReadOnlyList<Point2>? outline = shape == PixelSelectionShape.Ellipse ? EllipseOutline(rect) : null;
        ApplySelection(S.CreateSelectionMask(shape, rect), _combine, shape, outline);
    }

    private static IReadOnlyList<Point2> EllipseOutline(Rect2 r)
    {
        const int n = 64;
        var pts = new Point2[n];
        for (var i = 0; i < n; i++)
        {
            var t = i * 2 * Math.PI / n;
            pts[i] = new Point2(r.Center.X + Math.Cos(t) * r.Width / 2, r.Center.Y + Math.Sin(t) * r.Height / 2);
        }
        return pts;
    }

    public override void DoubleClick(PointerInput e)
    {
        if (_polygonActive) ClosePolygon();
    }

    public override void KeyDown(KeyInput e)
    {
        if (_polygonActive)
        {
            if (e.Key == VirtualKey.Enter) { ClosePolygon(); e.Handled = true; return; }
            if (e.Key == VirtualKey.Escape) { Cancel(); e.Handled = true; return; }
            if (e.Key == VirtualKey.Back && _points.Count > 1) { _points.RemoveAt(_points.Count - 1); S.Invalidate(); e.Handled = true; return; }
        }
        base.KeyDown(e);
    }

    /// <summary>Closes an in-progress polygon selection (≥ 3 vertices).</summary>
    public void ClosePolygon()
    {
        if (!_polygonActive) return;
        _polygonActive = false;
        var pts = _points.Distinct().ToList();
        _points.Clear();
        if (pts.Count >= 3 && GeometryMath.PolygonArea(pts) >= 1)
            ApplySelection(S.CreateSelectionMask(PixelSelectionShape.Polygon, Rect2.FromPoints(pts), pts), _combine, PixelSelectionShape.Polygon, pts);
        S.Invalidate();
    }

    public override void Cancel()
    {
        _dragging = false;
        _polygonActive = false;
        _points.Clear();
        _guides = Array.Empty<OverlayPrimitive>();
        CancelPixelMove();
    }

    public override void PointerLeave() => _hover = null;

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var list = new List<OverlayPrimitive>(MoveOverlay());
        list.AddRange(_guides);
        if (_polygonActive && _points.Count > 0)
        {
            var pts = _points.Concat(new[] { _current }).ToList();
            list.Add(new OverlayPath(pts, false) { Style = OverlayStyle.Marquee });
            list.Add(new OverlayHandle("vertex:0", _points[0], HandleKind.Vertex) { Style = OverlayStyle.Handle });
        }
        else if (_dragging)
        {
            if (Shape == PixelSelectionShape.Freehand && _points.Count > 1) list.Add(new OverlayPath(_points.ToList(), false) { Style = OverlayStyle.Marquee });
            else if (Shape == PixelSelectionShape.Ellipse) list.Add(new OverlayEllipse(Rect2.FromPoints(_down, _current)) { Style = OverlayStyle.Marquee });
            else list.Add(new OverlayRect(Rect2.FromPoints(_down, _current)) { Style = OverlayStyle.Marquee });
        }
        return list;
    }
}

/// <summary>
/// Magic Wand: click selects similar colours (tolerance; contiguous or global) via <c>IMagicWandService</c> (built-in
/// flood fill when the service is unavailable); Shift adds, Alt subtracts; then move/copy/delete like Selection.
/// </summary>
public sealed class MagicWandTool : PixelToolBase
{
    private PointerInput? _hover;
    private bool _pressedForMove;

    public MagicWandTool() : base(ToolKind.MagicWand) { }

    public override CursorHint Cursor => Session is null ? CursorHint.MagicWand : PixelCursor(_hover);

    public override void PointerDown(PointerInput e)
    {
        if (e.Button != PointerButton.Left) return;
        if (TryBeginPixelMove(e)) { _pressedForMove = true; return; }
        SelectAt(e.Position, CombineFor(e));
    }

    /// <summary>Runs the wand at <paramref name="p"/> and combines the result with the selection.</summary>
    public void SelectAt(Point2 p, SelectionCombine combine = SelectionCombine.Replace)
    {
        var size = RasterOps.CanvasPixels(S.ActivePage);
        var seed = new PointI((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        if ((uint)seed.X >= (uint)size.Width || (uint)seed.Y >= (uint)size.Height) return;
        var o = S.ToolOptions.MagicWand;
        var img = RasterOps.ComposeFlattenedBase(S.Document, S.ActivePage);
        var tol = Math.Clamp(o.Tolerance, 0, 100) / 100.0;
        Mask mask;
        if (S.Services.MagicWand is { } wand)
        {
            mask = wand.Select(img, seed, tol, contiguous: !o.GlobalColor);
            if (o.RefineRadius > 0) mask = wand.Refine(mask, o.RefineRadius);
        }
        else
        {
            mask = RasterOps.ColorRegion(img, seed, tol, contiguous: !o.GlobalColor);
        }
        ApplySelection(mask, combine, PixelSelectionShape.MagicWand, null);
    }

    public override void PointerMove(PointerInput e)
    {
        _hover = e;
        if (IsMovingPixels) UpdatePixelMove(e);
    }

    public override void PointerUp(PointerInput e)
    {
        if (_pressedForMove && IsMovingPixels) EndPixelMove(e);
        _pressedForMove = false;
    }

    public override void Cancel()
    {
        _pressedForMove = false;
        CancelPixelMove();
    }

    public override void PointerLeave() => _hover = null;

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom) => MoveOverlay().ToList();
}
