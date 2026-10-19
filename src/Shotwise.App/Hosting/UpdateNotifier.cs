using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.Platform;
using Shotwise.UI.Capture;

namespace Shotwise.App.Hosting;

/// <summary>App-side <see cref="IUpdateRestartHost"/>: capture/recording guard, editor save prompts, graceful exit.</summary>
internal sealed class AppUpdateRestartHost(IServiceProvider services) : IUpdateRestartHost
{
    // IsCapturing covers the selection overlay, countdown and an active (or paused) recording.
    public bool IsCaptureInProgress => services.GetService<ICaptureCoordinator>()?.IsCapturing == true;

    public Task<bool> SaveOrCloseDocumentsAsync() => services.GetService<IEditorHost>()?.CloseForExitAsync() ?? Task.FromResult(true);

    public void Exit() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => (Application.Current as App)?.ExitApplication()));
}

/// <summary>
/// Turns updater state into Windows toasts (when toasts are enabled): "Shotwise X is ready — Restart now / Later" after a background
/// download, "available" on a metered connection, the ask-first prompt, and "updated to X" after a restart. The editor shows the
/// same "ready" state as an InfoBar and the tray menu as "Restart to update"; both work when toasts are off.
/// </summary>
internal sealed class UpdateNotifier : IDisposable
{
    private readonly IUpdateService _updates;
    private readonly IServiceProvider _services;
    private readonly ILogger<UpdateNotifier> _log;
    private string? _notifiedReady, _notifiedAvailable;
    private bool _notifiedConsent, _started;

    public UpdateNotifier(IUpdateService updates, IServiceProvider services, ILogger<UpdateNotifier> log)
    {
        _updates = updates;
        _services = services;
        _log = log;
    }

    private INotificationService? Toasts => _services.GetService<INotificationService>();

    public void Start()
    {
        if (_started) return;
        _started = true;
        _updates.StatusChanged += OnStatus;
        if (InstallHooks.RestartedAfterUpdateTo is { } v)
        {
            _log.LogInformation("Updated to {Version}", v);
            Toasts?.Show(CS.Get("Update_UpdatedTitle"), CS.Format("Update_UpdatedBody", v), NotificationKind.Success);
        }
        else if (_updates.Status is { State: UpdateState.ReadyToRestart, Update: { } pending }) NotifyReady(pending.Version);   // downloaded in an earlier session
    }

    private void OnStatus(object? sender, UpdateStatus s)
    {
        try { Notify(s); }
        catch (Exception e) { _log.LogWarning(e, "Update notification failed"); }
    }

    private void Notify(UpdateStatus s)
    {
        switch (s.State)
        {
            case UpdateState.ReadyToRestart when s.Scheduled && s.Update is { } u:
                // Explicit checks (Help menu / Preferences) ask in their own dialog; the toast is for background downloads.
                NotifyReady(u.Version);
                break;
            case UpdateState.Available when s.Scheduled && s.Update is { } a && a.Version != _notifiedAvailable && s.Message is not null:
                // Found by the schedule but not downloaded (metered connection).
                _notifiedAvailable = a.Version;
                Toasts?.Show(CS.Format("Update_AvailableTitle", a.Version), CS.Get("Update_AvailableBody"), NotificationKind.Info,
                    [new NotificationAction(CS.Get("Update_Download"), () => _ = DownloadAsync())]);
                break;
            case UpdateState.AwaitingConsent when !_notifiedConsent:
                _notifiedConsent = true;
                Toasts?.Show(CS.Get("Update_ConsentTitle"), CS.Get("Update_ConsentBody"), NotificationKind.Info,
                    [new NotificationAction(CS.Get("Update_CheckNow"), () => _ = ApprovedCheckAsync()), new NotificationAction(CS.Get("Update_Later"), () => { })]);
                break;
        }
    }

    private void NotifyReady(string version)
    {
        if (version == _notifiedReady) return;
        _notifiedReady = version;
        Toasts?.Show(CS.Format("Update_ReadyTitle", version), CS.Get("Update_ReadyBody"), NotificationKind.Success,
            [new NotificationAction(CS.Get("Update_RestartNow"), RestartNow), new NotificationAction(CS.Get("Update_Later"), () => { })]);
    }

    /// <summary>"Restart now" from a toast or the tray menu (marshalled to the UI thread; guarded by the update service).</summary>
    public void RestartNow() => Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
    {
        try
        {
            if (await _updates.RestartToApplyAsync() == UpdateRestartResult.BlockedByRecording)
                Toasts?.Show(CS.Get("Update_BlockedTitle"), CS.Get("Update_BlockedBody"), NotificationKind.Warning);
        }
        catch (Exception e) { _log.LogWarning(e, "Restart to update failed"); }
    }));

    private async Task DownloadAsync()
    {
        try
        {
            if (await _updates.DownloadAsync(null, CancellationToken.None) && _updates.Status.Update is { } u) NotifyReady(u.Version);
        }
        catch (Exception e) { _log.LogInformation(e, "Update download from the toast failed"); }
    }

    /// <summary>The user approved the scheduled check ("Ask before checking" is on): check, then download per preferences.</summary>
    private async Task ApprovedCheckAsync()
    {
        try
        {
            if (await _updates.CheckAsync(userApproved: true, CancellationToken.None) is null) return;
            var prefs = _services.GetRequiredService<ISettingsService>().Current.Updates;
            var metered = _services.GetService<INetworkStatus>()?.IsMetered ?? true;
            if (UpdatePolicy.ShouldDownloadInBackground(prefs, metered)) await DownloadAsync();
            else if (_updates.Status.Update is { } a)
                Toasts?.Show(CS.Format("Update_AvailableTitle", a.Version), CS.Get("Update_AvailableBody"), NotificationKind.Info,
                    [new NotificationAction(CS.Get("Update_Download"), () => _ = DownloadAsync())]);
        }
        catch (Exception e) { _log.LogInformation(e, "Approved update check failed"); }
    }

    public void Dispose() => _updates.StatusChanged -= OnStatus;
}
