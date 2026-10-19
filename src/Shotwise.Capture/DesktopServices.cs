using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Vortice.DXGI;

namespace Shotwise.Capture;

public sealed class MonitorService : IMonitorService, IDisposable
{
    private readonly MessageThread messages;
    private readonly ILogger<MonitorService> logger;
    public event EventHandler? DisplaysChanged;
    public MonitorService(ILogger<MonitorService> logger)
    {
        this.logger = logger;
        messages = new(logger, receiveBroadcasts: true);
        messages.Message += msg => { if (msg == 0x7e) DisplaysChanged?.Invoke(this, EventArgs.Empty); };
    }
    public RectI VirtualScreenPx => GetMonitors().Select(m => m.BoundsPx).Aggregate(RectI.Empty, (a, b) => a.Union(b));
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        using var dpi = new DpiScope();
        var hdr = new Dictionary<IntPtr, bool>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
            using (adapter)
            for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
            using (output)
            {
                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                hdr[output.Description.Monitor] = output6?.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "DXGI monitor enumeration unavailable; using display monitors"); }
        var whiteLevels = DisplayWhiteLevel.Read();
        var result = new List<MonitorInfo>();
        Native.EnumMonitor callback = (IntPtr monitor, IntPtr dc, ref Native.Rect rect, IntPtr data) =>
        {
            var info = new Native.Monitor { Size = Marshal.SizeOf<Native.Monitor>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                var scale = Native.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x / 96.0 : 1;
                result.Add(new(info.Device, info.Bounds.Pixels, info.Work.Pixels, scale, (info.Flags & 1) != 0, hdr.GetValueOrDefault(monitor), whiteLevels.GetValueOrDefault(info.Device, 80), monitor));
            }
            return true;
        };
        Native.Check(Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero));
        return result.AsReadOnly();
    }
    public void Dispose() => messages.Dispose();
}

public sealed class WindowFinder : IWindowFinder
{
    internal static WindowInfo? Read(IntPtr hwnd, bool filter = true)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (filter && (!Native.IsWindowVisible(hwnd) || pid == Environment.ProcessId || ((long)Native.GetWindowLongPtr(hwnd, -20) & 0x80) != 0)) return null;
        if (filter && Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return null;
        if (!Native.GetWindowRect(hwnd, out var bounds)) return null;
        if (Native.DwmGetWindowAttribute(hwnd, 9, out Native.Rect extended, 16) == 0) bounds = extended;
        if (bounds.Pixels.IsEmpty) return null;
        Native.GetClientRect(hwnd, out var client);
        var origin = new Native.Point(); Native.ClientToScreen(hwnd, ref origin);
        string process = "";
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; } catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { }
        return new(hwnd, Native.Title(hwnd), process, (int)pid, bounds.Pixels, new(origin.X, origin.Y, client.Right, client.Bottom), Native.IsIconic(hwnd));
    }
    public IReadOnlyList<WindowInfo> EnumerateTopLevel()
    {
        using var dpi = new DpiScope();
        var result = new List<WindowInfo>();
        Native.EnumWindows((h, _) => { if (Read(h) is { } w) result.Add(w); return true; }, IntPtr.Zero);
        return result.AsReadOnly();
    }
    public WindowInfo? WindowAt(PointI screenPx, IEnumerable<IntPtr> ignore)
    {
        using var dpi = new DpiScope();
        var excluded = ignore.ToHashSet();
        foreach (var window in EnumerateTopLevel())
        {
            if (excluded.Contains(window.Hwnd) || window.IsMinimized || !window.BoundsPx.Contains(screenPx)) continue;
            WindowInfo found = window;
            Native.EnumChildWindows(window.Hwnd, (h, _) =>
            {
                if (!excluded.Contains(h) && Native.IsWindowVisible(h) && Read(h, false) is { } child && child.BoundsPx.Contains(screenPx) && child.BoundsPx.Width * (long)child.BoundsPx.Height < found.BoundsPx.Width * (long)found.BoundsPx.Height) found = child;
                return true;
            }, IntPtr.Zero);
            return found;
        }
        return null;
    }
    public WindowInfo? Foreground() { using var dpi = new DpiScope(); return Read(Native.GetForegroundWindow()); }
}

internal sealed class DibSurface : IDisposable
{
    internal IntPtr Dc { get; }
    internal IntPtr Bits { get; }
    private readonly IntPtr bitmap, previous;
    private readonly int width, height;
    /// <param name="clear">Zero the bits first. Callers that overwrite every pixel (a full-surface BitBlt) pass false.</param>
    internal unsafe DibSurface(int width, int height, bool clear = true)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Math.Min(BgraImage.MaxPixels, int.MaxValue / 4)) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height;
        Dc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (Dc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var info = new Native.BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        bitmap = Native.CreateDIBSection(Dc, ref info, 0, out var bits, IntPtr.Zero, 0); Bits = bits;
        if (bitmap == IntPtr.Zero) { var error = Marshal.GetLastPInvokeError(); Native.DeleteDC(Dc); throw new System.ComponentModel.Win32Exception(error); }
        if (clear) new Span<byte>((void*)Bits, checked(width * height * 4)).Clear();
        previous = Native.SelectObject(Dc, bitmap);
    }
    internal unsafe BgraImage Read(bool opaque = true)
    {
        Native.GdiFlush();
        var result = new BgraImage(width, height);
        var source = new ReadOnlySpan<byte>((void*)Bits, result.Pixels.Length);
        if (opaque) CaptureMath.CopyOpaque(source, result.Pixels);
        else source.CopyTo(result.Pixels);
        return result;
    }
    public void Dispose() { Native.SelectObject(Dc, previous); Native.DeleteObject(bitmap); Native.DeleteDC(Dc); }
}

public sealed class ScreenCaptureService(IMonitorService monitors, ILogger<ScreenCaptureService> logger) : IScreenCaptureService
{
    private sealed record CursorPlanes(BgraImage Black, BgraImage White);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CursorSnapshot, CursorPlanes> cursorPlanes = new();
    /// <summary>
    /// Freezes every monitor. Monitors are captured in parallel (one worker per monitor, each with its own PerMonitorV2 scope).
    /// SDR monitors use GDI BitBlt first: measured on a 3-monitor host (4K + two portrait 1440p), the per-snapshot desktop duplication
    /// (new device + duplication each time) timed out waiting for a frame (DXGI_ERROR_WAIT_TIMEOUT after 150 ms) on the 4K output, and
    /// rotated outputs always ended in GDI anyway, so duplication-first cost ~330 ms of a ~450 ms snapshot; parallel GDI takes ~100-130 ms.
    /// A fresh duplication only delivers a frame on the next desktop update, so the wait is inherent on a static desktop. Duplication
    /// stays as the fallback when GDI fails; HDR monitors with tone mapping use the WGC FP16 path.
    /// </summary>
    public async Task<ScreenSnapshot> SnapshotAllAsync(bool includeCursor, bool hdrToneMap, CancellationToken ct = default)
    {
        var list = await Task.Run(() => { using var dpi = new DpiScope(); return monitors.GetMonitors(); }, ct).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var images = await Task.WhenAll(list.Select(monitor => Task.Run(() => CaptureMonitor(monitor, monitor.IsHdr && hdrToneMap, ct), ct))).ConfigureAwait(false);
        CursorSnapshot? cursor = null;
        if (includeCursor) cursor = await Task.Run(() => { using var dpi = new DpiScope(); return ReadCursor(); }, ct).ConfigureAwait(false);
        logger.LogDebug("Snapshot of {Count} monitor(s) took {Ms} ms", list.Count, started.ElapsedMilliseconds);
        return new ScreenSnapshot(DateTimeOffset.Now, list.Select((m, i) => (m, images[i])).ToList().AsReadOnly(), cursor);
    }
    private BgraImage CaptureMonitor(MonitorInfo monitor, bool hdr, CancellationToken ct)
    {
        using var dpi = new DpiScope();
        ct.ThrowIfCancellationRequested();
        if (hdr)
        {
            try { return GraphicsCapture.Capture(monitor.HMonitor, false, true, monitor.SdrWhiteLevelNits, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "HDR capture fell back to GDI for {Monitor}", monitor.DeviceName); }
        }
        try { return CaptureGdi(monitor.BoundsPx, false); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            logger.LogDebug(ex, "GDI capture failed for {Monitor}; using desktop duplication", monitor.DeviceName);
            return DesktopDuplication.Capture(monitor, ct);
        }
    }
    public async Task<BgraImage> CaptureRectAsync(RectI virtualPx, bool includeCursor, CancellationToken ct = default)
    {
        var displays = await Task.Run(() => { using var dpi = new DpiScope(); return monitors.GetMonitors().Where(m => !m.BoundsPx.Intersect(virtualPx).IsEmpty).ToArray(); }, ct).ConfigureAwait(false);
        if (!displays.Any(m => m.IsHdr)) return await Task.Run(() => { using var dpi = new DpiScope(); ct.ThrowIfCancellationRequested(); return CaptureGdi(virtualPx, includeCursor); }, ct).ConfigureAwait(false);
        var frames = await Task.WhenAll(displays.Select(m => Task.Run(() => CaptureMonitor(m, m.IsHdr, ct), ct))).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var dpi = new DpiScope();
            return CropSnapshot(new(DateTimeOffset.Now, displays.Select((m, i) => (m, frames[i])).ToList(), includeCursor ? ReadCursor() : null), virtualPx);
        }, ct).ConfigureAwait(false);
    }
    internal static BgraImage CaptureGdi(RectI rect, bool includeCursor)
    {
        using var target = new DibSurface(rect.Width, rect.Height, clear: false);
        var desktop = Native.GetDC(IntPtr.Zero);
        try { Native.Check(Native.BitBlt(target.Dc, 0, 0, rect.Width, rect.Height, desktop, rect.X, rect.Y, 0x40cc0020)); }
        finally { Native.ReleaseDC(IntPtr.Zero, desktop); }
        if (includeCursor) DrawCursor(target.Dc, rect);
        return target.Read();
    }
    public Task<BgraImage> CaptureWindowAsync(IntPtr hwnd, bool clientOnly, bool includeCursor, CancellationToken ct = default) => Task.Run(() =>
    {
        using var dpi = new DpiScope(); ct.ThrowIfCancellationRequested();
        var window = WindowFinder.Read(hwnd, false) ?? throw new ArgumentException("Invalid window.", nameof(hwnd));
        Native.Check(Native.GetWindowRect(hwnd, out var raw));
        var rect = clientOnly ? window.ClientPx : window.BoundsPx;
        using var surface = new DibSurface(raw.Pixels.Width, raw.Pixels.Height);
        if (!Native.PrintWindow(hwnd, surface.Dc, 2))
        {
            var frame = GraphicsCapture.Capture(hwnd, true, false, 80, ct);
            var captureBounds = window.BoundsPx;
            var cropped = clientOnly ? frame.Crop(new(rect.X - captureBounds.X, rect.Y - captureBounds.Y, rect.Width, rect.Height)) : frame;
            if (includeCursor && ReadCursor() is { } cursor) { var p = CaptureMath.CursorOrigin(cursor.PositionPx, cursor.Hotspot, rect); cropped.DrawOver(cursor.Image, p.X, p.Y); }
            return cropped;
        }
        if (includeCursor) DrawCursor(surface.Dc, raw.Pixels);
        return surface.Read().Crop(new(rect.X - raw.Left, rect.Y - raw.Top, rect.Width, rect.Height));
    }, ct);
    public BgraImage CropSnapshot(ScreenSnapshot s, RectI virtualPx)
    {
        var result = new BgraImage(virtualPx.Width, virtualPx.Height);
        foreach (var (monitor, image) in s.Monitors) result.Blit(image, monitor.BoundsPx.X - virtualPx.X, monitor.BoundsPx.Y - virtualPx.Y);
        if (s.Cursor is { } cursor)
        {
            var p = CaptureMath.CursorOrigin(cursor.PositionPx, cursor.Hotspot, virtualPx);
            if (cursorPlanes.TryGetValue(cursor, out var planes))
            {
                var clip = new RectI(p.X, p.Y, cursor.Image.Width, cursor.Image.Height).Intersect(result.Bounds);
                for (int y = clip.Top; y < clip.Bottom; y++) for (int x = clip.Left; x < clip.Right; x++)
                {
                    int src = (y - p.Y) * cursor.Image.Stride + (x - p.X) * 4, dst = y * result.Stride + x * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int black = planes.Black.Pixels[src + c], white = planes.White.Pixels[src + c];
                        result.Pixels[dst + c] = (byte)Math.Clamp(black + (white - black) * result.Pixels[dst + c] / 255, 0, 255);
                    }
                    result.Pixels[dst + 3] = 255;
                }
            }
            else result.DrawOver(cursor.Image, p.X, p.Y);
        }
        return result;
    }
    public BgraImage ApplyFreehandMask(BgraImage img, IReadOnlyList<PointI> polygonPx) => CaptureMath.PolygonMask(img, polygonPx);
    internal static void DrawCursor(IntPtr dc, RectI rect)
    {
        var cursor = new Native.Cursor { Size = Marshal.SizeOf<Native.Cursor>() };
        if (!Native.GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0) return;
        var copy = Native.CopyIcon(cursor.Handle);
        try
        {
            if (copy == IntPtr.Zero || !Native.GetIconInfo(copy, out var icon)) return;
            try { var p = CaptureMath.CursorOrigin(new(cursor.Position.X, cursor.Position.Y), new((int)icon.X, (int)icon.Y), rect); Native.DrawIconEx(dc, p.X, p.Y, copy, 0, 0, 0, IntPtr.Zero, 3); }
            finally { Native.DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) Native.DeleteObject(icon.Color); }
        }
        finally { if (copy != IntPtr.Zero) Native.DestroyIcon(copy); }
    }
    private static CursorSnapshot? ReadCursor()
    {
        var cursor = new Native.Cursor { Size = Marshal.SizeOf<Native.Cursor>() };
        if (!Native.GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0) return null;
        var copy = Native.CopyIcon(cursor.Handle);
        try
        {
            if (copy == IntPtr.Zero || !Native.GetIconInfo(copy, out var icon)) return null;
            try
            {
                Native.GetObject(icon.Color != IntPtr.Zero ? icon.Color : icon.Mask, Marshal.SizeOf<Native.Bitmap>(), out var bitmap);
                int w = bitmap.Width, h = icon.Color != IntPtr.Zero ? bitmap.Height : bitmap.Height / 2;
                using var black = new DibSurface(w, h); using var white = new DibSurface(w, h);
                var whiteBytes = Enumerable.Repeat((byte)255, checked(w * h * 4)).ToArray(); Marshal.Copy(whiteBytes, 0, white.Bits, whiteBytes.Length);
                Native.DrawIconEx(black.Dc, 0, 0, copy, w, h, 0, IntPtr.Zero, 3); Native.DrawIconEx(white.Dc, 0, 0, copy, w, h, 0, IntPtr.Zero, 3);
                var b = black.Read(false); var a = white.Read(false);
                for (int i = 0; i < b.Pixels.Length; i += 4) b.Pixels[i + 3] = (byte)(255 - Math.Clamp(a.Pixels[i] - b.Pixels[i], 0, 255));
                var snapshot = new CursorSnapshot(b, new((int)icon.X, (int)icon.Y), new(cursor.Position.X, cursor.Position.Y));
                cursorPlanes.Add(snapshot, new(b, a));
                return snapshot;
            }
            finally { Native.DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) Native.DeleteObject(icon.Color); }
        }
        finally { if (copy != IntPtr.Zero) Native.DestroyIcon(copy); }
    }
}
