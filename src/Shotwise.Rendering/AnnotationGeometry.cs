using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>A cap shape: either a filled path (drawn with the stroke colour) or a stroked polyline.</summary>
internal sealed record CapShape(SKPath Path, bool Filled);

/// <summary>Centre line (already trimmed so caps sit cleanly on it) plus cap shapes for arrows/lines.</summary>
internal sealed class LinearGeometry : IDisposable
{
    public required SKPath Line { get; init; }
    public required SKPath FullLine { get; init; }
    public List<CapShape> Caps { get; } = new();

    public void Dispose()
    {
        Line.Dispose();
        FullLine.Dispose();
        foreach (var c in Caps) c.Path.Dispose();
    }
}

/// <summary>Pure geometry builders shared by painting, bounds measurement and hit-test outlines (local, unrotated canvas coordinates).</summary>
internal static class AnnotationGeometry
{
    public static float CapLength(double strokeWidth, double scale) => (float)(Math.Max(12, strokeWidth * 4.5) * Math.Max(0.1, scale));

    public static LinearGeometry BuildLinear(Annotation a)
    {
        ArrowCap sc, ec;
        double ss, es;
        bool bez;
        Point2? c1n, c2n;
        switch (a)
        {
            case ArrowAnnotation ar:
                sc = ar.StartCap; ec = ar.EndCap; ss = ar.StartCapScale; es = ar.EndCapScale; bez = ar.Bezier; c1n = ar.Control1; c2n = ar.Control2; break;
            case LineAnnotation ln:
                sc = ln.StartCap; ec = ln.EndCap; ss = 1; es = 1; bez = ln.Bezier; c1n = ln.Control1; c2n = ln.Control2; break;
            default:
                throw new ArgumentException("Not a linear annotation", nameof(a));
        }
        var s = a.Start.ToSk();
        var e = a.End.ToSk();
        var full = new SKPath();
        full.MoveTo(s);
        SKPoint c1 = s, c2 = e;
        if (bez)
        {
            c1 = (c1n ?? GeometryMath.Lerp(a.Start, a.End, 1.0 / 3)).ToSk();
            c2 = (c2n ?? GeometryMath.Lerp(a.Start, a.End, 2.0 / 3)).ToSk();
            full.CubicTo(c1, c2, e);
        }
        else full.LineTo(e);

        var w = Math.Max(1, a.Stroke.Width);
        // Outward directions at both ends.
        var startDir = Dir(s, bez ? FirstDistinct(s, c1, c2, e) : e);
        var endDir = Dir(e, bez ? FirstDistinct(e, c2, c1, s) : s);

        var lenS = CapLength(w, ss);
        var lenE = CapLength(w, es);
        var trimS = Trim(sc, lenS);
        var trimE = Trim(ec, lenE);

        using var meas = new SKPathMeasure(full, false);
        var total = meas.Length;
        var line = new SKPath();
        if (total <= 0.01) line.MoveTo(s);
        else
        {
            var t0 = Math.Min(trimS, total * 0.45f);
            var t1 = total - Math.Min(trimE, total * 0.45f);
            meas.GetSegment(t0, t1, line, true);
        }
        var res = new LinearGeometry { Line = line, FullLine = full };
        AddCap(res, sc, s, startDir, lenS, w);
        AddCap(res, ec, e, endDir, lenE, w);
        return res;
    }

    private static SKPoint FirstDistinct(SKPoint origin, SKPoint a, SKPoint b, SKPoint c)
    {
        foreach (var p in new[] { a, b, c }) if (Math.Abs(p.X - origin.X) + Math.Abs(p.Y - origin.Y) > 0.01) return p;
        return c;
    }

    /// <summary>Unit vector pointing away from the line, i.e. from <paramref name="toward"/> to <paramref name="at"/>.</summary>
    private static SKPoint Dir(SKPoint at, SKPoint toward)
    {
        var dx = at.X - toward.X;
        var dy = at.Y - toward.Y;
        var l = MathF.Sqrt(dx * dx + dy * dy);
        return l < 1e-4f ? new SKPoint(1, 0) : new SKPoint(dx / l, dy / l);
    }

    private static float Trim(ArrowCap cap, float len) => cap switch
    {
        ArrowCap.Arrow => len * 0.7f,
        ArrowCap.Triangle => len * 0.9f,
        _ => 0f,
    };

    private static void AddCap(LinearGeometry g, ArrowCap cap, SKPoint p, SKPoint u, float len, double w)
    {
        if (cap == ArrowCap.None) return;
        var n = new SKPoint(-u.Y, u.X);
        SKPoint At(float along, float across) => new(p.X + u.X * along + n.X * across, p.Y + u.Y * along + n.Y * across);
        var path = new SKPath();
        switch (cap)
        {
            case ArrowCap.Arrow:
                path.MoveTo(p);
                path.LineTo(At(-len, len * 0.42f));
                path.LineTo(At(-len * 0.7f, 0));
                path.LineTo(At(-len, -len * 0.42f));
                path.Close();
                g.Caps.Add(new CapShape(path, true));
                break;
            case ArrowCap.OpenArrow:
            {
                var l = len * 0.8f;
                path.MoveTo(At(-l, l * 0.5f));
                path.LineTo(p);
                path.LineTo(At(-l, -l * 0.5f));
                g.Caps.Add(new CapShape(path, false));
                break;
            }
            case ArrowCap.Triangle:
                path.MoveTo(p);
                path.LineTo(At(-len, len * 0.55f));
                path.LineTo(At(-len, -len * 0.55f));
                path.Close();
                g.Caps.Add(new CapShape(path, true));
                break;
            case ArrowCap.Round:
                path.AddCircle(p.X, p.Y, Math.Max(len * 0.35f, (float)w));
                g.Caps.Add(new CapShape(path, true));
                break;
            case ArrowCap.Square:
            {
                var h = Math.Max(len * 0.35f, (float)w);
                path.MoveTo(At(-h, h));
                path.LineTo(At(h, h));
                path.LineTo(At(h, -h));
                path.LineTo(At(-h, -h));
                path.Close();
                g.Caps.Add(new CapShape(path, true));
                break;
            }
            case ArrowCap.Diamond:
            {
                var h = Math.Max(len * 0.5f, (float)w * 1.2f);
                path.MoveTo(At(h, 0));
                path.LineTo(At(0, h * 0.7f));
                path.LineTo(At(-h, 0));
                path.LineTo(At(0, -h * 0.7f));
                path.Close();
                g.Caps.Add(new CapShape(path, true));
                break;
            }
            case ArrowCap.TBar:
                path.MoveTo(At(0, len * 0.6f));
                path.LineTo(At(0, -len * 0.6f));
                g.Caps.Add(new CapShape(path, false));
                break;
            default:
                path.Dispose();
                break;
        }
    }

    public static SKPath ShapePath(ShapeKind kind, Rect2 r, double cornerRadius, IReadOnlyList<Point2>? points = null)
    {
        var p = new SKPath();
        var rect = r.ToSk();
        switch (kind)
        {
            case ShapeKind.Ellipse: p.AddOval(rect); break;
            case ShapeKind.RoundedRectangle:
            {
                var rad = (float)Math.Clamp(cornerRadius, 0, Math.Min(r.Width, r.Height) / 2);
                p.AddRoundRect(rect, rad, rad);
                break;
            }
            case ShapeKind.Polygon when points is { Count: > 1 }:
                p.MoveTo(points[0].ToSk());
                for (var i = 1; i < points.Count; i++) p.LineTo(points[i].ToSk());
                p.Close();
                break;
            default: p.AddRect(rect); break;
        }
        return p;
    }

    /// <summary>Samples the pen stroke into a dense polyline (Catmull-Rom smoothing when enabled).</summary>
    public static List<SKPoint> PenSamples(IReadOnlyList<Point2> pts, bool smoothing)
    {
        var res = new List<SKPoint>();
        // drop consecutive duplicates
        var src = new List<Point2>(pts.Count);
        foreach (var p in pts) if (src.Count == 0 || src[^1].DistanceSquaredTo(p) > 1e-6) src.Add(p);
        if (src.Count == 0) return res;
        if (src.Count < 3 || !smoothing)
        {
            foreach (var p in src) res.Add(p.ToSk());
            return res;
        }
        res.Add(src[0].ToSk());
        for (var i = 0; i < src.Count - 1; i++)
        {
            var p0 = src[Math.Max(0, i - 1)];
            var p1 = src[i];
            var p2 = src[i + 1];
            var p3 = src[Math.Min(src.Count - 1, i + 2)];
            var seg = Math.Max(2, Math.Min(12, (int)(p1.DistanceTo(p2) / 2)));
            for (var k = 1; k <= seg; k++)
            {
                var t = (double)k / seg;
                var t2 = t * t;
                var t3 = t2 * t;
                var x = 0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                var y = 0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                res.Add(new SKPoint((float)x, (float)y));
            }
        }
        return res;
    }

    /// <summary>Nib half-vector for chisel nibs (null for round/square nibs).</summary>
    public static SKPoint? ChiselVector(PenNib nib, double width)
    {
        var h = (float)width / 2;
        return nib switch
        {
            PenNib.Horizontal => new SKPoint(h, 0),
            PenNib.Vertical => new SKPoint(0, h),
            PenNib.Diagonal => new SKPoint(h * 0.7071f, -h * 0.7071f),
            _ => null,
        };
    }

    /// <summary>Union geometry of a pen stroke for non-round nibs (convex hulls swept along the samples).</summary>
    public static SKPath PenNibPath(IReadOnlyList<SKPoint> samples, PenNib nib, double width)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        if (samples.Count == 0) return path;
        var vec = ChiselVector(nib, width);
        var h = (float)width / 2;
        // chisel nibs are thin rectangles: the nib line plus a small thickness perpendicular to it
        var thick = Math.Max(2f, (float)width * 0.22f) / 2;
        SKPoint[] Nib(SKPoint c)
        {
            if (vec is not { } v)
                return new[] { new SKPoint(c.X - h, c.Y - h), new SKPoint(c.X + h, c.Y - h), new SKPoint(c.X + h, c.Y + h), new SKPoint(c.X - h, c.Y + h) };
            var len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            var px = len < 1e-4f ? 0 : -v.Y / len * thick;
            var py = len < 1e-4f ? thick : v.X / len * thick;
            return new[]
            {
                new SKPoint(c.X - v.X - px, c.Y - v.Y - py), new SKPoint(c.X + v.X - px, c.Y + v.Y - py),
                new SKPoint(c.X + v.X + px, c.Y + v.Y + py), new SKPoint(c.X - v.X + px, c.Y - v.Y + py),
            };
        }
        if (samples.Count == 1)
        {
            AddHull(path, Nib(samples[0]));
            return path;
        }
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var pts = Nib(samples[i]).Concat(Nib(samples[i + 1])).ToList();
            AddHull(path, pts);
        }
        return path;
    }

    private static void AddHull(SKPath path, IEnumerable<SKPoint> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
        {
            // degenerate chisel segment: draw a hairline-thin sliver so it still has area
            if (pts.Count == 2)
            {
                path.MoveTo(pts[0].X, pts[0].Y - 0.5f);
                path.LineTo(pts[1].X, pts[1].Y - 0.5f);
                path.LineTo(pts[1].X, pts[1].Y + 0.5f);
                path.LineTo(pts[0].X, pts[0].Y + 0.5f);
                path.Close();
            }
            return;
        }
        static float Cross(SKPoint o, SKPoint a, SKPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<SKPoint>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        if (hull.Count < 3) return;
        path.MoveTo(hull[0]);
        for (var i = 1; i < hull.Count; i++) path.LineTo(hull[i]);
        path.Close();
    }

    /// <summary>Step badge body (circle/square) plus pointer triangle for pointer styles.</summary>
    public static SKPath StepPath(StepAnnotation s)
    {
        var r = Rect2.FromPoints(s.Start, s.End);
        var rect = r.ToSk();
        var path = new SKPath();
        var circle = s.Style is StepStyle.Circle or StepStyle.CirclePointer;
        if (circle) path.AddOval(rect);
        else
        {
            var rr = Math.Min(rect.Width, rect.Height) * 0.16f;
            path.AddRoundRect(rect, rr, rr);
        }
        if (s.Style is StepStyle.CirclePointer or StepStyle.SquarePointer && s.PointerTip is { } tip)
        {
            var c = r.Center;
            var dx = tip.X - c.X;
            var dy = tip.Y - c.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var radius = Math.Min(r.Width, r.Height) / 2;
            if (len > radius * 1.05)
            {
                var ux = dx / len;
                var uy = dy / len;
                var px = -uy;
                var py = ux;
                var half = radius * 0.62;
                var bx = c.X + ux * radius * 0.75;
                var by = c.Y + uy * radius * 0.75;
                using var tri = new SKPath();
                tri.MoveTo((float)(bx + px * half), (float)(by + py * half));
                tri.LineTo(tip.ToSk());
                tri.LineTo((float)(bx - px * half), (float)(by - py * half));
                tri.Close();
                return CalloutGeometry.Union(path, tri);
            }
        }
        return path;
    }

    /// <summary>Lens path for a magnifier.</summary>
    public static SKPath LensPath(MagnifyAnnotation m)
        => ShapePath(m.LensShape == ShapeKind.Polygon ? ShapeKind.Rectangle : m.LensShape, Rect2.FromPoints(m.Start, m.End), 12);

    /// <summary>Body path of a blur region.</summary>
    public static SKPath BlurPath(BlurAnnotation b)
        => ShapePath(b.Region == ShapeKind.Polygon ? ShapeKind.Rectangle : b.Region, Rect2.FromPoints(b.Start, b.End), 12);

    /// <summary>Flattens a path into polylines (curves subdivided).</summary>
    public static List<(List<Point2> Points, bool Closed)> Flatten(SKPath path, double tolerance = 0.5)
    {
        var res = new List<(List<Point2>, bool)>();
        List<Point2>? cur = null;
        var pts = new SKPoint[4];
        using var it = path.CreateRawIterator();
        SKPathVerb verb;
        var last = default(SKPoint);
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (cur is { Count: > 0 }) res.Add((cur, false));
                    cur = new List<Point2> { pts[0].ToPoint2() };
                    last = pts[0];
                    break;
                case SKPathVerb.Line:
                    cur ??= new List<Point2> { pts[0].ToPoint2() };
                    cur.Add(pts[1].ToPoint2());
                    last = pts[1];
                    break;
                case SKPathVerb.Quad:
                {
                    cur ??= new List<Point2> { pts[0].ToPoint2() };
                    var n = Segments(pts[0], pts[1], pts[2], tolerance);
                    for (var i = 1; i <= n; i++)
                    {
                        var t = (double)i / n;
                        var mt = 1 - t;
                        cur.Add(new Point2(mt * mt * pts[0].X + 2 * mt * t * pts[1].X + t * t * pts[2].X, mt * mt * pts[0].Y + 2 * mt * t * pts[1].Y + t * t * pts[2].Y));
                    }
                    last = pts[2];
                    break;
                }
                case SKPathVerb.Conic:
                {
                    cur ??= new List<Point2> { pts[0].ToPoint2() };
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                    for (var q = 0; q + 2 < quads.Length; q += 2)
                    {
                        var a = quads[q];
                        var b = quads[q + 1];
                        var c = quads[q + 2];
                        var n = Segments(a, b, c, tolerance);
                        for (var i = 1; i <= n; i++)
                        {
                            var t = (double)i / n;
                            var mt = 1 - t;
                            cur.Add(new Point2(mt * mt * a.X + 2 * mt * t * b.X + t * t * c.X, mt * mt * a.Y + 2 * mt * t * b.Y + t * t * c.Y));
                        }
                    }
                    last = pts[2];
                    break;
                }
                case SKPathVerb.Cubic:
                {
                    cur ??= new List<Point2> { pts[0].ToPoint2() };
                    var n = Math.Clamp((int)(Dist(pts[0], pts[1]) + Dist(pts[1], pts[2]) + Dist(pts[2], pts[3])) / 4, 8, 64);
                    for (var i = 1; i <= n; i++)
                    {
                        var t = (double)i / n;
                        var mt = 1 - t;
                        cur.Add(new Point2(
                            mt * mt * mt * pts[0].X + 3 * mt * mt * t * pts[1].X + 3 * mt * t * t * pts[2].X + t * t * t * pts[3].X,
                            mt * mt * mt * pts[0].Y + 3 * mt * mt * t * pts[1].Y + 3 * mt * t * t * pts[2].Y + t * t * t * pts[3].Y));
                    }
                    last = pts[3];
                    break;
                }
                case SKPathVerb.Close:
                    if (cur is { Count: > 1 })
                    {
                        if (cur[0].DistanceSquaredTo(cur[^1]) < 1e-9) cur.RemoveAt(cur.Count - 1);
                        res.Add((cur, true));
                    }
                    cur = null;
                    break;
            }
        }
        _ = last;
        if (cur is { Count: > 0 }) res.Add((cur, false));
        return res;
    }

    private static float Dist(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static int Segments(SKPoint a, SKPoint b, SKPoint c, double tol)
        => Math.Clamp((int)Math.Ceiling(Math.Sqrt((Dist(a, b) + Dist(b, c)) / Math.Max(0.1, tol)) * 1.5), 4, 48);
}
