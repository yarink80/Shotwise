using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Core's own implementations: <see cref="IAppPaths"/>, <see cref="ISettingsService"/>, <see cref="IPresetRepository"/>,
    /// <see cref="IShotxSerializer"/>, <see cref="ISnagxImporter"/> and <see cref="ICaptureEvents"/> (all singletons, TryAdd so hosts/tests can override).
    /// </summary>
    public static IServiceCollection AddShotwiseCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<ISettingsService, SettingsService>();
        services.TryAddSingleton<IPresetRepository, PresetRepository>();
        services.TryAddSingleton<IShotxSerializer, ShotxSerializer>();
        services.TryAddSingleton<ISnagxImporter, SnagxImporter>();
        services.TryAddSingleton<ICaptureEvents, CaptureEventHub>();
        return services;
    }
}
