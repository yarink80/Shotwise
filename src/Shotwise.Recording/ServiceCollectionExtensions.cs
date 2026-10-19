using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Media;
using Shotwise.Recording;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseRecordingServiceCollectionExtensions
{
    /// <summary>
    /// Registers recording services: IVideoRecorderFactory, IAudioDeviceService, IWebcamPreviewFactory.
    /// </summary>
    public static IServiceCollection AddShotwiseRecording(this IServiceCollection services)
    {
        services.TryAddSingleton<IVideoRecorderFactory, VideoRecorderFactory>();
        services.TryAddSingleton<IAudioDeviceService, RecordingDeviceService>();
        services.TryAddSingleton<IWebcamPreviewFactory, WebcamPreviewFactory>();
        services.TryAddTransient<IVideoRecorder>(sp => sp.GetRequiredService<IVideoRecorderFactory>().Create());
        services.TryAddTransient<IWebcamPreview>(sp => sp.GetRequiredService<IWebcamPreviewFactory>().Create());
        // Canvases and option-specific overlays are frame resources owned by each recorder;
        // their dimensions/options are supplied at Prepare, not global DI services.
        return services;
    }
}
