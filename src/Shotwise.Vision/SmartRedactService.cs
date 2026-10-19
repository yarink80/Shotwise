using System.Text;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// Smart Redact (VIS-05): OCR text is classified by <see cref="PiiDetector"/>; matched character spans are mapped back to
/// word boxes (partial words are cut proportionally); faces come from <see cref="IFaceDetector"/>.
/// </summary>
public sealed class SmartRedactService : ISmartRedactService
{
    private static readonly RedactCategory[] TextCategories =
    {
        RedactCategory.Email, RedactCategory.Phone, RedactCategory.CreditCard, RedactCategory.Ssn, RedactCategory.IpAddress,
        RedactCategory.Url, RedactCategory.Date, RedactCategory.PostalAddress
    };

    private readonly IOcrService _ocr;
    private readonly IFaceDetector _faces;
    private readonly ILogger<SmartRedactService> _log;

    public SmartRedactService(IOcrService ocr, IFaceDetector faces, ILogger<SmartRedactService> log)
    {
        _ocr = ocr;
        _faces = faces;
        _log = log;
    }

    public async Task<IReadOnlyList<RedactionHit>> FindAsync(BgraImage img, IReadOnlySet<RedactCategory> categories, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(categories);
        var hits = new List<RedactionHit>();

        if (TextCategories.Any(categories.Contains))
        {
            var ocr = await _ocr.RecognizeAsync(img, null, null, ct).ConfigureAwait(false);
            hits.AddRange(MatchLines(ocr, categories));
        }
        if (categories.Contains(RedactCategory.Face))
        {
            var faces = await _faces.DetectAsync(img, ct).ConfigureAwait(false);
            hits.AddRange(faces.Select(f => new RedactionHit(RedactCategory.Face, f, null)));
        }
        _log.LogDebug("Smart Redact found {Count} items", hits.Count);
        return hits.OrderBy(h => h.Bounds.Y).ThenBy(h => h.Bounds.X).ToList();
    }

    /// <summary>Runs the text classifiers over OCR lines (exposed for tests and for callers that already have OCR output).</summary>
    public static IReadOnlyList<RedactionHit> MatchLines(OcrResult ocr, IReadOnlySet<RedactCategory> categories)
    {
        var hits = new List<RedactionHit>();
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;
            var sb = new StringBuilder();
            var starts = new int[line.Words.Count];
            for (var i = 0; i < line.Words.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                starts[i] = sb.Length;
                sb.Append(line.Words[i].Text);
            }
            var text = sb.ToString();
            foreach (var m in PiiDetector.Find(text, categories))
            {
                var rect = Rect2.Empty;
                var any = false;
                for (var i = 0; i < line.Words.Count; i++)
                {
                    var w = line.Words[i];
                    var ws = starts[i];
                    var we = ws + w.Text.Length;
                    var s = Math.Max(ws, m.Start);
                    var e = Math.Min(we, m.End);
                    if (e <= s || w.Text.Length == 0) continue;
                    var x0 = w.Bounds.Left + w.Bounds.Width * (s - ws) / w.Text.Length;
                    var x1 = w.Bounds.Left + w.Bounds.Width * (e - ws) / w.Text.Length;
                    var piece = Rect2.FromLTRB(x0, w.Bounds.Top, x1, w.Bounds.Bottom);
                    rect = any ? rect.Union(piece) : piece;
                    any = true;
                }
                if (any) hits.Add(new RedactionHit(m.Category, rect.Inflate(1, 1), text.Substring(m.Start, m.Length)));
            }
        }
        return hits;
    }
}
