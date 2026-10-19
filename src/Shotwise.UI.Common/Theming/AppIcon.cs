using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shotwise.UI.Common.Theming;

/// <summary>
/// The Shotwise app icon (tools/icon-gen; linked from <c>src/Shotwise.App/Assets</c>): window icons, the pixel-hinted bitmaps for
/// small chrome (title bars), and the tray icon streams. <see cref="ApplyToAllWindows"/> gives every top-level window the icon
/// once per process, so no window has to set it itself.
/// </summary>
public static class AppIcon
{
    private const string Base = "pack://application:,,,/Shotwise.UI.Common;component/Assets/";
    private static BitmapDecoder? _decoder;
    private static bool _applied;

    /// <summary>Multi-resolution icon (16-256 px); WPF picks the best frame for the caption and the taskbar.</summary>
    public static ImageSource? WindowIcon => Decoder?.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault() is { } f ? f : null;

    private static BitmapDecoder? Decoder
    {
        get
        {
            if (_decoder is not null) return _decoder;
            try
            {
                var info = Application.GetResourceStream(new Uri(Base + "shotwise.ico", UriKind.Absolute));
                if (info is null) return null;
                using var s = info.Stream;
                _decoder = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
            {
                _decoder = null;
            }
            return _decoder;
        }
    }

    /// <summary>
    /// The pixel-hinted frame for an icon shown at <paramref name="dips"/> device-independent pixels on a display with
    /// <paramref name="dpiScale"/> (smallest frame at least that many pixels, else the largest).
    /// </summary>
    public static ImageSource? ForSize(double dips, double dpiScale = 1.0)
    {
        var frames = Decoder?.Frames;
        if (frames is null || frames.Count == 0) return null;
        var px = (int)Math.Round(dips * dpiScale);
        return frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= px) ?? frames.OrderByDescending(f => f.PixelWidth).First();
    }

    /// <summary>Opens the notification-area icon (.ico with 16-64 px frames); <paramref name="recording"/> = coral recording state.</summary>
    public static Stream? OpenTrayIcon(bool recording)
        => Application.GetResourceStream(new Uri(Base + (recording ? "shotwise-tray-recording.ico" : "shotwise-tray.ico"), UriKind.Absolute))?.Stream;

    /// <summary>Gives every <see cref="Window"/> that has no icon of its own the app icon when it loads (idempotent).</summary>
    public static void ApplyToAllWindows()
    {
        if (_applied) return;
        _applied = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        if (Application.Current is { } app)
            foreach (Window w in app.Windows) Apply(w);
    }

    /// <summary>Sets the app icon on <paramref name="w"/> unless it already has one.</summary>
    public static void Apply(Window w)
    {
        if (w.Icon is null && WindowIcon is { } icon) w.Icon = icon;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is Window w) Apply(w);
    }
}
