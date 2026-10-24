using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Shotwise.Core.Platform;
namespace Shotwise.Platform;
/// <summary>Owns a message-only HWND. Callbacks use the supplied context, or the thread pool.</summary>
public sealed class HotkeyService : IHotkeyService, IHotkeyProbe, IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SynchronizationContext? _context;
    private readonly ILogger _log;
    private readonly WndProc _proc;
    private readonly object _lifetime = new();
    private nint _window;
    private int _next;
    private bool _disposed;
    private record Entry(int NativeId, Hotkey Key, Action Callback);
    public HotkeyService(ILogger<HotkeyService> log, SynchronizationContext? context = null)
    {
        _context = context; _log = LogRedaction.Wrap(log); _proc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shotwise Hotkeys" };
        _thread.Start(); _ready.Task.GetAwaiter().GetResult();
    }
    private T Send<T>(Func<T> command)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _commands.Enqueue(() => { try { completion.SetResult(command()); } catch (Exception e) { completion.SetException(e); } });
            if (!PostMessage(_window, 0x8001, 0, 0)) throw new Win32Exception();
        }
        return completion.Task.GetAwaiter().GetResult();
    }
    public IReadOnlyDictionary<string, Hotkey> Registered => Send(() => (IReadOnlyDictionary<string, Hotkey>)_entries.ToDictionary(e => e.Key, e => e.Value.Key));
    public HotkeyRegistrationResult Register(string id, Hotkey key, Action callback, bool allowOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentNullException.ThrowIfNull(callback);
        return Send(() =>
        {
            if (!key.IsValidGlobalHotkey || !Enum.IsDefined(key.Key) || ((int)key.Modifiers & ~15) != 0 || key.Key == VirtualKey.F12) return HotkeyRegistrationResult.Unsupported;
            var duplicate = _entries.FirstOrDefault(p => p.Value.Key == key && p.Key != id);
            if (duplicate.Value is not null && !allowOverride) return HotkeyRegistrationResult.Duplicate;
            if (_entries.TryGetValue(id, out var old) && old.Key == key) { _entries[id] = old with { Callback = callback }; return HotkeyRegistrationResult.Ok; }
            if (duplicate.Value is not null)
            {
                _entries.Remove(duplicate.Key);
                _entries[id] = duplicate.Value with { Callback = callback };
                if (old is not null) UnregisterHotKey(_window, old.NativeId);
                return HotkeyRegistrationResult.Ok;
            }
            var nativeId = ++_next;
            if (nativeId >= ProbeId) { _next = 0; return HotkeyRegistrationResult.Unsupported; }
            if (!RegisterHotKey(_window, nativeId, (uint)key.Modifiers | 0x4000, (uint)key.Key)) return HotkeyRegistrationResult.InUseBySystemOrOtherApp;
            if (old is not null) Remove(id);
            _entries[id] = new(nativeId, key, callback); return HotkeyRegistrationResult.Ok;
        });
    }
    /// <summary>Native id of availability probes; registrations count up from 1 and wrap before it.</summary>
    private const int ProbeId = 0xBFFF;
    /// <summary>
    /// Non-destructive availability test on the hotkey thread: RegisterHotKey on this service's message-only window, then UnregisterHotKey
    /// at once. Keys this service already registered report <see cref="HotkeyProbeResult.HeldByShotwise"/> (Windows would refuse them too).
    /// </summary>
    public HotkeyProbeResult Probe(Hotkey key) => Send(() =>
    {
        if (!key.IsValidGlobalHotkey || !Enum.IsDefined(key.Key) || ((int)key.Modifiers & ~15) != 0 || key.Key == VirtualKey.F12) return HotkeyProbeResult.Invalid;
        if (_entries.Values.Any(e => e.Key == key)) return HotkeyProbeResult.HeldByShotwise;
        if (!RegisterHotKey(_window, ProbeId, (uint)key.Modifiers | 0x4000, (uint)key.Key)) return HotkeyProbeResult.HeldByOtherApp;
        UnregisterHotKey(_window, ProbeId);
        return HotkeyProbeResult.Free;
    });
    private void Remove(string id) { if (_entries.Remove(id, out var entry)) UnregisterHotKey(_window, entry.NativeId); }
    public void Unregister(string id) => Send(() => { Remove(id); return true; });
    public void UnregisterAll() => Send(() => { foreach (var id in _entries.Keys.ToArray()) Remove(id); return true; });
    public bool IsPrintScreenTakenBySnipping() { using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"); return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int value && value != 0; }
    private nint WindowProc(nint hwnd, uint message, nuint w, nint l)
    {
        if (message == 0x8001) { while (_commands.TryDequeue(out var action)) action(); return 0; }
        if (message == 0x0312)
        {
            var entry = _entries.Values.FirstOrDefault(e => e.NativeId == (int)w);
            if (entry is not null)
            {
                void Invoke(object? _) { try { entry.Callback(); } catch (Exception e) { _log.LogError(e, "Hotkey callback failed"); } }
                try { if (_context is not null) _context.Post(Invoke, null); else ThreadPool.QueueUserWorkItem(Invoke); }
                catch (Exception e) { _log.LogWarning(e, "Hotkey callback context unavailable"); }
            }
            return 0;
        }
        if (message == 0x0010) { DestroyWindow(hwnd); PostQuitMessage(0); return 0; }
        return DefWindowProc(hwnd, message, w, l);
    }
    private void Run()
    {
        string name = "Shotwise.Hotkeys." + Guid.NewGuid().ToString("N");
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = _proc, ClassName = name, Instance = GetModuleHandle(null) };
        ushort atom = 0;
        try
        {
            atom = RegisterClassEx(ref wc); if (atom == 0) throw new Win32Exception();
            _window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, new nint(-3), 0, wc.Instance, 0);
            if (_window == 0) throw new Win32Exception();
            _ready.SetResult();
            int result;
            while ((result = GetMessage(out var msg, 0, 0, 0)) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            if (result < 0) _log.LogError("Hotkey message loop failed: {Error}", Marshal.GetLastWin32Error());
        }
        catch (Exception e) { _ready.TrySetException(e); _log.LogError(e, "Hotkey thread failed"); }
        finally { foreach (var e in _entries.Values) UnregisterHotKey(_window, e.NativeId); if (_window != 0) DestroyWindow(_window); if (atom != 0) UnregisterClass(name, wc.Instance); }
    }
    public void Dispose()
    {
        lock (_lifetime) { if (_disposed) return; _disposed = true; PostMessage(_window, 0x0010, 0, 0); }
        _thread.Join();
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint hwnd, uint msg, nuint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WndProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Hwnd; public uint Id; public nuint W; public nint L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
