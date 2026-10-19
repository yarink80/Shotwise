namespace Shotwise.Core.Geometry;

/// <summary>Pure geometry helpers shared by the editor, renderer and vision code.</summary>
public static class GeometryMath
{
    public const double Epsilon = 1e-9;

    public static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
    public static double RadiansToDegrees(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Normalizes an angle to [0, 360).</summary>
    public static double NormalizeDegrees(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>Angle of the vector a→b in degrees (0 = +X, clockwise positive because +Y is down).</summary>
    public static double AngleDegrees(Point2 a, Point2 b) => RadiansToDegrees(Math.Atan2(b.Y - a.Y, b.X - a.X));

    public static Point2 Lerp(Point2 a, Point2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    /// <summary>Rotates <paramref name="p"/> around <paramref name="center"/> by <paramref name="degrees"/> (clockwise on screen).</summary>
    public static Point2 Rotate(Point2 p, Point2 center, double degrees)
    {
        if (degrees == 0) return p;
        var r = DegreesToRadians(degrees);
        var cos = Math.Cos(r);
        var sin = Math.Sin(r);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return new Point2(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    /// <summary>Axis-aligned bounds of <paramref name="r"/> rotated around its center.</summary>
    public static Rect2 RotatedBounds(Rect2 r, double degrees)
    {
        if (degrees == 0) return r;
        var c = r.Center;
        return Rect2.FromPoints(new[] { Rotate(r.TopLeft, c, degrees), Rotate(r.TopRight, c, degrees), Rotate(r.BottomRight, c, degrees), Rotate(r.BottomLeft, c, degrees) });
    }

    public static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < Epsilon) return p.DistanceTo(a);
        var t = Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return p.DistanceTo(new Point2(a.X + t * dx, a.Y + t * dy));
    }

    public static Point2 ClosestPointOnSegment(Point2 p, Point2 a, Point2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < Epsilon) return a;
        var t = Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return new Point2(a.X + t * dx, a.Y + t * dy);
    }

    public static double DistanceToPolyline(Point2 p, IReadOnlyList<Point2> pts, bool closed)
    {
        if (pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return p.DistanceTo(pts[0]);
        var best = double.PositiveInfinity;
        var n = pts.Count;
        var edges = closed ? n : n - 1;
        for (var i = 0; i < edges; i++)
        {
            var d = DistanceToSegment(p, pts[i], pts[(i + 1) % n]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool PointInPolygon(Point2 p, IReadOnlyList<Point2> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + double.Epsilon) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static bool PointInEllipse(Point2 p, Rect2 bounds)
    {
        var r = bounds.Normalize();
        if (r.Width <= 0 || r.Height <= 0) return false;
        var rx = r.Width / 2;
        var ry = r.Height / 2;
        var dx = (p.X - (r.X + rx)) / rx;
        var dy = (p.Y - (r.Y + ry)) / ry;
        return dx * dx + dy * dy <= 1.0;
    }

    public static bool SegmentsIntersect(Point2 p1, Point2 p2, Point2 p3, Point2 p4)
    {
        static double Cross(Point2 o, Point2 a, Point2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        static bool OnSeg(Point2 a, Point2 b, Point2 c) => Math.Min(a.X, b.X) <= c.X + Epsilon && c.X <= Math.Max(a.X, b.X) + Epsilon && Math.Min(a.Y, b.Y) <= c.Y + Epsilon && c.Y <= Math.Max(a.Y, b.Y) + Epsilon;
        var d1 = Cross(p3, p4, p1);
        var d2 = Cross(p3, p4, p2);
        var d3 = Cross(p1, p2, p3);
        var d4 = Cross(p1, p2, p4);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        if (Math.Abs(d1) < Epsilon && OnSeg(p3, p4, p1)) return true;
        if (Math.Abs(d2) < Epsilon && OnSeg(p3, p4, p2)) return true;
        if (Math.Abs(d3) < Epsilon && OnSeg(p1, p2, p3)) return true;
        if (Math.Abs(d4) < Epsilon && OnSeg(p1, p2, p4)) return true;
        return false;
    }

    public static bool SegmentIntersectsRect(Point2 a, Point2 b, Rect2 r)
    {
        if (r.Contains(a) || r.Contains(b)) return true;
        return SegmentsIntersect(a, b, r.TopLeft, r.TopRight) || SegmentsIntersect(a, b, r.TopRight, r.BottomRight)
            || SegmentsIntersect(a, b, r.BottomRight, r.BottomLeft) || SegmentsIntersect(a, b, r.BottomLeft, r.TopLeft);
    }

    /// <summary>Point on a cubic Bezier at parameter t.</summary>
    public static Point2 CubicBezier(Point2 p0, Point2 c1, Point2 c2, Point2 p3, double t)
    {
        var u = 1 - t;
        var b0 = u * u * u;
        var b1 = 3 * u * u * t;
        var b2 = 3 * u * t * t;
        var b3 = t * t * t;
        return new Point2(b0 * p0.X + b1 * c1.X + b2 * c2.X + b3 * p3.X, b0 * p0.Y + b1 * c1.Y + b2 * c2.Y + b3 * p3.Y);
    }

    /// <summary>Point on a quadratic Bezier at parameter t.</summary>
    public static Point2 QuadraticBezier(Point2 p0, Point2 c, Point2 p2, double t)
    {
        var u = 1 - t;
        return new Point2(u * u * p0.X + 2 * u * t * c.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * c.Y + t * t * p2.Y);
    }

    /// <summary>Flattens a cubic Bezier into <paramref name="segments"/>+1 points.</summary>
    public static IReadOnlyList<Point2> FlattenCubic(Point2 p0, Point2 c1, Point2 c2, Point2 p3, int segments = 24)
    {
        segments = Math.Max(1, segments);
        var pts = new Point2[segments + 1];
        for (var i = 0; i <= segments; i++) pts[i] = CubicBezier(p0, c1, c2, p3, (double)i / segments);
        return pts;
    }

    /// <summary>Snaps the end point so that the a→b direction is a multiple of <paramref name="stepDegrees"/> (Shift-constrain).</summary>
    public static Point2 SnapAngle(Point2 a, Point2 b, double stepDegrees = 45)
    {
        var len = a.DistanceTo(b);
        if (len < Epsilon) return b;
        var ang = Math.Round(AngleDegrees(a, b) / stepDegrees) * stepDegrees;
        var r = DegreesToRadians(ang);
        return new Point2(a.X + Math.Cos(r) * len, a.Y + Math.Sin(r) * len);
    }

    /// <summary>Constrains a drag rectangle from <paramref name="anchor"/> to a square (Shift-constrain for shapes).</summary>
    public static Point2 ConstrainSquare(Point2 anchor, Point2 p)
    {
        var dx = p.X - anchor.X;
        var dy = p.Y - anchor.Y;
        var s = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point2(anchor.X + Math.Sign(dx == 0 ? 1 : dx) * s, anchor.Y + Math.Sign(dy == 0 ? 1 : dy) * s);
    }

    /// <summary>Constrains a drag rectangle to an aspect ratio (width/height).</summary>
    public static Point2 ConstrainAspect(Point2 anchor, Point2 p, double aspect)
    {
        if (aspect <= 0) return p;
        var dx = p.X - anchor.X;
        var dy = p.Y - anchor.Y;
        var w = Math.Abs(dx);
        var h = Math.Abs(dy);
        if (w / Math.Max(h, Epsilon) > aspect) w = h * aspect; else h = w / aspect;
        return new Point2(anchor.X + Math.Sign(dx == 0 ? 1 : dx) * w, anchor.Y + Math.Sign(dy == 0 ? 1 : dy) * h);
    }

    public static double SnapToGrid(double v, double grid) => grid <= 0 ? v : Math.Round(v / grid) * grid;

    /// <summary>Ramer–Douglas–Peucker polyline simplification (used for pen smoothing).</summary>
    public static IReadOnlyList<Point2> Simplify(IReadOnlyList<Point2> pts, double tolerance)
    {
        if (pts.Count < 3 || tolerance <= 0) return pts.ToArray();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double maxD = 0;
            var idx = -1;
            for (var i = s + 1; i < e; i++)
            {
                var d = DistanceToSegment(pts[i], pts[s], pts[e]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tolerance)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        var result = new List<Point2>();
        for (var i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    /// <summary>Polygon area (absolute) via the shoelace formula.</summary>
    public static double PolygonArea(IReadOnlyList<Point2> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return Math.Abs(a / 2);
    }
}
