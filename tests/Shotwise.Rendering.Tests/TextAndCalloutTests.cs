using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class TextAndCalloutTests
{
    private static TextLayoutStyle Plain(double pad = 0) => TextLayoutStyle.Default with { Padding = new Thickness2(pad) };

    [Fact]
    public void Layout_SingleLine_HasLengthPlusOneCaretStops_Monotonic()
    {
        var info = T.Renderer.LayoutText(RichText.FromPlain("Hello world", "Segoe UI", 20), Plain(), 0);
        Assert.Single(info.Lines);
        var l = info.Lines[0];
        Assert.Equal(0, l.StartIndex);
        Assert.Equal(11, l.Length);
        Assert.Equal(12, l.CaretPositions.Count);
        for (var i = 1; i < l.CaretPositions.Count; i++) Assert.True(l.CaretPositions[i] >= l.CaretPositions[i - 1]);
        Assert.True(l.CaretPositions[^1] > 50);
        Assert.True(info.ContentSize.Width >= l.CaretPositions[^1] - 1);
    }

    [Fact]
    public void Layout_WrapsAtMaxWidth_AndIndicesAreContiguous()
    {
        var text = "The quick brown fox jumps over the lazy dog";
        var info = T.Renderer.LayoutText(RichText.FromPlain(text, "Segoe UI", 16), Plain(), 120);
        Assert.True(info.Lines.Count >= 3);
        var next = 0;
        foreach (var l in info.Lines)
        {
            Assert.Equal(next, l.StartIndex);
            next = l.StartIndex + l.Length;
            Assert.True(l.CaretPositions[^1] <= 120 + 8, $"line too wide: {l.CaretPositions[^1]}"); // a hanging trailing space may overshoot
        }
        Assert.Equal(text.Length, next);
        // lines stack downwards
        for (var i = 1; i < info.Lines.Count; i++) Assert.True(info.Lines[i].Bounds.Y > info.Lines[i - 1].Bounds.Y);
    }

    [Fact]
    public void Layout_ExplicitNewlines_CreateParagraphsWithEmptyLines()
    {
        var info = T.Renderer.LayoutText(RichText.FromPlain("ab\n\ncd", "Segoe UI", 16), Plain(), 0);
        Assert.Equal(3, info.Lines.Count);
        Assert.Equal(0, info.Lines[0].StartIndex); Assert.Equal(2, info.Lines[0].Length);
        Assert.Equal(3, info.Lines[1].StartIndex); Assert.Equal(0, info.Lines[1].Length);
        Assert.Equal(4, info.Lines[2].StartIndex); Assert.Equal(2, info.Lines[2].Length);
    }

    [Fact]
    public void Layout_EmptyText_HasOneLine()
    {
        var info = T.Renderer.LayoutText(new RichText(), Plain(4), 100);
        Assert.Single(info.Lines);
        Assert.True(info.ContentSize.Height > 8);
    }

    [Fact]
    public void Layout_HitTestAndCaretRect_AreConsistent()
    {
        var info = T.Renderer.LayoutText(RichText.FromPlain("abcdef", "Consolas", 20), Plain(), 0);
        for (var i = 0; i <= 6; i++)
        {
            var caret = info.CaretRect(i);
            var hit = info.HitTest(new Point2(caret.X, caret.Y + 2));
            Assert.Equal(i, hit);
        }
    }

    [Fact]
    public void Layout_MixedRuns_UsesBiggestFontForLineHeight_AndBoldIsWider()
    {
        var small = RichText.FromPlain("MMMM", "Segoe UI", 12);
        var bold = RichText.FromPlain("MMMM", "Segoe UI", 12, null, true);
        var w1 = T.Renderer.LayoutText(small, Plain(), 0).ContentSize.Width;
        var w2 = T.Renderer.LayoutText(bold, Plain(), 0).ContentSize.Width;
        Assert.True(w2 > w1);

        var mixed = new RichText(new[]
        {
            new TextRun { Text = "small ", FontSize = 10 },
            new TextRun { Text = "BIG", FontSize = 40 },
        });
        var info = T.Renderer.LayoutText(mixed, Plain(), 0);
        Assert.Single(info.Lines);
        Assert.True(info.Lines[0].Bounds.Height > 40);
        Assert.Equal(9, info.Lines[0].Length);
    }

    [Fact]
    public void Layout_Alignment_ShiftsLines()
    {
        var rt = RichText.FromPlain("Hi", "Segoe UI", 16);
        var left = T.Renderer.LayoutText(rt, Plain() with { HAlign = HorizontalAlign.Left }, 200).Lines[0].Bounds.X;
        var centre = T.Renderer.LayoutText(rt, Plain() with { HAlign = HorizontalAlign.Center }, 200).Lines[0].Bounds.X;
        var right = T.Renderer.LayoutText(rt, Plain() with { HAlign = HorizontalAlign.Right }, 200).Lines[0].Bounds.X;
        Assert.True(left < 1 && centre > 80 && right > 150);
    }

    [Fact]
    public void Layout_LineSpacing_ScalesHeight()
    {
        var rt = RichText.FromPlain("a\nb", "Segoe UI", 16);
        var one = T.Renderer.LayoutText(rt, Plain() with { LineSpacing = 1 }, 0).ContentSize.Height;
        var two = T.Renderer.LayoutText(rt, Plain() with { LineSpacing = 2 }, 0).ContentSize.Height;
        Assert.InRange(two / one, 1.8, 2.1);
    }

    [Fact]
    public void Layout_LongWordIsBroken()
    {
        var info = T.Renderer.LayoutText(RichText.FromPlain(new string('W', 40), "Segoe UI", 16), Plain(), 100);
        Assert.True(info.Lines.Count >= 4);
        Assert.Equal(40, info.Lines.Sum(l => l.Length));
    }

    [Fact]
    public void LayoutInBox_AppliesVerticalAlignment()
    {
        var rt = RichText.FromPlain("x", "Segoe UI", 16);
        var top = T.Renderer.LayoutTextInBox(rt, Plain() with { VAlign = VerticalAlign.Top }, new Size2(100, 100)).Lines[0].Bounds.Y;
        var mid = T.Renderer.LayoutTextInBox(rt, Plain() with { VAlign = VerticalAlign.Middle }, new Size2(100, 100)).Lines[0].Bounds.Y;
        var bot = T.Renderer.LayoutTextInBox(rt, Plain() with { VAlign = VerticalAlign.Bottom }, new Size2(100, 100)).Lines[0].Bounds.Y;
        Assert.True(top < mid && mid < bot);
    }

    [Fact]
    public void TextAnnotation_RendersInkInsideBox_AndRespectsColor()
    {
        var t = new TextAnnotation
        {
            Start = new(10, 10), End = new(190, 60),
            Text = RichText.FromPlain("Hello Shotwise", "Segoe UI", 24, Argb.Red),
        };
        var img = T.RenderOne(t, 200, 80);
        var red = 0;
        for (var y = 10; y < 60; y++) for (var x = 10; x < 190; x++) { var c = img.GetPixel(x, y); if (c.R > 180 && c.G < 90 && c.B < 90) red++; }
        Assert.True(red > 100, $"red ink px: {red}");
        Assert.True(T.IsWhite(img.GetPixel(195, 75)));
    }

    [Fact]
    public void TextAnnotation_Outline_DrawsOutlineColor()
    {
        var t = new TextAnnotation
        {
            Start = new(10, 10), End = new(190, 70),
            Text = RichText.FromPlain("OUTLINE", "Segoe UI", 36, Argb.White, true),
            TextStyle = TextLayoutStyle.Default with { OutlineColor = Argb.Blue, OutlineWidth = 3 },
        };
        var img = T.RenderOne(t, 200, 80);
        var blue = 0;
        for (var y = 0; y < 80; y++) for (var x = 0; x < 200; x++) { var c = img.GetPixel(x, y); if (c.B > 200 && c.R < 60) blue++; }
        Assert.True(blue > 80, $"outline px: {blue}");
    }

    [Fact]
    public void TextAnnotation_UnderlineAndStrikethrough()
    {
        var t = new TextAnnotation { Start = new(5, 5), End = new(195, 45) };
        t.Text = new RichText(new[] { new TextRun { Text = "WWWWWWWW", FontSize = 20, Underline = true } });
        var plain = new TextAnnotation { Start = new(5, 5), End = new(195, 45), Text = RichText.FromPlain("WWWWWWWW", "Segoe UI", 20) };
        var a = T.RenderOne(t, 200, 50);
        var b = T.RenderOne(plain, 200, 50);
        Assert.True(T.CountNonWhite(a) > T.CountNonWhite(b));
    }

    [Fact]
    public void TextAnnotation_ArabicAndLigatureShaping_DoesNotThrow_AndProducesCarets()
    {
        var info = T.Renderer.LayoutText(RichText.FromPlain("shalom שלום مرحبا ﬁne", "Segoe UI", 20), Plain(), 0);
        Assert.Equal(info.Lines[0].Length + 1, info.Lines[0].CaretPositions.Count);
    }

    [Fact]
    public void FixedBoxText_IsClipped()
    {
        var t = new TextAnnotation
        {
            Start = new(10, 10), End = new(60, 30),
            Text = RichText.FromPlain("This is far too long to fit in the box at all", "Segoe UI", 30),
            TextStyle = TextLayoutStyle.Default with { AutoSize = false, Padding = new Thickness2(0) },
        };
        var img = T.RenderOne(t, 200, 100);
        Assert.True(T.IsWhite(img.GetPixel(100, 20)));
        Assert.True(T.IsWhite(img.GetPixel(30, 50)));
    }

    [Fact]
    public void Callout_AllCanonicalPresets_RenderAndHaveOutline()
    {
        Assert.Equal(20, CalloutPresets.All.Count);
        foreach (var id in CalloutPresets.All)
        {
            var c = new CalloutAnnotation
            {
                ShapePresetId = id, Start = new(20, 20), End = new(180, 100),
                Fill = FillStyle.Solid(Argb.Yellow), Stroke = new StrokeStyle(Argb.Black, 2),
                Text = RichText.FromPlain("Hi", "Segoe UI", 16),
            };
            var img = T.RenderOne(c, 200, 130);
            Assert.True(T.CountNonWhite(img) > 600, $"{id} drew too little ({T.CountNonWhite(img)})");
            var b = T.Renderer.MeasureBounds(c);
            Assert.True(b.Width >= 150 && b.Height >= 70, id);
            var outline = T.Renderer.GetOutline(c);
            Assert.NotEmpty(outline.Figures);
            Assert.True(outline.HitTest(new Point2(100, 60), 2) || id is CalloutPresets.ThoughtBubble or CalloutPresets.Cloud or CalloutPresets.Burst or CalloutPresets.Ribbon,
                $"{id} centre not hit");
        }
    }

    [Fact]
    public void Callout_Tail_ExtendsBoundsAndPaintsTailPixels()
    {
        var c = new CalloutAnnotation
        {
            ShapePresetId = CalloutPresets.RoundedRectangle, Start = new(20, 20), End = new(120, 70),
            Fill = FillStyle.Solid(Argb.Yellow), Stroke = new StrokeStyle(Argb.Black, 2),
        };
        c.Tails.Add(new CalloutTail(new Point2(70, 140), 24));
        var img = T.RenderOne(c, 160, 160);
        Assert.False(T.IsWhite(img.GetPixel(70, 120)));
        Assert.True(T.Renderer.MeasureBounds(c).Bottom >= 139);
        Assert.True(T.Renderer.GetOutline(c).HitTest(new Point2(70, 110), 1));
    }

    [Fact]
    public void Callout_CurvedTail_DiffersFromStraight()
    {
        CalloutAnnotation Make(bool curved)
        {
            var c = new CalloutAnnotation { ShapePresetId = CalloutPresets.Ellipse, Start = new(20, 20), End = new(120, 70), Fill = FillStyle.Solid(Argb.Yellow), Stroke = StrokeStyle.None };
            c.Tails.Add(new CalloutTail(new Point2(140, 130), 30, curved));
            return c;
        }
        var a = T.RenderOne(Make(false), 160, 150);
        var b = T.RenderOne(Make(true), 160, 150);
        Assert.True(T.MeanDiff(a, b) > 0.2);
    }

    [Fact]
    public void Callout_Text_IsDrawnInsideBody()
    {
        var c = new CalloutAnnotation
        {
            ShapePresetId = CalloutPresets.Rectangle, Start = new(10, 10), End = new(190, 90), Fill = FillStyle.Solid(Argb.White), Stroke = StrokeStyle.None,
            Text = RichText.FromPlain("Centered", "Segoe UI", 24, Argb.Black),
        };
        var img = T.RenderOne(c, 200, 100);
        var ink = 0;
        for (var y = 30; y < 70; y++) for (var x = 40; x < 160; x++) if (img.GetPixel(x, y).R < 100) ink++;
        Assert.True(ink > 100);
    }

    [Fact]
    public void CalloutGeometry_BodyPathsAreNonDegenerate()
    {
        foreach (var id in CalloutPresets.All)
        {
            using var p = CalloutGeometry.BuildBody(id, new Rect2(0, 0, 160, 90));
            var b = p.Bounds;
            Assert.True(b.Width > 120 && b.Height > 60, id);
            Assert.True(b.Left >= -1 && b.Right <= 161 && b.Top >= -1 && b.Bottom <= 91, $"{id} escapes its box: {b}");
        }
    }
}
