namespace Shotwise.Core.Platform;

/// <summary>
/// What the "likely owner" heuristics may look at. Windows does not say which process holds a global hotkey, so Shotwise guesses
/// from running processes and a few per-user settings. Implemented in Shotwise.Platform; tests use fakes.
/// </summary>
public interface IHotkeyEnvironment
{
    /// <summary>Names (without ".exe") of the running processes of other programs; the current process is excluded.</summary>
    IReadOnlyCollection<string> RunningProcessNames();
    /// <summary>A value under HKEY_CURRENT_USER\<paramref name="subKey"/>, or null when missing / unreadable.</summary>
    object? ReadUserValue(string subKey, string name);
    /// <summary>Child key names of HKEY_CURRENT_USER\<paramref name="subKey"/> (empty when missing).</summary>
    IReadOnlyList<string> UserSubKeys(string subKey);
    /// <summary>Value names of HKEY_CURRENT_USER\<paramref name="subKey"/> (empty when missing).</summary>
    IReadOnlyList<string> UserValueNames(string subKey);
}

/// <summary>Which keys an owner entry can hold.</summary>
public enum HotkeyOwnerScope
{
    /// <summary>Only plain PrintScreen.</summary>
    PlainPrintScreen,
    /// <summary>PrintScreen with or without modifiers (screenshot tools).</summary>
    PrintScreenFamily,
    /// <summary>Only the listed <see cref="KnownHotkeyOwner.Keys"/>.</summary>
    SpecificKeys,
    /// <summary>User-defined hotkeys: any combination may be taken (listed after more specific matches).</summary>
    AnyKey,
}

/// <summary>How an entry is detected besides (or instead of) a running process.</summary>
public enum HotkeyOwnerSetting
{
    None,
    /// <summary>HKCU\Control Panel\Keyboard PrintScreenKeyForSnippingEnabled = 1.</summary>
    WindowsPrintScreenSnipping,
    /// <summary>The cloud client is running and its per-account "save screenshots" option is on (or unknown).</summary>
    CloudScreenshotSaving,
}

/// <summary>One row of the likely-owner table.</summary>
/// <param name="DisplayName">Shown as "in use by {DisplayName}".</param>
/// <param name="ProcessNames">Process names (without ".exe", case-insensitive); empty when only <paramref name="Setting"/> detects it.</param>
/// <param name="Scope">Keys the app is known to hold.</param>
/// <param name="Keys">For <see cref="HotkeyOwnerScope.SpecificKeys"/>: the default hotkeys of the app.</param>
/// <param name="Setting">Extra setting check.</param>
/// <param name="UncertainName">Name used when the setting cannot be read (e.g. "Dropbox (if screenshot saving is on)").</param>
public sealed record KnownHotkeyOwner(string DisplayName, string[] ProcessNames, HotkeyOwnerScope Scope, string[]? Keys = null,
    HotkeyOwnerSetting Setting = HotkeyOwnerSetting.None, string? UncertainName = null)
{
    public bool Covers(Hotkey key) => Scope switch
    {
        HotkeyOwnerScope.PlainPrintScreen => key is { Key: VirtualKey.PrintScreen, Modifiers: ModifierKeys.None },
        HotkeyOwnerScope.PrintScreenFamily => key.Key == VirtualKey.PrintScreen,
        HotkeyOwnerScope.SpecificKeys => Keys?.Any(k => Hotkey.TryParse(k, out var h) && h == key) == true,
        _ => true,
    };
}

/// <summary>
/// The likely-owner table (interop data: process and display names of well-known apps that register global hotkeys) and the
/// detector that matches it against the running processes and settings. Keep every app-specific name in <see cref="Table"/>.
/// </summary>
public static class KnownHotkeyOwners
{
    public const string WindowsSnippingName = "Windows Snipping Tool (Print screen setting)";
    public const string WindowsKeyboardSubKey = @"Control Panel\Keyboard";
    public const string WindowsSnippingValue = "PrintScreenKeyForSnippingEnabled";
    public const string OneDriveAccountsSubKey = @"Software\Microsoft\OneDrive\Accounts";

    public static readonly IReadOnlyList<KnownHotkeyOwner> Table = new KnownHotkeyOwner[]
    {
        new(WindowsSnippingName, Array.Empty<string>(), HotkeyOwnerScope.PlainPrintScreen, Setting: HotkeyOwnerSetting.WindowsPrintScreenSnipping),
        // screenshot tools: they usually take PrintScreen and its Shift/Ctrl/Alt variants
        new("Snagit", new[] { "SnagitCapture", "SnagitEditor", "Snagit32" }, HotkeyOwnerScope.PrintScreenFamily),
        new("ShareX", new[] { "ShareX" }, HotkeyOwnerScope.PrintScreenFamily),
        new("Greenshot", new[] { "Greenshot" }, HotkeyOwnerScope.PrintScreenFamily),
        new("Lightshot", new[] { "Lightshot" }, HotkeyOwnerScope.PrintScreenFamily),
        new("PicPick", new[] { "picpick" }, HotkeyOwnerScope.PrintScreenFamily),
        new("FastStone Capture", new[] { "FSCapture" }, HotkeyOwnerScope.PrintScreenFamily),
        new("Flameshot", new[] { "flameshot" }, HotkeyOwnerScope.PrintScreenFamily),
        new("Screenpresso", new[] { "Screenpresso" }, HotkeyOwnerScope.PrintScreenFamily),
        new("OneDrive (save screenshots to OneDrive)", new[] { "OneDrive" }, HotkeyOwnerScope.PrintScreenFamily,
            Setting: HotkeyOwnerSetting.CloudScreenshotSaving, UncertainName: "OneDrive (if screenshot saving is on)"),
        new("Dropbox (if screenshot saving is on)", new[] { "Dropbox" }, HotkeyOwnerScope.PrintScreenFamily),
        // hotkey-heavy apps with known default shortcuts
        new("Xbox Game Bar", new[] { "GameBar", "GameBarFTServer" }, HotkeyOwnerScope.SpecificKeys,
            new[] { "Win+G", "Win+Alt+R", "Win+Alt+G", "Win+Alt+B", "Win+Alt+M", "Win+Alt+T", "Win+Alt+PrintScreen" }),
        new("NVIDIA overlay", new[] { "NVIDIA Share", "NVIDIA Overlay", "NVIDIA app" }, HotkeyOwnerScope.SpecificKeys,
            new[] { "Alt+Z", "Alt+R", "Alt+F1", "Alt+F2", "Alt+F3", "Alt+F9", "Alt+F10", "Alt+Shift+F10" }),
        new("AMD Software", new[] { "RadeonSoftware", "AMDRSServ" }, HotkeyOwnerScope.SpecificKeys,
            new[] { "Alt+R", "Ctrl+Shift+E", "Ctrl+Shift+S", "Ctrl+Shift+I", "Ctrl+Shift+J", "Ctrl+Shift+O" }),
        new("PowerToys", new[] { "PowerToys" }, HotkeyOwnerScope.SpecificKeys,
            new[] { "Win+Shift+T", "Win+Shift+C", "Win+Ctrl+T", "Alt+Space", "Win+Shift+/", "Win+Ctrl+V" }),
        // apps whose hotkeys are user-defined
        new("Discord", new[] { "Discord" }, HotkeyOwnerScope.AnyKey),
        new("OBS Studio", new[] { "obs64", "obs32" }, HotkeyOwnerScope.AnyKey),
        new("AutoHotkey", new[] { "AutoHotkey", "AutoHotkey64", "AutoHotkey32", "AutoHotkeyU64", "AutoHotkeyU32", "AutoHotkeyUX" }, HotkeyOwnerScope.AnyKey),
        new("Shotwise (another copy)", new[] { "Shotwise" }, HotkeyOwnerScope.AnyKey),
    };

    /// <summary>Display names of the apps that probably hold <paramref name="key"/> (most specific first), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Detect(Hotkey key, IHotkeyEnvironment env, int max = 4)
    {
        ArgumentNullException.ThrowIfNull(env);
        HashSet<string> running;
        try { running = new HashSet<string>(env.RunningProcessNames(), StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { running = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        var specific = new List<string>();
        var generic = new List<string>();
        foreach (var entry in Table)
        {
            if (!entry.Covers(key)) continue;
            var name = Present(entry, running, env);
            if (name is null) continue;
            (entry.Scope == HotkeyOwnerScope.AnyKey ? generic : specific).Add(name);
        }
        return specific.Concat(generic).Distinct().Take(max).ToList();
    }

    /// <summary>The name to show when the entry is present on this PC, else null.</summary>
    private static string? Present(KnownHotkeyOwner entry, HashSet<string> running, IHotkeyEnvironment env)
    {
        bool processRunning = entry.ProcessNames.Any(running.Contains);
        switch (entry.Setting)
        {
            case HotkeyOwnerSetting.WindowsPrintScreenSnipping:
                return IsOn(Safe(() => env.ReadUserValue(WindowsKeyboardSubKey, WindowsSnippingValue))) == true ? entry.DisplayName : null;
            case HotkeyOwnerSetting.CloudScreenshotSaving:
                if (!processRunning) return null;
                return CloudScreenshotSetting(env) switch
                {
                    true => entry.DisplayName,
                    false => null,
                    null => entry.UncertainName ?? entry.DisplayName,
                };
            default:
                return processRunning ? entry.DisplayName : null;
        }
    }

    /// <summary>
    /// Best effort: any per-account value whose name mentions screenshots (true when one is on, false when all found are off, null when
    /// none can be read — the client keeps the option in its own files on most versions).
    /// </summary>
    private static bool? CloudScreenshotSetting(IHotkeyEnvironment env)
    {
        bool? result = null;
        foreach (var account in Safe(() => env.UserSubKeys(OneDriveAccountsSubKey)) ?? Array.Empty<string>())
        {
            var sub = OneDriveAccountsSubKey + "\\" + account;
            foreach (var value in Safe(() => env.UserValueNames(sub)) ?? Array.Empty<string>())
            {
                if (value.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var on = IsOn(Safe(() => env.ReadUserValue(sub, value)));
                if (on == true) return true;
                if (on == false) result = false;
            }
        }
        return result;
    }

    private static bool? IsOn(object? value) => value switch
    {
        int i => i != 0,
        long l => l != 0,
        string s when int.TryParse(s, out var n) => n != 0,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }
    private static object? Safe(Func<object?> f)
    {
        try { return f(); } catch (Exception) { return null; }
    }
}
