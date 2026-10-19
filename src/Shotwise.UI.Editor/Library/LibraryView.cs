using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Library;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Library;

/// <summary>
/// Library view (LIB-02/03/06/07, DESIGN-SYSTEM §4): left navigation (All / Images / Videos / Step Captures / Favourites with icons and
/// count badges, then "Browse by" groups with counts), a header with search box, sort and size, a virtualized grid of rounded capture
/// cards (thumbnail, title, date · size, hover actions open / share / favourite) and an empty state. Tag editing, open/delete/reveal,
/// share history and drag-out stay as before.
/// </summary>
internal sealed class LibraryView : Grid
{
    private const int PageSize = 240;

    private sealed record Row(IReadOnlyList<LibraryItem> Items);

    private sealed class RowConverter : IValueConverter
    {
        private readonly Func<Row, FrameworkElement> _build;
        public RowConverter(Func<Row, FrameworkElement> build) => _build = build;
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Row r ? _build(r) : null;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly ListBox _rows = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Single, Focusable = true, Padding = new Thickness(0) };
    private readonly StackPanel _filterNav = new();
    private readonly StackPanel _groupNav = new();
    private readonly Dictionary<string, NavItem> _filterItems = new();
    private readonly ComboBox _groupBy = new() { MinWidth = 120, FontSize = 12 };
    private readonly TextBox _search = new() { MinWidth = 260, MaxWidth = 420 };
    private readonly ComboBox _sort = new() { MinWidth = 140, FontSize = 12 };
    private readonly Slider _size = new() { Minimum = 140, Maximum = 300, Value = 200, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { Margin = new Thickness(2, 2, 0, 0), FontSize = 12 };
    private readonly EmptyState _empty = new() { Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, ImageSource?> _thumbs = new();
    private readonly List<LibraryItem> _items = new();
    private readonly HashSet<Guid> _selected = new();
    private LibraryQuery _baseQuery = new();
    private string _filter = "all";
    private string? _group;
    private int _columns = 4;
    private bool _loading;
    private bool _endReached;
    private bool _loadingFilters;
    private System.Windows.Threading.DispatcherTimer? _searchTimer;
    private bool _refreshQueued;
    private Guid? _anchor;
    private CancellationTokenSource? _cts;

    public LibraryView(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<LibraryView>();
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left navigation
        var sideBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder.SetResourceReference(Border.BackgroundProperty, Tok.Surface1);
        sideBorder.SetResourceReference(Border.BorderBrushProperty, Tok.BorderSubtle);
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var side = new StackPanel { Margin = new Thickness(12, 16, 12, 16) };
        var libTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 14) };
        libTitle.Children.Add(new SwIcon { Source = "Icon.Library.Color", Size = 22, Margin = new Thickness(0, 0, 10, 0) });
        libTitle.Children.Add(new TextBlock { Text = ES.T("Lib_Title", "Library"), VerticalAlignment = VerticalAlignment.Center }.Styled("Shotwise.Text.Subtitle"));
        side.Children.Add(libTitle);
        foreach (var (key, text, icon) in new[]
        {
            ("all", ES.T("Lib_AllFiles", "All Files"), "Icon.Grid.Regular"),
            ("image", ES.T("Lib_Images", "Images"), "Icon.Image.Regular"),
            ("video", ES.T("Lib_Videos", "Videos"), "Icon.Video.Regular"),
            ("steps", ES.T("Lib_Steps", "Step Captures"), "Icon.Tool.Step.Regular"),
            ("fav", ES.T("Lib_Favorites", "Favourites"), "Icon.Tool.Favorites.Regular"),
        })
        {
            var k = key;
            var n = new NavItem { Content = text, Icon = icon, GroupName = "LibFilter", IsChecked = key == "all" }.Styled("Shotwise.NavListItem");
            n.Checked += (_, _) => { if (_loadingFilters) return; _filter = k; _ = ReloadAsync(); };
            _filterItems[key] = n;
            _filterNav.Children.Add(n);
        }
        side.Children.Add(_filterNav);
        var browse = new PanelHeader { Content = ES.T("Lib_BrowseBy", "Browse by"), Actions = _groupBy, Padding = new Thickness(6, 18, 0, 6), FontSize = 12 };
        foreach (var (g, name) in new[] { (LibraryGroupBy.Month, ES.T("Lib_ByMonth", "Date (month)")), (LibraryGroupBy.Date, ES.T("Lib_ByDay", "Date (day)")), (LibraryGroupBy.Year, ES.T("Lib_ByYear", "Date (year)")), (LibraryGroupBy.App, ES.T("Lib_ByApp", "Application")), (LibraryGroupBy.Website, ES.T("Lib_ByWebsite", "Website")), (LibraryGroupBy.Tag, ES.T("Lib_ByTag", "Tag")), (LibraryGroupBy.Kind, ES.T("Lib_ByKind", "Media type")) })
            _groupBy.Items.Add(new ComboBoxItem { Content = name, Tag = g });
        _groupBy.SelectedIndex = 0;
        AutomationProperties.SetName(_groupBy, ES.T("Lib_GroupBy", "Group by"));
        side.Children.Add(browse);
        side.Children.Add(_groupNav);
        sideScroll.Content = side;
        sideBorder.Child = sideScroll;
        Children.Add(sideBorder);

        // ---- content
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetColumn(content, 1);

        var header = new Grid { Margin = new Thickness(24, 18, 20, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        _status.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        titles.Children.Add(_title);
        titles.Children.Add(_status);
        header.Children.Add(titles);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _search.Styled("Shotwise.SearchBox");
        _search.Tag = ES.T("Lib_SearchPlaceholder", "Search captures, text in images, apps, tags…");
        _search.ToolTip = ES.T("Lib_SearchTip", "Search names, applications, window titles, web addresses, text in images and tags");
        AutomationProperties.SetName(_search, ES.T("Lib_Search", "Search the library"));
        tools.Children.Add(_search);
        var clear = UiKit.IconButton(Icons.Glyph.Cancel, ES.T("Lib_ClearSearch", "Clear search"), () => _search.Text = "", 14);
        clear.Margin = new Thickness(2, 0, 10, 0);
        tools.Children.Add(clear);
        foreach (var (s, name) in new[] { (LibrarySort.NewestFirst, ES.T("Sort_Newest", "Newest first")), (LibrarySort.OldestFirst, ES.T("Sort_Oldest", "Oldest first")), (LibrarySort.NameAscending, ES.T("Sort_NameAsc", "Name A–Z")), (LibrarySort.NameDescending, ES.T("Sort_NameDesc", "Name Z–A")), (LibrarySort.LargestFirst, ES.T("Sort_Largest", "Largest first")), (LibrarySort.SmallestFirst, ES.T("Sort_Smallest", "Smallest first")) })
            _sort.Items.Add(new ComboBoxItem { Content = name, Tag = s });
        _sort.SelectedIndex = 0;
        AutomationProperties.SetName(_sort, ES.T("Lib_Sort", "Sort"));
        tools.Children.Add(_sort);
        var sizeIcon = UiKit.Glyph("Icon.Image.Regular", 14);
        sizeIcon.SetResourceReference(SwIcon.ForegroundProperty, Tok.Text3);
        sizeIcon.Margin = new Thickness(16, 0, 6, 0);
        tools.Children.Add(sizeIcon);
        _size.SetResourceReference(StyleProperty, "Shotwise.Slider");
        AutomationProperties.SetName(_size, ES.T("Lib_Size", "Thumbnail size"));
        tools.Children.Add(_size);
        var refresh = UiKit.IconButton(Icons.Glyph.Refresh, ES.T("Lib_Refresh", "Refresh (F5)"), () => _ = ReloadAsync(), 15);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        tools.Children.Add(refresh);
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        var top = new StackPanel();
        top.Children.Add(_importBanner);
        top.Children.Add(header);
        content.Children.Add(top);
        BuildImportBanner();

        var rowConverter = new RowConverter(BuildRow);
        var factory = new FrameworkElementFactory(typeof(ContentControl));
        factory.SetValue(FocusableProperty, false);
        factory.SetBinding(ContentControl.ContentProperty, new Binding { Converter = rowConverter });
        _rows.ItemTemplate = new DataTemplate { VisualTree = factory };
        _rows.ItemContainerStyle = NoSelectionStyle();
        VirtualizingPanel.SetIsVirtualizing(_rows, true);
        VirtualizingPanel.SetVirtualizationMode(_rows, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_rows, ScrollUnit.Pixel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_rows, ScrollBarVisibility.Disabled);
        SetRow(_rows, 1);
        content.Children.Add(_rows);
        _empty.Illustration = "Icon.Library.Color";
        _empty.Title = ES.T("Lib_EmptyTitle", "No captures here yet");
        _empty.Description = ES.T("Lib_EmptyBody", "Take a screenshot or a recording and it lands here automatically. Try another filter or search.");
        var newCapture = UiKit.PrimaryButton(ES.T("Learn_NewCapture", "New capture"), "Icon.Capture.Regular", () => NewCaptureRequested?.Invoke());
        newCapture.Margin = new Thickness(0);
        _empty.Action = newCapture;
        SetRow(_empty, 1);
        content.Children.Add(_empty);
        Children.Add(content);

        // events
        _groupBy.SelectionChanged += (_, _) => _ = LoadGroupsAsync();
        _sort.SelectionChanged += (_, _) => _ = ReloadAsync();
        _search.TextChanged += (_, _) =>
        {
            _searchTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchTimer.Stop();
            _searchTimer.Tick -= OnSearchTick;
            _searchTimer.Tick += OnSearchTick;
            _searchTimer.Start();
        };
        _size.ValueChanged += (_, _) => RebuildRows();
        _rows.SizeChanged += (_, _) => RebuildRows();
        _rows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScroll));
        PreviewKeyDown += OnKey;
        if (_ctx.Library is not null) _ctx.Library.Changed += OnLibraryChanged;
        Focusable = true;
        UpdateTitle();
    }

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e) => Dispatcher.BeginInvoke(QueueRefresh);

    // ------------------------------------------------------------------ import past captures (Library ▸ "Show prompt to import past captures")

    private readonly InfoBar _importBanner = new() { Margin = new Thickness(24, 16, 20, 0), Visibility = Visibility.Collapsed, IsClosable = false };
    private bool _bannerDismissed;

    private void BuildImportBanner()
    {
        _importBanner.Severity = InfoSeverity.Informational;
        _importBanner.Title = ES.T("Lib_ImportPastTitle", "Bring in your existing captures");
        _importBanner.Message = ES.T("Lib_ImportPastPrompt", "Bring your existing screenshots and recordings into the library to search, tag and reuse them.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var import = UiKit.PrimaryButton(ES.T("Lib_ImportPastFolder", "Import a folder…"), Icons.Glyph.Folder, () => UiKit.Fire(ImportPastCapturesAsync, Report));
        import.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(import);
        buttons.Children.Add(UiKit.GhostButton(ES.T("Lib_ImportPastLater", "Not now"), null, () => { _bannerDismissed = true; UpdateImportBanner(); }));
        buttons.Children.Add(UiKit.GhostButton(ES.T("Lib_ImportPastNever", "Don't show again"), null, () => _ctx.Settings.Update(s => s.Library.ShowImportPastCapturesPrompt = false)));
        _importBanner.Action = buttons;
        _ctx.Settings.Changed += OnSettingsChanged;
        UpdateImportBanner();
    }

    private void OnSettingsChanged(object? sender, Shotwise.Core.Settings.SettingsChangedEventArgs e)
    {
        if (e.Affects(nameof(Shotwise.Core.Settings.AppSettings.Library))) Dispatcher.BeginInvoke(UpdateImportBanner);
    }

    private void UpdateImportBanner()
        => _importBanner.Visibility = _ctx.Library is not null && _ctx.Settings.Current.Library.ShowImportPastCapturesPrompt && !_bannerDismissed ? Visibility.Visible : Visibility.Collapsed;

    private static readonly string[] PastCaptureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".mp4", ".shotx", ".snagx" };

    private async Task ImportPastCapturesAsync()
    {
        if (_ctx.Library is not { } lib) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = ES.T("Lib_ImportPastFolderTitle", "Choose a folder with past captures"), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dlg.ShowDialog(_ctx.ActiveWindow) != true) return;
        var folder = dlg.FolderName;
        _status.Text = ES.T("Msg_Importing", "Importing to the library…");
        var (imported, total) = await Task.Run(async () =>
        {
            var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => PastCaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Take(5000).ToList();
            var ok = 0;
            foreach (var f in files)
            {
                try { await lib.ImportFileAsync(f); ok++; }
                catch (Exception ex) when (ex is Shotwise.Core.ShotwiseException or IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Import of {File} failed", f); }
            }
            return (ok, files.Count);
        });
        _status.Text = ES.F("Msg_Imported", "Imported {0} of {1} files", imported, total);
        _bannerDismissed = true;
        UpdateImportBanner();
        await ReloadAsync();
    }

    /// <summary>Real window closure (R5-#5): unsubscribes from the shared library service and cancels pending work.</summary>
    public void ReleaseServices()
    {
        if (_ctx.Library is not null) _ctx.Library.Changed -= OnLibraryChanged;
        _ctx.Settings.Changed -= OnSettingsChanged;
        _cts?.Cancel();
        _searchTimer?.Stop();
        _thumbs.Clear();
        _rows.ItemsSource = null;
        _items.Clear();
    }

    public event Action<LibraryItem>? OpenRequested;
    public event Action<IReadOnlyList<LibraryItem>, string>? CommandRequested;
    /// <summary>The empty state's "New capture" button.</summary>
    public event Action? NewCaptureRequested;

    public IReadOnlyList<LibraryItem> SelectedItems => _items.Where(i => _selected.Contains(i.Id)).ToList();

    private static Style NoSelectionStyle()
    {
        var s = new Style(typeof(ListBoxItem));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        var tpl = new ControlTemplate(typeof(ListBoxItem));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        tpl.VisualTree = cp;
        s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return s;
    }

    public async Task ActivateAsync()
    {
        await LoadFilterCountsAsync();
        await LoadGroupsAsync();
        await ReloadAsync();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || !IsVisible) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            _refreshQueued = false;
            await Task.Delay(300);
            await LoadFilterCountsAsync();
            await LoadGroupsAsync(keepSelection: true);
            await ReloadAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnSearchTick(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        _ = ReloadAsync();
    }

    // ------------------------------------------------------------------ data

    /// <summary>Count badges of the left navigation (media-type group counts).</summary>
    private async Task LoadFilterCountsAsync()
    {
        if (_ctx.Library is null) return;
        try
        {
            var kinds = await _ctx.Library.GroupCountsAsync(LibraryGroupBy.Kind);
            int Count(params string[] keys) => kinds.Where(k => keys.Any(x => string.Equals(k.Key, x, StringComparison.OrdinalIgnoreCase))).Sum(k => k.Count);
            _filterItems["all"].Badge = kinds.Sum(k => k.Count);
            _filterItems["image"].Badge = Count(nameof(MediaKind.Image));
            _filterItems["video"].Badge = Count(nameof(MediaKind.Video), nameof(MediaKind.Gif));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Library counts failed"); }
    }

    private LibraryGroupBy GroupBy => (_groupBy.SelectedItem as ComboBoxItem)?.Tag is LibraryGroupBy g ? g : LibraryGroupBy.Month;

    private async Task LoadGroupsAsync(bool keepSelection = false)
    {
        if (_ctx.Library is null) return;
        var by = GroupBy;
        var previous = keepSelection ? _group : null;
        try
        {
            var counts = await _ctx.Library.GroupCountsAsync(by);
            _loadingFilters = true;
            _groupNav.Children.Clear();
            var icon = by switch
            {
                LibraryGroupBy.App => "Icon.Apps.Regular",
                LibraryGroupBy.Website => "Icon.Globe.Regular",
                LibraryGroupBy.Tag => "Icon.Tag.Regular",
                LibraryGroupBy.Kind => "Icon.Collections.Regular",
                _ => "Icon.Calendar.Regular",
            };
            var all = GroupItem(ES.T("Lib_AllGroups", "All"), null, "Icon.Grid.Regular", null);
            all.IsChecked = previous is null;
            _groupNav.Children.Add(all);
            foreach (var (key, count) in counts)
            {
                var text = string.IsNullOrEmpty(key) ? ES.T("Lib_Unknown", "(unknown)") : by == LibraryGroupBy.Month ? MonthName(key) : key;
                var item = GroupItem(text, key, icon, count);
                if (previous is not null && previous == key) item.IsChecked = true;
                _groupNav.Children.Add(item);
            }
            _group = _groupNav.Children.OfType<NavItem>().FirstOrDefault(n => n.IsChecked == true)?.Tag as string;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Group counts failed"); }
        finally { _loadingFilters = false; }
        if (!keepSelection) await ReloadAsync();
    }

    private static string MonthName(string key)
        => DateTime.TryParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("MMMM yyyy", CultureInfo.CurrentCulture) : key;

    private NavItem GroupItem(string text, string? key, string icon, int? count)
    {
        var n = new NavItem { Content = text, Icon = icon, Tag = key, Badge = count, GroupName = "LibGroup", Height = 32 }.Styled("Shotwise.NavListItem");
        n.Checked += (_, _) => { if (_loadingFilters) return; _group = key; _ = ReloadAsync(); };
        return n;
    }

    private LibraryQuery BuildQuery()
    {
        var q = new LibraryQuery { Text = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim() };
        q = _filter switch
        {
            "image" => q with { Kind = MediaKind.Image },
            "video" => q with { Kind = MediaKind.Video },
            "fav" => q with { FavoritesOnly = true },
            _ => q,
        };
        if ((_sort.SelectedItem as ComboBoxItem)?.Tag is LibrarySort sort) q = q with { Sort = sort };
        if (_group is { } key)
        {
            q = GroupBy switch
            {
                LibraryGroupBy.Date when DateOnly.TryParse(key, CultureInfo.InvariantCulture, out var d) => q with { From = d, To = d },
                LibraryGroupBy.Month when DateOnly.TryParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) => q with { From = m, To = m.AddMonths(1).AddDays(-1) },
                LibraryGroupBy.Year when int.TryParse(key, out var y) => q with { From = new DateOnly(y, 1, 1), To = new DateOnly(y, 12, 31) },
                LibraryGroupBy.App => q with { App = key },
                LibraryGroupBy.Website => q with { Website = key },
                LibraryGroupBy.Tag => q with { Tag = key },
                LibraryGroupBy.Kind when Enum.TryParse<MediaKind>(key, out var k) => q with { Kind = k },
                _ => q,
            };
        }
        return q with { Take = PageSize };
    }

    private void UpdateTitle()
    {
        var name = _filter switch
        {
            "image" => ES.T("Lib_Images", "Images"),
            "video" => ES.T("Lib_Videos", "Videos"),
            "steps" => ES.T("Lib_Steps", "Step Captures"),
            "fav" => ES.T("Lib_Favorites", "Favourites"),
            _ => ES.T("Lib_AllCaptures", "All captures"),
        };
        if (_group is { } g) name += " · " + (GroupBy == LibraryGroupBy.Month ? MonthName(g) : g);
        _title.Text = name;
    }

    private async Task ReloadAsync()
    {
        UpdateTitle();
        if (_ctx.Library is null)
        {
            _status.Text = ES.T("Lib_Unavailable", "The library is not available.");
            return;
        }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _baseQuery = BuildQuery();
        _loading = true;
        try
        {
            var list = await _ctx.Library.QueryAsync(_baseQuery, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (_filter == "steps") list = list.Where(i => i.IsStepCapture).ToList();
            _items.Clear();
            _items.AddRange(list);
            _endReached = list.Count < PageSize;
            _selected.IntersectWith(_items.Select(i => i.Id));
            RebuildRows();
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Library query failed");
            _status.Text = ex.Message;
        }
        finally { _loading = false; }
    }

    private async Task LoadMoreAsync()
    {
        if (_ctx.Library is null || _loading || _endReached) return;
        _loading = true;
        try
        {
            var more = await _ctx.Library.QueryAsync(_baseQuery with { Skip = _items.Count }, CancellationToken.None);
            _endReached = more.Count < PageSize;
            _items.AddRange(more);
            RebuildRows();
            UpdateStatus();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Library page failed"); }
        finally { _loading = false; }
    }

    private void UpdateStatus()
    {
        _status.Text = _items.Count == 0 ? ES.T("Lib_Empty", "No items match.") : ES.F("Lib_Count", "{0} items{1}", _items.Count, _selected.Count > 0 ? ES.F("Lib_Selected", ", {0} selected", _selected.Count) : "");
        _empty.Visibility = _items.Count == 0 && !_loading ? Visibility.Visible : Visibility.Collapsed;
        _rows.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2) _ = LoadMoreAsync();
    }

    // ------------------------------------------------------------------ grid

    private const double Gap = 16;
    private double TileWidth => _size.Value;
    private double ThumbHeight => Math.Round(_size.Value * 0.62);
    // card padding + thumbnail + title + caption: rows are at least this high and grow with font scaling (P3 P2-2)
    private double TileHeight => ThumbHeight + 64;

    private void RebuildRows()
    {
        var width = Math.Max(200, _rows.ActualWidth - 48 - 18);
        _columns = Math.Max(1, (int)((width + Gap) / (TileWidth + Gap)));
        var rows = new List<Row>();
        for (var i = 0; i < _items.Count; i += _columns) rows.Add(new Row(_items.Skip(i).Take(_columns).ToList()));
        var offset = _rows.Items.Count > 0 ? GetScrollViewer()?.VerticalOffset ?? 0 : 0;
        _rows.ItemsSource = rows;
        if (offset > 0) GetScrollViewer()?.ScrollToVerticalOffset(offset);
    }

    private ScrollViewer? GetScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(_rows) == 0) return null;
        var border = VisualTreeHelper.GetChild(_rows, 0);
        return border is Decorator { Child: ScrollViewer sv } ? sv : FindDescendant<ScrollViewer>(_rows);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } r) return r;
        }
        return null;
    }

    private FrameworkElement BuildRow(Row row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 4, 0, Gap - 4), MinHeight = TileHeight, VerticalAlignment = VerticalAlignment.Top };
        foreach (var item in row.Items) panel.Children.Add(BuildTile(item));
        return panel;
    }

    private FrameworkElement BuildTile(LibraryItem item)
    {
        var w = TileWidth;
        // rounded thumbnail
        var thumb = new Border { Height = ThumbHeight, CornerRadius = new CornerRadius(8), ClipToBounds = true };
        thumb.SetResourceReference(Border.BackgroundProperty, Tok.Surface2);
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
        var picture = new Border { CornerRadius = new CornerRadius(8), Background = brush };
        var host = new Grid { Height = ThumbHeight };
        host.Children.Add(thumb);
        host.Children.Add(picture);
        if (item.Kind is MediaKind.Video or MediaKind.Gif)
        {
            var dur = new StackPanel { Orientation = Orientation.Horizontal };
            var play = UiKit.Glyph("Icon.Play.Filled", 10);
            play.SetResourceReference(SwIcon.ForegroundProperty, Tok.HudText);
            dur.Children.Add(play);
            dur.Children.Add(new TextBlock { Text = (item.Kind == MediaKind.Gif ? "GIF " : "") + (item.Duration is { } d ? UiKit.FormatShortDuration(d) : ""), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0) }.Themed(TextBlock.ForegroundProperty, Tok.HudText));
            var badge = new Border { Child = dur, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8) };
            badge.SetResourceReference(Border.BackgroundProperty, Tok.Hud);
            host.Children.Add(badge);
        }
        if (item.IsFavorite)
            host.Children.Add(new SwIcon { Source = "Icon.Tool.Favorites.Color", Size = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) });

        // hover actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Opacity = 0 };
        Button Action(string glyph, string tip, Action click)
        {
            var b = UiKit.IconButton(glyph, tip, click, 14);
            b.Width = 28; b.Height = 28;
            b.Margin = new Thickness(3, 0, 0, 0);
            b.SetResourceReference(Control.BackgroundProperty, Tok.Surface1);
            b.SetResourceReference(Control.ForegroundProperty, Tok.Text);
            return b;
        }
        actions.Children.Add(Action(Icons.Glyph.OpenFile, ES.T("Lib_Open", "Open"), () => OpenRequested?.Invoke(item)));
        actions.Children.Add(Action(Icons.Glyph.Share, ES.T("Lib_Share", "Share…"), () => CommandRequested?.Invoke(new[] { item }, "share")));
        actions.Children.Add(Action(item.IsFavorite ? "Icon.Tool.Favorites.Color" : Icons.Glyph.Star, item.IsFavorite ? ES.T("Lib_Unfavorite", "Remove from favourites") : ES.T("Lib_Favorite", "Add to favourites"),
            () => UiKit.Fire(async () => { await _ctx.Library!.SetFavoriteAsync(item.Id, !item.IsFavorite); }, Report)));
        host.Children.Add(actions);

        var title = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        var meta = item.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + (item.SizeBytes > 0 ? " · " + UiKit.FormatSize(item.SizeBytes) : "") + (item.Width > 0 ? $" · {item.Width}×{item.Height}" : "");
        var sub = new TextBlock { Text = meta, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, Margin = new Thickness(2, 2, 2, 2) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        var sp = new StackPanel { Width = w - 16 };
        sp.Children.Add(host);
        sp.Children.Add(title);
        sp.Children.Add(sub);

        var shadow = new Border { CornerRadius = new CornerRadius(12), Opacity = 0 };
        shadow.SetResourceReference(Border.BackgroundProperty, Tok.Surface1);
        shadow.SetResourceReference(EffectProperty, Tok.ElevationCard);
        var border = new Border { Child = sp, Padding = new Thickness(8), CornerRadius = new CornerRadius(12), Tag = item, ToolTip = $"{item.Name}\n{item.Path}", BorderThickness = new Thickness(1.5) };
        var card = new Grid { Width = w, Margin = new Thickness(0, 0, Gap, 0), Tag = item };
        card.Children.Add(shadow);
        card.Children.Add(border);
        AutomationProperties.SetName(card, item.Name);
        ApplySelection(border);
        card.MouseEnter += (_, _) => { actions.Opacity = 1; shadow.Opacity = 1; };
        card.MouseLeave += (_, _) => { actions.Opacity = 0; shadow.Opacity = 0; };
        border.MouseLeftButtonDown += (_, e) => OnTileClick(item, e);
        border.MouseMove += (_, e) => OnTileMove(item, e);
        border.ContextMenu = BuildMenu(item);
        _ = LoadThumbAsync(item, brush);
        return card;
    }

    private void ApplySelection(Border b)
    {
        var selected = b.Tag is LibraryItem li && _selected.Contains(li.Id);
        b.SetResourceReference(Border.BackgroundProperty, selected ? Tok.AccentSubtle : Tok.Surface1);
        b.SetResourceReference(Border.BorderBrushProperty, selected ? Tok.Accent : Tok.BorderSubtle);
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var b in Descendants<Border>(_rows).Where(b => b.Tag is LibraryItem)) ApplySelection(b);
        UpdateStatus();
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

    private async Task LoadThumbAsync(LibraryItem item, ImageBrush target)
    {
        var path = item.ThumbnailPath;
        if (string.IsNullOrEmpty(path)) return;
        if (!_thumbs.TryGetValue(path, out var src))
        {
            src = await Task.Run(() => LoadBitmap(path));
            if (_thumbs.Count > 800) _thumbs.Clear();
            _thumbs[path] = src;
        }
        target.ImageSource = src;
    }

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 480;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ interaction

    private void OnTileClick(LibraryItem item, MouseButtonEventArgs e)
    {
        Focus();
        _dragCandidate = item;
        _dragOrigin = e.GetPosition(null);
        var ctrl = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        if (e.ClickCount == 2)
        {
            OpenRequested?.Invoke(item);
            e.Handled = true;
            return;
        }
        if (shift && _anchor is { } a)
        {
            var i0 = _items.FindIndex(x => x.Id == a);
            var i1 = _items.FindIndex(x => x.Id == item.Id);
            if (i0 >= 0 && i1 >= 0)
            {
                _selected.Clear();
                for (var i = Math.Min(i0, i1); i <= Math.Max(i0, i1); i++) _selected.Add(_items[i].Id);
            }
        }
        else if (ctrl)
        {
            if (!_selected.Remove(item.Id)) _selected.Add(item.Id);
            _anchor = item.Id;
        }
        else if (!_selected.Contains(item.Id))
        {
            _selected.Clear();
            _selected.Add(item.Id);
            _anchor = item.Id;
        }
        RefreshSelectionVisuals();
    }

    private LibraryItem? _dragCandidate;
    private Point _dragOrigin;

    private void OnTileMove(LibraryItem item, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        var items = _selected.Contains(item.Id) ? SelectedItems : new[] { item };
        var data = new DataObject();
        var sc = new System.Collections.Specialized.StringCollection();
        foreach (var i in items) sc.Add(i.Path);
        data.SetFileDropList(sc);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }

    private ContextMenu BuildMenu(LibraryItem item)
    {
        var menu = new ContextMenu();
        IReadOnlyList<LibraryItem> Targets() => _selected.Contains(item.Id) ? SelectedItems : new[] { item };
        MenuItem Mi(string header, Action<IReadOnlyList<LibraryItem>> act, string? gesture = null, string? glyph = null)
        {
            var m = new MenuItem { Header = header, InputGestureText = gesture };
            if (glyph is not null) m.Icon = UiKit.Glyph(glyph, 16);
            m.Click += (_, _) => act(Targets());
            return m;
        }
        menu.Items.Add(Mi(ES.T("Lib_Open", "Open"), t => OpenRequested?.Invoke(t[0]), "Enter", Icons.Glyph.OpenFile));
        menu.Items.Add(Mi(item.IsFavorite ? ES.T("Lib_Unfavorite", "Remove from favourites") : ES.T("Lib_Favorite", "Add to favourites"), t => UiKit.Fire(async () =>
        {
            foreach (var i in t) await _ctx.Library!.SetFavoriteAsync(i.Id, !item.IsFavorite);
        }, Report), null, Icons.Glyph.Star));
        menu.Items.Add(Mi(ES.T("Lib_Tags", "Tags…"), t => UiKit.Fire(() => EditTagsAsync(t), Report), null, Icons.Glyph.Tag));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(ES.T("Lib_CombineImages", "Combine Images…"), t => CommandRequested?.Invoke(t, "combineImages"), null, Icons.Glyph.Collections));
        menu.Items.Add(Mi(ES.T("Lib_CombineVideos", "Combine Videos…"), t => CommandRequested?.Invoke(t, "combineVideos"), null, Icons.Glyph.VideoClip));
        menu.Items.Add(Mi(ES.T("Lib_CreateGif", "Create GIF…"), t => CommandRequested?.Invoke(t, "createGif"), null, Icons.Glyph.Gif));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(ES.T("Lib_Reveal", "Show in Explorer"), t => Reveal(t[0]), null, Icons.Glyph.Folder));
        menu.Items.Add(Mi(ES.T("Lib_CopyPath", "Copy path"), t => { try { Clipboard.SetText(t[0].Path); } catch (System.Runtime.InteropServices.COMException) { } }, null, Icons.Glyph.Copy));
        menu.Items.Add(Mi(ES.T("Lib_ShareHistory", "Share history…"), t => CommandRequested?.Invoke(t, "shareHistory"), null, Icons.Glyph.History));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(ES.T("Lib_Delete", "Move to Recycle Bin"), t => UiKit.Fire(() => DeleteAsync(t), Report), "Del", Icons.Glyph.Delete));
        menu.Opened += (_, _) =>
        {
            var t = Targets();
            ((MenuItem)menu.Items[4]).IsEnabled = t.Count(i => i.Kind == MediaKind.Image) >= 2;
            ((MenuItem)menu.Items[5]).IsEnabled = t.Count(i => i.Kind is MediaKind.Video) >= 2;
            ((MenuItem)menu.Items[6]).IsEnabled = t.Count == 1 && t[0].Kind is MediaKind.Video or MediaKind.Gif || t.Count(i => i.Kind == MediaKind.Image) >= 2;
        };
        return menu;
    }

    private void Report(Exception ex) => _ = _ctx.ShowErrorAsync(ex);

    private static void Reveal(LibraryItem item)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private async Task EditTagsAsync(IReadOnlyList<LibraryItem> items)
    {
        if (_ctx.Library is null) return;
        var current = items.Count == 1 ? items[0].Tags : items.SelectMany(i => i.Tags).GroupBy(t => t).Where(g => g.Count() == items.Count).Select(g => g.Key).ToList();
        var all = await _ctx.Library.AllTagsAsync();
        var result = await _ctx.Dialogs.ShowTagEditor(new TagEditorRequest(current, all) { Owner = _ctx.ActiveWindow });
        if (result is null) return;
        var ids = items.Select(i => i.Id).ToList();
        if (result.Added.Count > 0) await _ctx.Library.AddTagsAsync(ids, result.Added);
        if (result.Removed.Count > 0) await _ctx.Library.RemoveTagsAsync(ids, result.Removed);
    }

    private async Task DeleteAsync(IReadOnlyList<LibraryItem> items)
    {
        if (_ctx.Library is null || items.Count == 0) return;
        if (_ctx.Settings.Current.Library.PromptBeforeDelete)
        {
            var r = await _ctx.Dialogs.Confirm(new ConfirmRequest(ES.T("Lib_DeleteTitle", "Move to Recycle Bin"),
                items.Count == 1 ? ES.F("Lib_DeleteOne", "Move \"{0}\" to the Recycle Bin?", items[0].Name) : ES.F("Lib_DeleteMany", "Move {0} items to the Recycle Bin?", items.Count),
                ES.T("Lib_Delete", "Move to Recycle Bin"), null, true, "Library.PromptBeforeDelete") { Owner = _ctx.ActiveWindow });
            if (!r.Confirmed) return;
            if (r.DontAskAgain) _ctx.Settings.Update(s => s.Library.PromptBeforeDelete = false);
        }
        await _ctx.Library.DeleteAsync(items.Select(i => i.Id), true);
        _selected.Clear();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_search.IsKeyboardFocused) return;
        var sel = SelectedItems;
        switch (e.Key)
        {
            case Key.Enter when sel.Count > 0: OpenRequested?.Invoke(sel[0]); e.Handled = true; break;
            case Key.Delete when sel.Count > 0: UiKit.Fire(() => DeleteAsync(sel), Report); e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control:
                _selected.Clear();
                foreach (var i in _items) _selected.Add(i.Id);
                RefreshSelectionVisuals();
                e.Handled = true;
                break;
            case Key.F5: _ = ReloadAsync(); e.Handled = true; break;
        }
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }
}
