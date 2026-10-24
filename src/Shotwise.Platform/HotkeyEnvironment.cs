using System.Diagnostics;
using Microsoft.Win32;
using Shotwise.Core.Platform;

namespace Shotwise.Platform;

/// <summary>Running processes and per-user registry values for the likely-owner heuristics (<see cref="KnownHotkeyOwners"/>). Read-only.</summary>
public sealed class WindowsHotkeyEnvironment : IHotkeyEnvironment
{
    public IReadOnlyCollection<string> RunningProcessNames()
    {
        var self = Environment.ProcessId;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.Id != self) names.Add(p.ProcessName); }
            catch (Exception) { /* exited meanwhile */ }
            finally { p.Dispose(); }
        }
        return names;
    }

    public object? ReadUserValue(string subKey, string name)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(subKey); return key?.GetValue(name); }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<string> UserSubKeys(string subKey)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(subKey); return key?.GetSubKeyNames() ?? Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> UserValueNames(string subKey)
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(subKey); return key?.GetValueNames() ?? Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
