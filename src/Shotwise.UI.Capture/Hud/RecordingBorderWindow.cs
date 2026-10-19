using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shotwise.Core.Geometry;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Hud;

/// <summary>
/// Click-through frame drawn just outside the recorded region (red while recording, amber while paused). Follows a recorded window
/// when its handle is given. Excluded from the recording itself.
/// </summary>
public sealed class RecordingBorderWindow : Window
{
    private const int Thickness = 3;
    private readonly Border _border = new() { BorderThickness = new Thickness(Thickness), IsHitTestVisible = false };
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly IntPtr _hwnd;
    private RectI _region;

    public RecordingBorderWindow(RectI regionPx, IntPtr followWindow = default)
    {
        _region = regionPx;
        _hwnd = followWindow;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;
        Content = _border;
        SetPaused(false);
        SourceInitialized += (_, _) =>
        {
            NativeUi.SetToolWindow(this, true);
            NativeUi.SetClickThrough(this, true);
            NativeUi.ExcludeFromCapture(this);
        };
        _follow.Tick += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            var r = NativeUi.ExtendedFrameBounds(_hwnd);
            if (r is { } rect && rect != _region) { _region = rect; Place(); }
        };
    }

    public void Open()
    {
        Show();
        Place();
        if (_hwnd != IntPtr.Zero) _follow.Start();
    }

    public void SetPaused(bool paused)
    {
        // coral while recording, amber while paused (design-system tokens)
        var fallback = paused ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0xFF, 0x4D, 0x5E);
        _border.BorderBrush = Application.Current?.TryFindResource(paused ? "Shotwise.WarningBrush" : "Shotwise.CaptureSolidBrush") as Brush ?? new SolidColorBrush(fallback);
    }

    private void Place() => NativeUi.PlaceTopmost(this, _region.Inflate(Thickness + 1, Thickness + 1));

    protected override void OnClosed(EventArgs e)
    {
        _follow.Stop();
        base.OnClosed(e);
    }
}
