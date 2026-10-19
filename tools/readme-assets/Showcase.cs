using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Shotwise.Rendering;
using SkiaSharp;
using static ReadmeAssets.Gfx;

namespace ReadmeAssets;

/// <summary>Hero banners, curated app screenshots and the feature illustrations.</summary>
internal static class Showcase
{
    // Untracked app screenshots (see Program); SHOTWISE_README_SHOTS overrides the folder.
    private static readonly string Shots = Environment.GetEnvironmentVariable("SHOTWISE_README_SHOTS") is { Length: > 0 } dir ? dir : "docs/screenshots";

    // ------------------------------------------------------------------ hero

    public static void Hero(bool dark)
    {
        const int w = 1600, h = 800;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, dark);

        var t1 = dark ? DText1 : Text1;
        var t2 = dark ? DText2 : Text2;
        using (var icon = Load("src/Shotwise.App/Assets/png/shotwise-256.png"))
        {
            Shadow(c, new SKRect(100, 200, 196, 296), 24, 26, 12, 50, Accent);
            DrawImage(c, icon, new SKRect(92, 192, 204, 304));
        }
        Text(c, "Shotwise", 90, 400, Font(92, SKFontStyleWeight.SemiBold, display: true), t1);
        var tag = Font(31, SKFontStyleWeight.Normal, display: true);
        Text(c, "Capture, record, annotate and share", 96, 458, tag, t1);
        Text(c, "your screen on Windows.", 96, 498, tag, t1);
        Text(c, "Free and open source  ·  Private by default  ·  Offline AI", 96, 548, Font(19), t2);

        var cf = Font(16, SKFontStyleWeight.SemiBold);
        float x = 96;
        var chipBg = dark ? DSurface1.WithAlpha(230) : SKColors.White.WithAlpha(235);
        var chipBorder = dark ? DSurface2 : Border;
        foreach (var (label, dot) in new[] { ("Capture", Coral), ("Record", CoralA), ("Annotate", Accent), ("Share", SKColor.Parse("#16A34A")) })
            x += Chip(c, label, x, 584, cf, chipBg, t1, chipBorder, 16, 38, dot) + 10;

        using (var editor = Load($"{Shots}/{(dark ? "d2-editor-dark.png" : "d2-editor-light.png")}"))
            Framed(c, editor, new SKRect(628, 104, 628 + 928, 104 + 580), 12, true, dark ? new SKColor(255, 255, 255, 30) : null, 48, 22, (byte)(dark ? 140 : 70));
        using (var cap = Load($"{Shots}/{(dark ? "capture-window-dark.png" : "capture-window-150.png")}"))
            Framed(c, cap, new SKRect(712, 528, 712 + 430, 528 + 222), 12, true, dark ? new SKColor(255, 255, 255, 36) : null, 40, 18, (byte)(dark ? 150 : 80));

        Save(b, dark ? "hero-dark.png" : "hero-light.png");
    }

    // ------------------------------------------------------------------ curated screenshots

    public static void Screenshots()
    {
        void Win(string src, string name, float scale = 1f, SKRectI? crop = null)
        {
            using var shot = Load($"{Shots}/{src}");
            using var cropped = crop is { } r ? Crop(shot, r) : null;
            using var win = Window(cropped ?? shot, scale);
            Save(win, name);
        }
        Win("d2-editor-light.png", "editor-light.png");
        Win("d2-editor-dark.png", "editor-dark.png");
        Win("d2-library-light.png", "library.png");
        Win("d2-video-light.png", "video-editor.png");
        Win("capture-window-150.png", "capture-window.png");
        Win("capture-window-more.png", "capture-options.png", 0.9f);
        Win("preferences-hotkeys.png", "preferences-hotkeys.png", 0.8f, new SKRectI(0, 0, 1110, 1166));
        Win("keyboard-shortcuts.png", "keyboard-shortcuts.png", 0.85f);

        using var icon = Load("src/Shotwise.App/Assets/png/shotwise-256.png");
        Save(icon, "icon.png");
    }

    // ------------------------------------------------------------------ selection overlay composite

    public static void Selection()
    {
        const int w = 1600, h = 760;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        var lf = Font(17, SKFontStyleWeight.SemiBold);
        using var overlay = Load($"{Shots}/d2-selection-overlay.png");
        using var mag = Load($"{Shots}/d2-selection-magnifier-dark.png");

        using (var main = Crop(overlay, new SKRectI(150, 196, 830, 604)))
        {
            var dst = new SKRect(70, 96, 70 + main.Width * 1.4f, 96 + main.Height * 1.4f);
            Framed(c, main, dst, 14, true, null, 36, 16, 70);
            Text(c, "Drag a region or click a window, then pick Image, Video, Panorama or Grab Text", dst.Left + 4, dst.Top - 20, lf, Text2);
        }
        using (var pill = Crop(overlay, new SKRectI(522, 15, 757, 56)))
        {
            var dst = new SKRect(1110, 140, 1110 + pill.Width * 1.5f, 140 + pill.Height * 1.5f);
            Framed(c, pill, dst, dst.Height / 2, true, new SKColor(0, 0, 0, 0), 30, 12, 60);
            Text(c, "Aspect locks and snap guides", dst.Left + 4, dst.Top - 18, lf, Text2);
        }
        using (var loupe = Crop(mag, new SKRectI(809, 359, 956, 545)))
        {
            var dst = new SKRect(1110, 330, 1110 + loupe.Width * 1.6f, 330 + loupe.Height * 1.6f);
            Framed(c, loupe, dst, 16, true, new SKColor(0, 0, 0, 0), 30, 12, 60);
            Text(c, "Pixel loupe with colour readout", dst.Left + 4, dst.Top - 18, lf, Text2);
        }
        Save(b, "selection-overlay.png");
    }

    // ------------------------------------------------------------------ recording composite

    public static void Recording()
    {
        const int w = 1600, h = 900;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        var lf = Font(17, SKFontStyleWeight.SemiBold);

        void Piece(string file, SKRectI crop, float radius, SKRect dst, string label)
        {
            using var shot = Load($"{Shots}/{file}");
            using var part = Crop(shot, crop);
            Framed(c, part, dst, radius, true, new SKColor(0, 0, 0, 0), 36, 16, 70);
            Text(c, label, dst.Left + 4, dst.Top - 16, lf, Text2);
        }

        var dlg = new SKRectI(263, 204, 905, 897);
        var dScale = 0.94f;
        Piece("d2-ready-to-record-dark.png", dlg, 16, new SKRect(90, 120, 90 + dlg.Width * dScale, 120 + dlg.Height * dScale), "Ready to record: mic, system audio, webcam, cursor effects");

        var hud = new SKRectI(324, 862, 1117, 998);
        Piece("d2-recording-hud.png", hud, 36, new SKRect(780, 150, 780 + hud.Width * 0.92f, 150 + hud.Height * 0.92f), "Recording controls with live audio meters");

        var draw = new SKRectI(295, 17, 986, 81);
        Piece("d2-screendraw-light.png", draw, 12, new SKRect(780, 400, 780 + draw.Width, 400 + draw.Height), "ScreenDraw: draw on screen while recording");

        var step = new SKRectI(267, 836, 749, 962);
        Piece("d2-step-hud-dark.png", step, 32, new SKRect(780, 580, 780 + step.Width * 0.92f, 580 + step.Height * 0.92f), "Step capture: every click becomes a step");

        Save(b, "recording.png");
    }

    // ------------------------------------------------------------------ stamps

    private static readonly string[][] StampRows =
    {
        new[] { "stamp-approved-green", "stamp-paid-red", "stamp-draft-blue", "comic-wow-burst", "comic-omg-cloud", "comic-yes-burst", "rosette-gold", "rosette-blue",
                "ribbon-left-red", "note-yellow-pinned", "note-pink-taped", "pixel-trophy", "pixel-heart", "pixel-gem", "bubble-shout-yellow", "bubble-speech-left-sky" },
        new[] { "straight-red", "curved-blue", "elbow-green", "u-turn-orange", "circular-purple", "hand-curved-red", "double-blue", "block-yellow",
                "tip-tag", "new", "important-tag", "done", "warning-tag", "idea", "check-glossy", "doodle-circle-red" },
        new[] { "grinning-face", "smiling-face-with-sunglasses", "star-struck", "thinking-face", "face-with-tears-of-joy", "thumbs-up", "clapping-hands", "red-heart",
                "fire", "rocket", "party-popper", "trophy", "light-bulb", "laptop", "camera-with-flash", "pushpin" },
        new[] { "sunflower", "cat-face", "dog-face", "fox", "butterfly", "rainbow", "globe-showing-americas", "pizza",
                "hot-beverage", "birthday-cake", "avocado", "strawberry", "airplane", "automobile", "high-voltage", "sparkles" },
        new[] { "key-ctrl-light", "key-shift-light", "key-c-light", "key-f1-light", "mouse-left-click-light", "cursor-hand-click", "cursor-zoom-in", "checkbox-checked-light",
                "radio-on-light", "notification-badge-light", "cross-glossy", "hand-check-green", "hand-circled-1", "doodle-scribble-red", "exclamation-glossy", "doodle-star-yellow" },
    };

    public static void Stamps()
    {
        var root = Path.Combine(Repo, "src", "Shotwise.App", "Content", "stamps");
        var all = Directory.GetFiles(root, "*.svg", SearchOption.AllDirectories);
        var byName = all.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var packs = Directory.GetDirectories(root).Length;
        var badges = Directory.GetFiles(Path.Combine(root, "badges"), "*.svg").OrderBy(f => f).ToArray();
        var badgeStyles = badges.GroupBy(f => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(f), @"-[0-9a-z]+$", "")).ToList();

        var rows = new List<List<string>>();
        // first row: one number per badge style
        var r0 = new List<string>();
        for (var i = 0; r0.Count < 16 && i < 64; i++)
        {
            var g = badgeStyles[i % badgeStyles.Count].ToList();
            var pick = g.FirstOrDefault(f => f.EndsWith($"-{(i % 9) + 1:00}.svg")) ?? g[Math.Min(g.Count - 1, i % g.Count)];
            if (!r0.Contains(pick)) r0.Add(pick);
        }
        rows.Add(r0);
        var used = new HashSet<string>(r0);
        var spare = all.Where(f => !f.Contains("badges")).OrderBy(f => f).ToArray();
        var spareIdx = 0;
        foreach (var names in StampRows)
        {
            var row = new List<string>();
            foreach (var n in names)
            {
                if (byName.TryGetValue(n, out var p) && used.Add(p)) { row.Add(p); continue; }
                Console.WriteLine($"    stamp '{n}' not found, using a spare");
                while (spareIdx < spare.Length && !used.Add(spare[spareIdx])) spareIdx++;
                if (spareIdx < spare.Length) row.Add(spare[spareIdx++]);
            }
            rows.Add(row);
        }

        const int cols = 16, cell = 84, pad = 56;
        int w = 1600, h = 160 + rows.Count * cell + pad;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        var card = new SKRect(40, 32, w - 40, h - 28);
        Shadow(c, card, 18, 30, 12, 40);
        using (var p = Fill(SKColors.White)) c.DrawRoundRect(card, 18, 18, p);
        Text(c, $"{all.Length:N0} stamps in {packs} packs", card.Left + 36, card.Top + 56, Font(28, SKFontStyleWeight.SemiBold, display: true), Text1);
        Text(c, "Badges · stickers · arrows · labels · marks · keys · interface · Fluent Emoji — searchable, with favourites and importable packs",
            card.Left + 36, card.Top + 88, Font(17), Text2);
        var gridW = cols * cell;
        var gx = card.Left + (card.Width - gridW) / 2;
        var gy = card.Top + 116;
        for (var r = 0; r < rows.Count; r++)
            for (var i = 0; i < Math.Min(cols, rows[r].Count); i++)
            {
                var cellRect = new SKRect(gx + i * cell + 10, gy + r * cell + 10, gx + (i + 1) * cell - 10, gy + (r + 1) * cell - 10);
                DrawSvg(c, rows[r][i], cellRect);
            }
        Console.WriteLine($"    {all.Length} stamps found in {packs} packs");
        Save(b, "stamps.png");
    }

    // ------------------------------------------------------------------ effects (real Shotwise effect processor)

    public static void Effects(IEffectProcessor fx)
    {
        using var scene = SampleScenes.Dashboard();
        var src = ToBgra(scene);
        var spot = SampleScenes.RevenueCard; spot.Inflate(14, 14);
        var items = new (string Label, EffectSpec? Spec)[]
        {
            ("Original", null),
            ("Torn edge", new EdgeEffect(EdgeStyle.Torn, EdgeSides.Bottom | EdgeSides.Right, 26, true, false)),
            ("Drop shadow", new ShadowEffect(Argb.Black, ShadowDirection.BottomRight, 22, 26, 0.45)),
            ("Perspective", new PerspectiveEffect(false, 28, 0, false, true)),
            ("Spotlight & magnify", new SpotlightMagnifyEffect(new Rect2(spot.Left, spot.Top, spot.Width, spot.Height), 0.35, 5, 1.15)),
            ("Page curl", new PageCurlEffect(Corner.BottomRight, 150)),
        };
        const int w = 1600, h = 860;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        const float tileW = 470, tileH = 330, gapX = 40, top = 56;
        var lf = Font(19, SKFontStyleWeight.SemiBold);
        for (var i = 0; i < items.Length; i++)
        {
            var col = i % 3; var row = i / 3;
            var x = (w - (3 * tileW + 2 * gapX)) / 2 + col * (tileW + gapX);
            var y = top + row * (tileH + 80);
            var img = items[i].Spec is { } spec ? fx.Apply(src, spec, null).Image : src;
            using var sb = FromBgra(img);
            var k = Math.Min(tileW / sb.Width, (tileH - 10) / sb.Height);
            var dw = sb.Width * k; var dh = sb.Height * k;
            var dst = new SKRect(x + (tileW - dw) / 2, y + (tileH - dh) / 2, x + (tileW + dw) / 2, y + (tileH + dh) / 2);
            if (items[i].Spec is null) Framed(c, sb, dst, 6, true, null, 18, 6, 40);
            else DrawImage(c, sb, dst);
            Text(c, items[i].Label, x + tileW / 2, y + tileH + 36, lf, Text1, SKTextAlign.Center);
        }
        Save(b, "effects.png");
    }

    // ------------------------------------------------------------------ Smart Redact (real OCR + classifiers)

    public static async Task SmartRedactAsync(ISmartRedactService redact, SkiaDocumentRenderer renderer, IImageCodec codec)
    {
        using var scene = SampleScenes.Profile();
        var src = ToBgra(scene);
        var cats = new HashSet<RedactCategory> { RedactCategory.Email, RedactCategory.Phone, RedactCategory.CreditCard, RedactCategory.Ssn, RedactCategory.IpAddress, RedactCategory.Url, RedactCategory.Date };
        var hits = await redact.FindAsync(src, cats, CancellationToken.None);
        Console.WriteLine($"    Smart Redact found {hits.Count}: {string.Join(", ", hits.Select(x => $"{x.Category} '{x.Text}'"))}");
        if (Environment.GetEnvironmentVariable("README_ASSETS_DEBUG") == "1" && Program.Ocr is { } dbg)
            foreach (var l in (await dbg.RecognizeAsync(src, null, null, CancellationToken.None)).Lines) Console.WriteLine($"      ocr: {l.Text}");
        var doc = ShotDocument.FromImage(src, null, codec);
        var page = doc.Pages[0];
        foreach (var hit in hits)
        {
            var r = hit.Bounds;
            page.Annotations.Add(new BlurAnnotation { Start = new(r.X - 6, r.Y - 5), End = new(r.Right + 6, r.Bottom + 5), Mode = BlurKind.Pixelate, Intensity = 70, Category = hit.Category });
        }
        using var after = FromBgra(renderer.RenderPage(doc, page, new RenderOptions()));

        const int w = 1600, h = 700;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        var lf = Font(20, SKFontStyleWeight.SemiBold);
        var left = new SKRect(60, 96, 60 + 690, 96 + 690 * scene.Height / (float)scene.Width);
        var right = new SKRect(w - 60 - 690, left.Top, w - 60, left.Bottom);
        Text(c, "Before", left.Left + 4, left.Top - 22, lf, Text2);
        Text(c, "After Smart Redact", right.Left + 4, right.Top - 22, lf, AccentText);
        Framed(c, scene, left, 12, true, null, 30, 12, 55);
        Framed(c, after, right, 12, true, new SKColor(91, 76, 245, 140), 30, 12, 55);
        // arrow between
        using (var ap = Fill(Accent))
        {
            var cx = w / 2f; var cy = left.MidY;
            c.DrawCircle(cx, cy, 30, ap);
            using var path = new SKPath();
            path.MoveTo(cx - 11, cy - 12); path.LineTo(cx + 13, cy); path.LineTo(cx - 11, cy + 12); path.Close();
            using var wp = Fill(SKColors.White);
            c.DrawPath(path, wp);
        }
        var names = new Dictionary<RedactCategory, string>
        {
            [RedactCategory.Email] = "Email", [RedactCategory.Phone] = "Phone", [RedactCategory.CreditCard] = "Credit card", [RedactCategory.Ssn] = "SSN",
            [RedactCategory.IpAddress] = "IP address", [RedactCategory.Url] = "URL", [RedactCategory.Date] = "Date",
        };
        var cf = Font(16, SKFontStyleWeight.SemiBold);
        float x = right.Left;
        foreach (var g in hits.GroupBy(x => x.Category))
            x += Chip(c, $"{names.GetValueOrDefault(g.Key, g.Key.ToString())}  {g.Count()}", x, right.Bottom + 34, cf, SKColors.White, Text1, Border, 14, 36, Accent) + 8;
        Text(c, "Also finds faces (optional on-device model). Every hit is an editable blur you can review.", left.Left + 4, right.Bottom + 58, Font(16), Text2);
        Save(b, "smart-redact.png");
    }

    // ------------------------------------------------------------------ Grab Text (real Windows OCR)

    public static async Task GrabTextAsync(IOcrService ocr)
    {
        using var scene = SampleScenes.ErrorDialog();
        var src = ToBgra(scene);
        var result = await ocr.RecognizeAsync(src, null, null, CancellationToken.None);
        Console.WriteLine($"    OCR ({ocr.EngineName}, {result.Language}): {result.Lines.Count} lines");

        const int w = 1600, h = 520;
        using var b = NewBitmap(w, h);
        using var c = new SKCanvas(b);
        BrandBackground(c, w, h, false);
        var k = 1.08f;
        var left = new SKRect(60, 110, 60 + scene.Width * k, 110 + scene.Height * k);
        Text(c, "Select any area", left.Left + 4, left.Top - 24, Font(20, SKFontStyleWeight.SemiBold), Text2);
        Framed(c, scene, left, 12, true, null, 30, 12, 55);
        using (var hp = Fill(Accent.WithAlpha(40)))
        using (var hs = Stroke(Accent.WithAlpha(170), 1.5f))
            foreach (var line in result.Lines)
            {
                var r = new SKRect(left.Left + (float)line.Bounds.X * k - 4, left.Top + (float)line.Bounds.Y * k - 3,
                    left.Left + (float)line.Bounds.Right * k + 4, left.Top + (float)line.Bounds.Bottom * k + 3);
                c.DrawRoundRect(r, 4, 4, hp);
                c.DrawRoundRect(r, 4, 4, hs);
            }

        // result card (illustration of the Grab Text result, filled with the real OCR output)
        var card = new SKRect(left.Right + 90, 96, w - 60, Math.Min(h - 40, 96 + 118 + result.Lines.Count * 34 + 50));
        Shadow(c, card, 14, 34, 14, 55);
        using (var p = Fill(SKColors.White)) c.DrawRoundRect(card, 14, 14, p);
        using (var p = Stroke(Border, 1)) c.DrawRoundRect(card, 14, 14, p);
        Text(c, "Grab Text", card.Left + 28, card.Top + 46, Font(22, SKFontStyleWeight.SemiBold, display: true), Text1);
        var btn = new SKRect(card.Right - 152, card.Top + 20, card.Right - 24, card.Top + 58);
        using (var p = Fill(Accent)) c.DrawRoundRect(btn, 8, 8, p);
        Text(c, "Copy all", btn.MidX, btn.MidY + 6, Font(16, SKFontStyleWeight.SemiBold), SKColors.White, SKTextAlign.Center);
        using (var p = Stroke(Border, 1)) c.DrawLine(card.Left + 1, card.Top + 76, card.Right - 1, card.Top + 76, p);
        var tf = Font(19);
        float y = card.Top + 118;
        foreach (var line in result.Lines)
        {
            var s = line.Text;
            while (Measure(s, tf) > card.Width - 56 && s.Length > 4) s = s[..^2];
            Text(c, s == line.Text ? s : s + "…", card.Left + 28, y, tf, Text1);
            y += 34;
            if (y > card.Bottom - 70) break;
        }
        Text(c, $"{ocr.EngineName} · runs offline · tables export to TSV / HTML", card.Left + 28, card.Bottom - 26, Font(15), Text3);
        Save(b, "grab-text.png");
    }
}
