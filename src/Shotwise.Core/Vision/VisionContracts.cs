using System.Net;
using System.Text;
using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;

namespace Shotwise.Core.Vision;

public sealed record OcrWord(string Text, Rect2 Bounds, double Confidence);
public sealed record OcrLine(string Text, Rect2 Bounds, IReadOnlyList<OcrWord> Words);
public sealed record OcrResult(string Language, string Text, IReadOnlyList<OcrLine> Lines, double Angle)
{
    public static OcrResult Empty(string language = "") => new(language, "", Array.Empty<OcrLine>(), 0);
}

public interface IOcrService
{
    IReadOnlyList<string> AvailableLanguages { get; }
    string EngineName { get; }
    Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default);
}

/// <summary>Table reconstructed from OCR word boxes: rows × columns of cell strings.</summary>
public sealed class TableResult
{
    public TableResult(IReadOnlyList<IReadOnlyList<string>> rows) => Rows = rows;

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
    public int RowCount => Rows.Count;
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
    public bool IsEmpty => RowCount == 0 || ColumnCount == 0;

    public string ToTsv()
    {
        var sb = new StringBuilder();
        foreach (var row in Rows)
        {
            for (var c = 0; c < ColumnCount; c++)
            {
                if (c > 0) sb.Append('\t');
                if (c < row.Count) sb.Append(row[c].Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public string ToHtml()
    {
        var sb = new StringBuilder("<table>");
        foreach (var row in Rows)
        {
            sb.Append("<tr>");
            for (var c = 0; c < ColumnCount; c++)
                sb.Append("<td>").Append(c < row.Count ? WebUtility.HtmlEncode(row[c]) : "").Append("</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</table>").ToString();
    }
}

public interface ITableExtractor { TableResult Extract(OcrResult ocr); }

public sealed record TextStyleSample(Argb TextColor, Argb BackgroundColor, double FontSizePx, bool Bold, string SuggestedFont);

public interface ITextEditService
{
    Task<IReadOnlyList<(OcrLine Line, TextStyleSample Style)>> AnalyzeAsync(BgraImage img, Rect2? region, CancellationToken ct);
}

public enum InpaintQuality { Fast, Quality, HighQuality }

public interface IInpaintingService
{
    InpaintQuality[] Available { get; }
    Task<BgraImage> InpaintAsync(BgraImage img, Mask mask, InpaintQuality q = InpaintQuality.Fast, CancellationToken ct = default);
}

public sealed record SmartComponent(Guid Id, RectI Bounds, Mask Mask);

public interface ISmartMoveService
{
    Task<double> AutoToleranceAsync(BgraImage img, CancellationToken ct);
    Task<IReadOnlyList<SmartComponent>> DetectAsync(BgraImage img, double tolerance, CancellationToken ct);
}

public interface ISimplifyService
{
    Task<IReadOnlyList<SimplifyShapeAnnotation>> GenerateAsync(BgraImage img, int detail, Palette palette, CancellationToken ct);
    Task<Palette> DominantColorsAsync(BgraImage img, int k, CancellationToken ct);
}

public enum RedactCategory { Email, Phone, CreditCard, Ssn, IpAddress, Url, Date, PostalAddress, Face, Other }

public sealed record RedactionHit(RedactCategory Category, Rect2 Bounds, string? Text);

public interface ISmartRedactService
{
    Task<IReadOnlyList<RedactionHit>> FindAsync(BgraImage img, IReadOnlySet<RedactCategory> categories, CancellationToken ct);
}

public interface IMagicWandService
{
    Mask Select(BgraImage img, PointI seed, double tolerance, bool contiguous);
    Mask Refine(Mask m, int radius);
}

public interface IFaceDetector
{
    Task<IReadOnlyList<Rect2>> DetectAsync(BgraImage img, CancellationToken ct);
}

/// <summary>Downloadable model descriptor (URL + SHA-256 + licence shown to the user).</summary>
public sealed record ModelInfo(string Id, string Name, string Purpose, Uri Url, string Sha256, long SizeBytes, string License, Uri? LicenseUrl = null);

/// <summary>Downloads (only on explicit EnsureAsync) and verifies models in %LOCALAPPDATA%\Shotwise\models.</summary>
public interface IModelManager
{
    IReadOnlyList<ModelInfo> Models { get; }
    bool IsAvailable(string modelId);
    /// <summary>Returns the local file path, downloading + verifying if needed.</summary>
    Task<string> EnsureAsync(string modelId, IProgress<double>? p, CancellationToken ct);
}
