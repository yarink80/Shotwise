using System.Windows.Input;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;

namespace Shotwise.UI.Capture.Selection;

public enum SelectionPhase { Idle, Dragging, Adjusting }

public enum HandleId { None, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, Body }

/// <summary>
/// Pure selection state machine shared by all overlay windows (no WPF elements): pointer/keyboard handling, region geometry,
/// aspect locks, guide snapping, nudging, freehand/multi-area collection. All coordinates are virtual-screen physical pixels.
/// </summary>
public sealed class SelectionController
{
    public const int MinSize = 8;
    private const int DragThreshold = 4;

    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private HandleId _dragHandle;
    private RectI _dragStartRect;
    private PointI _downPt;
    private PointI _lastPt;
    private bool _passedThreshold;
    private bool _moveWhileDragging;
    private bool _freehand;
    private bool _finished;

    public SelectionController(SelectionRequest request, RectI virtualBounds, IReadOnlyList<MonitorInfo> monitors)
    {
        Request = request;
        VirtualBounds = virtualBounds;
        _monitors = monitors;
        MagnifierOn = request.ShowMagnifier;
        GuidesOn = request.ShowGuides;
        AspectLock = request.Options.AspectLock;
        Cursor = new PointI(virtualBounds.X + virtualBounds.Width / 2, virtualBounds.Y + virtualBounds.Height / 2);
    }

    public SelectionRequest Request { get; }
    public RectI VirtualBounds { get; }
    public PointI Cursor { get; private set; }
    public SelectionPhase Phase { get; private set; } = SelectionPhase.Idle;
    /// <summary>Current rectangle (while dragging or adjusting).</summary>
    public RectI? Selection { get; private set; }
    public List<PointI> FreehandPoints { get; } = new();
    /// <summary>Committed areas of a Multiple Area capture.</summary>
    public List<RectI> Areas { get; } = new();
    public WindowInfo? HoverWindow { get; set; }
    public ElementInfo? HoverElement { get; set; }
    public AspectLock AspectLock { get; private set; }
    public bool MagnifierOn { get; private set; }
    public bool GuidesOn { get; private set; }
    public bool HelpVisible { get; private set; }
    /// <summary>Transient message (e.g. "Selection too small"); cleared by the next pointer move.</summary>
    public string? MessageKey { get; private set; }
    /// <summary>Closest standard resolution during a drag (label shown by the guide) and whether the selection snapped to it.</summary>
    public SizeI? GuideSize { get; private set; }
    public bool GuideSnapped { get; private set; }
    public ScrollProbe? Probe { get; set; }
    /// <summary>Hwnd used by the mini toolbar for scrolling detection.</summary>
    public WindowInfo? SelectionWindow { get; set; }

    public bool IsFinished => _finished;
    public bool IsDragging => Phase == SelectionPhase.Dragging;

    /// <summary>Fixed-size region preview that follows the cursor (SelectionType.FixedRegion).</summary>
    public RectI? FixedPreview
    {
        get
        {
            if (Request.Type != SelectionType.FixedRegion) return null;
            var o = Request.Options;
            int w = Math.Max(MinSize, o.FixedWidth), h = Math.Max(MinSize, o.FixedHeight);
            return KeepOnScreen(new RectI(Cursor.X - w / 2, Cursor.Y - h / 2, w, h));
        }
    }

    /// <summary>Highlight rectangle for window / element hover (null when the pointer is not over a selectable target).</summary>
    public RectI? HoverRect
    {
        get
        {
            if (Phase != SelectionPhase.Idle) return null;
            if (Request.HoverElements) return HoverElement?.BoundsPx;
            if (Request.HoverWindows) return HoverWindow is { } w ? (Request.Options.WindowClientOnly ? w.ClientPx : w.BoundsPx) : null;
            return null;
        }
    }

    /// <summary>The rectangle to draw as "the selection" (dragging/adjusting rect, fixed preview or hover target).</summary>
    public RectI? DisplayRect => Selection ?? FixedPreview ?? HoverRect;

    public event EventHandler? Changed;
    public event EventHandler<SelectionResult?>? Finished;
    /// <summary>Raised when the selection settles (released/nudged) so the host can probe scrolling etc.</summary>
    public event EventHandler? SelectionSettled;
    /// <summary>Raised when arrow keys should move the real mouse cursor (no selection yet).</summary>
    public event EventHandler<PointI>? CursorNudgeRequested;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks all views to repaint (used when asynchronous data such as hover targets or the scroll probe arrived).</summary>
    public void Invalidate() => Raise();

    // ------------------------------------------------------------------ pointer

    public void PointerMoved(PointI p)
    {
        p = ClampToBounds(p);
        var delta = new PointI(p.X - _lastPt.X, p.Y - _lastPt.Y);
        _lastPt = p;
        Cursor = p;
        MessageKey = null;

        if (Phase == SelectionPhase.Dragging)
        {
            if (!_passedThreshold)
            {
                if (_freehand || Math.Abs(p.X - _downPt.X) >= DragThreshold || Math.Abs(p.Y - _downPt.Y) >= DragThreshold)
                    _passedThreshold = true;
            }
            if (_passedThreshold) UpdateDrag(p, delta);
        }
        Raise();
    }

    public void PointerDown(PointI p, int handleTolerancePx)
    {
        if (_finished) return;
        p = ClampToBounds(p);
        Cursor = p; _lastPt = p; MessageKey = null;

        if (Request.Type == SelectionType.FixedRegion)
        {
            if (FixedPreview is { } fr) Complete(Build(fr, null, null, null, null, SelectionAction.Default));
            return;
        }

        if (Phase == SelectionPhase.Adjusting && Selection is { } sel)
        {
            var h = HitTest(sel, p, handleTolerancePx);
            if (h != HandleId.None)
            {
                _dragHandle = h;
                _dragStartRect = sel;
                _downPt = p;
                _passedThreshold = true;
                Phase = SelectionPhase.Dragging;
                _freehand = false;
                Raise();
                return;
            }
        }

        _freehand = Request.Type == SelectionType.Freehand;
        _downPt = p;
        _passedThreshold = false;
        _dragHandle = HandleId.None;
        _dragStartRect = default;
        _moveWhileDragging = false;
        Selection = null;
        FreehandPoints.Clear();
        if (_freehand) FreehandPoints.Add(p);
        Phase = SelectionPhase.Dragging;
        Raise();
    }

    public void PointerUp(PointI p)
    {
        if (_finished || Phase != SelectionPhase.Dragging) return;
        p = ClampToBounds(p);
        Cursor = p; _lastPt = p;
        GuideSize = null;

        // Resize / move of an existing selection.
        if (_dragHandle != HandleId.None)
        {
            Phase = SelectionPhase.Adjusting;
            _dragHandle = HandleId.None;
            Raise();
            SelectionSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_freehand)
        {
            if (FreehandPoints.Count >= 3 && PolygonBounds(FreehandPoints) is { } fb && fb.Width >= MinSize && fb.Height >= MinSize)
            {
                Complete(Build(fb, null, null, FreehandPoints.ToList(), null, SelectionAction.Default));
                return;
            }
            FreehandPoints.Clear();
            Phase = SelectionPhase.Idle;
            if (_passedThreshold) MessageKey = "Selection_TooSmall";
            Raise();
            return;
        }

        if (!_passedThreshold)
        {
            // Click without drag: select the hovered window / element.
            Phase = SelectionPhase.Idle;
            if (Request.HoverElements && HoverElement is { } el)
            {
                var rect = ClipToBounds(el.BoundsPx);
                Complete(Build(rect, null, el, null, null, SelectionAction.Default));
                return;
            }
            if (Request.HoverWindows && HoverWindow is { } w)
            {
                var rect = ClipToBounds(Request.Options.WindowClientOnly ? w.ClientPx : w.BoundsPx);
                if (Request.AdjustBeforeCommit)
                {
                    // Show handles + toolbar for the window rectangle; the window stays the capture target until the user resizes.
                    Selection = rect;
                    SelectionWindow = w;
                    Phase = SelectionPhase.Adjusting;
                    Raise();
                    SelectionSettled?.Invoke(this, EventArgs.Empty);
                    return;
                }
                Complete(Build(rect, w, null, null, null, Request.Type == SelectionType.ScrollingWindow ? SelectionAction.Scrolling : SelectionAction.Default));
                return;
            }
            Raise();
            return;
        }

        var r = Selection;
        if (r is null || r.Value.Width < MinSize || r.Value.Height < MinSize)
        {
            Selection = null;
            Phase = SelectionPhase.Idle;
            MessageKey = "Selection_TooSmall";
            Raise();
            return;
        }

        if (Request.Type == SelectionType.MultipleArea)
        {
            Areas.Add(r.Value);
            Selection = null;
            Phase = SelectionPhase.Idle;
            Raise();
            return;
        }

        SelectionWindow = null;
        if (Request.AdjustBeforeCommit)
        {
            Phase = SelectionPhase.Adjusting;
            Raise();
            SelectionSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        Complete(Build(r.Value, null, null, null, null, Request.Type == SelectionType.ScrollingWindow ? SelectionAction.Scrolling : SelectionAction.Default));
    }

    public void DoubleClick(PointI p)
    {
        if (Phase == SelectionPhase.Adjusting && Selection is { } s && s.Contains(p)) CommitCurrent(SelectionAction.Default);
        else if (Request.Type == SelectionType.MultipleArea && Areas.Count > 0) CommitMultiple();
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>Handles a key press; returns true when consumed.</summary>
    public bool KeyDown(Key key, ModifierKeys mods)
    {
        if (_finished) return false;
        bool shift = (mods & ModifierKeys.Shift) != 0, ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        int step = shift ? 10 : 1;
        switch (key)
        {
            case Key.Escape:
                if (HelpVisible) { HelpVisible = false; Raise(); return true; }
                Complete(null);
                return true;
            case Key.F1:
                HelpVisible = !HelpVisible; Raise(); return true;
            case Key.M:
                MagnifierOn = !MagnifierOn; Raise(); return true;
            case Key.G:
                GuidesOn = !GuidesOn; GuideSize = null; Raise(); return true;
            case Key.D1 or Key.NumPad1:
                ToggleAspect(AspectLock.Square); return true;
            case Key.D4 or Key.NumPad4:
                ToggleAspect(AspectLock.FourThree); return true;
            case Key.D6 or Key.NumPad6:
                ToggleAspect(AspectLock.SixteenNine); return true;
            case Key.D0 or Key.NumPad0:
                SetAspect(AspectLock.None); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                Nudge(key, step, ctrl, alt);
                return true;
            case Key.Space when Phase == SelectionPhase.Dragging && _dragHandle == HandleId.None && !_freehand:
                _moveWhileDragging = true;
                return true;
            case Key.Enter or Key.Space:
                return TryCommitByKey();
            case Key.F:
                CompleteFullscreen(shift);
                return true;
            case Key.Delete or Key.Back when Request.Type == SelectionType.MultipleArea && Areas.Count > 0:
                Areas.RemoveAt(Areas.Count - 1); Raise(); return true;
        }
        return false;
    }

    public void KeyUp(Key key)
    {
        if (key == Key.Space) _moveWhileDragging = false;
    }

    private bool TryCommitByKey()
    {
        if (Phase == SelectionPhase.Adjusting && Selection is not null) { CommitCurrent(SelectionAction.Default); return true; }
        if (Request.Type == SelectionType.MultipleArea && Areas.Count > 0) { CommitMultiple(); return true; }
        if (Request.Type == SelectionType.FixedRegion && FixedPreview is { } fr) { Complete(Build(fr, null, null, null, null, SelectionAction.Default)); return true; }
        if (Phase == SelectionPhase.Idle && Request.HoverElements && HoverElement is { } el)
        {
            Complete(Build(ClipToBounds(el.BoundsPx), null, el, null, null, SelectionAction.Default)); return true;
        }
        if (Phase == SelectionPhase.Idle && Request.HoverWindows && HoverWindow is { } w)
        {
            Complete(Build(ClipToBounds(w.BoundsPx), w, null, null, null, Request.Type == SelectionType.ScrollingWindow ? SelectionAction.Scrolling : SelectionAction.Default));
            return true;
        }
        return false;
    }

    private void Nudge(Key key, int step, bool ctrl, bool alt)
    {
        int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        if (Phase == SelectionPhase.Adjusting && Selection is { } s)
        {
            var l = s.Left; var t = s.Top; var r = s.Right; var b = s.Bottom;
            if (ctrl) { r += dx; b += dy; }            // Ctrl+arrow: resize from the bottom/right edge
            else if (alt) { l += dx; t += dy; }         // Alt+arrow: resize from the top/left edge
            else { l += dx; r += dx; t += dy; b += dy; }
            var n = RectI.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            if (n.Width < MinSize || n.Height < MinSize) return;
            if (!ctrl && !alt) n = KeepOnScreen(n);
            else n = ClipToBounds(n);
            if (AspectLock != AspectLock.None && (ctrl || alt)) n = ApplyAspectToRect(n);
            Selection = n;
            Raise();
            SelectionSettled?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (Phase == SelectionPhase.Dragging && _dragHandle == HandleId.None && Selection is not null)
        {
            var p = ClampToBounds(new PointI(Cursor.X + dx, Cursor.Y + dy));
            CursorNudgeRequested?.Invoke(this, p);
            PointerMoved(p);
            return;
        }
        CursorNudgeRequested?.Invoke(this, ClampToBounds(new PointI(Cursor.X + dx, Cursor.Y + dy)));
        PointerMoved(ClampToBounds(new PointI(Cursor.X + dx, Cursor.Y + dy)));
    }

    // ------------------------------------------------------------------ aspect & guides

    private void ToggleAspect(AspectLock a) => SetAspect(AspectLock == a ? AspectLock.None : a);

    /// <summary>Toolbar entry points (same effect as the 1 / 4 / 6 / M / G / F1 keys).</summary>
    public void ToggleAspectLock(AspectLock a) => ToggleAspect(a);
    public void ToggleMagnifier() { MagnifierOn = !MagnifierOn; Raise(); }
    public void ToggleGuides() { GuidesOn = !GuidesOn; GuideSize = null; Raise(); }
    public void ToggleHelp() { HelpVisible = !HelpVisible; Raise(); }

    private void SetAspect(AspectLock a)
    {
        AspectLock = a;
        if (Selection is { } s && a != AspectLock.None) Selection = ApplyAspectToRect(s);
        Raise();
        if (Phase == SelectionPhase.Adjusting) SelectionSettled?.Invoke(this, EventArgs.Empty);
    }

    private RectI ApplyAspectToRect(RectI r)
    {
        var (rw, rh) = StandardResolutions.Ratio(AspectLock);
        if (rw == 0) return r;
        double ratio = (double)rw / rh;
        int w = r.Width, h = r.Height;
        if (w / ratio > h) w = (int)Math.Round(h * ratio); else h = (int)Math.Round(w / ratio);
        return KeepOnScreen(new RectI(r.X, r.Y, Math.Max(MinSize, w), Math.Max(MinSize, h)));
    }

    // ------------------------------------------------------------------ drag math

    private void UpdateDrag(PointI p, PointI delta)
    {
        if (_freehand)
        {
            var last = FreehandPoints[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2) FreehandPoints.Add(p);
            Selection = PolygonBounds(FreehandPoints);
            return;
        }

        if (_dragHandle == HandleId.Body)
        {
            var dx = p.X - _downPt.X; var dy = p.Y - _downPt.Y;
            Selection = KeepOnScreen(_dragStartRect.Offset(dx, dy));
            return;
        }

        if (_dragHandle != HandleId.None)
        {
            Selection = ApplyGuides(ResizeFromHandle(_dragStartRect, _dragHandle, p), movedX: MovedX(_dragHandle), movedY: MovedY(_dragHandle));
            return;
        }

        // New region from the anchor.
        if (_moveWhileDragging && Selection is { } cur)
        {
            _downPt = new PointI(_downPt.X + delta.X, _downPt.Y + delta.Y);
            Selection = KeepOnScreen(cur.Offset(delta.X, delta.Y));
            return;
        }
        Selection = ApplyGuides(RegionFromAnchor(_downPt, p), p.X >= _downPt.X ? 1 : -1, p.Y >= _downPt.Y ? 1 : -1);
    }

    private static int MovedX(HandleId h) => h is HandleId.Left or HandleId.TopLeft or HandleId.BottomLeft ? -1 : h is HandleId.Right or HandleId.TopRight or HandleId.BottomRight ? 1 : 0;
    private static int MovedY(HandleId h) => h is HandleId.Top or HandleId.TopLeft or HandleId.TopRight ? -1 : h is HandleId.Bottom or HandleId.BottomLeft or HandleId.BottomRight ? 1 : 0;

    private RectI RegionFromAnchor(PointI anchor, PointI p)
    {
        int l = Math.Min(anchor.X, p.X), r = Math.Max(anchor.X, p.X), t = Math.Min(anchor.Y, p.Y), b = Math.Max(anchor.Y, p.Y);
        int mx = p.X >= anchor.X ? 1 : -1, my = p.Y >= anchor.Y ? 1 : -1;
        return LockAndFit(l, t, r, b, mx, my);
    }

    private RectI ResizeFromHandle(RectI start, HandleId h, PointI p)
    {
        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        int mx = MovedX(h), my = MovedY(h);
        if (mx < 0) l = p.X; else if (mx > 0) r = p.X;
        if (my < 0) t = p.Y; else if (my > 0) b = p.Y;
        if (l > r) { (l, r) = (r, l); mx = -mx; }
        if (t > b) { (t, b) = (b, t); my = -my; }
        return LockAndFit(l, t, r, b, mx, my);
    }

    /// <summary>Applies the aspect lock (when set) and keeps the rectangle inside the virtual screen.</summary>
    private RectI LockAndFit(int l, int t, int r, int b, int mx, int my)
    {
        var vb = VirtualBounds;
        l = Math.Clamp(l, vb.Left, vb.Right); r = Math.Clamp(r, vb.Left, vb.Right);
        t = Math.Clamp(t, vb.Top, vb.Bottom); b = Math.Clamp(b, vb.Top, vb.Bottom);
        var (rw, rh) = StandardResolutions.Ratio(AspectLock);
        if (rw != 0)
        {
            double ratio = (double)rw / rh;
            double w = r - l, h = b - t;
            if (mx != 0 && my != 0)
            {
                if (w / ratio > h) h = w / ratio; else w = h * ratio;
            }
            else if (mx != 0) h = w / ratio;
            else if (my != 0) w = h * ratio;
            else { if (w / ratio > h) h = w / ratio; else w = h * ratio; }

            // maximum extents available on the moving side
            double maxW = mx < 0 ? r - vb.Left : mx > 0 ? vb.Right - l : 2 * Math.Min((l + r) / 2.0 - vb.Left, vb.Right - (l + r) / 2.0);
            double maxH = my < 0 ? b - vb.Top : my > 0 ? vb.Bottom - t : 2 * Math.Min((t + b) / 2.0 - vb.Top, vb.Bottom - (t + b) / 2.0);
            double s = Math.Min(1.0, Math.Min(maxW / Math.Max(1, w), maxH / Math.Max(1, h)));
            w *= s; h *= s;

            int iw = (int)Math.Round(w), ih = (int)Math.Round(h);
            if (mx < 0) l = r - iw; else if (mx > 0) r = l + iw; else { int cx = (l + r) / 2; l = cx - iw / 2; r = l + iw; }
            if (my < 0) t = b - ih; else if (my > 0) b = t + ih; else { int cy = (t + b) / 2; t = cy - ih / 2; b = t + ih; }
        }
        return RectI.FromLTRB(l, t, Math.Max(l, r), Math.Max(t, b));
    }

    /// <summary>Snaps the size to the closest standard resolution when within the guide threshold and records the guide label.</summary>
    private RectI ApplyGuides(RectI r, int movedX, int movedY)
    {
        GuideSnapped = false;
        GuideSize = null;
        if (!GuidesOn || r.Width < MinSize || r.Height < MinSize) return r;
        var closest = StandardResolutions.FindClosest(r.Width, r.Height, AspectLock);
        if (closest is not { } c) return r;
        GuideSize = c;
        const int snap = 8;
        if (Math.Abs(c.Width - r.Width) <= snap && Math.Abs(c.Height - r.Height) <= snap)
        {
            int l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
            if (movedX < 0) l = rr - c.Width; else rr = l + c.Width;
            if (movedY < 0) t = b - c.Height; else b = t + c.Height;
            var snapped = RectI.FromLTRB(l, t, rr, b);
            if (VirtualBounds.Contains(snapped)) { GuideSnapped = true; return snapped; }
        }
        return r;
    }

    // ------------------------------------------------------------------ hit testing & helpers

    public static HandleId HitTest(RectI r, PointI p, int tol)
    {
        bool nearL = Math.Abs(p.X - r.Left) <= tol, nearR = Math.Abs(p.X - r.Right) <= tol;
        bool nearT = Math.Abs(p.Y - r.Top) <= tol, nearB = Math.Abs(p.Y - r.Bottom) <= tol;
        bool inX = p.X >= r.Left - tol && p.X <= r.Right + tol, inY = p.Y >= r.Top - tol && p.Y <= r.Bottom + tol;
        if (nearL && nearT) return HandleId.TopLeft;
        if (nearR && nearT) return HandleId.TopRight;
        if (nearL && nearB) return HandleId.BottomLeft;
        if (nearR && nearB) return HandleId.BottomRight;
        if (nearT && inX) return HandleId.Top;
        if (nearB && inX) return HandleId.Bottom;
        if (nearL && inY) return HandleId.Left;
        if (nearR && inY) return HandleId.Right;
        if (r.Contains(p)) return HandleId.Body;
        return HandleId.None;
    }

    public PointI ClampToBounds(PointI p)
        => new(Math.Clamp(p.X, VirtualBounds.Left, VirtualBounds.Right - 1), Math.Clamp(p.Y, VirtualBounds.Top, VirtualBounds.Bottom - 1));

    private RectI ClipToBounds(RectI r)
    {
        var c = r.Intersect(VirtualBounds);
        return c.IsEmpty ? r : c;
    }

    private RectI KeepOnScreen(RectI r)
    {
        var vb = VirtualBounds;
        int w = Math.Min(r.Width, vb.Width), h = Math.Min(r.Height, vb.Height);
        int x = Math.Clamp(r.X, vb.Left, vb.Right - w), y = Math.Clamp(r.Y, vb.Top, vb.Bottom - h);
        return new RectI(x, y, w, h);
    }

    private static RectI? PolygonBounds(IReadOnlyList<PointI> pts)
    {
        if (pts.Count == 0) return null;
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var p in pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        return RectI.FromLTRB(l, t, r + 1, b + 1);
    }

    public MonitorInfo? MonitorAt(PointI p) => _monitors.FirstOrDefault(m => m.BoundsPx.Contains(p)) ?? _monitors.FirstOrDefault();

    private MonitorInfo? MonitorOf(RectI r)
    {
        MonitorInfo? best = null; long bestArea = -1;
        foreach (var m in _monitors)
        {
            var i = m.BoundsPx.Intersect(r);
            long a = (long)i.Width * i.Height;
            if (a > bestArea) { bestArea = a; best = m; }
        }
        return best;
    }

    // ------------------------------------------------------------------ completion

    public void CommitCurrent(SelectionAction action, ScrollDirection direction = ScrollDirection.Vertical)
    {
        if (Selection is not { } r) return;
        Complete(Build(r, SelectionWindow, null, null, null, action, direction));
    }

    public void CommitMultiple()
    {
        if (Areas.Count == 0) return;
        var union = Areas.Aggregate((a, b) => a.Union(b));
        Complete(Build(union, null, null, null, Areas.ToList(), SelectionAction.Default));
    }

    public void Cancel() => Complete(null);

    /// <summary>Right click: clears an adjusted selection, otherwise cancels the capture.</summary>
    public void ClearOrCancel()
    {
        if (Phase == SelectionPhase.Adjusting || Selection is not null || Areas.Count > 0 && Request.Type != SelectionType.MultipleArea)
        {
            Selection = null; SelectionWindow = null; Probe = null; Phase = SelectionPhase.Idle; FreehandPoints.Clear();
            Raise();
            return;
        }
        Complete(null);
    }

    /// <summary>True when the rectangle lies entirely within one monitor (panoramic capture requirement).</summary>
    public bool FitsOneMonitor(RectI r) => _monitors.Any(m => m.BoundsPx.Contains(r));

    public MonitorInfo? HostMonitorFor(RectI r) => MonitorAt(new PointI(r.X + r.Width / 2, Math.Min(r.Bottom - 1, VirtualBounds.Bottom - 1)));

    private void CompleteFullscreen(bool all)
    {
        if (all)
        {
            Complete(new SelectionResult(SelectionType.Fullscreen, VirtualBounds, null, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, null, true, true));
            return;
        }
        var m = MonitorAt(Cursor);
        if (m is null) return;
        Complete(new SelectionResult(SelectionType.Fullscreen, m.BoundsPx, null, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, m, true, false));
    }

    private SelectionResult Build(RectI rect, WindowInfo? win, ElementInfo? el, IReadOnlyList<PointI>? poly, IReadOnlyList<RectI>? areas,
        SelectionAction action, ScrollDirection dir = ScrollDirection.Vertical)
    {
        // A resized/moved All-in-One window selection becomes a plain region.
        if (win is not null && Selection is { } sel && sel != ClipToBounds(Request.Options.WindowClientOnly ? win.ClientPx : win.BoundsPx))
            win = null;
        return new SelectionResult(Request.Type, rect, win, el, poly, areas, action, dir, MonitorOf(rect));
    }

    private void Complete(SelectionResult? result)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(this, result);
    }
}
