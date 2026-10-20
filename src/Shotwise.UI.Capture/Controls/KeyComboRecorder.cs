using System.Runtime.InteropServices;
using System.Windows.Threading;
using Shotwise.Core.Platform;
using CoreModifiers = Shotwise.Core.Platform.ModifierKeys;

namespace Shotwise.UI.Capture.Controls;

/// <summary>
/// Records one key combination through a temporary WH_KEYBOARD_LL hook. The hook sees keys before any global hotkey is
/// processed (Shotwise's own, other screenshot tools holding PrintScreen, Windows' PrintScreen→Snipping setting), so the
/// pressed combination is captured and swallowed instead of triggering a capture. Modifier keys are observed but never
/// swallowed (keeps the system's modifier state consistent). Events are raised on the dispatcher that started recording.
/// </summary>
internal sealed class KeyComboRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_BACK = 0x08, VK_DELETE = 0x2E;
    private static readonly HashSet<int> ModifierVks = new() { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc;   // must stay referenced while the hook is installed
    private int _swallowUpVk;

    /// <summary>A non-modifier key was pressed (with the modifiers held at that moment).</summary>
    public event Action<Hotkey>? Captured;
    /// <summary>The set of held modifiers changed (for a live "Ctrl+Shift+" preview); None when released.</summary>
    public event Action<CoreModifiers>? ModifiersChanged;
    /// <summary>Backspace/Delete without modifiers.</summary>
    public event Action? ClearRequested;
    /// <summary>Esc without modifiers.</summary>
    public event Action? CancelRequested;
    /// <summary>Tab without modifiers (passed through for keyboard navigation).</summary>
    public event Action? TabPressed;

    public KeyComboRecorder(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>True when the hook is installed; when false callers should fall back to WPF key events.</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _proc = null;
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _swallowUpVk = 0;
    }

    public void Dispose() => Stop();

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static CoreModifiers CurrentModifiers()
    {
        var m = CoreModifiers.None;
        if (Down(VK_CONTROL)) m |= CoreModifiers.Control;
        if (Down(VK_MENU)) m |= CoreModifiers.Alt;
        if (Down(VK_SHIFT)) m |= CoreModifiers.Shift;
        if (Down(VK_LWIN) || Down(VK_RWIN)) m |= CoreModifiers.Windows;
        return m;
    }

    private static CoreModifiers ModifierOf(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => CoreModifiers.Control,
        0x12 or 0xA4 or 0xA5 => CoreModifiers.Alt,
        0x10 or 0xA0 or 0xA1 => CoreModifiers.Shift,
        _ => CoreModifiers.Windows,
    };

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)info.vkCode;
        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        if (ModifierVks.Contains(vk))
        {
            // GetAsyncKeyState lags the hook by one event, so add/remove the key being reported now.
            var mods = CurrentModifiers();
            mods = isDown ? mods | ModifierOf(vk) : mods & ~ModifierOf(vk);
            _dispatcher.BeginInvoke(() => ModifiersChanged?.Invoke(mods));
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (isUp && vk == _swallowUpVk) { _swallowUpVk = 0; return (IntPtr)1; }
        if (!isDown) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var held = CurrentModifiers();
        if (held == CoreModifiers.None && vk == VK_TAB)
        {
            _dispatcher.BeginInvoke(() => TabPressed?.Invoke());
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        _swallowUpVk = vk;
        if (held == CoreModifiers.None && vk is VK_BACK or VK_DELETE) { _dispatcher.BeginInvoke(() => ClearRequested?.Invoke()); return (IntPtr)1; }
        if (held == CoreModifiers.None && vk == VK_ESCAPE) { _dispatcher.BeginInvoke(() => CancelRequested?.Invoke()); return (IntPtr)1; }
        var hotkey = new Hotkey((VirtualKey)vk, held);
        _dispatcher.BeginInvoke(() => Captured?.Invoke(hotkey));
        return (IntPtr)1;
    }
}
