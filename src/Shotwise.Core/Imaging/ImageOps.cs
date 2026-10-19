namespace Shotwise.Core.Imaging;

/// <summary>Small, dependency-free raster helpers (thumbnails, previews). Heavy processing lives in Rendering/Vision.</summary>
public static class ImageOps
{
    /// <summary>
    /// Scales <paramref name="src"/> so its longest side is at most <paramref name="maxSide"/> using an area (box) filter.
    /// Returns the source instance unchanged when it already fits. Integer rounding preserves aspect ratio.
    /// </summary>
    public static BgraImage Downscale(BgraImage src, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (maxSide <= 0) throw new ArgumentOutOfRangeException(nameof(maxSide));
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src;
        var scale = (double)maxSide / longest;
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        return ResizeArea(src, w, h);
    }

    /// <summary>Area-averaging resize (good quality for downscaling; nearest-ish when upscaling).</summary>
    public static BgraImage ResizeArea(BgraImage src, int width, int height)
    {
        var dst = new BgraImage(width, height) { DpiX = src.DpiX, DpiY = src.DpiY };
        var sx = (double)src.Width / width;
        var sy = (double)src.Height / height;
        var sp = src.Pixels;
        var dp = dst.Pixels;
        for (var y = 0; y < height; y++)
        {
            var y0 = y * sy;
            var y1 = Math.Min(src.Height, (y + 1) * sy);
            var iy0 = (int)y0;
            var iy1 = Math.Min(src.Height - 1, (int)Math.Ceiling(y1) - 1);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * sx;
                var x1 = Math.Min(src.Width, (x + 1) * sx);
                var ix0 = (int)x0;
                var ix1 = Math.Min(src.Width - 1, (int)Math.Ceiling(x1) - 1);
                double b = 0, g = 0, r = 0, a = 0, wsum = 0;
                for (var yy = iy0; yy <= iy1; yy++)
                {
                    var wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                    if (wy <= 0) continue;
                    var row = yy * src.Width;
                    for (var xx = ix0; xx <= ix1; xx++)
                    {
                        var wx = Math.Min(xx + 1, x1) - Math.Max(xx, x0);
                        if (wx <= 0) continue;
                        var wgt = wx * wy;
                        var i = (row + xx) * 4;
                        b += sp[i] * wgt; g += sp[i + 1] * wgt; r += sp[i + 2] * wgt; a += sp[i + 3] * wgt;
                        wsum += wgt;
                    }
                }
                var o = (y * width + x) * 4;
                if (wsum <= 0) continue;
                dp[o] = (byte)Math.Min(255, b / wsum + 0.5);
                dp[o + 1] = (byte)Math.Min(255, g / wsum + 0.5);
                dp[o + 2] = (byte)Math.Min(255, r / wsum + 0.5);
                dp[o + 3] = (byte)Math.Min(255, a / wsum + 0.5);
            }
        }
        return dst;
    }

    /// <summary>Composites the image over an opaque background colour (used for "prevent transparency" exports).</summary>
    public static BgraImage Flatten(BgraImage src, Argb background)
    {
        var dst = BgraImage.CreateFilled(src.Width, src.Height, background.WithAlpha(255));
        dst = new BgraImage(dst.Width, dst.Height, dst.Pixels) { DpiX = src.DpiX, DpiY = src.DpiY };
        dst.DrawOver(src, 0, 0);
        return dst;
    }
}
