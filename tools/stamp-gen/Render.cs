using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace StampGen;

/// <summary>
/// Visual QA: loads every stamp exactly like Shotwise.Rendering.AssetRasterCache (same SVG safety rules, Svg.Skia with external
/// resolution disabled), checks that it draws visible pixels, and renders contact sheets.
/// </summary>
internal static class Render
{
    public sealed record Item(string Category, string File, string Name, string Path);

    public static List<(string Folder, string Name, int Order, List<Item> Items)> Load(string stampsRoot)
    {
        var res = new List<(string, string, int, List<Item>)>();
        foreach (var dir in Directory.GetDirectories(stampsRoot))
        {
            var info = System.IO.Path.Combine(dir, "packageinfo.json");
            if (!File.Exists(info)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(info));
            var r = doc.RootElement;
            static string En(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.GetProperty("en").GetString()! : e.GetString()!;
            var name = En(r.GetProperty("category"));
            var order = r.TryGetProperty("order", out var o) ? o.GetInt32() : 1000;
            var items = new List<Item>();
            foreach (var s in r.GetProperty("stamps").EnumerateArray())
            {
                var f = s.GetProperty("file").GetString()!;
                items.Add(new Item(System.IO.Path.GetFileName(dir), f, En(s.GetProperty("name")), System.IO.Path.Combine(dir, f)));
            }
            res.Add((System.IO.Path.GetFileName(dir), name, order, items));
        }
        return res.OrderBy(x => x.Item3).ToList();
    }

    /// <summary>Mirror of AssetRasterCache.ValidateSvg (kept in sync by hand; the app is the authority).</summary>
    public static void ValidateLikeApp(byte[] data)
    {
        if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("SVG exceeds size limit.");
        using var input = new MemoryStream(data);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty SVG.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Invalid SVG.");
        foreach (var name in new[] { "width", "height" })
        {
            if (root.Attribute(name) is not { } dimension) continue;
            var match = Regex.Match(dimension.Value, @"^\s*([0-9]+(?:\.[0-9]+)?)\s*(px)?\s*$");
            if (!match.Success) throw new InvalidDataException("SVG dimensions exceed supported limits.");
        }
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "style" or "script" or "foreignObject" or "image" or "text") throw new InvalidDataException("Disallowed element " + element.Name.LocalName);
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if ((attribute.Name.LocalName is "href" or "src" or "base") && !value.StartsWith('#')) throw new InvalidDataException("External SVG reference.");
                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(value, @"^url\(\s*#[a-zA-Z0-9_-]+\s*\)$")) throw new InvalidDataException("External SVG URL.");
            }
        }
    }

    public static SKPicture? LoadPicture(byte[] data, out SKSvg owner)
    {
        Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
        Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
        Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;
        Svg.SvgDocument.DisableDtdProcessing = true;
        owner = new SKSvg();
        using var ms = new MemoryStream(data);
        return owner.Load(ms);
    }

    /// <summary>Validates every stamp; returns failures (file + reason).</summary>
    public static List<string> ValidateAll(string stampsRoot, Action<string> log)
    {
        var failures = new List<string>();
        var total = 0;
        foreach (var (folder, _, _, items) in Load(stampsRoot))
            foreach (var it in items)
            {
                total++;
                try
                {
                    var data = File.ReadAllBytes(it.Path);
                    ValidateLikeApp(data);
                    var pic = LoadPicture(data, out var owner);
                    using (owner)
                    {
                        if (pic is null) throw new InvalidDataException("Svg.Skia returned no picture");
                        var cull = pic.CullRect;
                        if (!(cull.Width > 0 && cull.Height > 0)) throw new InvalidDataException("empty cull rect");
                        using var bmp = Raster(pic, 96, 96, SKColors.Transparent);
                        var visible = 0;
                        foreach (var px in bmp.Pixels) if (px.Alpha > 24) visible++;
                        if (visible < 40) throw new InvalidDataException($"renders almost nothing ({visible} px)");
                    }
                }
                catch (Exception ex) { failures.Add($"{folder}/{it.File}: {ex.Message}"); }
            }
        log($"  validated {total} stamps through Svg.Skia: {failures.Count} failure(s)");
        return failures;
    }

    public static SKBitmap Raster(SKPicture pic, int w, int h, SKColor bg)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(bg);
        Draw(canvas, pic, new SKRect(0, 0, w, h));
        return bmp;
    }

    private static void Draw(SKCanvas canvas, SKPicture pic, SKRect cell)
    {
        var cull = pic.CullRect;
        var k = Math.Min(cell.Width / cull.Width, cell.Height / cull.Height);
        canvas.Save();
        canvas.Translate(cell.MidX - cull.MidX * k, cell.MidY - cull.MidY * k);
        canvas.Scale(k);
        canvas.DrawPicture(pic);
        canvas.Restore();
    }

    private static readonly SKColor LightBg = new(0xF6, 0xF7, 0xF9), DarkBg = new(0x1E, 0x20, 0x25);

    /// <summary>Contact sheet per category: every stamp at 64 px on light and dark, plus a 128 px sample on both.</summary>
    public static void Sheets(string stampsRoot, string outDir, string? previewDir, Action<string> log, ISet<string>? only = null)
    {
        Directory.CreateDirectory(outDir);
        using var tf = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var tfr = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        foreach (var (folder, name, _, items) in Load(stampsRoot))
        {
            if (only is not null && !only.Contains(folder)) continue;
            var pics = new List<(SKPicture? Pic, SKSvg Owner)>();
            foreach (var it in items) { var p = LoadPicture(File.ReadAllBytes(it.Path), out var o); pics.Add((p, o)); }
            const int cols64 = 24, cell64 = 76, cols128 = 12, cell128 = 152, pad = 16, head = 56, sub = 30;
            var rows64 = (items.Count + cols64 - 1) / cols64;
            var sample = Math.Min(items.Count, cols128 * 3);
            var rows128 = (sample + cols128 - 1) / cols128;
            var width = Math.Max(cols64 * cell64, cols128 * cell128) + pad * 2;
            var height = head + 2 * (sub + rows64 * cell64 + pad) + 2 * (sub + rows128 * cell128 + pad);
            using var bmp = new SKBitmap(width, height);
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.White);
            using var title = new SKFont(tf, 26);
            using var small = new SKFont(tfr, 15);
            using var ink = new SKPaint { Color = new SKColor(0x17, 0x20, 0x33), IsAntialias = true };
            c.DrawText($"{name} — {items.Count} stamps  ({folder})", pad, 38, title, ink);
            var y = head;
            foreach (var (bg, label, size, cols, cell, count) in new[]
            {
                (LightBg, "64 px · light", 64, cols64, cell64, items.Count), (DarkBg, "64 px · dark", 64, cols64, cell64, items.Count),
                (LightBg, "128 px · light (first " + sample + ")", 128, cols128, cell128, sample), (DarkBg, "128 px · dark (first " + sample + ")", 128, cols128, cell128, sample),
            })
            {
                c.DrawText(label, pad, y + 21, small, ink);
                y += sub;
                var rows = (count + cols - 1) / cols;
                using var bgp = new SKPaint { Color = bg };
                c.DrawRect(pad, y, cols * cell, rows * cell, bgp);
                for (var i = 0; i < count; i++)
                {
                    if (pics[i].Pic is not { } pic) continue;
                    var x0 = pad + i % cols * cell + (cell - size) / 2f;
                    var y0 = y + i / cols * cell + (cell - size) / 2f;
                    Draw(c, pic, new SKRect(x0, y0, x0 + size, y0 + size));
                }
                y += rows * cell + pad;
            }
            var file = Path.Combine(outDir, $"stamps-{folder}.png");
            Save(bmp, file);
            log($"  sheet {file} ({new FileInfo(file).Length / 1024} KB)");
            if (previewDir is not null) Preview(previewDir, folder, items, pics);
            foreach (var (p, o) in pics) { p?.Dispose(); o.Dispose(); }
        }
    }

    /// <summary>Zoomed QA pages (40 stamps per page at 128 px with names, light and dark halves) for close inspection.</summary>
    private static void Preview(string dir, string folder, List<Item> items, List<(SKPicture? Pic, SKSvg Owner)> pics)
    {
        Directory.CreateDirectory(dir);
        const int per = 40, cols = 10, cell = 150, labelH = 16;
        using var tfr = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        using var font = new SKFont(tfr, 11);
        for (var page = 0; page * per < items.Count; page++)
        {
            var n = Math.Min(per, items.Count - page * per);
            var rows = (n + cols - 1) / cols;
            using var bmp = new SKBitmap(cols * cell * 2, rows * (cell + labelH));
            using var c = new SKCanvas(bmp);
            for (var half = 0; half < 2; half++)
            {
                using var bg = new SKPaint { Color = half == 0 ? LightBg : DarkBg };
                c.DrawRect(half * cols * cell, 0, cols * cell, bmp.Height, bg);
                using var ink = new SKPaint { Color = half == 0 ? new SKColor(0x40, 0x40, 0x40) : new SKColor(0xC0, 0xC0, 0xC0), IsAntialias = true };
                for (var i = 0; i < n; i++)
                {
                    var idx = page * per + i;
                    var x0 = half * cols * cell + i % cols * cell;
                    var y0 = i / cols * (cell + labelH);
                    if (pics[idx].Pic is { } pic) Draw(c, pic, new SKRect(x0 + 11, y0 + 11, x0 + cell - 11, y0 + cell - 11));
                    var label = Path.GetFileNameWithoutExtension(items[idx].File);
                    if (label.Length > 24) label = label[..24];
                    c.DrawText(label, x0 + 4, y0 + cell + 11, font, ink);
                }
            }
            Save(bmp, Path.Combine(dir, $"{folder}-{page + 1:00}.png"));
        }
    }

    public static void Save(SKBitmap bmp, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(file);
        data.SaveTo(fs);
    }
}
