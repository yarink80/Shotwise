using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Documents;

/// <summary>Identifier of a binary asset inside a document (also its file stem in .shotx: assets/{id}.{ext}).</summary>
[JsonConverter(typeof(AssetIdJsonConverter))]
public readonly record struct AssetId(string Value)
{
    public static AssetId New() => new(Guid.NewGuid().ToString("N"));
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Safe identifiers: 1–64 chars of [A-Za-z0-9_-] (prevents path traversal when used as a file name).</summary>
    public bool IsValid => IsValidValue(Value);

    public static bool IsValidValue(string? v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 64) return false;
        foreach (var c in v)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    public override string ToString() => Value ?? "";
}

/// <summary>Raw asset entry.</summary>
public sealed record AssetInfo(AssetId Id, string MediaType, long Length, string Sha256);

/// <summary>
/// Per-document binary store (PNG/SVG/PDF/...), content-addressed (identical bytes are stored once).
/// <para>Lifetime: assets referenced from the document graph are always kept; additional holders (undo history,
/// clipboard) call <see cref="Retain"/>/<see cref="Release"/>. <see cref="Prune"/> removes assets that are neither
/// retained nor referenced by the given document roots. The store holds bytes only; <see cref="GetImage"/> decodes via
/// <see cref="Codec"/> (IImageCodec from DI) or the built-in PNG decoder and caches the result.</para>
/// Thread-safe.
/// </summary>
public sealed class AssetStore
{
    private sealed class Entry
    {
        public required byte[] Data;
        public required string MediaType;
        public required string Hash;
        public int RefCount;
        public BgraImage? Decoded;
    }

    private readonly object _gate = new();
    private readonly Dictionary<AssetId, Entry> _entries = new();
    private readonly Dictionary<string, AssetId> _byHash = new(StringComparer.Ordinal);

    /// <summary>Decoder used by <see cref="GetImage"/>; set by whoever creates/loads the document (serializer, editor).</summary>
    public IImageCodec? Codec { get; set; }

    /// <summary>Maximum number of decoded images kept in memory.</summary>
    public int MaxDecodedCacheEntries { get; set; } = 32;

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Adds bytes (deduplicated by SHA-256) and returns the asset id.</summary>
    public AssetId Add(byte[] data, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out var existing)) return existing;
            var id = AssetId.New();
            _entries[id] = new Entry { Data = data, MediaType = mediaType, Hash = hash };
            _byHash[hash] = id;
            return id;
        }
    }

    /// <summary>Encodes the image as PNG (via <see cref="Codec"/> when set, else the built-in encoder) and adds it.</summary>
    public AssetId AddImage(BgraImage image)
    {
        var bytes = Codec is { } c ? c.Encode(image, ImageFormat.Png) : PngCodec.Encode(image, System.IO.Compression.CompressionLevel.Fastest);
        var id = Add(bytes, PngCodec.MediaType);
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e) && e.Decoded is null) e.Decoded = image;
            TrimCacheLocked();
        }
        return id;
    }

    /// <summary>Adds bytes under a fixed id (used when loading documents). Replaces an existing entry with the same id.</summary>
    public void AddWithId(AssetId id, byte[] data, string mediaType)
    {
        if (!id.IsValid) throw new ShotwiseException(ErrorCode.InvalidFileFormat, $"Invalid asset id '{id.Value}'.");
        ArgumentNullException.ThrowIfNull(data);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var old)) _byHash.Remove(old.Hash);
            _entries[id] = new Entry { Data = data, MediaType = mediaType, Hash = hash };
            _byHash.TryAdd(hash, id);
        }
    }

    public bool Contains(AssetId id) { lock (_gate) return _entries.ContainsKey(id); }

    public byte[] Get(AssetId id)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e)) return e.Data;
        }
        throw new ShotwiseException(ErrorCode.AssetMissing, $"Asset '{id}' not found.");
    }

    public bool TryGet(AssetId id, out byte[] data, out string mediaType)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e)) { data = e.Data; mediaType = e.MediaType; return true; }
        }
        data = Array.Empty<byte>();
        mediaType = "";
        return false;
    }

    public string GetMediaType(AssetId id)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var e)) return e.MediaType;
        }
        throw new ShotwiseException(ErrorCode.AssetMissing, $"Asset '{id}' not found.");
    }

    /// <summary>Decodes a raster asset (cached). Vector assets (svg/pdf) must be rasterized by the renderer instead.</summary>
    public BgraImage GetImage(AssetId id)
    {
        Entry e;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out e!)) throw new ShotwiseException(ErrorCode.AssetMissing, $"Asset '{id}' not found.");
            if (e.Decoded is not null)
            {
                var cached = e.Decoded;
                TrimCacheLocked();
                return cached;
            }
        }
        BgraImage img;
        if (Codec is { } codec) img = codec.Decode(e.Data);
        else if (PngCodec.IsPng(e.Data)) img = PngCodec.Decode(e.Data);
        else throw new ShotwiseException(ErrorCode.CodecUnavailable, $"No image codec available to decode asset '{id}' ({e.MediaType}).");
        lock (_gate)
        {
            e.Decoded = img;
            TrimCacheLocked();
        }
        return img;
    }

    private void TrimCacheLocked()
    {
        var decoded = _entries.Values.Where(x => x.Decoded is not null).ToList();
        var limit = Math.Max(0, MaxDecodedCacheEntries);
        if (decoded.Count <= limit) return;
        foreach (var x in decoded.Take(decoded.Count - limit)) x.Decoded = null;
    }

    public void ClearImageCache()
    {
        lock (_gate) foreach (var e in _entries.Values) e.Decoded = null;
    }

    /// <summary>Marks an extra holder of the asset (undo history, clipboard). No-op for unknown ids.</summary>
    public void Retain(AssetId id)
    {
        lock (_gate) if (_entries.TryGetValue(id, out var e)) e.RefCount++;
    }

    /// <summary>Releases a holder; the asset stays until <see cref="Prune"/> runs and the document no longer references it.</summary>
    public void Release(AssetId id)
    {
        lock (_gate) if (_entries.TryGetValue(id, out var e) && e.RefCount > 0) e.RefCount--;
    }

    public int GetRefCount(AssetId id) { lock (_gate) return _entries.TryGetValue(id, out var e) ? e.RefCount : 0; }

    /// <summary>Removes assets with no holders that are not in <paramref name="documentRoots"/>. Returns the number removed.</summary>
    public int Prune(IEnumerable<AssetId> documentRoots)
    {
        var live = new HashSet<AssetId>(documentRoots);
        lock (_gate)
        {
            var dead = _entries.Where(kv => kv.Value.RefCount <= 0 && !live.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            foreach (var id in dead) RemoveLocked(id);
            return dead.Count;
        }
    }

    /// <summary>Force-removes an asset regardless of holders.</summary>
    public bool Remove(AssetId id) { lock (_gate) return RemoveLocked(id); }

    private bool RemoveLocked(AssetId id)
    {
        if (!_entries.Remove(id, out var e)) return false;
        if (_byHash.TryGetValue(e.Hash, out var h) && h == id) _byHash.Remove(e.Hash);
        return true;
    }

    public IReadOnlyList<AssetInfo> Enumerate()
    {
        lock (_gate) return _entries.Select(kv => new AssetInfo(kv.Key, kv.Value.MediaType, kv.Value.Data.LongLength, kv.Value.Hash)).ToList();
    }

    /// <summary>Copies an asset from another store (paste between documents). Returns the id in this store.</summary>
    public AssetId ImportFrom(AssetStore other, AssetId id)
    {
        if (ReferenceEquals(other, this)) return id;
        return other.TryGet(id, out var data, out var mt) ? Add(data, mt) : throw new ShotwiseException(ErrorCode.AssetMissing, $"Asset '{id}' not found.");
    }

    /// <summary>File extension used in .shotx for a media type.</summary>
    public static string ExtensionForMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/svg+xml" => "svg",
        "application/pdf" => "pdf",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/bmp" => "bmp",
        "image/tiff" => "tif",
        _ => "bin",
    };

    public static string MediaTypeForExtension(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png",
        "svg" => "image/svg+xml",
        "pdf" => "application/pdf",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        _ => "application/octet-stream",
    };
}
