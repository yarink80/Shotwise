using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;

namespace Shotwise.Capture;

public sealed class StepCaptureService(IScreenCaptureService screen, IMonitorService monitors, IWindowFinder windows, IElementFinder elements, IAppPaths paths, ILogger<StepCaptureService> logger) : IStepCaptureService
{
    public IStepCaptureSession CreateSession(StepCaptureOptions o) => new Session(o, screen, monitors, windows, elements, paths, logger);
    private sealed class Session : IStepCaptureSession
    {
        private readonly StepCaptureOptions options;
        private readonly IScreenCaptureService screen;
        private readonly IMonitorService monitors;
        private readonly IWindowFinder windows;
        private readonly IElementFinder elements;
        private readonly ILogger logger;
        private readonly string folder;
        private readonly List<StepCaptured> steps = new();
        private readonly Channel<(PointI Point, string Action, DateTimeOffset At)> clicks = Channel.CreateBounded<(PointI, string, DateTimeOffset)>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private readonly CancellationTokenSource shutdown = new();
        private MessageThread? worker;
        private Native.HookProc? callback;
        private IntPtr hook;
        private Task? consume;
        private Task<StepCaptureResult>? finish;
        private int paused, count;
        private bool disposed;
        public int Count => Volatile.Read(ref count);
        public event EventHandler<StepCaptured>? Captured;
        internal Session(StepCaptureOptions options, IScreenCaptureService screen, IMonitorService monitors, IWindowFinder windows, IElementFinder elements, IAppPaths paths, ILogger logger)
        {
            if (options.MaxSteps < 1 || options.RegionSizePx < 1 || options.DelayAfterClickMs < 0) throw new ArgumentOutOfRangeException(nameof(options));
            this.options = options; this.screen = screen; this.monitors = monitors; this.windows = windows; this.elements = elements; this.logger = logger;
            folder = options.OutputFolder ?? Path.Combine(paths.Temp, "steps", Guid.NewGuid().ToString("N"));
        }
        public async Task StartAsync(CancellationToken ct)
        {
            await lifecycle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (worker is not null || finish is not null) throw new InvalidOperationException("Step session cannot be started twice.");
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(folder);
                worker = new(logger);
                callback = (code, message, data) =>
                {
                    if (code >= 0 && Volatile.Read(ref paused) == 0 && Count < options.MaxSteps && message is 0x201 or 0x204 or 0x207)
                    {
                        var click = Marshal.PtrToStructure<Native.MouseHook>(data);
                        // Clicks on Shotwise's own windows (the step HUD's Stop/Pause buttons) are not steps (R1-#9). The owner is resolved
                        // here rather than in the consumer because the HUD closes right after a Stop click; WindowFromPoint and
                        // GetWindowThreadProcessId are cheap win32k queries that send no messages, so the hook stays fast.
                        if ((click.Flags & 1) == 0 && !IsOwnWindowAt(click.Pt))
                            clicks.Writer.TryWrite((new(click.Pt.X, click.Pt.Y), message == 0x201 ? "leftClick" : message == 0x204 ? "rightClick" : "middleClick", DateTimeOffset.Now));
                    }
                    return Native.CallNextHookEx(hook, code, message, data);
                };
                // Read the hook error on the thread that called SetWindowsHookEx (R1-#10).
                var (installed, error) = await worker.Invoke(() => { var h = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0); return (h, h == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0); }, ct).ConfigureAwait(false);
                hook = installed;
                if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(error);
                consume = Task.Run(ConsumeAsync);
            }
            finally { lifecycle.Release(); }
        }
        private static bool IsOwnWindowAt(Native.Point pt)
        {
            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        public void Pause() => Volatile.Write(ref paused, 1);
        public void Resume() => Volatile.Write(ref paused, 0);
        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var click in clicks.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
                {
                    if (Count >= options.MaxSteps) continue;
                    try
                    {
                        var element = await elements.ElementAtAsync(click.Point, shutdown.Token).ConfigureAwait(false);
                        var monitor = monitors.GetMonitors().FirstOrDefault(m => m.BoundsPx.Contains(click.Point));
                        if (monitor is null) continue;
                        var window = windows.WindowAt(click.Point, Array.Empty<IntPtr>());
                        await Task.Delay(options.DelayAfterClickMs, shutdown.Token).ConfigureAwait(false);
                        var rect = options.Area switch
                        {
                            StepCaptureArea.Monitor => monitor.BoundsPx,
                            StepCaptureArea.Window when window is not null => window.BoundsPx,
                            _ => CaptureMath.CenteredRegion(click.Point, options.RegionSizePx, monitor.BoundsPx)
                        };
                        var image = await screen.CaptureRectAsync(rect, options.IncludeCursor, shutdown.Token).ConfigureAwait(false);
                        if (options.HighlightClick)
                        {
                            int cx = click.Point.X - rect.X, cy = click.Point.Y - rect.Y;
                            for (int y = Math.Max(0, cy - 15); y < Math.Min(image.Height, cy + 16); y++)
                            for (int x = Math.Max(0, cx - 15); x < Math.Min(image.Width, cx + 16); x++)
                            {
                                double distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                                double opacity = Math.Clamp(1.5 - Math.Abs(distance - 12), 0, 1);
                                int p = y * image.Stride + x * 4;
                                image.Pixels[p] = (byte)(image.Pixels[p] * (1 - opacity));
                                image.Pixels[p + 1] = (byte)(image.Pixels[p + 1] * (1 - opacity) + 180 * opacity);
                                image.Pixels[p + 2] = (byte)(image.Pixels[p + 2] * (1 - opacity) + 255 * opacity);
                            }
                        }
                        var step = new StepCaptured(Count + 1, image, click.Point, click.Action, element?.Name, click.At) { CaptureBoundsPx = rect };
                        await AtomicFile.WriteAllBytesAsync(Path.Combine(folder, $"step-{step.Index:D4}.png"), Shotwise.Core.Imaging.PngCodec.Encode(image), shutdown.Token).ConfigureAwait(false);
                        steps.Add(step); Volatile.Write(ref count, steps.Count);
                        // Persist metadata only, never the potentially enormous raw raster arrays.
                        await WriteActions().ConfigureAwait(false);
                        try { Captured?.Invoke(this, step); } catch (Exception ex) { logger.LogWarning(ex, "Step capture subscriber failed"); }
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                    catch (Exception ex) { logger.LogWarning(ex, "Click capture failed"); }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
        private async Task WriteActions()
        {
            var json = JsonSerializer.Serialize(steps.Select(s => new { s.Index, s.ClickPx, s.Action, s.ElementName, s.At, ImageFile = $"step-{s.Index:D4}.png" }), ShotwiseJson.Indented);
            await AtomicFile.WriteAllTextAsync(Path.Combine(folder, "actions.json"), json, shutdown.Token).ConfigureAwait(false);
        }
        public async Task<StepCaptureResult> StopAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            Task<StepCaptureResult> result;
            try { ObjectDisposedException.ThrowIf(disposed, this); finish ??= Finish(); result = finish; }
            finally { lifecycle.Release(); }
            return await result.ConfigureAwait(false);
        }
        private async Task<StepCaptureResult> Finish()
        {
            Pause();
            if (worker is not null && hook != IntPtr.Zero) { await worker.Invoke(() => Native.UnhookWindowsHookEx(hook)).ConfigureAwait(false); hook = IntPtr.Zero; }
            clicks.Writer.TryComplete();
            if (consume is not null) await consume.ConfigureAwait(false);
            Directory.CreateDirectory(folder); await WriteActions().ConfigureAwait(false);
            return new(steps.ToArray(), Path.Combine(folder, "actions.json"));
        }
        public async ValueTask DisposeAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return; disposed = true; shutdown.Cancel();
                if (finish is not null) { try { await finish.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                if (worker is not null && hook != IntPtr.Zero) await worker.Invoke(() => Native.UnhookWindowsHookEx(hook)).ConfigureAwait(false);
                clicks.Writer.TryComplete(); if (consume is not null) await consume.ConfigureAwait(false);
                worker?.Dispose(); shutdown.Dispose(); GC.KeepAlive(callback);
            }
            finally { lifecycle.Release(); }
        }
    }
}
