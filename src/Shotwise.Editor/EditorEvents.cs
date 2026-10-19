using Shotwise.Core.Geometry;
using Shotwise.Core.Vision;

namespace Shotwise.Editor;

/// <summary>Raised when Grab Text finished; the UI shows the OCR result dialog (VIS-01).</summary>
/// <param name="Result">Recognised text with line/word boxes (canvas pixels).</param>
/// <param name="Region">Recognised region (null = whole page).</param>
public sealed record TextRecognizedEventArgs(OcrResult Result, Rect2? Region);

/// <summary>Raised after Smart Redact detection (counts per category drive the review pane).</summary>
public sealed record RedactionsFoundEventArgs(IReadOnlyList<RedactionHit> Hits)
{
    public IReadOnlyDictionary<RedactCategory, int> Counts { get; } = Hits.GroupBy(h => h.Category).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Raised after Smart Move detection finished (component count; 0 = "no objects found").</summary>
public sealed record SmartComponentsDetectedEventArgs(int Count, double Tolerance);

/// <summary>Colour sampled by an eyedropper gesture (Alt+click with Fill, or <see cref="EditorSession.SampleColor"/>).</summary>
public sealed record ColorSampledEventArgs(Shotwise.Core.Imaging.Argb Color, Point2 Position);
