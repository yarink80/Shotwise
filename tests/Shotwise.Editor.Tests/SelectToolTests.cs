using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class SelectToolTests
{
    private static TestKit Kit()
    {
        var k = new TestKit();
        k.S.SnappingEnabled = false;
        return k;
    }

    [Fact]
    public void Click_SelectsObject_EmptyClickDeselects()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.Click(20, 20);
        Assert.Equal(new[] { a.Id }, k.S.Selection.Ids);
        k.Click(150, 120);
        Assert.Empty(k.S.Selection.Ids);
    }

    [Fact]
    public void CtrlAndShiftClick_ToggleMultiSelection()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 10), End = new(140, 50) });
        k.Click(20, 20);
        k.Click(110, 20, ModifierKeys.Shift);
        Assert.Equal(2, k.S.Selection.Count);
        k.Click(20, 20, ModifierKeys.Control);
        Assert.Equal(new[] { b.Id }, k.S.Selection.Ids);
        _ = a;
    }

    [Fact]
    public void ClickOnGroupMember_SelectsWholeGroup()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 10), End = new(140, 50) });
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.S.Group();
        k.S.DeselectAll();
        k.Click(20, 20);
        Assert.Equal(new HashSet<Guid> { a.Id, b.Id }, k.S.Selection.ToSet());
    }

    [Fact]
    public void ClickOnMemberOfMultiSelection_ReducesToThatObject()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 10), End = new(140, 50) });
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.Click(20, 20);
        Assert.Equal(new[] { a.Id }, k.S.Selection.Ids);
        _ = b;
    }

    [Fact]
    public void Marquee_SelectsIntersecting_AndShiftAdds()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var b = k.Add(new ShapeAnnotation { Start = new(60, 10), End = new(80, 30) });
        var c = k.Add(new ShapeAnnotation { Start = new(150, 100), End = new(180, 130) });
        k.Drag(0, 0, 70, 40);
        Assert.Equal(new HashSet<Guid> { a.Id, b.Id }, k.S.Selection.ToSet());
        k.Drag(140, 90, 190, 140, ModifierKeys.Shift);
        Assert.Equal(3, k.S.Selection.Count);
        Assert.Contains(c.Id, k.S.Selection.Ids);
    }

    [Fact]
    public void MarqueeOverlay_IsDrawnWhileDragging()
    {
        using var k = Kit();
        k.S.PointerDown(P(5, 5));
        k.S.PointerMove(P(60, 50));
        var rect = Assert.Single(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Style == OverlayStyle.Marquee);
        Assert.Equal(new Rect2(5, 5, 55, 45), rect.Rect);
        k.S.PointerUp(P(60, 50));
        Assert.DoesNotContain(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Style == OverlayStyle.Marquee);
    }

    [Fact]
    public void Drag_MovesSelection_AsOneUndoStep()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var before = k.S.Undo.Position;
        k.Drag(20, 20, 50, 40, steps: 6);
        Assert.Equal(new Point2(40, 30), k.Get(a.Id).Start);
        Assert.Equal(before + 1, k.S.Undo.Position);
        Assert.Equal("Move", k.S.Undo.UndoLabel);
        k.S.Undo.Undo();
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public void ShiftDrag_LocksToDominantAxis()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.Drag(20, 20, 70, 28, ModifierKeys.Shift);
        Assert.Equal(new Point2(60, 10), k.Get(a.Id).Start);
    }

    [Fact]
    public void Click_WithoutDrag_DoesNotCreateUndoEntry()
    {
        using var k = Kit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var pos = k.S.Undo.Position;
        k.S.PointerDown(P(20, 20));
        k.S.PointerMove(P(21, 21)); // below 3 px threshold
        k.S.PointerUp(P(21, 21));
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Fact]
    public void LockedObject_IsSelectable_ButNotDraggable()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50), IsLocked = true });
        k.Drag(20, 20, 80, 80);
        Assert.Contains(a.Id, k.S.Selection.Ids);
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
        Assert.Empty(k.S.GetOverlay(1).OfType<OverlayHandle>());
    }

    [Fact]
    public void Esc_DuringDrag_RestoresObject()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var pos = k.S.Undo.Position;
        k.S.PointerDown(P(20, 20));
        k.S.PointerMove(P(60, 60));
        Assert.Equal(new Point2(50, 50), k.Get(a.Id).Start);
        k.Key(VirtualKey.Escape);
        Assert.Equal(new Point2(10, 10), k.Get(a.Id).Start);
        k.S.PointerUp(P(60, 60));
        Assert.Equal(pos, k.S.Undo.Position);
    }

    [Fact]
    public void ResizeHandle_ChangesBounds_OppositeCornerFixed()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.Selection.Select(a.Id);
        k.Drag(50, 50, 90, 70);
        Assert.Equal(new Rect2(10, 10, 80, 60), k.Get(a.Id).Bounds);
        Assert.Equal("Resize", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void ResizeEdgeHandle_ChangesOneDimension()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.Selection.Select(a.Id);
        k.Drag(30, 10, 30, 0);
        Assert.Equal(new Rect2(10, 0, 40, 50), k.Get(a.Id).Bounds);
    }

    [Fact]
    public void ShiftResize_KeepsAspect()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 30) }); // 40x20
        k.S.Selection.Select(a.Id);
        k.Drag(50, 30, 130, 35, ModifierKeys.Shift);
        var b = k.Get(a.Id).Bounds;
        Assert.Equal(2.0, b.Width / b.Height, 6);
        Assert.Equal(new Point2(10, 10), b.TopLeft);
    }

    [Fact]
    public void Stamp_KeepsAspectByDefault_ShiftFrees()
    {
        using var k = Kit();
        var a = k.Add(new StampAnnotation { Start = new(10, 10), End = new(50, 30) });
        k.S.Selection.Select(a.Id);
        // stamps show corner handles only
        Assert.Equal(4, k.S.GetOverlay(1).OfType<OverlayHandle>().Count(h => h.Kind != HandleKind.Rotate));
        k.Drag(50, 30, 130, 35);
        var b = k.Get(a.Id).Bounds;
        Assert.Equal(2.0, b.Width / b.Height, 6);
        k.Drag(b.Right, b.Bottom, b.Right, b.Bottom + 50, ModifierKeys.Shift);
        var c = k.Get(a.Id).Bounds;
        Assert.NotEqual(2.0, c.Width / c.Height, 3);
    }

    [Fact]
    public void Resize_RotatedObject_KeepsAnchorFixedInWorld()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(100, 80), RotationDegrees = 90 });
        k.S.Selection.Select(a.Id);
        var handles = k.S.GetOverlay(1).OfType<OverlayHandle>().ToDictionary(h => h.Kind);
        var anchorWorld = handles[HandleKind.TopLeft].Center;
        var drag = handles[HandleKind.BottomRight].Center;
        // drag the bottom-right corner (world) further away along the rotated width axis (world +Y)
        k.Drag(drag.X, drag.Y, drag.X, drag.Y + 20);
        var r = k.Get(a.Id);
        Assert.Equal(60, r.Bounds.Width, 6);
        Assert.Equal(20, r.Bounds.Height, 6);
        var newAnchor = GeometryMath.Rotate(r.Bounds.TopLeft, r.Bounds.Center, r.RotationDegrees);
        Assert.Equal(anchorWorld.X, newAnchor.X, 6);
        Assert.Equal(anchorWorld.Y, newAnchor.Y, 6);
    }

    [Fact]
    public void RotateHandle_SetsRotation_ShiftSnaps15()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(100, 100) });
        k.S.Selection.Select(a.Id);
        var rot = k.S.GetOverlay(1).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Rotate).Center; // (80, 36)
        k.Drag(rot.X, rot.Y, 104, 80); // to the right of the centre → +90°
        Assert.Equal(90, k.Get(a.Id).RotationDegrees, 6);
        var rot2 = k.S.GetOverlay(1).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Rotate).Center;
        k.Drag(rot2.X, rot2.Y, 80 + 20, 80 + 13, ModifierKeys.Shift); // ~123° → snapped
        Assert.Equal(0, k.Get(a.Id).RotationDegrees % 15, 6);
        Assert.Equal("Rotate", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void MultiSelectionRotate_RotatesPositionsAroundCommonCentre()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(40, 70), End = new(60, 80) });
        var b = k.Add(new ShapeAnnotation { Start = new(140, 70), End = new(160, 80) });
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.S.RotateSelection(90);
        var ca = k.Get(a.Id).Bounds.Center;
        Assert.Equal(100, ca.X, 6);
        Assert.Equal(25, ca.Y, 6);
        Assert.Equal(90, k.Get(b.Id).RotationDegrees, 6);
    }

    [Fact]
    public void LineHandles_MoveEndpoints_ShiftSnapsAngle()
    {
        using var k = Kit();
        var l = k.Add(new LineAnnotation { Start = new(10, 10), End = new(100, 10) });
        k.S.Selection.Select(l.Id);
        var kinds = k.S.GetOverlay(1).OfType<OverlayHandle>().Select(h => h.Kind).ToList();
        Assert.Equal(new[] { HandleKind.Start, HandleKind.End }, kinds);
        k.Drag(100, 10, 120, 50);
        Assert.Equal(new Point2(120, 50), k.Get(l.Id).End);
        k.Drag(120, 50, 150, 52, ModifierKeys.Shift);
        var e = k.Get(l.Id).End;
        Assert.Equal(10, e.Y, 6); // snapped to horizontal from the start point
    }

    [Fact]
    public void BezierControlHandle_InitialisesBothControls()
    {
        using var k = Kit();
        var a = k.Add(new ArrowAnnotation { Start = new(10, 50), End = new(100, 50), Bezier = true });
        k.S.Selection.Select(a.Id);
        var c1 = k.S.GetOverlay(1).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Control1).Center;
        Assert.Equal(new Point2(40, 50), c1);
        k.Drag(c1.X, c1.Y, 40, 20);
        var r = (ArrowAnnotation)k.Get(a.Id);
        Assert.Equal(new Point2(40, 20), r.Control1);
        Assert.Equal(new Point2(70, 50), r.Control2);
    }

    [Fact]
    public void CalloutTailHandle_MovesTip()
    {
        using var k = Kit();
        var c = k.Add(new CalloutAnnotation { Start = new(20, 20), End = new(100, 60), Tails = { new CalloutTail(new Point2(30, 100)) } });
        k.S.Selection.Select(c.Id);
        k.Drag(30, 100, 150, 120);
        Assert.Equal(new Point2(150, 120), ((CalloutAnnotation)k.Get(c.Id)).Tails[0].Tip);
        Assert.Equal(new Rect2(20, 20, 80, 40), k.Get(c.Id).Bounds);
    }

    [Fact]
    public void StepPointerHandle_AimsTail()
    {
        using var k = Kit();
        var s = k.Add(new StepAnnotation { Start = new(20, 20), End = new(56, 56), Style = StepStyle.CirclePointer, PointerTip = new Point2(90, 90) });
        k.S.Selection.Select(s.Id);
        Assert.DoesNotContain(k.S.GetOverlay(1).OfType<OverlayHandle>(), h => h.Kind == HandleKind.Rotate);
        k.Drag(90, 90, 150, 30);
        Assert.Equal(new Point2(150, 30), ((StepAnnotation)k.Get(s.Id)).PointerTip);
    }

    [Fact]
    public void PolygonVertexHandle_MovesVertex()
    {
        using var k = Kit();
        var p = k.Add(new ShapeAnnotation { Shape = ShapeKind.Polygon, Points = { new(20, 20), new(80, 20), new(50, 70) } });
        k.S.Selection.Select(p.Id);
        k.Drag(50, 70, 50, 100);
        Assert.Equal(new Point2(50, 100), ((ShapeAnnotation)k.Get(p.Id)).Points[2]);
    }

    [Fact]
    public void MagnifySourceHandle_MovesSourceOnly()
    {
        using var k = Kit();
        var m = k.Add(new MagnifyAnnotation { Start = new(100, 50), End = new(160, 110), SourceRect = new Rect2(20, 20, 30, 30) });
        k.S.Selection.Select(m.Id);
        k.Drag(35, 35, 45, 60);
        var r = (MagnifyAnnotation)k.Get(m.Id);
        Assert.Equal(new Rect2(30, 45, 30, 30), r.SourceRect);
        Assert.Equal(new Point2(100, 50), r.Start);
    }

    [Fact]
    public void ResizeMagnifyLens_KeepsSourceCentre_SizeFromScale()
    {
        using var k = Kit();
        var m = k.Add(new MagnifyAnnotation { Start = new(100, 50), End = new(160, 110), Scale = 2, SourceRect = new Rect2(20, 20, 30, 30) });
        k.S.Selection.Select(m.Id);
        k.Drag(160, 110, 180, 130);
        var r = (MagnifyAnnotation)k.Get(m.Id);
        Assert.Equal(new Point2(35, 35), r.SourceRect.Center);
        Assert.Equal(40, r.SourceRect.Width, 6);
    }

    [Fact]
    public void DoubleClick_Text_BeginsEdit()
    {
        using var k = Kit();
        var t = k.Add(new TextAnnotation { Start = new(10, 10), End = new(100, 40), Text = RichText.FromPlain("hi") });
        TextEditRequest? req = null;
        k.S.BeginTextEdit += (_, r) => req = r;
        k.S.DoubleClick(P(20, 20, clicks: 2));
        Assert.NotNull(req);
        Assert.Equal(t.Id, req!.AnnotationId);
        Assert.False(req.IsNew);
        Assert.Equal(t.Id, k.S.EditingAnnotationId);
    }

    [Fact]
    public void RightClick_SelectsObjectUnderPointer()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 10), End = new(140, 50) });
        k.S.Selection.Set(new[] { a.Id, b.Id });
        k.Click(20, 20, button: PointerButton.Right);
        Assert.Equal(2, k.S.Selection.Count); // already selected → keep multi-selection for the context menu
        k.S.DeselectAll();
        k.Click(110, 20, button: PointerButton.Right);
        Assert.Equal(new[] { b.Id }, k.S.Selection.Ids);
    }

    [Fact]
    public void Handles_SingleShape_HasEightPlusRotate_MultiUsesUnion()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        var b = k.Add(new ShapeAnnotation { Start = new(100, 100), End = new(140, 140) });
        k.S.Selection.Select(a.Id);
        var hs = k.S.GetOverlay(1).OfType<OverlayHandle>().ToList();
        Assert.Equal(9, hs.Count);
        k.S.Selection.Set(new[] { a.Id, b.Id });
        var ms = k.S.GetOverlay(1).OfType<OverlayHandle>().ToDictionary(h => h.Kind);
        Assert.Equal(new Point2(140, 140), ms[HandleKind.BottomRight].Center);
        Assert.Equal(new Point2(10, 10), ms[HandleKind.TopLeft].Center);
    }

    [Fact]
    public void Cursor_ReflectsHoverTarget()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.PointerMove(Hover(150, 120));
        Assert.Equal(CursorHint.Arrow, k.S.CurrentCursor);
        k.S.PointerMove(Hover(30, 30));
        Assert.Equal(CursorHint.Move, k.S.CurrentCursor);
        k.S.Selection.Select(a.Id);
        k.S.PointerMove(Hover(50, 50));
        Assert.Equal(CursorHint.SizeNWSE, k.S.CurrentCursor);
        k.S.PointerMove(Hover(30, 10));
        Assert.Equal(CursorHint.SizeNS, k.S.CurrentCursor);
    }

    [Fact]
    public void Cursor_RotatedHandle_UsesRotatedDirection()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(100, 100), RotationDegrees = 90 });
        k.S.Selection.Select(a.Id);
        var top = k.S.GetOverlay(1).OfType<OverlayHandle>().Single(h => h.Kind == HandleKind.Top).Center;
        k.S.PointerMove(Hover(top.X, top.Y));
        Assert.Equal(CursorHint.SizeWE, k.S.CurrentCursor);
    }

    [Fact]
    public void HoverOutline_ShownForUnselectedObject()
    {
        using var k = Kit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.PointerMove(Hover(30, 30));
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayRect>(), r => r.Style == OverlayStyle.Hover && r.Rect == new Rect2(10, 10, 40, 40));
    }

    [Fact]
    public void FavoritesTool_BehavesLikeSelect()
    {
        using var k = Kit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.ActiveTool = ToolKind.Favorites;
        k.Drag(20, 20, 40, 20);
        Assert.Equal(new Point2(30, 10), k.Get(a.Id).Start);
    }
}
