using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.UI.Capture;
using Shotwise.UI.Capture.Coordinator;
using Shotwise.UI.Capture.Selection;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseUiCaptureServiceCollectionExtensions
{
    /// <summary>
    /// Registers capture UI: <see cref="ICaptureCoordinator"/> (and the concrete <see cref="CaptureCoordinator"/>), the selection overlay
    /// service. Engine services (screen capture, window/element finder, scrolling, recorder, OCR, effects, share, hotkeys ...) are resolved
    /// lazily from DI by their Core interfaces. Call <see cref="CaptureUiStartup.Start(IServiceProvider, bool)"/> once the host started.
    /// </summary>
    public static IServiceCollection AddShotwiseUiCapture(this IServiceCollection services)
    {
        services.TryAddSingleton<ISelectionService>(sp => new SelectionService(
            sp.GetRequiredService<IWindowFinder>(),
            sp.GetService<IElementFinder>(),
            sp.GetService<IScrollingCaptureService>(),
            sp.GetService<IMonitorService>(),
            sp.GetService<ILogger<SelectionService>>()));
        services.TryAddSingleton<CaptureCoordinator>();
        services.TryAddSingleton<ICaptureCoordinator>(sp => sp.GetRequiredService<CaptureCoordinator>());
        // Effective hotkey registrations (fallbacks, problems) for the tray, editor menus and the shortcut sheet (POLISH-1).
        services.TryAddSingleton<Shotwise.Core.Platform.IHotkeyStatusSource>(sp => sp.GetRequiredService<CaptureCoordinator>());
        // Preferences pages contributed to the shared Preferences window (CONTRACT A6/A21). Built-in "General" is order 0.
        services.AddSingleton<Shotwise.UI.Common.Preferences.IPreferencesPage>(sp => new Shotwise.UI.Capture.Preferences.CapturePreferencesPageContribution(Shotwise.UI.Capture.Preferences.CapturePreferencePages.GeneralId, 50, sp));
        services.AddSingleton<Shotwise.UI.Common.Preferences.IPreferencesPage>(sp => new Shotwise.UI.Capture.Preferences.CapturePreferencesPageContribution(Shotwise.UI.Capture.Preferences.CapturePreferencePages.CaptureId, 100, sp));
        services.AddSingleton<Shotwise.UI.Common.Preferences.IPreferencesPage>(sp => new Shotwise.UI.Capture.Preferences.CapturePreferencesPageContribution(Shotwise.UI.Capture.Preferences.CapturePreferencePages.HotkeysId, 200, sp));
        return services;
    }

    /// <summary>Alias with the casing used in the work-package text.</summary>
    public static IServiceCollection AddShotwiseUICapture(this IServiceCollection services) => services.AddShotwiseUiCapture();
}
