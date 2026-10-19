using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;

namespace Shotwise.Editor.Internal;

/// <summary>Geometric transforms applied uniformly to every point-bearing field of an annotation.</summary>
internal static class AnnotationGeometry
{
    private const double Eps = 1e-9;

    /// <summary>True for kinds whose geometry is a start→end segment (handles are endpoints, no box/rotation).</summary>
    public static bool IsLineLike(Annotation a) => a is ArrowAnnotation or LineAnnotation;

    /// <summary>Kinds that keep their aspect ratio on corner resizes (Shift temporarily releases the lock).</summary>
    public static bool KeepsAspect(Annotation a) => a switch
    {
        StampAnnotation s => s.PreserveAspect,
        StepAnnotation => true,
        CursorAnnotation => true,
        _ => false,
    };

    /// <summary>Maps every geometric point of <paramref name="a"/> through <paramref name="map"/>.</summary>
    public static void Transform(Annotation a, Func<Point2, Point2> map)
    {
        a.Start = map(a.Start);
        a.End = map(a.End);
        switch (a)
        {
            case ArrowAnnotation ar:
                ar.Control1 = ar.Control1 is { } a1 ? map(a1) : null;
                ar.Control2 = ar.Control2 is { } a2 ? map(a2) : null;
                break;
            case LineAnnotation l:
                l.Control1 = l.Control1 is { } l1 ? map(l1) : null;
                l.Control2 = l.Control2 is { } l2 ? map(l2) : null;
                break;
            case ShapeAnnotation s:
                for (var i = 0; i < s.Points.Count; i++) s.Points[i] = map(s.Points[i]);
                break;
            case CalloutAnnotation c:
                for (var i = 0; i < c.Tails.Count; i++) c.Tails[i] = c.Tails[i] with { Tip = map(c.Tails[i].Tip) };
                break;
            case StepAnnotation st:
                st.PointerTip = st.PointerTip is { } t ? map(t) : null;
                break;
            case PenAnnotation p:
                for (var i = 0; i < p.Points.Count; i++) p.Points[i] = map(p.Points[i]);
                break;
            case HighlightAnnotation h when h.Points is not null:
                for (var i = 0; i < h.Points.Count; i++) h.Points[i] = map(h.Points[i]);
                break;
            case MagnifyAnnotation m:
                m.SourceRect = Rect2.FromPoints(map(m.SourceRect.TopLeft), map(m.SourceRect.BottomRight));
                break;
        }
    }

    /// <summary>
    /// Affine-maps the annotation from <paramref name="from"/> (its old unrotated bounds) to <paramref name="to"/>
    /// (may have negative width/height = mirrored drag). Magnify lenses keep their source centre and derive the source
    /// size from the lens size and scale.
    /// </summary>
    public static void Resize(Annotation a, Rect2 from, Rect2 to)
    {
        var sx = Math.Abs(from.Width) < Eps ? 1 : to.Width / from.Width;
        var sy = Math.Abs(from.Height) < Eps ? 1 : to.Height / from.Height;
        Rect2? source = a is MagnifyAnnotation mg ? mg.SourceRect : null;
        Transform(a, p => new Point2(to.X + (p.X - from.X) * sx, to.Y + (p.Y - from.Y) * sy));
        if (a is MagnifyAnnotation m && source is { } src)
        {
            var lens = m.Bounds;
            var scale = m.Scale > 0 ? m.Scale : 1;
            var w = lens.Width / scale;
            var h = lens.Height / scale;
            m.SourceRect = new Rect2(src.Center.X - w / 2, src.Center.Y - h / 2, w, h);
        }
    }

    /// <summary>Mirrors the annotation about <paramref name="center"/>; rotation is negated.</summary>
    public static void Flip(Annotation a, Point2 center, bool horizontal)
    {
        Transform(a, p => horizontal ? new Point2(2 * center.X - p.X, p.Y) : new Point2(p.X, 2 * center.Y - p.Y));
        a.RotationDegrees = GeometryMath.NormalizeDegrees(-a.RotationDegrees);
    }

    /// <summary>Rotates the annotation by <paramref name="degrees"/> around <paramref name="pivot"/>.</summary>
    public static void RotateAround(Annotation a, Point2 pivot, double degrees)
    {
        if (degrees == 0) return;
        if (IsLineLike(a))
        {
            Transform(a, p => GeometryMath.Rotate(p, pivot, degrees));
            return;
        }
        var c = a.Bounds.Center;
        var nc = GeometryMath.Rotate(c, pivot, degrees);
        a.Translate(nc.X - c.X, nc.Y - c.Y);
        a.RotationDegrees = GeometryMath.NormalizeDegrees(a.RotationDegrees + degrees);
    }

    /// <summary>Default Bezier control points for a straight segment (1/3 and 2/3 along it).</summary>
    public static (Point2 C1, Point2 C2) DefaultControls(Point2 s, Point2 e)
        => (GeometryMath.Lerp(s, e, 1.0 / 3), GeometryMath.Lerp(s, e, 2.0 / 3));

    /// <summary>Union of rotated bounds; <see cref="Rect2.Empty"/> for no items.</summary>
    public static Rect2 Union(IEnumerable<Annotation> items)
    {
        Rect2? r = null;
        foreach (var a in items) r = r is null ? a.RotatedBounds : Rect2.FromLTRB(Math.Min(r.Value.Left, a.RotatedBounds.Left), Math.Min(r.Value.Top, a.RotatedBounds.Top), Math.Max(r.Value.Right, a.RotatedBounds.Right), Math.Max(r.Value.Bottom, a.RotatedBounds.Bottom));
        return r ?? Rect2.Empty;
    }

    /// <summary>Converts a world point into the unrotated frame of a box rotated by <paramref name="degrees"/> about <paramref name="center"/>.</summary>
    public static Point2 ToLocal(Point2 world, Point2 center, double degrees) => GeometryMath.Rotate(world, center, -degrees);

    public static Point2 ToWorld(Point2 local, Point2 center, double degrees) => GeometryMath.Rotate(local, center, degrees);
}
