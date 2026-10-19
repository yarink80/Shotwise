using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shotwise.App.Hosting;
using Shotwise.App.Tray;
using Shotwise.Core;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Resources;
using Shotwise.UI.Common.Theming;

namespace Shotwise.App;

/// <summary>WPF application: starts the host, theme, tray icon and (unless --background) the main window.</summary>
public partial class App : Application
{
    private readonly IHost _host;
    private readonly CommandLineOptions _options;
    private readonly ILogger<App> _log;

    private readonly Smoke.SmokeOptions? _smoke;

    private readonly IReadOnlyList<string>? _uriArgs;

    private readonly bool _shortcutHandled;

    public App(IHost host, CommandLineOptions options, Smoke.SmokeOptions? smoke = null, IReadOnlyList<string>? uriArgs = null, bool shortcutHandled = false)
    {
        _host = host;
        _options = options;
        _smoke = smoke;
        _uriArgs = uriArgs;
        _shortcutHandled = shortcutHandled;
        _log = host.Services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public IServiceProvider Services => _host.Services;

    private System.Threading.Timer? _smokeWatchdog;
    private Smoke.SmokeRunner? _smokeRunner;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log.LogInformation("Shotwise {Version} starting (args: {Args})", Shotwise.Platform.UpdateService.EntryVersion(), string.Join(' ', e.Args));
        // A smoke run must never hang (CI, SSH sessions without a desktop): overall watchdog → failure report + exit code 2.
        if (_smoke is not null)
            _smokeWatchdog = new System.Threading.Timer(_ => SmokeTimedOut(), null, _smoke.Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        try
        {
            // Startup sequence (P3 B2): [single instance + settings + logging: Program] → host → library → theme → capture UI
            // (hotkeys, widget, clipboard watcher, printer watch folder, mobile inputs) → tray → mobile server → command line.
            var settings = Services.GetRequiredService<ISettingsService>();
            if (settings.Current.Advanced.CleanTempOnStartup) AppHost.CleanTemp(Services.GetRequiredService<IAppPaths>(), _log);

            await _host.StartAsync().ConfigureAwait(true);
            var startup = Services.GetRequiredService<StartupCoordinator>();
            var background = _options.Background || _smoke is not null;
            // The smoke run must never block on the one-time "PrintScreen is used by Windows" question.
            if (_smoke is not null) settings.Update(s => s.Hotkeys.PrintScreenConflictPrompted = true);
            Services.GetRequiredService<IThemeService>().Initialize();
            AppIcon.ApplyToAllWindows();   // every top-level window gets the Shotwise icon (caption, taskbar, Alt+Tab)
            await startup.StartCoreAsync(background).ConfigureAwait(true);

            Services.GetRequiredService<TrayIconManager>().Show();
            startup.StartBackgroundServices();
            startup.MaintainStartMenuShortcut(_smoke is not null, _shortcutHandled);

            var uriImport = Services.GetRequiredService<UriImportHandler>();
            if (Services.GetService<ISingleInstance>() is { } single)
                single.ArgumentsReceived += (_, args) => Dispatcher.InvokeAsync(() =>
                {
                    // Protocol activations are validated by Platform and consume all forwarded arguments.
                    if (uriImport.TryHandle(args.ToList())) return;
                    HandleCommandLine(CommandLineOptions.Parse(args));
                });

            if (startup.LibraryError is { } libErr && !background)
                _ = Dispatcher.InvokeAsync(() => MessageBox.Show(Localizer.ErrorMessage(libErr is ShotwiseException lse ? lse.Code : ErrorCode.LibraryUnavailable) + "\n\n" + libErr.Message,
                    Localizer.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Warning));

            if (_smoke is not null)
            {
                var runner = _smokeRunner = new Smoke.SmokeRunner(Services, this, _smoke);
                var failures = await runner.RunAsync().ConfigureAwait(true);
                _smokeWatchdog?.Dispose();
                Shutdown(failures == 0 ? 0 : 2);
                return;
            }

            if (_uriArgs is not null) { uriImport.TryHandle(_uriArgs); return; }
            if (!_options.Background) HandleCommandLine(_options.HasWork ? _options : _options with { OpenEditor = true });
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Startup failed");
            if (_smoke is not null)
            {
                // No message box in a smoke run (nobody can click it): report and exit 2.
                _smokeWatchdog?.Dispose();
                _smoke.WriteFailure("startup", ex.GetType().Name + ": " + ex.Message, _smokeRunner?.CompletedRows());
                Shutdown(2);
                return;
            }
            MessageBox.Show(Localizer.ErrorMessage(ex is ShotwiseException se ? se.Code : ErrorCode.Unknown) + "\n\n" + ex.Message,
                Localizer.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Smoke watchdog: the run exceeded its limit. Writes the report from another thread and terminates with code 2.</summary>
    private void SmokeTimedOut()
    {
        if (_smoke is null) return;
        var step = _smokeRunner?.CurrentStep;
        var detail = $"smoke run exceeded {_smoke.Timeout.TotalSeconds:0} s" + (step is null ? (_smokeRunner is null ? " during startup" : "") : $" in step '{step}'");
        try { _log.LogCritical("SMOKE watchdog: {Detail}", detail); } catch (Exception) { }
        _smoke.WriteFailure("watchdog", detail, _smokeRunner?.CompletedRows());
        // Environment.Exit normally suffices; if a hung UI thread blocks shutdown, terminate after a grace period.
        var exit = new System.Threading.Thread(() => Environment.Exit(2)) { IsBackground = true };
        exit.Start();
        if (!exit.Join(TimeSpan.FromSeconds(15))) TerminateProcess(GetCurrentProcess(), 2);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Acts on a (possibly forwarded) command line.</summary>
    public void HandleCommandLine(CommandLineOptions o)
    {
        // Forwarded "--exit" (the uninstaller asks the running instance to close; autosaves drain in OnExit).
        if (o.Exit) { ExitApplication(); return; }
        var editorHost = Services.GetService<Core.Documents.IEditorHost>();
        if (o.Files.Count > 0 && editorHost is not null)
        {
            foreach (var f in o.Files) _ = editorHost.OpenFileAsync(f);
            return;
        }
        if (o.Capture && Services.GetService<Core.Capture.ICaptureCoordinator>() is { } coordinator)
        {
            var presets = Services.GetRequiredService<Core.Capture.IPresetRepository>();
            var preset = o.CapturePreset is null ? presets.Active
                : presets.Find(o.CapturePreset) ?? presets.All.FirstOrDefault(p => string.Equals(p.Name, o.CapturePreset, StringComparison.CurrentCultureIgnoreCase)) ?? presets.Active;
            _ = coordinator.RunAsync(preset);
            return;
        }
        if (editorHost is not null) editorHost.ShowEditor();
        else ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var w = Services.GetRequiredService<MainWindow>();
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Real exit (tray "Exit").</summary>
    public void ExitApplication()
    {
        _log.LogInformation("Exit requested");
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services.GetService<TrayIconManager>()?.Dispose();
            // Let in-flight library autosaves / OCR finish before the host disposes the library.
            Task.Run(() => Services.GetRequiredService<CaptureLibraryBridge>().DrainAsync(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException or ObjectDisposedException)
        {
            _log.LogWarning(ex, "Host shutdown did not complete cleanly");
        }
        // Last step: a downloaded update is applied by Update.exe once this process has exited (restarting after "Restart now").
        try { Services.GetService<IUpdateService>()?.OnAppExit(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { _log.LogWarning(ex, "Update apply-on-exit skipped"); }
        _smokeWatchdog?.Dispose();
        _log.LogInformation("Shotwise exited with code {Code}", e.ApplicationExitCode);
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.LogError(e.Exception, "Unhandled UI exception");
        if (_smoke is not null) { e.Handled = true; return; }   // never block a smoke run on a message box
        var code = e.Exception is ShotwiseException se ? se.Code : ErrorCode.Unknown;
        MessageBox.Show(Localizer.ErrorMessage(code), Localizer.Get("App_Name"), MessageBoxButton.OK, code == ErrorCode.Unknown ? MessageBoxImage.Error : MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
