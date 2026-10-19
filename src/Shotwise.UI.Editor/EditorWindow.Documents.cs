using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Vision;
using Shotwise.Editor;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using Shotwise.UI.Editor.Share;
using Shotwise.UI.Editor.Tray;

namespace Shotwise.UI.Editor;

internal sealed partial class EditorWindow
{
    private static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm" };
    private int _untitled;
    private Shotwise.UI.Common.Dialogs.PrintSettings _printSettings = Shotwise.UI.Common.Dialogs.PrintSettings.Default;

    // ------------------------------------------------------------------ adding / activating

    public async Task<EditorDocument> AddImageDocumentAsync(ShotDocument doc, bool activate, bool autosaveToLibrary)
    {
        var d = new ImageEditorDocument(doc, _ctx);
        if (doc.FilePath is null && string.IsNullOrWhiteSpace(doc.Metadata.AppName) && doc.Metadata.WindowTitle is null) d.Title = ES.F("Doc_Untitled", "Untitled {0}", ++_untitled);
        WireSession(d);
        d.Changed += OnDocumentChanged;
        _docs.Add(d);
        // New captures are autosaved exactly once by the App's ICaptureEvents subscriber (CONTRACT A26); opening never creates
        // library items: link the document to the item that autosave produced (if any).
        if (autosaveToLibrary && doc.FilePath is null && _ctx.CaptureAutosave is { } autosave) await LinkAutosavedAsync(d, autosave);
        else if (doc.FilePath is { } p && _ctx.Library is not null) d.LibraryItem = await TryFindAsync(p);
        _tray.QueueRefresh();
        if (activate) await ActivateAsync(d);
        return d;
    }

    private async Task<LibraryItem?> TryFindAsync(string path)
    {
        try { return _ctx.Library is null ? null : await _ctx.Library.FindByPathAsync(path); }
        catch (Exception ex) { _log.LogDebug(ex, "Library lookup failed"); return null; }
    }

    private async Task LinkAutosavedAsync(ImageEditorDocument d, ICaptureAutosave autosave)
    {
        try
        {
            // The autosaved item holds the capture as it was when the document was opened: edits made while waiting for
            // the autosave owner must stay dirty (R5-#1), so the revision is captured before awaiting.
            var revision = d.Revision;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (await autosave.ForImageAsync(d.Document.Metadata, cts.Token) is { } item)
            {
                d.LibraryItem = item;
                d.MarkSaved(item.Path, revision);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ShotwiseException or IOException)
        {
            _log.LogWarning(ex, "Linking the capture to its library item failed");
        }
    }

    private async Task AutosaveNewAsync(ImageEditorDocument d)
    {
        await d.SaveGate.WaitAsync();
        try
        {
            var revision = d.Revision;
            var preview = _ctx.Renderer.RenderPage(d.Document, d.Document.Pages[0], new RenderOptions(1));
            var item = await _ctx.Library!.AddCaptureAsync(d.Document, preview);
            d.LibraryItem = item;
            d.MarkSaved(item.Path, revision);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autosaving the new capture to the library failed");
            _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ex is ShotwiseException se ? se.Code : ErrorCode.LibraryUnavailable), MessageKind.Warning);
        }
        finally { d.SaveGate.Release(); }
    }

    private void WireSession(ImageEditorDocument d)
    {
        var s = d.Session;
        s.Message += (_, e) => Dispatcher.BeginInvoke(() => ShowEditorMessage(e));
        s.TextRecognized += (_, e) => RunUi(() => ShowOcrAsync(d, e));
        s.RedactionsFound += (_, _) => { if (ReferenceEquals(_active, d)) ShowPane(SideMode.Redact); };
        s.SmartComponentsDetected += (_, e) => { if (ReferenceEquals(_active, d) && e.Count == 0) _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ErrorCode.NoComponentsFound), MessageKind.Info); };
        s.ColorSampled += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            var hex = e.Color.ToRgbHex();
            _toasts.Show(ES.F("Msg_ColorPicked", "Picked {0}", hex), MessageKind.Info, TimeSpan.FromSeconds(5), (ES.T("Msg_CopyColor", "Copy"), () => { try { Clipboard.SetText(hex); } catch (System.Runtime.InteropServices.COMException) { } }));
        });
        s.ActiveToolChanged += (_, _) => { if (ReferenceEquals(_active, d)) _toolbar.SetActive(s.ActiveTool); };
        s.SelectionChanged += (_, _) => { if (ReferenceEquals(_active, d)) { UpdateStatus(); CommandManager.InvalidateRequerySuggested(); } };
        s.BusyChanged += (_, _) => { if (ReferenceEquals(_active, d)) BusyChanged(s); };
        s.Undo.Changed += (_, _) => { if (ReferenceEquals(_active, d)) { CommandManager.InvalidateRequerySuggested(); UpdateTitle(); UpdateStatus(); } };
        s.ActivePageChanged += (_, _) => { if (ReferenceEquals(_active, d)) UpdateStatus(); };
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _active)) UpdateTitle();
        _tray.QueueRefresh();
    }

    public async Task ActivateAsync(EditorDocument doc)
    {
        if (_active is VideoEditorDocument && !ReferenceEquals(_active, doc)) _video.Pause();
        if (_active is ImageEditorDocument prev && !ReferenceEquals(prev, doc)) _canvas.CommitTextEditing();
        _active = doc;
        _view = View.Document;
        if (doc is VideoEditorDocument v) await _video.LoadAsync(v);
        UpdateAll();
        if (doc is ImageEditorDocument img) BusyChanged(img.Session);
    }

    private async Task ActivateTrayItemAsync(TrayItem item)
    {
        if (item.Document is { } d) { await ActivateAsync(d); return; }
        if (item.Library is { } li) await OpenLibraryItemAsync(li);
    }

    private Task OpenLibraryItemAsync(LibraryItem item) => OpenFileAsync(item.Path);

    // ------------------------------------------------------------------ open

    public async Task OpenDocumentAsync(ShotDocument doc, bool activate)
    {
        var existing = _docs.OfType<ImageEditorDocument>().FirstOrDefault(d => ReferenceEquals(d.Document, doc) || doc.FilePath is not null && string.Equals(d.FilePath, doc.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { if (activate) await ActivateAsync(existing); return; }
        await AddImageDocumentAsync(doc, activate, autosaveToLibrary: true);
    }

    public async Task OpenFilesAsync(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try { await OpenFileAsync(f); }
            catch (Exception ex) when (ex is ShotwiseException or IOException or UnauthorizedAccessException)
            {
                _toasts.Show($"{Path.GetFileName(f)}: {(ex is ShotwiseException se ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(se.Code) : ex.Message)}", MessageKind.Error);
            }
        }
    }

    public async Task OpenFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}");
        var existing = _docs.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { await ActivateAsync(existing); return; }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (VideoExtensions.Contains(ext)) { await OpenVideoAsync(path); return; }
        if (ext == ".shotx")
        {
            var doc = await _ctx.Serializer.LoadAsync(path);
            doc.FilePath = path;
            doc.Assets.Codec ??= _ctx.Codec;
            await AddImageDocumentAsync(doc, true, false);
            return;
        }
        if (ext == ".snagx" && _ctx.Snagx is { } snagx && snagx.CanImport(path))
        {
            var doc = await snagx.ImportAsync(path);
            doc.Assets.Codec ??= _ctx.Codec;
            await AddImageDocumentAsync(doc, true, true);
            return;
        }
        if (ext == ".gif" && _ctx.Gif is { } gifService)
        {
            var anim = await gifService.DecodeAsync(path, CancellationToken.None);
            if (anim.Frames.Count > 1)
            {
                await OpenVideoDocumentAsync(path, GifInfo(anim), true, anim);
                return;
            }
        }
        var pages = await Task.Run(() => _ctx.Codec.LoadAllPages(path));
        if (pages.Count == 0) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "The file contains no image.");
        var imageDoc = DocumentFactory.FromImages(pages, path, _ctx.Codec);
        imageDoc.FilePath = path;
        var d = (ImageEditorDocument)await AddImageDocumentAsync(imageDoc, true, false);
        d.MarkSaved(path);
        if (ImageFormatExtensions.TryFromExtension(ext, out var fmt)) d.ExportFormat = fmt;
    }

    private static VideoInfo GifInfo(AnimatedImage anim)
        => new(anim.Duration, anim.Width, anim.Height, anim.Frames.Count / Math.Max(0.1, anim.Duration.TotalSeconds), false, anim.Frames.Count);

    public async Task OpenVideoAsync(string path)
    {
        path = Path.GetFullPath(path);
        // A freshly published recording may be moved into the library by the autosave owner (CONTRACT A26): open it there.
        if (_ctx.CaptureAutosave is { } autosave)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                if (await autosave.ForVideoAsync(path, cts.Token) is { } item && File.Exists(item.Path)) path = Path.GetFullPath(item.Path);
            }
            catch (OperationCanceledException ex) { _log.LogWarning(ex, "Waiting for the video autosave timed out"); }
        }
        var existing = _docs.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { await ActivateAsync(existing); return; }
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && _ctx.Gif is { } g)
        {
            var anim = await g.DecodeAsync(path, CancellationToken.None);
            await OpenVideoDocumentAsync(path, GifInfo(anim), true, anim);
            return;
        }
        if (_ctx.Video is null) throw new ShotwiseException(ErrorCode.DecoderUnavailable, "Video support is not available.");
        var info = await _ctx.Video.ProbeAsync(path);
        await OpenVideoDocumentAsync(path, info, false, null);
    }

    private async Task OpenVideoDocumentAsync(string path, VideoInfo info, bool isGif, AnimatedImage? anim)
    {
        var d = new VideoEditorDocument(path, info, isGif, anim);
        d.Changed += OnDocumentChanged;
        d.LibraryItem = await TryFindAsync(path);
        _docs.Add(d);
        _tray.QueueRefresh();
        await ActivateAsync(d);
    }

    private Task OpenFrameAsImageAsync(BgraImage frame, string title)
    {
        var md = DocumentMetadata.Create();
        var doc = ShotDocument.FromImage(frame, md, _ctx.Codec);
        return OpenNewImageAsync(doc, title);
    }

    private async Task OpenNewImageAsync(ShotDocument doc, string? title = null)
    {
        var d = (ImageEditorDocument)await AddImageDocumentAsync(doc, true, true);
        if (title is not null && d.LibraryItem is null) d.Title = title;
    }

    // ------------------------------------------------------------------ new / open dialogs

    private Task NewCaptureAsync()
    {
        if (_ctx.Capture is { } capture)
        {
            capture.ShowCaptureWindow();
            return Task.CompletedTask;
        }
        return _ctx.InfoAsync(ES.T("Menu_NewCapture", "New Capture"), ES.T("Msg_NoCapture", "Capture is not available."), MessageKind.Warning);
    }

    private async Task NewImageAsync()
    {
        var r = await _ctx.Dialogs.ShowNewImage(new NewImageRequest(new Size2(1280, 720), Argb.White) { Owner = this });
        if (r is null) return;
        var doc = ShotDocument.Create(r.Size, r.Background);
        doc.Pages[0].Dpi = r.Dpi;
        await AddImageDocumentAsync(doc, true, false);
    }

    private async Task NewFromClipboardAsync()
    {
        var payload = EditorClipboard.Read(_ctx);
        if (payload?.Image is { } image)
        {
            await OpenNewImageAsync(ShotDocument.FromImage(image, DocumentMetadata.Create(), _ctx.Codec));
            return;
        }
        _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ErrorCode.ClipboardEmpty), MessageKind.Warning);
    }

    private async Task OpenDialogAsync()
    {
        var exts = _ctx.Codec.SupportedFormats.Where(f => f.CanRead).Select(f => "*" + f.Extension.TrimStart('*')).ToList();
        var image = string.Join(";", exts.Concat(new[] { "*.shotx", "*.snagx" }).Distinct());
        var video = string.Join(";", VideoExtensions.Select(e => "*" + e));
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = $"{ES.T("Open_AllSupported", "All supported files")}|{image};{video};*.gif|{ES.T("Open_Images", "Images and documents")}|{image}|{ES.T("Open_Videos", "Videos")}|{video};*.gif|{ES.T("Open_All", "All files")}|*.*",
            InitialDirectory = Directory.Exists(_ctx.Prefs.LastOpenFolder) ? _ctx.Prefs.LastOpenFolder : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (dlg.FileNames.Length > 0) _ctx.Settings.Update(s => s.Editor.LastOpenFolder = Path.GetDirectoryName(dlg.FileNames[0]));
        await OpenFilesAsync(dlg.FileNames);
    }

    private async Task CreateFromTemplateAsync()
    {
        var r = await _ctx.Dialogs.ShowTemplatePicker(new TemplatePickerRequest() { Owner = this });
        if (r is null) return;
        var doc = r.Template.Instantiate(_ctx.Codec);
        await AddImageDocumentAsync(doc, true, false);
    }

    private async Task DuplicateAsync(EditorDocument? source)
    {
        if (source is ImageEditorDocument img)
        {
            img.Session.CommitTextEditSafe();
            var copy = await CloneDocumentAsync(img.Document);
            var d = (ImageEditorDocument)await AddImageDocumentAsync(copy, true, false);
            d.Title = ES.F("Doc_Copy", "{0} copy", img.Title);
            if (_ctx.Library is not null && _ctx.Settings.Current.Library.AutosaveImages) await AutosaveNewAsync(d);
        }
        else if (source?.FilePath is { } path && File.Exists(path))
        {
            var target = UniquePath(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " copy" + Path.GetExtension(path)));
            File.Copy(path, target);
            if (_ctx.Library is not null) { try { await _ctx.Library.ImportFileAsync(target); } catch (Exception ex) { _log.LogDebug(ex, "Library import of duplicate failed"); } }
            await OpenFileAsync(target);
        }
    }

    private async Task<ShotDocument> CloneDocumentAsync(ShotDocument doc)
    {
        using var ms = new MemoryStream();
        var thumb = _ctx.Renderer.RenderThumbnail(doc, 256);
        await _ctx.Serializer.SaveAsync(doc, ms, thumb);
        ms.Position = 0;
        var copy = await _ctx.Serializer.LoadAsync(ms);
        copy.FilePath = null;
        copy.Assets.Codec ??= _ctx.Codec;
        return copy;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ save

    private static bool IsShotx(string? path) => path is not null && path.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutosaveBacked(ImageEditorDocument d) => d.LibraryItem is not null || IsShotx(d.FilePath);

    public async Task<bool> SaveAsync(EditorDocument doc, bool saveAs, bool silent = false)
    {
        switch (doc)
        {
            case ImageEditorDocument img: return await SaveImageAsync(img, saveAs, silent);
            case VideoEditorDocument vid: return await SaveVideoAsync(vid, saveAs);
        }
        return false;
    }

    private async Task<bool> SaveImageAsync(ImageEditorDocument d, bool saveAs, bool silent)
    {
        if (ReferenceEquals(d, _active)) _canvas.CommitTextEditing(); else d.Session.CommitTextEditSafe();
        var doc = d.Document;
        try
        {
            if (!saveAs)
            {
                if (IsShotx(d.FilePath))
                {
                    await SaveShotxAsync(d, d.FilePath!);
                    return true;
                }
                if (d.FilePath is null && _ctx.Library is not null && _ctx.Settings.Current.Library.AutosaveImages)
                {
                    await AutosaveNewAsync(d);
                    if (d.FilePath is not null) return true;
                }
                if (d.FilePath is not null && d.ExportFormat is { } fmt && File.Exists(d.FilePath))
                {
                    await SaveFlatAsync(d, d.FilePath, fmt, d.ExportOptions);
                    return true;
                }
                if (silent) return false;
            }
            var formats = _ctx.Codec.SupportedFormats;
            var s = _ctx.Settings.Current.Share.SaveAs;
            var folder = !string.IsNullOrWhiteSpace(s.LastFolder) && Directory.Exists(s.LastFolder) ? s.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (s.FolderMode == Shotwise.Core.Settings.SaveFolderMode.Fixed && Directory.Exists(s.FixedFolder)) folder = s.FixedFolder;
            var defaultFormat = s.RememberLastFormat ? s.DefaultFormat : "png";
            var result = await _ctx.Dialogs.ShowSaveAs(new SaveAsRequest(Sanitize(d.Title), folder, false, formats, defaultFormat, new EncodeOptions(JpegQuality: s.JpegQuality)) { Owner = this });
            if (result is null) return false;
            var path = result.Path;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) { path += "." + result.Format.TrimStart('.'); ext = Path.GetExtension(path); }
            _ctx.Settings.Update(x =>
            {
                x.Share.SaveAs.LastFolder = Path.GetDirectoryName(path) ?? "";
                if (x.Share.SaveAs.RememberLastFormat) x.Share.SaveAs.DefaultFormat = ext.TrimStart('.').ToLowerInvariant();
            });
            if (string.Equals(ext, ".shotx", StringComparison.OrdinalIgnoreCase))
            {
                await SaveShotxAsync(d, path);
                d.ExportFormat = null;
                return true;
            }
            if (!ImageFormatExtensions.TryFromExtension(ext, out var format))
                throw new ShotwiseException(ErrorCode.UnsupportedFormat, $"Unsupported format {ext}");
            await SaveFlatAsync(d, path, format, result.Options);
            d.ExportFormat = format;
            d.ExportOptions = result.Options;
            _toasts.Show(ES.F("Msg_Saved", "Saved {0}", Path.GetFileName(path)), MessageKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!silent) await _ctx.ShowErrorAsync(ex); else _log.LogWarning(ex, "Autosave failed for {Title}", d.Title);
            return false;
        }
    }

    /// <summary>
    /// Writes the document as .shotx. One write per document at a time (an older snapshot can never replace a newer
    /// one); the serializer snapshots synchronously, so the revision captured just before identifies the saved content
    /// and edits made during the asynchronous write keep the document dirty (R5-#1).
    /// </summary>
    private async Task SaveShotxAsync(ImageEditorDocument d, string path)
    {
        await d.SaveGate.WaitAsync();
        try
        {
            // Library-backed documents always save to the item's current catalog path (the library may have moved/renamed it).
            if (d.LibraryItem is not null && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))
                path = await ResolveMovedPathAsync(d, path);
            var revision = d.Revision;
            var thumb = _ctx.Renderer.RenderThumbnail(d.Document, 512);
            d.Document.FilePath = path;
            await _ctx.Serializer.SaveAsync(d.Document, path, thumb);
            d.MarkSaved(path, revision);
            if (_ctx.Library is { } lib)
            {
                var item = d.LibraryItem ?? await TryFindAsync(path);
                if (item is not null)
                {
                    try { await lib.UpdateAsync(item, d.Document); d.LibraryItem = await lib.GetAsync(item.Id) ?? item; }
                    catch (Exception ex) { _log.LogDebug(ex, "Library re-index failed"); }
                }
            }
        }
        finally { d.SaveGate.Release(); }
    }

    /// <summary>Re-reads the library item of a library-backed document whose root moved (R5-#8); returns the current path.</summary>
    private async Task<string> ResolveMovedPathAsync(ImageEditorDocument d, string path)
    {
        if (_ctx.Library is not { } lib || d.LibraryItem is not { } li) return path;
        try
        {
            if (await lib.GetAsync(li.Id) is { } current && !string.IsNullOrEmpty(current.Path))
            {
                d.LibraryItem = current;
                return current.Path;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Library item lookup failed"); }
        return path;
    }

    /// <summary>Saves the document to a flat image file (re-export on Save) with the same revision rule as .shotx saves.</summary>
    private async Task SaveFlatAsync(ImageEditorDocument d, string path, ImageFormat format, EncodeOptions? options)
    {
        await d.SaveGate.WaitAsync();
        try
        {
            var revision = await ExportFlatAsync(d, path, format, options);
            d.MarkSaved(path, revision);
        }
        finally { d.SaveGate.Release(); }
    }

    /// <summary>
    /// Renders on the UI thread (the session is not thread-safe, so the document must not be read while it may change)
    /// and encodes/writes off-thread. Returns the revision that was rendered.
    /// </summary>
    private async Task<long> ExportFlatAsync(ImageEditorDocument d, string path, ImageFormat format, EncodeOptions? options)
    {
        var doc = d.Document;
        var renderer = _ctx.Renderer;
        var codec = _ctx.Codec;
        options ??= new EncodeOptions();
        var revision = d.Revision;
        if (doc.Pages.Count > 1 && format is ImageFormat.Tiff or ImageFormat.Pdf)
        {
            var pages = doc.Pages.Select(p => renderer.RenderPage(doc, p, new RenderOptions(1))).ToList();
            await Task.Run(() => codec.SaveMultipage(pages, path, format, options));
        }
        else
        {
            var img = renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(1, true, null, options.Transparency));
            await Task.Run(() => codec.Save(img, path, format, options));
        }
        if (_ctx.Library is { } lib && path.StartsWith(lib.LibraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            try { await lib.ImportFileAsync(path); } catch (Exception ex) { _log.LogDebug(ex, "Library import failed"); }
        }
        return revision;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private async Task<bool> SaveVideoAsync(VideoEditorDocument v, bool saveAs)
    {
        if (!ReferenceEquals(_active, v)) await ActivateAsync(v);
        var overwrite = !saveAs;
        if (overwrite && v.Edl.IsIdentity) return true;
        try
        {
            var saved = await _video.SaveAsync(overwrite);
            if (saved is null) return false;
            // reload so duration / thumbnails reflect the rendered file
            await ReplaceVideoDocumentAsync(v, saved);
            return true;
        }
        catch (Exception ex)
        {
            await _ctx.ShowErrorAsync(ex);
            return false;
        }
    }

    private async Task ReplaceVideoDocumentAsync(VideoEditorDocument old, string path)
    {
        var index = _docs.IndexOf(old);
        _docs.Remove(old);
        old.Changed -= OnDocumentChanged;
        if (_active == old) _active = null;
        if (string.Equals(old.FilePath, path, StringComparison.OrdinalIgnoreCase) && _ctx.Library is { } lib)
        {
            try { var item = await TryFindAsync(path); if (item is not null) await lib.UpdateAsync(item); } catch (Exception ex) { _log.LogDebug(ex, "Library re-index failed"); }
        }
        await OpenVideoAsync(path);
        var created = _docs.LastOrDefault();
        if (created is not null && index >= 0 && index < _docs.Count - 1) { _docs.Remove(created); _docs.Insert(index, created); }
        _tray.QueueRefresh();
    }

    // ------------------------------------------------------------------ library moves / renames (R5-#8)

    private bool _libraryMoving;
    private string? _knownLibraryRoot;
    private bool _remapQueued;

    private void OnLibraryChanged(object? sender, Shotwise.Core.Library.LibraryChangedEventArgs e)
    {
        if (e.Kind is Shotwise.Core.Library.LibraryChangeKind.Added) return;
        var ids = e.Kind == Shotwise.Core.Library.LibraryChangeKind.Reset ? null : e.Ids;
        Dispatcher.BeginInvoke(() =>
        {
            if (ids is not null && !_docs.Any(d => d.LibraryItem is { } li && ids.Contains(li.Id))) return;
            if (ids is null)
            {
                if (_remapQueued) return;
                _remapQueued = true;
            }
            RunUi(async () =>
            {
                if (ids is null) _remapQueued = false;
                await RemapLibraryDocumentsAsync(ids);
            });
        });
    }

    /// <summary>
    /// Points open library-backed documents at their item's current catalog path (after a library move, merge with
    /// collision renames, or a rename), by stable item id. Documents whose item no longer resolves are mapped by their
    /// path relative to the previous root, else detached from the library so they are never silently saved outside it.
    /// </summary>
    private async Task RemapLibraryDocumentsAsync(IReadOnlyList<Guid>? ids)
    {
        if (_ctx.Library is not { } lib) return;
        var previousRoot = _knownLibraryRoot;
        var root = lib.LibraryRoot;
        _knownLibraryRoot = root;
        foreach (var d in _docs.ToList())
        {
            if (d.LibraryItem is not { } li) continue;
            if (ids is not null && !ids.Contains(li.Id)) continue;
            Shotwise.Core.Library.LibraryItem? current = null;
            try
            {
                current = await lib.GetAsync(li.Id);
                if (current is null && previousRoot is not null && d.FilePath is { } old && !string.Equals(previousRoot, root, StringComparison.OrdinalIgnoreCase)
                    && old.StartsWith(previousRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(root, Path.GetRelativePath(previousRoot, old));
                    if (File.Exists(candidate)) current = await lib.FindByPathAsync(candidate);
                }
            }
            catch (Exception ex) when (ex is ShotwiseException or IOException or InvalidOperationException)
            {
                _log.LogDebug(ex, "Library lookup for an open document failed");
                continue;
            }
            if (current is null)
            {
                if (ids is null || (d.FilePath is { } p && !File.Exists(p))) DetachFromLibrary(d);
                continue;
            }
            d.LibraryItem = current;
            if (string.Equals(d.FilePath, current.Path, StringComparison.OrdinalIgnoreCase)) continue;
            _log.LogInformation("Open document moved with the library: {Old} -> {New}", d.FilePath, current.Path);
            switch (d)
            {
                case ImageEditorDocument img:
                    img.FilePath = current.Path;
                    img.Document.FilePath = current.Path;
                    img.RaiseChanged();
                    break;
                case VideoEditorDocument vid when !vid.IsDirty && File.Exists(current.Path):
                    await ReplaceVideoDocumentAsync(vid, current.Path);
                    break;
                case VideoEditorDocument vid:
                    vid.FilePath = current.Path;
                    vid.RaiseChanged();
                    break;
            }
        }
        _tray.QueueRefresh();
    }

    private void DetachFromLibrary(EditorDocument d)
    {
        _log.LogWarning("Open document {Title} is no longer in the library; it will be saved with Save As", d.Title);
        d.LibraryItem = null;
        if (d is ImageEditorDocument img && img.FilePath is { } p && !File.Exists(p))
        {
            img.FilePath = null;
            img.Document.FilePath = null;
            img.Document.IsDirty = true;
        }
        d.RaiseChanged();
        _toasts.Show(ES.F("Msg_DocLeftLibrary", "\"{0}\" is no longer in the library. Use Save As to keep your changes.", d.Title), MessageKind.Warning);
    }

    // ------------------------------------------------------------------ autosave

    private async Task AutosaveTickAsync()
    {
        foreach (var d in _docs.OfType<ImageEditorDocument>().ToList())
        {
            if (!d.IsDirty || !IsAutosaveBacked(d) || d.SaveGate.CurrentCount == 0 || _libraryMoving) continue;
            if (DateTime.UtcNow - d.LastChangeUtc < TimeSpan.FromSeconds(3) || d.Session.IsBusy || d.Session.ActiveTextEdit is not null) continue;
            await SaveImageAsync(d, false, true);
        }
    }

    private async Task AutosaveAllAsync()
    {
        foreach (var d in _docs.OfType<ImageEditorDocument>().ToList())
            if (d.IsDirty && IsAutosaveBacked(d)) await SaveImageAsync(d, false, true);
    }

    // ------------------------------------------------------------------ close

    public async Task<bool> CloseAllAsync()
    {
        foreach (var d in _docs.ToList())
            if (!await CloseDocumentAsync(d, true)) return false;
        return true;
    }

    public async Task<bool> CloseDocumentAsync(EditorDocument doc, bool prompt = true)
    {
        if (doc is ImageEditorDocument img)
        {
            if (ReferenceEquals(doc, _active)) _canvas.CommitTextEditing();
            if (img.IsDirty)
            {
                var saved = false;
                if (IsAutosaveBacked(img)) saved = await SaveImageAsync(img, false, true);
                if (!saved && prompt)
                {
                    var choice = await SaveChangesDialog.AskAsync(this, img.Title);
                    if (choice == SaveChoice.Cancel) return false;
                    if (choice == SaveChoice.Save && !await SaveImageAsync(img, false, false)) return false;
                }
            }
        }
        else if (doc is VideoEditorDocument vid && vid.IsDirty && prompt)
        {
            var choice = await SaveChangesDialog.AskAsync(this, vid.Title);
            if (choice == SaveChoice.Cancel) return false;
            if (choice == SaveChoice.Save && !await SaveVideoAsync(vid, false)) return false;
            if (!_docs.Contains(vid)) return true;
        }
        var index = _docs.IndexOf(doc);
        _docs.Remove(doc);
        doc.Changed -= OnDocumentChanged;
        var pinnedIds = _ctx.Settings.Current.Library.PinnedItems;
        if (_ctx.Settings.Current.Library.RemoveUnpinnedFromTrayOnClose && !(doc.LibraryItem is { } li && pinnedIds.Contains(li.Id.ToString())) && !doc.IsPinned && doc.FilePath is { } p)
            _tray.Forget(new TrayItem { Key = "p:" + p, Library = doc.LibraryItem, Document = doc, Title = doc.Title });
        if (ReferenceEquals(doc, _active))
        {
            if (doc is VideoEditorDocument) { _video.Pause(); await _video.LoadAsync(null); }
            _active = _docs.Count == 0 ? null : _docs[Math.Clamp(index - 1, 0, _docs.Count - 1)];
            if (_active is VideoEditorDocument nv) await _video.LoadAsync(nv);
            UpdateAll();
        }
        doc.Dispose();
        _tray.QueueRefresh();
        return true;
    }

    // ------------------------------------------------------------------ messages

    private void ShowEditorMessage(EditorMessage m)
    {
        var text = m.Kind == EditorMessageKind.FeatureUnavailable
            ? ES.F("Msg_FeatureUnavailable", "This feature needs a component that is not installed. {0}", m.Message)
            : m.Code != ErrorCode.Unknown ? Shotwise.UI.Common.Resources.Localizer.ErrorMessage(m.Code) : m.Message;
        if (string.IsNullOrWhiteSpace(text)) text = m.Message;
        var kind = m.Kind switch { EditorMessageKind.Error => MessageKind.Error, EditorMessageKind.Warning or EditorMessageKind.FeatureUnavailable => MessageKind.Warning, _ => MessageKind.Info };
        _toasts.Show(text, kind);
    }

    private async Task ShowOcrAsync(ImageEditorDocument d, TextRecognizedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Result.Text))
        {
            _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ErrorCode.OcrFailed), MessageKind.Info);
            return;
        }
        TableResult? table = null;
        try
        {
            table = _ctx.Tables?.Extract(e.Result);
            if (table is { IsEmpty: true }) table = null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Table extraction failed"); }
        if (d.LibraryItem is { } li && e.Region is null && _ctx.Library is { } lib)
        {
            try { await lib.SetOcrTextAsync(li.Id, e.Result.Text); } catch (Exception ex) { _log.LogDebug(ex, "Storing OCR text failed"); }
        }
        await _ctx.Dialogs.ShowOcrResult(new OcrResultRequest(e.Result, table) { Owner = this });
    }
}

/// <summary>Builds documents from decoded raster pages.</summary>
internal static class DocumentFactory
{
    public static ShotDocument FromImages(IReadOnlyList<BgraImage> pages, string? sourcePath, IImageCodec codec)
    {
        var md = DocumentMetadata.Create(sourcePath is not null && File.Exists(sourcePath) ? new DateTimeOffset(File.GetLastWriteTime(sourcePath)) : null);
        var doc = ShotDocument.FromImage(pages[0], md, codec);
        for (var i = 1; i < pages.Count; i++)
        {
            var img = pages[i];
            var asset = doc.Assets.AddImage(img);
            doc.Pages.Add(new Page
            {
                CanvasSize = new Size2(img.Width, img.Height),
                Dpi = img.DpiX,
                Background = new CanvasBackground(Argb.Transparent, null, new BaseImageRef(asset, Point2.Zero)),
            });
        }
        return doc;
    }
}
