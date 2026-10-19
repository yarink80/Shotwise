using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Shotwise.Vision.Internal;

namespace Shotwise.Vision;

/// <summary>
/// "Edit Text" analysis (VIS-02): runs OCR, then per line samples the text and background colours with k-means(2) in Lab on the
/// line box, derives the font pixel size from the cap height and decides bold from the stroke-width / cap-height ratio.
/// </summary>
public sealed class TextEditService : ITextEditService
{
    private const double CapHeightEm = 0.72;   // Segoe UI / Arial class cap height as fraction of the em
    private const double BoldStrokeRatio = 0.165;

    private readonly IOcrService _ocr;
    private readonly ILogger<TextEditService> _log;

    public TextEditService(IOcrService ocr, ILogger<TextEditService> log)
    {
        _ocr = ocr;
        _log = log;
    }

    public async Task<IReadOnlyList<(OcrLine Line, TextStyleSample Style)>> AnalyzeAsync(BgraImage img, Rect2? region, CancellationToken ct)
    {
        var ocr = await _ocr.RecognizeAsync(img, region, null, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var list = new List<(OcrLine, TextStyleSample)>(ocr.Lines.Count);
            foreach (var line in ocr.Lines)
            {
                ct.ThrowIfCancellationRequested();
                var style = SampleLine(img, line);
                if (style is not null) list.Add((line, style));
            }
            _log.LogDebug("Analysed {Count} text lines", list.Count);
            return (IReadOnlyList<(OcrLine, TextStyleSample)>)list;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Samples the style of one OCR line; null when the box is degenerate.</summary>
    internal static TextStyleSample? SampleLine(BgraImage img, OcrLine line)
    {
        var box = line.Bounds.Inflate(2).ToRectIOutward().Intersect(img.Bounds);
        if (box.Width < 3 || box.Height < 3) return null;
        using var bgr = CvInterop.ToBgr(img, box);
        var s = Analyze(bgr);
        if (s is null) return null;
        return s with { SuggestedFont = SuggestFont(line, s.FontSizePx) };
    }

    /// <summary>Core analysis on an already cropped BGR line image.</summary>
    internal static TextStyleSample? Analyze(Mat bgr)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        using var lab = CvInterop.ToLab32(bgr);
        using var samples = lab.Reshape(1, w * h);                     // N x 3 float
        using var labels = new Mat();
        using var centers = new Mat();
        Cv2.Kmeans(samples, 2, labels, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 12, 0.5), 3, KMeansFlags.PpCenters, centers);

        var lbl = new int[w * h];
        labels.GetArray(out int[] arr);
        Array.Copy(arr, lbl, lbl.Length);

        // Background = the cluster that dominates the box border.
        int[] border = new int[2];
        for (var x = 0; x < w; x++) { border[lbl[x]]++; border[lbl[(h - 1) * w + x]]++; }
        for (var y = 0; y < h; y++) { border[lbl[y * w]]++; border[lbl[y * w + w - 1]]++; }
        var bgCluster = border[0] >= border[1] ? 0 : 1;
        var fgCluster = 1 - bgCluster;

        var bytes = CvInterop.ToBytes(bgr);
        double bb = 0, bg = 0, br = 0;
        long bn = 0;
        for (var i = 0; i < lbl.Length; i++)
            if (lbl[i] == bgCluster) { bb += bytes[i * 3]; bg += bytes[i * 3 + 1]; br += bytes[i * 3 + 2]; bn++; }
        if (bn == 0) return null;
        var bgB = bb / bn; var bgG = bg / bn; var bgR = br / bn;

        // Text colour: mean of the most distinct half of foreground pixels (anti-aliased fringes are blends towards the background).
        var fg = new List<(double d, byte b, byte g, byte r)>();
        for (var i = 0; i < lbl.Length; i++)
        {
            if (lbl[i] != fgCluster) continue;
            double db = bytes[i * 3] - bgB, dg = bytes[i * 3 + 1] - bgG, dr = bytes[i * 3 + 2] - bgR;
            fg.Add((db * db + dg * dg + dr * dr, bytes[i * 3], bytes[i * 3 + 1], bytes[i * 3 + 2]));
        }
        var bgColor = Argb.FromRgb(ToByte(bgR), ToByte(bgG), ToByte(bgB));
        if (fg.Count < 4)
        {
            // Nothing distinguishable from the background: no usable ink.
            return new TextStyleSample(bgColor.Luminance > 0.5 ? Argb.Black : Argb.White, bgColor, Math.Max(6, h * 0.6), false, "Segoe UI");
        }
        fg.Sort((a, b) => b.d.CompareTo(a.d));
        var take = Math.Max(1, fg.Count / 2);
        double tb = 0, tg = 0, tr = 0;
        for (var i = 0; i < take; i++) { tb += fg[i].b; tg += fg[i].g; tr += fg[i].r; }
        var textColor = Argb.FromRgb(ToByte(tr / take), ToByte(tg / take), ToByte(tb / take));

        // Ink mask & row profile -> cap height.
        var rowCount = new int[h];
        var ink = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (lbl[y * w + x] == fgCluster) { ink[y * w + x] = true; rowCount[y]++; }
        var max = rowCount.Max();
        if (max == 0) return new TextStyleSample(textColor, bgColor, Math.Max(6, h * 0.6), false, "Segoe UI");

        int top = Array.FindIndex(rowCount, c => c >= Math.Max(1, 0.10 * max));
        int baseline = Array.FindLastIndex(rowCount, c => c >= Math.Max(1, 0.35 * max));
        if (top < 0) top = 0;
        if (baseline < top) baseline = Array.FindLastIndex(rowCount, c => c > 0);
        var capHeight = Math.Max(1, baseline - top + 1);
        var fontPx = Math.Round(capHeight / CapHeightEm, 1);

        // Stroke width: median horizontal ink run in rows between cap top and baseline, ignoring long runs (horizontal bars).
        var runs = new List<int>();
        for (var y = top; y <= baseline; y++)
        {
            var run = 0;
            for (var x = 0; x <= w; x++)
            {
                if (x < w && ink[y * w + x]) { run++; continue; }
                if (run > 0 && run <= Math.Max(2, capHeight * 0.5)) runs.Add(run);
                run = 0;
            }
        }
        var bold = false;
        if (runs.Count >= 3)
        {
            runs.Sort();
            var stroke = runs[runs.Count / 2];
            bold = stroke / (double)capHeight >= BoldStrokeRatio && capHeight >= 5;
        }
        return new TextStyleSample(textColor, bgColor, fontPx, bold, "Segoe UI");
    }

    private static string SuggestFont(OcrLine line, double fontPx)
    {
        // Monospace: per-character advance is (nearly) identical across several words.
        var perChar = line.Words.Where(w => w.Text.Length >= 3).Select(w => w.Bounds.Width / w.Text.Length).ToArray();
        if (perChar.Length >= 3 && fontPx > 0)
        {
            var mean = perChar.Average();
            var cv = Math.Sqrt(perChar.Select(v => (v - mean) * (v - mean)).Average()) / mean;
            var ratio = mean / fontPx;
            if (cv < 0.035 && ratio is > 0.5 and < 0.66) return "Consolas";
        }
        return "Segoe UI";
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
