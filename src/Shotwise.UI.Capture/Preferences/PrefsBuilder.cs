using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>
/// Builder for preference pages: every section is a design-system card with a PanelHeader; options are ToggleSwitchRows, number and
/// choice rows (label left, control right) bound to getter/setter pairs.
/// </summary>
internal sealed class PrefsBuilder
{
    private readonly Action _changed;
    private StackPanel? _current;

    public PrefsBuilder(Action changed)
    {
        Ui.EnsureStyles();
        _changed = changed;
    }

    public StackPanel Root { get; } = new() { Margin = new Thickness(4, 4, 16, 16) };

    public ScrollViewer Wrap() => new() { Content = Root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };

    private StackPanel Current
    {
        get
        {
            if (_current is null) Section("");
            return _current!;
        }
    }

    public PrefsBuilder Section(string title, string? iconKey = null)
    {
        var col = new StackPanel();
        if (!string.IsNullOrEmpty(title))
        {
            var header = new PanelHeader { Content = title, Margin = new Thickness(4, 0, 0, 6) };
            col.Children.Add(header);
        }
        var card = Ui.Card(col, new Thickness(10, 10, 10, 8), elevation: 0, radius: 10);
        card.Margin = new Thickness(0, Root.Children.Count == 0 ? 0 : 14, 0, 0);
        Root.Children.Add(card);
        _current = col;
        return this;
    }

    public PrefsBuilder Note(string text)
    {
        var t = Ui.Label(text, 12, secondary: true, wrap: true);
        t.Margin = new Thickness(6, 6, 6, 4);
        Current.Children.Add(t);
        return this;
    }

    public ToggleButton Check(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        var c = Ui.Switch(label, get(), v => { set(v); _changed(); });
        if (tooltip is not null) c.ToolTip = tooltip;
        Current.Children.Add(c);
        return c;
    }

    private static Grid LabeledRow(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(6, 4, 6, 4), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Ui.Label(label, 13);
        l.TextWrapping = TextWrapping.Wrap;
        l.Margin = new Thickness(0, 0, 12, 0);
        g.Children.Add(l);
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        return g;
    }

    public TextBox Number(string label, Func<int> get, Action<int> set, int min, int max, string? suffix = null)
    {
        var box = new TextBox { Text = get().ToString(CultureInfo.CurrentCulture), Width = 90, MinHeight = 32 }.WithStyle("Shotwise.NumberBox");
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max)
            {
                box.ClearValue(Control.BorderBrushProperty);
                if (v != get()) { set(v); _changed(); }
            }
            else box.SetResourceReference(Control.BorderBrushProperty, "Shotwise.DangerBrush");
        };
        UIElement control = box;
        if (suffix is not null)
        {
            var unit = Ui.Label(suffix, 12, secondary: true);
            unit.Margin = new Thickness(8, 0, 0, 0);
            control = Ui.Row(box, unit);
        }
        Current.Children.Add(LabeledRow(label, control));
        return box;
    }

    public ComboBox Choice<T>(string label, IEnumerable<(T Value, string Text)> items, Func<T> get, Action<T> set, double width = 160)
    {
        var combo = Ui.Combo(items, get(), v => { set(v); _changed(); }, width);
        Current.Children.Add(LabeledRow(label, combo));
        return combo;
    }

    public void Add(UIElement e) => Current.Children.Add(e);
}
