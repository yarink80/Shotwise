using System.Windows;
using System.Windows.Controls;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Controls.Primitives;
using Shotwise.Core.Capture;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Selection;

/// <summary>
/// Floating pill at the top of the overlay: aspect ratio locks (1:1, 4:3, 16:9), magnifier and guide toggles and the help button.
/// The same actions are available on the keyboard; this makes them discoverable with the mouse.
/// </summary>
internal sealed class OverlayOptionsBar : Grid
{
    private readonly SelectionController _ctl;
    private readonly ToggleButton _a11, _a43, _a169, _mag, _guides;
    private readonly StackPanel _aspectGroup = new() { Orientation = Orientation.Horizontal };

    public OverlayOptionsBar(SelectionController ctl)
    {
        Ui.EnsureStyles();
        _ctl = ctl;
        Focusable = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var surface = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4), Child = row };
        surface.SetResourceReference(Border.BackgroundProperty, "Shotwise.Surface1Brush");
        surface.SetResourceReference(Border.BorderBrushProperty, "Shotwise.BorderSubtleBrush");
        surface.SetResourceReference(TextElement.ForegroundProperty, "Shotwise.TextPrimaryBrush");
        surface.SetResourceReference(TextElement.FontFamilyProperty, "Shotwise.FontFamily");
        Children.Add(Ui.ShadowLayer(20, "Shotwise.Elevation.Card"));
        Children.Add(surface);

        _a11 = Toggle("1:1", CS.Get("Help_AspectLocks"), null, () => _ctl.ToggleAspectLock(AspectLock.Square));
        _a43 = Toggle("4:3", CS.Get("Help_AspectLocks"), null, () => _ctl.ToggleAspectLock(AspectLock.FourThree));
        _a169 = Toggle("16:9", CS.Get("Help_AspectLocks"), null, () => _ctl.ToggleAspectLock(AspectLock.SixteenNine));
        _aspectGroup.Children.Add(_a11); _aspectGroup.Children.Add(_a43); _aspectGroup.Children.Add(_a169);
        _aspectGroup.Children.Add(Ui.VSeparator(18));
        row.Children.Add(_aspectGroup);
        _mag = Toggle(null, CS.Get("Help_Magnifier") + " (M)", "Icon.Tool.Magnify.Regular", () => _ctl.ToggleMagnifier());
        _guides = Toggle(null, CS.Get("Help_Guides") + " (G)", "Icon.Grid.Regular", () => _ctl.ToggleGuides());
        row.Children.Add(_mag); row.Children.Add(_guides);
        var help = Ui.IconButton("Icon.Help.Regular", CS.Get("Help_ToggleHelp") + " (F1)", () => _ctl.ToggleHelp(), 18);
        help.Focusable = false;
        help.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(help);
        _aspectGroup.Visibility = ctl.Request.Type is SelectionType.Region or SelectionType.MultipleArea ? Visibility.Visible : Visibility.Collapsed;
        Sync();
    }

    private static ToggleButton Toggle(string? content, string tip, string? icon, Action click)
    {
        var b = new ToggleButton { Content = content, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Focusable = false, FontSize = 12, FontWeight = FontWeights.SemiBold };
        if (icon is not null)
        {
            // icon-only toggle: round icon button (checked = accent tint)
            b.WithStyle("Shotwise.IconButton");
            SwUi.SetIcon(b, icon); SwUi.SetIconSize(b, 18); Ui.AutomationProperties(b, tip);
        }
        else
        {
            b.WithStyle("Shotwise.GhostToggleButton");
            b.MinWidth = 34; b.MinHeight = 30; b.Padding = new Thickness(8, 0, 8, 0);
            SwUi.SetCornerRadius(b, new CornerRadius(15));
        }
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Reflects the controller state in the toggle buttons.</summary>
    public void Sync()
    {
        _a11.IsChecked = _ctl.AspectLock == AspectLock.Square;
        _a43.IsChecked = _ctl.AspectLock == AspectLock.FourThree;
        _a169.IsChecked = _ctl.AspectLock == AspectLock.SixteenNine;
        _mag.IsChecked = _ctl.MagnifierOn;
        _guides.IsChecked = _ctl.GuidesOn;
    }
}
