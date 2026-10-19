using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;

namespace Shotwise.Editor.Internal;

/// <summary>Hit-test rule shared by the session and tools (ARCHITECTURE §4: tolerance max(4/zoom, strokeWidth/2)).</summary>
internal static class HitTesting
{
    /// <summary>Screen-pixel hit tolerance; divided by zoom to get canvas pixels.</summary>
    public const double ScreenTolerance = 4;

    public static double Tolerance(double zoom) => ScreenTolerance / (zoom > 0 ? zoom : 1);

    /// <summary>
    /// True when <paramref name="p"/> hits <paramref name="outline"/>: inside a filled closed figure, or within
    /// max(<paramref name="tolerance"/>, half the stroke width) of an edge. Empty outlines fall back to rotated bounds.
    /// </summary>
    public static bool Hit(Annotation a, Geometry2 outline, Point2 p, double tolerance)
    {
        if (outline.Figures.Count == 0)
            return HitRotatedBox(a.Bounds, a.RotationDegrees, p, Math.Max(tolerance, a.Stroke.Width / 2));
        foreach (var f in outline.Figures)
        {
            if (f.Points.Count == 0) continue;
            var tol = Math.Max(tolerance, Math.Max(f.StrokeWidth, a.Stroke.IsVisible ? a.Stroke.Width : 0) / 2);
            if (f.IsFilled && f.IsClosed && f.Points.Count >= 3 && GeometryMath.PointInPolygon(p, f.Points)) return true;
            if (f.Points.Count == 1)
            {
                if (p.DistanceTo(f.Points[0]) <= tol) return true;
                continue;
            }
            if (GeometryMath.DistanceToPolyline(p, f.Points, f.IsClosed) <= tol) return true;
        }
        return false;
    }

    /// <summary>Point inside a rotated rectangle (inflated by <paramref name="inflate"/>).</summary>
    public static bool HitRotatedBox(Rect2 box, double rotation, Point2 p, double inflate)
    {
        var local = AnnotationGeometry.ToLocal(p, box.Center, rotation);
        return box.Inflate(inflate).Contains(local);
    }
}
