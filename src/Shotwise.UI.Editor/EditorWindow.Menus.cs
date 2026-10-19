using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Settings;
using Shotwise.Core.Styles;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Infrastructure;
using Menu = System.Windows.Controls.Menu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Shotwise.UI.Editor;

internal sealed partial class EditorWindow
{
    private MenuItem _openRecent = null!;
    private MenuItem _videoMenu = null!;
    private MenuItem _shareMenu = null!;

    private static string Header(RoutedUICommand cmd) => ES.T("Cmd_" + cmd.Name, cmd.Text);

    /// <summary>Menu icon; items without one get an empty slot of the same size so every label lines up.</summary>
    private static FrameworkElement MenuIcon(string? glyph) => glyph is null ? new Border { Width = 16, Height = 16 } : UiKit.Glyph(glyph, 16);

    private MenuItem Mi(RoutedUICommand cmd, string? glyph = null, string? text = null)
        => new() { Command = cmd, Header = text ?? Header(cmd), Icon = MenuIcon(glyph) };

    private MenuItem Mi(ICommand cmd, string header, string? glyph = null)
        => new() { Command = cmd, Header = header, Icon = MenuIcon(glyph) };

    private static MenuItem Sub(string header, params object[] items) => Sub(header, null, items);

    private static MenuItem Sub(string header, string? glyph, params object[] items)
    {
        var m = new MenuItem { Header = header, Icon = MenuIcon(glyph) };
        foreach (var i in items) m.Items.Add(i);
        return m;
    }

    /// <summary>
    /// The classic menus (File, Edit, Image, View, Library, Share, Video, Help) live behind the top-bar menu button (and F10);
    /// every command keeps its keyboard shortcut through the window's command bindings.
    /// </summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { MinWidth = 220 };

        // ---- File
        _openRecent = Sub(ES.T("Menu_OpenRecent", "Open Recent"), Icons.Glyph.History);
        _openRecent.Items.Add(new MenuItem { Header = ES.T("Menu_Loading", "Loading…"), IsEnabled = false, Icon = MenuIcon(null) });
        _openRecent.SubmenuOpened += (_, _) => RunUi(PopulateRecentAsync);
        var newCapture = Mi(EditorCommands.NewCapture, Icons.Glyph.Camera);
        var file = Sub(ES.T("Menu_File", "File"), Icons.Glyph.Folder,
            newCapture, Mi(EditorCommands.OpenCaptureWindow),
            new Separator(),
            Mi(EditorCommands.NewImage, Icons.Glyph.Add), Mi(EditorCommands.NewFromClipboard, Icons.Glyph.Paste), Mi(EditorCommands.CreateFromTemplate, Icons.Glyph.Grid), Mi(EditorCommands.VideoFromImages, Icons.Glyph.Video),
            new Separator(),
            Mi(EditorCommands.Open, Icons.Glyph.OpenFile), _openRecent, Mi(EditorCommands.OpenLibrary, Icons.Glyph.Library),
            new Separator(),
            Mi(EditorCommands.Save, Icons.Glyph.Save), Mi(EditorCommands.SaveAs), Mi(EditorCommands.BatchExport, Icons.Glyph.Export),
            Sub(ES.T("Menu_Import", "Import"), Icons.Glyph.Download, Mi(EditorCommands.ImportToLibrary), Mi(EditorCommands.ImportStamps), Mi(EditorCommands.ImportTemplates)),
            new Separator(),
            Mi(EditorCommands.Print, Icons.Glyph.Print), Mi(EditorCommands.PrintPreview), Mi(EditorCommands.PageSetup), Mi(EditorCommands.QuickPrint),
            new Separator(),
            Mi(EditorCommands.Duplicate, Icons.Glyph.Copy), Mi(EditorCommands.Rename, Icons.Glyph.Edit), Mi(EditorCommands.Pin, Icons.Glyph.Pin), Mi(EditorCommands.MoveToRecycleBin, Icons.Glyph.Delete), Mi(EditorCommands.CloseDocument, Icons.Glyph.Close),
            new Separator(),
            Mi(EditorCommands.ConnectMobile), Mi(EditorCommands.EditorPreferences, Icons.Glyph.Settings), Mi(EditorCommands.CapturePreferences), Mi(EditorCommands.ManageModels),
            new Separator(),
            Mi(EditorCommands.ExitEditor), Mi(EditorCommands.ExitApp));
        // "New Capture" shows the global hotkey that really triggers a capture (e.g. Shift+PrintScreen when PrintScreen is taken).
        void UpdateCaptureGesture()
        {
            var status = _ctx.Services.GetService(typeof(Shotwise.Core.Platform.IHotkeyStatusSource)) as Shotwise.Core.Platform.IHotkeyStatusSource;
            var key = status?.Find(Shotwise.Core.Platform.HotkeyIds.GlobalCapture)?.Effective ?? _ctx.Settings.Current.Hotkeys.EffectiveGlobalCapture ?? _ctx.Settings.Current.Hotkeys.GlobalCapture;
            newCapture.InputGestureText = key is { IsEmpty: false } k ? k.ToDisplayString() : "";
        }
        UpdateCaptureGesture();
        file.SubmenuOpened += (_, _) => UpdateCaptureGesture();
        menu.Items.Add(file);

        // ---- Edit
        var order = Sub(ES.T("Menu_Order", "Order"), Mi(EditorCommands.BringForward), Mi(EditorCommands.BringToFront), Mi(EditorCommands.SendBackward), Mi(EditorCommands.SendToBack));
        var align = Sub(ES.T("Menu_Align", "Align"), Mi(EditorCommands.AlignLeft), Mi(EditorCommands.AlignCenter), Mi(EditorCommands.AlignRight), new Separator(),
            Mi(EditorCommands.AlignTop), Mi(EditorCommands.AlignMiddle), Mi(EditorCommands.AlignBottom), new Separator(), Mi(EditorCommands.DistributeH), Mi(EditorCommands.DistributeV));
        var edit = Sub(ES.T("Menu_Edit", "Edit"), Icons.Glyph.Edit,
            Mi(ApplicationCommands.Undo, ES.T("Cmd_Undo", "Undo"), Icons.Glyph.Undo), Mi(ApplicationCommands.Redo, ES.T("Cmd_Redo", "Redo"), Icons.Glyph.Redo),
            Mi(EditorCommands.RevertToOriginal, Icons.Glyph.Refresh), Mi(EditorCommands.UndoStack, Icons.Glyph.History),
            new Separator(),
            Mi(ApplicationCommands.Cut, ES.T("Cmd_Cut", "Cut"), Icons.Glyph.Cut), Mi(ApplicationCommands.Copy, ES.T("Cmd_Copy", "Copy"), Icons.Glyph.Copy), Mi(EditorCommands.CopyAll),
            Mi(ApplicationCommands.Paste, ES.T("Cmd_Paste", "Paste"), Icons.Glyph.Paste), Mi(EditorCommands.DuplicateObjects), Mi(ApplicationCommands.Delete, ES.T("Cmd_Delete", "Delete"), Icons.Glyph.Delete),
            new Separator(),
            Mi(ApplicationCommands.SelectAll, ES.T("Cmd_SelectAll", "Select All")), Mi(EditorCommands.SelectAllObjects), Mi(EditorCommands.Deselect), Mi(EditorCommands.InvertSelection),
            new Separator(),
            order, align, Mi(EditorCommands.Group), Mi(EditorCommands.Ungroup), Mi(EditorCommands.Lock), Mi(EditorCommands.Unlock),
            new Separator(),
            Mi(EditorCommands.Flatten), Mi(EditorCommands.FlattenAll),
            new Separator(),
            Mi(EditorCommands.GrabText, "Icon.Tool.GrabText.Regular"), Mi(EditorCommands.EditText, "Icon.Tool.Text.Regular"), Mi(EditorCommands.AddToQuickStyles, Icons.Glyph.Star));
        menu.Items.Add(edit);

        // ---- Image
        var tools = new MenuItem { Header = ES.T("Menu_Tools", "Tools"), Icon = MenuIcon(Icons.Glyph.Brush) };
        foreach (var kind in ToolInfo.DefaultOrder)
        {
            var k = kind;
            var item = new MenuItem { Header = ToolInfo.Name(k), Icon = Icons.ForTool(k, 16) };
            item.Click += (_, _) => { if (Session is { } s) { _canvas.CommitTextEditing(); s.ActiveTool = k; } };
            tools.Items.Add(item);
        }
        var rotate = Sub(ES.T("Menu_Rotate", "Rotate"), Icons.Glyph.Rotate, Mi(EditorCommands.RotateCw), Mi(EditorCommands.RotateCcw), Mi(EditorCommands.Rotate180), Mi(EditorCommands.CustomAngle));
        var arrange = Sub(ES.T("Menu_Arrange", "Arrange"), Mi(EditorCommands.FlipObjectsH), Mi(EditorCommands.FlipObjectsV));
        var image = Sub(ES.T("Menu_Image", "Image"), Icons.Glyph.Image,
            tools,
            new Separator(),
            Mi(EditorCommands.ResizeImage, Icons.Glyph.Resize), Mi(EditorCommands.ResizeCanvas), Mi(EditorCommands.CanvasColor, Icons.Glyph.Color), Mi(EditorCommands.Trim, "Icon.Tool.Crop.Regular"),
            rotate, Mi(EditorCommands.FlipImageH), Mi(EditorCommands.FlipImageV), arrange,
            new Separator(),
            Mi(EditorCommands.CanvasSnapping),
            new Separator(),
            Mi(EditorCommands.CombineImages, Icons.Glyph.Collections), Mi(EditorCommands.CombineInTemplate), Mi(EditorCommands.ApplyTemplate, Icons.Glyph.Grid),
            new Separator(),
            Mi(EditorCommands.SmartRedact, "Icon.Tool.Blur.Regular"), Mi(EditorCommands.AutoSimplify, "Icon.Tool.Simplify.Regular"), Mi(EditorCommands.TagImage, Icons.Glyph.Tag));
        menu.Items.Add(image);

        // ---- View
        var background = new MenuItem { Header = ES.T("Menu_Background", "Background Color"), Icon = MenuIcon(Icons.Glyph.Color) };
        foreach (var (wc, name) in new[] { (WorkspaceColor.LightGray, ES.T("Bg_LightGray", "Light Gray (Default)")), (WorkspaceColor.White, ES.T("Bg_White", "White")), (WorkspaceColor.Gray, ES.T("Bg_Gray", "Gray")), (WorkspaceColor.DarkGray, ES.T("Bg_DarkGray", "Dark Gray")), (WorkspaceColor.Black, ES.T("Bg_Black", "Black")), (WorkspaceColor.Custom, ES.T("Bg_Custom", "Custom…")) })
        {
            var color = wc;
            var item = new MenuItem { Header = name, IsCheckable = false, Tag = wc, Icon = MenuIcon(null) };
            item.Click += (_, _) => RunUi(() => SetWorkspaceColorAsync(color));
            background.Items.Add(item);
        }
        background.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem it in background.Items) it.IsChecked = it.Tag is WorkspaceColor c && c == _ctx.Prefs.WorkspaceColor;
        };
        var view = Sub(ES.T("Menu_View", "View"), Icons.Glyph.FullScreen,
            Mi(EditorCommands.ZoomIn, Icons.Glyph.ZoomIn), Mi(EditorCommands.ZoomOut, Icons.Glyph.ZoomOut), Mi(EditorCommands.ActualSize), Mi(EditorCommands.ZoomToFit, Icons.Glyph.Fit), Mi(EditorCommands.ShrinkToFit),
            new Separator(),
            Mi(EditorCommands.PixelGrid, Icons.Glyph.Grid), background,
            new Separator(),
            Mi(EditorCommands.ToggleProperties, Icons.Glyph.Properties), Mi(EditorCommands.ToggleEffects, Icons.Glyph.Effects), Mi(EditorCommands.ToggleTray, Icons.Glyph.History, ES.T("Cmd_RecentCaptures", "Recent Captures")), Mi(EditorCommands.ToggleDetails, Icons.Glyph.Info),
            new Separator(),
            Mi(EditorCommands.SwitchToLibrary, Icons.Glyph.Library), Mi(EditorCommands.SwitchToEditor));
        menu.Items.Add(view);

        // ---- Library
        menu.Items.Add(Sub(ES.T("Menu_Library", "Library"), Icons.Glyph.Library,
            Mi(EditorCommands.SwitchToLibrary, Icons.Glyph.Library), Mi(EditorCommands.LibrarySearch, Icons.Glyph.Search), Mi(EditorCommands.LibraryOpenSelected, Icons.Glyph.OpenFile), Mi(EditorCommands.LibraryReveal, Icons.Glyph.Folder), Mi(EditorCommands.LibraryRefresh, Icons.Glyph.Refresh),
            new Separator(),
            Mi(EditorCommands.ImportToLibrary, Icons.Glyph.Download), Mi(EditorCommands.LibraryBackup, Icons.Glyph.Cloud), Mi(EditorCommands.LibraryRestore), Mi(EditorCommands.LibraryLocation, Icons.Glyph.Folder)));

        // ---- Share (dynamic)
        _shareMenu = Sub(ES.T("Menu_Share", "Share"), Icons.Glyph.Share);
        _shareMenu.SubmenuOpened += (_, _) => PopulateShareMenu(_shareMenu, false);
        _shareMenu.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
        menu.Items.Add(_shareMenu);

        // ---- Video (contextual)
        _videoMenu = Sub(ES.T("Menu_Video", "Video"), Icons.Glyph.Video,
            Mi(EditorCommands.VideoPlayPause, Icons.Glyph.Play), Mi(EditorCommands.VideoPrevFrame, Icons.Glyph.Previous), Mi(EditorCommands.VideoNextFrame, Icons.Glyph.Next),
            new Separator(),
            Mi(EditorCommands.VideoCutOut, Icons.Glyph.Cut), Mi(EditorCommands.VideoMute, Icons.Glyph.Mute), Mi(EditorCommands.VideoVolumeUp, Icons.Glyph.Volume), Mi(EditorCommands.VideoVolumeDown),
            new Separator(),
            Mi(EditorCommands.VideoSaveFrame, Icons.Glyph.Image), Mi(EditorCommands.VideoSaveSelection, Icons.Glyph.VideoClip), Mi(EditorCommands.VideoSaveGif, Icons.Glyph.Gif),
            new Separator(),
            Mi(EditorCommands.CombineVideos, Icons.Glyph.Collections), Mi(EditorCommands.VideoProperties, Icons.Glyph.Info));
        _videoMenu.Visibility = Visibility.Collapsed;
        menu.Items.Add(_videoMenu);

        // ---- Help
        menu.Items.Add(Sub(ES.T("Menu_Help", "Help"), Icons.Glyph.Help,
            Mi(EditorCommands.GettingStarted, Icons.Glyph.Help), Mi(EditorCommands.LearningCenter, Icons.Glyph.Lightbulb), Mi(EditorCommands.Tutorials, Icons.Glyph.VideoClip), Mi(EditorCommands.WhatsNew, Icons.Glyph.Effects),
            Mi(EditorCommands.KeyboardShortcuts, "Icon.Keyboard.Regular"),
            new Separator(),
            Mi(EditorCommands.OpenLogFolder, Icons.Glyph.Folder), Mi(EditorCommands.CheckForUpdates, Icons.Glyph.Download),
            new Separator(),
            Mi(EditorCommands.About, Icons.Glyph.Info)));
        return menu;
    }

    private void RebuildDynamicMenus()
    {
        _videoMenu.Visibility = _view == View.Document && VideoDoc is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PopulateRecentAsync()
    {
        _openRecent.Items.Clear();
        var items = new List<(string Name, string Path)>();
        if (_ctx.Library is { } lib)
        {
            try
            {
                var recent = await lib.QueryAsync(new Shotwise.Core.Library.LibraryQuery(Take: 12));
                items.AddRange(recent.Select(r => (r.Name, r.Path)));
            }
            catch (Exception ex) { _log.LogDebug(ex, "Recent query failed"); }
        }
        foreach (var d in _docs.Where(d => d.FilePath is not null).Reverse())
            if (!items.Any(i => string.Equals(i.Path, d.FilePath, StringComparison.OrdinalIgnoreCase))) items.Insert(0, (d.Title, d.FilePath!));
        if (items.Count == 0) _openRecent.Items.Add(new MenuItem { Header = ES.T("Menu_NoRecent", "(none)"), IsEnabled = false });
        foreach (var (name, path) in items.Take(12))
        {
            var p = path;
            var item = new MenuItem { Header = name.Replace("_", "__"), ToolTip = path, Icon = MenuIcon(VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? Icons.Glyph.Video : Icons.Glyph.Image) };
            item.Click += (_, _) => RunUi(() => OpenFileAsync(p));
            _openRecent.Items.Add(item);
        }
    }

    private Task SetWorkspaceColorAsync(WorkspaceColor color)
    {
        if (color == WorkspaceColor.Custom)
            return PickCustomWorkspaceAsync();
        _ctx.Settings.Update(s => s.Editor.WorkspaceColor = color);
        return Task.CompletedTask;
    }

    private async Task PickCustomWorkspaceAsync()
    {
        var picked = await _ctx.Dialogs.ShowColorPicker(new ColorPickerRequest(_ctx.Prefs.CustomWorkspaceColor, false, ES.T("Bg_Custom", "Custom…")) { Owner = this });
        if (picked is null) return;
        _ctx.Settings.Update(s => { s.Editor.CustomWorkspaceColor = picked.Color; s.Editor.WorkspaceColor = WorkspaceColor.Custom; });
    }

    /// <summary>Fills the Share menu (or a context menu) with the destinations for the active document.</summary>
    private void PopulateShareMenu(MenuItem menu, bool linkOnly)
    {
        menu.Items.Clear();
        if (_active is null)
        {
            menu.Items.Add(new MenuItem { Header = ES.T("Share_NothingOpen", "Open an image or video to share it"), IsEnabled = false });
            return;
        }
        var cm = _share.BuildMenu(_active, linkOnly, () => RunUi(() => _share.ShowHistoryAsync(_active)), () => RunUi(() => _ctx.Dialogs.ShowShareAccounts(new ShareAccountsRequest() { Owner = this })));
        var items = cm.Items.Cast<object>().ToList();
        cm.Items.Clear();
        foreach (var i in items) menu.Items.Add(i);
    }

    private void ShowShareMenu()
    {
        if (_active is null) return;
        var cm = _share.BuildMenu(_active, false, () => RunUi(() => _share.ShowHistoryAsync(_active)), () => RunUi(() => _ctx.Dialogs.ShowShareAccounts(new ShareAccountsRequest() { Owner = this })));
        cm.PlacementTarget = _shareButton;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }
}
