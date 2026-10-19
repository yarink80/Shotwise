using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shotwise.Core.Geometry;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Hud;

/// <summary>Large centred "3 .. 2 .. 1" countdown shown before a timed capture or a recording. Click or Esc cancels.</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number = new() { FontSize = 96, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    private CountdownWindow(int seconds)
    {
        Ui.EnsureStyles();
        _remaining = seconds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = true;
        Width = 220; Height = 220;
        WindowStartupLocation = WindowStartupLocation.Manual;
        this.SetResourceReference(FontFamilyProperty, "Shotwise.FontFamily");

        // dark disc with a coral ring and glow
        var disc = new Border { CornerRadius = new CornerRadius(100), Margin = new Thickness(10), BorderThickness = new Thickness(6) };
        disc.SetResourceReference(Border.BackgroundProperty, "Shotwise.HudBrush");
        disc.SetResourceReference(Border.BorderBrushProperty, "Shotwise.CaptureBrush");
        disc.SetResourceReference(UIElement.EffectProperty, "Shotwise.Elevation.CaptureGlow");
        var grid = new Grid();
        _number.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(_number, FontNumeralAlignment.Tabular);
        grid.Children.Add(_number);
        var hint = Ui.Label(CS.Get("Countdown_Hint"), 11, brush: "Capture.HudMutedTextBrush");
        hint.HorizontalAlignment = HorizontalAlignment.Center; hint.VerticalAlignment = VerticalAlignment.Bottom; hint.Margin = new Thickness(0, 0, 0, 24);
        grid.Children.Add(hint);
        disc.Child = grid;
        Content = disc;
        _number.Text = _remaining.ToString();

        SourceInitialized += (_, _) => { NativeUi.ExcludeFromCapture(this); NativeUi.SetToolWindow(this, false); };
        Loaded += (_, _) => Pulse();
        MouseLeftButtonDown += (_, _) => Finish(false);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(false); } };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) Finish(true);
            else { _number.Text = _remaining.ToString(); Pulse(); }
        };
    }

    private void Pulse()
    {
        if (_number.RenderTransform is not ScaleTransform st) return;
        var anim = new DoubleAnimation(1.28, 1.0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Finish(bool completed)
    {
        _timer.Stop();
        if (_done.TrySetResult(completed)) { try { Close(); } catch (InvalidOperationException) { } }
    }

    /// <summary>Shows the countdown centred in <paramref name="areaPx"/> (screen pixels); false when the user cancelled or <paramref name="ct"/> fired.</summary>
    public static async Task<bool> RunAsync(int seconds, RectI areaPx, CancellationToken ct)
    {
        if (seconds <= 0) return true;
        var w = new CountdownWindow(seconds);
        using var reg = ct.Register(() => w.Dispatcher.BeginInvoke(new Action(() => w.Finish(false))));
        w.Show();
        var scale = NativeUi.ScaleOf(w);
        int size = (int)(220 * scale);
        NativeUi.PlaceTopmost(w, new RectI(areaPx.X + (areaPx.Width - size) / 2, areaPx.Y + (areaPx.Height - size) / 2, size, size), activate: true);
        w.Activate();
        w._timer.Start();
        return await w._done.Task;
    }
}

/// <summary>Compact dark pill used while waiting (scheduled capture) or between interval captures; shows live text and a Stop button.</summary>
public sealed class WaitHud : HudWindow
{
    private readonly TextBlock _text = Ui.Label("", 13, weight: FontWeights.SemiBold, brush: "Shotwise.HudTextBrush");
    private readonly TextBlock _sub = Ui.Label("", 11, brush: "Capture.HudMutedTextBrush");
    public event Action? StopRequested;

    public WaitHud(string stopTip) : base(true, HudKind.DarkPill)
    {
        ChromePadding = new Thickness(14, 8, 8, 8);
        ChromeRadius = new CornerRadius(28);
        var stop = Ui.RecordButton(stopTip, () => StopRequested?.Invoke(), "Icon.Stop.Filled");
        stop.Margin = new Thickness(14, 0, 0, 0);
        var icon = Ui.Icon("Icon.Timer.Regular", 22, "Shotwise.HudTextBrush");
        icon.Margin = new Thickness(0, 0, 10, 0);
        Content = Ui.Row(icon, Ui.Column(_text, _sub), stop);
    }

    public void SetText(string title, string subtitle) { _text.Text = title; _sub.Text = subtitle; }
}
