using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Shotwise.Core.Geometry;

namespace Shotwise.UI.Capture.Infrastructure;

/// <summary>Small set of user32/dwmapi calls used by the capture UI (window placement in physical pixels, capture exclusion, click-through).</summary>
internal static class NativeUi
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x80000;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>True when the process runs inside a Remote Desktop session (captures may be masked by the remote display driver).</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SM_REMOTESESSION) != 0;

    public static PointI CursorPx()
    {
        GetCursorPos(out var p);
        return new PointI(p.X, p.Y);
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void AddExStyle(IntPtr hwnd, int style)
    {
        var cur = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(cur | (uint)style));
    }

    public static void RemoveExStyle(IntPtr hwnd, int style)
    {
        var cur = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(cur & ~(long)(uint)style));
    }

    /// <summary>Hides the window from screenshots and recordings (Windows 10 2004+); silently ignored where unsupported.</summary>
    public static void ExcludeFromCapture(Window w)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        try { SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE); } catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    public static void SetClickThrough(Window w, bool on)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        if (on) AddExStyle(h, WS_EX_TRANSPARENT | WS_EX_LAYERED);
        else RemoveExStyle(h, WS_EX_TRANSPARENT);
    }

    public static void SetToolWindow(Window w, bool noActivate)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        AddExStyle(h, WS_EX_TOOLWINDOW | (noActivate ? WS_EX_NOACTIVATE : 0));
    }

    /// <summary>DWM-cloaks (hides from composition without hiding the window) or uncloaks a top-level window (DWMWA_CLOAK).</summary>
    public static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        if (hwnd == IntPtr.Zero) return;
        try { int value = cloaked ? 1 : 0; DwmSetWindowAttribute(hwnd, 13, ref value, sizeof(int)); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public static void RoundCorners(Window w)
    {
        try
        {
            var h = HandleOf(w);
            if (h == IntPtr.Zero) return;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    /// <summary>
    /// The window's HWND, creating it when needed; <see cref="IntPtr.Zero"/> when the window has already been closed (late events such as
    /// ContentRendered or DpiChanged can arrive after a fast selection closed the overlay).
    /// </summary>
    public static IntPtr HandleOf(Window w)
    {
        var helper = new WindowInteropHelper(w);
        if (helper.Handle != IntPtr.Zero) return helper.Handle;
        try { return helper.EnsureHandle(); }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    /// <summary>Moves/sizes the window to an exact rectangle in physical (virtual-screen) pixels and keeps it topmost.</summary>
    public static void PlaceTopmost(Window w, RectI px, bool activate = false)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, HWND_TOPMOST, px.X, px.Y, px.Width, px.Height, SWP_SHOWWINDOW | (activate ? 0 : SWP_NOACTIVATE));
    }

    public static void MoveTo(Window w, int x, int y)
    {
        var h = HandleOf(w);
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor the window is currently on.</summary>
    public static double ScaleOf(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(h);
            if (dpi > 0) return dpi / 96.0;
        }
        return VisualTreeHelperDpi(w);
    }

    private static double VisualTreeHelperDpi(Window w)
    {
        try { return System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX; } catch (InvalidOperationException) { return 1.0; }
    }

    public static RectI WindowRectPx(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return RectI.Empty;
        GetWindowRect(h, out var r);
        return RectI.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    /// <summary>Visible bounds (DWM extended frame) of a window in physical pixels, or null when the window is gone / minimised.</summary>
    public static RectI? ExtendedFrameBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        if (DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out r)) return null;
        var rect = RectI.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return rect.IsEmpty || rect.X <= -30000 ? null : rect;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
