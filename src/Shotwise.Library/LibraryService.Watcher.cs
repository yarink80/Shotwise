using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Library;

namespace Shotwise.Library;

public sealed partial class LibraryService
{
    private FileSystemWatcher? _watcher;
    private Timer? _watchTimer;
    private readonly object _pendLock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _retries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _procGate = new(1, 1);
    private int _needFull;
    private const int MaxRetries = 6;

    /// <summary>Quiet period after the last file-system event before the library is reconciled.</summary>
    internal int DebounceMs { get; set; } = 750;

    /// <summary>Set false (tests/maintenance) to skip creating the FileSystemWatcher on initialize.</summary>
    internal bool WatcherEnabled { get; set; } = true;

    private void StartWatcher()
    {
        StopWatcher();
        if (!WatcherEnabled || !Directory.Exists(_root)) return;
        try
        {
            var w = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnFsEvent(e.FullPath);
            w.Changed += (_, e) => OnFsEvent(e.FullPath);
            w.Deleted += (_, e) => OnFsEvent(e.FullPath);
            w.Renamed += (_, e) => { OnFsEvent(e.OldFullPath); OnFsEvent(e.FullPath); };
            w.Error += (_, e) =>
            {
                _log.LogWarning(e.GetException(), "File watcher overflow/error; scheduling full reconcile.");
                Interlocked.Exchange(ref _needFull, 1);
                Arm();
            };
            _watchTimer ??= new Timer(_ => { _ = Task.Run(ProcessPendingSafeAsync); }, null, Timeout.Infinite, Timeout.Infinite);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _log.LogWarning(ex, "Could not start file watcher on '{Root}'.", _root);
        }
    }

    private void StopWatcher()
    {
        var w = _watcher;
        _watcher = null;
        if (w is not null) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_pendLock) { _pending.Clear(); _retries.Clear(); }
    }

    private void OnFsEvent(string fullPath)
    {
        var rel = ToRel(fullPath);
        if (rel is null) return;
        var first = rel.Split('/')[0];
        if (first.StartsWith('.')) return; // .shotwise state and temp files
        lock (_pendLock) _pending.Add(rel);
        Arm();
    }

    private void Arm() { if (!_disposed) _watchTimer?.Change(DebounceMs, Timeout.Infinite); }

    private async Task ProcessPendingSafeAsync()
    {
        try { await ProcessPendingAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Watcher reconcile failed."); }
    }

    /// <summary>Processes queued file-system events now (also used by tests to avoid timing dependence).</summary>
    internal async Task ProcessPendingAsync(CancellationToken ct)
    {
        if (_db is null) return;
        await _procGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _needFull, 0) == 1)
            {
                await ReconcileAsync(null, ct).ConfigureAwait(false);
                return;
            }
            List<string> batch;
            lock (_pendLock) { batch = _pending.ToList(); _pending.Clear(); }
            if (batch.Count == 0) return;

            var added = new List<Guid>();
            var updated = new List<Guid>();
            var removed = new List<Guid>();
            var retry = new List<string>();
            await _indexGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var rel in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var full = ToFull(rel);
                    try
                    {
                        if (Directory.Exists(full))
                        {
                            foreach (var f in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                            {
                                var (item, isNew) = await IndexFileIfChangedAsync(f, null, ct).ConfigureAwait(false);
                                if (item is not null) (isNew ? added : updated).Add(item.Id);
                            }
                        }
                        else if (File.Exists(full))
                        {
                            if (!IsTracked(rel)) continue;
                            var (item, isNew) = await IndexFileIfChangedAsync(full, null, ct).ConfigureAwait(false);
                            if (item is not null) (isNew ? added : updated).Add(item.Id);
                        }
                        else
                        {
                            removed.AddRange(await RemoveRowsAsync(rel, true, ct).ConfigureAwait(false));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ShotwiseException)
                    {
                        // File likely still being written / locked: try again shortly.
                        lock (_pendLock)
                        {
                            _retries.TryGetValue(rel, out var n);
                            if (n < MaxRetries) { _retries[rel] = n + 1; retry.Add(rel); }
                            else _log.LogWarning(ex, "Giving up indexing '{Path}'.", full);
                        }
                    }
                }
            }
            finally { _indexGate.Release(); }
            if (retry.Count > 0)
            {
                lock (_pendLock) foreach (var r in retry) _pending.Add(r);
                _watchTimer?.Change(Math.Max(DebounceMs, 500), Timeout.Infinite);
            }
            if (added.Count + updated.Count + removed.Count > 0) ScheduleMirror();
            Raise(LibraryChangeKind.Added, added);
            Raise(LibraryChangeKind.Updated, updated);
            Raise(LibraryChangeKind.Removed, removed);
        }
        finally { _procGate.Release(); }
    }
}
