using Shotwise.Core.Geometry;

namespace Shotwise.Core.Imaging;

/// <summary>8-bit coverage mask (0 = outside, 255 = inside; intermediate values allowed for feathering).</summary>
public sealed class Mask
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public Mask(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Data = new byte[checked(width * height)];
    }

    public Mask(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < (long)width * height) throw new ArgumentException("Mask data too small.", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public byte this[int x, int y]
    {
        get => (uint)x < (uint)Width && (uint)y < (uint)Height ? Data[y * Width + x] : (byte)0;
        set { if ((uint)x < (uint)Width && (uint)y < (uint)Height) Data[y * Width + x] = value; }
    }

    public bool Contains(int x, int y) => this[x, y] >= 128;

    public bool IsEmpty
    {
        get { foreach (var b in Data) if (b != 0) return false; return true; }
    }

    public int Count
    {
        get { var n = 0; foreach (var b in Data) if (b >= 128) n++; return n; }
    }

    /// <summary>Tight integer bounds of non-zero coverage; empty when the mask is empty.</summary>
    public RectI PixelBounds
    {
        get
        {
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            for (var y = 0; y < Height; y++)
            {
                var row = y * Width;
                for (var x = 0; x < Width; x++)
                {
                    if (Data[row + x] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX < 0 ? RectI.Empty : RectI.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
    }

    /// <summary>Bounds of non-zero coverage in canvas pixel units.</summary>
    public Rect2 Bounds => PixelBounds.ToRect2();

    public Mask Clone() => new(Width, Height, (byte[])Data.Clone());

    public static Mask FromRects(int width, int height, IEnumerable<RectI> rects)
    {
        var m = new Mask(width, height);
        foreach (var r in rects)
        {
            var c = r.Intersect(new RectI(0, 0, width, height));
            if (c.IsEmpty) continue;
            for (var y = c.Y; y < c.Bottom; y++) Array.Fill(m.Data, (byte)255, y * width + c.X, c.Width);
        }
        return m;
    }

    public static Mask FromRect(int width, int height, RectI rect) => FromRects(width, height, new[] { rect });

    /// <summary>Scanline polygon fill (even-odd, pixel centres).</summary>
    public static Mask FromPolygon(int width, int height, IReadOnlyList<Point2> polygon)
    {
        var m = new Mask(width, height);
        if (polygon.Count < 3) return m;
        var xs = new List<double>();
        var bounds = Rect2.FromPoints(polygon).ToRectIOutward().Intersect(new RectI(0, 0, width, height));
        if (bounds.IsEmpty) return m;
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        {
            var sy = y + 0.5;
            xs.Clear();
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                    xs.Add(a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                var x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
                var x1 = Math.Min(width - 1, (int)Math.Floor(xs[k + 1] - 0.5));
                if (x1 >= x0) Array.Fill(m.Data, (byte)255, y * width + x0, x1 - x0 + 1);
            }
        }
        return m;
    }

    public static Mask FromPolygon(int width, int height, IReadOnlyList<PointI> polygon)
        => FromPolygon(width, height, polygon.Select(p => p.ToPoint2()).ToArray());

    public static Mask FromEllipse(int width, int height, Rect2 bounds)
    {
        var m = new Mask(width, height);
        var r = bounds.Normalize();
        var c = r.ToRectIOutward().Intersect(new RectI(0, 0, width, height));
        for (var y = c.Y; y < c.Bottom; y++)
            for (var x = c.X; x < c.Right; x++)
                if (GeometryMath.PointInEllipse(new Point2(x + 0.5, y + 0.5), r)) m.Data[y * width + x] = 255;
        return m;
    }

    public Mask Invert()
    {
        var m = new Mask(Width, Height);
        for (var i = 0; i < Data.Length; i++) m.Data[i] = (byte)(255 - Data[i]);
        return m;
    }

    public Mask Union(Mask other) => Combine(other, (a, b) => Math.Max(a, b));
    public Mask Intersect(Mask other) => Combine(other, (a, b) => Math.Min(a, b));
    public Mask Subtract(Mask other) => Combine(other, (a, b) => (byte)Math.Max(0, a - b));

    private Mask Combine(Mask other, Func<byte, byte, byte> op)
    {
        if (other.Width != Width || other.Height != Height) throw new ArgumentException("Mask sizes differ.", nameof(other));
        var m = new Mask(Width, Height);
        for (var i = 0; i < Data.Length; i++) m.Data[i] = op(Data[i], other.Data[i]);
        return m;
    }

    /// <summary>Crops to <paramref name="r"/> (clipped to mask bounds).</summary>
    public Mask Crop(RectI r)
    {
        var c = r.Intersect(new RectI(0, 0, Width, Height));
        if (c.IsEmpty) throw new ArgumentOutOfRangeException(nameof(r));
        var m = new Mask(c.Width, c.Height);
        for (var y = 0; y < c.Height; y++) Buffer.BlockCopy(Data, (c.Y + y) * Width + c.X, m.Data, y * c.Width, c.Width);
        return m;
    }

    /// <summary>Square-kernel binary dilation (radius &gt; 0) or erosion (radius &lt; 0).</summary>
    public Mask Morph(int radius)
    {
        if (radius == 0) return Clone();
        var dilate = radius > 0;
        var r = Math.Abs(radius);
        var tmp = new byte[Data.Length];
        var outp = new Mask(Width, Height);
        // separable: horizontal then vertical
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                byte v = dilate ? (byte)0 : (byte)255;
                for (var k = -r; k <= r; k++)
                {
                    var xx = Math.Clamp(x + k, 0, Width - 1);
                    var s = Data[y * Width + xx];
                    v = dilate ? Math.Max(v, s) : Math.Min(v, s);
                }
                tmp[y * Width + x] = v;
            }
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                byte v = dilate ? (byte)0 : (byte)255;
                for (var k = -r; k <= r; k++)
                {
                    var yy = Math.Clamp(y + k, 0, Height - 1);
                    var s = tmp[yy * Width + x];
                    v = dilate ? Math.Max(v, s) : Math.Min(v, s);
                }
                outp.Data[y * Width + x] = v;
            }
        return outp;
    }

    /// <summary>Applies the mask to an image's alpha (premultiplied): outside pixels become transparent.</summary>
    public BgraImage ApplyTo(BgraImage img)
    {
        if (img.Width != Width || img.Height != Height) throw new ArgumentException("Mask and image sizes differ.", nameof(img));
        var outImg = img.Clone();
        var px = outImg.Pixels;
        for (var i = 0; i < Data.Length; i++)
        {
            var m = Data[i];
            if (m == 255) continue;
            var o = i * 4;
            px[o] = (byte)(px[o] * m / 255);
            px[o + 1] = (byte)(px[o + 1] * m / 255);
            px[o + 2] = (byte)(px[o + 2] * m / 255);
            px[o + 3] = (byte)(px[o + 3] * m / 255);
        }
        return outImg;
    }
}
