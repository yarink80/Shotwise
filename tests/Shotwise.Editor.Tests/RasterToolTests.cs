using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor.Tools;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class RasterToolTests
{
    /// <summary>White 100x80 image with a black 20x20 square at (10,10) and another at (60,40).</summary>
    private static BgraImage Pattern()
    {
        var img = BgraImage.CreateFilled(100, 80, Argb.White);
        img.Blit(BgraImage.CreateFilled(20, 20, Argb.Black), 10, 10);
        img.Blit(BgraImage.CreateFilled(20, 20, Argb.Black), 60, 40);
        return img;
    }

    // ------------------------------------------------------------------ fill

    [Fact]
    public async Task Fill_Contiguous_FillsOnlyConnectedRegion()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ActiveTool = ToolKind.Fill;
        k.S.ToolOptions.Fill.Color = Argb.Red;
        k.Click(15, 15);
        await k.Idle();
        var b = k.Base();
        Assert.Equal(Argb.Red, b.GetPixel(15, 15));
        Assert.Equal(Argb.Black, b.GetPixel(65, 45));
        Assert.Equal(Argb.White, b.GetPixel(50, 5));
        Assert.Equal("Fill", k.S.Undo.UndoLabel);
        k.S.Undo.Undo();
        Assert.Equal(Argb.Black, k.Base().GetPixel(15, 15));
    }

    [Fact]
    public async Task Fill_Global_FillsAllMatchingPixels()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ToolOptions.Fill.Global = true;
        k.S.ToolOptions.Fill.Color = Argb.Blue;
        await k.S.FillAtAsync(new Point2(15, 15));
        Assert.Equal(Argb.Blue, k.Base().GetPixel(65, 45));
    }

    [Fact]
    public async Task Fill_Tolerance_IncludesSimilarColours()
    {
        var img = BgraImage.CreateFilled(40, 20, Argb.White);
        img.Blit(BgraImage.CreateFilled(20, 20, Argb.FromRgb(250, 250, 250)), 20, 0);
        using var k = new TestKit(image: img);
        k.S.ToolOptions.Fill.Tolerance = 0;
        await k.S.FillAtAsync(new Point2(5, 5));
        Assert.NotEqual(k.S.ToolOptions.Fill.Color, k.Base().GetPixel(30, 5));
        k.S.Undo.Undo();
        k.S.ToolOptions.Fill.Tolerance = 5;
        await k.S.FillAtAsync(new Point2(5, 5));
        Assert.Equal(k.S.ToolOptions.Fill.Color, k.Base().GetPixel(30, 5));
    }

    [Fact]
    public async Task Fill_Transparent_ClearsRegion()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ToolOptions.Fill.Transparent = true;
        await k.S.FillAtAsync(new Point2(15, 15));
        Assert.Equal(0, k.Base().GetPixel(15, 15).A);
    }

    [Fact]
    public async Task Fill_IsLimitedToPixelSelection()
    {
        using var k = new TestKit(image: Pattern());
        k.S.SetPixelSelection(Mask.FromRect(100, 80, new RectI(0, 0, 40, 80)), PixelSelectionShape.Rectangle, null);
        k.S.ToolOptions.Fill.Color = Argb.Green;
        await k.S.FillAtAsync(new Point2(5, 5));
        Assert.Equal(Argb.Green, k.Base().GetPixel(35, 5));
        Assert.Equal(Argb.White, k.Base().GetPixel(45, 5));
    }

    [Fact]
    public void Fill_RightClickSetsCanvasColour_AltSamples()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ActiveTool = ToolKind.Fill;
        Argb? sampled = null;
        k.S.ColorSampled += (_, e) => sampled = e.Color;
        k.Click(15, 15, ModifierKeys.Alt);
        Assert.Equal(Argb.Black, sampled);
        Assert.Equal(Argb.Black, k.S.ToolOptions.Fill.Color);
        k.S.ToolOptions.Fill.Color = Argb.Yellow;
        k.Click(50, 50, button: PointerButton.Right);
        Assert.Equal(Argb.Yellow, k.Page.Background.Color);
        k.S.Undo.Undo();
        Assert.Equal(Argb.Transparent, k.Page.Background.Color);
    }

    // ------------------------------------------------------------------ eraser

    [Fact]
    public async Task Eraser_Transparent_ErasesAlongStroke()
    {
        using var k = new TestKit(image: Pattern());
        k.S.ActiveTool = ToolKind.Eraser;
        k.S.ToolOptions.Eraser.Width = 6;
        k.S.ToolOptions.Eraser.Antialias = false;
        k.S.PointerDown(P(5, 40));
        k.S.PointerMove(P(50, 40));
        Assert.NotEmpty(k.S.GetOverlay(1).OfType<OverlayPath>());
        k.S.PointerMove(P(95, 40));
        k.S.PointerUp(P(95, 40));
        await k.Idle();
        var b = k.Base();
        Assert.Equal(0, b.GetPixel(50, 40).A);
        Assert.Equal(0, b.GetPixel(70, 41).A);
        Assert.Equal(255, b.GetPixel(50, 30).A);
        Assert.Equal("Erase", k.S.Undo.UndoLabel);
    }

    [Fact]
    public async Task Eraser_CanvasColourMode_PaintsCanvasColour()
    {
        using var k = new TestKit(image: Pattern());
        k.S.SetCanvasColor(Argb.Green);
        k.S.ToolOptions.Eraser.Mode = EraseMode.CanvasColor;
        k.S.ToolOptions.Eraser.Antialias = false;
        await k.S.EraseAsync(new[] { new Point2(20, 20) });
        Assert.Equal(Argb.Green, k.Base().GetPixel(20, 20));
    }

    // ------------------------------------------------------------------ crop

    [Fact]
    public void CropTool_DefaultFrameIsCanvas_DragNewFrame_EnterApplies()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(80, 80) });
        k.S.ActiveTool = ToolKind.Crop;
        var crop = (CropTool)k.S.CurrentTool;
        Assert.Equal(k.Page.CanvasRect, crop.CropRect);
        Assert.False(crop.HasPendingCrop);
        k.Drag(50, 40, 150, 120);
        Assert.Equal(new Rect2(50, 40, 100, 80), crop.CropRect);
        var overlay = k.S.GetOverlay(1);
        Assert.Equal(4, overlay.OfType<OverlayImageTint>().Count());
        Assert.Equal(8, overlay.OfType<OverlayHandle>().Count());
        Assert.Contains(overlay.OfType<OverlayText>(), t => t.Text == "100 × 80");
        k.Key(VirtualKey.Enter);
        Assert.Equal(new Size2(100, 80), k.Page.CanvasSize);
        Assert.Equal(new Point2(10, 20), k.Get(a.Id).Start);
        Assert.Equal(new Point2(-50, -40), k.Page.Background.BaseImage!.Offset); // non-destructive
        Assert.Equal(k.Page.CanvasRect, crop.CropRect);
    }

    [Fact]
    public void CropTool_MoveFrame_ClampedToCanvas_HandleResize_AspectLock()
    {
        using var k = new TestKit();
        k.S.ActiveTool = ToolKind.Crop;
        var crop = (CropTool)k.S.CurrentTool;
        crop.SetCropRect(new Rect2(20, 20, 50, 50));
        k.Drag(40, 40, 400, 40);
        Assert.Equal(new Rect2(150, 20, 50, 50), crop.CropRect);
        k.Drag(200, 70, 180, 100);
        Assert.Equal(new Rect2(150, 20, 30, 80), crop.CropRect);
        k.S.ToolOptions.Crop.AspectRatio = 1;
        k.Drag(180, 100, 190, 60);
        Assert.Equal(crop.CropRect.Width, crop.CropRect.Height, 6);
        k.Key(VirtualKey.Escape);
        Assert.Equal(k.Page.CanvasRect, crop.CropRect);
    }

    [Fact]
    public void Crop_DeleteCroppedContent_ReplacesBaseImage()
    {
        using var k = new TestKit(image: Pattern());
        k.Settings.Current.Editor.DeleteCroppedContent = true;
        k.S.Crop(new Rect2(10, 10, 20, 20));
        Assert.Equal(new Size2(20, 20), k.Page.CanvasSize);
        Assert.Equal(Point2.Zero, k.Page.Background.BaseImage!.Offset);
        Assert.Equal(20, k.Doc.Assets.GetImage(k.Page.Background.BaseImage.Asset).Width);
        Assert.False(k.S.CanResetCrop);
    }

    [Fact]
    public void ResetCrop_RestoresNonDestructiveCrop()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(60, 60), End = new(80, 80) });
        k.S.Crop(new Rect2(50, 50, 50, 50));
        Assert.True(k.S.CanResetCrop);
        k.S.ResetCrop();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Equal(new Point2(60, 60), k.Get(a.Id).Start);
        Assert.Equal(Point2.Zero, k.Page.Background.BaseImage!.Offset);
        Assert.Equal("Reset crop", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void ResetCrop_AfterRepeatedCrops_RestoresFullImageInOneStep()
    {
        using var k = new TestKit();
        k.S.Crop(new Rect2(10, 10, 150, 100));
        k.S.Crop(new Rect2(10, 10, 50, 50));
        k.S.ResetCrop();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
    }

    // ------------------------------------------------------------------ cut out

    [Fact]
    public void CutOutRaster_PlainJoin_RemovesRows()
    {
        var src = BgraImage.CreateFilled(10, 30, Argb.White);
        src.Blit(BgraImage.CreateFilled(10, 10, Argb.Red), 0, 0);
        src.Blit(BgraImage.CreateFilled(10, 10, Argb.Blue), 0, 20);
        var j = Internal.CutOutRaster.Join(src, CutOutOrientation.Horizontal, 10, 20, CutOutEdgeStyle.None);
        Assert.Equal(20, j.Height);
        Assert.Equal(Argb.Red, j.GetPixel(5, 9));
        Assert.Equal(Argb.Blue, j.GetPixel(5, 10));
        var v = Internal.CutOutRaster.Join(BgraImage.CreateFilled(30, 5, Argb.White), CutOutOrientation.Vertical, 5, 25, CutOutEdgeStyle.None);
        Assert.Equal(10, v.Width);
    }

    [Theory]
    [InlineData(CutOutEdgeStyle.Tear)]
    [InlineData(CutOutEdgeStyle.Wave)]
    [InlineData(CutOutEdgeStyle.Saw)]
    [InlineData(CutOutEdgeStyle.Sharktooth)]
    public void CutOutRaster_JaggedEdges_CutTransparentSeam(CutOutEdgeStyle edge)
    {
        var src = BgraImage.CreateFilled(120, 100, Argb.Gray);
        var j = Internal.CutOutRaster.Join(src, CutOutOrientation.Horizontal, 40, 60, edge);
        Assert.Equal(80, j.Height);
        for (var x = 0; x < 120; x++)
        {
            Assert.Equal(0, j.GetPixel(x, 39).A);   // last row of the top piece is always cut
            Assert.Equal(0, j.GetPixel(x, 40).A);   // first row of the bottom piece is always cut
            Assert.Equal(255, j.GetPixel(x, 20).A); // far from the seam untouched
            Assert.Equal(255, j.GetPixel(x, 70).A);
        }
        // the profile varies along the seam (jagged)
        var depths = Enumerable.Range(0, 120).Select(x => Enumerable.Range(0, 12).Count(d => j.GetPixel(x, 39 - d).A == 0)).Distinct().Count();
        Assert.True(depths > 1);
    }

    [Fact]
    public void CutOutRaster_Fade_RampsAlpha()
    {
        var j = Internal.CutOutRaster.Join(BgraImage.CreateFilled(10, 100, Argb.Gray), CutOutOrientation.Horizontal, 40, 60, CutOutEdgeStyle.Fade);
        var near = j.GetPixel(5, 39).A;
        var mid = j.GetPixel(5, 33).A;
        Assert.True(near < mid);
        Assert.True(mid < 255);
        Assert.Equal(255, j.GetPixel(5, 10).A);
    }

    [Fact]
    public void CutOutTool_DragRemovesBand_ShiftsAnnotations_Undo()
    {
        using var k = new TestKit();
        var below = k.Add(new ShapeAnnotation { Start = new(10, 100), End = new(30, 120) });
        var above = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 20) });
        k.S.ActiveTool = ToolKind.CutOut;
        k.S.ToolOptions.CutOut.Edge = CutOutEdgeStyle.None;
        k.S.PointerDown(P(50, 40));
        k.S.PointerMove(P(50, 70));
        var tool = (CutOutTool)k.S.CurrentTool;
        Assert.Equal((40.0, 70.0), tool.Band);
        Assert.Single(k.S.GetOverlay(1).OfType<OverlayImageTint>());
        k.S.PointerUp(P(50, 70));
        Assert.Equal(new Size2(200, 120), k.Page.CanvasSize);
        Assert.Equal(new Point2(10, 70), k.Get(below.Id).Start);
        Assert.Equal(new Point2(10, 10), k.Get(above.Id).Start);
        k.S.Undo.Undo();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Equal(new Point2(10, 100), k.Get(below.Id).Start);
    }

    [Fact]
    public void CutOutTool_Vertical_UsesXAxis()
    {
        using var k = new TestKit();
        k.S.ActiveTool = ToolKind.CutOut;
        k.S.ToolOptions.CutOut.Orientation = CutOutOrientation.Vertical;
        Assert.Equal(CursorHint.SizeWE, k.S.CurrentCursor);
        k.Drag(50, 10, 80, 100);
        Assert.Equal(new Size2(170, 150), k.Page.CanvasSize);
    }

    [Fact]
    public void CutOut_EntireExtent_IsRejected()
    {
        using var k = new TestKit();
        k.S.CutOut(CutOutOrientation.Horizontal, 0, 150);
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
    }
}
