using System.Runtime.InteropServices;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.UI.Editor.Surface;

/// <summary>BgraImage (premultiplied BGRA) ⇄ Skia bitmaps.</summary>
internal static class SkiaConvert
{
    public static SKBitmap ToBitmap(BgraImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(img.Pixels, 0, bmp.GetPixels(), img.Pixels.Length);
        return bmp;
    }

    public static SKColor ToSk(this Argb c) => new(c.R, c.G, c.B, c.A);

    public static SKBitmap TintBitmap(Mask mask, Argb tint, bool invert)
    {
        var bmp = new SKBitmap(new SKImageInfo(mask.Width, mask.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var buffer = new byte[mask.Width * mask.Height * 4];
        var ta = tint.A / 255.0;
        for (var i = 0; i < mask.Data.Length; i++)
        {
            var cover = mask.Data[i] / 255.0;
            if (invert) cover = 1 - cover;
            var a = cover * ta;
            if (a <= 0) continue;
            var o = i * 4;
            buffer[o] = (byte)Math.Round(tint.B * a);
            buffer[o + 1] = (byte)Math.Round(tint.G * a);
            buffer[o + 2] = (byte)Math.Round(tint.R * a);
            buffer[o + 3] = (byte)Math.Round(255 * a);
        }
        Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length);
        return bmp;
    }
}
