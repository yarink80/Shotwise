using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Vision.Tests;

public class TextStyleTests
{
    private static Mat TextImage(string text, Scalar fg, Scalar bg, double scale, int thickness, int w = 360, int h = 70)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, bg);
        Cv2.PutText(m, text, new Point(10, h - 22), HersheyFonts.HersheySimplex, scale, fg, thickness, LineTypes.AntiAlias);
        return m;
    }

    [Fact]
    public void SamplesColoursOfDarkTextOnLightBackground()
    {
        using var m = TextImage("Hello World", TestImages.Bgr(20, 40, 160), TestImages.Bgr(250, 240, 200), 1.0, 2);
        var s = TextEditService.Analyze(m)!;
        Assert.True(Argb.Distance(s.BackgroundColor, Argb.FromRgb(250, 240, 200)) < 0.05, s.BackgroundColor.ToHex());
        Assert.True(s.TextColor.B > 100 && s.TextColor.R < 90, s.TextColor.ToHex());
    }

    [Fact]
    public void SamplesColoursOfLightTextOnDarkBackground()
    {
        using var m = TextImage("Dark mode", TestImages.Bgr(240, 240, 240), TestImages.Bgr(30, 30, 40), 1.0, 2);
        var s = TextEditService.Analyze(m)!;
        Assert.True(s.BackgroundColor.Luminance < 0.05);
        Assert.True(s.TextColor.Luminance > 0.6);
    }

    [Fact]
    public void FontSize_ScalesWithGlyphHeight()
    {
        using var small = TextImage("HEIGHT", TestImages.Bgr(0, 0, 0), TestImages.Bgr(255, 255, 255), 0.6, 1);
        using var large = TextImage("HEIGHT", TestImages.Bgr(0, 0, 0), TestImages.Bgr(255, 255, 255), 1.2, 1);
        var a = TextEditService.Analyze(small)!;
        var b = TextEditService.Analyze(large)!;
        Assert.InRange(b.FontSizePx / a.FontSizePx, 1.7, 2.3);
        // Hershey simplex cap height is 21 px at scale 1.0 -> ~29 px em at 0.72 cap ratio
        Assert.InRange(b.FontSizePx, 30, 42);
    }

    [Fact]
    public void BoldHeuristic_SeparatesThinFromHeavyStrokes()
    {
        using var regular = TextImage("Illinois Hill", TestImages.Bgr(0, 0, 0), TestImages.Bgr(255, 255, 255), 1.0, 1);
        using var bold = TextImage("Illinois Hill", TestImages.Bgr(0, 0, 0), TestImages.Bgr(255, 255, 255), 1.0, 5);
        Assert.False(TextEditService.Analyze(regular)!.Bold);
        Assert.True(TextEditService.Analyze(bold)!.Bold);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesOcrLinesAndSamplesEach()
    {
        using var m = new Mat(120, 400, MatType.CV_8UC3, TestImages.Bgr(255, 255, 255));
        Cv2.PutText(m, "First line", new Point(10, 40), HersheyFonts.HersheySimplex, 1.0, TestImages.Bgr(200, 0, 0), 2, LineTypes.AntiAlias);
        Cv2.PutText(m, "Second", new Point(10, 100), HersheyFonts.HersheySimplex, 0.7, TestImages.Bgr(0, 120, 0), 1, LineTypes.AntiAlias);
        var l1 = new OcrLine("First line", new Rect2(8, 15, 180, 32), new[] { new OcrWord("First", new Rect2(8, 15, 80, 32), 1), new OcrWord("line", new Rect2(100, 15, 80, 32), 1) });
        var l2 = new OcrLine("Second", new Rect2(8, 80, 90, 26), new[] { new OcrWord("Second", new Rect2(8, 80, 90, 26), 1) });
        var svc = new TextEditService(new FixedOcr(new OcrResult("en", "", new[] { l1, l2 }, 0)), NullLogger<TextEditService>.Instance);
        var res = await svc.AnalyzeAsync(TestImages.ToImage(m), null, default);
        Assert.Equal(2, res.Count);
        Assert.True(res[0].Style.TextColor.R > 120 && res[0].Style.TextColor.G < 80);        // red-ish
        Assert.True(res[1].Style.TextColor.G > res[1].Style.TextColor.R);                    // green-ish
        Assert.True(res[0].Style.FontSizePx > res[1].Style.FontSizePx);
    }

    private sealed class FixedOcr : IOcrService
    {
        private readonly OcrResult _r;
        public FixedOcr(OcrResult r) => _r = r;
        public IReadOnlyList<string> AvailableLanguages => new[] { "en" };
        public string EngineName => "fixed";
        public Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default) => Task.FromResult(_r);
    }
}

public class OcrServiceTests
{
    private static readonly OcrService Svc = new(NullLogger<OcrService>.Instance);

    private static BgraImage Render(string text, double scale, int w = 700, int h = 120)
    {
        using var m = new Mat(h, w, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(m, text, new Point(15, h / 2 + 15), HersheyFonts.HersheyDuplex, scale, Scalar.All(0), 2, LineTypes.AntiAlias);
        return TestImages.ToImage(m);
    }

    // The OCR engine needs an installed language pack; these tests return early (pass vacuously) on machines without one.
    private static bool HasOcr => Svc.AvailableLanguages.Count > 0;

    [Fact]
    public async Task Recognizes_RenderedText_WithWordBoxes()
    {
        if (!HasOcr) return;
        var r = await Svc.RecognizeAsync(Render("Hello World 2024", 1.6), null, null, default);
        Assert.Contains("Hello", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(r.Lines);
        var w = r.Lines.SelectMany(l => l.Words).First(x => x.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(w.Bounds.X, 5, 60);
        Assert.InRange(w.Bounds.Y, 20, 80);
    }

    [Fact]
    public async Task Region_OffsetsBoxesIntoImageCoordinates()
    {
        if (!HasOcr) return;
        var img = Render("Region Text", 1.6);
        var region = new Rect2(0, 0, 700, 120);
        var all = await Svc.RecognizeAsync(img, region, null, default);
        var half = await Svc.RecognizeAsync(img, new Rect2(0, 0, 350, 120), null, default);
        Assert.NotEmpty(all.Lines);
        Assert.All(half.Lines.SelectMany(l => l.Words), w => Assert.True(w.Bounds.Right <= 360));
    }

    [Fact]
    public async Task SmallText_IsUpscaledForAccuracy()
    {
        if (!HasOcr) return;
        var r = await Svc.RecognizeAsync(Render("Small readable text", 0.55, 300, 50), null, null, default);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
    }

    [Fact]
    public async Task UnknownLanguage_FallsBackToProfileLanguages()
    {
        if (!HasOcr) return;
        var r = await Svc.RecognizeAsync(Render("Fallback", 1.6), null, "xx-YY", default);
        Assert.False(string.IsNullOrEmpty(r.Language));
    }

    [Fact]
    public async Task BlankImage_YieldsNoLines()
    {
        if (!HasOcr) return;
        using var m = new Mat(80, 200, MatType.CV_8UC3, Scalar.All(255));
        var r = await Svc.RecognizeAsync(TestImages.ToImage(m), null, null, default);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public async Task RegionOutsideImage_ReturnsEmpty()
    {
        var img = Render("x", 1.0, 100, 50);
        var r = await Svc.RecognizeAsync(img, new Rect2(500, 500, 10, 10), null, default);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public async Task Diagnostics_ReportsInstalledLanguages()
    {
        // informational: shows whether the OCR tests above ran for real on this machine
        var langs = string.Join(",", Svc.AvailableLanguages);
        var text = HasOcr ? (await Svc.RecognizeAsync(Render("Hello World 2024", 1.6), null, null, default)).Text : "";
        Assert.True(true, $"OCR languages: [{langs}] sample: [{text}]");
        Console.WriteLine($"OCR languages: [{langs}] sample: [{text}]");
    }

    [Fact]
    public void Engine_IsNamed()
        => Assert.Equal("Windows.Media.Ocr", Svc.EngineName);
}
