using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shotwise.Core.Media;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Hud;

public enum RecordingCommand { PauseResume, Stop, Restart, Cancel, ToggleMic, ToggleSystemAudio, ToggleWebcamFullscreen, ToggleScreenDraw }

/// <summary>
/// Recording control bar (dark translucent pill): pulsing record dot with a live timer, pause/resume, coral stop, microphone /
/// system-audio mute with small level meters, webcam fullscreen swap, ScreenDraw toggle, restart and cancel. Device toggles that
/// cannot change mid-recording are disabled with an explanatory tooltip. A compact variant (time, pause, stop, draw) is used when
/// recording a whole single monitor.
/// </summary>
public sealed class RecordingHudWindow : HudWindow
{
    private readonly TextBlock _time = new() { Text = "00:00", FontSize = 16, FontWeight = FontWeights.SemiBold, MinWidth = 50, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stateText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause, _stop, _restart, _cancel, _mic, _sys, _cam;
    private readonly ToggleButton _draw;
    private readonly LevelMeter _micMeter = new(), _sysMeter = new();
    private readonly Border _warning = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 6, 12, 6), Visibility = Visibility.Collapsed, Margin = new Thickness(4, 8, 4, 2) };
    private readonly TextBlock _warningText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Func<TimeSpan> _elapsed;
    // The recorder only advances its own elapsed time when frames / audio arrive; on a static screen that left the timer at 00:00 for a
    // while after start. A private wall clock (running only while recording) keeps the display live; the recorder value wins when ahead.
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private TimeSpan _lastRecorder;
    private bool _micMuted, _sysMuted, _paused, _frozen;

    public event Action<RecordingCommand>? CommandInvoked;

    public RecordingHudWindow(Func<TimeSpan> elapsed, bool micRecorded, bool systemRecorded, bool webcamRecorded, bool compact) : base(true, HudKind.DarkPill)
    {
        _elapsed = elapsed;
        ChromePadding = new Thickness(10, 7, 10, 7);
        ChromeRadius = new CornerRadius(30);

        _pause = HudButton("Icon.Pause.Filled", CS.Get("Hud_Pause"), RecordingCommand.PauseResume);
        _stop = HudButton("Icon.Stop.Filled", CS.Get("Hud_Stop"), RecordingCommand.Stop, "Capture.HudStopButton", 15);
        _restart = HudButton("Icon.Reset.Regular", CS.Get("Hud_Restart"), RecordingCommand.Restart);
        _cancel = HudButton("Icon.Dismiss.Regular", CS.Get("Hud_Cancel"), RecordingCommand.Cancel);
        _mic = HudButton("Icon.Mic.Regular", micRecorded ? CS.Get("Hud_MuteMic") : CS.Get("Recording_MicToggleDisabledTip"), RecordingCommand.ToggleMic);
        _sys = HudButton("Icon.Speaker.Regular", systemRecorded ? CS.Get("Hud_MuteSystem") : CS.Get("Recording_SysAudioToggleDisabledTip"), RecordingCommand.ToggleSystemAudio);
        _cam = HudButton("Icon.Webcam.Regular", webcamRecorded ? CS.Get("Hud_WebcamFullscreen") : CS.Get("Recording_CamToggleDisabledTip"), RecordingCommand.ToggleWebcamFullscreen);
        _draw = new ToggleButton { ToolTip = CS.Get("Hud_ScreenDraw") }.WithStyle("Capture.HudButton");
        SwUi.SetIcon(_draw, "Icon.Tool.Pen.Regular");
        Ui.AutomationProperties(_draw, CS.Get("Hud_ScreenDraw"));
        _draw.Click += (_, _) => Raise(RecordingCommand.ToggleScreenDraw);

        // Input toggles are fixed for the whole recording: only mute (mic / system audio) and fullscreen swap (webcam) work mid-recording.
        _mic.IsEnabled = micRecorded; _sys.IsEnabled = systemRecorded; _cam.IsEnabled = webcamRecorded;
        _micMeter.IsEnabled = micRecorded; _sysMeter.IsEnabled = systemRecorded;
        _micMeter.Opacity = micRecorded ? 1 : 0.35; _sysMeter.Opacity = systemRecorded ? 1 : 0.35;

        _time.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        _stateText.SetResourceReference(TextBlock.ForegroundProperty, "Capture.HudMutedTextBrush");
        _dot.SetResourceReference(Shape.FillProperty, "Shotwise.CaptureSolidBrush");
        _stateText.Text = CS.Get("Hud_Recording");
        _stateText.Margin = new Thickness(10, 0, 8, 0);

        var main = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        main.Children.Add(_dot);
        main.Children.Add(_time);
        if (!compact) main.Children.Add(_stateText); else _stateText.Visibility = Visibility.Collapsed;
        main.Children.Add(Ui.VSeparator(22, "Capture.HudSepBrush"));
        main.Children.Add(_pause);
        main.Children.Add(Gap(2));
        main.Children.Add(_stop);
        if (!compact)
        {
            main.Children.Add(Ui.VSeparator(22, "Capture.HudSepBrush"));
            main.Children.Add(Ui.Row(_mic, _micMeter));
            main.Children.Add(Ui.Row(_sys, _sysMeter));
            main.Children.Add(_cam);
            main.Children.Add(_draw);
            main.Children.Add(Ui.VSeparator(22, "Capture.HudSepBrush"));
            main.Children.Add(_restart);
            main.Children.Add(_cancel);
        }
        else
        {
            main.Children.Add(_draw);
        }

        var warnIcon = Ui.Icon("Icon.Warning.Color", 16);
        warnIcon.Margin = new Thickness(0, 0, 8, 0);
        _warning.Child = Ui.Row(warnIcon, _warningText);
        _warning.SetResourceReference(Border.BackgroundProperty, "Capture.HudHoverBrush");
        _warningText.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.HudTextBrush");

        var stack = new StackPanel();
        stack.Children.Add(main);
        stack.Children.Add(_warning);
        Content = stack;

        _clock.Tick += (_, _) => RefreshTime();
        _clock.Start();
        Closed += (_, _) => { _clock.Stop(); _dot.BeginAnimation(OpacityProperty, null); };
        StartPulse();
    }

    private static UIElement Gap(double w) => new Border { Width = w };

    private Button HudButton(string icon, string tip, RecordingCommand cmd, string style = "Capture.HudButton", double size = 18)
    {
        var b = new Button { ToolTip = tip }.WithStyle(style);
        SwUi.SetIcon(b, icon);
        SwUi.SetIconSize(b, size);
        Ui.AutomationProperties(b, tip);
        b.Click += (_, _) => Raise(cmd);
        return b;
    }

    private void Raise(RecordingCommand c) => CommandInvoked?.Invoke(c);

    private void StartPulse()
    {
        _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(750)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }

    private void StopPulse() => _dot.BeginAnimation(OpacityProperty, null);

    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Elapsed time currently shown: the recorder value, or the HUD's own wall clock while the recorder has not reported yet.</summary>
    private TimeSpan Current()
    {
        TimeSpan rec;
        try { rec = _elapsed(); } catch (Exception) { rec = TimeSpan.Zero; }
        if (rec + TimeSpan.FromSeconds(1) < _lastRecorder) _wall.Restart(); // recorder restarted
        _lastRecorder = rec;
        var wall = _wall.Elapsed;
        return rec > wall ? rec : wall;
    }

    private void RefreshTime()
    {
        if (_frozen) return;
        _time.Text = Format(Current());
    }

    public void SetState(RecorderState state)
    {
        bool wasPaused = _paused;
        _paused = state == RecorderState.Paused;
        bool busy = state is RecorderState.Stopping or RecorderState.Faulted;
        if (_paused || busy) _wall.Stop(); else _wall.Start();
        _frozen = busy;
        SwUi.SetIcon(_pause, _paused ? "Icon.Play.Filled" : "Icon.Pause.Filled");
        _pause.ToolTip = _paused ? CS.Get("Hud_Resume") : CS.Get("Hud_Pause");
        Ui.AutomationProperties(_pause, _paused ? CS.Get("Hud_Resume") : CS.Get("Hud_Pause"));
        _dot.SetResourceReference(Shape.FillProperty, _paused ? "Shotwise.WarningBrush" : "Shotwise.CaptureSolidBrush");
        if (_paused) StopPulse(); else if (wasPaused || !_dot.HasAnimatedProperties) StartPulse();
        _pause.IsEnabled = !busy; _stop.IsEnabled = !busy; _restart.IsEnabled = !busy; _cancel.IsEnabled = !busy;
        _stateText.Text = state switch { RecorderState.Paused => CS.Get("Hud_Paused"), RecorderState.Stopping => CS.Get("Hud_Stopping"), RecorderState.Faulted => CS.Get("Hud_Faulted"), _ => CS.Get("Hud_Recording") };
        _time.Text = Format(Current());
    }

    public void SetLevels(AudioLevels l)
    {
        _micMeter.Value = _micMuted ? 0 : Math.Clamp(l.Microphone, 0, 1);
        _sysMeter.Value = _sysMuted ? 0 : Math.Clamp(l.SystemAudio, 0, 1);
    }

    public void SetMicMuted(bool muted)
    {
        _micMuted = muted;
        SwUi.SetIcon(_mic, muted ? "Icon.MicOff.Regular" : "Icon.Mic.Regular");
        _mic.ToolTip = muted ? CS.Get("Hud_UnmuteMic") : CS.Get("Hud_MuteMic");
        if (muted) _micMeter.Value = 0;
    }

    public void SetSystemMuted(bool muted)
    {
        _sysMuted = muted;
        SwUi.SetIcon(_sys, muted ? "Icon.SpeakerMute.Regular" : "Icon.Speaker.Regular");
        _sys.ToolTip = muted ? CS.Get("Hud_UnmuteSystem") : CS.Get("Hud_MuteSystem");
        if (muted) _sysMeter.Value = 0;
    }

    public void SetDrawActive(bool active) => _draw.IsChecked = active;

    public void ShowWarning(string? message)
    {
        _warningText.Text = message ?? "";
        _warning.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
