using SkiaSharp;

namespace StampGen;

/// <summary>A colour family: highlight → mid → low gradient stops, an edge/outline tone and the text colour that sits on it.</summary>
internal sealed record Hue(string Key, string Hi, string Mid, string Lo, string Edge, string On);

internal static class Pal
{
    public static readonly Hue Red = new("red", "#FF8080", "#EF4444", "#C81E1E", "#8F1414", "#FFFFFF");
    public static readonly Hue Orange = new("orange", "#FFB869", "#F97316", "#D4520A", "#963A06", "#FFFFFF");
    public static readonly Hue Yellow = new("yellow", "#FFEB8A", "#FACC15", "#E0A500", "#9C6A05", "#3B2A00");
    public static readonly Hue Gold = new("gold", "#FFE38F", "#F5B82E", "#CF8C0A", "#875700", "#3B2A00");
    public static readonly Hue Green = new("green", "#74EBA6", "#22C55E", "#159640", "#0D652B", "#FFFFFF");
    public static readonly Hue Teal = new("teal", "#67EBD9", "#14B8A6", "#0D8F83", "#095E57", "#FFFFFF");
    public static readonly Hue Blue = new("blue", "#86BFFF", "#3B82F6", "#1D5FD8", "#153E99", "#FFFFFF");
    public static readonly Hue Navy = new("navy", "#6C8EE8", "#2F4FAA", "#1F3680", "#122150", "#FFFFFF");
    public static readonly Hue Purple = new("purple", "#C2AEFF", "#8B5CF6", "#6B3DE0", "#47289A", "#FFFFFF");
    public static readonly Hue Pink = new("pink", "#FFA3CE", "#EC4899", "#C62B78", "#871A52", "#FFFFFF");
    public static readonly Hue Charcoal = new("charcoal", "#66707F", "#3A414C", "#232830", "#101317", "#FFFFFF");
    public static readonly Hue White = new("white", "#FFFFFF", "#F3F4F7", "#D5D9E0", "#98A0AD", "#262B33");

    public static readonly Hue[] Six = { Red, Orange, Yellow, Green, Blue, Purple };
}

internal static class Art
{
    /// <summary>
    /// "Flat-3D" fill used across our packs: soft drop shadow, darker edge, vertical body gradient, glossy top lens and a
    /// thin rim highlight. All built from paths + gradients (renders identically in Svg.Skia, WPF and browsers).
    /// </summary>
    public static void Glossy(SvgDoc s, SKPath shape, Hue h, double shadow = 1, double edge = 0.03, double gloss = 1)
    {
        var b = shape.TightBounds;
        var u = Math.Min(b.Width, b.Height);
        if (shadow > 0) s.Shadow(shape, 0, u * 0.045, u * 0.07, 0.34 * shadow);
        s.Path(shape, s.V(b.Top, b.Bottom, new Stop(0, h.Lo), new Stop(1, h.Edge)));
        using var body = Geo.Deflate(shape, u * edge);
        var bb = body.TightBounds;
        s.Path(body, s.V(bb.Top, bb.Bottom, new Stop(0, h.Hi), new Stop(0.48, h.Mid), new Stop(1, h.Lo)));
        if (gloss > 0)
        {
            using var lens = Geo.Ellipse(bb.MidX, bb.Top - bb.Height * 0.32, bb.Width * 0.78, bb.Height * 0.78);
            using var inner = Geo.Deflate(body, u * 0.035);
            using var g = Geo.Inter(inner, lens);
            s.Path(g, s.V(bb.Top, bb.Top + bb.Height * 0.48, new Stop(0, "#FFFFFF", 0.5 * gloss), new Stop(1, "#FFFFFF", 0.04)));
            using var rim = Geo.TopRim(body, u * 0.018);
            s.Path(rim, "#FFFFFF", 0.55 * gloss);
            using var low = Geo.BottomRim(body, u * 0.03);
            s.Path(low, "#000000", 0.12);
        }
    }

    /// <summary>Glyph path with a soft 1-step drop shadow underneath.</summary>
    public static void Ink(SvgDoc s, SKPath glyph, string color, double shadowOpacity = 0.28, double dy = 0)
    {
        var b = glyph.TightBounds;
        if (shadowOpacity > 0)
        {
            var off = dy > 0 ? dy : Math.Max(1.2, b.Height * 0.045);
            using var sh = Geo.Translate(glyph, 0, off);
            s.Path(sh, "#000000", shadowOpacity);
        }
        s.Path(glyph, color);
    }

    /// <summary>Points of a polyline trimmed by <paramref name="len"/> at the start and/or end (arc length).</summary>
    public static List<(double X, double Y)> Trim(IReadOnlyList<(double X, double Y)> pts, double startLen, double endLen)
    {
        var l = pts.ToList();
        l = TrimEnd(l, endLen);
        l.Reverse();
        l = TrimEnd(l, startLen);
        l.Reverse();
        return l;
    }

    private static List<(double X, double Y)> TrimEnd(List<(double X, double Y)> pts, double len)
    {
        if (len <= 0) return pts;
        var res = new List<(double X, double Y)>(pts);
        while (res.Count >= 2 && len > 0)
        {
            var a = res[^2]; var b = res[^1];
            var d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (d <= len) { len -= d; res.RemoveAt(res.Count - 1); continue; }
            var t = (d - len) / d;
            res[^1] = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            len = 0;
        }
        return res;
    }

    private static (double X, double Y) Dir(IReadOnlyList<(double X, double Y)> pts, bool atEnd, double lookback)
    {
        // Direction over the last `lookback` units, so curved arrows get a natural head angle.
        var l = atEnd ? pts.ToList() : pts.Reverse().ToList();
        var tip = l[^1];
        var acc = 0.0; var i = l.Count - 1;
        while (i > 0 && acc < lookback) { acc += Math.Sqrt(Math.Pow(l[i].X - l[i - 1].X, 2) + Math.Pow(l[i].Y - l[i - 1].Y, 2)); i--; }
        var from = l[i];
        var dx = tip.X - from.X; var dy = tip.Y - from.Y; var n = Math.Sqrt(dx * dx + dy * dy);
        return n < 1e-9 ? (1, 0) : (dx / n, dy / n);
    }

    public static SKPath Head((double X, double Y) tip, (double X, double Y) dir, double length, double width, double notch = 0.18)
    {
        var (dx, dy) = dir; var nx = -dy; var ny = dx;
        var bx = tip.X - dx * length; var by = tip.Y - dy * length;
        var pts = new List<(double, double)>
        {
            (tip.X, tip.Y),
            (bx + nx * width / 2, by + ny * width / 2),
            (bx + dx * length * notch, by + dy * length * notch),
            (bx - nx * width / 2, by - ny * width / 2),
        };
        return Geo.RoundPoly(pts, Math.Max(1.5, width * 0.06));
    }

    /// <summary>Solid arrow along a centre line: stroked shaft + notched head(s).</summary>
    public static SKPath Arrow(IReadOnlyList<(double X, double Y)> line, double shaft, double headLen, double headW, bool startHead = false, bool endHead = true)
    {
        var endDir = Dir(line, true, headLen);
        var startDir = Dir(line, false, headLen);
        var body = Trim(line, startHead ? headLen * 0.75 : 0, endHead ? headLen * 0.75 : 0);
        using var center = Geo.Open(body);
        var acc = Geo.Stroke(center, shaft, startHead ? SKStrokeCap.Butt : SKStrokeCap.Round, SKStrokeJoin.Round);
        if (endHead)
        {
            using var h = Head(line[^1], endDir, headLen, headW);
            var n = Geo.Union(acc, h); acc.Dispose(); acc = n;
        }
        if (startHead)
        {
            using var h = Head(line[0], startDir, headLen, headW);
            var n = Geo.Union(acc, h); acc.Dispose(); acc = n;
        }
        return acc;
    }

    public static List<(double X, double Y)> Arc(double cx, double cy, double rx, double ry, double a0Deg, double a1Deg, int steps = 64)
    {
        var l = new List<(double, double)>();
        for (var i = 0; i <= steps; i++)
        {
            var a = (a0Deg + (a1Deg - a0Deg) * i / steps) * Math.PI / 180;
            l.Add((cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
        }
        return l;
    }

    public static List<(double X, double Y)> Line(double x0, double y0, double x1, double y1, int steps = 12)
    {
        var l = new List<(double, double)>();
        for (var i = 0; i <= steps; i++) l.Add((x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps));
        return l;
    }

    /// <summary>Adds wobble to a polyline (hand-drawn look).</summary>
    public static List<(double X, double Y)> Wobble(IReadOnlyList<(double X, double Y)> pts, double amp, int seed, double freq = 2.2)
    {
        var rnd = new Random(seed);
        var p1 = rnd.NextDouble() * 6.28; var p2 = rnd.NextDouble() * 6.28;
        var res = new List<(double, double)>();
        for (var i = 0; i < pts.Count; i++)
        {
            var t = (double)i / Math.Max(1, pts.Count - 1);
            res.Add((pts[i].X + amp * Math.Sin(p1 + t * freq * 6.28), pts[i].Y + amp * Math.Sin(p2 + t * freq * 5.1)));
        }
        return res;
    }

    public static List<(double X, double Y)> Densify(IReadOnlyList<(double X, double Y)> pts, double step = 3)
    {
        var res = new List<(double, double)> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            var d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var n = Math.Max(1, (int)Math.Ceiling(d / step));
            for (var k = 1; k <= n; k++) res.Add((a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n));
        }
        return res;
    }
}
