using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor.Tools;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class PixelSelectionTests
{
    private static BgraImage Pattern()
    {
        var img = BgraImage.CreateFilled(100, 80, Argb.White);
        img.Blit(BgraImage.CreateFilled(20, 20, Argb.Red), 10, 10);
        return img;
    }

    private static TestKit Kit(bool withServices = true)
    {
        var k = new TestKit(image: Pattern(), withServices: withServices);
        k.S.ActiveTool = ToolKind.Selection;
        return k;
    }

    [Fact]
    public void RectangleDrag_CreatesMask_AndOverlay()
    {
        using var k = Kit();
        k.Drag(10, 10, 30, 30);
        Assert.True(k.S.Selection.HasPixelSelection);
        Assert.Equal(new Rect2(10, 10, 20, 20), k.S.Selection.PixelBounds);
        Assert.Equal(400, k.S.Selection.PixelMask!.Count);
        Assert.Equal(PixelSelectionShape.Rectangle, k.S.Selection.PixelShape);
        Assert.Contains(k.S.GetOverlay(1).OfType<OverlayImageTint>(), t => t.Mask is not null);
        k.Click(80, 70);
        Assert.False(k.S.Selection.HasPixelSelection);
    }

    [Fact]
    public void Ellipse_Polygon_Freehand_Shapes()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.Shape = PixelSelectionShape.Ellipse;
        k.Drag(0, 0, 40, 40);
        Assert.Equal(PixelSelectionShape.Ellipse, k.S.Selection.PixelShape);
        Assert.InRange(k.S.Selection.PixelMask!.Count, 1200, 1320); // π·20² ≈ 1257
        Assert.NotNull(k.S.Selection.PixelOutline);

        k.S.DeselectAll();
        k.S.ToolOptions.Selection.Shape = PixelSelectionShape.Polygon;
        k.Click(10, 10);
        k.Click(50, 10);
        k.Click(50, 50);
        k.Key(VirtualKey.Enter);
        Assert.Equal(PixelSelectionShape.Polygon, k.S.Selection.PixelShape);
        Assert.InRange(k.S.Selection.PixelMask!.Count, 700, 900); // triangle 40x40/2 = 800

        k.S.DeselectAll();
        k.S.ToolOptions.Selection.Shape = PixelSelectionShape.Freehand;
        k.S.PointerDown(P(60, 10));
        foreach (var (x, y) in new[] { (90.0, 10.0), (90.0, 40.0), (60.0, 40.0) }) k.S.PointerMove(P(x, y));
        k.S.PointerUp(P(60, 40));
        Assert.Equal(PixelSelectionShape.Freehand, k.S.Selection.PixelShape);
        Assert.InRange(k.S.Selection.PixelMask!.Count, 800, 950); // ~30x30
    }

    [Fact]
    public void ShiftAdds_AltSubtracts()
    {
        using var k = Kit();
        k.Drag(0, 0, 20, 20);
        k.Drag(50, 50, 60, 60, ModifierKeys.Shift);
        Assert.Equal(500, k.S.Selection.PixelMask!.Count);
        k.Drag(0, 0, 10, 20, ModifierKeys.Alt);
        Assert.Equal(300, k.S.Selection.PixelMask!.Count);
    }

    [Fact]
    public void SelectAll_WithPixelTool_SelectsAllPixels_InvertWorks()
    {
        using var k = Kit();
        k.S.SelectAll();
        Assert.Equal(8000, k.S.Selection.PixelMask!.Count);
        k.S.DeselectAll();
        k.Drag(0, 0, 50, 80);
        k.S.InvertSelection();
        Assert.Equal(new Rect2(50, 0, 50, 80), k.S.Selection.PixelBounds);
    }

    [Fact]
    public async Task DeletePixels_Transparent()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.Background = SelectionBackground.Transparent;
        k.Drag(10, 10, 30, 30);
        k.Key(VirtualKey.Delete);
        await k.Idle();
        Assert.Equal(0, k.Base().GetPixel(15, 15).A);
        Assert.Equal(Argb.White, k.Base().GetPixel(5, 5));
        Assert.False(k.S.Selection.HasPixelSelection);
        Assert.Equal(0, k.Inpainting.Calls);
        k.S.Undo.Undo();
        Assert.Equal(Argb.Red, k.Base().GetPixel(15, 15));
    }

    [Fact]
    public async Task DeletePixels_AutoFill_UsesInpainting_QualityOption()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.HighQuality = true;
        k.Drag(10, 10, 30, 30);
        await k.S.DeletePixelsAsync();
        Assert.Equal(1, k.Inpainting.Calls);
        Assert.Equal(InpaintQuality.Quality, k.Inpainting.LastQuality);
        Assert.Equal(k.Inpainting.FillColor, k.Base().GetPixel(15, 15));
        Assert.Equal(Argb.White, k.Base().GetPixel(5, 5));
    }

    [Fact]
    public async Task DeletePixels_AutoFill_FallbackWithoutService_UsesSurroundings()
    {
        using var k = Kit(withServices: false);
        k.Drag(10, 10, 30, 30);
        await k.S.DeletePixelsAsync();
        Assert.Equal(Argb.White, k.Base().GetPixel(20, 20));
    }

    [Fact]
    public async Task DragInsideSelection_MovesPixels_SelectionFollows()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.Background = SelectionBackground.Transparent;
        k.Drag(10, 10, 30, 30);
        k.S.PointerDown(P(20, 20));
        k.S.PointerMove(P(50, 40));
        Assert.Single(k.S.GetOverlay(1).OfType<OverlayImage>());
        k.S.PointerUp(P(60, 40));
        await k.Idle();
        var b = k.Base();
        Assert.Equal(Argb.Red, b.GetPixel(55, 35));
        Assert.Equal(0, b.GetPixel(15, 15).A);
        Assert.Equal(new Rect2(50, 30, 20, 20), k.S.Selection.PixelBounds);
        Assert.Equal("Move pixels", k.S.Undo.UndoLabel);
    }

    [Fact]
    public async Task CtrlDrag_CopiesPixels_KeepsSource()
    {
        using var k = Kit();
        k.Drag(10, 10, 30, 30);
        k.S.PointerDown(P(20, 20, ModifierKeys.Control));
        k.S.PointerMove(P(70, 50, ModifierKeys.Control));
        k.S.PointerUp(P(70, 50, ModifierKeys.Control));
        await k.Idle();
        Assert.Equal(Argb.Red, k.Base().GetPixel(15, 15));
        Assert.Equal(Argb.Red, k.Base().GetPixel(65, 45));
        Assert.Equal(0, k.Inpainting.Calls);
        Assert.Equal("Copy pixels", k.S.Undo.UndoLabel);
    }

    [Fact]
    public async Task ArrowKeys_NudgePixels()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.Background = SelectionBackground.Transparent;
        k.Drag(10, 10, 30, 30);
        k.Key(VirtualKey.Right, ModifierKeys.Shift);
        await k.Idle();
        Assert.Equal(new Rect2(20, 10, 20, 20), k.S.Selection.PixelBounds);
        Assert.Equal(Argb.Red, k.Base().GetPixel(35, 15));
    }

    [Fact]
    public async Task CopyAndCut_PixelSelection_Payload()
    {
        using var k = Kit();
        k.S.ToolOptions.Selection.Background = SelectionBackground.Transparent;
        k.Drag(10, 10, 30, 30);
        var p = k.S.Copy();
        Assert.NotNull(p);
        Assert.False(p!.HasAnnotations);
        Assert.Equal(20, p.Image!.Width);
        Assert.Equal(Argb.Red, p.Image.GetPixel(5, 5));
        var c = k.S.Cut();
        Assert.NotNull(c);
        await k.Idle();
        Assert.Equal(0, k.Base().GetPixel(15, 15).A);
    }

    [Fact]
    public void SnapToObject_SnapsRectangleToObjectEdges()
    {
        using var k = Kit();
        k.Add(new Shotwise.Core.Annotations.ShapeAnnotation { Start = new(40, 40), End = new(70, 70) });
        k.S.ToolOptions.Selection.SnapToObject = true;
        k.Drag(10, 10, 38, 68);
        Assert.Equal(new Rect2(10, 10, 30, 60), k.S.Selection.PixelBounds);
    }

    [Fact]
    public void SwitchingToNonPixelTool_ClearsPixelSelection()
    {
        using var k = Kit();
        k.Drag(10, 10, 30, 30);
        k.S.ActiveTool = ToolKind.MagicWand;
        Assert.True(k.S.Selection.HasPixelSelection);
        k.S.ActiveTool = ToolKind.Arrow;
        Assert.False(k.S.Selection.HasPixelSelection);
    }

    // ------------------------------------------------------------------ magic wand

    [Fact]
    public void MagicWand_UsesService_WithToleranceAndContiguity()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ActiveTool = ToolKind.MagicWand;
        k.S.ToolOptions.MagicWand.Tolerance = 30;
        k.S.ToolOptions.MagicWand.GlobalColor = true;
        k.Click(15.7, 12.2);
        Assert.Equal(new PointI(15, 12), k.Wand.LastSeed);
        Assert.Equal(0.3, k.Wand.LastTolerance, 6);
        Assert.False(k.Wand.LastContiguous);
        Assert.Equal(PixelSelectionShape.MagicWand, k.S.Selection.PixelShape);
        Assert.Equal(new Rect2(10, 10, 20, 20), k.S.Selection.PixelBounds);
    }

    [Fact]
    public void MagicWand_RefineAndCombine()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ActiveTool = ToolKind.MagicWand;
        k.S.ToolOptions.MagicWand.RefineRadius = 1;
        var tool = (MagicWandTool)k.S.CurrentTool;
        tool.SelectAt(new Point2(15, 15));
        Assert.Equal(1, k.Wand.RefineCalls);
        Assert.Equal(new Rect2(9, 9, 22, 22), k.S.Selection.PixelBounds);
        k.Wand.Region = new RectI(60, 50, 10, 10);
        tool.SelectAt(new Point2(65, 55), SelectionCombine.Add);
        Assert.Equal(484 + 144, k.S.Selection.PixelMask!.Count);
    }

    [Fact]
    public void MagicWand_FallbackFloodFill_WithoutService()
    {
        using var k = new TestKit(image: Pattern(), withServices: false);
        k.S.ActiveTool = ToolKind.MagicWand;
        k.Click(15, 15);
        Assert.Equal(new Rect2(10, 10, 20, 20), k.S.Selection.PixelBounds);
        k.Click(50, 50, ModifierKeys.Alt); // subtract white area
        Assert.Equal(400, k.S.Selection.PixelMask!.Count);
    }

    [Fact]
    public async Task MagicWand_DragInsideSelection_MovesPixels()
    {
        using var k = new TestKit(image: Pattern(), withServices: false);
        k.S.ActiveTool = ToolKind.MagicWand;
        k.S.ToolOptions.MagicWand.Background = SelectionBackground.Transparent;
        k.Click(15, 15);
        k.Drag(15, 15, 55, 15);
        await k.Idle();
        Assert.Equal(Argb.Red, k.Base().GetPixel(55, 15));
        Assert.Equal(0, k.Base().GetPixel(15, 15).A);
    }
}
