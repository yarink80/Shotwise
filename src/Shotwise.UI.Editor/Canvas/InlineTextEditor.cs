using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Annotations;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Editor;
using Shotwise.UI.Common.Imaging;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Surface;

/// <summary>
/// Transparent in-place rich text editor (RichTextBox) laid over the canvas for the text-edit protocol
/// (EditorSession.BeginTextEdit → UpdateTextEdit → CommitTextEdit). Scaled and rotated to match the annotation.
/// </summary>
internal sealed class InlineTextEditor : System.Windows.Controls.Canvas
{
    private readonly RichTextBox _box;
    private readonly Border _barHost;
    private readonly ToggleButton _bold, _italic, _underline, _strike;
    private readonly ComboBox _fonts, _sizes;
    private readonly ColorSwatch _color;
    private TextEditRequest? _request;
    private TextRun _baseRun = new();
    private bool _loading;

    public InlineTextEditor()
    {
        IsHitTestVisible = true;
        Visibility = Visibility.Collapsed;
        ClipToBounds = false;

        _box = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            AcceptsTab = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsUndoEnabled = true,
            FocusVisualStyle = null,
            CaretBrush = Brushes.DodgerBlue,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(120, 30, 136, 229)),
            Style = null,
        };
        _box.SetValue(SpellCheck.IsEnabledProperty, true);
        _box.LostKeyboardFocus += OnLostFocus;
        _box.PreviewKeyDown += OnBoxKeyDown;
        _box.TextChanged += (_, _) => OnTextChanged();
        _box.SelectionChanged += (_, _) => SyncBar();

        _bold = UiKit.Toggle("B", ES.T("Text_Bold", "Bold (Ctrl+B)"), false, v => Apply(TextElement.FontWeightProperty, v ? FontWeights.Bold : FontWeights.Normal));
        _italic = UiKit.Toggle("I", ES.T("Text_Italic", "Italic (Ctrl+I)"), false, v => Apply(TextElement.FontStyleProperty, v ? FontStyles.Italic : FontStyles.Normal));
        _underline = UiKit.Toggle("U", ES.T("Text_Underline", "Underline (Ctrl+U)"), false, v => ApplyDecoration(TextDecorationLocation.Underline, v));
        _strike = UiKit.Toggle("S", ES.T("Text_Strike", "Strikethrough"), false, v => ApplyDecoration(TextDecorationLocation.Strikethrough, v));
        _fonts = new ComboBox { Width = 140, IsEditable = false, Margin = new Thickness(1) };
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)) _fonts.Items.Add(f);
        _fonts.SelectionChanged += (_, _) =>
        {
            if (!_loading && _fonts.SelectedItem is string s) Apply(TextElement.FontFamilyProperty, new FontFamily(s));
        };
        _sizes = new ComboBox { Width = 64, IsEditable = true, Margin = new Thickness(1) };
        foreach (var s in new[] { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72, 96 }) _sizes.Items.Add(s.ToString());
        _sizes.SelectionChanged += (_, _) =>
        {
            if (!_loading && _sizes.SelectedItem is string s && double.TryParse(s, out var size)) Apply(TextElement.FontSizeProperty, size);
        };
        _color = new ColorSwatch(Argb.Black, true, ES.T("Text_Color", "Text colour"));
        _color.ColorChanged += c => Apply(TextElement.ForegroundProperty, c.ToBrush());

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement e in new UIElement[] { _fonts, _sizes, _bold, _italic, _underline, _strike, _color }) bar.Children.Add(e);
        _barHost = new Border { Child = bar, Padding = new Thickness(4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        _barHost.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        _barHost.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        Children.Add(_box);
        Children.Add(_barHost);
    }

    /// <summary>
    /// Editor ▸ "Enable spell check" and "Spell check language" (EDT-10): toggles WPF spell checking (Windows spell
    /// checker) and sets the proofing language — the preference, else the UI language, else the current culture.
    /// </summary>
    public void ApplyPreferences(Shotwise.Core.Settings.EditorSettings prefs, string? uiLanguage)
    {
        _box.SetValue(SpellCheck.IsEnabledProperty, prefs.EnableSpellCheck);
        var tag = SpellCheckLanguageTag(prefs.SpellCheckLanguage, uiLanguage);
        try { _box.Language = System.Windows.Markup.XmlLanguage.GetLanguage(tag); }
        catch (ArgumentException) { _box.Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag); }
    }

    internal static string SpellCheckLanguageTag(string? preference, string? uiLanguage)
    {
        foreach (var candidate in new[] { preference, uiLanguage })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return System.Globalization.CultureInfo.GetCultureInfo(candidate.Trim()).IetfLanguageTag; }
            catch (System.Globalization.CultureNotFoundException) { }
        }
        var current = System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag;
        return string.IsNullOrEmpty(current) ? "en-US" : current;
    }

    /// <summary>User pressed Esc / clicked away / changed focus: commit the text (protocol §4.3).</summary>
    public event Action<RichText>? CommitRequested;

    public event Action<RichText>? TextEdited;

    public bool IsEditing => Visibility == Visibility.Visible && _request is not null;

    public RichText CurrentText => RichTextConversion.FromDocument(_box.Document, _baseRun);

    /// <summary>Positions the editor for <paramref name="r"/> at the given view transform (zoom in device px per canvas px).</summary>
    public void Begin(TextEditRequest r, double zoom, Point origin, double dpi)
    {
        _request = r;
        _loading = true;
        try
        {
            _baseRun = r.Text.Runs.FirstOrDefault() ?? new TextRun();
            _box.Document = RichTextConversion.ToDocument(r.Text, r.Style);
            _box.FontFamily = new FontFamily(_baseRun.FontFamily);
            _box.FontSize = _baseRun.FontSize;
            _box.FontWeight = _baseRun.Bold ? FontWeights.Bold : FontWeights.Normal;
            _box.FontStyle = _baseRun.Italic ? FontStyles.Italic : FontStyles.Normal;
            _box.Foreground = _baseRun.Color.ToBrush();
            _box.Width = Math.Max(8, r.Bounds.Width);
            _box.MinHeight = Math.Max(8, r.Bounds.Height);
            _box.Document.PageWidth = double.NaN;
            _box.VerticalContentAlignment = r.Style.VAlign switch { VerticalAlign.Middle => VerticalAlignment.Center, VerticalAlign.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Top };
            if (!r.Style.AutoSize) _box.Height = Math.Max(8, r.Bounds.Height); else _box.ClearValue(HeightProperty);
        }
        finally { _loading = false; }
        Visibility = Visibility.Visible;
        Reposition(zoom, origin, dpi);
        _box.Focus();
        Keyboard.Focus(_box);
        var caret = r.CaretIndex;
        if (caret is { } c) SetCaret(c); else _box.CaretPosition = _box.Document.ContentEnd;
        if (r.IsNew) _box.SelectAll();
        SyncBar();
    }

    public void Reposition(double zoom, Point origin, double dpi)
    {
        if (_request is not { } r) return;
        var scale = zoom / dpi;
        var tl = new Point((origin.X + r.Bounds.X * zoom) / dpi, (origin.Y + r.Bounds.Y * zoom) / dpi);
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(scale, scale));
        if (Math.Abs(r.RotationDegrees) > 0.001) group.Children.Add(new RotateTransform(r.RotationDegrees, r.Bounds.Width * scale / 2, r.Bounds.Height * scale / 2));
        group.Children.Add(new TranslateTransform(tl.X, tl.Y));
        _box.RenderTransform = group;
        _box.RenderTransformOrigin = new Point(0, 0);
        SetLeft(_box, 0);
        SetTop(_box, 0);
        SetLeft(_barHost, Math.Max(0, tl.X));
        SetTop(_barHost, Math.Max(0, tl.Y - 44));
    }

    public void End()
    {
        _request = null;
        Visibility = Visibility.Collapsed;
    }

    private void SetCaret(int index)
    {
        var pos = _box.Document.ContentStart;
        var remaining = index;
        foreach (var block in _box.Document.Blocks.OfType<Paragraph>())
        {
            var text = new TextRange(block.ContentStart, block.ContentEnd).Text;
            if (remaining <= text.Length)
            {
                pos = block.ContentStart.GetPositionAtOffset(0) ?? pos;
                var tp = block.ContentStart;
                var guard = 0;
                while (remaining > 0 && tp is not null && guard++ < 100000)
                {
                    var next = tp.GetNextInsertionPosition(LogicalDirection.Forward);
                    if (next is null) break;
                    tp = next;
                    remaining--;
                }
                pos = tp ?? pos;
                break;
            }
            remaining -= text.Length + 1;
            pos = block.ContentEnd;
        }
        _box.CaretPosition = pos;
    }

    private void OnTextChanged()
    {
        if (_loading || _request is null) return;
        TextEdited?.Invoke(CurrentText);
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CommitRequested?.Invoke(CurrentText);
        }
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_request is null) return;
        // Focus moved into the format bar (font box, colour dialog, …) is not "click-away".
        if (e.NewFocus is DependencyObject d && (IsInside(d, _barHost) || IsInside(d, _box))) return;
        if (_color.IsMouseCaptured || _fonts.IsDropDownOpen || _sizes.IsDropDownOpen) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_request is not null && !_box.IsKeyboardFocusWithin && !_barHost.IsKeyboardFocusWithin && !_barHost.IsMouseOver)
                CommitRequested?.Invoke(CurrentText);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var c = child; c is not null; c = c is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
            if (ReferenceEquals(c, parent)) return true;
        return false;
    }

    private void Apply(DependencyProperty dp, object value)
    {
        if (_loading) return;
        var sel = _box.Selection;
        sel.ApplyPropertyValue(dp, value);
        _box.Focus();
    }

    private void ApplyDecoration(TextDecorationLocation loc, bool on)
    {
        if (_loading) return;
        var sel = _box.Selection;
        var current = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection ?? new TextDecorationCollection();
        var updated = new TextDecorationCollection(current.Where(d => d.Location != loc));
        if (on) updated.Add(loc == TextDecorationLocation.Underline ? TextDecorations.Underline[0] : TextDecorations.Strikethrough[0]);
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, updated);
        _box.Focus();
    }

    private void SyncBar()
    {
        _loading = true;
        try
        {
            var sel = _box.Selection;
            _bold.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
            _italic.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s == FontStyles.Italic;
            var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            _underline.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
            _strike.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
            if (sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily ff) _fonts.SelectedItem = ff.Source;
            if (sel.GetPropertyValue(TextElement.FontSizeProperty) is double size) _sizes.Text = Math.Round(size).ToString();
            if (sel.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush b) _color.Color = b.Color.ToArgb();
        }
        finally { _loading = false; }
    }
}
