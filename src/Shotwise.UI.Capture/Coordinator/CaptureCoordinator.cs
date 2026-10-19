using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Capture.Selection;
using Shotwise.UI.Capture.Views;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Resources;

namespace Shotwise.UI.Capture.Coordinator;

/// <summary>
/// Orchestrates a capture run: preset -> timing (delay / interval / schedule) -> selection overlay -> engine capture -> metadata ->
/// preset effects -> outputs -> <see cref="ICaptureEvents"/>. Also owns global hotkeys, the capture window / widget and the
/// clipboard / printer / mobile "implicit capture" inputs. Engines are resolved lazily from DI by their Core interfaces.
/// </summary>
public sealed partial class CaptureCoordinator : ICaptureCoordinator, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly ISettingsService _settings;
    private readonly IPresetRepository _presets;
    private readonly ICaptureEvents _events;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private int _capturing;
    private bool _started;
    private bool _disposed;
    private bool _remoteWarned;
    private (CapturePreset Preset, SelectionResult? Selection)? _lastRun;

    private CaptureWindow? _captureWindow;
    private CaptureWidget? _widget;

    public CaptureCoordinator(IServiceProvider sp, ISettingsService settings, IPresetRepository presets, ICaptureEvents events, ILogger<CaptureCoordinator>? log = null)
    {
        _sp = sp;
        _settings = settings;
        _presets = presets;
        _events = events;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    private Dispatcher Dsp => Application.Current?.Dispatcher ?? throw new InvalidOperationException("No WPF application is running.");
    private T Need<T>() where T : notnull => _sp.GetRequiredService<T>();
    private T? Opt<T>() where T : class => _sp.GetService<T>();
    private CaptureSettings Prefs => _settings.Current.Capture;

    /// <inheritdoc />
    public bool IsCapturing => Volatile.Read(ref _capturing) != 0 || _recording is not null;

    /// <summary>Raised on the UI thread when a capture run starts/ends (the capture window disables its button meanwhile).</summary>
    public event EventHandler? IsCapturingChanged;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Idempotent startup: registers global hotkeys, wires the implicit inputs (clipboard / printer / mobile) and shows the
    /// capture window / widget according to settings. Call once after the host started (see <see cref="CaptureUiStartup"/>).
    /// </summary>
    public void Start(bool showWindows = true)
    {
        if (!Dsp.CheckAccess()) { Dsp.BeginInvoke(new Action(() => Start(showWindows))); return; }
        if (_started || _disposed) return;
        _started = true;
        RegisterHotkeys();
        WireInputs();
        _settings.Changed += OnSettingsChanged;
        _presets.Changed += OnPresetsChanged;
        // Overlay latency (CAP-19): build the per-monitor selection windows at idle so the first hotkey press does not pay ~130 ms per monitor.
        if (Opt<ISelectionService>() is SelectionService selection) selection.Prewarm();
        // ...and run the GDI snapshot path once on a 1×1 area so its first real use is not paying for JIT (~80 ms measured).
        Dsp.BeginInvoke(new Action(() =>
        {
            if (Opt<IScreenCaptureService>() is { } screen)
                _ = screen.CaptureRectAsync(new RectI(0, 0, 1, 1), false).ContinueWith(t => _log.LogDebug(t.Exception, "Snapshot warm-up failed"), TaskContinuationOptions.OnlyOnFaulted);
        }), DispatcherPriority.ApplicationIdle);
        if (showWindows)
        {
            if (_settings.Current.General.ShowCaptureWindowOnStartup) ShowCaptureWindow();
            if (_settings.Current.General.ShowCaptureWidget) ToggleWidget(true);
        }
        _log.LogInformation("Capture UI started");
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        Dsp.BeginInvoke(new Action(() =>
        {
            if (e.Affects(nameof(AppSettings.Hotkeys))) RegisterHotkeysIfConfigChanged();
            if (e.Affects(nameof(AppSettings.Capture))) UpdateClipboardWatcher();
            if (e.Affects(nameof(AppSettings.General)))
            {
                bool wantWidget = _settings.Current.General.ShowCaptureWidget;
                if (wantWidget != (_widget is not null)) ToggleWidget(wantWidget);
            }
        }));
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => Dsp.BeginInvoke(new Action(RegisterHotkeys));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _presets.Changed -= OnPresetsChanged;
        UnwireInputs();
        try { Opt<IHotkeyService>()?.UnregisterAll(); } catch (Exception ex) { _log.LogDebug(ex, "Hotkey cleanup failed"); }
        _runCts?.Cancel();
    }

    // ------------------------------------------------------------------ windows

    /// <inheritdoc />
    public void ShowCaptureWindow()
    {
        if (!Dsp.CheckAccess()) { Dsp.BeginInvoke(new Action(ShowCaptureWindow)); return; }
        if (_captureWindow is null)
        {
            _captureWindow = new CaptureWindow(this, _sp);
            _captureWindow.Closed += (_, _) => _captureWindow = null;
        }
        if (!_captureWindow.IsVisible) _captureWindow.Show();
        if (_captureWindow.WindowState == WindowState.Minimized) _captureWindow.WindowState = WindowState.Normal;
        _captureWindow.Activate();
    }

    /// <inheritdoc />
    public void ToggleWidget(bool show)
    {
        if (!Dsp.CheckAccess()) { Dsp.BeginInvoke(new Action(() => ToggleWidget(show))); return; }
        if (show)
        {
            if (_widget is null)
            {
                _widget = new CaptureWidget(this, _sp);
                _widget.Closed += (_, _) => _widget = null;
            }
            _widget.Open();
        }
        else if (_widget is not null)
        {
            var w = _widget; _widget = null;
            w.Close();
        }
        if (_settings.Current.General.ShowCaptureWidget != show) _settings.Update(s => s.General.ShowCaptureWidget = show);
    }

    /// <summary>Shows/hides all application windows (the "Show/hide app" hotkey).</summary>
    public void ToggleAppVisibility()
    {
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w is not HudWindow && w.ShowInTaskbar).ToList();
        if (windows.Any(w => w.IsVisible && w.WindowState != WindowState.Minimized))
        {
            foreach (var w in windows) w.WindowState = WindowState.Minimized;
        }
        else
        {
            if (Opt<Shotwise.Core.Documents.IEditorHost>() is { IsVisible: false } editor) editor.ShowEditor();
            ShowCaptureWindow();
        }
    }

    // ------------------------------------------------------------------ public run API

    /// <inheritdoc />
    public Task<CaptureOutcome?> RunAsync(CapturePreset preset, CancellationToken ct = default)
        => OnUi(() => RunCoreAsync(preset, ct));

    /// <inheritdoc />
    public async Task RepeatLastAsync()
    {
        await OnUi(async () =>
        {
            if (_lastRun is { } last) { await RunCoreAsync(last.Preset, CancellationToken.None, last.Selection); return (CaptureOutcome?)null; }
            var preset = _presets.LastUsed ?? _presets.Active;
            return await RunCoreAsync(preset, CancellationToken.None);
        });
    }

    /// <summary>Cancels the current capture run or recording attempt (hotkey / UI).</summary>
    public void CancelCurrent()
    {
        _runCts?.Cancel();
    }

    private Task<T> OnUi<T>(Func<Task<T>> f)
    {
        var d = Dsp;
        return d.CheckAccess() ? f() : d.InvokeAsync(f).Task.Unwrap();
    }

    private void SetCapturing(bool on)
    {
        Interlocked.Exchange(ref _capturing, on ? 1 : 0);
        IsCapturingChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ run core

    private async Task<CaptureOutcome?> RunCoreAsync(CapturePreset source, CancellationToken ct, SelectionResult? reuse = null)
    {
        if (IsCapturing)
        {
            _log.LogInformation("Capture request ignored: another capture is in progress");
            return null;
        }
        var sw = Stopwatch.StartNew();
        var preset = source.Clone();
        if (!_remoteWarned && NativeUi.IsRemoteSession())
        {
            _remoteWarned = true;
            Toast(CS.Get("Remote_Title"), CS.Get("Remote_Body"), NotificationKind.Warning);
        }
        SetCapturing(true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runCts = cts;
        try
        {
            _presets.MarkUsed(source);
            return await RunTimedAsync(preset, reuse, sw, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Capture cancelled");
            return null;
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
            return null;
        }
        finally
        {
            _runCts = null;
            SetCapturing(false);
        }
    }

    /// <summary>Handles schedule and interval timing around single capture runs.</summary>
    private async Task<CaptureOutcome?> RunTimedAsync(CapturePreset preset, SelectionResult? reuse, Stopwatch sw, CancellationToken ct)
    {
        var timing = preset.Timing;
        if (timing.ScheduleAt is { } at && at > DateTimeOffset.Now)
        {
            if (!await WaitForScheduleAsync(at, ct)) return null;
            sw.Restart();
        }

        var (outcome, selection) = await RunOnceAsync(preset, reuse, sw, ct);
        if (outcome is null && selection is null) return null;
        _lastRun = (preset, selection);

        if (timing.IntervalSeconds is > 0 && selection is not null && preset.Mode != CaptureMode.Video)
        {
            await RunIntervalsAsync(preset, selection, timing.IntervalSeconds.Value, ct);
        }
        return outcome;
    }

    private async Task<bool> WaitForScheduleAsync(DateTimeOffset at, CancellationToken ct)
    {
        var hud = new WaitHud(CS.Get("Toolbar_Cancel"));
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hud.StopRequested += () => link.Cancel();
        hud.Show();
        hud.PlaceTopCenter(PrimaryWorkArea());
        try
        {
            while (true)
            {
                var left = at - DateTimeOffset.Now;
                if (left <= TimeSpan.Zero) return true;
                hud.SetText(CS.Format("Schedule_Waiting", at.ToLocalTime().ToString("g")), CS.Format("Schedule_Remaining", FormatSpan(left)));
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(50, left.TotalMilliseconds))), link.Token);
            }
        }
        catch (OperationCanceledException) { return false; }
        finally { hud.CloseSafe(); }
    }

    private async Task RunIntervalsAsync(CapturePreset preset, SelectionResult selection, int seconds, CancellationToken ct)
    {
        var hud = new WaitHud(CS.Get("Interval_Stop"));
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hud.StopRequested += () => link.Cancel();
        hud.Show();
        hud.PlaceTopCenter(PrimaryWorkArea());
        int count = 1;
        try
        {
            while (!link.IsCancellationRequested)
            {
                var next = DateTimeOffset.Now.AddSeconds(seconds);
                while (DateTimeOffset.Now < next)
                {
                    hud.SetText(CS.Format("Interval_Title", count), CS.Format("Interval_Next", Math.Ceiling((next - DateTimeOffset.Now).TotalSeconds)));
                    await Task.Delay(250, link.Token);
                }
                hud.Visibility = Visibility.Hidden;
                var tweaked = preset.Clone();
                tweaked.Timing = tweaked.Timing with { DelaySeconds = null, ScheduleAt = null };
                var (outcome, _) = await RunOnceAsync(tweaked, selection, Stopwatch.StartNew(), link.Token);
                hud.Visibility = Visibility.Visible;
                if (outcome is null) break;
                count++;
            }
        }
        catch (OperationCanceledException) { }
        finally { hud.CloseSafe(); }
    }

    private static string FormatSpan(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    internal RectI PrimaryWorkArea()
    {
        var mon = Opt<IMonitorService>()?.GetMonitors();
        var m = mon?.FirstOrDefault(x => x.IsPrimary) ?? mon?.FirstOrDefault();
        return m?.WorkAreaPx ?? new RectI(0, 0, 1920, 1080);
    }

    internal MonitorInfo? MonitorOfPoint(PointI p)
    {
        var mon = Opt<IMonitorService>()?.GetMonitors();
        return mon?.FirstOrDefault(m => m.BoundsPx.Contains(p)) ?? mon?.FirstOrDefault(m => m.IsPrimary) ?? mon?.FirstOrDefault();
    }

    internal MonitorInfo? MonitorOfRect(RectI r)
    {
        var mon = Opt<IMonitorService>()?.GetMonitors();
        if (mon is null) return null;
        return mon.OrderByDescending(m => { var i = m.BoundsPx.Intersect(r); return (long)i.Width * i.Height; }).FirstOrDefault();
    }

    // ------------------------------------------------------------------ errors

    private async Task ReportErrorAsync(Exception ex)
    {
        _log.LogError(ex, "Capture failed");
        var code = ex is ShotwiseException se ? se.Code : ErrorCode.CaptureFailed;
        string message = Localizer.ErrorMessage(code);
        try
        {
            if (Opt<IDialogService>() is { } dialogs)
                await dialogs.ShowMessage(new MessageRequest(CS.Get("Error_CaptureTitle"), message, MessageKind.Error, ex.Message));
            else
                Opt<INotificationService>()?.Show(CS.Get("Error_CaptureTitle"), message, NotificationKind.Error);
        }
        catch (Exception inner)
        {
            _log.LogWarning(inner, "Could not show the error dialog");
        }
    }

    internal async Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        try
        {
            if (Opt<IDialogService>() is { } d) await d.ShowMessage(new MessageRequest(title, message, kind));
            else Opt<INotificationService>()?.Show(title, message, kind switch { MessageKind.Error => NotificationKind.Error, MessageKind.Warning => NotificationKind.Warning, MessageKind.Success => NotificationKind.Success, _ => NotificationKind.Info });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Message could not be shown"); }
    }

    internal void Toast(string title, string message, NotificationKind kind = NotificationKind.Info, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (!_settings.Current.General.ShowToasts) return;
        try { Opt<INotificationService>()?.Show(title, message, kind, actions); }
        catch (Exception ex) { _log.LogDebug(ex, "Toast failed"); }
    }
}
