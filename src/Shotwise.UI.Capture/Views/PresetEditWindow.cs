using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Controls;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Views;

/// <summary>
/// Edit a preset's name, group and global hotkey. Duplicate / unsupported combinations are rejected inline; the hotkey field shows
/// whether Windows or another app holds the key, suggests free common keys for the preset's kind, and offers "Move here" when another
/// Shotwise hotkey uses it. Keys that will not work (held by another app, reserved) can still be saved after a warning.
/// </summary>
public sealed class PresetEditWindow : DialogWindow
{
    private readonly TextBox _name = new() { MinWidth = 320 };
    private readonly ComboBox _group;
    private readonly HotkeyField _hotkey;
    private readonly TextBlock _error = Ui.Label("", 12, wrap: true);
    private readonly CaptureWindowModel _model;
    private readonly CapturePreset _preset;
    private readonly HotkeySettings _globals;
    private Hotkey? _warnedKey;

    private PresetEditWindow(CaptureWindowModel model, CapturePreset preset, IReadOnlyList<PresetGroup> groups, HotkeySettings globals, bool focusHotkey, IHotkeyAdvisor? advisor)
    {
        _model = model; _preset = preset; _globals = globals;
        Title = CS.Get("PresetEdit_Title");
        _name.Text = preset.Name;
        var items = new List<Ui.ComboItem<string?>> { new(null, CS.Get("Preset_NoGroup")) };
        items.AddRange(groups.OrderBy(g => g.Order).Select(g => new Ui.ComboItem<string?>(g.Id, g.Name)));
        _group = new ComboBox { ItemsSource = items, DisplayMemberPath = "Text", SelectedValuePath = "Value", MinWidth = 320 };
        _group.SelectedValue = preset.GroupId;
        if (_group.SelectedIndex < 0) _group.SelectedIndex = 0;
        _hotkey = new HotkeyField(advisor, HotkeyIds.Preset(preset.Id), HotkeyRules.PurposeOf(preset), preset.Name);
        _hotkey.Value = preset.Hotkey;
        _hotkey.ValueChanged += (_, _) => { _warnedKey = null; _error.Visibility = Visibility.Collapsed; };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush");
        _name.WithStyle("Shotwise.TextBox");
        _group.MinHeight = 32;
        _error.Visibility = Visibility.Collapsed;

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Width = 400 };
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
            if (!k.IsValidGlobalHotkey || k.Key == VirtualKey.F12) { Show(CS.Get("Hotkey_Unsupported")); return; }
            if (_hotkey.MoveFromId is null)
            {
                var clash = _model.FindHotkeyClash(_preset.Id, k, _globals);
                if (clash is not null) { Show(CS.Format("PresetEdit_HotkeyUsed", clash)); return; }
            }
            // held by another app or reserved: allowed, but only after a clear warning (second OK saves)
            if (_hotkey.Availability is { WillNotWork: true } && k != _preset.Hotkey && _warnedKey != k)
            {
                _warnedKey = k;
                Show(CS.Format("HotkeyCheck_WarnWontWork", k.ToDisplayString()), "Shotwise.WarningTextBrush");
                return;
            }
        }
        Accept();
    }

    private void Show(string text, string brush = "Shotwise.DangerTextBrush")
    {
        _error.Text = text;
        _error.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _error.Visibility = Visibility.Visible;
        _error.Margin = new Thickness(0, 10, 0, 0);
    }

    /// <summary>Shows the dialog and saves the changes; false when cancelled.</summary>
    public static Task<bool> EditAsync(CaptureWindowModel model, CapturePreset preset, IReadOnlyList<PresetGroup> groups, HotkeySettings globals, bool focusHotkey, object? owner)
        => EditAsync(model, preset, groups, globals, focusHotkey, owner, null);

    /// <summary>
    /// Shows the dialog and saves the changes; false when cancelled. With <paramref name="services"/> the hotkey field checks availability
    /// and suggests keys (<see cref="IHotkeyAdvisor"/>), and "Move here" clears the other Shotwise hotkey on save.
    /// </summary>
    public static async Task<bool> EditAsync(CaptureWindowModel model, CapturePreset preset, IReadOnlyList<PresetGroup> groups, HotkeySettings globals, bool focusHotkey, object? owner, IServiceProvider? services)
    {
        var w = new PresetEditWindow(model, preset, groups, globals, focusHotkey, services?.GetService<IHotkeyAdvisor>());
        if (await w.ShowDialogAsync(owner) != true) return false;
        var key = w._hotkey.Value;
        // "Move here": release the key from the other binding first so the re-registration never sees a duplicate
        if (key is { } moved && w._hotkey.MoveFromId is { } from && services?.GetService<ISettingsService>() is { } settings)
            await HotkeyBindings.ReleaseAsync(settings, model.Repository, from, moved);
        var copy = (model.Repository.Find(preset.Id) ?? preset).Clone();
        copy.Name = w._name.Text.Trim();
        copy.GroupId = w._group.SelectedValue as string;
        copy.Hotkey = key;
        await model.Repository.SaveAsync(copy);
        if (model.Working.Id == copy.Id) { model.Working.Name = copy.Name; model.Working.GroupId = copy.GroupId; model.Working.Hotkey = copy.Hotkey; }
        services?.GetService<IHotkeyAdvisor>()?.Invalidate();
        return true;
    }
}
