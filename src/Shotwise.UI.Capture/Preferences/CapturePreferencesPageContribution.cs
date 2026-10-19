using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Preferences;

namespace Shotwise.UI.Capture.Preferences;

/// <summary>
/// Adapts one <see cref="CapturePreferencePage"/> to the shared UI.Common <see cref="IPreferencesPage"/> contract (CONTRACT A6/A21),
/// so the Preferences window (UI.Dialogs) shows the capture General / Capture / Hotkeys pages.
/// </summary>
internal sealed class CapturePreferencesPageContribution : IPreferencesPage
{
    private readonly string _id;
    private readonly IServiceProvider _services;

    public CapturePreferencesPageContribution(string id, int order, IServiceProvider services)
    {
        _id = id;
        Order = order;
        _services = services;
    }

    private CapturePreferencePage Page => CapturePreferencePages.Create().First(p => p.Id == _id);

    public string Id => _id;
    // The host already has a "General" page (language, toasts, privacy…); name the capture one after what it holds.
    public string Title => _id == CapturePreferencePages.GeneralId ? CS.Get("Prefs_StartupPage") : Page.Title;
    public int Order { get; }

    public UserControl CreatePage() => CapturePreferencePages.CreateLive(Page, _services.GetRequiredService<ISettingsService>(), _services);

    public UserControl CreatePage(PreferencesPageContext context)
        => _id == CapturePreferencePages.HotkeysId
            // preset hotkeys live in presets.json: staged by the page and applied through the host's commit hook on OK
            ? new CaptureHotkeysPage(context.Working, context.Services ?? _services, context.Changed, context.RegisterCommit)
            : Page.Create(context.Working, context.Services ?? _services, context.Changed);
}
