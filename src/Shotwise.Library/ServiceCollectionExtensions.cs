using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Library;
using Shotwise.Core.Styles;
using Shotwise.Library;
using Shotwise.Library.Stamps;
using Shotwise.Library.Styles;
using Shotwise.Library.Templates;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseLibraryServiceCollectionExtensions
{
    /// <summary>
    /// Registers library services: <see cref="ILibraryService"/> (also resolvable as <see cref="LibraryService"/>),
    /// <see cref="IStyleRepository"/>, <see cref="IStampCatalog"/> and <see cref="ITemplateCatalog"/> (also as <see cref="TemplateCatalog"/>).
    /// Requires <c>AddShotwiseCore()</c> (IAppPaths, ISettingsService, IShotxSerializer). <c>IImageCodec</c> (Rendering) and
    /// <see cref="IMediaThumbnailSource"/> are optional. The host must call <c>ILibraryService.InitializeAsync</c> once at startup.
    /// </summary>
    public static IServiceCollection AddShotwiseLibrary(this IServiceCollection services)
    {
        services.TryAddSingleton<LibraryService>();
        services.TryAddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
        services.TryAddSingleton<IStyleRepository, StyleRepository>();
        services.TryAddSingleton<IStampCatalog, StampCatalog>();
        services.TryAddSingleton<TemplateCatalog>();
        services.TryAddSingleton<ITemplateCatalog>(sp => sp.GetRequiredService<TemplateCatalog>());
        return services;
    }
}
