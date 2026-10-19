using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Editor.Internal;

/// <summary>
/// Dependency-free raster helpers for the pixel tools (fill, eraser, pixel selection, smart move, cut-out).
/// All images are premultiplied BGRA (Core <see cref="BgraImage"/>). Pure functions: safe on any thread.
/// </summary>
internal static class RasterOps
{
    public static SizeI CanvasPixels(Page page)
        => new(Math.Max(1, (int)Math.Ceiling(page.CanvasSize.Width - 1e-6)), Math.Max(1, (int)Math.Ceiling(page.CanvasSize.Height - 1e-6)));

    /// <summary>
    /// The page's base raster at canvas size: transparent canvas with the base image copied at its offset (no canvas
    /// colour, no annotations). This is what pixel tools edit; the result is stored back as the new base image at (0,0).
    /// </summary>
    public static BgraImage ComposeBase(ShotDocument doc, Page page)
    {
        var size = CanvasPixels(page);
        var dst = new BgraImage(size.Width, size.Height) { DpiX = page.Dpi, DpiY = page.Dpi };
        if (page.Background.BaseImage is { } bi && !bi.Asset.IsEmpty && doc.Assets.Contains(bi.Asset))
        {
            var img = doc.Assets.GetImage(bi.Asset);
            dst.Blit(img, (int)Math.Round(bi.Offset.X), (int)Math.Round(bi.Offset.Y));
        }
        return dst;
    }

    /// <summary>Base raster composited over the canvas colour (opaque where the canvas is opaque) — input for inpainting/vision.</summary>
    public static BgraImage ComposeFlattenedBase(ShotDocument doc, Page page)
    {
        var baseImg = ComposeBase(doc, page);
        var bg = page.Background.Color;
        if (bg.A == 0) return baseImg;
        var dst = BgraImage.CreateFilled(baseImg.Width, baseImg.Height, bg);
        dst = new BgraImage(dst.Width, dst.Height, dst.Pixels) { DpiX = baseImg.DpiX, DpiY = baseImg.DpiY };
        dst.DrawOver(baseImg, 0, 0);
        return dst;
    }

    // ------------------------------------------------------------------ flood fill

    /// <summary>
    /// Colour region around <paramref name="seed"/>: pixels whose straight colour is within <paramref name="tolerance"/>
    /// (0..1, <see cref="Argb.Distance"/>) of the seed colour; 4-connected when <paramref name="contiguous"/>, else global.
    /// </summary>
    public static Mask ColorRegion(BgraImage img, PointI seed, double tolerance, bool contiguous)
    {
        var m = new Mask(img.Width, img.Height);
        if ((uint)seed.X >= (uint)img.Width || (uint)seed.Y >= (uint)img.Height) return m;
        var target = img.GetPixel(seed.X, seed.Y);
        var tol = Math.Clamp(tolerance, 0, 1) + 1e-9;
        bool Match(int x, int y) => Argb.Distance(img.GetPixel(x, y), target) <= tol;
        if (!contiguous)
        {
            for (var y = 0; y < img.Height; y++)
                for (var x = 0; x < img.Width; x++)
                    if (Match(x, y)) m.Data[y * img.Width + x] = 255;
            return m;
        }
        var w = img.Width;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seed.X, seed.Y));
        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            if (m.Data[sy * w + sx] != 0 || !Match(sx, sy)) continue;
            var l = sx;
            while (l > 0 && m.Data[sy * w + l - 1] == 0 && Match(l - 1, sy)) l--;
            var r = sx;
            while (r < w - 1 && m.Data[sy * w + r + 1] == 0 && Match(r + 1, sy)) r++;
            for (var x = l; x <= r; x++)
            {
                m.Data[sy * w + x] = 255;
                if (sy > 0 && m.Data[(sy - 1) * w + x] == 0) stack.Push((x, sy - 1));
                if (sy < img.Height - 1 && m.Data[(sy + 1) * w + x] == 0) stack.Push((x, sy + 1));
            }
        }
        return m;
    }

    /// <summary>Paints <paramref name="color"/> (straight alpha, scaled by mask coverage and <paramref name="opacity"/>) over masked pixels.</summary>
    public static void PaintMasked(BgraImage img, Mask m, Argb color, double opacity = 1, bool replace = false)
    {
        var (cb, cg, cr, ca) = BgraImage.Premultiply(color);
        var px = img.Pixels;
        var op = Math.Clamp(opacity, 0, 1);
        for (var i = 0; i < m.Data.Length; i++)
        {
            var cov = m.Data[i];
            if (cov == 0) continue;
            var k = cov / 255.0 * op;
            var o = i * 4;
            if (replace)
            {
                // lerp toward the colour (used for "transparent" fills and eraser)
                px[o] = (byte)Math.Round(px[o] + (cb - px[o]) * k);
                px[o + 1] = (byte)Math.Round(px[o + 1] + (cg - px[o + 1]) * k);
                px[o + 2] = (byte)Math.Round(px[o + 2] + (cr - px[o + 2]) * k);
                px[o + 3] = (byte)Math.Round(px[o + 3] + (ca - px[o + 3]) * k);
            }
            else
            {
                var sa = ca / 255.0 * k;
                var inv = 1 - sa;
                px[o] = (byte)Math.Min(255, cb * k + px[o] * inv + 0.5);
                px[o + 1] = (byte)Math.Min(255, cg * k + px[o + 1] * inv + 0.5);
                px[o + 2] = (byte)Math.Min(255, cr * k + px[o + 2] * inv + 0.5);
                px[o + 3] = (byte)Math.Min(255, ca * k + px[o + 3] * inv + 0.5);
            }
        }
    }

    // ------------------------------------------------------------------ strokes

    /// <summary>Coverage mask of a round-capped polyline of the given width (anti-aliased edge when requested).</summary>
    public static Mask StrokeMask(int width, int height, IReadOnlyList<Point2> points, double strokeWidth, bool antialias)
    {
        var m = new Mask(width, height);
        if (points.Count == 0) return m;
        var r = Math.Max(0.5, strokeWidth / 2);
        var pts = points.Count == 1 ? new[] { points[0], points[0] } : points.ToArray();
        for (var i = 0; i + 1 < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var bb = Rect2.FromPoints(a, b).Inflate(r + 1).ToRectIOutward().Intersect(new RectI(0, 0, width, height));
            for (var y = bb.Y; y < bb.Bottom; y++)
                for (var x = bb.X; x < bb.Right; x++)
                {
                    var d = GeometryMath.DistanceToSegment(new Point2(x + 0.5, y + 0.5), a, b);
                    byte v;
                    if (antialias) v = (byte)Math.Round(Math.Clamp(r + 0.5 - d, 0, 1) * 255);
                    else v = d <= r ? (byte)255 : (byte)0;
                    var idx = y * width + x;
                    if (v > m.Data[idx]) m.Data[idx] = v;
                }
        }
        return m;
    }

    // ------------------------------------------------------------------ pixel selection

    /// <summary>Copy of the masked pixels inside <paramref name="bounds"/> (alpha multiplied by coverage).</summary>
    public static BgraImage ExtractMasked(BgraImage img, Mask m, RectI bounds)
    {
        var c = bounds.Intersect(img.Bounds);
        if (c.IsEmpty) return new BgraImage(1, 1);
        var dst = img.Crop(c);
        for (var y = 0; y < c.Height; y++)
            for (var x = 0; x < c.Width; x++)
            {
                var cov = m[c.X + x, c.Y + y];
                if (cov == 255) continue;
                var o = (y * c.Width + x) * 4;
                for (var k = 0; k < 4; k++) dst.Pixels[o + k] = (byte)(dst.Pixels[o + k] * cov / 255);
            }
        return dst;
    }

    /// <summary>Makes the masked pixels transparent (coverage-weighted).</summary>
    public static void ClearMasked(BgraImage img, Mask m)
    {
        var px = img.Pixels;
        for (var i = 0; i < m.Data.Length; i++)
        {
            var cov = m.Data[i];
            if (cov == 0) continue;
            var keep = 255 - cov;
            var o = i * 4;
            for (var k = 0; k < 4; k++) px[o + k] = (byte)(px[o + k] * keep / 255);
        }
    }

    /// <summary>Mask shifted by an integer offset (pixels moved off-canvas are dropped).</summary>
    public static Mask Translate(Mask m, int dx, int dy)
    {
        var outp = new Mask(m.Width, m.Height);
        for (var y = 0; y < m.Height; y++)
        {
            var ny = y + dy;
            if ((uint)ny >= (uint)m.Height) continue;
            for (var x = 0; x < m.Width; x++)
            {
                var nx = x + dx;
                if ((uint)nx >= (uint)m.Width) continue;
                outp.Data[ny * m.Width + nx] = m.Data[y * m.Width + x];
            }
        }
        return outp;
    }

    /// <summary>
    /// Classical fallback inpainting (no vision service): every masked pixel becomes the average of a horizontal and a
    /// vertical linear interpolation between the nearest unmasked neighbours. Good for flat UI backgrounds.
    /// </summary>
    public static BgraImage FillFromSurroundings(BgraImage img, Mask m)
    {
        var w = img.Width;
        var h = img.Height;
        var src = img.Pixels;
        var dst = img.Clone();
        var horiz = new double[4];
        var vert = new double[4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (m.Data[y * w + x] < 128) continue;
                var hv = Interp(src, m, w, h, x, y, 1, 0, horiz);
                var vv = Interp(src, m, w, h, x, y, 0, 1, vert);
                var o = (y * w + x) * 4;
                for (var k = 0; k < 4; k++)
                {
                    double v = hv && vv ? (horiz[k] + vert[k]) / 2 : hv ? horiz[k] : vv ? vert[k] : 0;
                    dst.Pixels[o + k] = (byte)Math.Clamp(Math.Round(v), 0, 255);
                }
            }
        }
        return dst;
    }

    private static bool Interp(byte[] px, Mask m, int w, int h, int x, int y, int sx, int sy, double[] outp)
    {
        int ax = x, ay = y, bx = x, by = y;
        var hasA = false;
        var hasB = false;
        while (true)
        {
            ax -= sx; ay -= sy;
            if (ax < 0 || ay < 0) break;
            if (m.Data[ay * w + ax] < 128) { hasA = true; break; }
        }
        while (true)
        {
            bx += sx; by += sy;
            if (bx >= w || by >= h) break;
            if (m.Data[by * w + bx] < 128) { hasB = true; break; }
        }
        if (!hasA && !hasB) return false;
        var ia = (ay * w + ax) * 4;
        var ib = (by * w + bx) * 4;
        if (hasA && hasB)
        {
            var span = sx != 0 ? bx - ax : by - ay;
            var t = (double)(sx != 0 ? x - ax : y - ay) / span;
            for (var k = 0; k < 4; k++) outp[k] = px[ia + k] + (px[ib + k] - px[ia + k]) * t;
        }
        else
        {
            var i = hasA ? ia : ib;
            for (var k = 0; k < 4; k++) outp[k] = px[i + k];
        }
        return true;
    }

    /// <summary>Union of masks, or an empty canvas mask.</summary>
    public static Mask UnionAll(int w, int h, IEnumerable<Mask> masks)
    {
        var outp = new Mask(w, h);
        foreach (var m in masks)
        {
            if (m.Width != w || m.Height != h) continue;
            for (var i = 0; i < outp.Data.Length; i++) if (m.Data[i] > outp.Data[i]) outp.Data[i] = m.Data[i];
        }
        return outp;
    }
}
