using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;

namespace Shotwise.Editor.Tools;

/// <summary>
/// Select tool: click / Ctrl|Shift-click multi-select (groups expand), marquee, move (Shift axis-lock, snapping
/// guides), resize/rotate handles, endpoint/tail/pointer/vertex handles, drop-zone panning; double-click edits text.
/// Right-click selects the object under the pointer (the view then shows the context menu).
/// </summary>
public class SelectTool : ToolBase
{
    private SelectionManipulator? _manip;

    public SelectTool() : this(ToolKind.Select) { }

    protected SelectTool(ToolKind kind) : base(kind) { }

    internal SelectionManipulator Manipulator => _manip ?? throw new InvalidOperationException("Tool is not active.");

    public override bool ShowsObjectHandles => true;

    public override CursorHint Cursor => _manip?.Cursor ?? CursorHint.Arrow;

    public override void Activate(EditorSession s)
    {
        base.Activate(s);
        _manip = new SelectionManipulator(s);
    }

    public override void Deactivate()
    {
        _manip?.Cancel();
        _manip = null;
        base.Deactivate();
    }

    public override void PointerDown(PointerInput e)
    {
        if (e.Button == PointerButton.Right)
        {
            SelectUnderPointerForContextMenu(S, e);
            return;
        }
        if (e.Button != PointerButton.Left) return;
        Manipulator.TryBegin(e, allowMarquee: true);
    }

    public override void PointerMove(PointerInput e)
    {
        if (Manipulator.IsActive) Manipulator.Move(e);
        else Manipulator.Hover(e);
    }

    public override void PointerUp(PointerInput e)
    {
        if (Manipulator.IsActive) Manipulator.End(e);
    }

    public override void PointerLeave() => _manip?.ClearHover();

    public override void DoubleClick(PointerInput e)
    {
        var hit = S.HitTest(e.Position, e.EffectiveZoom);
        if (hit is TextAnnotation or CalloutAnnotation && !hit.IsLocked) S.BeginEditText(hit.Id);
    }

    public override void KeyDown(KeyInput e)
    {
        if (e.Key == VirtualKey.Escape && Manipulator.IsActive)
        {
            Manipulator.Cancel();
            e.Handled = true;
        }
    }

    public override void Cancel() => _manip?.Cancel();

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom) => _manip?.Overlay(zoom) ?? Array.Empty<OverlayPrimitive>();

    /// <summary>Right-click: selects the (group of the) object under the pointer unless it is already selected.</summary>
    internal static void SelectUnderPointerForContextMenu(EditorSession s, PointerInput e)
    {
        var hit = s.HitTest(e.Position, e.EffectiveZoom);
        if (hit is null) return;
        if (!s.Selection.Contains(hit.Id)) s.Selection.Set(s.ExpandToGroups(new[] { hit.Id }));
    }
}

/// <summary>Favorites tool: shows starred quick styles (UI); on the canvas it behaves like the Select tool.</summary>
public sealed class FavoritesTool : SelectTool
{
    public FavoritesTool() : base(ToolKind.Favorites) { }
}

/// <summary>
/// Move tool: Select-tool behaviour plus <b>Smart Move</b> (when <see cref="MoveToolOptions.SmartMove"/> is on):
/// detected UI components are outlined; pressing on one lifts it into a movable image object (background auto-filled)
/// and, if the button is still down, continues as a drag.
/// </summary>
public sealed class MoveTool : SelectTool
{
    private bool _pressed;
    private PointerInput _downInput;
    private PointerInput _lastInput;
    private SmartComponent? _hoverComponent;
    private Task? _detecting;
    private bool _lifting;
    private double _detectedTolerance = double.NaN;

    public MoveTool() : base(ToolKind.Move) { }

    public override bool ShowsBusyCursor => true;

    private bool SmartMoveOn => S.ToolOptions.Move.SmartMove;

    public override CursorHint Cursor
        => _lifting ? CursorHint.Wait : base.Cursor is var c && c != CursorHint.Arrow ? c : _hoverComponent is not null ? CursorHint.Hand : CursorHint.Arrow;

    public override void Activate(EditorSession s)
    {
        base.Activate(s);
        if (SmartMoveOn) EnsureDetected();
    }

    public override void Deactivate()
    {
        _hoverComponent = null;
        base.Deactivate();
    }

    public override void OnOptionsChanged()
    {
        if (!SmartMoveOn)
        {
            _hoverComponent = null;
            S.Invalidate();
            return;
        }
        var o = S.ToolOptions.Move;
        if (S.SmartComponents.Count == 0 || (!o.AutoTolerance && !double.IsNaN(_detectedTolerance) && Math.Abs(o.Tolerance - _detectedTolerance) > 1e-9))
            _ = Detect();
    }

    /// <summary>Starts Smart Move detection if no (current) components are cached.</summary>
    internal void EnsureDetected()
    {
        if (_detecting is { IsCompleted: false } || S.SmartComponents.Count > 0) return;
        _ = Detect();
    }

    private Task Detect()
    {
        var t = DetectCore();
        _detecting = t;
        return t;

        async Task DetectCore()
        {
            await S.DetectSmartComponentsAsync().ConfigureAwait(true);
            if (Session is not null) _detectedTolerance = S.ToolOptions.Move.Tolerance;
        }
    }

    public override void PointerDown(PointerInput e)
    {
        if (e.Button == PointerButton.Left)
        {
            _pressed = true;
            _downInput = _lastInput = e;
        }
        if (!SmartMoveOn || e.Button != PointerButton.Left || _lifting)
        {
            base.PointerDown(e);
            return;
        }
        // objects and handles win over components
        var z = e.EffectiveZoom;
        if (S.HitTest(e.Position, z) is not null || HandleUnderPointer(e))
        {
            base.PointerDown(e);
            return;
        }
        if (S.SmartComponentAt(e.Position) is { } comp)
        {
            _lifting = true;
            _hoverComponent = null;
            _ = S.RunBackground("Smart Move lift", ct => Lift(comp, ct));
            return;
        }
        base.PointerDown(e);
    }

    private bool HandleUnderPointer(PointerInput e)
    {
        var handles = Internal.SelectionHandles.For(S.SelectedAnnotations(), e.EffectiveZoom);
        return Internal.SelectionHandles.HitTest(handles, e.Position, e.EffectiveZoom) is not null;
    }

    private async Task Lift(SmartComponent comp, CancellationToken ct)
    {
        try
        {
            var session = S;
            var id = await session.LiftSmartComponentAsync(comp, ct).ConfigureAwait(true);
            if (Session is null || id is null) return;
            if (_pressed)
            {
                // continue the press as a drag of the lifted object
                Manipulator.TryBegin(_downInput, allowMarquee: false);
                if (_lastInput.Position != _downInput.Position) Manipulator.Move(_lastInput);
            }
        }
        finally
        {
            _lifting = false;
        }
    }

    public override void PointerMove(PointerInput e)
    {
        _lastInput = e;
        if (_lifting) return;
        base.PointerMove(e);
        if (!SmartMoveOn || Manipulator.IsActive) return;
        if (S.SmartComponents.Count == 0) { EnsureDetected(); return; }
        var comp = Manipulator.HasHoverTarget ? null : S.SmartComponentAt(e.Position);
        if (comp?.Id != _hoverComponent?.Id)
        {
            _hoverComponent = comp;
            S.Invalidate();
        }
    }

    public override void PointerUp(PointerInput e)
    {
        _pressed = false;
        _lastInput = e;
        if (_lifting) return;
        base.PointerUp(e);
    }

    public override IEnumerable<OverlayPrimitive> Overlay(double zoom)
    {
        var list = new List<OverlayPrimitive>(base.Overlay(zoom));
        if (Session is null || !SmartMoveOn) return list;
        foreach (var c in S.SmartComponents)
            list.Add(new OverlayRect(c.Bounds.ToRect2()) { Style = OverlayStyle.Hover with { Stroke = new Shotwise.Core.Imaging.Argb(0x991E88E5) } });
        if (_hoverComponent is { } h)
        {
            list.Add(new OverlayImageTint(h.Bounds.ToRect2(), new Shotwise.Core.Imaging.Argb(0x331E88E5), h.Mask.Width == h.Bounds.Width && h.Mask.Height == h.Bounds.Height ? null : h.Mask) { Style = OverlayStyle.Marquee });
            list.Add(new OverlayRect(h.Bounds.ToRect2()) { Style = OverlayStyle.Selection });
        }
        return list;
    }
}
