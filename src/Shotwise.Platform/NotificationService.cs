using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;
namespace Shotwise.Platform;
/// <summary>The toolkit registers the unpackaged AUMID/COM activator on first use. Actions are process-local.</summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, (Action Action, DateTimeOffset Expires)> _actions = new();
    private static readonly string AppLogoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "shotwise-96.png");
    public NotificationService(ISettingsService settings, ILogger<NotificationService> log)
    {
        _settings = settings; _log = LogRedaction.Wrap(log);
        ToastNotificationManagerCompat.OnActivated += Activated;
    }
    private void Activated(ToastNotificationActivatedEventArgsCompat args)
    {
        try
        {
            var parsed = ToastArguments.Parse(args.Argument);
            if (parsed.TryGetValue("action", out var id) && _actions.TryRemove(id, out var action) && action.Expires > DateTimeOffset.UtcNow) action.Action();
        }
        catch (Exception e) { _log.LogWarning(e, "Notification action failed"); }
    }
    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info, IReadOnlyList<NotificationAction>? actions = null)
    {
        var s = _settings.Current;
        if (!s.General.ShowToasts || s.General.HelpNotifications == HelpNotificationMode.None) return;
        try
        {
            foreach (var pair in _actions) if (pair.Value.Expires <= DateTimeOffset.UtcNow) _actions.TryRemove(pair.Key, out _);
            // Source-bearing text is suppressed when metadata privacy is enabled.
            var content = new ToastContentBuilder().AddText(s.Privacy.StripMetadataOnShare ? "Shotwise" : title)
                .AddText(s.Privacy.StripMetadataOnShare ? "An operation completed. Open Shotwise for details." : message);
            // App logo = the Shotwise icon shipped next to the exe (the header name/icon come from the Start menu shortcut's AUMID).
            if (File.Exists(AppLogoPath)) content.AddAppLogoOverride(new Uri(AppLogoPath), ToastGenericAppLogoCrop.Default);
            foreach (var action in actions?.Take(5) ?? Enumerable.Empty<NotificationAction>())
            {
                if (_actions.Count >= 256) break;
                var id = Guid.NewGuid().ToString("N"); _actions[id] = (action.Invoke, DateTimeOffset.UtcNow.AddDays(1));
                content.AddButton(new ToastButton().SetContent(action.Label).AddArgument("action", id).SetBackgroundActivation());
            }
            content.Show();
        }
        catch (Exception e) { _log.LogWarning(e, "Toast unavailable"); }
    }
    public void Dispose() { ToastNotificationManagerCompat.OnActivated -= Activated; _actions.Clear(); }
}
