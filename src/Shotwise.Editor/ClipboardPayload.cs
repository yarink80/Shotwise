using System.Text.Json;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;

namespace Shotwise.Editor;

/// <summary>
/// Editor clipboard content: copied annotations (with the assets they reference) and/or pixels.
/// The view puts <see cref="ToJson"/> on the system clipboard under <see cref="ClipboardFormat"/> plus a flattened
/// <see cref="Image"/> for other applications.
/// </summary>
public sealed class ClipboardPayload
{
    public const string ClipboardFormat = "Shotwise.Annotations.v1";

    public ClipboardPayload(IReadOnlyList<Annotation> annotations, IReadOnlyList<EmbeddedAsset> assets, Rect2 bounds, BgraImage? image = null, string? text = null)
    {
        Annotations = annotations;
        Assets = assets;
        Bounds = bounds;
        Image = image;
        Text = text;
    }

    /// <summary>Deep-cloned annotations (original ids; paste assigns new ids).</summary>
    public IReadOnlyList<Annotation> Annotations { get; }
    public IReadOnlyList<EmbeddedAsset> Assets { get; }
    /// <summary>Union bounds of the copied content in source-canvas coordinates.</summary>
    public Rect2 Bounds { get; }
    /// <summary>Raster content: copied pixels, or a flattened rendering of the annotations for external apps.</summary>
    public BgraImage? Image { get; }
    /// <summary>Plain text of copied text/callout annotations (for external apps).</summary>
    public string? Text { get; }

    public bool HasAnnotations => Annotations.Count > 0;
    public bool IsEmpty => Annotations.Count == 0 && Image is null && string.IsNullOrEmpty(Text);

    public static ClipboardPayload FromImage(BgraImage image) => new(Array.Empty<Annotation>(), Array.Empty<EmbeddedAsset>(), new Rect2(0, 0, image.Width, image.Height), image);

    private sealed record Wire(string Format, List<Annotation> Annotations, List<EmbeddedAsset> Assets, Rect2 Bounds, string? Text);

    /// <summary>Serializes annotations + assets (not the image) for cross-instance paste.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Wire(ClipboardFormat, Annotations.ToList(), Assets.ToList(), Bounds, Text), ShotwiseJson.Compact);

    public static ClipboardPayload? FromJson(string json)
    {
        try
        {
            var w = JsonSerializer.Deserialize<Wire>(json, ShotwiseJson.Compact);
            if (w is null || w.Format != ClipboardFormat) return null;
            return new ClipboardPayload(w.Annotations.Where(a => a is not null).ToList(), w.Assets ?? new(), w.Bounds, null, w.Text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
