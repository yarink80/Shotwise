using System.Runtime.InteropServices;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>Conversions between Core value types and SkiaSharp types.</summary>
internal static class SkiaInterop
{
    public static SKColor ToSk(this Argb c) => new(c.R, c.G, c.B, c.A);
    public static Argb ToArgb(this SKColor c) => Argb.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    public static SKPoint ToSk(this Point2 p) => new((float)p.X, (float)p.Y);
    public static Point2 ToPoint2(this SKPoint p) => new(p.X, p.Y);
    public static SKRect ToSk(this Rect2 r) => new((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
    public static Rect2 ToRect2(this SKRect r) => new(r.Left, r.Top, r.Width, r.Height);

    public static readonly SKImageInfo DefaultInfoTemplate = new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);

    public static SKImageInfo Info(int w, int h) => new(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>Creates an SKBitmap holding a copy of the (premultiplied BGRA) image.</summary>
    public static SKBitmap ToBitmap(BgraImage img)
    {
        var bmp = new SKBitmap(Info(img.Width, img.Height));
        Marshal.Copy(img.Pixels, 0, bmp.GetPixels(), img.Width * img.Height * 4);
        return bmp;
    }

    public static SKImage ToImage(BgraImage img)
    {
        using var bmp = ToBitmap(img);
        return SKImage.FromBitmap(bmp);
    }

    /// <summary>Copies any bitmap into a premultiplied BGRA <see cref="BgraImage"/> (converting when needed).</summary>
    public static BgraImage ToBgra(SKBitmap bmp, double dpiX = 96, double dpiY = 96)
    {
        var res = new BgraImage(bmp.Width, bmp.Height) { DpiX = dpiX, DpiY = dpiY };
        if (bmp.ColorType == SKColorType.Bgra8888 && bmp.AlphaType == SKAlphaType.Premul && bmp.RowBytes == bmp.Width * 4)
        {
            Marshal.Copy(bmp.GetPixels(), res.Pixels, 0, bmp.Width * bmp.Height * 4);
            return res;
        }
        var info = Info(bmp.Width, bmp.Height);
        var handle = GCHandle.Alloc(res.Pixels, GCHandleType.Pinned);
        try
        {
            using var pm = bmp.PeekPixels();
            if (!pm.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                throw new InvalidOperationException("Unable to convert bitmap to BGRA.");
        }
        finally { handle.Free(); }
        return res;
    }

    public static BgraImage ToBgra(SKImage img, double dpiX = 96, double dpiY = 96)
    {
        var res = new BgraImage(img.Width, img.Height) { DpiX = dpiX, DpiY = dpiY };
        var info = Info(img.Width, img.Height);
        var handle = GCHandle.Alloc(res.Pixels, GCHandleType.Pinned);
        try
        {
            if (!img.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                throw new InvalidOperationException("Unable to read image pixels.");
        }
        finally { handle.Free(); }
        return res;
    }

    /// <summary>a ∘ b: applies <paramref name="b"/> first, then <paramref name="a"/>.</summary>
    public static SKMatrix Mul(SKMatrix a, SKMatrix b) => new(
        a.ScaleX * b.ScaleX + a.SkewX * b.SkewY,
        a.ScaleX * b.SkewX + a.SkewX * b.ScaleY,
        a.ScaleX * b.TransX + a.SkewX * b.TransY + a.TransX,
        a.SkewY * b.ScaleX + a.ScaleY * b.SkewY,
        a.SkewY * b.SkewX + a.ScaleY * b.ScaleY,
        a.SkewY * b.TransX + a.ScaleY * b.TransY + a.TransY,
        0, 0, 1);

    public static SKMatrix Invert(SKMatrix m) => m.TryInvert(out var inv) ? inv : SKMatrix.Identity;

    public static float F(double v) => (float)v;
}
