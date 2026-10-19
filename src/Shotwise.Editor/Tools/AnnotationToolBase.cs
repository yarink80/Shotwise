using System.Globalization;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor.Tools;

/// <summary>
/// Base of the drawing tools. Pressing on a handle of the selection, on a selected object, or on an object of the
/// tool's own kind manipulates it (move/resize/…, like the Select tool); anywhere else (or with Alt) draws a new object.
/// </summary>
public abstract class AnnotationToolBase : ToolBase
{
    private SelectionManipulator? _manip;
    private bool _manipulating;
    private bool _creating;

    protected AnnotationToolBase(ToolKind kind) : base(kind) => DrawnKind = EditorSession.AnnotationKindFor(kind);

    /// <summary>Kind of annotation this tool creates.</summary>
    protected AnnotationKind? DrawnKind { get; }

    internal SelectionManipulator Manipulator => _manip ?? throw new InvalidOperationException("Tool is not active.");

    /// <summary>True while a creation gesture is in progress.</summary>
    protected bool IsCreating => _creating;

    public override bool ShowsObjectHandles => true;

    /// <summary>Cursor when not over a manipulable object.</summary>
    protected virtual CursorHint DrawCursor => CursorHint.Crosshair;

    public override CursorHint Cursor => _creating ? DrawCursor : _manip?.Cursor ?? DrawCursor;

    public override void Activate(EditorSession s)
    {
        base.Activate(s);
        _manip = new SelectionManipulator(s) { Clicked = OnObjectClicked };
    }

    public override void Deactivate()
    {
        Cancel();
        _manip = null;
        base.Deactivate();
    }

    /// <summary>Objects the tool may grab instead of drawing.</summary>
    protected virtual bool CanManipulate(Annotation a) => S.Selection.Contains(a.Id) || (DrawnKind is { } k && a.Kind == k);

    /// <summary>Click (no drag) on a manipulable object.</summary>
    protected virtual void OnObjectClicked(Guid id) { }

    public override void PointerDown(PointerInput e)
    {
        if (e.Button == PointerButton.Right)
        {
            SelectTool.SelectUnderPointerForContextMenu(S, e);
            return;
        }
        if (e.Button != PointerButton.Left) return;
        if (_creating) { ContinueCreate(e); return; }
        if (!e.Alt && Manipulator.TryBegin(e, allowMarquee: false, CanManipulate))
        {
            _manipulating = true;
            return;
        }
        _manip?.ClearHover();
        _creating = BeginCreate(e);
    }

    public override void PointerMove(PointerInput e)
    {
        if (_manipulating) Manipulator.Move(e);
        else if (_creating) UpdateCreate(e);
        else Manipulator.Hover(e, CanManipulate);
    }

    public override void PointerUp(PointerInput e)
    {
        if (_manipulating)
        {
            _manipulating = false;
            Manipulator.End(e);
            return;
        }
        if (_creating && EndCreate(e)) _creating = false;
    }

    public override void PointerLeave() => _manip?.ClearHover();

    public override void KeyDown(KeyInput e)
    {
        if (e.Key == VirtualKey.Escape && (_manipulating || _creating))
        {
            Cancel();
            e.Handled = true;
        }
    }

    public override void Cancel()
    {
        if (_manipulating) { _manipulating = false; _manip?.Cancel(); }
        if (_creating) { _creating = false; CancelCreate(); }
    }

    /// <summary>Ends a multi-click creation (polygon) from outside the pointer flow.</summary>
    protected void FinishCreating() => _creating = false;

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var list = new List<OverlayPrimitive>();
        if (_manip is not null) list.AddRange(_manip.Overlay(zoom));
        if (_creating) list.AddRange(CreateOverlay(zoom));
        return list;
    }

    /// <summary>Starts creating; return false to ignore the press.</summary>
    protected abstract bool BeginCreate(PointerInput e);
    protected abstract void UpdateCreate(PointerInput e);
    /// <summary>Pointer released while creating; return true when creation finished (false for multi-click shapes).</summary>
    protected abstract bool EndCreate(PointerInput e);
    protected abstract void CancelCreate();
    /// <summary>Additional press while creating (multi-click shapes).</summary>
    protected virtual void ContinueCreate(PointerInput e) { }
    protected virtual IEnumerable<OverlayPrimitive> CreateOverlay(double zoom) => Array.Empty<OverlayPrimitive>();

    // ------------------------------------------------------------------ helpers for subclasses

    /// <summary>Snaps <paramref name="p"/> to canvas/object edges when snapping is on; records guides for the overlay.</summary>
    protected Point2 Snap(Point2 p, double zoom, Guid? exclude = null)
    {
        Guides = Array.Empty<OverlayPrimitive>();
        if (!S.SnappingEnabled) return p;
        var others = S.ActivePage.AllAnnotations().Where(a => a.Id != exclude).Select(a => a.RotatedBounds);
        var r = SnapEngine.SnapPoint(p, S.ActivePage.CanvasRect, others, SelectionManipulator.SnapThreshold / (zoom > 0 ? zoom : 1));
        Guides = r.Guides.Select(g => (OverlayPrimitive)new OverlayLine(g.From, g.To) { Style = OverlayStyle.Guide }).ToList();
        return p.Offset(r.Dx, r.Dy);
    }

    /// <summary>Snap guides from the last <see cref="Snap"/> call.</summary>
    protected IReadOnlyList<OverlayPrimitive> Guides { get; private set; } = Array.Empty<OverlayPrimitive>();

    /// <summary>Adds the finished annotation (one undo step, with canvas auto-expand) and selects it.</summary>
    protected void Commit(Annotation a, string? label = null, IEnumerable<Shotwise.Core.Documents.AssetId>? assets = null, IEditCommand? extra = null)
    {
        IEditCommand add = new AddAnnotationCommand(S.ActivePage.Id, a, label: label);
        if (assets is not null) add = new AssetHoldingCommand(add, assets);
        var cmds = new List<IEditCommand> { add };
        if (extra is not null) cmds.Add(extra);
        if (S.ExpandCanvasCommandFor(new[] { a }) is { } expand) cmds.Add(expand);
        S.ExecuteComposite(add.Label, cmds);
        S.Selection.Select(a.Id);
    }

    /// <summary>"W × H" readout next to the pointer.</summary>
    protected static OverlayText SizeReadout(Point2 at, Rect2 r, double zoom)
        => new(new Point2(at.X + 12 / zoom, at.Y + 12 / zoom), string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Abs(r.Width))} × {Math.Round(Math.Abs(r.Height))}"));
}

/// <summary>
/// Drag-to-create tools: the object is previewed live in the document while dragging and added with one command on
/// release. Clicks without a drag either create a default-size object (<see cref="CreatesOnClick"/>) or are ignored.
/// </summary>
public abstract class DragCreateTool : AnnotationToolBase
{
    private Point2 _anchor;
    private Point2 _current;
    private bool _dragged;
    private double _zoom = 1;

    protected DragCreateTool(ToolKind kind) : base(kind) { }

    /// <summary>Object being created (live in the page while dragging).</summary>
    protected Annotation? Preview { get; private set; }
    protected Point2 Anchor => _anchor;

    /// <summary>When true a click without drag creates a default object via <see cref="ApplyClick"/>.</summary>
    protected virtual bool CreatesOnClick => false;

    /// <summary>Creates the new object from the tool style (geometry set by <see cref="ApplyDrag"/>).</summary>
    protected abstract Annotation CreateNew(Point2 anchor);

    /// <summary>Sets the preview geometry for a drag from <paramref name="anchor"/> to <paramref name="p"/>.</summary>
    protected abstract void ApplyDrag(Annotation a, Point2 anchor, Point2 p, PointerInput e);

    /// <summary>Default geometry for a click at <paramref name="anchor"/>.</summary>
    protected virtual void ApplyClick(Annotation a, Point2 anchor) { }

    /// <summary>Adds the finished object (default: <see cref="AnnotationToolBase.Commit"/>).</summary>
    protected virtual void Finish(Annotation a) => Commit(a);

    protected override bool BeginCreate(PointerInput e)
    {
        _zoom = e.EffectiveZoom;
        _anchor = _current = Snap(e.Position, _zoom);
        _dragged = false;
        var a = CreateNew(_anchor);
        ApplyDrag(a, _anchor, _anchor, e);
        Preview = a;
        S.Selection.ClearObjects();
        S.ActivePage.Annotations.Add(a);
        S.Invalidate();
        return true;
    }

    protected override void UpdateCreate(PointerInput e)
    {
        if (Preview is null) return;
        _zoom = e.EffectiveZoom;
        _current = Snap(e.Position, _zoom, Preview.Id);
        if (!_dragged && _anchor.DistanceTo(e.Position) * _zoom >= SelectionManipulator.DragThreshold) _dragged = true;
        var c = Preview.DeepClone();
        ApplyDrag(c, _anchor, _current, e);
        LiveEdit.Replace(S.ActivePage, c);
        Preview = c;
        S.Invalidate();
    }

    protected override bool EndCreate(PointerInput e)
    {
        if (Preview is null) return true;
        if (e.Position != _current) UpdateCreate(e);
        var a = Preview;
        Preview = null;
        LiveEdit.Remove(S.ActivePage, a.Id);
        if (!_dragged)
        {
            if (!CreatesOnClick) { S.Invalidate(); return true; }
            ApplyClick(a, _anchor);
        }
        Finish(a);
        return true;
    }

    protected override void CancelCreate()
    {
        if (Preview is null) return;
        LiveEdit.Remove(S.ActivePage, Preview.Id);
        Preview = null;
        S.Invalidate();
    }

    protected override IEnumerable<OverlayPrimitive> CreateOverlay(double zoom)
    {
        var list = new List<OverlayPrimitive>(Guides);
        if (Preview is not null && _dragged && !AnnotationGeometry.IsLineLike(Preview))
            list.Add(SizeReadout(_current, Rect2.FromPoints(_anchor, _current), zoom));
        return list;
    }

    /// <summary>Rectangle from a drag: Shift = square, Alt = from centre.</summary>
    protected static Rect2 DragRect(Point2 anchor, Point2 p, PointerInput e, double aspect = 0)
    {
        var q = aspect > 0 ? GeometryMath.ConstrainAspect(anchor, p, aspect) : e.Shift ? GeometryMath.ConstrainSquare(anchor, p) : p;
        if (e.Alt) return Rect2.FromPoints(new Point2(2 * anchor.X - q.X, 2 * anchor.Y - q.Y), q);
        return Rect2.FromPoints(anchor, q);
    }
}
