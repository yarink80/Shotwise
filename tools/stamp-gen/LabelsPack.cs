using SkiaSharp;

namespace StampGen;

/// <summary>Callout labels: speech-bubble labels (stable file names from v1: note.svg, tip.svg, …) and pointed tag labels.</summary>
internal static class LabelsPack
{
    private static readonly (string Word, Hue Hue, L Loc)[] Words =
    {
        ("NOTE", Pal.Blue, new("note", "Notiz", "nota", "note", "メモ", "nota")),
        ("TIP", Pal.Green, new("tip", "Tipp", "consejo", "astuce", "ヒント", "dica")),
        ("START", Pal.Green, new("start", "Start", "inicio", "début", "スタート", "início")),
        ("END", Pal.Red, new("end", "Ende", "fin", "fin", "終了", "fim")),
        ("STEP", Pal.Purple, new("step", "Schritt", "paso", "étape", "ステップ", "passo")),
        ("CHECK", Pal.Teal, new("check", "prüfen", "comprobar", "vérifier", "確認", "verificar")),
        ("DONE", Pal.Green, new("done", "erledigt", "hecho", "terminé", "完了", "concluído")),
        ("FIX", Pal.Orange, new("fix", "beheben", "arreglar", "corriger", "修正", "corrigir")),
        ("HERE", Pal.Red, new("here", "hier", "aquí", "ici", "ここ", "aqui")),
        ("HOW", Pal.Blue, new("how", "wie", "cómo", "comment", "方法", "como")),
        ("IDEA", Pal.Yellow, new("idea", "Idee", "idea", "idée", "アイデア", "ideia")),
        ("LOOK", Pal.Pink, new("look", "schau", "mira", "regarde", "見て", "veja")),
        ("NEW", Pal.Red, new("new", "neu", "nuevo", "nouveau", "新着", "novo")),
        ("NO", Pal.Red, new("no", "nein", "no", "non", "いいえ", "não")),
        ("READY", Pal.Green, new("ready", "bereit", "listo", "prêt", "準備完了", "pronto")),
        ("WAIT", Pal.Orange, new("wait", "warten", "espera", "attendre", "待って", "aguarde")),
        ("WHY", Pal.Purple, new("why", "warum", "por qué", "pourquoi", "なぜ", "por quê")),
        ("YES", Pal.Green, new("yes", "ja", "sí", "oui", "はい", "sim")),
        ("INFO", Pal.Blue, new("info", "Info", "información", "info", "情報", "informação")),
        ("WARNING", Pal.Orange, new("warning", "Warnung", "advertencia", "avertissement", "警告", "aviso")),
        ("IMPORTANT", Pal.Red, new("important", "wichtig", "importante", "important", "重要", "importante")),
        ("BETA", Pal.Purple, new("beta", "Beta", "beta", "bêta", "ベータ", "beta")),
        ("HINT", Pal.Teal, new("hint", "Hinweis", "pista", "indice", "ヒント", "dica")),
        ("TODO", Pal.Charcoal, new("to do", "Aufgabe", "pendiente", "à faire", "やること", "a fazer")),
    };

    private static readonly L Category = new("Callout labels", "Beschriftungen", "Etiquetas", "Étiquettes", "ラベル", "Rótulos");
    private static readonly L Bubble = new("speech bubble", "Sprechblase", "bocadillo", "bulle", "吹き出し", "balão");
    private static readonly L Tag = new("tag", "Etikett", "etiqueta", "étiquette", "タグ", "etiqueta");
    private static readonly L LabelWord = new("label", "Beschriftung", "etiqueta", "étiquette", "ラベル", "rótulo");

    public static Pack Build()
    {
        var pack = new Pack("labels", 10, Category);
        foreach (var (word, hue, loc) in Words)
        {
            var stem = word.ToLowerInvariant();
            var name = new L(Cap(loc.En), Cap(loc.De), Cap(loc.Es), Cap(loc.Fr), loc.Ja, Cap(loc.Pt));
            pack.Add(stem, BubbleSvg(word, hue), name, loc, Bubble, LabelWord, L.Same(stem));
        }
        foreach (var (word, hue, loc) in Words)
        {
            var stem = word.ToLowerInvariant();
            var name = T.Paren(new L(Cap(loc.En), Cap(loc.De), Cap(loc.Es), Cap(loc.Fr), loc.Ja, Cap(loc.Pt)), Tag);
            pack.Add(stem + "-tag", TagSvg(word, hue), name, loc, Tag, LabelWord, L.Same(stem));
        }
        return pack;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string BubbleSvg(string word, Hue hue)
    {
        using var probe = Txt.Raw(word, "fira-bold", 44, 0.02);
        var tw = probe.TightBounds.Width;
        var bodyW = Math.Max(104, tw + 52);
        var W = bodyW + 20;
        const double H = 128;
        var s = new SvgDoc(W, H);
        using var body = Geo.RRect(10, 12, bodyW, 80, 22);
        using var tail = Geo.RoundPoly(new List<(double, double)> { (30, 80), (66, 80), (24, 118) }, 4);
        using var shape = Geo.Union(body, tail);
        Art.Glossy(s, shape, hue);
        using var text = Txt.Fit(word, "fira-bold", 44, 10 + 22, 12 + 6, bodyW - 44, 80 - 12, tracking: 0.02);
        Art.Ink(s, text, hue.On, hue.On == "#FFFFFF" ? 0.25 : 0);
        return s.ToString();
    }

    private static string TagSvg(string word, Hue hue)
    {
        using var probe = Txt.Raw(word, "fira-bold", 40, 0.03);
        var tw = probe.TightBounds.Width;
        var W = Math.Max(150, tw + 96);
        const double H = 96;
        var s = new SvgDoc(W, H);
        var pts = new List<(double, double)> { (10, 48), (40, 16), (W - 10, 16), (W - 10, 80), (40, 80) };
        using var shape0 = Geo.RoundPoly(pts, 10);
        using var hole = Geo.Circle(40, 48, 7.5);
        using var shape = Geo.Diff(shape0, hole);
        Art.Glossy(s, shape, hue);
        // eyelet ring
        using var ringOuter = Geo.Circle(40, 48, 11);
        using var ring = Geo.Diff(ringOuter, hole);
        s.Path(ring, s.V(37, 59, new Stop(0, "#FFFFFF", 0.85), new Stop(1, hue.Edge, 0.8)));
        using var text = Txt.Fit(word, "fira-bold", 40, 58, 16, W - 10 - 58 - 12, 64, tracking: 0.03);
        Art.Ink(s, text, hue.On, hue.On == "#FFFFFF" ? 0.25 : 0);
        return s.ToString();
    }
}
