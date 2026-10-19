using System.Text.Json;
using Shotwise.Core.Capture;
using Shotwise.Core.Effects;
using Shotwise.Core.Serialization;

namespace Shotwise.UI.Capture.Views;

/// <summary>
/// Logic behind the Capture Window (plain class, no WPF): holds the working copy of the active preset, change tracking, the selection
/// types offered per mode, effect/output editing and preset-list operations. The window only renders it and forwards user input.
/// </summary>
public sealed class CaptureWindowModel
{
    private readonly IPresetRepository _repo;
    private string _baseline = "";

    public CaptureWindowModel(IPresetRepository repo)
    {
        _repo = repo;
        Working = repo.Active.Clone();
        _baseline = Snapshot(Working);
    }

    public IPresetRepository Repository => _repo;

    /// <summary>Unsaved working copy edited by the window controls; this is what the Capture button runs.</summary>
    public CapturePreset Working { get; private set; }

    public bool IsModified => Snapshot(Working) != _baseline;

    private static string Snapshot(CapturePreset p) => JsonSerializer.Serialize(p, ShotwiseJson.Compact);

    /// <summary>Selection types that make sense for a mode, in display order.</summary>
    public static IReadOnlyList<SelectionType> SelectionsFor(CaptureMode mode) => mode switch
    {
        CaptureMode.Video => new[] { SelectionType.Region, SelectionType.Window, SelectionType.Fullscreen, SelectionType.FixedRegion },
        CaptureMode.AllInOne => new[] { SelectionType.Region, SelectionType.Window, SelectionType.Fullscreen, SelectionType.FixedRegion },
        _ => new[]
        {
            SelectionType.Region, SelectionType.Window, SelectionType.Fullscreen, SelectionType.FixedRegion, SelectionType.Freehand,
            SelectionType.Object, SelectionType.MultipleArea, SelectionType.Menu, SelectionType.ScrollingWindow, SelectionType.Panoramic,
            SelectionType.GrabText, SelectionType.StepCapture, SelectionType.Clipboard, SelectionType.ListenToClipboard, SelectionType.File,
        },
    };

    /// <summary>Switches mode and keeps the selection type valid for it.</summary>
    public void SetMode(CaptureMode mode)
    {
        Working.Mode = mode;
        if (!SelectionsFor(mode).Contains(Working.Selection)) Working.Selection = SelectionType.Region;
    }

    public void SetSelection(SelectionType type) => Working.Selection = type;

    // ------------------------------------------------------------------ presets

    /// <summary>Makes <paramref name="preset"/> the active preset and loads a fresh working copy of it.</summary>
    public void Load(CapturePreset preset)
    {
        _repo.Active = preset;
        Working = preset.Clone();
        _baseline = Snapshot(Working);
    }

    /// <summary>Discards unsaved changes ("Reset current settings").</summary>
    public void ResetCurrent()
    {
        var saved = _repo.Find(Working.Id);
        if (saved is null) return;
        Working = saved.Clone();
        _baseline = Snapshot(Working);
    }

    /// <summary>Saves the working copy over its stored preset.</summary>
    public async Task SaveCurrentAsync()
    {
        await _repo.SaveAsync(Working);
        _baseline = Snapshot(Working);
    }

    /// <summary>Stores the working copy as a new preset and makes it active.</summary>
    public async Task<CapturePreset> SaveCurrentAsAsync(string name, string? groupId = null)
    {
        var copy = Working.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name.Trim();
        copy.GroupId = groupId ?? Working.GroupId;
        copy.IsBuiltIn = false;
        copy.Hotkey = null;
        copy.Order = _repo.All.Count == 0 ? 0 : _repo.All.Max(p => p.Order) + 1;
        await _repo.SaveAsync(copy);
        Load(_repo.Find(copy.Id) ?? copy);
        return copy;
    }

    public async Task DuplicateAsync(CapturePreset source)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = source.Name + " (2)";
        copy.IsBuiltIn = false;
        copy.Hotkey = null;
        copy.Order = _repo.All.Count == 0 ? 0 : _repo.All.Max(p => p.Order) + 1;
        await _repo.SaveAsync(copy);
    }

    public async Task RenameAsync(CapturePreset preset, string name)
    {
        var copy = (_repo.Find(preset.Id) ?? preset).Clone();
        copy.Name = name.Trim();
        await _repo.SaveAsync(copy);
        if (Working.Id == copy.Id) Working.Name = copy.Name;
    }

    public async Task DeleteAsync(CapturePreset preset)
    {
        await _repo.DeleteAsync(preset.Id);
        if (Working.Id == preset.Id && _repo.All.Count > 0) Load(_repo.Active);
    }

    public async Task MoveToGroupAsync(CapturePreset preset, string? groupId)
    {
        var copy = (_repo.Find(preset.Id) ?? preset).Clone();
        copy.GroupId = groupId;
        await _repo.SaveAsync(copy);
    }

    /// <summary>Sets/clears a per-preset hotkey; returns an error key when the combination is already used.</summary>
    public async Task<string?> SetHotkeyAsync(CapturePreset preset, Shotwise.Core.Platform.Hotkey? key, Shotwise.Core.Settings.HotkeySettings globals)
    {
        if (key is { } k)
        {
            var clash = FindHotkeyClash(preset.Id, k, globals);
            if (clash is not null) return clash;
        }
        var copy = (_repo.Find(preset.Id) ?? preset).Clone();
        copy.Hotkey = key;
        await _repo.SaveAsync(copy);
        if (Working.Id == copy.Id) Working.Hotkey = key;
        return null;
    }

    /// <summary>Returns the display name of whatever already uses <paramref name="key"/>, or null.</summary>
    public string? FindHotkeyClash(string presetId, Shotwise.Core.Platform.Hotkey key, Shotwise.Core.Settings.HotkeySettings globals)
    {
        var other = _repo.All.FirstOrDefault(p => p.Id != presetId && p.Hotkey is { } h && h == key);
        if (other is not null) return other.Name;
        foreach (var (name, h) in GlobalHotkeys(globals))
            if (h is { } g && g == key) return name;
        return null;
    }

    private static IEnumerable<(string, Shotwise.Core.Platform.Hotkey?)> GlobalHotkeys(Shotwise.Core.Settings.HotkeySettings g)
    {
        yield return (CS.Get("Hotkey_Capture"), g.GlobalCapture);
        yield return (CS.Get("Hotkey_CaptureFallback"), g.GlobalCaptureFallback);
        yield return (CS.Get("Hotkey_Repeat"), g.RepeatLastCapture);
        yield return (CS.Get("Hotkey_ShowHide"), g.ShowHideApp);
        yield return (CS.Get("Hotkey_VideoPause"), g.VideoStartPauseResume);
        yield return (CS.Get("Hotkey_VideoStop"), g.VideoStop);
        yield return (CS.Get("Hotkey_Draw"), g.ScreenDrawToggle);
        yield return (CS.Get("Hotkey_Webcam"), g.WebcamFullscreenToggle);
    }

    /// <summary>New order after dragging <paramref name="movedId"/> before <paramref name="targetId"/> (or to the end when null).</summary>
    public static IReadOnlyList<string> ReorderIds(IReadOnlyList<string> currentOrder, string movedId, string? targetId)
    {
        var list = currentOrder.Where(i => i != movedId).ToList();
        int idx = targetId is null ? list.Count : list.IndexOf(targetId);
        if (idx < 0) idx = list.Count;
        list.Insert(idx, movedId);
        return list;
    }

    // ------------------------------------------------------------------ effects & outputs

    /// <summary>The effect types offered by "Add effect" with their default parameters.</summary>
    public static IReadOnlyList<(string Key, Func<EffectSpec> Create)> EffectChoices { get; } = new (string, Func<EffectSpec>)[]
    {
        ("Effect_Border", () => EffectDefaults.Border), ("Effect_Edge", () => EffectDefaults.Edge), ("Effect_Shadow", () => EffectDefaults.Shadow),
        ("Effect_Perspective", () => EffectDefaults.Perspective), ("Effect_PageCurl", () => EffectDefaults.PageCurl),
        ("Effect_ColorAdjust", () => EffectDefaults.ColorAdjust), ("Effect_ColorReplace", () => EffectDefaults.ColorReplace),
        ("Effect_Filter", () => EffectDefaults.Filter), ("Effect_Watermark", () => EffectDefaults.Watermark), ("Effect_Caption", () => EffectDefaults.Caption),
        ("Effect_Resize", () => EffectDefaults.Resize), ("Effect_Rotate", () => EffectDefaults.Rotate), ("Effect_Trim", () => EffectDefaults.Trim),
        ("Effect_Transparency", () => EffectDefaults.Transparency),
    };

    public static string EffectName(EffectSpec e) => e switch
    {
        BorderEffect => CS.Get("Effect_Border"),
        EdgeEffect => CS.Get("Effect_Edge"),
        ShadowEffect => CS.Get("Effect_Shadow"),
        PerspectiveEffect => CS.Get("Effect_Perspective"),
        PageCurlEffect => CS.Get("Effect_PageCurl"),
        ColorAdjustEffect => CS.Get("Effect_ColorAdjust"),
        ColorReplaceEffect => CS.Get("Effect_ColorReplace"),
        FilterEffect => CS.Get("Effect_Filter"),
        WatermarkEffect => CS.Get("Effect_Watermark"),
        CaptionEffect => CS.Get("Effect_Caption"),
        ResizeEffect => CS.Get("Effect_Resize"),
        RotateEffect => CS.Get("Effect_Rotate"),
        TrimEffect => CS.Get("Effect_Trim"),
        SpotlightMagnifyEffect => CS.Get("Effect_Spotlight"),
        TransparencyEffect => CS.Get("Effect_Transparency"),
        UnknownEffect u => u.TypeName,
        _ => e.GetType().Name,
    };

    public void AddEffect(EffectSpec effect) => Working.Effects.Add(effect);

    public void RemoveEffect(int index)
    {
        if (index >= 0 && index < Working.Effects.Count) Working.Effects.RemoveAt(index);
    }

    public void ReplaceEffect(int index, EffectSpec effect)
    {
        if (index >= 0 && index < Working.Effects.Count) Working.Effects[index] = effect;
    }

    public void MoveEffect(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= Working.Effects.Count || to < 0 || to >= Working.Effects.Count) return;
        var e = Working.Effects[index];
        Working.Effects.RemoveAt(index);
        Working.Effects.Insert(to, e);
    }

    public bool HasOutput(string destinationId) => Working.Outputs.Any(o => string.Equals(o.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase));

    public void SetOutput(string destinationId, bool on)
    {
        Working.Outputs.RemoveAll(o => string.Equals(o.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase));
        if (on) Working.Outputs.Add(new OutputBinding(destinationId));
    }

    // ------------------------------------------------------------------ timing helpers

    /// <summary>Interval in seconds from a value + unit ("s", "min", "h").</summary>
    public static int IntervalSeconds(double value, int unitIndex) => (int)Math.Clamp(Math.Round(value * unitIndex switch { 1 => 60, 2 => 3600, _ => 1 }), 1, 7 * 24 * 3600);

    public static (double Value, int Unit) SplitInterval(int seconds)
        => seconds % 3600 == 0 ? (seconds / 3600.0, 2) : seconds % 60 == 0 ? (seconds / 60.0, 1) : (seconds, 0);
}
