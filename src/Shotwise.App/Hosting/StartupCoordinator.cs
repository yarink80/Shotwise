using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.App.Hosting;

/// <summary>
/// Runtime startup sequence after the host started (P3 B2): library → capture-event bridge → capture UI (hotkeys from
/// settings + presets, capture window/widget, clipboard watcher, printer watch folder, mobile FileReceived) → [tray, by App]
/// → mobile server (if enabled) → scheduled update check. Every step is isolated: a failing step is logged and the app continues.
/// </summary>
public sealed class StartupCoordinator
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly ILogger<StartupCoordinator> _log;

    public StartupCoordinator(IServiceProvider services, ISettingsService settings, ILogger<StartupCoordinator> log)
    {
        _services = services;
        _settings = settings;
        _log = log;
    }

    /// <summary>Completes when <see cref="ILibraryService.InitializeAsync"/> finished (faulted when the library is unavailable).</summary>
    public Task LibraryReady { get; private set; } = Task.CompletedTask;

    /// <summary>Library problem detected at startup (shown to the user by the App), or null.</summary>
    public Exception? LibraryError { get; private set; }

    /// <summary>Steps before the tray icon. Must run on the UI thread (capture UI creates windows/hotkey bindings).</summary>
    public async Task StartCoreAsync(bool background)
    {
        // 1. Library (catalog open/migrate/reconcile + watcher). Captures wait for it through the bridge.
        var library = _services.GetRequiredService<ILibraryService>();
        LibraryReady = Task.Run(() => library.InitializeAsync());
        _services.GetRequiredService<CaptureLibraryBridge>().Start(LibraryReady);
        try
        {
            await LibraryReady.ConfigureAwait(true);
            _log.LogInformation("Library ready at {Root}", library.LibraryRoot);
        }
        catch (Exception ex)
        {
            LibraryError = ex;
            _log.LogError(ex, "Library initialization failed ({Code})", ex is ShotwiseException se ? se.Code : ErrorCode.Unknown);
        }

        // 2. Capture UI: global hotkeys (settings + preset hotkeys), implicit inputs, capture window / widget per settings.
        try { Shotwise.UI.Capture.CaptureUiStartup.Start(_services, showWindows: !background); }
        catch (Exception ex) { _log.LogError(ex, "Capture UI startup failed"); }
    }

    /// <summary>
    /// True when this run must not touch the user's shell (smoke runs, or <c>SHOTWISE_DATA_ROOT</c> under %TEMP% = test isolation).
    /// </summary>
    public static bool IsIsolatedRun(bool smoke)
    {
        if (smoke) return true;
        var root = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    /// <summary>Creates or refreshes the Start menu shortcut for the running exe (returns false when it could not be written).</summary>
    public static bool EnsureStartMenuShortcut(IServiceProvider services, bool force = false)
    {
        var log = services.GetRequiredService<ILogger<StartupCoordinator>>();
        if (services.GetService<IStartMenuShortcut>() is not { } shortcut || Environment.ProcessPath is not { } exe) return false;
        try
        {
            var written = shortcut.Ensure(exe, force);
            if (!written) log.LogDebug("Start menu shortcut is current: {Path}", shortcut.ShortcutPath);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            log.LogWarning(ex, "Start menu shortcut could not be written ({Path})", shortcut.ShortcutPath);
            return false;
        }
    }

    /// <summary>
    /// Startup policy for the Start menu shortcut: create it when missing or stale (target path changed) unless the user turned it off
    /// (Preferences → Advanced) or this is an isolated test/smoke run. Runs off the UI thread.
    /// </summary>
    public void MaintainStartMenuShortcut(bool smoke, bool alreadyHandled)
    {
        if (alreadyHandled) return;
        if (IsIsolatedRun(smoke)) { _log.LogInformation("Start menu shortcut skipped (isolated run)"); return; }
        if (!_settings.Current.Advanced.ShowInStartMenu) return;
        var exe = Environment.ProcessPath;
        switch (Shotwise.Platform.InstallLocation.Detect(exe))
        {
            case Shotwise.Platform.InstallKind.Portable:
                // A portable copy leaves the Start menu alone (use --register-shortcut to opt in).
                _log.LogInformation("Start menu shortcut skipped (portable copy)");
                return;
            case Shotwise.Platform.InstallKind.Development when ShortcutBelongsToInstalledCopy(exe):
                // Only one Shotwise entry in Start: a dev/build-output run never re-points the installed app's shortcut.
                _log.LogInformation("Start menu shortcut kept (it opens the installed Shotwise)");
                return;
        }
        _ = Task.Run(() => EnsureStartMenuShortcut(_services));
    }

    private bool ShortcutBelongsToInstalledCopy(string? exe)
    {
        if (_services.GetService<IStartMenuShortcut>() is not { } shortcut) return false;
        var target = Shotwise.Platform.StartMenuShortcut.Read(shortcut.ShortcutPath)?.Target;
        return !string.IsNullOrEmpty(target) && File.Exists(target)
            && !string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)
            && Shotwise.Platform.InstallLocation.Detect(target) == Shotwise.Platform.InstallKind.Installed;
    }

    /// <summary>Steps after the tray icon: mobile server and the automatic update check.</summary>
    public void StartBackgroundServices()
    {
        // 3. Mobile connect server (PLT-05) only when the user enabled it and policy allows it.
        var s = _settings.Current;
        if (s.Mobile.AllowSharing && _services.GetService<IMobileConnectServer>() is { } mobile
            && _services.GetService<IPolicyService>()?.IsAllowed(PolicyFeature.MobileSharing) != false)
        {
            _ = Task.Run(async () =>
            {
                try { await mobile.StartAsync(CancellationToken.None).ConfigureAwait(false); _log.LogInformation("Mobile connect server listening on port {Port}", mobile.Port); }
                catch (Exception ex) { _log.LogWarning(ex, "Mobile connect server could not start"); }
            });
        }

        // 4. Updates (installed copies only): toasts for the updater's state, then the startup + periodic schedule (30 s after
        //    startup, re-evaluated hourly against "days between checks"; ask-first, policy and metered rules in UpdatePolicy).
        try
        {
            _services.GetService<UpdateNotifier>()?.Start();
            _services.GetService<IUpdateService>()?.StartScheduler();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Update scheduler could not start"); }
    }
}
