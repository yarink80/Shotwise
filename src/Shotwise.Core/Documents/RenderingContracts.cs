using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Documents;

/// <summary>Options for <see cref="IDocumentRenderer.RenderPage"/>.</summary>
public sealed record RenderOptions(double Scale = 1, bool IncludeAnnotations = true, IReadOnlySet<Guid>? Exclude = null, bool TransparentBackground = false)
{
    /// <summary>Optional clip in canvas pixels (render only this region; output size = region * Scale).</summary>
    public Rect2? Region { get; init; }
    /// <summary>When set, only these annotations are drawn (flatten-selection).</summary>
    public IReadOnlySet<Guid>? Only { get; init; }
    /// <summary>Draw template section guides / empty drop-zone placeholders (editor view only, never exports).</summary>
    public bool DrawEditorAdornments { get; init; }
}

/// <summary>One laid-out line of text.</summary>
/// <param name="StartIndex">Index of the first character (in the concatenated plain text).</param>
/// <param name="Length">Number of characters on the line (excluding the trailing line break).</param>
/// <param name="Bounds">Line box in annotation-local canvas pixels (relative to the text box origin).</param>
/// <param name="Baseline">Baseline Y in the same space.</param>
/// <param name="CaretPositions">X of every caret stop on the line: <c>Length + 1</c> entries.</param>
public sealed record TextLineInfo(int StartIndex, int Length, Rect2 Bounds, double Baseline, IReadOnlyList<double> CaretPositions);

/// <summary>Result of text layout: measured content size and per-line caret geometry for editing.</summary>
public sealed record TextLayoutInfo(Size2 ContentSize, IReadOnlyList<TextLineInfo> Lines)
{
    public static TextLayoutInfo Empty { get; } = new(Size2.Empty, Array.Empty<TextLineInfo>());

    /// <summary>Character index of the caret stop closest to <paramref name="p"/> (box-local coordinates).</summary>
    public int HitTest(Point2 p)
    {
        if (Lines.Count == 0) return 0;
        var line = Lines[^1];
        foreach (var l in Lines)
            if (p.Y < l.Bounds.Bottom) { line = l; break; }
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < line.CaretPositions.Count; i++)
        {
            var d = Math.Abs(line.CaretPositions[i] - p.X);
            if (d < bestD) { bestD = d; best = i; }
        }
        return line.StartIndex + best;
    }

    /// <summary>Caret rectangle (1 px wide) for a character index.</summary>
    public Rect2 CaretRect(int index)
    {
        if (Lines.Count == 0) return new Rect2(0, 0, 1, 16);
        foreach (var l in Lines)
        {
            if (index >= l.StartIndex && index <= l.StartIndex + l.Length)
            {
                var i = Math.Clamp(index - l.StartIndex, 0, l.CaretPositions.Count - 1);
                var x = l.CaretPositions.Count > 0 ? l.CaretPositions[i] : l.Bounds.X;
                return new Rect2(x, l.Bounds.Y, 1, l.Bounds.Height);
            }
        }
        var last = Lines[^1];
        return new Rect2(last.CaretPositions.Count > 0 ? last.CaretPositions[^1] : last.Bounds.Right, last.Bounds.Y, 1, last.Bounds.Height);
    }
}

/// <summary>Document renderer (implemented in Rendering with SkiaSharp).</summary>
public interface IDocumentRenderer
{
    /// <summary>Background + base raster + annotations into a new image (size = canvas * Scale, or Region * Scale).</summary>
    BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o);

    /// <summary>Draws one annotation onto an SKCanvas passed as object (keeps Core Skia-free).</summary>
    void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a);

    BgraImage RenderThumbnail(ShotDocument doc, int maxSide);

    /// <summary>Visual bounds including caps, stroke, shadow and text layout (rotation applied).</summary>
    Rect2 MeasureBounds(Annotation a);

    /// <summary>Lays out rich text inside <paramref name="maxWidth"/> (≤ 0 = unbounded).</summary>
    TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth);

    /// <summary>Hit-test outline (rotation applied, curves flattened).</summary>
    Geometry2 GetOutline(Annotation a);
}

/// <summary>Editor window host (implemented in UI.Editor).</summary>
public interface IEditorHost
{
    Task OpenAsync(ShotDocument doc, bool activate = true);
    Task OpenFileAsync(string path);
    Task OpenVideoAsync(string path);
    void ShowEditor();
    void ShowLibrary();
    bool IsVisible { get; }
    /// <summary>
    /// Before the app exits for an update restart: saves autosave-backed documents, asks about every other unsaved one and closes
    /// the editor. False when the user cancelled (the editor stays open).
    /// </summary>
    Task<bool> CloseForExitAsync();
}
