using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Serialization;
using Shotwise.Library.Catalog;

namespace Shotwise.Library;

public sealed partial class LibraryService
{
    internal sealed record IndexRow(string Rel, MediaKind Kind, string Name, long Created, long Modified, long Size, long MtimeTicks,
        int Width, int Height, long? DurationMs, string? App, string? Window, string? Url, string? Ocr, string? Callouts, bool Step,
        List<string> Tags, byte[]? Thumb, bool? Favorite, Guid? IdHint);

    private const string SelectCols =
        "i.id,i.rel,i.kind,i.name,i.created,i.modified,i.size,i.width,i.height,i.duration_ms,i.app,i.window,i.url,i.is_fav,i.is_step,i.has_thumb," +
        "(SELECT group_concat(tag, char(31)) FROM tags t WHERE t.item_id = i.id)";

    private LibraryItem Map(SqliteDataReader r)
    {
        var id = Guid.Parse(r.GetString(0));
        var tags = r.IsDBNull(16) ? Array.Empty<string>() : r.GetString(16).Split('\u001f');
        var hasThumb = r.GetInt64(15) != 0;
        return new LibraryItem(id, ToFull(r.GetString(1)), (MediaKind)r.GetInt32(2), r.GetString(3), FromMs(r.GetInt64(4)), FromMs(r.GetInt64(5)),
            r.GetInt64(6), r.GetInt32(7), r.GetInt32(8), r.IsDBNull(9) ? null : TimeSpan.FromMilliseconds(r.GetInt64(9)),
            r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
            tags, r.GetInt64(13) != 0, hasThumb ? Path.Combine(_thumbsDir, id.ToString("D") + ".png") : "")
        { IsStepCapture = r.GetInt64(14) != 0 };
    }

    // ------------------------------------------------------------------ building rows from files

    private static byte[] MakeThumbnail(BgraImage img)
        => PngCodec.Encode(ImageOps.Downscale(img, ThumbnailMaxSide), System.IO.Compression.CompressionLevel.Fastest);

    private BgraImage? TryDecodeImage(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 256L * 1024 * 1024) return null;
            if (string.Equals(fi.Extension, ".png", StringComparison.OrdinalIgnoreCase))
                return PngCodec.Decode(File.ReadAllBytes(path));
            return _codec?.Load(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not decode '{Path}' for thumbnail.", path);
            return null;
        }
    }

    internal async Task<IndexRow> BuildRowAsync(string fullPath, CatalogEntry? hint, DocumentMetadata? meta, ShotDocument? doc, BgraImage? preview, CancellationToken ct)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists) throw new ShotwiseException(ErrorCode.FileNotFound, "File not found.") { Detail = fullPath };
        var rel = ToRel(fullPath) ?? throw new ShotwiseException(ErrorCode.LibraryLocationInvalid, "File is outside the library.") { Detail = fullPath };
        var kind = KindOf(fullPath) ?? MediaKind.Other;
        var ext = fi.Extension.ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        int w = 0, h = 0;
        long? durMs = null;
        string? callouts = null;
        var step = false;
        BgraImage? thumbSource = preview;
        var hasOwnMetadata = false;

        if (ext == ".shotx")
        {
            if (meta is null && doc is null)
            {
                try { meta = await _shotx.ReadMetadataAsync(fullPath, ct).ConfigureAwait(false); }
                catch (ShotwiseException ex) when (ex.Code is ErrorCode.CorruptFile or ErrorCode.InvalidFileFormat or ErrorCode.NewerFormatVersion)
                { _log.LogWarning(ex, "Unreadable metadata in '{Path}'.", fullPath); }
            }
            if (doc is null && fi.Length <= MaxDocLoadBytes)
            {
                try { doc = await _shotx.LoadAsync(fullPath, ct).ConfigureAwait(false); }
                catch (ShotwiseException ex) { _log.LogWarning(ex, "Could not load '{Path}' for indexing; indexing without document data.", fullPath); }
            }
            meta ??= doc?.Metadata;
            hasOwnMetadata = meta is not null;
            if (doc is not null)
            {
                if (doc.Pages.Count > 0) { w = (int)Math.Round(doc.Pages[0].CanvasSize.Width); h = (int)Math.Round(doc.Pages[0].CanvasSize.Height); }
                callouts = doc.CollectAnnotationText();
            }
            if (thumbSource is null)
            {
                try
                {
                    var png = await _shotx.ReadThumbnailAsync(fullPath, ct).ConfigureAwait(false);
                    if (png is not null) thumbSource = PngCodec.Decode(png);
                }
                catch (Exception ex) when (ex is ShotwiseException or InvalidDataException) { _log.LogDebug(ex, "No stored thumbnail in '{Path}'.", fullPath); }
            }
            if (w == 0 && thumbSource is not null) { w = thumbSource.Width; h = thumbSource.Height; }
        }
        else if (kind == MediaKind.Image)
        {
            var img = TryDecodeImage(fullPath);
            if (img is not null) { w = img.Width; h = img.Height; thumbSource = img; }
        }
        else
        {
            var sc = await ReadSidecarAsync(fullPath, ct).ConfigureAwait(false);
            if (sc is not null)
            {
                meta ??= sc.Metadata;
                hasOwnMetadata = meta is not null;
                durMs = sc.DurationMs; w = sc.Width; h = sc.Height; step = sc.IsStepCapture;
            }
            if (thumbSource is null && _media is not null)
            {
                try { thumbSource = await _media.GetThumbnailAsync(fullPath, kind, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Media thumbnail failed for '{Path}'.", fullPath); }
            }
            if (w == 0 && thumbSource is not null) { w = thumbSource.Width; h = thumbSource.Height; }
        }

        if (meta?.SourcePresetId == PresetDefaults.StepCaptureId) step = true;
        var created = meta?.CaptureDate;
        if (created is null || !hasOwnMetadata)
        {
            created = hint?.Created ?? new DateTimeOffset(new DateTime(Math.Min(fi.CreationTimeUtc.Ticks, fi.LastWriteTimeUtc.Ticks), DateTimeKind.Utc));
        }
        var tags = NormalizeTags((meta?.Tags ?? Array.Empty<string>()).Concat(hint?.Tags ?? new List<string>()));
        var app = meta?.AppName ?? hint?.App;
        var window = meta?.WindowTitle ?? hint?.WindowTitle;
        var url = meta?.Url ?? hint?.Url;
        var ocr = !string.IsNullOrWhiteSpace(meta?.OcrText) ? meta!.OcrText : hint?.Ocr;
        if (hint is not null)
        {
            if (w == 0) { w = hint.Width; h = hint.Height; }
            durMs ??= hint.DurationMs;
            step |= hint.StepCapture;
        }
        byte[]? thumb = null;
        if (thumbSource is not null)
        {
            try { thumb = MakeThumbnail(thumbSource); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Thumbnail encode failed."); }
        }
        return new IndexRow(rel, kind, name, ToMs(created.Value), ToMs(new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)), fi.Length,
            fi.LastWriteTimeUtc.Ticks, w, h, durMs, app, window, url, ocr, callouts, step, tags, thumb, hint?.Favorite, hint?.Id);
    }

    private async Task<MediaSidecar?> ReadSidecarAsync(string mediaPath, CancellationToken ct)
    {
        var p = mediaPath + SidecarExtension;
        if (!File.Exists(p)) return null;
        try
        {
            var fi = new FileInfo(p);
            if (fi.Length > 4 * 1024 * 1024) return null;
            return JsonSerializer.Deserialize<MediaSidecar>(await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false), ShotwiseJson.Compact);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { _log.LogDebug(ex, "Bad sidecar '{Path}'.", p); return null; }
    }

    private static Task WriteSidecarAsync(string mediaPath, MediaSidecar sc, CancellationToken ct)
        => AtomicFile.WriteAllTextAsync(mediaPath + SidecarExtension, JsonSerializer.Serialize(sc, ShotwiseJson.Compact), ct);

    // ------------------------------------------------------------------ upsert / remove

    private static void FtsRefresh(SqliteConnection c, SqliteTransaction tx, long rid)
    {
        LibraryDatabase.Exec(c, "DELETE FROM items_fts WHERE rowid=@r", tx, ("@r", rid));
        LibraryDatabase.Exec(c,
            @"INSERT INTO items_fts(rowid,name,app,window,url,ocr,callouts,tags)
              SELECT rid, name, app, window, url, ocr, callouts, (SELECT group_concat(tag,' ') FROM tags WHERE item_id = items.id)
              FROM items WHERE rid=@r", tx, ("@r", rid));
    }

    /// <summary>Inserts or updates by relative path. Must be called while holding the index gate.</summary>
    private Task<(LibraryItem Item, bool IsNew, bool Changed)> UpsertAsync(IndexRow r, CancellationToken ct)
        => Db.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            long rid;
            string idStr;
            var isNew = false;
            long? oldSize = null, oldTicks = null;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT rid,id,size,mtime_ticks FROM items WHERE rel=@rel";
                q.Parameters.AddWithValue("@rel", r.Rel);
                using var rd = q.ExecuteReader();
                if (rd.Read()) { rid = rd.GetInt64(0); idStr = rd.GetString(1); oldSize = rd.GetInt64(2); oldTicks = rd.GetInt64(3); }
                else { rid = 0; idStr = ""; isNew = true; }
            }
            var website = HostOf(r.Url);
            if (isNew)
            {
                idStr = (r.IdHint is { } g && g != Guid.Empty ? g : Guid.NewGuid()).ToString("D");
                // A hinted id may already belong to a different row (duplicate catalog entries): fall back to a fresh id.
                if (LibraryDatabase.Scalar(c, "SELECT 1 FROM items WHERE id=@i", tx, ("@i", idStr)) is not null) idStr = Guid.NewGuid().ToString("D");
                LibraryDatabase.Exec(c,
                    @"INSERT INTO items(id,rel,kind,name,created,modified,size,mtime_ticks,width,height,duration_ms,app,window,url,website,ocr,callouts,is_fav,is_step,has_thumb)
                      VALUES(@id,@rel,@kind,@name,@created,@modified,@size,@ticks,@w,@h,@dur,@app,@win,@url,@site,@ocr,@call,@fav,@step,0)", tx,
                    ("@id", idStr), ("@rel", r.Rel), ("@kind", (int)r.Kind), ("@name", r.Name), ("@created", r.Created), ("@modified", r.Modified),
                    ("@size", r.Size), ("@ticks", r.MtimeTicks), ("@w", r.Width), ("@h", r.Height), ("@dur", r.DurationMs), ("@app", r.App),
                    ("@win", r.Window), ("@url", r.Url), ("@site", website), ("@ocr", r.Ocr), ("@call", r.Callouts),
                    ("@fav", r.Favorite == true ? 1 : 0), ("@step", r.Step ? 1 : 0));
                rid = Convert.ToInt64(LibraryDatabase.Scalar(c, "SELECT last_insert_rowid()", tx), CultureInfo.InvariantCulture);
            }
            else
            {
                LibraryDatabase.Exec(c,
                    @"UPDATE items SET kind=@kind,name=@name,created=@created,modified=@modified,size=@size,mtime_ticks=@ticks,width=@w,height=@h,
                        duration_ms=COALESCE(@dur,duration_ms),app=COALESCE(@app,app),window=COALESCE(@win,window),url=COALESCE(@url,url),
                        website=COALESCE(@site,website),ocr=COALESCE(@ocr,ocr),callouts=COALESCE(@call,callouts),is_step=(is_step OR @step)
                      WHERE rid=@rid", tx,
                    ("@kind", (int)r.Kind), ("@name", r.Name), ("@created", r.Created), ("@modified", r.Modified), ("@size", r.Size),
                    ("@ticks", r.MtimeTicks), ("@w", r.Width), ("@h", r.Height), ("@dur", r.DurationMs), ("@app", r.App), ("@win", r.Window),
                    ("@url", r.Url), ("@site", website), ("@ocr", r.Ocr), ("@call", r.Callouts), ("@step", r.Step ? 1 : 0), ("@rid", rid));
            }
            foreach (var t in r.Tags)
                LibraryDatabase.Exec(c, "INSERT OR IGNORE INTO tags(item_id,tag) VALUES(@i,@t)", tx, ("@i", idStr), ("@t", t));
            if (r.Thumb is not null)
            {
                Directory.CreateDirectory(_thumbsDir);
                File.WriteAllBytes(Path.Combine(_thumbsDir, idStr + ".png"), r.Thumb);
                LibraryDatabase.Exec(c, "UPDATE items SET has_thumb=1 WHERE rid=@r", tx, ("@r", rid));
            }
            FtsRefresh(c, tx, rid);
            LibraryItem item;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = $"SELECT {SelectCols} FROM items i WHERE i.rid=@r";
                q.Parameters.AddWithValue("@r", rid);
                using var rd = q.ExecuteReader();
                rd.Read();
                item = Map(rd);
            }
            tx.Commit();
            var changed = isNew || oldSize != r.Size || oldTicks != r.MtimeTicks;
            return (item, isNew, changed);
        }, ct);

    /// <summary>Removes rows by relative path (or whole folder prefix). Returns removed ids. Does not touch media files.</summary>
    private Task<List<Guid>> RemoveRowsAsync(string rel, bool prefix, CancellationToken ct)
        => Db.RunAsync(c =>
        {
            var ids = new List<(long Rid, string Id)>();
            using (var q = c.CreateCommand())
            {
                if (prefix)
                {
                    q.CommandText = @"SELECT rid,id FROM items WHERE rel = @rel OR rel LIKE @pat ESCAPE '\'";
                    q.Parameters.AddWithValue("@rel", rel);
                    q.Parameters.AddWithValue("@pat", EscapeLike(rel) + "/%");
                }
                else
                {
                    q.CommandText = "SELECT rid,id FROM items WHERE rel=@rel";
                    q.Parameters.AddWithValue("@rel", rel);
                }
                using var rd = q.ExecuteReader();
                while (rd.Read()) ids.Add((rd.GetInt64(0), rd.GetString(1)));
            }
            if (ids.Count == 0) return new List<Guid>();
            using var tx = c.BeginTransaction();
            foreach (var (rid, id) in ids) RemoveRowCore(c, tx, rid, id);
            tx.Commit();
            return ids.Select(x => Guid.Parse(x.Id)).ToList();
        }, ct);

    private void RemoveRowCore(SqliteConnection c, SqliteTransaction tx, long rid, string id)
    {
        LibraryDatabase.Exec(c, "DELETE FROM items_fts WHERE rowid=@r", tx, ("@r", rid));
        LibraryDatabase.Exec(c, "DELETE FROM tags WHERE item_id=@i", tx, ("@i", id));
        LibraryDatabase.Exec(c, "DELETE FROM items WHERE rid=@r", tx, ("@r", rid));
        try { File.Delete(Path.Combine(_thumbsDir, id + ".png")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ------------------------------------------------------------------ reconcile

    /// <summary>Indexes one existing file if new/changed (size or mtime differs). Caller holds the index gate.</summary>
    private async Task<(LibraryItem? Item, bool IsNew)> IndexFileIfChangedAsync(string full, Dictionary<string, CatalogEntry>? hints, CancellationToken ct)
    {
        var rel = ToRel(full);
        if (rel is null || !IsTracked(rel)) return (null, false);
        var fi = new FileInfo(full);
        if (!fi.Exists) return (null, false);
        var same = await Db.RunAsync(c => LibraryDatabase.Scalar(c, "SELECT 1 FROM items WHERE rel=@r AND size=@s AND mtime_ticks=@t", null,
            ("@r", rel), ("@s", fi.Length), ("@t", fi.LastWriteTimeUtc.Ticks)) is not null, ct).ConfigureAwait(false);
        if (same) return (null, false);
        CatalogEntry? hint = null;
        hints?.TryGetValue(rel, out hint);
        var row = await BuildRowAsync(full, hint, null, null, null, ct).ConfigureAwait(false);
        var (item, isNew, _) = await UpsertAsync(row, ct).ConfigureAwait(false);
        return (item, isNew);
    }

    /// <summary>Full reconciliation of the catalog with the library folder: adds new files, updates changed ones, drops missing ones.</summary>
    internal async Task ReconcileAsync(Dictionary<string, CatalogEntry>? hints, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ReconcileCoreAsync(hints, ct).ConfigureAwait(false); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Reconcile body; caller must hold the index gate.</summary>
    internal async Task ReconcileCoreAsync(Dictionary<string, CatalogEntry>? hints, CancellationToken ct)
    {
        {
            var added = new List<Guid>();
            var updated = new List<Guid>();
            var removed = new List<Guid>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = ToRel(f);
                    if (rel is null || !IsTracked(rel)) continue;
                    onDisk.Add(rel);
                    try
                    {
                        var (item, isNew) = await IndexFileIfChangedAsync(f, hints, ct).ConfigureAwait(false);
                        if (item is not null) (isNew ? added : updated).Add(item.Id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ShotwiseException)
                    {
                        _log.LogWarning(ex, "Skipping '{Path}' during reconcile.", f);
                    }
                }
            }
            var known = await Db.RunAsync(c =>
            {
                var l = new List<string>();
                using var q = c.CreateCommand();
                q.CommandText = "SELECT rel FROM items";
                using var rd = q.ExecuteReader();
                while (rd.Read()) l.Add(rd.GetString(0));
                return l;
            }, ct).ConfigureAwait(false);
            foreach (var rel in known)
            {
                if (onDisk.Contains(rel)) continue;
                removed.AddRange(await RemoveRowsAsync(rel, false, ct).ConfigureAwait(false));
            }
            if (added.Count + updated.Count + removed.Count > 0) ScheduleMirror();
            Raise(LibraryChangeKind.Added, added);
            Raise(LibraryChangeKind.Updated, updated);
            Raise(LibraryChangeKind.Removed, removed);
        }
    }

    // ------------------------------------------------------------------ catalog mirror / export

    private Timer? _mirrorTimer;
    private volatile bool _mirrorDirty;

    internal string MirrorPath(string root) => Path.Combine(root, StateFolderName, MirrorFileName);

    private void ScheduleMirror()
    {
        if (_disposed) return;
        _mirrorDirty = true;
        _mirrorTimer ??= new Timer(_ => { _ = FlushMirrorAsync(CancellationToken.None); }, null, Timeout.Infinite, Timeout.Infinite);
        _mirrorTimer.Change(MirrorDelayMs, Timeout.Infinite);
    }

    internal int MirrorDelayMs { get; set; } = 3000;

    internal async Task FlushMirrorAsync(CancellationToken ct)
    {
        if (_db is null) return;
        try
        {
            _mirrorDirty = false;
            var cat = await ExportCatalogAsync(ct).ConfigureAwait(false);
            await AtomicFile.WriteAllTextAsync(MirrorPath(_root), JsonSerializer.Serialize(cat, ShotwiseJson.Indented), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Could not write catalog mirror.");
        }
    }

    internal async Task<CatalogFile?> ReadMirrorAsync(string root, CancellationToken ct)
    {
        var p = MirrorPath(root);
        if (!File.Exists(p)) return null;
        try
        {
            if (new FileInfo(p).Length > 256L * 1024 * 1024) return null;
            var cat = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false), ShotwiseJson.Compact);
            return cat is { Format: CatalogFile.FileFormat } ? cat : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { _log.LogWarning(ex, "Catalog mirror unreadable."); return null; }
    }

    /// <summary>Exports tags, favourites, OCR text and share history (everything that cannot be re-derived from the files alone).</summary>
    public Task<CatalogFile> ExportCatalogAsync(CancellationToken ct = default)
        => Db.RunAsync(c =>
        {
            var cat = new CatalogFile();
            using (var q = c.CreateCommand())
            {
                q.CommandText = $"SELECT {SelectCols}, i.ocr FROM items i ORDER BY i.rel";
                using var rd = q.ExecuteReader();
                while (rd.Read())
                {
                    cat.Items.Add(new CatalogEntry
                    {
                        Id = Guid.Parse(rd.GetString(0)),
                        Path = rd.GetString(1),
                        Kind = (MediaKind)rd.GetInt32(2),
                        Name = rd.GetString(3),
                        Created = FromMs(rd.GetInt64(4)),
                        Width = rd.GetInt32(7),
                        Height = rd.GetInt32(8),
                        DurationMs = rd.IsDBNull(9) ? null : rd.GetInt64(9),
                        App = rd.IsDBNull(10) ? null : rd.GetString(10),
                        WindowTitle = rd.IsDBNull(11) ? null : rd.GetString(11),
                        Url = rd.IsDBNull(12) ? null : rd.GetString(12),
                        Favorite = rd.GetInt64(13) != 0,
                        StepCapture = rd.GetInt64(14) != 0,
                        Tags = rd.IsDBNull(16) ? new List<string>() : rd.GetString(16).Split('\u001f').ToList(),
                        Ocr = rd.IsDBNull(17) ? null : rd.GetString(17),
                    });
                }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT item_id,at,dest_id,dest_name,link,title FROM share_history ORDER BY id";
                using var rd = q.ExecuteReader();
                while (rd.Read())
                    cat.Shares.Add(new CatalogShare
                    {
                        ItemId = rd.IsDBNull(0) ? null : Guid.Parse(rd.GetString(0)),
                        At = FromMs(rd.GetInt64(1)),
                        DestinationId = rd.GetString(2),
                        DestinationName = rd.GetString(3),
                        Link = rd.IsDBNull(4) ? null : rd.GetString(4),
                        Title = rd.IsDBNull(5) ? null : rd.GetString(5),
                    });
            }
            return cat;
        }, ct);

    /// <summary>Re-inserts share history rows that are not yet present (dedupe on item+time+destination).</summary>
    private Task ApplySharesAsync(IEnumerable<CatalogShare> shares, CancellationToken ct)
        => Db.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var s in shares)
            {
                var item = s.ItemId?.ToString("D");
                if (LibraryDatabase.Scalar(c, "SELECT 1 FROM share_history WHERE item_id IS @i AND at=@at AND dest_id=@d", tx,
                        ("@i", item), ("@at", ToMs(s.At)), ("@d", s.DestinationId)) is not null) continue;
                LibraryDatabase.Exec(c, "INSERT INTO share_history(item_id,at,dest_id,dest_name,link,title) VALUES(@i,@at,@d,@n,@l,@t)", tx,
                    ("@i", item), ("@at", ToMs(s.At)), ("@d", s.DestinationId), ("@n", s.DestinationName), ("@l", s.Link), ("@t", s.Title));
            }
            tx.Commit();
        }, ct);
}
