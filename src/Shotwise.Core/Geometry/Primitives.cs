using System.Globalization;
using System.Text.Json.Serialization;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Geometry;

/// <summary>A point in canvas pixels (origin top-left, +Y down).</summary>
[JsonConverter(typeof(Point2JsonConverter))]
public readonly record struct Point2(double X, double Y)
{
    public static Point2 Zero => default;

    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);
    public static Point2 operator /(Point2 a, double s) => new(a.X / s, a.Y / s);

    public Point2 Offset(double dx, double dy) => new(X + dx, Y + dy);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Point2 other) => (this - other).Length;
    public double DistanceSquaredTo(Point2 other) { var dx = X - other.X; var dy = Y - other.Y; return dx * dx + dy * dy; }
    public PointI ToPointI() => new((int)Math.Round(X), (int)Math.Round(Y));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y}");
}

/// <summary>A size in canvas pixels.</summary>
public readonly record struct Size2(double Width, double Height)
{
    public static Size2 Empty => default;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public SizeI ToSizeI() => new((int)Math.Round(Width), (int)Math.Round(Height));
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
}

/// <summary>An axis-aligned rectangle in canvas pixels. Width/Height may be negative until <see cref="Normalize"/> is called.</summary>
[JsonConverter(typeof(Rect2JsonConverter))]
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public static Rect2 Empty => default;

    public Rect2(Point2 location, Size2 size) : this(location.X, location.Y, size.Width, size.Height) { }

    [JsonIgnore] public double Left => X;
    [JsonIgnore] public double Top => Y;
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    [JsonIgnore] public Point2 TopLeft => new(X, Y);
    [JsonIgnore] public Point2 TopRight => new(Right, Y);
    [JsonIgnore] public Point2 BottomLeft => new(X, Bottom);
    [JsonIgnore] public Point2 BottomRight => new(Right, Bottom);
    [JsonIgnore] public Point2 Center => new(X + Width / 2, Y + Height / 2);
    [JsonIgnore] public Size2 Size => new(Width, Height);
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Rectangle spanning two arbitrary corner points (always normalized).</summary>
    public static Rect2 FromPoints(Point2 a, Point2 b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Bounding rectangle of a point set; <see cref="Empty"/> when the set is empty.</summary>
    public static Rect2 FromPoints(IEnumerable<Point2> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var any = false;
        foreach (var p in points)
        {
            any = true;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return any ? new Rect2(minX, minY, maxX - minX, maxY - minY) : Empty;
    }

    public static Rect2 FromLTRB(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);

    /// <summary>Returns a rectangle with non-negative width and height.</summary>
    public Rect2 Normalize()
    {
        var x = Width < 0 ? X + Width : X;
        var y = Height < 0 ? Y + Height : Y;
        return new Rect2(x, y, Math.Abs(Width), Math.Abs(Height));
    }

    public bool Contains(Point2 p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool Contains(Rect2 r) => r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;
    public bool IntersectsWith(Rect2 r) => r.X <= Right && r.Right >= X && r.Y <= Bottom && r.Bottom >= Y;

    public Rect2 Union(Rect2 r)
    {
        if (Width == 0 && Height == 0 && X == 0 && Y == 0) return r;
        if (r.Width == 0 && r.Height == 0 && r.X == 0 && r.Y == 0) return this;
        return FromLTRB(Math.Min(X, r.X), Math.Min(Y, r.Y), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public Rect2 Union(Point2 p) => FromLTRB(Math.Min(X, p.X), Math.Min(Y, p.Y), Math.Max(Right, p.X), Math.Max(Bottom, p.Y));

    /// <summary>Intersection; <see cref="Empty"/> when the rectangles do not overlap.</summary>
    public Rect2 Intersect(Rect2 r)
    {
        var l = Math.Max(X, r.X);
        var t = Math.Max(Y, r.Y);
        var rr = Math.Min(Right, r.Right);
        var b = Math.Min(Bottom, r.Bottom);
        return rr < l || b < t ? Empty : FromLTRB(l, t, rr, b);
    }

    public Rect2 Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
    public Rect2 Inflate(double d) => Inflate(d, d);
    public Rect2 Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
    public Rect2 Offset(Point2 d) => Offset(d.X, d.Y);
    public Rect2 Scale(double s) => new(X * s, Y * s, Width * s, Height * s);

    /// <summary>Smallest integer rectangle containing this rectangle.</summary>
    public RectI ToRectIOutward()
    {
        var n = Normalize();
        var l = (int)Math.Floor(n.X);
        var t = (int)Math.Floor(n.Y);
        var r = (int)Math.Ceiling(n.Right);
        var b = (int)Math.Ceiling(n.Bottom);
        return new RectI(l, t, r - l, b - t);
    }

    public RectI ToRectIRounded() => new((int)Math.Round(X), (int)Math.Round(Y), (int)Math.Round(Width), (int)Math.Round(Height));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");
}

/// <summary>Integer point (device or image pixels).</summary>
public readonly record struct PointI(int X, int Y)
{
    public Point2 ToPoint2() => new(X, Y);
    public PointI Offset(int dx, int dy) => new(X + dx, Y + dy);
}

/// <summary>Integer size (device or image pixels).</summary>
public readonly record struct SizeI(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Integer rectangle (device or image pixels).</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public static RectI Empty => default;
    [JsonIgnore] public int Left => X;
    [JsonIgnore] public int Top => Y;
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    [JsonIgnore] public SizeI Size => new(Width, Height);
    [JsonIgnore] public PointI Location => new(X, Y);

    public static RectI FromLTRB(int l, int t, int r, int b) => new(l, t, r - l, b - t);
    public bool Contains(PointI p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public bool Contains(RectI r) => r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;
    public bool IntersectsWith(RectI r) => r.X < Right && r.Right > X && r.Y < Bottom && r.Bottom > Y;

    public RectI Intersect(RectI r)
    {
        var l = Math.Max(X, r.X);
        var t = Math.Max(Y, r.Y);
        var rr = Math.Min(Right, r.Right);
        var b = Math.Min(Bottom, r.Bottom);
        return rr <= l || b <= t ? Empty : FromLTRB(l, t, rr, b);
    }

    public RectI Union(RectI r)
    {
        if (IsEmpty) return r;
        if (r.IsEmpty) return this;
        return FromLTRB(Math.Min(X, r.X), Math.Min(Y, r.Y), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public RectI Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
    public Rect2 ToRect2() => new(X, Y, Width, Height);
}

/// <summary>Edge insets (padding/margins) in pixels. Named with a "2" suffix to avoid clashing with System.Windows.Thickness.</summary>
public readonly record struct Thickness2(double Left, double Top, double Right, double Bottom)
{
    public Thickness2(double uniform) : this(uniform, uniform, uniform, uniform) { }
    public static Thickness2 Zero => default;
    [JsonIgnore] public double Horizontal => Left + Right;
    [JsonIgnore] public double Vertical => Top + Bottom;
}

/// <summary>Nine-position anchor used by resize canvas, watermark, caption, print layout.</summary>
public enum Anchor9 { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Corner of a rectangle (page curl, webcam PiP placement).</summary>
public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public static class Anchor9Extensions
{
    /// <summary>Horizontal factor 0 (left), 0.5 (center) or 1 (right).</summary>
    public static double HorizontalFactor(this Anchor9 a) => a switch
    {
        Anchor9.TopLeft or Anchor9.MiddleLeft or Anchor9.BottomLeft => 0,
        Anchor9.TopCenter or Anchor9.Center or Anchor9.BottomCenter => 0.5,
        _ => 1,
    };

    /// <summary>Vertical factor 0 (top), 0.5 (middle) or 1 (bottom).</summary>
    public static double VerticalFactor(this Anchor9 a) => a switch
    {
        Anchor9.TopLeft or Anchor9.TopCenter or Anchor9.TopRight => 0,
        Anchor9.MiddleLeft or Anchor9.Center or Anchor9.MiddleRight => 0.5,
        _ => 1,
    };

    /// <summary>Offset at which content of <paramref name="content"/> size is placed inside <paramref name="container"/> for this anchor.</summary>
    public static Point2 PlacementOffset(this Anchor9 a, Size2 content, Size2 container)
        => new((container.Width - content.Width) * a.HorizontalFactor(), (container.Height - content.Height) * a.VerticalFactor());
}
