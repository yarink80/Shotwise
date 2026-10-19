using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwise.Core.Imaging;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.App.Smoke;

/// <summary>
/// Real screenshots of our own top-level windows for visual review (CONTRACT A30): <c>PrintWindow(PW_RENDERFULLCONTENT)</c>
/// of the window HWND (works when desktop-level capture only shows wallpaper, e.g. in agent sessions), falling back to
/// rendering the WPF visual tree with <see cref="RenderTargetBitmap"/> when PrintWindow returns nothing useful.
/// </summary>
internal static class WindowShot
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    /// <summary>Captures <paramref name="window"/> and writes a PNG; returns the method used and the image size.</summary>
    public static (string Method, int Width, int Height, double NonBlankFraction) Save(Window window, string path)
    {
        var image = ByPrintWindow(window);
        string method = "PrintWindow";
        if (image is null || NonBlank(image) < 0.02)
        {
            image = ByRenderTarget(window);
            method = "RenderTargetBitmap";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PngCodec.Encode(image));
        return (method, image.Width, image.Height, NonBlank(image));
    }

    private static BgraImage? ByPrintWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        // Our windows are excluded from capture (CAP-19, WDA_EXCLUDEFROMCAPTURE) which makes PrintWindow return black:
        // lift the affinity for the duration of the shot and restore it afterwards.
        bool restore = GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != 0 && SetWindowDisplayAffinity(hwnd, 0);
        try
        {
            if (restore) System.Threading.Thread.Sleep(120);
            if (!PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)) return null;
            var img = new BgraImage(w, h);
            Marshal.Copy(bits, img.Pixels, 0, img.Pixels.Length);
            for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255; // GDI leaves alpha undefined
            return img;
        }
        finally
        {
            if (restore) SetWindowDisplayAffinity(hwnd, affinity);
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static BgraImage ByRenderTarget(Window window)
    {
        var root = (FrameworkElement?)window.Content ?? window;
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Fluent windows have a transparent background (the Mica backdrop is drawn by DWM): paint the theme background.
            var background = window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? window.Background ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        rtb.Render(dv);
        _ = root;
        return rtb.ToBgraImage();
    }

    /// <summary>Fraction of pixels that differ from the most common colour (0 = a blank/uniform image).</summary>
    public static double NonBlank(BgraImage img)
    {
        if (img.Pixels.Length == 0) return 0;
        var counts = new Dictionary<uint, int>();
        int step = Math.Max(1, img.Pixels.Length / 4 / 20000);
        int samples = 0;
        for (int p = 0; p < img.Pixels.Length; p += 4 * step)
        {
            uint v = BitConverter.ToUInt32(img.Pixels, p) & 0x00F0F0F0; // ignore alpha and low bits
            counts[v] = counts.GetValueOrDefault(v) + 1; samples++;
        }
        int max = counts.Values.Max();
        return 1 - max / (double)samples;
    }
}
