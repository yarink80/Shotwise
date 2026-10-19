using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Vision.Inpainting;

namespace Shotwise.Vision.Tests;

public class MagicWandTests
{
    private static BgraImage TwoRects()
    {
        using var m = TestImages.Canvas(120, 80, TestImages.Bgr(255, 255, 255));
        Cv2.Rectangle(m, new Rect(10, 10, 30, 30), TestImages.Bgr(220, 30, 30), -1);
        Cv2.Rectangle(m, new Rect(70, 20, 30, 30), TestImages.Bgr(220, 30, 30), -1);
        return TestImages.ToImage(m);
    }

    [Fact]
    public void Contiguous_SelectsOnlyConnectedRegion()
    {
        var mask = new MagicWandService().Select(TwoRects(), new PointI(20, 20), 0.1, contiguous: true);
        Assert.Equal(900, mask.Count);
        Assert.True(mask.Contains(10, 10) && mask.Contains(39, 39));
        Assert.False(mask.Contains(70, 20));
        Assert.False(mask.Contains(5, 5));
    }

    [Fact]
    public void Global_SelectsAllMatchingRegions()
    {
        var mask = new MagicWandService().Select(TwoRects(), new PointI(20, 20), 0.1, contiguous: false);
        Assert.Equal(1800, mask.Count);
    }

    [Fact]
    public void Tolerance_ExpandsSelection()
    {
        using var m = TestImages.Canvas(100, 40, TestImages.Bgr(200, 200, 200));
        Cv2.Rectangle(m, new Rect(50, 0, 50, 40), TestImages.Bgr(185, 185, 185), -1);       // slightly darker half
        var img = TestImages.ToImage(m);
        var wand = new MagicWandService();
        Assert.Equal(50 * 40, wand.Select(img, new PointI(5, 5), 0.01, true).Count);
        Assert.Equal(100 * 40, wand.Select(img, new PointI(5, 5), 0.2, true).Count);
        Assert.Equal(100 * 40, wand.Select(img, new PointI(5, 5), 20, true).Count);          // percent form
    }

    [Fact]
    public void SeedOutside_GivesEmptyMask()
        => Assert.True(new MagicWandService().Select(TwoRects(), new PointI(-1, 5), 0.5, true).IsEmpty);

    [Fact]
    public void Refine_ClosesPinholesAndRemovesSpecks()
    {
        var mask = new Mask(60, 60);
        for (var y = 10; y < 50; y++) for (var x = 10; x < 50; x++) mask[x, y] = 255;
        mask[30, 30] = 0;                                   // pinhole
        mask[2, 2] = 255;                                   // speck
        var refined = new MagicWandService().Refine(mask, 2);
        Assert.True(refined.Contains(30, 30));
        Assert.False(refined.Contains(2, 2));
        Assert.True(refined.Contains(25, 25));
    }
}

public class SmartMoveTests
{
    private static BgraImage Ui()
    {
        using var m = TestImages.Canvas(400, 300, TestImages.Bgr(245, 245, 245));
        Cv2.Rectangle(m, new Rect(20, 20, 120, 40), TestImages.Bgr(30, 110, 220), -1);                    // button
        Cv2.Rectangle(m, new Rect(180, 30, 100, 100), TestImages.Bgr(230, 90, 60), -1);                   // tile
        Cv2.Rectangle(m, new Rect(40, 150, 200, 90), TestImages.Bgr(255, 255, 255), -1);                  // card with border
        Cv2.Rectangle(m, new Rect(40, 150, 200, 90), TestImages.Bgr(120, 120, 120), 1);
        Cv2.PutText(m, "OK", new Point(50, 50), HersheyFonts.HersheySimplex, 0.7, TestImages.Bgr(255, 255, 255), 2, LineTypes.AntiAlias);
        return TestImages.ToImage(m);
    }

    [Fact]
    public async Task Detect_FindsFilledComponents()
    {
        var svc = new SmartMoveService(NullLogger<SmartMoveService>.Instance);
        var tol = await svc.AutoToleranceAsync(Ui(), default);
        Assert.InRange(tol, 0, 1);
        var comps = await svc.DetectAsync(Ui(), tol, default);

        bool Has(RectI r) => comps.Any(c => Math.Abs(c.Bounds.X - r.X) <= 3 && Math.Abs(c.Bounds.Y - r.Y) <= 3 &&
                                            Math.Abs(c.Bounds.Width - r.Width) <= 6 && Math.Abs(c.Bounds.Height - r.Height) <= 6);
        Assert.True(Has(new RectI(20, 20, 120, 40)), "button not found: " + string.Join(", ", comps.Select(c => c.Bounds)));
        Assert.True(Has(new RectI(180, 30, 100, 100)), "tile not found");
        Assert.True(Has(new RectI(40, 150, 200, 90)), "card not found");
        Assert.All(comps, c =>
        {
            Assert.Equal(400, c.Mask.Width);
            Assert.True(c.Mask.Count > 0);
            Assert.NotEqual(Guid.Empty, c.Id);
        });
    }

    [Fact]
    public async Task Detect_MaskCoversTheComponentFill()
    {
        var svc = new SmartMoveService(NullLogger<SmartMoveService>.Instance);
        var comps = await svc.DetectAsync(Ui(), 0.2, default);
        var tile = comps.First(c => Math.Abs(c.Bounds.X - 180) <= 3 && Math.Abs(c.Bounds.Width - 100) <= 6);
        Assert.True(tile.Mask.Contains(230, 80));          // centre of tile
        Assert.False(tile.Mask.Contains(100, 200));
    }

    [Fact]
    public async Task BlankImage_HasNoComponents()
    {
        using var m = TestImages.Canvas(200, 100, TestImages.Bgr(240, 240, 240));
        var svc = new SmartMoveService(NullLogger<SmartMoveService>.Instance);
        Assert.Empty(await svc.DetectAsync(TestImages.ToImage(m), await svc.AutoToleranceAsync(TestImages.ToImage(m), default), default));
    }
}

public class SimplifyTests
{
    private static BgraImage Screen()
    {
        using var m = TestImages.Canvas(500, 320, TestImages.Bgr(255, 255, 255));
        Cv2.Rectangle(m, new Rect(20, 20, 460, 60), TestImages.Bgr(40, 60, 160), -1);                     // header bar
        Cv2.Rectangle(m, new Rect(300, 120, 160, 50), TestImages.Bgr(30, 160, 60), -1);                   // button
        for (var i = 0; i < 4; i++)
            Cv2.PutText(m, "Lorem ipsum dolor sit amet " + i, new Point(30, 130 + i * 28), HersheyFonts.HersheySimplex, 0.55, TestImages.Bgr(30, 30, 30), 1, LineTypes.AntiAlias);
        return TestImages.ToImage(m);
    }

    [Fact]
    public async Task DominantColors_OrdersByCoverage()
    {
        var svc = new SimplifyService(NullLogger<SimplifyService>.Instance);
        var pal = await svc.DominantColorsAsync(Screen(), 3, default);
        Assert.Equal(3, pal.Colors.Count);
        Assert.True(pal.Colors[0].R > 230 && pal.Colors[0].G > 230);              // white dominates
    }

    [Fact]
    public async Task Generate_ProducesRectsAndTextBars()
    {
        var svc = new SimplifyService(NullLogger<SimplifyService>.Instance);
        var pal = await svc.DominantColorsAsync(Screen(), 5, default);
        var shapes = await svc.GenerateAsync(Screen(), 7, pal, default);
        Assert.Contains(shapes, s => s.Shape is SimplifyShapeKind.Rectangle or SimplifyShapeKind.RoundedRectangle &&
                                     Math.Abs(s.Bounds.Width - 460) < 12 && Math.Abs(s.Bounds.Height - 60) < 12);
        var bars = shapes.Where(s => s.Shape is SimplifyShapeKind.TextLeft or SimplifyShapeKind.TextCenter or SimplifyShapeKind.TextRight).ToList();
        Assert.True(bars.Count >= 3, $"expected text bars, got {bars.Count}");
        Assert.All(shapes, s => Assert.InRange(s.PaletteIndex, 0, pal.Colors.Count - 1));
        // text bars are drawn after (on top of) container rectangles
        var firstBar = shapes.ToList().FindIndex(s => s.Shape is SimplifyShapeKind.TextLeft);
        var lastRect = shapes.ToList().FindLastIndex(s => s.Shape is SimplifyShapeKind.Rectangle or SimplifyShapeKind.RoundedRectangle);
        Assert.True(firstBar > lastRect);
    }

    [Fact]
    public async Task Detail_ControlsAmountOfShapes()
    {
        var svc = new SimplifyService(NullLogger<SimplifyService>.Instance);
        var pal = new Palette { Colors = { Argb.White, Argb.Black, Argb.Blue, Argb.Green } };
        var low = await svc.GenerateAsync(Screen(), 1, pal, default);
        var high = await svc.GenerateAsync(Screen(), 10, pal, default);
        Assert.True(high.Count >= low.Count);
        Assert.True(high.Count > 0);
    }

    [Fact]
    public async Task BlankImage_YieldsNothing()
    {
        using var m = TestImages.Canvas(200, 100, TestImages.Bgr(255, 255, 255));
        var svc = new SimplifyService(NullLogger<SimplifyService>.Instance);
        var r = await svc.GenerateAsync(TestImages.ToImage(m), 5, new Palette { Colors = { Argb.White } }, default);
        Assert.Empty(r);
    }
}

public class InpaintingTests
{
    private sealed class NoModels : IModelManager
    {
        public IReadOnlyList<ModelInfo> Models => Array.Empty<ModelInfo>();
        public bool IsAvailable(string modelId) => false;
        public Task<string> EnsureAsync(string modelId, IProgress<double>? p, CancellationToken ct) => throw new InvalidOperationException("must not be called");
    }

    private static InpaintingService Svc() => new(new NoModels(), NullLogger<InpaintingService>.Instance);

    private static (BgraImage Clean, BgraImage Dirty, Mask Mask) TextOnGradient()
    {
        using var clean = new Mat(120, 300, MatType.CV_8UC3);
        for (var y = 0; y < 120; y++)
            for (var x = 0; x < 300; x++)
                clean.Set(y, x, new Vec3b((byte)(200 + x / 15), (byte)(180 + y / 8), 170));
        var dirty = clean.Clone();
        Cv2.PutText(dirty, "Hello", new Point(60, 70), HersheyFonts.HersheySimplex, 1.4, TestImages.Bgr(10, 10, 10), 3, LineTypes.AntiAlias);
        using var diff = new Mat();
        Cv2.Absdiff(clean, dirty, diff);
        using var g = new Mat();
        Cv2.CvtColor(diff, g, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(g, g, 5, 255, ThresholdTypes.Binary);
        var mask = CvInteropMask(g);
        return (TestImages.ToImage(clean), TestImages.ToImage(dirty), mask);
    }

    private static Mask CvInteropMask(Mat m) => Shotwise.Vision.Internal.CvInterop.MatToMask(m);

    [Fact]
    public void Available_ExcludesHighQualityWithoutModel()
    {
        Assert.Equal(new[] { InpaintQuality.Fast, InpaintQuality.Quality }, Svc().Available);
    }

    [Theory]
    [InlineData(InpaintQuality.Fast, 6.0)]
    [InlineData(InpaintQuality.Quality, 3.0)]
    [InlineData(InpaintQuality.HighQuality, 3.0)]               // falls back to Quality when the model is absent
    public async Task TextOverGradient_IsRemoved(InpaintQuality q, double maxErr)
    {
        var (clean, dirty, mask) = TextOnGradient();
        var result = await Svc().InpaintAsync(dirty, mask, q, default);
        Assert.Equal(dirty.Width, result.Width);
        var err = TestImages.MeanAbsDiff(clean, result);
        var errDirty = TestImages.MeanAbsDiff(clean, dirty);
        Assert.True(err < errDirty / 3, $"error {err:F2} vs dirty {errDirty:F2}");
        Assert.True(err < maxErr, $"error {err:F2}");
    }

    [Fact]
    public async Task OutsideTheMask_IsUntouched()
    {
        var (_, dirty, mask) = TextOnGradient();
        var result = await Svc().InpaintAsync(dirty, mask, InpaintQuality.Quality, default);
        // pixel far from text
        Assert.Equal(dirty.GetPixel(5, 5), result.GetPixel(5, 5));
        Assert.Equal(dirty.GetPixel(290, 110), result.GetPixel(290, 110));
    }

    [Fact]
    public async Task Exemplar_ContinuesStripedPattern()
    {
        using var clean = new Mat(100, 160, MatType.CV_8UC3);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 160; x++)
                clean.Set(y, x, (x / 6) % 2 == 0 ? new Vec3b(60, 60, 200) : new Vec3b(230, 230, 230));
        var dirty = clean.Clone();
        Cv2.Rectangle(dirty, new Rect(60, 30, 36, 30), TestImages.Bgr(0, 255, 0), -1);
        var mask = Mask.FromRect(160, 100, new RectI(60, 30, 36, 30));
        var result = await Svc().InpaintAsync(TestImages.ToImage(dirty), mask, InpaintQuality.Quality, default);
        var cleanImg = TestImages.ToImage(clean);
        // inside the hole the vertical stripes continue: error well below that of a flat fill
        long bad = 0;
        for (var y = 30; y < 60; y++)
            for (var x = 60; x < 96; x++)
            {
                var a = cleanImg.GetPixel(x, y); var b = result.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 150) bad++;
            }
        Assert.True(bad < 36 * 30 * 0.2, $"{bad} wrong pixels of {36 * 30}");
    }

    [Fact]
    public async Task EmptyMask_ReturnsCopy()
    {
        var (_, dirty, _) = TextOnGradient();
        var result = await Svc().InpaintAsync(dirty, new Mask(dirty.Width, dirty.Height), InpaintQuality.Fast, default);
        Assert.NotSame(dirty, result);
        Assert.Equal(0, TestImages.MeanAbsDiff(dirty, result));
    }

    [Fact]
    public async Task MaskSizeMismatch_Throws()
    {
        var (_, dirty, _) = TextOnGradient();
        await Assert.ThrowsAsync<Shotwise.Core.ShotwiseException>(() => Svc().InpaintAsync(dirty, new Mask(10, 10), InpaintQuality.Fast, default));
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        var (_, dirty, mask) = TextOnGradient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Svc().InpaintAsync(dirty, mask, InpaintQuality.Quality, cts.Token));
    }
}
