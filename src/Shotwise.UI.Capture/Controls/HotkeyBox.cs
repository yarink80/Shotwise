using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shotwise.Core.Platform;
using Shotwise.UI.Capture.Infrastructure;
using CoreModifiers = Shotwise.Core.Platform.ModifierKeys;

namespace Shotwise.UI.Capture.Controls;

/// <summary>
/// Hotkey capture control: focus it and press a combination. Backspace / Delete (no modifiers) clears, Esc leaves without changing.
/// Shows the live modifiers while keys are held and flags combinations that cannot be used as global hotkeys.
/// While focused, keys are read through <see cref="KeyComboRecorder"/> (a low-level hook) so global hotkeys — including
/// PrintScreen held by Shotwise or another app — are recorded instead of firing.
/// </summary>
public sealed class HotkeyBox : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly Button _clear;
    private readonly KeyComboRecorder _recorder;
    private Hotkey? _value;
    private string _live = "";

    public event EventHandler? ValueChanged;

    public HotkeyBox()
    {
        Ui.EnsureStyles();
        Focusable = true;
        FocusVisualStyle = null;
        MinHeight = 34;
        MinWidth = 190;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.IBeam;
        SetResourceReference(BackgroundProperty, "Shotwise.Surface1Brush");
        SetResourceReference(BorderBrushProperty, "Shotwise.BorderStrongBrush");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextPrimaryBrush");
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "Shotwise.FontFamily");
        _clear = Ui.IconButton("Icon.Dismiss.Regular", CS.Get("Hotkey_Clear"), () => { Value = null; Raise(); }, 14);
        _clear.Width = _clear.Height = 26;
        _clear.Focusable = false;
        _clear.VerticalAlignment = VerticalAlignment.Center;
        _clear.Margin = new Thickness(0, 0, 4, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_text);
        Grid.SetColumn(_clear, 1);
        grid.Children.Add(_clear);
        Child = grid;

        _recorder = new KeyComboRecorder(Dispatcher);
        _recorder.Captured += h => { _live = ""; Value = h; Raise(); };
        _recorder.ModifiersChanged += m => { _live = m == CoreModifiers.None ? "" : new Hotkey(VirtualKey.None, m).ToDisplayStringSafe(); Refresh(); };
        _recorder.ClearRequested += () => { Value = null; Raise(); };
        _recorder.CancelRequested += () => Keyboard.ClearFocus();

        GotKeyboardFocus += (_, _) => { _live = ""; _recorder.Start(); Refresh(); SetResourceReference(BorderBrushProperty, "Shotwise.AccentBrush"); BorderThickness = new Thickness(2); };
        LostKeyboardFocus += (_, _) => { _live = ""; _recorder.Stop(); Refresh(); SetResourceReference(BorderBrushProperty, "Shotwise.BorderStrongBrush"); BorderThickness = new Thickness(1); };
        Unloaded += (_, _) => _recorder.Stop();
        MouseLeftButtonDown += (_, _) => Focus();
        // Fallback when the hook could not be installed. PrintScreen never arrives as KeyDown in WPF, so accept it on KeyUp.
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += (_, e) =>
        {
            if (_recorder.IsActive) { e.Handled = e.Key != Key.Tab; return; }
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Snapshot) { e.Handled = true; _live = ""; Value = new Hotkey(VirtualKey.PrintScreen, (CoreModifiers)(int)Keyboard.Modifiers); Raise(); return; }
            _live = ""; Refresh();
        };
        Refresh();
    }

    public Hotkey? Value
    {
        get => _value;
        set { _value = value is { IsEmpty: true } ? null : value; Refresh(); }
    }

    private void Raise() => ValueChanged?.Invoke(this, EventArgs.Empty);

    private void Refresh()
    {
        bool focused = IsKeyboardFocusWithin;
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextPrimaryBrush");
        if (focused && _live.Length > 0) { _text.Text = _live + "+"; _text.Opacity = 1; }
        else if (_value is { } v)
        {
            _text.Text = v.ToDisplayString();
            _text.Opacity = 1;
            if (!v.IsValidGlobalHotkey) _text.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush");
        }
        else { _text.Text = focused ? CS.Get("Hotkey_Press") : CS.Get("Hotkey_None"); _text.Opacity = 0.6; _text.FontWeight = FontWeights.Normal; }
        if (_value is not null || (focused && _live.Length > 0)) _text.FontWeight = FontWeights.SemiBold;
        _clear.Visibility = _value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_recorder.IsActive) { if (e.Key != Key.Tab) e.Handled = true; return; }   // the hook already handled it
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        var mods = (CoreModifiers)(int)Keyboard.Modifiers;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            _live = new Hotkey(VirtualKey.None, mods).ToDisplayStringSafe();
            Refresh();
            return;
        }
        if (mods == CoreModifiers.None)
        {
            if (key is Key.Back or Key.Delete) { Value = null; Raise(); return; }
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key == Key.Tab) { e.Handled = false; return; }
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        Value = new Hotkey((VirtualKey)vk, mods);
        Raise();
    }
}

internal static class HotkeyDisplayExtensions
{
    /// <summary>Modifier-only display ("Ctrl+Shift") used while the user is still pressing keys.</summary>
    public static string ToDisplayStringSafe(this Hotkey h)
    {
        var parts = new List<string>();
        if ((h.Modifiers & CoreModifiers.Control) != 0) parts.Add("Ctrl");
        if ((h.Modifiers & CoreModifiers.Alt) != 0) parts.Add("Alt");
        if ((h.Modifiers & CoreModifiers.Shift) != 0) parts.Add("Shift");
        if ((h.Modifiers & CoreModifiers.Windows) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }
}
