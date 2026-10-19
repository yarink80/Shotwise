using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// Auto Simplify (VIS-04). OCR-free: text lines come from a morphological-gradient + horizontal closing detector, UI blocks
/// from Canny + contour analysis (rectangle / rounded rectangle / ellipse), colours from k-means dominant colours mapped to
/// the supplied palette. <c>detail</c> 1..10 scales the minimum feature size, the merge distance and the result cap.
/// </summary>
public sealed class SimplifyService : ISimplifyService
{
    private readonly ILogger<SimplifyService> _log;

    public SimplifyService(ILogger<SimplifyService> log) => _log = log;

    public Task<Palette> DominantColorsAsync(BgraImage img, int k, CancellationToken ct)
        => Task.Run(() => DominantColors(img, Math.Clamp(k, 1, 32)), ct);

    public Task<IReadOnlyList<SimplifyShapeAnnotation>> GenerateAsync(BgraImage img, int detail, Palette palette, CancellationToken ct)
        => Task.Run(() => Generate(img, Math.Clamp(detail, 1, 10), palette, ct), ct);

    // ---- dominant colours ----------------------------------------------------------------

    internal static Palette DominantColors(BgraImage img, int k)
    {
        using var bgr = CvInterop.ToBgr(img);
        using var small = new Mat();
        var scale = Math.Min(1.0, 160.0 / Math.Max(bgr.Cols, bgr.Rows));
        Cv2.Resize(bgr, small, new Size(Math.Max(1, (int)(bgr.Cols * scale)), Math.Max(1, (int)(bgr.Rows * scale))), 0, 0, InterpolationFlags.Area);
        var n = small.Rows * small.Cols;
        k = Math.Min(k, n);
        using var f = new Mat();
        small.ConvertTo(f, MatType.CV_32FC3);
        using var samples = f.Reshape(1, n);
        using var labels = new Mat();
        using var centers = new Mat();
        Cv2.Kmeans(samples, k, labels, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 20, 0.5), 3, KMeansFlags.PpCenters, centers);
        labels.GetArray(out int[] lbl);
        var counts = new int[k];
        foreach (var l in lbl) counts[l]++;
        var order = Enumerable.Range(0, k).Where(i => counts[i] > 0).OrderByDescending(i => counts[i]).ToArray();
        var pal = new Palette { Name = "Image colors" };
        foreach (var i in order)
        {
            var b = centers.At<float>(i, 0); var g = centers.At<float>(i, 1); var r = centers.At<float>(i, 2);
            pal.Colors.Add(Argb.FromRgb(ToByte(r), ToByte(g), ToByte(b)));
        }
        return pal;
    }

    // ---- generation ----------------------------------------------------------------------

    private sealed record TextBlock(Rect Box, Argb Color);
    private sealed record RectShape(Rect Box, SimplifyShapeKind Kind, Argb Color);

    private IReadOnlyList<SimplifyShapeAnnotation> Generate(BgraImage img, int detail, Palette palette, CancellationToken ct)
    {
        var t = (detail - 1) / 9.0;
        var colors = palette.Colors.Count > 0 ? palette.Colors : DominantColors(img, 6).Colors;
        var lab = colors.Select(ToLab).ToArray();

        using var bgr = CvInterop.ToBgr(img);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        ct.ThrowIfCancellationRequested();

        var texts = DetectTextBlocks(bgr, gray, t, ct);
        var rects = DetectRects(bgr, gray, texts, t, ct);
        _log.LogDebug("Simplify detail {Detail}: {Rects} rects, {Text} text lines", detail, rects.Count, texts.Count);

        var result = new List<SimplifyShapeAnnotation>();
        // containers first (largest first) so smaller shapes and text bars paint on top
        foreach (var r in rects.OrderByDescending(r => (long)r.Box.Width * r.Box.Height))
            result.Add(Make(r.Kind, r.Box, r.Color, colors, lab));
        var aligns = ComputeAlignments(texts);
        for (var i = 0; i < texts.Count; i++)
            result.Add(Make(aligns[i], texts[i].Box, texts[i].Color, colors, lab));
        return result;
    }

    private static SimplifyShapeAnnotation Make(SimplifyShapeKind kind, Rect box, Argb src, IReadOnlyList<Argb> colors, (double L, double A, double B)[] lab)
    {
        var idx = Nearest(src, lab);
        return new SimplifyShapeAnnotation
        {
            Shape = kind,
            PaletteIndex = idx,
            Start = new Point2(box.X, box.Y),
            End = new Point2(box.Right, box.Bottom),
            Fill = FillStyle.Solid(colors[idx]),
        };
    }

    private List<TextBlock> DetectTextBlocks(Mat bgr, Mat gray, double t, CancellationToken ct)
    {
        using var grad = new Mat();
        using (var k3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3)))
            Cv2.MorphologyEx(gray, grad, MorphTypes.Gradient, k3);
        using var bin = new Mat();
        Cv2.Threshold(grad, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        // suppress very weak responses (flat images make Otsu pick noise)
        Cv2.MinMaxLoc(grad, out double _, out double gmax);
        if (gmax < 40) return new List<TextBlock>();

        var kw = (int)Math.Round(Lerp(21, 9, t)) | 1;
        using var merged = new Mat();
        using (var kh = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kw, 3)))
            Cv2.MorphologyEx(bin, merged, MorphTypes.Close, kh);
        ct.ThrowIfCancellationRequested();

        Cv2.FindContours(merged, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var minH = (int)Math.Round(Lerp(9, 6, t));
        var minW = (int)Math.Round(Lerp(24, 10, t));
        var list = new List<TextBlock>();
        foreach (var c in contours)
        {
            var box = Cv2.BoundingRect(c);
            if (box.Height < minH || box.Height > 64 || box.Width < minW) continue;
            if (box.Width < 1.6 * box.Height) continue;
            // text has a moderate density of gradient pixels; outlines of big boxes / flat bars do not
            using var roi = new Mat(bin, box);
            var density = Cv2.CountNonZero(roi) / (double)(box.Width * box.Height);
            if (density < 0.10 || density > 0.75) continue;
            // trim to the ink rows and give a little padding for the bar
            var padded = Inflate(box, 0, bgr.Size());
            using var crop = new Mat(bgr, padded);
            var style = TextEditService.Analyze(crop);
            var color = style?.TextColor ?? Argb.Gray;
            list.Add(new TextBlock(padded, color));
        }
        return list.OrderBy(b => b.Box.Y).ThenBy(b => b.Box.X).ToList();
    }

    private List<RectShape> DetectRects(Mat bgr, Mat gray, List<TextBlock> texts, double t, CancellationToken ct)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(3, 3), 0);
        using var edges = new Mat();
        Cv2.Canny(blur, edges, 20, 60);
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3))) Cv2.MorphologyEx(edges, edges, MorphTypes.Close, k);
        ct.ThrowIfCancellationRequested();

        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var minSide = (int)Math.Round(Lerp(40, 12, t));
        var maxCount = (int)Math.Round(Lerp(12, 150, t));
        var cands = new List<(Rect Box, SimplifyShapeKind Kind, double Area)>();
        foreach (var c in contours)
        {
            var box = Cv2.BoundingRect(c);
            if (box.Width < minSide || box.Height < minSide) continue;
            if (box.Width >= w * 0.98 && box.Height >= h * 0.98) continue;
            var bboxArea = (double)box.Width * box.Height;
            var area = Cv2.ContourArea(c);
            if (area < 0.5 * bboxArea) continue;                            // thin outlines / irregular shapes
            var per = Cv2.ArcLength(c, true);
            var rectness = area / bboxArea;
            var circ = per > 0 ? 4 * Math.PI * area / (per * per) : 0;
            var aspect = (double)Math.Max(box.Width, box.Height) / Math.Min(box.Width, box.Height);
            SimplifyShapeKind kind;
            if (circ > 0.85 && aspect < 1.15 && rectness is > 0.70 and < 0.86) kind = SimplifyShapeKind.Ellipse;
            else if (rectness >= 0.975) kind = SimplifyShapeKind.Rectangle;
            else if (rectness >= 0.86) kind = SimplifyShapeKind.RoundedRectangle;
            else continue;
            // text lines are handled separately: skip rectangles that are just the text block
            if (texts.Any(tb => IoU(tb.Box, box) > 0.5)) continue;
            cands.Add((box, kind, bboxArea));
        }

        // de-duplicate (outer/inner edge of the same border) keeping the larger
        var kept = new List<(Rect Box, SimplifyShapeKind Kind, double Area)>();
        foreach (var c in cands.OrderByDescending(c => c.Area))
        {
            if (kept.Any(k => IoU(k.Box, c.Box) > 0.80)) continue;
            kept.Add(c);
            if (kept.Count >= maxCount) break;
        }

        var result = new List<RectShape>(kept.Count);
        foreach (var k in kept)
            result.Add(new RectShape(k.Box, k.Kind, FillColor(bgr, k.Box)));
        return result;
    }

    /// <summary>Median colour of the box interior, ignoring a border and (via median) sparse text pixels.</summary>
    private static Argb FillColor(Mat bgr, Rect box)
    {
        var inset = Math.Max(2, Math.Min(box.Width, box.Height) / 8);
        var inner = new Rect(box.X + inset, box.Y + inset, Math.Max(1, box.Width - 2 * inset), Math.Max(1, box.Height - 2 * inset));
        inner = inner.Intersect(new Rect(0, 0, bgr.Cols, bgr.Rows));
        if (inner.Width <= 0 || inner.Height <= 0) inner = box;
        using var roi = new Mat(bgr, inner);
        var bytes = CvInterop.ToBytes(roi);
        var n = inner.Width * inner.Height;
        var med = new byte[3];
        var chan = new byte[n];
        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < n; i++) chan[i] = bytes[i * 3 + c];
            Array.Sort(chan);
            med[c] = chan[n / 2];
        }
        return Argb.FromRgb(med[2], med[1], med[0]);
    }

    private static SimplifyShapeKind[] ComputeAlignments(List<TextBlock> texts)
    {
        var res = Enumerable.Repeat(SimplifyShapeKind.TextLeft, texts.Count).ToArray();
        const int tol = 4;
        for (var i = 0; i < texts.Count; i++)
        {
            var a = texts[i].Box;
            var near = new List<Rect>();
            for (var j = 0; j < texts.Count; j++)
            {
                if (i == j) continue;
                var b = texts[j].Box;
                if (Math.Abs(b.Y - a.Y) > 4 * a.Height + 8) continue;
                near.Add(b);
            }
            if (near.Count == 0) continue;
            var left = near.Any(b => Math.Abs(b.Left - a.Left) <= tol);
            var right = near.Any(b => Math.Abs(b.Right - a.Right) <= tol);
            var center = near.Any(b => Math.Abs((b.Left + b.Right) / 2.0 - (a.Left + a.Right) / 2.0) <= tol);
            if (left) res[i] = SimplifyShapeKind.TextLeft;
            else if (right) res[i] = SimplifyShapeKind.TextRight;
            else if (center) res[i] = SimplifyShapeKind.TextCenter;
        }
        return res;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static Rect Inflate(Rect r, int d, Size bounds)
        => new Rect(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d).Intersect(new Rect(0, 0, bounds.Width, bounds.Height));

    private static double IoU(Rect a, Rect b)
    {
        var i = a.Intersect(b);
        if (i.Width <= 0 || i.Height <= 0) return 0;
        double inter = (double)i.Width * i.Height;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static (double L, double A, double B) ToLab(Argb c)
    {
        using var m = new Mat(1, 1, MatType.CV_8UC3, new Scalar(c.B, c.G, c.R));
        using var lab = CvInterop.ToLab32(m);
        var v = lab.At<Vec3f>(0, 0);
        return (v.Item0, v.Item1, v.Item2);
    }

    private static int Nearest(Argb c, (double L, double A, double B)[] palette)
    {
        var s = ToLab(c);
        var best = 0;
        var bd = double.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            double dl = palette[i].L - s.L, da = palette[i].A - s.A, db = palette[i].B - s.B;
            var d = dl * dl + da * da + db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }
}
