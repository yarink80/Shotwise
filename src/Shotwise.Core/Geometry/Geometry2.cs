namespace Shotwise.Core.Geometry;

/// <summary>One polyline or closed polygon of an outline.</summary>
/// <param name="Points">Vertices in canvas pixels (curves already flattened).</param>
/// <param name="IsClosed">True when the last point connects back to the first.</param>
/// <param name="IsFilled">True when the interior counts as "inside" for hit testing.</param>
/// <param name="StrokeWidth">Width of the stroke along this figure (adds to hit tolerance).</param>
public sealed record GeometryFigure(IReadOnlyList<Point2> Points, bool IsClosed, bool IsFilled, double StrokeWidth = 0);

/// <summary>
/// Outline geometry of an annotation for hit testing: a list of flattened polylines / closed shapes,
/// already transformed by the annotation's rotation. Produced by IDocumentRenderer.GetOutline.
/// </summary>
public sealed record Geometry2(IReadOnlyList<GeometryFigure> Figures)
{
    public static Geometry2 Empty { get; } = new(Array.Empty<GeometryFigure>());

    public static Geometry2 FromRect(Rect2 r, bool filled, double strokeWidth = 0)
        => new(new[] { new GeometryFigure(new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }, true, filled, strokeWidth) });

    public static Geometry2 FromPolyline(IReadOnlyList<Point2> points, double strokeWidth)
        => new(new[] { new GeometryFigure(points, false, false, strokeWidth) });

    public Rect2 Bounds => Rect2.FromPoints(Figures.SelectMany(f => f.Points));

    /// <summary>True when <paramref name="p"/> is inside a filled figure or within tolerance (+ half stroke) of any edge.</summary>
    public bool HitTest(Point2 p, double tolerance)
    {
        foreach (var f in Figures)
        {
            if (f.Points.Count == 0) continue;
            var tol = tolerance + f.StrokeWidth / 2;
            if (f.IsFilled && f.IsClosed && GeometryMath.PointInPolygon(p, f.Points)) return true;
            if (f.Points.Count == 1)
            {
                if (p.DistanceTo(f.Points[0]) <= tol) return true;
                continue;
            }
            if (GeometryMath.DistanceToPolyline(p, f.Points, f.IsClosed) <= tol) return true;
        }
        return false;
    }

    /// <summary>True when any part of the geometry lies inside <paramref name="r"/> (used for marquee selection "touching" mode).</summary>
    public bool IntersectsRect(Rect2 r)
    {
        foreach (var f in Figures)
        {
            foreach (var pt in f.Points) if (r.Contains(pt)) return true;
            var n = f.Points.Count;
            var edges = f.IsClosed ? n : n - 1;
            for (var i = 0; i < edges; i++)
            {
                var a = f.Points[i];
                var b = f.Points[(i + 1) % n];
                if (GeometryMath.SegmentIntersectsRect(a, b, r)) return true;
            }
            if (f.IsFilled && f.IsClosed && GeometryMath.PointInPolygon(r.Center, f.Points)) return true;
        }
        return false;
    }
}
