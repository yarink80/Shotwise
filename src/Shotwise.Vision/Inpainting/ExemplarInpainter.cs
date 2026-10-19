using OpenCvSharp;
using Shotwise.Vision.Internal;

namespace Shotwise.Vision.Inpainting;

/// <summary>
/// Cheap fills for UI-style backgrounds plus a Criminisi-style exemplar (patch-based) inpainter.
/// All operate on tightly cropped BGR (CV_8UC3) + mask (CV_8UC1, non-zero = hole) matrices and return a new BGR matrix.
/// </summary>
internal static class ExemplarInpainter
{
    /// <summary>Mean absolute Laplacian of the known ring around the hole: low means flat/smooth background.</summary>
    public static double RingRoughness(Mat bgr, Mat hole)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_16S, 1);
        using var ringOuter = new Mat();
        using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        Cv2.Dilate(hole, ringOuter, k);
        using var holeGrow = new Mat();
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(hole, holeGrow, k3);                                  // skip pixels whose Laplacian touches the hole
        using var ring = new Mat();
        Cv2.Subtract(ringOuter, holeGrow, ring);
        var n = Cv2.CountNonZero(ring);
        if (n == 0) return double.MaxValue;
        using var abs = new Mat();
        Cv2.ConvertScaleAbs(lap, abs);
        return Cv2.Mean(abs, ring).Val0;
    }

    /// <summary>
    /// Horizontal/vertical gradient fill (the "Lingo"-style UI filler): every hole pixel is interpolated along its row and along
    /// its column between the nearest known pixels; the shorter span gets more weight. Exact for solid colours and linear gradients.
    /// </summary>
    public static Mat GradientFill(Mat bgr, Mat hole)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        var src = CvInterop.ToBytes(bgr);
        var holeB = CvInterop.ToBytes(hole);
        var dst = (byte[])src.Clone();

        // For every pixel: nearest known to the left/right/up/down (index or -1).
        var left = new int[w * h]; var right = new int[w * h]; var up = new int[w * h]; var down = new int[w * h];
        for (var y = 0; y < h; y++)
        {
            var last = -1;
            for (var x = 0; x < w; x++) { if (holeB[y * w + x] == 0) last = x; left[y * w + x] = last; }
            last = -1;
            for (var x = w - 1; x >= 0; x--) { if (holeB[y * w + x] == 0) last = x; right[y * w + x] = last; }
        }
        for (var x = 0; x < w; x++)
        {
            var last = -1;
            for (var y = 0; y < h; y++) { if (holeB[y * w + x] == 0) last = y; up[y * w + x] = last; }
            last = -1;
            for (var y = h - 1; y >= 0; y--) { if (holeB[y * w + x] == 0) last = y; down[y * w + x] = last; }
        }

        Span<double> hv = stackalloc double[3];
        Span<double> vv = stackalloc double[3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (holeB[i] == 0) continue;
                var l = left[i]; var r = right[i]; var u = up[i]; var d = down[i];
                var hOk = Interp(src, w, y, l, r, x, true, hv, out var hSpan);
                var vOk = Interp(src, w, x, u, d, y, false, vv, out var vSpan);
                for (var c = 0; c < 3; c++)
                {
                    double v;
                    if (hOk && vOk)
                    {
                        double wh = 1.0 / Math.Max(1, hSpan), wv = 1.0 / Math.Max(1, vSpan);
                        v = (hv[c] * wh + vv[c] * wv) / (wh + wv);
                    }
                    else if (hOk) v = hv[c];
                    else if (vOk) v = vv[c];
                    else v = 128;
                    dst[i * 3 + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        return CvInterop.ToMatBgr(dst, w, h);
    }

    private static bool Interp(byte[] src, int w, int fixedCoord, int a, int b, int pos, bool horizontal, Span<double> o, out int span)
    {
        span = 0;
        if (a < 0 && b < 0) return false;
        int Idx(int p) => horizontal ? (fixedCoord * w + p) * 3 : (p * w + fixedCoord) * 3;
        if (a >= 0 && b >= 0)
        {
            span = b - a;
            var t = span == 0 ? 0 : (double)(pos - a) / span;
            var ia = Idx(a); var ib = Idx(b);
            for (var c = 0; c < 3; c++) o[c] = src[ia + c] + (src[ib + c] - src[ia + c]) * t;
            return true;
        }
        var only = a >= 0 ? a : b;
        span = Math.Abs(pos - only) * 2;                                   // one-sided extrapolation is less trustworthy
        var io = Idx(only);
        for (var c = 0; c < 3; c++) o[c] = src[io + c];
        return true;
    }

    /// <summary>
    /// Criminisi-style exemplar inpainting: repeatedly pick the front pixel with the highest priority
    /// (confidence x isophote strength), find the best fully-known source patch (SSD over the known target pixels) and copy it.
    /// Returns null when cancelled-by-limit (hole too large) so callers can fall back.
    /// </summary>
    public static Mat? Exemplar(Mat bgr, Mat hole, int patchRadius, int maxHolePixels, CancellationToken ct)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        var pix = CvInterop.ToBytes(bgr);
        var holeB = CvInterop.ToBytes(hole);
        var known = new bool[w * h];
        var remaining = 0;
        for (var i = 0; i < known.Length; i++) { known[i] = holeB[i] == 0; if (!known[i]) remaining++; }
        if (remaining == 0) return bgr.Clone();
        if (remaining > maxHolePixels) return null;

        var r = patchRadius;
        var conf = new float[w * h];
        for (var i = 0; i < conf.Length; i++) conf[i] = known[i] ? 1f : 0f;
        var gray = new float[w * h];
        void UpdateGray(int i) => gray[i] = 0.114f * pix[i * 3] + 0.587f * pix[i * 3 + 1] + 0.299f * pix[i * 3 + 2];
        for (var i = 0; i < gray.Length; i++) UpdateGray(i);

        // Bounding box of the holes: the front can only live inside it.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (!known[y * w + x]) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }

        // Source patches must be completely known: integral image of "unknown" counts is rebuilt as holes shrink; patches whose
        // pixels were filled later are never used as sources (original known set only).
        var sourceOk = new bool[w * h];
        {
            var originalKnown = (bool[])known.Clone();
            var integ = new int[(w + 1) * (h + 1)];
            for (var y = 0; y < h; y++)
            {
                var rowSum = 0;
                for (var x = 0; x < w; x++)
                {
                    rowSum += originalKnown[y * w + x] ? 0 : 1;
                    integ[(y + 1) * (w + 1) + x + 1] = integ[y * (w + 1) + x + 1] + rowSum;
                }
            }
            for (var y = r; y < h - r; y++)
                for (var x = r; x < w - r; x++)
                {
                    var x0 = x - r; var y0 = y - r; var x1 = x + r + 1; var y1 = y + r + 1;
                    var cnt = integ[y1 * (w + 1) + x1] - integ[y0 * (w + 1) + x1] - integ[y1 * (w + 1) + x0] + integ[y0 * (w + 1) + x0];
                    sourceOk[y * w + x] = cnt == 0;
                }
        }
        var sources = new List<int>();
        for (var i = 0; i < sourceOk.Length; i++) if (sourceOk[i]) sources.Add(i);
        if (sources.Count == 0) return null;
        var stride = sources.Count > 40000 ? 2 : 1;
        if (stride > 1) sources = sources.Where((_, idx) => idx % stride == 0).ToList();
        var srcArr = sources.ToArray();

        var side = 2 * r + 1;
        var front = new List<int>();
        var lockObj = new object();

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            front.Clear();
            for (var y = Math.Max(0, minY - 1); y <= Math.Min(h - 1, maxY + 1); y++)
                for (var x = Math.Max(0, minX - 1); x <= Math.Min(w - 1, maxX + 1); x++)
                {
                    var i = y * w + x;
                    if (known[i]) continue;
                    if ((x > 0 && known[i - 1]) || (x < w - 1 && known[i + 1]) || (y > 0 && known[i - w]) || (y < h - 1 && known[i + w])) front.Add(i);
                }
            if (front.Count == 0) break;

            // priorities
            var bestP = -1.0;
            var bestPix = front[0];
            foreach (var i in front)
            {
                int x = i % w, y = i / w;
                double csum = 0;
                var cnt = 0;
                for (var dy = -r; dy <= r; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (var dx = -r; dx <= r; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        csum += conf[yy * w + xx];
                        cnt++;
                    }
                }
                var c = csum / Math.Max(1, cnt);
                // isophote (perpendicular to image gradient) · normal of the fill front
                double gx = 0, gy = 0, nx = 0, ny = 0;
                if (x > 0 && x < w - 1 && known[i - 1] && known[i + 1]) gx = (gray[i + 1] - gray[i - 1]) / 2.0;
                else if (x < w - 1 && known[i + 1]) gx = gray[i + 1] - gray[i];
                else if (x > 0 && known[i - 1]) gx = gray[i] - gray[i - 1];
                if (y > 0 && y < h - 1 && known[i - w] && known[i + w]) gy = (gray[i + w] - gray[i - w]) / 2.0;
                else if (y < h - 1 && known[i + w]) gy = gray[i + w] - gray[i];
                else if (y > 0 && known[i - w]) gy = gray[i] - gray[i - w];
                nx = (x < w - 1 && known[i + 1] ? 1 : 0) - (x > 0 && known[i - 1] ? 1 : 0);
                ny = (y < h - 1 && known[i + w] ? 1 : 0) - (y > 0 && known[i - w] ? 1 : 0);
                var nl = Math.Sqrt(nx * nx + ny * ny);
                double d = 0.001;
                if (nl > 0) d += Math.Abs((-gy) * (nx / nl) + gx * (ny / nl)) / 255.0;
                var p = c * d;
                if (p > bestP) { bestP = p; bestPix = i; }
            }

            // best matching source patch for the highest-priority pixel
            int tx = bestPix % w, ty = bestPix / w;
            var tList = new List<(int off, int idx)>(side * side);
            for (var dy = -r; dy <= r; dy++)
            {
                var yy = ty + dy;
                if (yy < 0 || yy >= h) continue;
                for (var dx = -r; dx <= r; dx++)
                {
                    var xx = tx + dx;
                    if (xx < 0 || xx >= w) continue;
                    var idx = yy * w + xx;
                    if (known[idx]) tList.Add((dy * w + dx, idx));
                }
            }
            var tOff = tList.Select(t => t.off).ToArray();
            var tIdx = tList.Select(t => t.idx).ToArray();
            var bestCost = long.MaxValue;
            var bestSrc = -1;
            Parallel.For(0, 8, new ParallelOptions { CancellationToken = ct }, part =>
            {
                var localBest = long.MaxValue;
                var localSrc = -1;
                for (var s = part; s < srcArr.Length; s += 8)
                {
                    var c0 = srcArr[s];
                    long cost = 0;
                    for (var k = 0; k < tOff.Length && cost < localBest; k++)
                    {
                        var a = tIdx[k] * 3;
                        var b = (c0 + tOff[k]) * 3;
                        int d0 = pix[a] - pix[b], d1 = pix[a + 1] - pix[b + 1], d2 = pix[a + 2] - pix[b + 2];
                        cost += d0 * d0 + d1 * d1 + d2 * d2;
                    }
                    if (cost < localBest) { localBest = cost; localSrc = c0; }
                }
                lock (lockObj) { if (localSrc >= 0 && (localBest < bestCost || (localBest == bestCost && localSrc < bestSrc))) { bestCost = localBest; bestSrc = localSrc; } }
            });
            if (bestSrc < 0) return null;

            var pc = 0.0;
            {
                double cs = 0; var cn = 0;
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        int xx = tx + dx, yy = ty + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                        cs += conf[yy * w + xx]; cn++;
                    }
                pc = cs / Math.Max(1, cn);
            }
            for (var dy = -r; dy <= r; dy++)
            {
                var yy = ty + dy;
                if (yy < 0 || yy >= h) continue;
                for (var dx = -r; dx <= r; dx++)
                {
                    var xx = tx + dx;
                    if (xx < 0 || xx >= w) continue;
                    var idx = yy * w + xx;
                    if (known[idx]) continue;
                    var sidx = bestSrc + dy * w + dx;
                    pix[idx * 3] = pix[sidx * 3]; pix[idx * 3 + 1] = pix[sidx * 3 + 1]; pix[idx * 3 + 2] = pix[sidx * 3 + 2];
                    UpdateGray(idx);
                    conf[idx] = (float)pc;
                    known[idx] = true;
                    remaining--;
                }
            }
        }
        return CvInterop.ToMatBgr(pix, w, h);
    }
}
