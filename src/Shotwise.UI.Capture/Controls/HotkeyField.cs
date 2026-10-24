using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Infrastructure;
using Shotwise.UI.Common.Controls;

namespace Shotwise.UI.Capture.Controls;

/// <summary>
/// <see cref="HotkeyBox"/> with availability feedback (preset dialog): a live status line (Available / In use by another app — likely X /
/// Used by Shotwise for "Y" [Move here] / Reserved: reason), a Check button that asks Windows again, a "How to free it" tip, and a row
/// of suggestion chips for the binding's purpose (click applies the key). Without an <see cref="IHotkeyAdvisor"/> it is a plain box.
/// </summary>
public sealed class HotkeyField : StackPanel
{
    private readonly HotkeyBox _box = new();
    private readonly IHotkeyAdvisor? _advisor;
    private readonly string _bindingId;
    private readonly HotkeyPurpose _purpose;
    private readonly string _targetName;
    private readonly DockPanel _status;
    private readonly SwIcon _statusIcon;
    private readonly TextBlock _statusText;
    private readonly Button _move;
    private readonly TextBlock _tip = Ui.Label("", 11.5, wrap: true, brush: "Shotwise.TextSecondaryBrush");
    private readonly Button _openSettings;
    private readonly StackPanel _suggestions = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _noChips = Ui.Label(CS.Get("HotkeyCheck_NoSuggestions"), 11.5, wrap: true, brush: "Shotwise.TextTertiaryBrush");
    private string? _moveFromId;
    private string? _moveFromName;
    private Hotkey? _moveKey;

    public event EventHandler? ValueChanged;

    /// <param name="advisor">Availability / suggestions service (null = plain hotkey box).</param>
    /// <param name="bindingId">Registration id being edited (e.g. <see cref="HotkeyIds.Preset"/>).</param>
    /// <param name="purpose">Drives the suggestions.</param>
    /// <param name="targetName">Name of the binding ("All-in-One") for "Move PrintScreen to All-in-One".</param>
    public HotkeyField(IHotkeyAdvisor? advisor, string bindingId, HotkeyPurpose purpose, string targetName)
    {
        Ui.EnsureStyles();
        _advisor = advisor; _bindingId = bindingId; _purpose = purpose; _targetName = targetName;

        var check = Ui.TextButton(CS.Get("HotkeyCheck_Check"), () => Refresh(invalidate: true), "Icon.Search.Regular");
        check.ToolTip = CS.Get("HotkeyCheck_CheckTip");
        check.Margin = new Thickness(8, 0, 0, 0);
        check.MinHeight = 34;
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_box);
        Grid.SetColumn(check, 1);
        top.Children.Add(check);
        Children.Add(top);
        if (advisor is null) check.Visibility = Visibility.Collapsed;

        _status = HotkeyAdviceUi.StatusRow(out _statusIcon, out _statusText);
        _move = Ui.TextButton(CS.Get("HotkeyCheck_MoveHere"), MoveHere, ghost: true);
        _move.MinHeight = 24; _move.Padding = new Thickness(8, 0, 8, 0); _move.Margin = new Thickness(8, -3, 0, 0);
        _move.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(_move, Dock.Right);
        _status.Children.Insert(0, _move);
        _status.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_status);
        _tip.Margin = new Thickness(20, 4, 0, 0);
        Children.Add(_tip);
        _openSettings = Ui.TextButton(CS.Get("HotkeyCheck_OpenWindowsSettings"), HotkeyAdviceUi.OpenKeyboardSettings, ghost: true);
        _openSettings.MinHeight = 24; _openSettings.Padding = new Thickness(8, 0, 8, 0); _openSettings.Margin = new Thickness(12, 2, 0, 0);
        _openSettings.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(_openSettings);

        var header = Ui.Label(CS.Get("HotkeyCheck_Suggestions"), 12, secondary: true);
        header.Margin = new Thickness(0, 0, 0, 6);
        _suggestions.Children.Add(header);
        _suggestions.Children.Add(_chips);
        _suggestions.Children.Add(_noChips);
        Children.Add(_suggestions);

        _box.ValueChanged += (_, _) => { if (_box.Value != _moveKey) ClearMove(); Refresh(invalidate: false); ValueChanged?.Invoke(this, EventArgs.Empty); };
        Loaded += (_, _) => Refresh(invalidate: false);
        Refresh(invalidate: false);
    }

    public HotkeyBox Box => _box;

    public Hotkey? Value
    {
        get => _box.Value;
        set { _box.Value = value; if (value != _moveKey) ClearMove(); Refresh(invalidate: false); }
    }

    /// <summary>Last availability of <see cref="Value"/> (null when empty or no advisor).</summary>
    public HotkeyAvailability? Availability { get; private set; }

    /// <summary>The binding to clear on save when the user chose "Move here" for the current key, else null.</summary>
    public string? MoveFromId => _moveKey is { } k && k == _box.Value ? _moveFromId : null;

    public new void Focus() => _box.Focus();

    private void ClearMove() { _moveFromId = null; _moveFromName = null; _moveKey = null; }

    private void MoveHere()
    {
        if (Availability is not { Status: HotkeyAvailabilityStatus.UsedByShotwise, ShotwiseBindingId: { } from } a) return;
        _moveFromId = from; _moveFromName = a.ShotwiseBindingName; _moveKey = a.Key;
        Refresh(invalidate: false);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(HotkeySuggestion s)
    {
        _box.Value = s.Key;
        ClearMove();
        if (s.CanMoveHere) { _moveFromId = s.Availability.ShotwiseBindingId; _moveFromName = s.Availability.ShotwiseBindingName; _moveKey = s.Key; }
        Refresh(invalidate: false);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-checks the current key and rebuilds the suggestions (<paramref name="invalidate"/> = ask Windows again).</summary>
    public void Refresh(bool invalidate)
    {
        if (_advisor is null)
        {
            _status.Visibility = _tip.Visibility = _openSettings.Visibility = _suggestions.Visibility = Visibility.Collapsed;
            return;
        }
        if (invalidate) _advisor.Invalidate();
        var value = _box.Value;
        Availability = null;
        _move.Visibility = Visibility.Collapsed;
        _tip.Visibility = _openSettings.Visibility = Visibility.Collapsed;
        if (value is { } v)
        {
            HotkeyAvailability a;
            try { a = _advisor.CheckAvailability(v, _bindingId); }
            catch (Exception) { a = new HotkeyAvailability(v, HotkeyAvailabilityStatus.Available); }
            Availability = a;
            _status.Visibility = Visibility.Visible;
            if (MoveFromId is not null)
            {
                _statusIcon.Source = "Icon.Info.Filled";
                _statusIcon.SetResourceReference(SwIcon.ForegroundProperty, "Shotwise.AccentTextBrush");
                _statusText.Text = CS.Format("HotkeyCheck_MovePending", v.ToDisplayString(), _moveFromName ?? "");
                _statusText.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.AccentTextBrush");
            }
            else
            {
                HotkeyAdviceUi.Apply(_statusIcon, _statusText, a);
                if (a.Status == HotkeyAvailabilityStatus.UsedByShotwise && a.ShotwiseBindingId is not null)
                {
                    _move.Visibility = Visibility.Visible;
                    _move.ToolTip = CS.Format("HotkeyCheck_MoveHereTip", a.ShotwiseBindingName ?? "", v.ToDisplayString());
                }
                if (HotkeyAdviceUi.FreeTip(a) is { } tip)
                {
                    _tip.Text = tip;
                    _tip.Visibility = Visibility.Visible;
                    if (v.Key == VirtualKey.PrintScreen) _openSettings.Visibility = Visibility.Visible;
                }
            }
        }
        else _status.Visibility = Visibility.Collapsed;

        _chips.Children.Clear();
        IReadOnlyList<HotkeySuggestion> list;
        try { list = _advisor.Suggest(_purpose, _bindingId, 6, null); }
        catch (Exception) { list = Array.Empty<HotkeySuggestion>(); }
        foreach (var s in list.Where(s => s.Key != value)) _chips.Children.Add(HotkeyAdviceUi.Chip(s, _targetName, Apply));
        _noChips.Visibility = _chips.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suggestions.Visibility = Visibility.Visible;
    }
}
