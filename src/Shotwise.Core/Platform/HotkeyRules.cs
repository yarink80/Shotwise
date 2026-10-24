using Shotwise.Core.Capture;

namespace Shotwise.Core.Platform;

/// <summary>Why a combination is a poor or impossible global hotkey (localized by the UI; <see cref="HotkeyRules.Describe"/> gives English).</summary>
public enum HotkeyReservation
{
    None,
    /// <summary>A key without modifiers that people type (letters, digits, Enter, arrows...).</summary>
    PlainKey,
    /// <summary>Shift + a typing key (capital letters, symbols) or a text-selection combination.</summary>
    TypingKey,
    /// <summary>Win+L.</summary>
    LockScreen,
    /// <summary>Ctrl+Alt+Del.</summary>
    SecureAttention,
    /// <summary>Ctrl+Shift+Esc.</summary>
    TaskManager,
    /// <summary>Esc with modifiers (Start menu, window cycling).</summary>
    SystemEscape,
    /// <summary>Tab with modifiers (Alt+Tab, Ctrl+Tab, Win+Tab).</summary>
    AppSwitcher,
    /// <summary>Alt+F4.</summary>
    CloseWindow,
    /// <summary>Win+Shift+S (Windows screen snip).</summary>
    WindowsSnip,
    /// <summary>Win+PrintScreen (Windows saves a screenshot to Pictures).</summary>
    WindowsScreenshot,
    /// <summary>Alt+PrintScreen (Windows copies the active window).</summary>
    ActiveWindowScreenshot,
    /// <summary>Win+Alt+PrintScreen (Game Bar screenshot).</summary>
    GameBarScreenshot,
    /// <summary>Other Win+ combinations Windows uses (Win+letter, Win+digit, Win+arrows).</summary>
    WindowsShortcut,
    /// <summary>Any F12 combination (reserved by Windows for the debugger; RegisterHotKey refuses it).</summary>
    Debugger,
    /// <summary>Ctrl + a single letter/digit/symbol (copy, paste, save... in every app).</summary>
    AppShortcut,
    /// <summary>Alt + a letter/digit (menu access keys) or Alt+arrows (back/forward).</summary>
    MenuAccessKey,
    /// <summary>Enter / Space / Backspace with fewer than two modifiers.</summary>
    SystemKey,
}

/// <summary>What a hotkey is for; drives the ranked suggestions (<see cref="HotkeySuggestionCatalog"/>).</summary>
public enum HotkeyPurpose
{
    Generic,
    AllInOne,
    CaptureFallback,
    Image,
    Fullscreen,
    Window,
    Video,
    GrabText,
    Panoramic,
    StepCapture,
    Repeat,
    ShowHide,
    VideoPauseResume,
    VideoStop,
    ScreenDraw,
    Webcam,
}

/// <summary>Reserved / bad-global-hotkey rules (pure; no Windows calls).</summary>
public static class HotkeyRules
{
    /// <summary>Unsupported = cannot be a hotkey at all (empty, modifier only, unknown key).</summary>
    public static bool IsUnsupported(Hotkey k)
        => k.IsEmpty || Hotkey.IsModifierKey(k.Key) || !Enum.IsDefined(k.Key) || ((int)k.Modifiers & ~15) != 0;

    private static bool IsLetter(VirtualKey k) => k is >= VirtualKey.A and <= VirtualKey.Z;
    private static bool IsDigit(VirtualKey k) => k is >= VirtualKey.D0 and <= VirtualKey.D9;
    private static bool IsFunction(VirtualKey k) => k is >= VirtualKey.F1 and <= VirtualKey.F24;
    private static bool IsOem(VirtualKey k) => k is >= VirtualKey.OemSemicolon and <= VirtualKey.OemTilde or >= VirtualKey.OemOpenBrackets and <= VirtualKey.Oem102;
    private static bool IsNumPad(VirtualKey k) => k is >= VirtualKey.NumPad0 and <= VirtualKey.Divide;
    private static bool IsNavigation(VirtualKey k) => k is >= VirtualKey.PageUp and <= VirtualKey.Down or VirtualKey.Insert or VirtualKey.Delete;
    private static bool IsTyping(VirtualKey k) => IsLetter(k) || IsDigit(k) || IsOem(k) || IsNumPad(k) || k == VirtualKey.Space;

    private static int Count(ModifierKeys m) => System.Numerics.BitOperations.PopCount((uint)m);

    /// <summary>Why <paramref name="k"/> should not be used as a global hotkey, or <see cref="HotkeyReservation.None"/>.</summary>
    public static HotkeyReservation Reservation(Hotkey k)
    {
        if (IsUnsupported(k)) return HotkeyReservation.None;
        var m = k.Modifiers;
        var key = k.Key;
        bool win = m.HasFlag(ModifierKeys.Windows);

        if (key == VirtualKey.F12) return HotkeyReservation.Debugger;
        if (key == VirtualKey.L && m == ModifierKeys.Windows) return HotkeyReservation.LockScreen;
        if (key == VirtualKey.Delete && m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Alt)) return HotkeyReservation.SecureAttention;
        if (key == VirtualKey.Escape)
            return m == (ModifierKeys.Control | ModifierKeys.Shift) ? HotkeyReservation.TaskManager : m == ModifierKeys.None ? HotkeyReservation.PlainKey : HotkeyReservation.SystemEscape;
        if (key == VirtualKey.Tab) return m == ModifierKeys.None ? HotkeyReservation.PlainKey : HotkeyReservation.AppSwitcher;
        if (key == VirtualKey.F4 && m == ModifierKeys.Alt) return HotkeyReservation.CloseWindow;
        if (key == VirtualKey.S && m == (ModifierKeys.Windows | ModifierKeys.Shift)) return HotkeyReservation.WindowsSnip;
        if (key == VirtualKey.PrintScreen)
        {
            if (m == ModifierKeys.Alt) return HotkeyReservation.ActiveWindowScreenshot;
            if (m == ModifierKeys.Windows) return HotkeyReservation.WindowsScreenshot;
            if (m == (ModifierKeys.Windows | ModifierKeys.Alt)) return HotkeyReservation.GameBarScreenshot;
            return HotkeyReservation.None;
        }
        if (key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.Back && Count(m) < 2)
            return m == ModifierKeys.None ? HotkeyReservation.PlainKey : HotkeyReservation.SystemKey;
        if (m == ModifierKeys.None)
            return IsFunction(key) || key is VirtualKey.Pause or VirtualKey.ScrollLock ? HotkeyReservation.None : HotkeyReservation.PlainKey;
        if (m == ModifierKeys.Shift && (IsTyping(key) || IsNavigation(key))) return HotkeyReservation.TypingKey;
        if (!win && (m & ~(ModifierKeys.Control | ModifierKeys.Shift)) == 0 && IsNavigation(key)) return HotkeyReservation.TypingKey;   // Ctrl(+Shift)+arrows/Home/End select text
        if (win && (key is >= VirtualKey.Left and <= VirtualKey.Down || IsDigit(key))) return HotkeyReservation.WindowsShortcut;
        if (m == ModifierKeys.Windows && !IsFunction(key) && key is not (VirtualKey.Pause or VirtualKey.ScrollLock)) return HotkeyReservation.WindowsShortcut;
        if (m == ModifierKeys.Control && (IsTyping(key) || IsNavigation(key))) return HotkeyReservation.AppShortcut;
        if (m == ModifierKeys.Alt && (IsTyping(key) || IsNavigation(key))) return HotkeyReservation.MenuAccessKey;
        return HotkeyReservation.None;
    }

    /// <summary>English explanation of a reservation (logs, tests and fallback text).</summary>
    public static string Describe(HotkeyReservation r) => r switch
    {
        HotkeyReservation.PlainKey => "a key you type with — add Ctrl, Alt or Shift",
        HotkeyReservation.TypingKey => "used for typing or selecting text",
        HotkeyReservation.LockScreen => "Windows uses it to lock the PC",
        HotkeyReservation.SecureAttention => "reserved by Windows (security screen)",
        HotkeyReservation.TaskManager => "Windows uses it to open Task Manager",
        HotkeyReservation.SystemEscape => "Windows uses Esc combinations (Start menu, window switching)",
        HotkeyReservation.AppSwitcher => "Windows and apps use it to switch windows or tabs",
        HotkeyReservation.CloseWindow => "Windows uses it to close the active window",
        HotkeyReservation.WindowsSnip => "Windows uses it to open its screen snip",
        HotkeyReservation.WindowsScreenshot => "Windows uses it to save a screenshot to Pictures",
        HotkeyReservation.ActiveWindowScreenshot => "Windows uses it to copy the active window",
        HotkeyReservation.GameBarScreenshot => "the Xbox Game Bar uses it for screenshots",
        HotkeyReservation.WindowsShortcut => "Windows uses most Win+ shortcuts",
        HotkeyReservation.Debugger => "F12 combinations are reserved by Windows for the debugger",
        HotkeyReservation.AppShortcut => "almost every app uses it (copy, paste, save...)",
        HotkeyReservation.MenuAccessKey => "apps use Alt+ keys for menus and navigation",
        HotkeyReservation.SystemKey => "apps and Windows use it",
        _ => "",
    };

    /// <summary>Purpose of a binding id (<see cref="HotkeyIds"/>) or a preset-kind string ("all-in-one", "image", "video", "grab-text"...).</summary>
    public static HotkeyPurpose ParsePurpose(string? bindingIdOrKind)
    {
        if (string.IsNullOrWhiteSpace(bindingIdOrKind)) return HotkeyPurpose.Generic;
        switch (bindingIdOrKind)
        {
            case HotkeyIds.GlobalCapture: return HotkeyPurpose.AllInOne;
            case HotkeyIds.GlobalCaptureFallback: return HotkeyPurpose.CaptureFallback;
            case HotkeyIds.Repeat: return HotkeyPurpose.Repeat;
            case HotkeyIds.ShowHide: return HotkeyPurpose.ShowHide;
            case HotkeyIds.VideoPauseResume: return HotkeyPurpose.VideoPauseResume;
            case HotkeyIds.VideoStop: return HotkeyPurpose.VideoStop;
            case HotkeyIds.ScreenDraw: return HotkeyPurpose.ScreenDraw;
            case HotkeyIds.WebcamFullscreen: return HotkeyPurpose.Webcam;
        }
        if (HotkeyIds.PresetIdOf(bindingIdOrKind) is { } presetId)
        {
            return presetId switch
            {
                PresetDefaults.AllInOneId => HotkeyPurpose.AllInOne,
                PresetDefaults.ImageRegionId => HotkeyPurpose.Image,
                PresetDefaults.VideoId => HotkeyPurpose.Video,
                PresetDefaults.GrabTextId => HotkeyPurpose.GrabText,
                PresetDefaults.PanoramicId => HotkeyPurpose.Panoramic,
                PresetDefaults.StepCaptureId => HotkeyPurpose.StepCapture,
                PresetDefaults.FullscreenId => HotkeyPurpose.Fullscreen,
                PresetDefaults.WindowId => HotkeyPurpose.Window,
                _ => HotkeyPurpose.Generic,
            };
        }
        var kind = new string(bindingIdOrKind.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return kind switch
        {
            "allinone" or "global" or "capture" => HotkeyPurpose.AllInOne,
            "fallback" or "capturefallback" => HotkeyPurpose.CaptureFallback,
            "image" or "region" or "imageregion" => HotkeyPurpose.Image,
            "fullscreen" => HotkeyPurpose.Fullscreen,
            "window" => HotkeyPurpose.Window,
            "video" or "gif" => HotkeyPurpose.Video,
            "grabtext" or "text" or "ocr" => HotkeyPurpose.GrabText,
            "panoramic" or "scrolling" or "scrollingwindow" or "panorama" => HotkeyPurpose.Panoramic,
            "stepcapture" or "step" or "steps" => HotkeyPurpose.StepCapture,
            "repeat" or "repeatlast" => HotkeyPurpose.Repeat,
            "showhide" => HotkeyPurpose.ShowHide,
            "videopause" or "pause" => HotkeyPurpose.VideoPauseResume,
            "videostop" or "stop" => HotkeyPurpose.VideoStop,
            "screendraw" or "draw" => HotkeyPurpose.ScreenDraw,
            "webcam" => HotkeyPurpose.Webcam,
            _ => HotkeyPurpose.Generic,
        };
    }

    /// <summary>Purpose of a preset from its mode and selection (custom presets get the suggestions of the matching built-in).</summary>
    public static HotkeyPurpose PurposeOf(CapturePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Mode == CaptureMode.AllInOne) return HotkeyPurpose.AllInOne;
        if (preset.Mode == CaptureMode.Video) return HotkeyPurpose.Video;
        return preset.Selection switch
        {
            SelectionType.GrabText => HotkeyPurpose.GrabText,
            SelectionType.Panoramic or SelectionType.ScrollingWindow => HotkeyPurpose.Panoramic,
            SelectionType.StepCapture => HotkeyPurpose.StepCapture,
            SelectionType.Fullscreen => HotkeyPurpose.Fullscreen,
            SelectionType.Window => HotkeyPurpose.Window,
            _ => HotkeyPurpose.Image,
        };
    }
}

/// <summary>
/// Ranked common hotkeys per purpose. The first entry of <see cref="HotkeyPurpose.AllInOne"/> (PrintScreen) is an anchor: it is shown
/// even when it is unavailable, so the user sees why it is not offered (held by another app, or by another Shotwise hotkey).
/// </summary>
public static class HotkeySuggestionCatalog
{
    private static Hotkey H(string s) => Hotkey.Parse(s);

    // Suggestion pool rules (a global hotkey silently steals the combination from every app, and RegisterHotKey cannot detect
    // in-app shortcuts):
    //  * no Ctrl+Shift+letter/digit - common in-app shortcuts (browsers: reopen tab, hard reload; editors: command palette;
    //    spreadsheets: number formats);
    //  * no Ctrl+Alt+letter/digit - Ctrl+Alt is AltGr on many keyboard layouts, so these would block typing characters;
    //  * prefer the PrintScreen family, then Ctrl+Alt / Ctrl+Shift / Alt+Shift + F1-F11 (F12 is reserved for the debugger),
    //    which neither type characters nor collide with common app shortcuts.
    private static readonly Dictionary<HotkeyPurpose, string[]> Ranked = new()
    {
        [HotkeyPurpose.AllInOne] = new[] { "PrintScreen", "Shift+PrintScreen", "Ctrl+PrintScreen", "Ctrl+Alt+F1", "Alt+Shift+F1" },
        [HotkeyPurpose.CaptureFallback] = new[] { "Shift+PrintScreen", "Ctrl+PrintScreen", "Ctrl+Alt+F1", "Alt+Shift+F1" },
        [HotkeyPurpose.Image] = new[] { "Ctrl+Shift+PrintScreen", "Ctrl+Alt+F2", "Alt+Shift+F2" },
        [HotkeyPurpose.Fullscreen] = new[] { "Ctrl+PrintScreen", "Ctrl+Alt+F3", "Alt+Shift+F3" },
        [HotkeyPurpose.Window] = new[] { "Ctrl+Alt+F4", "Alt+Shift+F4" },
        [HotkeyPurpose.Video] = new[] { "Alt+Shift+PrintScreen", "Ctrl+Alt+F5", "Alt+Shift+F5" },
        [HotkeyPurpose.GrabText] = new[] { "Ctrl+Alt+PrintScreen", "Ctrl+Alt+F6", "Alt+Shift+F6" },
        [HotkeyPurpose.Panoramic] = new[] { "Ctrl+Alt+F7", "Alt+Shift+F7" },
        [HotkeyPurpose.StepCapture] = new[] { "Ctrl+Alt+F8", "Alt+Shift+F8" },
        [HotkeyPurpose.Repeat] = new[] { "Ctrl+Shift+F11", "Ctrl+Alt+F11", "Alt+Shift+F11" },
        [HotkeyPurpose.ShowHide] = new[] { "Ctrl+Alt+F10", "Alt+Shift+F10" },
        [HotkeyPurpose.VideoPauseResume] = new[] { "Ctrl+Shift+F9", "Ctrl+Alt+F9" },
        [HotkeyPurpose.VideoStop] = new[] { "Ctrl+Shift+F10", "Ctrl+Alt+F10" },
        [HotkeyPurpose.ScreenDraw] = new[] { "Ctrl+Shift+F8", "Ctrl+Alt+F8" },
        [HotkeyPurpose.Webcam] = new[] { "Ctrl+Shift+F", "Ctrl+Alt+F7" },
        [HotkeyPurpose.Generic] = Array.Empty<string>(),
    };

    /// <summary>Generic fallbacks appended to every list: Ctrl+Alt+F1…F11, then Alt+Shift+F1…F11, then Ctrl+Shift+F5…F11.</summary>
    public static IEnumerable<Hotkey> GenericFallbacks()
    {
        for (var f = VirtualKey.F1; f <= VirtualKey.F11; f++) yield return new Hotkey(f, ModifierKeys.Control | ModifierKeys.Alt);
        for (var f = VirtualKey.F1; f <= VirtualKey.F11; f++) yield return new Hotkey(f, ModifierKeys.Alt | ModifierKeys.Shift);
        for (var f = VirtualKey.F5; f <= VirtualKey.F11; f++) yield return new Hotkey(f, ModifierKeys.Control | ModifierKeys.Shift);
    }

    /// <summary>Candidates in rank order; <c>Anchor</c> = show even when unavailable.</summary>
    public static IEnumerable<(Hotkey Key, bool Anchor)> For(HotkeyPurpose purpose)
    {
        var seen = new HashSet<Hotkey>();
        var list = Ranked.TryGetValue(purpose, out var l) ? l : Array.Empty<string>();
        for (int i = 0; i < list.Length; i++)
        {
            var k = H(list[i]);
            if (seen.Add(k)) yield return (k, purpose == HotkeyPurpose.AllInOne && i == 0);
        }
        foreach (var k in GenericFallbacks())
            if (seen.Add(k)) yield return (k, false);
    }
}
