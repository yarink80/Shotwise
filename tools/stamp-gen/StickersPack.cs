using SkiaSharp;

namespace StampGen;

/// <summary>Stickers (original artwork): distressed rubber stamps, comic bursts with halftone, ribbons and banners, glossy speech
/// and thought bubbles, sticky notes, washi tape and a pixel-art set.</summary>
internal static class StickersPack
{
    private static readonly L StickerKw = new("sticker", "Sticker", "pegatina", "autocollant", "ステッカー", "adesivo");

    public static Pack Build()
    {
        var pack = new Pack("stickers", 30, new L("Stickers", "Sticker", "Pegatinas", "Autocollants", "ステッカー", "Adesivos"));
        RubberStamps(pack);
        Comics(pack);
        Ribbons(pack);
        Bubbles(pack);
        Notes(pack);
        Washi(pack);
        Pixel(pack);
        return pack;
    }

    // ------------------------------------------------------------------ rubber stamps

    private static readonly (string Word, L Loc)[] StampWords =
    {
        ("APPROVED", new("approved", "genehmigt", "aprobado", "approuvé", "承認", "aprovado")),
        ("REJECTED", new("rejected", "abgelehnt", "rechazado", "rejeté", "却下", "rejeitado")),
        ("PAID", new("paid", "bezahlt", "pagado", "payé", "支払済", "pago")),
        ("DRAFT", new("draft", "Entwurf", "borrador", "brouillon", "下書き", "rascunho")),
        ("CONFIDENTIAL", new("confidential", "vertraulich", "confidencial", "confidentiel", "社外秘", "confidencial")),
        ("URGENT", new("urgent", "dringend", "urgente", "urgent", "至急", "urgente")),
        ("DONE", new("done", "erledigt", "hecho", "terminé", "完了", "concluído")),
        ("TODO", new("to do", "zu erledigen", "pendiente", "à faire", "未完了", "a fazer")),
        ("NEW", new("new", "neu", "nuevo", "nouveau", "新規", "novo")),
        ("FINAL", new("final", "endgültig", "final", "final", "最終", "final")),
        ("COPY", new("copy", "Kopie", "copia", "copie", "コピー", "cópia")),
        ("VOID", new("void", "ungültig", "anulado", "annulé", "無効", "anulado")),
        ("SAMPLE", new("sample", "Muster", "muestra", "échantillon", "見本", "amostra")),
        ("REVIEWED", new("reviewed", "geprüft", "revisado", "révisé", "確認済", "revisado")),
        ("VERIFIED", new("verified", "verifiziert", "verificado", "vérifié", "検証済", "verificado")),
        ("ON HOLD", new("on hold", "zurückgestellt", "en espera", "en attente", "保留", "em espera")),
    };

    private static readonly (string Key, string Ink, L Name)[] StampInks =
    {
        ("red", "#D3263A", T.Color["red"]), ("blue", "#1F5FBF", T.Color["blue"]), ("green", "#1B8A4C", T.Color["green"]),
    };

    private static void RubberStamps(Pack pack)
    {
        L rubber = new("rubber stamp", "Stempel", "sello", "tampon", "スタンプ", "carimbo");
        var i = 0;
        foreach (var (word, loc) in StampWords)
        {
            var style = i % 3;
            foreach (var (key, ink, cname) in StampInks)
            {
                var seed = 1000 + i * 17 + key.Length * 31;
                pack.Add($"stamp-{FluentPack.Slug(word)}-{key}", RubberStamp(word, ink, style, seed), T.Paren(new L(word, loc.De.ToUpperInvariant(), loc.Es.ToUpperInvariant(), loc.Fr.ToUpperInvariant(), loc.Ja, loc.Pt.ToUpperInvariant()), cname),
                    loc, rubber, cname, L.Same(word.ToLowerInvariant()));
            }
            i++;
        }
    }

    private static string RubberStamp(string word, string ink, int style, int seed)
    {
        var rnd = new Random(seed);
        using var probe = Txt.Raw(word, "anton", 64, 0.06);
        var tw = probe.TightBounds.Width;
        double BW = tw + (style == 2 ? 84 : 56), BH = style == 2 ? 108 : 100;
        SKPath ink0;
        if (style == 2)
        {
            // oval seal: thick ellipse ring + thin inner ring, text with rules above/below
            using var o1 = Geo.Ellipse(BW / 2, BH / 2, BW / 2, BH / 2); using var o2 = Geo.Ellipse(BW / 2, BH / 2, BW / 2 - 7, BH / 2 - 7);
            using var ring = Geo.Diff(o1, o2);
            using var i1 = Geo.Ellipse(BW / 2, BH / 2, BW / 2 - 12, BH / 2 - 12); using var i2 = Geo.Ellipse(BW / 2, BH / 2, BW / 2 - 14.5, BH / 2 - 14.5);
            using var ring2 = Geo.Diff(i1, i2);
            using var text = Txt.Fit(word, "anton", 52, 40, 30, BW - 80, BH - 60, tracking: 0.06);
            using var rules = Geo.Concat(new[] { Geo.Rect(40, 24, BW - 80, 3), Geo.Rect(40, BH - 27, BW - 80, 3) });
            ink0 = Geo.UnionAll(new[] { ring, ring2, text, rules });
        }
        else
        {
            using var o1 = Geo.RRect(0, 0, BW, BH, style == 0 ? 10 : 18); using var o2 = Geo.RRect(7, 7, BW - 14, BH - 14, style == 0 ? 5 : 12);
            using var ring = Geo.Diff(o1, o2);
            SKPath? inner = null;
            if (style == 0)
            {
                using var i1 = Geo.RRect(12, 12, BW - 24, BH - 24, 3); using var i2 = Geo.RRect(14.5, 14.5, BW - 29, BH - 29, 2);
                inner = Geo.Diff(i1, i2);
            }
            using var text = Txt.Fit(word, "anton", 64, 26, 22, BW - 52, BH - 44, tracking: 0.06);
            ink0 = inner is null ? Geo.Union(ring, text) : Geo.UnionAll(new[] { ring, inner, text });
            inner?.Dispose();
        }
        // distress: speckles, scratches and a couple of worn patches cut out of the ink
        var holes = new SKPath();
        var n = (int)(BW * BH / 70);
        for (var k = 0; k < n; k++)
        {
            var x = rnd.NextDouble() * BW; var y = rnd.NextDouble() * BH;
            var r = 0.35 + Math.Pow(rnd.NextDouble(), 3) * 2.4;
            Geo.AddDot(holes, x, y, r, rnd, 1.3);
        }
        for (var k = 0; k < 9; k++)
        {
            var x = rnd.NextDouble() * BW; var y = rnd.NextDouble() * BH; var len = 8 + rnd.NextDouble() * 26; var a = rnd.NextDouble() * 40 - 20;
            using var sc = Geo.Rotate(Geo.RRect(x, y, len, 0.9 + rnd.NextDouble() * 0.8, 0.4), a, x, y);
            holes.AddPath(sc);
        }
        for (var k = 0; k < 3; k++)
        {
            var cx = rnd.NextDouble() * BW; var cy = rnd.NextDouble() * BH;
            for (var j = 0; j < 40; j++)
            {
                var x = cx + (rnd.NextDouble() - 0.5) * 30; var y = cy + (rnd.NextDouble() - 0.5) * 18; var r = 0.6 + rnd.NextDouble() * 1.4;
                Geo.AddDot(holes, x, y, r, rnd);
            }
        }
        using var holesU = holes.Simplify() ?? holes;
        using var worn = Geo.Diff(ink0, holesU);
        ink0.Dispose(); holes.Dispose();
        var angle = (rnd.NextDouble() < 0.5 ? -1 : 1) * (2 + rnd.NextDouble() * 4);
        using var rotated = Geo.Rotate(worn, angle, BW / 2, BH / 2);
        var b = rotated.TightBounds;
        using var placed = Geo.Translate(rotated, 6 - b.Left, 6 - b.Top);
        var s = new SvgDoc(b.Width + 12, b.Height + 12);
        var pb = placed.TightBounds;
        // uneven ink density: diagonal opacity variation
        s.Path(placed, s.Linear(pb.Left, pb.Top, pb.Right, pb.Bottom, new Stop(0, ink, 0.95), new Stop(0.35, ink, 0.78), new Stop(0.6, ink, 0.97), new Stop(1, ink, 0.82)));
        return s.ToString();
    }

    // ------------------------------------------------------------------ comic bursts

    private static readonly (string Word, L Loc)[] ComicWords =
    {
        ("WOW", new("wow", "wow", "guau", "waouh", "ワオ", "uau")), ("POW", new("pow", "peng", "pum", "paf", "バン", "pow")),
        ("BOOM", new("boom", "bumm", "bum", "boum", "ドカン", "bum")), ("OOPS", new("oops", "hoppla", "uy", "oups", "おっと", "opa")),
        ("YES!", new("yes", "ja", "sí", "oui", "はい", "sim")), ("NO!", new("no", "nein", "no", "non", "いいえ", "não")),
        ("NEW!", new("new", "neu", "nuevo", "nouveau", "新しい", "novo")), ("HOT!", new("hot", "heiß", "caliente", "chaud", "ホット", "quente")),
        ("OMG", new("omg", "omg", "dios mío", "omg", "なんと", "meu deus")), ("AHA!", new("aha", "aha", "ajá", "eurêka", "なるほど", "ahá")),
    };

    private static void Comics(Pack pack)
    {
        L comic = new("comic", "Comic", "cómic", "BD", "コミック", "quadrinhos");
        L burst = new("burst", "Explosion", "explosión", "explosion", "爆発", "explosão");
        L cloud = new("cloud", "Wolke", "nube", "nuage", "雲", "nuvem");
        var i = 0;
        foreach (var (word, loc) in ComicWords)
        {
            var slug = FluentPack.Slug(word);
            pack.Add($"comic-{slug}-burst", ComicBurst(word, i, false), T.Paren(L.Same(word), burst), loc, comic, burst);
            pack.Add($"comic-{slug}-cloud", ComicBurst(word, i, true), T.Paren(L.Same(word), cloud), loc, comic, cloud);
            i++;
        }
    }

    private static string ComicBurst(string word, int index, bool cloud)
    {
        const double W = 192, H = 156, cx = 96, cy = 80;
        var rnd = new Random(500 + index * 7 + (cloud ? 3 : 0));
        var s = new SvgDoc(W, H);
        SKPath shape;
        if (!cloud)
        {
            var pts = new List<(double, double)>();
            const int spikes = 15;
            for (var k = 0; k < spikes * 2; k++)
            {
                var a = (k * 180.0 / spikes - 90 + rnd.NextDouble() * 6) * Math.PI / 180;
                var r = k % 2 == 0 ? 0.86 + rnd.NextDouble() * 0.16 : 0.6 + rnd.NextDouble() * 0.06;
                pts.Add((cx + Math.Cos(a) * 88 * r, cy + Math.Sin(a) * 70 * r));
            }
            shape = Geo.Poly(pts);
            // speed lines
            for (var k = 0; k < 7; k++)
            {
                var a = (k * 360.0 / 7 + 20 + rnd.NextDouble() * 12) * Math.PI / 180;
                using var l = Geo.Open(new List<(double, double)> { (cx + Math.Cos(a) * 92, cy + Math.Sin(a) * 73), (cx + Math.Cos(a) * 99, cy + Math.Sin(a) * 79) });
                using var st = Geo.Stroke(l, 3.2);
                s.Path(st, "#1B1B1F");
            }
        }
        else
        {
            var parts = new List<SKPath> { Geo.Ellipse(cx, cy, 64, 46) };
            for (var k = 0; k < 12; k++)
            {
                var a = k * 30.0 * Math.PI / 180;
                var r = 21 + rnd.NextDouble() * 7;
                parts.Add(Geo.Circle(cx + Math.Cos(a) * 66, cy + Math.Sin(a) * 48, r));
            }
            shape = Geo.UnionAll(parts);
            foreach (var p in parts) p.Dispose();
        }
        var palettes = new[]
        {
            ("#FFF06B", "#FFB300", "#E8590C", "#F03E3E"), ("#7FDBFF", "#2B8CF0", "#1864AB", "#FFD43B"),
            ("#FF9CC2", "#F0447B", "#A61E4D", "#FFE066"), ("#B2F2BB", "#2FB344", "#2B8A3E", "#FFFFFF"),
        };
        var (c0, c1, dots, accent) = cloud ? palettes[1 + index % 3] : palettes[0];
        using var outline = Geo.Inflate(shape, 4.5);
        s.Shadow(outline, 3, 5, 6, 0.3);
        s.Path(outline, "#1B1B1F");
        s.Path(shape, s.Radial(cx - 10, cy - 14, 100, new[] { new Stop(0, c0), new Stop(0.55, c1), new Stop(1, dots) }));
        // halftone: dot size grows towards the lower right
        var halftone = new SKPath();
        for (var y = 6.0; y < H; y += 7)
            for (var x = 6.0 + (y / 7 % 2) * 3.5; x < W; x += 7)
            {
                var t = Math.Clamp(((x - cx) + (y - cy)) / 160 + 0.45, 0, 1);
                var r = 0.4 + 2.5 * t * t;
                Geo.AddDot(halftone, x, y, r);
            }
        using (var inner = Geo.Deflate(shape, 2))
        using (var ht = Geo.Inter(inner, halftone)) s.Path(ht, dots, 0.42);
        halftone.Dispose();
        if (!cloud)
        {
            using var inner2 = Geo.Scale(shape, 0.62, 0.62, cx, cy);
            s.Path(inner2, s.Radial(cx, cy - 8, 60, new[] { new Stop(0, "#FFFFFF", 0.55), new Stop(1, "#FFFFFF", 0) }));
        }
        else
        {
            using var hl = Geo.Ellipse(cx - 26, cy - 34, 34, 12);
            s.Path(hl, "#FFFFFF", 0.45);
        }
        // lettering: extruded, outlined, tilted
        using var text0 = Txt.Fit(word, "bangers", 74, 30, 40, W - 60, H - 72, tracking: 0.03);
        using var text = Geo.Rotate(text0, -7, cx, cy);
        using var extrude = Geo.Translate(text, 3.5, 4.5);
        using var extrudeOutline = Geo.Inflate(extrude, 4);
        using var textOutline = Geo.Inflate(text, 4);
        s.Path(extrudeOutline, "#1B1B1F");
        s.Path(textOutline, "#1B1B1F");
        var tb = text.TightBounds;
        s.Path(extrude, cloud ? "#1B1B1F" : "#C92A2A");
        s.Path(text, cloud ? s.V(tb.Top, tb.Bottom, new Stop(0, "#FFFFFF"), new Stop(1, accent == "#FFFFFF" ? "#E6FCF5" : accent)) : s.V(tb.Top, tb.Bottom, new Stop(0, "#FFF9DB"), new Stop(1, "#FFD43B")));
        shape.Dispose();
        return s.ToString();
    }

    // ------------------------------------------------------------------ ribbons & banners

    private static readonly Hue[] RibbonHues = { Pal.Red, Pal.Blue, Pal.Green, Pal.Gold, Pal.Purple };

    private static void Ribbons(Pack pack)
    {
        L ribbon = new("ribbon", "Band", "cinta", "ruban", "リボン", "fita");
        L banner = new("banner", "Banner", "pancarta", "bannière", "バナー", "faixa");
        var kinds = new (string Key, L Name, Func<SvgDoc, Hue, bool> Draw, double W, double H)[]
        {
            ("banner", new("Folded banner", "Gefaltetes Banner", "Pancarta plegada", "Bannière pliée", "折りたたみバナー", "Faixa dobrada"), (s, h) => { FoldedBanner(s, h); return true; }, 256, 104),
            ("ribbon-right", new("Ribbon pointing right", "Band nach rechts", "Cinta hacia la derecha", "Ruban vers la droite", "右向きリボン", "Fita para a direita"), (s, h) => { Pennant(s, h, false); return true; }, 224, 80),
            ("ribbon-left", new("Ribbon pointing left", "Band nach links", "Cinta hacia la izquierda", "Ruban vers la gauche", "左向きリボン", "Fita para a esquerda"), (s, h) => { Pennant(s, h, true); return true; }, 224, 80),
            ("banner-curved", new("Curved banner", "Gebogenes Banner", "Pancarta curva", "Bannière courbe", "カーブしたバナー", "Faixa curva"), (s, h) => { CurvedBanner(s, h); return true; }, 256, 120),
            ("rosette", new("Award rosette", "Rosette", "Escarapela", "Rosette", "ロゼット", "Roseta"), (s, h) => { Rosette(s, h); return true; }, 128, 168),
        };
        foreach (var (key, name, draw, w, h) in kinds)
            foreach (var hue in RibbonHues)
            {
                var s = new SvgDoc(w, h);
                draw(s, hue);
                var cname = T.Color[hue.Key];
                pack.Add($"{key}-{hue.Key}", s.ToString(), T.Paren(name, cname), ribbon, banner, cname, StickerKw);
            }
    }

    private static void Stitch(SvgDoc s, double x0, double x1, double y, string color = "#FFFFFF", double opacity = 0.55)
    {
        var p = new SKPath();
        for (var x = x0; x < x1 - 4; x += 9) p.AddRoundRect(new SKRect((float)x, (float)(y - 0.9), (float)(x + 5), (float)(y + 0.9)), 0.9f, 0.9f);
        s.Path(p, color, opacity);
        p.Dispose();
    }

    private static void FoldedBanner(SvgDoc s, Hue h)
    {
        using var lt = Geo.Poly(new List<(double, double)> { (6, 36), (54, 36), (54, 88), (6, 88), (22, 62) });
        using var rt = Geo.Poly(new List<(double, double)> { (250, 36), (202, 36), (202, 88), (250, 88), (234, 62) });
        using var band = Geo.Rect(38, 12, 180, 58);
        using var all = Geo.UnionAll(new[] { lt, rt, band });
        s.Shadow(all, 0, 4, 6, 0.28);
        s.Path(lt, s.V(36, 88, new Stop(0, h.Mid), new Stop(1, h.Lo)));
        s.Path(rt, s.V(36, 88, new Stop(0, h.Mid), new Stop(1, h.Lo)));
        using var lf = Geo.Poly(new List<(double, double)> { (38, 70), (54, 70), (54, 88) });
        using var rf = Geo.Poly(new List<(double, double)> { (218, 70), (202, 70), (202, 88) });
        s.Path(lf, h.Edge); s.Path(rf, h.Edge);
        s.Path(band, s.V(12, 70, new Stop(0, h.Hi), new Stop(0.45, h.Mid), new Stop(1, h.Lo)));
        using var top = Geo.Rect(38, 12, 180, 3); s.Path(top, "#FFFFFF", 0.45);
        using var gl = Geo.Rect(38, 15, 180, 18); s.Path(gl, s.V(15, 33, new Stop(0, "#FFFFFF", 0.3), new Stop(1, "#FFFFFF", 0)));
        Stitch(s, 46, 210, 19); Stitch(s, 46, 210, 63);
    }

    private static void Pennant(SvgDoc s, Hue h, bool left)
    {
        var pts = new List<(double, double)> { (8, 12), (192, 12), (216, 40), (192, 68), (8, 68), (26, 40) };
        if (left) pts = pts.Select(p => (224 - p.Item1, p.Item2)).Reverse().ToList();
        using var shape = Geo.RoundPoly(pts, 3);
        Art.Glossy(s, shape, h, 0.8, 0.025, 0.8);
        Stitch(s, left ? 40 : 30, left ? 194 : 184, 20); Stitch(s, left ? 40 : 30, left ? 194 : 184, 60);
    }

    private static void CurvedBanner(SvgDoc s, Hue h)
    {
        SKPath Band(double y0, double y1, double x0, double x1)
        {
            var p = new SKPath();
            p.MoveTo((float)x0, (float)y0); p.QuadTo(128, (float)(y0 - 36), (float)x1, (float)y0);
            p.LineTo((float)x1, (float)y1); p.QuadTo(128, (float)(y1 - 36), (float)x0, (float)y1); p.Close();
            return p;
        }
        using var lt = Geo.Poly(new List<(double, double)> { (4, 66), (48, 58), (54, 106), (10, 112), (24, 88) });
        using var rt = Geo.Poly(new List<(double, double)> { (252, 66), (208, 58), (202, 106), (246, 112), (232, 88) });
        using var band = Band(52, 98, 34, 222);
        using var all = Geo.UnionAll(new[] { lt, rt, band });
        s.Shadow(all, 0, 4, 6, 0.28);
        s.Path(lt, s.V(58, 112, new Stop(0, h.Mid), new Stop(1, h.Lo)));
        s.Path(rt, s.V(58, 112, new Stop(0, h.Mid), new Stop(1, h.Lo)));
        using var lf = Geo.Poly(new List<(double, double)> { (34, 98), (52, 92), (54, 106) });
        using var rf = Geo.Poly(new List<(double, double)> { (222, 98), (204, 92), (202, 106) });
        s.Path(lf, h.Edge); s.Path(rf, h.Edge);
        s.Path(band, s.V(34, 98, new Stop(0, h.Hi), new Stop(0.5, h.Mid), new Stop(1, h.Lo)));
        using var hi = Band(52, 60, 34, 222);
        s.Path(hi, "#FFFFFF", 0.3);
    }

    private static void Rosette(SvgDoc s, Hue h)
    {
        using var t1 = Geo.Poly(new List<(double, double)> { (40, 80), (64, 92), (52, 160), (38, 146), (24, 156) });
        using var t2 = Geo.Poly(new List<(double, double)> { (88, 80), (64, 92), (76, 160), (90, 146), (104, 156) });
        s.Shadow(t1, 0, 3, 4, 0.25); s.Shadow(t2, 0, 3, 4, 0.25);
        s.Path(t1, s.V(80, 160, new Stop(0, h.Lo), new Stop(1, h.Mid)));
        s.Path(t2, s.V(80, 160, new Stop(0, h.Edge), new Stop(1, h.Lo)));
        var parts = new List<SKPath>();
        for (var k = 0; k < 16; k++)
        {
            var a = k * 22.5 * Math.PI / 180;
            parts.Add(Geo.Circle(64 + Math.Cos(a) * 46, 62 + Math.Sin(a) * 46, 11));
        }
        parts.Add(Geo.Circle(64, 62, 48));
        using var scallop = Geo.UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        Art.Glossy(s, scallop, h, 1, 0.025, 0.5);
        using var disc = Geo.Circle(64, 62, 36);
        Art.Glossy(s, disc, Pal.Gold, 0.5, 0.05, 1);
        using var star = Geo.RoundPoly(Geo.StarPts(64, 63, 5, 22, 10), 2);
        Art.Ink(s, star, "#FFFFFF", 0.22);
    }

    // ------------------------------------------------------------------ bubbles

    private static readonly (string Key, string Hi, string Mid, string Edge, L Name)[] BubbleTints =
    {
        ("lavender", "#FFFFFF", "#E7E2FF", "#AFA2F0", T.Color["lavender"]),
        ("sky", "#FFFFFF", "#D3ECFF", "#77B6F2", T.Color["sky"]),
        ("mint", "#FFFFFF", "#D1F7E4", "#6FCB9F", T.Color["mint"]),
        ("yellow", "#FFFFFF", "#FFF4B8", "#F0C64A", T.Color["yellow"]),
        ("pink", "#FFFFFF", "#FFDCEB", "#F08DBA", T.Color["pink"]),
    };

    private static void Bubbles(Pack pack)
    {
        L bubble = new("speech bubble", "Sprechblase", "bocadillo", "bulle", "吹き出し", "balão de fala");
        var kinds = new (string Key, L Name, Func<SKPath> Make, double W, double H)[]
        {
            ("speech-left", new("Speech bubble (tail left)", "Sprechblase (links)", "Bocadillo (izquierda)", "Bulle (à gauche)", "吹き出し（左）", "Balão de fala (esquerda)"), () => OvalBubble(false), 176, 152),
            ("speech-right", new("Speech bubble (tail right)", "Sprechblase (rechts)", "Bocadillo (derecha)", "Bulle (à droite)", "吹き出し（右）", "Balão de fala (direita)"), () => OvalBubble(true), 176, 152),
            ("speech-box", new("Speech box", "Sprechkasten", "Cuadro de diálogo", "Bulle rectangulaire", "四角い吹き出し", "Caixa de fala"), RectBubble, 192, 144),
            ("thought", new("Thought bubble", "Gedankenblase", "Bocadillo de pensamiento", "Bulle de pensée", "思考の吹き出し", "Balão de pensamento"), ThoughtBubble, 176, 152),
            ("shout", new("Shout bubble", "Rufblase", "Bocadillo de grito", "Bulle de cri", "叫びの吹き出し", "Balão de grito"), ShoutBubble, 176, 152),
        };
        foreach (var (key, name, make, w, h) in kinds)
            foreach (var (tint, hi, mid, edge, tname) in BubbleTints)
            {
                var s = new SvgDoc(w, h);
                using var shape = make();
                GlossyBubble(s, shape, hi, mid, edge);
                pack.Add($"bubble-{key}-{tint}", s.ToString(), T.Paren(name, tname), bubble, tname, StickerKw);
            }
    }

    private static void GlossyBubble(SvgDoc s, SKPath shape, string hi, string mid, string edge)
    {
        var b = shape.TightBounds;
        s.Shadow(shape, 0, 5, 9, 0.24);
        s.Path(shape, s.Radial(b.Left + b.Width * 0.38, b.Top + b.Height * 0.3, Math.Max(b.Width, b.Height) * 0.85,
            new[] { new Stop(0, hi), new Stop(0.5, mid), new Stop(1, edge) }));
        // inner rim shading + soft specular highlight
        using var low = Geo.BottomRim(shape, 4); s.Path(low, edge, 0.5);
        using var rim = Geo.TopRim(shape, 2.5); s.Path(rim, "#FFFFFF", 0.9);
        var hx = b.Left + b.Width * 0.3; var hy = b.Top + b.Height * 0.22;
        using var spec = Geo.Ellipse(hx, hy, b.Width * 0.2, b.Height * 0.09);
        s.Path(spec, s.Radial(hx, hy, b.Width * 0.2, new[] { new Stop(0, "#FFFFFF", 0.95), new Stop(1, "#FFFFFF", 0) }, 1, 0.45));
    }

    private static SKPath OvalBubble(bool right)
    {
        using var body = Geo.Ellipse(88, 64, 80, 56);
        var tail = new SKPath();
        tail.MoveTo(46, 100); tail.QuadTo(44, 128, 18, 144); tail.QuadTo(58, 140, 82, 112); tail.Close();
        var u = Geo.Union(body, tail);
        tail.Dispose();
        if (!right) return u;
        var m = Geo.Transform(u, SKMatrix.CreateScale(-1, 1, 88, 0));
        u.Dispose();
        return m;
    }

    private static SKPath RectBubble()
    {
        using var body = Geo.RRect(8, 8, 176, 100, 26);
        using var tail = Geo.RoundPoly(new List<(double, double)> { (128, 100), (160, 100), (170, 138) }, 3);
        return Geo.Union(body, tail);
    }

    private static SKPath ThoughtBubble()
    {
        var parts = new List<SKPath> { Geo.Ellipse(92, 60, 66, 42) };
        var rnd = new Random(77);
        for (var k = 0; k < 11; k++)
        {
            var a = k * (360.0 / 11) * Math.PI / 180;
            parts.Add(Geo.Circle(92 + Math.Cos(a) * 64, 60 + Math.Sin(a) * 42, 18 + rnd.NextDouble() * 6));
        }
        parts.Add(Geo.Circle(36, 118, 11));
        parts.Add(Geo.Circle(18, 140, 7));
        var u = Geo.UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        return u;
    }

    private static SKPath ShoutBubble()
    {
        var rnd = new Random(91);
        var pts = new List<(double, double)>();
        for (var k = 0; k < 28; k++)
        {
            var a = (k * 360.0 / 28 - 90) * Math.PI / 180;
            var r = k % 2 == 0 ? 1.0 + rnd.NextDouble() * 0.08 : 0.8;
            pts.Add((88 + Math.Cos(a) * 82 * r, 66 + Math.Sin(a) * 58 * r));
        }
        pts.Insert(17, (30, 146));
        return Geo.RoundPoly(pts, 2);
    }

    // ------------------------------------------------------------------ sticky notes

    private static readonly (string Key, string Light, string Base, string Deep, L Name)[] NoteColors =
    {
        ("yellow", "#FFF7B0", "#FFE769", "#E6C433", T.Color["yellow"]), ("pink", "#FFD6E7", "#FFAFCF", "#E8799F", T.Color["pink"]),
        ("blue", "#D6ECFF", "#A9D5FF", "#6FAEE8", T.Color["blue"]), ("green", "#DDF8D5", "#B4ECA6", "#78C96A", T.Color["green"]),
        ("orange", "#FFE3C2", "#FFC789", "#EE9A45", T.Color["orange"]),
    };

    private static void Notes(Pack pack)
    {
        L note = new("sticky note", "Haftnotiz", "nota adhesiva", "pense-bête", "付箋", "nota adesiva");
        L pinned = new("pinned", "angepinnt", "con chincheta", "épinglé", "ピン留め", "fixada");
        L taped = new("taped", "angeklebt", "con cinta", "scotché", "テープ留め", "com fita");
        foreach (var variant in new[] { 0, 1, 2 })
            foreach (var (key, light, bas, deep, cname) in NoteColors)
            {
                var s = new SvgDoc(128, 128);
                StickyNote(s, light, bas, deep, variant, key.Length + variant * 5);
                var suffix = variant switch { 0 => "", 1 => "-pinned", _ => "-taped" };
                var name = variant switch { 0 => T.Paren(note, cname), 1 => T.Paren(note, new L("{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}・{1}", "{0}, {1}").F(cname, pinned)), _ => T.Paren(note, new L("{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}・{1}", "{0}, {1}").F(cname, taped)) };
                pack.Add($"note-{key}{suffix}", s.ToString(), name, note, cname, variant == 1 ? pinned : variant == 2 ? taped : note, StickerKw);
            }
    }

    private static void StickyNote(SvgDoc s, string light, string bas, string deep, int variant, int seed)
    {
        var angle = new[] { -3.0, 2.0, -1.5, 3.0, -2.5 }[seed % 5];
        using var paper0 = Geo.Poly(new List<(double, double)> { (14, 12), (114, 12), (114, 94), (96, 114), (14, 114) });
        using var paper = Geo.Rotate(paper0, angle, 64, 64);
        // curl shadow: stronger under the lifted corner
        using var sh0 = Geo.Poly(new List<(double, double)> { (18, 20), (114, 20), (118, 100), (100, 120), (18, 116) });
        using var sh = Geo.Rotate(sh0, angle, 64, 64);
        s.Shadow(sh, 1, 3, 6, 0.3);
        var b = paper.TightBounds;
        s.Path(paper, s.V(b.Top, b.Bottom, new Stop(0, light), new Stop(0.6, bas), new Stop(1, bas)));
        using var band0 = Geo.Rect(14, 12, 100, 16);
        using var band = Geo.Rotate(band0, angle, 64, 64);
        s.Path(band, deep, 0.18);
        using var fold0 = Geo.Poly(new List<(double, double)> { (114, 94), (96, 114), (98, 98) });
        using var fold = Geo.Rotate(fold0, angle, 64, 64);
        var fb = fold.TightBounds;
        s.Path(fold, s.Linear(fb.Left, fb.Bottom, fb.Right, fb.Top, new Stop(0, light), new Stop(1, deep)));
        using var rim = Geo.TopRim(paper, 1.5); s.Path(rim, "#FFFFFF", 0.6);
        // faint ruled lines
        for (var k = 0; k < 4; k++)
        {
            using var l0 = Geo.Rect(26, 46 + k * 16, 72 - (k == 3 ? 26 : 0), 1.6);
            using var l = Geo.Rotate(l0, angle, 64, 64);
            s.Path(l, deep, 0.35);
        }
        if (variant == 1)
        {
            s.GroundShadow(70, 24, 13, 6, 0.35);
            using var pin = Geo.Circle(64, 16, 11);
            Art.Glossy(s, pin, Pal.Red, 0.5, 0.06, 1);
        }
        if (variant == 2)
        {
            using var tape0 = TornStrip(38, 2, 52, 20, seed);
            using var tape = Geo.Rotate(tape0, angle - 4, 64, 12);
            s.Path(tape, "#FFFFFF", 0.55);
            using var tr = Geo.TopRim(tape, 1.2); s.Path(tr, "#FFFFFF", 0.6);
        }
    }

    private static SKPath TornStrip(double x, double y, double w, double h, int seed)
    {
        var rnd = new Random(seed);
        var pts = new List<(double, double)> { (x, y), (x + w, y) };
        var teeth = Math.Max(3, (int)(h / 4));
        for (var k = 1; k <= teeth; k++) pts.Add((x + w + (k % 2 == 0 ? 0 : 2.5 + rnd.NextDouble() * 2), y + h * k / teeth));
        pts.Add((x, y + h));
        for (var k = teeth - 1; k >= 1; k--) pts.Add((x + (k % 2 == 0 ? 0 : -2.5 - rnd.NextDouble() * 2), y + h * k / teeth));
        return Geo.Poly(pts);
    }

    // ------------------------------------------------------------------ washi tape

    private static void Washi(Pack pack)
    {
        L tape = new("washi tape", "Washi-Tape", "cinta washi", "masking tape", "マスキングテープ", "fita washi");
        L stripes = new("stripes", "Streifen", "rayas", "rayures", "ストライプ", "listras");
        L dots = new("dots", "Punkte", "lunares", "pois", "水玉", "bolinhas");
        L grid = new("grid", "Karo", "cuadrícula", "quadrillage", "格子", "xadrez");
        L chevron = new("chevron", "Zickzack", "zigzag", "chevrons", "山形", "chevron");
        L plain = new("plain", "einfarbig", "liso", "uni", "無地", "liso");
        L stars = new("stars", "Sterne", "estrellas", "étoiles", "星", "estrelas");
        L hearts = new("hearts", "Herzen", "corazones", "cœurs", "ハート", "corações");
        var list = new (string Key, string Base, string Ink, int Pattern, L Name)[]
        {
            ("stripes-pink", "#FFC2D9", "#FFFFFF", 0, stripes), ("dots-blue", "#9CCBFF", "#FFFFFF", 1, dots), ("grid-mint", "#B6EED3", "#5DBB8E", 2, grid),
            ("chevron-yellow", "#FFE58A", "#F2B705", 3, chevron), ("kraft", "#D9B88A", "#B98E57", 4, plain), ("stripes-navy", "#3B5BA9", "#FFFFFF", 0, stripes),
            ("dots-red", "#F25F5C", "#FFE3E3", 1, dots), ("stars-purple", "#B9A6FF", "#FFFFFF", 5, stars), ("hearts-pink", "#FFD6E7", "#F06595", 6, hearts),
            ("plain-teal", "#7FDCCF", "#FFFFFF", 4, plain),
        };
        var i = 0;
        foreach (var (key, bas, ink, pattern, name) in list)
        {
            var s = new SvgDoc(224, 72);
            using var strip0 = TornStrip(14, 16, 196, 38, 300 + i);
            using var strip = Geo.Rotate(strip0, i % 2 == 0 ? -3 : 2.5, 112, 36);
            s.Shadow(strip, 0, 2, 3, 0.18);
            s.Path(strip, bas, 0.9);
            var pat = new SKPath();
            switch (pattern)
            {
                case 0: for (var x = -40.0; x < 260; x += 16) pat.AddPoly(new[] { new SKPoint((float)x, 72), new SKPoint((float)(x + 7), 72), new SKPoint((float)(x + 47), 0), new SKPoint((float)(x + 40), 0) }); break;
                case 1: for (var y = 6.0; y < 72; y += 12) for (var x = 4.0 + (y / 12 % 2) * 6; x < 230; x += 12) pat.AddCircle((float)x, (float)y, 2.6f); break;
                case 2:
                    for (var x = 4.0; x < 230; x += 12) pat.AddRect(new SKRect((float)x, 0, (float)(x + 1.6), 72));
                    for (var y = 4.0; y < 72; y += 12) pat.AddRect(new SKRect(0, (float)y, 224, (float)(y + 1.6)));
                    break;
                case 3:
                    for (var y = 10.0; y < 72; y += 14)
                    {
                        var zig = new SKPath(); zig.MoveTo(0, (float)y);
                        for (var x = 0.0; x < 230; x += 12) { zig.LineTo((float)(x + 6), (float)(y - 6)); zig.LineTo((float)(x + 12), (float)y); }
                        using var zs = Geo.Stroke(zig, 3, SKStrokeCap.Butt, SKStrokeJoin.Miter); zig.Dispose(); pat.AddPath(zs);
                    }
                    break;
                case 4: break;
                case 5: for (var x = 10.0; x < 230; x += 24) { using var st = Geo.Poly(Geo.StarPts(x, 26 + (x / 24 % 2) * 18, 5, 7, 3)); pat.AddPath(st); } break;
                case 6:
                    for (var x = 10.0; x < 230; x += 24)
                    {
                        var y = 28 + (x / 24 % 2) * 14;
                        var hrt = new SKPath();
                        hrt.MoveTo((float)x, (float)(y + 7)); hrt.CubicTo((float)(x - 10), (float)y, (float)(x - 6), (float)(y - 8), (float)x, (float)(y - 3));
                        hrt.CubicTo((float)(x + 6), (float)(y - 8), (float)(x + 10), (float)y, (float)x, (float)(y + 7)); hrt.Close();
                        pat.AddPath(hrt); hrt.Dispose();
                    }
                    break;
            }
            if (!pat.IsEmpty)
            {
                using var simple = pat.Simplify() ?? new SKPath(pat);
                using var clip = Geo.Inter(strip, simple);
                s.Path(clip, ink, 0.75);
            }
            pat.Dispose();
            // fibre sheen
            var sb = strip.TightBounds;
            s.Path(strip, s.V(sb.Top, sb.Bottom, new Stop(0, "#FFFFFF", 0.35), new Stop(0.45, "#FFFFFF", 0.05), new Stop(1, "#000000", 0.06)));
            pack.Add($"washi-{key}", s.ToString(), T.Paren(tape, name), tape, name, StickerKw);
            i++;
        }
    }

    // ------------------------------------------------------------------ pixel art

    private static readonly (string Key, L Name, Dictionary<char, string> Pal, string[] Rows)[] Sprites =
    {
        ("heart", new("Pixel heart", "Pixelherz", "Corazón pixelado", "Cœur pixel", "ドット絵のハート", "Coração pixelado"),
            new() { ['k'] = "#2B1B1E", ['r'] = "#E8344A", ['d'] = "#A3162A", ['w'] = "#FFFFFF" },
            new[] { "................", "................", "..kkkk....kkkk..", ".krrrrk..krrrrk.", "krwwrrrkkrrrrrrk", "krwrrrrrrrrrrrrk", "krrrrrrrrrrrrrrk", "krrrrrrrrrrrrrdk",
                    ".krrrrrrrrrrrdk.", "..krrrrrrrrrdk..", "...krrrrrrrdk...", "....krrrrrdk....", ".....krrrdk.....", "......krdk......", ".......kk.......", "................" }),
        ("coin", new("Pixel coin", "Pixelmünze", "Moneda pixelada", "Pièce pixel", "ドット絵のコイン", "Moeda pixelada"),
            new() { ['k'] = "#3B2A00", ['y'] = "#FFD43B", ['o'] = "#E09A00", ['w'] = "#FFF6CC", ['s'] = "#B87503" },
            new[] { "................", ".....kkkkkk.....", "...kkyyyyyykk...", "..kyywwyyyyyyk..", "..kywyyyyyyyok..", ".kywyyyssyyyyok.", ".kywyyyssyyyyok.", ".kywyyyssyyyyok.",
                    ".kyyyyyssyyyyok.", ".kyyyyyssyyyyok.", ".kyyyyyssyyyyok.", "..kyyyyyyyyyok..", "..kyyyyyyyyook..", "...kkooooookk...", ".....kkkkkk.....", "................" }),
        ("star", new("Pixel star", "Pixelstern", "Estrella pixelada", "Étoile pixel", "ドット絵の星", "Estrela pixelada"),
            new() { ['k'] = "#4A3200", ['y'] = "#FFD43B", ['o'] = "#F08C00", ['w'] = "#FFF9DB" },
            new[] { "................", ".......kk.......", "......kyyk......", "......kwyk......", ".....kwyyyk.....", "kkkkkkwyyyykkkkk", "kyyyyyyyyyyyyyok", ".kyyyyyyyyyyyok.",
                    "..kyyykyykyyok..", "...kyykyykyok...", "...kyyyyyyyok...", "..kyyyyyyyyyok..", "..kyyyyokyyyok..", ".kyyyok..kyyyok.", ".kyok......kyok.", ".kk..........kk." }),
        ("trophy", new("Pixel trophy", "Pixelpokal", "Trofeo pixelado", "Trophée pixel", "ドット絵のトロフィー", "Troféu pixelado"),
            new() { ['k'] = "#3D2B00", ['y'] = "#FFD43B", ['o'] = "#E09A00", ['w'] = "#FFF6CC", ['b'] = "#8B5A2B" },
            new[] { "................", "..kkkkkkkkkkkk..", "kkkywyyyyyyyokkk", "k.kywyyyyyyyok.k", "k.kywyyyyyyyok.k", ".kkywyyyyyyyokk.", "...kwyyyyyyok...", "...kyyyyyyyok...",
                    "....kyyyyyok....", ".....kyyyok.....", "......kyok......", "......kyok......", ".....kyyyok.....", "...kkkkkkkkkk...", "...kbbbbbbbbk...", "...kkkkkkkkkk..." }),
        ("key", new("Pixel key", "Pixelschlüssel", "Llave pixelada", "Clé pixel", "ドット絵の鍵", "Chave pixelada"),
            new() { ['k'] = "#3D2B00", ['y'] = "#FFD43B", ['o'] = "#E09A00", ['w'] = "#FFF6CC" },
            new[] { "................", "................", "................", "................", "..kkkk..........", ".kyyyyk.........", "kywkkyyk........", "kyk..kykkkkkkkk.",
                    "kyk..kyyyyyyyyyk", "kyykkyoooooooook", ".kooook.kok.kok.", "..kkkk...k...k..", "................", "................", "................", "................" }),
        ("potion-red", new("Red potion", "Roter Trank", "Poción roja", "Potion rouge", "赤いポーション", "Poção vermelha"),
            new() { ['k'] = "#1E1530", ['c'] = "#A0703C", ['g'] = "#DDE7F0", ['l'] = "#E8344A", ['d'] = "#A3162A", ['w'] = "#FFFFFF" }, PotionRows),
        ("potion-blue", new("Blue potion", "Blauer Trank", "Poción azul", "Potion bleue", "青いポーション", "Poção azul"),
            new() { ['k'] = "#1E1530", ['c'] = "#A0703C", ['g'] = "#DDE7F0", ['l'] = "#3B82F6", ['d'] = "#1D4ED8", ['w'] = "#FFFFFF" }, PotionRows),
        ("potion-green", new("Green potion", "Grüner Trank", "Poción verde", "Potion verte", "緑のポーション", "Poção verde"),
            new() { ['k'] = "#1E1530", ['c'] = "#A0703C", ['g'] = "#DDE7F0", ['l'] = "#2FB344", ['d'] = "#1B7A2E", ['w'] = "#FFFFFF" }, PotionRows),
        ("gem", new("Pixel gem", "Pixeledelstein", "Gema pixelada", "Gemme pixel", "ドット絵の宝石", "Gema pixelada"),
            new() { ['k'] = "#102A43", ['a'] = "#A5F3FC", ['b'] = "#22D3EE", ['c'] = "#0891B2", ['d'] = "#155E75", ['w'] = "#FFFFFF" },
            new[] { "................", "................", "...kkkkkkkkkk...", "..kwaabbbbaabk..", ".kwaabbbbbbaabk.", "kkkkkkkkkkkkkkkk", "kabbbbccccbbbbck", ".kabbbccccbbbck.",
                    "..kabbccccbbck..", "...kabccccbck...", "....kbccccdk....", ".....kcccdk.....", "......kcdk......", ".......kk.......", "................", "................" }),
        ("crown", new("Pixel crown", "Pixelkrone", "Corona pixelada", "Couronne pixel", "ドット絵の王冠", "Coroa pixelada"),
            new() { ['k'] = "#3D2B00", ['y'] = "#FFD43B", ['o'] = "#E09A00", ['w'] = "#FFF6CC", ['r'] = "#E8344A", ['b'] = "#1C7ED6" },
            new[] { "................", "................", "................", ".k.....kk.....k.", "kyk...kyyk...kyk", "kyyk.kyyyyk.kyyk", "kyyykyyyyyykyyyk", "kywyyyyyyyyyyyok",
                    "kyyyyryybyyryyok", "kywyyyyyyyyyyyok", "kyyyyyyyyyyyyyok", "kooooooooooooook", "kkkkkkkkkkkkkkkk", "................", "................", "................" }),
        ("sword", new("Pixel sword", "Pixelschwert", "Espada pixelada", "Épée pixel", "ドット絵の剣", "Espada pixelada"),
            new() { ['k'] = "#1E1E24", ['s'] = "#E9ECEF", ['t'] = "#ADB5BD", ['w'] = "#FFFFFF", ['h'] = "#8B5A2B", ['g'] = "#FFD43B" },
            new[] { ".............kkk", "............kwsk", "...........kwstk", "..........kwstk.", ".........kwstk..", "........kwstk...", ".......kwstk....", "..kk..kwstk.....",
                    "..kgkkwstk......", "...kgkstk.......", "....kgkk........", "...khkgk........", "..khk.kgk.......", ".khk...kk.......", "kgk.............", ".k.............." }),
        ("shield", new("Pixel shield", "Pixelschild", "Escudo pixelado", "Bouclier pixel", "ドット絵の盾", "Escudo pixelado"),
            new() { ['k'] = "#102A43", ['s'] = "#CED4DA", ['b'] = "#1C7ED6", ['d'] = "#1864AB", ['w'] = "#74C0FC", ['y'] = "#FFD43B" },
            new[] { "..kkkkkkkkkkkk..", ".kssssssssssssk.", ".ksbbbbbbbbddsk.", ".ksbwbbyybbddsk.", ".ksbwbyyyybddsk.", ".ksbwyyyyyyddsk.", ".ksbbbyyyybddsk.", ".ksbbbbyybbddsk.",
                    "..ksbbbbbbddsk..", "..ksbbbbbbddsk..", "...ksbbbbddsk...", "....ksbbbdsk....", ".....ksbdsk.....", "......kssk......", ".......kk.......", "................" }),
        ("bomb", new("Pixel bomb", "Pixelbombe", "Bomba pixelada", "Bombe pixel", "ドット絵の爆弾", "Bomba pixelada"),
            new() { ['k'] = "#111216", ['b'] = "#343A40", ['l'] = "#5C636B", ['w'] = "#ADB5BD", ['f'] = "#A0703C", ['s'] = "#FFD43B", ['o'] = "#FF922B" },
            new[] { "............s...", "..........s.os.s", "...........sos..", "..........fs....", ".........f......", "......kkf.......", "....kkbbkk......", "...kbbbbbbk.....",
                    "..kbwwbbbbbk....", ".kbwbbbbbbbbk...", ".kbwbbbbbbbbk...", ".kbbbbbbbbbbk...", ".kbbbbbbbbblk...", "..kbbbbbbbllk...", "...kbbbbbllk....", ".....kkkkk......" }),
        ("chest", new("Treasure chest", "Schatztruhe", "Cofre del tesoro", "Coffre au trésor", "宝箱", "Baú do tesouro"),
            new() { ['k'] = "#2B1B0E", ['w'] = "#A0642D", ['d'] = "#6F421B", ['l'] = "#C98A4B", ['g'] = "#FFD43B", ['o'] = "#E09A00" },
            new[] { "................", "................", "..kkkkkkkkkkkk..", ".kllllllllllllk.", ".kwwwwwwwwwwwwk.", ".kwwwwwwwwwwwwk.", "kgggggggggggggok", "kdddddkggkdddddk",
                    "kwwwwwkgokwwwwwk", "kwwwwwwkkwwwwwwk", "kwwwwwwwwwwwwwwk", "kwwwwwwwwwwwwwwk", "kddddddddddddddk", "kkkkkkkkkkkkkkkk", "................", "................" }),
        ("apple", new("Pixel apple", "Pixelapfel", "Manzana pixelada", "Pomme pixel", "ドット絵のりんご", "Maçã pixelada"),
            new() { ['k'] = "#2B0B0B", ['r'] = "#E8344A", ['d'] = "#A3162A", ['w'] = "#FFD6DC", ['g'] = "#40C057", ['n'] = "#6F421B" },
            new[] { "........n.......", ".......n.gg.....", ".......ngg......", "...kkkknkkkk....", "..krrrrrrrrrk...", ".krwwrrrrrrrrk..", ".krwrrrrrrrrrk..", ".krrrrrrrrrrrk..",
                    ".krrrrrrrrrrdk..", ".krrrrrrrrrrdk..", "..krrrrrrrrdk...", "...krrrrrrdk....", "....kkk.kkk.....", "................", "................", "................" }),
        ("lightning", new("Pixel lightning", "Pixelblitz", "Rayo pixelado", "Éclair pixel", "ドット絵の稲妻", "Raio pixelado"),
            new() { ['k'] = "#3D2B00", ['y'] = "#FFD43B", ['o'] = "#F08C00", ['w'] = "#FFF9DB" },
            new[] { "..........kkkk..", ".........kyyok..", "........kyyok...", ".......kywok....", "......kywok.....", ".....kywyykkk...", "....kyyyyyyyk...", "...kkkkyyyok....",
                    "......kyyok.....", ".....kyyok......", "....kyyok.......", "...kyok.........", "..kyok..........", "..kok...........", "..kk............", "................" }),
    };

    private static string[] PotionRows => new[]
    {
        "......kkkk......", "......kcck......", "......kcck......", ".....kkkkkk.....", "......kggk......", "......kggk......", ".....kgggwk.....", "....kggggggk....",
        "...kggggggggk...", "..kllllllllllk..", "..klwllllllllk..", "..klwllllllldk..", "..kllllllllddk..", "...kllllllddk...", "....kkkkkkkk....", "................",
    };

    private static void Pixel(Pack pack)
    {
        L pixel = new("pixel art", "Pixelkunst", "pixel art", "pixel art", "ドット絵", "pixel art");
        L game = new("game", "Spiel", "juego", "jeu", "ゲーム", "jogo");
        foreach (var (key, name, pal, rows) in Sprites)
        {
            if (rows.Length != 16 || rows.Any(r => r.Length != 16)) throw new InvalidOperationException($"sprite {key}: rows must be 16x16 ({string.Join(",", rows.Select(r => r.Length))})");
            const double cell = 7, ox = 8, oy = 6;
            var s = new SvgDoc(128, 128);
            var all = new SKPath();
            var byColor = new Dictionary<char, SKPath>();
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; )
                {
                    var c = rows[y][x];
                    if (c == '.') { x++; continue; }
                    var x0 = x;
                    while (x < 16 && rows[y][x] == c) x++;
                    var r = new SKRect((float)(ox + x0 * cell), (float)(oy + y * cell), (float)(ox + x * cell), (float)(oy + (y + 1) * cell));
                    if (!byColor.TryGetValue(c, out var p)) byColor[c] = p = new SKPath();
                    p.AddRect(r);
                    all.AddRect(r);
                }
            using (var u = all.Simplify() ?? new SKPath(all))
            using (var sh = Geo.Translate(u, 4, 5)) s.Path(sh, "#000000", 0.22);
            foreach (var (c, p) in byColor)
            {
                if (!pal.TryGetValue(c, out var color)) throw new InvalidOperationException($"sprite {key}: no colour for '{c}'");
                using var simple = p.Simplify() ?? new SKPath(p);
                s.Path(simple, color);
                p.Dispose();
            }
            all.Dispose();
            pack.Add($"pixel-{key}", s.ToString(), name, pixel, game, StickerKw, L.Same("8-bit"));
        }
    }
}
