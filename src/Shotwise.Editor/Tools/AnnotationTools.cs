using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor.Tools;

/// <summary>Arrow / Line: drag start→end, Shift snaps the angle to 45° steps; Bezier styles get default control points.</summary>
public abstract class LineCreateTool : DragCreateTool
{
    protected LineCreateTool(ToolKind kind) : base(kind) { }

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        a.Start = anchor;
        a.End = e.Shift ? GeometryMath.SnapAngle(anchor, p) : p;
        var (c1, c2) = AnnotationGeometry.DefaultControls(a.Start, a.End);
        switch (a)
        {
            case ArrowAnnotation { Bezier: true } ar: ar.Control1 = c1; ar.Control2 = c2; break;
            case LineAnnotation { Bezier: true } ln: ln.Control1 = c1; ln.Control2 = c2; break;
            case ArrowAnnotation ar: ar.Control1 = ar.Control2 = null; break;
            case LineAnnotation ln: ln.Control1 = ln.Control2 = null; break;
        }
    }
}

public sealed class ArrowTool : LineCreateTool
{
    public ArrowTool() : base(ToolKind.Arrow) { }
    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<ArrowAnnotation>(ToolKind.Arrow);
}

public sealed class LineTool : LineCreateTool
{
    public LineTool() : base(ToolKind.Line) { }
    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<LineAnnotation>(ToolKind.Line);
}

/// <summary>Box-shaped objects drawn by dragging a rectangle (Shift = square, Alt = from centre).</summary>
public abstract class BoxCreateTool<T> : DragCreateTool where T : Annotation, new()
{
    protected BoxCreateTool(ToolKind kind) : base(kind) { }

    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<T>(Kind);

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        var r = DragRect(anchor, p, e);
        a.Start = r.TopLeft;
        a.End = r.BottomRight;
    }
}

/// <summary>
/// Shape: rectangle / rounded rectangle / ellipse by drag; polygon (style with <see cref="ShapeKind.Polygon"/>) by
/// clicking vertices — double-click, Enter or clicking the first vertex closes it, Backspace removes the last vertex,
/// Esc cancels.
/// </summary>
public sealed class ShapeTool : BoxCreateTool<ShapeAnnotation>
{
    private readonly List<Point2> _vertices = new();
    private ShapeAnnotation? _polygon;
    private Point2 _rubber;
    private double _zoom = 1;

    public ShapeTool() : base(ToolKind.Shape) { }

    private bool PolygonStyle => S.ActiveStyle(ToolKind.Shape)?.Prototype is ShapeAnnotation { Shape: ShapeKind.Polygon };

    /// <summary>Vertices placed so far for an in-progress polygon.</summary>
    public IReadOnlyList<Point2> PolygonVertices => _vertices;

    protected override bool BeginCreate(PointerInput e)
    {
        if (!PolygonStyle) return base.BeginCreate(e);
        _zoom = e.EffectiveZoom;
        _vertices.Clear();
        var p = Snap(e.Position, _zoom);
        _vertices.Add(p);
        _rubber = p;
        _polygon = S.CreateFromStyle<ShapeAnnotation>(ToolKind.Shape);
        _polygon.Shape = ShapeKind.Polygon;
        UpdatePolygonPreview(insert: true);
        S.Selection.ClearObjects();
        return true;
    }

    protected override void ContinueCreate(PointerInput e)
    {
        if (_polygon is null) return;
        _zoom = e.EffectiveZoom;
        var p = Snap(e.Position, _zoom, _polygon.Id);
        if (_vertices.Count >= 3 && p.DistanceTo(_vertices[0]) * _zoom <= Internal.SelectionHandles.ScreenHitRadius)
        {
            ClosePolygon();
            return;
        }
        if (_vertices.Count > 0 && p.DistanceTo(_vertices[^1]) * _zoom < 1) return;
        _vertices.Add(p);
        _rubber = p;
        UpdatePolygonPreview(insert: false);
    }

    protected override void UpdateCreate(PointerInput e)
    {
        if (_polygon is null) { base.UpdateCreate(e); return; }
        _zoom = e.EffectiveZoom;
        _rubber = e.Shift && _vertices.Count > 0 ? GeometryMath.SnapAngle(_vertices[^1], e.Position) : Snap(e.Position, _zoom, _polygon.Id);
        UpdatePolygonPreview(insert: false);
    }

    protected override bool EndCreate(PointerInput e) => _polygon is null ? base.EndCreate(e) : false;

    public override void DoubleClick(PointerInput e)
    {
        if (_polygon is not null) { ClosePolygon(); return; }
        base.DoubleClick(e);
    }

    public override void KeyDown(KeyInput e)
    {
        if (_polygon is not null)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter: ClosePolygon(); e.Handled = true; return;
                case VirtualKey.Back when _vertices.Count > 1:
                    _vertices.RemoveAt(_vertices.Count - 1);
                    UpdatePolygonPreview(insert: false);
                    e.Handled = true;
                    return;
            }
        }
        base.KeyDown(e);
    }

    private void UpdatePolygonPreview(bool insert)
    {
        if (_polygon is null) return;
        var pts = new List<Point2>(_vertices);
        if (pts.Count == 0 || pts[^1] != _rubber) pts.Add(_rubber);
        var c = (ShapeAnnotation)_polygon.DeepClone();
        c.Points = pts;
        var b = Rect2.FromPoints(pts);
        c.Start = b.TopLeft;
        c.End = b.BottomRight;
        if (insert) S.ActivePage.Annotations.Add(c);
        else LiveEdit.Replace(S.ActivePage, c);
        _polygon = c;
        S.Invalidate();
    }

    /// <summary>Finishes the polygon (≥ 3 distinct vertices) as one undo step.</summary>
    public void ClosePolygon()
    {
        if (_polygon is null) return;
        var poly = _polygon;
        var pts = _vertices.Distinct().ToList();
        LiveEdit.Remove(S.ActivePage, poly.Id);
        _polygon = null;
        _vertices.Clear();
        FinishCreating();
        if (pts.Count < 3 || GeometryMath.PolygonArea(pts) < 1) { S.Invalidate(); return; }
        poly.Points = pts;
        var b = Rect2.FromPoints(pts);
        poly.Start = b.TopLeft;
        poly.End = b.BottomRight;
        Commit(poly, "Add polygon");
    }

    protected override void CancelCreate()
    {
        if (_polygon is not null)
        {
            LiveEdit.Remove(S.ActivePage, _polygon.Id);
            _polygon = null;
            _vertices.Clear();
            S.Invalidate();
            return;
        }
        base.CancelCreate();
    }

    protected override IEnumerable<OverlayPrimitive> CreateOverlay(double zoom)
    {
        if (_polygon is null) return base.CreateOverlay(zoom);
        var list = new List<OverlayPrimitive>(Guides);
        foreach (var (v, i) in _vertices.Select((v, i) => (v, i)))
            list.Add(new OverlayHandle($"vertex:{i}", v, HandleKind.Vertex) { Style = OverlayStyle.Handle });
        return list;
    }
}

/// <summary>Callout: drag the box (click = default size); a default tail is added for presets that allow tails, then text editing starts.</summary>
public sealed class CalloutTool : DragCreateTool
{
    public static readonly Size2 DefaultSize = new(220, 110);

    private Rect2 _protoBounds;
    private List<CalloutTail> _protoTails = new();

    public CalloutTool() : base(ToolKind.Callout) { }

    protected override CursorHint DrawCursor => CursorHint.Crosshair;
    protected override bool CreatesOnClick => true;

    protected override Annotation CreateNew(Point2 anchor)
    {
        var c = S.CreateFromStyle<CalloutAnnotation>(ToolKind.Callout);
        _protoBounds = c.Bounds;
        _protoTails = new List<CalloutTail>(c.Tails);
        return c;
    }

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e) => Place((CalloutAnnotation)a, DragRect(anchor, p, e));

    protected override void ApplyClick(Annotation a, Point2 anchor) => Place((CalloutAnnotation)a, new Rect2(anchor.X, anchor.Y, DefaultSize.Width, DefaultSize.Height));

    private void Place(CalloutAnnotation c, Rect2 box)
    {
        c.Start = box.TopLeft;
        c.End = box.BottomRight;
        c.Tails.Clear();
        if (_protoTails.Count > 0 && _protoBounds.Width > 0 && _protoBounds.Height > 0)
        {
            // style tails keep their position relative to the box
            var sx = box.Width / _protoBounds.Width;
            var sy = box.Height / _protoBounds.Height;
            foreach (var t in _protoTails)
                c.Tails.Add(t with { Tip = new Point2(box.X + (t.Tip.X - _protoBounds.X) * sx, box.Y + (t.Tip.Y - _protoBounds.Y) * sy) });
        }
        else if (CalloutPresets.AllowsTails(c.ShapePresetId))
        {
            c.Tails.Add(new CalloutTail(new Point2(box.X + box.Width * 0.2, box.Bottom + Math.Max(30, box.Height * 0.6)), Math.Clamp(box.Width * 0.15, 8, 40)));
        }
    }

    protected override void Finish(Annotation a) => S.BeginNewTextEdit(a);

    protected override void OnObjectClicked(Guid id)
    {
        if (S.ActivePage.Find(id) is CalloutAnnotation) S.BeginEditText(id);
    }

    public override void DoubleClick(PointerInput e)
    {
        if (S.HitTest(e.Position, e.EffectiveZoom) is CalloutAnnotation c && !c.IsLocked) S.BeginEditText(c.Id);
    }

    /// <summary>Adds a tail to a callout (context menu); tip defaults below the box.</summary>
    public static void AddTail(EditorSession s, Guid calloutId)
    {
        if (s.ActivePage.Find(calloutId) is not CalloutAnnotation c) return;
        var b = c.Bounds;
        s.ModifyAnnotations(new[] { calloutId }, a => ((CalloutAnnotation)a).Tails.Add(new CalloutTail(new Point2(b.Center.X, b.Bottom + Math.Max(30, b.Height * 0.6)), Math.Clamp(b.Width * 0.15, 8, 40))), "Add tail");
    }

    /// <summary>Removes a callout tail by index.</summary>
    public static void RemoveTail(EditorSession s, Guid calloutId, int index)
    {
        if (s.ActivePage.Find(calloutId) is not CalloutAnnotation c || index < 0 || index >= c.Tails.Count) return;
        s.ModifyAnnotations(new[] { calloutId }, a => ((CalloutAnnotation)a).Tails.RemoveAt(index), "Remove tail");
    }
}

/// <summary>Text: click places a box that grows while typing, drag sets the box width; clicking an existing text box edits it.</summary>
public sealed class TextTool : DragCreateTool
{
    public TextTool() : base(ToolKind.Text) { }

    protected override CursorHint DrawCursor => CursorHint.IBeam;
    protected override bool CreatesOnClick => true;

    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<TextAnnotation>(ToolKind.Text);

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        var r = Rect2.FromPoints(anchor, p);
        a.Start = r.TopLeft;
        a.End = r.BottomRight;
    }

    protected override void ApplyClick(Annotation a, Point2 anchor)
    {
        var t = (TextAnnotation)a;
        var font = t.Text.Runs.FirstOrDefault()?.FontSize ?? new TextRun().FontSize;
        var w = Math.Max(60, font * 10) + t.TextStyle.Padding.Horizontal;
        var h = font * 1.35 * t.TextStyle.LineSpacing + t.TextStyle.Padding.Vertical;
        t.Start = anchor;
        t.End = new Point2(anchor.X + w, anchor.Y + h);
    }

    protected override void Finish(Annotation a)
    {
        var t = (TextAnnotation)a;
        if (t.Text.Runs.Count == 0) t.Text = new RichText(new[] { new TextRun() });
        else t.Text = new RichText(new[] { t.Text.Runs[0] with { Text = "" } });
        S.BeginNewTextEdit(t);
    }

    protected override void OnObjectClicked(Guid id)
    {
        if (S.ActivePage.Find(id) is TextAnnotation) S.BeginEditText(id);
    }

    public override void DoubleClick(PointerInput e)
    {
        if (S.HitTest(e.Position, e.EffectiveZoom) is TextAnnotation t && !t.IsLocked) S.BeginEditText(t.Id);
    }
}

/// <summary>Stamp: click places the current stamp at its natural size (centred), drag sets the size (aspect kept unless Shift).</summary>
public sealed class StampTool : DragCreateTool
{
    private Shotwise.Core.Documents.AssetId _asset;

    public StampTool() : base(ToolKind.Stamp) { }

    protected override CursorHint DrawCursor => S.ToolOptions.Stamp.HasStamp ? CursorHint.Stamp : CursorHint.NotAllowed;
    protected override bool CreatesOnClick => true;

    protected override bool BeginCreate(PointerInput e)
    {
        var o = S.ToolOptions.Stamp;
        if (!o.HasStamp)
        {
            S.Report(new EditorMessage(EditorMessageKind.Warning, Shotwise.Core.ErrorCode.InvalidArgument, "Choose a stamp first."));
            return false;
        }
        _asset = S.Document.Assets.Add(o.Data!, o.MediaType!);
        return base.BeginCreate(e);
    }

    protected override Annotation CreateNew(Point2 anchor)
    {
        var s = S.CreateFromStyle<StampAnnotation>(ToolKind.Stamp);
        s.Asset = _asset;
        s.StampId = S.ToolOptions.Stamp.StampId;
        return s;
    }

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        var n = S.ToolOptions.Stamp.NaturalSize;
        var keep = ((StampAnnotation)a).PreserveAspect && !e.Shift && n.Width > 0 && n.Height > 0;
        var r = keep ? Rect2.FromPoints(anchor, GeometryMath.ConstrainAspect(anchor, p, n.Width / n.Height)) : Rect2.FromPoints(anchor, p);
        a.Start = r.TopLeft;
        a.End = r.BottomRight;
    }

    protected override void ApplyClick(Annotation a, Point2 anchor)
    {
        var n = S.ToolOptions.Stamp.NaturalSize;
        var w = n.Width > 0 ? n.Width : 64;
        var h = n.Height > 0 ? n.Height : 64;
        a.Start = new Point2(anchor.X - w / 2, anchor.Y - h / 2);
        a.End = new Point2(anchor.X + w / 2, anchor.Y + h / 2);
    }

    protected override void Finish(Annotation a) => Commit(a, "Add stamp", new[] { _asset });
}

/// <summary>
/// Step: click places the next badge (value from the page counter); with a pointer style, dragging aims the pointer
/// tail (badge at the press point, tip at the release point).
/// </summary>
public sealed class StepTool : DragCreateTool
{
    public const double DefaultSize = 36;

    private double _size = DefaultSize;

    public StepTool() : base(ToolKind.Step) { }

    protected override bool CreatesOnClick => true;

    protected override Annotation CreateNew(Point2 anchor)
    {
        var s = S.CreateFromStyle<StepAnnotation>(ToolKind.Step);
        var b = Rect2.FromPoints(s.Start, s.End);
        _size = b.Width >= 8 ? Math.Max(b.Width, b.Height) : DefaultSize;
        s.Value = S.NextStepValue(s.Sequence);
        s.PointerTip = null;
        return s;
    }

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        var s = (StepAnnotation)a;
        s.Start = new Point2(anchor.X - _size / 2, anchor.Y - _size / 2);
        s.End = new Point2(anchor.X + _size / 2, anchor.Y + _size / 2);
        s.PointerTip = s.Style is StepStyle.CirclePointer or StepStyle.SquarePointer && p.DistanceTo(anchor) > _size / 2 ? p : null;
    }

    protected override void ApplyClick(Annotation a, Point2 anchor)
    {
        ApplyDrag(a, anchor, anchor, default);
        var s = (StepAnnotation)a;
        if (s.Style is StepStyle.CirclePointer or StepStyle.SquarePointer) s.PointerTip = SelectionHandles.DefaultPointerTip(s);
    }

    protected override void Finish(Annotation a) => S.PlaceStep((StepAnnotation)a, S.ExpandCanvasCommandFor(new[] { a }));

    protected override IEnumerable<OverlayPrimitive> CreateOverlay(double zoom) => Guides;
}

/// <summary>Pen: freehand stroke (Shift = straight line); smoothing reduces points; "create as vector" off flattens the stroke.</summary>
public sealed class PenTool : FreehandToolBase<PenAnnotation>
{
    public PenTool() : base(ToolKind.Pen) { }
    protected override CursorHint DrawCursor => CursorHint.Pen;
    protected override List<Point2> PointsOf(PenAnnotation a) => a.Points;
    protected override void SetPoints(PenAnnotation a, List<Point2> pts) => a.Points = pts;
    protected override bool Smooth(PenAnnotation a) => a.Smoothing;

    protected override void Complete(Annotation a)
    {
        if (S.ToolOptions.Pen.CreateAsVector)
        {
            Commit(a, "Pen");
            return;
        }
        var doc = S.Document;
        var renderer = S.Services.Renderer;
        var only = new HashSet<Guid> { a.Id };
        Commit(a, "Pen", extra: new FlattenAnnotationsCommand(S.ActivePage.Id, only, page => renderer.RenderPage(doc, page, new Shotwise.Core.Documents.RenderOptions { Only = only }), "Pen"));
        S.Selection.ClearObjects();
    }
}

/// <summary>Highlighter: rectangle (default) or freehand stroke (style with points / <see cref="HighlighterToolOptions.Freehand"/>).</summary>
public sealed class HighlighterTool : FreehandToolBase<HighlightAnnotation>
{
    public HighlighterTool() : base(ToolKind.Highlighter) { }

    protected override CursorHint DrawCursor => CursorHint.Highlighter;

    protected override bool IsFreehand(HighlightAnnotation a) => S.ToolOptions.Highlighter.Freehand || a.Points is not null;
    protected override List<Point2> PointsOf(HighlightAnnotation a) => a.Points ??= new List<Point2>();
    protected override void SetPoints(HighlightAnnotation a, List<Point2> pts) => a.Points = pts;
    protected override bool Smooth(HighlightAnnotation a) => true;
}

/// <summary>Shared freehand drawing (pen, freehand highlighter); rectangle mode when <see cref="IsFreehand"/> is false.</summary>
public abstract class FreehandToolBase<T> : DragCreateTool where T : Annotation, new()
{
    protected FreehandToolBase(ToolKind kind) : base(kind) { }

    protected virtual bool IsFreehand(T a) => true;
    protected abstract List<Point2> PointsOf(T a);
    protected abstract void SetPoints(T a, List<Point2> pts);
    protected abstract bool Smooth(T a);

    private readonly List<Point2> _raw = new();

    protected override Annotation CreateNew(Point2 anchor)
    {
        _raw.Clear();
        var a = S.CreateFromStyle<T>(Kind);
        if (IsFreehand(a)) SetPoints(a, new List<Point2>());
        return a;
    }

    protected override void ApplyDrag(Annotation annotation, Point2 anchor, Point2 p, PointerInput e)
    {
        var a = (T)annotation;
        if (!IsFreehand(a))
        {
            var r = DragRect(anchor, p, e);
            a.Start = r.TopLeft;
            a.End = r.BottomRight;
            return;
        }
        if (_raw.Count == 0) _raw.Add(anchor);
        if (p.DistanceTo(_raw[^1]) * e.EffectiveZoom >= 0.75) _raw.Add(p);
        var pts = e.Shift ? new List<Point2> { _raw[0], GeometryMath.SnapAngle(_raw[0], p) } : new List<Point2>(_raw);
        SetPoints(a, pts);
        var b = Rect2.FromPoints(pts);
        a.Start = b.TopLeft;
        a.End = b.BottomRight;
    }

    protected override bool CreatesOnClick => true;

    protected override void ApplyClick(Annotation annotation, Point2 anchor)
    {
        var a = (T)annotation;
        if (!IsFreehand(a)) return; // rectangle highlighter: click without drag does nothing useful; Finish skips empty boxes
        SetPoints(a, new List<Point2> { anchor, anchor.Offset(0.01, 0) });
        a.Start = anchor;
        a.End = anchor;
    }

    protected override bool EndCreate(PointerInput e)
    {
        var done = base.EndCreate(e);
        _raw.Clear();
        return done;
    }

    protected sealed override void Finish(Annotation annotation)
    {
        var a = (T)annotation;
        if (IsFreehand(a))
        {
            var pts = PointsOf(a);
            if (Smooth(a) && pts.Count > 2)
            {
                var tol = Kind == ToolKind.Pen ? S.ToolOptions.Pen.SimplifyTolerance : 0.75;
                SetPoints(a, GeometryMath.Simplify(pts, tol).ToList());
            }
        }
        else if (a.Bounds.Width < 1 || a.Bounds.Height < 1)
        {
            S.Invalidate();
            return;
        }
        Complete(a);
    }

    /// <summary>Adds the finished stroke (default: one undo step).</summary>
    protected virtual void Complete(Annotation a) => Commit(a);
}

/// <summary>Magnify: drag the lens; the source area starts under the lens (size = lens / scale) and is moved with its handle.</summary>
public sealed class MagnifyTool : DragCreateTool
{
    public static readonly Size2 DefaultSize = new(140, 140);

    public MagnifyTool() : base(ToolKind.Magnify) { }

    protected override bool CreatesOnClick => true;

    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<MagnifyAnnotation>(ToolKind.Magnify);

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e) => Place((MagnifyAnnotation)a, DragRect(anchor, p, e));

    protected override void ApplyClick(Annotation a, Point2 anchor)
        => Place((MagnifyAnnotation)a, new Rect2(anchor.X - DefaultSize.Width / 2, anchor.Y - DefaultSize.Height / 2, DefaultSize.Width, DefaultSize.Height));

    private static void Place(MagnifyAnnotation m, Rect2 lens)
    {
        m.Start = lens.TopLeft;
        m.End = lens.BottomRight;
        var s = m.Scale > 0 ? m.Scale : 2;
        var w = lens.Width / s;
        var h = lens.Height / s;
        m.SourceRect = new Rect2(lens.Center.X - w / 2, lens.Center.Y - h / 2, w, h);
    }

    protected override void Finish(Annotation a)
    {
        if (a.Bounds.Width < 2 || a.Bounds.Height < 2) return;
        Commit(a, "Magnify");
    }
}

/// <summary>Blur / pixelate / solid region (shape from the style); Shift = square.</summary>
public sealed class BlurTool : BoxCreateTool<BlurAnnotation>
{
    public BlurTool() : base(ToolKind.Blur) { }

    protected override void Finish(Annotation a)
    {
        if (a.Bounds.Width < 1 || a.Bounds.Height < 1) return;
        Commit(a, "Blur");
    }
}

/// <summary>Cursor: click inserts a cursor glyph (hotspot at the click); drag sizes it.</summary>
public sealed class CursorTool : DragCreateTool
{
    public const double BaseSize = 32;

    public CursorTool() : base(ToolKind.Cursor) { }

    protected override bool CreatesOnClick => true;

    protected override Annotation CreateNew(Point2 anchor) => S.CreateFromStyle<CursorAnnotation>(ToolKind.Cursor);

    protected override void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e)
    {
        var r = Rect2.FromPoints(anchor, GeometryMath.ConstrainSquare(anchor, p));
        a.Start = r.TopLeft;
        a.End = r.BottomRight;
    }

    protected override void ApplyClick(Annotation a, Point2 anchor)
    {
        var c = (CursorAnnotation)a;
        var size = BaseSize * (c.Scale > 0 ? c.Scale : 1);
        c.Start = anchor;
        c.End = new Point2(anchor.X + size, anchor.Y + size);
    }
}

/// <summary>
/// Simplify (manual): Draw mode drags simplified shapes (kind / palette colour / placeholder from
/// <see cref="SimplifyToolOptions"/>); Move mode selects and moves simplify shapes only (marquee included).
/// Auto Simplify is driven by <see cref="EditorSession.AutoSimplifyAsync"/>.
/// </summary>
public sealed class SimplifyTool : BoxCreateTool<SimplifyShapeAnnotation>
{
    private bool _moveGesture;

    public SimplifyTool() : base(ToolKind.Simplify) { }

    private bool MoveMode => S.ToolOptions.Simplify.Mode == SimplifyToolMode.Move;

    protected override bool CanManipulate(Annotation a) => a is SimplifyShapeAnnotation || S.Selection.Contains(a.Id);

    public override void PointerDown(PointerInput e)
    {
        if (MoveMode && e.Button == PointerButton.Left)
        {
            _moveGesture = Manipulator.TryBegin(e, allowMarquee: true, a => a is SimplifyShapeAnnotation);
            return;
        }
        base.PointerDown(e);
    }

    public override void PointerMove(PointerInput e)
    {
        if (_moveGesture) { Manipulator.Move(e); return; }
        base.PointerMove(e);
    }

    public override void PointerUp(PointerInput e)
    {
        if (_moveGesture) { _moveGesture = false; Manipulator.End(e); return; }
        base.PointerUp(e);
    }

    public override void Cancel()
    {
        if (_moveGesture) { _moveGesture = false; Manipulator.Cancel(); }
        base.Cancel();
    }

    protected override Annotation CreateNew(Point2 anchor)
    {
        var o = S.ToolOptions.Simplify;
        var a = S.CreateFromStyle<SimplifyShapeAnnotation>(ToolKind.Simplify);
        a.Shape = o.Shape;
        a.PaletteIndex = o.PaletteIndex;
        a.PlaceholderId = o.Shape == SimplifyShapeKind.Placeholder ? o.PlaceholderId : null;
        a.Fill = FillStyle.Solid(S.SimplifyColor(o.PaletteIndex));
        a.Opacity = Math.Clamp(S.ActivePage.Simplify.Opacity, 0, 1);
        return a;
    }

    protected override void Finish(Annotation a)
    {
        if (a.Bounds.Width < 1 || a.Bounds.Height < 1) return;
        Commit(a, "Simplify shape");
    }
}
