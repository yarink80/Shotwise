using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Dialogs;
using CaptureMode = Shotwise.Core.Capture.CaptureMode;

namespace Shotwise.UI.Capture.Views;

public sealed partial class CaptureWindow
{
    private const string DragFormat = "shotwise.preset";
    private readonly StackPanel _presetsHost = new();
    private readonly List<string> _displayOrder = new();
    private readonly Dictionary<string, TextBlock> _modifiedDots = new();
    private Popup? _presetsPopup;
    private Point _dragOrigin;
    private string? _dragCandidate;

    // ------------------------------------------------------------------ flyout

    /// <summary>Presets flyout: the preset list (icons, hotkeys, groups, drag to reorder) plus the management actions.</summary>
    private void ShowPresetsFlyout(FrameworkElement anchor)
    {
        if (_presetsPopup is null)
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330, Content = _presetsHost, Padding = new Thickness(0, 0, 4, 0) };
            var title = Ui.Label(CS.Get("Presets_Header"), 14, weight: FontWeights.SemiBold);
            title.Margin = new Thickness(4, 0, 0, 8);
            var panel = new StackPanel { Width = 340 };
            panel.Children.Add(title);
            panel.Children.Add(scroll);
            panel.Children.Add(Ui.Separator());
            panel.Children.Add(BuildPresetActions());
            var card = Ui.FloatingCard(panel, 12, new Thickness(12));
            card.Margin = new Thickness(10, 2, 10, 16);
            _presetsPopup = new Popup { Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = card };
        }
        _presetsPopup.PlacementTarget = anchor;
        _presetsPopup.Placement = PlacementMode.Bottom;
        _presetsPopup.IsOpen = true;
    }

    private UIElement BuildPresetActions()
    {
        var col = new StackPanel();
        Button Action(string text, string icon, Func<Task> run, bool danger = false)
        {
            var b = Ui.TextButton(text, async () => { if (_presetsPopup is not null) _presetsPopup.IsOpen = false; await run(); }, icon, ghost: true);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(8, 0, 8, 0);
            b.MinHeight = 32;
            return b;
        }
        col.Children.Add(Action(CS.Get("Preset_SaveAs"), "Icon.Add.Regular", SaveAsNewAsync));
        col.Children.Add(Action(CS.Get("Preset_ResetCurrent"), "Icon.Reset.Regular", () => { _m.ResetCurrent(); RefreshAll(); return Task.CompletedTask; }));
        col.Children.Add(Action(CS.Get("Group_New"), "Icon.Folder.Regular", async () => { await NewGroupAsync(); }));
        col.Children.Add(Action(CS.Get("Presets_Import"), "Icon.Download.Regular", ImportAsync));
        col.Children.Add(Action(CS.Get("Presets_ExportAll"), "Icon.Export.Regular", () => ExportAsync(null)));
        col.Children.Add(Action(CS.Get("Presets_ResetDefaults"), "Icon.History.Regular", async () =>
        {
            if (await ConfirmAsync(CS.Get("Presets_ResetDefaults"), CS.Get("Presets_ResetDefaultsBody"))) await _repo.ResetDefaultsAsync();
        }));
        return col;
    }

    private void BuildPresetHeaderOnly()
    {
        foreach (var (id, dot) in _modifiedDots)
            dot.Visibility = id == _m.Working.Id && _m.IsModified ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildPresets()
    {
        _presetsHost.Children.Clear();
        _modifiedDots.Clear();
        _displayOrder.Clear();
        var groups = _repo.Groups.OrderBy(g => g.Order).ToList();
        var all = _repo.All.OrderBy(p => p.Order).ToList();
        var groupIds = groups.Select(g => g.Id).ToHashSet();

        if (all.Count == 0) _presetsHost.Children.Add(Ui.Label(CS.Get("CW_NoPresets"), 12, secondary: true));
        foreach (var p in all.Where(p => p.GroupId is null || !groupIds.Contains(p.GroupId)))
            AddPreset(p);
        foreach (var g in groups)
        {
            var members = all.Where(p => p.GroupId == g.Id).ToList();
            _presetsHost.Children.Add(GroupHeader(g, members.Count));
            if (!g.IsExpanded) { foreach (var p in members) _displayOrder.Add(p.Id); continue; }
            foreach (var p in members) AddPreset(p, indent: 14);
        }

        // trailing drop zone: move to the end of the ungrouped list
        var tail = new Border { Height = 14, Background = Brushes.Transparent, AllowDrop = true };
        tail.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        tail.Drop += async (_, e) => { if (e.Data.GetData(DragFormat) is string id) await DropOnAsync(id, null, noGroup: true); e.Handled = true; };
        _presetsHost.Children.Add(tail);
        BuildPresetHeaderOnly();
    }

    private void AddPreset(CapturePreset p, double indent = 0)
    {
        _displayOrder.Add(p.Id);
        _presetsHost.Children.Add(PresetItem(p, indent));
    }

    /// <summary>Colour icon per preset kind (shared icon set).</summary>
    private static string IconFor(CapturePreset p) => p.Selection switch
    {
        SelectionType.GrabText => "Icon.Tool.GrabText.Color",
        SelectionType.Panoramic => "Icon.Scroll.Filled",
        SelectionType.StepCapture => "Icon.Tool.Step.Color",
        SelectionType.ScrollingWindow => "Icon.Scroll.Filled",
        SelectionType.Fullscreen => "Icon.Desktop.Filled",
        SelectionType.Window => "Icon.Window.Filled",
        _ => p.Mode switch { CaptureMode.Video => "Icon.Video.Color", CaptureMode.Image => "Icon.Image.Color", _ => "Icon.AllInOne.Color" },
    };

    private static string? IconBrushFor(CapturePreset p) => p.Selection switch
    {
        SelectionType.Panoramic => "Shotwise.WarningBrush",
        SelectionType.ScrollingWindow => "Shotwise.SuccessBrush",
        SelectionType.Fullscreen => "Shotwise.AccentBrush",
        SelectionType.Window => "Shotwise.AccentBrush",
        _ => null,
    };

    private UIElement GroupHeader(PresetGroup g, int count)
    {
        var chevron = Ui.Icon("Icon.ChevronDown.Regular", 12, "Shotwise.TextTertiaryBrush");
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(g.IsExpanded ? 0 : -90);
        var label = Ui.Label($"{g.Name}  ({count})", 12, secondary: true, weight: FontWeights.SemiBold);
        var row = Ui.Row(chevron, new Border { Width = 8 }, label);
        var b = new Border { Child = row, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 6, 0, 0), Background = Brushes.Transparent, AllowDrop = true, Cursor = Cursors.Hand };
        b.MouseLeftButtonUp += async (_, _) => await _repo.SaveGroupAsync(g with { IsExpanded = !g.IsExpanded });
        b.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        b.Drop += async (_, e) => { if (e.Data.GetData(DragFormat) is string id) await DropOnAsync(id, null, groupId: g.Id); e.Handled = true; };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(CS.Get("Group_Rename"), async () =>
        {
            var name = await InputPromptWindow.AskAsync(CS.Get("Group_Rename"), CS.Get("Group_NameLabel"), g.Name, this);
            if (!string.IsNullOrWhiteSpace(name)) await _repo.SaveGroupAsync(g with { Name = name.Trim() });
        }, "Icon.Editor.Regular"));
        menu.Items.Add(MenuItem(CS.Get("Group_Export"), async () => await ExportAsync(_repo.All.Where(p => p.GroupId == g.Id).Select(p => p.Id).ToList()), "Icon.Export.Regular"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(CS.Get("Group_Delete"), async () => { if (await ConfirmAsync(CS.Get("Group_Delete"), CS.Format("Group_DeleteBody", g.Name))) await _repo.DeleteGroupAsync(g.Id, false); }, "Icon.Delete.Regular"));
        menu.Items.Add(MenuItem(CS.Get("Group_DeleteWithPresets"), async () => { if (await ConfirmAsync(CS.Get("Group_DeleteWithPresets"), CS.Format("Group_DeleteWithPresetsBody", g.Name))) await _repo.DeleteGroupAsync(g.Id, true); }, "Icon.Delete.Regular"));
        b.ContextMenu = menu;
        return b;
    }

    private UIElement PresetItem(CapturePreset p, double indent)
    {
        bool active = p.Id == _m.Working.Id;
        var dot = new TextBlock { Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, ToolTip = CS.Get("Preset_Modified") };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.WarningBrush");
        _modifiedDots[p.Id] = dot;
        var name = Ui.Label(p.Name, 13, weight: active ? FontWeights.SemiBold : FontWeights.Normal, brush: active ? "Shotwise.AccentTextBrush" : null);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        UIElement hotkey = new Border();
        if (p.Hotkey is { } h)
        {
            // The key as registered right now; a key held by another app is shown struck-through in danger colours.
            var status = _coordinator.Find(Shotwise.Core.Platform.HotkeyIds.Preset(p.Id));
            bool failed = status is { State: Shotwise.Core.Platform.HotkeyState.InUse or Shotwise.Core.Platform.HotkeyState.Duplicate or Shotwise.Core.Platform.HotkeyState.Unsupported };
            var text = new TextBlock { Text = (status?.Display ?? h).ToDisplayString() };
            if (failed) { text.TextDecorations = TextDecorations.Strikethrough; text.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush"); }
            var chip = new Border { Child = text, Margin = new Thickness(8, 0, 0, 0), ToolTip = failed ? CS.Format("Hotkey_InUseDetail", h.ToDisplayString()) : null };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.Chip");
            hotkey = chip;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Icon(IconFor(p), 20, IconBrushFor(p) ?? (active ? "Shotwise.AccentTextBrush" : "Shotwise.TextSecondaryBrush"));
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(icon);
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        Grid.SetColumn(hotkey, 2); grid.Children.Add(hotkey);
        Grid.SetColumn(dot, 3); grid.Children.Add(dot);

        var b = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(indent, 2, 0, 0),
            BorderThickness = new Thickness(1),
            AllowDrop = true,
            Cursor = Cursors.Hand,
            Tag = p.Id,
            ToolTip = $"{CS.Get("Mode_" + p.Mode)} - {CS.Get("Sel_" + p.Selection)}",
        };
        b.BorderBrush = Brushes.Transparent;
        if (active) b.SetResourceReference(Border.BackgroundProperty, "Shotwise.AccentSubtleBrush"); else b.Background = Brushes.Transparent;
        b.MouseEnter += (_, _) => { if (!active) b.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface2Brush"); };
        b.MouseLeave += (_, _) => { if (!active) b.Background = Brushes.Transparent; };

        b.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                if (_presetsPopup is not null) _presetsPopup.IsOpen = false;
                if (!_coordinator.IsCapturing) _ = _coordinator.RunAsync(_repo.Find(p.Id)?.Clone() ?? p.Clone());
                return;
            }
            _dragOrigin = e.GetPosition(null);
            _dragCandidate = p.Id;
        };
        b.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate != p.Id) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragCandidate = null;
            DragDrop.DoDragDrop(b, new DataObject(DragFormat, p.Id), DragDropEffects.Move);
        };
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragCandidate == p.Id)
            {
                _dragCandidate = null;
                var preset = _repo.Find(p.Id) ?? p;
                _m.Load(preset);
                if (_presetsPopup is not null) _presetsPopup.IsOpen = false;
                RefreshAll();
            }
        };
        b.DragOver += (_, e) =>
        {
            bool ok = e.Data.GetDataPresent(DragFormat);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            if (ok) b.SetResourceReference(Border.BorderBrushProperty, "Shotwise.AccentBrush");
            e.Handled = true;
        };
        b.DragLeave += (_, _) => b.BorderBrush = Brushes.Transparent;
        b.Drop += async (_, e) =>
        {
            b.BorderBrush = Brushes.Transparent;
            if (e.Data.GetData(DragFormat) is string id) await DropOnAsync(id, p);
            e.Handled = true;
        };
        b.ContextMenu = BuildPresetMenu(p);
        return b;
    }

    private async Task DropOnAsync(string movedId, CapturePreset? before, string? groupId = null, bool noGroup = false)
    {
        try
        {
            var moved = _repo.Find(movedId);
            if (moved is null) return;
            string? targetGroup = before is not null ? before.GroupId : noGroup ? null : groupId;
            if (moved.GroupId != targetGroup) await _m.MoveToGroupAsync(moved, targetGroup);
            var order = CaptureWindowModel.ReorderIds(_displayOrder.ToList(), movedId, before?.Id);
            await _repo.ReorderAsync(order);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Reordering presets failed"); }
    }

    private static MenuItem MenuItem(string header, Func<Task> action, string? icon = null)
    {
        var mi = new MenuItem { Header = header };
        if (icon is not null) mi.Icon = Ui.Icon(icon, 16);
        mi.Click += async (_, _) => await action();
        return mi;
    }

    private static MenuItem MenuItem(string header, Action action, string? icon = null)
        => MenuItem(header, () => { action(); return Task.CompletedTask; }, icon);

    private ContextMenu BuildPresetMenu(CapturePreset p)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Run"), () => { if (!_coordinator.IsCapturing) _ = _coordinator.RunAsync(_repo.Find(p.Id)?.Clone() ?? p.Clone()); }, "Icon.Play.Filled"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Edit"), async () => await EditPresetAsync(p, focusHotkey: false), "Icon.Settings.Regular"));
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Rename"), async () =>
        {
            var name = await InputPromptWindow.AskAsync(CS.Get("PresetMenu_Rename"), CS.Get("PresetEdit_Name"), p.Name, this);
            if (!string.IsNullOrWhiteSpace(name)) await _m.RenameAsync(p, name);
        }, "Icon.Editor.Regular"));
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Duplicate"), async () => await _m.DuplicateAsync(p), "Icon.Copy.Regular"));
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_SetHotkey"), async () => await EditPresetAsync(p, focusHotkey: true), "Icon.Keyboard.Regular"));

        var move = new MenuItem { Header = CS.Get("PresetMenu_MoveToGroup"), Icon = Ui.Icon("Icon.Folder.Regular", 16) };
        move.Items.Add(MenuItem(CS.Get("Preset_NoGroup"), async () => await _m.MoveToGroupAsync(p, null)));
        foreach (var g in _repo.Groups.OrderBy(g => g.Order))
        {
            var gid = g.Id;
            move.Items.Add(MenuItem(g.Name, async () => await _m.MoveToGroupAsync(p, gid)));
        }
        move.Items.Add(new Separator());
        move.Items.Add(MenuItem(CS.Get("Group_New"), async () =>
        {
            var g = await NewGroupAsync();
            if (g is not null) await _m.MoveToGroupAsync(p, g.Id);
        }));
        menu.Items.Add(move);
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Export"), async () => await ExportAsync(new[] { p.Id }), "Icon.Export.Regular"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(CS.Get("PresetMenu_Delete"), async () =>
        {
            if (await ConfirmAsync(CS.Get("PresetMenu_Delete"), CS.Format("PresetMenu_DeleteBody", p.Name))) await _m.DeleteAsync(p);
        }, "Icon.Delete.Regular"));
        return menu;
    }

    private async Task EditPresetAsync(CapturePreset p, bool focusHotkey)
    {
        var current = _repo.Find(p.Id) ?? p;
        if (await PresetEditWindow.EditAsync(_m, current, _repo.Groups, _settings.Current.Hotkeys, focusHotkey, this, _sp)) RefreshAll();
    }

    private async Task SaveAsNewAsync()
    {
        var name = await InputPromptWindow.AskAsync(CS.Get("Preset_SaveAs"), CS.Get("PresetEdit_Name"), _m.Working.Name + " (2)", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _m.SaveCurrentAsAsync(name);
        RefreshAll();
    }

    private async Task<PresetGroup?> NewGroupAsync()
    {
        var name = await InputPromptWindow.AskAsync(CS.Get("Group_New"), CS.Get("Group_NameLabel"), "", this);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var order = _repo.Groups.Count == 0 ? 0 : _repo.Groups.Max(g => g.Order) + 1;
        var group = new PresetGroup(Guid.NewGuid().ToString("N"), name.Trim(), order);
        await _repo.SaveGroupAsync(group);
        return group;
    }

    // ------------------------------------------------------------------ import / export / confirm

    private async Task ExportAsync(IReadOnlyList<string>? ids)
    {
        try
        {
            string? path; IReadOnlyList<string> chosen;
            if (_sp.GetService<IDialogService>() is { } dialogs)
            {
                var r = await dialogs.ShowPresetImportExport(new PresetImportExportRequest(PresetTransferMode.Export, ids) { Owner = this });
                if (r is null) return;
                path = r.Path; chosen = r.PresetIds;
            }
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = CS.Get("Presets_FileFilter"), DefaultExt = ".shotpresets", FileName = "presets" };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName; chosen = ids ?? _repo.All.Select(p => p.Id).ToList();
            }
            await _repo.ExportAsync(chosen, path);
        }
        catch (Exception ex) { await ShowErrorAsync(ex); }
    }

    private async Task ImportAsync()
    {
        try
        {
            string? path;
            if (_sp.GetService<IDialogService>() is { } dialogs)
            {
                var r = await dialogs.ShowPresetImportExport(new PresetImportExportRequest(PresetTransferMode.Import) { Owner = this });
                path = r?.Path;
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = CS.Get("Presets_FileFilter"), CheckFileExists = true };
                path = dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            if (path is null) return;
            await _repo.ImportAsync(path);
        }
        catch (Exception ex) { await ShowErrorAsync(ex); }
    }

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        if (_sp.GetService<IDialogService>() is not { } dialogs)
            return MessageBox.Show(this, body, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        var r = await dialogs.Confirm(new ConfirmRequest(title, body, CS.Get("Dialog_Ok"), CS.Get("Toolbar_Cancel"), IsDestructive: true) { Owner = this });
        return r.Confirmed;
    }

    private async Task ShowErrorAsync(Exception ex)
    {
        _log?.LogWarning(ex, "Preset operation failed");
        if (_sp.GetService<IDialogService>() is { } d) await d.ShowMessage(new MessageRequest(CS.Get("Presets_Header"), ex.Message, MessageKind.Error) { Owner = this });
        else MessageBox.Show(this, ex.Message, CS.Get("Presets_Header"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
