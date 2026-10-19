using System.Globalization;
using System.Text;
using SkiaSharp;

namespace StampGen;

internal readonly record struct Stop(double Offset, string Color, double Opacity = 1);

/// <summary>Number formatting: at most two decimals, invariant, no trailing zeros.</summary>
internal static class Num
{
    public static string F(double v)
    {
        var r = Math.Round(v, 2, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0; // no "-0"
        return r.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Minimal SVG writer for our own artwork. Only features the app's Svg.Skia path renders reliably are emitted: paths, linear and
/// radial gradients (userSpaceOnUse), group opacity and transforms. No filters, masks, CSS or text (text is converted to paths).
/// </summary>
internal sealed class SvgDoc
{
    private readonly double _vw, _vh, _ow, _oh;
    private readonly StringBuilder _defs = new();
    private readonly StringBuilder _body = new();
    private int _id;

    public SvgDoc(double vw, double vh, double? ow = null, double? oh = null)
    {
        _vw = vw; _vh = vh; _ow = ow ?? vw; _oh = oh ?? vh;
    }

    public double W => _vw;
    public double H => _vh;

    private string NewId()
    {
        var n = _id++;
        const string a = "abcdefghijklmnopqrstuvwxyz";
        var s = "";
        do { s = a[n % 26] + s; n /= 26; } while (n > 0);
        return s;
    }

    private static void StopsTo(StringBuilder sb, Stop[] stops)
    {
        foreach (var s in stops)
        {
            sb.Append("<stop offset=\"").Append(Num.F(s.Offset)).Append("\" stop-color=\"").Append(s.Color).Append('"');
            if (s.Opacity < 1) sb.Append(" stop-opacity=\"").Append(Num.F(s.Opacity)).Append('"');
            sb.Append("/>");
        }
    }

    /// <summary>Linear gradient in user space; returns the <c>url(#id)</c> paint.</summary>
    public string Linear(double x1, double y1, double x2, double y2, params Stop[] stops)
    {
        var id = NewId();
        _defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{Num.F(x1)}\" y1=\"{Num.F(y1)}\" x2=\"{Num.F(x2)}\" y2=\"{Num.F(y2)}\">");
        StopsTo(_defs, stops);
        _defs.Append("</linearGradient>");
        return $"url(#{id})";
    }

    /// <summary>Vertical linear gradient over [y1, y2].</summary>
    public string V(double y1, double y2, params Stop[] stops) => Linear(0, y1, 0, y2, stops);

    /// <summary>Radial gradient (optionally elliptical via sx/sy scale around the centre).</summary>
    public string Radial(double cx, double cy, double r, Stop[] stops, double sx = 1, double sy = 1, double? fx = null, double? fy = null)
    {
        var id = NewId();
        _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{Num.F(cx)}\" cy=\"{Num.F(cy)}\" r=\"{Num.F(r)}\"");
        if (fx is not null) _defs.Append($" fx=\"{Num.F(fx.Value)}\" fy=\"{Num.F(fy ?? cy)}\"");
        if (sx != 1 || sy != 1)
            _defs.Append($" gradientTransform=\"matrix({Num.F(sx)} 0 0 {Num.F(sy)} {Num.F(cx - sx * cx)} {Num.F(cy - sy * cy)})\"");
        _defs.Append('>');
        StopsTo(_defs, stops);
        _defs.Append("</radialGradient>");
        return $"url(#{id})";
    }

    public SvgDoc Path(SKPath p, string fill, double opacity = 1, string? extra = null)
    {
        if (p.IsEmpty) return this;
        _body.Append("<path d=\"").Append(Geo.D(p)).Append("\" fill=\"").Append(fill).Append('"');
        if (p.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd) _body.Append(" fill-rule=\"evenodd\"");
        if (opacity < 1) _body.Append(" fill-opacity=\"").Append(Num.F(opacity)).Append('"');
        if (extra is not null) _body.Append(' ').Append(extra);
        _body.Append("/>");
        return this;
    }

    /// <summary>Stroked (unfilled) path, e.g. hairline rims.</summary>
    public SvgDoc StrokePath(SKPath p, string stroke, double width, double opacity = 1, string join = "round", string cap = "round")
    {
        if (p.IsEmpty) return this;
        _body.Append("<path d=\"").Append(Geo.D(p)).Append("\" fill=\"none\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(Num.F(width))
             .Append("\" stroke-linejoin=\"").Append(join).Append("\" stroke-linecap=\"").Append(cap).Append('"');
        if (opacity < 1) _body.Append(" stroke-opacity=\"").Append(Num.F(opacity)).Append('"');
        _body.Append("/>");
        return this;
    }

    public SvgDoc Open(string attrs) { _body.Append("<g ").Append(attrs).Append('>'); return this; }
    public SvgDoc Close() { _body.Append("</g>"); return this; }

    /// <summary>Soft drop shadow emulated with stacked, progressively inflated copies (no SVG filter).</summary>
    public SvgDoc Shadow(SKPath shape, double dx, double dy, double blur, double opacity, string color = "#000000", int layers = 5)
    {
        using var moved = Geo.Translate(shape, dx, dy);
        for (var i = layers - 1; i >= 0; i--)
        {
            var grow = blur * i / Math.Max(1, layers - 1) - blur * 0.35;
            using var p = grow >= 0 ? Geo.Inflate(moved, grow) : Geo.Deflate(moved, -grow);
            Path(p, color, opacity / layers);
        }
        return this;
    }

    /// <summary>Elliptical contact shadow (radial gradient fading to transparent).</summary>
    public SvgDoc GroundShadow(double cx, double cy, double rx, double ry, double opacity)
    {
        var g = Radial(cx, cy, rx, new[] { new Stop(0, "#000000", opacity), new Stop(0.55, "#000000", opacity * 0.45), new Stop(1, "#000000", 0) }, 1, ry / rx);
        using var e = Geo.Ellipse(cx, cy, rx, ry);
        return Path(e, g);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Num.F(_vw)} {Num.F(_vh)}\" width=\"{Num.F(_ow)}\" height=\"{Num.F(_oh)}\">");
        if (_defs.Length > 0) sb.Append("<defs>").Append(_defs).Append("</defs>");
        sb.Append(_body).Append("</svg>");
        return sb.ToString();
    }
}

/// <summary>Geometry helpers built on SKPath (path boolean ops give us distressed ink, halftone and highlights without masks).</summary>
internal static class Geo
{
    public static string D(SKPath p)
    {
        var sb = new StringBuilder();
        using var it = p.CreateRawIterator();
        var pts = new SKPoint[4];
        char last = '\0';
        void N(string s)
        {
            if (sb.Length > 0 && s[0] != '-')
            {
                var lc = sb[^1];
                if (char.IsDigit(lc) || lc == '.') sb.Append(' ');
            }
            sb.Append(s);
        }
        void Cmd(char c, params SKPoint[] ps)
        {
            if (c != last || c == 'M') sb.Append(c);
            foreach (var q in ps) { N(Num.F(q.X)); N(Num.F(q.Y)); }
            last = c;
        }
        SKPathVerb v;
        while ((v = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (v)
            {
                case SKPathVerb.Move: Cmd('M', pts[0]); break;
                case SKPathVerb.Line: Cmd('L', pts[1]); break;
                case SKPathVerb.Quad: Cmd('Q', pts[1], pts[2]); break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 1);
                    for (var i = 1; i + 1 < quads.Length; i += 2) Cmd('Q', quads[i], quads[i + 1]);
                    break;
                case SKPathVerb.Cubic: Cmd('C', pts[1], pts[2], pts[3]); break;
                case SKPathVerb.Close: sb.Append('Z'); last = 'Z'; break;
            }
        }
        return sb.ToString();
    }

    public static SKPath RRect(double x, double y, double w, double h, double r)
    {
        var p = new SKPath();
        p.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r, (float)r);
        return p;
    }

    public static SKPath RRect(double x, double y, double w, double h, double rtl, double rtr, double rbr, double rbl)
    {
        var p = new SKPath();
        var rr = new SKRoundRect();
        rr.SetRectRadii(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), new[]
        {
            new SKPoint((float)rtl, (float)rtl), new SKPoint((float)rtr, (float)rtr), new SKPoint((float)rbr, (float)rbr), new SKPoint((float)rbl, (float)rbl),
        });
        p.AddRoundRect(rr);
        return p;
    }

    public static SKPath Rect(double x, double y, double w, double h)
    {
        var p = new SKPath();
        p.AddRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
        return p;
    }

    public static SKPath Circle(double cx, double cy, double r)
    {
        var p = new SKPath();
        p.AddCircle((float)cx, (float)cy, (float)r);
        return p;
    }

    public static SKPath Ellipse(double cx, double cy, double rx, double ry)
    {
        var p = new SKPath();
        p.AddOval(new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry)));
        return p;
    }

    public static SKPath Poly(IEnumerable<(double X, double Y)> pts, bool close = true)
    {
        var p = new SKPath();
        var first = true;
        foreach (var (x, y) in pts)
        {
            if (first) p.MoveTo((float)x, (float)y); else p.LineTo((float)x, (float)y);
            first = false;
        }
        if (close) p.Close();
        return p;
    }

    /// <summary>Polygon with rounded corners (quadratic corner fillets of radius r, clamped to half the edge length).</summary>
    public static SKPath RoundPoly(IReadOnlyList<(double X, double Y)> pts, double r)
    {
        var p = new SKPath();
        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n]; var cur = pts[i]; var next = pts[(i + 1) % n];
            var (ax, ay) = Toward(cur, prev, r);
            var (bx, by) = Toward(cur, next, r);
            if (i == 0) p.MoveTo((float)ax, (float)ay); else p.LineTo((float)ax, (float)ay);
            p.QuadTo((float)cur.X, (float)cur.Y, (float)bx, (float)by);
        }
        p.Close();
        return p;
    }

    private static (double, double) Toward((double X, double Y) from, (double X, double Y) to, double r)
    {
        var dx = to.X - from.X; var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var d = Math.Min(r, len / 2);
        return (from.X + dx / len * d, from.Y + dy / len * d);
    }

    public static List<(double X, double Y)> StarPts(double cx, double cy, int n, double rOut, double rIn, double rotDeg = -90)
    {
        var l = new List<(double, double)>();
        for (var i = 0; i < n * 2; i++)
        {
            var a = (rotDeg + i * 180.0 / n) * Math.PI / 180;
            var r = i % 2 == 0 ? rOut : rIn;
            l.Add((cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
        }
        return l;
    }

    public static List<(double X, double Y)> RegularPts(double cx, double cy, int n, double r, double rotDeg = -90)
    {
        var l = new List<(double, double)>();
        for (var i = 0; i < n; i++)
        {
            var a = (rotDeg + i * 360.0 / n) * Math.PI / 180;
            l.Add((cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
        }
        return l;
    }

    /// <summary>Small dot as a polygon (cheaper than a curve; indistinguishable at dot sizes). Optional irregularity for speckles.</summary>
    public static void AddDot(SKPath p, double x, double y, double r, Random? rnd = null, double sx = 1)
    {
        var n = r < 1.2 ? 5 : r < 2.4 ? 6 : 8;
        var a0 = rnd?.NextDouble() * 6.28 ?? 0;
        for (var i = 0; i < n; i++)
        {
            var a = a0 + i * Math.PI * 2 / n;
            var rr = r * (rnd is null ? 1 : 0.7 + rnd.NextDouble() * 0.5);
            var px = (float)(x + Math.Cos(a) * rr * sx); var py = (float)(y + Math.Sin(a) * rr);
            if (i == 0) p.MoveTo(px, py); else p.LineTo(px, py);
        }
        p.Close();
    }

    public static SKPath Op(SKPath a, SKPath b, SKPathOp op) => a.Op(b, op) ?? new SKPath();
    public static SKPath Union(SKPath a, SKPath b) => Op(a, b, SKPathOp.Union);
    public static SKPath Diff(SKPath a, SKPath b) => Op(a, b, SKPathOp.Difference);
    public static SKPath Inter(SKPath a, SKPath b) => Op(a, b, SKPathOp.Intersect);

    public static SKPath UnionAll(IEnumerable<SKPath> parts)
    {
        var acc = new SKPath();
        foreach (var p in parts)
        {
            var next = Union(acc, p);
            acc.Dispose();
            acc = next;
        }
        return acc;
    }

    /// <summary>Concatenates paths without boolean ops (non-zero winding; fine for non-overlapping or same-direction parts).</summary>
    public static SKPath Concat(IEnumerable<SKPath> parts)
    {
        var acc = new SKPath();
        foreach (var p in parts) acc.AddPath(p);
        return acc;
    }

    public static SKPath Transform(SKPath p, SKMatrix m)
    {
        var c = new SKPath(p);
        c.Transform(m);
        return c;
    }

    public static SKPath Translate(SKPath p, double dx, double dy) => Transform(p, SKMatrix.CreateTranslation((float)dx, (float)dy));
    public static SKPath Rotate(SKPath p, double deg, double cx, double cy) => Transform(p, SKMatrix.CreateRotationDegrees((float)deg, (float)cx, (float)cy));
    public static SKPath Scale(SKPath p, double sx, double sy, double cx = 0, double cy = 0) => Transform(p, SKMatrix.CreateScale((float)sx, (float)sy, (float)cx, (float)cy));

    /// <summary>Outline of a stroked centre line as a filled path.</summary>
    public static SKPath Stroke(SKPath center, double width, SKStrokeCap cap = SKStrokeCap.Round, SKStrokeJoin join = SKStrokeJoin.Round)
    {
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = cap, StrokeJoin = join, IsAntialias = true };
        // Stroke contour by contour: a closed contour strokes to an outer + inner outline that must be read even-odd (a ring),
        // open contours keep non-zero so overlapping joins stay solid.
        var parts = new List<SKPath>();
        foreach (var (contour, closed) in Contours(center))
        {
            var dst = new SKPath();
            paint.GetFillPath(contour, dst, 4f);
            if (closed) dst.FillType = SKPathFillType.EvenOdd;
            var simple = dst.Simplify() ?? dst;
            if (!ReferenceEquals(simple, dst)) dst.Dispose();
            parts.Add(simple);
            contour.Dispose();
        }
        if (parts.Count == 1) return parts[0];
        var res = UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        return res;
    }

    private static List<(SKPath Path, bool Closed)> Contours(SKPath p)
    {
        var res = new List<(SKPath, bool)>();
        using var it = p.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPath? cur = null; var closed = false;
        SKPathVerb v;
        while ((v = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (v)
            {
                case SKPathVerb.Move:
                    if (cur is not null) res.Add((cur, closed));
                    cur = new SKPath(); closed = false; cur.MoveTo(pts[0]); break;
                case SKPathVerb.Line: cur!.LineTo(pts[1]); break;
                case SKPathVerb.Quad: cur!.QuadTo(pts[1], pts[2]); break;
                case SKPathVerb.Conic: cur!.ConicTo(pts[1], pts[2], it.ConicWeight()); break;
                case SKPathVerb.Cubic: cur!.CubicTo(pts[1], pts[2], pts[3]); break;
                case SKPathVerb.Close: cur!.Close(); closed = true; break;
            }
        }
        if (cur is not null) res.Add((cur, closed));
        return res;
    }

    public static SKPath Inflate(SKPath p, double d)
    {
        if (d <= 0.01) return new SKPath(p);
        using var s = Stroke(p, d * 2);
        return Union(p, s);
    }

    public static SKPath Deflate(SKPath p, double d)
    {
        if (d <= 0.01) return new SKPath(p);
        using var s = Stroke(p, d * 2);
        return Diff(p, s);
    }

    /// <summary>Thin crescent along the top edge of a shape (shape minus itself shifted down) — used as a rim highlight.</summary>
    public static SKPath TopRim(SKPath shape, double thickness)
    {
        using var shifted = Translate(shape, 0, thickness);
        return Diff(shape, shifted);
    }

    public static SKPath BottomRim(SKPath shape, double thickness)
    {
        using var shifted = Translate(shape, 0, -thickness);
        return Diff(shape, shifted);
    }

    public static SKRect Bounds(SKPath p) => p.TightBounds;

    /// <summary>Hand-drawn marker stroke: a filled ribbon along a centre line with jittered, tapering width.</summary>
    public static SKPath Marker(IReadOnlyList<(double X, double Y)> line, double width, int seed, double jitter = 0.18, double taper = 0.35)
    {
        var rnd = new Random(seed);
        var n = line.Count;
        var left = new List<(double, double)>(); var right = new List<(double, double)>();
        var phase = rnd.NextDouble() * 6.28;
        for (var i = 0; i < n; i++)
        {
            var a = line[Math.Max(0, i - 1)]; var b = line[Math.Min(n - 1, i + 1)];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
            var nx = -dy / len; var ny = dx / len;
            var t = n == 1 ? 0.5 : (double)i / (n - 1);
            var end = Math.Min(t, 1 - t);
            var taperK = end < 0.12 ? (1 - taper) + taper * end / 0.12 : 1.0;
            var w = width * taperK * (1 + jitter * Math.Sin(phase + t * 9.0) * 0.6 + jitter * (rnd.NextDouble() - 0.5) * 0.5) / 2;
            left.Add((line[i].X + nx * w, line[i].Y + ny * w));
            right.Add((line[i].X - nx * w, line[i].Y - ny * w));
        }
        // Build the ribbon in short overlapping chunks so loops that cross themselves (circles, stars) never fill their inside.
        var parts = new List<SKPath>();
        const int chunk = 24;
        for (var i0 = 0; i0 < n - 1; i0 += chunk)
        {
            var i1 = Math.Min(n - 1, i0 + chunk + 1);
            var pts = left.Skip(i0).Take(i1 - i0 + 1).Concat(right.Skip(i0).Take(i1 - i0 + 1).Reverse()).ToList();
            var poly = Poly(pts);
            var fixedPoly = poly.Simplify() ?? poly;
            if (!ReferenceEquals(fixedPoly, poly)) poly.Dispose();
            parts.Add(fixedPoly);
        }
        // Round ends: discs at both ends.
        parts.Add(Circle(line[0].X, line[0].Y, width * (1 - taper) / 2));
        parts.Add(Circle(line[n - 1].X, line[n - 1].Y, width * (1 - taper) / 2));
        var res = UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        var simple = res.Simplify() ?? res;
        return simple;
    }

    /// <summary>Samples a quadratic/cubic Bézier into points.</summary>
    public static List<(double X, double Y)> Bezier((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y)? p3 = null, int steps = 40)
    {
        var l = new List<(double, double)>();
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps; var u = 1 - t;
            if (p3 is null) l.Add((u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y));
            else
            {
                var q = p3.Value;
                l.Add((u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * q.X,
                       u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * q.Y));
            }
        }
        return l;
    }

    public static SKPath Open(IReadOnlyList<(double X, double Y)> pts) => Poly(pts, close: false);
}

/// <summary>Fonts (OFL / Apache) used only to turn glyphs into outlines at authoring time.</summary>
internal static class Txt
{
    private static readonly Dictionary<string, SKTypeface> Cache = new();
    public static string CacheDir { get; set; } = "";

    public static SKTypeface Get(string key)
    {
        if (Cache.TryGetValue(key, out var tf)) return tf;
        var file = Sources.FontFile(CacheDir, key);
        tf = SKTypeface.FromFile(file) ?? throw new InvalidOperationException("Cannot load font " + file);
        Cache[key] = tf;
        return tf;
    }

    public static bool Has(string key, string text)
    {
        var tf = Get(key);
        using var font = new SKFont(tf, 64);
        foreach (var ch in text.EnumerateRunes())
            if (font.GetGlyph(ch.Value) == 0) return false;
        return true;
    }

    /// <summary>Glyph outlines for <paramref name="text"/> at <paramref name="size"/>, baseline at y = 0.</summary>
    public static SKPath Raw(string text, string key, double size, double tracking = 0)
    {
        var tf = Get(key);
        using var font = new SKFont(tf, (float)size) { Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
        if (tracking == 0) return font.GetTextPath(text, new SKPoint(0, 0));
        var acc = new SKPath();
        double x = 0;
        foreach (var r in text.EnumerateRunes())
        {
            var s = r.ToString();
            using var g = font.GetTextPath(s, new SKPoint((float)x, 0));
            acc.AddPath(g);
            x += font.MeasureText(s) + tracking * size;
        }
        return acc;
    }

    public static double CapHeight(string key, double size)
    {
        using var font = new SKFont(Get(key), (float)size);
        var ch = font.Metrics.CapHeight;
        return ch > 0 ? ch : size * 0.7;
    }

    /// <summary>
    /// Text fitted into a box: scaled down to fit the width (never up), horizontally aligned (0 left, 0.5 centre, 1 right) and
    /// vertically centred on the cap height (so "a", "A" and "9" share a baseline) unless <paramref name="tight"/> is set.
    /// </summary>
    public static SKPath Fit(string text, string key, double size, double x, double y, double w, double h, double align = 0.5, bool tight = false, double tracking = 0)
    {
        var p = Raw(text, key, size, tracking);
        var b = p.TightBounds;
        if (b.Width <= 0) return p;
        var k = Math.Min(1.0, w / b.Width);
        if (tight) k = Math.Min(k, h / b.Height);
        var cap = CapHeight(key, size);
        double top = tight ? b.Top : -cap, height = tight ? b.Height : cap;
        var m = SKMatrix.CreateScale((float)k, (float)k);
        var tx = x + (w - b.Width * k) * align - b.Left * k;
        var ty = y + (h - height * k) / 2 - top * k;
        m = m.PostConcat(SKMatrix.CreateTranslation((float)tx, (float)ty));
        p.Transform(m);
        return p;
    }
}
