using System.Globalization;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Editor.Internal;

namespace Shotwise.Editor.Tools;

/// <summary>
/// Object manipulation shared by the Select/Move tools and every annotation tool: click / Ctrl|Shift multi-select
/// (groups expand), marquee, move with Shift axis-lock and snapping guides, resize with handles (rotation-aware, Shift =
/// keep aspect), rotate (Shift = 15° steps), endpoint / Bezier control / callout tail / step pointer / polygon vertex /
/// magnifier source handles, drop-zone content panning. The gesture is previewed live and committed as one
/// <see cref="ModifyAnnotationsCommand"/> (plus canvas auto-expand) on release.
/// </summary>
internal sealed class SelectionManipulator
{
    private enum Mode { None, Pending, Move, Resize, Rotate, Handle, Marquee, PanZone }

    /// <summary>Drag threshold in screen pixels.</summary>
    public const double DragThreshold = 3;
    /// <summary>Snap distance in screen pixels.</summary>
    public const double SnapThreshold = 6;

    private readonly EditorSession _s;
    private Mode _mode;
    private Point2 _down;
    private Point2 _current;
    private double _zoom = 1;
    private List<Annotation> _before = new();
    private Rect2 _frame;
    private double _frameRotation;
    private HandleInfo? _handle;
    private Rect2 _marquee;
    private bool _additive;
    private IReadOnlySet<Guid> _marqueeBase = new HashSet<Guid>();
    private IReadOnlyList<SnapGuide> _guides = Array.Empty<SnapGuide>();
    private Guid? _clicked;
    private bool _clickedWasSelected;
    private bool _toggleOnly;
    private Guid? _hoverId;
    private HandleInfo? _hoverHandle;
    private Func<Annotation, bool> _filter = _ => true;

    public SelectionManipulator(EditorSession session) => _s = session;

    public bool IsActive => _mode != Mode.None;
    public bool IsDragging => _mode is Mode.Move or Mode.Resize or Mode.Rotate or Mode.Handle or Mode.PanZone or Mode.Marquee;

    /// <summary>Raised on a click (no drag) on an object without modifiers; argument = clicked annotation id.</summary>
    public Action<Guid>? Clicked { get; set; }

    /// <summary>Starts a manipulation gesture when the pointer hits a handle, a matching object, or (optionally) empty space.</summary>
    public bool TryBegin(PointerInput e, bool allowMarquee, Func<Annotation, bool>? filter = null)
    {
        Reset();
        _filter = filter ?? (_ => true);
        _zoom = e.EffectiveZoom;
        _down = _current = e.Position;
        var page = _s.ActivePage;
        var sel = _s.SelectedAnnotations();

        var handle = SelectionHandles.HitTest(SelectionHandles.For(sel, _zoom), e.Position, _zoom);
        if (handle is not null)
        {
            _handle = handle;
            (_frame, _frameRotation) = SelectionHandles.Frame(sel);
            _before = sel.Select(a => a.DeepClone()).ToList();
            _mode = handle.Kind == HandleKind.Rotate ? Mode.Rotate : SelectionHandles.IsBoxHandle(handle.Kind) ? Mode.Resize : Mode.Handle;
            return true;
        }

        var hit = _s.HitTestAll(e.Position, _zoom).FirstOrDefault(_filter);
        if (hit is not null)
        {
            var ids = _s.ExpandToGroups(new[] { hit.Id });
            _clicked = hit.Id;
            _clickedWasSelected = _s.Selection.Contains(hit.Id);
            if (e.Control || e.Shift)
            {
                if (_clickedWasSelected)
                {
                    var set = _s.Selection.Ids.Where(id => !ids.Contains(id)).ToList();
                    _s.Selection.Set(set);
                    _toggleOnly = true;
                    _mode = Mode.Pending;
                    return true;
                }
                _s.Selection.Set(_s.Selection.Ids.Concat(ids));
            }
            else if (!_clickedWasSelected)
            {
                _s.Selection.Set(ids);
            }
            var selected = _s.SelectedAnnotations();
            if (hit is DropZoneAnnotation { Content: not null } zone && selected.Count == 1 && !zone.IsLocked)
            {
                _before = new List<Annotation> { zone.DeepClone() };
                _mode = Mode.PanZone;
                return true;
            }
            _before = selected.Where(a => !a.IsLocked && a is not DropZoneAnnotation).Select(a => a.DeepClone()).ToList();
            _mode = Mode.Pending;
            return true;
        }

        if (!allowMarquee) return false;
        _additive = e.Control || e.Shift;
        if (!_additive) _s.Selection.ClearObjects();
        _marqueeBase = _s.Selection.ToSet();
        _marquee = new Rect2(e.Position.X, e.Position.Y, 0, 0);
        _mode = Mode.Marquee;
        return true;
    }

    public void Move(PointerInput e)
    {
        if (_mode == Mode.None) return;
        _current = e.Position;
        _zoom = e.EffectiveZoom;
        var page = _s.ActivePage;
        switch (_mode)
        {
            case Mode.Pending:
                if (!_toggleOnly && _before.Count > 0 && _down.DistanceTo(e.Position) * _zoom >= DragThreshold)
                {
                    _mode = Mode.Move;
                    goto case Mode.Move;
                }
                break;
            case Mode.Move:
                {
                    var dx = e.Position.X - _down.X;
                    var dy = e.Position.Y - _down.Y;
                    if (e.Shift) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
                    _guides = Array.Empty<SnapGuide>();
                    if (_s.SnappingEnabled)
                    {
                        var moving = AnnotationGeometry.Union(_before).Offset(dx, dy);
                        var snap = SnapEngine.SnapRect(moving, page.CanvasRect, Others(), SnapThreshold / _zoom, snapX: !e.Shift || dx != 0, snapY: !e.Shift || dy != 0);
                        dx += snap.Dx;
                        dy += snap.Dy;
                        _guides = snap.Guides;
                    }
                    foreach (var b in _before)
                    {
                        var c = b.DeepClone();
                        c.Translate(dx, dy);
                        LiveEdit.Replace(page, c);
                    }
                    break;
                }
            case Mode.Resize:
                ApplyResize(e);
                break;
            case Mode.Rotate:
                ApplyRotate(e);
                break;
            case Mode.Handle:
                ApplyHandle(e);
                break;
            case Mode.PanZone:
                {
                    var c = (DropZoneAnnotation)_before[0].DeepClone();
                    c.ContentOffset = c.ContentOffset.Offset(e.Position.X - _down.X, e.Position.Y - _down.Y);
                    LiveEdit.Replace(page, c);
                    break;
                }
            case Mode.Marquee:
                _marquee = Rect2.FromPoints(_down, e.Position);
                break;
        }
        _s.Invalidate();
    }

    public void End(PointerInput e)
    {
        if (_mode == Mode.None) return;
        if (e.Position != _current) Move(e);
        var page = _s.ActivePage;
        switch (_mode)
        {
            case Mode.Pending:
                if (!_toggleOnly && _clicked is { } id && !e.Control && !e.Shift)
                {
                    if (_clickedWasSelected && _s.Selection.Count > 1) _s.Selection.Set(_s.ExpandToGroups(new[] { id }));
                    Clicked?.Invoke(id);
                }
                break;
            case Mode.Marquee:
                if (_marquee.Width * _zoom >= 2 || _marquee.Height * _zoom >= 2)
                {
                    var hits = _s.HitTestRect(_marquee).Where(_filter).Select(a => a.Id);
                    var ids = _s.ExpandToGroups(hits);
                    _s.Selection.Set(_additive ? _marqueeBase.Concat(ids) : ids);
                }
                break;
            default:
                Commit(page);
                break;
        }
        Reset();
        _s.Invalidate();
    }

    public void Cancel()
    {
        if (_mode is Mode.Move or Mode.Resize or Mode.Rotate or Mode.Handle or Mode.PanZone)
            foreach (var b in _before) LiveEdit.Replace(_s.ActivePage, b.DeepClone());
        Reset();
        _s.Invalidate();
    }

    private void Commit(Page page)
    {
        var befores = new List<Annotation>();
        var afters = new List<Annotation>();
        foreach (var b in _before)
        {
            var now = page.Find(b.Id);
            if (now is null) continue;
            befores.Add(b);
            afters.Add(now.DeepClone());
        }
        // restore the pre-gesture state, then execute the gesture as one command
        foreach (var b in befores) LiveEdit.Replace(page, b.DeepClone());
        if (afters.Count == 0 || afters.Zip(befores).All(t => SameGeometry(t.First, t.Second))) return;
        var label = _mode switch
        {
            Mode.Move => "Move",
            Mode.Resize => "Resize",
            Mode.Rotate => "Rotate",
            Mode.PanZone => "Pan image",
            _ => "Edit shape",
        };
        var cmds = new List<IEditCommand> { new ModifyAnnotationsCommand(page.Id, befores, afters, label) };
        if (_mode is Mode.Move or Mode.Resize or Mode.Handle && _s.ExpandCanvasCommandFor(afters.Where(a => page.IndexOf(a.Id) >= 0)) is { } expand) cmds.Add(expand);
        _s.ExecuteComposite(label, cmds);
    }

    private static bool SameGeometry(Annotation a, Annotation b)
        => a.Start == b.Start && a.End == b.End && a.RotationDegrees == b.RotationDegrees && a.Bounds == b.Bounds
           && (a is not DropZoneAnnotation da || b is not DropZoneAnnotation db || da.ContentOffset == db.ContentOffset)
           && (a is not CalloutAnnotation ca || b is not CalloutAnnotation cb || ca.Tails.SequenceEqual(cb.Tails))
           && (a is not StepAnnotation sa || b is not StepAnnotation sb || sa.PointerTip == sb.PointerTip)
           && (a is not MagnifyAnnotation ma || b is not MagnifyAnnotation mb || ma.SourceRect == mb.SourceRect)
           && (a is not ArrowAnnotation aa || b is not ArrowAnnotation ab || (aa.Control1 == ab.Control1 && aa.Control2 == ab.Control2))
           && (a is not LineAnnotation la || b is not LineAnnotation lb || (la.Control1 == lb.Control1 && la.Control2 == lb.Control2))
           && (a is not ShapeAnnotation sha || b is not ShapeAnnotation shb || sha.Points.SequenceEqual(shb.Points));

    private IEnumerable<Rect2> Others()
    {
        var moving = _before.Select(b => b.Id).ToHashSet();
        return _s.ActivePage.AllAnnotations().Where(a => !moving.Contains(a.Id)).Select(a => a.RotatedBounds);
    }

    private void ApplyResize(PointerInput e)
    {
        var page = _s.ActivePage;
        var kind = _handle!.Kind;
        var p = e.Position;
        _guides = Array.Empty<SnapGuide>();
        if (_s.SnappingEnabled && _frameRotation == 0)
        {
            var snap = SnapEngine.SnapPoint(p, page.CanvasRect, Others(), SnapThreshold / _zoom);
            p = p.Offset(snap.Dx, snap.Dy);
            _guides = snap.Guides;
        }
        var local = AnnotationGeometry.ToLocal(p, _frame.Center, _frameRotation);
        double l = _frame.Left, t = _frame.Top, r = _frame.Right, b = _frame.Bottom;
        var leftSide = kind is HandleKind.TopLeft or HandleKind.Left or HandleKind.BottomLeft;
        var rightSide = kind is HandleKind.TopRight or HandleKind.Right or HandleKind.BottomRight;
        var topSide = kind is HandleKind.TopLeft or HandleKind.Top or HandleKind.TopRight;
        var bottomSide = kind is HandleKind.BottomLeft or HandleKind.Bottom or HandleKind.BottomRight;
        if (leftSide) l = local.X;
        if (rightSide) r = local.X;
        if (topSide) t = local.Y;
        if (bottomSide) b = local.Y;

        var keepAspect = SelectionHandles.IsCorner(kind) && (_before.Count == 1 && AnnotationGeometry.KeepsAspect(_before[0]) ? !e.Shift : e.Shift);
        if (keepAspect && _frame.Width > 0 && _frame.Height > 0)
        {
            var ax = leftSide ? _frame.Right : _frame.Left;
            var ay = topSide ? _frame.Bottom : _frame.Top;
            var w = (leftSide ? l : r) - ax;
            var h = (topSide ? t : b) - ay;
            var scale = Math.Max(Math.Abs(w) / _frame.Width, Math.Abs(h) / _frame.Height);
            w = Math.Sign(w == 0 ? 1 : w) * _frame.Width * scale;
            h = Math.Sign(h == 0 ? 1 : h) * _frame.Height * scale;
            if (leftSide) l = ax + w; else r = ax + w;
            if (topSide) t = ay + h; else b = ay + h;
        }
        var min = 1.0;
        if (Math.Abs(r - l) < min) { if (leftSide) l = r - min * Math.Sign(r - l == 0 ? 1 : r - l); else if (rightSide) r = l + min * Math.Sign(r - l == 0 ? 1 : r - l); }
        if (Math.Abs(b - t) < min) { if (topSide) t = b - min * Math.Sign(b - t == 0 ? 1 : b - t); else if (bottomSide) b = t + min * Math.Sign(b - t == 0 ? 1 : b - t); }

        var to = new Rect2(l, t, r - l, b - t);
        if (_frameRotation != 0)
        {
            // keep the anchor fixed in world space: the object rotates around its (new) centre
            var nc = to.Normalize().Center;
            var wc = AnnotationGeometry.ToWorld(nc, _frame.Center, _frameRotation);
            to = to.Offset(wc.X - nc.X, wc.Y - nc.Y);
        }
        foreach (var bef in _before)
        {
            var c = bef.DeepClone();
            if (_before.Count == 1)
            {
                var tip = (c as StepAnnotation)?.PointerTip;
                AnnotationGeometry.Resize(c, _frame, to);
                if (c is StepAnnotation st) st.PointerTip = tip;
            }
            else
            {
                AnnotationGeometry.Resize(c, _frame, to);
            }
            LiveEdit.Replace(page, c);
        }
    }

    private void ApplyRotate(PointerInput e)
    {
        var page = _s.ActivePage;
        var center = _frame.Center;
        var delta = GeometryMath.AngleDegrees(center, e.Position) - GeometryMath.AngleDegrees(center, _down);
        if (_before.Count == 1)
        {
            var c = _before[0].DeepClone();
            var rot = _before[0].RotationDegrees + delta;
            if (e.Shift) rot = Math.Round(rot / 15) * 15;
            c.RotationDegrees = GeometryMath.NormalizeDegrees(rot);
            LiveEdit.Replace(page, c);
            return;
        }
        if (e.Shift) delta = Math.Round(delta / 15) * 15;
        foreach (var b in _before)
        {
            var c = b.DeepClone();
            AnnotationGeometry.RotateAround(c, center, delta);
            LiveEdit.Replace(page, c);
        }
    }

    private void ApplyHandle(PointerInput e)
    {
        if (_before.Count != 1) return;
        var page = _s.ActivePage;
        var c = _before[0].DeepClone();
        var p = e.Position;
        _guides = Array.Empty<SnapGuide>();
        if (_s.SnappingEnabled && _handle!.Kind is HandleKind.Start or HandleKind.End or HandleKind.Vertex)
        {
            var snap = SnapEngine.SnapPoint(p, page.CanvasRect, Others(), SnapThreshold / _zoom);
            p = p.Offset(snap.Dx, snap.Dy);
            _guides = snap.Guides;
        }
        switch (_handle!.Kind)
        {
            case HandleKind.Start:
                c.Start = e.Shift ? GeometryMath.SnapAngle(c.End, e.Position) : p;
                break;
            case HandleKind.End:
                c.End = e.Shift ? GeometryMath.SnapAngle(c.Start, e.Position) : p;
                break;
            case HandleKind.Control1 or HandleKind.Control2:
                {
                    var (d1, d2) = AnnotationGeometry.DefaultControls(c.Start, c.End);
                    var first = _handle.Kind == HandleKind.Control1;
                    switch (c)
                    {
                        case ArrowAnnotation ar:
                            ar.Control1 = first ? p : ar.Control1 ?? d1;
                            ar.Control2 = first ? ar.Control2 ?? d2 : p;
                            break;
                        case LineAnnotation ln:
                            ln.Control1 = first ? p : ln.Control1 ?? d1;
                            ln.Control2 = first ? ln.Control2 ?? d2 : p;
                            break;
                    }
                    break;
                }
            case HandleKind.TailTip when c is CalloutAnnotation co && _handle.Index >= 0 && _handle.Index < co.Tails.Count:
                co.Tails[_handle.Index] = co.Tails[_handle.Index] with { Tip = p };
                break;
            case HandleKind.PointerTip when c is StepAnnotation st:
                st.PointerTip = p;
                break;
            case HandleKind.Vertex when c is ShapeAnnotation sh && _handle.Index >= 0 && _handle.Index < sh.Points.Count:
                sh.Points[_handle.Index] = AnnotationGeometry.ToLocal(p, _before[0].Bounds.Center, _before[0].RotationDegrees);
                break;
            case HandleKind.LensSource when c is MagnifyAnnotation m:
                m.SourceRect = new Rect2(p.X - m.SourceRect.Width / 2, p.Y - m.SourceRect.Height / 2, m.SourceRect.Width, m.SourceRect.Height);
                break;
        }
        LiveEdit.Replace(page, c);
    }

    private void Reset()
    {
        _mode = Mode.None;
        _before = new List<Annotation>();
        _handle = null;
        _guides = Array.Empty<SnapGuide>();
        _clicked = null;
        _clickedWasSelected = false;
        _toggleOnly = false;
        _additive = false;
    }

    // ------------------------------------------------------------------ hover / cursor / overlay

    /// <summary>Updates hover state (no button pressed).</summary>
    public void Hover(PointerInput e, Func<Annotation, bool>? filter = null)
    {
        _zoom = e.EffectiveZoom;
        var f = filter ?? (_ => true);
        var sel = _s.SelectedAnnotations();
        _hoverHandle = SelectionHandles.HitTest(SelectionHandles.For(sel, _zoom), e.Position, _zoom);
        var prev = _hoverId;
        _hoverId = _hoverHandle is null ? _s.HitTestAll(e.Position, _zoom).FirstOrDefault(f)?.Id : null;
        if (prev != _hoverId) _s.Invalidate();
    }

    public void ClearHover()
    {
        if (_hoverId is null && _hoverHandle is null) return;
        _hoverId = null;
        _hoverHandle = null;
        _s.Invalidate();
    }

    public bool HasHoverTarget => _hoverHandle is not null || _hoverId is not null;

    /// <summary>Cursor for the current gesture or hover target; null when nothing manipulable is under the pointer.</summary>
    public CursorHint? Cursor
    {
        get
        {
            switch (_mode)
            {
                case Mode.Move: return CursorHint.Move;
                case Mode.PanZone: return CursorHint.HandGrab;
                case Mode.Rotate: return CursorHint.Rotate;
                case Mode.Resize: return SelectionHandles.CursorFor(_handle!.Kind, _frameRotation);
                case Mode.Handle: return CursorHint.Crosshair;
                case Mode.Marquee: return CursorHint.Crosshair;
            }
            if (_hoverHandle is { } h)
            {
                var sel = _s.SelectedAnnotations();
                return SelectionHandles.CursorFor(h.Kind, sel.Count == 1 ? sel[0].RotationDegrees : 0);
            }
            if (_hoverId is { } id && _s.ActivePage.Find(id) is { } a)
                return a.IsLocked ? CursorHint.Arrow : a is DropZoneAnnotation { Content: not null } ? CursorHint.Hand : CursorHint.Move;
            return null;
        }
    }

    public IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var list = new List<OverlayPrimitive>();
        if (_mode == Mode.Marquee && (_marquee.Width > 0 || _marquee.Height > 0))
            list.Add(new OverlayRect(_marquee) { Style = OverlayStyle.Marquee });
        foreach (var g in _guides) list.Add(new OverlayLine(g.From, g.To) { Style = OverlayStyle.Guide });
        if (_mode == Mode.None && _hoverId is { } hid && !_s.Selection.Contains(hid) && _s.ActivePage.Find(hid) is { } ha)
        {
            if (AnnotationGeometry.IsLineLike(ha)) list.Add(new OverlayLine(ha.Start, ha.End) { Style = OverlayStyle.Hover });
            else list.Add(new OverlayRect(SelectionHandles.ManipulationBox(ha), ha.RotationDegrees) { Style = OverlayStyle.Hover });
        }
        var z = zoom > 0 ? zoom : 1;
        if (_mode == Mode.Resize && _before.Count > 0)
        {
            var sel = _s.SelectedAnnotations();
            var box = sel.Count == 1 ? SelectionHandles.ManipulationBox(sel[0]) : AnnotationGeometry.Union(sel);
            list.Add(new OverlayText(new Point2(_current.X + 12 / z, _current.Y + 12 / z), string.Create(CultureInfo.InvariantCulture, $"{Math.Round(box.Width)} × {Math.Round(box.Height)}")));
        }
        if (_mode == Mode.Rotate && _before.Count > 0)
        {
            var sel = _s.SelectedAnnotations();
            var deg = sel.Count == 1 ? sel[0].RotationDegrees : GeometryMath.NormalizeDegrees(GeometryMath.AngleDegrees(_frame.Center, _current) - GeometryMath.AngleDegrees(_frame.Center, _down));
            list.Add(new OverlayText(new Point2(_current.X + 12 / z, _current.Y + 12 / z), string.Create(CultureInfo.InvariantCulture, $"{Math.Round(deg)}°")));
        }
        return list;
    }
}
