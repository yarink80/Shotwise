using System.Windows.Controls;
using Shotwise.Core.Settings;

namespace Shotwise.UI.Common.Preferences;

/// <summary>
/// A page contributed to the shared Preferences window (CONTRACT A6). UI libraries register implementations in DI
/// (<c>services.AddSingleton&lt;IPreferencesPage, …&gt;()</c>); the Preferences host (UI.Dialogs) discovers them as
/// <c>IEnumerable&lt;IPreferencesPage&gt;</c>. A contributed page whose <see cref="Id"/> equals a built-in page id replaces it.
/// </summary>
public interface IPreferencesPage
{
    /// <summary>Stable page id, e.g. "CaptureGeneral", "Capture", "Hotkeys" (also used for <c>PreferencesRequest.InitialPage</c>).</summary>
    string Id { get; }

    /// <summary>Localized navigation title.</summary>
    string Title { get; }

    /// <summary>Sort position in the navigation list (built-in pages use multiples of 100).</summary>
    int Order { get; }

    /// <summary>Creates a fresh, stand-alone page that applies its edits to the live settings immediately.</summary>
    UserControl CreatePage();

    /// <summary>
    /// Creates a fresh page for a staged Preferences window: edit <see cref="PreferencesPageContext.Working"/> in place and call
    /// <see cref="PreferencesPageContext.Changed"/> after each edit; the host applies the working copy on OK and discards it on Cancel.
    /// The default falls back to <see cref="CreatePage()"/> (live editing).
    /// </summary>
    UserControl CreatePage(PreferencesPageContext context) => CreatePage();
}

/// <summary>Staging context passed by the Preferences host to <see cref="IPreferencesPage.CreatePage(PreferencesPageContext)"/>.</summary>
/// <param name="Working">Settings copy owned by the Preferences window (shared by all contributed pages).</param>
/// <param name="Services">Application service provider (optional engine services such as IHotkeyService).</param>
/// <param name="Changed">Callback after every edit (marks the page dirty).</param>
/// <param name="RegisterCommit">
/// Optional: registers work the host runs on OK after the settings were applied (for staged data that does not live in
/// <see cref="AppSettings"/>, e.g. preset hotkeys). Null when the host applies edits live.
/// </param>
public sealed record PreferencesPageContext(AppSettings Working, IServiceProvider? Services, Action Changed, Action<Func<Task>>? RegisterCommit = null);
