using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Commands;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Editor.Infrastructure;
using Wpf.Ui.Controls;

namespace Shotwise.UI.Editor.Panels;

/// <summary>Undo Stack window (EDT-02): shows the history of the active document; clicking an entry jumps to that state.</summary>
internal sealed class UndoStackWindow : FluentWindow
{
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(8, 0, 8, 0) };
    private readonly System.Windows.Controls.TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly System.Windows.Controls.TextBlock _caption = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private UndoStack? _stack;
    private bool _updating;

    public UndoStackWindow()
    {
        Title = ES.T("Undo_WindowTitle", "Undo Stack");
        Width = 320;
        Height = 460;
        MinWidth = 240;
        ShowInTaskbar = false;
        // The window draws its own TitleBar (WPF-UI 4 requires content in the title bar area for custom chrome).
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        var grid = UiKit.XamlRoot<Grid>();
        grid.SetResourceReference(Panel.BackgroundProperty, Tok.Surface0);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleBar = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        if (TryFindResource("Icon.History.Color") is System.Windows.Media.ImageSource icon) titleBar.Icon = new ImageIcon { Source = icon, Width = 16, Height = 16 };
        grid.Children.Add(titleBar);
        var head = new StackPanel { Margin = new Thickness(16, 8, 16, 10) };
        _title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, Tok.Text);
        _caption.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, Tok.Text2);
        head.Children.Add(_title);
        head.Children.Add(_caption);
        Grid.SetRow(head, 1);
        grid.Children.Add(head);
        var card = new Shotwise.UI.Common.Controls.Card { Content = _list, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(12, 0, 12, 0), Elevation = 0 };
        Grid.SetRow(card, 2);
        grid.Children.Add(card);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(UiKit.GlyphTextButton(Icons.Glyph.Refresh, ES.T("Undo_UndoAll", "Revert to original"), () => _stack?.UndoAll()));
        Grid.SetRow(bar, 3);
        grid.Children.Add(bar);
        Content = grid;
        _list.SelectionChanged += (_, _) =>
        {
            if (_updating || _stack is null || _list.SelectedIndex < 0) return;
            if (_list.SelectedIndex != _stack.Position) _stack.GoTo(_list.SelectedIndex);
        };
        Closing += (_, e) =>
        {
            // Keep the window instance alive for reuse; just hide it.
            e.Cancel = true;
            Hide();
        };
    }

    public void Bind(UndoStack? stack, string documentTitle)
    {
        if (_stack is not null) _stack.Changed -= OnChanged;
        _stack = stack;
        if (_stack is not null) _stack.Changed += OnChanged;
        _title.Text = documentTitle;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private static FrameworkElement Entry(string text, int index, bool undone, bool original)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Opacity = undone ? 0.5 : 1 };
        var chip = UiKit.Chip(original ? "0" : index.ToString(), accent: !undone && !original);
        chip.MinWidth = 26;
        chip.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(chip);
        var t = new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontStyle = original ? FontStyles.Italic : FontStyles.Normal };
        t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, Tok.Text);
        row.Children.Add(t);
        return row;
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            _list.Items.Clear();
            if (_stack is null) { _caption.Text = ""; return; }
            _list.Items.Add(new ListBoxItem { Content = Entry(ES.T("Undo_Original", "Original image"), 0, false, true) });
            var history = _stack.History;
            for (var i = 0; i < history.Count; i++)
                _list.Items.Add(new ListBoxItem { Content = Entry(history[i], i + 1, i >= _stack.Position, false) });
            _caption.Text = ES.F("Undo_Caption", "{0} steps · click a step to go back to it", history.Count);
            _list.SelectedIndex = _stack.Position;
            _list.ScrollIntoView(_list.SelectedItem);
        }
        finally { _updating = false; }
    }
}
