using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Capture;
using Shotwise.Core.Capture;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseCaptureServiceCollectionExtensions
{
    /// <summary>
    /// Registers capture engines: IMonitorService, IScreenCaptureService, IWindowFinder, IElementFinder, ICaptureMetadataProvider,
    /// IScrollingCaptureService, IPanoramicCaptureService, IMenuCaptureService, IStepCaptureService, IClipboardImageWatcher, IPrinterCaptureService.
    /// </summary>
    public static IServiceCollection AddShotwiseCapture(this IServiceCollection services)
    {
        services.TryAddSingleton<IMonitorService, MonitorService>();
        services.TryAddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.TryAddSingleton<IWindowFinder, WindowFinder>();
        services.TryAddSingleton<ElementFinder>();
        services.TryAddSingleton<IElementFinder>(sp => sp.GetRequiredService<ElementFinder>());
        services.TryAddSingleton<ICaptureMetadataProvider, CaptureMetadataProvider>();
        services.TryAddSingleton<IScrollingCaptureService, ScrollingCaptureService>();
        services.TryAddSingleton<IPanoramicCaptureService, PanoramicCaptureService>();
        services.TryAddSingleton<IMenuCaptureService, MenuCaptureService>();
        services.TryAddSingleton<IStepCaptureService, StepCaptureService>();
        services.TryAddSingleton<ClipboardImageWatcher>();
        services.TryAddSingleton<IClipboardImageWatcher>(sp => sp.GetRequiredService<ClipboardImageWatcher>());
        services.TryAddSingleton<IPrinterCaptureService, PrinterCaptureService>();
        return services;
    }
}
