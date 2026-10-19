using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Recording;

internal sealed class CursorEffects : IFrameOverlay, IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public DesktopNative.Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public DesktopNative.Point Point; }
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nuint w, nint l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
    private readonly ConcurrentQueue<(Point2 Position, long At)> _incoming = new();
    private readonly List<(Point2 Position, long At)> _ripples = new();
    private readonly RecordingOptions _options;
    private readonly Thread? _thread;
    private readonly HookProc _callback;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private nint _hook;
    private readonly ILogger _logger;
    private int _disposed;
    public CursorEffects(RecordingOptions options, ILogger logger)
    {
        _options = options; _logger = logger; _callback = Hook;
        if (options.ClickAnimation)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Shotwise cursor hook" }; _thread.Start();
            _ready.Wait();
        }
    }
    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId(); PeekMessageW(out _, 0, 0, 0, 0);
            _hook = SetWindowsHookExW(14, _callback, GetModuleHandleW(null), 0);
            if (_hook == 0) _logger.LogWarning("Mouse hook unavailable: {Error}", Marshal.GetLastWin32Error());
            _ready.Set();
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
        }
        catch (Exception e) { _logger.LogError(e, "Mouse hook thread failed"); }
        finally { if (_hook != 0) UnhookWindowsHookEx(_hook); _ready.Set(); }
    }
    private nint Hook(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x201 or 0x204 or 0x207)
        {
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            if (_incoming.Count < 64) _incoming.Enqueue((new Point2(mouse.Point.X, mouse.Point.Y), Stopwatch.GetTimestamp()));
        }
        return CallNextHookEx(_hook, code, message, data);
    }
    public void Render(IOverlayCanvas canvas, TimeSpan timestamp, RectI capturePx)
    {
        var sx = canvas.FrameSize.Width / (double)capturePx.Width; var sy = canvas.FrameSize.Height / (double)capturePx.Height;
        Point2 Map(Point2 p) => new((p.X - capturePx.X) * sx, (p.Y - capturePx.Y) * sy);
        var cursor = new DesktopNative.CursorInfo { Size = Marshal.SizeOf<DesktopNative.CursorInfo>() };
        if (_options.CursorHighlight && DesktopNative.GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0)
        {
            var p = Map(new Point2(cursor.Position.X, cursor.Position.Y));
            canvas.DrawEllipse(new Rect2(p.X - 20, p.Y - 20, 40, 40), null, 0, _options.CursorHighlightColor);
        }
        while (_incoming.TryDequeue(out var ripple)) _ripples.Add(ripple);
        var now = Stopwatch.GetTimestamp();
        _ripples.RemoveAll(r => Stopwatch.GetElapsedTime(r.At, now).TotalSeconds > 0.6);
        foreach (var ripple in _ripples)
        {
            var age = Stopwatch.GetElapsedTime(ripple.At, now).TotalSeconds / 0.6;
            var p = Map(ripple.Position); var radius = 8 + age * 30;
            var c = _options.ClickColor;
            canvas.DrawEllipse(new Rect2(p.X - radius, p.Y - radius, radius * 2, radius * 2), Argb.FromArgb((byte)(c.A * (1 - age)), c.R, c.G, c.B), 3, null);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_thread != null) { PostThreadMessageW(_threadId, 0x12, 0, 0); _thread.Join(); }
        _ready.Dispose();
    }
}
