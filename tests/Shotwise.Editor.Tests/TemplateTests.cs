using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;
using static Shotwise.Editor.Tests.TestKit;

namespace Shotwise.Editor.Tests;

public class TemplateTests
{
    /// <summary>Canvas 200x300 with a header (0..100) holding a drop zone and a body (100..200) holding a drop zone + caption.</summary>
    private static (TestKit K, TemplateSection Header, TemplateSection Body, DropZoneAnnotation Zone1, DropZoneAnnotation Zone2) Template()
    {
        var k = new TestKit(200, 300);
        k.S.SnappingEnabled = false;
        var header = new TemplateSection { Type = SectionType.Header, Bounds = new Rect2(0, 0, 200, 100) };
        var z1 = new DropZoneAnnotation { SectionId = header.Id, Start = new(10, 10), End = new(190, 90) };
        header.Graphics.Add(z1);
        var body = new TemplateSection { Type = SectionType.Body, Bounds = new Rect2(0, 100, 200, 100) };
        var z2 = new DropZoneAnnotation { SectionId = body.Id, Start = new(10, 110), End = new(110, 190) };
        body.Graphics.Add(z2);
        body.Graphics.Add(new TextAnnotation { Start = new(120, 110), End = new(190, 130), Text = RichText.FromPlain("Step") });
        k.Page.Sections.Add(header);
        k.Page.Sections.Add(body);
        return (k, header, body, z1, z2);
    }

    [Fact]
    public void DropImage_IntoZone_SetsContent_HeldByUndo()
    {
        var (k, _, _, z1, _) = Template();
        using var __ = k;
        Assert.True(k.S.DropImage(BgraImage.CreateFilled(40, 20, Argb.Red), new Point2(50, 50)));
        var z = (DropZoneAnnotation)k.Get(z1.Id);
        Assert.NotNull(z.Content);
        Assert.Equal(1, z.ContentZoom);
        Assert.True(k.Doc.Assets.GetRefCount(z.Content!.Value) > 0);
        Assert.Equal(new[] { z1.Id }, k.S.Selection.Ids);
        k.S.Undo.Undo();
        Assert.Null(((DropZoneAnnotation)k.Get(z1.Id)).Content);
    }

    [Fact]
    public void DropImage_OutsideZones_CreatesImageObject()
    {
        var (k, _, _, _, _) = Template();
        using var __ = k;
        Assert.False(k.S.DropImage(BgraImage.CreateFilled(10, 10, Argb.Red), new Point2(100, 250)));
        var img = Assert.IsType<ImageAnnotation>(k.Page.Annotations.Single());
        Assert.Equal(new Point2(100, 250), img.Bounds.Center);
    }

    [Fact]
    public void DragOnFilledZone_PansContent_OneStep()
    {
        var (k, _, _, z1, _) = Template();
        using var __ = k;
        k.S.SetDropZoneImage(z1.Id, BgraImage.CreateFilled(40, 20, Argb.Red));
        var pos = k.S.Undo.Position;
        k.Drag(50, 50, 70, 40);
        var z = (DropZoneAnnotation)k.Get(z1.Id);
        Assert.Equal(new Point2(20, -10), z.ContentOffset);
        Assert.Equal(new Point2(10, 10), z.Start); // the zone itself does not move
        Assert.Equal(pos + 1, k.S.Undo.Position);
        Assert.Equal("Pan image", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void EmptyZone_IsNotDraggable()
    {
        var (k, _, _, z1, _) = Template();
        using var __ = k;
        k.Drag(50, 50, 70, 40);
        Assert.Equal(new Point2(10, 10), k.Get(z1.Id).Start);
        Assert.Empty(k.S.GetOverlay(1).OfType<OverlayHandle>());
    }

    [Fact]
    public void Wheel_ZoomsSelectedZoneContent()
    {
        var (k, _, _, z1, _) = Template();
        using var __ = k;
        k.S.SetDropZoneImage(z1.Id, BgraImage.CreateFilled(40, 20, Argb.Red));
        Assert.True(k.S.MouseWheel(P(50, 50), 120));
        Assert.Equal(1.1, ((DropZoneAnnotation)k.Get(z1.Id)).ContentZoom, 6);
        k.S.MouseWheel(P(50, 50), 120);
        Assert.Equal(1.21, ((DropZoneAnnotation)k.Get(z1.Id)).ContentZoom, 6);
        k.S.Undo.Undo(); // merged into one step
        Assert.Equal(1, ((DropZoneAnnotation)k.Get(z1.Id)).ContentZoom, 6);
        Assert.False(k.S.MouseWheel(P(150, 250), 120));
    }

    [Fact]
    public void ZoomDropZone_ScalesOffset_AndClamps()
    {
        var (k, _, _, z1, _) = Template();
        using var __ = k;
        k.S.SetDropZoneImage(z1.Id, BgraImage.CreateFilled(40, 20, Argb.Red));
        k.S.PanDropZone(z1.Id, 10, 0);
        k.S.ZoomDropZone(z1.Id, 2);
        var z = (DropZoneAnnotation)k.Get(z1.Id);
        Assert.Equal(new Point2(20, 0), z.ContentOffset);
        k.S.ZoomDropZone(z1.Id, 1000);
        Assert.Equal(DropZoneLayout.MaxZoom, ((DropZoneAnnotation)k.Get(z1.Id)).ContentZoom, 6);
    }

    [Fact]
    public void SwapAndClear_DropZones()
    {
        var (k, _, _, z1, z2) = Template();
        using var __ = k;
        k.S.SetDropZoneImage(z1.Id, BgraImage.CreateFilled(4, 4, Argb.Red));
        var asset = ((DropZoneAnnotation)k.Get(z1.Id)).Content;
        k.S.SwapDropZoneContent(z1.Id, z2.Id);
        Assert.Null(((DropZoneAnnotation)k.Get(z1.Id)).Content);
        Assert.Equal(asset, ((DropZoneAnnotation)k.Get(z2.Id)).Content);
        k.S.Selection.Select(z2.Id);
        k.S.DeleteSelection(); // clears the zone instead of deleting it
        Assert.NotNull(k.Page.Find(z2.Id));
        Assert.Null(((DropZoneAnnotation)k.Get(z2.Id)).Content);
    }

    [Fact]
    public void DropZoneLayout_CoverFitAndContentRect()
    {
        var zone = new DropZoneAnnotation { Start = new(0, 0), End = new(100, 50) };
        var img = new Size2(200, 200);
        Assert.Equal(0.5, DropZoneLayout.CoverScale(zone.Bounds, img), 6);
        Assert.Equal(0.25, DropZoneLayout.FitScale(zone.Bounds, img), 6);
        Assert.Equal(new Rect2(0, -25, 100, 100), DropZoneLayout.ContentRect(zone, img));
        zone.ContentZoom = 2;
        zone.ContentOffset = new Point2(10, 0);
        Assert.Equal(new Rect2(-40, -75, 200, 200), DropZoneLayout.ContentRect(zone, img));
        Assert.Equal(0.5, DropZoneLayout.FitZoom(zone.Bounds, img), 6);
    }

    [Fact]
    public void AddSection_CopiesLastBodyLayout_GrowsCanvas_ShiftsBelow()
    {
        var (k, _, body, _, _) = Template();
        using var __ = k;
        var footerObj = k.Add(new ShapeAnnotation { Start = new(10, 220), End = new(50, 260) });
        var id = k.S.AddSection();
        Assert.NotNull(id);
        Assert.Equal(new Size2(200, 400), k.Page.CanvasSize);
        var added = k.Page.Sections.Single(s => s.Id == id);
        Assert.Equal(new Rect2(0, 200, 200, 100), added.Bounds);
        Assert.Equal(2, added.Graphics.Count);
        var zone = added.Graphics.OfType<DropZoneAnnotation>().Single();
        Assert.Equal(id, zone.SectionId);
        Assert.Equal(new Point2(10, 210), zone.Start);
        Assert.Null(zone.Content);
        Assert.Equal(new Point2(10, 320), k.Get(footerObj.Id).Start);
        Assert.Equal(3, k.Page.Sections.Count);
        k.S.Undo.Undo();
        Assert.Equal(new Size2(200, 300), k.Page.CanvasSize);
        Assert.Equal(2, k.Page.Sections.Count);
        Assert.Equal(new Point2(10, 220), k.Get(footerObj.Id).Start);
        _ = body;
    }

    [Fact]
    public void DuplicateSection_InsertsBelowReference()
    {
        var (k, header, body, _, _) = Template();
        using var __ = k;
        var id = k.S.DuplicateSection(header.Id);
        var dup = k.Page.Sections.Single(s => s.Id == id);
        Assert.Equal(new Rect2(0, 100, 200, 100), dup.Bounds);
        Assert.Equal(new Rect2(0, 200, 200, 100), k.Page.Sections.Single(s => s.Id == body.Id).Bounds);
        Assert.Equal(1, k.Page.Sections.IndexOf(dup));
        Assert.NotEqual(header.Graphics[0].Id, dup.Graphics[0].Id);
    }

    [Fact]
    public void RemoveSection_ShrinksCanvas_MovesContentUp()
    {
        var (k, header, body, _, _) = Template();
        using var __ = k;
        var obj = k.Add(new ShapeAnnotation { Start = new(10, 220), End = new(50, 260) });
        k.S.RemoveSection(header.Id);
        Assert.Equal(new Size2(200, 200), k.Page.CanvasSize);
        Assert.Equal(new Rect2(0, 0, 200, 100), k.Page.Sections.Single().Bounds);
        Assert.Equal(new Point2(10, 120), k.Get(obj.Id).Start);
        k.S.Undo.Undo();
        Assert.Equal(2, k.Page.Sections.Count);
        _ = body;
    }

    [Fact]
    public void MoveSection_SwapsVerticalPositions()
    {
        var (k, header, body, z1, z2) = Template();
        using var __ = k;
        k.S.MoveSection(body.Id, -1);
        Assert.Equal(new Rect2(0, 0, 200, 100), k.Page.Sections.Single(s => s.Id == body.Id).Bounds);
        Assert.Equal(new Rect2(0, 100, 200, 100), k.Page.Sections.Single(s => s.Id == header.Id).Bounds);
        Assert.Equal(new Point2(10, 10), k.Get(z2.Id).Start);
        Assert.Equal(new Point2(10, 110), k.Get(z1.Id).Start);
        k.S.MoveSection(body.Id, -1); // already at the top
        Assert.Equal("Move section up", k.S.Undo.UndoLabel);
    }

    [Fact]
    public void SectionAt_FindsSection()
    {
        var (k, header, body, _, _) = Template();
        using var __ = k;
        Assert.Equal(header.Id, k.S.SectionAt(new Point2(5, 5))!.Id);
        Assert.Equal(body.Id, k.S.SectionAt(new Point2(5, 150))!.Id);
        Assert.Null(k.S.SectionAt(new Point2(5, 250)));
    }

    [Fact]
    public void TemplateInstantiate_ThenEditInSession()
    {
        var def = new TemplateDefinition { Name = "two", Prototype = new Page { CanvasSize = new Size2(100, 100) } };
        var s = new TemplateSection { Bounds = new Rect2(0, 0, 100, 100) };
        s.Graphics.Add(new DropZoneAnnotation { SectionId = s.Id, Start = new(0, 0), End = new(100, 100) });
        def.Prototype.Sections.Add(s);
        var doc = def.Instantiate(new TestCodec());
        using var session = new EditorSession(doc, new EditorServices(new TestRenderer(), new TestEffects(), new TestCodec()));
        Assert.True(session.DropImage(BgraImage.CreateFilled(10, 10, Argb.Blue), new Point2(50, 50)));
        Assert.NotNull(session.DropZoneAt(new Point2(50, 50))!.Content);
    }
}
