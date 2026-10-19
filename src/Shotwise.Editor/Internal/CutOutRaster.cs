using Shotwise.Core.Commands;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor.Internal;

/// <summary>
/// Cut-out raster: removes the band [start, end) and joins the remaining parts, then treats the seam with the chosen
/// edge style. Jagged styles (tear/wave/saw/sharktooth) cut a profiled gap into both pieces (the canvas colour shows
/// through, like torn paper); fade ramps the alpha of both pieces towards the seam. Geometry is our own design.
/// </summary>
internal static class CutOutRaster
{
    /// <summary>Seam depth in pixels for jagged styles (clamped to the available size).</summary>
    public const int SeamDepth = 10;
    /// <summary>Fade half-width in pixels.</summary>
    public const int FadeWidth = 12;

    public static BgraImage Join(BgraImage src, CutOutOrientation orientation, int start, int end, CutOutEdgeStyle edge)
    {
        var joined = JoinPlain(src, orientation, start, end);
        if (edge != CutOutEdgeStyle.None) ApplySeam(joined, orientation, start, edge);
        return joined;
    }

    public static BgraImage JoinPlain(BgraImage src, CutOutOrientation orientation, int start, int end)
    {
        if (orientation == CutOutOrientation.Horizontal)
        {
            var dst = new BgraImage(src.Width, src.Height - (end - start)) { DpiX = src.DpiX, DpiY = src.DpiY };
            var row = src.Width * 4;
            Buffer.BlockCopy(src.Pixels, 0, dst.Pixels, 0, start * row);
            Buffer.BlockCopy(src.Pixels, end * row, dst.Pixels, start * row, (src.Height - end) * row);
            return dst;
        }
        var w = src.Width - (end - start);
        var vdst = new BgraImage(w, src.Height) { DpiX = src.DpiX, DpiY = src.DpiY };
        for (var y = 0; y < src.Height; y++)
        {
            Buffer.BlockCopy(src.Pixels, y * src.Width * 4, vdst.Pixels, y * w * 4, start * 4);
            Buffer.BlockCopy(src.Pixels, (y * src.Width + end) * 4, vdst.Pixels, (y * w + start) * 4, (src.Width - end) * 4);
        }
        return vdst;
    }

    /// <summary>Profile 0..1 along the seam at position <paramref name="t"/> (pixels).</summary>
    public static double Profile(CutOutEdgeStyle edge, double t) => edge switch
    {
        CutOutEdgeStyle.Wave => 0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 40),
        CutOutEdgeStyle.Saw => Triangle(t / 16),
        CutOutEdgeStyle.Sharktooth => Frac(t / 22),
        CutOutEdgeStyle.Tear => Tear(t),
        _ => 0,
    };

    private static double Frac(double v) => v - Math.Floor(v);
    private static double Triangle(double v) { var f = Frac(v); return f < 0.5 ? f * 2 : (1 - f) * 2; }

    /// <summary>Deterministic rough profile: a few incommensurate sines plus hashed jitter.</summary>
    private static double Tear(double t)
    {
        var v = 0.5 + 0.22 * Math.Sin(t * 0.21) + 0.15 * Math.Sin(t * 0.53 + 1.3) + 0.08 * Math.Sin(t * 1.37 + 0.4);
        var n = (int)t;
        var h = (uint)(n * 2654435761u);
        h ^= h >> 13;
        v += ((h & 0xFF) / 255.0 - 0.5) * 0.2;
        return Math.Clamp(v, 0, 1);
    }

    private static void ApplySeam(BgraImage img, CutOutOrientation orientation, int seam, CutOutEdgeStyle edge)
    {
        var horizontal = orientation == CutOutOrientation.Horizontal;
        var along = horizontal ? img.Width : img.Height;
        var across = horizontal ? img.Height : img.Width;
        var before = seam;               // pixels available on the first piece
        var after = across - seam;       // pixels available on the second piece
        if (before <= 0 || after <= 0) return;

        void Scale(int a, int c, double k)
        {
            var (x, y) = horizontal ? (a, c) : (c, a);
            var o = (y * img.Width + x) * 4;
            for (var i = 0; i < 4; i++) img.Pixels[o + i] = (byte)Math.Round(img.Pixels[o + i] * k);
        }

        if (edge == CutOutEdgeStyle.Fade)
        {
            var f1 = Math.Min(FadeWidth, before);
            var f2 = Math.Min(FadeWidth, after);
            for (var a = 0; a < along; a++)
            {
                for (var d = 0; d < f1; d++) Scale(a, seam - 1 - d, (d + 0.5) / f1);
                for (var d = 0; d < f2; d++) Scale(a, seam + d, (d + 0.5) / f2);
            }
            return;
        }

        var depth1 = Math.Min(SeamDepth, before - 1);
        var depth2 = Math.Min(SeamDepth, after - 1);
        for (var a = 0; a < along; a++)
        {
            var p = Profile(edge, a);
            // first piece loses a profiled strip just before the seam; second piece the complementary strip after it
            var cut1 = 1 + (int)Math.Round(p * Math.Max(0, depth1 - 1));
            var cut2 = 1 + (int)Math.Round((1 - p) * Math.Max(0, depth2 - 1));
            for (var d = 0; d < Math.Min(cut1, before); d++) Scale(a, seam - 1 - d, 0);
            for (var d = 0; d < Math.Min(cut2, after); d++) Scale(a, seam + d, 0);
        }
    }
}
