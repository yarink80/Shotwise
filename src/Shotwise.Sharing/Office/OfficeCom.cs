using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Shotwise.Core;

namespace Shotwise.Sharing.Office;

/// <summary>
/// All Office automation runs on one dedicated STA thread (COM apartment rules; never touches the UI thread).
/// Office is late-bound (<c>dynamic</c>): no interop assemblies, works with any installed version.
/// </summary>
internal sealed class OfficeStaThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public OfficeStaThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Shotwise.Office.STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { tcs.SetCanceled(ct); return tcs.Task; }
        try
        {
            _queue.Add(() =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { tcs.TrySetException(new ObjectDisposedException(nameof(OfficeStaThread))); }
        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default) => RunAsync<object?>(() => { work(); return null; }, ct);

    public void Dispose() => _queue.CompleteAdding();
}

internal static class OfficeCom
{
    private static readonly Lazy<OfficeStaThread> Thread_ = new(() => new OfficeStaThread());
    public static OfficeStaThread Sta => Thread_.Value;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    /// <summary>The running instance of the application, or null when it is not running.</summary>
    public static object? TryGetRunning(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    /// <summary>Attaches to the running instance or (when allowed) starts a new one. Throws OfficeNotInstalled when not registered.</summary>
    public static object GetApplication(string progId, bool createIfMissing, string friendlyName, params string[] fallbackProgIds)
    {
        foreach (var id in new[] { progId }.Concat(fallbackProgIds))
        {
            var running = TryGetRunning(id);
            if (running is not null) return running;
        }
        if (!createIfMissing)
            throw new ShotwiseException(ErrorCode.DestinationUnavailable, $"{friendlyName} is not running.");
        foreach (var id in new[] { progId }.Concat(fallbackProgIds))
        {
            var t = Type.GetTypeFromProgID(id);
            if (t is null) continue;
            try { return Activator.CreateInstance(t)!; }
            catch (COMException ex) { throw new ShotwiseException(ErrorCode.ShareFailed, $"Could not start {friendlyName}: {ex.Message}", ex); }
        }
        throw new ShotwiseException(ErrorCode.OfficeNotInstalled, $"{friendlyName} is not installed.");
    }

    public static bool IsRegistered(params string[] progIds) => progIds.Any(id => CLSIDFromProgID(id, out _) == 0);

    public static ShotwiseException Wrap(COMException ex, string friendlyName)
        => new(ErrorCode.ShareFailed, $"{friendlyName} reported an error: {ex.Message}. Close any open dialogs in {friendlyName} and try again.", ex);

    /// <summary>Persistent copy of a (temp) file so linked pictures keep resolving after temp cleanup.</summary>
    public static string PersistForLinking(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Naming.NamingEngine.UniquePath(folder, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        File.Copy(path, target);
        return target;
    }
}
