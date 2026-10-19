using Shotwise.Core.Geometry;

namespace Shotwise.Editor.Internal;

/// <summary>Snap guide line in canvas pixels (drawn by the view as <see cref="OverlayLine"/> with <see cref="OverlayStyle.Guide"/>).</summary>
internal readonly record struct SnapGuide(Point2 From, Point2 To);

internal readonly record struct SnapResult(double Dx, double Dy, IReadOnlyList<SnapGuide> Guides)
{
    public static SnapResult None { get; } = new(0, 0, Array.Empty<SnapGuide>());
}

/// <summary>
/// Canvas snapping: aligns the left/centre/right (top/middle/bottom) of a moving rectangle — or a single dragged point —
/// with the canvas edges/centre and the edges/centres of other objects, within a screen-constant threshold.
/// </summary>
internal static class SnapEngine
{
    private readonly record struct Line(double Pos, double Min, double Max);

    private static (List<Line> Xs, List<Line> Ys) Targets(Rect2 canvas, IEnumerable<Rect2> others)
    {
        var xs = new List<Line>
        {
            new(canvas.Left, canvas.Top, canvas.Bottom), new(canvas.Center.X, canvas.Top, canvas.Bottom), new(canvas.Right, canvas.Top, canvas.Bottom),
        };
        var ys = new List<Line>
        {
            new(canvas.Top, canvas.Left, canvas.Right), new(canvas.Center.Y, canvas.Left, canvas.Right), new(canvas.Bottom, canvas.Left, canvas.Right),
        };
        foreach (var o in others)
        {
            xs.Add(new(o.Left, o.Top, o.Bottom));
            xs.Add(new(o.Center.X, o.Top, o.Bottom));
            xs.Add(new(o.Right, o.Top, o.Bottom));
            ys.Add(new(o.Top, o.Left, o.Right));
            ys.Add(new(o.Center.Y, o.Left, o.Right));
            ys.Add(new(o.Bottom, o.Left, o.Right));
        }
        return (xs, ys);
    }

    private static (double Delta, Line? Target) Best(IEnumerable<double> probes, List<Line> targets, double threshold)
    {
        var best = double.PositiveInfinity;
        Line? hit = null;
        foreach (var p in probes)
            foreach (var t in targets)
            {
                var d = t.Pos - p;
                if (Math.Abs(d) <= threshold && Math.Abs(d) < Math.Abs(best))
                {
                    best = d;
                    hit = t;
                }
            }
        return hit is null ? (0, null) : (best, hit);
    }

    /// <summary>Snaps a moving rectangle; returns the correction to add to the drag delta and the guides to draw.</summary>
    public static SnapResult SnapRect(Rect2 moving, Rect2 canvas, IEnumerable<Rect2> others, double threshold, bool snapX = true, bool snapY = true)
    {
        var (xs, ys) = Targets(canvas, others);
        var guides = new List<SnapGuide>();
        double dx = 0, dy = 0;
        if (snapX)
        {
            var (d, t) = Best(new[] { moving.Left, moving.Center.X, moving.Right }, xs, threshold);
            if (t is { } line)
            {
                dx = d;
                guides.Add(new SnapGuide(new Point2(line.Pos, Math.Min(line.Min, moving.Top + dy)), new Point2(line.Pos, Math.Max(line.Max, moving.Bottom + dy))));
            }
        }
        if (snapY)
        {
            var (d, t) = Best(new[] { moving.Top, moving.Center.Y, moving.Bottom }, ys, threshold);
            if (t is { } line)
            {
                dy = d;
                guides.Add(new SnapGuide(new Point2(Math.Min(line.Min, moving.Left + dx), line.Pos), new Point2(Math.Max(line.Max, moving.Right + dx), line.Pos)));
            }
        }
        return new SnapResult(dx, dy, guides);
    }

    /// <summary>Snaps a single point (resize corner, creation drag point, line endpoint).</summary>
    public static SnapResult SnapPoint(Point2 p, Rect2 canvas, IEnumerable<Rect2> others, double threshold)
    {
        var (xs, ys) = Targets(canvas, others);
        var guides = new List<SnapGuide>();
        var (dx, tx) = Best(new[] { p.X }, xs, threshold);
        var (dy, ty) = Best(new[] { p.Y }, ys, threshold);
        if (tx is { } lx) guides.Add(new SnapGuide(new Point2(lx.Pos, Math.Min(lx.Min, p.Y + dy)), new Point2(lx.Pos, Math.Max(lx.Max, p.Y + dy))));
        if (ty is { } ly) guides.Add(new SnapGuide(new Point2(Math.Min(ly.Min, p.X + dx), ly.Pos), new Point2(Math.Max(ly.Max, p.X + dx), ly.Pos)));
        return new SnapResult(dx, dy, guides);
    }
}
