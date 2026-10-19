using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;

namespace Shotwise.Editor.Internal;

/// <summary>One interactive handle of the current object selection.</summary>
/// <param name="Id">Stable id ("TopLeft", "Rotate", "Start", "tail:0", "vertex:3", "PointerTip", "LensSource"…).</param>
/// <param name="Kind">Handle kind (drives cursor + drag behaviour).</param>
/// <param name="Position">Centre in canvas pixels.</param>
/// <param name="Index">Tail / vertex index for indexed handles, else -1.</param>
internal sealed record HandleInfo(string Id, HandleKind Kind, Point2 Position, int Index = -1);

/// <summary>Computes selection handles; used both for overlay drawing and handle hit-testing so they always agree.</summary>
internal static class SelectionHandles
{
    /// <summary>Handle grab radius in screen pixels.</summary>
    public const double ScreenHitRadius = 6;
    /// <summary>Distance of the rotate handle above the top edge, in screen pixels.</summary>
    public const double RotateOffset = 24;

    private static readonly HandleKind[] BoxKinds =
    {
        HandleKind.TopLeft, HandleKind.Top, HandleKind.TopRight, HandleKind.Right,
        HandleKind.BottomRight, HandleKind.Bottom, HandleKind.BottomLeft, HandleKind.Left,
    };

    public static bool IsCorner(HandleKind k) => k is HandleKind.TopLeft or HandleKind.TopRight or HandleKind.BottomRight or HandleKind.BottomLeft;
    public static bool IsBoxHandle(HandleKind k) => (int)k <= (int)HandleKind.Left;

    /// <summary>Local position of a box handle on <paramref name="r"/> (unrotated).</summary>
    public static Point2 BoxPoint(Rect2 r, HandleKind k) => k switch
    {
        HandleKind.TopLeft => r.TopLeft,
        HandleKind.Top => new Point2(r.Center.X, r.Y),
        HandleKind.TopRight => r.TopRight,
        HandleKind.Right => new Point2(r.Right, r.Center.Y),
        HandleKind.BottomRight => r.BottomRight,
        HandleKind.Bottom => new Point2(r.Center.X, r.Bottom),
        HandleKind.BottomLeft => r.BottomLeft,
        HandleKind.Left => new Point2(r.X, r.Center.Y),
        _ => r.Center,
    };

    /// <summary>The box handle opposite <paramref name="k"/> (resize anchor).</summary>
    public static HandleKind Opposite(HandleKind k) => k switch
    {
        HandleKind.TopLeft => HandleKind.BottomRight,
        HandleKind.Top => HandleKind.Bottom,
        HandleKind.TopRight => HandleKind.BottomLeft,
        HandleKind.Right => HandleKind.Left,
        HandleKind.BottomRight => HandleKind.TopLeft,
        HandleKind.Bottom => HandleKind.Top,
        HandleKind.BottomLeft => HandleKind.TopRight,
        HandleKind.Left => HandleKind.Right,
        _ => k,
    };

    /// <summary>The frame handles are laid out on: single object = its bounds + rotation, multi = union of rotated bounds.</summary>
    public static (Rect2 Box, double Rotation) Frame(IReadOnlyList<Annotation> sel)
        => sel.Count == 1 ? (ManipulationBox(sel[0]), sel[0].RotationDegrees) : (AnnotationGeometry.Union(sel), 0);

    /// <summary>Box that resize handles act on (step badges exclude their pointer tail).</summary>
    public static Rect2 ManipulationBox(Annotation a) => a is StepAnnotation st ? Rect2.FromPoints(st.Start, st.End) : a.Bounds;

    public static IReadOnlyList<HandleInfo> For(IReadOnlyList<Annotation> sel, double zoom)
    {
        var list = new List<HandleInfo>();
        if (sel.Count == 0 || sel.Any(a => a.IsLocked)) return list;
        var z = zoom > 0 ? zoom : 1;
        if (sel.Count == 1)
        {
            var a = sel[0];
            switch (a)
            {
                case ArrowAnnotation ar:
                    AddLineHandles(list, ar.Start, ar.End, ar.Bezier, ar.Control1, ar.Control2);
                    return list;
                case LineAnnotation ln:
                    AddLineHandles(list, ln.Start, ln.End, ln.Bezier, ln.Control1, ln.Control2);
                    return list;
                case DropZoneAnnotation:
                    return list;
            }
            var box = ManipulationBox(a);
            var rot = a.RotationDegrees;
            var cornersOnly = AnnotationGeometry.KeepsAspect(a);
            foreach (var k in BoxKinds)
            {
                if (cornersOnly && !IsCorner(k)) continue;
                list.Add(new HandleInfo(k.ToString(), k, AnnotationGeometry.ToWorld(BoxPoint(box, k), box.Center, rot)));
            }
            if (a is not StepAnnotation) list.Add(new HandleInfo(nameof(HandleKind.Rotate), HandleKind.Rotate, RotatePoint(box, rot, z)));
            switch (a)
            {
                case CalloutAnnotation c:
                    for (var i = 0; i < c.Tails.Count; i++) list.Add(new HandleInfo($"tail:{i}", HandleKind.TailTip, c.Tails[i].Tip, i));
                    break;
                case StepAnnotation s when s.Style is StepStyle.CirclePointer or StepStyle.SquarePointer:
                    list.Add(new HandleInfo(nameof(HandleKind.PointerTip), HandleKind.PointerTip, s.PointerTip ?? DefaultPointerTip(s)));
                    break;
                case ShapeAnnotation { Shape: ShapeKind.Polygon } sh:
                    for (var i = 0; i < sh.Points.Count; i++)
                        list.Add(new HandleInfo($"vertex:{i}", HandleKind.Vertex, AnnotationGeometry.ToWorld(sh.Points[i], box.Center, rot), i));
                    break;
                case MagnifyAnnotation m:
                    list.Add(new HandleInfo(nameof(HandleKind.LensSource), HandleKind.LensSource, m.SourceRect.Center));
                    break;
            }
            return list;
        }

        var union = AnnotationGeometry.Union(sel);
        foreach (var k in BoxKinds) list.Add(new HandleInfo(k.ToString(), k, BoxPoint(union, k)));
        list.Add(new HandleInfo(nameof(HandleKind.Rotate), HandleKind.Rotate, RotatePoint(union, 0, z)));
        return list;
    }

    public static Point2 DefaultPointerTip(StepAnnotation s)
    {
        var r = Rect2.FromPoints(s.Start, s.End);
        return new Point2(r.Right + r.Width * 0.6, r.Bottom + r.Height * 0.6);
    }

    private static Point2 RotatePoint(Rect2 box, double rot, double zoom)
        => AnnotationGeometry.ToWorld(new Point2(box.Center.X, box.Y - RotateOffset / zoom), box.Center, rot);

    private static void AddLineHandles(List<HandleInfo> list, Point2 s, Point2 e, bool bezier, Point2? c1, Point2? c2)
    {
        list.Add(new HandleInfo(nameof(HandleKind.Start), HandleKind.Start, s));
        list.Add(new HandleInfo(nameof(HandleKind.End), HandleKind.End, e));
        if (!bezier) return;
        var (d1, d2) = AnnotationGeometry.DefaultControls(s, e);
        list.Add(new HandleInfo(nameof(HandleKind.Control1), HandleKind.Control1, c1 ?? d1));
        list.Add(new HandleInfo(nameof(HandleKind.Control2), HandleKind.Control2, c2 ?? d2));
    }

    /// <summary>Topmost handle within the grab radius of <paramref name="p"/> (indexed/endpoint handles win over box handles).</summary>
    public static HandleInfo? HitTest(IReadOnlyList<HandleInfo> handles, Point2 p, double zoom)
    {
        var r = ScreenHitRadius / (zoom > 0 ? zoom : 1);
        HandleInfo? best = null;
        var bestD = double.PositiveInfinity;
        // iterate in reverse so later (more specific) handles win ties
        for (var i = handles.Count - 1; i >= 0; i--)
        {
            var h = handles[i];
            var d = h.Position.DistanceTo(p);
            if (d <= r && d < bestD - 1e-9)
            {
                best = h;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>Resize cursor for a box handle, accounting for the frame rotation.</summary>
    public static CursorHint CursorFor(HandleKind kind, double rotation)
    {
        if (kind == HandleKind.Rotate) return CursorHint.Rotate;
        if (!IsBoxHandle(kind)) return CursorHint.Hand;
        var baseAngle = kind switch
        {
            HandleKind.Right or HandleKind.Left => 0.0,
            HandleKind.BottomRight or HandleKind.TopLeft => 45.0,
            HandleKind.Bottom or HandleKind.Top => 90.0,
            _ => 135.0,
        };
        var a = GeometryMath.NormalizeDegrees(baseAngle + rotation) % 180;
        return a switch
        {
            < 22.5 or >= 157.5 => CursorHint.SizeWE,
            < 67.5 => CursorHint.SizeNWSE,
            < 112.5 => CursorHint.SizeNS,
            _ => CursorHint.SizeNESW,
        };
    }
}
