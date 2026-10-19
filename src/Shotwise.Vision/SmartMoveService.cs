using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// UI component detection for Smart Move (VIS-03). Pipeline: L*a*b* neighbour colour-difference map (edge where
/// delta-E &gt; T) -&gt; morphological close -&gt; contour tree -&gt; filled region masks -&gt; size/aspect filters and
/// nested-region suppression. <c>tolerance</c> is 0..1 (T = 4 + 36 * tolerance delta-E); <see cref="AutoToleranceAsync"/>
/// derives it from the noise level of the image. Returned masks are canvas-sized (count is capped to bound memory).
/// </summary>
public sealed class SmartMoveService : ISmartMoveService
{
    private const int MinSide = 5;
    private const double MaxAreaFraction = 0.80;
    private const double MaxAspect = 60;
    private const long MaskMemoryBudget = 400_000_000;

    private readonly ILogger<SmartMoveService> _log;

    public SmartMoveService(ILogger<SmartMoveService> log) => _log = log;

    public Task<double> AutoToleranceAsync(BgraImage img, CancellationToken ct)
        => Task.Run(() =>
        {
            using var bgr = CvInterop.ToBgr(img);
            using var lab = CvInterop.ToLab32(bgr);
            lab.GetArray(out Vec3f[] px);
            var diffs = EdgeStrength(px, img.Width, img.Height, ct);

            // Histogram of the small (noise-like) non-zero neighbour differences.
            var hist = new int[25];
            var total = 0;
            foreach (var d in diffs)
            {
                if (d <= 0.25f || d > 6f) continue;
                hist[(int)(d / 0.25f)]++;
                total++;
            }
            double noise = 1;
            if (total > 0)
            {
                var acc = 0;
                for (var i = 0; i < hist.Length; i++) { acc += hist[i]; if (acc >= total / 2) { noise = (i + 0.5) * 0.25; break; } }
            }
            var t = Math.Clamp(noise * 3 + 4, 4, 22);
            var tol = (t - 4) / 36.0;
            _log.LogDebug("Smart Move auto tolerance: noise {Noise:F2} -> T {T:F1} -> {Tol:F3}", noise, t, tol);
            return tol;
        }, ct);

    public Task<IReadOnlyList<SmartComponent>> DetectAsync(BgraImage img, double tolerance, CancellationToken ct)
        => Task.Run<IReadOnlyList<SmartComponent>>(() => Detect(img, tolerance, ct), ct);

    private IReadOnlyList<SmartComponent> Detect(BgraImage img, double tolerance, CancellationToken ct)
    {
        var w = img.Width;
        var h = img.Height;
        tolerance = Math.Clamp(tolerance, 0, 1);
        var threshold = (float)(4 + 36 * tolerance);

        using var bgr = CvInterop.ToBgr(img);
        using var lab = CvInterop.ToLab32(bgr);
        lab.GetArray(out Vec3f[] px);
        var strength = EdgeStrength(px, w, h, ct);

        var edgeBytes = new byte[w * h];
        for (var i = 0; i < edgeBytes.Length; i++) if (strength[i] > threshold) edgeBytes[i] = 255;
        using var edges = new Mat(h, w, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(edgeBytes, 0, edges.Data, edgeBytes.Length);

        var ks = 2 * (int)Math.Round(1 + tolerance * 3) + 1;               // 3..9
        using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ks, ks)))
            Cv2.MorphologyEx(edges, edges, MorphTypes.Close, k);
        ct.ThrowIfCancellationRequested();

        Cv2.FindContours(edges, out var contours, out var hier, RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return Array.Empty<SmartComponent>();

        // depth (parity) of every contour: even = outer boundary of foreground, odd = hole boundary.
        var depth = new int[contours.Length];
        for (var i = 0; i < contours.Length; i++)
        {
            var d = 0;
            for (var p = hier[i].Parent; p >= 0; p = hier[p].Parent) d++;
            depth[i] = d;
        }

        var imgArea = (double)w * h;
        var info = new Dictionary<int, (Rect Box, double Area)>();
        for (var i = 0; i < contours.Length; i++)
        {
            if (depth[i] % 2 != 0) continue;
            var box = Cv2.BoundingRect(contours[i]);
            if (box.Width < MinSide || box.Height < MinSide) continue;
            var aspect = (double)Math.Max(box.Width, box.Height) / Math.Min(box.Width, box.Height);
            if (aspect > MaxAspect) continue;
            var area = Math.Max(Cv2.ContourArea(contours[i]), 1);
            if (area < MinSide * MinSide) continue;
            if ((double)box.Width * box.Height > MaxAreaFraction * imgArea) continue;
            info[i] = (box, (double)box.Width * box.Height);
        }

        // nested-region suppression: a container is kept only when it has a visible "body" of its own.
        var keep = new List<int>();
        foreach (var (i, v) in info)
        {
            var childArea = 0.0;
            var hasChildren = false;
            for (var hole = hier[i].Child; hole >= 0; hole = hier[hole].Next)
                for (var ch = hier[hole].Child; ch >= 0; ch = hier[ch].Next)
                    if (info.TryGetValue(ch, out var cv)) { childArea += cv.Area; hasChildren = true; }
            if (!hasChildren || (v.Area - childArea) / v.Area > 0.35) keep.Add(i);
        }

        var maxCount = (int)Math.Clamp(MaskMemoryBudget / Math.Max(1, (long)w * h), 8, 512);
        var ordered = keep.OrderByDescending(i => info[i].Area).Take(maxCount).ToList();

        var result = new List<SmartComponent>(ordered.Count);
        foreach (var i in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var box = info[i].Box;
            using var local = new Mat(box.Height, box.Width, MatType.CV_8UC1, Scalar.All(0));
            Cv2.DrawContours(local, new[] { contours[i] }, 0, Scalar.All(255), -1, LineTypes.Link8, null, int.MaxValue, new Point(-box.X, -box.Y));
            var lb = CvInterop.ToBytes(local);
            var mask = new Mask(w, h);
            for (var y = 0; y < box.Height; y++)
                Buffer.BlockCopy(lb, y * box.Width, mask.Data, (box.Y + y) * w + box.X, box.Width);
            result.Add(new SmartComponent(Guid.NewGuid(), new RectI(box.X, box.Y, box.Width, box.Height), mask));
        }
        _log.LogDebug("Smart Move: {Count} components (T={T:F1})", result.Count, threshold);
        return result;
    }

    /// <summary>Per pixel max of the L*a*b* distance to its right and lower neighbour.</summary>
    private static float[] EdgeStrength(Vec3f[] px, int w, int h, CancellationToken ct)
    {
        var s = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var best = 0f;
                if (x < w - 1) best = Dist(px[i], px[i + 1]);
                if (y < h - 1) { var d = Dist(px[i], px[i + w]); if (d > best) best = d; }
                s[i] = best;
            }
        }
        return s;
    }

    private static float Dist(Vec3f a, Vec3f b)
    {
        float dl = a.Item0 - b.Item0, da = a.Item1 - b.Item1, db = a.Item2 - b.Item2;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }
}
