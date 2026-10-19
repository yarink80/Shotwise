using OpenCvSharp;

namespace Shotwise.Vision.Stitching;

/// <summary>
/// Translation estimation between two consecutive scroll frames (vertical axis): Hanning-windowed phase correlation yields
/// several candidate shifts, each verified by normalised cross-correlation of the overlap; a template-matching search is the
/// fallback when the correlation peaks do not verify (repeating content, sticky regions).
/// </summary>
internal static class ScrollRegistration
{
    public const double MinScore = 0.92;
    private const int MaxPeaks = 5;

    /// <summary>
    /// Static top/bottom bands between two frames (rows whose content did not change while the rest scrolled).
    /// Returns null when the frames are identical (no movement) or the body does not change.
    /// </summary>
    public static (int Top, int Bottom)? DetectStaticBands(Mat prevGray, Mat currGray)
    {
        using var diff = new Mat();
        Cv2.Absdiff(prevGray, currGray, diff);
        using var rowMean = new Mat();
        Cv2.Reduce(diff, rowMean, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
        rowMean.GetArray(out float[] rm);
        var h = rm.Length;
        if (rm.Max() < 0.5f) return null;

        var maxBand = (int)(h * 0.35);
        var top = 0;
        while (top < maxBand && rm[top] < 1.0f) top++;
        var bottom = 0;
        while (bottom < maxBand && rm[h - 1 - bottom] < 1.0f) bottom++;
        if (top < 3) top = 0;
        if (bottom < 3) bottom = 0;
        // the changing part must still be a meaningful body
        if (h - top - bottom < Math.Max(32, h / 4)) return null;
        var bodyMax = 0f;
        for (var i = top; i < h - bottom; i++) bodyMax = Math.Max(bodyMax, rm[i]);
        return bodyMax < 1.5f ? null : (top, bottom);
    }

    /// <summary>Finds dy with prev(y + dy) == curr(y); dy &lt; 0 means the content moved the other way.</summary>
    public static bool TryRegister(Mat prev, Mat curr, out int dy, out double score)
    {
        dy = 0;
        score = 0;
        var h = prev.Rows;
        var w = prev.Cols;
        if (curr.Rows != h || curr.Cols != w || h < 32 || w < 16) return false;
        var minOverlap = Math.Max(12, (int)(h * 0.08));
        var maxShift = h - minOverlap;

        var cands = new List<int>();
        void AddCand(int c)
        {
            if (c != 0 && Math.Abs(c) <= maxShift && !cands.Contains(c)) cands.Add(c);
        }

        // 1. phase correlation (half resolution for large frames)
        var s = (long)w * h > 1_000_000 ? 0.5 : 1.0;
        Mat a = prev, b = curr;
        Mat? sa = null, sb = null;
        try
        {
            if (s < 1)
            {
                sa = new Mat(); sb = new Mat();
                var size = new Size((int)(w * s), (int)(h * s));
                Cv2.Resize(prev, sa, size, 0, 0, InterpolationFlags.Area);
                Cv2.Resize(curr, sb, size, 0, 0, InterpolationFlags.Area);
                a = sa; b = sb;
            }
            foreach (var (_, py) in PhasePeaks(a, b, MaxPeaks))
            {
                var c = (int)Math.Round(py / s);
                AddCand(c);
                AddCand(-c);
            }
        }
        finally { sa?.Dispose(); sb?.Dispose(); }

        var best = 0;
        var bestScore = -1.0;
        foreach (var c in cands)
        {
            var sc = Ncc(prev, curr, c, minOverlap);
            if (sc > bestScore) { bestScore = sc; best = c; }
        }

        // 2. template matching fallback
        if (bestScore < MinScore)
        {
            foreach (var c in TemplateCandidates(prev, curr, maxShift))
            {
                var sc = Ncc(prev, curr, c, minOverlap);
                if (sc > bestScore) { bestScore = sc; best = c; }
            }
        }
        if (bestScore < 0) return false;

        // 3. refine +-2 px around the winner
        for (var d = -2; d <= 2; d++)
        {
            if (d == 0) continue;
            var c = best + d;
            if (c == 0 || Math.Abs(c) > maxShift) continue;
            var sc = Ncc(prev, curr, c, minOverlap);
            if (sc > bestScore + 1e-4) { bestScore = sc; best = c; }
        }

        dy = best;
        score = bestScore;
        return bestScore >= MinScore;
    }

    /// <summary>Normalised cross-correlation of the overlap implied by <paramref name="dy"/> (-1 when not evaluable).</summary>
    public static double Ncc(Mat prev, Mat curr, int dy, int minOverlap)
    {
        var h = prev.Rows;
        var m = Math.Abs(dy);
        if (m >= h || h - m < minOverlap) return -1;
        using var pa = dy > 0 ? prev.RowRange(dy, h) : prev.RowRange(0, h - m);
        using var cb = dy > 0 ? curr.RowRange(0, h - dy) : curr.RowRange(m, h);
        Cv2.MeanStdDev(pa, out _, out var sdA);
        Cv2.MeanStdDev(cb, out _, out var sdB);
        if (sdA.Val0 < 1.0 && sdB.Val0 < 1.0) return 0.5;              // flat overlap: no evidence either way
        if (sdA.Val0 < 0.5 || sdB.Val0 < 0.5) return 0;
        using var res = new Mat();
        Cv2.MatchTemplate(pa, cb, res, TemplateMatchModes.CCoeffNormed);
        var v = res.At<float>(0, 0);
        return float.IsNaN(v) ? 0 : v;
    }

    private static IEnumerable<int> TemplateCandidates(Mat prev, Mat curr, int maxShift)
    {
        var h = prev.Rows;
        var th = Math.Clamp(h / 5, 24, 160);
        if (th >= h) yield break;
        foreach (var r0 in new[] { 0, (h - th) / 2, h - th })
        {
            using var templ = curr.RowRange(r0, r0 + th);
            Cv2.MeanStdDev(templ, out _, out var sd);
            if (sd.Val0 < 3) continue;
            using var res = new Mat();
            Cv2.MatchTemplate(prev, templ, res, TemplateMatchModes.CCoeffNormed);
            for (var k = 0; k < 3; k++)
            {
                Cv2.MinMaxLoc(res, out double _, out double maxV, out Point _, out Point maxLoc);
                if (maxV < 0.6) break;
                var c = maxLoc.Y - r0;
                if (c != 0 && Math.Abs(c) <= maxShift) yield return c;
                SuppressAround(res, maxLoc, 6);
            }
        }
    }

    /// <summary>Top correlation peaks (signed shifts) between two equally sized 8-bit grey images.</summary>
    public static List<(double Dx, double Dy)> PhasePeaks(Mat a8, Mat b8, int count)
    {
        var w = a8.Cols;
        var h = a8.Rows;
        var pw = Cv2.GetOptimalDFTSize(w);
        var ph = Cv2.GetOptimalDFTSize(h);

        using var win = new Mat();
        Cv2.CreateHanningWindow(win, new Size(w, h), MatType.CV_32F);
        using var fa = Prepare(a8, win, pw, ph);
        using var fb = Prepare(b8, win, pw, ph);
        using var cross = new Mat();
        Cv2.MulSpectrums(fa, fb, cross, DftFlags.None, true);

        var planes = Cv2.Split(cross);
        try
        {
            using var mag = new Mat();
            Cv2.Magnitude(planes[0], planes[1], mag);
            Cv2.Add(mag, Scalar.All(1e-6), mag);
            Cv2.Divide(planes[0], mag, planes[0]);
            Cv2.Divide(planes[1], mag, planes[1]);
            using var normalised = new Mat();
            Cv2.Merge(planes, normalised);
            using var r = new Mat();
            Cv2.Dft(normalised, r, DftFlags.Inverse | DftFlags.Scale | DftFlags.RealOutput);

            var peaks = new List<(double, double)>();
            for (var i = 0; i < count; i++)
            {
                Cv2.MinMaxLoc(r, out double _, out double maxV, out Point _, out Point loc);
                if (maxV <= 0) break;
                var x = loc.X > pw / 2 ? loc.X - pw : loc.X;
                var y = loc.Y > ph / 2 ? loc.Y - ph : loc.Y;
                peaks.Add((x, y));
                SuppressAround(r, loc, 5);
            }
            return peaks;
        }
        finally { foreach (var p in planes) p.Dispose(); }
    }

    private static Mat Prepare(Mat gray8, Mat window, int pw, int ph)
    {
        using var f = new Mat();
        gray8.ConvertTo(f, MatType.CV_32F);
        var mean = Cv2.Mean(f).Val0;
        Cv2.Subtract(f, Scalar.All(mean), f);
        Cv2.Multiply(f, window, f);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(f, padded, 0, ph - f.Rows, 0, pw - f.Cols, BorderTypes.Constant, Scalar.All(0));
        var spec = new Mat();
        Cv2.Dft(padded, spec, DftFlags.ComplexOutput);
        return spec;
    }

    private static void SuppressAround(Mat m, Point p, int radius)
    {
        var x0 = Math.Max(0, p.X - radius); var y0 = Math.Max(0, p.Y - radius);
        var x1 = Math.Min(m.Cols, p.X + radius + 1); var y1 = Math.Min(m.Rows, p.Y + radius + 1);
        using var roi = new Mat(m, new Rect(x0, y0, x1 - x0, y1 - y0));
        roi.SetTo(Scalar.All(0));
    }
}
