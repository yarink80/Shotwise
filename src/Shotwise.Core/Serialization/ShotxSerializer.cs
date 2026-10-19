using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Serialization;

/// <summary>
/// <c>.shotx</c> v1 reader/writer (ARCHITECTURE §7): ZIP with <c>thumbnail.png</c> first (Stored, ≤1024 px),
/// <c>manifest.json</c>, <c>metadata.json</c>, <c>pages/{pageId}.json</c>, <c>assets/{assetId}.{ext}</c> (PNG Stored).
/// Readers ignore/preserve unknown fields and reject traversal, &gt;2 GB uncompressed and &gt;10 000 entries.
/// </summary>
public sealed class ShotxSerializer : IShotxSerializer
{
    public const string FormatName = "shotx";
    public const int FormatVersion = 1;
    public const string FileExtension = ".shotx";
    public const int ThumbnailMaxSide = 1024;

    private readonly IImageCodec? _codec;
    private readonly ILogger<ShotxSerializer> _log;

    public ShotxSerializer(IImageCodec? codec = null, ILogger<ShotxSerializer>? logger = null)
    {
        _codec = codec;
        _log = logger ?? NullLogger<ShotxSerializer>.Instance;
    }

    // ------------------------------------------------------------------ save

    public async Task SaveAsync(ShotDocument doc, string path, BgraImage thumbnail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var snapshot = CreateSnapshot(doc, thumbnail);
        try
        {
            await AtomicFile.WriteWithAsync(path, (s, c) => Task.Run(() => WriteZip(snapshot, s, c), c), ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) { throw new ShotwiseException(ErrorCode.AccessDenied, $"Cannot write '{path}'.", ex); }
        catch (IOException ex) when (IsDiskFull(ex)) { throw new ShotwiseException(ErrorCode.DiskFull, "Not enough disk space.", ex); }
        doc.FilePath = path;
        doc.IsDirty = false;
    }

    public async Task SaveAsync(ShotDocument doc, Stream destination, BgraImage thumbnail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(destination);
        var snapshot = CreateSnapshot(doc, thumbnail);
        await Task.Run(() => WriteZip(snapshot, destination, ct), ct).ConfigureAwait(false);
    }

    private sealed record Snapshot(byte[] Thumbnail, byte[] Manifest, byte[] Metadata, IReadOnlyList<(string Name, byte[] Json)> Pages, IReadOnlyList<(string Name, byte[] Data, bool Store)> Assets);

    /// <summary>Serializes everything up-front on the calling thread so the document can keep changing while I/O runs.</summary>
    private Snapshot CreateSnapshot(ShotDocument doc, BgraImage thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        var thumb = ImageOps.Downscale(thumbnail, ThumbnailMaxSide);
        var thumbBytes = _codec is not null ? _codec.Encode(thumb, ImageFormat.Png) : PngCodec.Encode(thumb);

        var pages = new List<(string, byte[])>();
        foreach (var p in doc.Pages)
            pages.Add(($"pages/{p.Id:D}.json", JsonSerializer.SerializeToUtf8Bytes(p, ShotwiseJson.Compact)));

        var assets = new List<(string, byte[], bool)>();
        var referenced = doc.ReferencedAssets().Distinct().ToList();
        foreach (var id in referenced)
        {
            if (!id.IsValid) { _log.LogWarning("Skipping asset with invalid id {Id}", id.Value); continue; }
            if (!doc.Assets.TryGet(id, out var data, out var mt))
            {
                _log.LogWarning("Document {Doc} references missing asset {Asset}", doc.Id, id);
                continue;
            }
            var ext = AssetStore.ExtensionForMediaType(mt);
            var store = ext is "png" or "jpg" or "gif" or "webp";
            assets.Add(($"assets/{id.Value}.{ext}", data, store));
        }

        var now = DateTimeOffset.Now;
        var manifest = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["app"] = "Shotwise " + (typeof(ShotxSerializer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
            ["documentId"] = doc.Id.ToString("D"),
            ["pages"] = new JsonArray(pages.Select(p => (JsonNode)JsonValue.Create(p.Item1)!).ToArray()),
            ["created"] = doc.Metadata.CaptureDate.ToString("O"),
            ["modified"] = now.ToString("O"),
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ShotwiseJson.Indented);
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(doc.Metadata, ShotwiseJson.Indented);
        return new Snapshot(thumbBytes, manifestBytes, metadataBytes, pages, assets);
    }

    private static void WriteZip(Snapshot s, Stream destination, CancellationToken ct)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        Write(zip, "thumbnail.png", s.Thumbnail, CompressionLevel.NoCompression);
        Write(zip, "manifest.json", s.Manifest, CompressionLevel.Optimal);
        Write(zip, "metadata.json", s.Metadata, CompressionLevel.Optimal);
        foreach (var (name, json) in s.Pages)
        {
            ct.ThrowIfCancellationRequested();
            Write(zip, name, json, CompressionLevel.Optimal);
        }
        foreach (var (name, data, store) in s.Assets)
        {
            ct.ThrowIfCancellationRequested();
            Write(zip, name, data, store ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        }
    }

    private static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        e.LastWriteTime = DateTimeOffset.Now;
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }

    // ------------------------------------------------------------------ load

    public async Task<ShotDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}") { Detail = path };
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var doc = await LoadAsync(fs, ct).ConfigureAwait(false);
            doc.FilePath = path;
            return doc;
        }
        catch (UnauthorizedAccessException ex) { throw new ShotwiseException(ErrorCode.AccessDenied, $"Cannot read '{path}'.", ex); }
        catch (IOException ex) when (ex is not FileNotFoundException) { throw new ShotwiseException(ErrorCode.FileInUse, $"Cannot read '{path}': {ex.Message}", ex); }
    }

    public Task<ShotDocument> LoadAsync(Stream source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.Run(() => LoadCore(source, ct), ct);
    }

    private ShotDocument LoadCore(Stream source, CancellationToken ct)
    {
        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            ZipSafety.Validate(zip);

            var manifestEntry = ZipSafety.Find(zip, "manifest.json") ?? throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Missing manifest.json — not a .shotx document.");
            using var manifest = JsonDocument.Parse(ZipSafety.ReadAllBytes(manifestEntry, ZipSafety.MaxJsonEntryBytes), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetString(root, "format", out var format) || !string.Equals(format, FormatName, StringComparison.OrdinalIgnoreCase))
                throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Not a .shotx document.");
            var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 1;
            if (version > FormatVersion)
                throw new ShotwiseException(ErrorCode.NewerFormatVersion, $"Document format version {version} is newer than supported ({FormatVersion}).");

            var doc = new ShotDocument { FormatVersion = version };
            doc.Assets.Codec = _codec;
            if (TryGetString(root, "documentId", out var did) && Guid.TryParse(did, out var g)) doc.Id = g;

            var metaEntry = ZipSafety.Find(zip, "metadata.json");
            if (metaEntry is not null)
            {
                try
                {
                    doc.Metadata = JsonSerializer.Deserialize<DocumentMetadata>(ZipSafety.ReadAllBytes(metaEntry, ZipSafety.MaxJsonEntryBytes), ShotwiseJson.Compact) ?? DocumentMetadata.Create();
                }
                catch (JsonException ex)
                {
                    _log.LogWarning(ex, "metadata.json is invalid; using defaults");
                    doc.Metadata = DocumentMetadata.Create();
                }
            }

            // pages: manifest order, falling back to entry enumeration
            var pageNames = new List<string>();
            if (root.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
                foreach (var p in pagesEl.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { } pn) pageNames.Add(pn);
            if (pageNames.Count == 0)
                pageNames.AddRange(zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("pages/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order());

            if (pageNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pageNames.Count)
                throw new ShotwiseException(ErrorCode.CorruptFile, "Duplicate page reference.");
            foreach (var name in pageNames)
            {
                ct.ThrowIfCancellationRequested();
                if (!ZipSafety.IsSafeEntryName(name) || !name.StartsWith("pages/", StringComparison.OrdinalIgnoreCase))
                    throw new ShotwiseException(ErrorCode.PathTraversal, $"Invalid page reference '{name}'.") { Detail = name };
                var entry = ZipSafety.Find(zip, name) ?? throw new ShotwiseException(ErrorCode.CorruptFile, $"Missing page '{name}'.");
                AnnotationJsonConverter.ResetSkipped();
                var page = JsonSerializer.Deserialize<Page>(ZipSafety.ReadAllBytes(entry, ZipSafety.MaxJsonEntryBytes), ShotwiseJson.Compact)
                           ?? throw new ShotwiseException(ErrorCode.CorruptFile, $"Page '{name}' is empty.");
                if (AnnotationJsonConverter.SkippedUnknownKinds > 0)
                    _log.LogWarning("Skipped {Count} annotation(s) of unknown kind in {Page}", AnnotationJsonConverter.SkippedUnknownKinds, name);
                doc.Pages.Add(page);
            }
            if (doc.Pages.Count == 0) throw new ShotwiseException(ErrorCode.CorruptFile, "Document has no pages.");

            // assets
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith('/')) continue;
                ct.ThrowIfCancellationRequested();
                var file = e.FullName["assets/".Length..];
                if (file.Contains('/')) { _log.LogWarning("Ignoring nested asset entry {Entry}", e.FullName); continue; }
                var dot = file.LastIndexOf('.');
                var id = dot > 0 ? file[..dot] : file;
                var ext = dot > 0 ? file[(dot + 1)..] : "";
                if (!AssetId.IsValidValue(id)) { _log.LogWarning("Ignoring asset with invalid id {Entry}", e.FullName); continue; }
                doc.Assets.AddWithId(new AssetId(id), ZipSafety.ReadAllBytes(e, ZipSafety.MaxBinaryEntryBytes), AssetStore.MediaTypeForExtension(ext));
            }
            foreach (var missing in doc.ReferencedAssets().Distinct().Where(a => !doc.Assets.Contains(a)))
                _log.LogWarning("Document {Doc} references missing asset {Asset}", doc.Id, missing);

            doc.IsDirty = false;
            return doc;
        }
        catch (InvalidDataException ex)
        {
            throw new ShotwiseException(ErrorCode.CorruptFile, "The file is not a valid ZIP container.", ex);
        }
        catch (JsonException ex)
        {
            throw new ShotwiseException(ErrorCode.CorruptFile, $"Invalid JSON in document: {ex.Message}", ex);
        }
    }

    public Task<DocumentMetadata> ReadMetadataAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            ZipSafety.Validate(zip);
            var e = ZipSafety.Find(zip, "metadata.json");
            if (e is null) return DocumentMetadata.Create(File.GetCreationTime(path));
            return JsonSerializer.Deserialize<DocumentMetadata>(ZipSafety.ReadAllBytes(e, ZipSafety.MaxJsonEntryBytes), ShotwiseJson.Compact) ?? DocumentMetadata.Create();
        }
        catch (InvalidDataException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "The file is not a valid ZIP container.", ex); }
        catch (JsonException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "Invalid metadata.json.", ex); }
    }, ct);

    public Task<byte[]?> ReadThumbnailAsync(string path, CancellationToken ct = default) => Task.Run<byte[]?>(() =>
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            ZipSafety.Validate(zip);
            var e = ZipSafety.Find(zip, "thumbnail.png");
            return e is null ? null : ZipSafety.ReadAllBytes(e, 64 * 1024 * 1024);
        }
        catch (InvalidDataException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "The file is not a valid ZIP container.", ex); }
    }, ct);

    /// <summary>True when the file has the .shotx extension and a ZIP signature.</summary>
    public static bool IsShotxFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), FileExtension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        Span<byte> sig = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                value = p.Value.GetString() ?? "";
                return true;
            }
        value = "";
        return false;
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}
