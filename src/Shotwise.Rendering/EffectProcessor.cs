using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>
/// Raster effect engine (EFX-01..15). All geometry and masks (torn / wave / saw / sharktooth / bevel / fade edges, page curl,
/// perspective / shear) are generated procedurally. Stateless and thread-safe.
/// </summary>
public sealed partial class SkiaEffectProcessor : IEffectProcessor
{
    private readonly ILogger<SkiaEffectProcessor> _log;

    public SkiaEffectProcessor(ILogger<SkiaEffectProcessor>? log = null) => _log = log ?? NullLogger<SkiaEffectProcessor>.Instance;

    public EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effects);
        var cur = src;
        var offset = Point2.Zero;
        foreach (var e in effects)
        {
            if (e is null || !e.Enabled) continue;
            var r = Apply(cur, e, md);
            cur = r.Image;
            offset = new Point2(offset.X + r.ContentOffset.X, offset.Y + r.ContentOffset.Y);
        }
        return new EffectResult(cur, offset);
    }

    public BgraImage Preview(BgraImage src, EffectSpec e, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(src);
        var small = ImageOps.Downscale(src, Math.Max(1, maxSide));
        var f = (double)small.Width / src.Width;
        var scaled = Math.Abs(f - 1) < 1e-9 ? e : ScaleEffect(e, f);
        return Apply(small, scaled, null).Image;
    }

    /// <summary>Scales pixel-based parameters so a preview on a downscaled image resembles the full-size result.</summary>
    internal static EffectSpec ScaleEffect(EffectSpec e, double f) => e switch
    {
        BorderEffect b => b with { Width = Math.Max(1, b.Width * f) },
        EdgeEffect ed => ed with { Size = Math.Max(1, ed.Size * f) },
        ShadowEffect s => s with { Size = s.Size * f, Blur = s.Blur * f },
        PageCurlEffect pc => pc with { Size = pc.Size * f },
        CaptionEffect c => c with { FontSize = Math.Max(4, c.FontSize * f) },
        TrimEffect t => t with { Left = (int)(t.Left * f), Top = (int)(t.Top * f), Right = (int)(t.Right * f), Bottom = (int)(t.Bottom * f) },
        SpotlightMagnifyEffect sm => sm with { Region = new Rect2(sm.Region.X * f, sm.Region.Y * f, sm.Region.Width * f, sm.Region.Height * f), Blur = sm.Blur * f },
        ResizeEffect r => r with { Width = r.Width is { } w ? (int)Math.Max(1, w * f) : null, Height = r.Height is { } h ? (int)Math.Max(1, h * f) : null },
        _ => e,
    };

    public EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        if (!effect.Enabled) return new EffectResult(src, Point2.Zero);
        try
        {
            return effect switch
            {
                BorderEffect b => ApplyBorder(src, b),
                EdgeEffect ed => ApplyEdge(src, ed),
                ShadowEffect sh => ApplyShadow(src, sh),
                PerspectiveEffect p => ApplyPerspective(src, p),
                PageCurlEffect pc => ApplyPageCurl(src, pc),
                ColorAdjustEffect ca => new EffectResult(ApplyColorAdjust(src, ca), Point2.Zero),
                ColorReplaceEffect cr => new EffectResult(ApplyColorReplace(src, cr), Point2.Zero),
                FilterEffect f => new EffectResult(ApplyFilter(src, f), Point2.Zero),
                WatermarkEffect w => new EffectResult(ApplyWatermark(src, w), Point2.Zero),
                CaptionEffect c => ApplyCaption(src, c, md),
                ResizeEffect r => new EffectResult(ApplyResize(src, r), Point2.Zero),
                RotateEffect r => ApplyRotate(src, r),
                TrimEffect t => ApplyTrim(src, t),
                SpotlightMagnifyEffect s => new EffectResult(ApplySpotlight(src, s), Point2.Zero),
                TransparencyEffect t => new EffectResult(ApplyTransparency(src, t), Point2.Zero),
                _ => new EffectResult(src, Point2.Zero), // UnknownEffect: pass through
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ShotwiseException)
        {
            _log.LogWarning(ex, "Effect {Effect} failed; image passed through", effect.GetType().Name);
            return new EffectResult(src, Point2.Zero);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static SKSurface Surface(int w, int h)
    {
        if ((long)w * h > BgraImage.MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Effect result exceeds the maximum supported size.");
        var s = SKSurface.Create(SkiaInterop.Info(Math.Max(1, w), Math.Max(1, h)));
        s.Canvas.Clear(SKColors.Transparent);
        return s;
    }

    internal static BgraImage Read(SKSurface s, BgraImage like)
    {
        s.Canvas.Flush();
        using var snap = s.Snapshot();
        return SkiaInterop.ToBgra(snap, like.DpiX, like.DpiY);
    }

    internal static SKImage ToSk(BgraImage img) => SkiaInterop.ToImage(img);

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest);
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);

    // ------------------------------------------------------------------ EFX-01 border

    private static EffectResult ApplyBorder(BgraImage src, BorderEffect b)
    {
        var bw = (int)Math.Round(Math.Max(0, b.Width));
        if (bw <= 0 || b.Color.A == 0) return new EffectResult(src, Point2.Zero);
        using var img = ToSk(src);
        if (b.Outside)
        {
            using var s = Surface(src.Width + 2 * bw, src.Height + 2 * bw);
            using var p = new SKPaint { Color = b.Color.ToSk() };
            s.Canvas.DrawRect(0, 0, src.Width + 2 * bw, src.Height + 2 * bw, p);
            // clear under the image so translucent images do not show the border colour through
            using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
            s.Canvas.DrawRect(bw, bw, src.Width, src.Height, clear);
            s.Canvas.DrawImage(img, bw, bw);
            return new EffectResult(Read(s, src), new Point2(bw, bw));
        }
        using var s2 = Surface(src.Width, src.Height);
        s2.Canvas.DrawImage(img, 0, 0);
        using var p2 = new SKPaint { Color = b.Color.ToSk(), Style = SKPaintStyle.Stroke, StrokeWidth = bw * 2 };
        s2.Canvas.DrawRect(0, 0, src.Width, src.Height, p2); // stroke is centred on the edge: inner half = bw
        return new EffectResult(Read(s2, src), Point2.Zero);
    }

    // ------------------------------------------------------------------ EFX-03 shadow

    private static (double Dx, double Dy) DirectionVector(ShadowDirection d) => d switch
    {
        ShadowDirection.TopLeft => (-1, -1),
        ShadowDirection.Top => (0, -1),
        ShadowDirection.TopRight => (1, -1),
        ShadowDirection.Left => (-1, 0),
        ShadowDirection.Right => (1, 0),
        ShadowDirection.BottomLeft => (-1, 1),
        ShadowDirection.Bottom => (0, 1),
        ShadowDirection.BottomRight => (1, 1),
        _ => (0, 0),
    };

    private static EffectResult ApplyShadow(BgraImage src, ShadowEffect sh)
        => ShadowCore(src, sh.Color, DirectionVector(sh.Direction), sh.Size, sh.Blur, sh.Opacity, sh.Direction == ShadowDirection.AllSides, null);

    /// <summary>Drop shadow around the image. <paramref name="keepSides"/> (when set) limits which sides keep the extra padding.</summary>
    internal static EffectResult ShadowCore(BgraImage src, Argb color, (double Dx, double Dy) dir, double size, double blur, double opacity, bool allSides, EdgeSides? keepSides)
    {
        var norm = Math.Sqrt(dir.Dx * dir.Dx + dir.Dy * dir.Dy);
        double dx = 0, dy = 0;
        if (norm > 0) { dx = dir.Dx / norm * size; dy = dir.Dy / norm * size; }
        var sigma = Math.Max(0.01, (allSides ? blur + size : blur) / 2);
        var ext = (int)Math.Ceiling(sigma * 3) + 1;
        var left = (int)Math.Max(0, Math.Ceiling(ext - dx));
        var right = (int)Math.Max(0, Math.Ceiling(ext + dx));
        var top = (int)Math.Max(0, Math.Ceiling(ext - dy));
        var bottom = (int)Math.Max(0, Math.Ceiling(ext + dy));
        if (keepSides is { } ks)
        {
            if (!ks.HasFlag(EdgeSides.Left)) left = 0;
            if (!ks.HasFlag(EdgeSides.Right)) right = 0;
            if (!ks.HasFlag(EdgeSides.Top)) top = 0;
            if (!ks.HasFlag(EdgeSides.Bottom)) bottom = 0;
        }
        using var img = ToSk(src);
        using var s = Surface(src.Width + left + right, src.Height + top + bottom);
        var a = (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(opacity, 0, 1)), 0, 255);
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateDropShadowOnly((float)dx, (float)dy, (float)sigma, (float)sigma, new SKColor(color.R, color.G, color.B, a)),
        };
        s.Canvas.DrawImage(img, left, top, Nearest, paint);
        s.Canvas.DrawImage(img, left, top);
        return new EffectResult(Read(s, src), new Point2(left, top));
    }
}
