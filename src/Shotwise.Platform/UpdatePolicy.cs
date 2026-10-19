using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.Platform;

/// <summary>What the schedule does when it ticks.</summary>
public enum ScheduledCheckDecision
{
    /// <summary>Automatic checks are off, or the last check is recent enough.</summary>
    Skip,
    /// <summary>A check is due but "Ask before checking" is on: ask the user (once per session).</summary>
    AskFirst,
    /// <summary>Check the feed now.</summary>
    Check,
}

/// <summary>
/// Pure update rules (unit-tested): schedule, channel mapping, background-download gate, restart guards, version ordering and the
/// local test-feed override. <see cref="UpdateService"/> applies them.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>The first automatic check waits until startup work (library, hotkeys, windows) has settled.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>The schedule re-evaluates "is a check due?" this often, so a Shotwise left running for days still checks.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    /// <summary>Environment variable that points the updater at a local Velopack release folder instead of GitHub (testing).</summary>
    public const string LocalSourceVariable = "SHOTWISE_UPDATE_SOURCE";

    /// <summary>The public repository whose GitHub Releases feed installed copies.</summary>
    public const string GitHubRepositoryUrl = "https://github.com/yarink80/Shotwise";

    /// <summary>Stable reads GitHub releases only; Beta also reads pre-releases (Velopack <c>GithubSource(prerelease: true)</c>).</summary>
    public static bool IncludePrereleases(UpdateChannel channel) => channel == UpdateChannel.Beta;

    /// <summary>The channel a version belongs to: a SemVer pre-release ("0.9.2-beta.1") is Beta, anything else Stable.</summary>
    public static UpdateChannel ChannelOf(string version)
        => SemanticVersion.TryParse(version, out var v) && v.Prerelease.Length > 0 ? UpdateChannel.Beta : UpdateChannel.Stable;

    /// <summary>
    /// True when the last successful check is at least <see cref="UpdateSettings.DaysBetweenChecks"/> old, never happened, or lies
    /// more than a day in the future (the clock was turned back).
    /// </summary>
    public static bool IsCheckDue(UpdateSettings s, DateTimeOffset now)
    {
        if (s.LastCheck is not { } last) return true;
        if (last > now + TimeSpan.FromDays(1)) return true;
        return now - last >= TimeSpan.FromDays(Math.Clamp(s.DaysBetweenChecks, 1, 365));
    }

    /// <summary>The scheduled-check decision for the current preferences.</summary>
    public static ScheduledCheckDecision DecideScheduled(UpdateSettings s, DateTimeOffset now, bool alreadyAskedThisSession)
    {
        if (!s.AutomaticallyCheck || !IsCheckDue(s, now)) return ScheduledCheckDecision.Skip;
        if (s.PromptBeforeCheck) return alreadyAskedThisSession ? ScheduledCheckDecision.Skip : ScheduledCheckDecision.AskFirst;
        return ScheduledCheckDecision.Check;
    }

    /// <summary>A found update downloads in the background only when the user allows it and the connection is not metered.</summary>
    public static bool ShouldDownloadInBackground(UpdateSettings s, bool metered) => s.DownloadAutomatically && !metered;

    /// <summary>Why "Restart now" must not proceed, or null when it may (unsaved documents are handled by the save prompt next).</summary>
    public static UpdateRestartResult? RestartBlock(bool updateReady, bool captureInProgress)
        => !updateReady ? UpdateRestartResult.NothingToApply
            : captureInProgress ? UpdateRestartResult.BlockedByRecording
            : null;

    /// <summary>True when <paramref name="candidate"/> is a higher SemVer than <paramref name="current"/> (unparsable = not newer).</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!SemanticVersion.TryParse(candidate, out var c)) return false;
        if (string.IsNullOrWhiteSpace(current) || !SemanticVersion.TryParse(current, out var cur)) return true;
        return c.CompareTo(cur) > 0;
    }

    /// <summary>
    /// Validates <see cref="LocalSourceVariable"/>: an absolute, existing local folder (no UNC share, no URL). Returns its full path,
    /// or null when unset/invalid (the updater then uses GitHub).
    /// </summary>
    public static string? ValidateLocalSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(value)) return null;
            var full = Path.GetFullPath(value);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
