using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;

namespace Shotwise.Rendering;

public sealed partial class SkiaEffectProcessor
{
    private static byte ToByte(float v) => (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);

    /// <summary>Runs <paramref name="op"/> over every pixel as straight-alpha RGB floats (0..255); results are re-premultiplied.</summary>
    private static BgraImage MapStraight(BgraImage src, Func<float, float, float, (float R, float G, float B)> op)
    {
        var res = new BgraImage(src.Width, src.Height) { DpiX = src.DpiX, DpiY = src.DpiY };
        var sp = src.Pixels;
        var dp = res.Pixels;
        for (var i = 0; i < sp.Length; i += 4)
        {
            var a = sp[i + 3];
            if (a == 0) continue;
            float b = sp[i], g = sp[i + 1], r = sp[i + 2];
            if (a != 255)
            {
                var k = 255f / a;
                b = Math.Min(255, b * k); g = Math.Min(255, g * k); r = Math.Min(255, r * k);
            }
            var (nr, ng, nb) = op(r, g, b);
            if (a == 255)
            {
                dp[i] = ToByte(nb); dp[i + 1] = ToByte(ng); dp[i + 2] = ToByte(nr); dp[i + 3] = 255;
            }
            else
            {
                var k = a / 255f;
                dp[i] = ToByte(nb * k); dp[i + 1] = ToByte(ng * k); dp[i + 2] = ToByte(nr * k); dp[i + 3] = a;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ EFX-06

    internal static BgraImage ApplyColorAdjust(BgraImage src, ColorAdjustEffect e)
    {
        var bright = (float)Math.Clamp(e.Brightness, -100, 100) / 100f;
        var c = Math.Clamp(e.Contrast, -100, 100);
        var k = (float)Math.Tan((c / 100.0 + 1) * Math.PI / 4);
        if (c >= 99.9) k = 50;
        var gamma = (float)Math.Clamp(e.Gamma <= 0 ? 1 : e.Gamma, 0.1, 10);
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var v = MathF.Pow(i / 255f, 1f / gamma);
            v = (v - 0.5f) * k + 0.5f + bright;
            lut[i] = Math.Clamp(v, 0, 1) * 255f;
        }
        var identityTone = e.Brightness == 0 && e.Contrast == 0 && Math.Abs(gamma - 1) < 1e-6;
        var hueSat = e.Hue != 0 || e.Saturation != 0;
        if (identityTone && !hueSat) return src;

        float[] m = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        if (hueSat)
        {
            var s = (float)(1 + Math.Clamp(e.Saturation, -100, 100) / 100.0);
            var rad = e.Hue * Math.PI / 180.0;
            float cs = (float)Math.Cos(rad), sn = (float)Math.Sin(rad);
            float[] sat =
            {
                .213f + .787f * s, .715f - .715f * s, .072f - .072f * s,
                .213f - .213f * s, .715f + .285f * s, .072f - .072f * s,
                .213f - .213f * s, .715f - .715f * s, .072f + .928f * s,
            };
            float[] hue =
            {
                .213f + cs * .787f - sn * .213f, .715f - cs * .715f - sn * .715f, .072f - cs * .072f + sn * .928f,
                .213f - cs * .213f + sn * .143f, .715f + cs * .285f + sn * .140f, .072f - cs * .072f - sn * .283f,
                .213f - cs * .213f - sn * .787f, .715f - cs * .715f + sn * .715f, .072f + cs * .928f + sn * .072f,
            };
            m = new float[9];
            for (var r = 0; r < 3; r++)
                for (var col = 0; col < 3; col++)
                    m[r * 3 + col] = hue[r * 3] * sat[col] + hue[r * 3 + 1] * sat[3 + col] + hue[r * 3 + 2] * sat[6 + col];
        }
        return MapStraight(src, (r, g, b) =>
        {
            if (hueSat)
            {
                var nr = m[0] * r + m[1] * g + m[2] * b;
                var ng = m[3] * r + m[4] * g + m[5] * b;
                var nb = m[6] * r + m[7] * g + m[8] * b;
                r = Math.Clamp(nr, 0, 255); g = Math.Clamp(ng, 0, 255); b = Math.Clamp(nb, 0, 255);
            }
            return (lut[(int)(r + 0.5f)], lut[(int)(g + 0.5f)], lut[(int)(b + 0.5f)]);
        });
    }

    // ------------------------------------------------------------------ EFX-07

    internal static BgraImage ApplyColorReplace(BgraImage src, ColorReplaceEffect e)
    {
        if (e.Pairs.Count == 0) return src;
        var tol = Math.Clamp(e.Tolerance, 0, 1);
        var feather = Math.Max(0.0001, tol * 0.25);
        var res = src.Clone();
        var p = res.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 0) continue;
            var px = BgraImage.Unpremultiply(p[i], p[i + 1], p[i + 2], a);
            foreach (var (from, to) in e.Pairs)
            {
                var d = Argb.Distance(px.WithAlpha(255), from.WithAlpha(255));
                if (d > tol + feather) continue;
                var t = d <= tol ? 1.0 : 1.0 - (d - tol) / feather;
                var nc = Argb.Lerp(px.WithAlpha(255), to.WithAlpha(255), t);
                var outA = (byte)Math.Round(a + (to.A - 255) * 0.0); // alpha of the pixel is preserved
                var (b, g, r, aa) = BgraImage.Premultiply(nc.WithAlpha(outA));
                p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = aa;
                break;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ EFX-08

    internal static BgraImage ApplyFilter(BgraImage src, FilterEffect f)
    {
        var k = (float)Math.Clamp(f.Intensity, 0, 2);
        var op = (float)Math.Clamp(f.Opacity, 0, 1);
        if (op <= 0) return src;
        BgraImage filtered;
        switch (f.Kind)
        {
            case FilterKind.Grayscale:
            {
                var t = Math.Min(1, k);
                filtered = MapStraight(src, (r, g, b) => { var y = 0.299f * r + 0.587f * g + 0.114f * b; return (r + (y - r) * t, g + (y - g) * t, b + (y - b) * t); });
                break;
            }
            case FilterKind.Monochrome:
            {
                var t = Math.Min(1, k);
                filtered = MapStraight(src, (r, g, b) => { var y = 0.299f * r + 0.587f * g + 0.114f * b; var v = y >= 128 ? 255f : 0f; return (r + (v - r) * t, g + (v - g) * t, b + (v - b) * t); });
                break;
            }
            case FilterKind.Invert:
            {
                var t = Math.Min(1, k);
                filtered = MapStraight(src, (r, g, b) => (r + (255 - 2 * r) * t, g + (255 - 2 * g) * t, b + (255 - 2 * b) * t));
                break;
            }
            case FilterKind.Sepia:
            {
                var t = Math.Min(1, k);
                filtered = MapStraight(src, (r, g, b) =>
                {
                    var nr = 0.393f * r + 0.769f * g + 0.189f * b;
                    var ng = 0.349f * r + 0.686f * g + 0.168f * b;
                    var nb = 0.272f * r + 0.534f * g + 0.131f * b;
                    return (r + (Math.Min(255, nr) - r) * t, g + (Math.Min(255, ng) - g) * t, b + (Math.Min(255, nb) - b) * t);
                });
                break;
            }
            case FilterKind.Sharpen:
                filtered = Convolve3(src, new[] { 0, -k, 0, -k, 1 + 4 * k, -k, 0, -k, 0 }, 0, false);
                break;
            case FilterKind.Emboss:
            {
                var emb = Convolve3(src, new[] { -2f, -1, 0, -1, 1, 1, 0, 1, 2 }, 128, true);
                filtered = Blend(src, emb, Math.Min(1, k));
                break;
            }
            case FilterKind.Soften:
            {
                using var img = ToSk(src);
                using var s = Surface(src.Width, src.Height);
                using var paint = new SkiaSharp.SKPaint { ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(Math.Max(0.3f, k * 3), Math.Max(0.3f, k * 3), SkiaSharp.SKShaderTileMode.Clamp) };
                s.Canvas.DrawImage(img, 0, 0, Nearest, paint);
                filtered = Read(s, src);
                break;
            }
            default: return src;
        }
        return op >= 0.999f ? filtered : Blend(src, filtered, op);
    }

    private static BgraImage Blend(BgraImage a, BgraImage b, float t)
    {
        var res = new BgraImage(a.Width, a.Height) { DpiX = a.DpiX, DpiY = a.DpiY };
        var ap = a.Pixels; var bp = b.Pixels; var rp = res.Pixels;
        for (var i = 0; i < rp.Length; i++) rp[i] = (byte)(ap[i] + (bp[i] - ap[i]) * t + 0.5f);
        return res;
    }

    /// <summary>3x3 convolution on straight RGB (edge clamped). With <paramref name="gray"/> the kernel runs on luminance and writes grey.</summary>
    private static BgraImage Convolve3(BgraImage src, float[] kernel, float bias, bool gray)
    {
        int w = src.Width, h = src.Height;
        var res = new BgraImage(w, h) { DpiX = src.DpiX, DpiY = src.DpiY };
        var sp = src.Pixels; var dp = res.Pixels;
        var lum = gray ? new float[w * h] : null;
        if (lum is not null)
            for (var i = 0; i < w * h; i++)
            {
                var a = sp[i * 4 + 3];
                var k = a == 0 ? 0 : 255f / a;
                lum[i] = (0.299f * sp[i * 4 + 2] + 0.587f * sp[i * 4 + 1] + 0.114f * sp[i * 4]) * k;
            }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var a = sp[o + 3];
                if (a == 0) continue;
                float r = 0, g = 0, b = 0, l = 0;
                for (var ky = -1; ky <= 1; ky++)
                {
                    var yy = Math.Clamp(y + ky, 0, h - 1);
                    for (var kx = -1; kx <= 1; kx++)
                    {
                        var xx = Math.Clamp(x + kx, 0, w - 1);
                        var kv = kernel[(ky + 1) * 3 + kx + 1];
                        var i = (yy * w + xx) * 4;
                        if (lum is not null) { l += lum[yy * w + xx] * kv; continue; }
                        var pa = sp[i + 3];
                        var kk = pa == 0 ? 0 : 255f / pa;
                        b += sp[i] * kk * kv; g += sp[i + 1] * kk * kv; r += sp[i + 2] * kk * kv;
                    }
                }
                if (lum is not null) { r = g = b = l + bias; }
                else { r += bias; g += bias; b += bias; }
                var f = a / 255f;
                dp[o] = ToByte(b * f); dp[o + 1] = ToByte(g * f); dp[o + 2] = ToByte(r * f); dp[o + 3] = a;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ EFX-15

    internal static BgraImage ApplyTransparency(BgraImage src, TransparencyEffect e)
    {
        var tol = Math.Clamp(e.Tolerance, 0, 1);
        var feather = Math.Max(0.002, tol * 0.3);
        var res = src.Clone();
        var p = res.Pixels;
        var key = e.Color.WithAlpha(255);
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 0) continue;
            var px = BgraImage.Unpremultiply(p[i], p[i + 1], p[i + 2], a).WithAlpha(255);
            var d = Argb.Distance(px, key);
            if (d > tol + feather) continue;
            var keep = d <= tol ? 0f : (float)((d - tol) / feather);
            p[i] = (byte)(p[i] * keep + 0.5f); p[i + 1] = (byte)(p[i + 1] * keep + 0.5f);
            p[i + 2] = (byte)(p[i + 2] * keep + 0.5f); p[i + 3] = (byte)(a * keep + 0.5f);
        }
        return res;
    }
}
