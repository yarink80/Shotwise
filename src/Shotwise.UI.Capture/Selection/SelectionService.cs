using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Imaging;

namespace Shotwise.UI.Capture.Selection;

/// <summary>Interactive selection over a frozen <see cref="ScreenSnapshot"/> (one overlay window per monitor).</summary>
public interface ISelectionService
{
    /// <summary>Shows the overlay and completes with the user's selection, or null when cancelled. Must be called from any thread; UI work is marshalled.</summary>
    Task<SelectionResult?> SelectAsync(SelectionRequest request, ScreenSnapshot snapshot, Stopwatch? sinceHotkey = null, CancellationToken ct = default);
}

internal sealed class SelectionService : ISelectionService
{
    private readonly IWindowFinder _windows;
    private readonly IElementFinder? _elements;
    private readonly IScrollingCaptureService? _scrolling;
    private readonly IMonitorService? _monitors;
    private readonly ILogger _log;

    private OverlayWindowPool? _pool;
    private bool _displayHooked;

    public SelectionService(IWindowFinder windows, IElementFinder? elements, IScrollingCaptureService? scrolling, IMonitorService? monitors, ILogger<SelectionService>? log = null)
    {
        _windows = windows; _elements = elements; _scrolling = scrolling; _monitors = monitors;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>UI thread: the overlay window pool, created on first use; a display change drops it and re-warms after the layout settles.</summary>
    private OverlayWindowPool Pool
    {
        get
        {
            _pool ??= new OverlayWindowPool(_log);
            if (!_displayHooked)
            {
                _displayHooked = true;
                SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    _pool?.Invalidate();
                    await Task.Delay(2000);
                    Prewarm();
                }));
            }
            return _pool;
        }
    }

    /// <summary>
    /// Creates the overlay windows for the current monitors at idle priority so the first capture does not pay for window creation.
    /// Safe to call repeatedly; no-op without a WPF application or monitor service.
    /// </summary>
    internal void Prewarm()
    {
        if (_monitors is null || Application.Current?.Dispatcher is not { } dispatcher) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(Prewarm), DispatcherPriority.ApplicationIdle); return; }
        try { Pool.Warm(_monitors.GetMonitors()); }
        catch (Exception ex) { _log.LogDebug(ex, "Selection overlay warm-up skipped"); }
    }

    public async Task<SelectionResult?> SelectAsync(SelectionRequest request, ScreenSnapshot snapshot, Stopwatch? sinceHotkey = null, CancellationToken ct = default)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (snapshot.Monitors.Count == 0) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.NoMonitor, "The snapshot contains no monitor.");
        // One copy per monitor into a frozen WPF bitmap, in parallel off the UI thread (~5 ms per 4K monitor).
        var bitmaps = await Task.WhenAll(snapshot.Monitors.Select(m => Task.Run(() => (BitmapSource)m.Image.ToBitmapSource()))).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<SelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.InvokeAsync(() => Run(request, snapshot, bitmaps, sinceHotkey, tcs, ct), DispatcherPriority.Send);
        return await tcs.Task.ConfigureAwait(false);
    }

    private void Run(SelectionRequest request, ScreenSnapshot snapshot, BitmapSource[] bitmaps, Stopwatch? since, TaskCompletionSource<SelectionResult?> tcs, CancellationToken ct)
    {
        var monitors = snapshot.Monitors.Select(m => m.Monitor).ToList();
        var bounds = monitors.Select(m => m.BoundsPx).Aggregate((a, b) => a.Union(b));
        var ctl = new SelectionController(request, bounds, monitors);
        long tStart = since?.ElapsedMilliseconds ?? 0;
        var pool = Pool;
        var (windows, pooled) = pool.Rent(monitors);
        for (int i = 0; i < windows.Count; i++) windows[i].Attach(ctl, bitmaps[i], snapshot.Monitors[i].Image);
        long tBuilt = since?.ElapsedMilliseconds ?? 0;

        var hover = new HoverResolver(ctl, request, _windows, _elements, () => windows.Select(w => w.Handle).Where(h => h != IntPtr.Zero).ToList(), _log);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => hover.Tick();
        CancellationTokenSource probeCts = new();
        bool cleaned = false;

        void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            timer.Stop();
            probeCts.Cancel();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            pool.Return(windows, pooled);
        }
        void OnDisplayChanged(object? s, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(new Action(ctl.Cancel));

        ctl.Changed += (_, _) => { foreach (var w in windows) w.Refresh(); };
        ctl.CursorNudgeRequested += (_, p) => NativeUi.SetCursorPos(p.X, p.Y);
        ctl.SelectionSettled += async (_, _) =>
        {
            probeCts.Cancel();
            probeCts = new CancellationTokenSource();
            ctl.Probe = null;
            await ProbeScrollAsync(ctl, probeCts.Token);
        };
        ctl.Finished += (_, result) =>
        {
            Cleanup();
            tcs.TrySetResult(result);
        };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        ct.Register(() => Application.Current.Dispatcher.BeginInvoke(new Action(ctl.Cancel)));

        var cursorMonitor = ctl.MonitorAt(NativeUi.CursorPx());
        var primary = windows.FirstOrDefault(w => w.Monitor.DeviceName == cursorMonitor?.DeviceName) ?? windows[0];
        primary.SetHelpHost(true);
        ctl.PointerMoved(NativeUi.CursorPx());
        // Shown cloaked: WPF lays out and renders the new frozen frame while DWM keeps the windows invisible.
        foreach (var w in windows) w.ShowCloaked();
        long tShown = since?.ElapsedMilliseconds ?? 0;

        // Background priority runs after the dispatcher's Render pass that committed the first frame; DwmFlush waits for the next
        // composition so the render thread has presented it. Only then are the windows uncloaked: no black or stale frame is shown.
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (cleaned) return;
            NativeUi.DwmFlush();
            if (cleaned) return;
            foreach (var w in windows) w.Reveal();
            long visible = since?.ElapsedMilliseconds ?? 0;
            primary.Activate();
            NativeUi.SetForegroundWindow(primary.Handle);
            primary.Focus();
            timer.Start();
            foreach (var w in windows) w.EnableDeferredContent();
            if (since is not null)
            {
                _log.LogInformation("Selection overlay timing: run start {A} ms, content attached {B} ms, shown cloaked {C} ms, revealed {D} ms ({Pooled})",
                    tStart, tBuilt, tShown, visible, pooled ? "pooled windows" : "new windows");
                foreach (var w in windows) _log.LogInformation("Selection overlay first frame on {Monitor} {Ms} ms after hotkey", w.Monitor.DeviceName, visible);
                _log.LogInformation("Selection overlay visible {Ms} ms after hotkey ({Monitors} monitor(s))", visible, windows.Count);
            }
        }), DispatcherPriority.Background);
    }

    private async Task ProbeScrollAsync(SelectionController ctl, CancellationToken ct)
    {
        if (_scrolling is null || !ctl.Request.IsAllInOne || ctl.Selection is not { } sel) return;
        try
        {
            var center = new PointI(sel.X + sel.Width / 2, sel.Y + sel.Height / 2);
            var win = ctl.SelectionWindow ?? await Task.Run(() => _windows.WindowAt(center, Array.Empty<IntPtr>()), ct);
            if (win is null) return;
            var probe = await _scrolling.ProbeAsync(win.Hwnd, center, ct);
            if (ct.IsCancellationRequested || ctl.IsFinished) return;
            if (ctl.Selection == sel)
            {
                ctl.SelectionWindow ??= win;
                ctl.Probe = probe;
                ctl.Invalidate();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Scroll probe failed");
        }
    }

    /// <summary>Throttled async hover target lookup (window or UIA element under the cursor).</summary>
    private sealed class HoverResolver
    {
        private readonly SelectionController _ctl;
        private readonly SelectionRequest _req;
        private readonly IWindowFinder _windows;
        private readonly IElementFinder? _elements;
        private readonly Func<List<IntPtr>> _ignore;
        private readonly ILogger _log;
        private PointI _last = new(int.MinValue, int.MinValue);
        private bool _busy;
        private CancellationTokenSource? _cts;

        public HoverResolver(SelectionController ctl, SelectionRequest req, IWindowFinder windows, IElementFinder? elements, Func<List<IntPtr>> ignore, ILogger log)
        { _ctl = ctl; _req = req; _windows = windows; _elements = elements; _ignore = ignore; _log = log; }

        public async void Tick()
        {
            if (_busy || _ctl.IsFinished || _ctl.Phase != SelectionPhase.Idle || !(_req.HoverWindows || _req.HoverElements)) return;
            var pt = _ctl.Cursor;
            if (pt == _last) return;
            _last = pt;
            _busy = true;
            try
            {
                var ignore = _ignore();
                if (_req.HoverElements)
                {
                    _cts?.Cancel();
                    var cts = _cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    ElementInfo? el = null;
                    try { if (_elements is not null) el = await _elements.ElementAtAsync(pt, cts.Token); }
                    catch (OperationCanceledException) { }
                    if (el is null)
                    {
                        var w = await Task.Run(() => _windows.WindowAt(pt, ignore));
                        if (w is not null) el = new ElementInfo(w.Title, "Window", w.BoundsPx, w.Hwnd);
                    }
                    if (_ctl.Cursor == pt) { _ctl.HoverElement = el; _ctl.Invalidate(); }
                }
                else
                {
                    var w = await Task.Run(() => _windows.WindowAt(pt, ignore));
                    if (_ctl.Cursor == pt || _ctl.Phase == SelectionPhase.Idle) { _ctl.HoverWindow = w; _ctl.Invalidate(); }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Hover lookup failed");
            }
            finally { _busy = false; }
        }
    }
}
