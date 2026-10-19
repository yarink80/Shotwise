using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Renders small gallery thumbnails (quick styles, callout presets, stamps) with the document renderer.</summary>
internal sealed class PreviewRenderer
{
    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly Dictionary<string, ImageSource?> _cache = new();

    public PreviewRenderer(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<PreviewRenderer>();
    }

    public void Clear() => _cache.Clear();

    /// <summary>True while the Dark token set is applied (thumbnails then get a light "paper" so dark styles stay visible).</summary>
    private static bool DarkTheme => System.Windows.Application.Current?.TryFindResource("Shotwise.ThemeName") as string == "Dark";

    public ImageSource? QuickStyle(QuickStyle style, int w = 96, int h = 48)
    {
        var dark = DarkTheme;
        var key = $"qs:{style.Id}:{w}x{h}:{style.Prototype?.Kind}:{style.Prototype?.Stroke.Color.Value}:{style.Prototype?.Fill.Color.Value}:{dark}";
        if (_cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? result = null;
        try
        {
            // A stamp prototype carries no asset, so the renderer would draw its red "missing asset" X: no thumbnail instead.
            if (style.Prototype is { } proto and not StampAnnotation)
            {
                var a = proto.DeepClone();
                a.Id = Guid.NewGuid();
                a.RotationDegrees = 0;
                Fit(a, w, h);
                NormalizeForPreview(a, w, h);
                // Transparent so the light tile surface shows through; in Dark (and for plain text) a rounded light "paper" sits
                // behind the style so dark colours keep their contrast — styles are meant for screenshots, which are mostly light.
                // Very light styles (e.g. the Dark Mode theme's white arrows and text) get a dark paper instead, in both UI themes.
                var lightInk = IsLightInk(a);
                var paper = dark || lightInk || a is TextAnnotation { Fill.Kind: FillKind.None };
                result = paper ? RenderOnPaper(a, w, h, lightInk) : RenderOne(a, w, h, Argb.Transparent);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Quick style preview failed for {Id}", style.Id);
        }
        _cache[key] = result;
        return result;
    }

    public ImageSource? Callout(string presetId, int w = 64, int h = 44)
    {
        var key = $"co:{presetId}:{w}x{h}";
        if (_cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? result = null;
        try
        {
            var c = new CalloutAnnotation
            {
                ShapePresetId = presetId,
                Start = new Point2(5, 4),
                End = new Point2(w - 5, h - 12),
                Fill = FillStyle.Solid(Argb.FromRgb(0xFF, 0xF3, 0xB0)),
                Stroke = new StrokeStyle(Argb.FromRgb(0x55, 0x55, 0x55), 1.5),
            };
            if (CalloutPresets.AllowsTails(presetId)) c.Tails.Add(new CalloutTail(new Point2(w * 0.3, h - 2), 12));
            result = RenderOne(c, w, h, Argb.White);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Callout preview failed for {Id}", presetId);
        }
        _cache[key] = result;
        return result;
    }

    public ImageSource? Stamp(StampInfo info, byte[] data, int size = 48, bool cache = true)
    {
        var key = $"st:{info.Id}:{size}";
        if (cache && _cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? result = null;
        try
        {
            var doc = ShotDocument.Create(new Size2(size, size), Argb.Transparent);
            doc.Assets.Codec = _ctx.Codec;
            var asset = doc.Assets.Add(data, info.MediaType);
            var s = new StampAnnotation { Asset = asset, Start = new Point2(3, 3), End = new Point2(size - 3, size - 3) };
            doc.Pages[0].Annotations.Add(s);
            var img = _ctx.Renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(2, true, null, true));
            result = img.ToBitmapSource();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stamp preview failed for {Id}", info.Id);
        }
        if (cache) _cache[key] = result;
        return result;
    }

    /// <summary>True when the style's visible ink (text colour, else stroke, else fill) is so light it vanishes on light paper.</summary>
    private static bool IsLightInk(Annotation a)
    {
        Argb ink;
        if (a is TextAnnotation t && t.Text.Runs.FirstOrDefault() is { } run) ink = run.Color;
        else if (a is BlurAnnotation or StepAnnotation or CalloutAnnotation or HighlightAnnotation) return false; // own fill/sample; highlights multiply
        else if (a.Stroke.IsVisible && a.Stroke.Width > 0 && a.Stroke.Color.A > 0) ink = a.Stroke.Color;
        else if (a.Fill.Kind == FillKind.Solid) ink = a.Fill.Color;
        else return false;
        if (a is TextAnnotation { Fill.Kind: not FillKind.None }) return false; // text on its own background
        var lum = (0.2126 * ink.R + 0.7152 * ink.G + 0.0722 * ink.B) / 255.0;
        return ink.A > 96 && lum > 0.82;
    }

    private ImageSource RenderOnPaper(Annotation a, int w, int h, bool darkPaper = false)
    {
        var doc = ShotDocument.Create(new Size2(w, h), Argb.Transparent);
        doc.Pages[0].Annotations.Add(new ShapeAnnotation
        {
            Shape = ShapeKind.RoundedRectangle, CornerRadius = 6, Start = new Point2(0, 0), End = new Point2(w, h),
            Fill = FillStyle.Solid(darkPaper ? Argb.FromRgb(0x2B, 0x2D, 0x36) : Argb.FromRgb(0xF4, 0xF5, 0xF9)), Stroke = StrokeStyle.None,
        });
        AddBlurSample(doc, a, w, h);
        doc.Pages[0].Annotations.Add(a);
        return _ctx.Renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(2)).ToBitmapSource();
    }

    /// <summary>A blur over an empty tile shows nothing: put a small sample (coloured bars + text) underneath it.</summary>
    private static void AddBlurSample(ShotDocument doc, Annotation a, int w, int h)
    {
        if (a is not BlurAnnotation) return;
        var anns = doc.Pages[0].Annotations;
        anns.Add(new ShapeAnnotation { Shape = ShapeKind.Rectangle, Start = new Point2(8, 8), End = new Point2(w - 8, h / 2.0), Fill = FillStyle.Solid(Argb.FromRgb(0x42, 0x85, 0xF4)), Stroke = StrokeStyle.None });
        anns.Add(new ShapeAnnotation { Shape = ShapeKind.Rectangle, Start = new Point2(8, h / 2.0), End = new Point2(w / 2.0, h - 8), Fill = FillStyle.Solid(Argb.FromRgb(0xF4, 0xB4, 0x00)), Stroke = StrokeStyle.None });
        anns.Add(new ShapeAnnotation { Shape = ShapeKind.Rectangle, Start = new Point2(w / 2.0, h / 2.0), End = new Point2(w - 8, h - 8), Fill = FillStyle.Solid(Argb.FromRgb(0xDB, 0x44, 0x37)), Stroke = StrokeStyle.None });
        anns.Add(new TextAnnotation
        {
            Start = new Point2(8, 8), End = new Point2(w - 8, h - 8),
            Text = RichText.FromPlain("Aa", fontSize: Math.Min(20, h * 0.4), color: Argb.White, bold: true),
            TextStyle = TextLayoutStyle.Default with { Padding = new Thickness2(0, 0, 0, 0), HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle },
        });
    }

    private ImageSource RenderOne(Annotation a, int w, int h, Argb background)
    {
        var doc = ShotDocument.Create(new Size2(w, h), background);
        AddBlurSample(doc, a, w, h);
        doc.Pages[0].Annotations.Add(a);
        var img = _ctx.Renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(2));
        return img.ToBitmapSource();
    }

    /// <summary>Short sample text ("Aa") in the style's first run, sized to the thumbnail.</summary>
    private static RichText Sample(RichText source, double maxSize)
    {
        var run = source.Runs.FirstOrDefault() ?? new TextRun();
        return new RichText(new[] { run with { Text = "Aa", FontSize = Math.Min(run.FontSize, maxSize) } });
    }

    /// <summary>Reference width for <see cref="PreviewStrokeWidth"/>: widths well below it keep their ratios almost linearly.</summary>
    private const double StrokeReference = 7;

    /// <summary>
    /// Maps a style's stroke width to a thumbnail width that never exceeds <c>tile / 14</c>. The mapping is a smooth,
    /// monotonic soft clip (<c>max · tanh(w / ref)</c>), so thin, standard and heavy styles stay visibly ordered and roughly
    /// proportional, while a 7–23 px "Heavy" style no longer floods a 50 px tile.
    /// </summary>
    internal static double PreviewStrokeWidth(double width, int w, int h)
    {
        if (width <= 0) return width;
        var max = Math.Min(w, h) / 14.0;
        return Math.Max(Math.Min(0.75, width), max * Math.Tanh(width / StrokeReference));
    }

    /// <summary>Clamps stroke widths and arrow-cap sizes of a thumbnail prototype relative to the tile size.</summary>
    internal static void NormalizeForPreview(Annotation a, int w, int h)
    {
        if (a is HighlightAnnotation or StepAnnotation or TextAnnotation) return; // sized by their rectangles, not the stroke
        if (a.Stroke.Width > 0) a.Stroke = a.Stroke with { Width = PreviewStrokeWidth(a.Stroke.Width, w, h) };
        if (a is ArrowAnnotation arrow)
        {
            // Keep each cap within ~40 % of the shaft so the arrow still reads as an arrow (the renderer's cap length is
            // max(12, 4.5 · width) · scale, see AnnotationGeometry.CapLength).
            var length = Math.Sqrt(Math.Pow(arrow.End.X - arrow.Start.X, 2) + Math.Pow(arrow.End.Y - arrow.Start.Y, 2));
            var baseCap = Math.Max(12, Math.Max(1, arrow.Stroke.Width) * 4.5);
            var maxScale = length * 0.4 / baseCap;
            arrow.StartCapScale = Math.Min(arrow.StartCapScale, maxScale);
            arrow.EndCapScale = Math.Min(arrow.EndCapScale, maxScale);
        }
    }

    private static void Fit(Annotation a, int w, int h)
    {
        switch (a)
        {
            case ArrowAnnotation arrow:
                // A long diagonal so even heavy styles show their shaft, not just the head.
                arrow.Start = new Point2(7, h - 9);
                arrow.End = new Point2(w - 7, 9);
                if (arrow.Bezier) { arrow.Control1 = new Point2(w * 0.3, h * 0.9); arrow.Control2 = new Point2(w * 0.6, h * 0.1); }
                break;
            case LineAnnotation line:
                line.Start = new Point2(9, h - 10);
                line.End = new Point2(w - 9, 10);
                if (line.Bezier) { line.Control1 = new Point2(w * 0.3, h * 0.9); line.Control2 = new Point2(w * 0.6, h * 0.1); }
                break;
            case PenAnnotation pen:
                pen.Points = Enumerable.Range(0, 24).Select(i => new Point2(10 + i * (w - 20) / 23.0, h / 2.0 + Math.Sin(i / 23.0 * Math.PI * 2) * h * 0.28)).ToList();
                break;
            case ShapeAnnotation shape:
                shape.Shape = shape.Shape == ShapeKind.Polygon ? ShapeKind.Rectangle : shape.Shape;
                shape.Start = new Point2(12, 8);
                shape.End = new Point2(w - 12, h - 8);
                break;
            case StepAnnotation step:
                step.Start = new Point2(w / 2.0 - 15, h / 2.0 - 15);
                step.End = new Point2(w / 2.0 + 15, h / 2.0 + 15);
                step.Value = 1;
                if (step.Style is StepStyle.CirclePointer or StepStyle.SquarePointer) step.PointerTip = new Point2(w / 2.0 + 24, h / 2.0 + 20);
                break;
            case HighlightAnnotation hl when hl.Points is not null:
                // Freehand marker styles draw a stroke along points (a rectangle would be empty): a short swoosh.
                hl.Points = Enumerable.Range(0, 12).Select(i => new Point2(10 + i * (w - 20) / 11.0, h / 2.0 + 5 - i * 10 / 11.0)).ToList();
                hl.Stroke = hl.Stroke with { Width = Math.Min(hl.Stroke.Width, h * 0.32) };
                hl.Start = new Point2(10, h / 2.0 - 9);
                hl.End = new Point2(w - 10, h / 2.0 + 9);
                break;
            case HighlightAnnotation hl:
                hl.Start = new Point2(10, h / 2.0 - 9);
                hl.End = new Point2(w - 10, h / 2.0 + 9);
                break;
            case TextAnnotation text:
                text.Text = Sample(text.Text, Math.Min(22, h * 0.4));
                text.TextStyle = text.TextStyle with { Padding = new Thickness2(2, 2, 2, 2), HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle };
                text.Start = new Point2(3, 3);
                text.End = new Point2(w - 3, h - 3);
                break;
            case BlurAnnotation blur:
                blur.Start = new Point2(8, 8);
                blur.End = new Point2(w - 8, h - 8);
                break;
            case CalloutAnnotation callout:
                callout.Text = Sample(callout.Text, Math.Min(18, h * 0.3));
                callout.Start = new Point2(5, 5);
                callout.End = new Point2(w - 5, h - 14);
                callout.Tails.Clear();
                if (CalloutPresets.AllowsTails(callout.ShapePresetId)) callout.Tails.Add(new CalloutTail(new Point2(w * 0.3, h - 2), 12));
                break;
            case MagnifyAnnotation mag:
                mag.Start = new Point2(w / 2.0 - 22, 4);
                mag.End = new Point2(w / 2.0 + 22, h - 4);
                mag.SourceRect = new Rect2(0, 0, 20, 14);
                break;
            case CursorAnnotation cur:
                cur.Start = new Point2(w / 2.0 - 12, 5);
                cur.End = new Point2(w / 2.0 + 14, h - 5);
                break;
            default:
                a.Start = new Point2(12, 8);
                a.End = new Point2(w - 12, h - 8);
                break;
        }
    }
}
