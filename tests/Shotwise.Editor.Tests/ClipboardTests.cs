using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Xunit;

namespace Shotwise.Editor.Tests;

public class ClipboardTests
{
    [Fact]
    public void Copy_ProducesAnnotations_Assets_FlattenedBitmap_AndText()
    {
        using var k = new TestKit();
        var asset = k.Doc.Assets.AddImage(BgraImage.CreateFilled(4, 4, Argb.Red));
        var img = k.Add(new ImageAnnotation { Asset = asset, Start = new(10, 10), End = new(40, 30) });
        var txt = k.Add(new TextAnnotation { Start = new(60, 10), End = new(100, 30), Text = RichText.FromPlain("hello"), Fill = FillStyle.Solid(Argb.Blue) });
        k.S.Selection.Set(new[] { img.Id, txt.Id });
        var p = k.S.Copy();
        Assert.NotNull(p);
        Assert.Equal(2, p!.Annotations.Count);
        Assert.Single(p.Assets);
        Assert.Equal("hello", p.Text);
        Assert.Equal(new Rect2(10, 10, 90, 20), p.Bounds);
        Assert.NotNull(p.Image);
        Assert.Equal(90, p.Image!.Width);
        Assert.True(k.Renderer.LastOptions!.TransparentBackground);
        Assert.Equal(2, k.Renderer.LastOptions.Only!.Count);
        var round = ClipboardPayload.FromJson(p.ToJson());
        Assert.NotNull(round);
        Assert.Equal(2, round!.Annotations.Count);
        Assert.Equal(p.Assets[0].Data, round.Assets[0].Data);
    }

    [Fact]
    public void Copy_NothingSelected_ReturnsNull()
    {
        using var k = new TestKit();
        Assert.Null(k.S.Copy());
        Assert.Null(ClipboardPayload.FromJson("{\"format\":\"other\"}"));
        Assert.Null(ClipboardPayload.FromJson("not json"));
    }

    [Fact]
    public void Paste_AtPosition_CentresContent_SelectsIt()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var p = k.S.Copy()!;
        k.S.Paste(p, new Point2(100, 100));
        var pasted = Assert.Single(k.S.SelectedAnnotations());
        Assert.NotEqual(a.Id, pasted.Id);
        Assert.Equal(new Point2(100, 100), pasted.Bounds.Center);
    }

    [Fact]
    public void Paste_SamePlace_OffsetsWhenOriginalPresent()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var p = k.S.Copy()!;
        k.S.Paste(p, null);
        Assert.Equal(new Point2(20, 20), k.S.SelectedAnnotations()[0].Start);
        k.S.Selection.Select(a.Id);
        k.S.DeleteSelection();
        var other = new TestKit();
        using var _ = other;
        other.S.Paste(p, null);
        Assert.Equal(new Point2(10, 10), other.S.SelectedAnnotations()[0].Start);
    }

    [Fact]
    public void Cut_RemovesObjects_PayloadPastesBack()
    {
        using var k = new TestKit();
        var a = k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.S.Selection.Select(a.Id);
        var p = k.S.Cut()!;
        Assert.Empty(k.Page.Annotations);
        k.S.Paste(p, null);
        Assert.Single(k.Page.Annotations);
        Assert.Equal(new Point2(10, 10), k.Page.Annotations[0].Start);
    }

    [Fact]
    public void PasteImage_CreatesImageObject_HeldByUndo()
    {
        using var k = new TestKit();
        k.S.Paste(ClipboardPayload.FromImage(BgraImage.CreateFilled(20, 10, Argb.Red)), new Point2(50, 50));
        var img = Assert.IsType<ImageAnnotation>(Assert.Single(k.Page.Annotations));
        Assert.Equal(new Rect2(40, 45, 20, 10), img.Bounds);
        Assert.True(k.Doc.Assets.GetRefCount(img.Asset) > 0);
        k.S.Undo.Undo();
        Assert.Empty(k.Page.Annotations);
        k.S.Undo.Redo();
        Assert.True(k.Doc.Assets.Contains(((ImageAnnotation)k.Page.Annotations[0]).Asset));
    }

    [Fact]
    public void PasteImage_AutoFlattenPreference_DrawsIntoBase()
    {
        using var k = new TestKit();
        k.Settings.Current.Editor.AutoFlattenPastedImages = true;
        k.S.Paste(ClipboardPayload.FromImage(BgraImage.CreateFilled(20, 10, Argb.Red)), new Point2(50, 50));
        Assert.Empty(k.Page.Annotations);
        Assert.Equal(Argb.Red, k.Base().GetPixel(45, 50));
    }

    [Fact]
    public void PasteImage_LargerThanCanvas_ScaledWhenNotKeepingDimensions()
    {
        using var k = new TestKit();
        k.Settings.Current.Editor.KeepOriginalImageDimensions = false;
        k.S.Paste(ClipboardPayload.FromImage(BgraImage.CreateFilled(400, 150, Argb.Red)), null);
        var img = Assert.Single(k.Page.Annotations);
        Assert.Equal(200, img.Bounds.Width, 6);
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
    }

    [Fact]
    public void PasteImage_LargerThanCanvas_AutoExpandsCanvas()
    {
        using var k = new TestKit();
        k.S.Paste(ClipboardPayload.FromImage(BgraImage.CreateFilled(300, 100, Argb.Red)), null);
        Assert.Equal(new Size2(300, 150), k.Page.CanvasSize);
        Assert.Equal(new Rect2(0, 25, 300, 100), k.Page.Annotations[0].Bounds);
        k.S.Undo.Undo();
        Assert.Equal(new Size2(200, 150), k.Page.CanvasSize);
        Assert.Empty(k.Page.Annotations);
    }

    [Fact]
    public void PasteText_CreatesTextBox_WithToolStyleFormat()
    {
        using var k = new TestKit();
        k.S.SetToolStyle(ToolKind.Text, new QuickStyle { Tool = ToolKind.Text, Prototype = new TextAnnotation { Text = RichText.FromPlain("", "Consolas", 20, Argb.Blue) } });
        k.S.Paste(new ClipboardPayload(Array.Empty<Annotation>(), Array.Empty<Shotwise.Core.Documents.EmbeddedAsset>(), Rect2.Empty, null, "line1\nline2"), new Point2(100, 75));
        var t = Assert.IsType<TextAnnotation>(Assert.Single(k.Page.Annotations));
        Assert.Equal("line1\nline2", t.Text.PlainText);
        Assert.Equal("Consolas", t.Text.Runs[0].FontFamily);
        Assert.Equal(new Point2(100, 75), t.Bounds.Center);
    }

    [Fact]
    public void CopyAll_ContainsWholePageImage_AndAllAnnotations()
    {
        using var k = new TestKit();
        k.Add(new ShapeAnnotation { Start = new(10, 10), End = new(30, 30) });
        k.Add(new ShapeAnnotation { Start = new(50, 10), End = new(60, 30) });
        var p = k.S.CopyAll();
        Assert.Equal(2, p.Annotations.Count);
        Assert.Equal(200, p.Image!.Width);
        Assert.Equal(150, p.Image.Height);
        Assert.Equal(k.Page.CanvasRect, p.Bounds);
    }

    [Fact]
    public void Paste_IntoOtherDocument_ImportsAssets()
    {
        using var k1 = new TestKit();
        using var k2 = new TestKit();
        var asset = k1.Doc.Assets.AddImage(BgraImage.CreateFilled(3, 3, Argb.Red));
        var img = k1.Add(new StampAnnotation { Asset = asset, Start = new(0, 0), End = new(3, 3) });
        k1.S.Selection.Select(img.Id);
        var payload = ClipboardPayload.FromJson(k1.S.Copy()!.ToJson())!;
        k2.S.Paste(payload, new Point2(50, 50));
        var pasted = Assert.IsType<StampAnnotation>(Assert.Single(k2.Page.Annotations));
        Assert.True(k2.Doc.Assets.Contains(pasted.Asset));
    }
}
