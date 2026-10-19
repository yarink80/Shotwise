using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Shotwise.Core.Platform;
using Shotwise.UI.Common.Controls;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>
/// Editor InfoBar under the top bar: "Shotwise X.Y.Z is ready — Restart now / Later" while a downloaded update waits
/// (<see cref="UpdateState.ReadyToRestart"/>). "Later" (or the close button) hides it for that version; the update then installs
/// silently when Shotwise exits. "Restart now" goes through <see cref="IUpdateService.RestartToApplyAsync"/> (refused during a
/// recording, asks to save unsaved documents first).
/// </summary>
internal sealed class UpdateBar : Border
{
    private readonly IUpdateService _updates;
    private readonly Action<string, MessageKind> _toast;
    private readonly InfoBar _bar;
    private string? _dismissedVersion;

    public UpdateBar(IUpdateService updates, Action<string, MessageKind> toast)
    {
        _updates = updates;
        _toast = toast;
        Visibility = Visibility.Collapsed;
        Padding = new Thickness(12, 8, 12, 0);
        var restart = UiKit.PrimaryButton(ES.T("Update_RestartNow", "Restart now"), null, () => _ = RestartAsync());
        var later = UiKit.GhostButton(ES.T("Update_Later", "Later"), null, Dismiss);
        AutomationProperties.SetAutomationId(restart, "UpdateRestartNow");
        AutomationProperties.SetAutomationId(later, "UpdateLater");
        later.Margin = new Thickness(6, 0, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(restart);
        actions.Children.Add(later);
        _bar = new InfoBar { Severity = InfoSeverity.Success, Action = actions, IsClosable = true };
        AutomationProperties.SetAutomationId(_bar, "UpdateReadyBar");
        _bar.Closed += (_, _) => Dismiss();
        Child = _bar;
        Loaded += (_, _) => { _updates.StatusChanged += OnStatus; Render(_updates.Status); };
        Unloaded += (_, _) => _updates.StatusChanged -= OnStatus;
    }

    private void OnStatus(object? sender, UpdateStatus s) => Dispatcher.BeginInvoke(new Action(() => Render(s)));

    private void Render(UpdateStatus s)
    {
        var version = s.Update?.Version;
        var show = s.State == UpdateState.ReadyToRestart && version is not null && version != _dismissedVersion;
        if (show)
        {
            _bar.Title = ES.F("Update_ReadyTitle", "Shotwise {0} is ready", version);
            _bar.Message = ES.T("Update_ReadyBody", "Restart Shotwise to finish updating. Your work is saved first. If you choose Later, the update installs the next time you exit Shotwise.");
            _bar.IsOpen = true;
        }
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Dismiss()
    {
        _dismissedVersion = _updates.Status.Update?.Version;
        Visibility = Visibility.Collapsed;
    }

    private async Task RestartAsync()
    {
        switch (await _updates.RestartToApplyAsync())
        {
            case UpdateRestartResult.BlockedByRecording:
                _toast(ES.T("Update_BlockedBody", "Shotwise restarts to update after the capture or recording has finished."), MessageKind.Warning);
                break;
            case UpdateRestartResult.NothingToApply:
                Visibility = Visibility.Collapsed;
                break;
        }
    }
}
