using System.Text.Json;
using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Xunit;

namespace Shotwise.Core.Tests;

public class ColorAndGeometryTests
{
    [Theory]
    [InlineData("#80FF0000", 0x80FF0000u)]
    [InlineData("#FF0000", 0xFFFF0000u)]
    [InlineData("ff00ff00", 0xFF00FF00u)]
    [InlineData("#F00", 0xFFFF0000u)]
    [InlineData("#8F00", 0x88FF0000u)]
    [InlineData("0x11223344", 0x11223344u)]
    public void Argb_Parse(string s, uint expected) => Assert.Equal(new Argb(expected), Argb.Parse(s));

    [Fact]
    public void Argb_Hex_And_Invalid()
    {
        Assert.Equal("#0A0B0C0D", new Argb(0x0A0B0C0D).ToHex());
        Assert.False(Argb.TryParse("#12345", out _));
        Assert.False(Argb.TryParse("nope", out _));
        Assert.Equal("\"#FF112233\"", JsonSerializer.Serialize(new Argb(0xFF112233)));
        Assert.Equal(new Argb(0xFF112233), JsonSerializer.Deserialize<Argb>("\"#112233\""));
    }

    [Fact]
    public void Point_And_Rect_Json_AcceptLegacyForms()
    {
        Assert.Equal(new Point2(1.5, -2), JsonSerializer.Deserialize<Point2>("\"1.5,-2\""));
        Assert.Equal(new Point2(3, 4), JsonSerializer.Deserialize<Point2>("[3,4]"));
        Assert.Equal(new Point2(3, 4), JsonSerializer.Deserialize<Point2>("{\"X\":3,\"Y\":\"4\"}"));
        Assert.Equal(new Rect2(1, 2, 3, 4), JsonSerializer.Deserialize<Rect2>("\"1,2,3,4\""));
        Assert.Equal(new Rect2(1, 2, 3, 4), JsonSerializer.Deserialize<Rect2>("{\"x\":1,\"y\":2,\"w\":3,\"h\":4}"));
        Assert.Equal("{\"x\":1,\"y\":2,\"width\":3,\"height\":4}", JsonSerializer.Serialize(new Rect2(1, 2, 3, 4)));
    }

    [Fact]
    public void Rect_Operations()
    {
        var r = Rect2.FromPoints(new Point2(10, 10), new Point2(0, 5));
        Assert.Equal(new Rect2(0, 5, 10, 5), r);
        Assert.True(r.Contains(new Point2(5, 7)));
        Assert.Equal(new Rect2(5, 5, 5, 5), r.Intersect(new Rect2(5, 0, 20, 20)));
        Assert.Equal(Rect2.Empty, r.Intersect(new Rect2(100, 100, 1, 1)));
        Assert.Equal(new Rect2(0, 0, 10, 10), r.Union(new Rect2(0, 0, 1, 1)));
        Assert.Equal(new Rect2(-1, 4, 12, 7), r.Inflate(1));
        Assert.Equal(new RectI(0, 0, 3, 3), new Rect2(0.2, 0.1, 2.5, 2.2).ToRectIOutward());
        Assert.Equal(new Rect2(0, 0, 4, 2), new Rect2(4, 2, -4, -2).Normalize());
    }

    [Fact]
    public void GeometryMath_Basics()
    {
        Assert.Equal(1, GeometryMath.DistanceToSegment(new Point2(5, 1), new Point2(0, 0), new Point2(10, 0)), 9);
        var rotated = GeometryMath.Rotate(new Point2(10, 0), Point2.Zero, 90);
        Assert.Equal(0, rotated.X, 9);
        Assert.Equal(10, rotated.Y, 9);
        var tri = new[] { new Point2(0, 0), new Point2(10, 0), new Point2(0, 10) };
        Assert.True(GeometryMath.PointInPolygon(new Point2(2, 2), tri));
        Assert.False(GeometryMath.PointInPolygon(new Point2(8, 8), tri));
        Assert.Equal(50, GeometryMath.PolygonArea(tri), 9);
        var snapped = GeometryMath.SnapAngle(Point2.Zero, new Point2(10, 1));
        Assert.Equal(0, snapped.Y, 6);
        Assert.Equal(new Point2(5, 5), GeometryMath.ConstrainSquare(Point2.Zero, new Point2(5, 2)));
        Assert.Equal(3, GeometryMath.Simplify(new[] { new Point2(0, 0), new Point2(1, 0.01), new Point2(2, 0), new Point2(2, 5) }, 0.1).Count);
        var g = Geometry2.FromPolyline(new[] { new Point2(0, 0), new Point2(10, 0) }, 4);
        Assert.True(g.HitTest(new Point2(5, 2.5), 1));
        Assert.False(g.HitTest(new Point2(5, 4), 1));
        Assert.True(Geometry2.FromRect(new Rect2(0, 0, 10, 10), filled: true).HitTest(new Point2(5, 5), 0));
        Assert.True(g.IntersectsRect(new Rect2(4, -1, 2, 2)));
    }

    [Fact]
    public void Anchor_Placement()
    {
        Assert.Equal(new Point2(10, 10), Anchor9.Center.PlacementOffset(new Size2(20, 10), new Size2(40, 30)));
        Assert.Equal(new Point2(0, 20), Anchor9.BottomLeft.PlacementOffset(new Size2(20, 10), new Size2(40, 30)));
    }
}

public class ImagingTests
{
    [Fact]
    public void Png_RoundTrip_PreservesPixels_AndDpi()
    {
        var img = new BgraImage(13, 7) { DpiX = 144, DpiY = 144 };
        var rnd = new Random(42);
        for (var y = 0; y < 7; y++)
            for (var x = 0; x < 13; x++)
                img.SetPixel(x, y, Argb.FromArgb(255, (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        img.SetPixel(0, 0, Argb.Transparent);
        var png = PngCodec.Encode(img);
        Assert.True(PngCodec.IsPng(png));
        Assert.True(PngCodec.TryReadSize(png, out var w, out var h));
        Assert.Equal((13, 7), (w, h));
        var back = PngCodec.Decode(png);
        Assert.Equal(img.Pixels, back.Pixels);
        Assert.Equal(144, back.DpiX, 1);
    }

    [Fact]
    public void Png_Decode_RejectsCorruption()
    {
        var png = PngCodec.Encode(TestData.Gradient(4, 4));
        png[^10] ^= 0xFF; // corrupt IEND/IDAT CRC region
        Assert.Throws<ShotwiseException>(() => PngCodec.Decode(png));
        Assert.Throws<ShotwiseException>(() => PngCodec.Decode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void BgraImage_CropBlitAndPremultiply()
    {
        var img = BgraImage.CreateFilled(10, 10, Argb.Red);
        var small = BgraImage.CreateFilled(2, 2, Argb.Blue);
        img.Blit(small, 9, 9);
        Assert.Equal(Argb.Blue, img.GetPixel(9, 9));
        var crop = img.Crop(new RectI(8, 8, 5, 5));
        Assert.Equal(2, crop.Width);
        var half = BgraImage.CreateFilled(1, 1, new Argb(0x80FF0000));
        Assert.Equal(0x80, half.Pixels[3]);
        Assert.Equal(0x80, half.Pixels[2]); // premultiplied red
        Assert.Throws<ShotwiseException>(() => new BgraImage(100_000, 100_000));
    }

    [Fact]
    public void Downscale_KeepsAspect()
    {
        var d = ImageOps.Downscale(BgraImage.CreateFilled(4000, 1000, Argb.White), 1024);
        Assert.Equal((1024, 256), (d.Width, d.Height));
        Assert.Equal(Argb.White, d.GetPixel(100, 100));
    }

    [Fact]
    public void Mask_Helpers()
    {
        var m = Mask.FromRects(10, 10, new[] { new RectI(2, 2, 3, 3) });
        Assert.Equal(9, m.Count);
        Assert.Equal(new RectI(2, 2, 3, 3), m.PixelBounds);
        var poly = Mask.FromPolygon(10, 10, new[] { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10) });
        Assert.Equal(100, poly.Count);
        Assert.Equal(91, poly.Subtract(m).Count);
        Assert.Equal(91, m.Invert().Count);
        Assert.Equal(25, m.Morph(1).Count);
        Assert.Equal(1, m.Morph(-1).Count);
        var e = Mask.FromEllipse(20, 20, new Rect2(0, 0, 20, 20));
        Assert.InRange(e.Count, 300, 330); // ≈ π·100
        var img = m.ApplyTo(BgraImage.CreateFilled(10, 10, Argb.Red));
        Assert.Equal(0, img.GetPixel(0, 0).A);
        Assert.Equal(255, img.GetPixel(3, 3).A);
    }

    [Fact]
    public void AssetStore_Dedupes_AndPrunes()
    {
        var store = new AssetStore();
        var a = store.Add(new byte[] { 1, 2, 3 }, "application/octet-stream");
        var b = store.Add(new byte[] { 1, 2, 3 }, "application/octet-stream");
        Assert.Equal(a, b);
        var c = store.Add(new byte[] { 4 }, "application/octet-stream");
        store.Retain(c);
        Assert.Equal(1, store.Prune(Array.Empty<AssetId>())); // a removed, c retained
        Assert.True(store.Contains(c));
        store.Release(c);
        Assert.Equal(1, store.Prune(Array.Empty<AssetId>()));
        Assert.Equal(0, store.Count);
        Assert.Throws<ShotwiseException>(() => store.Get(a));
        Assert.Throws<ShotwiseException>(() => store.AddWithId(new AssetId("../x"), new byte[1], "x"));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F9", VirtualKey.F9, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+F9")]
    [InlineData("control + alt + prtsc", VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+PrintScreen")]
    [InlineData("PrintScreen", VirtualKey.PrintScreen, ModifierKeys.None, "PrintScreen")]
    [InlineData("Ctrl++", VirtualKey.OemPlus, ModifierKeys.Control, "Ctrl+=")]
    [InlineData("Ctrl+Num +", VirtualKey.Add, ModifierKeys.Control, "Ctrl+Num +")]
    [InlineData("Ctrl+Num 0", VirtualKey.NumPad0, ModifierKeys.Control, "Ctrl+Num 0")]
    [InlineData("Win+Shift+S", VirtualKey.S, ModifierKeys.Windows | ModifierKeys.Shift, "Shift+Win+S")]
    [InlineData("Alt+1", VirtualKey.D1, ModifierKeys.Alt, "Alt+1")]
    [InlineData("Ctrl+;", VirtualKey.OemSemicolon, ModifierKeys.Control, "Ctrl+;")]
    [InlineData("Shift+Del", VirtualKey.Delete, ModifierKeys.Shift, "Shift+Del")]
    public void Parse_And_Format(string text, VirtualKey key, ModifierKeys mods, string display)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(key, mods), h);
        Assert.Equal(display, h.ToDisplayString());
        Assert.Equal(h, Hotkey.Parse(h.ToDisplayString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+K")]
    [InlineData("Ctrl+Nonsense")]
    public void Parse_Rejects(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Validity()
    {
        Assert.True(Hotkey.Parse("PrintScreen").IsValidGlobalHotkey);
        Assert.True(Hotkey.Parse("F5").IsValidGlobalHotkey);
        Assert.False(Hotkey.Parse("A").IsValidGlobalHotkey);
        Assert.True(Hotkey.Parse("Ctrl+A").IsValidGlobalHotkey);
        Assert.False(new Hotkey(VirtualKey.LeftShift, ModifierKeys.Control).IsValidGlobalHotkey);
        Assert.Equal("\"Ctrl+Shift+F10\"", JsonSerializer.Serialize(Hotkey.Parse("Ctrl+Shift+F10"), ShotwiseJson.Compact));
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F10"), JsonSerializer.Deserialize<Hotkey>("\"Ctrl\\u002BShift\\u002BF10\""));
    }
}

public class EffectsJsonTests
{
    [Fact]
    public void Effects_RoundTrip_WithTypeDiscriminator()
    {
        var list = new List<EffectSpec>
        {
            EffectDefaults.Border, EffectDefaults.Edge with { Sides = EdgeSides.Top | EdgeSides.Left }, EffectDefaults.Shadow, EffectDefaults.Perspective,
            EffectDefaults.PageCurl, EffectDefaults.ColorAdjust, new ColorReplaceEffect(new[] { (Argb.Red, Argb.Blue), (Argb.White, Argb.Transparent) }, 0.3),
            EffectDefaults.Filter, EffectDefaults.Watermark, EffectDefaults.Caption, EffectDefaults.Resize, EffectDefaults.Rotate, EffectDefaults.Trim,
            new SpotlightMagnifyEffect(new Rect2(1, 2, 3, 4), 0.5, 3, 1.5), EffectDefaults.Transparency with { Enabled = false },
        };
        var json = JsonSerializer.Serialize(list, ShotwiseJson.Compact);
        Assert.Contains("{\"type\":\"pageCurl\"", json);
        Assert.Contains("\"sides\":\"top, left\"", json);
        var back = JsonSerializer.Deserialize<List<EffectSpec>>(json, ShotwiseJson.Compact)!;
        Assert.Equal(list.Count, back.Count);
        for (var i = 0; i < list.Count; i++)
        {
            Assert.Equal(list[i].GetType(), back[i].GetType());
            Assert.Equal(JsonSerializer.Serialize(list[i], ShotwiseJson.Compact), JsonSerializer.Serialize(back[i], ShotwiseJson.Compact));
        }
        Assert.False(back[^1].Enabled);
        Assert.Equal(2, ((ColorReplaceEffect)back[6]).Pairs.Count);
    }

    [Fact]
    public void UnknownEffect_RoundTripsUnchanged()
    {
        const string json = "[{\"type\":\"holographic\",\"intensity\":7,\"enabled\":false}]";
        var back = JsonSerializer.Deserialize<List<EffectSpec>>(json, ShotwiseJson.Compact)!;
        var u = Assert.IsType<UnknownEffect>(Assert.Single(back));
        Assert.Equal("holographic", u.TypeName);
        Assert.False(u.Enabled);
        Assert.Equal(json, JsonSerializer.Serialize(back, ShotwiseJson.Compact));
    }
}

public class VideoEditListTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void KeptRanges_TrimMinusMergedCuts()
    {
        var edl = new VideoEditList("a.mp4", S(60)) { Trim = new TimeRange(S(5), S(55)) };
        edl.Cuts.Add(new TimeRange(S(10), S(20)));
        edl.Cuts.Add(new TimeRange(S(15), S(25)));
        edl.Cuts.Add(new TimeRange(S(50), S(70)));
        var kept = edl.KeptRanges().ToList();
        Assert.Equal(new[] { new TimeRange(S(5), S(10)), new TimeRange(S(25), S(50)) }, kept);
        Assert.Equal(S(30), edl.KeptDuration);
        Assert.Equal(S(26), edl.ToSourceTime(S(6)));
        Assert.Equal(S(5), edl.ToEditedTime(S(15)));
        Assert.Equal(S(6), edl.ToEditedTime(S(26)));
    }

    [Fact]
    public void Commands_AreUndoable_AndCannotCutEverything()
    {
        var edl = new VideoEditList("a.mp4", S(10));
        var placeholder = new ShotDocument();
        var undo = new UndoStack(placeholder);
        undo.Execute(new SetTrimCommand(edl, new TimeRange(S(1), S(9))), placeholder);
        undo.Execute(new AddCutCommand(edl, new TimeRange(S(2), S(3))), placeholder);
        Assert.Equal(S(7), edl.KeptDuration);
        Assert.False(edl.CanAddCut(new TimeRange(S(0), S(10))));
        Assert.Throws<ShotwiseException>(() => new AddCutCommand(edl, new TimeRange(S(1), S(9))));
        undo.Undo();
        Assert.Empty(edl.Cuts);
        undo.Undo();
        Assert.True(edl.IsIdentity);
    }
}

public class ModelTests
{
    [Fact]
    public void StepLabels()
    {
        Assert.Equal("1", StepAnnotation.FormatValue(1, StepSequence.Number));
        Assert.Equal("A", StepAnnotation.FormatValue(1, StepSequence.Upper));
        Assert.Equal("Z", StepAnnotation.FormatValue(26, StepSequence.Upper));
        Assert.Equal("AA", StepAnnotation.FormatValue(27, StepSequence.Upper));
        Assert.Equal("ab", StepAnnotation.FormatValue(28, StepSequence.Lower));
    }

    [Fact]
    public void DeepClone_IsIndependent_AndTranslateMovesEverything()
    {
        var c = new CalloutAnnotation { Start = new(0, 0), End = new(10, 10), Tails = { new CalloutTail(new(20, 20)) }, Text = RichText.FromPlain("x") };
        var clone = (CalloutAnnotation)c.DeepClone();
        clone.Tails.Add(new CalloutTail(new(1, 1)));
        clone.Text.Runs.Add(new TextRun { Text = "y" });
        Assert.Single(c.Tails);
        Assert.Single(c.Text.Runs);
        Assert.Equal(c.Id, clone.Id);
        Assert.NotEqual(c.Id, c.CloneWithNewId().Id);
        c.Translate(5, 5);
        Assert.Equal(new Point2(25, 25), c.Tails[0].Tip);
        var mag = new MagnifyAnnotation { SourceRect = new Rect2(0, 0, 1, 1) };
        mag.Translate(1, 2);
        Assert.Equal(new Rect2(1, 2, 1, 1), mag.SourceRect);
    }

    [Fact]
    public void RichText_Normalize_MergesRuns()
    {
        var t = new RichText(new[] { new TextRun { Text = "a" }, new TextRun { Text = "b" }, new TextRun { Text = "c", Bold = true }, new TextRun { Text = "" } });
        var n = t.Normalize();
        Assert.Equal(2, n.Runs.Count);
        Assert.Equal("ab", n.Runs[0].Text);
        Assert.Equal("abc", t.PlainText);
    }

    [Fact]
    public void Template_Instantiate_RemapsIds()
    {
        var proto = new Page { CanvasSize = new(100, 100) };
        var sec = new TemplateSection { Type = SectionType.Body, Bounds = new Rect2(0, 0, 100, 100) };
        sec.Graphics.Add(new DropZoneAnnotation { SectionId = sec.Id });
        proto.Sections.Add(sec);
        var asset = new EmbeddedAsset(AssetId.New(), "image/png", PngCodec.Encode(TestData.Gradient(2, 2)));
        proto.Background = new CanvasBackground(Argb.White, null, new BaseImageRef(asset.Id, Point2.Zero));
        var t = new TemplateDefinition { Name = "Grid", MaxImages = 1, Prototype = proto, Assets = { asset }, LocalizedNames = { ["de"] = "Raster" } };
        var doc = t.Instantiate();
        var page = doc.Pages[0];
        Assert.NotEqual(sec.Id, page.Sections[0].Id);
        Assert.Equal(page.Sections[0].Id, ((DropZoneAnnotation)page.Sections[0].Graphics[0]).SectionId);
        Assert.True(doc.Assets.Contains(asset.Id));
        Assert.Equal("Raster", t.DisplayName("de-DE"));
        Assert.Equal("Grid", t.DisplayName("fr"));
        // prototype untouched
        Assert.Equal(sec.Id, ((DropZoneAnnotation)proto.Sections[0].Graphics[0]).SectionId);
    }

    [Fact]
    public void CapturePreset_Clone_IsDeep()
    {
        var p = new CapturePreset { Name = "x", Effects = { EffectDefaults.Border } };
        var c = p.Clone();
        c.Effects.Clear();
        Assert.Single(p.Effects);
        Assert.Equal(p.Id, c.Id);
    }

    [Fact]
    public void AppPaths_RedirectsUnderDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-root");
        var paths = new AppPaths(root);
        Assert.StartsWith(Path.GetFullPath(root), paths.AppData);
        Assert.EndsWith("logs", paths.Logs);
        Assert.EndsWith("settings.json", paths.SettingsFile);
        var real = new AppPaths(null);
        Assert.EndsWith(Path.Combine("Shotwise"), real.AppData);
        Assert.EndsWith(Path.Combine("Shotwise", "Library"), real.DefaultLibraryRoot);
        Assert.Equal(Path.GetDirectoryName(real.DefaultLibraryRoot), real.DefaultSaveFolder);
    }

    [Fact]
    public void AppPaths_DataRootAlsoRedirectsLibraryAndSaveFolder()
    {
        // Regression: isolated (--smoke / test) runs wrote exports into the real Documents\Shotwise.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sw-root-docs"));
        IAppPaths paths = new AppPaths(root);
        Assert.StartsWith(root, paths.DefaultLibraryRoot);
        Assert.StartsWith(root, paths.DefaultSaveFolder);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs))
        {
            Assert.False(paths.DefaultSaveFolder.StartsWith(docs, StringComparison.OrdinalIgnoreCase));
            Assert.False(paths.DefaultLibraryRoot.StartsWith(docs, StringComparison.OrdinalIgnoreCase));
        }
    }
}
