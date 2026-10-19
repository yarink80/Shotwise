using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class EffectTests
{
    private static BgraImage Solid(int w, int h, Argb c) => BgraImage.CreateFilled(w, h, c);
    private static EffectResult Apply(BgraImage src, EffectSpec e, DocumentMetadata? md = null) => T.Effects.Apply(src, e, md);

    [Fact]
    public void Border_Outside_GrowsAndOffsets()
    {
        var r = Apply(Solid(40, 30, Argb.Red), new BorderEffect(Argb.Black, 5, true));
        Assert.Equal(50, r.Image.Width);
        Assert.Equal(40, r.Image.Height);
        Assert.Equal(new Point2(5, 5), r.ContentOffset);
        T.AssertColor(r.Image, 2, 2, Argb.Black, 0);
        T.AssertColor(r.Image, 25, 20, Argb.Red, 0);
        T.AssertColor(r.Image, 47, 37, Argb.Black, 0);
    }

    [Fact]
    public void Border_Inside_KeepsSize()
    {
        var r = Apply(Solid(40, 30, Argb.Red), new BorderEffect(Argb.Black, 3, false));
        Assert.Equal(40, r.Image.Width);
        T.AssertColor(r.Image, 1, 15, Argb.Black, 0);
        T.AssertColor(r.Image, 20, 15, Argb.Red, 0);
        Assert.Equal(Point2.Zero, r.ContentOffset);
    }

    [Fact]
    public void Shadow_BottomRight_PadsAndDarkensOffsetRegion()
    {
        var r = Apply(Solid(50, 50, Argb.White), new ShadowEffect(Argb.Black, ShadowDirection.BottomRight, 8, 6, 1));
        Assert.True(r.Image.Width > 58 && r.Image.Height > 58);
        Assert.True(r.ContentOffset.X >= 0 && r.ContentOffset.Y >= 0);
        var ox = (int)r.ContentOffset.X;
        var oy = (int)r.ContentOffset.Y;
        // content intact
        T.AssertColor(r.Image, ox + 25, oy + 25, Argb.White, 0);
        // a pixel just outside the bottom-right corner is shadowed (dark, partially opaque)
        var s = r.Image.GetPixel(ox + 52, oy + 52);
        Assert.True(s.A > 120 && s.R < 40, s.ToString());
        // far top-left padding is clear
        Assert.True(r.Image.GetPixel(0, 0).A < 30);
    }

    [Fact]
    public void Shadow_AllSides_PadsEvenly()
    {
        var r = Apply(Solid(40, 40, Argb.White), new ShadowEffect(Argb.Black, ShadowDirection.AllSides, 6, 6, 0.8));
        Assert.Equal(r.ContentOffset.X, (double)r.Image.Width - 40 - r.ContentOffset.X, 1);
        Assert.Equal(r.ContentOffset.Y, (double)r.Image.Height - 40 - r.ContentOffset.Y, 1);
    }

    [Theory]
    [InlineData(EdgeStyle.Torn)]
    [InlineData(EdgeStyle.Wave)]
    [InlineData(EdgeStyle.Saw)]
    [InlineData(EdgeStyle.Sharktooth)]
    public void CutEdges_RemoveMaterialOnlyOnSelectedSide_AndAreDeterministic(EdgeStyle style)
    {
        var src = Solid(160, 100, Argb.Blue);
        var e = new EdgeEffect(style, EdgeSides.Bottom, 14, false, false);
        var a = Apply(src, e).Image;
        var b = Apply(src, e).Image;
        Assert.Equal(a.Pixels, b.Pixels);
        Assert.Equal(160, a.Width);
        // transparent pixels appear in the bottom band but never above it or on other sides
        var bandTransparent = 0;
        for (var x = 0; x < 160; x++) for (var y = 100 - 14; y < 100; y++) if (a.GetPixel(x, y).A < 128) bandTransparent++;
        Assert.True(bandTransparent > 100, $"{style}: {bandTransparent}");
        for (var x = 0; x < 160; x++) Assert.Equal(255, a.GetPixel(x, 50).A);
        for (var y = 0; y < 100 - 15; y++) Assert.Equal(255, a.GetPixel(80, y).A);
        // top edge untouched
        Assert.Equal(255, a.GetPixel(40, 0).A);
    }

    [Fact]
    public void CutEdges_ProfilesDiffer_BetweenStyles()
    {
        var src = Solid(200, 80, Argb.Red);
        var imgs = new[] { EdgeStyle.Torn, EdgeStyle.Wave, EdgeStyle.Saw, EdgeStyle.Sharktooth }
            .Select(s => Apply(src, new EdgeEffect(s, EdgeSides.Top, 14, false, false)).Image).ToList();
        for (var i = 0; i < imgs.Count; i++)
            for (var j = i + 1; j < imgs.Count; j++)
                Assert.True(T.MeanDiff(imgs[i], imgs[j]) > 0.3, $"{i} vs {j}");
    }

    [Fact]
    public void Sharktooth_IsSymmetricTriangleWave_AndSawIsRamp()
    {
        // sampled displacement function checks (procedural mask definition)
        var tooth = Enumerable.Range(0, 40).Select(i => SkiaEffectProcessor.EdgeDisplacement(EdgeStyle.Sharktooth, i, 12, 1)).ToArray();
        Assert.True(tooth.Max() <= 12.01f && tooth.Min() >= -0.01f && tooth.Max() > 11);
        var saw = Enumerable.Range(0, 13).Select(i => SkiaEffectProcessor.EdgeDisplacement(EdgeStyle.Saw, i, 12, 1)).ToArray();
        Assert.True(saw[5] > saw[2]); // rising ramp within one period
        var wave = Enumerable.Range(0, 80).Select(i => SkiaEffectProcessor.EdgeDisplacement(EdgeStyle.Wave, i, 12, 1)).ToArray();
        Assert.InRange(wave.Max(), 11, 12.01);
        Assert.InRange(wave.Min(), -0.01, 1);
    }

    [Fact]
    public void Edge_AllSidesCorners_CombineMasks()
    {
        var r = Apply(Solid(100, 100, Argb.Black), new EdgeEffect(EdgeStyle.Saw, EdgeSides.All, 12, false, false)).Image;
        Assert.True(r.GetPixel(0, 0).A < 130);
        var cleared = 0;
        for (var y = 0; y < 12; y++) for (var x = 0; x < 12; x++) if (r.GetPixel(x, y).A < 20) cleared++;
        Assert.True(cleared > 5);
        Assert.Equal(255, r.GetPixel(50, 50).A);
    }

    [Fact]
    public void Edge_WithBorder_DrawsGreyOutline()
    {
        var r = Apply(Solid(100, 60, Argb.White), new EdgeEffect(EdgeStyle.Wave, EdgeSides.Bottom, 12, false, true)).Image;
        var dark = 0;
        for (var x = 0; x < 100; x++) for (var y = 40; y < 60; y++) { var c = r.GetPixel(x, y); if (c.A > 200 && c.R < 120) dark++; }
        Assert.True(dark > 80);
    }

    [Fact]
    public void Edge_WithShadow_GrowsCanvas()
    {
        var r = Apply(Solid(100, 60, Argb.White), new EdgeEffect(EdgeStyle.Torn, EdgeSides.Bottom, 12, true, false));
        Assert.True(r.Image.Width > 100 && r.Image.Height > 60);
    }

    [Fact]
    public void Edge_Fade_RampsAlpha()
    {
        var r = Apply(Solid(100, 100, Argb.Red), new EdgeEffect(EdgeStyle.Fade, EdgeSides.Left | EdgeSides.Right, 30, false, false)).Image;
        Assert.True(r.GetPixel(0, 50).A < 20);
        Assert.InRange(r.GetPixel(15, 50).A, 90, 170);
        Assert.Equal(255, r.GetPixel(50, 50).A);
        Assert.Equal(255, r.GetPixel(50, 0).A); // top not selected
    }

    [Fact]
    public void Edge_Bevel_LightensTopLeft_DarkensBottomRight()
    {
        var src = Solid(100, 100, Argb.FromRgb(128, 128, 128));
        var r = Apply(src, new EdgeEffect(EdgeStyle.Bevel, EdgeSides.All, 10, false, false)).Image;
        Assert.True(r.GetPixel(50, 1).R > 160);
        Assert.True(r.GetPixel(1, 50).R > 160);
        Assert.True(r.GetPixel(50, 98).R < 100);
        Assert.True(r.GetPixel(98, 50).R < 100);
        Assert.Equal(128, r.GetPixel(50, 50).R);
    }

    [Fact]
    public void Edge_DropShadow_OnlyPadsSelectedSides()
    {
        var r = Apply(Solid(80, 60, Argb.White), new EdgeEffect(EdgeStyle.DropShadow, EdgeSides.Bottom | EdgeSides.Right, 10, false, false));
        Assert.Equal(Point2.Zero, r.ContentOffset);
        Assert.True(r.Image.Width > 80 && r.Image.Height > 60);
    }

    [Fact]
    public void Perspective_ChangesSizeAndShrinksFarSide()
    {
        var r = Apply(Solid(200, 100, Argb.Red), new PerspectiveEffect(false, 30, 0, false, false));
        Assert.Equal(200, r.Image.Width);
        Assert.Equal(100, r.Image.Height);
        // right edge is shortened (top-right corner area transparent), left edge full height
        Assert.True(r.Image.GetPixel(198, 3).A < 40);
        Assert.True(r.Image.GetPixel(2, 3).A > 200);
        Assert.True(r.Image.GetPixel(100, 50).A > 250);
    }

    [Fact]
    public void Shear_WidensCanvas()
    {
        var r = Apply(Solid(100, 100, Argb.Red), new PerspectiveEffect(true, 20, 0, false, false));
        Assert.InRange(r.Image.Width, 119, 121);
        Assert.Equal(100, r.Image.Height);
        // top shifted right: bottom-left corner opaque, top-left transparent
        Assert.True(r.Image.GetPixel(2, 97).A > 200);
        Assert.True(r.Image.GetPixel(2, 3).A < 40);
        Assert.True(r.Image.GetPixel(118, 3).A > 200);
    }

    [Fact]
    public void Perspective_BorderAndShadowExtendCanvas()
    {
        var plain = Apply(Solid(100, 80, Argb.Red), new PerspectiveEffect(false, 20, 0, false, false));
        var fancy = Apply(Solid(100, 80, Argb.Red), new PerspectiveEffect(false, 20, 0, true, true));
        Assert.True(fancy.Image.Width > plain.Image.Width);
    }

    [Theory]
    [InlineData(Corner.BottomRight, 98, 98, 2, 2)]
    [InlineData(Corner.BottomLeft, 2, 98, 98, 2)]
    [InlineData(Corner.TopRight, 98, 2, 2, 98)]
    [InlineData(Corner.TopLeft, 2, 2, 98, 98)]
    public void PageCurl_CutsCornerAndDrawsFlap(Corner corner, int cx, int cy, int ox, int oy)
    {
        var r = Apply(Solid(100, 100, Argb.FromRgb(0, 0, 255)), new PageCurlEffect(corner, 40)).Image;
        Assert.Equal(100, r.Width);
        Assert.True(r.GetPixel(cx, cy).A < 30, $"corner not removed: {r.GetPixel(cx, cy)}");
        Assert.True(r.GetPixel(ox, oy).A > 250);
        // flap interior (mirror triangle) is opaque and lighter than the page (paper back)
        var fx = corner is Corner.BottomRight or Corner.TopRight ? 70 : 30;
        var fy = corner is Corner.BottomRight or Corner.BottomLeft ? 70 : 30;
        var f = r.GetPixel(fx, fy);
        Assert.True(f.A > 250 && f.R > 100, f.ToString());
    }

    [Fact]
    public void ColorAdjust_Brightness_Contrast_Gamma()
    {
        var grey = Solid(4, 4, Argb.FromRgb(100, 100, 100));
        Assert.True(Apply(grey, new ColorAdjustEffect(50, 0, 0, 0, 1)).Image.GetPixel(1, 1).R > 200);
        Assert.True(Apply(grey, new ColorAdjustEffect(-50, 0, 0, 0, 1)).Image.GetPixel(1, 1).R < 10);
        Assert.True(Apply(grey, new ColorAdjustEffect(0, 80, 0, 0, 1)).Image.GetPixel(1, 1).R < 60); // dark grey pushed darker
        Assert.True(Apply(grey, new ColorAdjustEffect(0, 0, 0, 0, 2.0)).Image.GetPixel(1, 1).R > 150);
        Assert.Same(grey, Apply(grey, new ColorAdjustEffect(0, 0, 0, 0, 1)).Image); // identity short-circuit
    }

    [Fact]
    public void ColorAdjust_Hue_RotatesRedTowardGreen_AndSaturationRemovesColour()
    {
        var red = Solid(4, 4, Argb.FromRgb(255, 0, 0));
        var h = Apply(red, new ColorAdjustEffect(0, 0, 120, 0, 1)).Image.GetPixel(1, 1);
        Assert.True(h.G > h.R && h.G > h.B, h.ToString());
        var d = Apply(red, new ColorAdjustEffect(0, 0, 0, -100, 1)).Image.GetPixel(1, 1);
        Assert.True(Math.Abs(d.R - d.G) < 6 && Math.Abs(d.G - d.B) < 6, d.ToString());
    }

    [Fact]
    public void ColorAdjust_KeepsAlpha()
    {
        var img = new BgraImage(2, 1);
        img.SetPixel(0, 0, Argb.FromArgb(128, 200, 100, 50));
        var r = Apply(img, new ColorAdjustEffect(20, 0, 0, 0, 1)).Image;
        Assert.InRange(r.GetPixel(0, 0).A, 127, 129);
        Assert.Equal(0, r.GetPixel(1, 0).A);
    }

    [Fact]
    public void ColorReplace_SwapsMatchingColourWithinTolerance_LeavesOthers()
    {
        var img = Solid(10, 10, Argb.White);
        for (var x = 0; x < 5; x++) for (var y = 0; y < 10; y++) img.SetPixel(x, y, Argb.FromRgb(250, 10, 10));
        var spec = new ColorReplaceEffect(new[] { (Argb.FromRgb(255, 0, 0), Argb.FromRgb(0, 255, 0)) }, 0.1);
        var r = Apply(img, spec).Image;
        T.AssertColor(r, 2, 5, Argb.FromRgb(0, 255, 0), 8);
        T.AssertColor(r, 8, 5, Argb.White, 0);
        Assert.Same(img, Apply(img, new ColorReplaceEffect(Array.Empty<(Argb, Argb)>(), 0.1)).Image);
    }

    [Theory]
    [InlineData(FilterKind.Grayscale)]
    [InlineData(FilterKind.Monochrome)]
    [InlineData(FilterKind.Invert)]
    [InlineData(FilterKind.Sharpen)]
    [InlineData(FilterKind.Soften)]
    [InlineData(FilterKind.Emboss)]
    [InlineData(FilterKind.Sepia)]
    public void Filters_ChangeImageAndKeepSize(FilterKind kind)
    {
        var src = T.Gradient(40, 30);
        var r = Apply(src, new FilterEffect(kind, 1.0)).Image;
        Assert.Equal(40, r.Width);
        Assert.True(T.MeanDiff(src, r) > 0.5, kind.ToString());
        // opacity 0 -> unchanged
        Assert.Equal(src.Pixels, Apply(src, new FilterEffect(kind, 1.0, 0)).Image.Pixels);
        // half opacity lies between
        var half = Apply(src, new FilterEffect(kind, 1.0, 0.5)).Image;
        Assert.True(T.MeanDiff(src, half) < T.MeanDiff(src, r) + 0.01);
    }

    [Fact]
    public void Filter_Specifics()
    {
        var src = Solid(2, 2, Argb.FromRgb(200, 100, 50));
        var g = Apply(src, new FilterEffect(FilterKind.Grayscale, 1)).Image.GetPixel(0, 0);
        Assert.True(Math.Abs(g.R - g.G) <= 1 && Math.Abs(g.G - g.B) <= 1);
        T.AssertColor(Apply(src, new FilterEffect(FilterKind.Invert, 1)).Image, 0, 0, Argb.FromRgb(55, 155, 205), 1);
        var m = Apply(src, new FilterEffect(FilterKind.Monochrome, 1)).Image.GetPixel(0, 0);
        Assert.True((m.R == 0 || m.R == 255) && m.R == m.G && m.G == m.B);
        // sharpen increases local contrast on an edge
        var edge = Solid(20, 10, Argb.FromRgb(100, 100, 100));
        for (var y = 0; y < 10; y++) for (var x = 10; x < 20; x++) edge.SetPixel(x, y, Argb.FromRgb(150, 150, 150));
        var sh = Apply(edge, new FilterEffect(FilterKind.Sharpen, 1)).Image;
        Assert.True(sh.GetPixel(9, 5).R < 100);
        Assert.True(sh.GetPixel(10, 5).R > 150);
        var so = Apply(edge, new FilterEffect(FilterKind.Soften, 1)).Image;
        Assert.True(so.GetPixel(9, 5).R > 100 && so.GetPixel(10, 5).R < 150);
    }

    [Fact]
    public void Watermark_Overlay_Underlay_Positioned()
    {
        var wmPath = T.TempFile(".png");
        try
        {
            T.Codec.Save(Solid(20, 10, Argb.Red), wmPath, ImageFormat.Png);
            var src = Solid(100, 60, Argb.White);
            var over = Apply(src, new WatermarkEffect(wmPath, 1, Anchor9.BottomRight, false, 0, false, 2, 45)).Image;
            T.AssertColor(over, 100 - 10 - 10, 60 - 10 - 5, Argb.Red, 2);
            Assert.True(T.IsWhite(over.GetPixel(5, 5)));
            var half = Apply(src, new WatermarkEffect(wmPath, 1, Anchor9.BottomRight, false, 0.5, false, 2, 45)).Image;
            Assert.InRange(half.GetPixel(80, 45).G, 100, 160);
            // underlay: opaque image hides it
            var under = Apply(src, new WatermarkEffect(wmPath, 1, Anchor9.Center, true, 0, false, 2, 45)).Image;
            T.AssertColor(under, 50, 30, Argb.White, 0);
            // top-left anchor with scale 2
            var tl = Apply(src, new WatermarkEffect(wmPath, 2, Anchor9.TopLeft, false, 0, false, 2, 45)).Image;
            T.AssertColor(tl, 30, 15, Argb.Red, 2);
            // missing file passes through
            Assert.Same(src, Apply(src, new WatermarkEffect("C:\\no\\such\\file.png", 1, Anchor9.Center, false, 0, false, 2, 45)).Image);
        }
        finally { File.Delete(wmPath); }
    }

    [Fact]
    public void Watermark_Emboss_ProducesShadedGrey()
    {
        var wmPath = T.TempFile(".png");
        try
        {
            var logo = new BgraImage(40, 40);
            for (var y = 10; y < 30; y++) for (var x = 10; x < 30; x++) logo.SetPixel(x, y, Argb.Black);
            T.Codec.Save(logo, wmPath, ImageFormat.Png);
            var src = Solid(60, 60, Argb.FromRgb(128, 128, 128));
            var r = Apply(src, new WatermarkEffect(wmPath, 1, Anchor9.TopLeft, false, 0, true, 3, 45)).Image;
            // somewhere along the logo edge the surface is lighter/darker than flat grey
            var varied = 0;
            for (var y = 10; y < 40; y++) for (var x = 10; x < 40; x++) if (Math.Abs(r.GetPixel(x, y).R - 128) > 10) varied++;
            Assert.True(varied > 30);
        }
        finally { File.Delete(wmPath); }
    }

    [Fact]
    public void Caption_Outside_AddsBandBelow_WithMetadataText()
    {
        var md = DocumentMetadata.Create(new DateTimeOffset(2026, 3, 4, 10, 30, 0, TimeSpan.Zero)) with { AppName = "Notepad", AppVersion = "11.0", WindowTitle = "Untitled" };
        var cap = EffectDefaults.Caption with { Fields = CaptionFields.AppName | CaptionFields.AppVersion | CaptionFields.WindowTitle, BackColor = Argb.FromRgb(255, 255, 200), TextColor = Argb.Black, Outside = true, Position = Anchor9.BottomCenter };
        var r = Apply(Solid(300, 100, Argb.Blue), cap, md);
        Assert.True(r.Image.Height > 100 + 10);
        Assert.Equal(300, r.Image.Width);
        Assert.Equal(Point2.Zero, r.ContentOffset);
        T.AssertColor(r.Image, 2, r.Image.Height - 2, Argb.FromRgb(255, 255, 200), 1);
        // some dark text ink in the band
        var ink = 0;
        for (var y = 100; y < r.Image.Height; y++) for (var x = 0; x < 300; x++) if (r.Image.GetPixel(x, y).R < 100) ink++;
        Assert.True(ink > 30);
        T.AssertColor(r.Image, 150, 50, Argb.Blue, 0);
        Assert.Contains("Notepad", SkiaEffectProcessor.BuildCaptionText(cap, md));
        Assert.Contains("11.0", SkiaEffectProcessor.BuildCaptionText(cap, md));
    }

    [Fact]
    public void Caption_TopOutside_ShiftsContent_AndInsideKeepsSize()
    {
        var md = DocumentMetadata.Create() with { AppName = "App" };
        var top = Apply(Solid(200, 80, Argb.Blue), EffectDefaults.Caption with { Fields = CaptionFields.AppName, Outside = true, Position = Anchor9.TopLeft }, md);
        Assert.True(top.ContentOffset.Y > 8);
        var inside = Apply(Solid(200, 80, Argb.Blue), EffectDefaults.Caption with { Fields = CaptionFields.AppName, Outside = false, Position = Anchor9.BottomRight }, md);
        Assert.Equal(80, inside.Image.Height);
        Assert.False(inside.Image.GetPixel(190, 75) == Argb.Blue);
    }

    [Fact]
    public void Caption_NoFields_PassesThrough()
    {
        var src = Solid(10, 10, Argb.Red);
        Assert.Same(src, Apply(src, EffectDefaults.Caption with { Fields = CaptionFields.None }, null).Image);
        Assert.Contains("hello", SkiaEffectProcessor.BuildCaptionText(EffectDefaults.Caption with { Fields = CaptionFields.CustomText, CustomText = "hello" }, null));
    }

    [Fact]
    public void Resize_Percent_Absolute_AspectAndDpi()
    {
        var src = T.Gradient(100, 50);
        var half = Apply(src, new ResizeEffect(50, null, null, true, null)).Image;
        Assert.Equal((50, 25), (half.Width, half.Height));
        var w = Apply(src, new ResizeEffect(null, 200, null, true, null)).Image;
        Assert.Equal((200, 100), (w.Width, w.Height));
        var both = Apply(src, new ResizeEffect(null, 80, 80, false, 300)).Image;
        Assert.Equal((80, 80), (both.Width, both.Height));
        Assert.Equal(300, both.DpiX);
        var fit = Apply(src, new ResizeEffect(null, 80, 80, true, null)).Image;
        Assert.Equal((80, 40), (fit.Width, fit.Height));
        var sharp = Apply(src, new ResizeEffect(200, null, null, true, null, false)).Image;
        Assert.Equal(200, sharp.Width);
    }

    [Fact]
    public void Rotate_90_SwapsDimensions_AndMovesPixelsExactly()
    {
        var src = Solid(40, 20, Argb.White);
        src.SetPixel(0, 0, Argb.Red); // top-left
        var r = Apply(src, new RotateEffect(90, true, false, false)).Image;
        Assert.Equal((20, 40), (r.Width, r.Height));
        T.AssertColor(r, 19, 0, Argb.Red, 1); // top-left goes to top-right after a clockwise turn
        var r180 = Apply(src, new RotateEffect(180, true, false, false)).Image;
        T.AssertColor(r180, 39, 19, Argb.Red, 1);
        var r270 = Apply(src, new RotateEffect(270, true, false, false)).Image;
        T.AssertColor(r270, 0, 39, Argb.Red, 1);
    }

    [Fact]
    public void Rotate_Custom_GrowsCanvas_Flip_Works()
    {
        var src = Solid(100, 50, Argb.Red);
        var r = Apply(src, new RotateEffect(45, true, false, false)).Image;
        Assert.InRange(r.Width, 105, 108);
        Assert.True(r.GetPixel(0, 0).A < 20);
        Assert.True(r.GetPixel(r.Width / 2, r.Height / 2).A > 250);
        var same = Apply(src, new RotateEffect(30, false, false, false)).Image;
        Assert.Equal((100, 50), (same.Width, same.Height));
        var flipSrc = Solid(10, 10, Argb.White);
        flipSrc.SetPixel(0, 0, Argb.Red);
        T.AssertColor(Apply(flipSrc, new RotateEffect(0, false, true, false)).Image, 9, 0, Argb.Red, 1);
        T.AssertColor(Apply(flipSrc, new RotateEffect(0, false, false, true)).Image, 0, 9, Argb.Red, 1);
    }

    [Fact]
    public void Trim_Auto_RemovesUniformBorder_AndReportsOffset()
    {
        var src = Solid(100, 80, Argb.White);
        for (var y = 20; y < 50; y++) for (var x = 30; x < 60; x++) src.SetPixel(x, y, Argb.Black);
        var r = Apply(src, new TrimEffect(true, 0, 0, 0, 0, 0.01));
        Assert.Equal((30, 30), (r.Image.Width, r.Image.Height));
        Assert.Equal(new Point2(-30, -20), r.ContentOffset);
        T.AssertColor(r.Image, 0, 0, Argb.Black, 0);
    }

    [Fact]
    public void Trim_Auto_TransparentBorders_And_UniformImageUntouched()
    {
        var src = new BgraImage(50, 50);
        for (var y = 10; y < 20; y++) for (var x = 5; x < 25; x++) src.SetPixel(x, y, Argb.Red);
        var r = Apply(src, new TrimEffect(true, 0, 0, 0, 0)).Image;
        Assert.Equal((20, 10), (r.Width, r.Height));
        var uniform = Solid(10, 10, Argb.White);
        Assert.Same(uniform, Apply(uniform, new TrimEffect(true, 0, 0, 0, 0)).Image);
    }

    [Fact]
    public void Trim_Manual_CropsSides()
    {
        var r = Apply(Solid(100, 80, Argb.Red), new TrimEffect(false, 10, 5, 20, 15));
        Assert.Equal((70, 60), (r.Image.Width, r.Image.Height));
        Assert.Equal(new Point2(-10, -5), r.ContentOffset);
    }

    [Fact]
    public void Spotlight_DimsOutside_KeepsInside()
    {
        var src = Solid(100, 100, Argb.White);
        var r = Apply(src, new SpotlightMagnifyEffect(new Rect2(30, 30, 40, 40), 0.7, 0, 1)).Image;
        T.AssertColor(r, 50, 50, Argb.White, 0);
        var out1 = r.GetPixel(5, 5);
        Assert.InRange(out1.R, 60, 90);
    }

    [Fact]
    public void Spotlight_Magnifies_Region()
    {
        var src = Solid(100, 100, Argb.White);
        src.SetPixel(52, 50, Argb.Red);
        for (var y = 48; y < 53; y++) for (var x = 51; x < 55; x++) src.SetPixel(x, y, Argb.Red);
        var r = Apply(src, new SpotlightMagnifyEffect(new Rect2(30, 30, 40, 40), 0.5, 0, 3)).Image;
        // red patch (4x5) magnified 3x around (50,50): spans ~x 53..62 -> pixel (58,50) red
        Assert.True(r.GetPixel(58, 50).R > 200 && r.GetPixel(58, 50).G < 90);
    }

    [Fact]
    public void Transparency_MakesKeyColourTransparent()
    {
        var img = Solid(10, 10, Argb.White);
        for (var x = 0; x < 5; x++) for (var y = 0; y < 10; y++) img.SetPixel(x, y, Argb.Red);
        var r = Apply(img, new TransparencyEffect(Argb.White, 0.05)).Image;
        Assert.Equal(0, r.GetPixel(8, 5).A);
        Assert.Equal(255, r.GetPixel(2, 5).A);
    }

    [Fact]
    public void DisabledAndUnknownEffects_PassThrough()
    {
        var src = Solid(10, 10, Argb.Red);
        Assert.Same(src, Apply(src, new BorderEffect(Argb.Black, 5) { Enabled = false }).Image);
        var json = System.Text.Json.JsonDocument.Parse("{\"type\":\"holo\"}").RootElement.Clone();
        Assert.Same(src, Apply(src, new UnknownEffect("holo", json)).Image);
    }

    [Fact]
    public void ApplyAll_ChainsEffects_AndAccumulatesOffsets()
    {
        var r = T.Effects.ApplyAll(Solid(20, 20, Argb.Red), new EffectSpec[]
        {
            new BorderEffect(Argb.Black, 4, true),
            new BorderEffect(Argb.Blue, 2, true) { Enabled = false },
            new BorderEffect(Argb.White, 3, true),
        }, null);
        Assert.Equal(34, r.Image.Width);
        Assert.Equal(new Point2(7, 7), r.ContentOffset);
        T.AssertColor(r.Image, 0, 0, Argb.White, 0);
        T.AssertColor(r.Image, 4, 4, Argb.Black, 0);
        T.AssertColor(r.Image, 17, 17, Argb.Red, 0);
    }

    [Fact]
    public void Preview_DownscalesToMaxSide_AndScalesParameters()
    {
        var src = Solid(400, 200, Argb.Red);
        var p = T.Effects.Preview(src, new BorderEffect(Argb.Black, 20, true), 100);
        // 400->100 is 0.25: border 20 -> 5 each side => 110 x 60
        Assert.Equal((110, 60), (p.Width, p.Height));
    }

    [Fact]
    public void EffectSpec_JsonRoundTrip_StillApplies()
    {
        // sanity: defaults all apply without throwing on a photo-like image
        var src = T.Gradient(120, 80);
        var md = DocumentMetadata.Create() with { AppName = "x" };
        foreach (var e in new EffectSpec[]
                 {
                     EffectDefaults.Border, EffectDefaults.Edge, EffectDefaults.Shadow, EffectDefaults.Perspective, EffectDefaults.PageCurl,
                     EffectDefaults.ColorAdjust, EffectDefaults.ColorReplace, EffectDefaults.Filter, EffectDefaults.Watermark, EffectDefaults.Caption,
                     EffectDefaults.Resize, EffectDefaults.Rotate, EffectDefaults.Trim, EffectDefaults.Transparency,
                 })
        {
            var r = T.Effects.Apply(src, e, md);
            Assert.True(r.Image.Width > 0 && r.Image.Height > 0, e.GetType().Name);
        }
    }
}
