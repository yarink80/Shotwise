using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Shotwise.UI.Editor.Surface;

/// <summary>
/// Hosts the canvas drawing surface: the GPU-backed <see cref="SKGLElement"/> when the "Hardware-accelerated rendering"
/// preference (Advanced) is on, otherwise the CPU <see cref="SKElement"/>. Switches live when the preference changes and
/// falls back to the CPU surface (for the rest of the process) when OpenGL cannot be initialised or never paints.
/// </summary>
internal sealed class CanvasSurface : Border
{
    private static bool s_glFailed;

    private readonly ILogger _log;
    private FrameworkElement? _element;
    private DispatcherTimer? _watchdog;
    private bool _glPainted;

    public CanvasSurface(bool hardware, ILogger log)
    {
        _log = log;
        Focusable = false;
        SnapsToDevicePixels = true;
        SetHardware(hardware);
    }

    /// <summary>Paint callback: canvas, pixel width, pixel height.</summary>
    public event Action<SKCanvas, int, int>? Paint;

    /// <summary>True while the GPU surface is in use.</summary>
    public bool IsHardware { get; private set; }

    public void SetHardware(bool hardware)
    {
        hardware &= !s_glFailed;
        if (_element is not null && hardware == IsHardware) return;
        DetachElement();
        if (hardware && TryCreateGl() is { } gl)
        {
            _element = gl;
            IsHardware = true;
            StartWatchdog();
        }
        else
        {
            var sk = new SKElement { Focusable = false, SnapsToDevicePixels = true };
            sk.PaintSurface += OnCpuPaint;
            _element = sk;
            IsHardware = false;
        }
        Child = _element;
        Invalidate();
    }

    public void Invalidate() => _element?.InvalidateVisual();

    public void Release()
    {
        DetachElement();
        Paint = null;
    }

    private SKGLElement? TryCreateGl()
    {
        try
        {
            var gl = new SKGLElement { Focusable = false, SnapsToDevicePixels = true };
            gl.PaintSurface += OnGlPaint;
            _glPainted = false;
            return gl;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or InvalidOperationException or EntryPointNotFoundException or BadImageFormatException)
        {
            FailGl(ex);
            return null;
        }
    }

    private void StartWatchdog()
    {
        // GLWpfControl creates its context on Loaded; if no frame arrives while the canvas is visible, use the CPU surface.
        _watchdog?.Stop();
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _watchdog.Tick += (_, _) =>
        {
            if (_glPainted || !IsHardware) { _watchdog?.Stop(); return; }
            if (!IsVisible || ActualWidth < 1 || ActualHeight < 1) return; // nothing to paint yet
            _watchdog?.Stop();
            FailGl(null);
        };
        _watchdog.Start();
    }

    private void FailGl(Exception? ex)
    {
        s_glFailed = true;
        if (ex is null) _log.LogWarning("Hardware-accelerated canvas produced no frame; using software rendering");
        else _log.LogWarning(ex, "Hardware-accelerated canvas unavailable; using software rendering");
        if (IsHardware) Dispatcher.BeginInvoke(() => SetHardware(false));
    }

    private void DetachElement()
    {
        _watchdog?.Stop();
        switch (_element)
        {
            case SKElement sk: sk.PaintSurface -= OnCpuPaint; break;
            case SKGLElement gl:
                gl.PaintSurface -= OnGlPaint;
                if (gl is IDisposable d) { try { d.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _log.LogDebug(ex, "GL surface dispose failed"); } }
                break;
        }
        Child = null;
        _element = null;
    }

    private void OnCpuPaint(object? sender, SKPaintSurfaceEventArgs e) => Paint?.Invoke(e.Surface.Canvas, e.Info.Width, e.Info.Height);

    private void OnGlPaint(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        if (!_glPainted) _log.LogInformation("Canvas uses hardware-accelerated (OpenGL) rendering");
        _glPainted = true;
        try { Paint?.Invoke(e.Surface.Canvas, e.Info.Width, e.Info.Height); }
        finally { e.Surface.Canvas.Flush(); }
    }
}
