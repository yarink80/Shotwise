using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.App.Hosting;
using Shotwise.App.Tray;
using Shotwise.Core.Annotations;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.App.Smoke;

/// <summary>
/// Scripted end-to-end smoke run of the composed application (P3 C2) — see <see cref="SmokeOptions"/>. Each step is
/// independent: failures are recorded and the run continues. Results go to <c>smoke-results.md</c> (+ .json).
/// </summary>
internal sealed class SmokeRunner
{
    private readonly IServiceProvider _sp;
    private readonly App _app;
    private readonly SmokeOptions _o;
    private readonly ILogger _log;
    private readonly List<StepResult> _results = new();
    private readonly List<(string Name, string Path, string Detail)> _shots = new();
    private ShotDocument? _annotated;
    private string? _videoPath;

    private sealed record StepResult(string Name, bool Ok, string Detail, double Ms);

    public SmokeRunner(IServiceProvider sp, App app, SmokeOptions o)
    {
        _sp = sp;
        _app = app;
        _o = o;
        _log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Smoke");
    }

    private T Need<T>() where T : notnull => _sp.GetRequiredService<T>();

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_o.OutputDirectory);
        _log.LogInformation("Smoke run started; output {Dir}", _o.OutputDirectory);

        await Step("host-and-di", () =>
        {
            // The host was built with ValidateOnBuild/ValidateScopes; resolve the integration seams explicitly.
            var missing = new List<string>();
            void Check<T>(string n) { if (_sp.GetService(typeof(T)) is null) missing.Add(n); }
            Check<IVideoRecorderFactory>("IVideoRecorderFactory"); Check<IVideoFromImagesSessionFactory>("IVideoFromImagesSessionFactory");
            Check<IPrintService>("IPrintService"); Check<ICaptureAutosave>("ICaptureAutosave"); Check<Shotwise.Library.Catalog.IMediaThumbnailSource>("IMediaThumbnailSource");
            Check<IShareUi>("IShareUi"); Check<IDialogService>("IDialogService"); Check<IEditorHost>("IEditorHost"); Check<ICaptureCoordinator>("ICaptureCoordinator");
            Check<IClipboardImageWatcher>("IClipboardImageWatcher"); Check<IHotkeyService>("IHotkeyService"); Check<IUpdateService>("IUpdateService");
            var pages = _sp.GetServices<Shotwise.UI.Common.Preferences.IPreferencesPage>().Select(p => p.Id).ToList();
            if (pages.Count != 3) missing.Add($"IPreferencesPage x{pages.Count}");
            return Task.FromResult((missing.Count == 0, missing.Count == 0 ? $"all seams resolve; preference pages: {string.Join(", ", pages)}" : "missing: " + string.Join(", ", missing)));
        });

        await Step("library-initialized", async () =>
        {
            var startup = Need<StartupCoordinator>();
            try { await startup.LibraryReady; } catch { }
            var lib = Need<ILibraryService>();
            return (startup.LibraryError is null, startup.LibraryError?.Message ?? $"root {lib.LibraryRoot}");
        });

        await Step("tray-icon", () => Task.FromResult((Need<TrayIconManager>().IsCreated, Need<TrayIconManager>().IsCreated ? "notification-area icon created" : "not created: " + Need<TrayIconManager>().CreationError)));

        await Step("hotkeys-registered", () =>
        {
            var hk = Need<IHotkeyService>();
            var coordinator = (CaptureCoordinator)Need<ICaptureCoordinator>();
            var problems = coordinator.HotkeyProblems;
            var registered = string.Join(", ", hk.Registered.Select(kv => $"{kv.Key}={kv.Value.ToDisplayString()}"));
            // PrintScreen is often held by Windows' Snipping Tool or another screenshot app; Shotwise reports it (toast + one-time
            // prompt) - an environmental conflict, not a defect. Every other default must register.
            bool snipping = hk.IsPrintScreenTakenBySnipping();
            var real = problems.Where(p => !(p.Key.Key == VirtualKey.PrintScreen && p.Result == HotkeyRegistrationResult.InUseBySystemOrOtherApp)).ToList();
            bool recordingOnlyIdle = !hk.Registered.ContainsKey("capture.video.draw") && !hk.Registered.ContainsKey("capture.video.webcam");
            var detail = $"registered [{registered}]; PrintScreen owned by Snipping Tool={snipping}; recording-only keys idle={recordingOnlyIdle}"
                + (problems.Count > 0 ? "; reported: " + string.Join("; ", problems.Select(p => $"{p.Label} {p.Key.ToDisplayString()} {p.Result}")) : "");
            return Task.FromResult((real.Count == 0 && recordingOnlyIdle && hk.Registered.ContainsKey("capture.repeat") && hk.Registered.ContainsKey("capture.video.stop"), detail));
        });

        var inputPng = Path.Combine(_o.OutputDirectory, "smoke-input.png");
        await Step("open-png-via-command-line", async () =>
        {
            File.WriteAllBytes(inputPng, PngCodec.Encode(TestImage(960, 600)));
            _app.HandleCommandLine(CommandLineOptions.Parse(new[] { inputPng }));
            var host = Need<IEditorHost>();
            // Wait until the file is actually loaded and active (window title shows its name).
            var ok = await WaitUntil(() => host.IsVisible && EditorWindow() is { } w && w.Title.Contains("smoke-input", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(20));
            return (ok, ok ? $"editor window visible: '{EditorWindow()?.Title}'" : "editor did not open the file");
        });

        await Step("editor-renders-annotated-image", async () =>
        {
            var codec = Need<IImageCodec>();
            _annotated = ShotDocument.FromImage(codec.Load(inputPng), DocumentMetadata.Create() with { AppName = "Smoke", WindowTitle = "Annotated sample" }, codec);
            foreach (var a in SampleAnnotations()) _annotated.Pages[0].Annotations.Add(a);
            await Need<IEditorHost>().OpenAsync(_annotated);
            await WaitUntil(() => EditorWindow()?.Title.Contains("Smoke", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(10));
            await Task.Delay(1500);
            var w = EditorWindow();
            if (w is null) return (false, "no editor window");
            var shot = Shot(w, "editor-annotated");
            return (shot.NonBlank > 0.05 && w.Title.Contains("Smoke", StringComparison.Ordinal), $"'{w.Title}' {shot.Method} {shot.W}x{shot.H}, non-blank {shot.NonBlank:P0}");
        });

        var shotx = Path.Combine(_o.OutputDirectory, "smoke-annotated.shotx");
        await Step("save-and-reload-shotx", async () =>
        {
            var doc = _annotated ?? throw new InvalidOperationException("no annotated document");
            var ser = Need<IShotxSerializer>();
            await ser.SaveAsync(doc, shotx, Need<IDocumentRenderer>().RenderThumbnail(doc, 1024));
            var back = await ser.LoadAsync(shotx);
            int n0 = doc.Pages[0].Annotations.Count, n1 = back.Pages[0].Annotations.Count;
            return (n0 == n1 && new FileInfo(shotx).Length > 0, $"{n1}/{n0} annotations round-tripped, {new FileInfo(shotx).Length / 1024} KB");
        });

        await Step("library-lists-imported-shotx", async () =>
        {
            var lib = Need<ILibraryService>();
            var item = await lib.ImportFileAsync(shotx);
            var all = await lib.QueryAsync(new LibraryQuery(Take: 1000));
            var found = all.Any(i => i.Id == item.Id);
            var search = await lib.QueryAsync(new LibraryQuery(Text: "Annotated"));
            return (found, $"item {Path.GetFileName(item.Path)}; library has {all.Count} item(s); search 'Annotated' -> {search.Count}");
        });

        await Step("export-png-pdf-and-file-share-with-binding-options", async () =>
        {
            var doc = _annotated ?? throw new InvalidOperationException("no annotated document");
            var share = Need<IShareService>();
            var png = await share.MaterializeAsync(doc, "png", null, CancellationToken.None);
            var pdf = await share.MaterializeAsync(doc, "pdf", null, CancellationToken.None);
            var decoded = Need<IImageCodec>().Load(png[0].Path);
            var exportDir = Path.Combine(_o.OutputDirectory, "export");
            var options = JsonDocument.Parse(JsonSerializer.Serialize(new { folder = exportDir, promptEachTime = false })).RootElement;
            var r = await share.ShareAsync(KnownDestinations.File, png, Need<IShareUi>(), options, null, CancellationToken.None);
            var exported = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir).Length : 0;
            bool pdfOk = File.Exists(pdf[0].Path) && File.ReadAllBytes(pdf[0].Path).AsSpan(0, 4).SequenceEqual("%PDF"u8);
            // R2-P1 (FX-4A): preset bindings may only overlay allowlisted scalar options; a "folder" overlay must be ignored,
            // so the file goes to the File destination's own saved folder and nothing lands in the injected one.
            // The smoke run is isolated by SHOTWISE_DATA_ROOT: the File destination's default folder must live under it, never
            // in the real Documents\Shotwise.
            var dataRoot = Environment.GetEnvironmentVariable(Shotwise.Core.Platform.AppPaths.DataRootEnvironmentVariable);
            var isolated = string.IsNullOrWhiteSpace(dataRoot)
                || (r.Locations ?? Array.Empty<string>()).All(p => Path.GetFullPath(p).StartsWith(Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase));
            bool ok = decoded.Width == (int)doc.Pages[0].CanvasSize.Width && pdfOk && r.Outcome == ShareOutcome.Succeeded && exported == 0 && isolated;
            return (ok, $"png {decoded.Width}x{decoded.Height}; pdf {(pdfOk ? "valid" : "INVALID")} {new FileInfo(pdf[0].Path).Length / 1024} KB; file share -> {r.Outcome} (binding 'folder' overlay rejected: {exported} file in injected folder; saved {(isolated ? "inside" : "OUTSIDE")} the data root)");
        });

        await Step("capture-fixed-region-via-command-line", async () =>
        {
            var presets = Need<IPresetRepository>();
            var lib = Need<ILibraryService>();
            var bridge = Need<CaptureLibraryBridge>();
            var area = CaptureArea(640, 400);
            var preset = new CapturePreset
            {
                Name = "Smoke fixed region",
                Mode = CaptureMode.Image,
                Selection = SelectionType.FixedRegion,
                SelectionOptions = SelectionOptions.Default with { UseFixedPosition = true, FixedX = area.X, FixedY = area.Y, FixedWidth = area.Width, FixedHeight = area.Height },
                PreviewInEditor = true,
            };
            await presets.SaveAsync(preset);
            int before = (await lib.QueryAsync(new LibraryQuery(Take: 1000))).Count;
            int ocrBefore = bridge.OcrJobsStarted;
            var coordinator = Need<ICaptureCoordinator>();
            _app.HandleCommandLine(CommandLineOptions.Parse(new[] { "--capture=Smoke fixed region" }));
            await WaitUntil(() => coordinator.IsCapturing, TimeSpan.FromSeconds(3));
            var done = await WaitUntil(() => !coordinator.IsCapturing, TimeSpan.FromSeconds(30));
            await bridge.DrainAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(500);
            var items = await lib.QueryAsync(new LibraryQuery(Take: 1000));
            int added = items.Count - before;
            var newest = items.OrderByDescending(i => i.Created).FirstOrDefault();
            await presets.DeleteAsync(preset.Id);
            bool ok = done && added == 1 && newest is { Width: 640, Height: 400 };
            return (ok, $"capture finished={done}; library items added={added} (expected exactly 1 = single autosave owner); newest {newest?.Width}x{newest?.Height}; background OCR jobs {bridge.OcrJobsStarted - ocrBefore}");
        });

        if (_o.RecordVideo)
        {
            await Step("record-3s-video-fixed-region", async () =>
            {
                var coordinator = (CaptureCoordinator)Need<ICaptureCoordinator>();
                var bridge = Need<CaptureLibraryBridge>();
                var area = CaptureArea(640, 360);
                var preset = new CapturePreset
                {
                    Name = "Smoke video",
                    Mode = CaptureMode.Video,
                    Selection = SelectionType.FixedRegion,
                    SelectionOptions = SelectionOptions.Default with { UseFixedPosition = true, FixedX = area.X, FixedY = area.Y, FixedWidth = area.Width, FixedHeight = area.Height },
                    PreviewInEditor = true,
                    Recording = PresetDefaults.Recording with { RecordMicrophone = false, RecordSystemAudio = false, Webcam = false, Countdown = false, ShowReadyWindow = false, AutoStart = true },
                };
                var run = coordinator.RunAsync(preset);
                var recording = await WaitUntil(() => coordinator.IsRecording, TimeSpan.FromSeconds(20));
                if (!recording) { coordinator.CancelCurrent(); return (false, "recording did not start"); }
                await Task.Delay(1500);
                var hk = Need<IHotkeyService>();
                bool recordingKeys = hk.Registered.ContainsKey("capture.video.draw") && hk.Registered.ContainsKey("capture.video.webcam");
                if (Application.Current.Windows.OfType<Shotwise.UI.Capture.Hud.RecordingHudWindow>().FirstOrDefault() is { } hud) Shot(hud, "recording-hud");
                await Task.Delay(1700);
                coordinator.VideoStop();
                var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60));
                await bridge.DrainAsync(TimeSpan.FromSeconds(30));
                var video = outcome?.Video;
                if (video is null) return (false, "no video outcome");
                var item = await bridge.ForVideoAsync(video.Mp4Path);
                _videoPath = item?.Path ?? video.Mp4Path;
                var info = await Need<IVideoService>().ProbeAsync(_videoPath);
                bool released = !hk.Registered.ContainsKey("capture.video.draw");
                bool ok = info.Duration.TotalSeconds is > 2.0 and < 5.5 && info.Width > 0 && item is not null && recordingKeys && released;
                return (ok, $"{info.Width}x{info.Height} @{info.Fps:0.#} fps, {info.Duration.TotalSeconds:0.00} s, audio={info.HasAudio}; library item {(item is null ? "MISSING" : Path.GetFileName(item.Path))}; ScreenDraw/webcam hotkeys while recording={recordingKeys}, released after={released}");
            });

            await Step("video-view-renders", async () =>
            {
                if (_videoPath is null) return (false, "no video");
                await Need<IEditorHost>().OpenVideoAsync(_videoPath);
                await Task.Delay(2500);
                var w = EditorWindow();
                if (w is null) return (false, "no editor window");
                var shot = Shot(w, "editor-video-view");
                return (shot.NonBlank > 0.05, $"{shot.Method} {shot.W}x{shot.H}, non-blank {shot.NonBlank:P0}");
            });

            await Step("create-gif-from-video", async () =>
            {
                if (_videoPath is null) return (false, "no video");
                var gif = Need<IGifService>();
                var info = await Need<IVideoService>().ProbeAsync(_videoPath);
                var options = gif.DefaultsFor(GifPreset.Custom, info.Width, info.Height, info.Fps) with { Width = 320, Height = Math.Max(1, info.Height * 320 / Math.Max(1, info.Width)), Fps = 10 };
                var output = Path.Combine(_o.OutputDirectory, "smoke.gif");
                await gif.CreateFromVideoAsync(_videoPath, output, options.WithDitherMode(GifDitherMode.Ordered), null, CancellationToken.None);
                var anim = await gif.DecodeAsync(output, CancellationToken.None);
                return (anim.Frames.Count >= 15, $"{anim.Frames.Count} frames {anim.Width}x{anim.Height}, {anim.Duration.TotalSeconds:0.0} s, {new FileInfo(output).Length / 1024} KB (ordered dither)");
            });
        }

        await Step("library-view-renders", async () =>
        {
            Need<IEditorHost>().ShowLibrary();
            await Task.Delay(2000);
            var w = EditorWindow();
            if (w is null) return (false, "no editor window");
            var shot = Shot(w, "editor-library");
            return (shot.NonBlank > 0.05, $"{shot.Method} {shot.W}x{shot.H}, non-blank {shot.NonBlank:P0}");
        });

        await Step("capture-window-renders", async () =>
        {
            Need<ICaptureCoordinator>().ShowCaptureWindow();
            Window? w = null;
            await WaitUntil(() => (w = Application.Current.Windows.OfType<Shotwise.UI.Capture.Views.CaptureWindow>().FirstOrDefault(x => x.IsVisible)) is not null, TimeSpan.FromSeconds(10));
            if (w is null) return (false, "capture window not shown");
            await Task.Delay(1000);
            var shot = Shot(w, "capture-window");
            var dip = $"{w.ActualWidth:0}x{w.ActualHeight:0} DIP";
            // POLISH-1 review shots: 150 % / 200 % raster of the same layout, Dark theme, and "More options" expanded.
            var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            ShotScaled(w, "capture-window-150", 1.5);
            ShotScaled(w, "capture-window-200", 2.0);
            var theme = Need<Shotwise.UI.Common.Theming.IThemeService>();
            var requested = theme.RequestedTheme;
            theme.Apply(AppTheme.Dark);
            await Task.Delay(600);
            Shot(w, "capture-window-dark");
            theme.Apply(requested);
            await Task.Delay(300);
            var more = w.GetType().GetField("_more", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var refresh = w.GetType().GetMethod("RefreshMore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            double collapsedHeight = w.ActualHeight, expandedHeight = collapsedHeight;
            if (more is not null && refresh is not null)
            {
                more.SetValue(w, true); refresh.Invoke(w, null);
                await Task.Delay(600);
                expandedHeight = w.ActualHeight;
                Shot(w, "capture-window-more");
                more.SetValue(w, false); refresh.Invoke(w, null);
                await Task.Delay(300);
            }
            w.Hide();
            bool capturing = Need<ICaptureCoordinator>().IsCapturing;
            bool grows = expandedHeight > collapsedHeight + 40;
            return (shot.NonBlank > 0.05 && !capturing && grows, $"{shot.Method} {shot.W}x{shot.H} ({dip} at {dpi:P0}), non-blank {shot.NonBlank:P0}; More options grows {collapsedHeight:0} -> {expandedHeight:0} DIP; coordinator idle={!capturing}");
        });

        await Step("preferences-window-renders", async () =>
        {
            var (ok, detail) = await ShotModalAsync("preferences-hotkeys", d => d.ShowPreferences(new PreferencesRequest(PreferencesPage.Hotkeys)), "Preferences");
            // Startup & Updates: update status panel (version, channel, last check, status, buttons).
            var (updOk, updDetail) = await ShotModalAsync("preferences-startup-updates", d => d.ShowPreferences(new PreferencesRequest(PreferencesPage.CaptureGeneral)), "Preferences");
            ok &= updOk;
            detail += "; startup & updates: " + updDetail;
            // POLISH-1: About (app icon) and Help → Keyboard shortcuts (opened through the editor's own command binding).
            var (aboutOk, aboutDetail) = await ShotModalAsync("dialog-about", d => d.ShowAbout(new AboutRequest()), "About");
            var sheet = EditorWindow()?.CommandBindings.OfType<System.Windows.Input.CommandBinding>().Select(b => b.Command).OfType<System.Windows.Input.RoutedUICommand>().FirstOrDefault(c => c.Name == "KeyboardShortcuts");
            (bool, string) sheetResult = (false, "editor has no KeyboardShortcuts command");
            if (sheet is not null && EditorWindow() is { } editor)
                sheetResult = await ShotModalAsync("keyboard-shortcuts", _ => { sheet.Execute(null, editor); return Task.CompletedTask; }, "Keyboard");
            return (ok && aboutOk && sheetResult.Item1, $"{detail}; about: {aboutDetail}; shortcuts: {sheetResult.Item2}");
        });
        await Step("create-gif-dialog-renders", () => ShotModalAsync("dialog-create-gif", d => d.ShowCreateGif(new CreateGifRequest(_videoPath ?? inputPng, new VideoInfo(TimeSpan.FromSeconds(3), 640, 360, 30, false, 90), null, Path.Combine(_o.OutputDirectory, "x.gif"))), "Create"));
        await Step("share-accounts-dialog-renders", () => ShotModalAsync("dialog-share-accounts", d => d.ShowShareAccounts(new ShareAccountsRequest()), "Account"));

        await Step("selection-overlay-renders", async () =>
        {
            var coordinator = (CaptureCoordinator)Need<ICaptureCoordinator>();
            var preset = new CapturePreset { Name = "Smoke overlay", Mode = CaptureMode.Image, Selection = SelectionType.Region, PreviewInEditor = false };
            var run = coordinator.RunAsync(preset);
            Window? overlay = null;
            await WaitUntil(() => (overlay = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.GetType().Name == "SelectionOverlayWindow" && x.IsVisible)) is not null, TimeSpan.FromSeconds(10));
            if (overlay is null) { coordinator.CancelCurrent(); return (false, "overlay not shown"); }
            await Task.Delay(800);
            var shot = Shot(overlay, "selection-overlay");
            coordinator.CancelCurrent();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
            bool closed = await WaitUntil(() => !Application.Current.Windows.OfType<Window>().Any(x => x.GetType().Name == "SelectionOverlayWindow" && x.IsVisible), TimeSpan.FromSeconds(5));
            return (shot.NonBlank > 0.01 && closed, $"{shot.Method} {shot.W}x{shot.H}, non-blank {shot.NonBlank:P0}; cancelled cleanly={closed}");
        });

        await Need<CaptureLibraryBridge>().DrainAsync(TimeSpan.FromSeconds(30));
        await Step("no-duplicate-library-items", async () =>
        {
            var items = await Need<ILibraryService>().QueryAsync(new LibraryQuery(Take: 1000));
            var dupes = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            return (dupes.Count == 0, $"{items.Count} item(s): " + string.Join(", ", items.Select(i => $"{i.Kind}:{Path.GetFileName(i.Path)}")));
        });

        WriteReport();
        int failures = _results.Count(r => !r.Ok);
        _log.LogInformation("Smoke run finished: {Passed}/{Total} passed", _results.Count - failures, _results.Count);
        return failures;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The step running now (read by the App's smoke watchdog from another thread).</summary>
    public string? CurrentStep => Volatile.Read(ref _currentStep);
    private string? _currentStep;

    /// <summary>Markdown table rows of the steps finished so far (thread-safe snapshot for the watchdog report).</summary>
    public IReadOnlyList<string> CompletedRows()
    {
        lock (_results) return _results.Select(r => $"| {r.Name} | {(r.Ok ? "PASS" : "**FAIL**")} | {r.Detail.Replace("|", "/")} | {r.Ms:0} |").ToList();
    }

    private async Task Step(string name, Func<Task<(bool Ok, string Detail)>> body)
    {
        var sw = Stopwatch.StartNew();
        Volatile.Write(ref _currentStep, name);
        try
        {
            var (ok, detail) = await body();
            lock (_results) _results.Add(new StepResult(name, ok, detail, sw.Elapsed.TotalMilliseconds));
            _log.Log(ok ? LogLevel.Information : LogLevel.Warning, "SMOKE {Name}: {Outcome} - {Detail}", name, ok ? "PASS" : "FAIL", detail);
        }
        catch (Exception ex)
        {
            lock (_results) _results.Add(new StepResult(name, false, ex.GetType().Name + ": " + ex.Message, sw.Elapsed.TotalMilliseconds));
            _log.LogError(ex, "SMOKE {Name}: FAIL (exception)", name);
        }
        finally { Volatile.Write(ref _currentStep, null); }
    }

    private async Task<(bool, string)> ShotModalAsync(string shotName, Func<IDialogService, Task> show, string titleContains)
    {
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        var dialogs = Need<IDialogService>();
        var task = show(dialogs);
        Window? w = null;
        await WaitUntil(() => (w = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => !before.Contains(x) && x.IsVisible)) is not null, TimeSpan.FromSeconds(10));
        if (w is null) return (false, "dialog did not appear");
        await Task.Delay(1000);
        var shot = Shot(w, shotName);
        var title = w.Title;
        w.Close();
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        // Dialogs are mostly flat background; any rendered text/controls give > 1 %.
        return (shot.NonBlank > 0.01, $"'{title}' {shot.Method} {shot.W}x{shot.H}, non-blank {shot.NonBlank:P1}");
    }

    private (string Method, int W, int H, double NonBlank) Shot(Window w, string name)
    {
        var dir = _o.ScreenshotDirectory ?? Path.Combine(_o.OutputDirectory, "screenshots");
        var path = Path.Combine(dir, name + ".png");
        var (method, width, height, nonBlank) = WindowShot.Save(w, path);
        _shots.Add((name, path, $"{method} {width}x{height}"));
        return (method, width, height, nonBlank);
    }

    /// <summary>Renders the window's visual tree at <paramref name="scale"/> (e.g. 1.5 = 150 % DPI raster of the same DIP layout).</summary>
    private void ShotScaled(Window w, string name, double scale)
    {
        var dir = _o.ScreenshotDirectory ?? Path.Combine(_o.OutputDirectory, "screenshots");
        var path = Path.Combine(dir, name + ".png");
        int pw = (int)Math.Ceiling(w.ActualWidth * scale), ph = (int)Math.Ceiling(w.ActualHeight * scale);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background ?? System.Windows.Media.Brushes.White, null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
            dc.DrawRectangle(new VisualBrush(w) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
        }
        rtb.Render(dv);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Shotwise.Core.Imaging.PngCodec.Encode(Shotwise.UI.Common.Imaging.BitmapInterop.ToBgraImage(rtb)));
        _shots.Add((name, path, $"RenderTargetBitmap x{scale:0.#} {pw}x{ph}"));
    }

    private static Window? EditorWindow()
        => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.GetType().Name == "EditorWindow" && w.IsVisible);

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>A physical-pixel rectangle over the editor window (falls back to the primary monitor's top-left).</summary>
    private RectI CaptureArea(int w, int h)
    {
        if (EditorWindow() is { } win)
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (GetWindowRect(hwnd, out var r) && r.Right - r.Left >= w && r.Bottom - r.Top >= h)
                return new RectI(r.Left + (r.Right - r.Left - w) / 2, r.Top + (r.Bottom - r.Top - h) / 2, w, h);
        }
        var m = Need<IMonitorService>().GetMonitors().FirstOrDefault(x => x.IsPrimary) ?? Need<IMonitorService>().GetMonitors()[0];
        return new RectI(m.BoundsPx.X + 100, m.BoundsPx.Y + 100, w, h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>A UI-like synthetic screenshot (title bar, panels, buttons, text-ish bars) so annotations have context.</summary>
    private static BgraImage TestImage(int w, int h)
    {
        var img = BgraImage.CreateFilled(w, h, Argb.FromRgb(0xF3, 0xF4, 0xF6));
        void Fill(int x, int y, int ww, int hh, Argb c) { var b = BgraImage.CreateFilled(ww, hh, c); img.Blit(b, x, y); }
        Fill(0, 0, w, 44, Argb.FromRgb(0x1E, 0x40, 0xAF));
        Fill(0, 44, 200, h - 44, Argb.FromRgb(0xE5, 0xE7, 0xEB));
        for (int i = 0; i < 6; i++) Fill(20, 70 + i * 40, 150, 14, Argb.FromRgb(0x9C, 0xA3, 0xAF));
        Fill(230, 70, w - 260, 220, Argb.White);
        for (int i = 0; i < 6; i++) Fill(250, 92 + i * 30, (i % 2 == 0 ? 520 : 400), 12, Argb.FromRgb(0x4B, 0x55, 0x63));
        Fill(230, 320, 330, 240, Argb.White);
        Fill(590, 320, w - 620, 240, Argb.White);
        Fill(250, 500, 120, 36, Argb.FromRgb(0x25, 0x63, 0xEB));
        Fill(390, 500, 120, 36, Argb.FromRgb(0xD1, 0xD5, 0xDB));
        for (int i = 0; i < 4; i++) Fill(610, 345 + i * 28, 260, 10, Argb.FromRgb(0x6B, 0x72, 0x80));
        return img;
    }

    private static IEnumerable<Annotation> SampleAnnotations()
    {
        var red = Argb.FromRgb(0xE1, 0x1D, 0x48);
        yield return new ShapeAnnotation { Start = new Point2(240, 80), End = new Point2(790, 280), Stroke = new StrokeStyle(red, 4), Fill = FillStyle.None, Shape = ShapeKind.RoundedRectangle };
        yield return new ArrowAnnotation { Start = new Point2(560, 470), End = new Point2(380, 520), Stroke = new StrokeStyle(red, 6), Shadow = new ShadowStyle(Argb.FromArgb(0x80, 0, 0, 0), 45, 4, 6, 0.5) };
        yield return new CalloutAnnotation
        {
            Start = new Point2(600, 400), End = new Point2(900, 480), ShapePresetId = "rounded-rectangle",
            Fill = FillStyle.Solid(Argb.FromRgb(0xFE, 0xF0, 0x8A)), Stroke = new StrokeStyle(Argb.FromRgb(0x85, 0x4D, 0x0E), 2),
            Tails = { new CalloutTail(new Point2(560, 520)) },
            Text = RichText.FromPlain("Click Save to keep your changes", "Segoe UI", 18, Argb.Black, bold: true),
        };
        yield return new StepAnnotation { Start = new Point2(205, 60), End = new Point2(245, 100), Value = 1, Fill = FillStyle.Solid(red) };
        yield return new StepAnnotation { Start = new Point2(215, 480), End = new Point2(255, 520), Value = 2, Fill = FillStyle.Solid(red) };
        yield return new TextAnnotation { Start = new Point2(240, 300), End = new Point2(560, 330), Text = RichText.FromPlain("Shotwise smoke test", "Segoe UI", 20, Argb.FromRgb(0x11, 0x18, 0x27), bold: true) };
        yield return new HighlightAnnotation { Start = new Point2(248, 120), End = new Point2(780, 136), Fill = FillStyle.Solid(Argb.FromArgb(0xFF, 0xFD, 0xE0, 0x47)), Stroke = StrokeStyle.None };
        // Kept clear of the callout: the blur is topmost, so overlapping it pixelated the callout text (the P3/R3 "callout
        // text double-render" was this sample overlap, not a renderer defect).
        yield return new BlurAnnotation { Start = new Point2(605, 335), End = new Point2(880, 388), Mode = BlurKind.Pixelate, Intensity = 12, Stroke = StrokeStyle.None };
    }

    private void WriteReport()
    {
        var sb = new StringBuilder();
        int passed = _results.Count(r => r.Ok);
        sb.AppendLine("# Shotwise smoke run").AppendLine();
        sb.AppendLine($"- Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- Machine: {Environment.MachineName}, {Environment.OSVersion.VersionString}");
        sb.AppendLine($"- Data root: `{Environment.GetEnvironmentVariable("SHOTWISE_DATA_ROOT") ?? "(profile!)"}`");
        sb.AppendLine($"- Result: **{passed}/{_results.Count} passed**").AppendLine();
        sb.AppendLine("| Step | Result | Detail | ms |").AppendLine("|---|---|---|---|");
        foreach (var r in _results) sb.AppendLine($"| {r.Name} | {(r.Ok ? "PASS" : "**FAIL**")} | {r.Detail.Replace("|", "/")} | {r.Ms:0} |");
        sb.AppendLine().AppendLine("## Screenshots").AppendLine();
        foreach (var s in _shots) sb.AppendLine($"- `{s.Name}`: {s.Path} ({s.Detail})");
        AtomicFile.WriteAllText(Path.Combine(_o.OutputDirectory, "smoke-results.md"), sb.ToString());
        AtomicFile.WriteAllText(Path.Combine(_o.OutputDirectory, "smoke-results.json"),
            JsonSerializer.Serialize(new { results = _results, screenshots = _shots.Select(s => new { s.Name, s.Path, s.Detail }) }, ShotwiseJson.Indented));
    }
}
