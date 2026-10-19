using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Editor;
using Shotwise.Editor.Tools;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using Shotwise.UI.Editor.Share;
using Shotwise.UI.Editor.Tray;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Shotwise.UI.Editor;

internal sealed partial class EditorWindow
{
    // ------------------------------------------------------------------ selection helpers

    private IReadOnlyList<TrayItem> TraySelectionOrActiveItems()
    {
        if (_tray.IsKeyboardFocusWithin && _tray.Selected.Count > 0) return _tray.Selected;
        if (_active is { } d) return new[] { new TrayItem { Key = "d:" + d.Id, Document = d, Library = d.LibraryItem, Title = d.Title } };
        return _tray.Selected;
    }

    private IReadOnlyList<EditorDocument> TraySelectionOrActive()
        => TraySelectionOrActiveItems().Where(i => i.Document is not null).Select(i => i.Document!).ToList();

    private async Task<LibraryItem?> EnsureLibraryItemAsync(TrayItem item)
    {
        if (item.Library is not null) return item.Library;
        if (item.Document?.LibraryItem is { } li) return li;
        if (item.Path is { } p) return await TryFindAsync(p);
        return null;
    }

    // ------------------------------------------------------------------ tray / library commands

    private async Task HandleTrayCommandAsync(IReadOnlyList<TrayItem> items, string command)
    {
        if (items.Count == 0) return;
        switch (command)
        {
            case "open": await ActivateTrayItemAsync(items[0]); break;
            case "share":
                // library card hover action: open the item, then drop down the Share destinations
                await ActivateTrayItemAsync(items[0]);
                await Dispatcher.InvokeAsync(() => { if (_active is not null && _shareButton.IsEnabled) _shareButton.IsDropDownOpen = true; }, System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case "pin": await PinAsync(items); break;
            case "rename": await RenameAsync(items[0]); break;
            case "duplicate": await DuplicateItemAsync(items[0]); break;
            case "close":
                foreach (var d in items.Where(i => i.Document is not null).Select(i => i.Document!).ToList()) await CloseDocumentAsync(d);
                break;
            case "delete": await DeleteItemsAsync(items); break;
            case "combineImages": await CombineImagesAsync(items); break;
            case "combineVideos": await CombineVideosAsync(items); break;
            case "createGif": await CreateGifAsync(items); break;
            case "tag": await TagAsync(items); break;
            case "reveal": RevealInExplorer(items[0].Path); break;
            case "shareHistory": await _share.ShowHistoryAsync(items[0].Document, await EnsureLibraryItemAsync(items[0])); break;
        }
    }

    private Task HandleLibraryCommandAsync(IReadOnlyList<LibraryItem> items, string command)
    {
        var tray = items.Select(i => new TrayItem { Key = "p:" + i.Path, Library = i, Title = i.Name, Document = _docs.FirstOrDefault(d => string.Equals(d.FilePath, i.Path, StringComparison.OrdinalIgnoreCase)) }).ToList();
        return HandleTrayCommandAsync(tray, command);
    }

    private async Task DuplicateItemAsync(TrayItem item)
    {
        if (item.Document is not null) { await DuplicateAsync(item.Document); return; }
        if (item.Path is not { } path) return;
        if (path.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await _ctx.Serializer.LoadAsync(path);
            doc.FilePath = null;
            doc.Assets.Codec ??= _ctx.Codec;
            var d = (ImageEditorDocument)await AddImageDocumentAsync(doc, true, true);
            if (d.LibraryItem is null) d.Title = ES.F("Doc_Copy", "{0} copy", item.Title);
            return;
        }
        var fake = new VideoEditorDocument(path, new VideoInfo(TimeSpan.Zero, 0, 0, 0, false, 0), false, null);
        await DuplicateAsync(fake);
    }

    private async Task PinAsync(IReadOnlyList<TrayItem> items)
    {
        foreach (var item in items)
        {
            var li = await EnsureLibraryItemAsync(item);
            if (li is null)
            {
                if (item.Document is { } d) d.IsPinned = !d.IsPinned;
                continue;
            }
            var id = li.Id.ToString();
            _ctx.Settings.Update(s =>
            {
                if (!s.Library.PinnedItems.Remove(id)) s.Library.PinnedItems.Add(id);
            });
        }
        _tray.QueueRefresh();
    }

    private async Task RenameAsync(TrayItem? item)
    {
        if (item is null) return;
        var name = await InputDialog.AskAsync(this, ES.T("Cmd_Rename", "Rename"), ES.T("Rename_Prompt", "New name"), item.Title);
        if (string.IsNullOrWhiteSpace(name) || name == item.Title) return;
        var li = await EnsureLibraryItemAsync(item);
        if (li is not null && _ctx.Library is { } lib)
        {
            await lib.RenameAsync(li.Id, name);
            var updated = await lib.GetAsync(li.Id);
            if (item.Document is { } d && updated is not null)
            {
                d.LibraryItem = updated;
                d.FilePath = updated.Path;
                if (d is ImageEditorDocument img) img.Document.FilePath = updated.Path;
                d.Title = updated.Name;
            }
        }
        else if (item.Document is { } doc) doc.Title = name;
        _tray.QueueRefresh();
    }

    private async Task DeleteItemsAsync(IReadOnlyList<TrayItem> items)
    {
        if (items.Count == 0) return;
        var libraryItems = new List<LibraryItem>();
        foreach (var i in items) if (await EnsureLibraryItemAsync(i) is { } li) libraryItems.Add(li);
        if (_ctx.Settings.Current.Library.PromptBeforeDelete)
        {
            var message = items.Count == 1 ? ES.F("Lib_DeleteOne", "Move \"{0}\" to the Recycle Bin?", items[0].Title) : ES.F("Lib_DeleteMany", "Move {0} items to the Recycle Bin?", items.Count);
            var r = await _ctx.Dialogs.Confirm(new ConfirmRequest(ES.T("Cmd_MoveToRecycleBin", "Move to Recycle Bin"), message, ES.T("Cmd_MoveToRecycleBin", "Move to Recycle Bin"), null, true, "Library.PromptBeforeDelete") { Owner = this });
            if (!r.Confirmed) return;
            if (r.DontAskAgain) _ctx.Settings.Update(s => s.Library.PromptBeforeDelete = false);
        }
        foreach (var i in items.Where(i => i.Document is not null)) await CloseDocumentAsync(i.Document!, prompt: false, discardOnly: true);
        if (_ctx.Library is { } lib && libraryItems.Count > 0) await lib.DeleteAsync(libraryItems.Select(l => l.Id), true);
        else foreach (var i in items.Where(i => i.Path is not null && File.Exists(i.Path))) RecycleFile(i.Path!);
        _tray.QueueRefresh();
    }

    private Task<bool> CloseDocumentAsync(EditorDocument doc, bool prompt, bool discardOnly)
    {
        // discard: skip all saving (the file is going to the Recycle Bin)
        if (discardOnly && doc is ImageEditorDocument img) img.Session.Undo.MarkClean();
        if (discardOnly) img_ClearDirty(doc);
        return CloseDocumentAsync(doc, prompt);
    }

    private static void img_ClearDirty(EditorDocument doc)
    {
        if (doc is ImageEditorDocument i) i.MarkSaved(null);
        else if (doc is VideoEditorDocument v) v.Undo.MarkClean();
    }

    private static void RecycleFile(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception) { }
    }

    private async Task TagAsync(IReadOnlyList<TrayItem> items)
    {
        if (_ctx.Library is not { } lib) return;
        var libs = new List<LibraryItem>();
        foreach (var i in items) if (await EnsureLibraryItemAsync(i) is { } li) libs.Add(li);
        if (libs.Count == 0)
        {
            _toasts.Show(ES.T("Msg_NotInLibrary", "Save the image to the library first to tag it."), MessageKind.Info);
            return;
        }
        var current = libs.Count == 1 ? libs[0].Tags : libs.SelectMany(i => i.Tags).GroupBy(t => t).Where(g => g.Count() == libs.Count).Select(g => g.Key).ToList();
        var all = await lib.AllTagsAsync();
        var result = await _ctx.Dialogs.ShowTagEditor(new TagEditorRequest(current, all) { Owner = this });
        if (result is null) return;
        var ids = libs.Select(l => l.Id).ToList();
        if (result.Added.Count > 0) await lib.AddTagsAsync(ids, result.Added);
        if (result.Removed.Count > 0) await lib.RemoveTagsAsync(ids, result.Removed);
        foreach (var l in libs)
            if (_docs.FirstOrDefault(d => d.LibraryItem?.Id == l.Id) is { } d) d.LibraryItem = await lib.GetAsync(l.Id) ?? l;
    }

    // ------------------------------------------------------------------ image operations

    private async Task ApplyEffectAsync(EffectSpec effect)
    {
        if (Session is not { } s) return;
        _canvas.CommitTextEditing();
        await s.ApplyEffectAsync(effect);
    }

    private async Task CanvasColorAsync()
    {
        if (Session is not { } s) return;
        var r = await _ctx.Dialogs.ShowColorPicker(new ColorPickerRequest(s.ActivePage.Background.Color, true, ES.T("Cmd_CanvasColor", "Canvas Color…")) { Owner = this });
        if (r is not null) s.SetCanvasColor(r.Color);
    }

    private async Task CustomAngleAsync()
    {
        var text = await InputDialog.AskAsync(this, ES.T("Cmd_CustomAngle", "Custom Angle…"), ES.T("Msg_AnglePrompt", "Rotate by (degrees, positive = clockwise)"), "15");
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var angle) && !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out angle)) return;
        await ApplyEffectAsync(new RotateEffect(angle, true, false, false));
    }

    private async Task ResizeCanvasAsync()
    {
        if (Session is not { } s) return;
        var page = s.ActivePage;
        var r = await _ctx.Dialogs.ShowResizeCanvas(new ResizeCanvasRequest(page.CanvasSize, page.Background.Color, page.Dpi) { Owner = this });
        if (r is not null) s.ResizeCanvas(r.Size, r.Anchor, r.Fill);
    }

    private async Task ResizeImageAsync()
    {
        if (Session is not { } s) return;
        var r = await _ctx.Dialogs.ShowResizeImage(new ResizeImageRequest(s.ActivePage.CanvasSize, s.ActivePage.Dpi) { Owner = this });
        if (r is not null) await ApplyEffectAsync(r.Resize);
    }

    // ------------------------------------------------------------------ combine / templates

    private async Task<BgraImage> RenderItemAsync(TrayItem item)
    {
        if (item.Document is ImageEditorDocument img)
        {
            img.Session.CommitTextEditSafe();
            return _ctx.Renderer.RenderPage(img.Document, img.Document.Pages[0], new RenderOptions(1));
        }
        var path = item.Path ?? throw new ShotwiseException(ErrorCode.FileNotFound, "The item has no file.");
        if (path.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await _ctx.Serializer.LoadAsync(path);
            doc.Assets.Codec ??= _ctx.Codec;
            return _ctx.Renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(1));
        }
        return await Task.Run(() => _ctx.Codec.Load(path));
    }

    private IReadOnlyList<TrayItem> ImageSources(IReadOnlyList<TrayItem>? items)
    {
        var list = (items ?? (_tray.Selected.Count >= 2 ? _tray.Selected : _docs.Select(d => new TrayItem { Key = "d:" + d.Id, Document = d, Title = d.Title }).ToList())).Where(i => !i.IsVideo).ToList();
        return list;
    }

    private async Task CombineImagesAsync(IReadOnlyList<TrayItem>? items)
    {
        var sources = ImageSources(items);
        if (sources.Count < 2)
        {
            _toasts.Show(ES.T("Msg_NeedTwoImages", "Select at least two images in the tray to combine them."), MessageKind.Info);
            return;
        }
        var images = new List<(Guid Id, TrayItem Item, BgraImage Image)>();
        foreach (var s in sources) images.Add((Guid.NewGuid(), s, await RenderItemAsync(s)));
        var offered = images.Select(i => new CombineImageItem(i.Id, i.Item.Title, ImageOps.Downscale(i.Image, 200), new Size2(i.Image.Width, i.Image.Height))).ToList();
        var result = await _ctx.Dialogs.ShowCombineImages(new CombineImagesRequest(offered) { Owner = this });
        if (result is null) return;
        var map = images.ToDictionary(i => i.Id, i => i.Image);
        var ordered = result.Order.Where(map.ContainsKey).Select(id => map[id]).ToList();
        await OpenTemplateWithImagesAsync(result.Template, ordered, result.Caption);
    }

    private async Task CombineInTemplateAsync(IReadOnlyList<TrayItem>? items)
    {
        var sources = ImageSources(items);
        if (sources.Count == 0 && ImageDoc is { } cur) sources = new[] { new TrayItem { Key = "d:" + cur.Id, Document = cur, Title = cur.Title } };
        if (sources.Count == 0) return;
        var picked = await _ctx.Dialogs.ShowTemplatePicker(new TemplatePickerRequest(sources.Count) { Owner = this });
        if (picked is null) return;
        var images = new List<BgraImage>();
        foreach (var s in sources) images.Add(await RenderItemAsync(s));
        await OpenTemplateWithImagesAsync(picked.Template, images, null);
    }

    private async Task ApplyTemplateAsync()
    {
        if (ImageDoc is not { } cur) return;
        await CombineInTemplateAsync(new[] { new TrayItem { Key = "d:" + cur.Id, Document = cur, Title = cur.Title } });
    }

    private async Task OpenTemplateWithImagesAsync(TemplateDefinition template, IReadOnlyList<BgraImage> images, string? caption)
    {
        var doc = template.Instantiate(_ctx.Codec);
        var zones = doc.Pages[0].AllAnnotations().OfType<DropZoneAnnotation>().ToList();
        for (var i = 0; i < zones.Count && i < images.Count; i++)
            zones[i].Content = doc.Assets.AddImage(images[i]);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var page = doc.Pages[0];
            var text = new TextAnnotation
            {
                Start = new Point2(16, 8),
                End = new Point2(Math.Max(64, page.CanvasSize.Width - 16), 52),
                Text = RichText.FromPlain(caption.Trim(), "Segoe UI", 24, Argb.Black, true),
                TextStyle = new TextLayoutStyle { HAlign = HorizontalAlign.Center, AutoSize = false },
            };
            page.Annotations.Add(text);
        }
        await AddImageDocumentAsync(doc, true, true);
    }

    private async Task CombineVideosAsync(IReadOnlyList<TrayItem>? items)
    {
        if (_ctx.Video is null) { await _ctx.InfoAsync(ES.T("Cmd_CombineVideos", "Combine Videos…"), ES.T("Msg_NoVideo", "Video support is not available."), MessageKind.Warning); return; }
        var paths = (items ?? (_tray.Selected.Count >= 2 ? _tray.Selected : _docs.OfType<VideoEditorDocument>().Where(v => !v.IsGif).Select(v => new TrayItem { Key = "d:" + v.Id, Document = v, Title = v.Title }).ToList()))
            .Where(i => i.IsVideo && i.Path is not null && !i.Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)).Select(i => i.Path!).ToList();
        if (paths.Count < 2) { _toasts.Show(ES.T("Msg_NeedTwoVideos", "Select at least two videos to combine."), MessageKind.Info); return; }
        var suggested = Path.Combine(Path.GetDirectoryName(paths[0]) ?? _ctx.Paths.Temp, "Combined " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".mp4");
        var r = await _ctx.Dialogs.ShowCombineVideos(new CombineVideosRequest(paths, suggested) { Owner = this });
        if (r is null) return;
        var ok = await _overlay.RunAsync(ES.T("Msg_CombiningVideos", "Combining videos…"), Path.GetFileName(r.OutputPath), true, (p, ct) => _ctx.Video.CombineAsync(r.OrderedPaths, r.OutputPath, r.Options, p, ct));
        if (!ok) return;
        if (_ctx.Library is { } lib) { try { await lib.ImportFileAsync(r.OutputPath); } catch (Exception ex) { _log.LogDebug(ex, "Library import failed"); } }
        await OpenVideoAsync(r.OutputPath);
    }

    private async Task CreateGifAsync(IReadOnlyList<TrayItem> items)
    {
        if (_ctx.Gif is not { } gif) { await _ctx.InfoAsync(ES.T("Tray_CreateGif", "Create GIF…"), ES.T("Msg_NoGif", "GIF creation is not available."), MessageKind.Warning); return; }
        if (items.Count == 1 && items[0].IsVideo)
        {
            if (items[0].Document is not VideoEditorDocument) await OpenFileAsync(items[0].Path!);
            else await ActivateAsync(items[0].Document!);
            await _video.CreateGifAsync();
            return;
        }
        var images = items.Where(i => !i.IsVideo).ToList();
        if (images.Count < 2) return;
        var frames = new List<BgraImage>();
        foreach (var i in images) frames.Add(await RenderItemAsync(i));
        var first = frames[0];
        var suggested = Path.Combine(_ctx.Paths.Temp, "Animation.gif");
        var save = await _ctx.Dialogs.ShowSaveAs(new SaveAsRequest("Animation", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), true, _ctx.Codec.SupportedFormats, "gif") { Owner = this });
        if (save is null) return;
        var path = Path.ChangeExtension(save.Path, ".gif");
        var options = gif.DefaultsFor(GifPreset.Custom, first.Width, first.Height) with { Width = first.Width, Height = first.Height, Fps = 1 };
        async IAsyncEnumerable<(BgraImage Frame, TimeSpan Delay)> Seq()
        {
            foreach (var f in frames)
            {
                var scaled = f.Width == first.Width && f.Height == first.Height ? f : ImageOps.ResizeArea(f, first.Width, first.Height);
                yield return (scaled, TimeSpan.FromSeconds(1));
                await Task.Yield();
            }
        }
        var ok = await _overlay.RunAsync(ES.T("Msg_CreatingGif", "Creating GIF…"), Path.GetFileName(path), true, (p, ct) => gif.CreateFromFramesAsync(Seq(), path, options, ct));
        if (ok) await OpenFileAsync(path);
    }

    // ------------------------------------------------------------------ import / library maintenance

    private async Task ImportToLibraryAsync()
    {
        if (_ctx.Library is not { } lib) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = ES.T("Open_All", "All files") + "|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var files = dlg.FileNames;
        var imported = 0;
        await _overlay.RunAsync(ES.T("Msg_Importing", "Importing to the library…"), null, true, async (p, ct) =>
        {
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try { await lib.ImportFileAsync(files[i], ct); imported++; }
                catch (Exception ex) when (ex is ShotwiseException or IOException) { _log.LogWarning(ex, "Import failed for {File}", files[i]); }
                p.Report((i + 1) / (double)files.Length);
            }
        });
        _toasts.Show(ES.F("Msg_Imported", "Imported {0} of {1} files", imported, files.Length), imported == files.Length ? MessageKind.Success : MessageKind.Warning);
        _tray.QueueRefresh();
    }

    private async Task ImportStampsAsync()
    {
        if (_ctx.Stamps is not { } stamps) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = ES.T("Stamp_PackFilter", "Stamp packs (*.shotstamps)|*.shotstamps|All files|*.*") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) await stamps.ImportPackAsync(f);
        _toasts.Show(ES.T("Msg_StampsImported", "Stamps imported"), MessageKind.Success);
    }

    private async Task ImportTemplatesAsync()
    {
        if (_ctx.Templates is not { } templates) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = ES.T("Template_Filter", "Shotwise templates (*.shottemplate)|*.shottemplate|All files|*.*") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) await templates.ImportAsync(f);
        _toasts.Show(ES.T("Msg_TemplatesImported", "Templates imported"), MessageKind.Success);
    }

    private async Task LibraryBackupAsync()
    {
        if (_ctx.Library is not { } lib) return;
        var dlg = new SaveFileDialog { Filter = ES.T("Backup_Filter", "Shotwise library backup (*.shotarchive)|*.shotarchive"), FileName = $"Shotwise library {DateTime.Now:yyyy-MM-dd}.shotarchive", DefaultExt = ".shotarchive" };
        if (dlg.ShowDialog(this) != true) return;
        var ok = await _overlay.RunAsync(ES.T("Msg_BackingUp", "Backing up the library…"), Path.GetFileName(dlg.FileName), true, (p, ct) => lib.BackupAsync(dlg.FileName, p, ct));
        if (ok) _toasts.Show(ES.T("Msg_BackupDone", "Library backup created"), MessageKind.Success, null, (ES.T("Lib_Reveal", "Show in Explorer"), () => RevealInExplorer(dlg.FileName)));
    }

    private async Task LibraryRestoreAsync()
    {
        if (_ctx.Library is not { } lib) return;
        var dlg = new OpenFileDialog { Filter = ES.T("Backup_Filter", "Shotwise library backup (*.shotarchive)|*.shotarchive") };
        if (dlg.ShowDialog(this) != true) return;
        var confirm = await _ctx.Dialogs.Confirm(new ConfirmRequest(ES.T("Cmd_LibraryRestore", "Restore Backup…"), ES.T("Msg_RestoreConfirm", "Items from the backup are added to your library. Existing files are never overwritten. Continue?"), ES.T("Msg_Restore", "Restore")) { Owner = this });
        if (!confirm.Confirmed) return;
        var ok = await _overlay.RunAsync(ES.T("Msg_Restoring", "Restoring the library…"), Path.GetFileName(dlg.FileName), true, (p, ct) => lib.RestoreAsync(dlg.FileName, p, ct));
        if (ok) { _toasts.Show(ES.T("Msg_RestoreDone", "Backup restored"), MessageKind.Success); _tray.QueueRefresh(); }
    }

    private async Task LibraryLocationAsync()
    {
        if (_ctx.Library is not { } lib) return;
        var r = await _ctx.Dialogs.ShowLibraryLocation(new LibraryLocationRequest(lib.LibraryRoot) { Owner = this });
        if (r is null) return;
        if (r.Action == LibraryLocationAction.UseWithoutMoving)
        {
            _ctx.Settings.Update(s => s.Library.LibraryRoot = r.NewRoot);
            await _ctx.InfoAsync(ES.T("Cmd_LibraryLocation", "Library Location…"), ES.T("Msg_RestartForLibrary", "The new library location is used the next time Shotwise starts."));
            return;
        }
        // R5-#8: persist pending edits first, then block saves of open documents while files move, and point every open
        // library-backed document at its item's new catalog path before saving can resume.
        _canvas.CommitTextEditing();
        await AutosaveAllAsync();
        var gates = _docs.OfType<ImageEditorDocument>().Select(d => d.SaveGate).ToList();
        foreach (var g in gates) await g.WaitAsync();
        _libraryMoving = true;
        var ok = false;
        try
        {
            ok = await _overlay.RunAsync(ES.T("Msg_MovingLibrary", "Moving the library…"), r.NewRoot, true, (p, ct) => lib.MoveLibraryAsync(r.NewRoot, p, ct));
            await RemapLibraryDocumentsAsync(null);
        }
        finally
        {
            _libraryMoving = false;
            foreach (var g in gates) g.Release();
        }
        if (ok) { _toasts.Show(ES.T("Msg_LibraryMoved", "Library moved"), MessageKind.Success); _tray.QueueRefresh(); }
    }

    // ------------------------------------------------------------------ print / export

    private IReadOnlyList<BgraImage> RenderPagesForPrint()
    {
        var d = ImageDoc!;
        d.Session.CommitTextEditSafe();
        return d.Document.Pages.Select(p => _ctx.Renderer.RenderPage(d.Document, p, new RenderOptions(1))).ToList();
    }

    private async Task PrintAsync(bool quick)
    {
        if (ImageDoc is not { } d) return;
        var pages = RenderPagesForPrint();
        var r = await _ctx.Dialogs.ShowPrint(new PrintRequest(pages, new[] { d.Title }, _printSettings, quick) { Owner = this });
        if (r is not null) _printSettings = r.Settings;
    }

    private async Task PrintPreviewAsync()
    {
        if (ImageDoc is null) return;
        await _ctx.Dialogs.ShowPrintPreview(new PrintPreviewRequest(RenderPagesForPrint(), _printSettings) { Owner = this });
    }

    private async Task PageSetupAsync()
    {
        var r = await _ctx.Dialogs.ShowPageSetup(new PageSetupRequest(_printSettings) { Owner = this });
        if (r is not null) _printSettings = r.Settings;
    }

    private async Task BatchExportAsync()
    {
        var items = TraySelectionOrActiveItems();
        var files = items.Select(i => i.Path).Where(p => p is not null && File.Exists(p)).Cast<string>().ToList();
        if (files.Count == 0 && _docs.OfType<ImageEditorDocument>().Any())
        {
            // unsaved documents: export from memory via temp files
            foreach (var d in _docs.OfType<ImageEditorDocument>().Where(d => d.FilePath is null))
            {
                var temp = Path.Combine(_ctx.Paths.Temp, "batch", Sanitize(d.Title) + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                await ExportFlatAsync(d, temp, ImageFormat.Png, null);
                files.Add(temp);
            }
        }
        var r = await _ctx.Dialogs.ShowBatchExport(new BatchExportRequest(files) { Owner = this });
        if (r is null || r.Files.Count == 0) return;
        var done = 0;
        var ok = await _overlay.RunAsync(ES.T("Msg_BatchExport", "Exporting…"), null, true, async (p, ct) =>
        {
            Directory.CreateDirectory(r.OutputFolder);
            var naming = r.Naming ?? _ctx.Settings.Current.Share.Naming;
            var counter = naming.NextCounter;
            for (var i = 0; i < r.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var src = r.Files[i];
                try
                {
                    await Task.Run(() => ExportOne(src, r, naming, counter + i), ct);
                    done++;
                }
                catch (Exception ex) when (ex is ShotwiseException or IOException) { _log.LogWarning(ex, "Batch export failed for {File}", src); }
                p.Report((i + 1) / (double)r.Files.Count);
            }
            if (r.UseAutomaticNaming) _ctx.Settings.Update(s => s.Share.Naming.NextCounter = counter + r.Files.Count);
        });
        if (ok) _toasts.Show(ES.F("Msg_BatchDone", "Exported {0} of {1} files", done, r.Files.Count), done == r.Files.Count ? MessageKind.Success : MessageKind.Warning, null, (ES.T("Lib_Reveal", "Show in Explorer"), () => OpenFolder(r.OutputFolder)));
    }

    private void ExportOne(string src, BatchExportResult r, Shotwise.Core.Settings.NamingSettings naming, int counter)
    {
        BgraImage image;
        DocumentMetadata? md = null;
        if (src.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = Task.Run(() => _ctx.Serializer.LoadAsync(src)).GetAwaiter().GetResult();
            doc.Assets.Codec ??= _ctx.Codec;
            md = doc.Metadata;
            image = _ctx.Renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(1));
        }
        else image = _ctx.Codec.Load(src);
        if (r.Effects.Count > 0) image = _ctx.Effects.ApplyAll(image, r.Effects, md).Image;
        if (!ImageFormatExtensions.TryFromExtension(r.Format, out var fmt)) fmt = ImageFormat.Png;
        var name = r.UseAutomaticNaming ? AutoName(naming, md, image, counter) : Path.GetFileNameWithoutExtension(src);
        var target = Path.Combine(r.OutputFolder, name + fmt.DefaultExtension());
        if (!naming.PromptBeforeOverwrite || !File.Exists(target)) { }
        else target = UniquePath(target);
        _ctx.Codec.Save(image, target, fmt, r.Options);
    }

    private static string AutoName(Shotwise.Core.Settings.NamingSettings n, DocumentMetadata? md, BgraImage img, int counter)
    {
        var now = md?.CaptureDate ?? DateTimeOffset.Now;
        var name = n.Prefix + n.Pattern;
        name = name.Replace("{date}", now.ToString(n.DateFormat)).Replace("{time}", now.ToString(n.TimeFormat))
            .Replace("{app}", md?.AppName ?? "").Replace("{window}", md?.WindowTitle ?? "").Replace("{preset}", md?.SourcePresetId ?? "")
            .Replace("{width}", img.Width.ToString()).Replace("{height}", img.Height.ToString())
            .Replace("{counter}", counter.ToString("D" + Math.Clamp(n.CounterDigits, 1, 12)));
        return Sanitize(name);
    }

    private static void OpenFolder(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ drag out of the status bar grip

    private void OnDragGrip(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _active is null) return;
        try
        {
            var item = new TrayItem { Key = "d:" + _active.Id, Document = _active, Title = _active.Title };
            var temp = ExportActiveForDrag(item);
            if (temp is null) return;
            var data = new DataObject();
            var sc = new System.Collections.Specialized.StringCollection { temp };
            data.SetFileDropList(sc);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Drag out failed"); }
    }

    private string? ExportActiveForDrag(TrayItem item)
    {
        if (item.Document is VideoEditorDocument v) return v.FilePath;
        if (item.Document is not ImageEditorDocument d) return null;
        d.Session.CommitTextEditSafe();
        var folder = Path.Combine(_ctx.Paths.Temp, "drag");
        Directory.CreateDirectory(folder);
        var ext = _ctx.Prefs.DragAndDropFileType.TrimStart('.').ToLowerInvariant();
        if (!ImageFormatExtensions.TryFromExtension(ext, out var fmt)) { fmt = ImageFormat.Png; ext = "png"; }
        var target = Path.Combine(folder, Sanitize(d.Title) + "." + ext);
        var img = _ctx.Renderer.RenderPage(d.Document, d.Session.ActivePage, new RenderOptions(1));
        _ctx.Codec.Save(img, target, fmt);
        return target;
    }

    // ------------------------------------------------------------------ canvas context menu

    private void ShowCanvasContextMenu(Point2 at)
    {
        if (Session is not { } s) return;
        var menu = new ContextMenu();
        MenuItem Cmd(ICommand c, string header, string? gesture = null, string? glyph = null)
        {
            var m = new MenuItem { Command = c, Header = header, CommandTarget = this, Icon = MenuIcon(glyph) };
            if (gesture is not null) m.InputGestureText = gesture;
            return m;
        }
        var sel = s.SelectedAnnotations();
        if (sel.Count == 1)
        {
            switch (sel[0])
            {
                case StepAnnotation step:
                    var edit = new MenuItem { Header = ES.T("Props_EditValue", "Edit value…"), Icon = MenuIcon(Icons.Glyph.Edit) };
                    edit.Click += (_, _) => RunUi(async () =>
                    {
                        var v = await InputDialog.AskAsync(this, ES.T("Props_EditValue", "Edit value…"), ES.T("Props_StepValuePrompt", "Step value (number)"), step.Value.ToString());
                        if (int.TryParse(v, out var n) && n > 0) s.EditStepValue(step.Id, n);
                    });
                    var restart = new MenuItem { Header = ES.T("Props_RestartSequence", "Restart sequence"), Icon = MenuIcon(Icons.Glyph.Refresh) };
                    restart.Click += (_, _) => s.RestartStepSequence(step.Sequence, 1);
                    menu.Items.Add(edit);
                    menu.Items.Add(restart);
                    menu.Items.Add(new Separator());
                    break;
                case CalloutAnnotation callout:
                    var addTail = new MenuItem { Header = ES.T("Props_AddTail", "Add tail"), Icon = MenuIcon(Icons.Glyph.Add) };
                    addTail.Click += (_, _) => CalloutTool.AddTail(s, callout.Id);
                    var removeTail = new MenuItem { Header = ES.T("Props_RemoveTail", "Remove tail"), Icon = MenuIcon(null), IsEnabled = callout.Tails.Count > 0 };
                    removeTail.Click += (_, _) => CalloutTool.RemoveTail(s, callout.Id, callout.Tails.Count - 1);
                    menu.Items.Add(addTail);
                    menu.Items.Add(removeTail);
                    menu.Items.Add(new Separator());
                    break;
                case StampAnnotation stamp when _ctx.Stamps is not null && s.Document.Assets.TryGet(stamp.Asset, out var data, out var mediaType):
                    var addStamp = new MenuItem { Header = ES.T("Menu_AddToMyStamps", "Add to My Stamps"), Icon = MenuIcon("Icon.Tool.Stamp.Regular") };
                    addStamp.Click += (_, _) => RunUi(async () =>
                    {
                        var info = await _ctx.Stamps!.AddUserStampAsync(data, mediaType, stamp.StampId?.Split('/').Last() ?? "Stamp");
                        _toasts.Show(ES.F("Msg_StampAdded", "Added \"{0}\" to My Stamps", info.Name), MessageKind.Success);
                    });
                    menu.Items.Add(addStamp);
                    menu.Items.Add(new Separator());
                    break;
            }
        }
        if (sel.Count > 0) menu.Items.Add(Cmd(EditorCommands.AddToQuickStyles, Header(EditorCommands.AddToQuickStyles), null, Icons.Glyph.Star));
        menu.Items.Add(Cmd(ApplicationCommands.Cut, ES.T("Cmd_Cut", "Cut"), "Ctrl+X", Icons.Glyph.Cut));
        menu.Items.Add(Cmd(ApplicationCommands.Copy, ES.T("Cmd_Copy", "Copy"), "Ctrl+C", Icons.Glyph.Copy));
        menu.Items.Add(Cmd(ApplicationCommands.Paste, ES.T("Cmd_Paste", "Paste"), "Ctrl+V", Icons.Glyph.Paste));
        menu.Items.Add(Cmd(ApplicationCommands.Delete, ES.T("Cmd_Delete", "Delete"), "Del", Icons.Glyph.Delete));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd(EditorCommands.GrabText, Header(EditorCommands.GrabText), null, "Icon.Tool.GrabText.Regular"));
        menu.Items.Add(Cmd(ApplicationCommands.SelectAll, ES.T("Cmd_SelectAll", "Select All"), "Ctrl+A"));
        menu.Items.Add(Cmd(EditorCommands.SelectAllObjects, Header(EditorCommands.SelectAllObjects)));
        menu.Items.Add(Cmd(EditorCommands.Deselect, Header(EditorCommands.Deselect), "Ctrl+D"));
        if (sel.Count > 0)
        {
            menu.Items.Add(new Separator());
            var order = new MenuItem { Header = ES.T("Menu_Order", "Order"), Icon = MenuIcon(Icons.Glyph.Layers) };
            foreach (var c in new[] { EditorCommands.BringForward, EditorCommands.BringToFront, EditorCommands.SendBackward, EditorCommands.SendToBack }) order.Items.Add(Cmd(c, Header(c)));
            var align = new MenuItem { Header = ES.T("Menu_Align", "Align"), Icon = MenuIcon(null) };
            foreach (var c in new[] { EditorCommands.AlignLeft, EditorCommands.AlignCenter, EditorCommands.AlignRight, EditorCommands.AlignTop, EditorCommands.AlignMiddle, EditorCommands.AlignBottom }) align.Items.Add(Cmd(c, Header(c)));
            menu.Items.Add(order);
            menu.Items.Add(align);
            menu.Items.Add(Cmd(EditorCommands.FlipObjectsH, Header(EditorCommands.FlipObjectsH)));
            menu.Items.Add(Cmd(EditorCommands.FlipObjectsV, Header(EditorCommands.FlipObjectsV)));
            menu.Items.Add(Cmd(EditorCommands.Group, Header(EditorCommands.Group), "Ctrl+Shift+G"));
            menu.Items.Add(Cmd(EditorCommands.Ungroup, Header(EditorCommands.Ungroup), "Ctrl+Shift+U"));
            menu.Items.Add(Cmd(EditorCommands.Flatten, Header(EditorCommands.Flatten), "Ctrl+T"));
        }
        menu.Items.Add(Cmd(EditorCommands.FlattenAll, Header(EditorCommands.FlattenAll), "Ctrl+Shift+T"));
        menu.Items.Add(Cmd(EditorCommands.CanvasSnapping, Header(EditorCommands.CanvasSnapping), "Ctrl+;"));
        menu.Items.Add(Cmd(EditorCommands.CanvasColor, Header(EditorCommands.CanvasColor)));
        if (s.CanResetCrop)
        {
            var reset = new MenuItem { Header = ES.T("Crop_Reset", "Reset crop"), Icon = MenuIcon("Icon.Tool.Crop.Regular") };
            reset.Click += (_, _) => s.ResetCrop();
            menu.Items.Add(reset);
        }
        menu.PlacementTarget = _canvas;
        menu.IsOpen = true;
    }
}
