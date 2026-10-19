using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core.Annotations;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>Shared quick-style operations (used by the Styles panel, the properties panel and context menus).</summary>
internal static class QuickStyleActions
{
    /// <summary>Saves the current selection's look (or the tool's current style) as a new quick style in the active theme.</summary>
    public static async Task AddFromSelectionAsync(EditorContext ctx, EditorSession s)
    {
        if (ctx.Styles is null) return;
        var tool = s.ActiveTool;
        var sel = s.SelectedAnnotations();
        if (sel.Count > 0) tool = StyleApplier.ToolFor(sel[^1].Kind);
        var name = await InputDialog.AskAsync(ctx.ActiveWindow, ES.T("Styles_AddTitle", "Add to Quick Styles"), ES.T("Styles_NamePrompt", "Name for the new quick style"), ToolInfo.Name(tool) + " " + (ctx.Styles.ActiveTheme.StylesFor(tool).Count() + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        QuickStyle? qs = null;
        if (sel.Count > 0 && ToolInfo.IsAnnotationTool(tool)) qs = s.CreateQuickStyleFromSelection(name);
        if (qs is null) qs = FromTool(s, tool, name);
        if (qs is null) return;
        qs.Name = name;
        qs.Id = Guid.NewGuid().ToString("N");
        qs.IsBuiltIn = false;
        var theme = ctx.Styles.ActiveTheme;
        theme.QuickStyles.Add(qs);
        await ctx.Styles.SaveThemeAsync(theme);
    }

    public static QuickStyle? FromTool(EditorSession s, ToolKind tool, string name)
    {
        if (ToolInfo.IsAnnotationTool(tool))
        {
            var proto = s.CreatePrototype(tool);
            return proto is null ? null : new QuickStyle { Tool = tool, Name = name, Prototype = proto };
        }
        var json = s.ToolOptions.ToJson(tool);
        return json is null ? null : new QuickStyle { Tool = tool, Name = name, Options = json };
    }
}

/// <summary>
/// "Quick Styles" section of the right panel (DESIGN-SYSTEM §4): PanelHeader with the theme picker and a "…" menu (add current,
/// import / export / restore), a 4-column grid of <see cref="QuickStyleTile"/>s whose thumbnails are rendered from each quick
/// style's prototype annotation through <see cref="Shotwise.Core.Documents.IDocumentRenderer"/> (cached by
/// <see cref="PreviewRenderer"/>), favourites (star), rename / delete, the Favorites view and the theme colours.
/// </summary>
internal sealed class StylesPanel : StackPanel
{
    private const double TileSize = 60;

    private readonly EditorContext _ctx;
    private readonly PreviewRenderer _previews;
    private readonly ComboBox _themes = new() { MinWidth = 128, MaxWidth = 150, FontSize = 12 };
    private readonly TextBlock _caption = new() { Margin = new Thickness(16, 0, 16, 4) };
    private readonly ListBox _gallery = new() { MaxHeight = 3 * (TileSize + 8) + 10, SelectionMode = SelectionMode.Single, Margin = new Thickness(8, 0, 8, 0) };
    private readonly WrapPanel _colors = new() { Margin = new Thickness(0, 0, 0, 0) };
    private readonly StackPanel _colorsRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 6, 12, 4) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4, 16, 8), FontSize = 12 };
    private readonly ILogger _log;
    private EditorSession? _s;
    private ImageEditorDocument? _doc;
    private bool _loadingThemes;
    private bool _queued;
    private string? _lastUsedId;

    public StylesPanel(EditorContext ctx, PreviewRenderer previews)
    {
        _ctx = ctx;
        _previews = previews;
        _log = ctx.Log<StylesPanel>();

        AutomationProperties.SetName(_themes, ES.T("Styles_Theme", "Theme"));
        _themes.ToolTip = ES.T("Styles_ThemeTip", "Style theme");
        var more = UiKit.IconButton(Icons.Glyph.More, ES.T("Styles_More", "Quick style options"), () => { }, 16);
        more.Width = 28; more.Height = 28; more.Margin = new Thickness(4, 0, 0, 0);
        more.Click += (_, _) => ShowOptions(more);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_themes);
        actions.Children.Add(more);
        Children.Add(new PanelHeader
        {
            Content = ES.T("Tab_Styles", "Quick Styles"),
            Actions = actions,
            Padding = new Thickness(16, 12, 10, 6),
        });
        _caption.Styled("Shotwise.Text.Caption");
        Children.Add(_caption);

        _gallery.Styled("Shotwise.TileList");
        AutomationProperties.SetName(_gallery, ES.T("Tab_Styles", "Quick Styles"));
        ScrollViewer.SetVerticalScrollBarVisibility(_gallery, ScrollBarVisibility.Auto);
        _gallery.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && _gallery.SelectedItem is ListBoxItem { Tag: QuickStyle qs }) { Use(qs); e.Handled = true; }
        };
        Children.Add(_gallery);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        Children.Add(_empty);

        var colorsLabel = new TextBlock { Text = ES.T("Styles_ThemeColours", "Theme colours"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 12 };
        colorsLabel.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        _colorsRow.Children.Add(colorsLabel);
        _colorsRow.Children.Add(_colors);
        Children.Add(_colorsRow);
        Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0) }.Styled("Shotwise.Divider"));

        _themes.SelectionChanged += (_, _) =>
        {
            if (_loadingThemes || _themes.SelectedItem is not ComboBoxItem { Tag: Theme t } || _ctx.Styles is null) return;
            UiKit.Fire(async () => { await _ctx.Styles.SetActiveThemeAsync(t.Id); }, Report);
        };
        if (_ctx.Styles is not null) _ctx.Styles.Changed += OnStylesChanged;
        _ = LoadThemesAsync();
    }

    private void OnStylesChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { _previews.Clear(); _ = LoadThemesAsync(); Rebuild(); });

    /// <summary>Real window closure (R5-#5): unsubscribes from the shared style repository.</summary>
    public void ReleaseServices()
    {
        if (_ctx.Styles is not null) _ctx.Styles.Changed -= OnStylesChanged;
        Bind(null);
    }

    public void Bind(ImageEditorDocument? doc)
    {
        if (ReferenceEquals(doc, _doc)) return;
        if (_s is not null)
        {
            _s.ActiveToolChanged -= OnChanged;
            _s.SelectionChanged -= OnChanged;
            _s.ToolOptionsChanged -= OnChanged;
        }
        _doc = doc;
        _s = doc?.Session;
        if (_s is not null)
        {
            _s.ActiveToolChanged += OnChanged;
            _s.SelectionChanged += OnChanged;
        }
        Rebuild();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(() => { _queued = false; Rebuild(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Report(Exception ex) => _ = _ctx.ShowErrorAsync(ex);

    /// <summary>Re-renders the tiles (after a Light/Dark switch the thumbnails change their backdrop).</summary>
    public void Refresh() => Rebuild();

    private async Task LoadThemesAsync()
    {
        if (_ctx.Styles is null) return;
        var themes = await _ctx.Styles.GetThemesAsync();
        _loadingThemes = true;
        try
        {
            _themes.Items.Clear();
            foreach (var t in themes) _themes.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t });
            for (var i = 0; i < _themes.Items.Count; i++)
                if (_themes.Items[i] is ComboBoxItem { Tag: Theme t } && t.Id == _ctx.Styles.ActiveTheme.Id) _themes.SelectedIndex = i;
        }
        finally { _loadingThemes = false; }
    }

    private ToolKind CurrentTool()
    {
        if (_s is null) return ToolKind.Select;
        var tool = _s.ActiveTool;
        if (tool is ToolKind.Select or ToolKind.Move && _s.SelectedAnnotations() is { Count: > 0 } sel) tool = StyleApplier.ToolFor(sel[^1].Kind);
        return tool;
    }

    private void Rebuild()
    {
        _gallery.Items.Clear();
        _colors.Children.Clear();
        _empty.Text = "";
        _empty.Visibility = Visibility.Collapsed;
        _gallery.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        if (_s is null || _ctx.Styles is null)
        {
            _caption.Text = "";
            _empty.Text = ES.T("Styles_None", "Open an image to use quick styles.");
            _empty.Visibility = Visibility.Visible;
            _gallery.Visibility = Visibility.Collapsed;
            _colorsRow.Visibility = Visibility.Collapsed;
            return;
        }
        var theme = _ctx.Styles.ActiveTheme;
        var tool = CurrentTool();
        // Stamps have no meaningful quick styles (the stamp browser is the primary content): hide Quick Styles and Theme colours.
        Visibility = tool == ToolKind.Stamp ? Visibility.Collapsed : Visibility.Visible;
        var isFav = tool == ToolKind.Favorites;
        var styles = (isFav ? theme.Favorites : theme.StylesFor(tool)).ToList();
        _caption.Text = isFav ? ES.F("Styles_FavCount", "Favorites · {0}", styles.Count) : ES.F("Styles_ToolCount", "{0} · {1} styles", ToolInfo.Name(tool), styles.Count);
        foreach (var style in styles) _gallery.Items.Add(Tile(style, theme));
        if (styles.Count == 0)
        {
            _gallery.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
            _empty.Text = isFav ? ES.T("Styles_NoFavorites", "No favourites yet. Click the star on a quick style to add it here.")
                : ToolInfo.IsAnnotationTool(tool) ? ES.T("Styles_NoneForTool", "No quick styles for this tool.") : ES.F("Styles_NotForTool", "{0} has no quick styles. Pick a drawing tool to see its styles.", ToolInfo.Name(tool));
        }
        var showColors = ToolInfo.IsAnnotationTool(tool) && theme.ThemeColors.Count > 0;
        _colorsRow.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in theme.ThemeColors.Take(8))
        {
            var color = c;
            var dot = new Border { Background = color.ToBrush(), CornerRadius = new CornerRadius(9), Width = 18, Height = 18, BorderThickness = new Thickness(1) };
            dot.SetResourceReference(Border.BorderBrushProperty, Tok.BorderStrong);
            var b = new Button { ToolTip = ES.F("Styles_ApplyColour", "Use {0}", color.ToRgbHex()), Content = dot }.Styled("Shotwise.SwatchCell");
            AutomationProperties.SetName(b, ES.F("Styles_ApplyColour", "Use {0}", color.ToRgbHex()));
            b.Click += (_, _) => ApplyThemeColor(tool, color);
            _colors.Children.Add(b);
        }
    }

    private void ApplyThemeColor(ToolKind tool, Argb color)
    {
        if (_s is null || !ToolInfo.IsAnnotationTool(tool)) return;
        _s.ChangeStyleProperty(tool, a =>
        {
            if (a is StepAnnotation or BlurAnnotation or SimplifyShapeAnnotation or ShapeAnnotation { Fill.Kind: FillKind.Solid } or HighlightAnnotation) a.Fill = FillStyle.Solid(color);
            if (a.Stroke.IsVisible || a is not StepAnnotation) a.Stroke = a.Stroke with { Color = color };
            if (a is HighlightAnnotation) a.Stroke = a.Stroke with { Color = color };
        }, "Theme colour");
    }

    private void Use(QuickStyle style)
    {
        if (_s is null) return;
        _lastUsedId = style.Id;
        if (style.Tool == ToolKind.Favorites || CurrentTool() == ToolKind.Favorites) _s.UseFavorite(style); else _s.UseQuickStyle(style);
    }

    private ListBoxItem Tile(QuickStyle style, Theme theme)
    {
        var tile = new QuickStyleTile
        {
            TileSize = TileSize,
            Thumbnail = _previews.QuickStyle(style, 50, 50),
            IsFavorite = style.IsFavorite,
            Title = style.Name,
        };
        var host = new Grid();
        host.Children.Add(tile);
        if (tile.Thumbnail is null)
        {
            // no prototype (tool-option styles): show the tool icon + name instead of an empty tile
            var fallback = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            fallback.Children.Add(Icons.ForTool(style.Tool, 22));
            fallback.Children.Add(new TextBlock { Text = style.Name, FontSize = 9.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 52, HorizontalAlignment = HorizontalAlignment.Center }.Themed(TextBlock.ForegroundProperty, Tok.Text2));
            host.Children.Add(fallback);
        }
        // hover star (toggle favourite); the tile itself shows the colour star for favourites
        var star = new ToggleButton
        {
            Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 0),
            IsChecked = style.IsFavorite, ToolTip = style.IsFavorite ? ES.T("Styles_Unfavorite", "Remove from favourites") : ES.T("Styles_Favorite", "Add to favourites"),
            Opacity = 0, Focusable = false,
        }.Styled("Shotwise.IconButton");
        SwUi.SetIcon(star, style.IsFavorite ? "Icon.Tool.Favorites.Color" : Icons.Glyph.Star);
        SwUi.SetIconSize(star, 13);
        AutomationProperties.SetName(star, ES.T("Styles_Favorite", "Favourite"));
        host.MouseEnter += (_, _) => star.Opacity = 1;
        host.MouseLeave += (_, _) => star.Opacity = 0;
        star.Click += (_, _) => UiKit.Fire(() => ToggleFavoriteAsync(style, theme), Report);
        host.Children.Add(star);

        var item = new ListBoxItem { Content = host, Tag = style };
        AutomationProperties.SetName(item, style.Name);
        item.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, star)) return;
            Use(style);
        };
        if (style.Id == _lastUsedId) item.IsSelected = true;

        var menu = new ContextMenu();
        var use = new MenuItem { Header = ES.T("Styles_Use", "Use this style"), Icon = UiKit.Glyph(Icons.Glyph.Accept, 16) };
        use.Click += (_, _) => Use(style);
        var fav = new MenuItem { Header = style.IsFavorite ? ES.T("Styles_Unfavorite", "Remove from favourites") : ES.T("Styles_Favorite", "Add to favourites"), Icon = UiKit.Glyph(Icons.Glyph.Star, 16) };
        fav.Click += (_, _) => UiKit.Fire(() => ToggleFavoriteAsync(style, theme), Report);
        var rename = new MenuItem { Header = ES.T("Styles_Rename", "Rename…"), Icon = UiKit.Glyph(Icons.Glyph.Edit, 16) };
        rename.Click += (_, _) => UiKit.Fire(async () =>
        {
            var n = await InputDialog.AskAsync(_ctx.ActiveWindow, ES.T("Styles_Rename", "Rename…"), ES.T("Styles_NamePrompt", "Name for the new quick style"), style.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            style.Name = n;
            await _ctx.Styles!.SaveThemeAsync(theme);
        }, Report);
        var delete = new MenuItem { Header = ES.T("Styles_Delete", "Delete"), Icon = UiKit.Glyph(Icons.Glyph.Delete, 16) };
        delete.Click += (_, _) => UiKit.Fire(async () =>
        {
            theme.QuickStyles.RemoveAll(q => q.Id == style.Id);
            await _ctx.Styles!.SaveThemeAsync(theme);
        }, Report);
        menu.Items.Add(use);
        menu.Items.Add(fav);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        item.ContextMenu = menu;
        return item;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var cur = d; cur is not null; cur = cur is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, container)) return true;
        return false;
    }

    private async Task ToggleFavoriteAsync(QuickStyle style, Theme theme)
    {
        style.IsFavorite = !style.IsFavorite;
        await _ctx.Styles!.SaveThemeAsync(theme);
    }

    private void ShowOptions(UIElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var add = new MenuItem { Header = ES.T("Styles_Add", "Add current style"), Icon = UiKit.Glyph(Icons.Glyph.Add, 16), IsEnabled = _s is not null };
        add.Click += (_, _) => UiKit.Fire(AddCurrentAsync, Report);
        var import = new MenuItem { Header = ES.T("Styles_Import", "Import theme…"), Icon = UiKit.Glyph(Icons.Glyph.Download, 16) };
        import.Click += (_, _) => UiKit.Fire(ImportThemeAsync, Report);
        var export = new MenuItem { Header = ES.T("Styles_Export", "Export theme…"), Icon = UiKit.Glyph(Icons.Glyph.Export, 16) };
        export.Click += (_, _) => UiKit.Fire(ExportThemeAsync, Report);
        var restore = new MenuItem { Header = ES.T("Styles_Restore", "Restore defaults"), Icon = UiKit.Glyph(Icons.Glyph.Refresh, 16) };
        restore.Click += (_, _) => UiKit.Fire(RestoreAsync, Report);
        m.Items.Add(add);
        m.Items.Add(new Separator());
        m.Items.Add(import);
        m.Items.Add(export);
        m.Items.Add(restore);
        m.IsOpen = true;
    }

    private Task AddCurrentAsync() => _s is null ? Task.CompletedTask : QuickStyleActions.AddFromSelectionAsync(_ctx, _s);

    private async Task RestoreAsync()
    {
        if (_ctx.Styles is null) return;
        var r = await _ctx.Dialogs.Confirm(new Shotwise.UI.Common.Dialogs.ConfirmRequest(ES.T("Styles_Restore", "Restore defaults"), ES.T("Styles_RestoreConfirm", "Restore the built-in themes and quick styles? Your own themes are kept."), ES.T("Styles_Restore", "Restore defaults"), null, true) { Owner = _ctx.ActiveWindow });
        if (r.Confirmed) await _ctx.Styles.RestoreDefaultsAsync();
    }

    private async Task ImportThemeAsync()
    {
        if (_ctx.Styles is null) return;
        var dlg = new OpenFileDialog { Filter = ES.T("Styles_ThemeFilter", "Shotwise themes (*.shottheme)|*.shottheme|All files|*.*") };
        if (dlg.ShowDialog(_ctx.ActiveWindow) != true) return;
        var t = await _ctx.Styles.ImportAsync(dlg.FileName);
        await _ctx.Styles.SetActiveThemeAsync(t.Id);
    }

    private async Task ExportThemeAsync()
    {
        if (_ctx.Styles is null) return;
        var theme = _ctx.Styles.ActiveTheme;
        var dlg = new SaveFileDialog { Filter = ES.T("Styles_ThemeFilter", "Shotwise themes (*.shottheme)|*.shottheme|All files|*.*"), FileName = theme.Name + ".shottheme" };
        if (dlg.ShowDialog(_ctx.ActiveWindow) != true) return;
        await _ctx.Styles.ExportAsync(theme, dlg.FileName);
    }
}
