using System.Text;
using System.Text.Json.Serialization;
using Shotwise.Core.Serialization;

namespace Shotwise.Core.Platform;

/// <summary>Keyboard modifiers. Values match Win32 RegisterHotKey MOD_* flags.</summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>Virtual-key codes (values match Win32 VK_* constants).</summary>
public enum VirtualKey : ushort
{
    None = 0,
    Back = 0x08, Tab = 0x09, Clear = 0x0C, Enter = 0x0D,
    Shift = 0x10, Control = 0x11, Menu = 0x12, Pause = 0x13, CapsLock = 0x14,
    Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
    Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28,
    PrintScreen = 0x2C, Insert = 0x2D, Delete = 0x2E,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    LeftWindows = 0x5B, RightWindows = 0x5C, Apps = 0x5D,
    NumPad0 = 0x60, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply = 0x6A, Add = 0x6B, Separator = 0x6C, Subtract = 0x6D, Decimal = 0x6E, Divide = 0x6F,
    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    NumLock = 0x90, ScrollLock = 0x91,
    LeftShift = 0xA0, RightShift = 0xA1, LeftControl = 0xA2, RightControl = 0xA3, LeftAlt = 0xA4, RightAlt = 0xA5,
    VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF,
    MediaNextTrack = 0xB0, MediaPreviousTrack = 0xB1, MediaStop = 0xB2, MediaPlayPause = 0xB3,
    OemSemicolon = 0xBA, OemPlus = 0xBB, OemComma = 0xBC, OemMinus = 0xBD, OemPeriod = 0xBE, OemQuestion = 0xBF, OemTilde = 0xC0,
    OemOpenBrackets = 0xDB, OemPipe = 0xDC, OemCloseBrackets = 0xDD, OemQuotes = 0xDE, Oem102 = 0xE2,
}

/// <summary>A key + modifiers combination. JSON/display form: "Ctrl+Shift+F9", "PrintScreen", "Ctrl+=".</summary>
[JsonConverter(typeof(HotkeyJsonConverter))]
public readonly record struct Hotkey(VirtualKey Key, ModifierKeys Modifiers)
{
    public bool IsEmpty => Key == VirtualKey.None;

    public static bool IsModifierKey(VirtualKey k) => k is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
        or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.LeftAlt or VirtualKey.RightAlt or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>
    /// Whether this combination is acceptable as a global hotkey: a non-modifier key, and either at least one modifier
    /// or a key that is safe alone (function keys, PrintScreen, Pause, ScrollLock).
    /// </summary>
    public bool IsValidGlobalHotkey
    {
        get
        {
            if (IsEmpty || IsModifierKey(Key)) return false;
            if (Modifiers != ModifierKeys.None) return true;
            return Key is >= VirtualKey.F1 and <= VirtualKey.F24 or VirtualKey.PrintScreen or VirtualKey.Pause or VirtualKey.ScrollLock;
        }
    }

    public string ToDisplayString()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public override string ToString() => ToDisplayString();

    public static Hotkey Parse(string s) => TryParse(s, out var h) ? h : throw new FormatException($"Invalid hotkey '{s}'.");

    /// <summary>Parses "Ctrl+Shift+F9", "Control + Alt + PrtSc", "Ctrl++", "Ctrl+Num +", "Win+Shift+S" (case-insensitive).</summary>
    public static bool TryParse(string? s, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var text = s.Trim();
        string keyToken;
        string modPart;
        if (text.EndsWith('+'))
        {
            // the key itself is '+': "Ctrl++" (OemPlus), "+" alone, or "Ctrl+Num +" (numpad add)
            var rest = text[..^1].TrimEnd();
            if (rest.Length == 0) { keyToken = "+"; modPart = ""; }
            else if (rest.EndsWith('+')) { keyToken = "+"; modPart = rest[..^1]; }
            else if (rest.EndsWith("Num", StringComparison.OrdinalIgnoreCase))
            {
                keyToken = "Num +";
                var before = rest[..^3].TrimEnd();
                if (before.Length == 0) modPart = "";
                else if (before.EndsWith('+')) modPart = before[..^1];
                else return false;
            }
            else return false;
        }
        else
        {
            var idx = text.LastIndexOf('+');
            keyToken = idx < 0 ? text : text[(idx + 1)..];
            modPart = idx < 0 ? "" : text[..idx];
        }

        var mods = ModifierKeys.None;
        foreach (var raw in modPart.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = raw.Trim().ToLowerInvariant();
            switch (m)
            {
                case "ctrl" or "control" or "ctl" or "strg": mods |= ModifierKeys.Control; break;
                case "alt" or "menu" or "option": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows" or "meta" or "super" or "cmd": mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        if (!TryParseKey(keyToken.Trim(), out var key)) return false;
        hotkey = new Hotkey(key, mods);
        return true;
    }

    private static readonly Dictionary<VirtualKey, string> DisplayNames = new()
    {
        [VirtualKey.Back] = "Backspace", [VirtualKey.Enter] = "Enter", [VirtualKey.Escape] = "Esc", [VirtualKey.Space] = "Space",
        [VirtualKey.PageUp] = "PageUp", [VirtualKey.PageDown] = "PageDown", [VirtualKey.PrintScreen] = "PrintScreen",
        [VirtualKey.Insert] = "Ins", [VirtualKey.Delete] = "Del", [VirtualKey.CapsLock] = "CapsLock",
        [VirtualKey.Multiply] = "Num *", [VirtualKey.Add] = "Num +", [VirtualKey.Subtract] = "Num -", [VirtualKey.Decimal] = "Num .", [VirtualKey.Divide] = "Num /",
        [VirtualKey.OemSemicolon] = ";", [VirtualKey.OemPlus] = "=", [VirtualKey.OemComma] = ",", [VirtualKey.OemMinus] = "-", [VirtualKey.OemPeriod] = ".",
        [VirtualKey.OemQuestion] = "/", [VirtualKey.OemTilde] = "`", [VirtualKey.OemOpenBrackets] = "[", [VirtualKey.OemPipe] = "\\",
        [VirtualKey.OemCloseBrackets] = "]", [VirtualKey.OemQuotes] = "'",
    };

    private static readonly Dictionary<string, VirtualKey> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrtSc"] = VirtualKey.PrintScreen, ["PrtScn"] = VirtualKey.PrintScreen, ["Print"] = VirtualKey.PrintScreen, ["Print Screen"] = VirtualKey.PrintScreen,
        ["Snapshot"] = VirtualKey.PrintScreen, ["Del"] = VirtualKey.Delete, ["Ins"] = VirtualKey.Insert, ["Esc"] = VirtualKey.Escape,
        ["Return"] = VirtualKey.Enter, ["PgUp"] = VirtualKey.PageUp, ["PgDn"] = VirtualKey.PageDown, ["Backspace"] = VirtualKey.Back,
        ["+"] = VirtualKey.OemPlus, ["Plus"] = VirtualKey.OemPlus, ["Minus"] = VirtualKey.OemMinus, ["Break"] = VirtualKey.Pause,
        ["Spacebar"] = VirtualKey.Space, ["Caps"] = VirtualKey.CapsLock,
        ["Num+"] = VirtualKey.Add, ["Num-"] = VirtualKey.Subtract, ["Num*"] = VirtualKey.Multiply, ["Num/"] = VirtualKey.Divide, ["Num."] = VirtualKey.Decimal,
    };

    public static string KeyName(VirtualKey k)
    {
        if (DisplayNames.TryGetValue(k, out var n)) return n;
        if (k is >= VirtualKey.D0 and <= VirtualKey.D9) return ((char)('0' + (k - VirtualKey.D0))).ToString();
        if (k is >= VirtualKey.NumPad0 and <= VirtualKey.NumPad9) return "Num " + (k - VirtualKey.NumPad0);
        return k.ToString();
    }

    public static bool TryParseKey(string token, out VirtualKey key)
    {
        key = VirtualKey.None;
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var (k, name) in DisplayNames)
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) { key = k; return true; }
        if (Aliases.TryGetValue(token, out key)) return true;
        var compact = token.Replace(" ", "");
        if (Aliases.TryGetValue(compact, out key)) return true;
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') { key = (VirtualKey)((int)VirtualKey.A + (c - 'A')); return true; }
            if (c is >= '0' and <= '9') { key = (VirtualKey)((int)VirtualKey.D0 + (c - '0')); return true; }
        }
        if (compact.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && compact.Length == 4 && char.IsAsciiDigit(compact[3]))
        {
            key = (VirtualKey)((int)VirtualKey.NumPad0 + (compact[3] - '0'));
            return true;
        }
        if (Enum.TryParse(compact, ignoreCase: true, out VirtualKey parsed) && Enum.IsDefined(parsed) && parsed != VirtualKey.None)
        {
            key = parsed;
            return true;
        }
        return false;
    }
}
