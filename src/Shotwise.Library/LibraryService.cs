using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Library.Catalog;

namespace Shotwise.Library;

/// <summary>
/// SQLite (WAL + FTS5) backed library catalog over a folder of <c>.shotx</c>/image/video files (LIB-01..08).
/// <para>
/// Layout: <c>&lt;LibraryRoot&gt;\yyyy\MM\&lt;timestamp&gt;-&lt;app&gt;.shotx|.mp4|.gif</c>. Hidden state lives in
/// <c>&lt;LibraryRoot&gt;\.shotwise\</c> (mirror of tags/favourites/share history so a lost catalog can be rebuilt completely).
/// Thumbnails (PNG, longest side ≤ 320 px) live in the catalog's thumbnail cache folder, one file per item id.
/// </para>
/// <para>Thread-safety: every database access is serialised through one gate; file+catalog mutations are serialised by an
/// index gate so the watcher reconciler never races with explicit operations. <see cref="Changed"/> is raised on a thread-pool thread.</para>
/// </summary>
public sealed partial class LibraryService : ILibraryService, IDisposable
{
    public const int ThumbnailMaxSide = 320;
    internal const string StateFolderName = ".shotwise";
    internal const string MirrorFileName = "catalog.json";
    internal const string SidecarExtension = ".shotmeta";
    private const long MaxDocLoadBytes = 100L * 1024 * 1024;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".shotx", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp", ".ico" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm", ".wmv" };

    private readonly string _catalogPath;
    private readonly string _thumbsDir;
    private readonly IShotxSerializer _shotx;
    private readonly IImageCodec? _codec;
    private readonly IMediaThumbnailSource? _media;
    private readonly ISettingsService? _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _nameLock = new();
    private readonly HashSet<string> _reservedNames = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _root;
    private LibraryDatabase? _db;
    private bool _initialized;
    private bool _disposed;

    public LibraryService(IAppPaths paths, ISettingsService settings, IShotxSerializer shotx, ILogger<LibraryService>? logger = null,
        IImageCodec? codec = null, IMediaThumbnailSource? media = null)
        : this(ResolveRoot(paths, settings), Path.Combine(paths.LocalAppData, "library.db"), Path.Combine(paths.LocalAppData, "thumbnails"),
            shotx, logger, codec, media, settings)
    { }

    internal LibraryService(string root, string catalogPath, string thumbsDir, IShotxSerializer shotx, ILogger? logger = null,
        IImageCodec? codec = null, IMediaThumbnailSource? media = null, ISettingsService? settings = null)
    {
        _root = NormalizeRoot(root);
        _catalogPath = catalogPath;
        _thumbsDir = thumbsDir;
        _shotx = shotx;
        _codec = codec;
        _media = media;
        _settings = settings;
        _log = Shotwise.Core.Platform.LogRedaction.Wrap(logger ?? NullLogger.Instance);
    }

    public string LibraryRoot => _root;

    /// <summary>Folder holding cached thumbnails (<c>&lt;id&gt;.png</c>).</summary>
    public string ThumbnailsFolder => _thumbsDir;

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    private static string ResolveRoot(IAppPaths paths, ISettingsService settings)
    {
        var configured = settings.Current.Library.LibraryRoot;
        return string.IsNullOrWhiteSpace(configured) ? paths.DefaultLibraryRoot : configured;
    }

    internal static string NormalizeRoot(string root)
        => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private LibraryDatabase Db => _db ?? throw new ShotwiseException(ErrorCode.LibraryUnavailable, "The library has not been initialized.");

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Opens the catalog (rebuilding it from the files when missing/corrupt), reconciles with disk and starts the file watcher.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            try { Directory.CreateDirectory(_root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ShotwiseException(ErrorCode.LibraryLocationInvalid, $"The library folder '{_root}' is not accessible.", ex) { Detail = _root };
            }
            Directory.CreateDirectory(_thumbsDir);
            var fresh = !File.Exists(_catalogPath);
            _db = OpenOrRecreateCatalog(ref fresh);
            Dictionary<string, CatalogEntry>? hints = null;
            CatalogFile? mirror = null;
            if (fresh)
            {
                mirror = await ReadMirrorAsync(_root, ct).ConfigureAwait(false);
                hints = mirror?.Items.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            }
            await ReconcileAsync(hints, ct).ConfigureAwait(false);
            if (mirror is not null) await ApplySharesAsync(mirror.Shares, ct).ConfigureAwait(false);
            StartWatcher();
            _initialized = true;
            if (fresh) Raise(LibraryChangeKind.Reset, Array.Empty<Guid>());
        }
        finally { _initGate.Release(); }
    }

    private LibraryDatabase OpenOrRecreateCatalog(ref bool fresh)
    {
        try
        {
            return LibraryDatabase.Open(_catalogPath);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            _log.LogWarning(ex, "Catalog '{Path}' is unusable; rebuilding from library files.", _catalogPath);
            QuarantineCatalog();
            fresh = true;
            return LibraryDatabase.Open(_catalogPath);
        }
    }

    private void QuarantineCatalog()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _catalogPath + suffix;
            if (!File.Exists(f)) continue;
            try { File.Move(f, f + ".corrupt-" + stamp, overwrite: true); }
            catch (IOException) { try { File.Delete(f); } catch (IOException) { } }
        }
    }

    /// <summary>Discards the catalog and rebuilds it from the files (LIB-08). User state is restored from the mirror when present.</summary>
    public async Task RebuildFromFilesAsync(CancellationToken ct = default)
    {
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopWatcher();
            // Capture user state first so the rebuild keeps tags/favourites even if the mirror is stale.
            CatalogFile? state = null;
            if (_db is not null) { try { state = await ExportCatalogAsync(ct).ConfigureAwait(false); } catch (Exception ex) { _log.LogWarning(ex, "Could not export state before rebuild."); } }
            state ??= await ReadMirrorAsync(_root, ct).ConfigureAwait(false);
            _db?.Dispose();
            _db = null;
            QuarantineCatalog();
            var fresh = true;
            _db = OpenOrRecreateCatalog(ref fresh);
            await ReconcileAsync(state?.Items.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            if (state is not null) await ApplySharesAsync(state.Shares, ct).ConfigureAwait(false);
            StartWatcher();
            _initialized = true;
            Raise(LibraryChangeKind.Reset, Array.Empty<Guid>());
        }
        finally { _initGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatcher();
        _mirrorTimer?.Dispose();
        try { if (_mirrorDirty && _db is not null) FlushMirrorAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Mirror flush on dispose failed."); }
        _db?.Dispose();
    }

    private void Raise(LibraryChangeKind kind, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 && kind != LibraryChangeKind.Reset) return;
        var h = Changed;
        if (h is null) return;
        try { h(this, new LibraryChangedEventArgs(kind, ids)); }
        catch (Exception ex) { _log.LogWarning(ex, "A Changed handler threw."); }
    }

    // ------------------------------------------------------------------ path helpers

    internal string ToFull(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Relative '/' path under the root, or null when outside the root / inside a hidden folder.</summary>
    internal string? ToRel(string full)
    {
        var rel = Path.GetRelativePath(_root, Path.GetFullPath(full));
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || rel == ".") return null;
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal static bool IsHiddenRel(string rel) => rel.Split('/').Any(s => s.StartsWith('.'));

    internal static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExt.Contains(ext)) return MediaKind.Image;
        if (VideoExt.Contains(ext)) return MediaKind.Video;
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)) return MediaKind.Gif;
        return null;
    }

    internal static bool IsTracked(string rel) => !IsHiddenRel(rel) && KindOf(rel) is not null;

    private static string Sanitize(string? s, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        var lastUnderscore = false;
        foreach (var ch in s.Trim())
        {
            var bad = Array.IndexOf(invalid, ch) >= 0 || char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (bad) { if (!lastUnderscore) sb.Append('_'); lastUnderscore = true; }
            else { sb.Append(ch); lastUnderscore = false; }
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim('_', '.', ' ');
    }

    /// <summary>Allocates a unique <c>yyyy\MM\timestamp-app.ext</c> path and reserves it until the file exists.</summary>
    private string AllocatePath(DateTimeOffset when, string? app, string ext)
    {
        var local = when.ToLocalTime();
        var dir = Path.Combine(_root, local.ToString("yyyy", CultureInfo.InvariantCulture), local.ToString("MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var baseName = local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "-" + (Sanitize(app) is { Length: > 0 } a ? a : "capture");
        lock (_nameLock)
        {
            for (var i = 0; ; i++)
            {
                var name = i == 0 ? baseName : $"{baseName}-{i + 1}";
                var full = Path.Combine(dir, name + ext);
                if (!File.Exists(full) && _reservedNames.Add(full)) return full;
            }
        }
    }

    private void Release(string full) { lock (_nameLock) _reservedNames.Remove(full); }

    internal static long ToMs(DateTimeOffset d) => d.ToUnixTimeMilliseconds();
    internal static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    internal static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : null;

    internal static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var res = new List<string>();
        if (tags is null) return res;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var t = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (t.Length == 0) continue;
            if (t.Length > 64) t = t[..64];
            if (seen.Add(t)) res.Add(t);
        }
        return res;
    }
}
