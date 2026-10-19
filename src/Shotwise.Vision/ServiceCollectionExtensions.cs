using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Capture;
using Shotwise.Core.Media;
using Shotwise.Core.Vision;
using Shotwise.Vision;
using Shotwise.Vision.Inpainting;
using Shotwise.Vision.Stitching;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseVisionServiceCollectionExtensions
{
    /// <summary>
    /// Registers vision services: IOcrService, ITableExtractor, ITextEditService, IInpaintingService, ISmartMoveService, ISimplifyService,
    /// ISmartRedactService, IFaceDetector, IMagicWandService, IStitcher, INoiseSuppressor, IModelManager.
    /// Requires <c>IAppPaths</c> (registered by AddShotwiseCore) and logging.
    /// </summary>
    public static IServiceCollection AddShotwiseVision(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelManager, ModelManager>();
        services.TryAddSingleton<IOcrService, OcrService>();
        services.TryAddSingleton<ITableExtractor, TableExtractor>();
        services.TryAddSingleton<ITextEditService, TextEditService>();
        services.TryAddSingleton<IInpaintingService, InpaintingService>();
        services.TryAddSingleton<ISmartMoveService, SmartMoveService>();
        services.TryAddSingleton<ISimplifyService, SimplifyService>();
        services.TryAddSingleton<IFaceDetector, FaceDetector>();
        services.TryAddSingleton<ISmartRedactService, SmartRedactService>();
        services.TryAddSingleton<IMagicWandService, MagicWandService>();
        services.TryAddSingleton<IStitcher, Stitcher>();
        services.TryAddSingleton<INoiseSuppressor, NoiseSuppressor>();
        return services;
    }
}
