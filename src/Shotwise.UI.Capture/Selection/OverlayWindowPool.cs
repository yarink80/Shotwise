using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;

namespace Shotwise.UI.Capture.Selection;

/// <summary>
/// Keeps one hidden <see cref="SelectionOverlayWindow"/> per monitor between selections (UI thread only).
/// <para>
/// Measured on a 3-monitor host (4K @125 % + two 1440×2560 @100 %): creating a full-monitor WPF window (HWND + render target) costs
/// ~130 ms each on the UI thread, ~400 ms for three, while re-showing an existing hidden window costs ~5–15 ms. Windows are created
/// at idle after startup (<see cref="Warm"/>) or by the first selection, and rebuilt when the monitor layout (device, bounds, DPI)
/// changes. Cost while idle: three hidden windows with their swap chains (about the size of the monitors' back buffers).
/// </para>
/// </summary>
internal sealed class OverlayWindowPool
{
    private readonly ILogger _log;
    private readonly List<SelectionOverlayWindow> _windows = new();
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private string? _key, _warmedKey;
    private bool _inUse, _stale;
    private int _generation;

    public OverlayWindowPool(ILogger log) => _log = log;

    internal static string KeyOf(IReadOnlyList<MonitorInfo> monitors)
        => string.Join(";", monitors.Select(m => $"{m.DeviceName}|{m.BoundsPx.X},{m.BoundsPx.Y},{m.BoundsPx.Width},{m.BoundsPx.Height}|{m.DpiScale:0.###}"));

    /// <summary>Windows for <paramref name="monitors"/> in the same order. Not pooled when the pool is already in use.</summary>
    public (IReadOnlyList<SelectionOverlayWindow> Windows, bool Pooled) Rent(IReadOnlyList<MonitorInfo> monitors)
    {
        if (_inUse) return (monitors.Select(m => new SelectionOverlayWindow(m)).ToList(), false);
        Reset(monitors);
        while (_windows.Count < monitors.Count) _windows.Add(new SelectionOverlayWindow(monitors[_windows.Count]));
        _inUse = true;
        return (_windows.ToList(), true);
    }

    public void Return(IReadOnlyList<SelectionOverlayWindow> windows, bool pooled)
    {
        foreach (var w in windows) w.Detach();
        if (!pooled) { foreach (var w in windows) CloseQuietly(w); return; }
        _inUse = false;
        if (_stale) { _stale = false; CloseAll(); }
    }

    /// <summary>Creates missing windows for the current monitors, one per idle dispatcher slot (never while a selection runs).</summary>
    public void Warm(IReadOnlyList<MonitorInfo> monitors)
    {
        if (_inUse || monitors.Count == 0 || Application.Current?.Dispatcher is not { } dispatcher) return;
        Reset(monitors);
        int generation = _generation;
        void Next()
        {
            if (_inUse || generation != _generation) return;
            if (_windows.Count >= _monitors.Count) { DryRun(dispatcher, generation); return; }
            var w = new SelectionOverlayWindow(_monitors[_windows.Count]);
            try { w.Prepare(); _windows.Add(w); }
            catch (Exception ex) { _log.LogDebug(ex, "Overlay window warm-up failed"); CloseQuietly(w); return; }
            dispatcher.BeginInvoke(new Action(Next), DispatcherPriority.ApplicationIdle);
        }
        dispatcher.BeginInvoke(new Action(Next), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Shows every warmed window once, cloaked (never composited), with throw-away content and hides it again: the first Show of a
    /// window and the first use of the overlay controls (JIT, styles) are paid here instead of on the first hotkey press.
    /// </summary>
    private void DryRun(Dispatcher dispatcher, int generation)
    {
        if (_warmedKey == _key) return;
        _warmedKey = _key;
        var windows = _windows.ToList();
        var ctl = new SelectionController(new SelectionRequest(SelectionType.Region, CaptureMode.AllInOne, SelectionOptions.Default), _monitors.Select(m => m.BoundsPx).Aggregate((a, b) => a.Union(b)), _monitors);
        var pixel = new Shotwise.Core.Imaging.BgraImage(1, 1);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, new byte[4], 4);
        bitmap.Freeze();
        _inUse = true;
        try
        {
            foreach (var w in windows) { w.Attach(ctl, bitmap, pixel); w.WarmFloatingCards(); w.ShowCloaked(); }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Overlay dry run failed"); }
        dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var w in windows) w.Detach();
            _inUse = false;
            if (_stale) { _stale = false; CloseAll(); }
        }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Display layout changed: drop the windows now, or when the running selection returns them.</summary>
    public void Invalidate()
    {
        _generation++;
        if (_inUse) _stale = true;
        else CloseAll();
    }

    private void Reset(IReadOnlyList<MonitorInfo> monitors)
    {
        var key = KeyOf(monitors);
        if (key == _key) return;
        if (_key is not null) _log.LogDebug("Monitor layout changed; rebuilding selection overlay windows");
        CloseAll();
        _key = key;
        _monitors = monitors.ToList();
        _generation++;
    }

    private void CloseAll()
    {
        foreach (var w in _windows) CloseQuietly(w);
        _windows.Clear();
        _key = null;
        _warmedKey = null;
        _monitors = Array.Empty<MonitorInfo>();
    }

    private static void CloseQuietly(Window w)
    {
        try { w.Close(); } catch (InvalidOperationException) { }
    }
}
