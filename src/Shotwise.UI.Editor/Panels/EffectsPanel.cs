using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Editor;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// Effects panel mode (EFX-01..15, DESIGN-SYSTEM §4): Quick Effects and every effect as cards with an icon and a live thumbnail of
/// the current image with the effect applied (rendered off the UI thread, cached per document revision). Clicking a card edits the
/// effect in the effect dialog (live preview) and applies it; "+" adds it to the stack, which can be applied or saved as a Quick Effect.
/// </summary>
internal sealed class EffectsPanel : ScrollViewer
{
    private const int ThumbW = 128, ThumbH = 80;

    private readonly EditorContext _ctx;
    private readonly ILogger _log;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 4, 16, 20) };
    private readonly StackPanel _stackList = new();
    private readonly UniformGrid _quickGrid = new() { Columns = 2 };
    private readonly UniformGrid _catalogGrid = new() { Columns = 2 };
    private readonly WrapPanel _stackActions = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<EffectSpec> _stack = new();
    private readonly Dictionary<string, Image> _thumbTargets = new();
    private ImageEditorDocument? _doc;
    private EditorSession? _s;
    private long _previewRevision = -1;
    private object? _previewDoc;
    private CancellationTokenSource? _previewCts;

    public EffectsPanel(EditorContext ctx)
    {
        _ctx = ctx;
        _log = ctx.Log<EffectsPanel>();
        Content = _root;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        Build();
        ctx.QuickEffects.Changed += OnQuickEffectsChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) QueuePreviews(); };
    }

    private void OnQuickEffectsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { RefreshQuick(); _previewRevision = -1; QueuePreviews(); });

    /// <summary>Real window closure (R5-#5): unsubscribes from the shared quick-effect store.</summary>
    public void ReleaseServices()
    {
        _ctx.QuickEffects.Changed -= OnQuickEffectsChanged;
        _previewCts?.Cancel();
        _previewDebounce?.Stop();
        Bind(null);
    }

    public void Bind(ImageEditorDocument? doc)
    {
        if (_s is not null) { _s.BusyChanged -= OnBusy; _s.Undo.Changed -= OnUndo; }
        _doc = doc;
        _s = doc?.Session;
        if (_s is not null) { _s.BusyChanged += OnBusy; _s.Undo.Changed += OnUndo; }
        IsEnabled = doc is not null;
        QueuePreviews();
    }

    private void OnBusy(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => IsEnabled = _doc is not null && _s is { IsBusy: false });

    private System.Windows.Threading.DispatcherTimer? _previewDebounce;

    /// <summary>Edits (slider drags…) refresh the live thumbnails at most every 600 ms, and only while the panel is visible.</summary>
    private void OnUndo(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsVisible) return;
        _previewDebounce ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _previewDebounce.Stop();
        _previewDebounce.Tick -= OnPreviewDebounce;
        _previewDebounce.Tick += OnPreviewDebounce;
        _previewDebounce.Start();
    });

    private void OnPreviewDebounce(object? sender, EventArgs e)
    {
        _previewDebounce?.Stop();
        QueuePreviews();
    }

    private static readonly (string Key, string Name, string Icon, string Tint, Func<EffectSpec> Make)[] Catalog =
    {
        ("Border", "Border", "Icon.Window.Regular", "accent", () => EffectDefaults.Border),
        ("Edges", "Edges", "Icon.Tool.CutOut.Regular", "accent", () => EffectDefaults.Edge),
        ("Shadow", "Shadow", "Icon.Shadow.Regular", "accent", () => EffectDefaults.Shadow),
        ("Perspective", "Perspective and shear", "Icon.Resize.Regular", "accent", () => EffectDefaults.Perspective),
        ("PageCurl", "Page curl", "Icon.Document.Regular", "accent", () => EffectDefaults.PageCurl),
        ("ColorAdjust", "Colour adjustment", "Icon.Color.Regular", "success", () => EffectDefaults.ColorAdjust),
        ("ColorReplace", "Colour replacement", "Icon.Tool.Fill.Regular", "success", () => EffectDefaults.ColorReplace),
        ("Filter", "Filters", "Icon.Filter.Regular", "success", () => EffectDefaults.Filter),
        ("Transparency", "Set transparency", "Icon.Tool.Eraser.Regular", "success", () => EffectDefaults.Transparency),
        ("Watermark", "Watermark", "Icon.Image.Regular", "info", () => EffectDefaults.Watermark),
        ("Caption", "Capture info", "Icon.Info.Regular", "info", () => EffectDefaults.Caption),
        ("Spotlight", "Spotlight and magnify (selection)", "Icon.Tool.Magnify.Regular", "warning", () => new SpotlightMagnifyEffect(default, 0.55, 6, 1.5)),
        ("Resize", "Resize", "Icon.Resize.Regular", "neutral", () => EffectDefaults.Resize),
        ("Rotate", "Rotate and flip", "Icon.Reset.Regular", "neutral", () => EffectDefaults.Rotate),
        ("Trim", "Trim", "Icon.Tool.Crop.Regular", "neutral", () => EffectDefaults.Trim),
    };

    private static (string Bg, string Fg) Tint(string tint) => tint switch
    {
        "accent" => ("Shotwise.AccentSubtleBrush", "Shotwise.AccentTextBrush"),
        "success" => ("Shotwise.SuccessSubtleBrush", "Shotwise.SuccessTextBrush"),
        "info" => ("Shotwise.InfoSubtleBrush", "Shotwise.AccentTextBrush"),
        "warning" => ("Shotwise.WarningSubtleBrush", "Shotwise.WarningTextBrush"),
        _ => ("Shotwise.Surface2Brush", "Shotwise.TextSecondaryBrush"),
    };

    /// <summary>Effects whose preview needs a selection or that change the size of the whole image are shown with their icon only.</summary>
    private static bool Previewable(string key) => key is not ("Spotlight" or "Resize" or "Trim");

    public static string NameOf(EffectSpec e) => e switch
    {
        BorderEffect => ES.T("Effect_Border", "Border"),
        EdgeEffect ed => ES.F("Effect_EdgesOf", "Edges ({0})", ed.Style),
        ShadowEffect => ES.T("Effect_Shadow", "Shadow"),
        PerspectiveEffect p => p.Shear ? ES.T("Effect_Shear", "Shear") : ES.T("Effect_Perspective", "Perspective"),
        PageCurlEffect => ES.T("Effect_PageCurl", "Page curl"),
        ColorAdjustEffect => ES.T("Effect_ColorAdjust", "Colour adjustment"),
        ColorReplaceEffect => ES.T("Effect_ColorReplace", "Colour replacement"),
        FilterEffect f => ES.F("Effect_FilterOf", "Filter ({0})", f.Kind),
        WatermarkEffect => ES.T("Effect_Watermark", "Watermark"),
        CaptionEffect => ES.T("Effect_Caption", "Capture info"),
        ResizeEffect => ES.T("Effect_Resize", "Resize"),
        RotateEffect r => r.FlipH || r.FlipV ? ES.T("Effect_Flip", "Flip") : ES.F("Effect_RotateOf", "Rotate {0}°", Math.Round(r.Degrees)),
        TrimEffect => ES.T("Effect_Trim", "Trim"),
        SpotlightMagnifyEffect => ES.T("Effect_Spotlight", "Spotlight and magnify"),
        TransparencyEffect => ES.T("Effect_Transparency", "Transparency"),
        _ => e.GetType().Name,
    };

    private void Build()
    {
        _root.Children.Add(UiKit.Header(ES.T("Effects_Quick", "Quick Effects")));
        _root.Children.Add(_quickGrid);
        _root.Children.Add(UiKit.Header(ES.T("Effects_Add", "Add an effect")));
        _root.Children.Add(UiKit.Hint(ES.T("Effects_AddHint", "Click a card to adjust the effect with a live preview and apply it; + adds it to the stack.")));
        foreach (var (key, name, icon, tint, make) in Catalog)
            _catalogGrid.Children.Add(EffectCard("fx:" + key, ES.T("EffectName_" + key, name), icon, tint,
                () => UiKit.Fire(() => ApplyNowAsync(make()), Report),
                () => UiKit.Fire(() => AddToStackAsync(make()), Report), null));
        _root.Children.Add(_catalogGrid);
        _root.Children.Add(UiKit.Header(ES.T("Effects_Stack", "Effect stack")));
        _root.Children.Add(_stackList);
        _stackActions.Children.Add(UiKit.PrimaryButton(ES.T("Effects_ApplyStack", "Apply stack"), Icons.Glyph.Accept, () => UiKit.Fire(ApplyStackAsync, Report)));
        _stackActions.Children.Add(UiKit.TextButton(ES.T("Effects_SaveQuick", "Save as Quick Effect…"), () => UiKit.Fire(SaveQuickAsync, Report)));
        _stackActions.Children.Add(UiKit.GhostButton(ES.T("Effects_Clear", "Clear"), Icons.Glyph.Delete, () => { _stack.Clear(); RefreshStack(); }));
        _root.Children.Add(_stackActions);
        RefreshStack();
        RefreshQuick();
    }

    /// <summary>Card: live thumbnail (or tinted icon), name, tinted icon tile; optional "+" (add to stack) and context menu.</summary>
    private FrameworkElement EffectCard(string previewKey, string name, string icon, string tint, Action click, Action? add, ContextMenu? menu)
    {
        var (bg, fg) = Tint(tint);
        var thumbHost = new Grid { Height = ThumbH, ClipToBounds = true };
        var thumbBg = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        thumbBg.SetResourceReference(Border.BackgroundProperty, bg);
        thumbBg.SetResourceReference(Border.BorderBrushProperty, Tok.BorderSubtle);
        thumbHost.Children.Add(thumbBg);
        var bigIcon = UiKit.Glyph(icon, 28);
        bigIcon.SetResourceReference(SwIcon.ForegroundProperty, fg);
        thumbHost.Children.Add(bigIcon);
        var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6), Visibility = Visibility.Collapsed };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        thumbHost.Children.Add(img);
        _thumbTargets[previewKey] = img;
        var badge = Icons.Tile(icon, bg, fg, 22, 14);
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        badge.VerticalAlignment = VerticalAlignment.Bottom;
        badge.Margin = new Thickness(6);
        thumbHost.Children.Add(badge);

        var label = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(2, 6, 2, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text);
        var body = new StackPanel();
        body.Children.Add(thumbHost);
        body.Children.Add(label);

        var button = new Button { Content = body, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = name }.Styled("Shotwise.GhostButton");
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SwUi.SetCornerRadius(button, new CornerRadius(10));
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        if (menu is not null) button.ContextMenu = menu;

        var card = new Grid { Margin = new Thickness(0, 0, 4, 6) };
        card.Children.Add(button);
        if (add is not null)
        {
            var plus = UiKit.IconButton(Icons.Glyph.Add, ES.F("Effects_AddToStackTip", "Add {0} to the stack", name), add, 14);
            plus.Width = 26; plus.Height = 26;
            plus.HorizontalAlignment = HorizontalAlignment.Right;
            plus.VerticalAlignment = VerticalAlignment.Top;
            plus.Margin = new Thickness(0, 8, 8, 0);
            plus.SetResourceReference(Control.BackgroundProperty, Tok.Surface1);
            plus.Opacity = 0;
            card.MouseEnter += (_, _) => plus.Opacity = 1;
            card.MouseLeave += (_, _) => plus.Opacity = 0;
            plus.GotKeyboardFocus += (_, _) => plus.Opacity = 1;
            card.Children.Add(plus);
        }
        return card;
    }

    private void Report(Exception ex) => _ = _ctx.ShowErrorAsync(ex);

    // ------------------------------------------------------------------ live previews

    private void QueuePreviews()
    {
        if (!IsVisible || _doc is null || _s is null) return;
        var revision = _doc.Revision;
        if (ReferenceEquals(_previewDoc, _doc) && revision == _previewRevision) return;
        _previewDoc = _doc;
        _previewRevision = revision;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        BgraImage? source;
        try
        {
            // the session belongs to the UI thread: render here, apply the effects off-thread
            var full = _ctx.Renderer.RenderPage(_doc.Document, _s.ActivePage, new RenderOptions(1, true));
            source = Math.Max(full.Width, full.Height) > 320 ? ImageOps.Downscale(full, 320) : full;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Effect preview source failed"); return; }
        var jobs = Catalog.Where(c => Previewable(c.Key)).Select(c => ("fx:" + c.Key, (IReadOnlyList<EffectSpec>)new[] { c.Make() })).ToList();
        jobs.AddRange(_ctx.QuickEffects.Items.Select(q => ("qe:" + q.Id, (IReadOnlyList<EffectSpec>)q.Effects)));
        var fx = _ctx.Effects;
        var md = _doc.Document.Metadata;
        UiKit.Fire(async () =>
        {
            foreach (var (key, effects) in jobs)
            {
                if (cts.IsCancellationRequested) return;
                var result = await Task.Run(() =>
                {
                    try
                    {
                        var img = source;
                        foreach (var e in effects) img = fx.Apply(img, e, md).Image;
                        return Math.Max(img.Width, img.Height) > 256 ? ImageOps.Downscale(img, 256) : img;
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Effect preview {Key} failed", key); return null; }
                }, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (result is not null && _thumbTargets.TryGetValue(key, out var target))
                {
                    target.Source = result.ToBitmapSource();
                    target.Visibility = Visibility.Visible;
                    if (target.Parent is Grid g) foreach (var c in g.Children.OfType<SwIcon>()) c.Visibility = Visibility.Collapsed;
                }
            }
        }, ex => _log.LogDebug(ex, "Effect previews failed"));
    }

    // ------------------------------------------------------------------ actions

    private BgraImage? PreviewSource()
    {
        if (_doc is null || _s is null) return null;
        try
        {
            var img = _ctx.Renderer.RenderPage(_doc.Document, _s.ActivePage, new RenderOptions(1, true));
            return Math.Max(img.Width, img.Height) > 1280 ? ImageOps.Downscale(img, 1280) : img;
        }
        catch (Exception) { return null; }
    }

    private async Task<EffectSpec?> EditAsync(EffectSpec initial)
    {
        // Effects with their own first-class dialog.
        if (initial is ResizeEffect && _s is not null)
        {
            var r = await _ctx.Dialogs.ShowResizeImage(new ResizeImageRequest(_s.ActivePage.CanvasSize, _s.ActivePage.Dpi) { Owner = _ctx.ActiveWindow });
            return r?.Resize;
        }
        var result = await _ctx.Dialogs.ShowEffectEditor(new EffectEditorRequest(initial, PreviewSource()) { Owner = _ctx.ActiveWindow });
        return result?.Effect;
    }

    private async Task AddToStackAsync(EffectSpec initial)
    {
        var e = await EditAsync(initial);
        if (e is null) return;
        _stack.Add(e);
        RefreshStack();
    }

    private async Task ApplyNowAsync(EffectSpec initial)
    {
        if (_s is null) return;
        var e = await EditAsync(initial);
        if (e is null) return;
        await _s.ApplyEffectAsync(e);
    }

    private async Task ApplyStackAsync()
    {
        if (_s is null || _stack.Count == 0) return;
        foreach (var e in _stack.ToList()) await _s.ApplyEffectAsync(e);
        _stack.Clear();
        RefreshStack();
    }

    private async Task SaveQuickAsync()
    {
        if (_stack.Count == 0)
        {
            await _ctx.InfoAsync(ES.T("Effects_Quick", "Quick Effects"), ES.T("Effects_StackEmpty", "Add at least one effect to the stack first."));
            return;
        }
        var name = await InputDialog.AskAsync(_ctx.ActiveWindow, ES.T("Effects_SaveQuick", "Save as Quick Effect…"), ES.T("Effects_QuickName", "Name for the Quick Effect"), _stack.Count == 1 ? NameOf(_stack[0]) : ES.T("Effects_Combined", "Combined effect"));
        if (string.IsNullOrWhiteSpace(name)) return;
        _ctx.QuickEffects.Add(name, _stack.ToList());
    }

    private void RefreshStack()
    {
        _stackList.Children.Clear();
        _stackActions.Visibility = _stack.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_stack.Count == 0)
        {
            var empty = new Border { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = UiKit.Hint(ES.T("Effects_StackHint", "Effects you add appear here and are applied in order.")) };
            empty.SetResourceReference(Border.BorderBrushProperty, Tok.BorderStrong);
            ((TextBlock)empty.Child).Margin = new Thickness(0);
            _stackList.Children.Add(empty);
            return;
        }
        for (var i = 0; i < _stack.Count; i++)
        {
            var index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4), MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bg = new Border { CornerRadius = new CornerRadius(8) };
            bg.SetResourceReference(Border.BackgroundProperty, Tok.Surface2);
            Grid.SetColumnSpan(bg, 6);
            row.Children.Add(bg);
            var num = UiKit.Chip((i + 1).ToString(), accent: true);
            num.Margin = new Thickness(8, 0, 8, 0);
            row.Children.Add(num);
            var label = UiKit.Label(NameOf(_stack[i]), false);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            AddRowButton(row, 2, Icons.Glyph.Edit, ES.T("Common_Edit", "Edit"), () => UiKit.Fire(async () =>
            {
                var e = await EditAsync(_stack[index]);
                if (e is not null) { _stack[index] = e; RefreshStack(); }
            }, Report));
            AddRowButton(row, 3, Icons.Glyph.Up, ES.T("Common_MoveUp", "Move up"), () => { if (index > 0) { (_stack[index - 1], _stack[index]) = (_stack[index], _stack[index - 1]); RefreshStack(); } });
            AddRowButton(row, 4, Icons.Glyph.Down, ES.T("Common_MoveDown", "Move down"), () => { if (index < _stack.Count - 1) { (_stack[index + 1], _stack[index]) = (_stack[index], _stack[index + 1]); RefreshStack(); } });
            AddRowButton(row, 5, Icons.Glyph.Delete, ES.T("Common_Remove", "Remove"), () => { _stack.RemoveAt(index); RefreshStack(); });
            _stackList.Children.Add(row);
        }
    }

    private static void AddRowButton(Grid row, int column, string glyph, string tip, Action click)
    {
        var b = UiKit.IconButton(glyph, tip, click, 13);
        b.Width = 28;
        b.Height = 28;
        Grid.SetColumn(b, column);
        row.Children.Add(b);
    }

    private void RefreshQuick()
    {
        _quickGrid.Children.Clear();
        foreach (var key in _thumbTargets.Keys.Where(k => k.StartsWith("qe:", StringComparison.Ordinal)).ToList()) _thumbTargets.Remove(key);
        foreach (var q in _ctx.QuickEffects.Items)
        {
            var quick = q;
            var menu = new ContextMenu();
            var apply = new MenuItem { Header = ES.T("Effects_ApplyQuick", "Apply"), Icon = UiKit.Glyph(Icons.Glyph.Accept, 16) };
            apply.Click += (_, _) => UiKit.Fire(() => ApplyQuickAsync(quick), Report);
            var rename = new MenuItem { Header = ES.T("Common_Rename", "Rename"), Icon = UiKit.Glyph(Icons.Glyph.Edit, 16) };
            rename.Click += (_, _) => UiKit.Fire(async () =>
            {
                var n = await InputDialog.AskAsync(_ctx.ActiveWindow, ES.T("Common_Rename", "Rename"), ES.T("Effects_QuickName", "Name for the Quick Effect"), quick.Name);
                if (!string.IsNullOrWhiteSpace(n)) _ctx.QuickEffects.Rename(quick.Id, n);
            }, Report);
            var delete = new MenuItem { Header = ES.T("Common_Delete", "Delete"), Icon = UiKit.Glyph(Icons.Glyph.Delete, 16) };
            delete.Click += (_, _) => _ctx.QuickEffects.Remove(quick.Id);
            menu.Items.Add(apply);
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            var card = EffectCard("qe:" + q.Id, q.Name, "Icon.Effects.Regular", "accent", () => UiKit.Fire(() => ApplyQuickAsync(quick), Report), null, menu);
            card.ToolTip = string.Join(" + ", q.Effects.Select(NameOf));
            _quickGrid.Children.Add(card);
        }
    }

    private async Task ApplyQuickAsync(QuickEffect quick)
    {
        if (_s is null) return;
        foreach (var e in quick.Effects) await _s.ApplyEffectAsync(e);
    }
}
