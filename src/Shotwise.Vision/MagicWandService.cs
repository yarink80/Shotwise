using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// Magic wand (VIS-06): colour distance (CIE76 delta-E in L*a*b*) to the seed pixel, contiguous (4-connected flood fill) or global.
/// <c>tolerance</c> is 0..1 where 1 = delta-E 100 (black vs white); values &gt; 1 are read as percent (0..100).
/// </summary>
public sealed class MagicWandService : IMagicWandService
{
    public Mask Select(BgraImage img, PointI seed, double tolerance, bool contiguous)
    {
        ArgumentNullException.ThrowIfNull(img);
        var mask = new Mask(img.Width, img.Height);
        if ((uint)seed.X >= (uint)img.Width || (uint)seed.Y >= (uint)img.Height) return mask;

        if (tolerance > 1) tolerance /= 100.0;
        var maxDe = Math.Clamp(tolerance, 0, 1) * 100.0;
        var maxDe2 = maxDe * maxDe;

        using var bgr = CvInterop.ToBgr(img);
        using var lab = CvInterop.ToLab32(bgr);
        lab.GetArray(out Vec3f[] px);
        var w = img.Width;
        var h = img.Height;
        var s = px[seed.Y * w + seed.X];

        bool Match(int i)
        {
            double dl = px[i].Item0 - s.Item0, da = px[i].Item1 - s.Item1, db = px[i].Item2 - s.Item2;
            return dl * dl + da * da + db * db <= maxDe2;
        }

        var data = mask.Data;
        if (!contiguous)
        {
            for (var i = 0; i < data.Length; i++) if (Match(i)) data[i] = 255;
            return mask;
        }

        var stack = new Stack<int>();
        var start = seed.Y * w + seed.X;
        data[start] = 255;
        stack.Push(start);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            int x = i % w, y = i / w;
            if (x > 0 && data[i - 1] == 0 && Match(i - 1)) { data[i - 1] = 255; stack.Push(i - 1); }
            if (x < w - 1 && data[i + 1] == 0 && Match(i + 1)) { data[i + 1] = 255; stack.Push(i + 1); }
            if (y > 0 && data[i - w] == 0 && Match(i - w)) { data[i - w] = 255; stack.Push(i - w); }
            if (y < h - 1 && data[i + w] == 0 && Match(i + w)) { data[i + w] = 255; stack.Push(i + w); }
        }
        return mask;
    }

    /// <summary>Closes pinholes and removes specks/spurs of the given radius (elliptical structuring element).</summary>
    public Mask Refine(Mask m, int radius)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (radius <= 0) return m.Clone();
        using var src = CvInterop.MaskToMat(m);
        using var bin = new Mat();
        Cv2.Threshold(src, bin, 127, 255, ThresholdTypes.Binary);
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * radius + 1, 2 * radius + 1));
        using var closed = new Mat();
        Cv2.MorphologyEx(bin, closed, MorphTypes.Close, k);
        using var opened = new Mat();
        Cv2.MorphologyEx(closed, opened, MorphTypes.Open, k);
        return CvInterop.MatToMask(opened);
    }
}
