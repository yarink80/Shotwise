using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Share;

/// <summary>Share button / menu: destinations filtered by <see cref="IShareService.For"/>, progress, result toast, share history.</summary>
internal sealed class ShareController
{
    private static readonly string[] ImageFormatPreference = { "png", "jpg", "pdf", "webp", "tiff", "bmp", "gif", "shotx" };

    private readonly EditorContext _ctx;
    private readonly ProgressOverlay _overlay;
    private readonly ToastHost _toasts;
    private readonly ILogger _log;
    private bool _running;

    public ShareController(EditorContext ctx, ProgressOverlay overlay, ToastHost toasts)
    {
        _ctx = ctx;
        _overlay = overlay;
        _toasts = toasts;
        _log = ctx.Log<ShareController>();
    }

    public bool IsAvailable => _ctx.Share is not null;

    private static ShareFile Probe(EditorDocument doc)
    {
        switch (doc)
        {
            case VideoEditorDocument v:
                return new ShareFile(v.FilePath ?? "", v.IsGif ? MediaKind.Gif : MediaKind.Video, v.IsGif ? "gif" : "mp4", v.Title, v.Info.Width, v.Info.Height, v.Info.Duration, false);
            case ImageEditorDocument i:
                var page = i.Session.ActivePage;
                return new ShareFile(i.FilePath ?? "", MediaKind.Image, "png", i.Title, (int)page.CanvasSize.Width, (int)page.CanvasSize.Height, null, false);
            default:
                return new ShareFile("", MediaKind.Image, "png", doc.Title, 0, 0, null, false);
        }
    }

    public IReadOnlyList<IShareDestination> DestinationsFor(EditorDocument doc)
    {
        if (_ctx.Share is not { } share) return Array.Empty<IShareDestination>();
        try
        {
            return share.For(new[] { Probe(doc) }).Where(d => share.IsEnabled(d.Id)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing share destinations failed");
            return Array.Empty<IShareDestination>();
        }
    }

    /// <summary>Destination icon: a category-tinted tile with the destination's glyph (design tokens only).</summary>
    private static FrameworkElement DestinationIcon(IShareDestination d)
    {
        var (bg, fg) = Icons.DestinationTint(d.Category);
        return Icons.Tile(Icons.ForDestination(d.Id, d.Category), bg, fg, 24, 15);
    }

    public ContextMenu BuildMenu(EditorDocument doc, bool linkOnly, Action openHistory, Action openAccounts, Action<string>? onUsed = null)
    {
        var menu = new ContextMenu { MinWidth = 240 };
        var list = DestinationsFor(doc);
        if (linkOnly) list = list.Where(d => d.Category is DestinationCategory.Storage or DestinationCategory.Uploader or DestinationCategory.Microsoft or DestinationCategory.Custom).ToList();
        DestinationCategory? last = null;
        foreach (var d in list.OrderBy(d => d.Category).ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (last is not null && last != d.Category) menu.Items.Add(new Separator());
            last = d.Category;
            var dest = d;
            var item = new MenuItem { Header = d.DisplayName, Icon = DestinationIcon(d) };
            if (d.RequiresAccount && !d.IsConfigured) item.ToolTip = ES.T("Share_NeedsAccount", "Sign in under Share Preferences to use this destination.");
            item.Click += (_, _) =>
            {
                onUsed?.Invoke(dest.Id);
                UiKit.Fire(() => ShareAsync(doc, dest), ex => _ = _ctx.ShowErrorAsync(ex));
            };
            menu.Items.Add(item);
        }
        if (list.Count == 0) menu.Items.Add(new MenuItem { Header = ES.T("Share_NoDestinations", "No destinations available for this item"), IsEnabled = false });
        menu.Items.Add(new Separator());
        // SHR-04..12: every destination with an options form (FTP/SFTP host+login, S3 bucket+keys, custom uploader,
        // program, e-mail, Office, cloud folders/titles) is reachable from here.
        var configurable = (_ctx.Share?.Destinations ?? Array.Empty<IShareDestination>()).Where(d => d.CanShowOptions)
            .OrderBy(d => d.Category).ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (configurable.Count > 0)
        {
            var options = new MenuItem { Header = ES.T("Share_DestinationOptions", "Destination options"), Icon = Icons.Tile(Icons.Glyph.Settings, Tok.Surface2, Tok.Text2, 24, 15) };
            foreach (var d in configurable)
            {
                var id = d.Id;
                var item = new MenuItem { Header = ES.F("Share_OptionsFor", "{0}…", d.DisplayName), Icon = DestinationIcon(d) };
                if (!d.IsConfigured) item.ToolTip = ES.T("Share_NotConfigured", "Not set up yet");
                item.Click += (_, _) => UiKit.Fire(() => ShowOptionsAsync(id), ex => _ = _ctx.ShowErrorAsync(ex));
                options.Items.Add(item);
            }
            menu.Items.Add(options);
        }
        var history = new MenuItem { Header = ES.T("Share_History", "Share history…"), Icon = Icons.Tile(Icons.Glyph.History, Tok.Surface2, Tok.Text2, 24, 15) };
        history.Click += (_, _) => openHistory();
        menu.Items.Add(history);
        var prefs = new MenuItem { Header = ES.T("Share_Preferences", "Share preferences…"), Icon = Icons.Tile(Icons.Glyph.Person, Tok.Surface2, Tok.Text2, 24, 15) };
        prefs.Click += (_, _) => openAccounts();
        menu.Items.Add(prefs);
        return menu;
    }

    /// <summary>Opens a destination's options form (the dialog service hosts it; FX-5 owns the dialog).</summary>
    public Task ShowOptionsAsync(string destinationId)
        => _ctx.Dialogs.ShowDestinationOptions(new DestinationOptionsRequest(destinationId) { Owner = _ctx.ActiveWindow });

    private static string ChooseFormat(IShareDestination d, EditorDocument doc)
    {
        if (doc is VideoEditorDocument v)
        {
            if (v.IsGif) return d.SupportedFormats.Contains("agif") ? "agif" : d.SupportedFormats.Contains("gif") ? "gif" : d.SupportedFormats.FirstOrDefault() ?? "gif";
            return d.SupportedFormats.Contains("mp4") ? "mp4" : d.SupportedFormats.FirstOrDefault() ?? "mp4";
        }
        return ImageFormatPreference.FirstOrDefault(f => d.SupportedFormats.Contains(f)) ?? d.SupportedFormats.FirstOrDefault() ?? "png";
    }

    public async Task ShareAsync(EditorDocument doc, IShareDestination dest)
    {
        if (_ctx.Share is not { } share || _ctx.ShareUi is not { } ui)
        {
            await _ctx.InfoAsync(ES.T("Share_Title", "Share"), ES.T("Share_Unavailable", "Sharing is not available in this build."), MessageKind.Warning);
            return;
        }
        if (_running || _overlay.IsBusy)
        {
            _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ErrorCode.ShareInProgress), MessageKind.Warning);
            return;
        }
        // A destination that still needs its settings (host, bucket, program…) opens its options first; the destination's
        // own PrepareAsync still validates/prompts afterwards (account sign-in happens there or in Share Preferences).
        if (!dest.IsConfigured && dest.CanShowOptions && !dest.RequiresAccount) await ShowOptionsAsync(dest.Id);
        _running = true;
        ShareResult? result = null;
        try
        {
            var completed = await _overlay.RunAsync(ES.F("Share_Progress", "Sharing to {0}…", dest.DisplayName), doc.Title, true, async (progress, ct) =>
            {
                IReadOnlyList<ShareFile> files;
                var format = ChooseFormat(dest, doc);
                if (doc is ImageEditorDocument img)
                {
                    img.Session.CommitTextEditSafe();
                    files = await share.MaterializeAsync(img.Document, format, null, ct);
                }
                else if (doc is VideoEditorDocument vid)
                {
                    files = new[] { await PrepareVideoAsync(vid, format, progress, ct) };
                }
                else return;
                // Library-backed docs: record share history against the library item even though files are temp copies (CONTRACT A18).
                if (doc.LibraryItem is { } li) files = files.Select(f => f with { LibraryItemId = li.Id }).ToList();
                result = await share.ShareAsync(dest.Id, files, ui, progress, ct);
            });
            if (!completed) return;
        }
        finally { _running = false; }
        if (result is not { } r) return;
        await ReportAsync(doc, dest, r);
    }

    private async Task<ShareFile> PrepareVideoAsync(VideoEditorDocument vid, string format, IProgress<double> progress, CancellationToken ct)
    {
        var info = Probe(vid) with { Format = format == "agif" ? "gif" : format };
        if (vid.Edl.IsIdentity || vid.IsGif || _ctx.Video is null) return info;
        var temp = Path.Combine(_ctx.Paths.Temp, "share", Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await _ctx.Video.RenderAsync(vid.Edl.Clone(), temp, new VideoExportOptions(), progress, ct);
        return info with { Path = temp, Duration = vid.Edl.KeptDuration };
    }

    private async Task ReportAsync(EditorDocument doc, IShareDestination dest, ShareResult r)
    {
        switch (r.Outcome)
        {
            case ShareOutcome.Succeeded:
                var actions = new List<(string, Action)>();
                if (!string.IsNullOrEmpty(r.Link))
                {
                    var link = r.Link;
                    actions.Add((ES.T("Share_CopyLink", "Copy link"), () => { try { Clipboard.SetText(link); } catch (System.Runtime.InteropServices.COMException) { } }));
                    actions.Add((ES.T("Share_OpenLink", "Open link"), () => OpenLink(link)));
                }
                if (r.OpenDestination is { } open) actions.Add((ES.T("Share_OpenDestination", "Open"), open));
                _toasts.Show(r.Message ?? ES.F("Share_Done", "Shared to {0}.", dest.DisplayName), MessageKind.Success, null, actions.ToArray());
                if (!string.IsNullOrEmpty(r.ClipboardText) && _ctx.Settings.Current.Share.CopyLinkToClipboard && _ctx.Clipboard is { } cb)
                {
                    try { cb.SetText(r.ClipboardText); } catch (ShotwiseException) { }
                }
                break;
            case ShareOutcome.Failed:
                _toasts.Show(r.Message ?? ES.F("Share_Failed", "Sharing to {0} failed.", dest.DisplayName), MessageKind.Error);
                break;
        }
        await Task.CompletedTask;
    }

    private static void OpenLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Share history for the document's library item (LIB-07): list with copy/open link.</summary>
    public async Task ShowHistoryAsync(EditorDocument? doc, LibraryItem? item = null)
    {
        var lib = _ctx.Library;
        if (lib is null) return;
        item ??= doc?.LibraryItem;
        if (item is null && doc?.FilePath is { } p) item = await lib.FindByPathAsync(p);
        var entries = await lib.GetShareHistoryAsync(item?.Id);
        if (entries.Count == 0)
        {
            await _ctx.InfoAsync(ES.T("Share_History", "Share history"), ES.T("Share_NoHistory", "This item has not been shared yet."));
            return;
        }
        var details = string.Join(Environment.NewLine, entries.OrderByDescending(e => e.At).Select(e => $"{e.At.ToLocalTime():g}  {e.DestinationName}{(string.IsNullOrEmpty(e.Link) ? "" : "  " + e.Link)}"));
        var last = entries.OrderByDescending(e => e.At).FirstOrDefault(e => !string.IsNullOrEmpty(e.Link));
        if (last?.Link is { } link)
            _toasts.Show(ES.F("Share_LastLink", "Last link: {0}", link), MessageKind.Info, TimeSpan.FromSeconds(20),
                (ES.T("Share_CopyLink", "Copy link"), () => { try { Clipboard.SetText(link); } catch (System.Runtime.InteropServices.COMException) { } }),
                (ES.T("Share_OpenLink", "Open link"), () => OpenLink(link)));
        await _ctx.Dialogs.ShowMessage(new MessageRequest(ES.T("Share_History", "Share history"), item?.Name ?? ES.T("Share_AllHistory", "All items"), MessageKind.Info, details) { Owner = _ctx.ActiveWindow });
    }
}

internal static class SessionExtensions
{
    /// <summary>Commits an open inline text edit before exporting (no-op otherwise).</summary>
    public static void CommitTextEditSafe(this Shotwise.Editor.EditorSession s)
    {
        if (s.ActiveTextEdit is not null) s.CommitTextEdit();
    }
}
