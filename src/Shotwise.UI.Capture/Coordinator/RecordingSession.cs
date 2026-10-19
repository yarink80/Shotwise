using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Capture.ScreenDraw;
using Shotwise.UI.Capture.Selection;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Capture.Coordinator;

public enum RecordingEnd { Completed, Cancelled, Redo }

public sealed record RecordingOutcome(RecordingEnd End, VideoCaptureResult? Result);

/// <summary>
/// One recording from "ready" to "stopped": optional Ready window, countdown, recorder lifecycle, HUD (full or minimized), region
/// border, ScreenDraw overlay, audio meters, warnings and the global-hotkey actions (pause/resume, stop, draw, webcam swap).
/// All members must be used from the UI thread.
/// </summary>
internal sealed class RecordingSession
{
    private readonly IServiceProvider _sp;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly CapturePreset _preset;
    private readonly SelectionResult _sel;
    private readonly Dispatcher _dsp = Application.Current.Dispatcher;
    private readonly TaskCompletionSource<RecordingOutcome> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IVideoRecorder? _recorder;
    private RecordingHudWindow? _hud;
    private RecordingBorderWindow? _border;
    private ScreenDrawWindow? _draw;
    private ScreenDrawModel? _drawModel;
    private RecordingOptions _options = new();
    private bool _micMuted, _sysMuted, _webcamFull, _stopping, _started, _restarting;
    private RectI _region;
    private MonitorInfo? _monitor;
    private int _levelPending;
    private DispatcherTimer? _warningTimer;

    public RecordingSession(IServiceProvider sp, ISettingsService settings, ILogger log, CapturePreset preset, SelectionResult sel)
    {
        _sp = sp; _settings = settings; _log = log; _preset = preset; _sel = sel;
    }

    public bool IsRecording => _recorder is { State: RecorderState.Recording or RecorderState.Paused };

    private readonly List<string> _warnings = new();

    /// <summary>Distinct recorder warning keys raised during the session (e.g. Recording_SourceLost, Recording_SavedToAlternatePath).</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    private CaptureSettings Prefs => _settings.Current.Capture;
    private T? Opt<T>() where T : class => _sp.GetService<T>();

    // ------------------------------------------------------------------ setup

    private RecordingOptions BuildOptions()
    {
        var o = _preset.Recording;
        var p = Prefs;
        var policy = Opt<IPolicyService>();
        bool micOk = policy?.IsAllowed(PolicyFeature.Microphone) ?? true;
        bool sysOk = policy?.IsAllowed(PolicyFeature.SystemAudio) ?? true;
        bool camOk = (policy?.IsAllowed(PolicyFeature.Webcam) ?? true) && p.EnableCamera;
        int fps = o.Fps != 30 ? o.Fps : p.VideoFps;
        int quality = o.Quality != 70 ? o.Quality : p.VideoQuality;
        return o with
        {
            Fps = fps,
            Quality = quality,
            ScaleTo1080p = p.ScaleTo1080p,
            HardwareEncoding = o.HardwareEncoding && !p.DisableHardwareAcceleratedRecording,
            RecordMicrophone = o.RecordMicrophone && micOk,
            RecordSystemAudio = o.RecordSystemAudio && sysOk,
            Webcam = o.Webcam && camOk,
            Countdown = o.Countdown && p.ShowVideoCountdown,
            ShowReadyWindow = o.ShowReadyWindow && p.ShowReadyToRecordWindow,
            AutoStart = o.AutoStart || p.AutoStartVideoRecording,
        };
    }

    private RectI CurrentRegion()
        => _sel.Window is { } w ? NativeUi.ExtendedFrameBounds(w.Hwnd) ?? _sel.RegionPx : _sel.RegionPx;

    private RecordingTarget BuildTarget()
    {
        var monitors = Opt<IMonitorService>()?.GetMonitors();
        if (_sel.IsWindowSelection && _sel.Window is { } w) return new RecordingTarget(null, w.Hwnd, null);
        if (_sel.WholeMonitor && !_sel.AllMonitors && _sel.Monitor is { } m && monitors is not null)
        {
            int idx = monitors.ToList().FindIndex(x => x.DeviceName == m.DeviceName);
            if (idx >= 0) return new RecordingTarget(null, null, idx);
        }
        return new RecordingTarget(_region, null, null);
    }

    private string OutputPath()
    {
        var dir = !string.IsNullOrWhiteSpace(Prefs.RecordingTempFolder)
            ? Prefs.RecordingTempFolder
            : Path.Combine(Opt<IAppPaths>()?.Temp ?? Path.GetTempPath(), "Recordings");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
    }

    // ------------------------------------------------------------------ run

    public async Task<RecordingOutcome> RunAsync(CancellationToken ct)
    {
        var policy = Opt<IPolicyService>();
        if (policy is not null && !policy.IsAllowed(PolicyFeature.Video))
            throw new ShotwiseException(ErrorCode.VideoDisabledByPolicy, "Video recording is disabled by policy.");

        _region = CurrentRegion();
        var monitors = Opt<IMonitorService>()?.GetMonitors() ?? Array.Empty<MonitorInfo>();
        _monitor = _sel.Monitor ?? monitors.OrderByDescending(m => { var i = m.BoundsPx.Intersect(_region); return (long)i.Width * i.Height; }).FirstOrDefault();
        var work = _monitor?.WorkAreaPx ?? new RectI(0, 0, 1920, 1080);
        _options = BuildOptions();

        var devices = Opt<IAudioDeviceService>();
        var mics = devices?.Microphones() ?? Array.Empty<DeviceInfo>();
        var cams = devices?.Webcams() ?? Array.Empty<DeviceInfo>();
        string? micId = _options.MicrophoneId ?? devices?.DefaultMicrophone()?.Id;
        bool micMuted = Prefs.AlertWhenMicrophoneMuted && _options.RecordMicrophone && micId is not null && (devices?.IsMicrophoneMuted(micId) ?? false);

        // ---- ready window
        if (_options.ShowReadyWindow && !_options.AutoStart)
        {
            var ctx = new ReadyContext(_options, _region, work, DescribeTarget(), mics, cams, micMuted,
                Opt<INoiseSuppressor>()?.IsAvailable ?? false,
                policy?.IsAllowed(PolicyFeature.Microphone) ?? true, policy?.IsAllowed(PolicyFeature.SystemAudio) ?? true,
                (policy?.IsAllowed(PolicyFeature.Webcam) ?? true) && Prefs.EnableCamera,
                Opt<IWebcamPreviewFactory>(), _log);
            var ready = await ReadyToRecordWindow.ShowAsync(ctx);
            if (ready.Action == ReadyAction.Cancel) return new RecordingOutcome(RecordingEnd.Cancelled, null);
            if (ready.Action == ReadyAction.Redo) return new RecordingOutcome(RecordingEnd.Redo, null);
            _options = ready.Options;
        }
        else if (micMuted && Opt<IDialogService>() is { } dialogs)
        {
            var c = await dialogs.Confirm(new ConfirmRequest(CS.Get("Ready_MicMutedTitle"), CS.Get("Ready_MicMuted"), CS.Get("Ready_RecordAnyway"), CS.Get("Toolbar_Cancel")));
            if (!c.Confirmed) return new RecordingOutcome(RecordingEnd.Cancelled, null);
        }

        if (!_options.RecordMicrophone) _micMuted = true;

        // ---- recorder
        var recorder = Opt<IVideoRecorderFactory>()?.Create() ?? throw new ShotwiseException(ErrorCode.EncoderUnavailable, "No recorder is registered.");
        _recorder = recorder;
        recorder.StateChanged += OnStateChanged;
        recorder.Levels += OnLevels;
        recorder.Warning += OnWarning;
        try
        {
            await recorder.PrepareAsync(BuildTarget(), _options, OutputPath(), ct);

            if (_options.Countdown && !await CountdownWindow.RunAsync(Math.Max(1, Prefs.CountdownSeconds), _region, ct))
            {
                await recorder.CancelAsync();
                return new RecordingOutcome(RecordingEnd.Cancelled, null);
            }

            _drawModel = new ScreenDrawModel { Color = Prefs.ScreenDrawColor, FadeSeconds = Prefs.ScreenDrawFadeSeconds };
            recorder.AddOverlay(_drawModel);

            await recorder.StartAsync();
            _started = true;
            OpenWindows(work, monitors.Count);
            using var reg = ct.Register(() => _dsp.BeginInvoke(new Action(() => _ = EndAsync(cancel: true))));

            var outcome = await _end.Task;
            return outcome;
        }
        finally
        {
            recorder.StateChanged -= OnStateChanged;
            recorder.Levels -= OnLevels;
            recorder.Warning -= OnWarning;
            CloseWindows();
            _warningTimer?.Stop();
            try { await recorder.DisposeAsync(); } catch (Exception ex) { _log.LogDebug(ex, "Recorder dispose failed"); }
            _recorder = null;
        }
    }

    private string DescribeTarget()
    {
        if (_sel.IsWindowSelection && _sel.Window is { } w) return $"{w.Title} - {w.BoundsPx.Width} × {w.BoundsPx.Height}";
        return CS.Format("Ready_Region", _region.Width, _region.Height);
    }

    private void OpenWindows(RectI work, int monitorCount)
    {
        bool wholeMonitor = _monitor is not null && _region == _monitor.BoundsPx;
        bool compact = Prefs.MinimizeControlsWhenFullscreenOneMonitor && monitorCount <= 1 && wholeMonitor;

        _hud = new RecordingHudWindow(() => _recorder?.Elapsed ?? TimeSpan.Zero,
            _options.RecordMicrophone, _options.RecordSystemAudio, _options.Webcam, compact);
        _hud.CommandInvoked += OnHudCommand;
        _hud.SetState(RecorderState.Recording);
        _hud.SetMicMuted(false);
        _hud.SetSystemMuted(false);
        if (compact) _hud.PlaceTopCenter(work);
        else _hud.PlaceNear(_region, work);

        if (!wholeMonitor)
        {
            _border = new RecordingBorderWindow(_region, _sel.IsWindowSelection ? _sel.Window!.Hwnd : IntPtr.Zero);
            _border.Open();
        }
    }

    private void CloseWindows()
    {
        _hud?.CloseSafe(); _hud = null;
        _border?.Close(); _border = null;
        if (_draw is not null) { _draw.SetDrawing(false); _draw.Close(); _draw = null; }
    }

    // ------------------------------------------------------------------ recorder events (worker threads)

    private void OnStateChanged(object? sender, RecorderState s)
    {
        _dsp.BeginInvoke(new Action(() =>
        {
            _hud?.SetState(s);
            _border?.SetPaused(s == RecorderState.Paused);
            if (s == RecorderState.Faulted && !_end.Task.IsCompleted)
                _end.TrySetException(new ShotwiseException(ErrorCode.RecordingFailed, "The recorder reported a failure."));
            // FX-1: on source loss (window closed, microphone unplugged, device lost) or the low-disk stop the recorder finalizes the
            // MP4 itself and returns to Idle; StopAsync then hands back the partial result. Treat that as a normal Stop so the
            // recording is published/opened instead of leaving the HUD open. The live state is re-checked because the event is
            // marshalled late and a Restart passes through Idle.
            if (s == RecorderState.Idle && _started && !_stopping && !_restarting && _recorder is { State: RecorderState.Idle })
            {
                _log.LogInformation("Recorder stopped on its own; finishing the recording");
                _ = EndAsync(cancel: false);
            }
        }));
    }

    private void OnLevels(object? sender, AudioLevels l)
    {
        if (Interlocked.Exchange(ref _levelPending, 1) == 1) return;
        _dsp.BeginInvoke(new Action(() => { _hud?.SetLevels(l); Interlocked.Exchange(ref _levelPending, 0); }), DispatcherPriority.Background);
    }

    private void OnWarning(object? sender, string key)
    {
        _dsp.BeginInvoke(new Action(() =>
        {
            var text = CS.Warning(key);
            _log.LogWarning("Recorder warning: {Warning}", key);
            if (!_warnings.Contains(key)) _warnings.Add(key);
            _hud?.ShowWarning(text);
            _warningTimer?.Stop();
            _warningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _warningTimer.Tick += (_, _) => { _warningTimer?.Stop(); _hud?.ShowWarning(null); };
            _warningTimer.Start();
        }));
    }

    // ------------------------------------------------------------------ commands

    private void OnHudCommand(RecordingCommand c)
    {
        switch (c)
        {
            case RecordingCommand.PauseResume: _ = PauseResumeAsync(); break;
            case RecordingCommand.Stop: _ = EndAsync(cancel: false); break;
            case RecordingCommand.Cancel: _ = ConfirmCancelAsync(); break;
            case RecordingCommand.Restart: _ = RestartAsync(); break;
            case RecordingCommand.ToggleMic: ToggleMic(); break;
            case RecordingCommand.ToggleSystemAudio: ToggleSystemAudio(); break;
            case RecordingCommand.ToggleWebcamFullscreen: ToggleWebcamFullscreen(); break;
            case RecordingCommand.ToggleScreenDraw: ToggleScreenDraw(); break;
        }
    }

    public async Task PauseResumeAsync()
    {
        var r = _recorder;
        if (r is null || _stopping) return;
        try
        {
            if (r.State == RecorderState.Recording) await r.PauseAsync();
            else if (r.State == RecorderState.Paused) await r.ResumeAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Pause/resume failed"); }
    }

    public Task StopAsync() => EndAsync(cancel: false);

    private async Task ConfirmCancelAsync()
    {
        var r = _recorder;
        if (r is null) return;
        if (r.Elapsed > TimeSpan.FromSeconds(3) && Opt<IDialogService>() is { } dialogs)
        {
            var c = await dialogs.Confirm(new ConfirmRequest(CS.Get("Hud_CancelTitle"), CS.Get("Hud_CancelBody"), CS.Get("Hud_Discard"), CS.Get("Hud_KeepRecording"), IsDestructive: true));
            if (!c.Confirmed) return;
        }
        await EndAsync(cancel: true);
    }

    private async Task RestartAsync()
    {
        var r = _recorder;
        if (r is null || _stopping) return;
        _restarting = true;
        try { await r.RestartAsync(); _hud?.ShowWarning(null); }
        catch (Exception ex) { _log.LogWarning(ex, "Restart failed"); _end.TrySetException(ex); }
        finally { _ = _dsp.BeginInvoke(new Action(() => _restarting = false), DispatcherPriority.Background); }
    }

    private async Task EndAsync(bool cancel)
    {
        var r = _recorder;
        if (r is null || _stopping) return;
        _stopping = true;
        try
        {
            // A cancelled recording that the recorder already finalized on its own (source lost) is published: keep it.
            if (cancel && _started && r.State == RecorderState.Idle && !_restarting) cancel = false;
            if (cancel)
            {
                await r.CancelAsync();
                _end.TrySetResult(new RecordingOutcome(RecordingEnd.Cancelled, null));
            }
            else
            {
                _hud?.SetState(RecorderState.Stopping);
                var result = await r.StopAsync();
                result = result with { Metadata = result.Metadata with { SourcePresetId = _preset.Id } };
                _end.TrySetResult(new RecordingOutcome(RecordingEnd.Completed, result));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ending the recording failed");
            _end.TrySetException(ex);
        }
    }

    private void ToggleMic()
    {
        if (!_options.RecordMicrophone || _recorder is null) return;
        _micMuted = !_micMuted;
        _recorder.SetMicrophoneMuted(_micMuted);
        _hud?.SetMicMuted(_micMuted);
    }

    private void ToggleSystemAudio()
    {
        if (!_options.RecordSystemAudio || _recorder is null) return;
        _sysMuted = !_sysMuted;
        _recorder.SetSystemAudioMuted(_sysMuted);
        _hud?.SetSystemMuted(_sysMuted);
    }

    public void ToggleWebcamFullscreen()
    {
        if (!_options.Webcam || _recorder is null) return;
        _webcamFull = !_webcamFull;
        _recorder.SetWebcamFullscreen(_webcamFull);
    }

    public void ToggleScreenDraw()
    {
        if (_recorder is null || _drawModel is null || _monitor is null) return;
        if (_draw is null)
        {
            var dialogs = Opt<IDialogService>();
            Func<Shotwise.Core.Imaging.Argb, Task<Shotwise.Core.Imaging.Argb?>>? pick = dialogs is null ? null : async initial =>
            {
                var r = await dialogs.ShowColorPicker(new ColorPickerRequest(initial, false, CS.Get("Draw_MoreColors")));
                return r?.Color;
            };
            _draw = new ScreenDrawWindow(_drawModel, _monitor.BoundsPx, pick, (color, fade) => _settings.Update(s =>
            {
                s.Capture.ScreenDrawColor = color;
                s.Capture.ScreenDrawFadeSeconds = fade;
            }));
            _draw.ModeChanged += on => _hud?.SetDrawActive(on);
            _draw.Open();
        }
        _draw.SetDrawing(!_draw.IsDrawing);
    }
}
