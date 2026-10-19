using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Library.Catalog;

namespace Shotwise.Library;

public sealed partial class LibraryService
{
    /// <summary>
    /// Moves (or merges into) another folder: every file is copied first (progress 0..0.95), the catalog is switched in one
    /// transaction, and only then are the old files removed. Any failure or cancellation before the switch deletes
    /// everything copied so far and leaves the library untouched.
    /// </summary>
    public async Task MoveLibraryAsync(string newRoot, IProgress<double>? p, CancellationToken ct)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(newRoot)) throw new ShotwiseException(ErrorCode.LibraryLocationInvalid, "No folder given.");
        var target = NormalizeRoot(newRoot);
        var oldRoot = _root;
        if (string.Equals(target, oldRoot, StringComparison.OrdinalIgnoreCase)) return;
        var sep = Path.DirectorySeparatorChar;
        if (target.StartsWith(oldRoot + sep, StringComparison.OrdinalIgnoreCase) || oldRoot.StartsWith(target + sep, StringComparison.OrdinalIgnoreCase))
            throw new ShotwiseException(ErrorCode.LibraryLocationInvalid, "The new library folder must not contain or be inside the current one.") { Detail = target };

        var createdDirs = new List<string>();
        var createdFiles = new List<string>();
        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        StopWatcher();
        var switched = false;
        try
        {
            try
            {
                var probeDir = target;
                if (!Directory.Exists(probeDir)) { Directory.CreateDirectory(probeDir); createdDirs.Add(probeDir); }
                var probe = Path.Combine(probeDir, ".probe-" + Guid.NewGuid().ToString("N")[..8]);
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ShotwiseException(ErrorCode.LibraryLocationInvalid, "The new folder is not writable.", ex) { Detail = target };
            }

            var all = Directory.EnumerateFiles(oldRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => !Path.GetFileName(f).StartsWith('.') || f.Contains(Path.DirectorySeparatorChar + StateFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToList();
            long total = Math.Max(1L, all.Sum(f => new FileInfo(f).Length));
            long done = 0;
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Primary media first so a renamed primary can drag its sidecar along.
            foreach (var src in all.OrderBy(f => f.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                ct.ThrowIfCancellationRequested();
                var rel = ToRel(src)!;
                string destRel = rel;
                if (rel.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
                {
                    var primary = rel[..^SidecarExtension.Length];
                    if (remap.TryGetValue(primary, out var np)) destRel = np + SidecarExtension;
                }
                var dest = Path.Combine(target, destRel.Replace('/', sep));
                var srcInfo = new FileInfo(src);
                if (File.Exists(dest))
                {
                    var di = new FileInfo(dest);
                    if (di.Length == srcInfo.Length && await FilesEqualAsync(src, dest, ct).ConfigureAwait(false))
                    { destinations[src] = dest; done += srcInfo.Length; p?.Report(0.95 * done / total); continue; }
                    if (!destRel.StartsWith(StateFolderName + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = Path.GetDirectoryName(dest)!;
                        var stem = Path.GetFileNameWithoutExtension(dest);
                        var ext = Path.GetExtension(dest);
                        for (var i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stem} (moved {i}){ext}");
                        destRel = Path.GetRelativePath(target, dest).Replace(sep, '/');
                    }
                    else { done += srcInfo.Length; continue; } // keep target's own mirror; it is rebuilt after the merge
                }
                if (!string.Equals(destRel, rel, StringComparison.OrdinalIgnoreCase)) remap[rel] = destRel;
                var destDir = Path.GetDirectoryName(dest)!;
                var missing = new Stack<string>();
                for (var d = destDir; !Directory.Exists(d); d = Path.GetDirectoryName(d)!) missing.Push(d);
                Directory.CreateDirectory(destDir);
                foreach (var d in missing) createdDirs.Add(d);
                createdFiles.Add(dest); // tracked before copying so a partial file is removed on rollback
                await CopyFileAsync(src, dest, ct, n => { done += n; p?.Report(0.95 * Math.Min(1.0, (double)done / total)); }).ConfigureAwait(false);
                if (!await FilesEqualAsync(src, dest, ct).ConfigureAwait(false))
                    throw new IOException("Source changed during library move.");
                destinations[src] = dest;
            }

            // Switch: remap conflicting rels in one transaction, then flip the root.
            await Db.RunAsync(c =>
            {
                using var tx = c.BeginTransaction();
                foreach (var (o, n) in remap)
                    LibraryDatabase.Exec(c, "UPDATE items SET rel=@n WHERE rel=@o", tx, ("@n", n), ("@o", o));
                tx.Commit();
            }, ct).ConfigureAwait(false);
            _root = target;
            switched = true;
            if (_settings is not null) _settings.Update(s => s.Library.LibraryRoot = target);
            lock (_nameLock) _reservedNames.Clear();

            // Merge: index anything that was already in the target folder (uses the target's mirror for tags etc.).
            try
            {
                var mirror = await ReadMirrorAsync(target, CancellationToken.None).ConfigureAwait(false);
                await ReconcileCoreAsync(mirror?.Items.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Post-move reconcile failed; it will be retried by the watcher/next start."); }

            // Remove the old copies (best effort) and empty folders.
            foreach (var src in all)
            {
                try
                {
                    if (destinations.TryGetValue(src, out var destination) && await FilesEqualAsync(src, destination, CancellationToken.None).ConfigureAwait(false))
                        File.Delete(src);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not remove old file '{Path}'.", src); }
            }
            foreach (var d in Directory.EnumerateDirectories(oldRoot, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { if (!Directory.EnumerateFileSystemEntries(oldRoot).Any()) Directory.Delete(oldRoot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            p?.Report(1.0);
            ScheduleMirror();
        }
        catch (Exception ex)
        {
            if (!switched)
            {
                foreach (var f in createdFiles) TryDelete(f);
                foreach (var d in Enumerable.Reverse(createdDirs))
                    try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            StartWatcher();
            if (ex is OperationCanceledException || ex is ShotwiseException) throw;
            throw new ShotwiseException(ErrorCode.LibraryMoveFailed, "The library could not be moved: " + ex.Message, ex) { Detail = target };
        }
        finally
        {
            _indexGate.Release();
        }
        StartWatcher();
        Raise(LibraryChangeKind.Reset, Array.Empty<Guid>());
    }

    private static async Task CopyFileAsync(string src, string dest, CancellationToken ct, Action<int> onBytes)
    {
        await using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        await using var o = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buf = new byte[81920];
        int n;
        while ((n = await i.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await o.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            onBytes(n);
        }
        await o.FlushAsync(ct).ConfigureAwait(false);
        o.Close();
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
    }

    private static async Task<bool> FilesEqualAsync(string source, string target, CancellationToken ct)
    {
        await using var a = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var b = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return await StreamsEqualAsync(a, b, ct).ConfigureAwait(false);
    }

    private static async Task<bool> StreamsEqualAsync(Stream a, Stream b, CancellationToken ct)
    {
        var ah = await System.Security.Cryptography.SHA256.HashDataAsync(a, ct).ConfigureAwait(false);
        var bh = await System.Security.Cryptography.SHA256.HashDataAsync(b, ct).ConfigureAwait(false);
        return ah.AsSpan().SequenceEqual(bh);
    }
}
