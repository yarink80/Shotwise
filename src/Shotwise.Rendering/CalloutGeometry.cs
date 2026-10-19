using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>
/// Library of callout body shapes (our own designs, expressed as Skia paths built from unit-square polygons, curves and booleans).
/// Every id in <see cref="CalloutPresets.All"/> is implemented; unknown ids fall back to a rounded rectangle.
/// </summary>
public static class CalloutGeometry
{
    /// <summary>Builds the body path (no tails) for a preset inside <paramref name="r"/>.</summary>
    public static SKPath BuildBody(string? presetId, Rect2 r)
    {
        var rect = r.ToSk();
        var w = rect.Width;
        var h = rect.Height;
        var path = new SKPath();
        switch (presetId)
        {
            case CalloutPresets.Rectangle:
                path.AddRect(rect);
                break;
            case CalloutPresets.Ellipse:
                path.AddOval(rect);
                break;
            case CalloutPresets.Pill:
                path.AddRoundRect(rect, h / 2, h / 2);
                break;
            case CalloutPresets.SpeechBubble:
            {
                // Rounded body with a built-in tail notch at the bottom-left.
                var tailH = h * 0.2f;
                var body = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom - tailH);
                var rr = Math.Min(Math.Min(w, h) * 0.18f, 24f);
                path.AddRoundRect(body, rr, rr);
                using var tail = new SKPath();
                tail.MoveTo(rect.Left + w * 0.18f, body.Bottom - 1);
                tail.LineTo(rect.Left + w * 0.12f, rect.Bottom);
                tail.LineTo(rect.Left + w * 0.38f, body.Bottom - 1);
                tail.Close();
                return Union(path, tail);
            }
            case CalloutPresets.SpeechBubbleRound:
            {
                var tailH = h * 0.18f;
                var body = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom - tailH);
                path.AddOval(body);
                using var tail = new SKPath();
                tail.MoveTo(rect.Left + w * 0.22f, body.Bottom - body.Height * 0.12f);
                tail.LineTo(rect.Left + w * 0.12f, rect.Bottom);
                tail.LineTo(rect.Left + w * 0.40f, body.Bottom - body.Height * 0.02f);
                tail.Close();
                return Union(path, tail);
            }
            case CalloutPresets.ThoughtBubble:
            {
                var bodyBottom = rect.Bottom - h * 0.2f;
                path.AddOval(new SKRect(rect.Left, rect.Top, rect.Right, bodyBottom));
                var res = path;
                var r1 = Math.Min(w, h) * 0.07f;
                var r2 = Math.Min(w, h) * 0.045f;
                using var c1 = new SKPath();
                c1.AddCircle(rect.Left + w * 0.2f, rect.Bottom - h * 0.1f - r1 * 0.4f, r1);
                using var c2 = new SKPath();
                c2.AddCircle(rect.Left + w * 0.1f, rect.Bottom - r2, r2);
                res = Union(res, c1);
                return Union(res, c2);
            }
            case CalloutPresets.Cloud:
            {
                var cx = rect.MidX;
                var cy = rect.MidY;
                var rx = w / 2;
                var ry = h / 2;
                var inner = new SKPath();
                inner.AddOval(new SKRect(cx - rx * 0.78f, cy - ry * 0.78f, cx + rx * 0.78f, cy + ry * 0.78f));
                const int bumps = 10;
                var br = (float)(Math.PI * Math.Min(rx, ry) * 0.78 / bumps) * 1.35f;
                var res = inner;
                for (var i = 0; i < bumps; i++)
                {
                    var a = 2 * Math.PI * i / bumps;
                    var px = cx + (float)Math.Cos(a) * (rx - br);
                    var py = cy + (float)Math.Sin(a) * (ry - br);
                    using var c = new SKPath();
                    c.AddCircle(px, py, br);
                    res = Union(res, c);
                }
                return res;
            }
            case CalloutPresets.Banner:
                Poly(path, rect, new[] { (0f, 0f), (1f, 0f), (0.94f, 0.5f), (1f, 1f), (0f, 1f), (0.06f, 0.5f) });
                break;
            case CalloutPresets.Ribbon:
                Poly(path, rect, new[]
                {
                    (0f, 0.2f), (0.12f, 0.2f), (0.12f, 0f), (0.88f, 0f), (0.88f, 0.2f), (1f, 0.2f), (0.94f, 0.5f), (1f, 0.8f),
                    (0.88f, 0.8f), (0.88f, 1f), (0.12f, 1f), (0.12f, 0.8f), (0f, 0.8f), (0.06f, 0.5f),
                });
                break;
            case CalloutPresets.Tag:
            {
                var tip = Math.Min(w * 0.25f, h * 0.5f);
                path.MoveTo(rect.Left, rect.Top);
                path.LineTo(rect.Right - tip, rect.Top);
                path.LineTo(rect.Right, rect.MidY);
                path.LineTo(rect.Right - tip, rect.Bottom);
                path.LineTo(rect.Left, rect.Bottom);
                path.Close();
                var hr = Math.Min(h, w) * 0.07f;
                path.AddCircle(rect.Left + hr * 2.2f, rect.MidY, hr, SKPathDirection.CounterClockwise);
                path.FillType = SKPathFillType.EvenOdd;
                break;
            }
            case CalloutPresets.Burst:
            {
                const int points = 16;
                for (var i = 0; i < points * 2; i++)
                {
                    var a = Math.PI * i / points - Math.PI / 2;
                    var k = i % 2 == 0 ? 1f : 0.74f;
                    var px = rect.MidX + (float)Math.Cos(a) * w / 2 * k;
                    var py = rect.MidY + (float)Math.Sin(a) * h / 2 * k;
                    if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                }
                path.Close();
                break;
            }
            case CalloutPresets.Hexagon:
                Poly(path, rect, new[] { (0.25f, 0f), (0.75f, 0f), (1f, 0.5f), (0.75f, 1f), (0.25f, 1f), (0f, 0.5f) });
                break;
            case CalloutPresets.Note:
            {
                var f = Math.Min(Math.Min(w, h) * 0.22f, 40f);
                path.MoveTo(rect.Left, rect.Top);
                path.LineTo(rect.Right - f, rect.Top);
                path.LineTo(rect.Right, rect.Top + f);
                path.LineTo(rect.Right, rect.Bottom);
                path.LineTo(rect.Left, rect.Bottom);
                path.Close();
                // folded corner flap
                path.MoveTo(rect.Right - f, rect.Top);
                path.LineTo(rect.Right - f, rect.Top + f);
                path.LineTo(rect.Right, rect.Top + f);
                path.Close();
                break;
            }
            case CalloutPresets.Shield:
            {
                path.MoveTo(rect.Left, rect.Top + h * 0.12f);
                path.CubicTo(rect.Left + w * 0.3f, rect.Top + h * 0.12f, rect.Left + w * 0.42f, rect.Top + h * 0.06f, rect.MidX, rect.Top);
                path.CubicTo(rect.Left + w * 0.58f, rect.Top + h * 0.06f, rect.Left + w * 0.7f, rect.Top + h * 0.12f, rect.Right, rect.Top + h * 0.12f);
                path.CubicTo(rect.Right, rect.Top + h * 0.6f, rect.Left + w * 0.78f, rect.Top + h * 0.86f, rect.MidX, rect.Bottom);
                path.CubicTo(rect.Left + w * 0.22f, rect.Top + h * 0.86f, rect.Left, rect.Top + h * 0.6f, rect.Left, rect.Top + h * 0.12f);
                path.Close();
                break;
            }
            case CalloutPresets.ArrowRight:
                Poly(path, rect, new[] { (0f, 0.25f), (0.6f, 0.25f), (0.6f, 0f), (1f, 0.5f), (0.6f, 1f), (0.6f, 0.75f), (0f, 0.75f) });
                break;
            case CalloutPresets.ArrowLeft:
                Poly(path, rect, new[] { (1f, 0.25f), (0.4f, 0.25f), (0.4f, 0f), (0f, 0.5f), (0.4f, 1f), (0.4f, 0.75f), (1f, 0.75f) });
                break;
            case CalloutPresets.ArrowUp:
                Poly(path, rect, new[] { (0.25f, 1f), (0.25f, 0.4f), (0f, 0.4f), (0.5f, 0f), (1f, 0.4f), (0.75f, 0.4f), (0.75f, 1f) });
                break;
            case CalloutPresets.ArrowDown:
                Poly(path, rect, new[] { (0.25f, 0f), (0.25f, 0.6f), (0f, 0.6f), (0.5f, 1f), (1f, 0.6f), (0.75f, 0.6f), (0.75f, 0f) });
                break;
            case CalloutPresets.ArrowDouble:
                Poly(path, rect, new[]
                {
                    (0f, 0.5f), (0.25f, 0f), (0.25f, 0.25f), (0.75f, 0.25f), (0.75f, 0f), (1f, 0.5f),
                    (0.75f, 1f), (0.75f, 0.75f), (0.25f, 0.75f), (0.25f, 1f),
                });
                break;
            case CalloutPresets.RoundedRectangle:
            default:
            {
                var rr = Math.Min(Math.Min(w, h) * 0.2f, 28f);
                path.AddRoundRect(rect, rr, rr);
                break;
            }
        }
        return path;
    }

    /// <summary>Body path plus user tails (when the preset allows tails). Caller owns the returned path.</summary>
    public static SKPath Build(string? presetId, Rect2 r, IReadOnlyList<CalloutTail>? tails)
    {
        var body = BuildBody(presetId, r);
        if (tails is null || tails.Count == 0 || presetId is null || !CalloutPresets.AllowsTails(presetId)) return body;
        var res = body;
        var isRound = presetId is CalloutPresets.Ellipse or CalloutPresets.SpeechBubbleRound or CalloutPresets.ThoughtBubble or CalloutPresets.Cloud;
        foreach (var t in tails)
        {
            using var tp = BuildTail(r, t, isRound);
            if (tp is null) continue;
            res = Union(res, tp);
        }
        return res;
    }

    private static SKPath? BuildTail(Rect2 r, CalloutTail t, bool ellipse)
    {
        if (r.Width < 1 || r.Height < 1) return null;
        var c = r.Center;
        var dx = t.Tip.X - c.X;
        var dy = t.Tip.Y - c.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return null;
        var ux = dx / len;
        var uy = dy / len;
        // distance from centre to border along the ray
        double tb;
        if (ellipse)
        {
            var a = r.Width / 2;
            var b = r.Height / 2;
            tb = 1.0 / Math.Sqrt(ux * ux / (a * a) + uy * uy / (b * b));
        }
        else
        {
            var tx = Math.Abs(ux) < 1e-9 ? double.MaxValue : (r.Width / 2) / Math.Abs(ux);
            var ty = Math.Abs(uy) < 1e-9 ? double.MaxValue : (r.Height / 2) / Math.Abs(uy);
            tb = Math.Min(tx, ty);
        }
        if (len <= tb + 1) return null; // tip inside the body
        var inset = Math.Min(tb * 0.25, 12);
        var bx = c.X + ux * (tb - inset);
        var by = c.Y + uy * (tb - inset);
        // Base direction: perpendicular to the ray, widened a little to stay on the border for rectangles.
        var px = -uy;
        var py = ux;
        var half = Math.Max(2, t.Width / 2);
        var b1 = new SKPoint((float)(bx + px * half), (float)(by + py * half));
        var b2 = new SKPoint((float)(bx - px * half), (float)(by - py * half));
        var tip = t.Tip.ToSk();
        var p = new SKPath();
        p.MoveTo(b1);
        if (t.Curved)
        {
            var mx = (float)((bx + t.Tip.X) / 2);
            var my = (float)((by + t.Tip.Y) / 2);
            var bend = (float)(Math.Min(len, 200) * 0.18);
            p.QuadTo(mx + (float)px * bend, my + (float)py * bend, tip.X, tip.Y);
            p.QuadTo(mx - (float)px * bend * 0.1f, my - (float)py * bend * 0.1f, b2.X, b2.Y);
        }
        else
        {
            p.LineTo(tip);
            p.LineTo(b2);
        }
        p.Close();
        return p;
    }

    private static void Poly(SKPath path, SKRect r, (float X, float Y)[] unit)
    {
        for (var i = 0; i < unit.Length; i++)
        {
            var x = r.Left + unit[i].X * r.Width;
            var y = r.Top + unit[i].Y * r.Height;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
    }

    /// <summary>Boolean union; disposes <paramref name="a"/> and returns the merged path (falls back to <paramref name="a"/> on failure).</summary>
    internal static SKPath Union(SKPath a, SKPath b)
    {
        var res = a.Op(b, SKPathOp.Union);
        if (res is null) { a.AddPath(b); return a; }
        a.Dispose();
        return res;
    }
}
