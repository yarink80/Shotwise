using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Rendering;
using Xunit;

namespace Shotwise.Rendering.Tests;

internal static class T
{
    public static SkiaDocumentRenderer Renderer { get; } = new(new SkiaImageCodec());
    public static SkiaEffectProcessor Effects { get; } = new();
    public static SkiaImageCodec Codec { get; } = new();

    public static (ShotDocument Doc, Page Page) Doc(int w = 200, int h = 150, Argb? bg = null)
    {
        var doc = ShotDocument.Create(new Size2(w, h), bg ?? Argb.White);
        return (doc, doc.Pages[0]);
    }

    public static BgraImage Render(ShotDocument doc, Page page, double scale = 1, Action<RenderOptions>? _ = null)
        => Renderer.RenderPage(doc, page, new RenderOptions(scale));

    /// <summary>Renders a single annotation onto a white canvas.</summary>
    public static BgraImage RenderOne(Annotation a, int w = 200, int h = 150, Argb? bg = null)
    {
        var (doc, page) = Doc(w, h, bg);
        page.Annotations.Add(a);
        return Render(doc, page);
    }

    public static Argb Px(BgraImage img, int x, int y) => img.GetPixel(x, y);

    public static void AssertColor(BgraImage img, int x, int y, Argb expected, int tol = 6)
    {
        var c = img.GetPixel(x, y);
        Assert.True(Math.Abs(c.A - expected.A) <= tol && Math.Abs(c.R - expected.R) <= tol && Math.Abs(c.G - expected.G) <= tol && Math.Abs(c.B - expected.B) <= tol,
            $"Pixel ({x},{y}) was {c} but expected {expected} (tol {tol}).");
    }

    public static bool IsWhite(Argb c, int tol = 3) => c.A >= 255 - tol && c.R >= 255 - tol && c.G >= 255 - tol && c.B >= 255 - tol;

    /// <summary>Mean absolute per-channel difference (0..255) between two same-sized images.</summary>
    public static double MeanDiff(BgraImage a, BgraImage b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i++) sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return (double)sum / a.Pixels.Length;
    }

    public static int CountNonWhite(BgraImage img, Rect2? area = null)
    {
        var r = area ?? new Rect2(0, 0, img.Width, img.Height);
        var n = 0;
        for (var y = (int)r.Y; y < (int)r.Bottom && y < img.Height; y++)
            for (var x = (int)r.X; x < (int)r.Right && x < img.Width; x++)
                if (!IsWhite(img.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>Photo-like gradient test image (opaque).</summary>
    public static BgraImage Gradient(int w, int h)
    {
        var img = new BgraImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img.SetPixel(x, y, Argb.FromRgb((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x + y) * 255 / Math.Max(1, w + h - 2))));
        return img;
    }

    public static BgraImage Checker(int w, int h, int cell, Argb a, Argb b)
    {
        var img = new BgraImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                img.SetPixel(x, y, ((x / cell) + (y / cell)) % 2 == 0 ? a : b);
        return img;
    }

    public static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), "shotwise-render-" + Guid.NewGuid().ToString("N") + ext);
}
