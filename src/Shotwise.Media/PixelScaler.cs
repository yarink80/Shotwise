using Shotwise.Core.Imaging;

namespace Shotwise.Media;

internal static class PixelScaler
{
    public static BgraImage Resize(BgraImage source, int width, int height, bool letterbox = false, bool opaquePadding = true)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        var result = new BgraImage(width, height);
        double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        int w = letterbox ? Math.Max(1, (int)Math.Round(source.Width * scale)) : width;
        int h = letterbox ? Math.Max(1, (int)Math.Round(source.Height * scale)) : height;
        int ox = (width - w) / 2, oy = (height - h) / 2;
        if (opaquePadding) for (int i = 3; i < result.Pixels.Length; i += 4) result.Pixels[i] = 255;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double sx = Math.Clamp((x + .5) * source.Width / w - .5, 0, source.Width - 1);
            double sy = Math.Clamp((y + .5) * source.Height / h - .5, 0, source.Height - 1);
            int ix = (int)sx, iy = (int)sy, jx = Math.Min(ix + 1, source.Width - 1), jy = Math.Min(iy + 1, source.Height - 1);
            for (int c = 0; c < 4; c++)
            {
                double a = source.Pixels[(iy * source.Width + ix) * 4 + c] * (1 - sx + ix) + source.Pixels[(iy * source.Width + jx) * 4 + c] * (sx - ix);
                double b = source.Pixels[(jy * source.Width + ix) * 4 + c] * (1 - sx + ix) + source.Pixels[(jy * source.Width + jx) * 4 + c] * (sx - ix);
                result.Pixels[((y + oy) * width + x + ox) * 4 + c] = (byte)Math.Clamp(Math.Round(a * (1 - sy + iy) + b * (sy - iy)), 0, 255);
            }
        }
        return result;
    }
}
