using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shotwise.UI.Common.Controls;
using Button = System.Windows.Controls.Button;

namespace Shotwise.UI.Dialogs;

/// <summary>
/// Builds one hand-laid-out settings page: section headings, captioned rows (caption left, editor right), check boxes labelled by
/// their own content only, and page-local action buttons. Every editor is bound to a getter/setter pair on the staged settings copy.
/// </summary>
internal sealed class PageBuilder
{
    public const double CaptionWidth = 250;
    public const double PageWidth = 600;
    private Panel Current => section ?? Root;
    private StackPanel? section;
    private readonly Action changed;
    private readonly Action<Exception> fail;

    public PageBuilder(Action changed, Action<Exception> fail) { this.changed = changed; this.fail = fail; }

    public StackPanel Root { get; } = new() { Margin = new Thickness(4, 0, 12, 16), MaxWidth = PageWidth, HorizontalAlignment = HorizontalAlignment.Left };

    public ScrollViewer Wrap() => new() { Content = Root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };

    public PageBuilder Section(string key)
    {
        section = new StackPanel();
        var header = new PanelHeader { Content = Texts.Get(key), Margin = new Thickness(0, 0, 0, 8) };
        section.Children.Add(header);
        Root.Children.Add(new Card { Content = section, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16), Width = PageWidth });
        return this;
    }

    public TextBlock Note(string text, bool localized = true)
    {
        var t = new TextBlock { Text = localized ? Texts.Get(text) : text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };
        Current.Children.Add(t); return t;
    }

    public ToggleSwitchRow Check(string key, Func<bool> get, Action<bool> set, string? tooltipKey = null)
    {
        var caption = Texts.Get(key);
        var box = new ToggleSwitchRow { Content = caption, IsChecked = get(), Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (_, _) => { set(true); changed(); };
        box.Unchecked += (_, _) => { set(false); changed(); };
        if (tooltipKey is not null) box.ToolTip = Texts.Get(tooltipKey);
        AutomationProperties.SetName(box, caption);
        Current.Children.Add(box); return box;
    }

    public TextBox Number(string key, Func<int> get, Action<int> set, int min, int max, string? suffixKey = null)
    {
        var box = new TextBox { Text = get().ToString(CultureInfo.CurrentCulture), Width = 90, MinHeight = 30 };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max)
            {
                box.ClearValue(Control.BorderBrushProperty);
                if (v != get()) { set(v); changed(); }
            }
            else box.SetResourceReference(Control.BorderBrushProperty, "Shotwise.DangerBrush");
        };
        var inline = new StackPanel { Orientation = Orientation.Horizontal };
        inline.Children.Add(box);
        if (suffixKey is not null) inline.Children.Add(new TextBlock { Text = " " + Texts.Get(suffixKey), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        Row(key, inline, box); return box;
    }

    public TextBox Text(string key, Func<string> get, Action<string> set, double width = 300, string? placeholderKey = null)
    {
        var box = new TextBox { Text = get(), Width = width, MinHeight = 30 };
        if (placeholderKey is not null) box.ToolTip = Texts.Get(placeholderKey);
        box.TextChanged += (_, _) => { if (box.Text != get()) { set(box.Text); changed(); } };
        Row(key, box, box); return box;
    }

    /// <summary>Combo of values shown by <paramref name="text"/> (default: localized enum name / ToString).</summary>
    public ComboBox Choice<T>(string key, IEnumerable<T> items, Func<T> get, Action<T> set, Func<T, string>? text = null, double width = 220)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), Width = width, MinHeight = 30 };
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new FuncConverter(v => v is null ? "" : text is not null ? text((T)v) : v is Enum e ? Texts.Enum(e) : v.ToString() ?? "") });
        template.VisualTree = factory; combo.ItemTemplate = template;
        combo.SelectedItem = get();
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is T value && !EqualityComparer<T>.Default.Equals(value, get())) { set(value); changed(); } };
        Row(key, combo, combo); return combo;
    }

    public Button Action(string key, Func<Task> action, Panel? host = null)
    {
        var b = new Button { Content = Texts.Get(key), Margin = new Thickness(0, 4, 8, 4), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { fail(ex); }
            finally { b.IsEnabled = true; }
        };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.SecondaryButton");
        (host ?? Current).Children.Add(b); return b;
    }

    /// <summary>A horizontal wrap of action buttons (each created through <see cref="Action"/>).</summary>
    public WrapPanel ButtonRow()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) }; Current.Children.Add(row); return row;
    }

    /// <summary>Caption on the left, editor on the right.</summary>
    public void Row(string key, FrameworkElement editor, FrameworkElement? automationTarget = null)
    {
        var caption = Texts.Get(key);
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(automationTarget ?? editor, caption);
        var property = new PropertyRow { Label = caption, LabelWidth = CaptionWidth, Content = editor, Margin = new Thickness(0, 4, 0, 4) };
        Current.Children.Add(property); LastRow = property;
    }

    /// <summary>The row (caption + editor) most recently added by Row/Text/Number/Choice; lets pages show/hide dependent rows.</summary>
    public FrameworkElement? LastRow { get; private set; }

    public void Add(UIElement element) => Current.Children.Add(element);
}

internal sealed class FuncConverter(Func<object?, string> convert) : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => convert(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
