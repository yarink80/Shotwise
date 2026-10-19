using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Editor.Tests;

public class HitTestingTests
{
    [Fact]
    public void HitTest_ReturnsTopmostAnnotation()
    {
        using var k = new TestKit();
        var bottom = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(80, 80) });
        var top = k.Add(new ShapeAnnotation { Start = new(40, 40), End = new(120, 120) });
        Assert.Equal(top.Id, k.S.HitTest(new Point2(50, 50), 1)!.Id);
        Assert.Equal(bottom.Id, k.S.HitTest(new Point2(20, 20), 1)!.Id);
        Assert.Null(k.S.HitTest(new Point2(150, 20), 1));
        Assert.Equal(new[] { top.Id, bottom.Id }, k.S.HitTestAll(new Point2(50, 50), 1).Select(a => a.Id));
    }

    [Fact]
    public void HitTest_RotatedObject_UsesRotatedOutline()
    {
        using var k = new TestKit();
        // 100x20 bar centred at (100,75), rotated 90° → vertical bar
        var bar = k.Add(new ShapeAnnotation { Start = new(50, 65), End = new(150, 85), RotationDegrees = 90 });
        Assert.NotNull(k.S.HitTest(new Point2(100, 30), 1));   // inside rotated bar (top part)
        Assert.Null(k.S.HitTest(new Point2(60, 75), 1));        // inside unrotated bar only
        Assert.Equal(bar.Id, k.S.HitTest(new Point2(100, 120), 1)!.Id);
    }

    [Fact]
    public void HitTest_RotatedObject_FallbackWithoutOutline()
    {
        using var k = new TestKit();
        k.Renderer.EmptyOutlines = true;
        k.Add(new ShapeAnnotation { Start = new(50, 65), End = new(150, 85), RotationDegrees = 90 });
        Assert.NotNull(k.S.HitTest(new Point2(100, 30), 1));
        Assert.Null(k.S.HitTest(new Point2(60, 75), 1));
    }

    [Fact]
    public void HitTest_LineTolerance_ScalesWithZoom()
    {
        using var k = new TestKit();
        var line = k.Add(new LineAnnotation { Start = new(10, 50), End = new(190, 50), Stroke = new StrokeStyle(Argb.Red, 1) });
        Assert.Equal(line.Id, k.S.HitTest(new Point2(100, 53.5), 1)?.Id);   // 3.5 px < 4/1
        Assert.Null(k.S.HitTest(new Point2(100, 53.5), 4));                  // tolerance 1 px at 400 %
        Assert.Equal(line.Id, k.S.HitTest(new Point2(100, 50.8), 4)?.Id);
        Assert.Equal(line.Id, k.S.HitTest(new Point2(100, 60), 0.25)?.Id);   // 16 px at 25 %
    }

    [Fact]
    public void HitTest_ThickStroke_WidensTolerance()
    {
        using var k = new TestKit();
        var line = k.Add(new LineAnnotation { Start = new(10, 50), End = new(190, 50), Stroke = new StrokeStyle(Argb.Red, 20) });
        Assert.Equal(line.Id, k.S.HitTest(new Point2(100, 59), 4)?.Id); // half stroke = 10
        Assert.Null(k.S.HitTest(new Point2(100, 62), 4));
    }

    [Fact]
    public void HitTest_IncludesSectionGraphics_BelowTopLevel()
    {
        using var k = new TestKit();
        var section = new TemplateSection { Bounds = new Rect2(0, 0, 200, 150) };
        var zone = new DropZoneAnnotation { SectionId = section.Id, Start = new(0, 0), End = new(200, 150) };
        section.Graphics.Add(zone);
        k.Page.Sections.Add(section);
        Assert.Equal(zone.Id, k.S.HitTest(new Point2(20, 20), 1)?.Id);
        var shape = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(40, 40) });
        Assert.Equal(shape.Id, k.S.HitTest(new Point2(20, 20), 1)?.Id);
    }

    [Fact]
    public void HitTestRect_ReturnsIntersectingTopLevelObjects()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        var b = k.Add(new LineAnnotation { Start = new(100, 10), End = new(150, 60) });
        k.Add(new ShapeAnnotation { Start = new(160, 100), End = new(190, 140) });
        var hits = k.S.HitTestRect(new Rect2(0, 0, 120, 40)).Select(x => x.Id).ToHashSet();
        Assert.Equal(new HashSet<Guid> { a.Id, b.Id }, hits);
    }
}
