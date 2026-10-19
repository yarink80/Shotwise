using Shotwise.Core.Documents;
using Shotwise.Core.Library;

namespace Shotwise.Library.Catalog;

/// <summary>
/// JSON export of the user-level catalog state (<c>catalog.json</c> inside a <c>.shotarchive</c> and the mirror at
/// <c>&lt;LibraryRoot&gt;\.shotwise\catalog.json</c>). Paths are relative to the library root with '/' separators.
/// </summary>
public sealed class CatalogFile
{
    public const string FileFormat = "shotcatalog";
    public string Format { get; set; } = FileFormat;
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<CatalogEntry> Items { get; set; } = new();
    public List<CatalogShare> Shares { get; set; } = new();
}

public sealed class CatalogEntry
{
    public Guid Id { get; set; }
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public string? App { get; set; }
    public string? WindowTitle { get; set; }
    public string? Url { get; set; }
    public string? Ocr { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public bool StepCapture { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long? DurationMs { get; set; }
}

public sealed class CatalogShare
{
    public Guid? ItemId { get; set; }
    public DateTimeOffset At { get; set; }
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public string? Link { get; set; }
    public string? Title { get; set; }
}

/// <summary>Manifest at the root of a <c>.shotarchive</c>.</summary>
public sealed class ArchiveManifest
{
    public const string FileFormat = "shotarchive";
    public string Format { get; set; } = FileFormat;
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int FileCount { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>Sidecar for media files that cannot carry metadata themselves (<c>name.mp4.shotmeta</c>).</summary>
internal sealed class MediaSidecar
{
    public DocumentMetadata? Metadata { get; set; }
    public long? DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsStepCapture { get; set; }
}

/// <summary>
/// Optional hook used to create thumbnails for video/GIF files (the Library has no media decoder). The composition
/// root may register an adapter over <c>IVideoService</c>; without one video items simply have no thumbnail.
/// </summary>
public interface IMediaThumbnailSource
{
    Task<Shotwise.Core.Imaging.BgraImage?> GetThumbnailAsync(string path, MediaKind kind, CancellationToken ct = default);
}
