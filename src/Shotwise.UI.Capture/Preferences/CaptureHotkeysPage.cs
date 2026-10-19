using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Capture.Infrastructure;
using CoreModifiers = Shotwise.Core.Platform.ModifierKeys;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>
/// Preferences → Hotkeys (POLISH-1): one row per global hotkey and per preset with the key chip, a live status pill (Active, Using
/// fallback, In use by another app, Off, ...), Record (press a new combination; global hotkeys are suspended meanwhile so the keys
/// reach the recorder), Clear, and Restore defaults. Conflicts (unsupported combinations, keys used by another row) are rejected inline.
/// Settings hotkeys edit the staged <see cref="AppSettings"/>; preset hotkeys are staged here and saved through the host's commit
/// hook on OK (or immediately when the page is used live). The coordinator re-registers on both changes.
/// </summary>
public sealed class CaptureHotkeysPage : UserControl
{
    private const string FallbackRowId = HotkeyIds.GlobalCapture + ".fallback";

    private readonly AppSettings _s;
    private readonly Action _changed;
    private readonly IServiceProvider? _sp;
    private readonly Action<Func<Task>>? _registerCommit;
    private readonly IHotkeyStatusSource? _status;
    private readonly List<Row> _rows = new();
    private bool _commitRegistered;
    private Row? _recording;
    private IDisposable? _suspension;

    private sealed class Row
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string? Hint { get; init; }
        public required Func<Hotkey?> Get { get; init; }
        public required Action<Hotkey?> Set { get; init; }
        public Func<Hotkey?>? Default { get; init; }
        public string? PresetId { get; init; }
        public Hotkey? Original { get; set; }
        public Border Chip { get; } = new() { MinWidth = 132, MinHeight = 28, Focusable = true, FocusVisualStyle = null, Padding = new Thickness(10, 3, 10, 3) };
        public TextBlock ChipText { get; } = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        public Border Pill { get; } = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        public TextBlock PillText { get; } = new() { FontSize = 11, FontWeight = FontWeights.SemiBold };
        public TextBlock Detail { get; } = Ui.Label("", 11.5, wrap: true);
        public Button Clear { get; set; } = null!;
        public string Live { get; set; } = "";
    }

    public CaptureHotkeysPage(AppSettings s, IServiceProvider? sp, Action changed, Action<Func<Task>>? registerCommit = null)
    {
        _s = s; _sp = sp; _changed = changed; _registerCommit = registerCommit;
        _status = sp?.GetService<IHotkeyStatusSource>();
        var h = s.Hotkeys;
        var defaults = HotkeySettings.Defaults();
        var b = new PrefsBuilder(changed);

        b.Section(CS.Get("Prefs_HotkeysGlobal"), "Icon.Keyboard.Regular");
        b.Note(CS.Get("Prefs_HotkeysIntro"));
        AddRow(b, new Row { Id = HotkeyIds.GlobalCapture, Label = CS.Get("Hotkey_Capture"), Hint = CS.Get("Hotkey_GlobalHint"), Get = () => h.GlobalCapture, Set = v => h.GlobalCapture = v, Default = () => defaults.GlobalCapture });
        AddRow(b, new Row { Id = FallbackRowId, Label = CS.Get("Hotkey_CaptureFallback"), Hint = CS.Get("Hotkey_CaptureFallbackHint"), Get = () => h.GlobalCaptureFallback, Set = v => h.GlobalCaptureFallback = v, Default = () => defaults.GlobalCaptureFallback });
        AddRow(b, new Row { Id = HotkeyIds.Repeat, Label = CS.Get("Hotkey_Repeat"), Get = () => h.RepeatLastCapture, Set = v => h.RepeatLastCapture = v, Default = () => defaults.RepeatLastCapture });
        AddRow(b, new Row { Id = HotkeyIds.ShowHide, Label = CS.Get("Hotkey_ShowHide"), Get = () => h.ShowHideApp, Set = v => h.ShowHideApp = v, Default = () => defaults.ShowHideApp });
        AddRow(b, new Row { Id = HotkeyIds.VideoPauseResume, Label = CS.Get("Hotkey_VideoPause"), Get = () => h.VideoStartPauseResume, Set = v => h.VideoStartPauseResume = v, Default = () => defaults.VideoStartPauseResume });
        AddRow(b, new Row { Id = HotkeyIds.VideoStop, Label = CS.Get("Hotkey_VideoStop"), Get = () => h.VideoStop, Set = v => h.VideoStop = v, Default = () => defaults.VideoStop });
        AddRow(b, new Row { Id = HotkeyIds.ScreenDraw, Label = CS.Get("Hotkey_Draw"), Hint = CS.Get("Hotkey_RecordingHint"), Get = () => h.ScreenDrawToggle, Set = v => h.ScreenDrawToggle = v, Default = () => defaults.ScreenDrawToggle });
        AddRow(b, new Row { Id = HotkeyIds.WebcamFullscreen, Label = CS.Get("Hotkey_Webcam"), Hint = CS.Get("Hotkey_RecordingHint"), Get = () => h.WebcamFullscreenToggle, Set = v => h.WebcamFullscreenToggle = v, Default = () => defaults.WebcamFullscreenToggle });

        var repo = sp?.GetService<IPresetRepository>();
        if (repo is not null)
        {
            b.Section(CS.Get("Prefs_HotkeysPresets"), "Icon.Collections.Regular");
            foreach (var p in repo.All)
            {
                Hotkey? pending = p.Hotkey;
                var id = p.Id;
                AddRow(b, new Row
                {
                    Id = HotkeyIds.Preset(id), PresetId = id, Label = p.Name, Hint = CS.Get("Hotkey_PresetHint"),
                    Get = () => pending, Set = v => pending = v,
                    Default = p.IsBuiltIn ? () => PresetDefaults.DefaultHotkey(id) : null,
                });
            }
        }

        b.Section("");
        b.Check(CS.Get("Prefs_AllowOverride"), () => h.AllowOverrideOtherAssignments, v => h.AllowOverrideOtherAssignments = v);
        var restore = Ui.TextButton(CS.Get("Prefs_RestoreDefaults"), RestoreDefaults, "Icon.Reset.Regular");
        restore.ToolTip = CS.Get("Prefs_RestoreDefaultsTip");
        restore.Margin = new Thickness(6, 8, 6, 4);
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        b.Add(restore);

        Content = b.Wrap();
        if (_status is not null) _status.Changed += OnStatusChanged;
        Unloaded += (_, _) => { EndRecording(); if (_status is not null) _status.Changed -= OnStatusChanged; };
        Loaded += (_, _) => { if (_status is not null) { _status.Changed -= OnStatusChanged; _status.Changed += OnStatusChanged; } RefreshAll(); };
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshAll));

    // ------------------------------------------------------------------ row layout

    private void AddRow(PrefsBuilder b, Row row)
    {
        row.Original = row.Get();
        var name = Ui.Label(row.Label, 13);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(name);
        if (row.Hint is not null)
        {
            var hint = Ui.Label(row.Hint, 11.5);
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.TextTertiaryBrush");
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(hint);
        }

        row.Chip.Child = row.ChipText;
        row.Chip.Cursor = Cursors.Hand;
        row.Chip.ToolTip = CS.Get("Hotkey_RecordTip");
        System.Windows.Automation.AutomationProperties.SetName(row.Chip, row.Label);
        row.Chip.MouseLeftButtonUp += (_, _) => StartRecording(row);
        row.Chip.PreviewKeyDown += (_, e) => OnRecorderKey(row, e, isUp: false);
        row.Chip.PreviewKeyUp += (_, e) => OnRecorderKey(row, e, isUp: true);
        row.Chip.LostKeyboardFocus += (_, _) => { if (ReferenceEquals(_recording, row)) EndRecording(); };
        row.Pill.Child = row.PillText;

        var record = Ui.TextButton(CS.Get("Hotkey_Record"), () => StartRecording(row), ghost: true);
        record.MinHeight = 28; record.Padding = new Thickness(10, 0, 10, 0); record.Margin = new Thickness(8, 0, 0, 0);
        record.ToolTip = CS.Get("Hotkey_RecordTip");
        Ui.AutomationProperties(record, CS.Get("Hotkey_Record") + " " + row.Label);
        row.Clear = Ui.IconButton("Icon.Dismiss.Regular", CS.Get("Hotkey_Clear"), () => Assign(row, null), 14);
        row.Clear.Width = row.Clear.Height = 28;
        Ui.AutomationProperties(row.Clear, CS.Get("Hotkey_ClearShort") + " " + row.Label);

        var grid = new Grid { Margin = new Thickness(6, 4, 2, 0), MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Pill.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(names);
        Place(grid, row.Chip, 1); Place(grid, row.Pill, 2); Place(grid, record, 3); Place(grid, row.Clear, 4);
        row.Chip.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.ChipText.VerticalAlignment = VerticalAlignment.Center;
        b.Add(grid);
        row.Detail.Margin = new Thickness(6, 0, 6, 4);
        row.Detail.Visibility = Visibility.Collapsed;
        b.Add(row.Detail);
        _rows.Add(row);
        Refresh(row);
    }

    private static void Place(Grid g, FrameworkElement e, int column)
    {
        e.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    // ------------------------------------------------------------------ recording a combination

    private void StartRecording(Row row)
    {
        if (ReferenceEquals(_recording, row)) return;
        EndRecording();
        _recording = row;
        row.Live = "";
        // Release our own global hotkeys so e.g. Ctrl+Shift+PrintScreen reaches the recorder instead of starting a capture.
        _suspension = (_sp?.GetService<ICaptureCoordinator>() as CaptureCoordinator)?.SuspendHotkeys();
        Refresh(row);
        row.Chip.Focus();
        Keyboard.Focus(row.Chip);
    }

    private void EndRecording()
    {
        var row = _recording;
        _recording = null;
        _suspension?.Dispose();
        _suspension = null;
        if (row is not null) { row.Live = ""; Refresh(row); }
    }

    private void OnRecorderKey(Row row, KeyEventArgs e, bool isUp)
    {
        if (!ReferenceEquals(_recording, row)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        var mods = (CoreModifiers)(int)Keyboard.Modifiers;
        // PrintScreen only produces a key-up message, so it is accepted on key up; every other key on key down.
        if (isUp && key != Key.Snapshot) { if (IsModifier(key)) { row.Live = ModifiersText(mods); Refresh(row); } e.Handled = true; return; }
        if (!isUp && key == Key.Snapshot) { e.Handled = true; return; }
        e.Handled = true;
        if (IsModifier(key)) { row.Live = ModifiersText(mods); Refresh(row); return; }
        if (mods == CoreModifiers.None)
        {
            if (key == Key.Escape) { EndRecording(); return; }
            if (key == Key.Tab) { e.Handled = false; EndRecording(); return; }
            if (key is Key.Back or Key.Delete) { EndRecording(); Assign(row, null); return; }
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        EndRecording();
        Assign(row, new Hotkey((VirtualKey)vk, mods));
    }

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string ModifiersText(CoreModifiers m)
    {
        var parts = new List<string>();
        if ((m & CoreModifiers.Control) != 0) parts.Add("Ctrl");
        if ((m & CoreModifiers.Alt) != 0) parts.Add("Alt");
        if ((m & CoreModifiers.Shift) != 0) parts.Add("Shift");
        if ((m & CoreModifiers.Windows) != 0) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    // ------------------------------------------------------------------ assigning / validating

    private void Assign(Row row, Hotkey? key)
    {
        if (key is { } k)
        {
            var others = _rows.Where(r => !ReferenceEquals(r, row)).Select(r => (r.Label, r.Get()));
            var clash = HotkeyRegistration.FindConflict(k, others, out bool unsupported);
            if (unsupported) { ShowError(row, CS.Get("Hotkey_Unsupported")); return; }
            if (clash is not null) { ShowError(row, CS.Format("PresetEdit_HotkeyUsed", clash)); return; }
        }
        if (row.Get() == key) { Refresh(row); return; }
        row.Set(key);
        if (row.PresetId is not null) PresetChanged(); else _changed();
        Refresh(row);
    }

    private void ShowError(Row row, string text)
    {
        Refresh(row);
        row.Detail.Text = text;
        row.Detail.SetResourceReference(TextBlock.ForegroundProperty, "Shotwise.DangerTextBrush");
        row.Detail.Visibility = Visibility.Visible;
    }

    private void RestoreDefaults()
    {
        EndRecording();
        // settings hotkeys first, then the built-in preset defaults that do not collide with what the user kept
        foreach (var r in _rows.Where(r => r.PresetId is null && r.Default is not null)) r.Set(r.Default!());
        foreach (var r in _rows.Where(r => r.PresetId is not null && r.Default is not null))
        {
            var d = r.Default!();
            if (d is { } k && _rows.Any(o => !ReferenceEquals(o, r) && o.Get() == k)) continue;
            r.Set(d);
        }
        _changed();
        PresetChanged();
        RefreshAll();
    }

    private void PresetChanged()
    {
        _changed();
        if (_registerCommit is null) { _ = ApplyPresetHotkeysAsync(); return; }
        if (_commitRegistered) return;
        _commitRegistered = true;
        _registerCommit(ApplyPresetHotkeysAsync);
    }

    /// <summary>Saves the staged preset hotkeys (a clone per preset, so the repository can tell an explicit clear from "never set").</summary>
    private async Task ApplyPresetHotkeysAsync()
    {
        var repo = _sp?.GetService<IPresetRepository>();
        if (repo is null) return;
        foreach (var r in _rows.Where(r => r.PresetId is not null && r.Get() != r.Original))
        {
            if (repo.Find(r.PresetId!) is not { } current) continue;
            var copy = current.Clone();
            copy.Hotkey = r.Get();
            try
            {
                await repo.SaveAsync(copy);
                r.Original = copy.Hotkey;
            }
            catch (Exception ex) { _sp?.GetService<ILogger<CaptureHotkeysPage>>()?.LogWarning(ex, "Saving the hotkey of preset {Preset} failed", r.Label); }
        }
    }

    // ------------------------------------------------------------------ status display

    private void RefreshAll()
    {
        foreach (var r in _rows) Refresh(r);
    }

    private void Refresh(Row row)
    {
        var value = row.Get();
        bool recording = ReferenceEquals(_recording, row);
        row.Chip.SetResourceReference(StyleProperty, recording ? "Shotwise.AccentChip" : "Shotwise.Chip");
        row.Chip.Padding = new Thickness(10, 3, 10, 3);
        row.ChipText.Text = recording ? (row.Live.Length > 0 ? row.Live : CS.Get("Hotkey_Recording")) : value is { IsEmpty: false } v ? v.ToDisplayString() : CS.Get("Hotkey_None");
        row.ChipText.FontWeight = value is null && !recording ? FontWeights.Normal : FontWeights.SemiBold;
        row.ChipText.SetResourceReference(TextBlock.ForegroundProperty, recording ? "Shotwise.AccentTextBrush" : value is null ? "Shotwise.TextTertiaryBrush" : "Shotwise.TextPrimaryBrush");
        row.Clear.Visibility = value is null ? Visibility.Hidden : Visibility.Visible;

        var (state, detail, detailBrush) = StateOf(row);
        var (text, bg, fg) = state switch
        {
            "pending" => (CS.Get("Hotkey_State_Pending"), "Shotwise.AccentSubtleBrush", "Shotwise.AccentTextBrush"),
            nameof(HotkeyState.Active) => (CS.Get("Hotkey_State_Active"), "Shotwise.SuccessSubtleBrush", "Shotwise.SuccessTextBrush"),
            nameof(HotkeyState.Fallback) => (CS.Get("Hotkey_State_Fallback"), "Shotwise.WarningSubtleBrush", "Shotwise.WarningTextBrush"),
            nameof(HotkeyState.InUse) => (CS.Get("Hotkey_State_InUse"), "Shotwise.DangerSubtleBrush", "Shotwise.DangerTextBrush"),
            nameof(HotkeyState.Duplicate) => (CS.Get("Hotkey_State_Duplicate"), "Shotwise.DangerSubtleBrush", "Shotwise.DangerTextBrush"),
            nameof(HotkeyState.Unsupported) => (CS.Get("Hotkey_State_Unsupported"), "Shotwise.DangerSubtleBrush", "Shotwise.DangerTextBrush"),
            nameof(HotkeyState.RecordingOnly) => (CS.Get("Hotkey_State_RecordingOnly"), "Shotwise.Surface2Brush", "Shotwise.TextSecondaryBrush"),
            "standby" => (CS.Get("Hotkey_State_Standby"), "Shotwise.Surface2Brush", "Shotwise.TextSecondaryBrush"),
            _ => (CS.Get("Hotkey_State_Off"), "Shotwise.Surface2Brush", "Shotwise.TextSecondaryBrush"),
        };
        row.PillText.Text = text;
        row.Pill.SetResourceReference(Border.BackgroundProperty, bg);
        row.PillText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        row.Pill.Visibility = state is null ? Visibility.Hidden : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(row.Chip, text);

        row.Detail.Text = detail ?? "";
        row.Detail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (detail is not null) row.Detail.SetResourceReference(TextBlock.ForegroundProperty, detailBrush);
    }

    /// <summary>Pill state key + optional explanation line for a row (pending edits win over the last registration result).</summary>
    private (string? State, string? Detail, string DetailBrush) StateOf(Row row)
    {
        var value = row.Get();
        if (value != row.Original) return ("pending", null, "");
        if (value is null) return (nameof(HotkeyState.Off), null, "");
        if (_status is null) return (null, null, "");

        if (row.Id == FallbackRowId)
        {
            var global = _status.Find(HotkeyIds.GlobalCapture);
            return global?.State == HotkeyState.Fallback && global.Effective == value ? (nameof(HotkeyState.Active), null, "") : ("standby", null, "");
        }
        var s = _status.Find(row.Id);
        if (s is null) return (null, null, "");
        if (s.Configured != value) return ("pending", null, "");
        return s.State switch
        {
            HotkeyState.Fallback => (nameof(HotkeyState.Fallback), CS.Format("Hotkey_FallbackActive", value.Value.ToDisplayString(), s.Effective?.ToDisplayString() ?? ""), "Shotwise.WarningTextBrush"),
            HotkeyState.InUse => (nameof(HotkeyState.InUse), CS.Format("Hotkey_InUseDetail", value.Value.ToDisplayString()), "Shotwise.DangerTextBrush"),
            HotkeyState.Duplicate => (nameof(HotkeyState.Duplicate), CS.Format("Hotkey_DuplicateDetail", value.Value.ToDisplayString()), "Shotwise.DangerTextBrush"),
            HotkeyState.Unsupported => (nameof(HotkeyState.Unsupported), CS.Get("Hotkey_Unsupported"), "Shotwise.DangerTextBrush"),
            var st => (st.ToString(), null, ""),
        };
    }
}
