using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Xunit;

namespace Shotwise.Editor.Tests;

/// <summary>Foundation smoke tests: the public editor API is callable with fakes and never throws in normal use.</summary>
public class EditorSessionSmokeTests
{
    internal sealed class FakeRenderer : IDocumentRenderer
    {
        public BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o)
            => BgraImage.CreateFilled(Math.Max(1, (int)page.CanvasSize.Width), Math.Max(1, (int)page.CanvasSize.Height), Argb.White);
        public void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a) { }
        public BgraImage RenderThumbnail(ShotDocument doc, int maxSide) => BgraImage.CreateFilled(4, 4, Argb.White);
        public Rect2 MeasureBounds(Annotation a) => a.RotatedBounds;
        public TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth) => TextLayoutInfo.Empty;
        public Geometry2 GetOutline(Annotation a) => Geometry2.FromRect(a.Bounds, filled: true, a.Stroke.Width);
    }

    internal sealed class FakeEffects : IEffectProcessor
    {
        public EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md)
        {
            var dst = new BgraImage(src.Width + 4, src.Height + 4);
            dst.Blit(src, 2, 2);
            return new EffectResult(dst, new Point2(2, 2));
        }
        public EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md) => new(src, Point2.Zero);
        public BgraImage Preview(BgraImage src, EffectSpec e, int maxSide) => src;
    }

    internal sealed class FakeCodec : IImageCodec
    {
        public BgraImage Decode(ReadOnlySpan<byte> data) => PngCodec.Decode(data);
        public BgraImage Load(string path) => PngCodec.Decode(File.ReadAllBytes(path));
        public IReadOnlyList<BgraImage> LoadAllPages(string path) => new[] { Load(path) };
        public byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null) => PngCodec.Encode(img);
        public void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null) => File.WriteAllBytes(path, Encode(img, fmt, o));
        public void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null) => throw new NotSupportedException();
        public IReadOnlyList<ImageFormatInfo> SupportedFormats { get; } = Array.Empty<ImageFormatInfo>();
    }

    private static EditorSession NewSession(out ShotDocument doc)
    {
        doc = ShotDocument.FromImage(BgraImage.CreateFilled(100, 80, Argb.Gray));
        return new EditorSession(doc, new EditorServices(new FakeRenderer(), new FakeEffects(), new FakeCodec()));
    }

    [Fact]
    public void AllToolsAndInput_DoNotThrow()
    {
        using var s = NewSession(out _);
        foreach (var tool in Enum.GetValues<ToolKind>())
        {
            s.ActiveTool = tool;
            Assert.Equal(tool, s.CurrentTool.Kind);
            var p = new PointerInput(new Point2(10, 10), PointerButton.Left, ModifierKeys.None, 1);
            s.PointerDown(p);
            s.PointerMove(p with { Position = new Point2(20, 20) });
            s.PointerUp(p with { Position = new Point2(20, 20) });
            s.DoubleClick(p);
            s.PointerLeave();
            s.KeyDown(new KeyInput(VirtualKey.A, ModifierKeys.None));
            _ = s.GetOverlay(1.0);
            _ = s.CurrentCursor;
        }
    }

    [Fact]
    public async Task MenuCommands_WorkThroughUndo()
    {
        using var s = NewSession(out var doc);
        var page = s.ActivePage;
        var a = new ShapeAnnotation { Start = new(10, 10), End = new(20, 20) };
        var b = new ShapeAnnotation { Start = new(40, 10), End = new(50, 30) };
        var c = new ShapeAnnotation { Start = new(70, 10), End = new(90, 20) };
        s.Execute(new AddAnnotationsCommand(page.Id, new Annotation[] { a, b, c }, "Add"));

        var invalidations = 0;
        s.Invalidated += (_, _) => invalidations++;

        s.SelectAll();
        Assert.Equal(3, s.Selection.Count);
        Assert.NotEmpty(s.GetOverlay(2.0).OfType<OverlayHandle>());
        s.Align(AlignKind.Top);
        Assert.All(page.Annotations, x => Assert.Equal(10, x.Bounds.Y));
        s.Distribute(horizontal: true);
        s.Group();
        Assert.NotNull(page.Annotations[0].GroupId);
        s.Ungroup();
        s.BringToFront();
        s.SendToBack();
        s.Lock(true);
        s.Lock(false);
        s.ModifySelection(x => x.Opacity = 0.5, "Opacity");
        Assert.All(page.Annotations, x => Assert.Equal(0.5, x.Opacity));

        var payload = s.Copy();
        Assert.NotNull(payload);
        Assert.NotNull(ClipboardPayload.FromJson(payload!.ToJson()));
        s.Paste(payload, null);
        Assert.Equal(6, page.Annotations.Count);
        s.Duplicate();
        Assert.Equal(9, page.Annotations.Count);
        s.InvertSelection();
        s.DeleteSelection();
        s.Undo.Undo();

        s.Selection.Select(a.Id);
        s.KeyDown(new KeyInput(VirtualKey.Right, ModifierKeys.None));
        s.KeyDown(new KeyInput(VirtualKey.Right, ModifierKeys.None));
        Assert.Equal(12, page.Find(a.Id)!.Start.X);

        s.ApplyStyleToSelection(new QuickStyle { Tool = ToolKind.Shape, Prototype = new ShapeAnnotation { Stroke = new StrokeStyle(Argb.Blue, 7) } });
        Assert.Equal(7, page.Find(a.Id)!.Stroke.Width);

        Assert.Equal(a.Id, s.HitTest(new Point2(15, 15), 1)?.Id);

        s.ResizeCanvas(new Size2(120, 100), Anchor9.TopLeft, Argb.White);
        s.Crop(new Rect2(0, 0, 110, 90));
        await s.ApplyEffectAsync(EffectDefaults.Border);
        Assert.Equal(new Size2(114, 94), page.CanvasSize);
        s.CutOut(CutOutOrientation.Horizontal, 40, 50);
        Assert.Equal(84, page.CanvasSize.Height);
        s.SelectAll();
        s.Flatten(all: false);
        s.Flatten(all: true);
        Assert.Empty(page.Annotations);

        while (s.Undo.CanUndo) s.Undo.Undo();
        Assert.Equal(new Size2(100, 80), page.CanvasSize);
        Assert.Empty(page.Annotations);
        Assert.True(invalidations > 0);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Paste_ImportsAssets_AndRemapsIds()
    {
        using var s1 = NewSession(out var doc1);
        using var s2 = NewSession(out var doc2);
        var asset = doc1.Assets.AddImage(BgraImage.CreateFilled(3, 3, Argb.Red));
        var img = new ImageAnnotation { Asset = asset, Start = new(0, 0), End = new(3, 3) };
        s1.Execute(new AddAnnotationCommand(s1.ActivePage.Id, img));
        s1.Selection.Select(img.Id);
        var payload = ClipboardPayload.FromJson(s1.Copy()!.ToJson())!;
        s2.Paste(payload, new Point2(50, 50));
        var pasted = Assert.IsType<ImageAnnotation>(Assert.Single(s2.ActivePage.Annotations));
        Assert.NotEqual(img.Id, pasted.Id);
        Assert.True(doc2.Assets.Contains(pasted.Asset));
        Assert.Equal(new Point2(50, 50), pasted.Bounds.Center);
    }
}
