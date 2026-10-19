using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Naming;

namespace Shotwise.Sharing;

/// <summary>
/// Destination registry + share orchestration: capability / enablement / policy filtering, one share at a time,
/// prepare → share → history → copy link → toast.
/// </summary>
public sealed class ShareService : IShareService
{
    private readonly IReadOnlyList<IShareDestination> _destinations;
    private readonly IAppPaths _paths;
    private readonly ISettingsService? _settings;
    private readonly IPolicyService? _policy;
    private readonly ILibraryService? _library;
    private readonly INotificationService? _notifications;
    private readonly IClipboardWriter? _clipboard;
    private readonly IDocumentRenderer? _renderer;
    private readonly IImageCodec? _codec;
    private readonly IShotxSerializer? _shotx;
    private readonly ILogger _log;
    private readonly NamingCounter _counter;
    private readonly HashSet<string> _disabledFallback = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _running = new(1, 1);

    public ShareService(
        IEnumerable<IShareDestination> destinations,
        IAppPaths paths,
        ISettingsService? settings = null,
        IPolicyService? policy = null,
        ILibraryService? library = null,
        INotificationService? notifications = null,
        IClipboardWriter? clipboard = null,
        IDocumentRenderer? renderer = null,
        IImageCodec? codec = null,
        IShotxSerializer? shotx = null,
        ILogger<ShareService>? log = null)
    {
        _destinations = destinations.ToList();
        _paths = paths;
        _settings = settings;
        _policy = policy;
        _library = library;
        _notifications = notifications;
        _clipboard = clipboard;
        _renderer = renderer;
        _codec = codec;
        _shotx = shotx;
        _log = (ILogger?)log ?? NullLogger.Instance;
        _counter = new NamingCounter(settings);
    }

    public IReadOnlyList<IShareDestination> Destinations => _destinations;

    public bool IsShareRunning => _running.CurrentCount == 0;

    public bool IsEnabled(string id)
    {
        if (_destinations.FirstOrDefault(d => Same(d.Id, id)) is { } d && d is IUnavailableDestination) return false;
        if (_settings is not null)
            return !_settings.Current.Share.DisabledDestinations.Contains(id, StringComparer.OrdinalIgnoreCase);
        lock (_gate) return !_disabledFallback.Contains(id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (_settings is not null)
        {
            _settings.Update(s =>
            {
                var list = s.Share.DisabledDestinations;
                list.RemoveAll(x => Same(x, id));
                if (!enabled) list.Add(id);
            });
            return;
        }
        lock (_gate)
        {
            if (enabled) _disabledFallback.Remove(id);
            else _disabledFallback.Add(id);
        }
    }

    /// <summary>True when IPolicyService (if any) allows the destination.</summary>
    public bool IsAllowedByPolicy(IShareDestination d)
    {
        if (_policy is null) return true;
        if (!_policy.IsAllowed(PolicyFeature.Outputs)) return false;
        if (d is IPolicyGatedDestination g)
            foreach (var f in g.RequiredPolicies)
                if (!_policy.IsAllowed(f)) return false;
        return true;
    }

    public IEnumerable<IShareDestination> For(IReadOnlyList<ShareFile> files)
    {
        if (files.Count == 0) return Enumerable.Empty<IShareDestination>();
        return _destinations.Where(d => IsEnabled(d.Id) && IsAllowedByPolicy(d) && Supports(d, files));
    }

    internal static bool Supports(IShareDestination d, IReadOnlyList<ShareFile> files)
    {
        if (files.Count == 0) return false;
        if (files.Count > 1 && !d.SupportsMultiple) return false;
        foreach (var f in files)
        {
            if (!d.SupportedFormats.Contains(FormatKeys.Of(f))) return false;
            if (f.IsStepCapture && !d.SupportsStepCapture) return false;
        }
        return true;
    }

    /// <summary>Per-binding options (CONTRACT A24): overlaid on the destination's saved settings for the duration of this share.</summary>
    public async Task<ShareResult> ShareAsync(string destinationId, IReadOnlyList<ShareFile> files, IShareUi ui, System.Text.Json.JsonElement? options, IProgress<double>? p, CancellationToken ct)
    {
        if (options is not { ValueKind: System.Text.Json.JsonValueKind.Object } o)
            return await ShareAsync(destinationId, files, ui, p, ct).ConfigureAwait(false);
        var id = _destinations.FirstOrDefault(d => Same(d.Id, destinationId))?.Id ?? destinationId;
        using (Infrastructure.DestinationSettingsStore.Overlay(id, o))
            return await ShareAsync(destinationId, files, ui, p, ct).ConfigureAwait(false);
    }

    public async Task<ShareResult> ShareAsync(string destinationId, IReadOnlyList<ShareFile> files, IShareUi ui, IProgress<double>? p, CancellationToken ct)
    {
        if (files is null || files.Count == 0) return Fail(ErrorCode.ShareNoFile, "There is nothing to share.");
        var dest = _destinations.FirstOrDefault(d => Same(d.Id, destinationId));
        if (dest is null) return Fail(ErrorCode.DestinationUnavailable, $"Unknown destination '{destinationId}'.");
        if (dest is IUnavailableDestination u) return Fail(ErrorCode.DestinationUnavailable, u.UnavailableReason);
        if (!IsEnabled(dest.Id)) return Fail(ErrorCode.DestinationUnavailable, $"{dest.DisplayName} is turned off in Preferences.");
        if (!IsAllowedByPolicy(dest)) return Fail(ErrorCode.PolicyDisabled, $"{dest.DisplayName} is disabled by policy.");
        if (files.Count > 1 && !dest.SupportsMultiple) return Fail(ErrorCode.ShareMultipleFiles, $"{dest.DisplayName} can share only one file at a time.");
        if (!Supports(dest, files)) return Fail(ErrorCode.UnsupportedFormat, $"{dest.DisplayName} does not accept this kind of file.");
        foreach (var f in files)
            if (!File.Exists(f.Path)) return Fail(ErrorCode.FileNotFound, $"File not found: {Path.GetFileName(f.Path)}");

        if (!await _running.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return Fail(ErrorCode.ShareInProgress, "Another share is already in progress.");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!dest.IsConfigured && dest.CanShowOptions)
            {
                await dest.ShowOptionsAsync(ui).ConfigureAwait(false);
                if (!dest.IsConfigured) return ShareResult.Cancel();
            }
            if (!await dest.PrepareAsync(ui, files, ct).ConfigureAwait(false))
                return ShareResult.Cancel();

            ShareResult result;
            try { result = await dest.ShareAsync(files, p, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ShareResult.Cancel(); }
            catch (ShotwiseException ex) when (ex.Code is ErrorCode.ShareCancelled or ErrorCode.Cancelled) { return ShareResult.Cancel(); }
            catch (ShotwiseException ex)
            {
                _log.LogWarning(ex, "Share to {Destination} failed", dest.Id);
                result = ShareResult.Failure(ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Share to {Destination} failed", dest.Id);
                result = ShareResult.Failure(ex.Message);
            }

            if (result.Outcome == ShareOutcome.Succeeded)
                await OnSucceededAsync(dest, files, result).ConfigureAwait(false);
            else if (result.Outcome == ShareOutcome.Failed)
                Toast(dest.DisplayName, result.Message ?? "Sharing failed.", NotificationKind.Error, null);
            return result;
        }
        catch (OperationCanceledException) { return ShareResult.Cancel(); }
        finally { _running.Release(); }
    }

    private async Task OnSucceededAsync(IShareDestination dest, IReadOnlyList<ShareFile> files, ShareResult result)
    {
        await RecordHistoryAsync(dest, files, result).ConfigureAwait(false);

        var copied = false;
        var copyLink = _settings?.Current.Share.CopyLinkToClipboard ?? true;
        var text = result.ClipboardText ?? result.Link;
        if (copyLink && !string.IsNullOrEmpty(text) && _clipboard is not null)
        {
            try { _clipboard.SetText(text); copied = true; }
            catch (Exception ex) { _log.LogWarning(ex, "Could not copy share link to the clipboard"); }
        }

        var actions = new List<NotificationAction>();
        if (result.OpenDestination is { } open) actions.Add(new NotificationAction("Open", open, "open"));
        else if (result.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var lu) && lu.Scheme is "http" or "https")
            actions.Add(new NotificationAction("Open link", () => OpenShell(lu.AbsoluteUri), "open"));
        if (result.Link is { } l2 && _clipboard is not null && !copied)
            actions.Add(new NotificationAction("Copy link", () => _clipboard.SetText(l2), "copy"));

        var msg = result.Message ?? (copied ? "Link copied to the clipboard." : files.Count == 1 ? "Shared 1 file." : $"Shared {files.Count} files.");
        Toast(dest.DisplayName, msg, NotificationKind.Success, actions.Count > 0 ? actions : null);
    }

    private async Task RecordHistoryAsync(IShareDestination dest, IReadOnlyList<ShareFile> files, ShareResult result)
    {
        if (_library is null) return;
        foreach (var f in files)
        {
            try
            {
                // Prefer the explicit library item (temp-materialized files are not in the library; CONTRACT A18).
                Guid? itemId = f.LibraryItemId;
                if (itemId is null)
                {
                    var item = await _library.FindByPathAsync(f.Path).ConfigureAwait(false);
                    if (item is null) continue;
                    itemId = item.Id;
                }
                await _library.AddShareHistoryAsync(itemId.Value, new ShareHistoryEntry(DateTimeOffset.Now, dest.Id, dest.DisplayName, result.Link, f.Title)).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not record share history for {Path}", f.Path); }
        }
    }

    private void Toast(string title, string message, NotificationKind kind, IReadOnlyList<NotificationAction>? actions)
    {
        if (_notifications is null) return;
        if (_settings is not null && !_settings.Current.General.ShowToasts) return;
        try { _notifications.Show(title, message, kind, actions); }
        catch (Exception ex) { _log.LogWarning(ex, "Toast failed"); }
    }

    private static ShareResult Fail(ErrorCode code, string message) => ShareResult.Failure(message);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void OpenShell(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { /* best effort */ }
    }

    // ----- Materialize -----

    public async Task<IReadOnlyList<ShareFile>> MaterializeAsync(ShotDocument doc, string format, EncodeOptions? o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_renderer is null || _codec is null)
            throw new ShotwiseException(ErrorCode.DestinationUnavailable, "Rendering services are not available.");
        if (doc.Pages.Count == 0) throw new ShotwiseException(ErrorCode.ShareNoFile, "The document has no pages.");

        var key = FormatKeys.Normalize(format);
        var share = _settings?.Current.Share ?? new ShareSettings();
        var omit = share.DoNotIncludeCaptureSourceInNames || (_settings?.Current.Privacy.StripMetadataOnShare ?? false);
        var naming = share.Naming;
        var dir = Path.Combine(_paths.Temp, "share", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        o ??= new EncodeOptions(JpegQuality: share.SaveAs.JpegQuality);

        return await Task.Run<IReadOnlyList<ShareFile>>(async () =>
        {
            var ctx = NamingContext.FromDocument(doc, _counter.Next());
            var baseName = NamingEngine.BuildName(ctx, naming, omit);
            var title = omit ? baseName : (doc.Metadata.WindowTitle ?? baseName);
            var result = new List<ShareFile>();

            if (key == "shotx")
            {
                if (_shotx is null) throw new ShotwiseException(ErrorCode.UnsupportedFormat, "The Shotwise document format is not available.");
                var path = NamingEngine.UniquePath(dir, baseName, ".shotx");
                await _shotx.SaveAsync(doc, path, _renderer.RenderThumbnail(doc, 1024), ct).ConfigureAwait(false);
                result.Add(new ShareFile(path, MediaKind.Image, "shotx", title, (int)doc.Pages[0].CanvasSize.Width, (int)doc.Pages[0].CanvasSize.Height, null, false));
                return result;
            }

            if (!TryParseFormat(key, out var fmt))
                throw new ShotwiseException(ErrorCode.UnsupportedFormat, $"Unsupported export format '{format}'.");
            var ext = fmt.DefaultExtension();
            var shareKey = key == "tif" ? "tiff" : key;

            var pages = new List<BgraImage>();
            foreach (var page in doc.Pages)
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(_renderer.RenderPage(doc, page, new RenderOptions(1)));
            }

            if (pages.Count > 1 && fmt is ImageFormat.Pdf or ImageFormat.Tiff)
            {
                var path = NamingEngine.UniquePath(dir, baseName, ext);
                _codec.SaveMultipage(pages, path, fmt, o);
                result.Add(new ShareFile(path, MediaKind.Image, shareKey, title, pages[0].Width, pages[0].Height, null, false));
                return result;
            }

            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var name = pages.Count > 1 ? $"{baseName}_p{i + 1}" : baseName;
                var path = NamingEngine.UniquePath(dir, name, ext);
                _codec.Save(pages[i], path, fmt, o);
                result.Add(new ShareFile(path, MediaKind.Image, shareKey, pages.Count > 1 ? $"{title} ({i + 1})" : title, pages[i].Width, pages[i].Height, null, false));
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    internal static bool TryParseFormat(string key, out ImageFormat fmt)
    {
        switch (FormatKeys.Normalize(key))
        {
            case "png": fmt = ImageFormat.Png; return true;
            case "jpg": fmt = ImageFormat.Jpeg; return true;
            case "bmp": fmt = ImageFormat.Bmp; return true;
            case "gif": fmt = ImageFormat.Gif; return true;
            case "tiff": fmt = ImageFormat.Tiff; return true;
            case "webp": fmt = ImageFormat.WebP; return true;
            case "pdf": fmt = ImageFormat.Pdf; return true;
            case "ico": fmt = ImageFormat.Ico; return true;
            default: fmt = ImageFormat.Png; return false;
        }
    }
}
