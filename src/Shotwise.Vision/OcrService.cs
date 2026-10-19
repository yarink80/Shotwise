using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;

namespace Shotwise.Vision;

/// <summary>
/// <see cref="IOcrService"/> on Windows.Media.Ocr (VIS-01). Language: explicit tag, else the user-profile languages,
/// else any installed recogniser. Small text is upscaled x2 (retry) for accuracy; oversized input is scaled down to the engine limit.
/// </summary>
public sealed class OcrService : IOcrService
{
    private const double SmallTextHeightPx = 16;
    private readonly ILogger<OcrService> _log;

    public OcrService(ILogger<OcrService> log) => _log = log;

    public string EngineName => "Windows.Media.Ocr";

    public IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not enumerate OCR languages"); return Array.Empty<string>(); }
        }
    }

    public async Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(img);
        ct.ThrowIfCancellationRequested();

        var crop = (region is { } r ? r.Normalize().ToRectIOutward() : img.Bounds).Intersect(img.Bounds);
        if (crop.IsEmpty) return OcrResult.Empty();

        var engine = CreateEngine(language);
        var langTag = engine.RecognizerLanguage?.LanguageTag ?? "";
        if (crop.Width < 3 || crop.Height < 3) return OcrResult.Empty(langTag);

        var maxDim = (double)OcrEngine.MaxImageDimension;
        using var bgr = CvInterop.ToBgr(img, crop);

        var fit = Math.Min(1.0, maxDim / Math.Max(crop.Width, crop.Height));
        var first = await RunAsync(engine, bgr, fit, ct).ConfigureAwait(false);
        var best = first;

        if (first.WordCount > 0 &&first.MedianWordHeight < SmallTextHeightPx &&fit * 2 * Math.Max(crop.Width, crop.Height) <= maxDim)
        {
            // small text: retry at 2x (3x for very tiny glyphs) and keep whichever recognised more characters
            var factor = first.MedianWordHeight < 9 ? 3.0 : 2.0;
            if (factor * Math.Max(crop.Width, crop.Height) > maxDim) factor = 2.0;
            var second = await RunAsync(engine, bgr, factor, ct).ConfigureAwait(false);
            if (second.CharCount >= first.CharCount) best = second;
            _log.LogDebug("OCR small-text retry x{Factor}: {A} -> {B} chars", factor, first.CharCount, second.CharCount);
        }
        else if (first.WordCount == 0 &&crop.Height < 60 && 2 * Math.Max(crop.Width, crop.Height) <= maxDim)
        {
            var second = await RunAsync(engine, bgr, 2.0, ct).ConfigureAwait(false);
            if (second.CharCount > 0) best = second;
        }

        var lines = new List<OcrLine>(best.Lines.Count);
        foreach (var l in best.Lines)
        {
            var words = l.Words.Select(w => new OcrWord(w.Text, Offset(w.Bounds, crop), w.Confidence)).ToArray();
            lines.Add(new OcrLine(l.Text, Offset(l.Bounds, crop), words));
        }
        return new OcrResult(langTag, string.Join("\n", lines.Select(l => l.Text)), lines, best.Angle);
    }

    private static Rect2 Offset(Rect2 r, RectI crop) => r.Offset(crop.X, crop.Y);

    private OcrEngine CreateEngine(string? language)
    {
        OcrEngine? engine = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(language))
            {
                var lang = new Language(language);
                if (OcrEngine.IsLanguageSupported(lang)) engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is null) _log.LogInformation("OCR language {Language} not installed; falling back to profile languages", language);
            }
            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                var any = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                if (any is not null) engine = OcrEngine.TryCreateFromLanguage(any);
            }
        }
        catch (Exception ex)
        {
            throw new ShotwiseException(ErrorCode.OcrUnavailable, "Windows OCR is not available on this system.", ex);
        }
        return engine ?? throw new ShotwiseException(ErrorCode.OcrLanguageMissing,
            "No OCR language pack is installed. Add a language with OCR support in Windows Settings > Time & language.");
    }

    private sealed record Pass(IReadOnlyList<OcrLine> Lines, double Angle)
    {
        public IEnumerable<OcrWord> Words => Lines.SelectMany(l => l.Words);
        public int WordCount => Lines.Sum(l => l.Words.Count);
        public int CharCount => Lines.Sum(l => l.Text.Length);
        public double MedianWordHeight
        {
            get
            {
                var hs = Words.Select(w => w.Bounds.Height).OrderBy(h => h).ToArray();
                return hs.Length == 0 ? 0 : hs[hs.Length / 2];
            }
        }
    }

    /// <summary>Runs one recognition pass at <paramref name="scale"/>; returned boxes are in the unscaled crop's pixel space.</summary>
    private async Task<Pass> RunAsync(OcrEngine engine, Mat bgr, double scale, CancellationToken ct)
    {
        Mat? scaled = null;
        try
        {
            var src = bgr;
            if (Math.Abs(scale - 1.0) > 1e-6)
            {
                scaled = new Mat();
                var w = Math.Max(1, (int)Math.Round(bgr.Cols * scale));
                var h = Math.Max(1, (int)Math.Round(bgr.Rows * scale));
                Cv2.Resize(bgr, scaled, new Size(w, h), 0, 0, scale > 1 ? InterpolationFlags.Cubic : InterpolationFlags.Area);
                src = scaled;
            }
            using var bgra = new Mat();
            Cv2.CvtColor(src, bgra, ColorConversionCodes.BGR2BGRA);
            var bytes = CvInterop.ToBytes(bgra);

            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Cols, bgra.Rows, BitmapAlphaMode.Ignore);
            Windows.Media.Ocr.OcrResult res;
            // note: Windows.Media.Ocr.OcrLine/OcrWord are used below through var only
            try
            {
                res = await engine.RecognizeAsync(bmp).AsTask(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ShotwiseException(ErrorCode.OcrFailed, "Text recognition failed.", ex);
            }

            var lines = new List<OcrLine>();
            foreach (var l in res.Lines)
            {
                var words = new List<OcrWord>();
                foreach (var w in l.Words)
                {
                    var b = w.BoundingRect;
                    words.Add(new OcrWord(w.Text, new Rect2(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale), 1.0));
                }
                if (words.Count == 0) continue;
                var bounds = words.Skip(1).Aggregate(words[0].Bounds, (acc, w) => acc.Union(w.Bounds));
                lines.Add(new OcrLine(l.Text, bounds, words));
            }
            return new Pass(lines, res.TextAngle ?? 0);
        }
        finally { scaled?.Dispose(); }
    }
}
