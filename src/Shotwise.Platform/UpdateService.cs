using System.Reflection;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.Platform;

/// <summary>
/// The package manager behind <see cref="UpdateService"/>. The App implements it on Velopack's <c>UpdateManager</c> with a
/// <c>GithubSource</c> (Velopack is referenced by the App only); <see cref="UnsupportedUpdateEngine"/> stands in for development builds.
/// </summary>
public interface IUpdateEngine
{
    /// <summary>Null when this copy can update itself; otherwise a plain-English reason (development build, smoke run, …).</summary>
    string? UnsupportedReason { get; }
    /// <summary>Installed version (SemVer text).</summary>
    string CurrentVersion { get; }
    /// <summary>Human-readable feed location for logs and Preferences.</summary>
    string SourceDescription { get; }
    /// <summary>Checks the feed; null when nothing newer exists. Throws on network/feed errors.</summary>
    Task<UpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct);
    /// <summary>Downloads (delta or full) and verifies the release found by the last <see cref="CheckAsync"/>; progress 0-100.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken ct);
    /// <summary>Version of a downloaded, verified package waiting to be applied, or null.</summary>
    string? PendingVersion { get; }
    /// <summary>Starts the updater, which waits for this process to exit, applies the pending package and starts Shotwise again.</summary>
    void ApplyAndRestart();
    /// <summary>Starts the updater, which waits for this process to exit and applies the pending package silently (no restart).</summary>
    void ApplyOnExit();
}

/// <summary>Engine for copies that are not updated in place.</summary>
public sealed class UnsupportedUpdateEngine(string reason, string? currentVersion = null) : IUpdateEngine
{
    public string? UnsupportedReason { get; } = reason;
    public string CurrentVersion { get; } = currentVersion ?? UpdateService.EntryVersion();
    public string SourceDescription => "GitHub " + UpdatePolicy.GitHubRepositoryUrl.Replace("https://github.com/", "", StringComparison.Ordinal);
    public Task<UpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct) => Task.FromResult<UpdateInfo?>(null);
    public Task DownloadAsync(Action<int> progress, CancellationToken ct) => Task.CompletedTask;
    public string? PendingVersion => null;
    public void ApplyAndRestart() { }
    public void ApplyOnExit() { }
}

/// <summary>Default restart host (no capture/editor known): never busy, nothing to save, exit is a no-op.</summary>
public sealed class NullUpdateRestartHost : IUpdateRestartHost
{
    public bool IsCaptureInProgress => false;
    public Task<bool> SaveOrCloseDocumentsAsync() => Task.FromResult(true);
    public void Exit() { }
}

/// <summary>
/// Update orchestration (PLT-04) over an <see cref="IUpdateEngine"/>: schedule (startup + hourly re-evaluation of
/// "every N days"), ask-first preference, policy <c>AllowUpdates</c>, offline handling, background download gated on
/// non-metered connections (a download stops when the connection becomes metered), and restart/exit application guarded against
/// active captures and unsaved documents. All rules live in <see cref="UpdatePolicy"/>.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private const string PolicyMessage = "Updates are turned off by your organization's policy (AllowUpdates).";
    private const string OfflineMessage = "You are offline. Shotwise will check again later.";
    private readonly IUpdateEngine _engine;
    private readonly ISettingsService _settings;
    private readonly IPolicyService _policy;
    private readonly INetworkStatus _network;
    private readonly IUpdateRestartHost _host;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private UpdateStatus _status;
    private CancellationTokenSource? _scheduler;
    private bool _askedThisSession;
    private UpdateChannel _configuredChannel;
    private volatile bool _restartRequested;
    private int _exitHandled;

    internal TimeSpan StartupDelay { get; set; } = UpdatePolicy.StartupDelay;
    internal TimeSpan TickInterval { get; set; } = UpdatePolicy.TickInterval;
    internal TimeSpan MeteredPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public UpdateService(IUpdateEngine engine, ISettingsService settings, IPolicyService policy, INetworkStatus network, IUpdateRestartHost host, ILogger<UpdateService> log)
        : this(engine, settings, policy, network, host, log, TimeProvider.System) { }

    internal UpdateService(IUpdateEngine engine, ISettingsService settings, IPolicyService policy, INetworkStatus network, IUpdateRestartHost host, ILogger log, TimeProvider clock)
    {
        _engine = engine; _settings = settings; _policy = policy; _network = network; _host = host; _log = log; _clock = clock;
        _status = InitialStatus();
        _configuredChannel = settings.Current.Updates.Channel;
        _settings.Changed += OnSettingsChanged;
        _log.LogInformation("Updater: {State} (version {Version}, channel {Channel}, source {Source}){Reason}", _status.State, _status.CurrentVersion, _status.Channel,
            _status.Source, _status.Message is { } m ? " - " + m : "");
    }

    public UpdateStatus Status { get { lock (_lock) return _status; } }
    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>The entry assembly's informational version without build metadata ("0.9.1").</summary>
    public static string EntryVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = v.IndexOf('+'); return plus > 0 ? v[..plus] : v;
    }

    private bool Supported => _engine.UnsupportedReason is null;

    private UpdateStatus InitialStatus()
    {
        var s = _settings.Current.Updates;
        var status = new UpdateStatus(UpdateState.Idle, _engine.CurrentVersion, s.Channel) { LastCheck = s.LastCheck, Source = _engine.SourceDescription };
        if (_engine.UnsupportedReason is { } reason) return status with { State = UpdateState.NotSupported, Message = reason };
        if (!_policy.IsAllowed(PolicyFeature.Updates)) return status with { State = UpdateState.Disabled, Message = PolicyMessage };
        // Downloaded in an earlier session but not applied yet (e.g. the exit-time apply did not run): offer the restart again.
        if (SafePending() is { } pending && UpdatePolicy.IsNewer(pending, _engine.CurrentVersion))
            return status with { State = UpdateState.ReadyToRestart, Update = new UpdateInfo(pending, null, 0, UpdatePolicy.ChannelOf(pending) == UpdateChannel.Beta), Progress = 100 };
        return status;
    }

    private string? SafePending()
    {
        try { return _engine.PendingVersion; }
        catch (Exception e) { _log.LogWarning(e, "Updater: reading the pending package failed"); return null; }
    }

    private void Set(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus next;
        lock (_lock)
        {
            next = change(_status);
            if (next == _status) return;
            _status = next;
        }
        try { StatusChanged?.Invoke(this, next); }
        catch (Exception e) { _log.LogWarning(e, "Updater: a status listener failed"); }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!e.ChangedSections.Contains(nameof(AppSettings.Updates))) return;
        var u = e.Settings.Updates;
        var switched = u.Channel != _configuredChannel;
        _configuredChannel = u.Channel;
        Set(s =>
        {
            var next = s with { LastCheck = u.LastCheck };
            if (!switched) return next;
            next = next with { Channel = u.Channel };
            // A channel switch invalidates a found-but-not-downloaded release of the other channel.
            if (s.State is UpdateState.Available or UpdateState.UpToDate) next = next with { State = UpdateState.Idle, Update = null };
            return next;
        });
    }

    /// <summary>False (and status Disabled) when policy forbids updates now.</summary>
    private bool PolicyAllows()
    {
        if (_policy.IsAllowed(PolicyFeature.Updates))
        {
            if (Status.State == UpdateState.Disabled) Set(s => s with { State = UpdateState.Idle, Message = null });
            return true;
        }
        Set(s => s with { State = UpdateState.Disabled, Message = PolicyMessage, Update = null });
        return false;
    }

    // ------------------------------------------------------------------ schedule

    public void StartScheduler()
    {
        if (!Supported) return;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_scheduler is not null) return;
            _scheduler = cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => ScheduleLoopAsync(cts.Token));
    }

    private async Task ScheduleLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, _clock, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try { await RunScheduledOnceAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "Updater: scheduled check failed"); }
                await Task.Delay(TickInterval, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One schedule tick: decide, then check and (when allowed) download in the background.</summary>
    internal async Task RunScheduledOnceAsync(CancellationToken ct)
    {
        if (!Supported || !PolicyAllows()) return;
        if (Status.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart or UpdateState.AwaitingConsent) return;
        var prefs = _settings.Current.Updates;
        switch (UpdatePolicy.DecideScheduled(prefs, _clock.GetUtcNow(), _askedThisSession))
        {
            case ScheduledCheckDecision.Skip:
                return;
            case ScheduledCheckDecision.AskFirst:
                _askedThisSession = true;
                _log.LogInformation("Updater: a check is due; asking first (Ask before checking is on)");
                Set(s => s with { State = UpdateState.AwaitingConsent, Message = null });
                return;
        }
        if (!_network.IsOnline)
        {
            Set(s => s with { State = UpdateState.Failed, Offline = true, Message = OfflineMessage });
            return;   // LastCheck unchanged: the next tick tries again
        }
        if (await CheckCoreAsync(null, ct, scheduled: true).ConfigureAwait(false) is null) return;
        await DownloadInBackgroundIfAllowedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>After a check found a release: download it when background download is on and the connection is not metered.</summary>
    internal async Task DownloadInBackgroundIfAllowedAsync(CancellationToken ct)
    {
        var prefs = _settings.Current.Updates;
        var metered = _network.IsMetered;
        if (!UpdatePolicy.ShouldDownloadInBackground(prefs, metered))
        {
            _log.LogInformation("Updater: {Version} found; not downloading in the background ({Reason})", Status.Update?.Version, metered ? "metered connection" : "background download is off");
            if (metered) Set(s => s with { Message = "Metered connection: the update downloads when you choose Download." });
            return;
        }
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = new Timer(_ => { if (_network.IsMetered) { _log.LogInformation("Updater: connection became metered; pausing the download"); watch.Cancel(); } },
            null, MeteredPollInterval, MeteredPollInterval);
        try { await DownloadCoreAsync(null, watch.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Set(s => s with { Message = "Download paused: the connection is metered." });
        }
    }

    // ------------------------------------------------------------------ check / download

    public async Task<UpdateInfo?> CheckAsync(bool userApproved, CancellationToken ct, UpdateChannel? channel = null)
    {
        if (!Supported || !PolicyAllows()) return null;
        if (!userApproved)
        {
            switch (UpdatePolicy.DecideScheduled(_settings.Current.Updates, _clock.GetUtcNow(), _askedThisSession))
            {
                case ScheduledCheckDecision.Skip: return null;
                case ScheduledCheckDecision.AskFirst:
                    _askedThisSession = true;
                    Set(s => s with { State = UpdateState.AwaitingConsent });
                    return null;
            }
        }
        return await CheckCoreAsync(channel, ct, scheduled: !userApproved).ConfigureAwait(false);
    }

    private async Task<UpdateInfo?> CheckCoreAsync(UpdateChannel? channel, CancellationToken ct, bool scheduled)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status is { State: UpdateState.ReadyToRestart, Update: { } ready }) return ready;   // restart first; then look further
            var ch = channel ?? _settings.Current.Updates.Channel;
            var before = Status;
            Set(s => s with { State = UpdateState.Checking, Channel = ch, Message = null, Offline = false, Progress = 0, Update = null, Scheduled = scheduled });
            UpdateInfo? found;
            try { found = await _engine.CheckAsync(UpdatePolicy.IncludePrereleases(ch), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Set(_ => before);
                throw;
            }
            catch (Exception e)
            {
                var offline = !_network.IsOnline;
                _log.LogWarning(e, "Updater: check failed ({Source})", _engine.SourceDescription);
                Set(s => s with { State = UpdateState.Failed, Offline = offline, Message = offline ? OfflineMessage : "Could not reach the update server: " + e.Message });
                return null;
            }
            var now = _clock.GetUtcNow();
            _settings.Update(v => v.Updates.LastCheck = now);
            if (found is null || !UpdatePolicy.IsNewer(found.Version, _engine.CurrentVersion))
            {
                _log.LogInformation("Updater: up to date ({Version}, {Channel})", _engine.CurrentVersion, ch);
                Set(s => s with { State = UpdateState.UpToDate, LastCheck = now, Update = null });
                return null;
            }
            _log.LogInformation("Updater: {Version} available ({Channel}, {Size:N0} bytes)", found.Version, ch, found.SizeBytes);
            Set(s => s with { State = UpdateState.Available, LastCheck = now, Update = found });
            return found;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!Supported || !PolicyAllows()) return false;
        return await DownloadCoreAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task<bool> DownloadCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Status;
            if (current.State == UpdateState.ReadyToRestart) return true;
            if (current.Update is null || current.State is not (UpdateState.Available or UpdateState.Failed)) return false;
            Set(s => s with { State = UpdateState.Downloading, Progress = 0, Message = null, Offline = false });
            try
            {
                await _engine.DownloadAsync(p =>
                {
                    p = Math.Clamp(p, 0, 100);
                    progress?.Report(p / 100.0);
                    Set(s => s.State == UpdateState.Downloading ? s with { Progress = p } : s);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Set(s => s with { State = UpdateState.Available, Progress = 0 });
                throw;
            }
            catch (Exception e)
            {
                var offline = !_network.IsOnline;
                _log.LogWarning(e, "Updater: download of {Version} failed", current.Update.Version);
                Set(s => s with { State = UpdateState.Failed, Offline = offline, Message = offline ? OfflineMessage : "The download failed: " + e.Message });
                return false;
            }
            _log.LogInformation("Updater: {Version} downloaded and verified; it applies on restart or when Shotwise exits", current.Update.Version);
            Set(s => s with { State = UpdateState.ReadyToRestart, Progress = 100 });
            return true;
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ apply

    public async Task<UpdateRestartResult> RestartToApplyAsync()
    {
        var ready = Status.State == UpdateState.ReadyToRestart && Supported;
        if (UpdatePolicy.RestartBlock(ready, _host.IsCaptureInProgress) is { } blocked)
        {
            _log.LogInformation("Updater: restart refused ({Reason})", blocked);
            return blocked;
        }
        if (!await _host.SaveOrCloseDocumentsAsync().ConfigureAwait(true))
        {
            _log.LogInformation("Updater: restart cancelled at the save prompt");
            return UpdateRestartResult.Cancelled;
        }
        // A capture may have started while the save prompt was open.
        if (_host.IsCaptureInProgress) return UpdateRestartResult.BlockedByRecording;
        _log.LogInformation("Updater: restarting to apply {Version}", Status.Update?.Version);
        _restartRequested = true;
        _host.Exit();
        return UpdateRestartResult.Restarting;
    }

    public void OnAppExit()
    {
        if (Interlocked.Exchange(ref _exitHandled, 1) == 1) return;
        StopScheduler();
        if (!Supported || !_policy.IsAllowed(PolicyFeature.Updates)) return;
        var pending = SafePending();
        if (pending is null || !UpdatePolicy.IsNewer(pending, _engine.CurrentVersion)) return;
        try
        {
            if (_restartRequested)
            {
                _log.LogInformation("Updater: applying {Version} and restarting", pending);
                _engine.ApplyAndRestart();
            }
            else
            {
                _log.LogInformation("Updater: applying {Version} silently after exit", pending);
                _engine.ApplyOnExit();
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Updater: starting the update failed; it stays pending"); }
    }

    private void StopScheduler()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _scheduler; _scheduler = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        StopScheduler();
    }
}
