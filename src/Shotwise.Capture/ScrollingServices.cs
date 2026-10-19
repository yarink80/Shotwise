using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Capture;

public sealed class ScrollingCaptureService(ElementFinder elements, IScreenCaptureService screen, IStitcher stitchers, ILogger<ScrollingCaptureService> logger) : IScrollingCaptureService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ScrollProbe> ProbeAsync(IntPtr hwnd, PointI at, CancellationToken ct)
    {
        var probe = await elements.Probe(hwnd, at, ct).ConfigureAwait(false);
        static bool HasScroll(IntPtr hwnd, int bar)
        {
            var info = new Native.ScrollInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.ScrollInfo>(), Mask = 0x17 };
            return Native.GetScrollInfo(hwnd, bar, ref info) && info.Max - (long)info.Min + 1 > info.Page;
        }
        return probe with { CanScrollVertical = probe.CanScrollVertical || HasScroll(hwnd, 1), CanScrollHorizontal = probe.CanScrollHorizontal || HasScroll(hwnd, 0) };
    }
    public async Task<BgraImage> CaptureAsync(ScrollCaptureRequest req, IProgress<ScrollProgress>? progress, CancellationToken ct)
    {
        if (req.AreaPx.IsEmpty || req.MaxFrames < 1 || req.StepDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(req));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var dpi = new DpiScope(); var root = Native.GetAncestor(req.Hwnd, 2);
                Native.SetForegroundWindow(root);
                if (Native.GetForegroundWindow() != root) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.ScrollingFailed, "Scrolling target could not be foregrounded.");
            }, ct).ConfigureAwait(false);
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (req.StartAtTopLeft) { await Move(req, true, true, ct); await Move(req, false, true, ct); }
            int count = 0;
            using var rows = stitchers.Create(req.Direction == ScrollDirection.Horizontal ? StitchDirection.Horizontal : StitchDirection.Vertical);
            bool both = req.Direction == ScrollDirection.Both;
            bool horizontal = req.Direction == ScrollDirection.Horizontal || both;
            while (count < req.MaxFrames)
            {
                using var row = both ? stitchers.Create(StitchDirection.Horizontal) : null;
                BgraImage? previous = null;
                while (count < req.MaxFrames)
                {
                    ct.ThrowIfCancellationRequested();
                    var frame = await screen.CaptureRectAsync(req.AreaPx, false, ct).ConfigureAwait(false);
                    if (previous is not null && previous.Pixels.AsSpan().SequenceEqual(frame.Pixels))
                    {
                        if (horizontal) break;
                        await Task.Run(() =>
                        {
                            if (Native.GetForegroundWindow() != Native.GetAncestor(req.Hwnd, 2)) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.ScrollingFailed, "Scrolling target lost foreground focus.");
                            ScrollInput.PageDown();
                        }, ct).ConfigureAwait(false);
                        await Task.Delay(Math.Max(16, req.StepDelayMs), ct).ConfigureAwait(false);
                        frame = await screen.CaptureRectAsync(req.AreaPx, false, ct).ConfigureAwait(false);
                        if (previous.Pixels.AsSpan().SequenceEqual(frame.Pixels)) break;
                    }
                    bool added = (row ?? rows).AddFrame(frame);
                    if (!added && previous is not null) { logger.LogDebug("Scrolling stitcher rejected frame {Frame}", count); break; }
                    previous = frame; count++;
                    progress?.Report(new(count, (row ?? rows).Preview(1024)));
                    await Move(req, horizontal, false, ct).ConfigureAwait(false);
                }
                if (!both) break;
                if (previous is null || !rows.AddFrame(row!.Result())) break;
                var before = await screen.CaptureRectAsync(req.AreaPx, false, ct).ConfigureAwait(false);
                await Move(req, false, false, ct).ConfigureAwait(false);
                var after = await screen.CaptureRectAsync(req.AreaPx, false, ct).ConfigureAwait(false);
                if (before.Pixels.AsSpan().SequenceEqual(after.Pixels)) break;
                await Move(req, true, true, ct).ConfigureAwait(false);
            }
            return rows.Result();
        }
        finally { gate.Release(); }
    }
    private async Task Move(ScrollCaptureRequest req, bool horizontal, bool reset, CancellationToken ct)
    {
        var centre = new PointI(req.AreaPx.X + req.AreaPx.Width / 2, req.AreaPx.Y + req.AreaPx.Height / 2);
        if (!await elements.Scroll(req.Hwnd, centre, horizontal, reset, ct).ConfigureAwait(false))
        {
            await Task.Run(() =>
            {
                using var dpi = new DpiScope();
                var info = new Native.ScrollInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.ScrollInfo>(), Mask = 0x17 };
                if (reset || Native.GetScrollInfo(req.Hwnd, horizontal ? 0 : 1, ref info))
                    Native.SendMessageTimeout(req.Hwnd, horizontal ? 0x114u : 0x115u, reset ? 6u : 3u, 0, 2, 250, out _);
                else
                {
                    if (Native.GetForegroundWindow() != Native.GetAncestor(req.Hwnd, 2)) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.ScrollingFailed, "Scrolling target lost foreground focus.");
                    try { ScrollInput.Wheel(req.AreaPx, horizontal); }
                    catch (System.ComponentModel.Win32Exception) when (!horizontal) { ScrollInput.PageDown(); }
                }
            }, ct).ConfigureAwait(false);
        }
        await Task.Delay(Math.Max(16, req.StepDelayMs), ct).ConfigureAwait(false);
    }
}

public sealed class PanoramicCaptureService(IScreenCaptureService screen, IMonitorService monitors, IStitcher stitchers, ILogger<PanoramicCaptureService> logger) : IPanoramicCaptureService
{
    public IPanoramicSession CreateSession() => new Session(screen, monitors, stitchers, logger);
    private sealed class Session(IScreenCaptureService screen, IMonitorService monitors, IStitcher stitchers, ILogger logger) : IPanoramicSession
    {
        private readonly SemaphoreSlim lifecycle = new(1, 1);
        private CancellationTokenSource? stop;
        private Task? loop;
        private IIncrementalStitcher? stitcher;
        private bool disposed;
        public event EventHandler<BgraImage>? PreviewUpdated;
        public async Task StartAsync(RectI areaPx, CancellationToken ct)
        {
            await lifecycle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (loop is not null) throw new InvalidOperationException("Panoramic session already started.");
                if (areaPx.IsEmpty) throw new ArgumentOutOfRangeException(nameof(areaPx));
                stitcher = stitchers.Create(StitchDirection.Auto); stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var token = stop.Token;
                var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                loop = Task.Run(() =>
                {
                    try
                    {
                        using var dpi = new DpiScope();
                        var monitor = monitors.GetMonitors().FirstOrDefault(m => m.BoundsPx.Intersect(areaPx) == areaPx) ?? throw new ArgumentException("Panoramic area must fit one monitor.", nameof(areaPx));
                        GraphicsCapture.FrameSource? source = null;
                        try { source = new(monitor.HMonitor, false, monitor.IsHdr, monitor.SdrWhiteLevelNits); }
                        catch (Exception ex) { logger.LogWarning(ex, "Panoramic WGC unavailable; using screen capture fallback"); }
                        using (source)
                        do
                        {
                            long started = Environment.TickCount64;
                            var frame = source is null ? screen.CaptureRectAsync(areaPx, false, token).GetAwaiter().GetResult() : source.NextFrame(token).Crop(new(areaPx.X - monitor.BoundsPx.X, areaPx.Y - monitor.BoundsPx.Y, areaPx.Width, areaPx.Height));
                            if (stitcher.AddFrame(frame))
                            {
                                var preview = stitcher.Preview(1024);
                                try { PreviewUpdated?.Invoke(this, preview); } catch (Exception ex) { logger.LogWarning(ex, "Panoramic preview subscriber failed"); }
                            }
                            first.TrySetResult();
                            token.WaitHandle.WaitOne((int)Math.Max(0, 67 - (Environment.TickCount64 - started)));
                        } while (!token.IsCancellationRequested);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { first.TrySetCanceled(token); }
                    catch (Exception ex) { first.TrySetException(ex); logger.LogError(ex, "Panoramic acquisition failed"); throw; }
                }, CancellationToken.None);
                await first.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally { lifecycle.Release(); }
        }
        public async Task<BgraImage> StopAndStitchAsync(CancellationToken ct)
        {
            await lifecycle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (loop is null) throw new InvalidOperationException("Panoramic session has not started.");
                stop!.Cancel(); await loop.WaitAsync(ct).ConfigureAwait(false); return stitcher!.Result();
            }
            finally { lifecycle.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try { if (disposed) return; disposed = true; stop?.Cancel(); if (loop is not null) { try { await loop.ConfigureAwait(false); } catch (Exception ex) { logger.LogDebug(ex, "Disposing failed panoramic session"); } } stitcher?.Dispose(); stop?.Dispose(); }
            finally { lifecycle.Release(); }
        }
    }
}
