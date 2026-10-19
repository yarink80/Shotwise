using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>Procedurally drawn mouse-cursor glyphs on a 24x24 design grid (our own artwork, no system bitmaps).</summary>
internal static class CursorGlyphs
{
    public const float Grid = 24f;

    /// <summary>Box the glyph occupies: the annotation box, or a natural 24px*Scale square at Start when degenerate.</summary>
    public static Rect2 Box(CursorAnnotation cu)
    {
        var r = Rect2.FromPoints(cu.Start, cu.End);
        if (r.Width < 2 || r.Height < 2)
        {
            var s = Grid * Math.Max(0.1, cu.Scale);
            return new Rect2(cu.Start.X, cu.Start.Y, s, s);
        }
        return r;
    }

    public static SKPath Build(CursorKind kind, SKRect box)
    {
        var k = Math.Min(box.Width, box.Height) / Grid;
        var ox = box.Left;
        var oy = box.Top;
        SKPoint P(float x, float y) => new(ox + x * k, oy + y * k);
        var path = new SKPath { FillType = SKPathFillType.Winding };
        void Poly(params float[] xy)
        {
            path.MoveTo(P(xy[0], xy[1]));
            for (var i = 2; i < xy.Length; i += 2) path.LineTo(P(xy[i], xy[i + 1]));
            path.Close();
        }
        void RRect(float x, float y, float w, float h, float rad)
        {
            path.AddRoundRect(new SKRect(ox + x * k, oy + y * k, ox + (x + w) * k, oy + (y + h) * k), rad * k, rad * k);
        }
        switch (kind)
        {
            case CursorKind.Arrow:
                Poly(1, 1, 1, 20, 5.6f, 15.6f, 9.2f, 23, 12.2f, 21.7f, 8.7f, 14.4f, 15, 14.4f);
                break;
            case CursorKind.Hand:
            {
                var p = new SKPath();
                p.AddRoundRect(new SKRect(ox + 8 * k, oy + 1 * k, ox + 11.6f * k, oy + 14 * k), 1.8f * k, 1.8f * k);
                var res = p;
                foreach (var (x, y, h) in new[] { (11.4f, 8f, 8f), (14.4f, 9f, 7f), (17.4f, 10.2f, 6f) })
                {
                    using var f = new SKPath();
                    f.AddRoundRect(new SKRect(ox + x * k, oy + y * k, ox + (x + 3.2f) * k, oy + (y + h + 4) * k), 1.6f * k, 1.6f * k);
                    res = CalloutGeometry.Union(res, f);
                }
                using var palm = new SKPath();
                palm.AddRoundRect(new SKRect(ox + 6f * k, oy + 12f * k, ox + 20.6f * k, oy + 23f * k), 4.5f * k, 4.5f * k);
                res = CalloutGeometry.Union(res, palm);
                using var thumb = new SKPath();
                thumb.MoveTo(P(7f, 14f));
                thumb.LineTo(P(3.2f, 11f));
                thumb.LineTo(P(1.8f, 12.6f));
                thumb.LineTo(P(6.5f, 19f));
                thumb.Close();
                res = CalloutGeometry.Union(res, thumb);
                path.Dispose();
                return res;
            }
            case CursorKind.IBeam:
                RRect(7, 2, 10, 2.2f, 0.4f);
                RRect(10.9f, 2, 2.2f, 20, 0.4f);
                RRect(7, 19.8f, 10, 2.2f, 0.4f);
                break;
            case CursorKind.Wait:
                Poly(5, 1.5f, 19, 1.5f, 19, 4, 13.2f, 12, 19, 20, 19, 22.5f, 5, 22.5f, 5, 20, 10.8f, 12, 5, 4);
                break;
            case CursorKind.Cross:
                RRect(11, 2, 2, 20, 0.5f);
                RRect(2, 11, 20, 2, 0.5f);
                break;
            case CursorKind.Move:
                RRect(11, 5, 2, 14, 0.5f);
                RRect(5, 11, 14, 2, 0.5f);
                Poly(12, 0.5f, 16, 5, 8, 5);
                Poly(12, 23.5f, 16, 19, 8, 19);
                Poly(0.5f, 12, 5, 8, 5, 16);
                Poly(23.5f, 12, 19, 8, 19, 16);
                break;
            default:
                Poly(1, 1, 1, 20, 5.6f, 15.6f, 15, 14.4f);
                break;
        }
        return path;
    }
}
