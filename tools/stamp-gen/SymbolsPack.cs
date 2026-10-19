using SkiaSharp;

namespace StampGen;

/// <summary>Symbols: numbered/lettered badges, gradient arrows, marks (checks, crosses, punctuation, math, hand-drawn highlights).</summary>
internal static class SymbolsPack
{
    private static readonly L Number = new("Number {0}", "Zahl {0}", "Número {0}", "Numéro {0}", "数字 {0}", "Número {0}");
    private static readonly L LetterName = new("Letter {0}", "Buchstabe {0}", "Letra {0}", "Lettre {0}", "文字 {0}", "Letra {0}");
    private static readonly L Pair = new("{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}, {1}", "{0}・{1}", "{0}, {1}");
    private static readonly L NumberKw = new("number", "Zahl", "número", "nombre", "数字", "número");
    private static readonly L StepKw = new("step", "Schritt", "paso", "étape", "ステップ", "passo");
    private static readonly L BadgeKw = new("badge", "Abzeichen", "insignia", "badge", "バッジ", "emblema");
    private static readonly L LetterKw = new("letter", "Buchstabe", "letra", "lettre", "文字", "letra");
    private static readonly L ArrowKw = new("arrow", "Pfeil", "flecha", "flèche", "矢印", "seta");
    private static readonly L HandKw = new("hand-drawn", "handgezeichnet", "dibujado a mano", "dessiné à la main", "手書き", "desenhado à mão");

    private static readonly L Circle = new("circle", "Kreis", "círculo", "cercle", "円", "círculo");
    private static readonly L Square = new("rounded square", "abgerundetes Quadrat", "cuadrado redondeado", "carré arrondi", "角丸四角", "quadrado arredondado");
    private static readonly L TagShape = new("tag", "Etikett", "etiqueta", "étiquette", "タグ", "etiqueta");
    private static readonly L Shield = new("shield", "Schild", "escudo", "bouclier", "盾", "escudo");
    private static readonly L Burst = new("starburst", "Stern", "estallido", "étoile", "スター", "explosão");
    private static readonly L Hexagon = new("hexagon", "Sechseck", "hexágono", "hexagone", "六角形", "hexágono");

    public static IEnumerable<Pack> Build()
    {
        yield return Badges();
        yield return Arrows();
        yield return Marks();
    }

    // ------------------------------------------------------------------ badges

    private sealed record Shape(string Key, L Name, Func<SKPath> Make, SKRect TextBox);

    private static readonly Shape[] Shapes =
    {
        new("circle", Circle, () => Geo.Circle(64, 61, 52), new SKRect(24, 21, 104, 101)),
        new("square", Square, () => Geo.RRect(12, 9, 104, 104, 26), new SKRect(22, 21, 106, 101)),
        new("tag", TagShape, () => Geo.RoundPoly(new List<(double, double)> { (8, 14), (88, 14), (122, 61), (88, 108), (8, 108) }, 12), new SKRect(18, 21, 92, 101)),
        new("shield", Shield, () =>
        {
            var p = new SKPath();
            p.MoveTo(64, 6); p.CubicTo(80, 16, 98, 20, 114, 20); p.CubicTo(114, 70, 100, 98, 64, 118); p.CubicTo(28, 98, 14, 70, 14, 20); p.CubicTo(30, 20, 48, 16, 64, 6); p.Close();
            return p;
        }, new SKRect(28, 22, 100, 92)),
        new("burst", Burst, () => Geo.RoundPoly(Geo.StarPts(64, 61, 12, 60, 47), 5), new SKRect(30, 25, 98, 97)),
        new("hexagon", Hexagon, () => Geo.RoundPoly(Geo.RegularPts(64, 61, 6, 58, -90), 12), new SKRect(24, 23, 104, 99)),
    };

    private static readonly (string Shape, Hue Hue)[] BadgeVariants =
    {
        ("circle", Pal.Red), ("circle", Pal.Blue), ("circle", Pal.White), ("circle", Pal.Charcoal),
        ("square", Pal.Purple), ("tag", Pal.Orange), ("shield", Pal.Navy), ("burst", Pal.Yellow), ("hexagon", Pal.Teal),
    };

    private static string Badge(Shape shape, Hue hue, string label)
    {
        var s = new SvgDoc(128, 128);
        using var path = shape.Make();
        Art.Glossy(s, path, hue);
        var box = shape.TextBox;
        var size = label.Length == 1 ? 70 : 60;
        using var text = Txt.Fit(label, "fira-bold", size, box.Left, box.Top, box.Width, box.Height, tracking: -0.02);
        Art.Ink(s, text, hue.On, hue.On == "#FFFFFF" ? 0.3 : 0);
        return s.ToString();
    }

    private static Pack Badges()
    {
        var pack = new Pack("badges", 20, new L("Numbers and badges", "Zahlen und Abzeichen", "Números e insignias", "Chiffres et badges", "数字とバッジ", "Números e emblemas"));
        foreach (var (shapeKey, hue) in BadgeVariants)
        {
            var shape = Shapes.First(x => x.Key == shapeKey);
            var color = T.Color[hue.Key];
            for (var n = 0; n <= 30; n++)
            {
                var label = n.ToString();
                pack.Add($"{shapeKey}-{hue.Key}-{n:00}", Badge(shape, hue, label), T.Paren(Number.F(L.Same(label)), Pair.F(shape.Name, color)),
                    L.Same(label), NumberKw, StepKw, BadgeKw, shape.Name, color);
            }
        }
        foreach (var hue in new[] { Pal.Blue, Pal.Charcoal })
        {
            var color = T.Color[hue.Key];
            for (var c = 'A'; c <= 'Z'; c++)
            {
                var label = c.ToString();
                pack.Add($"letter-{hue.Key}-{char.ToLowerInvariant(c)}", Badge(Shapes[0], hue, label), T.Paren(LetterName.F(L.Same(label)), Pair.F(Circle, color)),
                    L.Same(label), LetterKw, BadgeKw, Circle, color);
            }
        }
        return pack;
    }

    // ------------------------------------------------------------------ arrows

    private sealed record ArrowKind(string Key, L Name, Func<SKPath> Make, bool Hand = false);

    private static List<(double X, double Y)> UTurn() =>
        Art.Line(34, 114, 34, 56).Concat(Art.Arc(64, 56, 30, 30, 180, 360).Skip(1)).Concat(Art.Line(94, 56, 94, 112).Skip(1)).ToList();

    private static readonly ArrowKind[] ArrowKinds =
    {
        new("straight", new("Arrow right", "Pfeil nach rechts", "Flecha a la derecha", "Flèche droite", "右矢印", "Seta para a direita"),
            () => Art.Arrow(Art.Line(12, 64, 116, 64), 20, 44, 58)),
        new("diagonal", new("Diagonal arrow", "Diagonaler Pfeil", "Flecha diagonal", "Flèche diagonale", "斜め矢印", "Seta diagonal"),
            () => Art.Arrow(Art.Line(20, 108, 110, 18), 20, 44, 58)),
        new("curved", new("Curved arrow", "Gebogener Pfeil", "Flecha curva", "Flèche courbe", "曲がった矢印", "Seta curva"),
            () => Art.Arrow(Geo.Bezier((12, 104), (44, 8), (116, 60)), 18, 40, 54)),
        new("u-turn", new("U-turn arrow", "Wendepfeil", "Flecha de cambio de sentido", "Flèche demi-tour", "Uターン矢印", "Seta de retorno"),
            () => Art.Arrow(UTurn(), 18, 38, 52)),
        new("double", new("Double arrow", "Doppelpfeil", "Flecha doble", "Double flèche", "両方向矢印", "Seta dupla"),
            () => Art.Arrow(Art.Line(8, 64, 120, 64), 18, 38, 54, startHead: true)),
        new("circular", new("Circular arrow", "Kreispfeil", "Flecha circular", "Flèche circulaire", "円形矢印", "Seta circular"),
            () => Art.Arrow(Art.Arc(64, 66, 42, 42, -50, 235), 16, 36, 48)),
        new("elbow", new("Elbow arrow", "Winkelpfeil", "Flecha en codo", "Flèche coudée", "折れ矢印", "Seta em cotovelo"),
            () => Art.Arrow(Art.Densify(new List<(double, double)> { (16, 30), (70, 30), (86, 34), (90, 50), (90, 116) }, 2), 18, 38, 52)),
        new("block", new("Block arrow", "Blockpfeil", "Flecha gruesa", "Flèche épaisse", "太い矢印", "Seta grossa"),
            () => Art.Arrow(Art.Line(10, 64, 118, 64), 38, 58, 96)),
        new("hand", new("Hand-drawn arrow", "Handgezeichneter Pfeil", "Flecha dibujada a mano", "Flèche dessinée", "手書きの矢印", "Seta desenhada à mão"),
            () => HandArrow(Art.Wobble(Art.Line(14, 70, 112, 58, 40), 2.2, 11), 7), true),
        new("hand-curved", new("Hand-drawn curved arrow", "Handgezeichneter Bogenpfeil", "Flecha curva a mano", "Flèche courbe dessinée", "手書きの曲がった矢印", "Seta curva à mão"),
            () => HandArrow(Art.Wobble(Geo.Bezier((14, 110), (20, 20), (112, 34)), 1.6, 12), 31), true),
    };

    /// <summary>Marker-pen arrow: wobbly shaft + two head strokes.</summary>
    public static SKPath HandArrow(List<(double X, double Y)> line, int seed, double width = 9)
    {
        var dense = Art.Densify(line, 2);
        using var shaft = Geo.Marker(dense, width, seed);
        var tip = dense[^1];
        var back = dense[Math.Max(0, dense.Count - 8)];
        var a = Math.Atan2(tip.Y - back.Y, tip.X - back.X);
        var parts = new List<SKPath> { new SKPath(shaft) };
        foreach (var side in new[] { -1, 1 })
        {
            var ang = a + Math.PI + side * 0.52;
            var end = (tip.X + Math.Cos(ang) * 30, tip.Y + Math.Sin(ang) * 30);
            var mid = ((tip.X + end.Item1) / 2 + side * 1.5, (tip.Y + end.Item2) / 2 + 1.0);
            parts.Add(Geo.Marker(Art.Densify(new List<(double, double)> { tip, mid, end }, 2), width * 0.95, seed + side * 7, taper: 0.5));
        }
        var res = Geo.UnionAll(parts);
        foreach (var p in parts) p.Dispose();
        return res;
    }

    private static Pack Arrows()
    {
        var pack = new Pack("arrows", 21, new L("Arrows", "Pfeile", "Flechas", "Flèches", "矢印", "Setas"));
        foreach (var kind in ArrowKinds)
            foreach (var hue in Pal.Six)
            {
                var s = new SvgDoc(128, 128);
                using var p = kind.Make();
                if (kind.Hand)
                {
                    Art.Ink(s, p, s.V(p.TightBounds.Top, p.TightBounds.Bottom, new Stop(0, hue.Mid), new Stop(1, hue.Lo)), 0.18, 2.5);
                }
                else Art.Glossy(s, p, hue, edge: 0.035);
                var color = T.Color[hue.Key];
                pack.Add($"{kind.Key}-{hue.Key}", s.ToString(), T.Paren(kind.Name, color), ArrowKw, color, kind.Hand ? HandKw : L.Same("arrow"));
            }
        return pack;
    }

    // ------------------------------------------------------------------ marks

    private static readonly (string Key, string Color, L Name)[] Inks =
    {
        ("red", "#E03131", T.Color["red"]), ("green", "#2F9E44", T.Color["green"]), ("blue", "#1C6FD6", T.Color["blue"]), ("black", "#24292F", T.Color["black"]),
    };

    private static SKPath CheckShape(double w = 17) { using var c = Geo.Open(new List<(double, double)> { (32, 66), (55, 88), (97, 40) }); return Geo.Stroke(c, w); }

    private static SKPath CrossShape(double w = 17)
    {
        using var a = Geo.Open(new List<(double, double)> { (40, 37), (88, 85) });
        using var b = Geo.Open(new List<(double, double)> { (88, 37), (40, 85) });
        using var sa = Geo.Stroke(a, w); using var sb = Geo.Stroke(b, w);
        return Geo.Union(sa, sb);
    }

    private static string RoundMark(Hue hue, Func<SKPath> glyph, Func<SKPath>? outer = null, string? glyphColor = null)
    {
        var s = new SvgDoc(128, 128);
        using var shape = outer?.Invoke() ?? Geo.Circle(64, 61, 52);
        Art.Glossy(s, shape, hue);
        using var g = glyph();
        Art.Ink(s, g, glyphColor ?? hue.On, 0.25);
        return s.ToString();
    }

    private static SKPath Glyph(string text, string font, double size, double x, double y, double w, double h) => Txt.Fit(text, font, size, x, y, w, h, tight: true);

    private static Pack Marks()
    {
        var pack = new Pack("marks", 22, new L("Marks and symbols", "Zeichen und Symbole", "Marcas y símbolos", "Marques et symboles", "記号とマーク", "Marcas e símbolos"));
        L check = new("check mark", "Häkchen", "marca de verificación", "coche", "チェックマーク", "marca de seleção");
        L cross = new("cross mark", "Kreuz", "marca de cruz", "croix", "バツ印", "marca de x");
        L question = new("question mark", "Fragezeichen", "signo de interrogación", "point d’interrogation", "疑問符", "ponto de interrogação");
        L excl = new("exclamation mark", "Ausrufezeichen", "signo de exclamación", "point d’exclamation", "感嘆符", "ponto de exclamação");
        L info = new("information", "Information", "información", "information", "情報", "informação");
        L plus = new("plus", "Plus", "más", "plus", "プラス", "mais");
        L minus = new("minus", "Minus", "menos", "moins", "マイナス", "menos");
        L warn = new("warning", "Warnung", "advertencia", "avertissement", "警告", "aviso");
        L stop = new("stop", "Stopp", "alto", "stop", "停止", "pare");
        L prohibited = new("prohibited", "verboten", "prohibido", "interdit", "禁止", "proibido");
        L yes = new("yes", "ja", "sí", "oui", "はい", "sim");
        L no = new("no", "nein", "no", "non", "いいえ", "não");

        pack.Add("check-circle", RoundMark(Pal.Green, () => CheckShape()), check, yes, L.Same("ok"));
        pack.Add("cross-circle", RoundMark(Pal.Red, () => CrossShape()), cross, no, L.Same("x"));
        pack.Add("question-circle", RoundMark(Pal.Blue, () => Glyph("?", "fira-bold", 80, 34, 26, 60, 70)), question, L.Same("?"));
        pack.Add("exclamation-circle", RoundMark(Pal.Orange, () => Glyph("!", "fira-bold", 80, 34, 26, 60, 70)), excl, L.Same("!"));
        pack.Add("info-circle", RoundMark(Pal.Blue, () => Glyph("i", "fira-bold", 80, 34, 26, 60, 70)), info, L.Same("i"));
        pack.Add("plus-circle", RoundMark(Pal.Green, () => { using var a = Geo.RRect(32, 53, 64, 16, 8); using var b = Geo.RRect(56, 29, 16, 64, 8); return Geo.Union(a, b); }), plus, L.Same("+"));
        pack.Add("minus-circle", RoundMark(Pal.Red, () => Geo.RRect(32, 53, 64, 16, 8)), minus, L.Same("-"));
        pack.Add("warning-triangle", RoundMark(Pal.Yellow, () => Glyph("!", "fira-bold", 70, 44, 40, 40, 56),
            () => Geo.RoundPoly(new List<(double, double)> { (64, 8), (122, 110), (6, 110) }, 14)), warn, excl);
        pack.Add("stop-octagon", RoundMark(Pal.Red, () => { using var a = Geo.RRect(30, 52, 68, 18, 4); return new SKPath(a); },
            () => Geo.RoundPoly(Geo.RegularPts(64, 61, 8, 56, -112.5), 8)), stop);
        pack.Add("prohibited", RoundMark(Pal.Red, () => new SKPath(), () =>
        {
            using var o = Geo.Circle(64, 61, 54); using var i = Geo.Circle(64, 61, 38);
            using var ring = Geo.Diff(o, i);
            using var bar = Geo.Rotate(Geo.Rect(22, 53, 84, 16), -45, 64, 61);
            return Geo.Union(ring, bar);
        }), prohibited, no);

        // big glossy glyphs
        pack.Add("check-glossy", GlossyGlyph(CheckShape(26), Pal.Green), check, yes);
        pack.Add("cross-glossy", GlossyGlyph(CrossShape(26), Pal.Red), cross, no);
        pack.Add("question-glossy", GlossyGlyph(Glyph("?", "fira-bold", 120, 24, 8, 80, 108), Pal.Blue), question, L.Same("?"));
        pack.Add("exclamation-glossy", GlossyGlyph(Glyph("!", "fira-bold", 120, 34, 8, 60, 108), Pal.Orange), excl, L.Same("!"));

        // hand-drawn marks in four inks
        foreach (var (key, color, cname) in Inks)
        {
            pack.Add($"hand-check-{key}", Hand(color, s => HandCheck(17)), T.Paren(check, cname), HandKw, yes, cname);
            pack.Add($"hand-cross-{key}", Hand(color, s => HandCross(23)), T.Paren(cross, cname), HandKw, no, cname);
            pack.Add($"hand-question-{key}", Hand(color, s => Glyph("?", "marker", 120, 24, 8, 80, 112)), T.Paren(question, cname), HandKw, cname, L.Same("?"));
            pack.Add($"hand-exclamation-{key}", Hand(color, s => Glyph("!", "marker", 120, 34, 8, 60, 112)), T.Paren(excl, cname), HandKw, cname, L.Same("!"));
        }
        L checkbox = new("ticked box", "angekreuztes Kästchen", "casilla marcada", "case cochée", "チェックボックス", "caixa marcada");
        foreach (var (key, color, cname) in Inks.Where(i => i.Key is "black" or "red"))
            pack.Add($"hand-checkbox-{key}", Hand(color, s => HandCheckbox(5)), T.Paren(checkbox, cname), HandKw, check, cname);
        L circled = new("Circled {0}", "Eingekreiste {0}", "{0} en círculo", "{0} entouré", "丸囲み {0}", "{0} circulado");
        for (var n = 1; n <= 9; n++)
        {
            var label = n.ToString();
            pack.Add($"hand-circled-{n}", Hand("#E03131", s =>
            {
                using var ring = Geo.Marker(Art.Densify(Art.Wobble(Art.Arc(64, 64, 52, 50, -100, 262, 90), 1.2, n), 2), 6.5, n * 13);
                using var g = Glyph(label, "marker", 76, 38, 26, 52, 76);
                return Geo.Union(ring, g);
            }), circled.F(L.Same(label)), HandKw, NumberKw, L.Same(label), T.Color["red"]);
        }

        // brackets, quotes, math — glossy glyphs in red and charcoal
        var glyphs = new (string Ch, string Key, L Name)[]
        {
            ("(", "paren-left", new("Left parenthesis", "Runde Klammer links", "Paréntesis izquierdo", "Parenthèse gauche", "左丸括弧", "Parêntese esquerdo")),
            (")", "paren-right", new("Right parenthesis", "Runde Klammer rechts", "Paréntesis derecho", "Parenthèse droite", "右丸括弧", "Parêntese direito")),
            ("[", "bracket-left", new("Left bracket", "Eckige Klammer links", "Corchete izquierdo", "Crochet gauche", "左角括弧", "Colchete esquerdo")),
            ("]", "bracket-right", new("Right bracket", "Eckige Klammer rechts", "Corchete derecho", "Crochet droit", "右角括弧", "Colchete direito")),
            ("{", "brace-left", new("Left brace", "Geschweifte Klammer links", "Llave izquierda", "Accolade gauche", "左波括弧", "Chave esquerda")),
            ("}", "brace-right", new("Right brace", "Geschweifte Klammer rechts", "Llave derecha", "Accolade droite", "右波括弧", "Chave direita")),
            ("“", "quote-open", new("Opening quote", "Anführungszeichen öffnend", "Comillas de apertura", "Guillemet ouvrant", "開き引用符", "Aspas de abertura")),
            ("”", "quote-close", new("Closing quote", "Anführungszeichen schließend", "Comillas de cierre", "Guillemet fermant", "閉じ引用符", "Aspas de fechamento")),
            ("«", "guillemet-left", new("Left guillemet", "Guillemet links", "Comillas angulares izquierdas", "Guillemet gauche", "左ギュメ", "Aspas angulares esquerdas")),
            ("»", "guillemet-right", new("Right guillemet", "Guillemet rechts", "Comillas angulares derechas", "Guillemet droit", "右ギュメ", "Aspas angulares direitas")),
            ("&", "ampersand", new("Ampersand", "Und-Zeichen", "Et", "Esperluette", "アンパサンド", "E comercial")),
            ("#", "hash", new("Hash", "Raute", "Almohadilla", "Croisillon", "シャープ", "Cerquilha")),
            ("@", "at", new("At sign", "At-Zeichen", "Arroba", "Arobase", "アットマーク", "Arroba")),
            ("*", "asterisk", new("Asterisk", "Sternchen", "Asterisco", "Astérisque", "アスタリスク", "Asterisco")),
            ("+", "plus", new("Plus", "Plus", "Más", "Plus", "プラス", "Mais")),
            ("−", "minus", new("Minus", "Minus", "Menos", "Moins", "マイナス", "Menos")),
            ("×", "multiply", new("Multiply", "Mal", "Multiplicar", "Multiplier", "掛ける", "Multiplicar")),
            ("÷", "divide", new("Divide", "Geteilt", "Dividir", "Diviser", "割る", "Dividir")),
            ("=", "equals", new("Equals", "Gleich", "Igual", "Égal", "等号", "Igual")),
            ("≠", "not-equal", new("Not equal", "Ungleich", "No igual", "Différent", "等しくない", "Diferente")),
            ("≈", "approx", new("Approximately", "Ungefähr", "Aproximadamente", "Environ", "ほぼ等しい", "Aproximadamente")),
            ("∑", "sum", new("Sum", "Summe", "Suma", "Somme", "総和", "Soma")),
            ("π", "pi", new("Pi", "Pi", "Pi", "Pi", "パイ", "Pi")),
            ("√", "root", new("Square root", "Wurzel", "Raíz cuadrada", "Racine carrée", "平方根", "Raiz quadrada")),
            ("%", "percent", new("Percent", "Prozent", "Por ciento", "Pour cent", "パーセント", "Por cento")),
            ("∞", "infinity", new("Infinity", "Unendlich", "Infinito", "Infini", "無限大", "Infinito")),
            ("±", "plus-minus", new("Plus-minus", "Plusminus", "Más menos", "Plus ou moins", "プラスマイナス", "Mais ou menos")),
            ("<", "less", new("Less than", "Kleiner als", "Menor que", "Inférieur à", "小なり", "Menor que")),
            (">", "greater", new("Greater than", "Größer als", "Mayor que", "Supérieur à", "大なり", "Maior que")),
        };
        L symbolKw = new("symbol", "Symbol", "símbolo", "symbole", "記号", "símbolo");
        foreach (var (ch, key, name) in glyphs)
        {
            var font = Txt.Has("fira-bold", ch) ? "fira-bold" : throw new InvalidOperationException($"glyph {ch} missing in Fira Sans Bold");
            foreach (var hue in new[] { Pal.Red, Pal.Charcoal })
            {
                using var g = Glyph(ch, font, 130, 14, 10, 100, 104);
                var color = T.Color[hue.Key];
                pack.Add($"{key}-{hue.Key}", GlossyGlyph(new SKPath(g), hue, 0.02), T.Paren(name, color), symbolKw, L.Same(ch), color);
            }
        }

        // highlighting doodles
        L highlight = new("highlight", "Hervorhebung", "resaltar", "surligner", "強調", "destaque");
        var doodles = new (string Key, L Name, double W, double H, Func<int, SKPath> Make)[]
        {
            ("circle", new("Hand-drawn circle", "Handgezeichneter Kreis", "Círculo a mano", "Cercle dessiné", "手書きの丸", "Círculo à mão"), 128, 128,
                seed => Geo.Marker(Art.Densify(Art.Wobble(Art.Arc(64, 64, 54, 50, -110, 268, 100), 1.5, seed), 2), 7, seed)),
            ("oval", new("Hand-drawn oval", "Handgezeichnetes Oval", "Óvalo a mano", "Ovale dessiné", "手書きの楕円", "Oval à mão"), 224, 112,
                seed => Geo.Marker(Art.Densify(Art.Wobble(Art.Arc(112, 56, 102, 44, -120, 262, 120), 1.8, seed), 2), 7, seed)),
            ("box", new("Hand-drawn box", "Handgezeichneter Rahmen", "Recuadro a mano", "Cadre dessiné", "手書きの四角", "Caixa à mão"), 224, 128,
                seed => Geo.Marker(Art.Densify(Art.Wobble(new List<(double, double)> { (14, 16), (206, 12), (212, 112), (18, 116), (10, 22), (40, 18) }, 1.5, seed), 2), 6.5, seed, taper: 0.2)),
            ("underline", new("Hand-drawn underline", "Handgezeichnete Unterstreichung", "Subrayado a mano", "Soulignement dessiné", "手書きの下線", "Sublinhado à mão"), 224, 48,
                seed => Geo.Marker(Art.Densify(Geo.Bezier((10, 30), (110, 18), (214, 26)), 2), 9, seed)),
            ("wavy", new("Wavy underline", "Wellenlinie", "Subrayado ondulado", "Soulignement ondulé", "波線", "Sublinhado ondulado"), 224, 48,
                seed => Geo.Marker(Enumerable.Range(0, 101).Select(i => (12 + i * 2.0, 24 + 9 * Math.Sin(i * 0.3))).ToList(), 7, seed, taper: 0.3)),
            ("double-underline", new("Double underline", "Doppelte Unterstreichung", "Doble subrayado", "Double soulignement", "二重下線", "Sublinhado duplo"), 224, 56,
                seed =>
                {
                    using var a = Geo.Marker(Art.Densify(Geo.Bezier((10, 18), (110, 10), (214, 16)), 2), 7, seed);
                    using var b = Geo.Marker(Art.Densify(Geo.Bezier((22, 40), (120, 34), (204, 40)), 2), 7, seed + 1);
                    return Geo.Union(a, b);
                }),
            ("strike", new("Strike-through", "Durchgestrichen", "Tachado", "Barré", "取り消し線", "Riscado"), 224, 48,
                seed => Geo.Marker(Art.Densify(Geo.Bezier((8, 28), (110, 20), (216, 18)), 2), 8, seed)),
            ("scribble", new("Scribble", "Gekritzel", "Garabato", "Gribouillis", "なぐり書き", "Rabisco"), 192, 96,
                seed => Geo.Marker(Art.Densify(Enumerable.Range(0, 13).Select(i => (16 + i * 13.3, i % 2 == 0 ? 82 - i * 1.5 : 14 + i * 1.2)).ToList(), 2), 6, seed, taper: 0.2)),
            ("zigzag", new("Zigzag", "Zickzack", "Zigzag", "Zigzag", "ジグザグ", "Ziguezague"), 224, 56,
                seed => Geo.Marker(Art.Densify(Enumerable.Range(0, 11).Select(i => (12 + i * 20.0, i % 2 == 0 ? 40.0 : 14.0)).ToList(), 2), 6.5, seed, taper: 0.2)),
            ("star", new("Hand-drawn star", "Handgezeichneter Stern", "Estrella a mano", "Étoile dessinée", "手書きの星", "Estrela à mão"), 128, 128,
                seed =>
                {
                    var pts = Geo.RegularPts(64, 68, 5, 54, -90);
                    var order = new[] { 0, 2, 4, 1, 3, 0 }.Select(i => pts[i]).ToList();
                    order.Add((pts[0].X + 6, pts[0].Y + 10));
                    return Geo.Marker(Art.Densify(Art.Wobble(order, 1.2, seed), 2), 6.5, seed, taper: 0.2);
                }),
        };
        var doodleInks = new (string Key, string Color, L Name)[] { Inks[0], ("yellow", "#F2B705", T.Color["yellow"]), Inks[2], Inks[3] };
        var di = 0;
        foreach (var (key, name, w, h, make) in doodles)
            foreach (var (ink, color, cname) in doodleInks)
            {
                var s = new SvgDoc(w, h);
                using var p = make(100 + di++);
                HandInk(s, p, color);
                pack.Add($"doodle-{key}-{ink}", s.ToString(), T.Paren(name, cname), HandKw, highlight, cname);
            }
        return pack;
    }

    private static string Shade(string hex)
    {
        var c = SKColor.Parse(hex);
        return $"#{(byte)(c.Red * 0.8):X2}{(byte)(c.Green * 0.8):X2}{(byte)(c.Blue * 0.8):X2}";
    }

    private static string GlossyGlyph(SKPath glyph, Hue hue, double edge = 0.03)
    {
        var s = new SvgDoc(128, 128);
        Art.Glossy(s, glyph, hue, edge: edge, gloss: 0.8);
        glyph.Dispose();
        return s.ToString();
    }

    private static string Hand(string color, Func<SvgDoc, SKPath> make)
    {
        var s = new SvgDoc(128, 128);
        using var p = make(s);
        HandInk(s, p, color);
        return s.ToString();
    }

    /// <summary>Marker ink with a subtle gradient; near-black ink gets a faint light halo so it stays legible on dark images.</summary>
    public static void HandInk(SvgDoc s, SKPath p, string color)
    {
        var c = SKColor.Parse(color);
        if (c.Red + c.Green + c.Blue < 180)
        {
            using var halo = Geo.Inflate(p, 2.2);
            s.Path(halo, "#FFFFFF", 0.5);
        }
        Art.Ink(s, p, s.V(p.TightBounds.Top, p.TightBounds.Bottom, new Stop(0, color), new Stop(1, Shade(color))), 0.15, 2);
    }

    private static SKPath HandCheck(int seed) =>
        Geo.Marker(Art.Densify(Art.Wobble(new List<(double, double)> { (22, 66), (34, 74), (50, 100), (62, 80), (82, 46), (110, 16) }, 1.2, seed), 2), 13, seed, taper: 0.55);

    private static SKPath HandCross(int seed)
    {
        using var a = Geo.Marker(Art.Densify(Art.Wobble(Geo.Bezier((24, 22), (60, 62), (104, 106)), 1.4, seed), 2), 12, seed, taper: 0.5);
        using var b = Geo.Marker(Art.Densify(Art.Wobble(Geo.Bezier((102, 20), (66, 58), (22, 108)), 1.4, seed + 3), 2), 12, seed + 3, taper: 0.5);
        return Geo.Union(a, b);
    }

    private static SKPath HandCheckbox(int seed)
    {
        using var box = Geo.Marker(Art.Densify(Art.Wobble(new List<(double, double)> { (14, 30), (96, 28), (98, 114), (16, 116), (12, 34) }, 1.2, seed), 2), 6, seed, taper: 0.15);
        using var tick = Geo.Marker(Art.Densify(Art.Wobble(new List<(double, double)> { (30, 70), (52, 98), (84, 40), (118, 6) }, 1.0, seed + 9), 2), 9, seed + 9, taper: 0.5);
        return Geo.Union(box, tick);
    }
}
