using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Capture;

internal static class CaptureMath
{
    /// <summary>Copies 32-bit BGRX pixels and forces alpha to 255 (vectorized; GDI leaves the X byte undefined).</summary>
    internal static void CopyOpaque(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length % 4 != 0 || destination.Length < source.Length) throw new ArgumentException("Pixel buffers must hold whole BGRA pixels.", nameof(source));
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source);
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination)[..src.Length];
        uint alpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;
        int i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && src.Length >= System.Numerics.Vector<uint>.Count)
        {
            var mask = new System.Numerics.Vector<uint>(alpha);
            int n = System.Numerics.Vector<uint>.Count;
            for (; i <= src.Length - n; i += n) (new System.Numerics.Vector<uint>(src[i..]) | mask).CopyTo(dst[i..]);
        }
        for (; i < src.Length; i++) dst[i] = src[i] | alpha;
    }

    internal static PointI CursorOrigin(PointI position, PointI hotspot, RectI capture) => new(position.X - hotspot.X - capture.X, position.Y - hotspot.Y - capture.Y);
    internal static RectI CenteredRegion(PointI point, int size, RectI bounds)
    {
        if (size <= 0 || bounds.IsEmpty) throw new ArgumentOutOfRangeException(nameof(size));
        int w = Math.Min(size, bounds.Width), h = Math.Min(size, bounds.Height);
        return new((int)Math.Clamp((long)point.X - w / 2, bounds.Left, (long)bounds.Right - w), (int)Math.Clamp((long)point.Y - h / 2, bounds.Top, (long)bounds.Bottom - h), w, h);
    }
    internal static BgraImage PolygonMask(BgraImage image, IReadOnlyList<PointI> polygon)
    {
        if (polygon.Count < 3) throw new ArgumentException("A polygon needs at least three points.", nameof(polygon));
        var result = image.Clone();
        // Even-odd scan conversion with 4x4 subpixel coverage; scales premultiplied colour and alpha together.
        var crossings = new List<double>(polygon.Count);
        var coverage = new int[image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            Array.Clear(coverage);
            for (int sy = 0; sy < 4; sy++)
            {
                double scan = y + (sy + .5) / 4;
                crossings.Clear();
                for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                {
                    var a = polygon[j]; var b = polygon[i];
                    if ((a.Y > scan) != (b.Y > scan)) crossings.Add(a.X + (scan - a.Y) * ((double)b.X - a.X) / ((double)b.Y - a.Y));
                }
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    int first = (int)Math.Clamp(Math.Ceiling(crossings[i] * 4 - .5), 0, image.Width * 4d);
                    int last = (int)Math.Clamp(Math.Ceiling(crossings[i + 1] * 4 - .5), 0, image.Width * 4d);
                    for (int sx = first; sx < last; sx++) coverage[sx / 4]++;
                }
            }
            for (int x = 0; x < image.Width; x++) for (int c = 0; c < 4; c++)
            { int p = y * image.Stride + x * 4 + c; result.Pixels[p] = (byte)((image.Pixels[p] * coverage[x] + 8) / 16); }
        }
        return result;
    }
}
