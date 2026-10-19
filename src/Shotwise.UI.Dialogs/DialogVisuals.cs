using System.Windows;
using System.Windows.Controls;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Dialogs;

/// <summary>Dialog-only composition of D1 components. Brushes stay live when the theme changes.</summary>
internal static class DialogVisuals
{
    public static Border Pill(TextBlock text, bool success = false)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, success ? "Shotwise.SuccessTextBrush" : "Shotwise.AccentTextBrush");
        var pill = new Border { Child = text, CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        pill.SetResourceReference(Border.BackgroundProperty, success ? "Shotwise.SuccessSubtleBrush" : "Shotwise.AccentSubtleBrush"); return pill;
    }

    // The original selector remains the source of truth, including its validation and change events.
    public static FrameworkElement Choices(ComboBox selector, string icon, bool descriptions = false)
    {
        selector.Visibility = Visibility.Collapsed;
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 12) };
        var buttons = new List<(object Value, NavItem Button)>();
        var group = Guid.NewGuid().ToString();
        foreach (var value in selector.Items.Cast<object>())
        {
            var label = value is Enum e ? Texts.Enum(e) : value.ToString()?.ToUpperInvariant() ?? "";
            object content = label;
            if (descriptions)
            {
                var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
                var description = value.ToString() switch { "ScreenVideo" => "Clear text · 5 fps", "HighMotion" => "Smooth motion · 15 fps", "Custom" => "Your own settings", _ => "Compact file · 3 fps" };
                stack.Children.Add(new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) }); content = stack;
            }
            var button = new NavItem { Content = content, Icon = icon, GroupName = group, Margin = new Thickness(0, 0, 8, 8), Width = descriptions ? 244 : double.NaN, Height = descriptions ? 68 : 36, MinHeight = descriptions ? 68 : 36 };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.NavListItem");
            button.Checked += (_, _) => selector.SelectedItem = value;
            panel.Children.Add(button); buttons.Add((value, button));
        }
        void Sync() { foreach (var entry in buttons) entry.Button.IsChecked = Equals(entry.Value, selector.SelectedItem); }
        selector.SelectionChanged += (_, _) => Sync(); Sync(); return panel;
    }
}
