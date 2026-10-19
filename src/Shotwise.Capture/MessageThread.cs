using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Shotwise.Capture;

/// <summary>Owns hooks and windows on one MTA thread. A hidden top-level window also receives display broadcasts.</summary>
internal sealed class MessageThread : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<IntPtr> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<(Action Execute, Action<Exception> Fail)> work = new();
    private readonly object lifecycle = new();
    private readonly ILogger logger;
    private readonly bool receiveBroadcasts;
    private int disposed;
    internal event Action<uint>? Message;
    internal IntPtr Hwnd => ready.Task.GetAwaiter().GetResult();
    internal MessageThread(ILogger logger, bool receiveBroadcasts = false)
    {
        this.logger = logger; this.receiveBroadcasts = receiveBroadcasts;
        thread = new Thread(Run) { IsBackground = true, Name = "Shotwise capture messages" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    internal Task<T> Invoke<T>(Func<T> action, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var hwnd = Hwnd;
            work.Enqueue((() => { if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; } try { done.TrySetResult(action()); } catch (Exception ex) { done.TrySetException(ex); } }, ex => done.TrySetException(ex)));
            if (!Native.PostMessage(hwnd, 0x8001, 0, 0)) done.TrySetException(new Win32Exception());
        }
        // Once a native mutation starts, preserve its result so callers can release installed hooks/handles.
        return done.Task;
    }
    private void Run()
    {
        using var dpi = new DpiScope();
        var name = "ShotwiseCapture_" + Guid.NewGuid().ToString("N");
        Native.WndProc proc = (hwnd, msg, wp, lp) =>
        {
            try
            {
                if (msg == 0x8001) { while (work.TryDequeue(out var a)) a.Execute(); return 0; }
                if (msg == 0x10) { Native.DestroyWindow(hwnd); return 0; }
                if (msg == 2) { Native.PostQuitMessage(0); return 0; }
                Message?.Invoke(msg);
            }
            catch (Exception ex) { logger.LogError(ex, "Capture message handler failed"); }
            return Native.DefWindowProc(hwnd, msg, wp, lp);
        };
        var cls = new Native.WindowClass { Name = name, Proc = proc };
        IntPtr window = IntPtr.Zero;
        try
        {
            if (Native.RegisterClass(ref cls) == 0) throw new Win32Exception();
            window = Native.CreateWindowEx(0x80, name, "", 0, 0, 0, 0, 0, receiveBroadcasts ? IntPtr.Zero : new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception();
            ready.TrySetResult(window);
            int status;
            while ((status = Native.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
            if (status < 0) logger.LogError("Capture GetMessage failed");
        }
        catch (Exception ex) { ready.TrySetException(ex); logger.LogError(ex, "Capture message thread failed"); }
        finally
        {
            if (window != IntPtr.Zero) Native.DestroyWindow(window);
            Native.UnregisterClass(name, IntPtr.Zero);
            while (work.TryDequeue(out var a)) a.Fail(new ObjectDisposedException(nameof(MessageThread)));
            GC.KeepAlive(proc);
        }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed != 0) return;
            disposed = 1;
            try { Native.PostMessage(Hwnd, 0x10, 0, 0); } catch (Exception ex) { logger.LogDebug(ex, "Capture window did not initialize"); }
        }
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(5));
    }
}
