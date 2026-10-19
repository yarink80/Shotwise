using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Capture.Hud;

/// <summary>
/// Floating live webcam preview in the selected PiP shape (circle / rounded square / rounded rectangle), driven by
/// <see cref="IWebcamPreviewFactory"/>. Draggable, excluded from captures.
/// </summary>
public sealed class WebcamPreviewWindow : HudWindow
{
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _frame = new();
    private readonly Border _ring = new() { BorderThickness = new Thickness(3), IsHitTestVisible = false };
    private readonly Grid _stage = new();
    private IWebcamPreview? _preview;
    private WriteableBitmap? _bitmap;
    private int _pending;
    private WebcamShape _shape;

    public WebcamPreviewWindow(WebcamShape shape) : base(noActivate: true, HudKind.Bare)
    {
        // Preview picture inside a white ring with a soft shadow (the picture itself is clipped to the PiP shape).
        _ring.SetResourceReference(Border.BorderBrushProperty, "Shotwise.OnAccentBrush");
        _ring.SetResourceReference(UIElement.EffectProperty, "Shotwise.Elevation.Flyout");
        _frame.Child = _image;
        _frame.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface3Brush");
        _stage.Children.Add(_frame);
        _stage.Children.Add(_ring);
        Content = _stage;
        SetShape(shape);
        Closed += (_, _) => StopPreview();
    }

    public void SetShape(WebcamShape shape)
    {
        _shape = shape;
        switch (shape)
        {
            case WebcamShape.Circle: _stage.Width = _stage.Height = 180; _frame.CornerRadius = _ring.CornerRadius = new CornerRadius(90); break;
            case WebcamShape.RoundedSquare: _stage.Width = _stage.Height = 180; _frame.CornerRadius = _ring.CornerRadius = new CornerRadius(28); break;
            default: _stage.Width = 256; _stage.Height = 144; _frame.CornerRadius = _ring.CornerRadius = new CornerRadius(20); break;
        }
        _frame.Clip = new RectangleGeometry(new Rect(0, 0, _stage.Width, _stage.Height), _frame.CornerRadius.TopLeft, _frame.CornerRadius.TopLeft);
    }

    public async Task StartAsync(IWebcamPreviewFactory factory, string deviceId, ILogger? log, CancellationToken ct)
    {
        StopPreview();
        try
        {
            var p = factory.Create();
            p.Frame += OnFrame;
            _preview = p;
            await p.StartAsync(deviceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Webcam preview failed to start");
            StopPreview();
            throw;
        }
    }

    private void OnFrame(object? sender, BgraImage frame)
    {
        // Frames arrive on a capture thread; drop frames while the UI is busy so latency stays low.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                {
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
                    _image.Source = _bitmap;
                }
                frame.CopyTo(_bitmap);
            }
            finally { Interlocked.Exchange(ref _pending, 0); }
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    public void StopPreview()
    {
        var p = _preview;
        _preview = null;
        if (p is null) return;
        p.Frame -= OnFrame;
        try { p.Dispose(); } catch (Exception) { /* device already gone */ }
    }
}
