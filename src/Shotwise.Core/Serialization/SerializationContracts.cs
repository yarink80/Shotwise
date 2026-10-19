using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Serialization;

/// <summary>Native document format (.shotx, ARCHITECTURE §7). Implemented in Core by <see cref="ShotxSerializer"/>.</summary>
public interface IShotxSerializer
{
    /// <summary>Saves atomically (temp file + rename). <paramref name="thumbnail"/> is downscaled to ≤1024 px and stored first, uncompressed.</summary>
    Task SaveAsync(ShotDocument doc, string path, BgraImage thumbnail, CancellationToken ct = default);
    Task<ShotDocument> LoadAsync(string path, CancellationToken ct = default);
    Task SaveAsync(ShotDocument doc, Stream destination, BgraImage thumbnail, CancellationToken ct = default);
    Task<ShotDocument> LoadAsync(Stream source, CancellationToken ct = default);
    /// <summary>Reads only metadata.json (library indexing).</summary>
    Task<DocumentMetadata> ReadMetadataAsync(string path, CancellationToken ct = default);
    /// <summary>Reads the stored thumbnail.png bytes (null if absent).</summary>
    Task<byte[]?> ReadThumbnailAsync(string path, CancellationToken ct = default);
}

/// <summary>Best-effort importer for the legacy third-party ".snagx" format (see <see cref="SnagxImporter"/>).</summary>
public interface ISnagxImporter
{
    bool CanImport(string path);
    Task<ShotDocument> ImportAsync(string path, CancellationToken ct = default);
}
