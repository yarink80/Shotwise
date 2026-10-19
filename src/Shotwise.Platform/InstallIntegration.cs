using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Shotwise.Core.Platform;

namespace Shotwise.Platform;

/// <summary>How the running Shotwise.exe was deployed.</summary>
public enum InstallKind
{
    /// <summary>A build output / plain folder (developer run, tests).</summary>
    Development,
    /// <summary>Installed by Shotwise-Setup.exe (Velopack): <c>%LOCALAPPDATA%\Shotwise.App\current\Shotwise.exe</c> next to <c>..\Update.exe</c>.</summary>
    Installed,
    /// <summary>Extracted from the portable zip (Velopack layout plus a <c>.portable</c> marker in the root).</summary>
    Portable,
}

/// <summary>Detects the deployment layout without a Velopack dependency (Velopack itself is referenced only by the App).</summary>
public static class InstallLocation
{
    /// <summary>Velopack package id; the installer puts the app in <c>%LOCALAPPDATA%\{PackageId}</c>.</summary>
    /// <remarks>Deliberately not "Shotwise": <c>%LOCALAPPDATA%\Shotwise</c> holds the catalog, thumbnails, logs and models, and
    /// Velopack's uninstaller deletes its whole install folder.</remarks>
    public const string PackageId = "Shotwise.App";

    public static InstallKind Detect(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return InstallKind.Development;
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        var root = dir is null ? null : Path.GetDirectoryName(dir);
        if (root is null || !File.Exists(Path.Combine(root, "Update.exe")) || !File.Exists(Path.Combine(dir!, "sq.version"))) return InstallKind.Development;
        return File.Exists(Path.Combine(root, ".portable")) ? InstallKind.Portable : InstallKind.Installed;
    }

    /// <summary>The Velopack root folder (parent of <c>current</c>) for an installed or portable exe; null for a development build.</summary>
    public static string? RootOf(string exePath)
        => Detect(exePath) == InstallKind.Development ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(exePath)));

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

/// <summary>Per-user Desktop shortcut; same writer (exe icon + AUMID) as the Start menu shortcut.</summary>
public sealed class DesktopShortcut : IDesktopShortcut
{
    private readonly StartMenuShortcut _link;

    public DesktopShortcut(ILogger<StartMenuShortcut> log) : this(DefaultPath, log) { }

    /// <summary>Test constructor: a shortcut at <paramref name="shortcutPath"/>.</summary>
    public DesktopShortcut(string shortcutPath, ILogger<StartMenuShortcut> log) => _link = new StartMenuShortcut(shortcutPath, log);

    /// <summary><c>Desktop\Shotwise.lnk</c> (follows a OneDrive-redirected Desktop).</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppIdentity.DisplayName + ".lnk");

    public string ShortcutPath => _link.ShortcutPath;
    public bool Exists => File.Exists(ShortcutPath);
    public bool Ensure(string exePath, bool force = false) => _link.Ensure(exePath, force);
    public void Remove() => _link.Remove();
}

/// <summary>
/// Shell integration done by the installer hooks (Velopack fast callbacks in the App's Main, 15–30 s budget, no UI):
/// after install / update, register the per-user file association, URI scheme, image verb, shell extension and shortcuts; before
/// uninstall, stop the running instance and remove all of it. User data (settings, catalog, library, captures) is never touched.
/// Every step is independent: a failing step is reported through <c>log</c> and the next one still runs.
/// </summary>
public static class InstallIntegration
{
    /// <summary>Install (<paramref name="firstInstall"/> = true) or update hook for <paramref name="exePath"/> (the installed exe).</summary>
    public static int Integrate(string exePath, bool firstInstall, Action<string> log, string? startMenuPath = null, string? desktopPath = null)
    {
        var failures = 0;
        var shell = new ShellRegistration(NullLogger<ShellRegistration>.Instance);
        var startMenu = new StartMenuShortcut(startMenuPath ?? StartMenuShortcut.DefaultPath, NullLogger<StartMenuShortcut>.Instance);
        var desktop = new DesktopShortcut(desktopPath ?? DesktopShortcut.DefaultPath, NullLogger<StartMenuShortcut>.Instance);

        Step("file association .shotx, 'Edit in Shotwise' verb, shell extension", () =>
        {
            shell.RegisterFileAssociations();
            return shell.IsShellExtensionRegistered ? "thumbnail/property handler registered" : "shell extension DLL missing - thumbnails unavailable";
        });
        Step("shotwise:// URI scheme", () => { shell.RegisterUriScheme(); return "registered"; });
        // Start menu: one Shotwise.lnk (Velopack's own shortcuts are disabled in tools/package.ps1). A fresh install replaces any
        // older/dev shortcut at the same path; an update only refreshes a shortcut the user kept (Preferences can turn it off).
        Step("Start menu shortcut", () =>
            firstInstall || File.Exists(startMenu.ShortcutPath) ? (startMenu.Ensure(exePath, force: firstInstall) ? "written " : "current ") + startMenu.ShortcutPath : "skipped (turned off)");
        Step("Desktop shortcut", () =>
            firstInstall || desktop.Exists ? (desktop.Ensure(exePath, force: firstInstall) ? "written " : "current ") + desktop.ShortcutPath : "skipped (removed by user)");
        // Autostart is opt-in (Preferences → General); only re-point an existing Run value at this exe.
        Step("autostart Run value", () =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(RunValue) is not string) return "not enabled";
            run.SetValue(RunValue, $"\"{exePath}\" --background");
            return "refreshed";
        });
        return failures;

        void Step(string name, Func<string> action)
        {
            try { log($"{name}: {action()}"); }
            catch (Exception e) { failures++; log($"{name}: FAILED {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// Before-uninstall hook: asks a running Shotwise to exit (graceful, then forced after <paramref name="exitTimeout"/>), then removes
    /// the shell extension, file association, URI scheme, image verb, autostart value, shortcuts that point into
    /// <paramref name="installRoot"/> (or at nothing) and the toast registration. Settings, catalog, library and captures are kept.
    /// </summary>
    public static int Remove(string exePath, string installRoot, Action<string> log, TimeSpan? exitTimeout = null, string? startMenuPath = null, string? desktopPath = null)
    {
        var failures = 0;
        Step("running instance", () => StopRunningInstance(installRoot, exitTimeout ?? TimeSpan.FromSeconds(12)));
        Step("file association, URI scheme, image verb, shell extension", () => { new ShellRegistration(NullLogger<ShellRegistration>.Instance).UnregisterAll(); return "removed"; });
        Step("autostart Run value", () =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(RunValue) is not string) return "not present";
            run.DeleteValue(RunValue, false);
            return "removed";
        });
        Step("Start menu shortcut", () => RemoveOwnedLink(startMenuPath ?? StartMenuShortcut.DefaultPath, installRoot));
        Step("Desktop shortcut", () => RemoveOwnedLink(desktopPath ?? DesktopShortcut.DefaultPath, installRoot));
        Step("toast registration", () =>
        {
            ProcessIdentity.Apply();   // the toolkit derives the registration id from the process AUMID
            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.Uninstall();
            return "removed";
        });
        return failures;

        void Step(string name, Func<string> action)
        {
            try { log($"{name}: {action()}"); }
            catch (Exception e) { failures++; log($"{name}: FAILED {e.GetType().Name}: {e.Message}"); }
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Shotwise";

    /// <summary>Deletes a shortcut only when it points into the install root or at a file that no longer exists (a dev build's link survives).</summary>
    internal static string RemoveOwnedLink(string path, string installRoot)
    {
        if (!File.Exists(path)) return "not present";
        var info = StartMenuShortcut.Read(path);
        if (info is not null && !string.IsNullOrEmpty(info.Target) && File.Exists(info.Target) && !InstallLocation.IsUnder(info.Target, installRoot))
            return "kept (points at another Shotwise: " + info.Target + ")";
        File.Delete(path);
        return "removed " + path;
    }

    /// <summary>Forwards "--exit" to the first instance over the single-instance pipe, then waits; kills what is left.</summary>
    private static string StopRunningInstance(string installRoot, TimeSpan timeout)
    {
        var running = FindInstalledProcesses(installRoot);
        if (running.Count == 0) return "none running";
        var asked = false;
        using (var single = new SingleInstance(NullLogger<SingleInstance>.Instance))
        {
            if (!single.IsFirstInstance) asked = single.ForwardToFirstInstanceAsync(new[] { "--exit" }).GetAwaiter().GetResult();
        }
        var deadline = DateTime.UtcNow + timeout;
        foreach (var p in running)
        {
            var left = deadline - DateTime.UtcNow;
            if (left > TimeSpan.Zero) p.WaitForExit(left);
        }
        var killed = 0;
        foreach (var p in running)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); killed++; } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        return $"{running.Count} running; exit requested: {asked}; closed gracefully: {running.Count - killed}; force-stopped: {killed}";
    }

    private static List<Process> FindInstalledProcesses(string installRoot)
    {
        var self = Environment.ProcessId;
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Shotwise"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (p.Id != self && path is not null && InstallLocation.IsUnder(path, installRoot)) result.Add(p);
            else p.Dispose();
        }
        return result;
    }
}
