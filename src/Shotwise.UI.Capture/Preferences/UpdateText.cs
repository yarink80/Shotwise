using System.Globalization;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>Localized texts for <see cref="UpdateStatus"/> (Preferences → Startup &amp; Updates, tray, toasts).</summary>
public static class UpdateText
{
    public static string Channel(UpdateChannel c) => CS.Get(c == UpdateChannel.Beta ? "Update_ChannelBeta" : "Update_ChannelStable");

    public static string VersionLine(UpdateStatus s) => CS.Format("Update_Version", s.CurrentVersion, CS.Get(s.Channel == UpdateChannel.Beta ? "Update_ChannelBeta" : "Update_ChannelStable"));

    public static string LastCheckLine(UpdateStatus s)
        => CS.Format("Update_LastCheck", s.LastCheck is { } t ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : CS.Get("Update_Never"));

    public static string State(UpdateStatus s) => s.State switch
    {
        UpdateState.NotSupported => CS.Format("Update_StateUnsupported", s.Message ?? ""),
        UpdateState.Disabled => CS.Get("Update_StateDisabled"),
        UpdateState.AwaitingConsent => CS.Get("Update_StateConsent"),
        UpdateState.Checking => CS.Get("Update_StateChecking"),
        UpdateState.UpToDate => CS.Get("Update_StateUpToDate"),
        UpdateState.Available => CS.Format("Update_StateAvailable", s.Update?.Version) + (s.Message is { } m ? " " + m : ""),
        UpdateState.Downloading => CS.Format("Update_StateDownloading", s.Update?.Version, s.Progress),
        UpdateState.ReadyToRestart => CS.Format("Update_StateReady", s.Update?.Version),
        UpdateState.Failed => s.Offline ? CS.Get("Update_StateOffline") : CS.Format("Update_StateFailed", s.Message ?? ""),
        _ => CS.Get("Update_StateIdle"),
    };

    /// <summary>"What's new: …" excerpt of the release notes, or empty.</summary>
    public static string Notes(string? snippet) => string.IsNullOrWhiteSpace(snippet) ? "" : CS.Format("Update_Notes", snippet);
}
