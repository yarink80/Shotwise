using Shotwise.Core.Geometry;

namespace Shotwise.Core.Imaging;

/// <summary>
/// In-memory raster: premultiplied BGRA8, row stride = Width*4, origin top-left.
/// This is the interchange format between every engine (capture, rendering, vision, media).
/// </summary>
public sealed class BgraImage
{
    /// <summary>Hard cap on pixel count (16k × 16k) to limit untrusted image allocations.</summary>
    public const long MaxPixels = 268_435_456L;

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public double DpiX { get; init; } = 96;
    public double DpiY { get; init; } = 96;

    public int Stride => Width * 4;
    public SizeI Size => new(Width, Height);
    public RectI Bounds => new(0, 0, Width, Height);

    public BgraImage(int w, int h)
    {
        Validate(w, h);
        Width = w;
        Height = h;
        Pixels = new byte[checked(w * h * 4)];
    }

    public BgraImage(int w, int h, byte[] pixels)
    {
        Validate(w, h);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < (long)w * h * 4) throw new ArgumentException($"Pixel buffer too small for {w}x{h} BGRA.", nameof(pixels));
        Width = w;
        Height = h;
        Pixels = pixels;
    }

    private static void Validate(int w, int h)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), $"Image dimensions must be positive (got {w}x{h}).");
        if ((long)w * h > MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, $"Image {w}x{h} exceeds the maximum supported size.");
    }

    public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone()) { DpiX = DpiX, DpiY = DpiY };

    /// <summary>Copies the intersection of <paramref name="r"/> with the image bounds.</summary>
    public BgraImage Crop(RectI r)
    {
        var c = r.Intersect(Bounds);
        if (c.IsEmpty) throw new ArgumentOutOfRangeException(nameof(r), "Crop rectangle does not intersect the image.");
        var dst = new BgraImage(c.Width, c.Height) { DpiX = DpiX, DpiY = DpiY };
        var rowBytes = c.Width * 4;
        for (var y = 0; y < c.Height; y++)
            Buffer.BlockCopy(Pixels, ((c.Y + y) * Width + c.X) * 4, dst.Pixels, y * rowBytes, rowBytes);
        return dst;
    }

    /// <summary>Creates an image filled with a straight-alpha colour.</summary>
    public static BgraImage CreateFilled(int w, int h, Argb color)
    {
        var img = new BgraImage(w, h);
        img.Fill(color);
        return img;
    }

    /// <summary>Fills the whole image with a straight-alpha colour (stored premultiplied).</summary>
    public void Fill(Argb color)
    {
        var (b, g, r, a) = Premultiply(color);
        var px = Pixels;
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
    }

    /// <summary>Returns the straight-alpha colour of a pixel.</summary>
    public Argb GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x));
        var i = (y * Width + x) * 4;
        return Unpremultiply(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    /// <summary>Sets a pixel from a straight-alpha colour.</summary>
    public void SetPixel(int x, int y, Argb c)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x));
        var i = (y * Width + x) * 4;
        var (b, g, r, a) = Premultiply(c);
        Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = a;
    }

    /// <summary>Copies <paramref name="src"/> onto this image at (x, y) without blending (clipped).</summary>
    public void Blit(BgraImage src, int x, int y)
    {
        var dstRect = new RectI(x, y, src.Width, src.Height).Intersect(Bounds);
        if (dstRect.IsEmpty) return;
        var rowBytes = dstRect.Width * 4;
        for (var row = 0; row < dstRect.Height; row++)
        {
            var sy = dstRect.Y - y + row;
            var sx = dstRect.X - x;
            Buffer.BlockCopy(src.Pixels, (sy * src.Width + sx) * 4, Pixels, ((dstRect.Y + row) * Width + dstRect.X) * 4, rowBytes);
        }
    }

    /// <summary>Source-over composites premultiplied <paramref name="src"/> onto this image at (x, y) (clipped).</summary>
    public void DrawOver(BgraImage src, int x, int y, double opacity = 1.0)
    {
        var dstRect = new RectI(x, y, src.Width, src.Height).Intersect(Bounds);
        if (dstRect.IsEmpty) return;
        var op = Math.Clamp(opacity, 0, 1);
        for (var row = 0; row < dstRect.Height; row++)
        {
            var sy = dstRect.Y - y + row;
            var si = (sy * src.Width + (dstRect.X - x)) * 4;
            var di = ((dstRect.Y + row) * Width + dstRect.X) * 4;
            for (var col = 0; col < dstRect.Width; col++, si += 4, di += 4)
            {
                double sa = src.Pixels[si + 3] * op / 255.0;
                if (sa <= 0) continue;
                var inv = 1 - sa;
                Pixels[di] = (byte)Math.Min(255, src.Pixels[si] * op + Pixels[di] * inv + 0.5);
                Pixels[di + 1] = (byte)Math.Min(255, src.Pixels[si + 1] * op + Pixels[di + 1] * inv + 0.5);
                Pixels[di + 2] = (byte)Math.Min(255, src.Pixels[si + 2] * op + Pixels[di + 2] * inv + 0.5);
                Pixels[di + 3] = (byte)Math.Min(255, src.Pixels[si + 3] * op + Pixels[di + 3] * inv + 0.5);
            }
        }
    }

    public static (byte B, byte G, byte R, byte A) Premultiply(Argb c)
    {
        if (c.A == 255) return (c.B, c.G, c.R, 255);
        if (c.A == 0) return (0, 0, 0, 0);
        return ((byte)((c.B * c.A + 127) / 255), (byte)((c.G * c.A + 127) / 255), (byte)((c.R * c.A + 127) / 255), c.A);
    }

    public static Argb Unpremultiply(byte b, byte g, byte r, byte a)
    {
        if (a == 255) return Argb.FromArgb(255, r, g, b);
        if (a == 0) return Argb.Transparent;
        return Argb.FromArgb(a, (byte)Math.Min(255, (r * 255 + a / 2) / a), (byte)Math.Min(255, (g * 255 + a / 2) / a), (byte)Math.Min(255, (b * 255 + a / 2) / a));
    }

    /// <summary>True when every pixel is fully opaque.</summary>
    public bool IsOpaque()
    {
        var px = Pixels;
        for (var i = 3; i < px.Length; i += 4) if (px[i] != 255) return false;
        return true;
    }
}
