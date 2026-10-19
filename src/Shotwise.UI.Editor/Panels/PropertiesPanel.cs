using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shotwise.Core.Annotations;
using Shotwise.Core.Commands;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.Editor.Tools;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Documents;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// "Tool Properties" (DESIGN-SYSTEM §4): shows the style of the active tool (prototype) or of the selected object(s) and writes
/// edits through <see cref="EditorSession.ChangeStyleProperty"/> / tool option objects. Layout: PanelHeader, a hero row of big
/// <see cref="ColorSwatchButton"/>s (Color / Fill / …) with the <see cref="ShadowDirectionPicker"/>, a row of
/// <see cref="StylePreviewComboBox"/>es (arrow caps, dash, width) and <see cref="SliderWithValue"/> / ToggleSwitch rows below.
/// Rebuilt on tool, selection and undo changes.
/// </summary>
internal sealed class PropertiesPanel : ScrollViewer
{
    /// <summary><see cref="PanelHeader"/> (with its chevron toggle) that reports <see cref="PanelHeader.IsExpanded"/> changes.</summary>
    private sealed class SectionHeader : PanelHeader
    {
        static SectionHeader() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(typeof(PanelHeader)));

        public event Action<bool>? ExpandedChanged;

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsExpandedProperty) ExpandedChanged?.Invoke((bool)e.NewValue);
        }
    }

    private readonly EditorContext _ctx;
    private readonly PreviewRenderer _previews;
    private readonly EditorLayout _layout;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 0, 16, 20) };
    private readonly Grid _stampGrid = new() { Margin = new Thickness(16, 10, 16, 8) };
    private readonly StackPanel _stampSection = new();
    private StampGallery? _gallery;
    private EditorSession? _galleryFor;
    private StackPanel _body = new();
    private ImageEditorDocument? _doc;
    private EditorSession? _s;
    private ToolKind _tool = ToolKind.Select;
    private Annotation? _target;
    private bool _busy;
    private bool _refreshQueued;
    private UniformGrid _hero = new();
    private Grid _combos = new();
    private static IReadOnlyList<string>? _fontNames;

    public PropertiesPanel(EditorContext ctx, PreviewRenderer previews, EditorLayout layout)
    {
        _ctx = ctx;
        _previews = previews;
        _layout = layout;
        _stampGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _stampGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stampSection, 1);
        _stampGrid.Children.Add(_stampSection);
        Content = _root;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
    }

    /// <summary>Panel asks the host to show another pane ("redact", "simplify", "styles").</summary>
    public event Action<string>? PaneRequested;

    public void Bind(ImageEditorDocument? doc)
    {
        if (ReferenceEquals(doc, _doc)) return;
        if (_s is not null)
        {
            _s.SelectionChanged -= OnChanged;
            _s.ActiveToolChanged -= OnChanged;
            _s.ToolOptionsChanged -= OnChanged;
            _s.ActivePageChanged -= OnChanged;
            _s.Undo.Changed -= OnChanged;
        }
        _doc = doc;
        _s = doc?.Session;
        if (_s is not null)
        {
            _s.SelectionChanged += OnChanged;
            _s.ActiveToolChanged += OnChanged;
            _s.ToolOptionsChanged += OnChanged;
            _s.ActivePageChanged += OnChanged;
            _s.Undo.Changed += OnChanged;
        }
        Rebuild();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_busy || _refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!_busy) Rebuild();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ editing helpers

    private void Edit(Action<Annotation> mutate, string label, string? key = null, bool rebuild = false)
    {
        if (_s is null) return;
        _busy = true;
        try { _s.ChangeStyleProperty(_tool, mutate, label, key); }
        finally { _busy = false; }
        if (rebuild) Rebuild();
    }

    private void EditAs<T>(Action<T> mutate, string label, string? key = null, bool rebuild = false) where T : Annotation
        => Edit(a => { if (a is T t) mutate(t); }, label, key, rebuild);

    private void Options(Action change, bool rebuild = false)
    {
        if (_s is null) return;
        _busy = true;
        try
        {
            change();
            _s.NotifyToolOptionsChanged();
        }
        finally { _busy = false; }
        if (rebuild) Rebuild();
    }

    private T Add<T>(T el) where T : UIElement
    {
        _body.Children.Add(el);
        return el;
    }

    private void Row(string label, UIElement c) => Add(UiKit.Row(label, c));

    private void Slider(string label, double min, double max, double value, string fmt, Action<double, string> apply, double step = 1)
    {
        var s = new SliderRow(min, max, value, fmt, step);
        s.Changed += apply;
        Row(label, s);
    }

    private void Swatch(string label, Argb color, Action<Argb> apply, bool alpha = true)
    {
        var sw = new ColorSwatch(color, alpha, label);
        sw.ColorChanged += apply;
        Row(label, sw);
    }

    /// <summary>Big captioned swatch in the hero row at the top of the panel.</summary>
    private void HeroSwatch(string caption, Argb color, Action<Argb> apply, bool alpha = true)
    {
        var sw = new ColorSwatch(color, alpha, caption, caption, 36) { Margin = new Thickness(0, 0, 0, 4) };
        sw.ColorChanged += apply;
        _hero.Children.Add(sw);
    }

    private void HeroCombo(FrameworkElement combo, string tooltip)
    {
        combo.ToolTip = tooltip;
        AutomationProperties.SetName(combo, tooltip);
        combo.Margin = new Thickness(_combos.Children.Count == 0 ? 0 : 6, 0, 0, 0);
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _combos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(combo, _combos.Children.Count);
        _combos.Children.Add(combo);
    }

    private ComboBox Combo<T>(string label, IEnumerable<(string, T)> items, T value, Action<T> apply) where T : notnull
    {
        var c = UiKit.Combo(items, value, apply);
        Row(label, c);
        return c;
    }

    private void Check(string text, bool value, Action<bool> apply) => Add(UiKit.Check(text, value, apply));

    private void Actions(params Button[] buttons)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buttons) bar.Children.Add(b);
        Add(bar);
    }

    private static IReadOnlyList<string> FontNames => _fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string Enumish(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static IEnumerable<(string, T)> EnumItems<T>(params T[] only) where T : struct, Enum
        => (only.Length > 0 ? only : Enum.GetValues<T>()).Select(v => (Enumish(v.ToString()), v));

    // ------------------------------------------------------------------ preview combos (arrow caps, dash, width)

    private static readonly Dictionary<(ArrowCap, bool), Geometry> CapGeometries = new();

    /// <summary>Filled preview of a line end: a short shaft plus the cap shape (mirrored for the start end).</summary>
    private static Geometry CapGeometry(ArrowCap cap, bool start)
    {
        if (CapGeometries.TryGetValue((cap, start), out var g)) return g;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, 0.01, 14)));     // fixes the bounds so every preview scales alike
        group.Children.Add(new RectangleGeometry(new Rect(32, 0, 0.01, 14)));
        var shaftEnd = cap switch { ArrowCap.None => 31, ArrowCap.Round => 24, ArrowCap.Square => 23, ArrowCap.TBar => 29, ArrowCap.Diamond => 22, _ => 21 };
        group.Children.Add(new RectangleGeometry(new Rect(2, 5.75, shaftEnd - 2, 2.5)));
        var head = cap switch
        {
            ArrowCap.Arrow => "M19,1.5 L31.5,7 L19,12.5 Z",
            ArrowCap.OpenArrow => "M19,0.8 L31.5,7 L19,13.2 L19,10.6 L26.4,7 L19,3.4 Z",
            ArrowCap.Triangle => "M17,0 L32,7 L17,14 Z",
            ArrowCap.Round => "M27,2.5 A4.5,4.5 0 1 1 26.99,2.5 Z",
            ArrowCap.Square => "M22.5,2.5 H31.5 V11.5 H22.5 Z",
            ArrowCap.Diamond => "M26,1.5 L31.5,7 L26,12.5 L20.5,7 Z",
            ArrowCap.TBar => "M28.5,1 H31 V13 H28.5 Z",
            _ => null,
        };
        if (head is not null) group.Children.Add(Geometry.Parse(head));
        if (start) group.Transform = new ScaleTransform(-1, 1, 16, 7);
        group.Freeze();
        CapGeometries[(cap, start)] = group;
        return group;
    }

    private StylePreviewComboBox CapCombo(IEnumerable<ArrowCap> caps, ArrowCap value, bool start, Action<ArrowCap> apply)
    {
        var list = caps.ToList();
        var combo = new StylePreviewComboBox { MinWidth = 64 };
        combo.SetResourceReference(StylePreviewComboBox.PreviewTemplateProperty, "Shotwise.PreviewTemplate.Geometry");
        foreach (var c in list) combo.Items.Add(CapGeometry(c, start));
        combo.SelectedIndex = Math.Max(0, list.IndexOf(value));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) apply(list[combo.SelectedIndex]); };
        return combo;
    }

    private static readonly (StrokeDash Dash, DoubleCollection Pattern)[] DashPatterns =
    {
        (StrokeDash.Solid, new DoubleCollection()),
        (StrokeDash.Dash, new DoubleCollection { 3, 1.6 }),
        (StrokeDash.Dot, new DoubleCollection { 1, 1 }),
        (StrokeDash.DashDot, new DoubleCollection { 3, 1.2, 1, 1.2 }),
    };

    private StylePreviewComboBox DashCombo(StrokeDash value, Action<StrokeDash> apply)
    {
        var combo = new StylePreviewComboBox { MinWidth = 76 };
        combo.SetResourceReference(StylePreviewComboBox.PreviewTemplateProperty, "Shotwise.PreviewTemplate.DashArray");
        foreach (var (_, p) in DashPatterns) combo.Items.Add(p);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(DashPatterns, d => d.Dash == value));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) apply(DashPatterns[combo.SelectedIndex].Dash); };
        return combo;
    }

    private static readonly double[] WidthPresets = { 1, 2, 3, 4, 6, 8, 10, 12, 16 };

    private StylePreviewComboBox WidthCombo(double value, Action<double> apply)
    {
        var values = WidthPresets.ToList();
        if (!values.Any(v => Math.Abs(v - value) < 0.01)) { values.Add(Math.Round(value, 1)); values.Sort(); }
        var combo = new StylePreviewComboBox { MinWidth = 76 };
        combo.SetResourceReference(StylePreviewComboBox.PreviewTemplateProperty, "Shotwise.PreviewTemplate.LineWidth");
        foreach (var v in values) combo.Items.Add(v);
        combo.SelectedIndex = values.FindIndex(v => Math.Abs(v - value) < 0.06);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is double v && Math.Abs(v - value) > 0.01) apply(v); };
        return combo;
    }

    // ------------------------------------------------------------------ build

    private void Rebuild()
    {
        _root.Children.Clear();
        _stampSection.Children.Clear();
        _body = new StackPanel();
        if (_s is null || _doc is null)
        {
            Add(UiKit.Hint(ES.T("Props_NoDoc", "Open or capture an image to see tool properties.")));
            Assemble("none", null, false);
            return;
        }
        var s = _s;
        var selection = s.SelectedAnnotations();
        _tool = s.ActiveTool;
        var selectLike = _tool is ToolKind.Select or ToolKind.Move or ToolKind.Favorites;
        if (selectLike && selection.Count > 0) _tool = StyleApplier.ToolFor(selection[^1].Kind);

        // identity row: tool icon + name (+ selection chip)
        var title = selectLike && selection.Count > 0
            ? (selection.Count > 1 ? ES.F("Props_Selected", "{0} objects selected", selection.Count) : ToolInfo.Name(_tool))
            : ToolInfo.Name(s.ActiveTool);
        var ident = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        ident.Children.Add(Icons.ForTool(selectLike && selection.Count > 0 ? _tool : s.ActiveTool, 20));
        ident.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Themed(TextBlock.ForegroundProperty, Tok.Text));
        if (selectLike && selection.Count > 0)
        {
            var chip = UiKit.Chip(ES.T("Props_SelectionChip", "Selection"), accent: true);
            chip.Margin = new Thickness(8, 0, 0, 0);
            ident.Children.Add(chip);
        }
        if (s.ActiveTool != ToolKind.Stamp) Add(ident); // the Stamp tool's section header sits under the browser: no need to repeat the name
        if (selectLike && selection.Count > 0) BuildSelectionActions(s, selection);

        _hero = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 8) };
        _combos = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        Add(_hero);
        Add(_combos);

        _target = null;
        if (ToolInfo.IsAnnotationTool(_tool))
        {
            var kind = EditorSession.AnnotationKindFor(_tool);
            _target = selection.LastOrDefault(a => a.Kind == kind) ?? s.CreatePrototype(_tool);
        }

        if (_target is not null) BuildAnnotationProperties(_target);
        else BuildToolOptions(s);
        if (s.ActiveTool == ToolKind.Move) BuildSmartMove(s);
        if (selectLike && selection.Count == 0 && s.ActiveTool != ToolKind.Move) BuildCanvasProperties(s);

        _hero.Visibility = _hero.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _combos.Visibility = _combos.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Assemble(s.ActiveTool.ToString(), ToolInfo.Description(s.ActiveTool), s.ActiveTool == ToolKind.Stamp);
    }

    /// <summary>
    /// Puts the built <see cref="_body"/> into a collapsible "Tool Properties" section (state persisted per tool in the layout;
    /// Stamp defaults to collapsed). For the Stamp tool the browser takes the top and the section sits at the bottom.
    /// </summary>
    private void Assemble(string key, string? help, bool stampFirst)
    {
        var expanded = _layout.PropsExpanded.TryGetValue(key, out var saved) ? saved : key != nameof(ToolKind.Stamp);
        var header = new SectionHeader
        {
            Content = ES.T("Props_ToolProperties", "Tool Properties"),
            CanCollapse = true,
            IsExpanded = expanded,
            HelpText = help,
            Padding = new Thickness(0, stampFirst ? 4 : 10, 0, 6),
            MinHeight = 34,
        };
        FrameworkElement bodyEl = stampFirst
            ? new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body, Padding = new Thickness(0, 0, 8, 0) }
            : _body;
        bodyEl.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        header.ExpandedChanged += open =>
        {
            bodyEl.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (key == "none") return;
            _layout.PropsExpanded[key] = open;
            _ctx.Layout.Save(_layout);
        };

        if (stampFirst && _s is not null && _ctx.Stamps is not null)
        {
            if (_gallery is null || !ReferenceEquals(_galleryFor, _s))
            {
                if (_gallery is not null) _stampGrid.Children.Remove(_gallery);
                _gallery = new StampGallery(_ctx, _previews, _s, _layout);
                _galleryFor = _s;
                _stampGrid.Children.Insert(0, _gallery);
            }
            _stampSection.Children.Add(new Border { Margin = new Thickness(0, 4, 0, 0) }.Styled("Shotwise.Divider"));
            _stampSection.Children.Add(header);
            _stampSection.Children.Add(bodyEl);
            Content = _stampGrid;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            _root.Children.Add(header);
            _root.Children.Add(bodyEl);
            Content = _root;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    private void BuildSelectionActions(EditorSession s, IReadOnlyList<Annotation> selection)
    {
        var bar = new WrapPanel { Margin = new Thickness(-6, 0, 0, 8) };
        var addStyle = UiKit.GhostButton(ES.T("Props_AddQuickStyle", "Add to Quick Styles"), Icons.Glyph.Star, () => UiKit.Fire(() => QuickStyleActions.AddFromSelectionAsync(_ctx, s), ex => _ = _ctx.ShowErrorAsync(ex)));
        var dup = UiKit.GhostButton(ES.T("Props_Duplicate", "Duplicate"), Icons.Glyph.Copy, s.Duplicate);
        var del = UiKit.GhostButton(ES.T("Props_Delete", "Delete"), Icons.Glyph.Delete, s.DeleteSelection);
        foreach (var b in new[] { addStyle, dup, del }) { b.MinHeight = 30; bar.Children.Add(b); }
        Add(bar);
        if (selection.Count == 1 && selection[0] is StepAnnotation step)
        {
            Actions(
                UiKit.TextButton(ES.T("Props_EditValue", "Edit value…"), () => UiKit.Fire(async () =>
                {
                    var v = await InputDialog.AskAsync(_ctx.ActiveWindow, ES.T("Props_EditValue", "Edit value…"), ES.T("Props_StepValuePrompt", "Step value (number)"), step.Value.ToString());
                    if (int.TryParse(v, out var n) && n > 0) s.EditStepValue(step.Id, n);
                }, ex => _ = _ctx.ShowErrorAsync(ex))),
                UiKit.TextButton(ES.T("Props_RestartSequence", "Restart sequence"), () => s.RestartStepSequence(step.Sequence, 1)));
        }
        if (selection.Count == 1 && selection[0] is CalloutAnnotation callout)
        {
            Actions(
                UiKit.GlyphTextButton(Icons.Glyph.Add, ES.T("Props_AddTail", "Add tail"), () => { CalloutTool.AddTail(s, callout.Id); Rebuild(); }),
                UiKit.TextButton(ES.T("Props_RemoveTail", "Remove tail"), () => { if (callout.Tails.Count > 0) CalloutTool.RemoveTail(s, callout.Id, callout.Tails.Count - 1); Rebuild(); }));
        }
    }

    // ------------------------------------------------------------------ annotation properties

    private void BuildAnnotationProperties(Annotation t)
    {
        switch (t)
        {
            case ArrowAnnotation a: ArrowSection(a); break;
            case LineAnnotation l: LineSection(l); break;
            case ShapeAnnotation sh: ShapeSection(sh); break;
            case CalloutAnnotation c: CalloutSection(c); break;
            case TextAnnotation tx: TextSection(tx); break;
            case StampAnnotation: StampSection(); break;
            case StepAnnotation st: StepSection(st); break;
            case PenAnnotation p: PenSection(p); break;
            case HighlightAnnotation h: HighlightSection(h); break;
            case MagnifyAnnotation m: MagnifySection(m); break;
            case BlurAnnotation b: BlurSection(b); break;
            case CursorAnnotation cu: CursorSection(cu); break;
            case SimplifyShapeAnnotation: SimplifySection(); break;
        }
        CommonSection(t);
    }

    /// <summary>Outline colour (hero), width slider and dash style (preview combo).</summary>
    private void StrokeSection(Annotation t, bool width = true, bool dash = true, string? caption = null, bool widthCombo = false)
    {
        HeroSwatch(caption ?? ES.T("Props_Color", "Color"), t.Stroke.Color, c => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Color = c }, "Outline colour"));
        if (dash) HeroCombo(DashCombo(t.Stroke.Dash, d => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Dash = d }, "Line style")), ES.T("Props_Dash", "Line style"));
        if (width && widthCombo) HeroCombo(WidthCombo(t.Stroke.Width, v => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Width = v }, "Line width", null, true)), ES.T("Props_Width", "Width"));
        if (width) Slider(ES.T("Props_Width", "Width"), 0, 40, t.Stroke.Width, "0.#", (v, k) => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Width = v }, "Line width", k), 0.5);
    }

    /// <summary>Fill colour (hero). Picking "No colour" removes the fill.</summary>
    private void FillSection(Annotation t)
        => HeroSwatch(ES.T("Props_Fill", "Fill"), t.Fill.Kind == FillKind.None ? Argb.Transparent : t.Fill.Color,
            c => Edit(a => a.Fill = c.A == 0 ? FillStyle.None : FillStyle.Solid(c), "Fill colour"));

    private static string ShadowName(ShadowDirection d) => d switch
    {
        ShadowDirection.None => ES.T("Shadow_None", "None"),
        ShadowDirection.Around => ES.T("Shadow_Around", "All around"),
        ShadowDirection.TopLeft => ES.T("Shadow_TopLeft", "Top left"),
        ShadowDirection.Top => ES.T("Shadow_Top", "Top"),
        ShadowDirection.TopRight => ES.T("Shadow_TopRight", "Top right"),
        ShadowDirection.Left => ES.T("Shadow_Left", "Left"),
        ShadowDirection.Right => ES.T("Shadow_Right", "Right"),
        ShadowDirection.BottomLeft => ES.T("Shadow_BottomLeft", "Bottom left"),
        ShadowDirection.Bottom => ES.T("Shadow_Bottom", "Bottom"),
        _ => ES.T("Shadow_BottomRight", "Bottom right"),
    };

    /// <summary>Compact inline "Shadow: [None ▾]" row; the 3×3 direction picker opens in a flyout.</summary>
    private void ShadowRow(Annotation t)
    {
        var dir0 = t.Shadow is null ? ShadowDirection.None : t.Shadow.Distance <= 0.01 ? ShadowDirection.Around : ShadowDirectionPicker.FromAngle(t.Shadow.AngleDegrees);
        var picker = new ShadowDirectionPicker { Direction = dir0 };
        var card = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 12, 12), Child = picker, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.25 } };
        card.SetResourceReference(Border.BackgroundProperty, Tok.Surface1);
        card.SetResourceReference(Border.BorderBrushProperty, Tok.BorderSubtle);
        var popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, Child = card };
        var label = new TextBlock { Text = ShadowName(dir0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(label);
        content.Children.Add(UiKit.Glyph(Icons.Glyph.Down, 12));
        var btn = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120, ToolTip = ES.T("Props_ShadowDirection", "Shadow direction") }.Styled("Shotwise.SecondaryButton");
        AutomationProperties.SetName(btn, ES.T("Props_Shadow", "Shadow"));
        popup.PlacementTarget = btn;
        btn.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        var host = new Grid();
        host.Children.Add(btn);
        host.Children.Add(popup);
        Row(ES.T("Props_Shadow", "Shadow"), host);
        ShadowPicker(picker, popup);
    }

    private void ShadowPicker(ShadowDirectionPicker picker, Popup popup)
    {
        // The cells' own Click handlers set Direction first; the bubbling Click then applies it (no descriptor subscription → no leak).
        picker.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) =>
        {
            var dir = picker.Direction;
            Edit(a =>
            {
                if (dir == ShadowDirection.None) { a.Shadow = null; return; }
                var current = a.Shadow ?? ShadowStyle.Default;
                a.Shadow = dir == ShadowDirection.Around
                    ? current with { Distance = 0, Blur = Math.Max(current.Blur, 10) }
                    : current with { AngleDegrees = ShadowDirectionPicker.ToAngle(dir) ?? current.AngleDegrees, Distance = current.Distance <= 0.01 ? ShadowStyle.Default.Distance : current.Distance };
            }, "Shadow");
            popup.IsOpen = false;
            Dispatcher.BeginInvoke(Rebuild, System.Windows.Threading.DispatcherPriority.Background);
        }));
    }

    private void CommonSection(Annotation t)
    {
        Slider(ES.T("Props_Opacity", "Opacity"), 0, 100, t.Opacity * 100, "0'%'", (v, k) => Edit(a => a.Opacity = v / 100, "Opacity", k));
        if (t is not HighlightAnnotation and not BlurAnnotation and not SimplifyShapeAnnotation) ShadowRow(t);
        if (t.Shadow is { } sh)
        {
            Add(UiKit.Header(ES.T("Props_Shadow", "Shadow")));
            Swatch(ES.T("Props_ShadowColour", "Colour"), sh.Color, c => Edit(a => a.Shadow = (a.Shadow ?? ShadowStyle.Default) with { Color = c }, "Shadow colour"));
            Slider(ES.T("Props_ShadowAngle", "Angle"), 0, 360, sh.AngleDegrees, "0'°'", (v, k) => Edit(a => a.Shadow = (a.Shadow ?? ShadowStyle.Default) with { AngleDegrees = v }, "Shadow angle", k));
            Slider(ES.T("Props_ShadowDistance", "Distance"), 0, 60, sh.Distance, "0", (v, k) => Edit(a => a.Shadow = (a.Shadow ?? ShadowStyle.Default) with { Distance = v }, "Shadow distance", k));
            Slider(ES.T("Props_ShadowBlur", "Blur"), 0, 60, sh.Blur, "0", (v, k) => Edit(a => a.Shadow = (a.Shadow ?? ShadowStyle.Default) with { Blur = v }, "Shadow blur", k));
            Slider(ES.T("Props_ShadowOpacity", "Opacity"), 0, 100, sh.Opacity * 100, "0'%'", (v, k) => Edit(a => a.Shadow = (a.Shadow ?? ShadowStyle.Default) with { Opacity = v / 100 }, "Shadow opacity", k));
        }
    }

    private static IEnumerable<ArrowCap> Caps(bool lineOnly)
        => lineOnly ? new[] { ArrowCap.None, ArrowCap.Round, ArrowCap.Square } : Enum.GetValues<ArrowCap>();

    private void ArrowSection(ArrowAnnotation t)
    {
        HeroSwatch(ES.T("Props_Color", "Color"), t.Stroke.Color, c => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Color = c }, "Outline colour"));
        HeroCombo(CapCombo(Caps(false), t.StartCap, true, v => EditAs<ArrowAnnotation>(a => a.StartCap = v, "Start cap")), ES.T("Props_StartCap", "Start"));
        HeroCombo(DashCombo(t.Stroke.Dash, d => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Dash = d }, "Line style")), ES.T("Props_Dash", "Line style"));
        HeroCombo(CapCombo(Caps(false), t.EndCap, false, v => EditAs<ArrowAnnotation>(a => a.EndCap = v, "End cap")), ES.T("Props_EndCap", "End"));
        Slider(ES.T("Props_Width", "Width"), 0, 40, t.Stroke.Width, "0.#", (v, k) => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Width = v }, "Line width", k), 0.5);
        var start = new SliderRow(0.3, 4, t.StartCapScale, "0.0", 0.1) { IsEnabled = t.StartCap != ArrowCap.None };
        start.Changed += (v, k) => EditAs<ArrowAnnotation>(a => a.StartCapScale = v, "Start size", k);
        Row(ES.T("Props_StartSize", "Start size"), start);
        var end = new SliderRow(0.3, 4, t.EndCapScale, "0.0", 0.1) { IsEnabled = t.EndCap != ArrowCap.None };
        end.Changed += (v, k) => EditAs<ArrowAnnotation>(a => a.EndCapScale = v, "End size", k);
        Row(ES.T("Props_EndSize", "End size"), end);
        Check(ES.T("Props_Bezier", "Curved (Bézier)"), t.Bezier, on => EditAs<ArrowAnnotation>(a => SetBezier(a, on), "Curve", null, true));
    }

    private void LineSection(LineAnnotation t)
    {
        HeroSwatch(ES.T("Props_Color", "Color"), t.Stroke.Color, c => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Color = c }, "Outline colour"));
        HeroCombo(CapCombo(Caps(true), t.StartCap, true, v => EditAs<LineAnnotation>(a => a.StartCap = v, "Start cap")), ES.T("Props_StartCap", "Start"));
        HeroCombo(DashCombo(t.Stroke.Dash, d => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Dash = d }, "Line style")), ES.T("Props_Dash", "Line style"));
        HeroCombo(CapCombo(Caps(true), t.EndCap, false, v => EditAs<LineAnnotation>(a => a.EndCap = v, "End cap")), ES.T("Props_EndCap", "End"));
        Slider(ES.T("Props_Width", "Width"), 0, 40, t.Stroke.Width, "0.#", (v, k) => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Width = v }, "Line width", k), 0.5);
        Check(ES.T("Props_DoubleLine", "Double line"), t.DoubleLine, on => EditAs<LineAnnotation>(a => a.DoubleLine = on, "Double line"));
        Check(ES.T("Props_Bezier", "Curved (Bézier)"), t.Bezier, on => EditAs<LineAnnotation>(a => SetBezier(a, on), "Curve", null, true));
    }

    private static void SetBezier(Annotation a, bool on)
    {
        Point2? c1, c2;
        var s = a.Start;
        var e = a.End;
        c1 = new Point2(s.X + (e.X - s.X) / 3 - (e.Y - s.Y) * 0.2, s.Y + (e.Y - s.Y) / 3 + (e.X - s.X) * 0.2);
        c2 = new Point2(s.X + (e.X - s.X) * 2 / 3 - (e.Y - s.Y) * 0.2, s.Y + (e.Y - s.Y) * 2 / 3 + (e.X - s.X) * 0.2);
        switch (a)
        {
            case ArrowAnnotation ar:
                ar.Bezier = on;
                if (on) { ar.Control1 ??= c1; ar.Control2 ??= c2; } else { ar.Control1 = null; ar.Control2 = null; }
                break;
            case LineAnnotation li:
                li.Bezier = on;
                if (on) { li.Control1 ??= c1; li.Control2 ??= c2; } else { li.Control1 = null; li.Control2 = null; }
                break;
        }
    }

    private void ShapeSection(ShapeAnnotation t)
    {
        StrokeSection(t, caption: ES.T("Props_Outline", "Outline"), widthCombo: true);
        FillSection(t);
        if (t.Shape != ShapeKind.Polygon)
            Combo(ES.T("Props_ShapeKind", "Shape"), new[] { (ES.T("Shape_Rect", "Rectangle"), ShapeKind.Rectangle), (ES.T("Shape_Rounded", "Rounded rectangle"), ShapeKind.RoundedRectangle), (ES.T("Shape_Ellipse", "Ellipse"), ShapeKind.Ellipse) },
                t.Shape, v => EditAs<ShapeAnnotation>(a => { if (a.Shape != ShapeKind.Polygon) a.Shape = v; }, "Shape type", null, true));
        if (t.Shape == ShapeKind.RoundedRectangle)
            Slider(ES.T("Props_CornerRadius", "Corner radius"), 0, 120, t.CornerRadius, "0", (v, k) => EditAs<ShapeAnnotation>(a => a.CornerRadius = v, "Corner radius", k));
    }

    private void CalloutSection(CalloutAnnotation t)
    {
        StrokeSection(t, caption: ES.T("Props_Outline", "Outline"), widthCombo: true);
        FillSection(t);
        Add(UiKit.Header(ES.T("Props_CalloutShape", "Callout shape")));
        var wrap = new WrapPanel { Margin = new Thickness(-2, 0, 0, 6) };
        foreach (var id in CalloutPresets.All)
        {
            var img = new Image { Source = _previews.Callout(id), Width = 44, Height = 30, Stretch = Stretch.Uniform };
            var b = new ToggleButton
            {
                Content = img,
                IsChecked = string.Equals(t.ShapePresetId, id, StringComparison.OrdinalIgnoreCase),
                ToolTip = Enumish(string.Concat(id.Split('-').Select(p => char.ToUpper(p[0]) + p[1..]))),
                Width = 52, Height = 40, Margin = new Thickness(2),
            }.Styled("Shotwise.ShadowCell");
            AutomationProperties.SetName(b, (string)b.ToolTip);
            var preset = id;
            b.Click += (_, _) => EditAs<CalloutAnnotation>(a =>
            {
                a.ShapePresetId = preset;
                if (!CalloutPresets.AllowsTails(preset)) a.Tails.Clear();
            }, "Callout shape", null, true);
            wrap.Children.Add(b);
        }
        Add(wrap);
        if (CalloutPresets.AllowsTails(t.ShapePresetId))
        {
            Add(UiKit.Header(ES.T("Props_Tail", "Tail")));
            Slider(ES.T("Props_TailWidth", "Width"), 4, 120, t.Tails.FirstOrDefault()?.Width ?? 20, "0", (v, k) => EditAs<CalloutAnnotation>(a => { for (var i = 0; i < a.Tails.Count; i++) a.Tails[i] = a.Tails[i] with { Width = v }; }, "Tail width", k));
            Check(ES.T("Props_TailCurved", "Curved tail"), t.Tails.FirstOrDefault()?.Curved ?? false, on => EditAs<CalloutAnnotation>(a => { for (var i = 0; i < a.Tails.Count; i++) a.Tails[i] = a.Tails[i] with { Curved = on }; }, "Tail style"));
        }
        TextProps(t);
    }

    private void TextSection(TextAnnotation t)
    {
        var run = TextOf(t).Runs.FirstOrDefault() ?? new TextRun();
        HeroSwatch(ES.T("Props_TextColourShort", "Text"), run.Color, c => EditRuns(r => r with { Color = c }, "Text colour"));
        FillSection(t);
        TextProps(t, includeColour: false);
        Add(UiKit.Header(ES.T("Props_TextOutline", "Text outline")));
        var style = t.TextStyle;
        Swatch(ES.T("Props_Colour", "Colour"), style.OutlineColor ?? Argb.Black, c => EditText(a => a with { OutlineColor = c }, "Text outline colour"));
        Slider(ES.T("Props_Width", "Width"), 0, 12, style.OutlineWidth, "0.#", (v, k) => EditText(a => a with { OutlineWidth = v }, "Text outline width", k), 0.5);
        Check(ES.T("Props_AutoSize", "Resize box to fit text"), style.AutoSize, on => EditText(a => a with { AutoSize = on }, "Auto size"));
        Add(UiKit.Header(ES.T("Props_Border", "Border")));
        Swatch(ES.T("Props_Colour", "Colour"), t.Stroke.Color, c => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Color = c }, "Outline colour"));
        Slider(ES.T("Props_Width", "Width"), 0, 40, t.Stroke.Width, "0.#", (v, k) => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Width = v }, "Line width", k), 0.5);
        Row(ES.T("Props_Dash", "Style"), DashCombo(t.Stroke.Dash, d => EditAs<Annotation>(a => a.Stroke = a.Stroke with { Dash = d }, "Line style")));
    }

    private static RichText TextOf(Annotation a) => a is TextAnnotation ta ? ta.Text : a is CalloutAnnotation c ? c.Text : new RichText();
    private static TextLayoutStyle StyleOf(Annotation a) => a is TextAnnotation ta ? ta.TextStyle : a is CalloutAnnotation c ? c.TextStyle : TextLayoutStyle.Default;

    private void EditRuns(Func<TextRun, TextRun> map, string label, string? key = null, bool rebuild = false)
        => Edit(a =>
        {
            var rt = TextOf(a);
            if (rt.Runs.Count == 0) rt = new RichText(new[] { new TextRun() });
            var mapped = rt.MapRuns(map);
            if (a is TextAnnotation ta) ta.Text = mapped; else if (a is CalloutAnnotation c) c.Text = mapped;
        }, label, key, rebuild);

    private void EditText(Func<TextLayoutStyle, TextLayoutStyle> map, string label, string? key = null)
        => Edit(a =>
        {
            if (a is TextAnnotation ta) ta.TextStyle = map(ta.TextStyle);
            else if (a is CalloutAnnotation c) c.TextStyle = map(c.TextStyle);
        }, label, key);

    private void TextProps(Annotation t, bool includeColour = true)
    {
        var run = TextOf(t).Runs.FirstOrDefault() ?? new TextRun();
        var style = StyleOf(t);
        Add(UiKit.Header(ES.T("Props_Font", "Font")));
        var fonts = new ComboBox { MinWidth = 120, FontSize = 13 };
        foreach (var f in FontNames) fonts.Items.Add(f);
        fonts.SelectedItem = FontNames.FirstOrDefault(f => string.Equals(f, run.FontFamily, StringComparison.OrdinalIgnoreCase));
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) EditRuns(r => r with { FontFamily = f }, "Font"); };
        AutomationProperties.SetName(fonts, ES.T("Props_Family", "Font family"));
        Row(ES.T("Props_Family", "Family"), fonts);
        Slider(ES.T("Props_Size", "Size"), 6, 200, run.FontSize, "0", (v, k) => EditRuns(r => r with { FontSize = v }, "Font size", k));
        var fmt = new StackPanel { Orientation = Orientation.Horizontal };
        fmt.Children.Add(UiKit.Toggle("B", ES.T("Text_Bold", "Bold"), run.Bold, on => EditRuns(r => r with { Bold = on }, "Bold")));
        fmt.Children.Add(UiKit.Toggle("I", ES.T("Text_Italic", "Italic"), run.Italic, on => EditRuns(r => r with { Italic = on }, "Italic")));
        fmt.Children.Add(UiKit.Toggle("U", ES.T("Text_Underline", "Underline"), run.Underline, on => EditRuns(r => r with { Underline = on }, "Underline")));
        fmt.Children.Add(UiKit.Toggle("S", ES.T("Text_Strike", "Strikethrough"), run.Strikethrough, on => EditRuns(r => r with { Strikethrough = on }, "Strikethrough")));
        Row(ES.T("Props_Format", "Format"), fmt);
        if (includeColour) Swatch(ES.T("Props_TextColour", "Text colour"), run.Color, c => EditRuns(r => r with { Color = c }, "Text colour"));
        Add(UiKit.Header(ES.T("Props_Layout", "Layout")));
        var h = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, v, tip) in new[] { ("Icon.Text.AlignLeft", HorizontalAlign.Left, "Left"), ("Icon.Text.AlignCenter", HorizontalAlign.Center, "Center"), ("Icon.Text.AlignRight", HorizontalAlign.Right, "Right"), ("Icon.Text.AlignJustify", HorizontalAlign.Justify, "Justify") })
        {
            var value = v;
            var rb = new ToggleButton { Content = AlignGlyph(value), IsChecked = style.HAlign == v, Width = 32, Height = 30, Padding = new Thickness(0), ToolTip = ES.T("Align_" + tip, tip) }.Styled("Shotwise.GhostToggleButton");
            AutomationProperties.SetName(rb, ES.T("Align_" + tip, tip));
            rb.Click += (_, _) => EditText(s => s with { HAlign = value }, "Horizontal alignment");
            rb.Click += (_, _) => { foreach (var other in h.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, rb); };
            h.Children.Add(rb);
            _ = glyph;
        }
        Row(ES.T("Props_HAlign", "Horizontal"), h);
        Combo(ES.T("Props_VAlign", "Vertical"), EnumItems<VerticalAlign>(), style.VAlign, v => EditText(s => s with { VAlign = v }, "Vertical alignment"));
        Slider(ES.T("Props_Padding", "Padding"), 0, 60, style.Padding.Left, "0", (v, k) => EditText(s => s with { Padding = new Thickness2(v) }, "Padding", k));
        Slider(ES.T("Props_LineSpacing", "Line spacing"), 0.5, 3, style.LineSpacing, "0.0", (v, k) => EditText(s => s with { LineSpacing = v }, "Line spacing", k), 0.1);
    }

    /// <summary>Four-line alignment glyph drawn in the current foreground (no icon in the set covers text alignment).</summary>
    private static FrameworkElement AlignGlyph(HorizontalAlign a)
    {
        var lines = new[] { 14.0, 9, 14, 7 };
        var canvas = new Canvas { Width = 16, Height = 12, IsHitTestVisible = false };
        for (var i = 0; i < lines.Length; i++)
        {
            var w = a == HorizontalAlign.Justify ? 16 : lines[i] + 2;
            var x = a switch { HorizontalAlign.Center => (16 - w) / 2, HorizontalAlign.Right => 16 - w, _ => 0 };
            var r = new System.Windows.Shapes.Rectangle { Width = w, Height = 1.6, RadiusX = 0.8, RadiusY = 0.8 };
            r.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, i * 3.3);
            canvas.Children.Add(r);
        }
        return canvas;
    }

    private void StampSection()
    {
        // The browser itself lives above the (collapsible) Tool Properties section; here: size reset.
        var o = _s!.ToolOptions.Stamp;
        var reset = UiKit.GlyphTextButton(Icons.Glyph.Refresh, ES.T("Props_ResetStampSize", "Reset size"), () =>
        {
            if (!o.HasStamp) return;
            Options(() => o.NaturalSize = StampGallery.NaturalSize(o.Data!, o.MediaType!));
            if (_s.SelectedAnnotations().Any(a => a is StampAnnotation sa && sa.StampId == o.StampId))
                EditAs<StampAnnotation>(a =>
                {
                    if (a.StampId != o.StampId) return;
                    a.End = new Point2(a.Start.X + o.NaturalSize.Width, a.Start.Y + o.NaturalSize.Height);
                }, "Reset stamp size");
        });
        reset.ToolTip = ES.T("Props_ResetStampSizeTip", "Restore the stamp's natural size");
        Actions(reset);
    }

    private void StepSection(StepAnnotation t)
    {
        HeroSwatch(ES.T("Props_Color", "Color"), t.Fill.Color, c => Edit(a => a.Fill = FillStyle.Solid(c), "Step colour"));
        HeroSwatch(ES.T("Props_TextColourShort", "Text"), t.TextColor, c => EditAs<StepAnnotation>(a => a.TextColor = c, "Step text colour"));
        Combo(ES.T("Props_StepStyle", "Style"), EnumItems<StepStyle>(), t.Style, v => EditAs<StepAnnotation>(a =>
        {
            a.Style = v;
            if (v is StepStyle.CirclePointer or StepStyle.SquarePointer) a.PointerTip ??= new Point2(a.End.X + 20, a.End.Y + 20);
            else a.PointerTip = null;
        }, "Step style"));
        Combo(ES.T("Props_StepSequence", "Sequence"), new[] { ("1, 2, 3", StepSequence.Number), ("A, B, C", StepSequence.Upper), ("a, b, c", StepSequence.Lower) }, t.Sequence, v => EditAs<StepAnnotation>(a => a.Sequence = v, "Step sequence"));
        var next = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        next.Children.Add(UiKit.Label(ES.T("Props_NextStepLabel", "Next value")));
        var chip = UiKit.Chip(StepAnnotation.FormatValue(_s!.NextStepValue(t.Sequence), t.Sequence), accent: true);
        chip.Margin = new Thickness(8, 0, 0, 0);
        next.Children.Add(chip);
        Add(next);
        Actions(UiKit.GlyphTextButton(Icons.Glyph.Refresh, ES.T("Props_RestartSequence", "Restart sequence"), () => { _s!.RestartStepSequence(t.Sequence, 1); Rebuild(); }));
    }

    private void PenSection(PenAnnotation t)
    {
        StrokeSection(t, true, false, widthCombo: true);
        Add(UiKit.Header(ES.T("Props_Pen", "Pen")));
        Combo(ES.T("Props_Nib", "Nib"), EnumItems<PenNib>(), t.Nib, v => EditAs<PenAnnotation>(a => a.Nib = v, "Pen nib"));
        Check(ES.T("Props_Smooth", "Smooth"), t.Smoothing, on => EditAs<PenAnnotation>(a => a.Smoothing = on, "Smoothing"));
        Check(ES.T("Props_Antialias", "Antialias"), t.Antialias, on => EditAs<PenAnnotation>(a => a.Antialias = on, "Antialias"));
        Check(ES.T("Props_CreateAsVector", "Create as vector"), _s!.ToolOptions.Pen.CreateAsVector, on => Options(() => _s!.ToolOptions.Pen.CreateAsVector = on));
    }

    private void HighlightSection(HighlightAnnotation t)
    {
        HeroSwatch(ES.T("Props_Color", "Color"), t.Fill.Color, c => EditAs<HighlightAnnotation>(a => { a.Fill = FillStyle.Solid(c); a.Stroke = a.Stroke with { Color = c }; }, "Highlight colour"));
        HeroCombo(WidthCombo(t.Stroke.Width, v => EditAs<HighlightAnnotation>(a => a.Stroke = a.Stroke with { Width = v }, "Highlight width", null, true)), ES.T("Props_Width", "Width"));
        Slider(ES.T("Props_Width", "Width"), 2, 80, t.Stroke.Width, "0", (v, k) => EditAs<HighlightAnnotation>(a => a.Stroke = a.Stroke with { Width = v }, "Highlight width", k));
        Check(ES.T("Props_Freehand", "Freehand"), _s!.ToolOptions.Highlighter.Freehand, on => Options(() => _s!.ToolOptions.Highlighter.Freehand = on));
    }

    private void MagnifySection(MagnifyAnnotation t)
    {
        StrokeSection(t, true, false, caption: ES.T("Props_Outline", "Outline"));
        Combo(ES.T("Props_Lens", "Lens"), new[] { (ES.T("Shape_Ellipse", "Ellipse"), ShapeKind.Ellipse), (ES.T("Shape_Rect", "Rectangle"), ShapeKind.Rectangle), (ES.T("Shape_Rounded", "Rounded rectangle"), ShapeKind.RoundedRectangle) },
            t.LensShape, v => EditAs<MagnifyAnnotation>(a => a.LensShape = v, "Lens shape"));
        Slider(ES.T("Props_Scale", "Zoom"), 100, 800, t.Scale * 100, "0'%'", (v, k) => EditAs<MagnifyAnnotation>(a => a.Scale = v / 100, "Magnification", k), 10);
        Check(ES.T("Props_Connector", "Show connector"), t.ShowConnector, on => EditAs<MagnifyAnnotation>(a => a.ShowConnector = on, "Connector"));
    }

    private void BlurSection(BlurAnnotation t)
    {
        var modes = new[] { (ES.T("Blur_Blur", "Blur"), BlurKind.Gaussian, "Icon.Tool.Blur.Regular"), (ES.T("Blur_Pixelate", "Pixelate"), BlurKind.Pixelate, "Icon.Grid.Regular"), (ES.T("Blur_Solid", "Solid"), BlurKind.Solid, "Icon.Color.Regular") };
        var seg = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (name, mode, icon) in modes)
        {
            var m = mode;
            var b = new ToggleButton { Content = name, IsChecked = t.Mode == mode, Margin = new Thickness(0, 0, 4, 0) }.Styled("Shotwise.GhostToggleButton");
            SwUi.SetIcon(b, icon);
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => EditAs<BlurAnnotation>(a => a.Mode = m, "Blur type", null, true);
            seg.Children.Add(b);
        }
        Add(seg);
        Combo(ES.T("Props_Region", "Region"), new[] { (ES.T("Shape_Rect", "Rectangle"), ShapeKind.Rectangle), (ES.T("Shape_Rounded", "Rounded rectangle"), ShapeKind.RoundedRectangle), (ES.T("Shape_Ellipse", "Ellipse"), ShapeKind.Ellipse) }, t.Region, v => EditAs<BlurAnnotation>(a => a.Region = v, "Blur shape"));
        if (t.Mode != BlurKind.Solid)
            Slider(ES.T("Props_Intensity", "Intensity"), 1, 100, t.Intensity, "0", (v, k) => EditAs<BlurAnnotation>(a => a.Intensity = v, "Blur intensity", k));
        else
            HeroSwatch(ES.T("Props_Colour", "Colour"), t.SolidColor, c => EditAs<BlurAnnotation>(a => a.SolidColor = c, "Cover colour"), false);
        Actions(UiKit.GlyphTextButton("Icon.Tool.Blur.Color", ES.T("Props_SmartRedact", "Smart Redact…"), () => PaneRequested?.Invoke("redact")));
    }

    private void CursorSection(CursorAnnotation t)
    {
        Combo(ES.T("Props_CursorKind", "Cursor"), EnumItems<CursorKind>(), t.Cursor, v => EditAs<CursorAnnotation>(a => a.Cursor = v, "Cursor kind"));
        Slider(ES.T("Props_Scale", "Scale"), 50, 400, t.Scale * 100, "0'%'", (v, k) => EditAs<CursorAnnotation>(a => a.Scale = v / 100, "Cursor scale", k), 5);
    }

    private void SimplifySection()
    {
        var o = _s!.ToolOptions.Simplify;
        Combo(ES.T("Props_Mode", "Mode"), new[] { (ES.T("Simplify_Draw", "Draw"), SimplifyToolMode.Draw), (ES.T("Simplify_Move", "Move"), SimplifyToolMode.Move) }, o.Mode, v => Options(() => o.Mode = v));
        Combo(ES.T("Props_ShapeKind", "Shape"), EnumItems<SimplifyShapeKind>(), o.Shape, v => Options(() => o.Shape = v));
        var palette = new WrapPanel();
        for (var i = 0; i < 8; i++)
        {
            var index = i;
            var color = _s.SimplifyColor(i);
            var b = new ToggleButton { Width = 28, Height = 28, Margin = new Thickness(0, 0, 2, 2), IsChecked = o.PaletteIndex == i, Content = new Border { Background = color.ToBrush(), CornerRadius = new CornerRadius(4), Width = 18, Height = 18 }, ToolTip = color.ToRgbHex() }.Styled("Shotwise.ShadowCell");
            AutomationProperties.SetName(b, ES.F("Props_PaletteColour", "Palette colour {0}", i + 1));
            b.Click += (_, _) => Options(() => o.PaletteIndex = index, true);
            palette.Children.Add(b);
        }
        Row(ES.T("Props_Palette", "Palette"), palette);
        Actions(UiKit.GlyphTextButton("Icon.Tool.Simplify.Color", ES.T("Props_AutoSimplify", "Auto Simplify…"), () => PaneRequested?.Invoke("simplify")));
    }

    // ------------------------------------------------------------------ non-annotation tools

    private void BuildToolOptions(EditorSession s)
    {
        var o = s.ToolOptions;
        switch (s.ActiveTool)
        {
            case ToolKind.Fill:
                HeroSwatch(ES.T("Props_Fill", "Fill"), o.Fill.Color, c => Options(() => o.Fill.Color = c));
                Slider(ES.T("Props_Tolerance", "Tolerance"), 0, 100, o.Fill.Tolerance, "0'%'", (v, _) => Options(() => o.Fill.Tolerance = v));
                Slider(ES.T("Props_Opacity", "Opacity"), 0, 100, o.Fill.Opacity * 100, "0'%'", (v, _) => Options(() => o.Fill.Opacity = v / 100));
                Check(ES.T("Props_Transparent", "Fill with transparency"), o.Fill.Transparent, on => Options(() => o.Fill.Transparent = on));
                Check(ES.T("Props_GlobalColour", "Global colour fill"), o.Fill.Global, on => Options(() => o.Fill.Global = on));
                Add(UiKit.Hint(ES.T("Props_FillHint", "Alt+click picks a colour from the image. Right-click sets the canvas colour.")));
                break;
            case ToolKind.Eraser:
                Slider(ES.T("Props_Width", "Width"), 1, 200, o.Eraser.Width, "0", (v, _) => Options(() => o.Eraser.Width = v));
                Slider(ES.T("Props_Opacity", "Opacity"), 0, 100, o.Eraser.Opacity * 100, "0'%'", (v, _) => Options(() => o.Eraser.Opacity = v / 100));
                Combo(ES.T("Props_EraseTo", "Erase to"), new[] { (ES.T("Erase_Transparent", "Transparent"), EraseMode.Transparent), (ES.T("Erase_Canvas", "Canvas colour"), EraseMode.CanvasColor) }, o.Eraser.Mode, v => Options(() => o.Eraser.Mode = v));
                Check(ES.T("Props_Antialias", "Antialias"), o.Eraser.Antialias, on => Options(() => o.Eraser.Antialias = on));
                break;
            case ToolKind.Selection:
                Combo(ES.T("Props_Shape", "Shape"), new[] { (ES.T("Shape_Rect", "Rectangle"), PixelSelectionShape.Rectangle), (ES.T("Shape_Ellipse", "Ellipse"), PixelSelectionShape.Ellipse), (ES.T("Shape_Polygon", "Polygon"), PixelSelectionShape.Polygon), (ES.T("Shape_Freehand", "Freehand"), PixelSelectionShape.Freehand) }, o.Selection.Shape, v => Options(() => o.Selection.Shape = v));
                Check(ES.T("Props_SnapObject", "Snap to object"), o.Selection.SnapToObject, on => Options(() => o.Selection.SnapToObject = on));
                PixelOptions(s, o.Selection.Background, o.Selection.HighQuality, b => o.Selection.Background = b, q => o.Selection.HighQuality = q);
                PixelActions(s);
                break;
            case ToolKind.MagicWand:
                Slider(ES.T("Props_Tolerance", "Tolerance"), 0, 100, o.MagicWand.Tolerance, "0'%'", (v, _) => Options(() => o.MagicWand.Tolerance = v));
                Slider(ES.T("Props_Refine", "Refine edge"), 0, 10, o.MagicWand.RefineRadius, "0", (v, _) => Options(() => o.MagicWand.RefineRadius = (int)v));
                Check(ES.T("Props_GlobalColour", "Global colour selection"), o.MagicWand.GlobalColor, on => Options(() => o.MagicWand.GlobalColor = on));
                PixelOptions(s, o.MagicWand.Background, o.MagicWand.HighQuality, b => o.MagicWand.Background = b, q => o.MagicWand.HighQuality = q);
                PixelActions(s);
                break;
            case ToolKind.Crop: BuildCrop(s); break;
            case ToolKind.CutOut:
                Combo(ES.T("Props_Direction", "Direction"), new[] { (ES.T("CutOut_Horizontal", "Horizontal band"), CutOutOrientation.Horizontal), (ES.T("CutOut_Vertical", "Vertical band"), CutOutOrientation.Vertical) }, o.CutOut.Orientation, v => Options(() => o.CutOut.Orientation = v));
                Combo(ES.T("Props_Edge", "Edge"), EnumItems<CutOutEdgeStyle>(), o.CutOut.Edge, v => Options(() => o.CutOut.Edge = v));
                Add(UiKit.Hint(ES.T("Props_CutOutHint", "Drag across the image to mark the strip to remove.")));
                break;
            case ToolKind.GrabText:
                Check(ES.T("Props_EditTextMode", "Edit text (replace text with editable objects)"), o.GrabText.EditText, on => Options(() => o.GrabText.EditText = on));
                if (_ctx.Ocr is { AvailableLanguages.Count: > 0 } ocr)
                    Combo(ES.T("Props_Language", "Language"), ocr.AvailableLanguages.Select(l => (l, l)), o.GrabText.Language ?? ocr.AvailableLanguages[0], v => Options(() => o.GrabText.Language = v));
                Actions(UiKit.PrimaryButton(ES.T("Props_GrabNow", "Grab text from whole image"), "Icon.Tool.GrabText.Regular", () => UiKit.Fire(async () =>
                {
                    if (o.GrabText.EditText) await s.EditTextAsync(); else await s.GrabTextAsync();
                }, ex => _ = _ctx.ShowErrorAsync(ex))));
                break;
            case ToolKind.Simplify: SimplifySection(); break;
        }
    }

    private void PixelOptions(EditorSession s, SelectionBackground bg, bool hq, Action<SelectionBackground> setBg, Action<bool> setQ)
    {
        Combo(ES.T("Props_Background", "Background"), new[] { (ES.T("Bg_AutoFill", "Auto-fill"), SelectionBackground.AutoFill), (ES.T("Bg_Transparent", "Transparent"), SelectionBackground.Transparent) }, bg, v => Options(() => setBg(v)));
        Combo(ES.T("Props_Quality", "Quality"), new[] { (ES.T("Quality_Speed", "Speed"), false), (ES.T("Quality_Photo", "Photo quality"), true) }, hq, v =>
        {
            Options(() => setQ(v));
            if (v) OfferInpaintingModel();
        });
        if (hq) InpaintingModelHint();
    }

    private const string InpaintingModelId = "lama";

    private bool InpaintingModelMissing => _ctx.Models is { } m && !m.IsAvailable(InpaintingModelId) && m.Models.Any(x => x.Id == InpaintingModelId);

    /// <summary>Photo-quality fill uses the downloadable LaMa model; offer it instead of silently using the basic fill.</summary>
    private void OfferInpaintingModel()
    {
        if (!InpaintingModelMissing) return;
        UiKit.Fire(async () =>
        {
            await _ctx.EnsureModelAsync(InpaintingModelId, ES.T("Model_LamaReason", "Photo-quality fill (Smart Move, Edit Text, selection auto-fill) needs the LaMa inpainting model. Without it a basic fill is used."));
            Rebuild();
        }, ex => _ = _ctx.ShowErrorAsync(ex));
    }

    private void InpaintingModelHint()
    {
        if (!InpaintingModelMissing) return;
        var bar = new InfoBar
        {
            Severity = InfoSeverity.Warning,
            Message = ES.T("Model_LamaMissing", "The photo-quality model is not installed; a basic fill is used."),
            IsClosable = false,
            Margin = new Thickness(0, 6, 0, 6),
            Action = UiKit.GhostButton(ES.T("Model_Download", "Download model…"), Icons.Glyph.Download, OfferInpaintingModel),
        };
        Add(bar);
    }

    private void PixelActions(EditorSession s)
        => Actions(
            UiKit.TextButton(ES.T("Menu_SelectAll", "Select all"), s.SelectAllPixels),
            UiKit.TextButton(ES.T("Menu_Deselect", "Deselect"), s.DeselectAll),
            UiKit.GlyphTextButton(Icons.Glyph.Delete, ES.T("Menu_Delete", "Delete"), () => UiKit.Fire(() => s.DeletePixelsAsync(), ex => _ = _ctx.ShowErrorAsync(ex))));

    private void BuildCrop(EditorSession s)
    {
        var aspects = new (string, double)[] { (ES.T("Crop_Free", "Free"), 0), ("1:1", 1.0), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0), ("16:10", 1.6), ("3:4", 0.75), ("9:16", 9 / 16.0) };
        var current = s.ToolOptions.Crop.AspectRatio ?? 0;
        var match = aspects.FirstOrDefault(a => Math.Abs(a.Item2 - current) < 1e-6);
        Combo(ES.T("Props_Aspect", "Aspect ratio"), aspects, match.Item2, v => Options(() => s.ToolOptions.Crop.AspectRatio = v > 0 ? v : null));
        if (s.CurrentTool is CropTool crop)
        {
            var r = crop.CropRect;
            var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            size.Children.Add(UiKit.Label(ES.T("Props_CropSize", "Size")));
            var chip = UiKit.Chip($"{Math.Round(r.Width)} × {Math.Round(r.Height)} px");
            chip.Margin = new Thickness(8, 0, 0, 0);
            size.Children.Add(chip);
            Add(size);
        }
        var reset = UiKit.TextButton(ES.T("Crop_Reset", "Reset crop"), () => { s.ResetCrop(); Rebuild(); });
        reset.IsEnabled = s.CanResetCrop;
        Actions(
            UiKit.PrimaryButton(ES.T("Crop_Apply", "Apply"), Icons.Glyph.Accept, () => { (s.CurrentTool as CropTool)?.Apply(); Rebuild(); }),
            UiKit.TextButton(ES.T("Crop_Cancel", "Cancel"), () => { (s.CurrentTool as CropTool)?.Reset(); Rebuild(); }),
            reset);
        Check(ES.T("Props_DeleteCropped", "Delete cropped content"), _ctx.Prefs.DeleteCroppedContent, on => _ctx.Settings.Update(x => x.Editor.DeleteCroppedContent = on));
    }

    private void BuildSmartMove(EditorSession s)
    {
        var o = s.ToolOptions.Move;
        Add(UiKit.Header(ES.T("Props_SmartMove", "Smart Move")));
        Check(ES.T("Props_SmartMoveOn", "Detect interface elements"), o.SmartMove, on => Options(() => o.SmartMove = on, true));
        if (o.SmartMove)
        {
            Check(ES.T("Props_AutoDetail", "Automatic detail"), o.AutoTolerance, on => Options(() => o.AutoTolerance = on, true));
            if (!o.AutoTolerance)
                Slider(ES.T("Props_Detail", "Detail"), 0, 100, o.Tolerance * 100, "0", (v, _) => Options(() => o.Tolerance = v / 100));
            Check(ES.T("Props_PhotoQuality", "Photo quality fill"), o.HighQuality, on =>
            {
                Options(() => o.HighQuality = on);
                if (on) OfferInpaintingModel();
            });
            if (o.HighQuality) InpaintingModelHint();
            Add(UiKit.Hint(ES.F("Props_ComponentsFound", "{0} objects found", s.SmartComponents.Count)));
        }
    }

    private void BuildCanvasProperties(EditorSession s)
    {
        var page = s.ActivePage;
        HeroSwatch(ES.T("Props_CanvasColourShort", "Canvas"), page.Background.Color, c => { _busy = true; try { s.SetCanvasColor(c); } finally { _busy = false; } });
        var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        size.Children.Add(UiKit.Label(ES.T("Props_CanvasSize", "Canvas size")));
        var chip = UiKit.Chip($"{page.CanvasSize.Width:0} × {page.CanvasSize.Height:0} px");
        chip.Margin = new Thickness(8, 0, 0, 0);
        size.Children.Add(chip);
        Add(size);
        Slider(ES.T("Props_CornerRadius", "Corner radius"), 0, 200, page.CornerRadius, "0", (v, _) => { _busy = true; try { s.SetCanvasStyle(v, page.CanvasShadow); } finally { _busy = false; } });
        Check(ES.T("Props_CanvasShadow", "Canvas shadow"), page.CanvasShadow is not null, on => { _busy = true; try { s.SetCanvasStyle(page.CornerRadius, on ? ShadowStyle.Default : null); } finally { _busy = false; } Rebuild(); });
        Check(ES.T("Props_AutoExpand", "Auto-expand canvas"), _ctx.Prefs.AutoExpandCanvas, on => _ctx.Settings.Update(x => x.Editor.AutoExpandCanvas = on));
    }
}
