using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Controls;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Views;

/// <summary>Edit a preset's name, group and global hotkey (duplicate / unsupported combinations are rejected inline).</summary>
public sealed class PresetEditWindow : DialogWindow
{
    private readonly TextBox _name = new() { MinWidth = 320 };
    private readonly ComboBox _group;
    private readonly HotkeyBox _hotkey = new();
    private readonly TextBlock _error = Ui.Label("", 12, wrap: true);
    private readonly CaptureWindowModel _model;
    private readonly CapturePreset _preset;
    private readonly HotkeySettings _globals;

    private PresetEditWindow(CaptureWindowModel model, CapturePreset preset, IReadOnlyList<PresetGroup> groups, HotkeySettings globals, bool focusHotkey)
    {
        _model = model; _preset = preset; _globals = globals;
        Title = CS.Get("PresetEdit_Title");
        _name.Text = preset.Name;
        var items = new List<Ui.ComboItem<string?>> { new(null, CS.Get("Preset_NoGroup")) };
        items.AddRange(groups.OrderBy(g => g.Order).Select(g => new Ui.ComboItem<string?>(g.Id, g.Name)));
        _group = new ComboBox { ItemsSource = items, DisplayMemberPath = "Text", SelectedValuePath = "Value", MinWidth = 320 };
        _group.SelectedValue = preset.GroupId;
        if (_group.SelectedIndex < 0) _group.SelectedIndex = 0;
        _hotkey.Value = preset.Hotkey;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush");
        _name.WithStyle("Shotwise.TextBox");
        _group.MinHeight = 32;
        _error.Visibility = Visibility.Collapsed;

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Width = 380 };
        root.Children.Add(Ui.Label(CS.Get("PresetEdit_Title"), 17, weight: FontWeights.SemiBold));
        root.Children.Add(Field(CS.Get("PresetEdit_Name"), _name));
        root.Children.Add(Field(CS.Get("PresetEdit_Group"), _group));
        root.Children.Add(Field(CS.Get("PresetEdit_Hotkey"), _hotkey));
        root.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = Ui.TextButton(CS.Get("Dialog_Ok"), TryAccept, accent: true);
        ok.IsDefault = true;
        var cancel = Ui.TextButton(CS.Get("Toolbar_Cancel"), CancelDialog);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new Wpf.Ui.Controls.TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        Grid.SetRow(root, 1);
        grid.Children.Add(root);
        ExtendsContentIntoTitleBar = true;
        Content = grid;
        Loaded += (_, _) => { if (focusHotkey) _hotkey.Focus(); else { _name.Focus(); _name.SelectAll(); } };
    }

    private static UIElement Field(string label, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var l = Ui.Label(label, 12, secondary: true);
        l.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(l);
        p.Children.Add(control);
        return p;
    }

    private void TryAccept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Show(CS.Get("PresetEdit_NameRequired")); return; }
        if (_hotkey.Value is { } k)
        {
            if (!k.IsValidGlobalHotkey) { Show(CS.Get("Hotkey_Unsupported")); return; }
            var clash = _model.FindHotkeyClash(_preset.Id, k, _globals);
            if (clash is not null) { Show(CS.Format("PresetEdit_HotkeyUsed", clash)); return; }
        }
        Accept();
    }

    private void Show(string text) { _error.Text = text; _error.Visibility = Visibility.Visible; _error.Margin = new Thickness(0, 10, 0, 0); }

    /// <summary>Shows the dialog and saves the changes; false when cancelled.</summary>
    public static async Task<bool> EditAsync(CaptureWindowModel model, CapturePreset preset, IReadOnlyList<PresetGroup> groups, HotkeySettings globals, bool focusHotkey, object? owner)
    {
        var w = new PresetEditWindow(model, preset, groups, globals, focusHotkey);
        if (await w.ShowDialogAsync(owner) != true) return false;
        var copy = (model.Repository.Find(preset.Id) ?? preset).Clone();
        copy.Name = w._name.Text.Trim();
        copy.GroupId = w._group.SelectedValue as string;
        copy.Hotkey = w._hotkey.Value;
        await model.Repository.SaveAsync(copy);
        if (model.Working.Id == copy.Id) { model.Working.Name = copy.Name; model.Working.GroupId = copy.GroupId; model.Working.Hotkey = copy.Hotkey; }
        return true;
    }
}
