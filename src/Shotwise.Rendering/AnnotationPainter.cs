using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>Already-rendered content below an annotation, for blur / pixelate / magnify.</summary>
internal sealed class Backdrop : IDisposable
{
    public required SKImage Image { get; init; }
    /// <summary>Maps backdrop image pixels to device pixels.</summary>
    public SKMatrix ImageToDevice { get; init; } = SKMatrix.Identity;
    public bool Owned { get; init; } = true;
    public void Dispose() { if (Owned) Image.Dispose(); }
}

internal sealed class PaintContext
{
    public required ShotDocument Doc { get; init; }
    public required Page Page { get; init; }
    public bool Adornments { get; init; }
    /// <summary>Document-to-device matrix in effect when painting started (before any annotation rotation).</summary>
    public SKMatrix Base { get; set; } = SKMatrix.Identity;
    /// <summary>Supplies the backdrop for blur/magnify; may return null (then a neutral placeholder is drawn).</summary>
    public Func<SKCanvas, PaintContext, Annotation, Backdrop?>? BackdropProvider { get; init; }
}

/// <summary>Draws every annotation kind onto an SKCanvas (local coordinates = canvas pixels).</summary>
internal sealed class AnnotationPainter
{
    private readonly AssetRasterCache _assets;
    public AnnotationPainter(AssetRasterCache assets) => _assets = assets;

    public void Paint(SKCanvas canvas, PaintContext ctx, Annotation a)
    {
        if (a.Opacity <= 0) return;
        var bounds = a.Bounds;
        var center = bounds.Center.ToSk();
        ctx.Base = canvas.TotalMatrix;
        Backdrop? backdrop = null;
        try
        {
            if (a is BlurAnnotation { Mode: not BlurKind.Solid } or MagnifyAnnotation)
                backdrop = ctx.BackdropProvider?.Invoke(canvas, ctx, a);

            canvas.Save();
            if (Math.Abs(a.RotationDegrees) > 1e-6) canvas.RotateDegrees((float)a.RotationDegrees, center.X, center.Y);

            var shadow = a.Shadow is { Opacity: > 0 } sh && sh.Color.A > 0 ? sh : null;
            var multiply = a is HighlightAnnotation;
            var needLayer = a.Opacity < 0.999 || shadow is not null || multiply;
            SKPaint? layerPaint = null;
            if (needLayer)
            {
                layerPaint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)Math.Clamp(Math.Round(a.Opacity * 255), 0, 255)) };
                if (multiply) layerPaint.BlendMode = SKBlendMode.Multiply;
                if (shadow is not null)
                {
                    var (dx, dy) = shadow.Offset;
                    var rad = -a.RotationDegrees * Math.PI / 180.0; // keep light direction fixed in document space
                    var ldx = (float)(dx * Math.Cos(rad) - dy * Math.Sin(rad));
                    var ldy = (float)(dx * Math.Sin(rad) + dy * Math.Cos(rad));
                    var sigma = (float)Math.Max(0.01, shadow.Blur / 2);
                    layerPaint.ImageFilter = SKImageFilter.CreateDropShadow(ldx, ldy, sigma, sigma, shadow.Color.WithOpacity(Math.Clamp(shadow.Opacity, 0, 1)).ToSk());
                }
                canvas.SaveLayer(layerPaint);
            }
            try
            {
                PaintBody(canvas, ctx, a, backdrop);
            }
            finally
            {
                if (needLayer) canvas.Restore();
                layerPaint?.Dispose();
                canvas.Restore();
            }
        }
        finally
        {
            backdrop?.Dispose();
        }
    }

    private void PaintBody(SKCanvas c, PaintContext ctx, Annotation a, Backdrop? backdrop)
    {
        switch (a)
        {
            case ArrowAnnotation: PaintLinear(c, a); break;
            case LineAnnotation: PaintLinear(c, a); break;
            case ShapeAnnotation s: PaintShape(c, s); break;
            case CalloutAnnotation co: PaintCallout(c, ctx, co); break;
            case TextAnnotation t: PaintText(c, ctx, t); break;
            case StampAnnotation st: PaintStamp(c, ctx, st); break;
            case StepAnnotation sp: PaintStep(c, sp); break;
            case PenAnnotation pen: PaintPen(c, pen); break;
            case HighlightAnnotation h: PaintHighlight(c, h); break;
            case MagnifyAnnotation m: PaintMagnify(c, ctx, m, backdrop); break;
            case BlurAnnotation b: PaintBlur(c, b, backdrop); break;
            case ImageAnnotation im: PaintImage(c, ctx, im); break;
            case CursorAnnotation cu: PaintCursor(c, cu); break;
            case DropZoneAnnotation dz: PaintDropZone(c, ctx, dz); break;
            case SimplifyShapeAnnotation ss: PaintSimplify(c, ctx, ss); break;
        }
    }

    // ---------------------------------------------------------------- paints

    internal static SKPaint? MakeFill(Annotation a, Rect2 b) => MakeFill(a.Fill, b);

    internal static SKPaint? MakeFill(FillStyle f, Rect2 b)
    {
        if (!f.IsVisible) return null;
        var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        if (f.Kind == FillKind.Gradient && f.Gradient is { } g) p.Shader = MakeGradient(g, b);
        else p.Color = f.Color.ToSk();
        return p;
    }

    internal static SKShader? MakeGradient(GradientFill g, Rect2 b)
    {
        if (g.Stops.Count == 0) return null;
        var stops = g.Stops.OrderBy(s => s.Offset).ToList();
        var colors = stops.Select(s => s.Color.ToSk()).ToArray();
        var pos = stops.Select(s => (float)Math.Clamp(s.Offset, 0, 1)).ToArray();
        if (colors.Length == 1) return SKShader.CreateColor(colors[0]);
        if (g.Kind == GradientKind.Radial)
        {
            var r = (float)Math.Max(1, Math.Max(b.Width, b.Height) / 2);
            return SKShader.CreateRadialGradient(b.Center.ToSk(), r, colors, pos, SKShaderTileMode.Clamp);
        }
        var ang = g.AngleDegrees * Math.PI / 180.0;
        var dx = Math.Cos(ang);
        var dy = Math.Sin(ang);
        var half = (Math.Abs(dx) * b.Width + Math.Abs(dy) * b.Height) / 2;
        var c = b.Center;
        var p0 = new SKPoint((float)(c.X - dx * half), (float)(c.Y - dy * half));
        var p1 = new SKPoint((float)(c.X + dx * half), (float)(c.Y + dy * half));
        return SKShader.CreateLinearGradient(p0, p1, colors, pos, SKShaderTileMode.Clamp);
    }

    internal static SKPaint? MakeStroke(StrokeStyle s, bool ignoreDash = false)
    {
        if (!s.IsVisible) return null;
        var w = (float)s.Width;
        var p = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = w,
            Color = s.Color.ToSk(),
            StrokeJoin = s.Join switch { LineJoin.Miter => SKStrokeJoin.Miter, LineJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Round },
            StrokeMiter = 4,
        };
        if (!ignoreDash)
        {
            switch (s.Dash)
            {
                case StrokeDash.Dash: p.PathEffect = SKPathEffect.CreateDash(new[] { w * 3, w * 2 }, 0); break;
                case StrokeDash.Dot: p.StrokeCap = SKStrokeCap.Round; p.PathEffect = SKPathEffect.CreateDash(new[] { 0.01f, w * 2 }, 0); break;
                case StrokeDash.DashDot: p.PathEffect = SKPathEffect.CreateDash(new[] { w * 4, w * 2, w, w * 2 }, 0); break;
            }
        }
        return p;
    }

    private static void FillStroke(SKCanvas c, SKPath path, Annotation a, Rect2 bounds)
    {
        using var fill = MakeFill(a, bounds);
        if (fill is not null) c.DrawPath(path, fill);
        using var stroke = MakeStroke(a.Stroke);
        if (stroke is not null) c.DrawPath(path, stroke);
    }

    // ---------------------------------------------------------------- kinds

    private static void PaintLinear(SKCanvas c, Annotation a)
    {
        using var g = AnnotationGeometry.BuildLinear(a);
        var line = a as LineAnnotation;
        if (a.Stroke.IsVisible)
        {
            if (line is { DoubleLine: true })
            {
                using var outer = StrokeOutline(g.Line, a.Stroke.Width, SKStrokeCap.Butt);
                using var inner = StrokeOutline(g.Line, a.Stroke.Width / 3, SKStrokeCap.Butt);
                using var ring = outer.Op(inner, SKPathOp.Difference) ?? outer;
                using var fp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = a.Stroke.Color.ToSk() };
                c.DrawPath(ring, fp);
            }
            else
            {
                using var sp = MakeStroke(a.Stroke);
                if (sp is not null) c.DrawPath(g.Line, sp);
            }
        }
        if (!a.Stroke.IsVisible) return;
        using var capFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = a.Stroke.Color.ToSk() };
        using var capStroke = MakeStroke(a.Stroke with { Dash = StrokeDash.Solid, Join = LineJoin.Round }, true)!;
        foreach (var cap in g.Caps)
        {
            if (cap.Filled) c.DrawPath(cap.Path, capFill);
            else c.DrawPath(cap.Path, capStroke);
        }
    }

    private static SKPath StrokeOutline(SKPath src, double width, SKStrokeCap cap)
    {
        using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(0.1, width), StrokeCap = cap, StrokeJoin = SKStrokeJoin.Round };
        var dst = new SKPath();
        p.GetFillPath(src, dst);
        return dst;
    }

    private static void PaintShape(SKCanvas c, ShapeAnnotation s)
    {
        using var path = AnnotationGeometry.ShapePath(s.Shape, s.Bounds, s.CornerRadius, s.Points);
        FillStroke(c, path, s, s.Bounds);
    }

    private static void DrawText(SKCanvas c, RichText text, TextLayoutStyle style, Rect2 box, bool clip, string? placeholder, bool adornments, double textRotationDummy = 0)
    {
        _ = textRotationDummy;
        var rt = text;
        var showPlaceholder = false;
        if (rt.IsEmpty && adornments && !string.IsNullOrEmpty(placeholder))
        {
            rt = RichText.FromPlain(placeholder, "Segoe UI", 16, Argb.FromArgb(150, 128, 128, 128));
            showPlaceholder = true;
        }
        if (rt.Runs.Count == 0) return;
        var layout = TextLayoutEngine.Layout(rt, style, box.Width, box.Height);
        TextLayoutEngine.Draw(c, layout, new SKPoint((float)box.X, (float)box.Y), showPlaceholder ? style with { OutlineWidth = 0 } : style,
            clip ? box.ToSk() : null);
    }

    private static void PaintCallout(SKCanvas c, PaintContext ctx, CalloutAnnotation co)
    {
        using var path = CalloutGeometry.Build(co.ShapePresetId, co.Bounds, co.Tails);
        var union = co.Bounds;
        FillStroke(c, path, co, union);
        DrawText(c, co.Text, co.TextStyle, co.Bounds, !co.TextStyle.AutoSize, co.PlaceholderText, ctx.Adornments);
    }

    private static void PaintText(SKCanvas c, PaintContext ctx, TextAnnotation t)
    {
        var r = t.Bounds;
        using var fill = MakeFill(t, r);
        if (fill is not null) c.DrawRect(r.ToSk(), fill);
        using var stroke = MakeStroke(t.Stroke);
        if (stroke is not null) c.DrawRect(r.ToSk(), stroke);
        DrawText(c, t.Text, t.TextStyle, r, !t.TextStyle.AutoSize, t.PlaceholderText, ctx.Adornments);
    }

    private void PaintStamp(SKCanvas c, PaintContext ctx, StampAnnotation st)
    {
        var r = st.Bounds;
        if (r.IsEmpty) return;
        var devW = (int)Math.Ceiling(r.Width * Math.Abs(ctx.Base.ScaleX == 0 ? 1 : ctx.Base.ScaleX));
        var d = _assets.Get(ctx.Doc.Assets, st.Asset, devW);
        if (d is null)
        {
            DrawMissing(c, r);
            return;
        }
        DrawDrawable(c, d, r.ToSk(), st.PreserveAspect, null);
    }

    private void PaintImage(SKCanvas c, PaintContext ctx, ImageAnnotation im)
    {
        var r = im.Bounds;
        if (r.IsEmpty) return;
        var devW = (int)Math.Ceiling(r.Width * Math.Abs(ctx.Base.ScaleX == 0 ? 1 : ctx.Base.ScaleX));
        var d = _assets.Get(ctx.Doc.Assets, im.Asset, devW);
        if (d is null) DrawMissing(c, r);
        else
        {
            SKRect? crop = im.SourceCrop is { } sc ? new SKRect(sc.X, sc.Y, sc.Right, sc.Bottom) : null;
            DrawDrawable(c, d, r.ToSk(), false, crop);
        }
        using var stroke = MakeStroke(im.Stroke);
        if (stroke is not null) c.DrawRect(r.ToSk(), stroke);
    }

    private static void DrawMissing(SKCanvas c, Rect2 r)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(200, 60, 60, 200) };
        c.DrawRect(r.ToSk(), p);
        c.DrawLine((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom, p);
        c.DrawLine((float)r.Right, (float)r.Top, (float)r.Left, (float)r.Bottom, p);
    }

    internal static void DrawDrawable(SKCanvas c, AssetDrawable d, SKRect dest, bool preserveAspect, SKRect? crop)
    {
        var src = crop ?? new SKRect(0, 0, d.Width, d.Height);
        if (src.Width <= 0 || src.Height <= 0) return;
        var target = dest;
        if (preserveAspect)
        {
            var k = Math.Min(dest.Width / src.Width, dest.Height / src.Height);
            var w = src.Width * k;
            var h = src.Height * k;
            target = new SKRect(dest.MidX - w / 2, dest.MidY - h / 2, dest.MidX + w / 2, dest.MidY + h / 2);
        }
        if (d.Picture is { } pic)
        {
            c.Save();
            var m = SKMatrix.CreateScale(target.Width / src.Width, target.Height / src.Height);
            m = SkiaInterop.Mul(SKMatrix.CreateTranslation(target.Left, target.Top), SkiaInterop.Mul(m, SKMatrix.CreateTranslation(-src.Left, -src.Top)));
            c.ClipRect(target, SKClipOperation.Intersect, true);
            c.DrawPicture(pic, in m);
            c.Restore();
        }
        else if (d.Image is { } img)
        {
            using var paint = new SKPaint { IsAntialias = true };
            var down = target.Width < src.Width || target.Height < src.Height;
            var sampling = down ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            c.DrawImage(img, src, target, sampling, paint);
        }
    }

    private static void PaintStep(SKCanvas c, StepAnnotation s)
    {
        using var path = AnnotationGeometry.StepPath(s);
        var r = Rect2.FromPoints(s.Start, s.End);
        FillStroke(c, path, s, r);
        var label = s.Label;
        var size = Math.Min(r.Width, r.Height);
        var fs = size * (label.Length switch { <= 1 => 0.58, 2 => 0.46, 3 => 0.36, _ => 0.3 });
        if (fs < 1) return;
        var text = RichText.FromPlain(label, s.FontFamily, fs, s.TextColor, bold: true);
        var style = new TextLayoutStyle { HAlign = HorizontalAlign.Center, VAlign = VerticalAlign.Middle, Padding = new Thickness2(0) };
        var layout = TextLayoutEngine.Layout(text, style, r.Width + 4, r.Height);
        TextLayoutEngine.Draw(c, layout, new SKPoint((float)r.X - 2, (float)r.Y), style);
    }

    private static void PaintPen(SKCanvas c, PenAnnotation pen)
    {
        if (!pen.Stroke.IsVisible || pen.Points.Count == 0) return;
        var samples = AnnotationGeometry.PenSamples(pen.Points, pen.Smoothing);
        var w = (float)pen.Stroke.Width;
        if (pen.Nib == PenNib.Circle)
        {
            using var sp = new SKPaint
            {
                IsAntialias = pen.Antialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = w,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = pen.Stroke.Color.ToSk(),
            };
            if (samples.Count == 1)
            {
                using var dot = new SKPaint { IsAntialias = pen.Antialias, Style = SKPaintStyle.Fill, Color = pen.Stroke.Color.ToSk() };
                c.DrawCircle(samples[0], w / 2, dot);
                return;
            }
            using var path = new SKPath();
            path.MoveTo(samples[0]);
            for (var i = 1; i < samples.Count; i++) path.LineTo(samples[i]);
            c.DrawPath(path, sp);
            return;
        }
        using var nib = AnnotationGeometry.PenNibPath(samples, pen.Nib, w);
        using var fp = new SKPaint { IsAntialias = pen.Antialias, Style = SKPaintStyle.Fill, Color = pen.Stroke.Color.ToSk() };
        c.DrawPath(nib, fp);
    }

    private static void PaintHighlight(SKCanvas c, HighlightAnnotation h)
    {
        if (h.Points is { Count: > 0 } pts)
        {
            if (!h.Stroke.IsVisible) return;
            var samples = AnnotationGeometry.PenSamples(pts, false);
            using var sp = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)h.Stroke.Width,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = h.Stroke.Color.ToSk(),
            };
            if (samples.Count == 1)
            {
                using var dot = new SKPaint { IsAntialias = true, Color = h.Stroke.Color.ToSk() };
                c.DrawCircle(samples[0], (float)h.Stroke.Width / 2, dot);
                return;
            }
            using var path = new SKPath();
            path.MoveTo(samples[0]);
            for (var i = 1; i < samples.Count; i++) path.LineTo(samples[i]);
            c.DrawPath(path, sp);
            return;
        }
        using var fill = MakeFill(h.Fill, h.Bounds);
        if (fill is not null) c.DrawRect(h.Bounds.ToSk(), fill);
    }

    private void PaintMagnify(SKCanvas c, PaintContext ctx, MagnifyAnnotation m, Backdrop? bd)
    {
        using var lens = AnnotationGeometry.LensPath(m);
        var lensB = m.Bounds;
        if (lensB.IsEmpty) return;
        var lc = lensB.Center;
        var src = m.SourceRect;
        var sc = src.IsEmpty ? lc : src.Center;
        var k = (float)Math.Max(0.1, m.Scale);

        // connector (below the lens content)
        if (m.ShowConnector && !src.IsEmpty)
        {
            using var cp = MakeStroke(m.Stroke with { Dash = StrokeDash.Solid }) ?? new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Gray, IsAntialias = true };
            c.Save();
            c.ClipPath(lens, SKClipOperation.Difference, true);
            c.ClipRect(src.ToSk(), SKClipOperation.Difference, true);
            c.DrawLine(lc.ToSk(), sc.ToSk(), cp);
            c.Restore();
            c.DrawRect(src.ToSk(), cp);
        }

        // lens content
        if (bd is not null)
        {
            var mdoc = SkiaInterop.Mul(SKMatrix.CreateTranslation((float)lc.X, (float)lc.Y),
                SkiaInterop.Mul(SKMatrix.CreateScale(k, k), SKMatrix.CreateTranslation((float)-sc.X, (float)-sc.Y)));
            var ctm = c.TotalMatrix;
            var l = SkiaInterop.Mul(SkiaInterop.Invert(ctm),
                SkiaInterop.Mul(ctx.Base, SkiaInterop.Mul(mdoc, SkiaInterop.Mul(SkiaInterop.Invert(ctx.Base), bd.ImageToDevice))));
            using var shader = bd.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKCubicResampler.Mitchell), l);
            using var paint = new SKPaint { IsAntialias = true, Shader = shader };
            c.DrawPath(lens, paint);
        }
        else
        {
            using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(240, 240, 240) };
            c.DrawPath(lens, bg);
        }
        using var border = MakeStroke(m.Stroke with { Dash = StrokeDash.Solid });
        if (border is not null) c.DrawPath(lens, border);
    }

    private static void PaintBlur(SKCanvas c, BlurAnnotation b, Backdrop? bd)
    {
        using var path = AnnotationGeometry.BlurPath(b);
        if (b.Mode == BlurKind.Solid || bd is null)
        {
            using var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = (b.Mode == BlurKind.Solid ? b.SolidColor : Argb.FromArgb(255, 128, 128, 128)).ToSk() };
            c.DrawPath(path, sp);
            return;
        }
        var ctm = c.TotalMatrix;
        var imgToLocal = SkiaInterop.Mul(SkiaInterop.Invert(ctm), bd.ImageToDevice);
        var localToImg = SkiaInterop.Invert(imgToLocal);
        var imgBounds = localToImg.MapRect(path.Bounds);
        var scale = MathF.Sqrt(localToImg.ScaleX * localToImg.ScaleX + localToImg.SkewY * localToImg.SkewY);
        if (scale <= 0) scale = 1;
        var intensity = Math.Clamp(b.Intensity, 1, 100);
        var full = new SKRectI(0, 0, bd.Image.Width, bd.Image.Height);

        if (b.Mode == BlurKind.Gaussian)
        {
            var sigma = (float)Math.Max(1, intensity * 0.2) * scale;
            var pad = (int)Math.Ceiling(sigma * 3) + 2;
            var region = SKRectI.Intersect(full, SKRectI.Round(SKRect.Inflate(imgBounds, pad, pad)));
            if (region.Width <= 0 || region.Height <= 0) return;
            using var sub = bd.Image.Subset(region);
            c.Save();
            c.ClipPath(path, SKClipOperation.Intersect, true);
            c.Concat(in imgToLocal);
            using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { ImageFilter = filter, IsAntialias = true };
            c.DrawImage(sub, region.Left, region.Top, new SKSamplingOptions(SKFilterMode.Linear), paint);
            c.Restore();
        }
        else
        {
            var block = (float)(1 + intensity * 0.18) * scale;
            block = Math.Max(2, block);
            var region = SKRectI.Intersect(full, SKRectI.Round(SKRect.Inflate(imgBounds, 1, 1)));
            if (region.Width <= 0 || region.Height <= 0) return;
            using var sub = bd.Image.Subset(region);
            var bw = Math.Max(1, (int)Math.Ceiling(region.Width / block));
            var bh = Math.Max(1, (int)Math.Ceiling(region.Height / block));
            using var tiny = new SKBitmap(SkiaInterop.Info(bw, bh));
            using (var tc = new SKCanvas(tiny))
            {
                tc.Clear(SKColors.Transparent);
                tc.Scale(1f / block);
                tc.DrawImage(sub, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            using var tinyImg = SKImage.FromBitmap(tiny);
            c.Save();
            c.ClipPath(path, SKClipOperation.Intersect, true);
            c.Concat(in imgToLocal);
            var dest = new SKRect(region.Left, region.Top, region.Left + bw * block, region.Top + bh * block);
            using var paint = new SKPaint { IsAntialias = false };
            c.DrawImage(tinyImg, dest, new SKSamplingOptions(SKFilterMode.Nearest), paint);
            c.Restore();
        }
    }

    private void PaintDropZone(SKCanvas c, PaintContext ctx, DropZoneAnnotation dz)
    {
        var r = dz.Bounds;
        if (r.IsEmpty) return;
        var rr = (float)Math.Clamp(dz.CornerRadius, 0, Math.Min(r.Width, r.Height) / 2);
        using var clip = new SKPath();
        clip.AddRoundRect(r.ToSk(), rr, rr);
        var d = dz.Content is { IsEmpty: false } id ? _assets.Get(ctx.Doc.Assets, id, (int)Math.Ceiling(r.Width * Math.Abs(ctx.Base.ScaleX == 0 ? 1 : ctx.Base.ScaleX) * Math.Max(1, dz.ContentZoom))) : null;
        if (d is not null && d.Width > 0 && d.Height > 0)
        {
            var cover = Math.Max(r.Width / d.Width, r.Height / d.Height) * Math.Max(0.01, dz.ContentZoom);
            var w = d.Width * cover;
            var h = d.Height * cover;
            var cx = r.Center.X + dz.ContentOffset.X;
            var cy = r.Center.Y + dz.ContentOffset.Y;
            c.Save();
            c.ClipPath(clip, SKClipOperation.Intersect, true);
            DrawDrawable(c, d, new SKRect((float)(cx - w / 2), (float)(cy - h / 2), (float)(cx + w / 2), (float)(cy + h / 2)), false, null);
            c.Restore();
            if (ctx.Adornments)
            {
                using var sp = MakeStroke(dz.Stroke);
                if (sp is not null) c.DrawPath(clip, sp);
            }
            return;
        }
        if (!ctx.Adornments) return; // empty slots never export
        using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(128, 128, 128, 28) };
        c.DrawPath(clip, bg);
        using var stroke = MakeStroke(dz.Stroke) ?? new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Gray, IsAntialias = true };
        c.DrawPath(clip, stroke);
        var cc = r.Center;
        var arm = (float)Math.Min(Math.Min(r.Width, r.Height) * 0.18, 24);
        using var plus = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, StrokeCap = SKStrokeCap.Round, Color = new SKColor(128, 128, 128, 200) };
        c.DrawLine((float)cc.X - arm, (float)cc.Y, (float)cc.X + arm, (float)cc.Y, plus);
        c.DrawLine((float)cc.X, (float)cc.Y - arm, (float)cc.X, (float)cc.Y + arm, plus);
    }

    private static void PaintSimplify(SKCanvas c, PaintContext ctx, SimplifyShapeAnnotation s)
    {
        var r = s.Bounds;
        if (r.IsEmpty) return;
        var color = s.Fill.IsVisible ? s.Fill.Color : Argb.LightGray;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.ToSk() };
        var rect = r.ToSk();
        switch (s.Shape)
        {
            case SimplifyShapeKind.Rectangle: c.DrawRect(rect, fill); break;
            case SimplifyShapeKind.RoundedRectangle:
            {
                var rad = Math.Min(rect.Width, rect.Height) * 0.2f;
                c.DrawRoundRect(rect, rad, rad, fill);
                break;
            }
            case SimplifyShapeKind.Ellipse: c.DrawOval(rect, fill); break;
            case SimplifyShapeKind.TextLeft:
            case SimplifyShapeKind.TextCenter:
            case SimplifyShapeKind.TextRight:
                DrawTextBars(c, ctx.Page.Simplify, s, rect, fill);
                break;
            case SimplifyShapeKind.Placeholder:
                DrawPlaceholder(c, s.PlaceholderId, rect, fill, color);
                break;
        }
    }

    private static void DrawTextBars(SKCanvas c, SimplifySettings st, SimplifyShapeAnnotation s, SKRect r, SKPaint fill)
    {
        var barH = (float)Math.Clamp(st.TextHeight * 0.8, 2, Math.Max(2, r.Height));
        var pitch = (float)(Math.Max(st.TextHeight, 3) * 1.6 * Math.Max(0.5, st.LineSpacing));
        var rows = Math.Max(1, (int)Math.Floor((r.Height + (pitch - barH)) / pitch));
        var total = rows * pitch - (pitch - barH);
        var top = r.Top + Math.Max(0, (r.Height - total) / 2);
        var rng = new Random(HashCode.Combine(s.Id.GetHashCode(), 17));
        for (var i = 0; i < rows; i++)
        {
            var last = i == rows - 1 && rows > 1;
            var frac = last ? 0.35f + (float)rng.NextDouble() * 0.3f : 0.82f + (float)rng.NextDouble() * 0.18f;
            var w = r.Width * frac;
            var x = s.Shape switch
            {
                SimplifyShapeKind.TextCenter => r.Left + (r.Width - w) / 2,
                SimplifyShapeKind.TextRight => r.Right - w,
                _ => r.Left,
            };
            var y = top + i * pitch;
            c.DrawRoundRect(new SKRect(x, y, x + w, y + barH), barH / 2, barH / 2, fill);
        }
    }

    private static void DrawPlaceholder(SKCanvas c, string? id, SKRect r, SKPaint fill, Argb color)
    {
        var glyph = color.Luminance > 0.6 ? new SKColor(255, 255, 255, 230) : new SKColor(255, 255, 255, 230);
        using var gp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = glyph };
        using var gs = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, Math.Min(r.Width, r.Height) * 0.05f), Color = glyph, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        var m = Math.Min(r.Width, r.Height);
        switch (id?.ToLowerInvariant())
        {
            case "avatar":
            {
                c.DrawOval(r, fill);
                c.Save();
                using var clip = new SKPath();
                clip.AddOval(r);
                c.ClipPath(clip, SKClipOperation.Intersect, true);
                c.DrawCircle(r.MidX, r.Top + r.Height * 0.38f, m * 0.18f, gp);
                c.DrawOval(new SKRect(r.MidX - m * 0.34f, r.Top + r.Height * 0.62f, r.MidX + m * 0.34f, r.Top + r.Height * 1.2f), gp);
                c.Restore();
                break;
            }
            case "video":
            {
                var rad = m * 0.08f;
                c.DrawRoundRect(r, rad, rad, fill);
                using var tri = new SKPath();
                tri.MoveTo(r.MidX - m * 0.12f, r.MidY - m * 0.18f);
                tri.LineTo(r.MidX + m * 0.2f, r.MidY);
                tri.LineTo(r.MidX - m * 0.12f, r.MidY + m * 0.18f);
                tri.Close();
                c.DrawPath(tri, gp);
                break;
            }
            case "cursor":
            {
                c.DrawRoundRect(r, m * 0.08f, m * 0.08f, fill);
                var cr = new SKRect(r.MidX - m * 0.2f, r.MidY - m * 0.3f, r.MidX + m * 0.2f, r.MidY + m * 0.3f);
                using var cp = CursorGlyphs.Build(CursorKind.Arrow, cr);
                c.DrawPath(cp, gp);
                break;
            }
            case "window-buttons":
            {
                var rad = Math.Min(r.Height / 2, r.Width / 8);
                var gap = rad * 2.6f;
                var cx = r.Left + rad * 1.5f;
                for (var i = 0; i < 3; i++) c.DrawCircle(cx + i * gap, r.MidY, rad, fill);
                break;
            }
            default: // "image" and anything unknown: frame with sun + hills
            {
                c.DrawRoundRect(r, m * 0.06f, m * 0.06f, fill);
                c.DrawCircle(r.Left + r.Width * 0.3f, r.Top + r.Height * 0.32f, m * 0.08f, gp);
                using var hills = new SKPath();
                hills.MoveTo(r.Left + r.Width * 0.12f, r.Bottom - r.Height * 0.15f);
                hills.LineTo(r.Left + r.Width * 0.38f, r.Top + r.Height * 0.5f);
                hills.LineTo(r.Left + r.Width * 0.55f, r.Top + r.Height * 0.7f);
                hills.LineTo(r.Left + r.Width * 0.7f, r.Top + r.Height * 0.45f);
                hills.LineTo(r.Right - r.Width * 0.12f, r.Bottom - r.Height * 0.15f);
                hills.Close();
                c.DrawPath(hills, gp);
                break;
            }
        }
        _ = gs;
    }

    private static void PaintCursor(SKCanvas c, CursorAnnotation cu)
    {
        var r = CursorGlyphs.Box(cu);
        using var path = CursorGlyphs.Build(cu.Cursor, r.ToSk());
        var k = (float)Math.Min(r.Width, r.Height) / 24f;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = cu.Cursor == CursorKind.IBeam ? new SKColor(0, 0, 0) : SKColors.White };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 1.4f * k), Color = SKColors.Black, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, fill);
        c.DrawPath(path, stroke);
    }
}
