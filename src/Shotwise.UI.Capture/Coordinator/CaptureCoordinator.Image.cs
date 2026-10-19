using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;
using Shotwise.Core.Sharing;
using Shotwise.Core.Vision;
using Shotwise.UI.Capture.Hud;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Capture.Selection;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Resources;

namespace Shotwise.UI.Capture.Coordinator;

public sealed partial class CaptureCoordinator
{
    private enum Route { Image, Video, Scrolling, Panoramic, GrabText }

    private static Route RouteOf(CapturePreset p, SelectionResult s)
    {
        switch (s.Action)
        {
            case SelectionAction.RecordVideo: return Route.Video;
            case SelectionAction.Scrolling: return Route.Scrolling;
            case SelectionAction.Panoramic: return Route.Panoramic;
            case SelectionAction.GrabText: return Route.GrabText;
            case SelectionAction.CaptureImage: return Route.Image;
        }
        if (p.Mode == CaptureMode.Video) return Route.Video;
        return p.Selection switch
        {
            SelectionType.Panoramic => Route.Panoramic,
            SelectionType.ScrollingWindow => Route.Scrolling,
            SelectionType.GrabText => Route.GrabText,
            _ => Route.Image,
        };
    }

    /// <summary>One capture of the preset (no interval looping). Returns the outcome and the selection used (for repeat / interval).</summary>
    private async Task<(CaptureOutcome? Outcome, SelectionResult? Selection)> RunOnceAsync(CapturePreset preset, SelectionResult? reuse, Stopwatch sw, CancellationToken ct)
    {
        using var shield = OwnWindowShield.Apply(Prefs.HideAppDuringCapture);
        if (shield.HidSomething) await Task.Delay(150, ct);

        switch (preset.Selection)
        {
            case SelectionType.Clipboard: return (await CaptureFromClipboardAsync(preset, ct), null);
            case SelectionType.ListenToClipboard: StartListening(preset); return (null, null);
            case SelectionType.File: return (await CaptureFromFileAsync(preset, ct), null);
            case SelectionType.StepCapture: return (await StepCaptureAsync(preset, ct), null);
            case SelectionType.Menu when preset.Mode != CaptureMode.Video: return (await MenuCaptureAsync(preset, ct), null);
        }

        while (true)
        {
            ScreenSnapshot? snap = null;
            var sel = reuse;
            bool live = reuse is not null;
            if (sel is null)
            {
                sel = await ImmediateSelectionAsync(preset);
                if (sel is null)
                {
                    var screen = Need<IScreenCaptureService>();
                    long snapStart = sw.ElapsedMilliseconds;
                    snap = await screen.SnapshotAllAsync(preset.IncludeCursor && preset.Mode != CaptureMode.Video, Prefs.SupportHdr, ct);
                    _log.LogInformation("Selection overlay timing: snapshot {Start}–{End} ms after hotkey", snapStart, sw.ElapsedMilliseconds);
                    sel = await Need<ISelectionService>().SelectAsync(BuildRequest(preset), snap, sw, ct);
                    if (sel is null) return (null, null);
                }
            }

            if (sel.RegionPx.Width < SelectionController.MinSize || sel.RegionPx.Height < SelectionController.MinSize)
            {
                await ShowMessageAsync(CS.Get("Error_CaptureTitle"), CS.Format("Selection_TooSmall", SelectionController.MinSize), MessageKind.Warning);
                return (null, null);
            }

            switch (RouteOf(preset, sel))
            {
                case Route.Video:
                {
                    var video = await RecordVideoAsync(preset, sel, ct);
                    if (video.Redo) { reuse = null; sw.Restart(); continue; }
                    return (video.Outcome, video.Outcome is null ? null : sel);
                }
                case Route.Scrolling:
                    return (await ScrollingCaptureAsync(preset, sel, ct), sel);
                case Route.Panoramic:
                    return (await PanoramicCaptureAsync(preset, sel, ct), sel);
                case Route.GrabText:
                    return (await GrabTextAsync(preset, sel, snap, live, ct), sel);
            }

            // ---- plain image
            if (preset.Timing.DelaySeconds is > 0)
            {
                // Delayed capture: the frozen snapshot is discarded and the screen is captured live after the countdown.
                snap = null;
                live = true;
                if (!await WaitDelayAsync(preset.Timing, sel.RegionPx, ct)) return (null, null);
            }

            var image = await AcquireImageAsync(preset, sel, snap, live, ct);
            var md = await CollectMetadataAsync(preset, sel, ct);
            var cr = new CaptureResult(image, md);
            var processed = await ProcessImageAsync(cr, preset, ct);
            return (new CaptureOutcome(CaptureMode.Image, Image: processed), sel);
        }
    }

    private async Task<bool> WaitDelayAsync(TimingOptions timing, RectI area, CancellationToken ct)
    {
        int seconds = timing.DelaySeconds ?? 0;
        if (seconds <= 0) return true;
        if (timing.ShowCountdown) return await CountdownWindow.RunAsync(seconds, area, ct);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return true;
    }

    private SelectionRequest BuildRequest(CapturePreset p)
    {
        var type = p.Selection;
        if (p.Mode == CaptureMode.Video && type is not (SelectionType.Region or SelectionType.Window or SelectionType.FixedRegion))
            type = SelectionType.Region;
        if (type is SelectionType.Fullscreen or SelectionType.Menu or SelectionType.StepCapture) type = SelectionType.Region;
        return new SelectionRequest(type, p.Mode, p.SelectionOptions, Prefs.ShowMagnifier, Prefs.SimplifiedCrosshairs, Prefs.ShowSelectionGuides, Prefs.ShowSelectionHelpHint, p.IncludeCursor);
    }

    /// <summary>Selections that need no overlay: fullscreen, "capture active window", fixed region at a fixed position.</summary>
    private async Task<SelectionResult?> ImmediateSelectionAsync(CapturePreset p)
    {
        var o = p.SelectionOptions;
        var monitors = Opt<IMonitorService>();
        switch (p.Selection)
        {
            case SelectionType.Fullscreen:
            {
                var list = monitors?.GetMonitors() ?? Array.Empty<MonitorInfo>();
                if (list.Count == 0) throw new ShotwiseException(ErrorCode.NoMonitor, "No monitor found.");
                if (o.FullscreenAllMonitors)
                    return new SelectionResult(SelectionType.Fullscreen, monitors!.VirtualScreenPx, null, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, null, true, true);
                var m = o.MonitorIndex is { } i && i >= 0 && i < list.Count ? list[i] : MonitorOfPoint(NativeUi.CursorPx()) ?? list[0];
                return new SelectionResult(SelectionType.Fullscreen, m.BoundsPx, null, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, m, true, false);
            }
            case SelectionType.Window when o.WindowCaptureActive:
            {
                var finder = Need<IWindowFinder>();
                var pid = Environment.ProcessId;
                var win = await Task.Run(() =>
                {
                    var fg = finder.Foreground();
                    if (fg is not null && fg.ProcessId != pid && !fg.IsMinimized) return fg;
                    return finder.EnumerateTopLevel().FirstOrDefault(w => w.ProcessId != pid && !w.IsMinimized && !w.BoundsPx.IsEmpty);
                });
                if (win is null) throw new ShotwiseException(ErrorCode.WindowNotFound, "No window to capture.");
                var rect = o.WindowClientOnly ? win.ClientPx : win.BoundsPx;
                return new SelectionResult(SelectionType.Window, rect, win, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, MonitorOfRect(rect));
            }
            case SelectionType.FixedRegion when o.UseFixedPosition && o.FixedX is { } fx && o.FixedY is { } fy:
            {
                var vb = monitors?.VirtualScreenPx ?? new RectI(0, 0, 1920, 1080);
                var rect = new RectI(fx, fy, Math.Max(SelectionController.MinSize, o.FixedWidth), Math.Max(SelectionController.MinSize, o.FixedHeight)).Intersect(vb);
                if (rect.IsEmpty) return null;
                return new SelectionResult(SelectionType.FixedRegion, rect, null, null, null, null, SelectionAction.Default, ScrollDirection.Vertical, MonitorOfRect(rect));
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ pixel acquisition

    private async Task<BgraImage> AcquireImageAsync(CapturePreset preset, SelectionResult sel, ScreenSnapshot? snap, bool live, CancellationToken ct)
    {
        var screen = Need<IScreenCaptureService>();
        bool cursor = preset.IncludeCursor;

        async Task<BgraImage> Crop(RectI rect)
            => !live && snap is not null ? screen.CropSnapshot(snap, rect) : await screen.CaptureRectAsync(rect, cursor, ct);

        if (sel.IsWindowSelection && sel.Window is { } win)
        {
            try
            {
                return await screen.CaptureWindowAsync(win.Hwnd, preset.SelectionOptions.WindowClientOnly, cursor, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation(ex, "Window capture failed; falling back to screen region");
            }
        }

        if (sel.Areas is { Count: > 1 } areas)
        {
            var union = sel.RegionPx;
            var canvas = new BgraImage(union.Width, union.Height);
            foreach (var a in areas)
            {
                var part = await Crop(a);
                canvas.Blit(part, a.X - union.X, a.Y - union.Y);
            }
            return canvas;
        }

        if (sel.PolygonPx is { Count: >= 3 } poly)
        {
            var b = sel.RegionPx;
            var raw = await Crop(b);
            var local = poly.Select(p => new PointI(p.X - b.X, p.Y - b.Y)).ToList();
            return screen.ApplyFreehandMask(raw, local);
        }

        return await Crop(sel.RegionPx);
    }

    private async Task<DocumentMetadata> CollectMetadataAsync(CapturePreset preset, SelectionResult sel, CancellationToken ct)
    {
        DocumentMetadata md;
        try
        {
            IntPtr? hwnd = sel.Window?.Hwnd;
            if ((hwnd is null || hwnd == IntPtr.Zero) && sel.Element is { OwnerHwnd: var owner } && owner != IntPtr.Zero) hwnd = owner;
            if (hwnd is null || hwnd == IntPtr.Zero)
            {
                var center = new PointI(sel.RegionPx.X + sel.RegionPx.Width / 2, sel.RegionPx.Y + sel.RegionPx.Height / 2);
                var finder = Opt<IWindowFinder>();
                hwnd = finder is null ? null : await Task.Run(() => finder.WindowAt(center, Array.Empty<IntPtr>())?.Hwnd);
            }
            var provider = Opt<ICaptureMetadataProvider>();
            md = provider is null ? DocumentMetadata.Create() : await provider.CollectAsync(hwnd, NativeUi.CursorPx(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Metadata collection failed");
            md = DocumentMetadata.Create();
        }
        return md with { SourcePresetId = preset.Id };
    }

    // ------------------------------------------------------------------ scrolling

    private async Task<CaptureOutcome?> ScrollingCaptureAsync(CapturePreset preset, SelectionResult sel, CancellationToken ct)
    {
        var scrolling = Need<IScrollingCaptureService>();
        var finder = Need<IWindowFinder>();
        var area = sel.RegionPx;
        var center = new PointI(area.X + area.Width / 2, area.Y + area.Height / 2);
        var win = sel.Window ?? await Task.Run(() => finder.WindowAt(center, Array.Empty<IntPtr>()), ct);
        if (win is null)
            throw new ShotwiseException(ErrorCode.WindowNotFound, "No window under the selection.");

        var probe = await scrolling.ProbeAsync(win.Hwnd, center, ct);
        // All-in-One: the direction arrow clicked in the mini toolbar; otherwise the preset's scrolling option.
        var requested = preset.Mode == CaptureMode.AllInOne ? sel.ScrollDirection : preset.SelectionOptions.ScrollDirection;
        var direction = ClampDirection(requested, probe);
        if (direction is null)
        {
            await ShowMessageAsync(CS.Get("Scroll_FailedTitle"), CS.Get("Scroll_NotScrollable"), MessageKind.Warning);
            return null;
        }
        // A whole window was selected (not a dragged region): scroll exactly the scrollable area the probe found.
        if (sel.Window is not null && probe.ScrollAreaPx is { IsEmpty: false } scrollArea)
            area = scrollArea;

        if (area.Width < SelectionController.MinSize || area.Height < SelectionController.MinSize)
        {
            await ShowMessageAsync(CS.Get("Scroll_FailedTitle"), CS.Format("Selection_TooSmall", SelectionController.MinSize), MessageKind.Warning);
            return null;
        }

        bool requireStart = Prefs.RequireStartButtonForScrolling;
        var hud = new ScrollingHudWindow(requireStart);
        var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hud.CancelRequested += () => { startTcs.TrySetResult(false); link.Cancel(); };
        hud.StartRequested += () => startTcs.TrySetResult(true);
        var work = MonitorOfRect(area)?.WorkAreaPx ?? PrimaryWorkArea();
        hud.PlaceNear(area, work);
        try
        {
            if (requireStart)
            {
                if (!await startTcs.Task) return null;
                hud.SetRunning();
            }
            if (preset.Timing.DelaySeconds is > 0 && !await WaitDelayAsync(preset.Timing, area, link.Token)) return null;

            var progress = new Progress<ScrollProgress>(p => hud.SetProgress(p.Frames, p.Preview));
            var req = new ScrollCaptureRequest(win.Hwnd, area, direction.Value, preset.SelectionOptions.ScrollStartAtTopLeft, preset.SelectionOptions.ScrollStepDelayMs);
            BgraImage image;
            try
            {
                image = await scrolling.CaptureAsync(req, progress, link.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Scrolling capture failed");
                hud.CloseSafe();
                var code = ex is ShotwiseException se ? se.Code : ErrorCode.ScrollingFailed;
                await ShowMessageAsync(CS.Get("Scroll_FailedTitle"), Localizer.ErrorMessage(code) + "\n\n" + CS.Get("Scroll_FailedTips"), MessageKind.Warning);
                return null;
            }
            hud.CloseSafe();
            var md = await CollectMetadataAsync(preset, sel with { Window = win }, ct);
            var processed = await ProcessImageAsync(new CaptureResult(image, md), preset, ct);
            return new CaptureOutcome(CaptureMode.Image, Image: processed);
        }
        finally { hud.CloseSafe(); }
    }

    private static ScrollDirection? ClampDirection(ScrollDirection wanted, ScrollProbe p)
    {
        bool v = p.CanScrollVertical, h = p.CanScrollHorizontal;
        if (!v && !h) return null;
        return wanted switch
        {
            ScrollDirection.Both => v && h ? ScrollDirection.Both : v ? ScrollDirection.Vertical : ScrollDirection.Horizontal,
            ScrollDirection.Horizontal => h ? ScrollDirection.Horizontal : ScrollDirection.Vertical,
            _ => v ? ScrollDirection.Vertical : ScrollDirection.Horizontal,
        };
    }

    // ------------------------------------------------------------------ panoramic

    private async Task<CaptureOutcome?> PanoramicCaptureAsync(CapturePreset preset, SelectionResult sel, CancellationToken ct)
    {
        var service = Need<IPanoramicCaptureService>();
        var monitor = sel.Monitor ?? MonitorOfRect(sel.RegionPx);
        var area = monitor is null ? sel.RegionPx : sel.RegionPx.Intersect(monitor.BoundsPx);
        if (area.Width < SelectionController.MinSize || area.Height < SelectionController.MinSize)
        {
            await ShowMessageAsync(CS.Get("Error_CaptureTitle"), CS.Format("Selection_TooSmall", SelectionController.MinSize), MessageKind.Warning);
            return null;
        }

        var hud = new PanoramicHudWindow();
        var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hud.StartRequested += () => startTcs.TrySetResult(true);
        hud.StopRequested += () => stopTcs.TrySetResult(true);
        hud.CancelRequested += () => { startTcs.TrySetResult(false); stopTcs.TrySetResult(false); };
        using var reg = ct.Register(() => { startTcs.TrySetResult(false); stopTcs.TrySetResult(false); });
        hud.PlaceNear(area, monitor?.WorkAreaPx ?? PrimaryWorkArea(), 14);
        await using var session = service.CreateSession();
        int lastPreview = 0;
        session.PreviewUpdated += (_, img) =>
        {
            // Throttle UI updates to ~6 per second; frames arrive on a worker thread.
            var now = Environment.TickCount;
            if (now - lastPreview < 160) return;
            lastPreview = now;
            hud.Dispatcher.BeginInvoke(new Action(() => hud.SetPreview(img)), System.Windows.Threading.DispatcherPriority.Background);
        };
        try
        {
            if (!await startTcs.Task) return null;
            await session.StartAsync(area, ct);
            hud.SetCapturing();
            if (!await stopTcs.Task) return null;
            hud.SetStitching();
            var image = await session.StopAndStitchAsync(ct);
            hud.CloseSafe();
            var md = await CollectMetadataAsync(preset, sel, ct);
            var processed = await ProcessImageAsync(new CaptureResult(image, md), preset, ct);
            return new CaptureOutcome(CaptureMode.Image, Image: processed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Panoramic capture failed");
            hud.CloseSafe();
            var code = ex is ShotwiseException se ? se.Code : ErrorCode.StitchingFailed;
            await ShowMessageAsync(CS.Get("Error_CaptureTitle"), Localizer.ErrorMessage(code), MessageKind.Warning);
            return null;
        }
        finally { hud.CloseSafe(); }
    }

    // ------------------------------------------------------------------ menu

    private async Task<CaptureOutcome?> MenuCaptureAsync(CapturePreset preset, CancellationToken ct)
    {
        var service = Need<IMenuCaptureService>();
        int seconds = Math.Max(3, preset.Timing.DelaySeconds ?? 5);
        var hud = new WaitHud(CS.Get("Toolbar_Cancel"));
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hud.StopRequested += () => link.Cancel();
        hud.Show();
        hud.PlaceTopCenter(PrimaryWorkArea());
        var end = DateTimeOffset.Now.AddSeconds(seconds);
        var ticker = Task.Run(async () =>
        {
            try
            {
                while (DateTimeOffset.Now < end && !link.IsCancellationRequested)
                {
                    var left = Math.Ceiling((end - DateTimeOffset.Now).TotalSeconds);
                    hud.Dispatcher.Invoke(() => hud.SetText(CS.Get("Menu_OpenNow"), CS.Format("Menu_Remaining", left)));
                    await Task.Delay(250, link.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        BgraImage? image;
        try
        {
            image = await service.CaptureMenusAsync(preset.SelectionOptions.MenuIncludeMenuBar, preset.SelectionOptions.MenuCascaded, TimeSpan.FromSeconds(seconds), link.Token);
        }
        finally
        {
            link.Cancel();
            await ticker;
            hud.CloseSafe();
        }
        if (image is null)
        {
            await ShowMessageAsync(CS.Get("Error_CaptureTitle"), CS.Get("Menu_NothingCaptured"), MessageKind.Info);
            return null;
        }
        var md = (Opt<ICaptureMetadataProvider>() is { } p ? await SafeMetadata(p, ct) : DocumentMetadata.Create()) with { SourcePresetId = preset.Id };
        var processed = await ProcessImageAsync(new CaptureResult(image, md), preset, ct);
        return new CaptureOutcome(CaptureMode.Image, Image: processed);
    }

    private async Task<DocumentMetadata> SafeMetadata(ICaptureMetadataProvider p, CancellationToken ct)
    {
        try { return await p.CollectAsync(null, NativeUi.CursorPx(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Metadata failed"); return DocumentMetadata.Create(); }
    }

    // ------------------------------------------------------------------ grab text

    private async Task<CaptureOutcome?> GrabTextAsync(CapturePreset preset, SelectionResult sel, ScreenSnapshot? snap, bool live, CancellationToken ct)
    {
        if (Opt<IPolicyService>() is { } policy && !policy.IsAllowed(PolicyFeature.Ocr))
            throw new ShotwiseException(ErrorCode.PolicyDisabled, "OCR is disabled by policy.");
        var ocr = Opt<IOcrService>() ?? throw new ShotwiseException(ErrorCode.OcrUnavailable, "No OCR engine available.");
        if (preset.Timing.DelaySeconds is > 0 && !live)
        {
            if (!await WaitDelayAsync(preset.Timing, sel.RegionPx, ct)) return null;
            snap = null; live = true;
        }
        var image = await AcquireImageAsync(preset, sel, snap, live, ct);
        OcrResult result;
        try { result = await ocr.RecognizeAsync(image, null, null, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not ShotwiseException)
        {
            throw new ShotwiseException(ErrorCode.OcrUnavailable, ex.Message);
        }
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            await ShowMessageAsync(CS.Get("GrabText_Title"), CS.Get("GrabText_NoText"), MessageKind.Info);
            return null;
        }

        Need<IClipboardWriter>().SetText(result.Text);
        TableResult? table = null;
        try
        {
            var t = Opt<ITableExtractor>()?.Extract(result);
            if (t is not null && t.Rows.Count > 1 && t.Rows.Any(r => r.Count > 1)) table = t;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Table extraction failed"); }
        Toast(CS.Get("GrabText_Title"), CS.Get("GrabText_Copied"), NotificationKind.Success);

        if (Opt<IDialogService>() is { } dialogs)
            await dialogs.ShowOcrResult(new OcrResultRequest(result, table));

        var md = await CollectMetadataAsync(preset, sel, ct);
        md = md with { OcrText = result.Text };
        var cr = new CaptureResult(image, md);
        if (preset.PreviewInEditor || preset.Outputs.Count > 0)
            cr = await ProcessImageAsync(cr, preset.WithoutClipboard(), ct);
        return new CaptureOutcome(CaptureMode.Image, Image: cr, Text: result.Text);
    }

    // ------------------------------------------------------------------ clipboard / file

    private async Task<CaptureOutcome?> CaptureFromClipboardAsync(CapturePreset preset, CancellationToken ct)
    {
        BgraImage? image = null;
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bs = System.Windows.Clipboard.GetImage();
                if (bs is not null) image = Shotwise.UI.Common.Imaging.BitmapInterop.ToBgraImage(bs);
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new ShotwiseException(ErrorCode.ClipboardUnavailable, ex.Message);
        }
        if (image is null) throw new ShotwiseException(ErrorCode.ClipboardEmpty, "The clipboard contains no image.");
        var md = DocumentMetadata.Create() with { SourcePresetId = preset.Id };
        var processed = await ProcessImageAsync(new CaptureResult(image, md), preset, ct);
        return new CaptureOutcome(CaptureMode.Image, Image: processed);
    }

    private async Task<CaptureOutcome?> CaptureFromFileAsync(CapturePreset preset, CancellationToken ct)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = CS.Get("File_OpenTitle"),
            Filter = CS.Get("File_Filter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return null;
        var codec = Need<IImageCodec>();
        var image = await Task.Run(() => codec.Load(dlg.FileName), ct);
        var md = DocumentMetadata.Create() with { SourcePresetId = preset.Id, WindowTitle = System.IO.Path.GetFileName(dlg.FileName) };
        var processed = await ProcessImageAsync(new CaptureResult(image, md), preset, ct);
        return new CaptureOutcome(CaptureMode.Image, Image: processed);
    }
}

internal static class PresetExtensions
{
    /// <summary>Copy of the preset that does not overwrite the clipboard (Grab Text already placed the text there).</summary>
    public static CapturePreset WithoutClipboard(this CapturePreset p)
    {
        var c = p.Clone();
        c.CopyToClipboard = false;
        return c;
    }
}
