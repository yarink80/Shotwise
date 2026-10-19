using System.IO;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Annotations;
using Shotwise.Core.Imaging;
using SkiaSharp;

namespace Shotwise.Rendering;

public sealed partial class SkiaEffectProcessor
{
    // ------------------------------------------------------------------ EFX-11 resize

    private static BgraImage ApplyResize(BgraImage src, ResizeEffect r)
    {
        int tw, th;
        if (r.Percent is { } pc && pc > 0)
        {
            tw = (int)Math.Round(src.Width * pc / 100.0);
            th = (int)Math.Round(src.Height * pc / 100.0);
        }
        else if (r.Width is > 0 or null && r.Height is > 0 or null && (r.Width is not null || r.Height is not null))
        {
            var aspect = (double)src.Width / src.Height;
            if (r.Width is { } w && r.Height is { } h)
            {
                if (r.KeepAspect)
                {
                    var k = Math.Min((double)w / src.Width, (double)h / src.Height);
                    tw = (int)Math.Round(src.Width * k);
                    th = (int)Math.Round(src.Height * k);
                }
                else { tw = w; th = h; }
            }
            else if (r.Width is { } w1) { tw = w1; th = r.KeepAspect ? (int)Math.Round(w1 / aspect) : src.Height; }
            else { var h1 = r.Height!.Value; th = h1; tw = r.KeepAspect ? (int)Math.Round(h1 * aspect) : src.Width; }
        }
        else { tw = src.Width; th = src.Height; }
        tw = Math.Max(1, tw);
        th = Math.Max(1, th);
        double dpiX = r.Dpi is > 0 ? r.Dpi.Value : src.DpiX;
        double dpiY = r.Dpi is > 0 ? r.Dpi.Value : src.DpiY;
        if (tw == src.Width && th == src.Height)
            return new BgraImage(src.Width, src.Height, src.Pixels) { DpiX = dpiX, DpiY = dpiY };
        using var img = ToSk(src);
        using var s = Surface(tw, th);
        var down = tw < src.Width || th < src.Height;
        var sampling = !r.Smooth ? Nearest : down ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var paint = new SKPaint { IsAntialias = true };
        s.Canvas.DrawImage(img, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, tw, th), sampling, paint);
        s.Canvas.Flush();
        using var snap = s.Snapshot();
        return SkiaInterop.ToBgra(snap, dpiX, dpiY);
    }

    // ------------------------------------------------------------------ EFX-12 rotate / flip

    private static EffectResult ApplyRotate(BgraImage src, RotateEffect r)
    {
        var deg = ((r.Degrees % 360) + 360) % 360;
        if (deg == 0 && !r.FlipH && !r.FlipV) return new EffectResult(src, Point2.Zero);
        double w = src.Width, h = src.Height;
        var rad = deg * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double nw = w, nh = h;
        if (r.ResizeCanvas)
        {
            nw = Math.Abs(w * cos) + Math.Abs(h * sin);
            nh = Math.Abs(w * sin) + Math.Abs(h * cos);
        }
        // snap exact quarter turns to avoid fractional sizes
        var quarter = Math.Abs(deg % 90) < 1e-9;
        var ow = Math.Max(1, (int)Math.Round(nw - (quarter ? 0 : 0)));
        var oh = Math.Max(1, (int)Math.Round(nh));
        if (r.ResizeCanvas && !quarter) { ow = (int)Math.Ceiling(nw - 1e-6); oh = (int)Math.Ceiling(nh - 1e-6); }

        using var img = ToSk(src);
        using var s = Surface(ow, oh);
        var c = s.Canvas;
        c.Translate(ow / 2f, oh / 2f);
        c.RotateDegrees((float)deg);
        c.Scale(r.FlipH ? -1 : 1, r.FlipV ? -1 : 1);
        c.Translate((float)(-w / 2), (float)(-h / 2));
        using var paint = new SKPaint { IsAntialias = true };
        c.DrawImage(img, 0, 0, quarter ? Nearest : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        // translation of the original's centre to the new image's centre
        return new EffectResult(Read(s, src), new Point2((ow - w) / 2, (oh - h) / 2));
    }

    // ------------------------------------------------------------------ EFX-13 trim

    private static EffectResult ApplyTrim(BgraImage src, TrimEffect t)
    {
        int l = 0, tp = 0, r = src.Width, b = src.Height;
        if (t.Auto)
        {
            var px = src.Pixels;
            var w = src.Width;
            var h = src.Height;
            var key = src.GetPixel(0, 0);
            var tol = Math.Clamp(t.Tolerance, 0, 1);
            bool Same(int x, int y)
            {
                var c = src.GetPixel(x, y);
                if (key.A == 0 && c.A == 0) return true;
                return Argb.Distance(c, key) <= tol;
            }
            bool RowSame(int y) { for (var x = 0; x < w; x++) if (!Same(x, y)) return false; return true; }
            bool ColSame(int x, int y0, int y1) { for (var y = y0; y < y1; y++) if (!Same(x, y)) return false; return true; }
            while (tp < h && RowSame(tp)) tp++;
            if (tp >= h) return new EffectResult(src, Point2.Zero); // uniform image: nothing to trim to
            b = h;
            while (b > tp + 1 && RowSame(b - 1)) b--;
            while (l < w && ColSame(l, tp, b)) l++;
            r = w;
            while (r > l + 1 && ColSame(r - 1, tp, b)) r--;
            _ = px;
        }
        else
        {
            l = Math.Clamp(t.Left, 0, src.Width - 1);
            tp = Math.Clamp(t.Top, 0, src.Height - 1);
            r = Math.Clamp(src.Width - t.Right, l + 1, src.Width);
            b = Math.Clamp(src.Height - t.Bottom, tp + 1, src.Height);
        }
        if (l == 0 && tp == 0 && r == src.Width && b == src.Height) return new EffectResult(src, Point2.Zero);
        return new EffectResult(src.Crop(new RectI(l, tp, r - l, b - tp)), new Point2(-l, -tp));
    }

    // ------------------------------------------------------------------ EFX-14 spotlight & magnify

    private static BgraImage ApplySpotlight(BgraImage src, SpotlightMagnifyEffect e)
    {
        var region = e.Region.Normalize().Intersect(new Rect2(0, 0, src.Width, src.Height));
        if (region.IsEmpty) return src;
        using var img = ToSk(src);
        using var s = Surface(src.Width, src.Height);
        var c = s.Canvas;
        var rect = region.ToSk();
        // outside: blurred + dimmed
        c.Save();
        c.ClipRect(rect, SKClipOperation.Difference, false);
        var sigma = (float)Math.Max(0, e.Blur / 2);
        using (var paint = new SKPaint { ImageFilter = sigma > 0.01f ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null })
            c.DrawImage(img, 0, 0, Nearest, paint);
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Clamp(Math.Round(e.DimOpacity * 255), 0, 255)) })
            c.DrawRect(0, 0, src.Width, src.Height, dim);
        c.Restore();
        // inside: original, optionally magnified about the region centre
        c.Save();
        c.ClipRect(rect, SKClipOperation.Intersect, true);
        var mag = (float)Math.Max(1, e.Magnify);
        if (mag > 1.001f)
        {
            var cc = region.Center;
            c.Translate((float)cc.X, (float)cc.Y);
            c.Scale(mag, mag);
            c.Translate((float)-cc.X, (float)-cc.Y);
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(img, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        else c.DrawImage(img, 0, 0, Nearest);
        c.Restore();
        return Read(s, src);
    }

    // ------------------------------------------------------------------ EFX-09 watermark

    private static BgraImage ApplyWatermark(BgraImage src, WatermarkEffect e)
    {
        if (string.IsNullOrWhiteSpace(e.ImagePath) || !File.Exists(e.ImagePath)) return src;
        byte[] data;
        try { data = File.ReadAllBytes(e.ImagePath); }
        catch (IOException) { return src; }
        catch (UnauthorizedAccessException) { return src; }
        using var wm = SKBitmap.Decode(data);
        if (wm is null) return src;
        var k = (float)Math.Max(0.01, e.Scale);
        var ww = Math.Max(1, (int)Math.Round(wm.Width * k));
        var wh = Math.Max(1, (int)Math.Round(wm.Height * k));
        var margin = 10.0;
        var pos = e.Position.PlacementOffset(new Size2(ww, wh), new Size2(src.Width, src.Height));
        var px = pos.X + (e.Position.HorizontalFactor() == 0 ? margin : e.Position.HorizontalFactor() == 1 ? -margin : 0);
        var py = pos.Y + (e.Position.VerticalFactor() == 0 ? margin : e.Position.VerticalFactor() == 1 ? -margin : 0);
        var dst = new SKRect((float)px, (float)py, (float)px + ww, (float)py + wh);
        var opacity = (float)Math.Clamp(1 - e.Transparency, 0, 1);

        SKImage wmImage;
        if (e.Emboss) wmImage = EmbossImage(wm, e.EmbossDepth, e.EmbossAngle);
        else wmImage = SKImage.FromBitmap(wm);
        using var _wm = wmImage;

        using var baseImg = ToSk(src);
        using var s = Surface(src.Width, src.Height);
        var c = s.Canvas;
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255)) };
        var sampling = k < 1 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        if (e.Underlay)
        {
            c.DrawImage(wmImage, new SKRect(0, 0, wmImage.Width, wmImage.Height), dst, sampling, paint);
            c.DrawImage(baseImg, 0, 0);
        }
        else
        {
            c.DrawImage(baseImg, 0, 0);
            c.DrawImage(wmImage, new SKRect(0, 0, wmImage.Width, wmImage.Height), dst, sampling, paint);
        }
        return Read(s, src);
    }

    /// <summary>Relief version of the watermark: grey shading derived from the luminance gradient along the light angle, alpha kept.</summary>
    private static SKImage EmbossImage(SKBitmap wm, double depth, double angleDeg)
    {
        var bgra = SkiaInterop.ToBgra(wm);
        int w = bgra.Width, h = bgra.Height;
        var rad = angleDeg * Math.PI / 180;
        float dx = (float)Math.Cos(rad), dy = (float)-Math.Sin(rad);
        var height = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var a = bgra.Pixels[i * 4 + 3] / 255f;
            height[i] = (0.299f * bgra.Pixels[i * 4 + 2] + 0.587f * bgra.Pixels[i * 4 + 1] + 0.114f * bgra.Pixels[i * 4]) / 255f * a + a;
        }
        float H(int x, int y) => height[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];
        var res = new BgraImage(w, h);
        var d = (float)Math.Max(0.5, depth);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var a = bgra.Pixels[o + 3];
                if (a == 0) continue;
                var gx = H(x + 1, y) - H(x - 1, y);
                var gy = H(x, y + 1) - H(x, y - 1);
                var shade = 0.5f + (gx * dx + gy * dy) * 0.5f * d * 0.5f;
                var v = ToByte(Math.Clamp(shade, 0, 1) * 255f);
                var af = a / 255f;
                res.Pixels[o] = (byte)(v * af + 0.5f); res.Pixels[o + 1] = (byte)(v * af + 0.5f); res.Pixels[o + 2] = (byte)(v * af + 0.5f); res.Pixels[o + 3] = a;
            }
        return SkiaInterop.ToImage(res);
    }

    // ------------------------------------------------------------------ EFX-10 capture info caption

    internal static string BuildCaptionText(CaptionEffect c, DocumentMetadata? md)
    {
        var parts = new List<string>();
        if (md is not null)
        {
            if (c.Fields.HasFlag(CaptionFields.AppName) && !string.IsNullOrWhiteSpace(md.AppName)) parts.Add(md.AppName!);
            if (c.Fields.HasFlag(CaptionFields.AppVersion) && !string.IsNullOrWhiteSpace(md.AppVersion)) parts.Add("v" + md.AppVersion!.TrimStart('v', 'V'));
            if (c.Fields.HasFlag(CaptionFields.OsVersion) && !string.IsNullOrWhiteSpace(md.OsVersion)) parts.Add(md.OsVersion!);
            if (c.Fields.HasFlag(CaptionFields.WindowTitle) && !string.IsNullOrWhiteSpace(md.WindowTitle)) parts.Add(md.WindowTitle!);
            if (c.Fields.HasFlag(CaptionFields.Url) && !string.IsNullOrWhiteSpace(md.Url)) parts.Add(md.Url!);
            if (c.Fields.HasFlag(CaptionFields.Timestamp))
                parts.Add(md.CaptureDate.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        }
        else if (c.Fields.HasFlag(CaptionFields.Timestamp)) parts.Add(DateTime.Now.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        if (c.Fields.HasFlag(CaptionFields.CustomText) && !string.IsNullOrWhiteSpace(c.CustomText)) parts.Add(c.CustomText!);
        return string.Join("  •  ", parts);
    }

    private static EffectResult ApplyCaption(BgraImage src, CaptionEffect c, DocumentMetadata? md)
    {
        var text = BuildCaptionText(c, md);
        if (string.IsNullOrWhiteSpace(text)) return new EffectResult(src, Point2.Zero);
        var rt = RichText.FromPlain(text, c.FontFamily, Math.Max(4, c.FontSize), c.TextColor);
        var hAlign = c.Position.HorizontalFactor() switch { 0 => HorizontalAlign.Left, 1 => HorizontalAlign.Right, _ => HorizontalAlign.Center };
        var style = new TextLayoutStyle { HAlign = hAlign, VAlign = VerticalAlign.Middle, Padding = new Thickness2(6) };
        var vFactor = c.Position.VerticalFactor();
        if (c.Outside)
        {
            var layout = TextLayoutEngine.Layout(rt, style, src.Width, 0);
            var bandH = (int)Math.Ceiling(layout.Info.ContentSize.Height);
            var top = vFactor == 0;
            using var img = ToSk(src);
            using var s = Surface(src.Width, src.Height + bandH);
            var cv = s.Canvas;
            using (var bg = new SKPaint { Color = c.BackColor.ToSk() })
                cv.DrawRect(0, top ? 0 : src.Height, src.Width, bandH, bg);
            cv.DrawImage(img, 0, top ? bandH : 0);
            TextLayoutEngine.Draw(cv, layout, new SKPoint(0, top ? 0 : src.Height), style);
            return new EffectResult(Read(s, src), new Point2(0, top ? bandH : 0));
        }
        else
        {
            var maxW = Math.Max(20, src.Width);
            var layout = TextLayoutEngine.Layout(rt, style with { HAlign = HorizontalAlign.Left }, maxW, 0);
            var bw = Math.Min(src.Width, layout.Info.ContentSize.Width);
            var bh = layout.Info.ContentSize.Height;
            var pos = c.Position.PlacementOffset(new Size2(bw, bh), new Size2(src.Width, src.Height));
            using var img = ToSk(src);
            using var s = Surface(src.Width, src.Height);
            var cv = s.Canvas;
            cv.DrawImage(img, 0, 0);
            using (var bg = new SKPaint { Color = c.BackColor.ToSk() })
                cv.DrawRect((float)pos.X, (float)pos.Y, (float)bw, (float)bh, bg);
            TextLayoutEngine.Draw(cv, layout, new SKPoint((float)pos.X, (float)pos.Y), style, new SKRect((float)pos.X, (float)pos.Y, (float)(pos.X + bw), (float)(pos.Y + bh)));
            return new EffectResult(Read(s, src), Point2.Zero);
        }
    }
}
