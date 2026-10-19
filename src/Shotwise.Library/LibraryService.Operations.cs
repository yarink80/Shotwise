using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Library.Catalog;

namespace Shotwise.Library;

public sealed partial class LibraryService
{
    private void EnsureReady() => _ = Db;

    // ------------------------------------------------------------------ adding items

    public async Task<LibraryItem> AddCaptureAsync(ShotDocument doc, BgraImage flattenedPreview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(flattenedPreview);
        EnsureReady();
        var path = AllocatePath(doc.Metadata.CaptureDate, doc.Metadata.AppName, ".shotx");
        try
        {
            await _shotx.SaveAsync(doc, path, flattenedPreview, ct).ConfigureAwait(false);
            doc.FilePath = path;
            var row = await BuildRowAsync(path, null, doc.Metadata, doc, flattenedPreview, ct).ConfigureAwait(false);
            LibraryItem item;
            await _indexGate.WaitAsync(ct).ConfigureAwait(false);
            try { (item, _, _) = await UpsertAsync(row, ct).ConfigureAwait(false); }
            finally { _indexGate.Release(); }
            ScheduleMirror();
            Raise(LibraryChangeKind.Added, new[] { item.Id });
            return item;
        }
        finally { Release(path); }
    }

    public async Task<LibraryItem> AddVideoAsync(VideoCaptureResult video, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        EnsureReady();
        if (!File.Exists(video.Mp4Path)) throw new ShotwiseException(ErrorCode.FileNotFound, "The recording file does not exist.") { Detail = video.Mp4Path };
        var srcExt = Path.GetExtension(video.Mp4Path).ToLowerInvariant();
        var ext = video.IsGif ? ".gif" : (VideoExt.Contains(srcExt) ? srcExt : ".mp4");
        var inside = ToRel(video.Mp4Path) is { } r0 && !IsHiddenRel(r0);
        var dest = inside ? Path.GetFullPath(video.Mp4Path) : AllocatePath(video.Metadata.CaptureDate, video.Metadata.AppName, ext);
        try
        {
            if (!inside) await MoveFileAsync(video.Mp4Path, dest, ct).ConfigureAwait(false);
            await WriteSidecarAsync(dest, new MediaSidecar
            {
                Metadata = video.Metadata,
                DurationMs = (long)video.Duration.TotalMilliseconds,
                Width = (int)Math.Round(video.FrameSize.Width),
                Height = (int)Math.Round(video.FrameSize.Height),
            }, ct).ConfigureAwait(false);
            var row = await BuildRowAsync(dest, null, video.Metadata, null, null, ct).ConfigureAwait(false);
            LibraryItem item;
            await _indexGate.WaitAsync(ct).ConfigureAwait(false);
            try { (item, _, _) = await UpsertAsync(row, ct).ConfigureAwait(false); }
            finally { _indexGate.Release(); }
            ScheduleMirror();
            Raise(LibraryChangeKind.Added, new[] { item.Id });
            return item;
        }
        finally { Release(dest); }
    }

    private static async Task MoveFileAsync(string src, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try { File.Move(src, dest); }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
        {
            await using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var o = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await i.CopyToAsync(o, ct).ConfigureAwait(false);
            File.Delete(src);
        }
    }

    public async Task<LibraryItem> ImportFileAsync(string path, CancellationToken ct = default)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ShotwiseException(ErrorCode.FileNotFound, "The file does not exist.") { Detail = path };
        if (KindOf(path) is null)
            throw new ShotwiseException(ErrorCode.UnsupportedFormat, "This file type cannot be added to the library.") { Detail = path };
        var full = Path.GetFullPath(path);
        var rel = ToRel(full);
        string dest;
        var copied = false;
        if (rel is not null && !IsHiddenRel(rel)) dest = full;
        else
        {
            var fi = new FileInfo(full);
            var when = new DateTimeOffset(new DateTime(Math.Min(fi.CreationTimeUtc.Ticks, fi.LastWriteTimeUtc.Ticks), DateTimeKind.Utc));
            dest = AllocatePath(when, Path.GetFileNameWithoutExtension(full), fi.Extension.ToLowerInvariant());
            copied = true;
        }
        try
        {
            if (copied)
            {
                File.Copy(full, dest, overwrite: false);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(full));
                if (File.Exists(full + SidecarExtension)) File.Copy(full + SidecarExtension, dest + SidecarExtension, true);
            }
            var row = await BuildRowAsync(dest, null, null, null, null, ct).ConfigureAwait(false);
            LibraryItem item;
            bool isNew;
            await _indexGate.WaitAsync(ct).ConfigureAwait(false);
            try { (item, isNew, _) = await UpsertAsync(row, ct).ConfigureAwait(false); }
            finally { _indexGate.Release(); }
            ScheduleMirror();
            Raise(isNew ? LibraryChangeKind.Added : LibraryChangeKind.Updated, new[] { item.Id });
            return item;
        }
        finally { if (copied) Release(dest); }
    }

    public async Task UpdateAsync(LibraryItem item, ShotDocument? doc = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureReady();
        var current = await GetAsync(item.Id, ct).ConfigureAwait(false)
            ?? throw new ShotwiseException(ErrorCode.LibraryItemNotFound, "The library item no longer exists.") { Detail = item.Path };
        var path = current.Path;
        if (!File.Exists(path))
        {
            await _indexGate.WaitAsync(ct).ConfigureAwait(false);
            try { await RemoveRowsAsync(ToRel(path)!, false, ct).ConfigureAwait(false); }
            finally { _indexGate.Release(); }
            Raise(LibraryChangeKind.Removed, new[] { current.Id });
            throw new ShotwiseException(ErrorCode.FileNotFound, "The library file is missing.") { Detail = path };
        }
        var row = await BuildRowAsync(path, null, doc?.Metadata, doc, null, ct).ConfigureAwait(false);
        LibraryItem updated;
        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try { (updated, _, _) = await UpsertAsync(row, ct).ConfigureAwait(false); }
        finally { _indexGate.Release(); }
        ScheduleMirror();
        Raise(LibraryChangeKind.Updated, new[] { updated.Id });
    }

    // ------------------------------------------------------------------ queries

    private static IEnumerable<string> SearchTokens(string text)
        => Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value);

    public Task<IReadOnlyList<LibraryItem>> QueryAsync(LibraryQuery q, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);
        return Db.RunAsync<IReadOnlyList<LibraryItem>>(c =>
        {
            var where = new List<string>();
            using var cmd = c.CreateCommand();
            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                var toks = SearchTokens(q.Text).Select(t => "\"" + t + "\"*").ToList();
                if (toks.Count > 0)
                {
                    where.Add("i.rid IN (SELECT rowid FROM items_fts WHERE items_fts MATCH @fts)");
                    cmd.Parameters.AddWithValue("@fts", string.Join(' ', toks));
                }
            }
            if (q.Kind is { } k) { where.Add("i.kind=@kind"); cmd.Parameters.AddWithValue("@kind", (int)k); }
            if (q.App is { } app)
            {
                if (app.Length == 0) where.Add("(i.app IS NULL OR i.app='')");
                else { where.Add("i.app=@app COLLATE NOCASE"); cmd.Parameters.AddWithValue("@app", app); }
            }
            if (q.Website is { } site)
            {
                if (site.Length == 0) where.Add("(i.website IS NULL OR i.website='')");
                else { where.Add("i.website=@site COLLATE NOCASE"); cmd.Parameters.AddWithValue("@site", site); }
            }
            if (!string.IsNullOrWhiteSpace(q.Tag))
            {
                where.Add("EXISTS(SELECT 1 FROM tags t WHERE t.item_id=i.id AND t.tag=@tag)");
                cmd.Parameters.AddWithValue("@tag", q.Tag.Trim());
            }
            if (q.From is { } from)
            {
                where.Add("i.created >= @from");
                cmd.Parameters.AddWithValue("@from", ToMs(new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local))));
            }
            if (q.To is { } to)
            {
                where.Add("i.created < @to");
                cmd.Parameters.AddWithValue("@to", ToMs(new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local))));
            }
            if (q.FavoritesOnly) where.Add("i.is_fav=1");
            var order = q.Sort switch
            {
                LibrarySort.OldestFirst => "i.created ASC, i.rid ASC",
                LibrarySort.NameAscending => "i.name COLLATE NOCASE ASC, i.rid ASC",
                LibrarySort.NameDescending => "i.name COLLATE NOCASE DESC, i.rid DESC",
                LibrarySort.LargestFirst => "i.size DESC, i.rid DESC",
                LibrarySort.SmallestFirst => "i.size ASC, i.rid ASC",
                _ => "i.created DESC, i.rid DESC",
            };
            cmd.CommandText = $"SELECT {SelectCols} FROM items i" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                              $" ORDER BY {order} LIMIT @take OFFSET @skip";
            cmd.Parameters.AddWithValue("@take", q.Take <= 0 ? 200 : q.Take);
            cmd.Parameters.AddWithValue("@skip", Math.Max(0, q.Skip));
            var list = new List<LibraryItem>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(Map(rd));
            return list;
        }, ct);
    }

    public Task<LibraryItem?> GetAsync(Guid id, CancellationToken ct = default)
        => Db.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SelectCols} FROM items i WHERE i.id=@id";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? Map(rd) : null;
        }, ct);

    public Task<LibraryItem?> FindByPathAsync(string path, CancellationToken ct = default)
    {
        var rel = string.IsNullOrWhiteSpace(path) ? null : ToRel(path);
        if (rel is null) return Task.FromResult<LibraryItem?>(null);
        return Db.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SelectCols} FROM items i WHERE i.rel=@rel";
            cmd.Parameters.AddWithValue("@rel", rel);
            using var rd = cmd.ExecuteReader();
            return rd.Read() ? Map(rd) : null;
        }, ct);
    }

    public Task<IReadOnlyList<(string Key, int Count)>> GroupCountsAsync(LibraryGroupBy by, CancellationToken ct = default)
        => Db.RunAsync<IReadOnlyList<(string Key, int Count)>>(c =>
        {
            var sql = by switch
            {
                LibraryGroupBy.Date => "SELECT strftime('%Y-%m-%d', created/1000, 'unixepoch', 'localtime') k, COUNT(*) n FROM items GROUP BY k ORDER BY k DESC",
                LibraryGroupBy.Year => "SELECT strftime('%Y', created/1000, 'unixepoch', 'localtime') k, COUNT(*) n FROM items GROUP BY k ORDER BY k DESC",
                LibraryGroupBy.Month => "SELECT strftime('%Y-%m', created/1000, 'unixepoch', 'localtime') k, COUNT(*) n FROM items GROUP BY k ORDER BY k DESC",
                LibraryGroupBy.App => "SELECT COALESCE(app,'') k, COUNT(*) n FROM items GROUP BY k COLLATE NOCASE ORDER BY n DESC, k COLLATE NOCASE",
                LibraryGroupBy.Website => "SELECT COALESCE(website,'') k, COUNT(*) n FROM items GROUP BY k COLLATE NOCASE ORDER BY n DESC, k COLLATE NOCASE",
                LibraryGroupBy.Tag => "SELECT tag k, COUNT(*) n FROM tags GROUP BY tag ORDER BY n DESC, k COLLATE NOCASE",
                _ => "SELECT kind k, COUNT(*) n FROM items GROUP BY kind ORDER BY kind",
            };
            var list = new List<(string, int)>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var key = by == LibraryGroupBy.Kind ? ((MediaKind)rd.GetInt32(0)).ToString() : rd.GetString(0);
                list.Add((key, rd.GetInt32(1)));
            }
            return list;
        }, ct);

    // ------------------------------------------------------------------ tags / favourites / OCR

    private Task<List<Guid>> MutateAsync(IEnumerable<Guid> ids, Action<SqliteConnection, SqliteTransaction, long, string> perItem)
    {
        var idList = ids.Distinct().ToList();
        return Db.RunAsync(c =>
        {
            var changed = new List<Guid>();
            using var tx = c.BeginTransaction();
            foreach (var id in idList)
            {
                var ids2 = id.ToString("D");
                var rid = LibraryDatabase.Scalar(c, "SELECT rid FROM items WHERE id=@i", tx, ("@i", ids2));
                if (rid is null) continue;
                perItem(c, tx, Convert.ToInt64(rid, CultureInfo.InvariantCulture), ids2);
                FtsRefresh(c, tx, Convert.ToInt64(rid, CultureInfo.InvariantCulture));
                changed.Add(id);
            }
            tx.Commit();
            return changed;
        });
    }

    public async Task AddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags)
    {
        var norm = NormalizeTags(tags);
        if (norm.Count == 0) return;
        var changed = await MutateAsync(ids, (c, tx, rid, id) =>
        {
            foreach (var t in norm) LibraryDatabase.Exec(c, "INSERT OR IGNORE INTO tags(item_id,tag) VALUES(@i,@t)", tx, ("@i", id), ("@t", t));
        }).ConfigureAwait(false);
        ScheduleMirror();
        Raise(LibraryChangeKind.Updated, changed);
    }

    public async Task RemoveTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags)
    {
        var norm = NormalizeTags(tags);
        if (norm.Count == 0) return;
        var changed = await MutateAsync(ids, (c, tx, rid, id) =>
        {
            foreach (var t in norm) LibraryDatabase.Exec(c, "DELETE FROM tags WHERE item_id=@i AND tag=@t", tx, ("@i", id), ("@t", t));
        }).ConfigureAwait(false);
        ScheduleMirror();
        Raise(LibraryChangeKind.Updated, changed);
    }

    public Task<IReadOnlyList<string>> AllTagsAsync()
        => Db.RunAsync<IReadOnlyList<string>>(c =>
        {
            var l = new List<string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT tag FROM tags ORDER BY tag COLLATE NOCASE";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) l.Add(rd.GetString(0));
            return l;
        });

    public async Task SetFavoriteAsync(Guid id, bool fav)
    {
        var changed = await MutateAsync(new[] { id }, (c, tx, rid, sid) =>
            LibraryDatabase.Exec(c, "UPDATE items SET is_fav=@f WHERE rid=@r", tx, ("@f", fav ? 1 : 0), ("@r", rid))).ConfigureAwait(false);
        ScheduleMirror();
        Raise(LibraryChangeKind.Updated, changed);
    }

    public async Task SetOcrTextAsync(Guid id, string text)
    {
        var changed = await MutateAsync(new[] { id }, (c, tx, rid, sid) =>
            LibraryDatabase.Exec(c, "UPDATE items SET ocr=@o WHERE rid=@r", tx, ("@o", text), ("@r", rid))).ConfigureAwait(false);
        ScheduleMirror();
        Raise(LibraryChangeKind.Updated, changed);
    }

    // ------------------------------------------------------------------ share history

    public async Task AddShareHistoryAsync(Guid itemId, ShareHistoryEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        await Db.RunAsync(c => LibraryDatabase.Exec(c,
            "INSERT INTO share_history(item_id,at,dest_id,dest_name,link,title) VALUES(@i,@at,@d,@n,@l,@t)", null,
            ("@i", itemId == Guid.Empty ? null : itemId.ToString("D")), ("@at", ToMs(e.At)), ("@d", e.DestinationId), ("@n", e.DestinationName),
            ("@l", e.Link), ("@t", e.Title))).ConfigureAwait(false);
        ScheduleMirror();
        if (itemId != Guid.Empty) Raise(LibraryChangeKind.Updated, new[] { itemId });
    }

    public Task<IReadOnlyList<ShareHistoryEntry>> GetShareHistoryAsync(Guid? itemId = null)
        => Db.RunAsync<IReadOnlyList<ShareHistoryEntry>>(c =>
        {
            var l = new List<ShareHistoryEntry>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT at,dest_id,dest_name,link,title FROM share_history" + (itemId is null ? "" : " WHERE item_id=@i") + " ORDER BY at DESC, id DESC";
            if (itemId is { } g) cmd.Parameters.AddWithValue("@i", g.ToString("D"));
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                l.Add(new ShareHistoryEntry(FromMs(rd.GetInt64(0)), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4)));
            return l;
        });

    // ------------------------------------------------------------------ delete / rename

    public async Task DeleteAsync(IEnumerable<Guid> ids, bool toRecycleBin = true)
    {
        EnsureReady();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return;
        await _indexGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await Db.RunAsync(c =>
            {
                var l = new List<(Guid Id, string Rel)>();
                foreach (var id in idList)
                    if (LibraryDatabase.Scalar(c, "SELECT rel FROM items WHERE id=@i", null, ("@i", id.ToString("D"))) is string rel) l.Add((id, rel));
                return l;
            }).ConfigureAwait(false);
            var files = new List<string>();
            foreach (var (_, rel) in entries)
            {
                var f = ToFull(rel);
                files.Add(f);
                if (File.Exists(f + SidecarExtension)) files.Add(f + SidecarExtension);
            }
            int rc = 0;
            if (toRecycleBin) rc = await Task.Run(() => RecycleBin.Delete(files)).ConfigureAwait(false);
            else
                foreach (var f in files)
                {
                    try { File.Delete(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not delete '{Path}'.", f); }
                }
            var removed = new List<Guid>();
            var failed = 0;
            foreach (var (id, rel) in entries)
            {
                if (File.Exists(ToFull(rel))) { failed++; continue; }
                await RemoveRowsAsync(rel, false, CancellationToken.None).ConfigureAwait(false);
                removed.Add(id);
            }
            if (removed.Count > 0) ScheduleMirror();
            Raise(LibraryChangeKind.Removed, removed);
            if (failed > 0)
                throw new ShotwiseException(rc == 1223 ? ErrorCode.Cancelled : ErrorCode.AccessDenied,
                    $"{failed} file(s) could not be deleted (code {rc}).");
        }
        finally { _indexGate.Release(); }
    }

    public async Task RenameAsync(Guid id, string newName)
    {
        EnsureReady();
        var clean = Sanitize(Path.GetFileNameWithoutExtension(newName) is { Length: > 0 } n && newName.Contains('.') &&
                             KindOf(newName) is not null ? n : newName, 120);
        if (clean.Length == 0) throw new ShotwiseException(ErrorCode.InvalidArgument, "The name is empty or invalid.");
        await _indexGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var rel = await Db.RunAsync(c => LibraryDatabase.Scalar(c, "SELECT rel FROM items WHERE id=@i", null, ("@i", id.ToString("D"))) as string).ConfigureAwait(false)
                ?? throw new ShotwiseException(ErrorCode.LibraryItemNotFound, "The library item no longer exists.");
            var oldFull = ToFull(rel);
            var ext = Path.GetExtension(oldFull);
            var dir = Path.GetDirectoryName(oldFull)!;
            var target = Path.Combine(dir, clean + ext);
            if (!string.Equals(target, oldFull, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{clean} ({i}){ext}");
                File.Move(oldFull, target);
                if (File.Exists(oldFull + SidecarExtension)) File.Move(oldFull + SidecarExtension, target + SidecarExtension, true);
            }
            else if (!string.Equals(target, oldFull, StringComparison.Ordinal)) File.Move(oldFull, target); // case-only rename
            var newRel = ToRel(target)!;
            var finalName = Path.GetFileNameWithoutExtension(target);
            await Db.RunAsync(c =>
            {
                using var tx = c.BeginTransaction();
                LibraryDatabase.Exec(c, "UPDATE items SET rel=@rel,name=@n WHERE id=@i", tx, ("@rel", newRel), ("@n", finalName), ("@i", id.ToString("D")));
                var rid = Convert.ToInt64(LibraryDatabase.Scalar(c, "SELECT rid FROM items WHERE id=@i", tx, ("@i", id.ToString("D"))), CultureInfo.InvariantCulture);
                FtsRefresh(c, tx, rid);
                tx.Commit();
            }).ConfigureAwait(false);
            ScheduleMirror();
            Raise(LibraryChangeKind.Updated, new[] { id });
        }
        finally { _indexGate.Release(); }
    }
}
