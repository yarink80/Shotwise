using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Tools;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class DrawingToolTests
{
    private static TestKit Kit(ToolKind tool)
    {
        var k = new TestKit();
        k.S.SnappingEnabled = false;
        k.S.ActiveTool = tool;
        return k;
    }

    [Fact]
    public void Arrow_DragCreates_OneUndoStep_Selected()
    {
        using var k = Kit(ToolKind.Arrow);
        k.Drag(10, 10, 100, 60);
        var a = Assert.IsType<ArrowAnnotation>(Assert.Single(k.Page.Annotations));
        Assert.Equal(new Point2(10, 10), a.Start);
        Assert.Equal(new Point2(100, 60), a.End);
        Assert.Equal(new[] { a.Id }, k.S.Selection.Ids);
        Assert.Equal(1, k.S.Undo.Position);
        k.S.Undo.Undo();
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public void Arrow_LivePreview_IsInDocumentDuringDrag()
    {
        using var k = Kit(ToolKind.Arrow);
        k.S.PointerDown(P(10, 10));
        k.S.PointerMove(P(60, 60));
        Assert.Single(k.Page.Annotations);
        Assert.Equal(0, k.S.Undo.Position);
        k.Key(VirtualKey.Escape);
        Assert.Empty(k.Page.Annotations);
        k.S.PointerUp(P(60, 60));
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public void Arrow_ShiftSnapsAngle_ClickCreatesNothing()
    {
        using var k = Kit(ToolKind.Arrow);
        k.Drag(10, 10, 100, 20, ModifierKeys.Shift);
        var a = k.Page.Annotations.Single();
        Assert.Equal(10, a.End.Y, 6);
        k.S.DeselectAll();
        k.Click(150, 120);
        Assert.Single(k.Page.Annotations);
    }

    [Fact]
    public void Arrow_UsesToolStyle()
    {
        using var k = Kit(ToolKind.Arrow);
        k.S.SetToolStyle(ToolKind.Arrow, new QuickStyle { Tool = ToolKind.Arrow, Prototype = new ArrowAnnotation { Stroke = new StrokeStyle(Argb.Blue, 7), StartCap = ArrowCap.Diamond, Opacity = 0.5 } });
        k.Drag(10, 10, 100, 60);
        var a = (ArrowAnnotation)k.Page.Annotations.Single();
        Assert.Equal(7, a.Stroke.Width);
        Assert.Equal(ArrowCap.Diamond, a.StartCap);
        Assert.Equal(0.5, a.Opacity);
    }

    [Fact]
    public void ThemeStyle_IsDefault_WhenNothingSet()
    {
        using var k = new TestKit();
        k.Styles.ActiveTheme.QuickStyles.Add(new QuickStyle { Tool = ToolKind.Shape, Prototype = new ShapeAnnotation { Stroke = new StrokeStyle(Argb.Green, 11) } });
        k.S.ActiveTool = ToolKind.Shape;
        k.Drag(10, 10, 60, 60);
        Assert.Equal(11, k.Page.Annotations.Single().Stroke.Width);
    }

    [Fact]
    public void Line_BezierStyle_GetsDefaultControls()
    {
        using var k = Kit(ToolKind.Line);
        k.S.SetToolStyle(ToolKind.Line, new QuickStyle { Tool = ToolKind.Line, Prototype = new LineAnnotation { Bezier = true } });
        k.Drag(10, 10, 100, 10);
        var l = (LineAnnotation)k.Page.Annotations.Single();
        Assert.Equal(new Point2(40, 10), l.Control1);
        Assert.Equal(new Point2(70, 10), l.Control2);
    }

    [Fact]
    public void DrawingTool_ManipulatesOwnKind_DrawsOverOtherKinds_AltForcesDraw()
    {
        using var k = Kit(ToolKind.Arrow);
        k.Drag(10, 10, 100, 10);
        var first = k.Page.Annotations.Single();
        k.S.DeselectAll();
        k.Drag(50, 10, 50, 40);      // grab the existing arrow's body → move
        Assert.Single(k.Page.Annotations);
        Assert.Equal(new Point2(10, 40), k.Get(first.Id).Start);
        var shape = k.Add(new ShapeAnnotation { Start = new(120, 80), End = new(190, 140) });
        k.Drag(130, 90, 180, 130);   // over a shape (other kind, not selected) → new arrow
        Assert.Equal(3, k.Page.Annotations.Count);
        Assert.Equal(new Point2(120, 80), k.Get(shape.Id).Start);
        k.S.DeselectAll();
        k.Drag(50, 40, 90, 90, ModifierKeys.Alt); // Alt over an arrow → draw
        Assert.Equal(4, k.Page.Annotations.Count);
    }

    [Fact]
    public void Shape_ShiftSquare_AltFromCentre()
    {
        using var k = Kit(ToolKind.Shape);
        k.Drag(10, 10, 60, 30, ModifierKeys.Shift);
        Assert.Equal(new Rect2(10, 10, 50, 50), k.Page.Annotations[0].Bounds);
        k.S.DeselectAll();
        k.Drag(100, 100, 120, 110, ModifierKeys.Alt);
        Assert.Equal(new Rect2(80, 90, 40, 20), k.Page.Annotations[1].Bounds);
    }

    [Fact]
    public void Shape_SizeReadout_DuringDrag()
    {
        using var k = Kit(ToolKind.Shape);
        k.S.PointerDown(P(10, 10));
        k.S.PointerMove(P(70, 40));
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayText>(), t => t.Text == "60 × 30");
        k.S.PointerUp(P(70, 40));
    }

    [Fact]
    public void Polygon_ClickVertices_DoubleClickCloses()
    {
        using var k = Kit(ToolKind.Shape);
        k.S.SetToolStyle(ToolKind.Shape, new QuickStyle { Tool = ToolKind.Shape, Prototype = new ShapeAnnotation { Shape = ShapeKind.Polygon } });
        k.Click(10, 10);
        k.Click(100, 10);
        k.S.PointerMove(P(60, 50));
        Assert.Equal(2, ((ShapeTool)k.S.CurrentTool).PolygonVertices.Count);
        Assert.Equal(0, k.S.Undo.Position);
        k.Click(60, 80);
        k.S.DoubleClick(P(60, 80, clicks: 2));
        var p = (ShapeAnnotation)k.Page.Annotations.Single();
        Assert.Equal(ShapeKind.Polygon, p.Shape);
        Assert.Equal(new[] { new Point2(10, 10), new Point2(100, 10), new Point2(60, 80) }, p.Points);
        Assert.Equal(1, k.S.Undo.Position);
    }

    [Fact]
    public void Polygon_ClickFirstVertex_Closes_BackspaceRemoves_EscCancels()
    {
        using var k = Kit(ToolKind.Shape);
        k.S.SetToolStyle(ToolKind.Shape, new QuickStyle { Tool = ToolKind.Shape, Prototype = new ShapeAnnotation { Shape = ShapeKind.Polygon } });
        k.Click(10, 10);
        k.Click(100, 10);
        k.Click(150, 60);
        k.Key(VirtualKey.Back);
        Assert.Equal(2, ((ShapeTool)k.S.CurrentTool).PolygonVertices.Count);
        k.Click(60, 80);
        k.Click(11, 11); // near the first vertex → close
        Assert.Equal(3, ((ShapeAnnotation)k.Page.Annotations.Single()).Points.Count);

        k.S.DeselectAll();
        k.Click(10, 100, ModifierKeys.Alt);
        k.Click(50, 100, ModifierKeys.Alt);
        k.Key(VirtualKey.Escape);
        Assert.Single(k.Page.Annotations);
    }

    [Fact]
    public void Polygon_EnterCloses_TooFewPointsDiscarded()
    {
        using var k = Kit(ToolKind.Shape);
        k.S.SetToolStyle(ToolKind.Shape, new QuickStyle { Tool = ToolKind.Shape, Prototype = new ShapeAnnotation { Shape = ShapeKind.Polygon } });
        k.Click(10, 10);
        k.Click(50, 10);
        k.Key(VirtualKey.Enter);
        Assert.Empty(k.Page.Annotations);
        k.Click(10, 10);
        k.Click(50, 10);
        k.Click(30, 50);
        k.Key(VirtualKey.Enter);
        Assert.Single(k.Page.Annotations);
    }

    [Fact]
    public void Callout_CreatesWithDefaultTail_AndStartsTextEdit_CommitAddsOneStep()
    {
        using var k = Kit(ToolKind.Callout);
        TextEditRequest? req = null;
        k.S.BeginTextEdit += (_, r) => req = r;
        k.Drag(20, 20, 120, 70);
        Assert.NotNull(req);
        Assert.True(req!.IsNew);
        var c = (CalloutAnnotation)k.Page.Annotations.Single();
        Assert.Single(c.Tails);
        Assert.True(c.Tails[0].Tip.Y > 70);
        Assert.Equal(0, k.S.Undo.Position); // pending until commit
        k.S.CommitTextEdit(RichText.FromPlain("Note"));
        Assert.Equal(1, k.S.Undo.Position);
        Assert.Equal("Note", ((CalloutAnnotation)k.Page.Annotations.Single()).Text.PlainText);
        Assert.Null(k.S.ActiveTextEdit);
        k.S.Undo.Undo();
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public void Callout_Click_DefaultSize_EmptyCalloutIsKept()
    {
        using var k = Kit(ToolKind.Callout);
        k.Click(20, 20);
        k.S.CommitTextEdit(new RichText());
        var c = k.Page.Annotations.Single();
        Assert.Equal(new Rect2(20, 20, CalloutTool.DefaultSize.Width, CalloutTool.DefaultSize.Height), c.Bounds);
    }

    [Fact]
    public void Callout_StyleTails_KeepRelativePosition()
    {
        using var k = Kit(ToolKind.Callout);
        var proto = new CalloutAnnotation { Start = new(0, 0), End = new(100, 50), Tails = { new CalloutTail(new Point2(50, 100)) } };
        k.S.SetToolStyle(ToolKind.Callout, new QuickStyle { Tool = ToolKind.Callout, Prototype = proto });
        k.Drag(10, 10, 210, 110);
        k.S.CommitTextEdit(RichText.FromPlain("x"));
        var c = (CalloutAnnotation)k.Page.Annotations.Single();
        Assert.Equal(new Point2(110, 210), c.Tails[0].Tip);
    }

    [Fact]
    public void Callout_AddAndRemoveTail()
    {
        using var k = new TestKit();
        var c = k.Add(new CalloutAnnotation { Start = new(20, 20), End = new(100, 60) });
        CalloutTool.AddTail(k.S, c.Id);
        Assert.Single(((CalloutAnnotation)k.Get(c.Id)).Tails);
        CalloutTool.RemoveTail(k.S, c.Id, 0);
        Assert.Empty(((CalloutAnnotation)k.Get(c.Id)).Tails);
    }

    [Fact]
    public void Stamp_WithoutStamp_ReportsWarning()
    {
        using var k = Kit(ToolKind.Stamp);
        k.Click(50, 50);
        Assert.Empty(k.Page.Annotations);
        Assert.Contains(k.Messages, m => m.Kind == EditorMessageKind.Warning);
        Assert.Equal(CursorHint.NotAllowed, k.S.CurrentCursor);
    }

    [Fact]
    public void Stamp_Click_PlacesNaturalSizeCentred_DragKeepsAspect()
    {
        using var k = Kit(ToolKind.Stamp);
        var o = k.S.ToolOptions.Stamp;
        o.Data = new byte[] { 1, 2, 3 };
        o.MediaType = "image/svg+xml";
        o.StampId = "arrow-1";
        o.NaturalSize = new Size2(40, 20);
        k.Click(100, 100);
        var s = (StampAnnotation)k.Page.Annotations.Single();
        Assert.Equal(new Rect2(80, 90, 40, 20), s.Bounds);
        Assert.Equal("arrow-1", s.StampId);
        Assert.True(k.Doc.Assets.Contains(s.Asset));
        Assert.True(k.Doc.Assets.GetRefCount(s.Asset) > 0);
        k.S.DeselectAll();
        k.Drag(10, 10, 90, 15);
        var s2 = k.Page.Annotations[1];
        Assert.Equal(2.0, s2.Bounds.Width / s2.Bounds.Height, 6);
    }

    [Fact]
    public void Step_PlacesSequentialValues_CounterUndone()
    {
        using var k = Kit(ToolKind.Step);
        k.Click(30, 30);
        k.Click(80, 30);
        k.Click(130, 30);
        var values = k.Page.Annotations.Cast<StepAnnotation>().Select(s => s.Value).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, values);
        Assert.Equal(4, k.S.NextStepValue(StepSequence.Number));
        Assert.Equal(new Rect2(12, 12, StepTool.DefaultSize, StepTool.DefaultSize), k.Page.Annotations[0].Bounds);
        k.S.Undo.Undo();
        Assert.Equal(3, k.S.NextStepValue(StepSequence.Number));
        Assert.Equal(2, k.Page.Annotations.Count);
    }

    [Fact]
    public void Step_ClickOnExistingStep_SelectsInsteadOfPlacing()
    {
        using var k = Kit(ToolKind.Step);
        k.Click(30, 30);
        k.S.DeselectAll();
        k.Click(30, 30);
        Assert.Single(k.Page.Annotations);
        Assert.Single(k.S.Selection.Ids);
    }

    [Fact]
    public void Step_LetterSequences_AreIndependent()
    {
        using var k = Kit(ToolKind.Step);
        k.S.SetToolStyle(ToolKind.Step, new QuickStyle { Tool = ToolKind.Step, Prototype = new StepAnnotation { Sequence = StepSequence.Upper } });
        k.Click(30, 30);
        k.Click(80, 30);
        k.S.SetToolStyle(ToolKind.Step, new QuickStyle { Tool = ToolKind.Step, Prototype = new StepAnnotation { Sequence = StepSequence.Number } });
        k.Click(130, 30);
        var labels = k.Page.Annotations.Cast<StepAnnotation>().Select(s => s.Label).ToArray();
        Assert.Equal(new[] { "A", "B", "1" }, labels);
        Assert.Equal(3, k.S.NextStepValue(StepSequence.Upper));
        Assert.Equal(1, k.S.NextStepValue(StepSequence.Lower));
    }

    [Fact]
    public void Step_PointerStyle_DragAimsTip()
    {
        using var k = Kit(ToolKind.Step);
        k.S.SetToolStyle(ToolKind.Step, new QuickStyle { Tool = ToolKind.Step, Prototype = new StepAnnotation { Style = StepStyle.CirclePointer } });
        k.Drag(50, 50, 120, 90);
        var s = (StepAnnotation)k.Page.Annotations.Single();
        Assert.Equal(new Point2(120, 90), s.PointerTip);
        Assert.Equal(new Point2(50, 50), Rect2.FromPoints(s.Start, s.End).Center);
        k.S.DeselectAll();
        k.Click(150, 120, ModifierKeys.Alt);
        Assert.NotNull(((StepAnnotation)k.Page.Annotations[1]).PointerTip);
    }

    [Fact]
    public void Step_DeleteDoesNotRenumber_RestartEditAndRenumber()
    {
        using var k = Kit(ToolKind.Step);
        k.Click(30, 30);
        k.Click(80, 30);
        k.Click(130, 30);
        var second = k.Page.Annotations[1].Id;
        k.S.Selection.Select(second);
        k.S.DeleteSelection();
        Assert.Equal(new[] { 1, 3 }, k.Page.Annotations.Cast<StepAnnotation>().Select(s => s.Value));
        Assert.Equal(4, k.S.NextStepValue(StepSequence.Number));

        k.S.RenumberSteps(StepSequence.Number);
        Assert.Equal(new[] { 1, 2 }, k.Page.Annotations.Cast<StepAnnotation>().Select(s => s.Value));
        Assert.Equal(3, k.S.NextStepValue(StepSequence.Number));

        k.S.RestartStepSequence(StepSequence.Number, 10);
        k.S.DeselectAll();
        k.Click(30, 100);
        Assert.Equal(10, ((StepAnnotation)k.Page.Annotations[^1]).Value);

        k.S.EditStepValue(k.Page.Annotations[0].Id, 5);
        Assert.Equal(5, ((StepAnnotation)k.Page.Annotations[0]).Value);
        Assert.Equal(6, k.S.NextStepValue(StepSequence.Number));
        k.S.Undo.Undo();
        Assert.Equal(1, ((StepAnnotation)k.Page.Annotations[0]).Value);
        Assert.Equal(11, k.S.NextStepValue(StepSequence.Number));
    }

    [Fact]
    public void Pen_FreehandStroke_SimplifiedWhenSmoothing()
    {
        using var k = Kit(ToolKind.Pen);
        k.S.PointerDown(P(10, 10));
        for (var x = 11; x <= 100; x++) k.S.PointerMove(P(x, 10 + (x % 2) * 0.1));
        k.S.PointerUp(P(100, 10));
        var p = (PenAnnotation)k.Page.Annotations.Single();
        Assert.True(p.Points.Count < 10);
        Assert.Equal(new Point2(10, 10), p.Points[0]);
        Assert.Equal(100, p.Points[^1].X, 6);
    }

    [Fact]
    public void Pen_ShiftDrawsStraightLine_ClickMakesDot()
    {
        using var k = Kit(ToolKind.Pen);
        k.Drag(10, 10, 100, 14, ModifierKeys.Shift, steps: 10);
        var p = (PenAnnotation)k.Page.Annotations.Single();
        Assert.Equal(2, p.Points.Count);
        Assert.Equal(10, p.Points[1].Y, 6);
        k.S.DeselectAll();
        k.Click(150, 120, ModifierKeys.Alt);
        Assert.Equal(2, ((PenAnnotation)k.Page.Annotations[1]).Points.Count);
    }

    [Fact]
    public void Pen_NotVector_FlattensStrokeIntoImage()
    {
        using var k = Kit(ToolKind.Pen);
        k.S.ToolOptions.Pen.CreateAsVector = false;
        k.S.SetToolStyle(ToolKind.Pen, new QuickStyle { Tool = ToolKind.Pen, Prototype = new PenAnnotation { Stroke = new StrokeStyle(Argb.Red, 4), Smoothing = false } });
        k.Drag(10, 10, 100, 60);
        Assert.Empty(k.Page.Annotations);
        Assert.Equal(Argb.Red, k.Base().GetPixel(50, 35));
        Assert.Equal(1, k.S.Undo.Position);
        k.S.Undo.Undo();
        Assert.Equal(Argb.White, k.Base().GetPixel(50, 35));
    }

    [Fact]
    public void Highlighter_RectangleAndFreehand()
    {
        using var k = Kit(ToolKind.Highlighter);
        k.Drag(10, 10, 80, 30);
        var h = (HighlightAnnotation)k.Page.Annotations.Single();
        Assert.Null(h.Points);
        Assert.Equal(new Rect2(10, 10, 70, 20), h.Bounds);
        k.S.ToolOptions.Highlighter.Freehand = true;
        k.S.DeselectAll();
        k.Drag(10, 100, 150, 100, ModifierKeys.Alt, steps: 8);
        var f = (HighlightAnnotation)k.Page.Annotations[1];
        Assert.NotNull(f.Points);
        Assert.True(f.Points!.Count >= 2);
        k.S.ToolOptions.Highlighter.Freehand = false;
        k.S.DeselectAll();
        k.Click(150, 140, ModifierKeys.Alt); // rectangle click → nothing
        Assert.Equal(2, k.Page.Annotations.Count);
    }

    [Fact]
    public void Magnify_SourceIsLensOverScale()
    {
        using var k = Kit(ToolKind.Magnify);
        k.Drag(20, 20, 100, 80);
        var m = (MagnifyAnnotation)k.Page.Annotations.Single();
        Assert.Equal(new Rect2(20, 20, 80, 60), m.Bounds);
        Assert.Equal(new Rect2(40, 35, 40, 30), m.SourceRect);
        k.S.DeselectAll();
        k.Click(150, 100, ModifierKeys.Alt);
        Assert.Equal(MagnifyTool.DefaultSize.Width, k.Page.Annotations[1].Bounds.Width);
    }

    [Fact]
    public void Blur_Rectangle_UsesStyle()
    {
        using var k = Kit(ToolKind.Blur);
        k.S.SetToolStyle(ToolKind.Blur, new QuickStyle { Tool = ToolKind.Blur, Prototype = new BlurAnnotation { Mode = BlurKind.Pixelate, Region = ShapeKind.Ellipse } });
        k.Drag(10, 10, 60, 40);
        var b = (BlurAnnotation)k.Page.Annotations.Single();
        Assert.Equal(BlurKind.Pixelate, b.Mode);
        Assert.Equal(ShapeKind.Ellipse, b.Region);
        Assert.Null(b.Category);
        k.S.DeselectAll();
        k.Click(150, 120);
        Assert.Single(k.Page.Annotations);
    }

    [Fact]
    public void Cursor_ClickPlacesGlyphAtHotspot()
    {
        using var k = Kit(ToolKind.Cursor);
        k.S.SetToolStyle(ToolKind.Cursor, new QuickStyle { Tool = ToolKind.Cursor, Prototype = new CursorAnnotation { Cursor = CursorKind.Hand, Scale = 2 } });
        k.Click(50, 50);
        var c = (CursorAnnotation)k.Page.Annotations.Single();
        Assert.Equal(CursorKind.Hand, c.Cursor);
        Assert.Equal(new Rect2(50, 50, 64, 64), c.Bounds);
    }

    [Fact]
    public void Simplify_DrawUsesPaletteColour_MoveModeMovesOnlySimplifyShapes()
    {
        using var k = Kit(ToolKind.Simplify);
        k.S.ToolOptions.Simplify.Shape = SimplifyShapeKind.TextCenter;
        k.S.ToolOptions.Simplify.PaletteIndex = 2;
        k.Drag(10, 10, 60, 20);
        var s = (SimplifyShapeAnnotation)k.Page.Annotations.Single();
        Assert.Equal(SimplifyShapeKind.TextCenter, s.Shape);
        Assert.Equal(k.S.SimplifyColor(2), s.Fill.Color);
        var other = k.Add(new ShapeAnnotation { Start = new(100, 100), End = new(150, 140) });
        k.S.ToolOptions.Simplify.Mode = SimplifyToolMode.Move;
        k.S.DeselectAll();
        k.Drag(20, 15, 40, 35);
        Assert.Equal(new Point2(30, 30), k.Get(s.Id).Start);
        k.Drag(110, 110, 130, 130);
        Assert.Equal(new Point2(100, 100), k.Get(other.Id).Start);
    }

    [Fact]
    public void RememberedStyle_SharedAcrossSessions()
    {
        var memory = new ToolStyleMemory();
        using var k1 = new TestKit(configure: s => s with { StyleMemory = memory });
        k1.S.SetToolStyle(ToolKind.Arrow, new QuickStyle { Tool = ToolKind.Arrow, Prototype = new ArrowAnnotation { Stroke = new StrokeStyle(Argb.Blue, 13) } });
        using var k2 = new TestKit(configure: s => s with { StyleMemory = memory });
        Assert.Equal(13, k2.S.ActiveStyle(ToolKind.Arrow)!.Prototype!.Stroke.Width);
        k2.S.ActiveTool = ToolKind.Arrow;
        k2.S.SnappingEnabled = false;
        k2.Drag(10, 10, 100, 60);
        Assert.Equal(13, k2.Page.Annotations.Single().Stroke.Width);
    }

    [Fact]
    public void ChangeStyleProperty_AppliesToSelection_AndNextObject()
    {
        using var k = Kit(ToolKind.Shape);
        k.Drag(10, 10, 60, 60);
        var first = k.Page.Annotations.Single().Id;
        var pos = k.S.Undo.Position;
        for (var w = 2; w <= 6; w++) k.S.ChangeStyleProperty(ToolKind.Shape, a => a.Stroke = a.Stroke with { Width = w }, "Width", "shape-width");
        Assert.Equal(6, k.Get(first).Stroke.Width);
        Assert.Equal(pos + 1, k.S.Undo.Position); // slider drag merged
        k.S.DeselectAll();
        k.Drag(100, 100, 150, 140);
        Assert.Equal(6, k.Page.Annotations[1].Stroke.Width);
    }

    [Fact]
    public void UseFavorite_SwitchesToolAndAppliesStyle()
    {
        using var k = new TestKit();
        var s = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50) });
        k.S.Selection.Select(s.Id);
        var fav = new QuickStyle { Tool = ToolKind.Shape, IsFavorite = true, Prototype = new ShapeAnnotation { Stroke = new StrokeStyle(Argb.Purple, 5) } };
        k.S.ActiveTool = ToolKind.Favorites;
        k.S.UseFavorite(fav);
        Assert.Equal(ToolKind.Shape, k.S.ActiveTool);
        Assert.Same(fav, k.S.ActiveStyle(ToolKind.Shape));
    }

    [Fact]
    public void AllTools_HaveRealControllers()
    {
        using var k = new TestKit();
        foreach (var t in Enum.GetValues<ToolKind>())
        {
            k.S.ActiveTool = t;
            Assert.IsNotType<StubTool>(k.S.CurrentTool);
            Assert.Equal(t, k.S.CurrentTool.Kind);
        }
    }

    [Fact]
    public void ToolCursors_AreToolSpecific()
    {
        using var k = new TestKit();
        k.S.ActiveTool = ToolKind.Pen;
        Assert.Equal(CursorHint.Pen, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.Text;
        Assert.Equal(CursorHint.IBeam, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.Fill;
        Assert.Equal(CursorHint.Fill, k.S.CurrentCursor);
        k.S.PointerMove(Hover(10, 10, ModifierKeys.Alt));
        Assert.Equal(CursorHint.Eyedropper, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.Eraser;
        Assert.Equal(CursorHint.Eraser, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.MagicWand;
        Assert.Equal(CursorHint.MagicWand, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.Shape;
        Assert.Equal(CursorHint.Crosshair, k.S.CurrentCursor);
        k.S.ActiveTool = ToolKind.CutOut;
        Assert.Equal(CursorHint.SizeNS, k.S.CurrentCursor);
    }
}
