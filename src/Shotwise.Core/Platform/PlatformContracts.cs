using Shotwise.Core.Settings;

namespace Shotwise.Core.Platform;

public enum HotkeyRegistrationResult { Ok, Duplicate, Unsupported, InUseBySystemOrOtherApp }

public interface IHotkeyService
{
    HotkeyRegistrationResult Register(string id, Hotkey key, Action callback, bool allowOverride);
    void Unregister(string id);
    void UnregisterAll();
    IReadOnlyDictionary<string, Hotkey> Registered { get; }
    /// <summary>True when Windows' "Use the Print screen key to open screen capture" setting is on.</summary>
    bool IsPrintScreenTakenBySnipping();
}

public interface ISingleInstance
{
    bool IsFirstInstance { get; }
    event EventHandler<string[]>? ArgumentsReceived;
    Task<bool> ForwardToFirstInstanceAsync(string[] args);
}

public interface IAutostartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public interface IShellRegistration
{
    void RegisterFileAssociations();
    void RegisterUriScheme();
    bool IsShellExtensionRegistered { get; }
    /// <summary>Removes every per-user (HKCU) key that <see cref="RegisterFileAssociations"/> and <see cref="RegisterUriScheme"/> wrote.</summary>
    void UnregisterAll();
}

public enum NotificationKind { Info, Success, Warning, Error }

/// <summary>Toast button. <see cref="Invoke"/> runs in-process when the user clicks it.</summary>
public sealed record NotificationAction(string Label, Action Invoke, string? Argument = null);

public interface INotificationService
{
    void Show(string title, string message, NotificationKind kind = NotificationKind.Info, IReadOnlyList<NotificationAction>? actions = null);
}

/// <summary>A newer Shotwise release offered by the update feed (GitHub Releases, see docs/UPDATES.md).</summary>
/// <param name="Version">SemVer 2 text, e.g. "0.9.1" or "0.9.2-beta.1".</param>
/// <param name="ReleaseNotes">Markdown release notes from the package, or null.</param>
/// <param name="SizeBytes">Download size (delta packages when available, otherwise the full package); 0 = unknown.</param>
public sealed record UpdateInfo(string Version, string? ReleaseNotes = null, long SizeBytes = 0, bool IsPrerelease = false);

/// <summary>Where the updater is in its cycle; drives the Preferences status line, the editor InfoBar, the tray item and toasts.</summary>
public enum UpdateState
{
    /// <summary>This copy is not updated in place (development build, smoke/isolated run); <see cref="UpdateStatus.Message"/> says why.</summary>
    NotSupported,
    /// <summary>Turned off by policy (<c>allowUpdates</c> / <c>HKLM\Software\Policies\Shotwise\AllowUpdates = 0</c>).</summary>
    Disabled,
    /// <summary>Nothing checked yet in this session.</summary>
    Idle,
    /// <summary>A scheduled check is due and "Ask before checking for updates" is on: waiting for the user to approve it.</summary>
    AwaitingConsent,
    Checking,
    UpToDate,
    /// <summary>A newer version exists but is not downloaded (metered connection, background download off, or not yet approved).</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified: applied by a restart, or silently when Shotwise next exits.</summary>
    ReadyToRestart,
    /// <summary>The last check or download failed (offline, feed unreachable, rate-limited); retried on the next schedule tick.</summary>
    Failed,
}

/// <summary>Snapshot of the updater, raised through <see cref="IUpdateService.StatusChanged"/>.</summary>
public sealed record UpdateStatus(UpdateState State, string CurrentVersion, UpdateChannel Channel)
{
    public DateTimeOffset? LastCheck { get; init; }
    /// <summary>The release found by the last check (also set while <see cref="UpdateState.Downloading"/> / <see cref="UpdateState.ReadyToRestart"/>).</summary>
    public UpdateInfo? Update { get; init; }
    /// <summary>Download progress 0-100 while <see cref="UpdateState.Downloading"/>.</summary>
    public int Progress { get; init; }
    /// <summary>Plain-English detail: why updates are unsupported/disabled, or the last error.</summary>
    public string? Message { get; init; }
    /// <summary>True when the last failure was "no internet connection".</summary>
    public bool Offline { get; init; }
    /// <summary>Where updates come from (e.g. "GitHub yarink80/Shotwise", or a local test folder).</summary>
    public string? Source { get; init; }
    /// <summary>True when the current check/download was started by the schedule (toasts), false for an explicit user check (dialogs).</summary>
    public bool Scheduled { get; init; }
}

public enum UpdateRestartResult
{
    /// <summary>Shotwise is closing; the update is applied and Shotwise starts again.</summary>
    Restarting,
    /// <summary>No downloaded update is waiting.</summary>
    NothingToApply,
    /// <summary>A capture or recording is in progress; nothing was interrupted.</summary>
    BlockedByRecording,
    /// <summary>The user cancelled the save prompt for an unsaved document.</summary>
    Cancelled,
}

/// <summary>
/// In-place updater for installed copies (Velopack + GitHub Releases). Automatic checks follow the Updates preferences
/// (<see cref="UpdateSettings"/>); downloads never start on a metered connection unless the user asks; an update is applied
/// only when Shotwise restarts (never while recording, never with unsaved documents) or silently after it exits.
/// </summary>
public interface IUpdateService
{
    UpdateStatus Status { get; }
    /// <summary>Raised on any thread whenever <see cref="Status"/> changes.</summary>
    event EventHandler<UpdateStatus>? StatusChanged;
    /// <summary>Starts the startup + periodic schedule (idempotent; no-op when unsupported).</summary>
    void StartScheduler();
    /// <summary>
    /// Checks the feed. <paramref name="userApproved"/> = true: an explicit "Check for updates" (bypasses the schedule and the
    /// ask-first preference; policy still applies). <paramref name="channel"/> overrides the configured channel (Preferences shows
    /// a staged, not yet saved channel). Returns null when up to date, unsupported, disabled or offline (see <see cref="Status"/>).
    /// </summary>
    Task<UpdateInfo?> CheckAsync(bool userApproved, CancellationToken ct, UpdateChannel? channel = null);
    /// <summary>Downloads the release found by the last check; true when it is ready to apply.</summary>
    Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct);
    /// <summary>"Restart now": refuses during a capture/recording, asks to save unsaved documents, then exits and restarts updated.</summary>
    Task<UpdateRestartResult> RestartToApplyAsync();
    /// <summary>Called by the App as the last step of a normal exit: applies a waiting update (restarting when requested).</summary>
    void OnAppExit();
}

/// <summary>App-side hooks for an update restart (implemented by the composition root).</summary>
public interface IUpdateRestartHost
{
    /// <summary>True while a capture or a recording is in progress.</summary>
    bool IsCaptureInProgress { get; }
    /// <summary>Saves or closes every open document, prompting for unsaved ones; false when the user cancelled.</summary>
    Task<bool> SaveOrCloseDocumentsAsync();
    /// <summary>Starts a graceful exit (autosaves drain, the host stops, then <see cref="IUpdateService.OnAppExit"/> runs).</summary>
    void Exit();
}

/// <summary>Pairing payload: URL with one-time token (encoded in the QR code) and its expiry.</summary>
public sealed record PairingInfo(Uri Url, string Token, DateTimeOffset ExpiresAt, byte[]? QrCodePng = null);

public sealed record ReceivedFile(string Path, string OriginalName, string MediaType, long SizeBytes, string? DeviceName, DateTimeOffset ReceivedAt);

public interface IMobileConnectServer
{
    bool IsRunning { get; }
    int Port { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    PairingInfo CreatePairing(TimeSpan validity);
    event EventHandler<ReceivedFile>? FileReceived;
}

public enum PolicyFeature { Ocr, Outputs, SystemAudio, Microphone, Webcam, Video, MobileSharing, Ftp, CloudSharing, Updates, Telemetry, Experiments }

/// <summary>Feature switches: HKLM\Software\Policies\Shotwise (wins) + settings.</summary>
public interface IPolicyService
{
    bool IsAllowed(PolicyFeature f);
}

/// <summary>Well-known per-user locations (see ARCHITECTURE §6).</summary>
public interface IAppPaths
{
    /// <summary>%APPDATA%\Shotwise (settings, presets, themes, palettes, user stamps/templates, share settings).</summary>
    string AppData { get; }
    /// <summary>%LOCALAPPDATA%\Shotwise (catalog, thumbnails, models, logs).</summary>
    string LocalAppData { get; }
    string Logs { get; }
    /// <summary>%TEMP%\Shotwise (cleaned at start).</summary>
    string Temp { get; }
    string Models { get; }
    /// <summary>Built-in content shipped next to the executable (stamps, templates, themes, palettes, cursors).</summary>
    string Content { get; }
    /// <summary>Default library root: %USERPROFILE%\Documents\Shotwise\Library (under the data root when one is set).</summary>
    string DefaultLibraryRoot { get; }
    /// <summary>
    /// Default folder for saved/exported files when no folder is configured: %USERPROFILE%\Documents\Shotwise, or
    /// <c>&lt;data root&gt;\Documents</c> when <c>SHOTWISE_DATA_ROOT</c> is set, so isolated runs never write to the real profile.
    /// </summary>
    string DefaultSaveFolder => Path.GetDirectoryName(DefaultLibraryRoot) ?? DefaultLibraryRoot;
}
