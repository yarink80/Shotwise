using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class RendererPrimitiveTests
{
    [Fact]
    public void BlankPage_IsBackgroundColor()
    {
        var (doc, page) = T.Doc(40, 30, Argb.FromRgb(10, 120, 200));
        var img = T.Render(doc, page);
        Assert.Equal(40, img.Width);
        Assert.Equal(30, img.Height);
        T.AssertColor(img, 0, 0, Argb.FromRgb(10, 120, 200), 0);
        T.AssertColor(img, 39, 29, Argb.FromRgb(10, 120, 200), 0);
    }

    [Fact]
    public void TransparentBackground_Option()
    {
        var (doc, page) = T.Doc(10, 10, Argb.Red);
        var img = T.Renderer.RenderPage(doc, page, new RenderOptions(1, true, null, true));
        Assert.Equal(0, img.GetPixel(5, 5).A);
    }

    [Fact]
    public void GradientBackground()
    {
        var (doc, page) = T.Doc(100, 20);
        page.Background = new CanvasBackground(Argb.White, GradientFill.Linear(Argb.Black, Argb.White, 0));
        var img = T.Render(doc, page);
        Assert.True(img.GetPixel(2, 10).R < 20);
        Assert.True(img.GetPixel(97, 10).R > 235);
        Assert.InRange(img.GetPixel(50, 10).R, 110, 145);
    }

    [Fact]
    public void BaseImage_DrawnAtOffset()
    {
        var (doc, page) = T.Doc(60, 60, Argb.White);
        var bmp = BgraImage.CreateFilled(20, 20, Argb.Blue);
        var id = doc.Assets.AddImage(bmp);
        page.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(id, new Point2(10, 15)));
        var img = T.Render(doc, page);
        T.AssertColor(img, 15, 20, Argb.Blue, 1);
        Assert.True(T.IsWhite(img.GetPixel(5, 5)));
        Assert.True(T.IsWhite(img.GetPixel(35, 40)));
    }

    [Fact]
    public void ScaleAndRegion_AffectOutputSize()
    {
        var (doc, page) = T.Doc(100, 80);
        page.Annotations.Add(new ShapeAnnotation { Start = new(10, 10), End = new(50, 50), Fill = FillStyle.Solid(Argb.Red), Stroke = StrokeStyle.None });
        var x2 = T.Renderer.RenderPage(doc, page, new RenderOptions(2));
        Assert.Equal(200, x2.Width);
        Assert.Equal(160, x2.Height);
        T.AssertColor(x2, 60, 60, Argb.Red, 2);
        var region = T.Renderer.RenderPage(doc, page, new RenderOptions(1) { Region = new Rect2(20, 20, 40, 40) });
        Assert.Equal(40, region.Width);
        T.AssertColor(region, 5, 5, Argb.Red, 2);
        Assert.True(T.IsWhite(region.GetPixel(35, 35)));
    }

    [Fact]
    public void Rectangle_FillAndStroke()
    {
        var img = T.RenderOne(new ShapeAnnotation
        {
            Start = new(20, 20), End = new(100, 80), Shape = ShapeKind.Rectangle,
            Fill = FillStyle.Solid(Argb.Yellow), Stroke = new StrokeStyle(Argb.Blue, 6),
        });
        T.AssertColor(img, 60, 50, Argb.Yellow, 1);
        T.AssertColor(img, 20, 50, Argb.Blue, 3); // stroke centred on edge
        Assert.True(T.IsWhite(img.GetPixel(10, 50)));
    }

    [Fact]
    public void Ellipse_CornersEmpty_CenterFilled()
    {
        var img = T.RenderOne(new ShapeAnnotation { Start = new(20, 20), End = new(120, 120), Shape = ShapeKind.Ellipse, Fill = FillStyle.Solid(Argb.Green), Stroke = StrokeStyle.None });
        T.AssertColor(img, 70, 70, Argb.Green, 1);
        Assert.True(T.IsWhite(img.GetPixel(22, 22)));
    }

    [Fact]
    public void RoundedRectangle_CornerRadiusClipsCorner()
    {
        var img = T.RenderOne(new ShapeAnnotation { Start = new(10, 10), End = new(110, 110), Shape = ShapeKind.RoundedRectangle, CornerRadius = 30, Fill = FillStyle.Solid(Argb.Black), Stroke = StrokeStyle.None });
        Assert.True(T.IsWhite(img.GetPixel(11, 11)));
        T.AssertColor(img, 60, 11, Argb.Black, 2);
    }

    [Fact]
    public void Polygon_Triangle()
    {
        var s = new ShapeAnnotation { Shape = ShapeKind.Polygon, Fill = FillStyle.Solid(Argb.Red), Stroke = StrokeStyle.None };
        s.Points.AddRange(new[] { new Point2(50, 10), new Point2(90, 90), new Point2(10, 90) });
        var img = T.RenderOne(s, 100, 100);
        T.AssertColor(img, 50, 70, Argb.Red, 1);
        Assert.True(T.IsWhite(img.GetPixel(15, 20)));
    }

    [Fact]
    public void GradientFill_Applied()
    {
        var img = T.RenderOne(new ShapeAnnotation
        {
            Start = new(0, 0), End = new(100, 100), Stroke = StrokeStyle.None,
            Fill = FillStyle.FromGradient(GradientFill.Linear(Argb.Black, Argb.White, 0)),
        }, 100, 100);
        Assert.True(img.GetPixel(3, 50).R < 30);
        Assert.True(img.GetPixel(96, 50).R > 225);
    }

    [Fact]
    public void Opacity_BlendsWithBackground()
    {
        var img = T.RenderOne(new ShapeAnnotation { Start = new(0, 0), End = new(50, 50), Fill = FillStyle.Solid(Argb.Black), Stroke = StrokeStyle.None, Opacity = 0.5 }, 60, 60);
        var c = img.GetPixel(25, 25);
        Assert.InRange(c.R, 120, 135);
    }

    [Fact]
    public void Rotation_RotatesAroundCenter()
    {
        // A wide bar rotated 90 degrees becomes a tall bar.
        var a = new ShapeAnnotation { Start = new(20, 60), End = new(180, 80), Fill = FillStyle.Solid(Argb.Black), Stroke = StrokeStyle.None, RotationDegrees = 90 };
        var img = T.RenderOne(a, 200, 140);
        T.AssertColor(img, 100, 20, Argb.Black, 2);
        T.AssertColor(img, 100, 120, Argb.Black, 2);
        Assert.True(T.IsWhite(img.GetPixel(30, 70)));
    }

    [Fact]
    public void DropShadow_PaintsOffsetShadow_AndGrowsBounds()
    {
        var a = new ShapeAnnotation
        {
            Start = new(20, 20), End = new(70, 70), Fill = FillStyle.Solid(Argb.White), Stroke = new StrokeStyle(Argb.Black, 2),
            Shadow = new ShadowStyle(Argb.Black, 45, 12, 4, 1),
        };
        var img = T.RenderOne(a, 120, 120, Argb.White);
        // shadow falls to the bottom-right
        Assert.True(img.GetPixel(75, 75).R < 160);
        Assert.True(T.IsWhite(img.GetPixel(10, 10)));
        var plain = a.DeepClone();
        plain.Shadow = null;
        Assert.True(T.Renderer.MeasureBounds(a).Width > T.Renderer.MeasureBounds(plain).Width + 5);
    }

    [Fact]
    public void Highlight_MultipliesOverContent()
    {
        var (doc, page) = T.Doc(100, 60);
        page.Annotations.Add(new ShapeAnnotation { Start = new(0, 0), End = new(50, 60), Fill = FillStyle.Solid(Argb.Black), Stroke = StrokeStyle.None });
        page.Annotations.Add(new HighlightAnnotation { Start = new(0, 20), End = new(100, 40) });
        var img = T.Render(doc, page);
        // black stays black under a multiply highlight; white becomes yellow
        Assert.True(img.GetPixel(25, 30).R < 8);
        var y = img.GetPixel(75, 30);
        Assert.True(y.R > 200 && y.B < 235, y.ToString());
        Assert.True(T.IsWhite(img.GetPixel(75, 5)));
    }

    [Fact]
    public void Highlight_Freehand()
    {
        var h = new HighlightAnnotation { Points = new List<Point2> { new(10, 30), new(90, 30) }, Stroke = new StrokeStyle(Argb.HighlightYellow, 14) };
        var img = T.RenderOne(h, 100, 60);
        Assert.False(T.IsWhite(img.GetPixel(50, 30)));
        Assert.True(T.IsWhite(img.GetPixel(50, 5)));
    }

    [Fact]
    public void Arrow_DrawsLineAndHead()
    {
        var a = new ArrowAnnotation { Start = new(20, 75), End = new(180, 75), Stroke = new StrokeStyle(Argb.Black, 4), EndCap = ArrowCap.Arrow };
        var img = T.RenderOne(a);
        T.AssertColor(img, 80, 75, Argb.Black, 2);
        Assert.False(T.IsWhite(img.GetPixel(168, 70))); // head wider than the shaft
        Assert.True(T.IsWhite(img.GetPixel(80, 60)));
        Assert.True(T.IsWhite(img.GetPixel(10, 75)));
    }

    [Theory]
    [InlineData(ArrowCap.Arrow)]
    [InlineData(ArrowCap.OpenArrow)]
    [InlineData(ArrowCap.Triangle)]
    [InlineData(ArrowCap.Round)]
    [InlineData(ArrowCap.Square)]
    [InlineData(ArrowCap.Diamond)]
    [InlineData(ArrowCap.TBar)]
    public void ArrowCaps_AllRenderAndExtendBounds(ArrowCap cap)
    {
        var a = new ArrowAnnotation { Start = new(40, 75), End = new(160, 75), Stroke = new StrokeStyle(Argb.Black, 4), StartCap = cap, EndCap = cap };
        var img = T.RenderOne(a);
        Assert.True(T.CountNonWhite(img) > 400);
        var b = T.Renderer.MeasureBounds(a);
        Assert.True(b.Height > 4);
        Assert.True(b.Contains(new Point2(40, 75)) && b.Contains(new Point2(160, 75)));
    }

    [Fact]
    public void Arrow_Bezier_CurvesAwayFromChord()
    {
        var a = new ArrowAnnotation
        {
            Start = new(20, 100), End = new(180, 100), Bezier = true, Control1 = new(60, 20), Control2 = new(140, 20),
            Stroke = new StrokeStyle(Argb.Black, 3), EndCap = ArrowCap.None,
        };
        var img = T.RenderOne(a);
        // curve peaks near y = 100 - 0.75*80 = 40 at x = 100
        Assert.False(T.IsWhite(img.GetPixel(100, 40)));
        Assert.True(T.IsWhite(img.GetPixel(100, 100)));
        var bounds = T.Renderer.MeasureBounds(a);
        Assert.True(bounds.Y < 45);
    }

    [Fact]
    public void Line_DoubleLine_HasHollowCentre()
    {
        var l = new LineAnnotation { Start = new(20, 50), End = new(180, 50), Stroke = new StrokeStyle(Argb.Black, 12), DoubleLine = true, StartCap = ArrowCap.None, EndCap = ArrowCap.None };
        var img = T.RenderOne(l, 200, 100);
        Assert.True(img.GetPixel(100, 50).R > 200);
        Assert.True(img.GetPixel(100, 46).R < 60);
    }

    [Theory]
    [InlineData(StrokeDash.Dash)]
    [InlineData(StrokeDash.Dot)]
    [InlineData(StrokeDash.DashDot)]
    public void DashStyles_LeaveGaps(StrokeDash dash)
    {
        var l = new LineAnnotation { Start = new(10, 30), End = new(190, 30), Stroke = new StrokeStyle(Argb.Black, 4, dash), StartCap = ArrowCap.None, EndCap = ArrowCap.None };
        var img = T.RenderOne(l, 200, 60);
        var dark = 0;
        for (var x = 10; x < 190; x++) if (img.GetPixel(x, 30).R < 100) dark++;
        Assert.InRange(dark, 20, 160);
    }

    [Theory]
    [InlineData(PenNib.Circle, 12, 5)]
    [InlineData(PenNib.Horizontal, 5, 1)]
    [InlineData(PenNib.Vertical, 1, 12)]
    [InlineData(PenNib.Square, 8, 8)]
    public void PenNibs_ProduceExpectedFootprint(PenNib nib, int expectW, int expectH)
    {
        // a single dot of the pen at the centre: footprint ~ nib shape of the stroke width
        var p = new PenAnnotation { Nib = nib, Stroke = new StrokeStyle(Argb.Black, 12), Smoothing = false };
        p.Points.AddRange(new[] { new Point2(50, 50), new Point2(50.2, 50.2) });
        var img = T.RenderOne(p, 100, 100);
        int minX = 100, maxX = 0, minY = 100, maxY = 0;
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                if (img.GetPixel(x, y).R < 128) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        var w = maxX - minX + 1;
        var h = maxY - minY + 1;
        if (nib == PenNib.Circle) { Assert.InRange(w, 11, 14); Assert.InRange(h, 11, 14); }
        else if (nib == PenNib.Square) { Assert.InRange(w, 11, 14); Assert.InRange(h, 11, 14); }
        else if (nib == PenNib.Horizontal) { Assert.InRange(w, 10, 14); Assert.True(h <= 4, $"h={h}"); }
        else { Assert.InRange(h, 10, 14); Assert.True(w <= 4, $"w={w}"); }
        _ = expectW; _ = expectH;
    }

    [Fact]
    public void Pen_SmoothingPassesThroughPoints_AndStaysContinuous()
    {
        var p = new PenAnnotation { Stroke = new StrokeStyle(Argb.Black, 3), Smoothing = true };
        p.Points.AddRange(new[] { new Point2(10, 90), new Point2(50, 20), new Point2(90, 90), new Point2(130, 20), new Point2(170, 90) });
        var img = T.RenderOne(p, 180, 110);
        Assert.False(T.IsWhite(img.GetPixel(50, 20)));
        Assert.False(T.IsWhite(img.GetPixel(90, 90)));
        var bounds = T.Renderer.MeasureBounds(p);
        Assert.True(bounds.Contains(new Point2(50, 20)));
    }

    [Fact]
    public void Step_Badge_DrawsLabelInTextColor()
    {
        var s = new StepAnnotation { Start = new(20, 20), End = new(70, 70), Value = 3, Fill = FillStyle.Solid(Argb.Red) };
        var img = T.RenderOne(s, 100, 100);
        T.AssertColor(img, 25, 45, Argb.Red, 2);
        // white label pixels exist in the centre area
        var white = 0;
        for (var y = 30; y < 62; y++) for (var x = 30; x < 62; x++) if (img.GetPixel(x, y).G > 230) white++;
        Assert.True(white > 30);
    }

    [Fact]
    public void Step_PointerExtendsBoundsAndPaints()
    {
        var s = new StepAnnotation { Start = new(20, 20), End = new(60, 60), Style = StepStyle.CirclePointer, PointerTip = new Point2(110, 110), Fill = FillStyle.Solid(Argb.Blue) };
        var img = T.RenderOne(s, 140, 140);
        T.AssertColor(img, 85, 85, Argb.Blue, 2);
        Assert.True(T.Renderer.MeasureBounds(s).Right >= 109);
    }

    [Fact]
    public void Cursor_AllKindsDraw()
    {
        foreach (var k in Enum.GetValues<CursorKind>())
        {
            var img = T.RenderOne(new CursorAnnotation { Cursor = k, Start = new(10, 10), End = new(58, 58) }, 80, 80);
            Assert.True(T.CountNonWhite(img) > 100, k.ToString());
        }
    }

    [Fact]
    public void Cursor_DegenerateBoxUsesNaturalSize()
    {
        var img = T.RenderOne(new CursorAnnotation { Cursor = CursorKind.Arrow, Start = new(10, 10), End = new(10, 10), Scale = 2 }, 80, 80);
        Assert.True(T.CountNonWhite(img, new Rect2(10, 10, 48, 48)) > 100);
    }

    [Fact]
    public void SimplifyShapes_Draw()
    {
        foreach (var k in Enum.GetValues<SimplifyShapeKind>())
        {
            var s = new SimplifyShapeAnnotation { Shape = k, Start = new(10, 10), End = new(150, 70), Fill = FillStyle.Solid(Argb.Gray), PlaceholderId = "avatar" };
            var img = T.RenderOne(s, 160, 80);
            Assert.True(T.CountNonWhite(img) > 100, k.ToString());
        }
        foreach (var id in new[] { "avatar", "image", "video", "cursor", "window-buttons" })
        {
            var s = new SimplifyShapeAnnotation { Shape = SimplifyShapeKind.Placeholder, PlaceholderId = id, Start = new(10, 10), End = new(110, 70), Fill = FillStyle.Solid(Argb.Gray) };
            Assert.True(T.CountNonWhite(T.RenderOne(s, 120, 80)) > 50, id);
        }
    }

    [Fact]
    public void Blur_Solid_FillsRegion()
    {
        var (doc, page) = T.Doc(100, 100);
        page.Annotations.Add(new BlurAnnotation { Start = new(20, 20), End = new(60, 60), Mode = BlurKind.Solid, SolidColor = Argb.Black });
        var img = T.Render(doc, page);
        T.AssertColor(img, 40, 40, Argb.Black, 1);
        Assert.True(T.IsWhite(img.GetPixel(70, 70)));
    }

    [Fact]
    public void Blur_Gaussian_SmoothsUnderlyingContent_OnlyInsideRegion()
    {
        var (doc, page) = T.Doc(120, 80);
        var checker = T.Checker(120, 80, 4, Argb.Black, Argb.White);
        page.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(doc.Assets.AddImage(checker), Point2.Zero));
        page.Annotations.Add(new BlurAnnotation { Start = new(30, 10), End = new(90, 70), Mode = BlurKind.Gaussian, Intensity = 60 });
        var img = T.Render(doc, page);
        // inside: near mid grey (variance gone); outside: untouched checker
        var inside = img.GetPixel(60, 40);
        Assert.InRange(inside.R, 90, 170);
        T.AssertColor(img, 2, 2, Argb.Black, 1);
        T.AssertColor(img, 6, 2, Argb.White, 1);
        // left outside neighbour of the region keeps hard edges
        var a = img.GetPixel(24, 40).R;
        var b = img.GetPixel(28, 40).R;
        Assert.True(Math.Abs(a - b) > 200);
    }

    [Fact]
    public void Blur_Pixelate_ProducesUniformBlocks()
    {
        var (doc, page) = T.Doc(120, 80);
        var g = T.Gradient(120, 80);
        page.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(doc.Assets.AddImage(g), Point2.Zero));
        page.Annotations.Add(new BlurAnnotation { Start = new(20, 10), End = new(100, 70), Mode = BlurKind.Pixelate, Intensity = 50 });
        var img = T.Render(doc, page);
        // adjacent pixels in the same block are identical somewhere in the region
        var same = 0;
        for (var x = 21; x < 99; x++) if (img.GetPixel(x, 40) == img.GetPixel(x + 1, 40)) same++;
        Assert.True(same > 50, $"identical neighbours: {same}");
        // untouched outside
        Assert.Equal(g.GetPixel(5, 5), img.GetPixel(5, 5));
    }

    [Fact]
    public void Magnify_ShowsScaledSource()
    {
        var (doc, page) = T.Doc(200, 100);
        // small red square at (20..30, 20..30) on white; lens at right magnifies it x4
        var bmp = BgraImage.CreateFilled(200, 100, Argb.White);
        for (var y = 20; y < 30; y++) for (var x = 20; x < 30; x++) bmp.SetPixel(x, y, Argb.Red);
        page.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(doc.Assets.AddImage(bmp), Point2.Zero));
        page.Annotations.Add(new MagnifyAnnotation
        {
            Start = new(100, 10), End = new(180, 90), LensShape = ShapeKind.Rectangle, Scale = 4, SourceRect = new Rect2(20, 20, 20, 20),
            Stroke = StrokeStyle.None, Shadow = null, ShowConnector = false,
        });
        var img = T.Render(doc, page);
        // source centre is (30,30), lens centre (140,50): the red square (20..30) lands on x 100..140, y 10..50
        T.AssertColor(img, 120, 30, Argb.Red, 6);
        T.AssertColor(img, 135, 45, Argb.Red, 25);
        Assert.True(T.IsWhite(img.GetPixel(160, 70)), img.GetPixel(160, 70).ToString());
        Assert.True(T.IsWhite(img.GetPixel(176, 86)));
    }

    [Fact]
    public void Image_Annotation_DrawsAsset_AndSourceCrop()
    {
        var (doc, page) = T.Doc(100, 100);
        var src = BgraImage.CreateFilled(20, 20, Argb.Red);
        for (var y = 0; y < 20; y++) for (var x = 10; x < 20; x++) src.SetPixel(x, y, Argb.Blue);
        var id = doc.Assets.AddImage(src);
        page.Annotations.Add(new ImageAnnotation { Asset = id, Start = new(10, 10), End = new(50, 50) });
        page.Annotations.Add(new ImageAnnotation { Asset = id, Start = new(60, 10), End = new(90, 40), SourceCrop = new RectI(10, 0, 10, 20) });
        var img = T.Render(doc, page);
        T.AssertColor(img, 15, 30, Argb.Red, 4);
        T.AssertColor(img, 45, 30, Argb.Blue, 4);
        T.AssertColor(img, 62, 20, Argb.Blue, 12);
    }

    [Fact]
    public void DropZone_EmptyExportsNothing_ButEditorShowsPlaceholder()
    {
        var (doc, page) = T.Doc(100, 100);
        page.Annotations.Add(new DropZoneAnnotation { Start = new(10, 10), End = new(90, 90) });
        Assert.Equal(0, T.CountNonWhite(T.Render(doc, page)));
        var ed = T.Renderer.RenderPage(doc, page, new RenderOptions(1) { DrawEditorAdornments = true });
        Assert.True(T.CountNonWhite(ed) > 100);
    }

    [Fact]
    public void DropZone_ContentPanAndZoom()
    {
        var (doc, page) = T.Doc(100, 100);
        // left half red, right half blue
        var src = BgraImage.CreateFilled(100, 50, Argb.Red);
        for (var y = 0; y < 50; y++) for (var x = 50; x < 100; x++) src.SetPixel(x, y, Argb.Blue);
        var id = doc.Assets.AddImage(src);
        var dz = new DropZoneAnnotation { Start = new(0, 25), End = new(100, 75), Content = id, Stroke = StrokeStyle.None };
        page.Annotations.Add(dz);
        var img = T.Render(doc, page);
        T.AssertColor(img, 25, 50, Argb.Red, 3);
        T.AssertColor(img, 75, 50, Argb.Blue, 3);
        dz.ContentOffset = new Point2(-50, 0); // pan content left by 50 -> blue fills the centre/left
        img = T.Render(doc, page);
        T.AssertColor(img, 25, 50, Argb.Blue, 3);
        dz.ContentOffset = new Point2(0, 0);
        dz.ContentZoom = 2; // zoom around centre: centre boundary stays, content outside clipped
        img = T.Render(doc, page);
        T.AssertColor(img, 40, 50, Argb.Red, 3);
        T.AssertColor(img, 60, 50, Argb.Blue, 3);
        Assert.True(T.IsWhite(img.GetPixel(50, 10))); // zone is clipped to its bounds
    }

    [Fact]
    public void Sections_BackgroundsAndGraphicsRender()
    {
        var (doc, page) = T.Doc(100, 100);
        var sec = new TemplateSection { Bounds = new Rect2(0, 0, 100, 50), Background = Argb.FromRgb(0, 0, 255) };
        sec.Graphics.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30), Fill = FillStyle.Solid(Argb.Red), Stroke = StrokeStyle.None });
        page.Sections.Add(sec);
        var img = T.Render(doc, page);
        T.AssertColor(img, 70, 20, Argb.FromRgb(0, 0, 255), 1);
        T.AssertColor(img, 20, 20, Argb.Red, 1);
        Assert.True(T.IsWhite(img.GetPixel(70, 80)));
    }

    [Fact]
    public void ExcludeAndOnly_Filter()
    {
        var (doc, page) = T.Doc(60, 60);
        var a = new ShapeAnnotation { Start = new(0, 0), End = new(30, 30), Fill = FillStyle.Solid(Argb.Red), Stroke = StrokeStyle.None };
        var b = new ShapeAnnotation { Start = new(30, 30), End = new(60, 60), Fill = FillStyle.Solid(Argb.Blue), Stroke = StrokeStyle.None };
        page.Annotations.Add(a); page.Annotations.Add(b);
        var ex = T.Renderer.RenderPage(doc, page, new RenderOptions(1, true, new HashSet<Guid> { a.Id }));
        Assert.True(T.IsWhite(ex.GetPixel(10, 10)));
        T.AssertColor(ex, 40, 40, Argb.Blue, 1);
        var only = T.Renderer.RenderPage(doc, page, new RenderOptions(1) { Only = new HashSet<Guid> { a.Id } });
        T.AssertColor(only, 10, 10, Argb.Red, 1);
        Assert.True(T.IsWhite(only.GetPixel(40, 40)));
        var none = T.Renderer.RenderPage(doc, page, new RenderOptions(1, false));
        Assert.True(T.IsWhite(none.GetPixel(10, 10)));
    }

    [Fact]
    public void Thumbnail_FitsMaxSide()
    {
        var (doc, page) = T.Doc(400, 200);
        page.Annotations.Add(new ShapeAnnotation { Start = new(0, 0), End = new(400, 100), Fill = FillStyle.Solid(Argb.Red), Stroke = StrokeStyle.None });
        var t = T.Renderer.RenderThumbnail(doc, 100);
        Assert.Equal(100, t.Width);
        Assert.Equal(50, t.Height);
        T.AssertColor(t, 50, 10, Argb.Red, 2);
    }

    [Fact]
    public void RenderAnnotation_ToExternalCanvas_IncludingBlurFallback()
    {
        var (doc, page) = T.Doc(80, 60);
        var checker = T.Checker(80, 60, 2, Argb.Black, Argb.White);
        page.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(doc.Assets.AddImage(checker), Point2.Zero));
        var blur = new BlurAnnotation { Start = new(10, 10), End = new(70, 50), Mode = BlurKind.Gaussian, Intensity = 80 };
        page.Annotations.Add(blur);
        using var bmp = new SkiaSharp.SKBitmap(SkiaInterop.Info(80, 60));
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(SkiaSharp.SKColors.White);
        T.Renderer.RenderAnnotation(canvas, doc, page, blur);
        var img = SkiaInterop.ToBgra(bmp);
        Assert.InRange(img.GetPixel(40, 30).R, 80, 180);
        // pixels outside the region remain the canvas clear colour
        Assert.True(T.IsWhite(img.GetPixel(2, 2)));
    }

    [Fact]
    public void UnknownCalloutPreset_FallsBackToRoundedRectangle()
    {
        var c = new CalloutAnnotation { ShapePresetId = "does-not-exist", Start = new(10, 10), End = new(110, 70), Fill = FillStyle.Solid(Argb.Yellow), Stroke = StrokeStyle.None };
        var img = T.RenderOne(c, 120, 80);
        T.AssertColor(img, 60, 40, Argb.Yellow, 1);
        Assert.True(T.IsWhite(img.GetPixel(11, 11)));
    }
}
