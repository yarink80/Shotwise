using System.Globalization;
using System.Text;
using SkiaSharp;
using Svg.Skia;

namespace IconGen;

/// <summary>
/// Shotwise app icon generator. The artwork is our own: a rounded indigo-violet tile (top-left #7C70FF to bottom-right #5B4CF5,
/// soft top highlight), four white viewfinder corner brackets and a coral shutter dot (#FF7A45 to #FF4D5E) with a small inner
/// highlight. Every size has its own hand-tuned spec so strokes land on whole pixels; at 32 px and below the details (dot shadow,
/// round caps, rounded bracket corners at 24 and below) are dropped. Outputs SVGs, PNGs, multi-size .ico files and a contact sheet.
/// </summary>
internal static class Program
{
    private static readonly int[] AppSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    private static readonly int[] TraySizes = { 16, 20, 24, 32, 40, 48, 64 };

    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.Length > 0 ? args[0] : FindRepoRoot());
        var preview = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
        var assets = Path.Combine(root, "src", "Shotwise.App", "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "png"));
        Directory.CreateDirectory(Path.Combine(assets, "svg"));

        // Master artwork (256 design grid, full detail).
        File.WriteAllText(Path.Combine(assets, "shotwise-icon.svg"), Svg(Spec.For(256), Variant.Normal, master: true));

        var app = new List<(int Size, SKBitmap Bitmap)>();
        foreach (var s in AppSizes)
        {
            var svg = Svg(Spec.For(s), Variant.Normal);
            File.WriteAllText(Path.Combine(assets, "svg", $"shotwise-{s}.svg"), svg);
            var bmp = Render(svg, s);
            Save(bmp, Path.Combine(assets, "png", $"shotwise-{s}.png"));
            app.Add((s, bmp));
        }
        WriteIco(Path.Combine(assets, "shotwise.ico"), app);

        // Notification area: the same hinted artwork at the tray sizes (16-32 px = 100-200 % DPI), plus the recording state.
        var tray = app.Where(a => TraySizes.Contains(a.Size)).ToList();
        WriteIco(Path.Combine(assets, "shotwise-tray.ico"), tray);
        var rec = new List<(int Size, SKBitmap Bitmap)>();
        foreach (var s in TraySizes)
        {
            var svg = Svg(Spec.For(s), Variant.Recording);
            if (s == 32) File.WriteAllText(Path.Combine(assets, "svg", "shotwise-recording-32.svg"), svg);
            rec.Add((s, Render(svg, s)));
        }
        WriteIco(Path.Combine(assets, "shotwise-tray-recording.ico"), rec);

        var sheet = ContactSheet(app, rec);
        var sheetPath = Path.Combine(root, "docs", "screenshots", "app-icon-sizes.png");
        Save(sheet, sheetPath);
        if (preview is not null)
        {
            Directory.CreateDirectory(preview);
            Save(sheet, Path.Combine(preview, "app-icon-sizes.png"));
        }
        Console.WriteLine($"Wrote icons to {assets} and the contact sheet to {sheetPath}");
        return 0;
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Shotwise.sln"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    // ------------------------------------------------------------------ design

    internal enum Variant { Normal, Recording }

    /// <summary>
    /// Per-size geometry in output pixels. <c>D</c> = distance from the centre to the bracket stroke centre, <c>Arm</c> = bracket arm
    /// length, <c>Corner</c> = inner radius of the bracket bend (0 = square, butt caps), <c>Dot</c> = shutter dot radius.
    /// </summary>
    internal sealed record Spec(int S, double Inset, double Radius, double D, double Arm, double Stroke, double Corner, double Dot, bool Details)
    {
        public double C => S / 2.0;

        public static Spec For(int s) => s switch
        {
            16 => new(16, 0, 3.5, 4.5, 3, 1, 0, 3, false),
            20 => new(20, 1, 4, 5, 3, 2, 0, 3, false),
            24 => new(24, 1, 5, 6, 4, 2, 0, 3.5, false),
            32 => new(32, 1, 7, 8.5, 5.5, 3, 1, 5, false),
            40 => new(40, 2, 8, 10.5, 7, 3, 1.5, 6, true),
            48 => new(48, 2, 10, 12, 8, 4, 2, 7, true),
            64 => new(64, 3, 14, 16, 11, 4, 3, 9, true),
            96 => new(96, 4, 21, 24, 16, 6, 4.5, 13, true),
            128 => new(128, 6, 28, 32, 21, 8, 6, 17, true),
            _ => new(256, 12, 56, 64, 42, 16, 12, 34, true),
        };
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Svg(Spec p, Variant v, bool master = false)
    {
        double s = p.S, m = p.Inset, c = p.C, w = s - 2 * m;
        bool recording = v == Variant.Recording;
        double r = recording ? RecordingDot(p) : p.Dot;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{p.S}\" height=\"{p.S}\" viewBox=\"0 0 {p.S} {p.S}\">\n");
        if (master) sb.Append("  <!-- Shotwise app icon (original artwork). Generated by tools/icon-gen (edit the generator, not this file). -->\n");
        sb.Append("  <defs>\n");
        sb.Append($"    <linearGradient id=\"tile\" x1=\"{F(m)}\" y1=\"{F(m)}\" x2=\"{F(s - m)}\" y2=\"{F(s - m)}\" gradientUnits=\"userSpaceOnUse\">" +
                  "<stop offset=\"0\" stop-color=\"#7C70FF\"/><stop offset=\"1\" stop-color=\"#5B4CF5\"/></linearGradient>\n");
        sb.Append($"    <linearGradient id=\"hl\" x1=\"0\" y1=\"{F(m)}\" x2=\"0\" y2=\"{F(m + w * 0.6)}\" gradientUnits=\"userSpaceOnUse\">" +
                  $"<stop offset=\"0\" stop-color=\"#FFFFFF\" stop-opacity=\"{(p.Details ? "0.24" : "0.16")}\"/><stop offset=\"1\" stop-color=\"#FFFFFF\" stop-opacity=\"0\"/></linearGradient>\n");
        sb.Append($"    <linearGradient id=\"dot\" x1=\"{F(c - r)}\" y1=\"{F(c - r)}\" x2=\"{F(c + r)}\" y2=\"{F(c + r)}\" gradientUnits=\"userSpaceOnUse\">" +
                  "<stop offset=\"0\" stop-color=\"#FF7A45\"/><stop offset=\"1\" stop-color=\"#FF4D5E\"/></linearGradient>\n");
        sb.Append($"    <radialGradient id=\"dotHl\" cx=\"{F(c - r * 0.3)}\" cy=\"{F(c - r * 0.36)}\" r=\"{F(r * 0.6)}\" gradientUnits=\"userSpaceOnUse\">" +
                  "<stop offset=\"0\" stop-color=\"#FFFFFF\" stop-opacity=\"0.45\"/><stop offset=\"1\" stop-color=\"#FFFFFF\" stop-opacity=\"0\"/></radialGradient>\n");
        sb.Append("  </defs>\n");

        // tile + soft top highlight (+ hairline inner edge from 64 px)
        string rect = $"x=\"{F(m)}\" y=\"{F(m)}\" width=\"{F(w)}\" height=\"{F(w)}\" rx=\"{F(p.Radius)}\"";
        sb.Append($"  <rect {rect} fill=\"url(#tile)\"/>\n");
        sb.Append($"  <rect {rect} fill=\"url(#hl)\"/>\n");
        double e = p.S >= 64 ? p.S / 128.0 : 0;
        if (e > 0)
            sb.Append($"  <rect x=\"{F(m + e / 2)}\" y=\"{F(m + e / 2)}\" width=\"{F(w - e)}\" height=\"{F(w - e)}\" rx=\"{F(p.Radius - e / 2)}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-opacity=\"0.16\" stroke-width=\"{F(e)}\"/>\n");

        // viewfinder brackets; the recording tray state drops them at tray sizes so the big coral dot reads instantly
        if (!(recording && p.S <= 32))
        {
            string caps = p.Details ? "round" : "butt";
            string join = p.Corner > 0 ? "round" : "miter";
            sb.Append($"  <path d=\"{Brackets(p)}\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"{F(p.Stroke)}\" stroke-linecap=\"{caps}\" stroke-linejoin=\"{join}\"/>\n");
        }

        // shutter / record dot
        if (recording)
        {
            double ring = Math.Max(1, Math.Round(p.S / 16.0));
            sb.Append($"  <circle cx=\"{F(c)}\" cy=\"{F(c)}\" r=\"{F(r + ring)}\" fill=\"#FFFFFF\"/>\n");
        }
        else if (p.Details) sb.Append($"  <circle cx=\"{F(c)}\" cy=\"{F(c + r * 0.10)}\" r=\"{F(r * 1.04)}\" fill=\"#2B1E9E\" fill-opacity=\"0.28\"/>\n");
        sb.Append($"  <circle cx=\"{F(c)}\" cy=\"{F(c)}\" r=\"{F(r)}\" fill=\"url(#dot)\"/>\n");
        if (p.S >= 32) sb.Append($"  <circle cx=\"{F(c)}\" cy=\"{F(c)}\" r=\"{F(r)}\" fill=\"url(#dotHl)\"/>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>Recording state: larger dot with a white ring (pixel-aligned at the tray sizes).</summary>
    private static double RecordingDot(Spec p) => p.S switch
    {
        16 => 4, 20 => 5, 24 => 6, 32 => 8, 40 => 9, 48 => 11, 64 => 14, _ => p.Dot * 1.4,
    };

    private static string Brackets(Spec p)
    {
        double c = p.C, d = p.D, a = p.Arm, k = p.Corner;
        var sb = new StringBuilder();
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            double x = c + sx * d, y = c + sy * d;          // corner (stroke centre)
            double ax = x - sx * a, ay = y - sy * a;         // arm ends, pointing inwards
            if (k > 0)
            {
                // vertical arm end -> corner arc (clockwise when sx*sy > 0) -> horizontal arm end
                sb.Append($"M{F(x)} {F(ay)}V{F(y - sy * k)}");
                sb.Append($"A{F(k)} {F(k)} 0 0 {(sx * sy > 0 ? 1 : 0)} {F(x - sx * k)} {F(y)}");
                sb.Append($"H{F(ax)}");
            }
            else sb.Append($"M{F(x)} {F(ay)}V{F(y)}H{F(ax)}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ rendering

    internal static SKBitmap Render(string svgText, int size)
    {
        using var svg = new SKSvg();
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(svgText))) svg.Load(ms);
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var pic = svg.Picture ?? throw new InvalidOperationException("SVG did not render");
        var bounds = pic.CullRect;
        canvas.Scale(size / Math.Max(1f, bounds.Width), size / Math.Max(1f, bounds.Height));
        canvas.DrawPicture(pic);
        canvas.Flush();
        using var img = surface.Snapshot();
        img.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0);
        return bmp;
    }

    private static void Save(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Writes an .ico: 32-bpp BI_RGB DIB entries (with AND mask) below 256 px, a PNG entry at 256 px.</summary>
    private static void WriteIco(string path, IReadOnlyList<(int Size, SKBitmap Bitmap)> images)
    {
        var blobs = images.Select(i => i.Size >= 256 ? Png(i.Bitmap) : Dib(i.Bitmap)).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int s = images[i].Size;
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(blobs[i].Length); w.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) w.Write(b);
    }

    private static byte[] Png(SKBitmap b) { using var d = b.Encode(SKEncodedImageFormat.Png, 100); return d.ToArray(); }

    private static byte[] Dib(SKBitmap b)
    {
        int s = b.Width;
        int maskStride = ((s + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32); w.Write(0);
        w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = s - 1; y >= 0; y--)
            for (int x = 0; x < s; x++)
            {
                var c = b.GetPixel(x, y);
                w.Write(c.Blue); w.Write(c.Green); w.Write(c.Red); w.Write(c.Alpha);
            }
        for (int y = s - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < s; x++) if (b.GetPixel(x, y).Alpha == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(row);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ contact sheet

    private static SKBitmap ContactSheet(List<(int Size, SKBitmap Bitmap)> app, List<(int Size, SKBitmap Bitmap)> rec)
    {
        const int Gap = 26, Margin = 28, Band = 300, Zoom = 6;
        var zoomSet = app.Where(a => a.Size <= 32).Concat(rec.Where(r => r.Size is 16 or 24 or 32)).ToList();
        int width = Math.Max(Margin * 2 + app.Sum(a => a.Size) + Gap * (app.Count - 1), Margin * 2 + zoomSet.Sum(z => z.Size * Zoom) + Gap * (zoomSet.Count - 1));
        int zoomHeight = 32 * Zoom + 60;
        int height = 56 + Band * 2 + zoomHeight;
        var bmp = new SKBitmap(width, height);
        using var cv = new SKCanvas(bmp);
        var light = new SKColor(0xF6, 0xF7, 0xFB);
        var dark = new SKColor(0x16, 0x17, 0x1C);
        cv.Clear(light);
        using var title = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 20);
        using var label = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13);
        using var inkDark = new SKPaint { Color = new SKColor(0x14, 0x15, 0x1A), IsAntialias = true };
        using var inkGrey = new SKPaint { Color = new SKColor(0x8A, 0x8F, 0xA3), IsAntialias = true };
        using var inkLight = new SKPaint { Color = new SKColor(0xB4, 0xB8, 0xC8), IsAntialias = true };
        cv.DrawText("Shotwise app icon: every size at actual pixels (Light and Dark), tray sizes magnified x6", Margin, 36, title, inkDark);

        void DrawBand(float top, SKColor bg, SKPaint text)
        {
            using var paint = new SKPaint { Color = bg };
            cv.DrawRect(0, top, width, Band, paint);
            float x = Margin;
            foreach (var (s, b) in app)
            {
                float y = top + 20 + (256 - s) / 2f;
                cv.DrawBitmap(b, x, y);
                var t = $"{s}";
                cv.DrawText(t, x + s / 2f - label.MeasureText(t) / 2f, top + Band - 14, label, text);
                x += s + Gap;
            }
        }
        DrawBand(56, light, inkGrey);
        DrawBand(56 + Band, dark, inkLight);

        float zx = Margin, zTop = 56 + Band * 2 + 16;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        int i = 0;
        foreach (var (s, b) in zoomSet)
        {
            bool isRec = i++ >= 4;
            using var img = SKImage.FromBitmap(b);
            cv.DrawImage(img, new SKRect(zx, zTop, zx + s * Zoom, zTop + s * Zoom), sampling);
            var t = isRec ? $"{s} recording" : $"{s}";
            cv.DrawText(t, zx, zTop + 32 * Zoom + 26, label, inkGrey);
            zx += s * Zoom + Gap;
        }
        return bmp;
    }
}
