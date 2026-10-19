using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Shotwise.Core;
using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Shotwise.Editor;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>Smart Redact review pane (VIS-05): categories with counts and toggles, redaction type and colour, Done / Clear.</summary>
internal sealed class SmartRedactPane : ScrollViewer
{
    private static readonly RedactCategory[] Categories =
    {
        RedactCategory.Face, RedactCategory.Date, RedactCategory.PostalAddress, RedactCategory.Phone, RedactCategory.Email,
        RedactCategory.Url, RedactCategory.IpAddress, RedactCategory.Ssn, RedactCategory.CreditCard,
    };

    private readonly EditorContext _ctx;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 0, 16, 20) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), FontSize = 12 };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6), Height = 4 };
    private readonly HashSet<RedactCategory> _enabled = new(Categories);
    private readonly UniformGrid _modes = new() { Rows = 1, Margin = new Thickness(0, 0, 0, 8) };
    private ImageEditorDocument? _doc;
    private EditorSession? _s;
    private BlurKind _mode = BlurKind.Pixelate;
    private Argb _color = Argb.Black;
    private bool _reviewing;
    private ColorSwatch _swatch = null!;

    public SmartRedactPane(EditorContext ctx)
    {
        _ctx = ctx;
        Content = _root;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        _root.Children.Add(UiKit.Hint(ES.T("Redact_Hint", "Finds sensitive information in the image and covers it. Review the categories, then click Done.")));
        var scan = UiKit.PrimaryButton(ES.T("Redact_Scan", "Find sensitive information"), Icons.Glyph.Search, () => UiKit.Fire(ScanAsync, ex => _ = _ctx.ShowErrorAsync(ex)));
        scan.HorizontalAlignment = HorizontalAlignment.Stretch;
        scan.Margin = new Thickness(0, 4, 0, 4);
        _root.Children.Add(scan);
        _progress.SetResourceReference(ForegroundProperty, Tok.Accent);
        _root.Children.Add(_progress);
        _status.SetResourceReference(TextBlock.ForegroundProperty, Tok.Text2);
        _root.Children.Add(_status);
        _root.Children.Add(UiKit.Header(ES.T("Redact_Categories", "Find")));
        _root.Children.Add(_list);
        _root.Children.Add(UiKit.Header(ES.T("Redact_Type", "Redaction type")));
        foreach (var (name, kind, icon) in new[] { (ES.T("Blur_Blur", "Blur"), BlurKind.Gaussian, "Icon.Tool.Blur.Regular"), (ES.T("Blur_Pixelate", "Pixelate"), BlurKind.Pixelate, "Icon.Grid.Regular"), (ES.T("Blur_Solid", "Solid"), BlurKind.Solid, "Icon.Color.Regular") })
        {
            var k = kind;
            var b = UiKit.GhostToggle(name, icon);
            b.Margin = new Thickness(0, 0, 4, 0);
            b.Tag = kind;
            b.Click += (_, _) => { _mode = k; SyncModes(); ApplyMode(); };
            _modes.Children.Add(b);
        }
        _root.Children.Add(_modes);
        _swatch = new ColorSwatch(_color, false, ES.T("Redact_Colour", "Cover colour"));
        _swatch.ColorChanged += c => { _color = c; ApplyMode(); };
        _root.Children.Add(UiKit.Row(ES.T("Redact_Colour", "Cover colour"), _swatch));
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(UiKit.PrimaryButton(ES.T("Redact_Done", "Done"), Icons.Glyph.Accept, Done));
        bar.Children.Add(UiKit.TextButton(ES.T("Redact_Clear", "Clear"), Clear));
        _root.Children.Add(bar);
        SyncModes();
        Render(null);
    }

    private void SyncModes()
    {
        foreach (var t in _modes.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()) t.IsChecked = t.Tag is BlurKind k && k == _mode;
        if (_swatch is not null) _swatch.Color = _color;
    }

    public void Bind(ImageEditorDocument? doc)
    {
        if (_s is not null) { _s.RedactionsFound -= OnFound; _s.Message -= OnMessage; }
        _doc = doc;
        _s = doc?.Session;
        _reviewing = false;
        if (_s is not null)
        {
            _s.RedactionsFound += OnFound;
            _s.Message += OnMessage;
            var settings = _s.ActivePage.SmartRedact;
            _mode = settings.Mode;
            _color = settings.SolidColor;
        }
        SyncModes();
        Render(null);
    }

    private const string FaceModelId = "yunet";

    private bool FaceModelMissing => _ctx.Models is { } m && !m.IsAvailable(FaceModelId) && m.Models.Any(x => x.Id == FaceModelId);

    private async Task<bool> OfferFaceModelAsync()
    {
        var ok = await _ctx.EnsureModelAsync(FaceModelId, ES.T("Model_FaceReason", "Finding faces needs the YuNet face detection model. Other categories work without it."));
        Render(_s?.LastRedactionHits);
        return ok;
    }

    private async Task ScanAsync()
    {
        if (_s is null) return;
        // VIS-05: faces need a downloadable model — ask instead of silently finding nothing.
        var facesSkipped = false;
        if (_enabled.Contains(RedactCategory.Face) && FaceModelMissing) facesSkipped = !await OfferFaceModelAsync();
        _progress.Visibility = Visibility.Visible;
        _status.Text = ES.T("Redact_Scanning", "Looking for sensitive information…");
        try
        {
            ApplyMode();
            var n = await _s.SmartRedactAsync(_enabled.ToHashSet());
            _reviewing = true;
            if (n == 0) _status.Text = ES.T("Redact_NoData", "No sensitive data was found.");
            if (facesSkipped) _status.Text = (_status.Text + " " + ES.T("Redact_FacesSkipped", "Faces were not searched: the face model is not installed.")).Trim();
            Render(_s.LastRedactionHits);
        }
        finally { _progress.Visibility = Visibility.Collapsed; }
    }

    private void ApplyMode() => _s?.SetSmartRedactMode(_mode, _color);

    private void OnFound(object? sender, RedactionsFoundEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _reviewing = true;
        _status.Text = e.Hits.Count == 0 ? ES.T("Redact_NoData", "No sensitive data was found.") : ES.F("Redact_Found", "{0} items found.", e.Hits.Count);
        Render(e.Hits);
    });

    private void OnMessage(object? sender, EditorMessage e)
    {
        if (e.Code == ErrorCode.NoRedactionsFound) Dispatcher.BeginInvoke(() => _status.Text = ES.T("Redact_NoData", "No sensitive data was found."));
    }

    private void Render(IReadOnlyList<RedactionHit>? hits)
    {
        _list.Children.Clear();
        var counts = hits?.GroupBy(h => h.Category).ToDictionary(g => g.Key, g => g.Count()) ?? new Dictionary<RedactCategory, int>();
        foreach (var cat in Categories)
        {
            var category = cat;
            var check = UiKit.Check(CategoryName(cat), _enabled.Contains(cat), on =>
            {
                if (on) _enabled.Add(category); else _enabled.Remove(category);
                _s?.SetSmartRedactCategory(category, on);
            });
            check.Icon = CategoryIcon(cat);
            if (hits is not null)
            {
                var n = counts.GetValueOrDefault(cat);
                check.Trailing = UiKit.Chip(n.ToString(), accent: n > 0);
            }
            _list.Children.Add(check);
            if (cat == RedactCategory.Face && FaceModelMissing)
            {
                var download = UiKit.TextButton(ES.T("Redact_DownloadFaceModel", "Download face model…"), () => UiKit.Fire(async () => { await OfferFaceModelAsync(); }, ex => _ = _ctx.ShowErrorAsync(ex)),
                    ES.T("Redact_FaceModelTip", "Faces are found with a small downloadable model (YuNet, MIT licence)."));
                download.Margin = new Thickness(24, 0, 0, 4);
                download.HorizontalAlignment = HorizontalAlignment.Left;
                _list.Children.Add(download);
            }
        }
    }

    private static object CategoryIcon(RedactCategory c) => UiKit.IconSource(c switch
    {
        RedactCategory.Face => Icons.Glyph.Person,
        RedactCategory.Date => Icons.Glyph.Calendar,
        RedactCategory.PostalAddress => Icons.Glyph.Home,
        RedactCategory.Phone => "Icon.Keyboard.Regular",
        RedactCategory.Email => Icons.Glyph.Mail,
        RedactCategory.Url => Icons.Glyph.Link,
        RedactCategory.IpAddress => Icons.Glyph.Globe,
        RedactCategory.Ssn => "Icon.Document.Regular",
        RedactCategory.CreditCard => "Icon.Tag.Regular",
        _ => Icons.Glyph.Info,
    });

    private static string CategoryName(RedactCategory c) => c switch
    {
        RedactCategory.Face => ES.T("Redact_Face", "Faces"),
        RedactCategory.Date => ES.T("Redact_Date", "Dates"),
        RedactCategory.PostalAddress => ES.T("Redact_Address", "Addresses"),
        RedactCategory.Phone => ES.T("Redact_Phone", "Phone numbers"),
        RedactCategory.Email => ES.T("Redact_Email", "Email addresses"),
        RedactCategory.Url => ES.T("Redact_Url", "Web addresses"),
        RedactCategory.IpAddress => ES.T("Redact_Ip", "IP addresses"),
        RedactCategory.Ssn => ES.T("Redact_Ssn", "Social security numbers"),
        RedactCategory.CreditCard => ES.T("Redact_Card", "Credit card numbers"),
        _ => c.ToString(),
    };

    private void Done()
    {
        _s?.FinishSmartRedact();
        _reviewing = false;
        _status.Text = "";
        Render(null);
    }

    private void Clear()
    {
        _s?.ClearSmartRedact();
        _reviewing = false;
        _status.Text = "";
        Render(null);
    }

    public bool IsReviewing => _reviewing;
}

/// <summary>Auto Simplify pane (VIS-04): on/off, detail, palette, opacity, show original, lock objects.</summary>
internal sealed class SimplifyPane : ScrollViewer
{
    private readonly EditorContext _ctx;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 0, 16, 20) };
    private ImageEditorDocument? _doc;
    private EditorSession? _s;
    private System.Windows.Threading.DispatcherTimer? _regen;
    private bool _pendingRegenerate;
    private bool _building;

    public SimplifyPane(EditorContext ctx)
    {
        _ctx = ctx;
        Content = _root;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    public void Bind(ImageEditorDocument? doc)
    {
        if (_s is not null) _s.Undo.Changed -= OnUndo;
        _doc = doc;
        _s = doc?.Session;
        if (_s is not null) _s.Undo.Changed += OnUndo;
        Build();
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        if (!_building) Dispatcher.BeginInvoke(Build, System.Windows.Threading.DispatcherPriority.Background);
    }

    private SimplifySettings Current => _s!.ActivePage.Simplify;

    private void Change(Func<SimplifySettings, SimplifySettings> map, bool regenerate)
    {
        if (_s is null) return;
        _building = true;
        try
        {
            var next = map(Current);
            _pendingRegenerate |= regenerate && next.AutoSimplifyEnabled;
            if (regenerate)
            {
                _regen ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                _regen.Stop();
                _regen.Tick -= OnRegen;
                _regen.Tick += OnRegen;
                _pendingSettings = next;
                _regen.Start();
            }
            else UiKit.Fire(() => _s.SetSimplifySettingsAsync(next, false), ex => _ = _ctx.ShowErrorAsync(ex));
        }
        finally { _building = false; }
    }

    private SimplifySettings? _pendingSettings;

    private void OnRegen(object? sender, EventArgs e)
    {
        _regen?.Stop();
        if (_s is null || _pendingSettings is not { } settings) return;
        var regen = _pendingRegenerate;
        _pendingRegenerate = false;
        _pendingSettings = null;
        UiKit.Fire(() => _s.SetSimplifySettingsAsync(settings, regen), ex => _ = _ctx.ShowErrorAsync(ex));
    }

    private void Build()
    {
        _root.Children.Clear();
        if (_s is null)
        {
            _root.Children.Add(UiKit.Hint(ES.T("Props_NoDoc", "Open or capture an image to see tool properties.")));
            return;
        }
        var cur = Current;
        _root.Children.Add(UiKit.Hint(ES.T("Simplify_Hint", "Replaces text and interface detail with simple shapes so viewers focus on what matters.")));
        var auto = UiKit.Check(ES.T("Simplify_Auto", "Auto Simplify"), cur.AutoSimplifyEnabled, on =>
        {
            if (on) UiKit.Fire(async () => { await _s.AutoSimplifyAsync(); Dispatcher.Invoke(Build); }, ex => _ = _ctx.ShowErrorAsync(ex));
            else { _s.DisableAutoSimplify(); Build(); }
        });
        auto.Icon = "Icon.Tool.Simplify.Color";
        auto.FontWeight = FontWeights.SemiBold;
        _root.Children.Add(auto);
        _root.Children.Add(UiKit.Header(ES.T("Simplify_Look", "Look")));
        var detail = new SliderRow(1, 10, cur.Detail, "0");
        detail.Changed += (v, _) => Change(s => s with { Detail = (int)Math.Round(v) }, true);
        _root.Children.Add(UiKit.Row(ES.T("Simplify_Detail", "Detail"), detail));
        _root.Children.Add(UiKit.Row(ES.T("Simplify_Palette", "Palette"), UiKit.Combo(new[]
        {
            (ES.T("Palette_Image", "Image colours"), SimplifyPaletteKind.ImageColors), (ES.T("Palette_Light", "Light"), SimplifyPaletteKind.Light),
            (ES.T("Palette_Dark", "Dark"), SimplifyPaletteKind.Dark), (ES.T("Palette_Gray", "Grayscale"), SimplifyPaletteKind.Grayscale), (ES.T("Palette_Custom", "Custom"), SimplifyPaletteKind.Custom),
        }, cur.Palette, p => { Change(s => s with { Palette = p }, true); })));
        if (cur.Palette == SimplifyPaletteKind.Custom)
        {
            var colors = new WrapPanel();
            for (var i = 0; i < 8; i++)
            {
                var idx = i;
                var color = idx < cur.CustomColors.Count ? cur.CustomColors[idx] : Argb.LightGray;
                var sw = new ColorSwatch(color, false);
                sw.ColorChanged += c => Change(s =>
                {
                    var list = s.CustomColors.ToList();
                    while (list.Count <= idx) list.Add(Argb.LightGray);
                    list[idx] = c;
                    return s with { CustomColors = list };
                }, true);
                sw.Margin = new Thickness(2);
                colors.Children.Add(sw);
            }
            _root.Children.Add(UiKit.Row(ES.T("Simplify_Colours", "Colours"), colors));
        }
        var opacity = new SliderRow(0, 100, cur.Opacity * 100, "0'%'");
        opacity.Changed += (v, _) => Change(s => s with { Opacity = v / 100 }, false);
        _root.Children.Add(UiKit.Row(ES.T("Simplify_Opacity", "Opacity"), opacity));
        _root.Children.Add(UiKit.Check(ES.T("Simplify_ShowOriginal", "Show original"), cur.ShowOriginal, on => { Change(s => s with { ShowOriginal = on }, false); Build(); }));
        if (cur.ShowOriginal)
        {
            var orig = new SliderRow(0, 100, cur.OriginalOpacity * 100, "0'%'");
            orig.Changed += (v, _) => Change(s => s with { OriginalOpacity = v / 100 }, false);
            _root.Children.Add(UiKit.Row(ES.T("Simplify_OriginalOpacity", "Original opacity"), orig));
        }
        _root.Children.Add(UiKit.Check(ES.T("Simplify_Lock", "Lock objects"), cur.LockObjects, on => Change(s => s with { LockObjects = on }, false)));
        var spacing = new SliderRow(0.5, 3, cur.LineSpacing, "0.0", 0.1);
        spacing.Changed += (v, _) => Change(s => s with { LineSpacing = v }, true);
        _root.Children.Add(UiKit.Row(ES.T("Simplify_LineSpacing", "Line spacing"), spacing));
        var height = new SliderRow(4, 40, cur.TextHeight, "0");
        height.Changed += (v, _) => Change(s => s with { TextHeight = v }, true);
        _root.Children.Add(UiKit.Row(ES.T("Simplify_TextHeight", "Text height"), height));
    }
}

internal static class TextBlockExtensions
{
    public static TextBlock Wrapped(this TextBlock tb)
    {
        tb.TextWrapping = TextWrapping.Wrap;
        tb.Margin = new Thickness(0, 2, 0, 6);
        return tb;
    }
}
