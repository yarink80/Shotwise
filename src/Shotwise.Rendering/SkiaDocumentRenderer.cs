using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>SkiaSharp implementation of <see cref="IDocumentRenderer"/>. Thread-safe: no mutable per-call state is shared.</summary>
public sealed class SkiaDocumentRenderer : IDocumentRenderer
{
    private readonly ILogger<SkiaDocumentRenderer> _log;
    private readonly AssetRasterCache _assets;
    private readonly AnnotationPainter _painter;

    public SkiaDocumentRenderer(IImageCodec? codec = null, ILogger<SkiaDocumentRenderer>? log = null)
    {
        _log = log ?? NullLogger<SkiaDocumentRenderer>.Instance;
        _assets = new AssetRasterCache(codec);
        _painter = new AnnotationPainter(_assets);
    }

    // ---------------------------------------------------------------- rendering

    public BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(page);
        o ??= new RenderOptions();
        var scale = o.Scale > 0 ? o.Scale : 1;
        var region = (o.Region ?? page.CanvasRect).Normalize();
        if (region.IsEmpty) region = new Rect2(0, 0, Math.Max(1, page.CanvasSize.Width), Math.Max(1, page.CanvasSize.Height));
        var w = Math.Max(1, (int)Math.Ceiling(region.Width * scale - 1e-6));
        var h = Math.Max(1, (int)Math.Ceiling(region.Height * scale - 1e-6));
        _ = new BgraImage(1, 1); // validates nothing; real size validated below
        if ((long)w * h > BgraImage.MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, $"Render size {w}x{h} exceeds the maximum supported size.");

        using var surface = SKSurface.Create(SkiaInterop.Info(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        canvas.Translate((float)-region.X, (float)-region.Y);
        DrawPage(canvas, surface, doc, page, o, scale);
        canvas.Flush();
        using var snap = surface.Snapshot();
        return SkiaInterop.ToBgra(snap, page.Dpi * scale, page.Dpi * scale);
    }

    private void DrawPage(SKCanvas canvas, SKSurface? surface, ShotDocument doc, Page page, RenderOptions o, double scale)
    {
        var canvasRect = page.CanvasRect.ToSk();
        var save = canvas.Save();
        if (page.CornerRadius > 0)
        {
            using var rr = new SKPath();
            var rad = (float)Math.Min(page.CornerRadius, Math.Min(page.CanvasSize.Width, page.CanvasSize.Height) / 2);
            rr.AddRoundRect(canvasRect, rad, rad);
            canvas.ClipPath(rr, SKClipOperation.Intersect, true);
        }

        // background
        if (!o.TransparentBackground)
        {
            var bg = page.Background;
            if (bg.Gradient is { Stops.Count: > 0 } g)
            {
                using var p = new SKPaint { Shader = AnnotationPainter.MakeGradient(g, page.CanvasRect), IsAntialias = false };
                canvas.DrawRect(canvasRect, p);
            }
            else if (bg.Color.A > 0)
            {
                using var p = new SKPaint { Color = bg.Color.ToSk() };
                canvas.DrawRect(canvasRect, p);
            }
        }
        // base raster
        if (page.Background.BaseImage is { } bi && !bi.Asset.IsEmpty)
        {
            var d = _assets.Get(doc.Assets, bi.Asset);
            if (d?.Image is { } img)
            {
                using var paint = new SKPaint { IsAntialias = false };
                var sampling = Math.Abs(scale - 1) < 1e-6 ? new SKSamplingOptions(SKFilterMode.Nearest)
                    : scale < 1 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
                var dst = new SKRect((float)bi.Offset.X, (float)bi.Offset.Y, (float)bi.Offset.X + img.Width, (float)bi.Offset.Y + img.Height);
                canvas.DrawImage(img, new SKRect(0, 0, img.Width, img.Height), dst, sampling, paint);
            }
        }

        var ctx = new PaintContext
        {
            Doc = doc,
            Page = page,
            Adornments = o.DrawEditorAdornments,
            BackdropProvider = surface is null ? null : (c, _, _) =>
            {
                c.Flush();
                return new Backdrop { Image = surface.Snapshot(), ImageToDevice = SKMatrix.Identity, Owned = true };
            },
        };

        if (o.IncludeAnnotations)
        {
            foreach (var s in page.Sections)
            {
                if (s.Background is { A: > 0 } sbg)
                {
                    using var p = new SKPaint { Color = sbg.ToSk() };
                    canvas.DrawRect(s.Bounds.ToSk(), p);
                }
                foreach (var a in s.Graphics) PaintIfIncluded(canvas, ctx, a, o);
                if (o.DrawEditorAdornments) DrawSectionGuide(canvas, s);
            }
            foreach (var a in page.Annotations) PaintIfIncluded(canvas, ctx, a, o);
        }
        canvas.RestoreToCount(save);
    }

    private void PaintIfIncluded(SKCanvas canvas, PaintContext ctx, Annotation a, RenderOptions o)
    {
        if (o.Exclude is { } ex && ex.Contains(a.Id)) return;
        if (o.Only is { } only && !only.Contains(a.Id)) return;
        try { _painter.Paint(canvas, ctx, a); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Failed to paint annotation {Kind} {Id}", a.Kind, a.Id);
        }
    }

    private static void DrawSectionGuide(SKCanvas canvas, TemplateSection s)
    {
        using var p = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(60, 130, 230, 150),
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0),
        };
        canvas.DrawRect(s.Bounds.ToSk(), p);
    }

    public void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a)
    {
        ArgumentNullException.ThrowIfNull(skCanvas);
        if (skCanvas is not SKCanvas canvas) throw new ArgumentException("Expected an SKCanvas.", nameof(skCanvas));
        var baseMatrix = canvas.TotalMatrix;
        var ctx = new PaintContext
        {
            Doc = doc,
            Page = page,
            Adornments = false,
            BackdropProvider = (c, pc, ann) => BuildFallbackBackdrop(c, pc, ann, baseMatrix),
        };
        _painter.Paint(canvas, ctx, a);
    }

    /// <summary>Re-renders the page content beneath <paramref name="target"/> (document pixels at scale 1) to feed blur / magnify.</summary>
    private Backdrop? BuildFallbackBackdrop(SKCanvas canvas, PaintContext ctx, Annotation target, SKMatrix baseMatrix)
    {
        var page = ctx.Page;
        var w = Math.Max(1, (int)Math.Ceiling(page.CanvasSize.Width));
        var h = Math.Max(1, (int)Math.Ceiling(page.CanvasSize.Height));
        if ((long)w * h > BgraImage.MaxPixels) return null;
        var below = new HashSet<Guid>();
        foreach (var a in page.AllAnnotations())
        {
            if (a.Id == target.Id) break;
            below.Add(a.Id);
        }
        using var surface = SKSurface.Create(SkiaInterop.Info(w, h));
        surface.Canvas.Clear(SKColors.Transparent);
        DrawPage(surface.Canvas, surface, ctx.Doc, page, new RenderOptions(1, true, null, false) { Only = below }, 1);
        return new Backdrop { Image = surface.Snapshot(), ImageToDevice = baseMatrix, Owned = true };
    }

    public BgraImage RenderThumbnail(ShotDocument doc, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (maxSide <= 0) throw new ArgumentOutOfRangeException(nameof(maxSide));
        var page = doc.Pages.FirstOrDefault() ?? throw new ShotwiseException(ErrorCode.InvalidArgument, "Document has no pages.");
        var longest = Math.Max(1, Math.Max(page.CanvasSize.Width, page.CanvasSize.Height));
        var scale = Math.Min(1.0, maxSide / longest);
        if (page.CanvasSize.Width * scale < 1 || page.CanvasSize.Height * scale < 1) scale = 1.0 / Math.Min(page.CanvasSize.Width, page.CanvasSize.Height);
        return RenderPage(doc, page, new RenderOptions(scale, true, null, false));
    }

    // ---------------------------------------------------------------- text

    public TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TextLayoutEngine.Layout(text, style ?? TextLayoutStyle.Default, maxWidth).Info;
    }

    /// <summary>Lays out text inside a fixed box (applies vertical alignment, unlike <see cref="LayoutText"/>). Coordinates are box-local.</summary>
    public TextLayoutInfo LayoutTextInBox(RichText text, TextLayoutStyle style, Size2 box)
        => TextLayoutEngine.Layout(text, style, box.Width, box.Height).Info;

    // ---------------------------------------------------------------- bounds & outline

    public Rect2 MeasureBounds(Annotation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var def = a.Bounds;
        var r = LocalVisualBounds(a);
        if (a.Shadow is { Opacity: > 0 } sh && sh.Color.A > 0)
        {
            var (dx, dy) = sh.Offset;
            var m = sh.Blur * 1.5;
            r = r.Union(r.Offset(dx, dy).Inflate(m));
        }
        if (Math.Abs(a.RotationDegrees) > 1e-9)
        {
            var c = def.Center;
            r = Rect2.FromPoints(new[]
            {
                GeometryMath.Rotate(r.TopLeft, c, a.RotationDegrees), GeometryMath.Rotate(r.TopRight, c, a.RotationDegrees),
                GeometryMath.Rotate(r.BottomRight, c, a.RotationDegrees), GeometryMath.Rotate(r.BottomLeft, c, a.RotationDegrees),
            });
        }
        return r;
    }

    private static Rect2 PathBounds(SKPath p) => p.Bounds.ToRect2();

    private Rect2 LocalVisualBounds(Annotation a)
    {
        var sw = a.Stroke.IsVisible ? a.Stroke.Width : 0;
        var half = sw / 2 + (a.Stroke.Join == LineJoin.Miter ? sw * 1.5 : 0);
        switch (a)
        {
            case ArrowAnnotation:
            case LineAnnotation:
            {
                using var g = AnnotationGeometry.BuildLinear(a);
                var r = PathBounds(g.FullLine);
                foreach (var c in g.Caps) r = r.Union(PathBounds(c.Path));
                return r.Inflate(Math.Max(half, 0.5));
            }
            case ShapeAnnotation s:
            {
                using var p = AnnotationGeometry.ShapePath(s.Shape, s.Bounds, s.CornerRadius, s.Points);
                return (p.IsEmpty ? s.Bounds : PathBounds(p)).Inflate(half);
            }
            case CalloutAnnotation co:
            {
                using var p = CalloutGeometry.Build(co.ShapePresetId, co.Bounds, co.Tails);
                var r = PathBounds(p).Inflate(half);
                if (co.TextStyle.AutoSize && !co.Text.IsEmpty)
                {
                    var l = TextLayoutEngine.Layout(co.Text, co.TextStyle, co.Bounds.Width, co.Bounds.Height).Info;
                    r = r.Union(new Rect2(co.Bounds.X, co.Bounds.Y, l.ContentSize.Width, l.ContentSize.Height));
                }
                return r;
            }
            case TextAnnotation t:
            {
                var r = t.Bounds.Inflate(half);
                if (t.TextStyle.AutoSize && !t.Text.IsEmpty)
                {
                    var l = TextLayoutEngine.Layout(t.Text, t.TextStyle, t.Bounds.Width, t.Bounds.Height).Info;
                    var tb = new Rect2(t.Bounds.X, t.Bounds.Y, l.ContentSize.Width, l.ContentSize.Height);
                    if (t.TextStyle.OutlineColor is not null && t.TextStyle.OutlineWidth > 0) tb = tb.Inflate(t.TextStyle.OutlineWidth);
                    r = r.Union(tb);
                }
                return r;
            }
            case StepAnnotation st:
            {
                using var p = AnnotationGeometry.StepPath(st);
                return PathBounds(p).Inflate(half);
            }
            case PenAnnotation pen:
            {
                var pts = AnnotationGeometry.PenSamples(pen.Points, pen.Smoothing);
                if (pts.Count == 0) return Rect2.FromPoints(pen.Start, pen.End);
                var r = Rect2.FromPoints(pts.Select(p => p.ToPoint2()));
                var ext = pen.Stroke.Width / 2 * (pen.Nib is PenNib.Circle or PenNib.Horizontal or PenNib.Vertical ? 1 : 1.42) + 1;
                return r.Inflate(ext);
            }
            case HighlightAnnotation h:
                return h.Points is { Count: > 0 } ? h.Bounds.Inflate(h.Stroke.Width / 2 + 1) : h.Bounds;
            case MagnifyAnnotation m:
            {
                using var p = AnnotationGeometry.LensPath(m);
                var r = PathBounds(p).Inflate(half);
                if (m.ShowConnector && !m.SourceRect.IsEmpty) r = r.Union(m.SourceRect.Inflate(half));
                return r;
            }
            case CursorAnnotation cu:
                return CursorGlyphs.Box(cu).Inflate(2);
            case ImageAnnotation or StampAnnotation or BlurAnnotation or DropZoneAnnotation or SimplifyShapeAnnotation:
                return a.Bounds.Inflate(half);
            default:
                return a.Bounds.Inflate(half);
        }
    }

    public Geometry2 GetOutline(Annotation a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var figs = new List<GeometryFigure>();
        var sw = a.Stroke.IsVisible ? a.Stroke.Width : 0;

        void AddPath(SKPath p, bool filled, double strokeWidth)
        {
            foreach (var (pts, closed) in AnnotationGeometry.Flatten(p))
                figs.Add(new GeometryFigure(pts, closed, filled && closed, strokeWidth));
        }

        switch (a)
        {
            case ArrowAnnotation:
            case LineAnnotation:
            {
                using var g = AnnotationGeometry.BuildLinear(a);
                AddPath(g.FullLine, false, Math.Max(sw, 1));
                foreach (var c in g.Caps) AddPath(c.Path, c.Filled, c.Filled ? 0 : Math.Max(sw, 1));
                break;
            }
            case ShapeAnnotation s:
            {
                using var p = AnnotationGeometry.ShapePath(s.Shape, s.Bounds, s.CornerRadius, s.Points);
                AddPath(p, s.Fill.IsVisible, sw);
                break;
            }
            case CalloutAnnotation co:
            {
                using var p = CalloutGeometry.Build(co.ShapePresetId, co.Bounds, co.Tails);
                AddPath(p, true, sw);
                break;
            }
            case StepAnnotation st:
            {
                using var p = AnnotationGeometry.StepPath(st);
                AddPath(p, true, sw);
                break;
            }
            case PenAnnotation pen:
            {
                var pts = AnnotationGeometry.PenSamples(pen.Points, pen.Smoothing).Select(p => p.ToPoint2()).ToList();
                if (pts.Count == 0) break;
                figs.Add(new GeometryFigure(pts, false, false, Math.Max(pen.Stroke.Width, 1)));
                break;
            }
            case HighlightAnnotation h when h.Points is { Count: > 0 } hp:
                figs.Add(new GeometryFigure(AnnotationGeometry.PenSamples(hp, false).Select(p => p.ToPoint2()).ToList(), false, false, Math.Max(h.Stroke.Width, 1)));
                break;
            case MagnifyAnnotation m:
            {
                using var p = AnnotationGeometry.LensPath(m);
                AddPath(p, true, sw);
                if (m.ShowConnector && !m.SourceRect.IsEmpty)
                {
                    var sr = m.SourceRect;
                    figs.Add(new GeometryFigure(new[] { sr.TopLeft, sr.TopRight, sr.BottomRight, sr.BottomLeft }, true, false, sw));
                }
                break;
            }
            case BlurAnnotation b:
            {
                using var p = AnnotationGeometry.BlurPath(b);
                AddPath(p, true, 0);
                break;
            }
            case CursorAnnotation cu:
                figs.Add(new GeometryFigure(RectPoints(CursorGlyphs.Box(cu)), true, true, 0));
                break;
            default:
                figs.Add(new GeometryFigure(RectPoints(a.Bounds), true, true, sw));
                break;
        }

        if (Math.Abs(a.RotationDegrees) > 1e-9)
        {
            var c = a.Bounds.Center;
            for (var i = 0; i < figs.Count; i++)
            {
                var f = figs[i];
                figs[i] = f with { Points = f.Points.Select(p => GeometryMath.Rotate(p, c, a.RotationDegrees)).ToList() };
            }
        }
        return new Geometry2(figs);
    }

    private static IReadOnlyList<Point2> RectPoints(Rect2 r) => new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
}
