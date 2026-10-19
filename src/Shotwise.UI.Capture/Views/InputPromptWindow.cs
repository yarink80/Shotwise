using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Views;

/// <summary>Small modal text prompt (preset name, group name, capture caption).</summary>
public sealed class InputPromptWindow : DialogWindow
{
    private readonly TextBox _box = new() { MinWidth = 320 };

    private InputPromptWindow(string title, string label, string initial)
    {
        Title = title;
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Ui.Label(title, 17, weight: FontWeights.SemiBold));
        var l = Ui.Label(label, 12, secondary: true);
        l.Margin = new Thickness(0, 8, 0, 6);
        root.Children.Add(l);
        _box.WithStyle("Shotwise.TextBox");
        _box.Text = initial;
        root.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = Ui.TextButton(CS.Get("Dialog_Ok"), Accept, accent: true);
        ok.IsDefault = true;
        var cancel = Ui.TextButton(CS.Get("Toolbar_Cancel"), CancelDialog);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        // FluentWindow needs a title bar row for chrome; keep it minimal.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new Wpf.Ui.Controls.TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });
        Grid.SetRow(root, 1);
        grid.Children.Add(root);
        ExtendsContentIntoTitleBar = true;
        Content = grid;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    /// <summary>Returns the entered text, or null when cancelled.</summary>
    public static async Task<string?> AskAsync(string title, string label, string initial, object? owner)
    {
        var w = new InputPromptWindow(title, label, initial);
        var ok = await w.ShowDialogAsync(owner);
        return ok == true ? w._box.Text : null;
    }
}
