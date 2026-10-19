using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StampGen;

/// <summary>A string in the six UI cultures (en, de, es, fr, ja, pt-BR).</summary>
internal sealed record L(string En, string De, string Es, string Fr, string Ja, string Pt)
{
    public static readonly string[] Cultures = { "en", "de", "es", "fr", "ja", "pt-BR" };

    public static L Same(string s) => new(s, s, s, s, s, s);

    public string this[int i] => i switch { 0 => En, 1 => De, 2 => Es, 3 => Fr, 4 => Ja, _ => Pt };

    public string this[string culture] => this[Array.IndexOf(Cultures, culture)];

    /// <summary>Per-culture string.Format of this template with localized arguments.</summary>
    public L F(params L[] args)
    {
        string Fmt(int i) => string.Format(this[i], args.Select(a => (object)a[i]).ToArray());
        return new L(Fmt(0), Fmt(1), Fmt(2), Fmt(3), Fmt(4), Fmt(5));
    }

    public static implicit operator L(string s) => Same(s);
}

internal sealed class PackEntry
{
    public required string File { get; init; }
    public required Dictionary<string, string> Name { get; init; }
    public required Dictionary<string, List<string>> Keywords { get; init; }
    public required string Svg { get; init; }
}

/// <summary>
/// One built-in stamp category = one folder under Content/stamps with a <c>packageinfo.json</c> in the schema StampCatalog reads
/// (<c>file</c>/<c>name</c>/<c>keywords</c>; every stamp has keywords in all six cultures, English as fallback).
/// </summary>
internal sealed class Pack
{
    public Pack(string folder, int order, L category)
    {
        Folder = folder; Order = order; Category = category;
    }

    public string Folder { get; }
    public string Id => "shotwise." + Folder;
    public int Order { get; }
    public L Category { get; }
    public List<PackEntry> Entries { get; } = new();
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a stamp. Keywords are localized terms; the category name and the words of the name are added automatically.</summary>
    public void Add(string stem, string svg, L name, params L[] keywords)
    {
        var kw = new Dictionary<string, List<string>>();
        for (var i = 0; i < L.Cultures.Length; i++)
        {
            var list = new List<string>();
            foreach (var k in keywords) AddTerm(list, k[i]);
            AddTerm(list, name[i]);
            AddTerm(list, Category[i]);
            kw[L.Cultures[i]] = list;
        }
        var names = new Dictionary<string, string>();
        for (var i = 0; i < L.Cultures.Length; i++) names[L.Cultures[i]] = name[i];
        AddRaw(stem, svg, names, kw);
    }

    public void AddRaw(string stem, string svg, Dictionary<string, string> names, Dictionary<string, List<string>> keywords)
    {
        var file = stem + ".svg";
        if (!_files.Add(file)) throw new InvalidOperationException($"Duplicate stamp file {Folder}/{file}");
        foreach (var c in L.Cultures)
        {
            if (!keywords.TryGetValue(c, out var l) || l.Count == 0) throw new InvalidOperationException($"{Folder}/{file}: no {c} keywords");
            if (!names.TryGetValue(c, out var n) || string.IsNullOrWhiteSpace(n)) throw new InvalidOperationException($"{Folder}/{file}: no {c} name");
        }
        Entries.Add(new PackEntry { File = file, Name = names, Keywords = keywords, Svg = svg });
    }

    public static void AddTerm(List<string> list, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return;
        var t = term.Trim().ToLowerInvariant();
        if (!list.Contains(t)) list.Add(t);
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    public void Write(string stampsRoot)
    {
        var dir = Path.Combine(stampsRoot, Folder);
        Directory.CreateDirectory(dir);
        foreach (var e in Entries) File.WriteAllText(Path.Combine(dir, e.File), e.Svg, new UTF8Encoding(false));
        var cat = L.Cultures.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => Category[x.i]);
        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": 1,\n  \"assetCategory\": \"stamps\",\n");
        sb.Append("  \"id\": ").Append(JsonSerializer.Serialize(Id, Json)).Append(",\n");
        sb.Append("  \"order\": ").Append(Order).Append(",\n");
        sb.Append("  \"category\": ").Append(JsonSerializer.Serialize(cat, Json)).Append(",\n");
        sb.Append("  \"name\": ").Append(JsonSerializer.Serialize(cat, Json)).Append(",\n");
        sb.Append("  \"stamps\": [\n");
        for (var i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            sb.Append("    ").Append(JsonSerializer.Serialize(new { file = e.File, name = e.Name, keywords = e.Keywords }, Json));
            sb.Append(i + 1 < Entries.Count ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(Path.Combine(dir, "packageinfo.json"), sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>Shared localized vocabulary for our own packs.</summary>
internal static class T
{
    public static readonly L Light = new("light", "hell", "claro", "clair", "ライト", "claro");
    public static readonly L Dark = new("dark", "dunkel", "oscuro", "sombre", "ダーク", "escuro");

    public static readonly Dictionary<string, L> Color = new()
    {
        ["red"] = new("red", "rot", "rojo", "rouge", "赤", "vermelho"),
        ["orange"] = new("orange", "orange", "naranja", "orange", "オレンジ", "laranja"),
        ["yellow"] = new("yellow", "gelb", "amarillo", "jaune", "黄色", "amarelo"),
        ["gold"] = new("gold", "gold", "dorado", "or", "金色", "dourado"),
        ["green"] = new("green", "grün", "verde", "vert", "緑", "verde"),
        ["teal"] = new("teal", "türkis", "turquesa", "turquoise", "ティール", "turquesa"),
        ["blue"] = new("blue", "blau", "azul", "bleu", "青", "azul"),
        ["navy"] = new("navy", "marineblau", "azul marino", "bleu marine", "紺", "azul-marinho"),
        ["purple"] = new("purple", "lila", "morado", "violet", "紫", "roxo"),
        ["pink"] = new("pink", "rosa", "rosa", "rose", "ピンク", "rosa"),
        ["charcoal"] = new("charcoal", "anthrazit", "carbón", "anthracite", "チャコール", "grafite"),
        ["black"] = new("black", "schwarz", "negro", "noir", "黒", "preto"),
        ["white"] = new("white", "weiß", "blanco", "blanc", "白", "branco"),
        ["lavender"] = new("lavender", "lavendel", "lavanda", "lavande", "ラベンダー", "lavanda"),
        ["mint"] = new("mint", "mint", "menta", "menthe", "ミント", "menta"),
        ["sky"] = new("sky blue", "himmelblau", "celeste", "bleu ciel", "空色", "azul-céu"),
    };

    public static L Paren(L a, L b) => new L("{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})", "{0}（{1}）", "{0} ({1})").F(a, b);
}
