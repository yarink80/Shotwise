using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.App.Hosting;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Resources;

namespace Shotwise.App;

public static class Program
{
    private const string MutexName = @"Local\Shotwise.SingleInstance";

    /// <summary>"--register-shortcut": create/refresh the Start menu shortcut even in isolated (test) runs; alone = one-shot, then exit.</summary>
    public const string RegisterShortcutSwitch = "--register-shortcut";

    [STAThread]
    public static int Main(string[] args)
    {
        // Installer hooks first (Velopack): "--veloapp-*" invocations register/unregister the shell integration and exit here.
        InstallHooks.Configure(args).Run();
        // Explicit AppUserModelID before any window, tray icon or toast: taskbar grouping, toasts and the Start menu shortcut share it.
        Shotwise.Platform.ProcessIdentity.Apply();
        // Hidden, opt-in integration smoke mode (P3 C2): "--smoke [--smoke-out=<dir>] [--smoke-screenshots=<dir>]".
        var smoke = Smoke.SmokeOptions.TryParse(ref args);
        bool registerShortcut = args.Any(a => string.Equals(a, RegisterShortcutSwitch, StringComparison.OrdinalIgnoreCase));
        if (registerShortcut) args = args.Where(a => !string.Equals(a, RegisterShortcutSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        // shotwise: protocol activations consume the WHOLE command line (FX-3 contract): never parse switches/files from it.
        var uriArgs = UriImportHandler.IsActivation(args) ? args : null;
        var options = uriArgs is not null ? new CommandLineOptions { Background = true } : CommandLineOptions.Parse(args);
        var paths = new AppPaths();
        paths.EnsureCreated();
        var settings = new SettingsService(paths);
        Localizer.ApplyLanguage(settings.Current.General.Language);

        Microsoft.Extensions.Hosting.IHost builtHost;
        try { builtHost = AppHost.Build(args, paths, settings, smoke is not null); }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException)
        {
            // DI validation (ValidateOnBuild/ValidateScopes) failed: record it next to the logs and fail fast.
            try { Shotwise.Core.Serialization.AtomicFile.WriteAllText(System.IO.Path.Combine(paths.Logs, "startup-error.txt"), ex.ToString()); } catch (System.IO.IOException) { }
            if (smoke is not null) { smoke.WriteFailure("host-build", ex.GetType().Name + ": " + ex.Message); return 2; }
            System.Windows.MessageBox.Show(ex.Message, "Shotwise", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 3;
        }
        using var host = builtHost;

        if (registerShortcut)
        {
            var ok = StartupCoordinator.EnsureStartMenuShortcut(host.Services, force: true);
            // One-shot ("Shotwise.exe --register-shortcut"): nothing else to do.
            if (args.Length == 0 && smoke is null) return ok ? 0 : 4;
        }

        // Single instance: prefer the Platform implementation (mutex + named-pipe forwarding); fall back to a plain mutex.
        Mutex? fallbackMutex = null;
        var single = host.Services.GetService<ISingleInstance>();
        if (single is not null)
        {
            if (!single.IsFirstInstance)
            {
                if (smoke is not null)
                {
                    // A smoke run needs its own process: report instead of silently forwarding to the running Shotwise.
                    smoke.WriteFailure("single-instance", "another Shotwise instance is already running; exit it (Shotwise.exe --exit) and retry");
                    return 2;
                }
                single.ForwardToFirstInstanceAsync(args).GetAwaiter().GetResult();
                return 0;
            }
        }
        else
        {
            fallbackMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                fallbackMutex.Dispose();
                return 0;
            }
        }
        // "--exit" (uninstaller / scripts) with nothing running: do not start the app.
        if (options.Exit) { fallbackMutex?.ReleaseMutex(); fallbackMutex?.Dispose(); return 0; }

        try
        {
            var app = new App(host, options, smoke, uriArgs, shortcutHandled: registerShortcut);
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            if (fallbackMutex is not null)
            {
                fallbackMutex.ReleaseMutex();
                fallbackMutex.Dispose();
            }
        }
    }
}
