using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

public sealed partial class SkiaEffectProcessor
{
    // ------------------------------------------------------------------ EFX-02 edges

    private static EffectResult ApplyEdge(BgraImage src, EdgeEffect e)
    {
        if (e.Sides == EdgeSides.None || e.Size <= 0) return new EffectResult(src, Point2.Zero);
        var size = Math.Max(1, e.Size);
        BgraImage img;
        var offset = Point2.Zero;
        switch (e.Style)
        {
            case EdgeStyle.DropShadow:
            {
                var dx = ((e.Sides.HasFlag(EdgeSides.Right) ? 1 : 0) - (e.Sides.HasFlag(EdgeSides.Left) ? 1 : 0)) * 0.5;
                var dy = ((e.Sides.HasFlag(EdgeSides.Bottom) ? 1 : 0) - (e.Sides.HasFlag(EdgeSides.Top) ? 1 : 0)) * 0.5;
                var all = e.Sides == EdgeSides.All;
                var r = ShadowCore(src, Argb.Black, (dx, dy), all ? 0 : size * 0.5, size, 0.55, all, e.Sides);
                return e.WithBorder ? new EffectResult(DrawInsetBorder(r.Image, 1), r.ContentOffset) : r;
            }
            case EdgeStyle.Fade: img = FadeEdges(src, e.Sides, size); break;
            case EdgeStyle.Bevel: img = BevelEdges(src, e.Sides, size); break;
            default: img = CutEdges(src, e.Style, e.Sides, size); break;
        }
        if (e.WithBorder && e.Style is not (EdgeStyle.Fade or EdgeStyle.Bevel)) img = DrawInsetBorder(img, 1);
        if (e.WithShadow && e.Style is not EdgeStyle.DropShadow)
        {
            var r = ShadowCore(img, Argb.Black, (1, 1), Math.Max(3, size * 0.4), Math.Max(4, size * 0.7), 0.5, false, null);
            return new EffectResult(r.Image, new Point2(r.ContentOffset.X + offset.X, r.ContentOffset.Y + offset.Y));
        }
        return new EffectResult(img, offset);
    }

    private static float Hash(int n)
    {
        n = (n << 13) ^ n;
        return 1f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824f;
    }

    private static float ValueNoise(float x, int seed)
    {
        var i = (int)MathF.Floor(x);
        var f = x - i;
        var s = f * f * (3 - 2 * f);
        var a = Hash(i + seed * 131);
        var b = Hash(i + 1 + seed * 131);
        return a + (b - a) * s;
    }

    /// <summary>Distance (0..size) the material is removed from the edge at position <paramref name="t"/> along the edge.</summary>
    internal static float EdgeDisplacement(EdgeStyle style, float t, float size, int seed)
    {
        switch (style)
        {
            case EdgeStyle.Torn:
            {
                var v = 0.5f + 0.34f * ValueNoise(t / (size * 0.9f), seed) + 0.24f * ValueNoise(t / (size * 0.28f) + 7, seed + 3)
                        + 0.14f * Hash((int)MathF.Floor(t * 1.3f) + seed * 977);
                return size * Math.Clamp(v, 0.04f, 1f);
            }
            case EdgeStyle.Wave:
                return size * (0.5f + 0.5f * MathF.Sin(2 * MathF.PI * t / (size * 3.2f) + seed));
            case EdgeStyle.Saw:
            {
                var p = Math.Max(6f, size * 1.1f);
                var f = t / p - MathF.Floor(t / p);
                return size * f;
            }
            case EdgeStyle.Sharktooth:
            {
                var p = Math.Max(8f, size * 1.4f);
                var f = t / p - MathF.Floor(t / p);
                return size * (1f - MathF.Abs(2f * f - 1f));
            }
            default: return 0;
        }
    }

    private static BgraImage CutEdges(BgraImage src, EdgeStyle style, EdgeSides sides, double sizeD)
    {
        var size = (float)sizeD;
        var w = src.Width;
        var h = src.Height;
        var res = src.Clone();
        var px = res.Pixels;
        // Precompute displacement profiles per side.
        float[]? top = sides.HasFlag(EdgeSides.Top) ? Profile(style, w, size, 1) : null;
        float[]? bottom = sides.HasFlag(EdgeSides.Bottom) ? Profile(style, w, size, 2) : null;
        float[]? left = sides.HasFlag(EdgeSides.Left) ? Profile(style, h, size, 3) : null;
        float[]? right = sides.HasFlag(EdgeSides.Right) ? Profile(style, h, size, 4) : null;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cov = 1f;
                if (top is not null && y < size + 1) cov *= Math.Clamp(y + 0.5f - top[x], 0, 1);
                if (bottom is not null && h - 1 - y < size + 1) cov *= Math.Clamp(h - 1 - y + 0.5f - bottom[x], 0, 1);
                if (left is not null && x < size + 1) cov *= Math.Clamp(x + 0.5f - left[y], 0, 1);
                if (right is not null && w - 1 - x < size + 1) cov *= Math.Clamp(w - 1 - x + 0.5f - right[y], 0, 1);
                if (cov >= 0.999f) continue;
                var i = (y * w + x) * 4;
                px[i] = (byte)(px[i] * cov + 0.5f);
                px[i + 1] = (byte)(px[i + 1] * cov + 0.5f);
                px[i + 2] = (byte)(px[i + 2] * cov + 0.5f);
                px[i + 3] = (byte)(px[i + 3] * cov + 0.5f);
            }
        }
        return res;
    }

    private static float[] Profile(EdgeStyle style, int length, float size, int seed)
    {
        var p = new float[length];
        for (var i = 0; i < length; i++) p[i] = EdgeDisplacement(style, i, size, seed);
        return p;
    }

    private static BgraImage FadeEdges(BgraImage src, EdgeSides sides, double sizeD)
    {
        var size = (float)sizeD;
        var w = src.Width;
        var h = src.Height;
        var res = src.Clone();
        var px = res.Pixels;
        static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var cov = 1f;
                if (sides.HasFlag(EdgeSides.Top)) cov *= Smooth((y + 0.5f) / size);
                if (sides.HasFlag(EdgeSides.Bottom)) cov *= Smooth((h - y - 0.5f) / size);
                if (sides.HasFlag(EdgeSides.Left)) cov *= Smooth((x + 0.5f) / size);
                if (sides.HasFlag(EdgeSides.Right)) cov *= Smooth((w - x - 0.5f) / size);
                if (cov >= 0.999f) continue;
                var i = (y * w + x) * 4;
                px[i] = (byte)(px[i] * cov + 0.5f);
                px[i + 1] = (byte)(px[i + 1] * cov + 0.5f);
                px[i + 2] = (byte)(px[i + 2] * cov + 0.5f);
                px[i + 3] = (byte)(px[i + 3] * cov + 0.5f);
            }
        }
        return res;
    }

    /// <summary>3D bevel: top/left bands are lightened, bottom/right bands darkened, with mitred corners.</summary>
    private static BgraImage BevelEdges(BgraImage src, EdgeSides sides, double sizeD)
    {
        var size = (float)sizeD;
        var w = src.Width;
        var h = src.Height;
        var res = src.Clone();
        var px = res.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var best = float.MaxValue;
                var light = false;
                if (sides.HasFlag(EdgeSides.Top)) { var d = y + 0.5f; if (d < best) { best = d; light = true; } }
                if (sides.HasFlag(EdgeSides.Left)) { var d = x + 0.5f; if (d < best) { best = d; light = true; } }
                if (sides.HasFlag(EdgeSides.Bottom)) { var d = h - y - 0.5f; if (d < best) { best = d; light = false; } }
                if (sides.HasFlag(EdgeSides.Right)) { var d = w - x - 0.5f; if (d < best) { best = d; light = false; } }
                if (best >= size) continue;
                var t = 1f - best / size;
                var i = (y * w + x) * 4;
                var a = px[i + 3];
                if (a == 0) continue;
                if (light)
                {
                    var k = 0.55f * t;
                    for (var c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(a, px[i + c] + (a - px[i + c]) * k);
                }
                else
                {
                    var k = 1f - 0.5f * t;
                    for (var c = 0; c < 3; c++) px[i + c] = (byte)(px[i + c] * k);
                }
            }
        }
        return res;
    }

    /// <summary>Draws a 1px (inside) outline along the boundary of the opaque area, including cut edges.</summary>
    private static BgraImage DrawInsetBorder(BgraImage src, int width)
    {
        var w = src.Width;
        var h = src.Height;
        var res = src.Clone();
        var sp = src.Pixels;
        var dp = res.Pixels;
        bool Transparent(int x, int y) => (uint)x >= (uint)w || (uint)y >= (uint)h || sp[(y * w + x) * 4 + 3] < 128;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (sp[i + 3] < 128) continue;
                var edge = false;
                for (var d = 1; d <= width && !edge; d++)
                    edge = Transparent(x - d, y) || Transparent(x + d, y) || Transparent(x, y - d) || Transparent(x, y + d);
                if (!edge) continue;
                dp[i] = 0x50; dp[i + 1] = 0x50; dp[i + 2] = 0x50; dp[i + 3] = 255;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ EFX-05 page curl

    private static EffectResult ApplyPageCurl(BgraImage src, PageCurlEffect pc)
    {
        var w = src.Width;
        var h = src.Height;
        var s = (float)Math.Clamp(pc.Size, 2, Math.Min(w, h) * 0.9);
        // Work in a frame where the curl is at the bottom-right, then flip back.
        var flipX = pc.Corner is Corner.TopLeft or Corner.BottomLeft;
        var flipY = pc.Corner is Corner.TopLeft or Corner.TopRight;
        using var img = ToSk(src);
        using var surface = Surface(w, h);
        var c = surface.Canvas;
        c.Save();
        if (flipX) { c.Translate(w, 0); c.Scale(-1, 1); }
        if (flipY) { c.Translate(0, h); c.Scale(1, -1); }

        // main body without the corner triangle
        using var body = new SKPath();
        body.MoveTo(0, 0); body.LineTo(w, 0); body.LineTo(w, h - s); body.LineTo(w - s, h); body.LineTo(0, h); body.Close();
        // flap (folded-over corner, mirrored)
        using var flap = new SKPath();
        flap.MoveTo(w - s, h); flap.LineTo(w, h - s); flap.LineTo(w - s, h - s); flap.Close();

        c.Save();
        c.ClipPath(body, SKClipOperation.Intersect, true);
        c.DrawImage(img, 0, 0);
        // soft shadow cast by the flap onto the page
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, s * 0.07f) })
        {
            c.Save();
            c.Translate(-s * 0.05f, -s * 0.05f);
            c.DrawPath(flap, shadow);
            c.Restore();
        }
        c.Restore();

        // flap back side: mirrored source pixels, whitened, with a shading gradient
        c.Save();
        c.ClipPath(flap, SKClipOperation.Intersect, true);
        var cc = w + h - s;
        var reflect = new SKMatrix(0, -1, cc, -1, 0, cc, 0, 0, 1);
        c.Concat(in reflect);
        c.DrawImage(img, 0, 0);
        c.Restore();
        c.Save();
        c.ClipPath(flap, SKClipOperation.Intersect, true);
        using (var sheen = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(w - s / 2, h - s / 2), new SKPoint(w - s, h - s),
                new[] { new SKColor(255, 255, 255, 235), new SKColor(205, 205, 205, 200) }, null, SKShaderTileMode.Clamp),
        })
            c.DrawRect(w - s, h - s, s, s, sheen);
        c.Restore();
        using (var fold = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0, 0, 0, 60) })
            c.DrawLine(w - s, h, w, h - s, fold);
        c.Restore();
        return new EffectResult(Read(surface, src), Point2.Zero);
    }

    // ------------------------------------------------------------------ EFX-04 perspective / shear

    private static EffectResult ApplyPerspective(BgraImage src, PerspectiveEffect p)
    {
        double w = src.Width, h = src.Height;
        var hz = Math.Clamp(p.Horizontal, -90, 90) / 100.0;
        var vt = Math.Clamp(p.Vertical, -90, 90) / 100.0;
        if (Math.Abs(hz) < 1e-6 && Math.Abs(vt) < 1e-6) return new EffectResult(src, Point2.Zero);

        // destination quad for TL, TR, BR, BL
        SKPoint[] quad;
        if (p.Shear)
        {
            // x' = x + hz*(H - y);  y' = y + vt*x
            SKPoint M(double x, double y) => new((float)(x + hz * (h - y)), (float)(y + vt * x));
            quad = new[] { M(0, 0), M(w, 0), M(w, h), M(0, h) };
        }
        else
        {
            var ih = hz * h / 2; // vertical inset at the far side
            var iw = vt * w / 2;
            var tl = new SKPoint(0, 0);
            var tr = new SKPoint((float)w, 0);
            var br = new SKPoint((float)w, (float)h);
            var bl = new SKPoint(0, (float)h);
            if (hz > 0) { tr.Y += (float)ih; br.Y -= (float)ih; }
            else if (hz < 0) { tl.Y += (float)-ih; bl.Y -= (float)-ih; }
            if (vt > 0) { bl.X += (float)iw; br.X -= (float)iw; }
            else if (vt < 0) { tl.X += (float)-iw; tr.X -= (float)-iw; }
            quad = new[] { tl, tr, br, bl };
        }
        var minX = quad.Min(q => q.X);
        var minY = quad.Min(q => q.Y);
        var maxX = quad.Max(q => q.X);
        var maxY = quad.Max(q => q.Y);
        var pad = p.Border ? 2 : 0;
        var ow = (int)Math.Ceiling(maxX - minX) + pad * 2;
        var oh = (int)Math.Ceiling(maxY - minY) + pad * 2;
        for (var i = 0; i < 4; i++) quad[i] = new SKPoint(quad[i].X - minX + pad, quad[i].Y - minY + pad);

        var m = HomographyFromUnitRect(w, h, quad);
        using var img = ToSk(src);
        using var surface = Surface(ow, oh);
        var c = surface.Canvas;
        using var clip = new SKPath();
        clip.MoveTo(quad[0]); clip.LineTo(quad[1]); clip.LineTo(quad[2]); clip.LineTo(quad[3]); clip.Close();
        c.Save();
        c.ClipPath(clip, SKClipOperation.Intersect, true);
        c.Concat(in m);
        using (var paint = new SKPaint { IsAntialias = true })
            c.DrawImage(img, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        c.Restore();
        if (p.Border)
        {
            using var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x50, 0x50, 0x50), StrokeJoin = SKStrokeJoin.Miter };
            c.DrawPath(clip, bp);
        }
        var result = new EffectResult(Read(surface, src), new Point2(-minX + pad, -minY + pad));
        if (p.Shadow)
        {
            var sh = ShadowCore(result.Image, Argb.Black, (1, 1), 8, 10, 0.5, false, null);
            return new EffectResult(sh.Image, new Point2(result.ContentOffset.X + sh.ContentOffset.X, result.ContentOffset.Y + sh.ContentOffset.Y));
        }
        return result;
    }

    /// <summary>Projective transform mapping the rectangle (0,0,w,h) onto an arbitrary quad (TL, TR, BR, BL).</summary>
    internal static SKMatrix HomographyFromUnitRect(double w, double h, SKPoint[] q)
    {
        var src = new (double X, double Y)[] { (0, 0), (w, 0), (w, h), (0, h) };
        // Solve for h0..h7 with h8 = 1:  u = (h0 x + h1 y + h2)/(h6 x + h7 y + 1),  v = (h3 x + h4 y + h5)/(h6 x + h7 y + 1)
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = src[i];
            double u = q[i].X, v = q[i].Y;
            a[2 * i, 0] = x; a[2 * i, 1] = y; a[2 * i, 2] = 1; a[2 * i, 6] = -u * x; a[2 * i, 7] = -u * y; a[2 * i, 8] = u;
            a[2 * i + 1, 3] = x; a[2 * i + 1, 4] = y; a[2 * i + 1, 5] = 1; a[2 * i + 1, 6] = -v * x; a[2 * i + 1, 7] = -v * y; a[2 * i + 1, 8] = v;
        }
        for (var col = 0; col < 8; col++)
        {
            var piv = col;
            for (var r = col + 1; r < 8; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) return SKMatrix.Identity;
            if (piv != col) for (var k = 0; k < 9; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
            var div = a[col, col];
            for (var k = col; k < 9; k++) a[col, k] /= div;
            for (var r = 0; r < 8; r++)
            {
                if (r == col) continue;
                var f = a[r, col];
                if (f == 0) continue;
                for (var k = col; k < 9; k++) a[r, k] -= f * a[col, k];
            }
        }
        var hh = new float[8];
        for (var i = 0; i < 8; i++) hh[i] = (float)a[i, 8];
        return new SKMatrix(hh[0], hh[1], hh[2], hh[3], hh[4], hh[5], hh[6], hh[7], 1);
    }
}
