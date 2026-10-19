using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Rendering;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseRenderingServiceCollectionExtensions
{
    /// <summary>Registers rendering services: <see cref="IDocumentRenderer"/>, <see cref="IEffectProcessor"/>, <see cref="IImageCodec"/>, <see cref="IPdfExporter"/>.</summary>
    public static IServiceCollection AddShotwiseRendering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IImageCodec, SkiaImageCodec>();
        services.TryAddSingleton<IPdfExporter, PdfExporter>();
        services.TryAddSingleton<IEffectProcessor, SkiaEffectProcessor>();
        services.TryAddSingleton<IDocumentRenderer, SkiaDocumentRenderer>();
        return services;
    }
}
