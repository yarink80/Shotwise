namespace Shotwise.Core.Platform;

/// <summary>Registration ids of the global hotkeys (shared by the coordinator, preferences, tray and the shortcut sheet).</summary>
public static class HotkeyIds
{
    public const string GlobalCapture = "capture.global";
    /// <summary>The configured fallback of <see cref="GlobalCapture"/> (registered under the <see cref="GlobalCapture"/> id when used).</summary>
    public const string GlobalCaptureFallback = "capture.global.fallback";
    public const string Repeat = "capture.repeat";
    public const string ShowHide = "capture.showhide";
    public const string VideoPauseResume = "capture.video.pause";
    public const string VideoStop = "capture.video.stop";
    public const string ScreenDraw = "capture.video.draw";
    public const string WebcamFullscreen = "capture.video.webcam";
    public const string PresetPrefix = "capture.preset.";

    public static string Preset(string presetId) => PresetPrefix + presetId;

    /// <summary>The preset id of a <see cref="Preset"/> registration id, or null for a settings hotkey.</summary>
    public static string? PresetIdOf(string? id) => id is not null && id.StartsWith(PresetPrefix, StringComparison.Ordinal) ? id[PresetPrefix.Length..] : null;

    /// <summary>
    /// True when two ids name the same binding. The global capture and its fallback count as one: the fallback key is registered
    /// under <see cref="GlobalCapture"/> while it is in use.
    /// </summary>
    public static bool SameBinding(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        static bool Capture(string x) => x is GlobalCapture or GlobalCaptureFallback;
        return Capture(a) && Capture(b);
    }
}

/// <summary>What happened to one configured global hotkey in the last registration pass.</summary>
public enum HotkeyState
{
    /// <summary>Registered with the configured key.</summary>
    Active,
    /// <summary>The configured key was taken; the fallback key is registered instead.</summary>
    Fallback,
    /// <summary>Held by Windows or another application (no fallback available or the fallback is taken too).</summary>
    InUse,
    /// <summary>Another Shotwise hotkey already uses the key.</summary>
    Duplicate,
    /// <summary>The key cannot be a global hotkey (modifier only, plain letter, F12...).</summary>
    Unsupported,
    /// <summary>No key assigned.</summary>
    Off,
    /// <summary>Only registered while a recording runs (ScreenDraw, webcam swap).</summary>
    RecordingOnly,
}

/// <param name="Id">Registration id (<see cref="HotkeyIds"/>).</param>
/// <param name="Label">Display name (localized by the UI that produced it).</param>
/// <param name="Configured">The key the user configured (or the default).</param>
/// <param name="Effective">The key actually registered (fallback when <see cref="State"/> is <see cref="HotkeyState.Fallback"/>), else null.</param>
/// <param name="IsPreset">True for per-preset hotkeys.</param>
public sealed record HotkeyStatus(string Id, string Label, Hotkey? Configured, Hotkey? Effective, HotkeyState State, bool IsPreset = false)
{
    /// <summary>The key to show in UI captions: the effective key when registered, else the configured one.</summary>
    public Hotkey? Display => Effective ?? Configured;
}

/// <summary>Live view of the global hotkey registrations (implemented by the capture coordinator, read by tray / editor / preferences).</summary>
public interface IHotkeyStatusSource
{
    IReadOnlyList<HotkeyStatus> Current { get; }
    HotkeyStatus? Find(string id);
    /// <summary>Raised after each registration pass (on the UI thread).</summary>
    event EventHandler? Changed;
}

/// <summary>Result of <see cref="HotkeyRegistration.RegisterWithFallback"/>.</summary>
public readonly record struct FallbackRegistration(HotkeyRegistrationResult Result, Hotkey? Effective, bool UsedFallback, HotkeyRegistrationResult PreferredResult);

public static class HotkeyRegistration
{
    /// <summary>
    /// Registers <paramref name="preferred"/>; when Windows or another app holds it (<see cref="HotkeyRegistrationResult.InUseBySystemOrOtherApp"/>)
    /// tries each fallback in order. Duplicate / unsupported results do not fall back (they are configuration problems).
    /// </summary>
    public static FallbackRegistration RegisterWithFallback(this IHotkeyService service, string id, Hotkey preferred, IEnumerable<Hotkey> fallbacks, Action callback, bool allowOverride)
    {
        ArgumentNullException.ThrowIfNull(service);
        var first = service.Register(id, preferred, callback, allowOverride);
        if (first == HotkeyRegistrationResult.Ok) return new(first, preferred, false, first);
        if (first != HotkeyRegistrationResult.InUseBySystemOrOtherApp) return new(first, null, false, first);
        foreach (var f in fallbacks)
        {
            if (f.IsEmpty || f == preferred || !f.IsValidGlobalHotkey) continue;
            var r = service.Register(id, f, callback, allowOverride);
            if (r == HotkeyRegistrationResult.Ok) return new(r, f, true, first);
        }
        return new(first, null, false, first);
    }

    /// <summary>Maps a registration result to the state shown in preferences.</summary>
    public static HotkeyState StateOf(HotkeyRegistrationResult r, bool usedFallback = false) => r switch
    {
        HotkeyRegistrationResult.Ok => usedFallback ? HotkeyState.Fallback : HotkeyState.Active,
        HotkeyRegistrationResult.Duplicate => HotkeyState.Duplicate,
        HotkeyRegistrationResult.InUseBySystemOrOtherApp => HotkeyState.InUse,
        _ => HotkeyState.Unsupported,
    };

    /// <summary>
    /// Why <paramref name="key"/> cannot be used next to <paramref name="others"/> (label → key), or null when it can:
    /// "unsupported" (modifier only / plain key / F12) or the label of the hotkey that already uses it.
    /// </summary>
    public static string? FindConflict(Hotkey key, IEnumerable<(string Label, Hotkey? Key)> others, out bool unsupported)
    {
        unsupported = !key.IsValidGlobalHotkey || key.Key == VirtualKey.F12;
        if (unsupported) return null;
        foreach (var (label, other) in others)
            if (other is { } o && o == key) return label;
        return null;
    }
}

/// <summary>Process identity shared by the toast notifier, taskbar grouping and the Start menu shortcut.</summary>
public static class AppIdentity
{
    /// <summary>Explicit AppUserModelID of Shotwise (set on the process at startup and on the Start menu shortcut).</summary>
    public const string AppUserModelId = "Shotwise.App";
    public const string DisplayName = "Shotwise";
    public const string Description = "Capture your screen, mark it up and share it.";
}

/// <summary>
/// The per-user Start menu shortcut (<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Shotwise.lnk</c>) pointing at the running
/// Shotwise.exe, with the exe's icon and <see cref="AppIdentity.AppUserModelId"/> so toasts show the Shotwise name and icon.
/// </summary>
public interface IStartMenuShortcut
{
    string ShortcutPath { get; }
    /// <summary>True when the shortcut exists, targets <paramref name="exePath"/> and carries the Shotwise AUMID.</summary>
    bool IsCurrent(string exePath);
    /// <summary>Creates or rewrites the shortcut for <paramref name="exePath"/>; returns true when the file was written.</summary>
    bool Ensure(string exePath, bool force = false);
    /// <summary>Deletes the shortcut (no-op when missing).</summary>
    void Remove();
}

/// <summary>
/// Optional per-user Desktop shortcut (<c>Desktop\Shotwise.lnk</c>), written like <see cref="IStartMenuShortcut"/> (exe icon + AUMID).
/// The installer creates it on first install; Preferences → Advanced adds or removes it; updates only refresh an existing one.
/// </summary>
public interface IDesktopShortcut
{
    string ShortcutPath { get; }
    bool Exists { get; }
    /// <summary>Creates or rewrites the shortcut for <paramref name="exePath"/>; returns true when the file was written.</summary>
    bool Ensure(string exePath, bool force = false);
    /// <summary>Deletes the shortcut (no-op when missing).</summary>
    void Remove();
}
