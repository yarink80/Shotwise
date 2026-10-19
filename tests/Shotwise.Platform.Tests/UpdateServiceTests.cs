using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Xunit;

namespace Shotwise.Platform.Tests;

/// <summary>Update policy (schedule, metered, guards, channel mapping) and the orchestration over a fake Velopack engine.</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Shotwise-Updates-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ------------------------------------------------------------------ fakes

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = UpdateServiceTests.Now; public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeEngine : IUpdateEngine
    {
        public string? UnsupportedReason { get; set; }
        public string CurrentVersion { get; set; } = "0.9.1";
        public string SourceDescription => "fake feed";
        public UpdateInfo? Offer { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public bool? LastPrerelease { get; private set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public string? PendingVersion { get; set; }
        public int Restarts, ExitApplies;
        public TaskCompletionSource? DownloadGate { get; set; }
        public Task<UpdateInfo?> CheckAsync(bool includePrereleases, CancellationToken ct)
        {
            Checks++; LastPrerelease = includePrereleases;
            if (CheckError is { } e) throw e;
            return Task.FromResult(Offer);
        }
        public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            progress(50);
            if (DownloadGate is { } gate) await gate.Task.WaitAsync(ct);
            if (DownloadError is { } e) throw e;
            progress(100);
            PendingVersion = Offer?.Version;
        }
        public void ApplyAndRestart() => Restarts++;
        public void ApplyOnExit() => ExitApplies++;
    }

    private sealed class Policy : IPolicyService { public bool Allow = true; public bool IsAllowed(PolicyFeature f) => f != PolicyFeature.Updates || Allow; }
    private sealed class Network : INetworkStatus { public bool IsOnline { get; set; } = true; public bool IsMetered { get; set; } }
    private sealed class Host : IUpdateRestartHost
    {
        public bool IsCaptureInProgress { get; set; }
        public bool SaveResult = true;
        public int SavePrompts, Exits;
        public Task<bool> SaveOrCloseDocumentsAsync() { SavePrompts++; return Task.FromResult(SaveResult); }
        public void Exit() => Exits++;
    }

    private sealed record Rig(UpdateService Service, FakeEngine Engine, SettingsService Settings, Policy Policy, Network Network, Host Host, Clock Clock);

    private Rig Create(Action<UpdateSettings>? prefs = null, Action<FakeEngine>? engine = null)
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsService(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
        if (prefs is not null) settings.Update(s => prefs(s.Updates));
        var e = new FakeEngine(); engine?.Invoke(e);
        var policy = new Policy(); var network = new Network(); var host = new Host(); var clock = new Clock();
        var service = new UpdateService(e, settings, policy, network, host, NullLogger.Instance, clock) { MeteredPollInterval = TimeSpan.FromMilliseconds(20) };
        return new Rig(service, e, settings, policy, network, host, clock);
    }

    private static UpdateInfo Release(string v = "0.9.2") => new(v, "## What's new\n- **Faster** startup\n- Fixes", 1234);

    // ------------------------------------------------------------------ UpdatePolicy

    [Fact] public void DefaultsAreDailyBackgroundDownloadStable()
    {
        var s = new UpdateSettings();
        Assert.True(s.AutomaticallyCheck); Assert.False(s.PromptBeforeCheck); Assert.Equal(1, s.DaysBetweenChecks);
        Assert.True(s.DownloadAutomatically); Assert.Equal(UpdateChannel.Stable, s.Channel);
    }

    [Fact] public void LegacyWeeklyDefaultMigratesToDailyOnce()
    {
        var old = new AppSettings { SchemaVersion = 1 }; old.Updates.DaysBetweenChecks = 7; old.Normalize();
        Assert.Equal(1, old.Updates.DaysBetweenChecks); Assert.Equal(AppSettings.CurrentSchemaVersion, old.SchemaVersion);
        var custom = new AppSettings { SchemaVersion = 1 }; custom.Updates.DaysBetweenChecks = 14; custom.Normalize();
        Assert.Equal(14, custom.Updates.DaysBetweenChecks);
        var current = new AppSettings(); current.Updates.DaysBetweenChecks = 7; current.Normalize();
        Assert.Equal(7, current.Updates.DaysBetweenChecks);   // a weekly choice made under schema 2 is kept
    }

    [Theory]
    [InlineData(null, 1, true)]          // never checked
    [InlineData(-23.9, 1, false)]        // checked < 1 day ago
    [InlineData(-24.0, 1, true)]         // exactly 1 day
    [InlineData(-24.0 * 6, 7, false)]
    [InlineData(-24.0 * 7, 7, true)]
    [InlineData(48.0, 1, true)]          // clock turned back: last check "in the future"
    public void CheckIsDueAfterInterval(double? hoursAgo, int days, bool due)
    {
        var s = new UpdateSettings { DaysBetweenChecks = days, LastCheck = hoursAgo is { } h ? Now.AddHours(h) : null };
        Assert.Equal(due, UpdatePolicy.IsCheckDue(s, Now));
    }

    [Fact] public void ScheduledDecisionHonoursAutoCheckAndAskFirst()
    {
        Assert.Equal(ScheduledCheckDecision.Check, UpdatePolicy.DecideScheduled(new UpdateSettings(), Now, false));
        Assert.Equal(ScheduledCheckDecision.Skip, UpdatePolicy.DecideScheduled(new UpdateSettings { AutomaticallyCheck = false }, Now, false));
        Assert.Equal(ScheduledCheckDecision.Skip, UpdatePolicy.DecideScheduled(new UpdateSettings { LastCheck = Now.AddHours(-1) }, Now, false));
        Assert.Equal(ScheduledCheckDecision.AskFirst, UpdatePolicy.DecideScheduled(new UpdateSettings { PromptBeforeCheck = true }, Now, false));
        Assert.Equal(ScheduledCheckDecision.Skip, UpdatePolicy.DecideScheduled(new UpdateSettings { PromptBeforeCheck = true }, Now, true));   // once per session
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]      // metered: never in the background
    [InlineData(false, false, false)]    // background download off
    public void BackgroundDownloadNeedsPreferenceAndUnmeteredConnection(bool auto, bool metered, bool expected)
        => Assert.Equal(expected, UpdatePolicy.ShouldDownloadInBackground(new UpdateSettings { DownloadAutomatically = auto }, metered));

    [Fact] public void RestartGuards()
    {
        Assert.Equal(UpdateRestartResult.NothingToApply, UpdatePolicy.RestartBlock(updateReady: false, captureInProgress: false));
        Assert.Equal(UpdateRestartResult.BlockedByRecording, UpdatePolicy.RestartBlock(updateReady: true, captureInProgress: true));
        Assert.Null(UpdatePolicy.RestartBlock(updateReady: true, captureInProgress: false));
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, false)]
    [InlineData(UpdateChannel.Beta, true)]
    public void ChannelMapsToGitHubPrereleaseFlag(UpdateChannel channel, bool prerelease) => Assert.Equal(prerelease, UpdatePolicy.IncludePrereleases(channel));

    [Theory]
    [InlineData("0.9.1", UpdateChannel.Stable)]
    [InlineData("0.9.2-beta.1", UpdateChannel.Beta)]
    [InlineData("1.0.0-rc.2+build.5", UpdateChannel.Beta)]
    [InlineData("1.0.0+build.5", UpdateChannel.Stable)]
    public void VersionChannel(string version, UpdateChannel channel) => Assert.Equal(channel, UpdatePolicy.ChannelOf(version));

    [Theory]
    [InlineData("0.9.1", "0.9.0", true)]
    [InlineData("0.9.1-test.2", "0.9.1-test.1", true)]
    [InlineData("0.9.1", "0.9.1-beta.3", true)]
    [InlineData("0.9.1-beta.3", "0.9.1", false)]
    [InlineData("0.9.0", "0.9.0", false)]
    [InlineData("garbage", "0.9.0", false)]
    public void VersionOrdering(string candidate, string current, bool newer) => Assert.Equal(newer, UpdatePolicy.IsNewer(candidate, current));

    [Fact] public void LocalSourceMustBeAnExistingLocalFolder()
    {
        Directory.CreateDirectory(_dir);
        Assert.Equal(Path.GetFullPath(_dir), UpdatePolicy.ValidateLocalSource(_dir));
        Assert.Equal(Path.GetFullPath(_dir), UpdatePolicy.ValidateLocalSource("\"" + _dir + "\""));
        Assert.Null(UpdatePolicy.ValidateLocalSource(null));
        Assert.Null(UpdatePolicy.ValidateLocalSource(Path.Combine(_dir, "missing")));
        Assert.Null(UpdatePolicy.ValidateLocalSource(@"\\server\share\feed"));
        Assert.Null(UpdatePolicy.ValidateLocalSource("https://example.org/feed"));
        Assert.Null(UpdatePolicy.ValidateLocalSource(@"relative\feed"));
    }

    [Fact] public void ReleaseNotesSnippetIsPlainAndShort()
    {
        var md = "# Shotwise 0.9.2\n\n## New\n- **Automatic updates** from [GitHub](https://github.com/x)\n- `Restart now` button\n\n```\ncode\n```\n| a | b |";
        Assert.Equal("Automatic updates from GitHub · Restart now button", ReleaseNotes.Snippet(md));
        var cut = ReleaseNotes.Snippet(string.Join("\n", Enumerable.Repeat("- word word word word", 40)), 60);
        Assert.True(cut.Length <= 61); Assert.EndsWith("…", cut);
        Assert.Equal("", ReleaseNotes.Snippet(null));
    }

    // ------------------------------------------------------------------ orchestration

    [Fact] public async Task UnsupportedCopyNeverChecks()
    {
        var r = Create(engine: e => e.UnsupportedReason = "development build");
        Assert.Equal(UpdateState.NotSupported, r.Service.Status.State);
        Assert.Null(await r.Service.CheckAsync(true, default));
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(0, r.Engine.Checks);
        r.Service.StartScheduler();   // no-op
        r.Service.OnAppExit();
        Assert.Equal(0, r.Engine.ExitApplies);
    }

    [Fact] public async Task PolicyDisablesChecksDownloadsAndApply()
    {
        var r = Create(engine: e => e.Offer = Release());
        r.Policy.Allow = false;
        Assert.Null(await r.Service.CheckAsync(true, default));
        Assert.Equal(UpdateState.Disabled, r.Service.Status.State);
        Assert.Equal(0, r.Engine.Checks);
        r.Engine.PendingVersion = "0.9.2";
        r.Service.OnAppExit();
        Assert.Equal(0, r.Engine.ExitApplies);
        r.Policy.Allow = true;
        Assert.NotNull(await r.Service.CheckAsync(true, default));   // re-enabled at the next request
    }

    [Fact] public async Task ScheduledCheckDownloadsInBackgroundAndRecordsLastCheck()
    {
        var r = Create(engine: e => e.Offer = Release());
        var states = new List<UpdateState>();
        r.Service.StatusChanged += (_, s) => states.Add(s.State);
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.ReadyToRestart, r.Service.Status.State);
        Assert.True(r.Service.Status.Scheduled);
        Assert.Equal("0.9.2", r.Service.Status.Update!.Version);
        Assert.Equal(Now, r.Settings.Current.Updates.LastCheck);
        Assert.Contains(UpdateState.Checking, states); Assert.Contains(UpdateState.Downloading, states);
        Assert.False(r.Engine.LastPrerelease);
        // next tick within the interval: no new check
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(1, r.Engine.Checks);
    }

    [Fact] public async Task ScheduleRespectsIntervalAndAutoCheckOff()
    {
        var r = Create(prefs: p => { p.DaysBetweenChecks = 3; p.LastCheck = Now.AddDays(-2); }, engine: e => e.Offer = Release());
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(0, r.Engine.Checks);
        r.Clock.Now = Now.AddDays(1);
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(1, r.Engine.Checks);

        var off = Create(prefs: p => p.AutomaticallyCheck = false, engine: e => e.Offer = Release());
        await off.Service.RunScheduledOnceAsync(default);
        Assert.Null(await off.Service.CheckAsync(userApproved: false, default));
        Assert.Equal(0, off.Engine.Checks);
        Assert.NotNull(await off.Service.CheckAsync(userApproved: true, default));   // the Help menu still works
    }

    [Fact] public async Task MeteredConnectionSkipsBackgroundDownloadButManualDownloadWorks()
    {
        var r = Create(engine: e => e.Offer = Release());
        r.Network.IsMetered = true;
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.Available, r.Service.Status.State);
        Assert.Equal(0, r.Engine.Downloads);
        Assert.Contains("Metered", r.Service.Status.Message);
        Assert.True(await r.Service.DownloadAsync(null, default));   // explicit user choice
        Assert.Equal(UpdateState.ReadyToRestart, r.Service.Status.State);
    }

    [Fact] public async Task BackgroundDownloadPausesWhenConnectionBecomesMetered()
    {
        var r = Create(engine: e => { e.Offer = Release(); e.DownloadGate = new TaskCompletionSource(); });
        var run = r.Service.RunScheduledOnceAsync(default);
        await WaitUntil(() => r.Service.Status.State == UpdateState.Downloading);
        Assert.Equal(50, r.Service.Status.Progress);
        r.Network.IsMetered = true;
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateState.Available, r.Service.Status.State);
        Assert.Contains("metered", r.Service.Status.Message);
    }

    [Fact] public async Task BackgroundDownloadOffLeavesUpdateAvailable()
    {
        var r = Create(prefs: p => p.DownloadAutomatically = false, engine: e => e.Offer = Release());
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.Available, r.Service.Status.State);
        Assert.Equal(0, r.Engine.Downloads);
    }

    [Fact] public async Task AskFirstWaitsForConsentOncePerSession()
    {
        var r = Create(prefs: p => p.PromptBeforeCheck = true, engine: e => e.Offer = Release());
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.AwaitingConsent, r.Service.Status.State);
        Assert.Equal(0, r.Engine.Checks);
        Assert.NotNull(await r.Service.CheckAsync(userApproved: true, default));   // "Check now" on the toast
        Assert.Equal(1, r.Engine.Checks);
    }

    [Fact] public async Task OfflineIsGracefulAndRetried()
    {
        var r = Create(engine: e => e.Offer = Release());
        r.Network.IsOnline = false;
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.Failed, r.Service.Status.State);
        Assert.True(r.Service.Status.Offline);
        Assert.Null(r.Settings.Current.Updates.LastCheck);   // not recorded: the next tick retries
        Assert.Equal(0, r.Engine.Checks);
        r.Network.IsOnline = true;
        await r.Service.RunScheduledOnceAsync(default);
        Assert.Equal(UpdateState.ReadyToRestart, r.Service.Status.State);
    }

    [Fact] public async Task FeedErrorsBecomeFailedStatusNotExceptions()
    {
        var r = Create(engine: e => e.CheckError = new HttpRequestException("403 rate limit"));
        Assert.Null(await r.Service.CheckAsync(true, default));
        Assert.Equal(UpdateState.Failed, r.Service.Status.State);
        Assert.Contains("rate limit", r.Service.Status.Message);
        Assert.Null(r.Settings.Current.Updates.LastCheck);

        var d = Create(engine: e => { e.Offer = Release(); e.DownloadError = new IOException("checksum mismatch"); });
        await d.Service.CheckAsync(true, default);
        Assert.False(await d.Service.DownloadAsync(null, default));
        Assert.Equal(UpdateState.Failed, d.Service.Status.State);
        Assert.NotNull(d.Service.Status.Update);   // retry possible
    }

    [Fact] public async Task UpToDateWhenFeedOffersNothingNewer()
    {
        var r = Create(engine: e => { e.Offer = Release("0.9.1"); });
        Assert.Null(await r.Service.CheckAsync(true, default));
        Assert.Equal(UpdateState.UpToDate, r.Service.Status.State);
        Assert.Equal(Now, r.Service.Status.LastCheck);
    }

    [Fact] public async Task BetaChannelReadsPrereleasesAndOverrideWins()
    {
        var r = Create(prefs: p => p.Channel = UpdateChannel.Beta, engine: e => e.Offer = Release("0.9.2-beta.1"));
        Assert.NotNull(await r.Service.CheckAsync(true, default));
        Assert.True(r.Engine.LastPrerelease);
        await r.Service.CheckAsync(true, default, UpdateChannel.Stable);
        Assert.False(r.Engine.LastPrerelease);
        Assert.Equal(UpdateChannel.Stable, r.Service.Status.Channel);
    }

    [Fact] public async Task ChannelSwitchDropsAFoundRelease()
    {
        var r = Create(engine: e => e.Offer = Release());
        await r.Service.CheckAsync(true, default);
        Assert.Equal(UpdateState.Available, r.Service.Status.State);
        r.Settings.Update(s => s.Updates.Channel = UpdateChannel.Beta);
        Assert.Equal(UpdateState.Idle, r.Service.Status.State);
        Assert.Equal(UpdateChannel.Beta, r.Service.Status.Channel);
    }

    [Fact] public async Task RestartIsRefusedDuringRecording()
    {
        var r = await ReadyRig();
        r.Host.IsCaptureInProgress = true;
        Assert.Equal(UpdateRestartResult.BlockedByRecording, await r.Service.RestartToApplyAsync());
        Assert.Equal(0, r.Host.SavePrompts); Assert.Equal(0, r.Host.Exits);
        Assert.Equal(UpdateState.ReadyToRestart, r.Service.Status.State);
    }

    [Fact] public async Task RestartAsksToSaveAndCancelKeepsTheAppOpen()
    {
        var r = await ReadyRig();
        r.Host.SaveResult = false;
        Assert.Equal(UpdateRestartResult.Cancelled, await r.Service.RestartToApplyAsync());
        Assert.Equal(1, r.Host.SavePrompts); Assert.Equal(0, r.Host.Exits);
        r.Service.OnAppExit();   // a later normal exit still applies silently, without restarting
        Assert.Equal(1, r.Engine.ExitApplies); Assert.Equal(0, r.Engine.Restarts);
    }

    [Fact] public async Task RestartNowSavesExitsAndRestartsAfterCleanup()
    {
        var r = await ReadyRig();
        Assert.Equal(UpdateRestartResult.Restarting, await r.Service.RestartToApplyAsync());
        Assert.Equal(1, r.Host.SavePrompts); Assert.Equal(1, r.Host.Exits);
        Assert.Equal(0, r.Engine.Restarts);   // applied only from OnAppExit, after the host shut down
        r.Service.OnAppExit();
        r.Service.OnAppExit();   // idempotent
        Assert.Equal(1, r.Engine.Restarts); Assert.Equal(0, r.Engine.ExitApplies);
    }

    [Fact] public async Task NothingToApplyWithoutDownload()
    {
        var r = Create(engine: e => e.Offer = Release());
        await r.Service.CheckAsync(true, default);
        Assert.Equal(UpdateRestartResult.NothingToApply, await r.Service.RestartToApplyAsync());
        r.Service.OnAppExit();
        Assert.Equal(0, r.Engine.ExitApplies);
    }

    [Fact] public void PendingPackageFromEarlierSessionIsOfferedAtStartup()
    {
        var r = Create(engine: e => e.PendingVersion = "0.9.2");
        Assert.Equal(UpdateState.ReadyToRestart, r.Service.Status.State);
        Assert.Equal("0.9.2", r.Service.Status.Update!.Version);
        var stale = Create(engine: e => e.PendingVersion = "0.9.1");   // same as installed: nothing pending
        Assert.Equal(UpdateState.Idle, stale.Service.Status.State);
    }

    [Fact] public async Task SchedulerRunsAfterStartupDelay()
    {
        var r = Create(engine: e => e.Offer = Release());
        r.Service.StartupDelay = TimeSpan.FromMilliseconds(10);
        r.Service.StartScheduler();
        r.Service.StartScheduler();   // idempotent
        await WaitUntil(() => r.Service.Status.State == UpdateState.ReadyToRestart);
        Assert.Equal(1, r.Engine.Checks);
        r.Service.Dispose();
    }

    private async Task<Rig> ReadyRig()
    {
        var r = Create(engine: e => e.Offer = Release());
        await r.Service.CheckAsync(true, default);
        Assert.True(await r.Service.DownloadAsync(null, default));
        return r;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ tray icon recovery (TaskbarCreated)

    [Fact] public void TrayIconFailureDoesNotThrowAndRetriesOnTaskbarCreated()
    {
        var fail = true; var alive = false; var calls = 0;
        var recovery = new TrayIconRecovery(() => { calls++; if (fail) throw new InvalidOperationException("TryCreate failed."); alive = true; return true; }, () => alive, NullLogger.Instance);
        Assert.False(recovery.TryCreate());
        Assert.False(recovery.IsCreated);
        Assert.IsType<InvalidOperationException>(recovery.LastError);
        Assert.False(recovery.OnWindowMessage(0x0400));   // unrelated message
        Assert.Equal(1, calls);
        Assert.NotEqual(0u, TrayIconRecovery.TaskbarCreatedMessage);
        Assert.True(recovery.OnWindowMessage(TrayIconRecovery.TaskbarCreatedMessage));   // shell still not ready
        Assert.False(recovery.IsCreated);
        fail = false;
        Assert.True(recovery.OnWindowMessage(TrayIconRecovery.TaskbarCreatedMessage));
        Assert.True(recovery.IsCreated); Assert.Null(recovery.LastError);
        Assert.Equal(3, calls);
        Assert.True(recovery.OnWindowMessage(TrayIconRecovery.TaskbarCreatedMessage));   // alive icon: left alone
        Assert.Equal(3, calls);
        Assert.Equal(3, recovery.Attempts);
    }
}
