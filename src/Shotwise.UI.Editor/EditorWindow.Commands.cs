using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.Editor.Tools;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Infrastructure;
using ReleaseNotes = Shotwise.Core.Platform.ReleaseNotes;
using UpdateRestartResult = Shotwise.Core.Platform.UpdateRestartResult;
using UpdateState = Shotwise.Core.Platform.UpdateState;

namespace Shotwise.UI.Editor;

internal sealed partial class EditorWindow
{
    private bool Img => _view == View.Document && ImageDoc is not null;
    private bool AnyDoc => _view == View.Document && _active is not null;
    private bool HasSelection => Img && Session!.SelectedAnnotations().Count > 0;
    private bool Idle => Img && !Session!.IsBusy;
    private bool Vid => _view == View.Document && VideoDoc is not null;

    private void Bind(RoutedUICommand cmd, Action exec, Func<bool>? can = null)
        => CommandBindings.Add(new CommandBinding(cmd,
            (_, e) =>
            {
                try { exec(); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Command {Command} failed", cmd.Name);
                    _ = _ctx.ShowErrorAsync(ex);
                }
                e.Handled = true;
            },
            (_, e) =>
            {
                e.CanExecute = can?.Invoke() ?? true;
                e.Handled = true;
            }));

    private void BindAsync(RoutedUICommand cmd, Func<Task> exec, Func<bool>? can = null)
        => Bind(cmd, () => RunUi(exec), can);

    private void BindStd(ICommand cmd, Action exec, Func<bool>? can = null)
        => CommandBindings.Add(new CommandBinding(cmd,
            (_, e) =>
            {
                try { exec(); }
                catch (Exception ex) { _ = _ctx.ShowErrorAsync(ex); }
                e.Handled = true;
            },
            (_, e) =>
            {
                e.CanExecute = can?.Invoke() ?? false;
                e.Handled = true;
            }));

    private void BindCommands()
    {
        // ------------------------------------------------ standard edit commands
        BindStd(ApplicationCommands.Undo, DoUndo, () => Img && Session!.Undo.CanUndo || Vid && VideoDoc!.Undo.CanUndo);
        BindStd(ApplicationCommands.Redo, DoRedo, () => Img && Session!.Undo.CanRedo || Vid && VideoDoc!.Undo.CanRedo);
        BindStd(ApplicationCommands.Cut, DoCut, () => HasSelection || Img && Session!.Selection.HasPixelSelection);
        BindStd(ApplicationCommands.Copy, DoCopy, () => Img);
        BindStd(ApplicationCommands.Paste, DoPaste, () => _view == View.Document && (Img || AnyDoc || true));
        BindStd(ApplicationCommands.Delete, () => Session?.DeleteSelection(), () => Img && !Session!.Selection.IsEmpty);
        BindStd(ApplicationCommands.SelectAll, () => { if (Session!.ActiveTool is ToolKind.Selection or ToolKind.MagicWand) Session.SelectAllPixels(); else Session.SelectAll(); }, () => Img);

        Bind(EditorCommands.RevertToOriginal, () => { _canvas.CommitTextEditing(); if (Img) Session!.Undo.UndoAll(); else VideoDoc?.Undo.UndoAll(); }, () => Img && Session!.Undo.CanUndo || Vid && VideoDoc!.Undo.CanUndo);
        Bind(EditorCommands.UndoStack, ShowUndoStack, () => AnyDoc);
        Bind(EditorCommands.CopyAll, () => WriteClipboard(Session!.CopyAll()), () => Img);
        Bind(EditorCommands.Deselect, () => Session!.DeselectAll(), () => Img);
        Bind(EditorCommands.SelectAllObjects, () => Session!.SelectAllObjects(), () => Img);
        Bind(EditorCommands.InvertSelection, () => Session!.InvertSelection(), () => Img);
        Bind(EditorCommands.Group, () => Session!.Group(), () => Img && Session!.Selection.Count > 1);
        Bind(EditorCommands.Ungroup, () => Session!.Ungroup(), () => HasSelection);
        Bind(EditorCommands.BringForward, () => Session!.BringForward(), () => HasSelection);
        Bind(EditorCommands.SendBackward, () => Session!.SendBackward(), () => HasSelection);
        Bind(EditorCommands.BringToFront, () => Session!.BringToFront(), () => HasSelection);
        Bind(EditorCommands.SendToBack, () => Session!.SendToBack(), () => HasSelection);
        Bind(EditorCommands.AlignLeft, () => Session!.Align(AlignKind.Left), () => HasSelection);
        Bind(EditorCommands.AlignCenter, () => Session!.Align(AlignKind.Center), () => HasSelection);
        Bind(EditorCommands.AlignRight, () => Session!.Align(AlignKind.Right), () => HasSelection);
        Bind(EditorCommands.AlignTop, () => Session!.Align(AlignKind.Top), () => HasSelection);
        Bind(EditorCommands.AlignMiddle, () => Session!.Align(AlignKind.Middle), () => HasSelection);
        Bind(EditorCommands.AlignBottom, () => Session!.Align(AlignKind.Bottom), () => HasSelection);
        Bind(EditorCommands.DistributeH, () => Session!.Distribute(true), () => Img && Session!.Selection.Count > 2);
        Bind(EditorCommands.DistributeV, () => Session!.Distribute(false), () => Img && Session!.Selection.Count > 2);
        Bind(EditorCommands.Lock, () => Session!.Lock(true), () => HasSelection);
        Bind(EditorCommands.Unlock, () => Session!.Lock(false), () => HasSelection);
        Bind(EditorCommands.FlipObjectsH, () => Session!.FlipSelection(true), () => HasSelection);
        Bind(EditorCommands.FlipObjectsV, () => Session!.FlipSelection(false), () => HasSelection);
        Bind(EditorCommands.Flatten, () => { _canvas.CommitTextEditing(); Session!.Flatten(false); }, () => Img);
        Bind(EditorCommands.FlattenAll, () => { _canvas.CommitTextEditing(); Session!.FlattenAll(); }, () => Img);
        BindAsync(EditorCommands.GrabText, () => Session!.GrabTextAsync(), () => Idle);
        BindAsync(EditorCommands.EditText, () => Session!.EditTextAsync(), () => Idle);
        BindAsync(EditorCommands.AddToQuickStyles, () => Panels.QuickStyleActions.AddFromSelectionAsync(_ctx, Session!), () => HasSelection);
        Bind(EditorCommands.DuplicateObjects, () => { _canvas.CommitTextEditing(); Session!.Duplicate(); }, () => HasSelection);

        // ------------------------------------------------ file
        BindAsync(EditorCommands.NewCapture, NewCaptureAsync);
        BindAsync(EditorCommands.NewImage, NewImageAsync);
        BindAsync(EditorCommands.NewFromClipboard, NewFromClipboardAsync);
        BindAsync(EditorCommands.Open, OpenDialogAsync);
        BindAsync(EditorCommands.CreateFromTemplate, CreateFromTemplateAsync);
        BindAsync(EditorCommands.ImportToLibrary, ImportToLibraryAsync, () => _ctx.Library is not null);
        BindAsync(EditorCommands.ImportStamps, ImportStampsAsync, () => _ctx.Stamps is not null);
        BindAsync(EditorCommands.ImportTemplates, ImportTemplatesAsync, () => _ctx.Templates is not null);
        BindAsync(EditorCommands.Save, () => SaveAsync(_active!, false), () => AnyDoc && _active!.IsDirty || AnyDoc && _active!.FilePath is null);
        BindAsync(EditorCommands.SaveAs, () => SaveAsync(_active!, true), () => AnyDoc);
        BindAsync(EditorCommands.BatchExport, BatchExportAsync);
        BindAsync(EditorCommands.Print, () => PrintAsync(false), () => Img);
        BindAsync(EditorCommands.QuickPrint, () => PrintAsync(true), () => Img);
        BindAsync(EditorCommands.PrintPreview, PrintPreviewAsync, () => Img);
        BindAsync(EditorCommands.PageSetup, PageSetupAsync);
        BindAsync(EditorCommands.Duplicate, () => DuplicateItemAsync(TraySelectionOrActiveItems()[0]), () => AnyDoc || _tray.Selected.Count > 0);
        BindAsync(EditorCommands.Rename, () => RenameAsync(TraySelectionOrActiveItems().FirstOrDefault()), () => AnyDoc || _tray.Selected.Count > 0);
        BindAsync(EditorCommands.Pin, () => PinAsync(TraySelectionOrActiveItems()), () => AnyDoc || _tray.Selected.Count > 0);
        BindAsync(EditorCommands.CloseDocument, () => CloseDocumentAsync(_active!), () => AnyDoc);
        BindAsync(EditorCommands.MoveToRecycleBin, () => DeleteItemsAsync(TraySelectionOrActiveItems()), () => AnyDoc || _tray.Selected.Count > 0);
        BindAsync(EditorCommands.EditorPreferences, () => _ctx.Dialogs.ShowPreferences(new PreferencesRequest(PreferencesPage.Editor) { Owner = this }));
        BindAsync(EditorCommands.CapturePreferences, () => _ctx.Dialogs.ShowPreferences(new PreferencesRequest(PreferencesPage.CaptureGeneral) { Owner = this }));
        BindAsync(EditorCommands.ConnectMobile, () => _ctx.Dialogs.ShowMobileConnect(new MobileConnectRequest { Owner = this }));
        Bind(EditorCommands.ExitEditor, Close);
        BindAsync(EditorCommands.VideoFromImages, VideoFromImagesAsync, () => Img && _ctx.VideoFromImages is not null && !_vfi.IsRecording);
        Bind(EditorCommands.OpenLibrary, () => SetView(View.Library), () => _ctx.Library is not null);
        Bind(EditorCommands.OpenCaptureWindow, () => _ctx.Capture?.ShowCaptureWindow(), () => _ctx.Capture is not null);
        BindAsync(EditorCommands.ExitApp, ExitApplicationAsync);
        BindAsync(EditorCommands.ManageModels, () => _ctx.Dialogs.ShowPreferences(new PreferencesRequest(PreferencesPage.Models) { Owner = this }));
        Bind(EditorCommands.ToggleDetails, () => _details.Visibility = _details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        BindAsync(EditorCommands.VideoProperties, EditVideoPropertiesAsync, () => Vid && !VideoDoc!.IsGif && _ctx.Video is not null);
        Bind(EditorCommands.LearningCenter, () => { _learn.ShowTab(0); SetView(View.Learning); });
        Bind(EditorCommands.OpenLogFolder, () => OpenFolder(_ctx.Paths.Logs));

        // ------------------------------------------------ image
        BindAsync(EditorCommands.Trim, () => ApplyEffectAsync(Shotwise.Core.Effects.EffectDefaults.Trim), () => Idle);
        Bind(EditorCommands.SmartRedact, () => { ShowPane(SideMode.Redact); }, () => Img);
        Bind(EditorCommands.AutoSimplify, () => { ShowPane(SideMode.Simplify); }, () => Img);
        BindAsync(EditorCommands.CanvasColor, CanvasColorAsync, () => Img);
        BindAsync(EditorCommands.CombineImages, () => CombineImagesAsync(null), () => _docs.Count >= 2 || _tray.Selected.Count >= 2);
        BindAsync(EditorCommands.CombineInTemplate, () => CombineInTemplateAsync(null), () => _docs.Count >= 1 || _tray.Selected.Count >= 1);
        BindAsync(EditorCommands.ApplyTemplate, ApplyTemplateAsync, () => Img);
        BindAsync(EditorCommands.RotateCw, () => ApplyEffectAsync(new Shotwise.Core.Effects.RotateEffect(90, true, false, false)), () => Idle);
        BindAsync(EditorCommands.RotateCcw, () => ApplyEffectAsync(new Shotwise.Core.Effects.RotateEffect(270, true, false, false)), () => Idle);
        BindAsync(EditorCommands.Rotate180, () => ApplyEffectAsync(new Shotwise.Core.Effects.RotateEffect(180, true, false, false)), () => Idle);
        BindAsync(EditorCommands.CustomAngle, CustomAngleAsync, () => Idle);
        BindAsync(EditorCommands.FlipImageH, () => ApplyEffectAsync(new Shotwise.Core.Effects.RotateEffect(0, false, true, false)), () => Idle);
        BindAsync(EditorCommands.FlipImageV, () => ApplyEffectAsync(new Shotwise.Core.Effects.RotateEffect(0, false, false, true)), () => Idle);
        BindAsync(EditorCommands.ResizeCanvas, ResizeCanvasAsync, () => Img);
        BindAsync(EditorCommands.ResizeImage, ResizeImageAsync, () => Idle);
        Bind(EditorCommands.CanvasSnapping, () =>
        {
            var on = !Session!.SnappingEnabled;
            Session.SnappingEnabled = on;
            _ctx.Settings.Update(s => s.Editor.CanvasSnapping = on);
            _toasts.Show(on ? ES.T("Msg_SnapOn", "Canvas snapping on") : ES.T("Msg_SnapOff", "Canvas snapping off"), MessageKind.Info, TimeSpan.FromSeconds(2));
        }, () => Img);
        BindAsync(EditorCommands.TagImage, () => TagAsync(TraySelectionOrActiveItems()), () => AnyDoc);

        // ------------------------------------------------ view
        Bind(EditorCommands.ZoomIn, _canvas.ZoomIn, () => Img);
        Bind(EditorCommands.ZoomOut, _canvas.ZoomOut, () => Img);
        Bind(EditorCommands.ActualSize, _canvas.ActualSize, () => Img);
        Bind(EditorCommands.ZoomToFit, () => _canvas.ZoomToFit(false), () => Img);
        Bind(EditorCommands.ShrinkToFit, () => _canvas.ZoomToFit(true), () => Img);
        Bind(EditorCommands.PixelGrid, () =>
        {
            _canvas.ShowPixelGrid = !_canvas.ShowPixelGrid;
            var on = _canvas.ShowPixelGrid;
            _ctx.Settings.Update(s => s.Editor.ShowPixelGrid = on);
        }, () => Img);
        Bind(EditorCommands.ToggleProperties, () => { if (_layout.ShowSidePanel && _sideMode == SideMode.Properties) SetSideVisible(false); else ShowPane(SideMode.Properties); });
        Bind(EditorCommands.ToggleEffects, () => { if (_layout.ShowSidePanel && _sideMode == SideMode.Effects) SetSideVisible(false); else ShowPane(SideMode.Effects); });
        Bind(EditorCommands.ToggleTray, () => { _layout.ShowTray = !_layout.ShowTray; _tray.Visibility = _layout.ShowTray ? Visibility.Visible : Visibility.Collapsed; SyncPanelToggles(); });
        Bind(EditorCommands.SwitchToLibrary, () => SetView(View.Library));
        Bind(EditorCommands.SwitchToEditor, () => SetView(View.Document));

        // ------------------------------------------------ library
        Bind(EditorCommands.LibrarySearch, () => { SetView(View.Library); Dispatcher.BeginInvoke(_library.FocusSearch, System.Windows.Threading.DispatcherPriority.Input); }, () => _ctx.Library is not null);
        Bind(EditorCommands.LibraryOpenSelected, () => { if (_library.SelectedItems.FirstOrDefault() is { } li) RunUi(() => OpenLibraryItemAsync(li)); }, () => _view == View.Library && _library.SelectedItems.Count > 0);
        Bind(EditorCommands.LibraryReveal, () => RevealInExplorer(_view == View.Library ? _library.SelectedItems.FirstOrDefault()?.Path : _active?.FilePath), () => _view == View.Library && _library.SelectedItems.Count > 0 || AnyDoc && _active!.FilePath is not null);
        Bind(EditorCommands.LibraryRefresh, () => RunUi(_library.ActivateAsync), () => _view == View.Library);
        BindAsync(EditorCommands.LibraryBackup, LibraryBackupAsync, () => _ctx.Library is not null);
        BindAsync(EditorCommands.LibraryRestore, LibraryRestoreAsync, () => _ctx.Library is not null);
        BindAsync(EditorCommands.LibraryLocation, LibraryLocationAsync, () => _ctx.Library is not null);

        // ------------------------------------------------ share
        Bind(EditorCommands.ShareLink, () =>
        {
            if (_active is null) return;
            var cm = _share.BuildMenu(_active, true, () => RunUi(() => _share.ShowHistoryAsync(_active)), () => RunUi(() => _ctx.Dialogs.ShowShareAccounts(new ShareAccountsRequest { Owner = this })));
            cm.PlacementTarget = _shareButton;
            cm.IsOpen = true;
        }, () => AnyDoc);
        BindAsync(EditorCommands.ShareHistory, () => _share.ShowHistoryAsync(_active), () => AnyDoc && _ctx.Library is not null);
        BindAsync(EditorCommands.ShareAccounts, () => _ctx.Dialogs.ShowShareAccounts(new ShareAccountsRequest { Owner = this }));

        // ------------------------------------------------ video
        Bind(EditorCommands.VideoPlayPause, _video.TogglePlay, () => Vid);
        Bind(EditorCommands.VideoPrevFrame, () => _video.Step(-1), () => Vid);
        Bind(EditorCommands.VideoNextFrame, () => _video.Step(1), () => Vid);
        BindAsync(EditorCommands.VideoCutOut, _video.CutOutAsync, () => Vid);
        Bind(EditorCommands.VideoMute, _video.ToggleMute, () => Vid);
        Bind(EditorCommands.VideoVolumeUp, () => _video.AdjustVolume(0.1), () => Vid);
        Bind(EditorCommands.VideoVolumeDown, () => _video.AdjustVolume(-0.1), () => Vid);
        BindAsync(EditorCommands.VideoSaveFrame, _video.SaveFrameAsync, () => Vid);
        BindAsync(EditorCommands.VideoSaveSelection, _video.SaveSelectionAsync, () => Vid);
        BindAsync(EditorCommands.VideoSaveGif, _video.CreateGifAsync, () => Vid && _ctx.Gif is not null);
        BindAsync(EditorCommands.CombineVideos, () => CombineVideosAsync(null), () => _tray.Selected.Count >= 2 || _docs.OfType<Documents.VideoEditorDocument>().Count() >= 2);

        // ------------------------------------------------ help
        Bind(EditorCommands.GettingStarted, () => { _learn.ShowTab(0); SetView(View.Learning); });
        Bind(EditorCommands.Tutorials, () => { _learn.ShowTab(1); SetView(View.Learning); });
        Bind(EditorCommands.WhatsNew, () => { _learn.ShowTab(3); SetView(View.Learning); });
        BindAsync(EditorCommands.CheckForUpdates, CheckForUpdatesAsync);
        BindAsync(EditorCommands.About, () => _ctx.Dialogs.ShowAbout(new AboutRequest { Owner = this }));
        BindAsync(EditorCommands.KeyboardShortcuts, ShowKeyboardShortcutsAsync);
    }

    /// <summary>Help → Keyboard shortcuts: global hotkeys (effective keys) + every editor shortcut.</summary>
    private async Task ShowKeyboardShortcutsAsync()
    {
        var status = _ctx.Services.GetService(typeof(Shotwise.Core.Platform.IHotkeyStatusSource)) as Shotwise.Core.Platform.IHotkeyStatusSource;
        var sheet = new Panels.ShortcutSheetWindow(status, _ctx.Settings,
            () => _ = _ctx.Dialogs.ShowPreferences(new PreferencesRequest(PreferencesPage.Hotkeys) { Owner = this }));
        await sheet.ShowDialogAsync(this);
    }

    // ------------------------------------------------------------------ edit helpers

    private void DoUndo()
    {
        _canvas.CommitTextEditing();
        if (Img) Session!.Undo.Undo(); else if (Vid) VideoDoc!.Undo.Undo();
    }

    private void DoRedo()
    {
        _canvas.CommitTextEditing();
        if (Img) Session!.Undo.Redo(); else if (Vid) VideoDoc!.Undo.Redo();
    }

    private void DoCut()
    {
        _canvas.CommitTextEditing();
        var payload = Session!.Cut();
        if (payload is not null) WriteClipboard(payload);
    }

    private void DoCopy()
    {
        _canvas.CommitTextEditing();
        var payload = Session!.Copy() ?? Session.CopyAll();
        WriteClipboard(payload);
    }

    private void WriteClipboard(Shotwise.Editor.ClipboardPayload payload)
    {
        EditorClipboard.Write(_ctx, payload);
        _toasts.Show(ES.T("Msg_Copied", "Copied to the clipboard"), MessageKind.Info, TimeSpan.FromSeconds(2));
    }

    private void DoPaste()
    {
        if (!Img)
        {
            RunUi(NewFromClipboardAsync);
            return;
        }
        var payload = EditorClipboard.Read(_ctx);
        if (payload is null || payload.IsEmpty)
        {
            _toasts.Show(Shotwise.UI.Common.Resources.Localizer.ErrorMessage(ErrorCode.ClipboardEmpty), MessageKind.Warning);
            return;
        }
        _canvas.CommitTextEditing();
        Session!.Paste(payload, _canvas.PointerCanvasPosition());
    }

    private void ShowUndoStack()
    {
        _undoWindow ??= new Panels.UndoStackWindow { Owner = this };
        _undoWindow.Bind(Session?.Undo ?? VideoDoc?.Undo, _active?.Title ?? "");
        _undoWindow.Show();
        _undoWindow.Activate();
    }

    private static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_ctx.Updates is null)
        {
            await _ctx.InfoAsync(ES.T("Cmd_CheckForUpdates", "Check for Updates"), ES.T("Msg_UpdatesOff", "Update checking is not available."));
            return;
        }
        var updates = _ctx.Updates;
        var title = ES.T("Cmd_CheckForUpdates", "Check for Updates");
        var status = updates.Status;
        switch (status.State)
        {
            case UpdateState.NotSupported:
                await _ctx.InfoAsync(title, ES.F("Msg_UpdatesUnsupported", "Shotwise {0}. {1}", status.CurrentVersion, status.Message));
                return;
            case UpdateState.Disabled:
                await _ctx.InfoAsync(title, ES.T("Msg_UpdatesPolicy", "Updates are turned off by your organization."));
                return;
            case UpdateState.ReadyToRestart:
                await OfferRestartAsync(status.Update?.Version ?? "");
                return;
        }

        UpdateInfoHolder? holder = null;
        var finished = await _overlay.RunAsync(ES.T("Msg_CheckingUpdates", "Checking for updates…"), null, true, async (_, ct) =>
        {
            // Explicit user request = approved check (bypasses the schedule and the ask-first preference; policy still applies).
            holder = new UpdateInfoHolder(await updates.CheckAsync(userApproved: true, ct));
        });
        if (!finished || holder is null) return;
        if (holder.Info is not { } info)
        {
            var after = updates.Status;
            var text = after.State switch
            {
                UpdateState.ReadyToRestart => null,
                UpdateState.Failed when after.Offline => ES.T("Msg_UpdatesOffline", "You are offline. Connect to the internet and try again."),
                UpdateState.Failed => ES.F("Msg_UpdateCheckFailed", "The update check did not complete: {0}", after.Message),
                _ => ES.F("Msg_UpToDateVersion", "Shotwise {0} is up to date.", after.CurrentVersion),
            };
            if (text is null) { await OfferRestartAsync(after.Update?.Version ?? ""); return; }
            await _ctx.InfoAsync(title, text, after.State == UpdateState.Failed ? MessageKind.Warning : MessageKind.Info);
            return;
        }
        if (updates.Status.State == UpdateState.ReadyToRestart) { await OfferRestartAsync(info.Version); return; }
        var notes = ReleaseNotes.Snippet(info.ReleaseNotes, 400);
        var body = ES.F("Msg_UpdateAvailableBody2", "Shotwise {0} is available (you have {1}). Download it now?", info.Version, updates.Status.CurrentVersion)
            + (notes.Length > 0 ? "\n\n" + ES.F("Msg_UpdateNotes", "What's new: {0}", notes) : "")
            + (info.SizeBytes > 0 ? "\n\n" + ES.F("Msg_UpdateSize", "Download size: {0:N1} MB", info.SizeBytes / 1048576.0) : "");
        var r = await _ctx.Dialogs.Confirm(new ConfirmRequest(ES.T("Msg_UpdateAvailable", "Update available"), body, ES.T("Msg_Download", "Download")) { Owner = this });
        if (!r.Confirmed) return;
        var ok = false;
        var done = await _overlay.RunAsync(ES.T("Msg_Downloading", "Downloading update…"), info.Version, true, async (p, ct) => ok = await updates.DownloadAsync(p, ct));
        if (!done) return;
        if (!ok)
        {
            await _ctx.InfoAsync(title, ES.F("Msg_UpdateDownloadFailed", "The download did not complete: {0}", updates.Status.Message), MessageKind.Warning);
            return;
        }
        await OfferRestartAsync(info.Version);
    }

    /// <summary>"Shotwise X is ready. Restart now?" — Later keeps the InfoBar; the update then installs when Shotwise exits.</summary>
    private async Task OfferRestartAsync(string version)
    {
        var r = await _ctx.Dialogs.Confirm(new ConfirmRequest(ES.F("Update_ReadyTitle", "Shotwise {0} is ready", version),
            ES.T("Update_ReadyBody", "Restart Shotwise to finish updating. Your work is saved first. If you choose Later, the update installs the next time you exit Shotwise."),
            ES.T("Update_RestartNow", "Restart now"), ES.T("Update_Later", "Later")) { Owner = this });
        if (!r.Confirmed || _ctx.Updates is null) return;
        if (await _ctx.Updates.RestartToApplyAsync() == UpdateRestartResult.BlockedByRecording)
            _toasts.Show(ES.T("Update_BlockedBody", "Shotwise restarts to update after the capture or recording has finished."), MessageKind.Warning);
    }

    private sealed record UpdateInfoHolder(Shotwise.Core.Platform.UpdateInfo? Info);
}
