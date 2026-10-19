using Shotwise.Core.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace ReadmeAssets;

/// <summary>Small SkiaSharp helpers shared by every generator (brand tokens, frames, text, PNG output).</summary>
internal static class Gfx
{
    // Brand tokens (docs/DESIGN-SYSTEM.md).
    public static readonly SKColor Accent = SKColor.Parse("#5B4CF5");
    public static readonly SKColor AccentDark = SKColor.Parse("#7C70FF");
    public static readonly SKColor AccentText = SKColor.Parse("#4A3BE0");
    public static readonly SKColor GradA = SKColor.Parse("#7A6CFF");
    public static readonly SKColor GradB = SKColor.Parse("#4F3FEA");
    public static readonly SKColor Coral = SKColor.Parse("#FF4D5E");
    public static readonly SKColor CoralA = SKColor.Parse("#FF7A45");
    public static readonly SKColor Surface0 = SKColor.Parse("#F6F7FB");
    public static readonly SKColor Surface1 = SKColors.White;
    public static readonly SKColor Surface2 = SKColor.Parse("#EEF0F7");
    public static readonly SKColor Border = SKColor.Parse("#E3E5EE");
    public static readonly SKColor BorderStrong = SKColor.Parse("#CDD1DE");
    public static readonly SKColor Text1 = SKColor.Parse("#14151A");
    public static readonly SKColor Text2 = SKColor.Parse("#5A5F73");
    public static readonly SKColor Text3 = SKColor.Parse("#8A8FA3");
    public static readonly SKColor DSurface0 = SKColor.Parse("#16171C");
    public static readonly SKColor DSurface1 = SKColor.Parse("#1F2027");
    public static readonly SKColor DSurface2 = SKColor.Parse("#2A2B34");
    public static readonly SKColor DText1 = SKColor.Parse("#F3F4F8");
    public static readonly SKColor DText2 = SKColor.Parse("#B4B8C8");

    public static string Repo = "";
    public static string OutDir = "";
    public static readonly List<(string Name, long Bytes, int W, int H)> Written = new();

    public static SKTypeface Face(SKFontStyleWeight weight = SKFontStyleWeight.Normal, string family = "Segoe UI")
        => SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
           ?? SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    public static SKTypeface Display(SKFontStyleWeight weight = SKFontStyleWeight.SemiBold)
    {
        var tf = SKTypeface.FromFamilyName("Segoe UI Variable Display", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        return tf is not null && tf.FamilyName.Contains("Variable", StringComparison.OrdinalIgnoreCase) ? tf : Face(weight);
    }

    public static SKFont Font(float size, SKFontStyleWeight weight = SKFontStyleWeight.Normal, bool display = false)
        => new(display ? Display(weight) : Face(weight), size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };

    public static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c };
    public static SKPaint Stroke(SKColor c, float w) => new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = c, StrokeWidth = w };

    public static void Text(SKCanvas c, string s, float x, float y, SKFont f, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var p = Fill(color);
        c.DrawText(s, x, y, align, f, p);
    }

    public static float Measure(string s, SKFont f) => f.MeasureText(s);

    public static SKBitmap NewBitmap(int w, int h, SKColor? clear = null)
    {
        var b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(b);
        c.Clear(clear ?? SKColors.Transparent);
        return b;
    }

    public static SKBitmap Load(string relOrAbs)
    {
        var path = Path.IsPathRooted(relOrAbs) ? relOrAbs : Path.Combine(Repo, relOrAbs);
        using var raw = SKBitmap.Decode(path) ?? throw new FileNotFoundException(path);
        var b = NewBitmap(raw.Width, raw.Height);
        using var c = new SKCanvas(b);
        c.DrawBitmap(raw, 0, 0);
        return b;
    }

    public static SKBitmap Crop(SKBitmap src, SKRectI r)
    {
        var b = NewBitmap(r.Width, r.Height);
        using var c = new SKCanvas(b);
        c.DrawBitmap(src, r, new SKRect(0, 0, r.Width, r.Height));
        return b;
    }

    public static readonly SKSamplingOptions Hq = new(SKCubicResampler.Mitchell);

    public static void DrawImage(SKCanvas c, SKBitmap bmp, SKRect dst, SKPaint? paint = null)
    {
        using var img = SKImage.FromBitmap(bmp);
        c.DrawImage(img, new SKRect(0, 0, bmp.Width, bmp.Height), dst, Hq, paint);
    }

    public static SKBitmap Resize(SKBitmap src, int w, int h)
    {
        var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        DrawImage(c, src, new SKRect(0, 0, w, h));
        return b;
    }

    /// <summary>Soft elevation shadow behind a rounded rect (drawn before the content).</summary>
    public static void Shadow(SKCanvas c, SKRect r, float radius, float blur = 28, float dy = 14, byte alpha = 60, SKColor? tint = null)
    {
        using var p = Fill((tint ?? new SKColor(20, 18, 60)).WithAlpha(alpha));
        p.ImageFilter = SKImageFilter.CreateBlur(blur / 2, blur / 2);
        var s = r; s.Offset(0, dy);
        c.DrawRoundRect(s, radius, radius, p);
        using var p2 = Fill(new SKColor(20, 18, 60, (byte)(alpha / 3)));
        p2.ImageFilter = SKImageFilter.CreateBlur(3, 3);
        var s2 = r; s2.Offset(0, 2);
        c.DrawRoundRect(s2, radius, radius, p2);
    }

    /// <summary>Draws a screenshot clipped to a rounded rect with a 1px border and a soft shadow.</summary>
    public static void Framed(SKCanvas c, SKBitmap shot, SKRect dst, float radius = 12, bool shadow = true, SKColor? border = null, float blur = 36, float dy = 18, byte alpha = 70)
    {
        if (shadow) Shadow(c, dst, radius, blur, dy, alpha);
        c.Save();
        using (var clip = new SKPath()) { clip.AddRoundRect(dst, radius, radius); c.ClipPath(clip, SKClipOperation.Intersect, true); }
        DrawImage(c, shot, dst);
        c.Restore();
        using var bp = Stroke(border ?? new SKColor(0, 0, 0, 34), 1);
        var b = dst; b.Inflate(-0.5f, -0.5f);
        c.DrawRoundRect(b, radius, radius, bp);
    }

    /// <summary>A screenshot on a transparent canvas with margins for its shadow (looks right on GitHub light and dark).</summary>
    public static SKBitmap Window(SKBitmap shot, float scale = 1f, float radius = 10, int margin = 36)
    {
        var w = (int)Math.Round(shot.Width * scale); var h = (int)Math.Round(shot.Height * scale);
        var b = NewBitmap(w + margin * 2, h + margin * 2);
        using var c = new SKCanvas(b);
        Framed(c, shot, new SKRect(margin, margin - 8, margin + w, margin - 8 + h), radius, true, null, 30, 12, 55);
        return b;
    }

    public static void Pill(SKCanvas c, SKRect r, SKColor bg, SKColor? border = null)
    {
        using var p = Fill(bg);
        c.DrawRoundRect(r, r.Height / 2, r.Height / 2, p);
        if (border is { } bc) { using var s = Stroke(bc, 1); c.DrawRoundRect(r, r.Height / 2, r.Height / 2, s); }
    }

    /// <summary>Text pill (chip). Returns its width.</summary>
    public static float Chip(SKCanvas c, string label, float x, float y, SKFont f, SKColor bg, SKColor fg, SKColor? border = null, float padX = 16, float h = 36, SKColor? dot = null)
    {
        var tw = Measure(label, f);
        var extra = dot is null ? 0 : 16;
        var r = new SKRect(x, y, x + tw + padX * 2 + extra, y + h);
        Pill(c, r, bg, border);
        if (dot is { } dc) { using var dp = Fill(dc); c.DrawCircle(x + padX + 4, y + h / 2, 4.5f, dp); }
        var m = f.Metrics;
        Text(c, label, x + padX + extra, y + h / 2 - (m.Ascent + m.Descent) / 2, f, fg);
        return r.Width;
    }

    public static void BrandBackground(SKCanvas c, int w, int h, bool dark)
    {
        var a = dark ? SKColor.Parse("#15151D") : SKColor.Parse("#F7F7FD");
        var b = dark ? SKColor.Parse("#1D1A36") : SKColor.Parse("#ECEAFF");
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), new[] { a, b }, null, SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, w, h, p);
        }
        void Blob(float cx, float cy, float r, SKColor col, byte alpha)
        {
            using var p = new SKPaint { IsAntialias = true };
            p.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r, new[] { col.WithAlpha(alpha), col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, w, h, p);
        }
        Blob(w * 0.86f, h * 0.10f, h * 0.95f, GradA, (byte)(dark ? 70 : 80));
        Blob(w * 0.05f, h * 1.02f, h * 0.75f, CoralA, (byte)(dark ? 34 : 46));
        Blob(w * 0.42f, h * 0.45f, h * 0.55f, SKColor.Parse("#9AD0FF"), (byte)(dark ? 0 : 26));
    }

    // ------------------------------------------------------------------ svg stamps

    public static SKPicture? Svg(string path, out SKSvg owner)
    {
        global::Svg.SvgDocument.ResolveExternalXmlEntites = global::Svg.ExternalType.None;
        global::Svg.SvgDocument.ResolveExternalImages = global::Svg.ExternalType.None;
        global::Svg.SvgDocument.ResolveExternalElements = global::Svg.ExternalType.None;
        global::Svg.SvgDocument.DisableDtdProcessing = true;
        owner = new SKSvg();
        return owner.Load(path);
    }

    public static void DrawSvg(SKCanvas c, string path, SKRect cell)
    {
        var pic = Svg(path, out var owner);
        using (owner)
        {
            if (pic is null) return;
            var cull = pic.CullRect;
            var k = Math.Min(cell.Width / cull.Width, cell.Height / cull.Height);
            c.Save();
            c.Translate(cell.MidX - cull.MidX * k, cell.MidY - cull.MidY * k);
            c.Scale(k);
            c.DrawPicture(pic);
            c.Restore();
        }
    }

    // ------------------------------------------------------------------ BgraImage interop

    public static BgraImage ToBgra(SKBitmap b)
    {
        var src = b.ColorType == SKColorType.Bgra8888 && b.AlphaType == SKAlphaType.Premul ? b : b.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("Bitmap conversion failed.");
        var px = new byte[src.Width * src.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(src.GetPixels(), px, 0, px.Length);
        return new BgraImage(src.Width, src.Height, px);
    }

    public static SKBitmap FromBgra(BgraImage img)
    {
        var b = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(img.Pixels, 0, b.GetPixels(), img.Pixels.Length);
        return b;
    }

    // ------------------------------------------------------------------ output

    public static void Save(SKBitmap b, string name)
    {
        var path = Path.Combine(OutDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var pix = b.PeekPixels();
        using var data = name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? pix.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 90))
            : pix.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9));
        File.WriteAllBytes(path, (data ?? throw new InvalidOperationException($"Encoding {name} failed.")).ToArray());
        Record(path, b.Width, b.Height);
    }

    public static void Record(string path, int w, int h)
    {
        var len = new FileInfo(path).Length;
        Written.RemoveAll(x => x.Name == Path.GetFileName(path));
        Written.Add((Path.GetFileName(path), len, w, h));
        Console.WriteLine($"  {Path.GetFileName(path),-28} {w}x{h}  {len / 1024.0:0} KB");
        if (len > 1_500_000) Console.Error.WriteLine($"  WARNING {Path.GetFileName(path)} is over 1.5 MB");
    }
}
