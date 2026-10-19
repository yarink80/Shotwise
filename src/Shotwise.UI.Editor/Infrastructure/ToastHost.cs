using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>
/// In-window transient notifications (share results, "nothing found", feature unavailable…) with optional actions, shown as
/// design-system toasts (<see cref="InfoBar"/> with the <c>Shotwise.Toast</c> style: Surface1 card, flyout elevation, severity icon).
/// </summary>
internal sealed class ToastHost : StackPanel
{
    public ToastHost()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 8, 8);
        IsHitTestVisible = true;
    }

    public void Show(string message, MessageKind kind = MessageKind.Info, TimeSpan? duration = null, params (string Text, Action Action)[] actions)
    {
        var toast = new InfoBar
        {
            Message = message,
            Severity = kind switch
            {
                MessageKind.Error => InfoSeverity.Error,
                MessageKind.Warning => InfoSeverity.Warning,
                MessageKind.Success => InfoSeverity.Success,
                _ => InfoSeverity.Informational,
            },
            MinWidth = 280,
        }.Styled("Shotwise.Toast");
        toast.Margin = new Thickness(16, 6, 16, 10);
        if (actions.Length > 0)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (t, a) in actions)
            {
                var action = a;
                var b = UiKit.GhostButton(t, null, () => { action(); Children.Remove(toast); });
                b.SetResourceReference(Control.ForegroundProperty, Tok.AccentText);
                b.FontWeight = FontWeights.SemiBold;
                b.MinHeight = 28;
                bar.Children.Add(b);
            }
            toast.Action = bar;
        }
        toast.Closed += (_, _) => Children.Remove(toast);
        Children.Add(toast);
        while (Children.Count > 4) Children.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(actions.Length > 0 ? 12 : 6) };
        timer.Tick += (_, _) => { timer.Stop(); Children.Remove(toast); };
        toast.MouseEnter += (_, _) => timer.Stop();
        toast.MouseLeave += (_, _) => timer.Start();
        timer.Start();
    }
}
