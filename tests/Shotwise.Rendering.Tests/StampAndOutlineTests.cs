using System.Text;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class StampAndOutlineTests
{
    private const string RedSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\" viewBox=\"0 0 100 50\"><rect width=\"100\" height=\"50\" fill=\"#ff0000\"/></svg>";

    [Fact]
    public void SvgStamp_RendersVectorAndPreservesAspect()
    {
        var (doc, page) = T.Doc(200, 200);
        var id = doc.Assets.Add(Encoding.UTF8.GetBytes(RedSvg), "image/svg+xml");
        page.Annotations.Add(new StampAnnotation { Asset = id, Start = new(0, 0), End = new(200, 200), PreserveAspect = true });
        var img = T.Render(doc, page);
        // 2:1 artwork centred in a square box: red band between y=50..150
        T.AssertColor(img, 100, 100, Argb.FromRgb(255, 0, 0), 2);
        Assert.True(T.IsWhite(img.GetPixel(100, 20)));
        Assert.True(T.IsWhite(img.GetPixel(100, 180)));
    }

    [Fact]
    public void SvgStamp_StretchWhenAspectNotPreserved()
    {
        var (doc, page) = T.Doc(200, 200);
        var id = doc.Assets.Add(Encoding.UTF8.GetBytes(RedSvg), "image/svg+xml");
        page.Annotations.Add(new StampAnnotation { Asset = id, Start = new(0, 0), End = new(200, 200), PreserveAspect = false });
        var img = T.Render(doc, page);
        T.AssertColor(img, 100, 20, Argb.FromRgb(255, 0, 0), 2);
        T.AssertColor(img, 100, 180, Argb.FromRgb(255, 0, 0), 2);
    }

    [Fact]
    public void SvgStamp_IsCrispAtHighScale()
    {
        var (doc, page) = T.Doc(100, 50);
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"10\" fill=\"#000000\"/></svg>";
        var id = doc.Assets.Add(Encoding.UTF8.GetBytes(svg), "image/svg+xml");
        page.Annotations.Add(new StampAnnotation { Asset = id, Start = new(0, 0), End = new(100, 50), PreserveAspect = false });
        var img = T.Renderer.RenderPage(doc, page, new RenderOptions(4));
        // vector edge at x = 50 * 4 = 200 stays pixel exact
        Assert.True(img.GetPixel(198, 100).R < 10);
        Assert.True(img.GetPixel(202, 100).R > 245);
    }

    [Fact]
    public void PdfStamp_IsRasterizedThroughWindowsDataPdf()
    {
        var src = BgraImage.CreateFilled(200, 100, Argb.FromRgb(0, 0, 255));
        var pdf = PdfExporter.Build(new[] { src }, new PdfPageOptions());
        var (doc, page) = T.Doc(200, 200);
        var id = doc.Assets.Add(pdf, "application/pdf");
        page.Annotations.Add(new StampAnnotation { Asset = id, Start = new(0, 0), End = new(200, 200), PreserveAspect = true });
        var img = T.Render(doc, page);
        var c = img.GetPixel(100, 100);
        Assert.True(c.B > 200 && c.R < 60, c.ToString());
        Assert.True(T.IsWhite(img.GetPixel(100, 10)));
    }

    [Fact]
    public void MissingStampAsset_DrawsPlaceholderWithoutThrowing()
    {
        var (doc, page) = T.Doc(100, 100);
        page.Annotations.Add(new StampAnnotation { Asset = AssetId.New(), Start = new(10, 10), End = new(90, 90) });
        var img = T.Render(doc, page);
        Assert.True(T.CountNonWhite(img) > 50);
    }

    // ---------------------------------------------------------------- bounds & outline

    [Fact]
    public void MeasureBounds_IncludesStrokeAndRotation()
    {
        var s = new ShapeAnnotation { Start = new(100, 100), End = new(200, 120), Stroke = new StrokeStyle(Argb.Black, 10) };
        var b = T.Renderer.MeasureBounds(s);
        Assert.Equal(95, b.X, 1);
        Assert.Equal(110, b.Width, 1);
        s.RotationDegrees = 90;
        var r = T.Renderer.MeasureBounds(s);
        Assert.True(r.Height > 100 && r.Width < 40, r.ToString());
        Assert.Equal(150, r.Center.X, 1);
        Assert.Equal(110, r.Center.Y, 1);
    }

    [Fact]
    public void MeasureBounds_PenInflatesByNibRadius()
    {
        var p = new PenAnnotation { Stroke = new StrokeStyle(Argb.Black, 20), Smoothing = false };
        p.Points.AddRange(new[] { new Point2(50, 50), new Point2(100, 50) });
        var b = T.Renderer.MeasureBounds(p);
        Assert.True(b.X <= 40 && b.Right >= 110 && b.Height >= 20);
    }

    [Fact]
    public void MeasureBounds_TextBoxGrowsToLayoutContent()
    {
        var t = new TextAnnotation { Start = new(10, 10), End = new(20, 20), Text = RichText.FromPlain("A fairly long line that overflows", "Segoe UI", 20) };
        t.TextStyle = TextLayoutStyle.Default with { AutoSize = true };
        // with a 10px wide box the text wraps into a tall column
        var b = T.Renderer.MeasureBounds(t);
        Assert.True(b.Height > 40);
    }

    [Fact]
    public void Outline_UnfilledShape_HitsEdgeNotCentre()
    {
        var s = new ShapeAnnotation { Start = new(20, 20), End = new(120, 80), Stroke = new StrokeStyle(Argb.Black, 4) };
        var o = T.Renderer.GetOutline(s);
        Assert.True(o.HitTest(new Point2(20, 50), 2));
        Assert.False(o.HitTest(new Point2(70, 50), 2));
        s.Fill = FillStyle.Solid(Argb.Red);
        Assert.True(T.Renderer.GetOutline(s).HitTest(new Point2(70, 50), 2));
    }

    [Fact]
    public void Outline_RotatedRectangle_FollowsRotation()
    {
        var s = new ShapeAnnotation { Start = new(100, 100), End = new(200, 120), Fill = FillStyle.Solid(Argb.Red), RotationDegrees = 90 };
        var o = T.Renderer.GetOutline(s);
        Assert.True(o.HitTest(new Point2(150, 60), 1));   // inside the rotated (tall) rect
        Assert.False(o.HitTest(new Point2(110, 110), 1)); // inside the unrotated rect only
    }

    [Fact]
    public void Outline_Arrow_HitsShaftAndHeadOnly()
    {
        var a = new ArrowAnnotation { Start = new(20, 100), End = new(180, 100), Stroke = new StrokeStyle(Argb.Black, 4) };
        var o = T.Renderer.GetOutline(a);
        Assert.True(o.HitTest(new Point2(80, 101), 2));
        Assert.True(o.HitTest(new Point2(172, 96), 1)); // head body
        Assert.False(o.HitTest(new Point2(80, 130), 3));
    }

    [Fact]
    public void Outline_BezierArrow_FollowsCurve()
    {
        var a = new ArrowAnnotation { Start = new(20, 100), End = new(180, 100), Bezier = true, Control1 = new(60, 20), Control2 = new(140, 20), Stroke = new StrokeStyle(Argb.Black, 4) };
        var o = T.Renderer.GetOutline(a);
        Assert.True(o.HitTest(new Point2(100, 40), 2));
        Assert.False(o.HitTest(new Point2(100, 100), 2));
    }

    [Fact]
    public void Outline_Pen_PolylineHit()
    {
        var p = new PenAnnotation { Stroke = new StrokeStyle(Argb.Black, 4), Smoothing = false };
        p.Points.AddRange(new[] { new Point2(10, 10), new Point2(100, 10), new Point2(100, 80) });
        var o = T.Renderer.GetOutline(p);
        Assert.True(o.HitTest(new Point2(50, 11), 2));
        Assert.False(o.HitTest(new Point2(50, 60), 2));
        Assert.True(o.IntersectsRect(new Rect2(90, 40, 20, 20)));
    }

    [Fact]
    public void Outline_Ellipse_ExcludesCorners()
    {
        var s = new ShapeAnnotation { Shape = ShapeKind.Ellipse, Start = new(0, 0), End = new(100, 100), Fill = FillStyle.Solid(Argb.Red) };
        var o = T.Renderer.GetOutline(s);
        Assert.True(o.HitTest(new Point2(50, 50), 0));
        Assert.False(o.HitTest(new Point2(5, 5), 0));
    }

    [Fact]
    public void Outline_BlurAndStamp_AreFilledRects()
    {
        foreach (Annotation a in new Annotation[]
                 {
                     new BlurAnnotation { Start = new(10, 10), End = new(60, 60) },
                     new StampAnnotation { Start = new(10, 10), End = new(60, 60) },
                     new ImageAnnotation { Start = new(10, 10), End = new(60, 60) },
                     new TextAnnotation { Start = new(10, 10), End = new(60, 60) },
                 })
            Assert.True(T.Renderer.GetOutline(a).HitTest(new Point2(35, 35), 0), a.Kind.ToString());
    }

    [Fact]
    public void Outline_Magnify_CoversLensAndSource()
    {
        var m = new MagnifyAnnotation { Start = new(100, 10), End = new(180, 90), Scale = 3, SourceRect = new Rect2(10, 10, 30, 30) };
        var o = T.Renderer.GetOutline(m);
        Assert.True(o.HitTest(new Point2(140, 50), 1));
        Assert.True(o.HitTest(new Point2(10, 25), 2));
        Assert.True(T.Renderer.MeasureBounds(m).X <= 10);
    }

    [Fact]
    public void Outline_Step_WithPointer_IncludesTail()
    {
        var s = new StepAnnotation { Start = new(20, 20), End = new(60, 60), Style = StepStyle.CirclePointer, PointerTip = new Point2(120, 120) };
        var o = T.Renderer.GetOutline(s);
        Assert.True(o.HitTest(new Point2(100, 100), 1));
        Assert.True(o.HitTest(new Point2(40, 40), 0));
    }
}
