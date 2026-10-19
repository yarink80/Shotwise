using System.Runtime.InteropServices;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Recording;

internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; public readonly RectI Rect => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public int Size, Flags; public nint Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] internal struct IconInfo { public int Icon, XHotspot, YHotspot; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    internal delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint rect, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] internal static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] internal static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, out BitmapObject bitmap);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapObject { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    internal static (BgraImage Image, PointI Position)? Cursor()
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || !GetIconInfo(cursor.Cursor, out var icon)) return null;
        try
        {
            var width = GetSystemMetrics(13); var height = GetSystemMetrics(14);
            if (icon.Color != 0 && GetObjectW(icon.Color, Marshal.SizeOf<BitmapObject>(), out var bitmap) != 0) { width = bitmap.Width; height = bitmap.Height; }
            width = Math.Clamp(width, 1, 512); height = Math.Clamp(height, 1, 512);
            var black = Draw(0); var white = Draw(255);
            for (var i = 0; i < black.Pixels.Length; i += 4)
            {
                var difference = Math.Max(white.Pixels[i] - black.Pixels[i], Math.Max(white.Pixels[i + 1] - black.Pixels[i + 1], white.Pixels[i + 2] - black.Pixels[i + 2]));
                black.Pixels[i + 3] = (byte)(255 - Math.Clamp(difference, 0, 255));
            }
            return (black, new PointI(cursor.Position.X - icon.XHotspot, cursor.Position.Y - icon.YHotspot));
            BgraImage Draw(byte background)
            {
                var dc = CreateCompatibleDC(0); nint dib = 0, old = 0;
                try
                {
                    var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
                    dib = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
                    if (dc == 0 || dib == 0) throw new ShotwiseException(ErrorCode.CaptureFailed, "Cursor buffer creation failed.");
                    old = SelectObject(dc, dib);
                    var image = new BgraImage(width, height); Array.Fill(image.Pixels, background);
                    Marshal.Copy(image.Pixels, 0, bits, image.Pixels.Length);
                    DrawIconEx(dc, 0, 0, cursor.Cursor, width, height, 0, 0, 3);
                    Marshal.Copy(bits, image.Pixels, 0, image.Pixels.Length); return image;
                }
                finally { if (old != 0) SelectObject(dc, old); if (dib != 0) DeleteObject(dib); if (dc != 0) DeleteDC(dc); }
            }
        }
        finally { if (icon.Mask != 0) DeleteObject(icon.Mask); if (icon.Color != 0) DeleteObject(icon.Color); }
    }
    internal static List<(nint Handle, RectI Bounds)> Monitors()
    {
        var result = new List<(nint, RectI)>();
        MonitorCallback callback = (nint h, nint dc, ref NativeRect rect, nint data) => { result.Add((h, rect.Rect)); return true; };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new ShotwiseException(ErrorCode.NoMonitor, "No capture display.");
        return result;
    }
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, char[] text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    private const int DwmwaExtendedFrameBounds = 9;
    /// <summary>
    /// Visible window bounds in physical pixels: the DWM extended frame (what WGC window frames contain, without the
    /// invisible resize borders), falling back to <c>GetWindowRect</c> only when DWM cannot report it.
    /// </summary>
    internal static bool TryGetWindowBounds(nint window, out RectI bounds)
    {
        bounds = default;
        if (window == 0 || !IsWindow(window)) return false;
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) >= 0 && frame.Right > frame.Left && frame.Bottom > frame.Top)
        { bounds = frame.Rect; return true; }
        if (!GetWindowRect(window, out var rect)) return false;
        bounds = rect.Rect; return true;
    }
    /// <summary>Window title and owning process name for recording metadata (best effort).</summary>
    internal static (string? Title, string? App) WindowInfo(nint window)
    {
        string? title = null, app = null;
        try { var buffer = new char[512]; var length = GetWindowTextW(window, buffer, buffer.Length); if (length > 0) title = new string(buffer, 0, length); } catch (Exception) { }
        try
        {
            GetWindowThreadProcessId(window, out var id);
            if (id != 0) { using var process = System.Diagnostics.Process.GetProcessById((int)id); app = process.ProcessName; }
        }
        catch (Exception) { }
        return (title, app);
    }
    internal static RectI Resolve(RecordingTarget target)
    {
        if ((target.RegionPx.HasValue ? 1 : 0) + (target.Window.HasValue ? 1 : 0) + (target.MonitorIndex.HasValue ? 1 : 0) != 1)
            throw new ShotwiseException(ErrorCode.InvalidArgument, "Exactly one recording target is required.");
        if (target.RegionPx is { } region) return region;
        if (target.Window is { } window)
        {
            if (!TryGetWindowBounds(window, out var bounds)) throw new ShotwiseException(ErrorCode.WindowNotFound, "Recording window is unavailable.");
            return bounds;
        }
        var monitors = Monitors(); var index = target.MonitorIndex!.Value;
        if (index < 0 || index >= monitors.Count) throw new ShotwiseException(ErrorCode.NoMonitor, "Recording monitor is unavailable.");
        return monitors[index].Bounds;
    }
}

internal sealed class GdiFrameSource : IDisposable
{
    private nint _screen, _memory, _bitmap, _previous, _bits;
    private readonly RectI _bounds;
    private readonly bool _cursor;
    public GdiFrameSource(RectI bounds, bool cursor)
    {
        _bounds = bounds; _cursor = cursor;
        try
        {
            _screen = DesktopNative.GetDC(0); _memory = DesktopNative.CreateCompatibleDC(_screen);
            var info = new DesktopNative.BitmapInfo { Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, Bits = 32 };
            _bitmap = DesktopNative.CreateDIBSection(_screen, ref info, 0, out _bits, 0, 0);
            if (_screen == 0 || _memory == 0 || _bitmap == 0) throw new ShotwiseException(ErrorCode.CaptureFailed, "Desktop buffer creation failed.");
            _previous = DesktopNative.SelectObject(_memory, _bitmap);
        }
        catch { Dispose(); throw; }
    }
    public BgraImage Capture()
    {
        if (!DesktopNative.BitBlt(_memory, 0, 0, _bounds.Width, _bounds.Height, _screen, _bounds.X, _bounds.Y, 0x40CC0020))
            throw new ShotwiseException(ErrorCode.CaptureFailed, "Desktop capture failed.");
        if (_cursor)
        {
            var cursor = new DesktopNative.CursorInfo { Size = Marshal.SizeOf<DesktopNative.CursorInfo>() };
            if (DesktopNative.GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && DesktopNative.GetIconInfo(cursor.Cursor, out var icon))
            {
                try { DesktopNative.DrawIconEx(_memory, cursor.Position.X - _bounds.X - icon.XHotspot, cursor.Position.Y - _bounds.Y - icon.YHotspot, cursor.Cursor, 0, 0, 0, 0, 3); }
                finally { if (icon.Mask != 0) DesktopNative.DeleteObject(icon.Mask); if (icon.Color != 0) DesktopNative.DeleteObject(icon.Color); }
            }
        }
        var image = new BgraImage(_bounds.Width, _bounds.Height);
        Marshal.Copy(_bits, image.Pixels, 0, image.Pixels.Length);
        for (var i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
        return image;
    }
    public void Dispose()
    {
        if (_previous != 0 && _memory != 0) DesktopNative.SelectObject(_memory, _previous);
        if (_bitmap != 0) DesktopNative.DeleteObject(_bitmap);
        if (_memory != 0) DesktopNative.DeleteDC(_memory);
        if (_screen != 0) DesktopNative.ReleaseDC(0, _screen);
        _screen = _memory = _bitmap = _previous = _bits = 0;
    }
}
