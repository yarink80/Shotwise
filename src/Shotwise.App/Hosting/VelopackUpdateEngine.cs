using System.IO;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;
using Shotwise.Platform;
using Velopack;
using Velopack.Sources;
using CoreUpdateInfo = Shotwise.Core.Platform.UpdateInfo;

namespace Shotwise.App.Hosting;

/// <summary>
/// <see cref="IUpdateEngine"/> on Velopack's <see cref="UpdateManager"/>. The feed is the GitHub Releases of
/// <see cref="UpdatePolicy.GitHubRepositoryUrl"/> (<see cref="GithubSource"/>, anonymous, <c>prerelease</c> = Beta channel). Velopack
/// downloads the delta or full <c>.nupkg</c> into <c>%LOCALAPPDATA%\Shotwise.App\packages</c> and verifies its SHA-256/SHA-1 against
/// <c>releases.win.json</c> of the same release before it can be applied; Update.exe applies it after Shotwise exits.
/// <para>
/// Testing: <c>SHOTWISE_UPDATE_SOURCE=&lt;local folder&gt;</c> (an absolute, existing local folder holding a vpk output:
/// <c>releases.win.json</c> + <c>*.nupkg</c>) replaces GitHub with a <see cref="SimpleFileSource"/>. It is an explicit opt-in for
/// update testing; it is logged as a warning and shown as the update source in Preferences.
/// </para>
/// </summary>
internal sealed class VelopackUpdateEngine : IUpdateEngine
{
    private readonly ILogger _log;
    private readonly string? _localSource;
    private readonly object _lock = new();
    private UpdateManager? _manager;
    private bool _managerPrerelease;
    private Velopack.UpdateInfo? _found;

    private VelopackUpdateEngine(ILogger log, string? localSource)
    {
        _log = log;
        _localSource = localSource;
        _manager = CreateManager(false);
        _managerPrerelease = false;
    }

    /// <summary>
    /// The engine for this process: Velopack when running from an installed (or portable) Velopack layout; otherwise an
    /// <see cref="UnsupportedUpdateEngine"/> (development build, <c>--smoke</c>, or <c>SHOTWISE_DATA_ROOT</c> test isolation).
    /// </summary>
    public static IUpdateEngine Create(bool smoke, ILogger log)
    {
        var version = UpdateService.EntryVersion();
        if (smoke) return new UnsupportedUpdateEngine("Updates are off in smoke-test runs (--smoke).", version);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Shotwise.Core.Platform.AppPaths.DataRootEnvironmentVariable)))
            return new UnsupportedUpdateEngine("Updates are off while SHOTWISE_DATA_ROOT redirects Shotwise's data (test isolation).", version);
        var kind = InstallLocation.Detect(Environment.ProcessPath);
        if (kind == InstallKind.Development)
            return new UnsupportedUpdateEngine("This is a development build. Updates apply to Shotwise installed with Shotwise-Setup.exe.", version);

        var raw = Environment.GetEnvironmentVariable(UpdatePolicy.LocalSourceVariable);
        var local = UpdatePolicy.ValidateLocalSource(raw);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (local is null) log.LogWarning("Updater: {Variable} ignored (must be an absolute, existing local folder): {Value}", UpdatePolicy.LocalSourceVariable, raw);
            else log.LogWarning("Updater: TEST FEED - {Variable} replaces GitHub with the local folder {Folder}", UpdatePolicy.LocalSourceVariable, local);
        }
        try
        {
            var engine = new VelopackUpdateEngine(log, local);
            if (engine._manager is not { IsInstalled: true })
                return new UnsupportedUpdateEngine("Velopack does not recognise this folder as an installed Shotwise.", version);
            return engine;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Updater: Velopack could not be initialised");
            return new UnsupportedUpdateEngine("The updater could not start: " + e.Message, version);
        }
    }

    public string? UnsupportedReason => null;

    public string CurrentVersion => Manager().CurrentVersion?.ToString() ?? UpdateService.EntryVersion();

    public string SourceDescription => _localSource is { } dir
        ? "local test folder " + dir + " (" + UpdatePolicy.LocalSourceVariable + ")"
        : "GitHub " + UpdatePolicy.GitHubRepositoryUrl.Replace("https://github.com/", "", StringComparison.Ordinal);

    private UpdateManager CreateManager(bool prerelease)
    {
        IUpdateSource source = _localSource is { } dir
            ? new SimpleFileSource(new DirectoryInfo(dir))
            : new GithubSource(UpdatePolicy.GitHubRepositoryUrl, accessToken: null, prerelease: prerelease);
        // Never downgrade: a Beta user switching back to Stable stays on the beta until a newer stable release exists.
        return new UpdateManager(source, new UpdateOptions { AllowVersionDowngrade = false });
    }

    private UpdateManager Manager(bool? prerelease = null)
    {
        lock (_lock)
        {
            if (_manager is null || (prerelease is { } p && p != _managerPrerelease))
            {
                _manager = CreateManager(prerelease ?? false);
                _managerPrerelease = prerelease ?? false;
                _found = null;
            }
            return _manager;
        }
    }

    public async Task<CoreUpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct)
    {
        var manager = Manager(includePrereleases);
        // CheckForUpdatesAsync takes no token; abandon the wait on cancellation (the HTTP request times out on its own).
        var info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        lock (_lock) _found = info;
        if (info is null) return null;
        var target = info.TargetFullRelease;
        var deltas = info.DeltasToTarget ?? [];
        long size = deltas.Length > 0 && info.BaseRelease is not null ? deltas.Sum(d => d.Size) : target.Size;
        var version = target.Version.ToString();
        _log.LogInformation("Updater: feed offers {Version} ({Deltas} delta package(s), {Size:N0} bytes)", version, deltas.Length, size);
        return new CoreUpdateInfo(version, target.NotesMarkdown, size, target.Version.IsPrerelease);
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
    {
        Velopack.UpdateInfo found;
        UpdateManager manager;
        lock (_lock)
        {
            found = _found ?? throw new InvalidOperationException("Check for updates first.");
            manager = _manager!;
        }
        await manager.DownloadUpdatesAsync(found, progress, ct).ConfigureAwait(false);
    }

    public string? PendingVersion => Manager().UpdatePendingRestart?.Version.ToString();

    public void ApplyAndRestart()
    {
        // Same as UpdateManager.ApplyUpdatesAndRestart, minus its immediate Environment.Exit: Update.exe waits for this process,
        // which the App is already closing gracefully (library autosaves drained, host stopped and disposed).
        var manager = Manager();
        manager.WaitExitThenApplyUpdates(manager.UpdatePendingRestart, silent: false, restart: true, restartArgs: []);
    }

    public void ApplyOnExit()
    {
        var manager = Manager();
        manager.WaitExitThenApplyUpdates(manager.UpdatePendingRestart, silent: true, restart: false, restartArgs: []);
    }
}
