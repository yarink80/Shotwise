using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Media;
using Shotwise.Media;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseMediaServiceCollectionExtensions
{
    /// <summary>
    /// Registers media services: IVideoService, IGifService, IVideoFromImagesSessionFactory.
    /// </summary>
    public static IServiceCollection AddShotwiseMedia(this IServiceCollection services)
    {
        services.TryAddSingleton<IVideoService, VideoService>();
        services.TryAddSingleton<IGifService, GifService>();
        services.TryAddSingleton<IVideoFromImagesSessionFactory, VideoFromImagesSessionFactory>();
        services.TryAddTransient<IVideoFromImagesSession, VideoFromImagesSession>();
        return services;
    }
}
