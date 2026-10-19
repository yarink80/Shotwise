using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Core.Vision;

namespace Shotwise.App.Hosting;

/// <summary>
/// The application's single <see cref="ICaptureEvents"/> subscriber (CONTRACT A22/A26): every published capture is
/// autosaved to the library exactly once (LIB-01 preferences <c>Library.AutosaveImages/AutosaveVideos</c>), then
/// optionally OCR'd in the background (<c>Editor.BackgroundOcr</c>, policy <see cref="PolicyFeature.Ocr"/>) and the text
/// stored with <see cref="ILibraryService.SetOcrTextAsync"/>. Opening the editor is the capture coordinator's job; the
/// editor links its document to the saved item through <see cref="ICaptureAutosave"/>.
/// </summary>
public sealed class CaptureLibraryBridge : ICaptureAutosave, IDisposable
{
    private readonly ICaptureEvents _events;
    private readonly ILibraryService _library;
    private readonly ISettingsService _settings;
    private readonly IDocumentRenderer _renderer;
    private readonly IImageCodec _codec;
    private readonly IOcrService? _ocr;
    private readonly IPolicyService? _policy;
    private readonly Shotwise.Core.Media.IVideoService? _video;
    private readonly ILogger<CaptureLibraryBridge> _log;
    private readonly ConditionalWeakTable<DocumentMetadata, Task<LibraryItem?>> _images = new();
    private readonly ConcurrentDictionary<string, Task<LibraryItem?>> _videos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Task> _pending = new();
    private Task _ready = Task.CompletedTask;
    private bool _started;

    public CaptureLibraryBridge(ICaptureEvents events, ILibraryService library, ISettingsService settings, IDocumentRenderer renderer,
        IImageCodec codec, ILogger<CaptureLibraryBridge> log, IOcrService? ocr = null, IPolicyService? policy = null, Shotwise.Core.Media.IVideoService? video = null)
    {
        _events = events;
        _library = library;
        _settings = settings;
        _renderer = renderer;
        _codec = codec;
        _ocr = ocr;
        _policy = policy;
        _video = video;
        _log = log;
    }

    /// <summary>Number of background OCR jobs started (diagnostics / smoke harness).</summary>
    public int OcrJobsStarted;

    /// <summary>Starts listening. <paramref name="libraryReady"/> = the library initialization task (saves wait for it).</summary>
    public void Start(Task libraryReady)
    {
        if (_started) return;
        _started = true;
        _ready = libraryReady;
        _events.ImageCaptured += OnImageCaptured;
        _events.VideoCaptured += OnVideoCaptured;
    }

    public Task<LibraryItem?> ForImageAsync(DocumentMetadata metadata, CancellationToken ct = default)
        => metadata is not null && _images.TryGetValue(metadata, out var t) ? t.WaitAsync(ct) : Task.FromResult<LibraryItem?>(null);

    public Task<LibraryItem?> ForVideoAsync(string publishedPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publishedPath)) return Task.FromResult<LibraryItem?>(null);
        return _videos.TryGetValue(Normalize(publishedPath), out var t) ? t.WaitAsync(ct) : Task.FromResult<LibraryItem?>(null);
    }

    /// <summary>Waits for every autosave / OCR job started so far (smoke harness, orderly shutdown).</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var all = _pending.ToArray();
        try { await Task.WhenAll(all).WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { _log.LogWarning("Pending library jobs did not finish within {Timeout}", timeout); }
        catch (Exception) { /* individual failures were logged */ }
    }

    private void OnImageCaptured(object? sender, CaptureResult result)
    {
        if (!_settings.Current.Library.AutosaveImages) return;
        BgraImage preview;
        ShotDocument doc;
        try
        {
            // Same construction as the coordinator's editor document; rendered here (publisher = UI thread) so the
            // renderer is never used concurrently with the editor.
            doc = ShotDocument.FromImage(result.Image, result.Metadata, _codec);
            if (result.InitialAnnotations is { Count: > 0 } list && doc.Pages.Count > 0)
                foreach (var a in list) doc.Pages[0].Annotations.Add(a.DeepClone());
            preview = doc.Pages[0].Annotations.Count == 0 ? result.Image : _renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(1));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Preparing the capture for the library failed");
            return;
        }
        var task = SaveImageAsync(doc, preview, result.Image);
        _images.AddOrUpdate(result.Metadata, task);
        _pending.Enqueue(task);
    }

    private async Task<LibraryItem?> SaveImageAsync(ShotDocument doc, BgraImage preview, BgraImage raster)
    {
        try
        {
            await _ready.ConfigureAwait(false);
            var item = await _library.AddCaptureAsync(doc, preview).ConfigureAwait(false);
            _log.LogInformation("Capture autosaved to the library: {Path}", item.Path);
            if (_ocr is not null && _settings.Current.Editor.BackgroundOcr && _policy?.IsAllowed(PolicyFeature.Ocr) != false)
            {
                Interlocked.Increment(ref OcrJobsStarted);
                _pending.Enqueue(Task.Run(() => OcrAsync(item, raster)));
            }
            return item;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autosaving the capture to the library failed");
            return null;
        }
    }

    private async Task OcrAsync(LibraryItem item, BgraImage raster)
    {
        try
        {
            if (_ocr!.AvailableLanguages.Count == 0) { _log.LogDebug("Background OCR skipped: no OCR language installed"); return; }
            var r = await _ocr.RecognizeAsync(raster).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(r.Text)) return;
            await _library.SetOcrTextAsync(item.Id, r.Text).ConfigureAwait(false);
            _log.LogInformation("Background OCR stored {Chars} characters for {Path}", r.Text.Length, item.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Background OCR failed for {Path}", item.Path);
        }
    }

    private void OnVideoCaptured(object? sender, VideoCaptureResult video)
    {
        var autosave = _settings.Current.Library.AutosaveVideos;
        if (!autosave && !CanTag(video)) return;
        // The editor waits for this task (ForVideoAsync) before opening the file, so tagging never races the player.
        var task = ProcessVideoAsync(video, autosave);
        _videos[Normalize(video.Mp4Path)] = task;
        _pending.Enqueue(task);
        // Keep the lookup table small: forget entries after a few minutes.
        _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => _videos.TryRemove(Normalize(video.Mp4Path), out Task<LibraryItem?>? _), TaskScheduler.Default);
    }

    private bool CanTag(VideoCaptureResult video)
        => _video is not null && !video.IsGif && video.Mp4Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>MED-06: title / date / source application written into the recording's MP4 metadata.</summary>
    public static Shotwise.Core.Media.VideoMetadata MetadataFor(VideoCaptureResult video)
    {
        var md = video.Metadata;
        var title = !string.IsNullOrWhiteSpace(md.WindowTitle) ? md.WindowTitle!.Trim()
            : !string.IsNullOrWhiteSpace(md.AppName) ? $"{md.AppName} {md.CaptureDate.ToLocalTime():yyyy-MM-dd HH.mm.ss}"
            : $"Recording {md.CaptureDate.ToLocalTime():yyyy-MM-dd HH.mm.ss}";
        return new Shotwise.Core.Media.VideoMetadata(title, string.IsNullOrWhiteSpace(md.Url) ? null : md.Url, md.Tags, md.CaptureDate, md.AppName);
    }

    private async Task<LibraryItem?> ProcessVideoAsync(VideoCaptureResult video, bool autosave)
    {
        if (CanTag(video))
        {
            try
            {
                await _video!.WriteMetadataAsync(video.Mp4Path, MetadataFor(video)).ConfigureAwait(false);
                _log.LogDebug("Recording metadata written to {Path}", video.Mp4Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.LogWarning(ex, "Writing the recording's metadata failed for {Path}", video.Mp4Path);
            }
        }
        return autosave ? await SaveVideoAsync(video).ConfigureAwait(false) : null;
    }

    private async Task<LibraryItem?> SaveVideoAsync(VideoCaptureResult video)
    {
        try
        {
            await _ready.ConfigureAwait(false);
            var item = await _library.AddVideoAsync(video).ConfigureAwait(false);
            _log.LogInformation("Video autosaved to the library: {Path}", item.Path);
            return item;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autosaving the video to the library failed");
            return null;
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    public void Dispose()
    {
        if (!_started) return;
        _events.ImageCaptured -= OnImageCaptured;
        _events.VideoCaptured -= OnVideoCaptured;
    }
}
