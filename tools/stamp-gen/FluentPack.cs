using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StampGen;

/// <summary>
/// Microsoft Fluent Emoji (MIT) → seven emoji categories. Uses the Color SVG of every asset (Flat only when Color is missing);
/// skin-tone variants are skipped (the Default rendering is kept). Names and keywords: Fluent metadata (en) + Unicode CLDR
/// annotations (de, es, fr, ja, pt), English fallback. SVGs are re-serialized: numbers rounded, ids shortened, metadata dropped,
/// natural size set to 128 px.
/// </summary>
internal static class FluentPack
{
    private static readonly (string Folder, int Order, L Name, string[] Groups)[] Categories =
    {
        ("emoji-people", 50, new L("Smileys and people", "Smileys und Personen", "Caras y personas", "Smileys et personnes", "顔と人", "Carinhas e pessoas"), new[] { "Smileys & Emotion", "People & Body" }),
        ("emoji-nature", 51, new L("Animals and nature", "Tiere und Natur", "Animales y naturaleza", "Animaux et nature", "動物と自然", "Animais e natureza"), new[] { "Animals & Nature" }),
        ("emoji-food", 52, new L("Food and drink", "Essen und Trinken", "Comida y bebida", "Nourriture et boissons", "食べ物と飲み物", "Comidas e bebidas"), new[] { "Food & Drink" }),
        ("emoji-activities", 53, new L("Activities", "Aktivitäten", "Actividades", "Activités", "アクティビティ", "Atividades"), new[] { "Activities" }),
        ("emoji-travel", 54, new L("Travel and places", "Reisen und Orte", "Viajes y lugares", "Voyages et lieux", "旅行と場所", "Viagens e lugares"), new[] { "Travel & Places" }),
        ("emoji-objects", 55, new L("Objects", "Objekte", "Objetos", "Objets", "物", "Objetos"), new[] { "Objects" }),
        ("emoji-symbols", 56, new L("Emoji symbols and flags", "Emoji-Symbole und Flaggen", "Símbolos y banderas emoji", "Symboles et drapeaux emoji", "絵文字の記号と旗", "Símbolos e bandeiras emoji"), new[] { "Symbols", "Flags" }),
    };

    public static List<Pack> Build(string cache, Action<string> log)
    {
        var assets = Path.Combine(Sources.FluentDir(cache), "assets");
        var cldr = Sources.CldrLocales.Skip(1).ToDictionary(l => l, l => LoadCldr(cache, l));
        var cldrEn = LoadCldr(cache, "en");
        var packs = Categories.Select(c => new Pack(c.Folder, c.Order, c.Name)).ToList();
        int skipped = 0, flat = 0;
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Sources.EmojiTestFile(cache)))
        {
            var m = Regex.Match(line, @"^([0-9A-F ]+?)\s*;");
            if (!m.Success) continue;
            var key = string.Join(" ", m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).Where(x => x != "fe0f"));
            order.TryAdd(key, order.Count);
        }
        var items = new List<(int Sort, string Group, string Dir, JsonElement Meta)>();
        foreach (var dir in Directory.GetDirectories(assets))
        {
            var metaFile = Path.Combine(dir, "metadata.json");
            if (!File.Exists(metaFile)) { skipped++; continue; }
            var meta = JsonDocument.Parse(File.ReadAllBytes(metaFile)).RootElement.Clone();
            var uni = string.Join(" ", (Str(meta, "unicode") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x != "fe0f"));
            var sort = order.TryGetValue(uni, out var s) ? s : int.MaxValue;
            items.Add((sort, meta.GetProperty("group").GetString() ?? "", dir, meta));
        }
        // Stable order: Unicode sort order where known, then folder name.
        foreach (var (_, group, dir, meta) in items.OrderBy(i => i.Sort).ThenBy(i => Path.GetFileName(i.Dir), StringComparer.Ordinal))
        {
            var catIndex = Array.FindIndex(Categories, c => c.Groups.Contains(group));
            if (catIndex < 0) { log($"  ! unknown group '{group}' in {Path.GetFileName(dir)}"); skipped++; continue; }
            var svgFile = Pick(dir, "Color") ?? Pick(dir, "Flat");
            if (svgFile is null) { log($"  ! no SVG for {Path.GetFileName(dir)}"); skipped++; continue; }
            if (!svgFile.Contains("Color")) flat++;

            var cldrName = Str(meta, "cldr") ?? Path.GetFileName(dir);
            var glyph = Str(meta, "glyph") ?? "";
            var stem = Slug(cldrName);
            var pack = packs[catIndex];
            var unique = stem;
            for (var i = 2; pack.Entries.Any(e => e.File.Equals(unique + ".svg", StringComparison.OrdinalIgnoreCase)); i++) unique = stem + "-" + i;

            var names = new Dictionary<string, string>();
            var kws = new Dictionary<string, List<string>>();
            var enName = Str(meta, "tts") ?? cldrName;
            names["en"] = Cap(enName);
            var en = new List<string>();
            Pack.AddTerm(en, enName);
            if (meta.TryGetProperty("keywords", out var k) && k.ValueKind == JsonValueKind.Array)
                foreach (var w in k.EnumerateArray()) Pack.AddTerm(en, w.GetString() ?? "");
            if (Lookup(cldrEn, glyph) is { } enAnn) foreach (var w in enAnn.Keywords) Pack.AddTerm(en, w);
            Pack.AddTerm(en, "emoji");
            Pack.AddTerm(en, pack.Category.En);
            kws["en"] = en;
            for (var i = 1; i < L.Cultures.Length; i++)
            {
                var culture = L.Cultures[i];
                var loc = culture == "pt-BR" ? "pt" : culture;
                var list = new List<string>();
                string name;
                if (Lookup(cldr[loc], glyph) is { } ann)
                {
                    name = Cap(ann.Name ?? enName);
                    Pack.AddTerm(list, ann.Name ?? "");
                    foreach (var w in ann.Keywords) Pack.AddTerm(list, w);
                }
                else name = Cap(enName);
                foreach (var w in en) Pack.AddTerm(list, w); // English fallback keeps every search working
                Pack.AddTerm(list, pack.Category[i]);
                names[culture] = name;
                kws[culture] = list;
            }
            pack.AddRaw(unique, Optimize(File.ReadAllText(svgFile), 128), names, kws);
        }
        log($"  fluent: {packs.Sum(p => p.Entries.Count)} emoji ({flat} flat fallbacks, {skipped} skipped)");
        return packs;
    }

    private static string? Pick(string dir, string style)
    {
        var d = Path.Combine(dir, style);
        if (Directory.Exists(d)) return Directory.GetFiles(d, "*.svg").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        d = Path.Combine(dir, "Default", style);
        if (Directory.Exists(d)) return Directory.GetFiles(d, "*.svg").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];

    public static string Slug(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }
        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }

    private sealed record Ann(string? Name, List<string> Keywords);

    private static Dictionary<string, Ann> LoadCldr(string cache, string loc)
    {
        var map = new Dictionary<string, Ann>(StringComparer.Ordinal);
        foreach (var (kind, root) in new[] { ("ann", "annotations"), ("der", "annotationsDerived") })
        {
            var file = Sources.CldrFile(cache, kind, loc);
            if (!File.Exists(file)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!doc.RootElement.TryGetProperty(root, out var r) || !r.TryGetProperty("annotations", out var a)) continue;
            foreach (var p in a.EnumerateObject())
            {
                var name = p.Value.TryGetProperty("tts", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 ? t[0].GetString() : null;
                var kw = p.Value.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                map.TryAdd(p.Name, new Ann(name, kw));
            }
        }
        return map;
    }

    private static Ann? Lookup(Dictionary<string, Ann> map, string glyph)
    {
        if (glyph.Length == 0) return null;
        if (map.TryGetValue(glyph, out var a)) return a;
        var noVs = glyph.Replace("️", "");
        if (map.TryGetValue(noVs, out a)) return a;
        if (map.TryGetValue(noVs + "️", out a)) return a;
        return null;
    }

    // ------------------------------------------------------------------ SVG optimizer

    private static readonly Regex NumberRx = new(@"-?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    private static readonly HashSet<string> Fine = new() { "transform", "gradientTransform", "patternTransform", "values" };

    /// <summary>Re-serializes an upstream SVG: drops comments/metadata, rounds numbers (2 decimals; 3 significant digits for
    /// small transform/colour-matrix coefficients), compacts path data, shortens ids and sets the natural size.</summary>
    public static string Optimize(string svg, double size)
    {
        var doc = XDocument.Parse(svg, LoadOptions.None);
        var root = doc.Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        foreach (var c in root.DescendantNodes().OfType<XComment>().ToList()) c.Remove();
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName is "metadata" or "title" or "desc").ToList()) e.Remove();

        // Short ids.
        var ids = root.DescendantsAndSelf().Select(e => e.Attribute("id")?.Value).Where(v => v is not null).Distinct().ToList();
        var map = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count; i++) map[ids[i]!] = ShortId(i);
        foreach (var e in root.DescendantsAndSelf())
            foreach (var a in e.Attributes().ToList())
            {
                var v = a.Value;
                if (a.Name.LocalName == "id" && map.TryGetValue(v, out var nid)) { a.Value = nid; continue; }
                if (v.Contains("url(#")) v = Regex.Replace(v, @"url\(\s*#([^)\s]+)\s*\)", m => map.TryGetValue(m.Groups[1].Value, out var n) ? $"url(#{n})" : m.Value);
                if (a.Name.LocalName == "href" && v.StartsWith('#') && map.TryGetValue(v[1..], out var h)) v = "#" + h;
                if (a.Name.LocalName is "id" or "href" or "in" or "in2" or "result" or "fill" or "stroke" or "flood-color" or "stop-color" or "color-interpolation-filters" or "filterUnits" or "gradientUnits" or "maskUnits" or "fill-rule" or "clip-rule" or "xmlns")
                { a.Value = v; continue; }
                var fine = Fine.Contains(a.Name.LocalName);
                v = NumberRx.Replace(v, m => Round(m.Value, fine));
                if (a.Name.LocalName == "d") v = CompactPath(v);
                a.Value = v;
            }
        root.SetAttributeValue("width", Num.F(size));
        root.SetAttributeValue("height", Num.F(size));
        var sb = new StringBuilder();
        using (var w = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, NewLineHandling = System.Xml.NewLineHandling.None }))
            root.Save(w);
        return sb.ToString();
    }

    private static string ShortId(int n)
    {
        const string a = "abcdefghijklmnopqrstuvwxyz";
        var s = "";
        do { s = a[n % 26] + s; n /= 26; } while (n > 0);
        return s;
    }

    private static string Round(string token, bool fine)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return token;
        if (fine && Math.Abs(v) < 10 && v != 0)
        {
            // keep three significant digits for small coefficients (rotation/scale matrices, colour matrices)
            var digits = Math.Max(2, 2 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
            var r = Math.Round(v, Math.Min(digits, 6), MidpointRounding.AwayFromZero);
            return r == 0 ? "0" : r.ToString("0.######", CultureInfo.InvariantCulture);
        }
        return Num.F(v);
    }

    private static string CompactPath(string d)
    {
        d = Regex.Replace(d, @"\s+", " ").Trim();
        d = Regex.Replace(d, @"\s*([MmLlHhVvCcSsQqTtAaZz])\s*", "$1");
        d = Regex.Replace(d, @" -", "-");
        d = Regex.Replace(d, @",", " ");
        d = Regex.Replace(d, @" -", "-");
        return d;
    }
}
