using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Shotwise.Core.Styles;
using Shotwise.Editor;
using Shotwise.UI.Editor.Infrastructure;

namespace Shotwise.UI.Editor.Panels;

/// <summary>
/// Editor ▸ "Show cursor quick-style popup" (EDT-07): after picking a drawing tool, a small popup at the mouse offers the
/// tool's quick styles of the active theme; clicking one uses it. Closes on click-away, on choice, or after a few seconds.
/// </summary>
internal sealed class QuickStylePopup
{
    private const int MaxStyles = 8;
    private readonly EditorContext _ctx;
    private readonly PreviewRenderer _previews;
    private readonly Popup _popup = new() { Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = 12, VerticalOffset = 16 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public QuickStylePopup(EditorContext ctx, PreviewRenderer previews)
    {
        _ctx = ctx;
        _previews = previews;
        _timer.Tick += (_, _) => Close();
    }

    public void Close()
    {
        _timer.Stop();
        _popup.IsOpen = false;
    }

    public void ShowFor(EditorSession session, ToolKind tool, UIElement placementTarget)
    {
        Close();
        if (!_ctx.Prefs.ShowCursorQuickStylePopup || _ctx.Styles is not { } styles || !ToolInfo.IsAnnotationTool(tool)) return;
        var list = styles.ActiveTheme.StylesFor(tool).Take(MaxStyles).ToList();
        if (list.Count < 2) return;
        var panel = new WrapPanel { MaxWidth = 4 * 60 };
        foreach (var style in list)
        {
            var s = style;
            var tile = new Shotwise.UI.Common.Controls.QuickStyleTile { TileSize = 52, Thumbnail = _previews.QuickStyle(s, 50, 50), IsFavorite = s.IsFavorite, Title = s.Name };
            var b = new Button { Content = tile, Margin = new Thickness(1), ToolTip = s.Name };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Shotwise.GhostButton");
            b.Padding = new Thickness(2);
            Shotwise.UI.Common.Controls.SwUi.SetCornerRadius(b, new CornerRadius(10));
            System.Windows.Automation.AutomationProperties.SetName(b, s.Name);
            b.Click += (_, _) => { session.UseQuickStyle(s); Close(); };
            panel.Children.Add(b);
        }
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = ES.F("QuickPopup_Title", "{0} styles", ToolInfo.Name(tool)), Margin = new Thickness(4, 0, 4, 6) }.Styled("Shotwise.Text.Overline"));
        stack.Children.Add(panel);
        var card = new Shotwise.UI.Common.Controls.Card { Content = stack, Padding = new Thickness(8, 8, 8, 6), Elevation = 2, Margin = new Thickness(10, 4, 10, 16) };
        card.MouseEnter += (_, _) => _timer.Stop();
        card.MouseLeave += (_, _) => _timer.Start();
        _popup.Child = card;
        _popup.PlacementTarget = placementTarget;
        _popup.IsOpen = true;
        _timer.Start();
    }
}
