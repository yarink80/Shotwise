using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// Help → Keyboard shortcuts (POLISH-1): the global hotkeys as they are registered right now (effective key, fallback / problem
/// state from <see cref="IHotkeyStatusSource"/>) and every editor shortcut, generated from <see cref="EditorCommands"/> and the
/// standard WPF application commands, so the sheet cannot drift from the real bindings.
/// </summary>
internal sealed class ShortcutSheetWindow : DialogWindow
{
    public ShortcutSheetWindow(IHotkeyStatusSource? hotkeys, ISettingsService settings, Action openHotkeyPreferences)
    {
        Title = ES.T("Shortcuts_Title", "Keyboard shortcuts");
        SizeToContent = SizeToContent.Manual;
        Width = 860;
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 80);
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, Tok.Surface0);
        SetResourceReference(FontFamilyProperty, Tok.FontFamily);

        var root = UiKit.XamlRoot<DockPanel>();
        root.Margin = new Thickness(20, 12, 20, 16);

        // header
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new SwIcon { Source = "Icon.Keyboard.Regular", Size = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }.Themed(SwIcon.ForegroundProperty, Tok.AccentText));
        title.Children.Add(new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center }.Styled("Shotwise.Text.Title"));
        var prefs = UiKit.GhostButton(ES.T("Shortcuts_Change", "Change hotkeys…"), Icons.Glyph.Settings, () => { Close(); openHotkeyPreferences(); });
        DockPanel.SetDock(prefs, Dock.Right);
        head.Children.Add(prefs);
        head.Children.Add(title);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var close = new Button { Content = ES.T("Common_Close", "Close"), IsCancel = true, IsDefault = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }.Styled("Shotwise.PrimaryButton");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);

        // global hotkeys (left column, first)
        left.Children.Add(Section(ES.T("Shortcuts_Global", "Global hotkeys (work in every app)"), GlobalRows(hotkeys, settings)));

        // editor shortcuts: distribute the groups over the two columns by size
        var groups = EditorCommands.ShortcutGroups().ToList();
        var std = StandardEditCommands();
        int editIndex = groups.FindIndex(g => g.Group == ES.T("Menu_Edit", "Edit"));
        if (editIndex >= 0) groups[editIndex] = (groups[editIndex].Group, std.Concat(groups[editIndex].Items).ToList());
        int leftCount = GlobalRows(hotkeys, settings).Count + 2, rightCount = 0;
        foreach (var (group, items) in groups)
        {
            var rows = items.Select(i => (i.Name, i.Gesture, (string?)null, false)).ToList();
            if (leftCount <= rightCount) { left.Children.Add(Section(group, rows)); leftCount += rows.Count + 2; }
            else { right.Children.Add(Section(group, rows)); rightCount += rows.Count + 2; }
        }

        var scroll = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };
        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>Global hotkeys with their effective key and, when it is not simply active, the state.</summary>
    private static List<(string Name, string Gesture, string? Note, bool Problem)> GlobalRows(IHotkeyStatusSource? source, ISettingsService settings)
    {
        var rows = new List<(string, string, string?, bool)>();
        if (source is not null && source.Current.Count > 0)
        {
            foreach (var s in source.Current)
            {
                if (s.State == HotkeyState.Off) continue;
                string? note = s.State switch
                {
                    HotkeyState.Fallback => ES.T("Shortcuts_Fallback", "fallback - {0} is taken").Replace("{0}", s.Configured?.ToDisplayString() ?? ""),
                    HotkeyState.InUse => ES.T("Shortcuts_InUse", "in use by another app"),
                    HotkeyState.Duplicate => ES.T("Shortcuts_Duplicate", "duplicate"),
                    HotkeyState.Unsupported => ES.T("Shortcuts_Unsupported", "not supported"),
                    HotkeyState.RecordingOnly => ES.T("Shortcuts_RecordingOnly", "while recording"),
                    _ => null,
                };
                rows.Add((s.Label, s.Display?.ToDisplayString() ?? "", note, s.State is HotkeyState.InUse or HotkeyState.Duplicate or HotkeyState.Unsupported));
            }
            return rows;
        }
        var h = settings.Current.Hotkeys;
        foreach (var (name, key) in new[] { (ES.T("Shortcuts_Capture", "Capture"), h.EffectiveGlobalCapture ?? h.GlobalCapture), (ES.T("Shortcuts_Repeat", "Repeat last capture"), h.RepeatLastCapture),
                     (ES.T("Shortcuts_VideoPause", "Video start / pause / resume"), h.VideoStartPauseResume), (ES.T("Shortcuts_VideoStop", "Video stop"), h.VideoStop) })
            if (key is { } k) rows.Add((name, k.ToDisplayString(), null, false));
        return rows;
    }

    /// <summary>Undo / redo / clipboard / select all - the WPF application commands the editor binds.</summary>
    private static List<(string Name, string Gesture)> StandardEditCommands()
    {
        var list = new List<(string, string)>();
        foreach (var (cmd, name) in new (RoutedUICommand Cmd, string Name)[]
                 {
                     (ApplicationCommands.Undo, ES.T("Cmd_Undo", "Undo")), (ApplicationCommands.Redo, ES.T("Cmd_Redo", "Redo")),
                     (ApplicationCommands.Cut, ES.T("Cmd_Cut", "Cut")), (ApplicationCommands.Copy, ES.T("Cmd_Copy", "Copy")),
                     (ApplicationCommands.Paste, ES.T("Cmd_Paste", "Paste")), (ApplicationCommands.SelectAll, ES.T("Cmd_SelectAll", "Select All")),
                 })
        {
            var g = cmd.InputGestures.OfType<KeyGesture>().FirstOrDefault();
            // menu captions may carry the shortcut in brackets ("Undo (Ctrl+Z)"); the sheet shows it in the chip instead
            if (g is not null) list.Add((System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(.*\)\s*$", ""), EditorCommands.Gesture(g)));
        }
        list.Add((ES.T("Cmd_Delete", "Delete"), "Del"));
        return list;
    }

    private static FrameworkElement Section(string header, IEnumerable<(string Name, string Gesture, string? Note, bool Problem)> rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new PanelHeader { Content = header, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (name, gesture, note, problem) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 26 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }.Themed(TextBlock.ForegroundProperty, Tok.Text));
            if (note is not null)
            {
                bool fallback = note.StartsWith(ES.T("Shortcuts_FallbackPrefix", "fallback"), StringComparison.CurrentCultureIgnoreCase);
                label.Children.Add(new TextBlock { Text = note, FontSize = 11.5 }.Themed(TextBlock.ForegroundProperty, problem ? "Shotwise.DangerTextBrush" : fallback ? "Shotwise.WarningTextBrush" : Tok.Text3));
            }
            g.Children.Add(label);
            var chip = new Border { Child = new TextBlock { Text = gesture, FontSize = 12, FontWeight = FontWeights.SemiBold }, Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(12, 0, 0, 0) }.Styled("Shotwise.Chip");
            Grid.SetColumn(chip, 1);
            g.Children.Add(chip);
            panel.Children.Add(g);
        }
        return new Card { Content = panel, Padding = new Thickness(16, 12, 16, 12), Elevation = 1, Margin = new Thickness(0, 0, 0, 12) };
    }
}
