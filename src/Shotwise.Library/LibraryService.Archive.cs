using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Serialization;
using Shotwise.Library.Catalog;

namespace Shotwise.Library;

public sealed partial class LibraryService
{
    internal const string ArchiveManifestName = "manifest.json";
    internal const string ArchiveCatalogName = "catalog.json";
    internal const string ArchiveFilesPrefix = "files/";
    private const int ArchiveMaxEntries = 500_000;
    private const long ArchiveMaxTotal = 50L * 1024 * 1024 * 1024;
    public long MaxRestoreBytes { get; set; } = ArchiveMaxTotal;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Safe relative '/' path that cannot escape the library or name a Windows device.</summary>
    internal static bool IsSafeRelativePath(string rel)
    {
        if (!ZipSafety.IsSafeEntryName(rel) || rel.EndsWith('/')) return false;
        foreach (var seg in rel.Split('/'))
        {
            var stem = seg.Contains('.') ? seg[..seg.IndexOf('.')] : seg;
            if (ReservedNames.Contains(stem)) return false;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ backup

    public async Task BackupAsync(string archivePath, IProgress<double>? p, CancellationToken ct)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ShotwiseException(ErrorCode.InvalidArgument, "No archive path given.");
        var full = Path.GetFullPath(archivePath);
        var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var cat = await ExportCatalogAsync(ct).ConfigureAwait(false);
            // Only archive files that exist; sidecars travel with their media file.
            var files = new List<(string Rel, long Size)>();
            foreach (var e in cat.Items.ToList())
            {
                var f = ToFull(e.Path);
                if (!File.Exists(f)) { cat.Items.Remove(e); continue; }
                files.Add((e.Path, new FileInfo(f).Length));
                if (File.Exists(f + SidecarExtension)) files.Add((e.Path + SidecarExtension, new FileInfo(f + SidecarExtension).Length));
            }
            var total = Math.Max(1L, files.Sum(x => x.Size));
            long done = 0;
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonEntryAsync(zip, ArchiveManifestName, new ArchiveManifest { FileCount = files.Count, AppVersion = typeof(LibraryService).Assembly.GetName().Version?.ToString() }, ct).ConfigureAwait(false);
                await WriteJsonEntryAsync(zip, ArchiveCatalogName, cat, ct).ConfigureAwait(false);
                foreach (var (rel, _) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(rel).ToLowerInvariant();
                    var level = ext is ".bmp" or ".tif" or ".tiff" or ".ico" or SidecarExtension ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                    var entry = zip.CreateEntry(ArchiveFilesPrefix + rel, level);
                    entry.LastWriteTime = File.GetLastWriteTime(ToFull(rel));
                    await using var es = entry.Open();
                    await using var src = new FileStream(ToFull(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await es.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        p?.Report(Math.Min(0.99, (double)done / total));
                    }
                }
            }
            File.Move(tmp, full, overwrite: true);
            p?.Report(1.0);
        }
        catch (OperationCanceledException) { TryDelete(tmp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(tmp);
            throw new ShotwiseException(ErrorCode.BackupFailed, "The backup could not be written: " + ex.Message, ex) { Detail = archivePath };
        }
        catch { TryDelete(tmp); throw; }
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await JsonSerializer.SerializeAsync(s, value, ShotwiseJson.Indented, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Validates the whole archive (names, limits, manifest, catalog) <i>before</i> writing anything, then extracts into the
    /// library (never overwriting different existing files — those get a unique name) and re-indexes. Any failure removes
    /// every file created so far.
    /// </summary>
    public async Task RestoreAsync(string archivePath, IProgress<double>? p, CancellationToken ct)
    {
        EnsureReady();
        if (!File.Exists(archivePath)) throw new ShotwiseException(ErrorCode.FileNotFound, "The archive does not exist.") { Detail = archivePath };
        var created = new List<string>();
        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(archivePath); }
            catch (InvalidDataException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "The archive is not a valid .shotarchive.", ex); }
            using (zip)
            {
                var (catalog, fileEntries) = ValidateArchive(zip, MaxRestoreBytes);
                var hints = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
                var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long total = Math.Max(1L, fileEntries.Sum(e => e.Entry.Length));
                long done = 0;
                try
                {
                    foreach (var (rel, entry) in fileEntries.OrderBy(e => e.Rel.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = ToFull(rel);
                        var finalRel = rel;
                        if (rel.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase) && remap.TryGetValue(rel[..^SidecarExtension.Length], out var primaryRel))
                        { finalRel = primaryRel + SidecarExtension; target = ToFull(finalRel); }
                        if (!IsInsideRoot(target)) throw new ShotwiseException(ErrorCode.PathTraversal, "Archive entry escapes the library.") { Detail = rel };
                        if (File.Exists(target))
                        {
                            if (new FileInfo(target).Length == entry.Length)
                            {
                                await using var existing = File.OpenRead(target);
                                await using var archived = entry.Open();
                                if (await StreamsEqualAsync(existing, archived, ct).ConfigureAwait(false))
                                { remap[rel] = finalRel; done += entry.Length; p?.Report(0.95 * done / total); continue; }
                            }
                            // Different content under the same name: keep both.
                            var dir = Path.GetDirectoryName(target)!;
                            var stem = Path.GetFileNameWithoutExtension(target);
                            var ext = Path.GetExtension(target);
                            for (var i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{stem} (restored {i}){ext}");
                            finalRel = ToRel(target)!;
                        }
                        remap[rel] = finalRel;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var tmp = Path.Combine(Path.GetDirectoryName(target)!, "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
                        try
                        {
                            await using (var o = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                            await using (var i = entry.Open())
                            {
                                var buf = new byte[81920];
                                long written = 0;
                                int n;
                                while ((n = await i.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                                {
                                    written += n;
                                    if (written > entry.Length || written > ZipSafety.MaxBinaryEntryBytes)
                                        throw new ShotwiseException(ErrorCode.CorruptFile, "Archive entry is larger than declared.") { Detail = rel };
                                    await o.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                                    done += n;
                                    p?.Report(0.95 * Math.Min(1.0, (double)done / total));
                                }
                                if (written != entry.Length) throw new ShotwiseException(ErrorCode.CorruptFile, "Archive entry is truncated.") { Detail = rel };
                            }
                            File.Move(tmp, target);
                            File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                            created.Add(target);
                        }
                        catch { TryDelete(tmp); throw; }
                    }
                    foreach (var e in catalog.Items)
                    {
                        var rel = remap.TryGetValue(e.Path, out var nr) ? nr : e.Path;
                        hints[rel] = e;
                    }
                    await ReconcileCoreAsync(hints, ct).ConfigureAwait(false);
                    await MergeUserStateAsync(hints, ct).ConfigureAwait(false);
                    await ApplySharesAsync(catalog.Shares, ct).ConfigureAwait(false);
                }
                catch
                {
                    foreach (var f in created) TryDelete(f);
                    // Drop rows the failed attempt may already have indexed.
                    try { await ReconcileCoreAsync(null, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { _log.LogDebug(ex, "Rollback reconcile failed."); }
                    throw;
                }
            }
            ScheduleMirror();
            p?.Report(1.0);
            Raise(LibraryChangeKind.Reset, Array.Empty<Guid>());
        }
        catch (OperationCanceledException) { throw; }
        catch (ShotwiseException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            throw new ShotwiseException(ErrorCode.RestoreFailed, "The archive could not be restored: " + ex.Message, ex) { Detail = archivePath };
        }
        finally { _indexGate.Release(); }
    }

    private bool IsInsideRoot(string full)
    {
        var f = Path.GetFullPath(full);
        return f.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks the archive without extracting. Throws PathTraversal / TooManyEntries / FileTooLarge / InvalidFileFormat / CorruptFile.</summary>
    internal static (CatalogFile Catalog, List<(string Rel, ZipArchiveEntry Entry)> Files) ValidateArchive(ZipArchive zip, long maxTotal = ArchiveMaxTotal)
    {
        if (maxTotal <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotal));
        ZipSafety.Validate(zip, ArchiveMaxEntries, maxTotal);
        var manifestEntry = ZipSafety.Find(zip, ArchiveManifestName)
            ?? throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The archive has no manifest.");
        var catalogEntry = ZipSafety.Find(zip, ArchiveCatalogName)
            ?? throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The archive has no catalog.");
        ArchiveManifest? manifest;
        CatalogFile? catalog;
        try
        {
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(ZipSafety.ReadAllBytes(manifestEntry, ZipSafety.MaxJsonEntryBytes), ShotwiseJson.Compact);
            catalog = JsonSerializer.Deserialize<CatalogFile>(ZipSafety.ReadAllBytes(catalogEntry, ZipSafety.MaxJsonEntryBytes), ShotwiseJson.Compact);
        }
        catch (JsonException ex) { throw new ShotwiseException(ErrorCode.CorruptFile, "The archive metadata is invalid.", ex); }
        if (manifest is null || manifest.Format != ArchiveManifest.FileFormat)
            throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Not a Shotwise archive.");
        if (manifest.Version > 1) throw new ShotwiseException(ErrorCode.NewerFormatVersion, "The archive was created by a newer version.");
        if (catalog is null || catalog.Format != CatalogFile.FileFormat)
            throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The archive catalog is invalid.");
        foreach (var it in catalog.Items)
            if (!IsSafeRelativePath(it.Path))
                throw new ShotwiseException(ErrorCode.PathTraversal, "Unsafe path in archive catalog.") { Detail = it.Path };

        var files = new List<(string, ZipArchiveEntry)>();
        foreach (var e in zip.Entries)
        {
            if (e.FullName is ArchiveManifestName or ArchiveCatalogName) continue;
            if (e.FullName.EndsWith('/')) continue; // directory entries are ignored
            if (!e.FullName.StartsWith(ArchiveFilesPrefix, StringComparison.Ordinal))
                throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Unexpected entry in archive.") { Detail = e.FullName };
            var rel = e.FullName[ArchiveFilesPrefix.Length..];
            var primary = rel.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase) ? rel[..^SidecarExtension.Length] : rel;
            if (!IsSafeRelativePath(rel) || !IsTracked(primary))
                throw new ShotwiseException(ErrorCode.PathTraversal, "Unsafe or unsupported file in archive.") { Detail = e.FullName };
            if (e.Length > ZipSafety.MaxBinaryEntryBytes) throw new ShotwiseException(ErrorCode.FileTooLarge, "Archive entry too large.") { Detail = e.FullName };
            files.Add((rel, e));
        }
        return (catalog, files);
    }

    /// <summary>Merges tags/favourites/OCR from a catalog into rows that already exist (re-indexing only applies hints to new rows).</summary>
    private Task MergeUserStateAsync(Dictionary<string, CatalogEntry> hints, CancellationToken ct)
        => Db.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var (rel, h) in hints)
            {
                var rid = LibraryDatabase.Scalar(c, "SELECT rid FROM items WHERE rel=@r", tx, ("@r", rel));
                if (rid is null) continue;
                var r = Convert.ToInt64(rid, System.Globalization.CultureInfo.InvariantCulture);
                var id = (string)LibraryDatabase.Scalar(c, "SELECT id FROM items WHERE rid=@r", tx, ("@r", r))!;
                foreach (var t in NormalizeTags(h.Tags))
                    LibraryDatabase.Exec(c, "INSERT OR IGNORE INTO tags(item_id,tag) VALUES(@i,@t)", tx, ("@i", id), ("@t", t));
                if (h.Favorite) LibraryDatabase.Exec(c, "UPDATE items SET is_fav=1 WHERE rid=@r", tx, ("@r", r));
                if (!string.IsNullOrWhiteSpace(h.Ocr))
                    LibraryDatabase.Exec(c, "UPDATE items SET ocr=@o WHERE rid=@r AND (ocr IS NULL OR ocr='')", tx, ("@o", h.Ocr), ("@r", r));
                FtsRefresh(c, tx, r);
            }
            tx.Commit();
        }, ct);
}
