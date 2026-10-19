using Shotwise.Core.Platform;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Platform;
namespace Microsoft.Extensions.DependencyInjection;
public static class ShotwisePlatformServiceCollectionExtensions
{
    public static IServiceCollection AddShotwisePlatform(this IServiceCollection services)
    {
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<UriActivation>();
        services.AddSingleton<ISingleInstance, SingleInstance>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<IShellRegistration, ShellRegistration>();
        services.AddSingleton<IStartMenuShortcut>(sp => new StartMenuShortcut(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StartMenuShortcut>>()));
        services.AddSingleton<IDesktopShortcut>(sp => new DesktopShortcut(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StartMenuShortcut>>()));
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IPolicyService, PolicyService>();
        // Updater: the App replaces the engine (Velopack) and the restart host (capture/editor guards, graceful exit).
        services.TryAddSingleton<INetworkStatus, NetworkStatus>();
        services.TryAddSingleton<IUpdateEngine>(_ => new UnsupportedUpdateEngine("This copy of Shotwise is not installed by Shotwise-Setup.exe, so it is not updated in place."));
        services.TryAddSingleton<IUpdateRestartHost, NullUpdateRestartHost>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());
        services.AddSingleton<IMobileConnectServer, MobileConnectServer>();
        services.AddSingleton<PlatformDiagnostics>();
        services.AddHostedService(sp => sp.GetRequiredService<PlatformDiagnostics>());
        return services;
    }
}
