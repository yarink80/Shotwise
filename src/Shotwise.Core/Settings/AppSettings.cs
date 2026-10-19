using System.Text.Json;
using System.Text.Json.Serialization;
using Shotwise.Core.Imaging;
using Shotwise.Core.Platform;

namespace Shotwise.Core.Settings;

public enum AppTheme { System, Light, Dark }
public enum TooltipDetail { TextOnly, TextAndAnimation }
public enum HelpNotificationMode { All, HelpOnly, None }
public enum UsageDataMode { Off, Anonymous, Full }
public enum WorkspaceColor { White, LightGray, Gray, DarkGray, Black, Custom }
public enum ThumbnailSize { Small, Medium, Large }
public enum OcrEngineKind { Windows, Tesseract }
public enum SaveFolderMode { LastUsed, Fixed, Ask }
public enum SaveNameMode { Ask, Automatic, Fixed }

/// <summary>
/// Root of all user preferences, persisted as %APPDATA%\Shotwise\settings.json.
/// Every option on the Preferences pages maps to a property here with its default.
/// Unknown JSON members are preserved in <see cref="Extra"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Settings layout version. 2 (0.9.1): daily update checks become the default (see <see cref="Normalize"/>).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 2;
    public GeneralSettings General { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public LibrarySettings Library { get; set; } = new();
    public ShareSettings Share { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public ExperimentsSettings Experiments { get; set; } = new();
    public PrinterCaptureSettings Printer { get; set; } = new();
    public MobileSettings Mobile { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Names of the top-level sections (used for change notifications).</summary>
    public static IReadOnlyList<string> SectionNames { get; } = new[]
    {
        nameof(General), nameof(Capture), nameof(Hotkeys), nameof(Editor), nameof(Library), nameof(Share),
        nameof(Advanced), nameof(Experiments), nameof(Printer), nameof(Mobile), nameof(Updates), nameof(Privacy),
    };

    internal object GetSection(string name) => name switch
    {
        nameof(General) => General,
        nameof(Capture) => Capture,
        nameof(Hotkeys) => Hotkeys,
        nameof(Editor) => Editor,
        nameof(Library) => Library,
        nameof(Share) => Share,
        nameof(Advanced) => Advanced,
        nameof(Experiments) => Experiments,
        nameof(Printer) => Printer,
        nameof(Mobile) => Mobile,
        nameof(Updates) => Updates,
        nameof(Privacy) => Privacy,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Repairs nulls / out-of-range values after loading a possibly hand-edited file.</summary>
    public void Normalize()
    {
        General ??= new(); Capture ??= new(); Hotkeys ??= new(); Editor ??= new(); Library ??= new(); Share ??= new();
        Advanced ??= new(); Experiments ??= new(); Printer ??= new(); Mobile ??= new(); Updates ??= new(); Privacy ??= new();
        Library.MaxTrayItems = Math.Clamp(Library.MaxTrayItems, 1, LibrarySettings.MaxTrayItemsLimit);
        // Schema 1 → 2: 0.9.0 stored the old weekly default; a value the user never changed becomes the new daily default.
        if (SchemaVersion < 2 && Updates.DaysBetweenChecks == UpdateSettings.LegacyDefaultDays) Updates.DaysBetweenChecks = 1;
        if (SchemaVersion < CurrentSchemaVersion) SchemaVersion = CurrentSchemaVersion;
        Updates.DaysBetweenChecks = Math.Clamp(Updates.DaysBetweenChecks, 1, 365);
        Mobile.Port = Mobile.Port is > 0 and <= 65535 ? Mobile.Port : MobileSettings.DefaultPort;
        if (!Media.RecordingOptions.SupportedFps.Contains(Capture.VideoFps)) Capture.VideoFps = 30;
        Capture.VideoQuality = Math.Clamp(Capture.VideoQuality, 1, 100);
        Share.Naming ??= new();
        Share.SaveAs ??= new();
        Share.ClientIds ??= new();
        Share.LoopbackPorts ??= new();
        foreach (var k in Share.LoopbackPorts.Where(p => p.Value is <= 0 or > 65535).Select(p => p.Key).ToList()) Share.LoopbackPorts.Remove(k);
        Share.DisabledDestinations ??= new();
        Editor.MyColors ??= new();
        Editor.ToolbarTools ??= new();
        Library.PinnedItems ??= new();
        Experiments.EnabledExperiments ??= new();
    }
}

/// <summary>Capture Preferences → General + Editor Preferences → General.</summary>
public sealed class GeneralSettings
{
    public bool RunAtWindowsStartup { get; set; }
    /// <summary>Open the Capture Window when the app starts.</summary>
    public bool ShowCaptureWindowOnStartup { get; set; } = true;
    public bool ShowCaptureWidget { get; set; }
    /// <summary>UI theme; Light by default (DESIGN-SYSTEM §2) - Dark and System stay selectable in Preferences.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Light;
    /// <summary>UI language (culture name); empty = follow Windows.</summary>
    public string Language { get; set; } = "";
    public bool ShowOnboarding { get; set; } = true;
    public TooltipDetail Tooltips { get; set; } = TooltipDetail.TextOnly;
    public HelpNotificationMode HelpNotifications { get; set; } = HelpNotificationMode.HelpOnly;
    /// <summary>Show toast when a capture is saved / a share completes.</summary>
    public bool ShowToasts { get; set; } = true;
    /// <summary>Minimise to tray instead of exiting when the last window closes.</summary>
    public bool StayInTray { get; set; } = true;
}

/// <summary>Capture Preferences → Capture.</summary>
public sealed class CaptureSettings
{
    public bool HideAppDuringCapture { get; set; } = true;
    /// <summary>Open screenshots copied from system tools (Win+Shift+S, PrintScreen) in the editor.</summary>
    public bool OpenScreenshotsFromSystemTools { get; set; }
    public bool ShowMagnifier { get; set; } = true;
    public bool SimplifiedCrosshairs { get; set; }
    public bool RequireStartButtonForScrolling { get; set; }
    public bool AlertWhenMicrophoneMuted { get; set; } = true;
    public bool SupportHdr { get; set; }
    public bool AutoStartVideoRecording { get; set; }
    public bool ShowVideoCountdown { get; set; } = true;
    public bool ShowReadyToRecordWindow { get; set; } = true;
    public bool MinimizeControlsWhenFullscreenOneMonitor { get; set; } = true;
    public bool ScaleTo1080p { get; set; } = true;
    public bool EnableCamera { get; set; } = true;
    public bool DisableHardwareAcceleratedRecording { get; set; }
    /// <summary>Video frame rate: 5, 10, 15, 30 or 60.</summary>
    public int VideoFps { get; set; } = 30;
    /// <summary>Video quality 1..100.</summary>
    public int VideoQuality { get; set; } = 70;
    /// <summary>Warn when free disk space for recordings drops below this many MB.</summary>
    public int LowDiskWarningMb { get; set; } = 2048;
    /// <summary>Stop recording when free disk space drops below this many MB.</summary>
    public int LowDiskStopMb { get; set; } = 500;
    /// <summary>ScreenDraw: fade drawings after N seconds (0 = never).</summary>
    public int ScreenDrawFadeSeconds { get; set; } = 3;
    public Argb ScreenDrawColor { get; set; } = Argb.Red;
    /// <summary>Number of countdown seconds shown before recording/timed capture.</summary>
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>Show the selection keyboard help hint (F1) on the overlay.</summary>
    public bool ShowSelectionHelpHint { get; set; } = true;
    /// <summary>Snap region edges to common resolutions (guides).</summary>
    public bool ShowSelectionGuides { get; set; } = true;
    /// <summary>Folder for temporary recording files; empty = %TEMP%\Shotwise.</summary>
    public string RecordingTempFolder { get; set; } = "";
}

/// <summary>Capture Preferences → Hotkeys (per-preset hotkeys live on presets).</summary>
public sealed class HotkeySettings
{
    public Hotkey? GlobalCapture { get; set; } = new Hotkey(VirtualKey.PrintScreen, ModifierKeys.None);
    public Hotkey? ShowHideApp { get; set; }
    public Hotkey? RepeatLastCapture { get; set; } = new Hotkey(VirtualKey.F11, ModifierKeys.Control | ModifierKeys.Shift);
    public Hotkey? VideoStartPauseResume { get; set; } = new Hotkey(VirtualKey.F9, ModifierKeys.Control | ModifierKeys.Shift);
    public Hotkey? VideoStop { get; set; } = new Hotkey(VirtualKey.F10, ModifierKeys.Control | ModifierKeys.Shift);
    /// <summary>Ctrl+Shift+F8 (F12 combinations are reserved by Windows for the debugger and cannot be registered).</summary>
    public Hotkey? ScreenDrawToggle { get; set; } = new Hotkey(VirtualKey.F8, ModifierKeys.Control | ModifierKeys.Shift);
    public Hotkey? WebcamFullscreenToggle { get; set; } = new Hotkey(VirtualKey.F, ModifierKeys.Control | ModifierKeys.Shift);
    /// <summary>
    /// Used for the global capture when <see cref="GlobalCapture"/> is held by Windows or another app (POLISH-1): Shift+PrintScreen.
    /// The preferred key is retried on every start; null = no fallback.
    /// </summary>
    public Hotkey? GlobalCaptureFallback { get; set; } = new Hotkey(VirtualKey.PrintScreen, ModifierKeys.Shift);
    /// <summary>Runtime state: the key the global capture was last registered with (preferred or fallback); null = not registered.</summary>
    public Hotkey? EffectiveGlobalCapture { get; set; }
    /// <summary>Runtime state: the one-time "PrintScreen is used by another app, using the fallback" notice was shown.</summary>
    public bool FallbackNoticeShown { get; set; }
    public bool AllowOverrideOtherAssignments { get; set; }
    /// <summary>User has been asked about Windows' PrintScreen → screen snipping setting.</summary>
    public bool PrintScreenConflictPrompted { get; set; }

    public static HotkeySettings Defaults() => new();
}

/// <summary>Editor Preferences → Editor + Advanced (editing behaviour).</summary>
public sealed class EditorSettings
{
    public WorkspaceColor WorkspaceColor { get; set; } = WorkspaceColor.LightGray;
    public Argb CustomWorkspaceColor { get; set; } = new(0xFFE0E0E0);
    public ThumbnailSize RecentThumbnailSize { get; set; } = ThumbnailSize.Medium;
    public bool ShowCursorQuickStylePopup { get; set; } = true;
    public bool ShowToolbarLabels { get; set; } = true;
    public bool AutoExpandCanvas { get; set; } = true;
    public bool EscapeClosesEditor { get; set; }
    public bool EnableSpellCheck { get; set; } = true;
    /// <summary>Spell check language (culture); empty = UI language.</summary>
    public string SpellCheckLanguage { get; set; } = "";
    public bool AutoFlattenPastedImages { get; set; }
    /// <summary>Format used when dragging items out of the editor (extension without dot).</summary>
    public string DragAndDropFileType { get; set; } = "png";
    public bool KeepOriginalImageDimensions { get; set; } = true;
    public bool DeleteCroppedContent { get; set; }
    public bool KeepEditorRunningInBackground { get; set; } = true;
    public bool CanvasSnapping { get; set; } = true;
    public bool ShowPixelGrid { get; set; }
    public bool ZoomToFitOnOpen { get; set; } = true;
    /// <summary>Clipboard: also place HTML (img tag) on the clipboard.</summary>
    public bool AddHtmlToClipboard { get; set; }
    /// <summary>Clipboard: composite transparent pixels over white.</summary>
    public bool PreventClipboardTransparency { get; set; }
    /// <summary>Email: embed images inline instead of attaching.</summary>
    public bool EmbedEmailImages { get; set; } = true;
    public bool PreviewRecentCaptures { get; set; } = true;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Windows;
    /// <summary>OCR new captures in the background (search + text tools).</summary>
    public bool BackgroundOcr { get; set; } = true;
    public List<Argb> MyColors { get; set; } = new();
    /// <summary>Ordered list of tool kinds shown on the toolbar (empty = default set).</summary>
    public List<string> ToolbarTools { get; set; } = new();
    public string? ActiveThemeId { get; set; }
    public string? LastOpenFolder { get; set; }
}

/// <summary>Editor Preferences → Library.</summary>
public sealed class LibrarySettings
{
    public const int MaxTrayItemsLimit = 200;
    public bool AutosaveImages { get; set; } = true;
    public bool AutosaveVideos { get; set; } = true;
    /// <summary>Library root folder; empty = default (Documents\Shotwise\Library).</summary>
    public string LibraryRoot { get; set; } = "";
    public bool ShowImportPastCapturesPrompt { get; set; } = true;
    /// <summary>Recent captures shown in the tray (1..200).</summary>
    public int MaxTrayItems { get; set; } = 50;
    public bool PromptBeforeDelete { get; set; } = true;
    public bool RemoveUnpinnedFromTrayOnClose { get; set; }
    public List<string> PinnedItems { get; set; } = new();
}

/// <summary>Automatic file naming (SHR-01).</summary>
public sealed class NamingSettings
{
    /// <summary>Token pattern: {date} {time} {app} {window} {counter} {preset} {width} {height}.</summary>
    public string Pattern { get; set; } = "{date}_{time}";
    public string Prefix { get; set; } = "Shotwise_";
    public int CounterDigits { get; set; } = 3;
    public int CounterStart { get; set; } = 1;
    public int NextCounter { get; set; } = 1;
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public string TimeFormat { get; set; } = "HH-mm-ss";
    public bool PromptBeforeOverwrite { get; set; } = true;
}

public sealed class SaveAsSettings
{
    public SaveFolderMode FolderMode { get; set; } = SaveFolderMode.LastUsed;
    public string FixedFolder { get; set; } = "";
    public string LastFolder { get; set; } = "";
    public SaveNameMode NameMode { get; set; } = SaveNameMode.Ask;
    public bool RememberLastFormat { get; set; } = true;
    public string DefaultFormat { get; set; } = "png";
    public int JpegQuality { get; set; } = 90;
}

/// <summary>Editor Preferences → Share + output defaults. Secrets are never stored here (Credential Manager).</summary>
public sealed class ShareSettings
{
    public bool DoNotIncludeCaptureSourceInNames { get; set; }
    public List<string> DisabledDestinations { get; set; } = new();
    public bool CopyLinkToClipboard { get; set; } = true;
    public NamingSettings Naming { get; set; } = new();
    public SaveAsSettings SaveAs { get; set; } = new();
    /// <summary>User-supplied OAuth client ids per provider ("microsoft", "google", "dropbox", "box", "slack", "imgur").</summary>
    public Dictionary<string, string> ClientIds { get; set; } = new();
    /// <summary>Fixed OAuth loopback redirect ports per provider (Dropbox, Box, Imgur, X need a pre-registered redirect URI); absent = random port.</summary>
    public Dictionary<string, int> LoopbackPorts { get; set; } = new();
}

/// <summary>Editor Preferences → Advanced (non-editing items).</summary>
public sealed class AdvancedSettings
{
    public string LogLevel { get; set; } = "Information";
    public int MaxLogFiles { get; set; } = 10;
    public bool CleanTempOnStartup { get; set; } = true;
    public bool HardwareAcceleratedRendering { get; set; } = true;
    /// <summary>Create local crash dumps on unhandled exceptions.</summary>
    public bool WriteCrashDumps { get; set; } = true;
    /// <summary>Keep a Shotwise shortcut in the user's Start menu (created/refreshed at startup; removed when turned off).</summary>
    public bool ShowInStartMenu { get; set; } = true;
}

/// <summary>Editor Preferences → Experiments.</summary>
public sealed class ExperimentsSettings
{
    public bool AgreedToTerms { get; set; }
    public bool Enabled { get; set; }
    public List<string> EnabledExperiments { get; set; } = new();
}

/// <summary>Printer capture settings (CAP-20).</summary>
public sealed class PrinterCaptureSettings
{
    public const string DefaultPrinterName = "Shotwise Printer";
    /// <summary>Folder watched for printed pages; empty = %TEMP%\Shotwise\Printer.</summary>
    public string TempDirectory { get; set; } = "";
    public bool CloseAfterCaptureIfNotRunning { get; set; } = true;
    public string PrinterName { get; set; } = DefaultPrinterName;
}

/// <summary>Mobile Connect (PLT-05).</summary>
public sealed class MobileSettings
{
    public const int DefaultPort = 8303;
    public bool AllowSharing { get; set; }
    public int Port { get; set; } = DefaultPort;
    /// <summary>Name shown on paired devices; empty = computer name.</summary>
    public string ConnectionName { get; set; } = "";
    public int PairingValidityMinutes { get; set; } = 10;
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>Release channel: Stable = GitHub releases only; Beta = also GitHub pre-releases (e.g. 0.9.2-beta.1).</summary>
public enum UpdateChannel { Stable, Beta }

/// <summary>
/// Updater (PLT-04, Preferences → Startup &amp; Updates). Installed copies check GitHub Releases on startup and every
/// <see cref="DaysBetweenChecks"/> days, download in the background (never on a metered connection) and ask before restarting.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>Old default of <see cref="DaysBetweenChecks"/> (0.9.0 and earlier); migrated to the daily default once.</summary>
    internal const int LegacyDefaultDays = 7;

    public bool AutomaticallyCheck { get; set; } = true;
    /// <summary>Ask (toast) before a scheduled check contacts the feed.</summary>
    public bool PromptBeforeCheck { get; set; }
    public int DaysBetweenChecks { get; set; } = 1;
    /// <summary>Download a found update in the background (skipped on metered connections).</summary>
    public bool DownloadAutomatically { get; set; } = true;
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    /// <summary>Time of the last check that reached the feed (successful or "up to date").</summary>
    public DateTimeOffset? LastCheck { get; set; }
}

/// <summary>Data &amp; privacy (PLT-07): telemetry off by default, no network endpoint shipped.</summary>
public sealed class PrivacySettings
{
    public UsageDataMode UsageData { get; set; } = UsageDataMode.Off;
    /// <summary>Strip capture source (app/window/url) from shared files' metadata.</summary>
    public bool StripMetadataOnShare { get; set; }
}

public sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(AppSettings settings, IReadOnlySet<string> changedSections)
    {
        Settings = settings;
        ChangedSections = changedSections;
    }

    public AppSettings Settings { get; }
    /// <summary>Top-level section names whose content changed (e.g. "Hotkeys", "General").</summary>
    public IReadOnlySet<string> ChangedSections { get; }
    public bool Affects(string section) => ChangedSections.Contains(section);
}

/// <summary>Settings persistence. Read <see cref="Current"/>; mutate only via <see cref="Update"/>.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    Task SaveAsync();
    /// <summary>Applies <paramref name="mutate"/>, persists (atomic write) and raises <see cref="Changed"/> for changed sections.</summary>
    void Update(Action<AppSettings> mutate);
    event EventHandler<SettingsChangedEventArgs>? Changed;
    Task ResetAsync();
}
