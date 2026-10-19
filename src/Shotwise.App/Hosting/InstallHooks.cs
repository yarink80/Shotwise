using System.IO;
using Shotwise.Core.Platform;
using Shotwise.Platform;
using Velopack;

namespace Shotwise.App.Hosting;

/// <summary>
/// Velopack bootstrap (first call in <see cref="Program.Main"/>). Setup.exe / Update.exe start Shotwise.exe with
/// <c>--veloapp-install|updated|obsolete|uninstall &lt;version&gt;</c>; <see cref="VelopackApp.Run"/> runs the matching fast callback
/// below and exits the process (no UI, no host, 15–30 s budget). For every other start it returns immediately.
/// Each hook appends what it did to <c>%LOCALAPPDATA%\Shotwise\logs\install.log</c>.
/// Update checks/downloads are not done here: <see cref="VelopackUpdateEngine"/> + <c>Shotwise.Platform.UpdateService</c> (docs/UPDATES.md).
/// </summary>
internal static class InstallHooks
{
    /// <summary>Set when Velopack restarted Shotwise right after applying an update (the new version), otherwise null.</summary>
    public static string? RestartedAfterUpdateTo { get; private set; }

    /// <summary>The Velopack builder with Shotwise's hooks; <see cref="Program.Main"/> calls <c>.Run()</c> on it as its first statement.</summary>
    public static VelopackApp Configure(string[] args) => VelopackApp.Build()
        .SetArgs(args)
        // Same id as ProcessIdentity / the Start menu shortcut / toasts (Velopack would otherwise use "velopack.Shotwise.App").
        .SetAppUserModelId(AppIdentity.AppUserModelId)
        // A downloaded update is applied only by "Restart now" or after Shotwise exits - never by surprise at the next start.
        .SetAutoApplyOnStartup(false)
        .OnRestarted(v => { RestartedAfterUpdateTo = v.ToString(); Log("restarted", "running " + v + " after an update"); })
        .OnAfterInstallFastCallback(v => Integrate("after-install " + v, firstInstall: true))
        .OnAfterUpdateFastCallback(v => Integrate("after-update " + v, firstInstall: false))
        .OnBeforeUninstallFastCallback(v => Uninstall("before-uninstall " + v));

    private static void Integrate(string hook, bool firstInstall)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Shotwise.exe");
        Log(hook, $"exe {exe}");
        var failures = InstallIntegration.Integrate(exe, firstInstall, line => Log(hook, line));
        Log(hook, failures == 0 ? "done" : $"done with {failures} failed step(s)");
    }

    private static void Uninstall(string hook)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Shotwise.exe");
        var root = InstallLocation.RootOf(exe) ?? Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? AppContext.BaseDirectory;
        Log(hook, $"exe {exe}; install root {root}");
        // 30 s budget: 12 s for a graceful exit of the running instance, the rest is registry/file work.
        var failures = InstallIntegration.Remove(exe, root, line => Log(hook, line), TimeSpan.FromSeconds(12));
        var paths = new AppPaths();
        Log(hook, $"user data kept: settings {paths.AppData}; catalog/logs/models {paths.LocalAppData}; library {paths.DefaultLibraryRoot}");
        Log(hook, failures == 0 ? "done" : $"done with {failures} failed step(s)");
    }

    private static void Log(string hook, string line)
    {
        try
        {
            var dir = new AppPaths().Logs;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "install.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{hook}] {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
