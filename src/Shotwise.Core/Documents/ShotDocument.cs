using System.Text.Json;
using System.Text.Json.Serialization;
using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Core.Documents;

/// <summary>One capture / editable file: pages + metadata + asset store.</summary>
public sealed class ShotDocument
{
    public const int CurrentFormatVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public List<Page> Pages { get; set; } = new();
    public DocumentMetadata Metadata { get; set; } = DocumentMetadata.Create();
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Path of the backing file (.shotx) or null for unsaved captures.</summary>
    [JsonIgnore] public string? FilePath { get; set; }
    [JsonIgnore] public bool IsDirty { get; set; }
    [JsonIgnore] public AssetStore Assets { get; } = new();

    /// <summary>Creates a document with a blank page of the given size and colour.</summary>
    public static ShotDocument Create(Size2 canvasSize, Argb background, DocumentMetadata? metadata = null)
    {
        var doc = new ShotDocument { Metadata = metadata ?? DocumentMetadata.Create() };
        doc.Pages.Add(new Page { CanvasSize = canvasSize, Background = new CanvasBackground(background) });
        return doc;
    }

    /// <summary>Creates a document whose single page uses <paramref name="image"/> as base raster.</summary>
    public static ShotDocument FromImage(BgraImage image, DocumentMetadata? metadata = null, IImageCodec? codec = null)
    {
        var doc = new ShotDocument { Metadata = metadata ?? DocumentMetadata.Create() };
        doc.Assets.Codec = codec;
        var asset = doc.Assets.AddImage(image);
        doc.Pages.Add(new Page
        {
            CanvasSize = new Size2(image.Width, image.Height),
            Dpi = image.DpiX,
            Background = new CanvasBackground(Argb.Transparent, null, new BaseImageRef(asset, Point2.Zero)),
        });
        return doc;
    }

    public Page? FindPage(Guid pageId) => Pages.FirstOrDefault(p => p.Id == pageId);

    /// <summary>Page containing <paramref name="pageId"/>; throws <see cref="ShotwiseException"/> when absent.</summary>
    public Page GetPage(Guid pageId) => FindPage(pageId) ?? throw new ShotwiseException(ErrorCode.InvalidArgument, $"Page {pageId} not found.");

    public (Page Page, Annotation Annotation)? FindAnnotation(Guid annotationId)
    {
        foreach (var p in Pages)
        {
            var a = p.Find(annotationId);
            if (a is not null) return (p, a);
        }
        return null;
    }

    /// <summary>All asset ids referenced from the document graph (backgrounds, image/stamp/drop-zone content, sections).</summary>
    public IEnumerable<AssetId> ReferencedAssets()
    {
        foreach (var p in Pages)
            foreach (var id in p.ReferencedAssets())
                yield return id;
    }

    /// <summary>Concatenated text of all text-bearing annotations (callouts, text boxes) — used for library search.</summary>
    public string CollectAnnotationText()
    {
        var parts = new List<string>();
        foreach (var p in Pages)
            foreach (var a in p.AllAnnotations())
                switch (a)
                {
                    case TextAnnotation t when !t.Text.IsEmpty: parts.Add(t.Text.PlainText); break;
                    case CalloutAnnotation c when !c.Text.IsEmpty: parts.Add(c.Text.PlainText); break;
                }
        return string.Join("\n", parts);
    }
}

/// <summary>Canvas background: colour, optional gradient, optional base raster (asset + offset in canvas pixels).</summary>
public sealed record CanvasBackground(Argb Color, GradientFill? Gradient = null, BaseImageRef? BaseImage = null);

/// <summary>Reference to the page's base raster.</summary>
public sealed record BaseImageRef(AssetId Asset, Point2 Offset);

/// <summary>Next values of the three step sequences on a page.</summary>
public sealed record StepCounters(int Number = 1, int Upper = 1, int Lower = 1)
{
    public int Next(StepSequence s) => s switch { StepSequence.Upper => Upper, StepSequence.Lower => Lower, _ => Number };

    public StepCounters With(StepSequence s, int next) => s switch
    {
        StepSequence.Upper => this with { Upper = next },
        StepSequence.Lower => this with { Lower = next },
        _ => this with { Number = next },
    };

    public StepCounters Advance(StepSequence s) => With(s, Next(s) + 1);
}

public enum SimplifyPaletteKind { ImageColors, Custom, Light, Dark, Grayscale }

/// <summary>Per-page Simplify state.</summary>
public sealed record SimplifySettings
{
    public bool AutoSimplifyEnabled { get; init; }
    /// <summary>Detail level 1..10.</summary>
    public int Detail { get; init; } = 8;
    /// <summary>Opacity of generated shapes 0..1.</summary>
    public double Opacity { get; init; } = 1.0;
    public bool ShowOriginal { get; init; }
    /// <summary>Opacity of the original image while "show original" is on.</summary>
    public double OriginalOpacity { get; init; } = 0.3;
    public bool LockObjects { get; init; } = true;
    public SimplifyPaletteKind Palette { get; init; } = SimplifyPaletteKind.ImageColors;
    public IReadOnlyList<Argb> CustomColors { get; init; } = Array.Empty<Argb>();
    public double LineSpacing { get; init; } = 1.0;
    public double TextHeight { get; init; } = 10;

    public static SimplifySettings Default { get; } = new();
}

/// <summary>Per-page Smart Redact review state.</summary>
public sealed record SmartRedactSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyList<RedactCategory> Categories { get; init; } = Enum.GetValues<RedactCategory>();
    public BlurKind Mode { get; init; } = BlurKind.Pixelate;
    public Argb SolidColor { get; init; } = Argb.Black;

    public static SmartRedactSettings Default { get; } = new();
}

/// <summary>Persisted view state.</summary>
public sealed record ViewState
{
    public double Zoom { get; init; } = 1.0;
    public Point2 Scroll { get; init; }
    public bool FitToWindow { get; init; } = true;
    public bool ShowPixelGrid { get; init; }

    public static ViewState Default { get; } = new();
}

/// <summary>One canvas of a document. Annotation list order is z-order (0 = bottom).</summary>
public sealed class Page : IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Size2 CanvasSize { get; set; }
    public double Dpi { get; set; } = 96;
    public CanvasBackground Background { get; set; } = new(Argb.White);
    public double CornerRadius { get; set; }
    public ShadowStyle? CanvasShadow { get; set; }
    public List<Annotation> Annotations { get; set; } = new();
    public List<TemplateSection> Sections { get; set; } = new();
    public StepCounters StepCounters { get; set; } = new();
    public SimplifySettings Simplify { get; set; } = SimplifySettings.Default;
    public SmartRedactSettings SmartRedact { get; set; } = SmartRedactSettings.Default;
    public ViewState View { get; set; } = ViewState.Default;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public Rect2 CanvasRect => new(0, 0, CanvasSize.Width, CanvasSize.Height);

    public int IndexOf(Guid annotationId) => Annotations.FindIndex(a => a.Id == annotationId);
    public Annotation? Find(Guid annotationId) => Annotations.FirstOrDefault(a => a.Id == annotationId) ?? Sections.SelectMany(s => s.Graphics).FirstOrDefault(a => a.Id == annotationId);

    /// <summary>Top-level annotations followed by section graphics.</summary>
    public IEnumerable<Annotation> AllAnnotations() => Annotations.Concat(Sections.SelectMany(s => s.Graphics));

    public IEnumerable<AssetId> ReferencedAssets()
    {
        if (Background.BaseImage is { } bi && !bi.Asset.IsEmpty) yield return bi.Asset;
        foreach (var a in AllAnnotations())
            switch (a)
            {
                case StampAnnotation s when !s.Asset.IsEmpty: yield return s.Asset; break;
                case ImageAnnotation i when !i.Asset.IsEmpty: yield return i.Asset; break;
                case DropZoneAnnotation d when d.Content is { IsEmpty: false } c: yield return c; break;
            }
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        Annotations ??= new();
        Sections ??= new();
        Annotations.RemoveAll(a => a is null);
        StepCounters ??= new();
        Simplify ??= SimplifySettings.Default;
        SmartRedact ??= SmartRedactSettings.Default;
        View ??= ViewState.Default;
        Background ??= new CanvasBackground(Argb.White);
    }

    /// <summary>Deep copy with the same ids (annotations deep-cloned).</summary>
    public Page DeepClone()
    {
        var p = (Page)MemberwiseClone();
        p.Annotations = Annotations.Select(a => a.DeepClone()).ToList();
        p.Sections = Sections.Select(s => s.DeepClone()).ToList();
        p.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return p;
    }
}

/// <summary>
/// Capture provenance (persisted as metadata.json). All fields optional except <see cref="CaptureDate"/>.
/// </summary>
public sealed record DocumentMetadata(
    DateTimeOffset CaptureDate, string? AppName, string? AppCompany, string? AppVersion,
    string? WindowTitle, string? Url, string? OsVersion, string? LanguageCode,
    string? OcrText, IReadOnlyList<string> Tags, PointI? CursorPosition, string? SourcePresetId)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? Array.Empty<string>();

    /// <summary>Unknown fields preserved on round-trip.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public static DocumentMetadata Create(DateTimeOffset? captureDate = null)
        => new(captureDate ?? DateTimeOffset.Now, null, null, null, null, null, Environment.OSVersion.VersionString,
            System.Globalization.CultureInfo.CurrentUICulture.Name, null, Array.Empty<string>(), null, null);
}
