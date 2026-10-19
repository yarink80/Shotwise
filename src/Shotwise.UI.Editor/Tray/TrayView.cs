using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Library;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;
using Shotwise.UI.Editor.Share;

namespace Shotwise.UI.Editor.Tray;

/// <summary>One tile of the tray: an open document and/or a recent library item.</summary>
internal sealed class TrayItem
{
    public required string Key { get; init; }
    public EditorDocument? Document { get; set; }
    public LibraryItem? Library { get; set; }
    public string Title { get; set; } = "";
    public string? Path => Document?.FilePath ?? Library?.Path;
    public bool IsPinned { get; set; }
    public bool IsVideo => Document?.IsVideo ?? Library?.Kind is MediaKind.Video or MediaKind.Gif;
    public bool IsOpen => Document is not null;
    public bool IsDirty => Document?.IsDirty ?? false;
    public ImageSource? Thumbnail { get; set; }
}

/// <summary>
/// Bottom tray strip (EDT-11): open documents plus recent library items (max from preferences), pin/unpin, rename,
/// close, delete, drag reorder, drag out as file, multi-select actions (Combine Images / Videos, Create GIF).
/// </summary>
internal sealed class TrayView : Border
{
    public const string DragFormat = "Shotwise.TrayItem";

    private readonly EditorContext _ctx;
    private readonly Func<IReadOnlyList<EditorDocument>> _openDocs;
    private readonly ListBox _list;
    private readonly ILogger _log;
    private readonly Dictionary<string, ImageSource?> _thumbCache = new();
    private readonly List<TrayItem> _items = new();
    private readonly List<string> _order = new();
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private EditorDocument? _active;
    private IReadOnlyList<LibraryItem> _recent = Array.Empty<LibraryItem>();
    private Point _dragStart;
    private TrayItem? _dragItem;
    private bool _refreshQueued;
    private CancellationTokenSource? _cts;

    public TrayView(EditorContext ctx, Func<IReadOnlyList<EditorDocument>> openDocs)
    {
        _ctx = ctx;
        _openDocs = openDocs;
        _log = ctx.Log<TrayView>();
        this.Themed(BackgroundProperty, Tok.Surface1);
        Padding = new Thickness(6, 0, 8, 4);
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            AllowDrop = true,
            Padding = new Thickness(0),
        }.Styled("Shotwise.TrayList");
        AutomationProperties.SetName(_list, ES.T("Cmd_RecentCaptures", "Recent Captures"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonDown += OnMouseDown;
        _list.PreviewMouseMove += OnMouseMove;
        _list.PreviewMouseLeftButtonUp += OnMouseUp;
        _list.Drop += OnDrop;
        _list.DragOver += OnDragOver;
        _list.PreviewKeyDown += OnKeyDown;
        _list.SelectionChanged += (_, _) => SyncRings();
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.Children.Add(_list);
        var library = new Button { Content = ES.T("Tray_Library", "Library"), ToolTip = ES.T("Tray_LibraryTip", "Open the library (all captures)"), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, MinHeight = 30, Command = EditorCommands.SwitchToLibrary }.Styled("Shotwise.SecondaryButton");
        Shotwise.UI.Common.Controls.SwUi.SetIcon(library, "Icon.Library.Regular");
        Grid.SetColumn(library, 1);
        host.Children.Add(library);
        Child = host;
        _ctx.Settings.Changed += OnSettingsChanged;
        if (_ctx.Library is not null) _ctx.Library.Changed += OnLibraryChanged;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(AppSettings.Library)) || e.Affects(nameof(AppSettings.Editor))) Dispatcher.BeginInvoke(QueueRefresh);
    }

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e) => Dispatcher.BeginInvoke(QueueRefresh);

    /// <summary>Real window closure (R5-#5): unsubscribes from the shared services and cancels pending work.</summary>
    public void ReleaseServices()
    {
        _ctx.Settings.Changed -= OnSettingsChanged;
        if (_ctx.Library is not null) _ctx.Library.Changed -= OnLibraryChanged;
        _cts?.Cancel();
        _dragCts?.Cancel();
        _thumbCache.Clear();
        _list.Items.Clear();
        _items.Clear();
    }

    public event Action<TrayItem>? ItemActivated;
    public event Action<IReadOnlyList<TrayItem>, string>? CommandRequested;
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public IReadOnlyList<TrayItem> Selected => _list.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<TrayItem>().ToList();

    public int TileWidth => _ctx.Prefs.RecentThumbnailSize switch { ThumbnailSize.Small => 84, ThumbnailSize.Large => 150, _ => 108 };

    private double TileHeight => Math.Round(TileWidth * 0.66);

    public void SetActive(EditorDocument? doc)
    {
        _active = doc;
        SyncRings();
    }

    /// <summary>Accent ring on the active document's card and on selected cards.</summary>
    private void SyncRings()
    {
        foreach (var li in _list.Items.OfType<ListBoxItem>())
        {
            if (li.Tag is not TrayItem t || CardOf(li) is not { } card) continue;
            card.IsSelected = li.IsSelected || _active is not null && ReferenceEquals(t.Document, _active);
            if (TitleOf(li) is { } title) title.SetResourceReference(TextBlock.ForegroundProperty, card.IsSelected ? Tok.AccentText : t.IsOpen ? Tok.Text : Tok.Text2);
        }
    }

    private static Shotwise.UI.Common.Controls.TrayItem? CardOf(ListBoxItem li) => li.Content is StackPanel { Children: [Shotwise.UI.Common.Controls.TrayItem card, ..] } ? card : null;
    private static TextBlock? TitleOf(ListBoxItem li) => li.Content is StackPanel { Children: [_, TextBlock title, ..] } ? title : null;

    public void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            _refreshQueued = false;
            try { await RefreshAsync(); }
            catch (Exception ex) { _log.LogDebug(ex, "Tray refresh failed"); }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Hides a closed item from the tray when "remove unpinned items on close" is on.</summary>
    public void Forget(TrayItem item)
    {
        if (item.Path is { } p) _hidden.Add(p);
        _items.Remove(item);
    }

    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var lib = _ctx.Library;
        if (lib is not null)
        {
            try
            {
                var take = Math.Clamp(_ctx.Settings.Current.Library.MaxTrayItems, 1, LibrarySettings.MaxTrayItemsLimit);
                _recent = await lib.QueryAsync(new LibraryQuery(Take: take, Sort: LibrarySort.NewestFirst), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Tray library query failed"); }
        }
        if (cts.IsCancellationRequested) return;
        Rebuild();
    }

    private void Rebuild()
    {
        var pinned = _ctx.Settings.Current.Library.PinnedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, TrayItem>(StringComparer.OrdinalIgnoreCase);
        var docs = _openDocs();
        foreach (var d in docs)
        {
            var key = d.FilePath is { } fp ? "p:" + fp : "d:" + d.Id;
            map[key] = new TrayItem { Key = key, Document = d, Library = d.LibraryItem, Title = d.Title, IsPinned = d.LibraryItem is { } li && pinned.Contains(li.Id.ToString()) || d.IsPinned };
        }
        var max = Math.Clamp(_ctx.Settings.Current.Library.MaxTrayItems, 1, LibrarySettings.MaxTrayItemsLimit);
        foreach (var li in _recent)
        {
            var key = "p:" + li.Path;
            if (map.TryGetValue(key, out var existing)) { existing.Library = li; existing.IsPinned |= pinned.Contains(li.Id.ToString()); continue; }
            if (_hidden.Contains(li.Path) && !pinned.Contains(li.Id.ToString())) continue;
            if (map.Count >= max + docs.Count) break;
            map[key] = new TrayItem { Key = key, Library = li, Title = li.Name, IsPinned = pinned.Contains(li.Id.ToString()) };
        }
        // order: custom order first (drag reorder), pinned next, then open docs, then recent as returned.
        var ordered = new List<TrayItem>();
        foreach (var k in _order) if (map.Remove(k, out var t)) ordered.Add(t);
        var rest = map.Values.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.IsOpen).ToList();
        ordered.AddRange(rest);
        _items.Clear();
        _items.AddRange(ordered);
        _order.Clear();
        _order.AddRange(_items.Select(i => i.Key));

        var selectedKeys = Selected.Select(s => s.Key).ToHashSet();
        _list.Items.Clear();
        foreach (var item in _items)
        {
            var li = new ListBoxItem { Tag = item, Content = BuildTile(item) };
            AutomationProperties.SetName(li, item.Title);
            li.ContextMenu = BuildMenu(item);
            _list.Items.Add(li);
            if (selectedKeys.Contains(item.Key)) li.IsSelected = true;
            _ = LoadThumbnailAsync(item, li);
        }
        SetActive(_active);
    }

    private FrameworkElement BuildTile(TrayItem item)
    {
        var w = TileWidth;
        var duration = item.Document is VideoEditorDocument vd ? vd.Info.Duration : item.Library?.Duration;
        var card = new Shotwise.UI.Common.Controls.TrayItem
        {
            ItemWidth = w,
            ItemHeight = TileHeight,
            Thumbnail = item.Thumbnail,
            IsPinned = item.IsPinned,
            IsModified = item.IsDirty,
            Duration = item.IsVideo ? (duration is { } d ? UiKit.FormatShortDuration(d) : "GIF") : null,
            Title = item.Title,
        };
        if (item.IsDirty) card.ToolTip = ES.T("Tray_Unsaved", "Unsaved changes");
        var title = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w, FontSize = 11, Margin = new Thickness(1, 5, 1, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, item.IsOpen ? Tok.Text : Tok.Text2);
        if (item.IsOpen) title.FontWeight = FontWeights.SemiBold;
        var sp = new StackPanel { Width = w };
        sp.Children.Add(card);
        sp.Children.Add(title);
        sp.ToolTip = item.Path ?? item.Title;
        // Editor ▸ "Preview recent captures": hovering a tile shows a large preview of the capture.
        sp.ToolTipOpening += (_, _) =>
        {
            if (!_ctx.Prefs.PreviewRecentCaptures || item.Thumbnail is null) { sp.ToolTip = item.Path ?? item.Title; return; }
            var tip = new StackPanel { MaxWidth = 360 };
            tip.Children.Add(new Image { Source = item.Thumbnail, MaxWidth = 360, MaxHeight = 240, Stretch = Stretch.Uniform });
            tip.Children.Add(new TextBlock { Text = item.Path ?? item.Title, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            sp.ToolTip = tip;
        };
        return sp;
    }

    private async Task LoadThumbnailAsync(TrayItem item, ListBoxItem container)
    {
        try
        {
            ImageSource? src = null;
            if (item.Document is ImageEditorDocument img)
            {
                src = img.Thumbnail;
                if (src is null || img.ThumbnailDirty)
                {
                    img.ThumbnailDirty = false;
                    try
                    {
                        var render = _ctx.Renderer.RenderThumbnail(img.Document, 256);
                        src = render.ToBitmapSource();
                        img.Thumbnail = src;
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Thumbnail render failed"); }
                }
            }
            else if (item.Library is { } li && !string.IsNullOrEmpty(li.ThumbnailPath))
            {
                if (!_thumbCache.TryGetValue(li.ThumbnailPath, out src))
                {
                    src = await Task.Run(() => LoadBitmap(li.ThumbnailPath));
                    _thumbCache[li.ThumbnailPath] = src;
                }
            }
            else if (item.Document is VideoEditorDocument vid && vid.Thumbnail is null && _ctx.Video is { } vs && vid.FilePath is { } vp)
            {
                var frames = await vs.GetThumbnailsAsync(vp, 1, 120, CancellationToken.None);
                if (frames.Count > 0) { vid.Thumbnail = frames[0].ToBitmapSource(); }
                src = vid.Thumbnail;
            }
            else src = item.Document?.Thumbnail;

            item.Thumbnail = src;
            if (CardOf(container) is { } card) card.Thumbnail = src;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Tray thumbnail failed"); }
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 256;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Re-renders thumbnails of open documents that changed (called on a timer by the window).</summary>
    public void RefreshOpenThumbnails()
    {
        foreach (var li in _list.Items.OfType<ListBoxItem>())
            if (li.Tag is TrayItem { Document: ImageEditorDocument { ThumbnailDirty: true } } item && DateTime.UtcNow - ((ImageEditorDocument)item.Document).LastChangeUtc > TimeSpan.FromMilliseconds(700))
                _ = LoadThumbnailAsync(item, li);
    }

    // ------------------------------------------------------------------ context menu

    private ContextMenu BuildMenu(TrayItem item)
    {
        var menu = new ContextMenu();
        MenuItem Mi(string header, string command, string? gesture = null, string? glyph = null)
        {
            var m = new MenuItem { Header = header, InputGestureText = gesture };
            if (glyph is not null) m.Icon = UiKit.Glyph(glyph, 16);
            m.Click += (_, _) => CommandRequested?.Invoke(SelectionFor(item), command);
            return m;
        }
        menu.Items.Add(Mi(ES.T("Tray_Open", "Open"), "open", null, Icons.Glyph.OpenFile));
        menu.Items.Add(Mi(item.IsPinned ? ES.T("Tray_Unpin", "Unpin") : ES.T("Tray_Pin", "Pin"), "pin", "Ctrl+Shift+P", Icons.Glyph.Pin));
        menu.Items.Add(Mi(ES.T("Tray_Rename", "Rename"), "rename", "F2", Icons.Glyph.Edit));
        menu.Items.Add(Mi(ES.T("Tray_Duplicate", "Duplicate"), "duplicate", "Ctrl+Shift+D", Icons.Glyph.Copy));
        menu.Items.Add(new Separator());
        var combine = Mi(ES.T("Tray_CombineImages", "Combine Images…"), "combineImages", "Ctrl+Alt+C", Icons.Glyph.Collections);
        var combineV = Mi(ES.T("Tray_CombineVideos", "Combine Videos…"), "combineVideos", "Ctrl+Shift+V", Icons.Glyph.VideoClip);
        var gif = Mi(ES.T("Tray_CreateGif", "Create GIF…"), "createGif", null, Icons.Glyph.Gif);
        menu.Items.Add(combine);
        menu.Items.Add(combineV);
        menu.Items.Add(gif);
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(ES.T("Tray_Tag", "Tags…"), "tag", null, Icons.Glyph.Tag));
        menu.Items.Add(Mi(ES.T("Tray_Reveal", "Show in Explorer"), "reveal", null, Icons.Glyph.Folder));
        menu.Items.Add(Mi(ES.T("Tray_ShareHistory", "Share history…"), "shareHistory", null, Icons.Glyph.History));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(ES.T("Tray_Close", "Close"), "close", "Ctrl+F4", Icons.Glyph.Close));
        menu.Items.Add(Mi(ES.T("Tray_Delete", "Move to Recycle Bin"), "delete", "Ctrl+Del", Icons.Glyph.Delete));
        menu.Opened += (_, _) =>
        {
            var sel = SelectionFor(item);
            var images = sel.Count(t => !t.IsVideo);
            var videos = sel.Count(t => t.IsVideo);
            combine.IsEnabled = images >= 2;
            combineV.IsEnabled = videos >= 2;
            gif.IsEnabled = sel.Count == 1 && sel[0].IsVideo || sel.Count >= 2 && images >= 2;
            ((MenuItem)menu.Items[2]).IsEnabled = sel.Count == 1;
        };
        return menu;
    }

    private IReadOnlyList<TrayItem> SelectionFor(TrayItem clicked)
    {
        var sel = Selected;
        return sel.Any(s => s.Key == clicked.Key) ? sel : new[] { clicked };
    }

    // ------------------------------------------------------------------ mouse / drag

    private TrayItem? ItemAt(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem) source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.Tag as TrayItem;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemAt(e.OriginalSource as DependencyObject);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        _dragItem = null;
        if (item is null || Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
        ItemActivated?.Invoke(item);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        if (_dragPreparing) return;
        var files = Selected.Any(s => s.Key == item.Key) ? Selected : new[] { item };
        UiKit.Fire(() => StartDragAsync(item, files), ex => _log.LogDebug(ex, "Drag failed"));
    }

    private bool _dragPreparing;
    private CancellationTokenSource? _dragCts;

    /// <summary>
    /// R5-#6: prepares the drag files without blocking the dispatcher (closed documents load and render off the UI
    /// thread; open documents render on the UI thread and encode off it), then starts the OLE drag only if the mouse
    /// button is still down. Re-entrancy guarded; Esc/close cancels via <see cref="_dragCts"/>.
    /// </summary>
    private async Task StartDragAsync(TrayItem item, IReadOnlyList<TrayItem> files)
    {
        _dragPreparing = true;
        _dragCts?.Cancel();
        using var cts = _dragCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var slow = files.Any(f => !f.IsVideo && (f.Document is ImageEditorDocument || f.Path?.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase) == true));
        if (slow) Mouse.OverrideCursor = Cursors.AppStarting;
        var paths = new List<string>();
        try
        {
            foreach (var f in files)
            {
                try
                {
                    if (await ExportForDragAsync(f, cts.Token) is { } p) paths.Add(p);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "Drag export failed for {Title}", f.Title);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (slow) Mouse.OverrideCursor = null;
            _dragPreparing = false;
            if (ReferenceEquals(_dragCts, cts)) _dragCts = null;
        }
        // The user may have released the button while the files were prepared: then there is nothing to drag.
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        var data = new DataObject();
        data.SetData(DragFormat, item.Key);
        if (paths.Count > 0)
        {
            var sc = new System.Collections.Specialized.StringCollection();
            sc.AddRange(paths.ToArray());
            data.SetFileDropList(sc);
        }
        DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    /// <summary>Exports a tray item to a temp file in the drag-and-drop format preference (default PNG).</summary>
    private async Task<string?> ExportForDragAsync(TrayItem item, CancellationToken ct)
    {
        if (item.IsVideo) return item.Path;
        var ext = _ctx.Prefs.DragAndDropFileType.TrimStart('.').ToLowerInvariant();
        if (!Shotwise.Core.Imaging.ImageFormatExtensions.TryFromExtension(ext, out var fmt)) { fmt = Shotwise.Core.Imaging.ImageFormat.Png; ext = "png"; }
        var folder = Path.Combine(_ctx.Paths.Temp, "drag");
        var name = string.Concat(item.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var target = Path.Combine(folder, $"{name}.{ext}");
        var codec = _ctx.Codec;
        var renderer = _ctx.Renderer;
        if (item.Document is ImageEditorDocument open)
        {
            // The live document belongs to the UI thread: render it here, encode/write off-thread.
            open.Session.CommitTextEditSafe();
            var render = renderer.RenderPage(open.Document, open.Document.Pages[0], new RenderOptions(1));
            await Task.Run(() => { Directory.CreateDirectory(folder); codec.Save(render, target, fmt); }, ct);
            return target;
        }
        if (item.Path is not { } p) return null;
        if (!p.EndsWith(".shotx", StringComparison.OrdinalIgnoreCase)) return p;
        // A closed document is private to this operation: load, render and encode entirely off the UI thread.
        var serializer = _ctx.Serializer;
        await Task.Run(async () =>
        {
            var doc = await serializer.LoadAsync(p, ct).ConfigureAwait(false);
            doc.Assets.Codec ??= codec;
            ct.ThrowIfCancellationRequested();
            var render = renderer.RenderPage(doc, doc.Pages[0], new RenderOptions(1));
            Directory.CreateDirectory(folder);
            codec.Save(render, target, fmt);
        }, ct);
        return target;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? ((e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move)
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is string key)
        {
            var target = ItemAt(e.OriginalSource as DependencyObject);
            var source = _items.FirstOrDefault(i => i.Key == key);
            if (source is null) return;
            if (target is not null && !ReferenceEquals(target, source) && (e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            {
                CommandRequested?.Invoke(new[] { source, target }, source.IsVideo || target.IsVideo ? "combineVideos" : "combineImages");
                return;
            }
            var from = _order.IndexOf(key);
            var to = target is null ? _order.Count - 1 : _order.IndexOf(target.Key);
            if (from < 0 || to < 0 || from == to) return;
            _order.RemoveAt(from);
            _order.Insert(Math.Min(to, _order.Count), key);
            Rebuild();
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        if (e.Key == Key.F2) { CommandRequested?.Invoke(sel, "rename"); e.Handled = true; }
        else if (e.Key == Key.Enter) { ItemActivated?.Invoke(sel[0]); e.Handled = true; }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { CommandRequested?.Invoke(sel, "delete"); e.Handled = true; }
    }
}
