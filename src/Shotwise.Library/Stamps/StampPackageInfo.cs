using System.Text.Json;

namespace Shotwise.Library.Stamps;

/// <summary>
/// <c>packageinfo.json</c> schema (version 1) shared by built-in stamp folders and <c>.shotstamps</c> packs.
/// <code>
/// {
///   "version": 1,
///   "assetCategory": "stamps",                       // optional, informational
///   "id": "arrows",                                  // optional; defaults to the folder / file name
///   "order": 10,                                     // optional sort position of the category
///   "category": { "en": "Arrows", "de": "Pfeile" },  // localized category name (a plain string means "en")
///   "stamps": [
///     { "filename": "arrow-right.svg",               // relative to the pack folder, no ".." (required)
///       "name":     { "en": "Arrow right" },         // optional localized display name (default: humanised file name)
///       "keywords": { "en": ["arrow", "right"], "de": ["pfeil", "rechts"] } } // arrays or comma/space separated strings
///   ]
/// }
/// </code>
/// The reader is deliberately tolerant (property names are case-insensitive; string-or-array-or-map values;
/// <c>file</c> is accepted for <c>filename</c> and a top-level <c>name</c> map for <c>category</c>).
/// Writers (user pack import, "Add to My Stamps") always emit the canonical camelCase form above.
/// </summary>
public sealed class StampPackageInfo
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? AssetCategory { get; set; } = "stamps";
    public string? Id { get; set; }
    public int? Order { get; set; }
    public Dictionary<string, string> Category { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StampPackageEntry> Stamps { get; set; } = new();

    public static StampPackageInfo Parse(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("packageinfo.json must be an object.");
        var pkg = new StampPackageInfo();
        if (Get(root, "version") is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var ver)) pkg.Version = ver;
        pkg.AssetCategory = Get(root, "assetCategory") is { ValueKind: JsonValueKind.String } ac ? ac.GetString() : null;
        pkg.Id = Get(root, "id") is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;
        if (Get(root, "order") is { ValueKind: JsonValueKind.Number } o && o.TryGetInt32(out var ord)) pkg.Order = ord;
        // "category" is canonical; a top-level "name" map (used by the shipped packs) is accepted as the category name too.
        if ((Get(root, "category") ?? Get(root, "name")) is { } cat) pkg.Category = ReadLocalizedStrings(cat);
        if (Get(root, "stamps") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var entry = new StampPackageEntry
                {
                    // "filename" is canonical; "file" is accepted (shipped packs).
                    Filename = (Get(e, "filename") ?? Get(e, "file")) is { ValueKind: JsonValueKind.String } fn ? fn.GetString() ?? "" : "",
                    Name = Get(e, "name") is { } n ? ReadLocalizedStrings(n) : new(StringComparer.OrdinalIgnoreCase),
                    Keywords = Get(e, "keywords") is { } k ? ReadLocalizedLists(k) : new(StringComparer.OrdinalIgnoreCase),
                };
                pkg.Stamps.Add(entry);
            }
        return pkg;
    }

    private static JsonElement? Get(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static Dictionary<string, string> ReadLocalizedStrings(JsonElement el)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind == JsonValueKind.String) { if (el.GetString() is { Length: > 0 } s) d["en"] = s; }
        else if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s) d[p.Name] = s;
        return d;
    }

    private static Dictionary<string, List<string>> ReadLocalizedLists(JsonElement el)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject()) d[p.Name] = ReadList(p.Value);
        else d["en"] = ReadList(el);
        return d;
    }

    private static List<string> ReadList(JsonElement el)
    {
        var l = new List<string>();
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString() ?? "";
            l.AddRange(s.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (l.Count == 1 && l[0].Contains(' ')) { var parts = l[0].Split(' ', StringSplitOptions.RemoveEmptyEntries); l.Clear(); l.AddRange(parts); }
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var v in el.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) l.Add(s.Trim());
        return l;
    }

    /// <summary>Canonical camelCase model for writing.</summary>
    internal object ToJsonModel() => new
    {
        version = CurrentVersion,
        assetCategory = AssetCategory ?? "stamps",
        id = Id,
        order = Order,
        category = Category,
        stamps = Stamps.Select(s => new { filename = s.Filename, name = s.Name.Count > 0 ? s.Name : null, keywords = s.Keywords.Count > 0 ? s.Keywords : null }),
    };
}

public sealed class StampPackageEntry
{
    public string Filename { get; set; } = "";
    public Dictionary<string, string> Name { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Keywords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
