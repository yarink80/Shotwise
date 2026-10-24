using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Controls;

/// <summary>Shared text and widgets for hotkey availability (preset dialog and Preferences → Hotkeys).</summary>
internal static class HotkeyAdviceUi
{
    /// <summary>Localized reason of a reservation ("Windows uses it to copy the active window").</summary>
    public static string ReservedText(HotkeyReservation r)
    {
        var key = "HotkeyReserved_" + r;
        var text = CS.Get(key);
        return text == $"[{key}]" ? HotkeyRules.Describe(r) : text;
    }

    public static string Owners(IReadOnlyList<string> owners) => string.Join(", ", owners);

    /// <summary>Icon key, text and text brush of the status line for <paramref name="a"/>.</summary>
    public static (string Icon, string Text, string Brush) Status(HotkeyAvailability a) => a.Status switch
    {
        HotkeyAvailabilityStatus.Available => ("Icon.Checkmark.Filled", CS.Get("HotkeyCheck_Available"), "Shotwise.SuccessTextBrush"),
        HotkeyAvailabilityStatus.InUseByOtherApp => ("Icon.Warning.Filled",
            a.LikelyOwners.Count > 0 ? CS.Format("HotkeyCheck_InUseLikely", Owners(a.LikelyOwners)) : CS.Get("HotkeyCheck_InUse"), "Shotwise.WarningTextBrush"),
        HotkeyAvailabilityStatus.UsedByShotwise => ("Icon.Info.Filled", CS.Format("HotkeyCheck_UsedByShotwise", a.ShotwiseBindingName ?? ""), "Shotwise.AccentTextBrush"),
        HotkeyAvailabilityStatus.Reserved => ("Icon.Error.Filled", CS.Format("HotkeyCheck_Reserved", ReservedText(a.Reservation)), "Shotwise.DangerTextBrush"),
        _ => ("Icon.Error.Filled", CS.Get("Hotkey_Unsupported"), "Shotwise.DangerTextBrush"),
    };

    /// <summary>"How to free it" for a key held by another app, or null.</summary>
    public static string? FreeTip(HotkeyAvailability a)
    {
        if (a.Status != HotkeyAvailabilityStatus.InUseByOtherApp) return null;
        var key = a.Key.ToDisplayString();
        if (a.LikelyOwners.Contains(KnownHotkeyOwners.WindowsSnippingName)) return CS.Get("HotkeyCheck_FreeTipSnipping");
        var tip = a.LikelyOwners.Count > 0 ? CS.Format("HotkeyCheck_FreeTip", key, a.LikelyOwners[0]) : CS.Format("HotkeyCheck_FreeTipUnknown", key);
        if (a.Key.Key == VirtualKey.PrintScreen) tip += " " + CS.Get("HotkeyCheck_FreeTipPrintScreen");
        return tip;
    }

    /// <summary>Opens Windows Settings → Accessibility → Keyboard (the Print screen key option).</summary>
    public static void OpenKeyboardSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
        catch (Exception) { /* settings app missing: nothing to do */ }
    }

    /// <summary>
    /// A key-cap chip for a suggestion. Available → click applies; held by another Shotwise hotkey → "Move {key} to {target}" (accent);
    /// held by another app → disabled with "{key} — in use by {owner}".
    /// </summary>
    public static Button Chip(HotkeySuggestion s, string targetName, Action<HotkeySuggestion> apply)
    {
        var key = s.Key.ToDisplayString();
        string text, tip;
        if (s.IsAvailable) { text = key; tip = CS.Format("HotkeyCheck_ChipTip", key); }
        else if (s.CanMoveHere) { text = CS.Format("HotkeyCheck_ChipMove", key, targetName); tip = CS.Format("HotkeyCheck_MoveHereTip", s.Availability.ShotwiseBindingName ?? "", key); }
        else
        {
            text = s.Availability.LikelyOwners.Count > 0 ? CS.Format("HotkeyCheck_ChipInUse", key, s.Availability.LikelyOwners[0]) : CS.Format("HotkeyCheck_ChipInUseUnknown", key);
            tip = FreeTip(s.Availability) ?? text;
        }
        var b = new Button { Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 }, ToolTip = tip };
        b.WithStyle("Capture.KeyChip");
        ToolTipService.SetShowOnDisabled(b, true);
        if (s.CanMoveHere) b.Tag = "move";
        b.IsEnabled = s.IsAvailable || s.CanMoveHere;
        Ui.AutomationProperties(b, text);
        b.Click += (_, e) => { e.Handled = true; apply(s); };
        return b;
    }

    /// <summary>Icon + text status row (wrapping text).</summary>
    public static DockPanel StatusRow(out Shotwise.UI.Common.Controls.SwIcon icon, out TextBlock text)
    {
        icon = Ui.Icon("Icon.Checkmark.Filled", 14);
        icon.Margin = new Thickness(0, 1, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        text = Ui.Label("", 12, wrap: true);
        text.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    public static void Apply(Shotwise.UI.Common.Controls.SwIcon icon, TextBlock text, HotkeyAvailability a)
    {
        var (i, t, brush) = Status(a);
        icon.Source = i;
        icon.SetResourceReference(Shotwise.UI.Common.Controls.SwIcon.ForegroundProperty, brush);
        text.Text = t;
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
