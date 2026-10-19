using System.Text.Json;
using Shotwise.Core.Effects;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Capture;

public enum AspectLock { None, Square, FourThree, SixteenNine }

/// <summary>Selection-type specific options (immutable; use <c>with</c>).</summary>
public sealed record SelectionOptions
{
    // Fixed region
    public int FixedWidth { get; init; } = 640;
    public int FixedHeight { get; init; } = 480;
    public bool UseFixedPosition { get; init; }
    public int? FixedX { get; init; }
    public int? FixedY { get; init; }
    public AspectLock AspectLock { get; init; } = AspectLock.None;

    // Window
    /// <summary>Capture the active window immediately (no hover selection).</summary>
    public bool WindowCaptureActive { get; init; }
    public bool WindowClientOnly { get; init; }

    // Fullscreen
    public bool FullscreenAllMonitors { get; init; }
    /// <summary>Monitor index for fullscreen; null = monitor under the cursor.</summary>
    public int? MonitorIndex { get; init; }

    // Menu
    public bool MenuIncludeMenuBar { get; init; }
    public bool MenuCascaded { get; init; } = true;

    // Scrolling
    public ScrollDirection ScrollDirection { get; init; } = ScrollDirection.Vertical;
    public bool ScrollStartAtTopLeft { get; init; } = true;
    public int ScrollStepDelayMs { get; init; } = 250;

    // Step capture
    public StepCaptureOptions Steps { get; init; } = new();

    public static SelectionOptions Default { get; } = new();
}

/// <summary>Delay / interval / schedule timing (null = off).</summary>
public sealed record TimingOptions
{
    public int? DelaySeconds { get; init; }
    public int? IntervalSeconds { get; init; }
    public DateTimeOffset? ScheduleAt { get; init; }
    public bool ShowCountdown { get; init; } = true;

    public static TimingOptions None { get; } = new();
    public bool IsTimed => DelaySeconds is > 0 || IntervalSeconds is > 0 || ScheduleAt is not null;
}

/// <summary>An after-capture output: share destination id + destination-specific options JSON.</summary>
public sealed record OutputBinding(string DestinationId, JsonElement? Options = null);

public sealed record PresetGroup(string Id, string Name, int Order = 0, bool IsExpanded = true);

/// <summary>Well-known share destination ids (WP-SHARING registers destinations with these ids).</summary>
public static class KnownDestinations
{
    public const string File = "file";
    public const string Clipboard = "clipboard";
    public const string Print = "print";
    public const string Email = "email";
    public const string Ftp = "ftp";
    public const string Program = "program";
    public const string Word = "word";
    public const string Excel = "excel";
    public const string PowerPoint = "powerpoint";
    public const string OneNote = "onenote";
    public const string OneDrive = "onedrive";
    public const string SharePoint = "sharepoint";
    public const string Teams = "teams";
    public const string GoogleDrive = "googledrive";
    public const string YouTube = "youtube";
    public const string Dropbox = "dropbox";
    public const string Box = "box";
    public const string Slack = "slack";
    public const string Imgur = "imgur";
    public const string S3 = "s3";
    public const string CustomUploader = "custom-uploader";
    public const string Evernote = "evernote";
    public const string X = "x";
}

/// <summary>A named capture configuration (mode, selection, timing, effects, outputs, recording, hotkey).</summary>
public sealed class CapturePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? GroupId { get; set; }
    public int Order { get; set; }
    public CaptureMode Mode { get; set; } = CaptureMode.AllInOne;
    public SelectionType Selection { get; set; } = SelectionType.Region;
    public SelectionOptions SelectionOptions { get; set; } = SelectionOptions.Default;
    public TimingOptions Timing { get; set; } = TimingOptions.None;
    public bool IncludeCursor { get; set; }
    public bool PreviewInEditor { get; set; } = true;
    public bool CopyToClipboard { get; set; }
    /// <summary>Program destination id/config to open the capture in ("Open in"), or null.</summary>
    public string? OpenInProgramId { get; set; }
    public bool RequiresReview { get; set; }
    public List<EffectSpec> Effects { get; set; } = new();
    public List<OutputBinding> Outputs { get; set; } = new();
    public RecordingOptions Recording { get; set; } = PresetDefaults.Recording;
    public Hotkey? Hotkey { get; set; }
    /// <summary>
    /// The user removed this preset's hotkey on purpose (set by <see cref="PresetRepository.SaveAsync"/> when a saved hotkey becomes null),
    /// so default-hotkey migrations leave it alone.
    /// </summary>
    public bool HotkeyCleared { get; set; }
    public bool IsBuiltIn { get; set; }

    /// <summary>Deep copy (via JSON) — same id.</summary>
    public CapturePreset Clone()
        => JsonSerializer.Deserialize<CapturePreset>(JsonSerializer.Serialize(this, ShotwiseJson.Compact), ShotwiseJson.Compact)!;

    public override string ToString() => $"{Name} ({Mode}/{Selection})";
}

public static class PresetDefaults
{
    /// <summary>Our default recording options (cursor highlight yellow, click ripple blue).</summary>
    public static RecordingOptions Recording { get; } = new RecordingOptions() with
    {
        CursorHighlightColor = new Imaging.Argb(0x66FFEB3B),
        ClickColor = new Imaging.Argb(0xCC1E88E5),
    };

    public const string AllInOneId = "builtin.all-in-one";
    public const string ImageRegionId = "builtin.image-region";
    public const string VideoId = "builtin.video";
    public const string GrabTextId = "builtin.grab-text";
    public const string PanoramicId = "builtin.panoramic";
    public const string StepCaptureId = "builtin.step-capture";
    public const string FullscreenId = "builtin.fullscreen";
    public const string WindowId = "builtin.window";

    /// <summary>
    /// Version of the built-in preset hotkey defaults. Preset files written before a version get the defaults applied once
    /// (only to built-in presets without a hotkey that the user did not clear, and only when the key is still free).
    /// </summary>
    public const int HotkeyDefaultsVersion = 1;

    /// <summary>
    /// Default global hotkeys of the built-in presets (POLISH-1). The All-in-One preset runs from the global capture hotkey
    /// (Settings → Hotkeys: PrintScreen, falling back to Shift+PrintScreen), so it has no preset hotkey of its own.
    /// </summary>
    public static Hotkey? DefaultHotkey(string presetId) => presetId switch
    {
        ImageRegionId => new Hotkey(VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Shift),
        VideoId => new Hotkey(VirtualKey.PrintScreen, ModifierKeys.Alt | ModifierKeys.Shift),
        GrabTextId => new Hotkey(VirtualKey.PrintScreen, ModifierKeys.Control | ModifierKeys.Alt),
        _ => null,
    };

    /// <summary>The built-in presets shipped by default (SPEC CAP-15), in display order.</summary>
    public static IReadOnlyList<CapturePreset> CreateBuiltIns() => new List<CapturePreset>
    {
        new() { Id = AllInOneId, Name = "All-in-One", Mode = CaptureMode.AllInOne, Selection = SelectionType.Region, IsBuiltIn = true, Order = 0 },
        new() { Id = ImageRegionId, Name = "Image Region", Mode = CaptureMode.Image, Selection = SelectionType.Region, IsBuiltIn = true, Order = 1, Hotkey = DefaultHotkey(ImageRegionId) },
        new() { Id = VideoId, Name = "Video", Mode = CaptureMode.Video, Selection = SelectionType.Region, IsBuiltIn = true, Order = 2, Hotkey = DefaultHotkey(VideoId) },
        new() { Id = GrabTextId, Name = "Grab Text", Mode = CaptureMode.Image, Selection = SelectionType.GrabText, PreviewInEditor = false, IsBuiltIn = true, Order = 3, Hotkey = DefaultHotkey(GrabTextId) },
        new() { Id = PanoramicId, Name = "Panoramic", Mode = CaptureMode.Image, Selection = SelectionType.Panoramic, IsBuiltIn = true, Order = 4 },
        new() { Id = StepCaptureId, Name = "Step Capture", Mode = CaptureMode.Image, Selection = SelectionType.StepCapture, IsBuiltIn = true, Order = 5 },
        new() { Id = FullscreenId, Name = "Fullscreen", Mode = CaptureMode.Image, Selection = SelectionType.Fullscreen, IsBuiltIn = true, Order = 6 },
        new() { Id = WindowId, Name = "Window", Mode = CaptureMode.Image, Selection = SelectionType.Window, IsBuiltIn = true, Order = 7 },
    };
}

public interface IPresetRepository
{
    IReadOnlyList<CapturePreset> All { get; }
    IReadOnlyList<PresetGroup> Groups { get; }
    /// <summary>Preset used by the global capture hotkey / Capture button. Setting it persists the choice.</summary>
    CapturePreset Active { get; set; }
    /// <summary>Last preset actually run (may be an unsaved ad-hoc preset); used by "Repeat last capture".</summary>
    CapturePreset? LastUsed { get; }
    void MarkUsed(CapturePreset preset);
    CapturePreset? Find(string id);
    /// <summary>Inserts or replaces (by id).</summary>
    Task SaveAsync(CapturePreset preset);
    Task DeleteAsync(string id);
    /// <summary>Sets the display order to the given id sequence.</summary>
    Task ReorderAsync(IReadOnlyList<string> ids);
    Task SaveGroupAsync(PresetGroup group);
    /// <summary>Deletes a group; its presets move to "no group" (or are deleted when <paramref name="deletePresets"/>).</summary>
    Task DeleteGroupAsync(string groupId, bool deletePresets = false);
    /// <summary>Imports a <c>.shotpresets</c> file (presets get new ids on conflict; duplicate hotkeys are cleared).</summary>
    Task ImportAsync(string path);
    Task ExportAsync(IEnumerable<string> ids, string path);
    /// <summary>Restores built-in presets to their shipped definitions (re-adding deleted ones). User presets are kept.</summary>
    Task ResetDefaultsAsync();
    event EventHandler Changed;
}
