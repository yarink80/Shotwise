using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// Stamp browser (primary content of the right panel while the Stamp tool is active): search across all categories (keyword
/// matching is done by the catalog), category picker, a "recent" strip, and a virtualised grid of 64 px tiles. The grid is a
/// <see cref="ListBox"/> of rows (N tiles per row, N follows the width) so only the visible rows exist; thumbnails load
/// asynchronously (two at a time, tiles that scrolled away are skipped) into a bounded cache. Hover = large preview tooltip,
/// star = favourite. Choosing a stamp arms the Stamp tool.
/// </summary>
internal sealed class StampGallery : Grid
{
    private const double TileSize = 64, TileGap = 2, ThumbDip = 48;
    private const int ThumbCacheLimit = 800, RecentLimit = 10;

    private readonly EditorContext _ctx;
    private readonly PreviewRenderer _previews;
    private readonly EditorSession _s;
    private readonly EditorLayout _layout;
    private readonly ComboBox _categories = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13 };
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _recentHost = new() { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _recentTiles = new();
    private readonly ILogger _log;

    private readonly Dictionary<string, ImageSource?> _thumbs = new();
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly Dictionary<string, StampInfo> _byId = new();
    private readonly HashSet<string> _favs = new();
    private List<StampInfo> _all = new();
    private List<StampInfo> _current = new();
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private bool _loadingCategories = true;
    private bool _subscribed;
    private bool _ignoreCatalogChange;
    private int _perRow = 4;
    private string? _selectedId;

    public StampGallery(EditorContext ctx, PreviewRenderer previews, EditorSession session, EditorLayout layout)
    {
        _ctx = ctx;
        _previews = previews;
        _s = session;
        _layout = layout;
        _log = ctx.Log<StampGallery>();
        _selectedId = session.ToolOptions.Stamp.StampId;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // row 0: search + import
        var top = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search.SetResourceReference(StyleProperty, "Shotwise.SearchBox");
        _search.Tag = ES.T("Stamp_SearchAll", "Search all stamps");
        AutomationProperties.SetName(_search, ES.T("Stamp_SearchAll", "Search all stamps"));
        top.Children.Add(_search);
        var import = UiKit.IconButton(Icons.Glyph.Download, ES.T("Stamp_Import", "Import stamps…"), () => UiKit.Fire(ImportAsync, ex => _ = _ctx.ShowErrorAsync(ex)));
        import.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(import, 1);
        top.Children.Add(import);
        Children.Add(top);

        // row 1: category
        AutomationProperties.SetName(_categories, ES.T("Stamp_Category", "Stamp category"));
        Grid.SetRow(_categories, 1);
        _categories.Margin = new Thickness(0, 0, 0, 8);
        Children.Add(_categories);

        // row 2: recent strip
        var recentLabel = UiKit.Header(ES.T("Stamp_Recent", "Recently used"));
        recentLabel.Margin = new Thickness(0, 0, 0, 2);
        _recentHost.Children.Add(recentLabel);
        _recentHost.Children.Add(_recentTiles);
        Grid.SetRow(_recentHost, 2);
        Children.Add(_recentHost);

        // row 3: virtualised grid
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.Padding = new Thickness(0);
        _list.SelectionMode = SelectionMode.Single;
        _list.Focusable = false;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'>" +
            "<Setter Property='Focusable' Value='False'/><Setter Property='Padding' Value='0'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><ContentPresenter/></ControlTemplate></Setter.Value></Setter></Style>");
        var rowFactory = new FrameworkElementFactory(typeof(StampRowHost));
        _list.ItemTemplate = new DataTemplate { VisualTree = rowFactory };
        Grid.SetRow(_list, 3);
        Children.Add(_list);

        // row 4: status
        _status.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        Grid.SetRow(_status, 4);
        Children.Add(_status);

        if (_ctx.Stamps is null)
        {
            _status.Text = ES.T("Stamp_Unavailable", "The stamp catalog is not available.");
            return;
        }
        _categories.SelectionChanged += (_, _) => { if (!_loadingCategories) _ = RefreshAsync(); };
        _search.TextChanged += (_, _) => DebouncedSearch();
        _list.SizeChanged += (_, _) => UpdatePerRow();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        Subscribe();
        _ = LoadAllAsync();
    }

    private void Subscribe()
    {
        if (_subscribed || _ctx.Stamps is null) return;
        _subscribed = true;
        _ctx.Stamps.Changed += OnCatalogChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _ctx.Stamps is null) return;
        _subscribed = false;
        _ctx.Stamps.Changed -= OnCatalogChanged;
        _cts?.Cancel();
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (_ignoreCatalogChange) return;
        Dispatcher.BeginInvoke(() => _ = LoadAllAsync());
    }

    private string Culture => CultureInfo.CurrentUICulture.Name;

    private async Task LoadAllAsync()
    {
        if (_ctx.Stamps is null) return;
        try
        {
            var cats = await _ctx.Stamps.GetCategoriesAsync();
            _all = (await _ctx.Stamps.SearchAsync("", null, Culture)).ToList();
            _byId.Clear();
            _favs.Clear();
            foreach (var i in _all) { _byId[i.Id] = i; if (i.IsFavorite) _favs.Add(i.Id); }

            _loadingCategories = true;
            var previous = (_categories.SelectedItem as ComboBoxItem)?.Tag as string;
            _categories.Items.Clear();
            _categories.Items.Add(new ComboBoxItem { Content = ES.F("Stamp_AllCount", "All stamps ({0})", _all.Count), Tag = "all" });
            _categories.Items.Add(new ComboBoxItem { Content = ES.T("Stamp_Favorites", "Favourites"), Tag = "favorites" });
            foreach (var c in cats) _categories.Items.Add(new ComboBoxItem { Content = $"{c.Name} ({c.Count})", Tag = c.Id });
            _categories.SelectedIndex = 0;
            for (var i = 0; i < _categories.Items.Count; i++)
                if (_categories.Items[i] is ComboBoxItem { Tag: string t } && t == previous) _categories.SelectedIndex = i;
            _loadingCategories = false;
            BuildRecent();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stamp catalog load failed");
            _status.Text = ex.Message;
        }
    }

    private void DebouncedSearch()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Stop();
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _ = RefreshAsync();
    }

    /// <summary>Search spans all categories (the category picker only filters while the box is empty).</summary>
    private async Task RefreshAsync()
    {
        if (_ctx.Stamps is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var query = _search.Text.Trim();
        var category = (_categories.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        try
        {
            List<StampInfo> list;
            if (query.Length > 0) list = (await _ctx.Stamps.SearchAsync(query, null, Culture)).ToList();
            else if (category == "all") list = _all;
            else if (category == "favorites") list = _all.Where(i => _favs.Contains(i.Id)).ToList();
            else list = (await _ctx.Stamps.SearchAsync("", category, Culture)).ToList();
            if (cts.IsCancellationRequested) return;
            _current = list;
            _status.Text = list.Count == 0 ? ES.T("Stamp_NoResults", "No stamps found.")
                : query.Length > 0 ? ES.F("Stamp_ResultsAll", "{0} results in all categories", list.Count)
                : ES.F("Stamp_Count", "{0} stamps", list.Count);
            _recentHost.Visibility = query.Length == 0 && _recentTiles.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildRows(scrollHome: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stamp search failed");
            _status.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------------ rows (virtualised)

    private void UpdatePerRow()
    {
        var n = Math.Max(1, (int)Math.Floor((_list.ActualWidth - 10) / (TileSize + TileGap * 2)));
        if (n == _perRow) return;
        _perRow = n;
        BuildRows(scrollHome: false);
    }

    private void BuildRows(bool scrollHome)
    {
        var rows = new List<StampRow>((_current.Count + _perRow - 1) / _perRow);
        for (var i = 0; i < _current.Count; i += _perRow)
            rows.Add(new StampRow(this, _current.Skip(i).Take(_perRow).ToList()));
        _list.ItemsSource = rows;
        if (scrollHome && rows.Count > 0) _list.ScrollIntoView(rows[0]);
    }

    private sealed record StampRow(StampGallery Owner, IReadOnlyList<StampInfo> Items);

    /// <summary>One grid row; rebuilds its tiles whenever the (recycled) container gets new data.</summary>
    private sealed class StampRowHost : Border
    {
        public StampRowHost() => DataContextChanged += (_, _) => Fill();

        private void Fill()
        {
            if (DataContext is not StampRow row) { Child = null; return; }
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var info in row.Items) panel.Children.Add(row.Owner.MakeTile(info, TileSize, ThumbDip, true));
            Child = panel;
        }
    }

    // ------------------------------------------------------------------ tiles

    private ToggleButton MakeTile(StampInfo info, double size, double thumb, bool star)
    {
        var img = new Image { Width = thumb, Height = thumb, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var host = new Grid();
        host.Children.Add(img);
        var b = new ToggleButton
        {
            Content = host,
            IsChecked = info.Id == _selectedId,
            Tag = info.Id,
            Width = size,
            Height = size,
            Margin = new Thickness(TileGap),
        }.Styled("Shotwise.ShadowCell");
        b.Width = size;
        b.Height = size;
        AutomationProperties.SetName(b, info.Name);
        b.ToolTip = info.Name;
        b.ToolTipOpening += async (_, _) => await BuildPreviewTooltipAsync(b, info);

        if (star)
        {
            var fav = _favs.Contains(info.Id);
            var st = new ToggleButton
            {
                Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -3, 0), IsChecked = fav, Focusable = false, Opacity = fav ? 1 : 0,
                ToolTip = fav ? ES.T("Stamp_Unfavorite", "Remove from favourites") : ES.T("Stamp_Favorite", "Add to favourites"),
            }.Styled("Shotwise.IconButton");
            SwUi.SetIcon(st, UiKit.IconSource(fav ? "Icon.Tool.Favorites.Color" : Icons.Glyph.Star));
            SwUi.SetIconSize(st, 12);
            AutomationProperties.SetName(st, ES.T("Stamp_Favorite", "Add to favourites"));
            b.MouseEnter += (_, _) => st.Opacity = 1;
            b.MouseLeave += (_, _) => st.Opacity = _favs.Contains(info.Id) ? 1 : 0;
            st.Click += (_, e) =>
            {
                e.Handled = true;
                ToggleFavourite(info);
                var now = _favs.Contains(info.Id);
                st.IsChecked = now;
                SwUi.SetIcon(st, UiKit.IconSource(now ? "Icon.Tool.Favorites.Color" : Icons.Glyph.Star));
            };
            host.Children.Add(st);
        }

        b.Click += (_, _) => _ = ChooseAsync(info);
        var menu = new ContextMenu();
        var favItem = new MenuItem { Header = _favs.Contains(info.Id) ? ES.T("Stamp_Unfavorite", "Remove from favourites") : ES.T("Stamp_Favorite", "Add to favourites") };
        favItem.Click += (_, _) => ToggleFavourite(info);
        menu.Items.Add(favItem);
        b.ContextMenu = menu;

        // thumbnail: cached or loaded asynchronously; a tile that was recycled away before its turn is skipped
        if (_thumbs.TryGetValue(info.Id, out var hit)) img.Source = hit;
        else _ = LoadIntoAsync(img, info);
        return b;
    }

    private async Task LoadIntoAsync(Image img, StampInfo info)
    {
        bool Wanted() => img.IsLoaded || PresentationSource.FromVisual(img) is not null;
        // a shared in-flight load may have been started for a tile that was recycled away (returns null): retry once for this one
        for (var attempt = 0; attempt < 2 && img.Source is null; attempt++)
        {
            var source = await ThumbAsync(info, Wanted);
            if (source is not null) { img.Source = source; return; }
            if (!Wanted()) return;
        }
    }

    private Task<ImageSource?> ThumbAsync(StampInfo info, Func<bool> wanted)
    {
        if (_thumbs.TryGetValue(info.Id, out var hit)) return Task.FromResult(hit);
        if (_inflight.TryGetValue(info.Id, out var running)) return running;
        var task = LoadThumbAsync(info, wanted);
        _inflight[info.Id] = task;
        return task;
    }

    private async Task<ImageSource?> LoadThumbAsync(StampInfo info, Func<bool> wanted)
    {
        await _gate.WaitAsync();
        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background); // let scrolling/input run first
            if (!wanted()) return null;
            var data = await _ctx.Stamps!.LoadAsync(info.Id);
            var img = _previews.Stamp(info, data, (int)ThumbDip, cache: false);
            if (img is Freezable { CanFreeze: true } f) f.Freeze();
            if (_thumbs.Count >= ThumbCacheLimit) _thumbs.Clear();
            _thumbs[info.Id] = img;
            return img;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stamp thumbnail failed: {Id}", info.Id);
            return null;
        }
        finally
        {
            _inflight.Remove(info.Id);
            _gate.Release();
        }
    }

    /// <summary>Hover preview: a larger render plus name and category.</summary>
    private async Task BuildPreviewTooltipAsync(FrameworkElement owner, StampInfo info)
    {
        try
        {
            var data = await _ctx.Stamps!.LoadAsync(info.Id);
            var big = _previews.Stamp(info, data, 112, cache: false);
            var panel = new StackPanel { MinWidth = 120 };
            panel.Children.Add(new Border { Child = new Image { Source = big, Width = 112, Height = 112, Stretch = Stretch.Uniform }, Padding = new Thickness(4) });
            panel.Children.Add(new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 180, Margin = new Thickness(4, 2, 4, 0) });
            panel.Children.Add(new TextBlock { Text = info.CategoryId, FontSize = 11, Opacity = 0.7, Margin = new Thickness(4, 0, 4, 4) });
            owner.ToolTip = panel;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Stamp preview tooltip failed: {Id}", info.Id); }
    }

    private void ToggleFavourite(StampInfo info)
    {
        if (_ctx.Stamps is null) return;
        _ignoreCatalogChange = true;
        try { _ctx.Stamps.ToggleFavorite(info.Id); }
        finally { _ignoreCatalogChange = false; }
        if (!_favs.Remove(info.Id)) _favs.Add(info.Id);
        if ((_categories.SelectedItem as ComboBoxItem)?.Tag as string == "favorites" && _search.Text.Trim().Length == 0) _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ choosing + recents

    private async Task ChooseAsync(StampInfo info)
    {
        try
        {
            var data = await _ctx.Stamps!.LoadAsync(info.Id);
            _selectedId = info.Id;
            var o = _s.ToolOptions.Stamp;
            o.Data = data;
            o.MediaType = info.MediaType;
            o.StampId = info.Id;
            o.NaturalSize = NaturalSize(data, info.MediaType);
            _s.ActiveTool = ToolKind.Stamp;
            _s.NotifyToolOptionsChanged();
            MarkSelected();
            PushRecent(info.Id);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stamp load failed: {Id}", info.Id);
        }
    }

    private void MarkSelected()
    {
        foreach (var t in Descendants<ToggleButton>(this))
            if (t.Tag is string id) t.IsChecked = id == _selectedId;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    private void PushRecent(string id)
    {
        _layout.RecentStamps.Remove(id);
        _layout.RecentStamps.Insert(0, id);
        if (_layout.RecentStamps.Count > RecentLimit) _layout.RecentStamps.RemoveRange(RecentLimit, _layout.RecentStamps.Count - RecentLimit);
        _ctx.Layout.Save(_layout);
        BuildRecent();
    }

    private void BuildRecent()
    {
        _recentTiles.Children.Clear();
        foreach (var id in _layout.RecentStamps)
            if (_byId.TryGetValue(id, out var info)) _recentTiles.Children.Add(MakeTile(info, 44, 32, false));
        _recentHost.Visibility = _recentTiles.Children.Count > 0 && _search.Text.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ImportAsync()
    {
        if (_ctx.Stamps is null) return;
        var dlg = new OpenFileDialog { Filter = ES.T("Stamp_PackFilter", "Stamp packs (*.shotstamps)|*.shotstamps|All files|*.*"), Multiselect = true };
        if (dlg.ShowDialog(_ctx.ActiveWindow) != true) return;
        foreach (var f in dlg.FileNames) await _ctx.Stamps.ImportPackAsync(f);
    }

    /// <summary>Best-effort natural size of a stamp asset (SVG width/height/viewBox, raster decode, default 96).</summary>
    public static Size2 NaturalSize(byte[] data, string mediaType)
    {
        try
        {
            if (mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
            {
                var head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 2048));
                double? w = Attr(head, "width"), h = Attr(head, "height");
                if (w is > 0 && h is > 0) return Fit(w.Value, h.Value);
                var m = Regex.Match(head, "viewBox\\s*=\\s*\"\\s*[-\\d.]+[ ,]+[-\\d.]+[ ,]+([\\d.]+)[ ,]+([\\d.]+)\"");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vw) && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vh) && vw > 0 && vh > 0)
                    return Fit(vw, vh);
            }
            else if (!mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream(data);
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                return Fit(frame.PixelWidth, frame.PixelHeight);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException) { }
        return new Size2(96, 96);
    }

    private static double? Attr(string head, string name)
    {
        var m = Regex.Match(head, "<svg[^>]*\\s" + name + "\\s*=\\s*\"([\\d.]+)(px)?\"");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static Size2 Fit(double w, double h)
    {
        var max = 192.0;
        var k = Math.Min(1.0, max / Math.Max(w, h));
        if (Math.Max(w, h) < 48) k = 48 / Math.Max(w, h);
        return new Size2(Math.Max(8, w * k), Math.Max(8, h * k));
    }
}
