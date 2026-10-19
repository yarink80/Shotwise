using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Documents;

public enum SectionType { Header, Body, Footer, Repeatable }

/// <summary>A template section (header/body/footer/repeatable body) with its own graphics (drop zones, text, shapes).</summary>
public sealed class TemplateSection : System.Text.Json.Serialization.IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SectionType Type { get; set; } = SectionType.Body;
    public Rect2 Bounds { get; set; }
    public Argb? Background { get; set; }
    public List<Annotation> Graphics { get; set; } = new();

    public TemplateSection DeepClone()
    {
        var s = (TemplateSection)MemberwiseClone();
        s.Graphics = Graphics.Select(g => g.DeepClone()).ToList();
        return s;
    }

    /// <summary>Copy with fresh section/graphic ids (duplicate section). Drop-zone SectionId is re-pointed.</summary>
    public TemplateSection CloneWithNewIds()
    {
        var s = DeepClone();
        s.Id = Guid.NewGuid();
        foreach (var g in s.Graphics)
        {
            g.Id = Guid.NewGuid();
            if (g is DropZoneAnnotation dz) { dz.SectionId = s.Id; dz.Content = null; }
        }
        return s;
    }

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        Graphics ??= new();
        Graphics.RemoveAll(g => g is null);
    }
}

/// <summary>Binary asset embedded in a portable file (templates, clipboard payloads). Serialized as base64.</summary>
public sealed record EmbeddedAsset(AssetId Id, string MediaType, byte[] Data);

/// <summary>Template definition (<c>.shottemplate</c> JSON). <see cref="Prototype"/> is the page layout; its assets travel in <see cref="Assets"/>.</summary>
public sealed class TemplateDefinition
{
    public const string FileFormat = "shottemplate";

    public string Format { get; set; } = FileFormat;
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Localized names keyed by culture ("de", "fr", …).</summary>
    public Dictionary<string, string> LocalizedNames { get; set; } = new();
    public string? Category { get; set; }
    public List<string> Keywords { get; set; } = new();
    /// <summary>Maximum number of images the template accepts (drop zones incl. repeatable bodies).</summary>
    public int MaxImages { get; set; }
    public Page Prototype { get; set; } = new();
    public List<EmbeddedAsset> Assets { get; set; } = new();
    public bool IsBuiltIn { get; set; }

    public string DisplayName(string uiCulture)
    {
        if (LocalizedNames.TryGetValue(uiCulture, out var n)) return n;
        var two = uiCulture.Split('-')[0];
        return LocalizedNames.TryGetValue(two, out n) ? n : Name;
    }

    /// <summary>Instantiates the template into a new document (fresh ids; assets copied into the document store).</summary>
    public ShotDocument Instantiate(IImageCodec? codec = null)
    {
        var doc = new ShotDocument();
        doc.Assets.Codec = codec;
        foreach (var a in Assets) if (a.Id.IsValid) doc.Assets.AddWithId(a.Id, a.Data, a.MediaType);
        var page = Prototype.DeepClone();
        page.Id = Guid.NewGuid();
        var sectionMap = new Dictionary<Guid, Guid>();
        foreach (var s in page.Sections)
        {
            var nid = Guid.NewGuid();
            sectionMap[s.Id] = nid;
            s.Id = nid;
        }
        foreach (var a in page.AllAnnotations())
        {
            a.Id = Guid.NewGuid();
            if (a is DropZoneAnnotation dz && sectionMap.TryGetValue(dz.SectionId, out var ns)) dz.SectionId = ns;
        }
        doc.Pages.Add(page);
        doc.Metadata = DocumentMetadata.Create() with { SourcePresetId = null };
        return doc;
    }
}
