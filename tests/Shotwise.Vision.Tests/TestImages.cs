using OpenCvSharp;
using Shotwise.Core.Imaging;
using Shotwise.Vision.Internal;

namespace Shotwise.Vision.Tests;

/// <summary>Synthetic image builders used across the vision tests.</summary>
internal static class TestImages
{
    public static Scalar Bgr(byte r, byte g, byte b) => new(b, g, r);

    /// <summary>Solid canvas as a Mat (BGR).</summary>
    public static Mat Canvas(int w, int h, Scalar color)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, color);
        return m;
    }

    public static BgraImage ToImage(Mat bgr) => CvInterop.FromMat(bgr);

    /// <summary>A tall "web page" with irregular content (so every strip is unique) used for stitching tests.</summary>
    public static Mat TexturedPage(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var m = new Mat(h, w, MatType.CV_8UC3, Scalar.All(250));
        for (var y = 8; y < h - 30; y += 22 + rng.Next(18))
        {
            var kind = rng.Next(4);
            if (kind == 0)
            {
                var col = Bgr((byte)rng.Next(40, 230), (byte)rng.Next(40, 230), (byte)rng.Next(40, 230));
                Cv2.Rectangle(m, new Rect(rng.Next(0, w / 3), y, rng.Next(w / 4, w / 2), 12 + rng.Next(10)), col, -1);
            }
            else
            {
                var text = string.Concat(Enumerable.Range(0, 5 + rng.Next(18)).Select(_ => (char)('a' + rng.Next(26))));
                Cv2.PutText(m, text + " " + rng.Next(1000, 99999), new Point(rng.Next(4, w / 4), y + 14), HersheyFonts.HersheySimplex, 0.5 + rng.NextDouble() * 0.3, Bgr(20, 20, 20), 1, LineTypes.AntiAlias);
            }
        }
        return m;
    }

    /// <summary>Mean absolute per-channel difference of two equally sized images (0..255).</summary>
    public static double MeanAbsDiff(BgraImage a, BgraImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.MaxValue;
        long sum = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
        }
        return sum / (3.0 * (a.Pixels.Length / 4));
    }
}
