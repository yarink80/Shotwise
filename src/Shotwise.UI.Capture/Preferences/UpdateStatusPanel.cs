using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
using Shotwise.UI.Capture.Infrastructure;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>
/// Preferences → Startup &amp; Updates status block: current version and channel, last check, live status, a release-notes excerpt
/// and the Check / Download / Restart now buttons. Follows <see cref="IUpdateService.StatusChanged"/> while it is loaded.
/// </summary>
internal sealed class UpdateStatusPanel : StackPanel
{
    private readonly IUpdateService _updates;
    private readonly Func<UpdateChannel> _channel;
    private readonly TextBlock _version = Ui.Label("", 13, weight: FontWeights.SemiBold);
    private readonly TextBlock _lastCheck = Ui.Label("", 12, secondary: true);
    private readonly TextBlock _state = Ui.Label("", 12, wrap: true);
    private readonly TextBlock _notes = Ui.Label("", 12, secondary: true, wrap: true);
    private readonly TextBlock _source = Ui.Label("", 11, secondary: true, wrap: true);
    private readonly Button _check, _download, _restart;
    private CancellationTokenSource? _busy;

    public UpdateStatusPanel(IUpdateService updates, Func<UpdateChannel> channel)
    {
        _updates = updates;
        _channel = channel;
        Margin = new Thickness(6, 2, 6, 8);
        _check = Ui.TextButton(CS.Get("Update_Check"), () => _ = CheckAsync(), "Icon.Search.Regular");
        _download = Ui.TextButton(CS.Get("Update_Download"), () => _ = DownloadAsync(), "Icon.Download.Regular");
        _restart = Ui.TextButton(CS.Get("Update_RestartNow"), () => _ = RestartAsync(), accent: true);
        foreach (var b in new[] { _download, _restart }) b.Margin = new Thickness(8, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_restart, CS.Get("Update_RestartNow"));
        foreach (var t in new[] { _lastCheck, _state, _notes, _source }) t.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(_version);
        Children.Add(_lastCheck);
        Children.Add(_state);
        Children.Add(_notes);
        var buttons = Ui.Row(_check, _download, _restart);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(buttons);
        Children.Add(_source);
        Loaded += (_, _) => { _updates.StatusChanged += OnStatus; Render(_updates.Status); };
        Unloaded += (_, _) => { _updates.StatusChanged -= OnStatus; _busy?.Cancel(); };
        Render(_updates.Status);
    }

    private void OnStatus(object? sender, UpdateStatus s) => Dispatcher.BeginInvoke(new Action(() => Render(s)));

    private void Render(UpdateStatus s)
    {
        _version.Text = UpdateText.VersionLine(s);
        _lastCheck.Text = UpdateText.LastCheckLine(s);
        _state.Text = UpdateText.State(s);
        var snippet = s.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToRestart ? ReleaseNotes.Snippet(s.Update?.ReleaseNotes) : "";
        _notes.Text = UpdateText.Notes(snippet);
        _notes.Visibility = snippet.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _source.Text = s.Source is { } src ? CS.Format("Update_Source", src) : "";
        var usable = s.State is not (UpdateState.NotSupported or UpdateState.Disabled);
        _check.IsEnabled = usable && s.State is not (UpdateState.Checking or UpdateState.Downloading);
        _check.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        _download.Visibility = s.State == UpdateState.Available || (s.State == UpdateState.Failed && s.Update is not null) ? Visibility.Visible : Visibility.Collapsed;
        _restart.Visibility = s.State == UpdateState.ReadyToRestart ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckAsync()
    {
        _busy = new CancellationTokenSource();
        try { await _updates.CheckAsync(userApproved: true, _busy.Token, _channel()); }
        catch (OperationCanceledException) { }
    }

    private async Task DownloadAsync()
    {
        _busy = new CancellationTokenSource();
        try { await _updates.DownloadAsync(null, _busy.Token); }
        catch (OperationCanceledException) { }
    }

    private async Task RestartAsync()
    {
        var result = await _updates.RestartToApplyAsync();
        if (result == UpdateRestartResult.BlockedByRecording) _state.Text = CS.Get("Update_BlockedBody");
    }
}
