using System.Windows;
using System.Windows.Controls;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Minimal single-line prompt (rename, quick style/effect name, step value) built on the shared dialog window.</summary>
internal sealed class InputDialog : DialogWindow
{
    private readonly TextBox _text;

    private InputDialog(string title, string prompt, string initial, string okText)
    {
        Title = title;
        MinWidth = 400;
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        _text = new TextBox { Text = initial, MinWidth = 340, Margin = new Thickness(0, 10, 0, 20) }.Styled("Shotwise.TextBox");
        System.Windows.Automation.AutomationProperties.SetName(_text, prompt);
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) }.Styled("Shotwise.PrimaryButton");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = ES.T("Common_Cancel", "Cancel"), IsCancel = true, MinWidth = 88 }.Styled("Shotwise.SecondaryButton");
        cancel.Click += (_, _) => CancelDialog();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(24, 52, 24, 20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }.Styled("Shotwise.Text.Body"));
        panel.Children.Add(_text);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.SelectAll();
        };
    }

    public static async Task<string?> AskAsync(object? owner, string title, string prompt, string initial = "", string? okText = null)
    {
        var d = new InputDialog(title, prompt, initial, okText ?? ES.T("Common_OK", "OK"));
        var r = await d.ShowDialogAsync(owner);
        return r == true ? d._text.Text : null;
    }
}

/// <summary>Result of the "save changes?" prompt.</summary>
internal enum SaveChoice { Save, Discard, Cancel }

/// <summary>Three-way save prompt (Save / Don't save / Cancel) used when closing modified documents.</summary>
internal sealed class SaveChangesDialog : DialogWindow
{
    private SaveChoice _choice = SaveChoice.Cancel;

    private SaveChangesDialog(string name)
    {
        Title = ES.T("Close_SaveTitle", "Save changes?");
        CloseOnEscape = true;
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        var panel = new StackPanel { Margin = new Thickness(24, 52, 24, 20) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        head.Children.Add(new SwIcon { Source = "Icon.Warning.Color", Size = 28, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top });
        head.Children.Add(new TextBlock { Text = ES.F("Close_SavePrompt", "\"{0}\" has unsaved changes. Do you want to save them?", name), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center }.Styled("Shotwise.Text.Body"));
        panel.Children.Add(head);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button Make(string text, SaveChoice c, string style, bool isDefault = false)
        {
            var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault }.Styled(style);
            b.Click += (_, _) => { _choice = c; if (c == SaveChoice.Cancel) CancelDialog(); else Accept(); };
            return b;
        }
        buttons.Children.Add(Make(ES.T("Close_Save", "Save"), SaveChoice.Save, "Shotwise.PrimaryButton", true));
        buttons.Children.Add(Make(ES.T("Close_Discard", "Don't save"), SaveChoice.Discard, "Shotwise.SecondaryButton"));
        buttons.Children.Add(Make(ES.T("Common_Cancel", "Cancel"), SaveChoice.Cancel, "Shotwise.GhostButton"));
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<SaveChoice> AskAsync(object? owner, string name)
    {
        var d = new SaveChangesDialog(name);
        await d.ShowDialogAsync(owner);
        return d._choice;
    }
}
