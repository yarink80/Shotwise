using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Editor.Tests;

public class CombineImagesTests
{
    private static readonly Size2[] Three = { new(100, 50), new(200, 100), new(50, 50) };

    [Fact]
    public void Columns_CommonHeight_PreservesAspect()
    {
        var r = CombineImagesLayout.Compute(Three, new CombineLayoutOptions { Kind = CombineLayoutKind.Columns, Spacing = 10, Margin = 5 });
        Assert.All(r.Slots, s => Assert.Equal(100, s.Height));
        Assert.Equal(new Rect2(5, 5, 200, 100), r.Slots[0]);
        Assert.Equal(new Rect2(215, 5, 200, 100), r.Slots[1]);
        Assert.Equal(new Rect2(425, 5, 100, 100), r.Slots[2]);
        Assert.Equal(new Size2(530, 110), r.CanvasSize);
        Assert.Null(r.CaptionRect);
    }

    [Fact]
    public void Rows_CommonWidth_Stacked()
    {
        var r = CombineImagesLayout.Compute(Three, new CombineLayoutOptions { Kind = CombineLayoutKind.Rows, Spacing = 10, Margin = 0 });
        Assert.All(r.Slots, s => Assert.Equal(200, s.Width));
        Assert.Equal(new[] { 0.0, 110.0, 220.0 }, r.Slots.Select(s => s.Y));
        Assert.Equal(new Size2(200, 420), r.CanvasSize);
    }

    [Fact]
    public void Grid_NearSquare_UniformCells()
    {
        var sizes = Enumerable.Repeat(new Size2(100, 80), 5).ToArray();
        var r = CombineImagesLayout.Compute(sizes, new CombineLayoutOptions { Kind = CombineLayoutKind.Grid, Spacing = 10, Margin = 10 });
        Assert.Equal(new Rect2(10, 10, 100, 80), r.Slots[0]);
        Assert.Equal(new Rect2(230, 10, 100, 80), r.Slots[2]);
        Assert.Equal(new Rect2(10, 100, 100, 80), r.Slots[3]); // 3 columns x 2 rows
        Assert.Equal(new Size2(340, 190), r.CanvasSize);
    }

    [Fact]
    public void FeatureLeft_And_FeatureTop()
    {
        var left = CombineImagesLayout.Compute(Three, new CombineLayoutOptions { Kind = CombineLayoutKind.FeatureLeft, Spacing = 10, Margin = 0 });
        Assert.Equal(new Rect2(0, 0, 100, 50), left.Slots[0]);
        Assert.Equal(110, left.Slots[1].X);
        Assert.Equal(20, left.Slots[1].Height); // (50 - 10) / 2
        Assert.Equal(30, left.Slots[2].Y);
        var top = CombineImagesLayout.Compute(Three, new CombineLayoutOptions { Kind = CombineLayoutKind.FeatureTop, Spacing = 10, Margin = 0 });
        Assert.Equal(new Rect2(0, 0, 100, 50), top.Slots[0]);
        Assert.Equal(60, top.Slots[1].Y);
        Assert.Equal(45, top.Slots[1].Width);
    }

    [Fact]
    public void MaxSlotSide_CapsHugeImages()
    {
        var r = CombineImagesLayout.Compute(new[] { new Size2(4000, 2000), new Size2(4000, 2000) }, new CombineLayoutOptions { Kind = CombineLayoutKind.Columns, MaxSlotSide = 500, Margin = 0, Spacing = 0 });
        Assert.All(r.Slots, s => Assert.Equal(500, s.Height));
        Assert.Equal(1000, r.Slots[0].Width);
    }

    [Fact]
    public void Caption_AddsFooterStrip()
    {
        var r = CombineImagesLayout.Compute(Three, new CombineLayoutOptions { Kind = CombineLayoutKind.Columns, Spacing = 10, Margin = 5, Caption = "Before / after", CaptionHeight = 40 });
        Assert.NotNull(r.CaptionRect);
        Assert.Equal(new Rect2(5, 115, 520, 40), r.CaptionRect);
        Assert.Equal(160, r.CanvasSize.Height);
    }

    [Fact]
    public void Compute_RejectsEmptyInput()
        => Assert.Throws<ArgumentException>(() => CombineImagesLayout.Compute(Array.Empty<Size2>(), new CombineLayoutOptions()));

    [Fact]
    public void CreateDocument_BuildsSectionsWithFilledDropZones_AndCaption()
    {
        var imgs = new[] { BgraImage.CreateFilled(100, 50, Argb.Red), BgraImage.CreateFilled(50, 50, Argb.Blue) };
        var doc = CombineImagesLayout.CreateDocument(imgs, new CombineLayoutOptions { Kind = CombineLayoutKind.Grid, Caption = "Hi", Background = Argb.LightGray }, new TestCodec());
        var page = doc.Pages.Single();
        Assert.Equal(Argb.LightGray, page.Background.Color);
        Assert.Equal(3, page.Sections.Count);
        var zones = page.Sections.SelectMany(s => s.Graphics).OfType<DropZoneAnnotation>().ToList();
        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.True(doc.Assets.Contains(z.Content!.Value)));
        Assert.All(zones, z => Assert.Equal(page.Sections.Single(s => s.Graphics.Contains(z)).Id, z.SectionId));
        // grid cells are 100x50; the 50x50 image fits (zoom < 1 keeps it letterboxed)
        Assert.Equal(0.5, zones[1].ContentZoom, 6);
        var caption = page.Sections.Single(s => s.Type == SectionType.Footer).Graphics.OfType<TextAnnotation>().Single();
        Assert.Equal("Hi", caption.Text.PlainText);
        Assert.Equal(HorizontalAlign.Center, caption.TextStyle.HAlign);
    }

    [Fact]
    public void CombinedDocument_SupportsSwapInSession()
    {
        var imgs = new[] { BgraImage.CreateFilled(100, 50, Argb.Red), BgraImage.CreateFilled(100, 50, Argb.Blue) };
        var doc = CombineImagesLayout.CreateDocument(imgs, new CombineLayoutOptions { Kind = CombineLayoutKind.Columns }, new TestCodec());
        using var s = new EditorSession(doc, new EditorServices(new TestRenderer(), new TestEffects(), new TestCodec()));
        var zones = doc.Pages[0].Sections.SelectMany(x => x.Graphics).OfType<DropZoneAnnotation>().ToList();
        var a = zones[0].Content;
        var b = zones[1].Content;
        s.SwapDropZoneContent(zones[0].Id, zones[1].Id);
        Assert.Equal(b, ((DropZoneAnnotation)doc.Pages[0].Find(zones[0].Id)!).Content);
        Assert.Equal(a, ((DropZoneAnnotation)doc.Pages[0].Find(zones[1].Id)!).Content);
    }
}
