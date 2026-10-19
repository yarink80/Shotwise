using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Capture;

public sealed class MenuCaptureService(IScreenCaptureService screen, ILogger<MenuCaptureService> logger) : IMenuCaptureService
{
    public async Task<BgraImage?> CaptureMenusAsync(bool includeMenuBar, bool cascaded, TimeSpan wait, CancellationToken ct)
    {
        if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait));
        var owner = Native.GetForegroundWindow();
        await Task.Delay(wait, ct).ConfigureAwait(false);
        // Only the menus that are open when the wait ends are captured (R1-#12): popups that were opened during the wait and closed
        // again (submenu A while the user moved on to B) are not part of the result, and every popup is sampled after it finished
        // painting rather than at popup start. EnumWindows walks the z-order top-down, so the first popup is the topmost one.
        var captured = await Task.Run(() =>
        {
            using var dpi = new DpiScope();
            var popups = new List<(RectI Rect, BgraImage Image)>();
            Native.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (Native.Class(hwnd) == "#32768" && Native.IsWindowVisible(hwnd) && !IsCloaked(hwnd) && Native.GetWindowRect(hwnd, out var r) && !r.Pixels.IsEmpty)
                        popups.Add((r.Pixels, ScreenCaptureService.CaptureGdi(r.Pixels, false)));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { logger.LogDebug(ex, "Menu popup capture failed"); }
                return true;
            }, IntPtr.Zero);
            return popups;
        }, ct).ConfigureAwait(false);
        return await Compose(SelectPopups(captured, cascaded), includeMenuBar, owner, ct).ConfigureAwait(false);
    }

    private static bool IsCloaked(IntPtr hwnd) => Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0;

    /// <summary>
    /// Popups in z-order (topmost first) → popups to draw, bottom-most first so cascaded submenus overlap their parents as on screen.
    /// Non-cascaded keeps only the topmost visible popup.
    /// </summary>
    internal static IReadOnlyList<T> SelectPopups<T>(IReadOnlyList<T> zOrderTopFirst, bool cascaded)
    {
        if (zOrderTopFirst.Count == 0) return Array.Empty<T>();
        if (!cascaded) return new[] { zOrderTopFirst[0] };
        return zOrderTopFirst.Reverse().ToArray();
    }

    private async Task<BgraImage?> Compose(IReadOnlyList<(RectI Rect, BgraImage Image)> captured, bool includeMenuBar, IntPtr owner, CancellationToken ct)
    {
        if (captured.Count == 0) return null;
        var bounds = captured.Select(f => f.Rect).Aggregate((a, b) => a.Union(b));
        BgraImage? barImage = null; RectI bar = RectI.Empty;
        if (includeMenuBar)
        {
            var info = new Native.MenuBar { Size = (uint)Marshal.SizeOf<Native.MenuBar>() };
            if (Native.GetMenuBarInfo(owner, -3, 0, ref info) && !info.Bounds.Pixels.IsEmpty) { bar = info.Bounds.Pixels; bounds = bounds.Union(bar); barImage = await screen.CaptureRectAsync(bar, false, ct).ConfigureAwait(false); }
        }
        var image = new BgraImage(bounds.Width, bounds.Height);
        if (barImage is not null) image.Blit(barImage, bar.X - bounds.X, bar.Y - bounds.Y);
        foreach (var f in captured) image.Blit(f.Image, f.Rect.X - bounds.X, f.Rect.Y - bounds.Y);
        return image;
    }
}
