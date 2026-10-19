using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwise.Core.Imaging;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Capture.Hud;

internal static class HudImages
{
    /// <summary>Downscales (nearest, cheap) for HUD thumbnails so large stitched previews never stall the UI thread.</summary>
    public static BitmapSource Thumbnail(BgraImage img, int maxSide)
    {
        var src = img.ToBitmapSource();
        double s = Math.Min(1.0, maxSide / (double)Math.Max(img.Width, img.Height));
        if (s >= 1) return src;
        var tb = new TransformedBitmap(src, new ScaleTransform(s, s));
        tb.Freeze();
        return tb;
    }

    /// <summary>Rounded inset (Surface2) that hosts a live preview image.</summary>
    public static Border PreviewFrame(UIElement image, double radius = 10)
    {
        var b = new Border { Child = image, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), ClipToBounds = true, SnapsToDevicePixels = true };
        b.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
        b.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface2Brush");
        return b;
    }

    public static StackPanel Title(string iconKey, string title, TextBlock status)
    {
        var icon = Ui.Icon(iconKey, 24, "Shotwise.AccentBrush");
        icon.Margin = new Thickness(0, 0, 10, 0);
        var text = Ui.Column(Ui.Label(title, 15, weight: FontWeights.SemiBold), status);
        return Ui.Row(icon, text);
    }
}

/// <summary>Panoramic capture HUD: Start, live stitched preview (updated by the engine), Stop and Cancel.</summary>
public sealed class PanoramicHudWindow : HudWindow
{
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxWidth = 340, MaxHeight = 260, MinWidth = 340, MinHeight = 120 };
    private readonly TextBlock _status = Ui.Label("", 12, secondary: true, wrap: true);
    private readonly Button _start, _stop;
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    public event Action? StartRequested, StopRequested, CancelRequested;

    public PanoramicHudWindow() : base(true, HudKind.Card)
    {
        ChromePadding = new Thickness(16);
        _start = Ui.TextButton(CS.Get("Panoramic_Start"), () => StartRequested?.Invoke(), "Icon.Play.Filled", accent: true);
        _stop = Ui.RecordButton(CS.Get("Panoramic_Stop"), () => StopRequested?.Invoke(), "Icon.Stop.Filled");
        _stop.Visibility = Visibility.Collapsed;
        var cancel = Ui.TextButton(CS.Get("Toolbar_Cancel"), () => CancelRequested?.Invoke(), ghost: true);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_start); buttons.Children.Add(_stop); buttons.Children.Add(cancel);
        var previewBorder = HudImages.PreviewFrame(_preview);
        previewBorder.Margin = new Thickness(0, 12, 0, 0);
        var col = new StackPanel { Width = 344 };
        col.Children.Add(HudImages.Title("Icon.Scroll.Filled", CS.Get("Panoramic_Title"), _status));
        col.Children.Add(previewBorder);
        col.Children.Add(_busy);
        col.Children.Add(buttons);
        Content = col;
        _status.Text = CS.Get("Panoramic_Ready");
    }

    public void SetCapturing()
    {
        _start.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Visible;
        _status.Text = CS.Get("Panoramic_Capturing");
    }

    public void SetPreview(BgraImage image)
    {
        _preview.Source = HudImages.Thumbnail(image, 520);
        _status.Text = CS.Format("Panoramic_Size", image.Width, image.Height);
    }

    public void SetStitching()
    {
        _stop.IsEnabled = false;
        _busy.Visibility = Visibility.Visible;
        _status.Text = CS.Get("Panoramic_Stitching");
    }
}

/// <summary>Scrolling capture HUD: optional Start button (preference), progress with live preview and Cancel.</summary>
public sealed class ScrollingHudWindow : HudWindow
{
    private readonly TextBlock _status = Ui.Label("", 13, weight: FontWeights.SemiBold);
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 160, MaxWidth = 240 };
    private readonly Border _previewFrame;
    private readonly Button _start;
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };

    public event Action? StartRequested, CancelRequested;

    public ScrollingHudWindow(bool requireStart) : base(true, HudKind.Card)
    {
        ChromePadding = new Thickness(16);
        _start = Ui.TextButton(CS.Get("Scrolling_Start"), () => StartRequested?.Invoke(), "Icon.Play.Filled", accent: true);
        _start.Visibility = requireStart ? Visibility.Visible : Visibility.Collapsed;
        var cancel = Ui.TextButton(CS.Get("Toolbar_Cancel"), () => CancelRequested?.Invoke(), ghost: true);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_start); buttons.Children.Add(cancel);
        _previewFrame = HudImages.PreviewFrame(_preview);
        _previewFrame.Margin = new Thickness(0, 10, 0, 0);
        _previewFrame.Visibility = Visibility.Collapsed;
        var icon = Ui.Icon("Icon.Scroll.Filled", 22, "Shotwise.AccentBrush");
        icon.Margin = new Thickness(0, 0, 10, 0);
        var col = new StackPanel { MinWidth = 260 };
        col.Children.Add(Ui.Row(icon, _status));
        col.Children.Add(_previewFrame);
        col.Children.Add(_bar);
        col.Children.Add(buttons);
        Content = col;
        _status.Text = requireStart ? CS.Get("Scrolling_PressStart") : CS.Get("Scrolling_Working");
        _bar.Visibility = requireStart ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetRunning()
    {
        _start.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Visible;
        _status.Text = CS.Get("Scrolling_Working");
    }

    public void SetProgress(int frames, BgraImage? preview)
    {
        _status.Text = CS.Format("Scrolling_Frames", frames);
        if (preview is not null)
        {
            _preview.Source = HudImages.Thumbnail(preview, 320);
            _previewFrame.Visibility = Visibility.Visible;
        }
    }

    public void SetStitching()
    {
        _status.Text = CS.Get("Scrolling_Stitching");
    }
}

/// <summary>Step capture HUD (dark pill): step counter badge, last-step thumbnail, Pause/Resume, Restart, Cancel and Finish.</summary>
public sealed class StepCaptureHudWindow : HudWindow
{
    private readonly TextBlock _count = new() { Text = "0", FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 11, Margin = new Thickness(6, 8, 6, 0) };
    private readonly Image _thumb = new() { Width = 96, Height = 60, Stretch = Stretch.UniformToFill };
    private readonly Border _thumbFrame;
    private readonly Button _pause;
    private bool _paused;

    public event Action? PauseToggled, StopRequested, RestartRequested, CancelRequested;

    public StepCaptureHudWindow() : base(true, HudKind.DarkPill)
    {
        ChromePadding = new Thickness(12, 10, 12, 10);
        ChromeRadius = new CornerRadius(24);
        _pause = Ui.IconButton("Icon.Pause.Filled", CS.Get("Hud_Pause"), () => PauseToggled?.Invoke(), 18, "Capture.HudButton");
        var stop = Ui.TextButton(CS.Get("Steps_Finish"), () => StopRequested?.Invoke(), "Icon.Checkmark.Regular", accent: true);
        stop.Margin = new Thickness(6, 0, 0, 0);
        var restart = Ui.IconButton("Icon.Reset.Regular", CS.Get("Hud_Restart"), () => RestartRequested?.Invoke(), 18, "Capture.HudButton");
        var cancel = Ui.IconButton("Icon.Dismiss.Regular", CS.Get("Hud_Cancel"), () => CancelRequested?.Invoke(), 18, "Capture.HudButton");

        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Child = _count, Margin = new Thickness(0, 0, 10, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "Shotwise.AccentGradientBrush");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.OnAccentBrush");
        var title = new TextBlock { Text = CS.Get("Steps_Title"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Capture.HudMutedTextBrush");

        _thumbFrame = new Border { Child = _thumb, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1) };
        _thumbFrame.SetResourceReference(Border.BorderBrushProperty, "Capture.HudBorderBrush");

        var row = Ui.Row(badge, title, _thumbFrame, Ui.VSeparator(22, "Capture.HudSepBrush"), _pause, restart, cancel, stop);
        var col = new StackPanel();
        col.Children.Add(row);
        _hint.Text = CS.Get("Steps_Hint");
        col.Children.Add(_hint);
        Content = col;
    }

    public void SetCount(int n, BgraImage? last)
    {
        _count.Text = n.ToString();
        if (last is not null)
        {
            _thumb.Source = HudImages.Thumbnail(last, 192);
            _thumbFrame.Visibility = Visibility.Visible;
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        SwUi.SetIcon(_pause, paused ? "Icon.Play.Filled" : "Icon.Pause.Filled");
        _pause.ToolTip = paused ? CS.Get("Steps_Resume") : CS.Get("Hud_Pause");
        _hint.Text = paused ? CS.Get("Steps_PausedHint") : CS.Get("Steps_Hint");
    }

    public bool IsPaused => _paused;
}
