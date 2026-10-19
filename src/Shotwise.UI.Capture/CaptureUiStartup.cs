using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core.Capture;
using Shotwise.UI.Capture.Coordinator;

namespace Shotwise.UI.Capture;

/// <summary>
/// Entry point the application calls once after the host started (UI thread): resolves the coordinator, registers the global hotkeys,
/// hooks clipboard / printer / mobile inputs and opens the capture window / widget per settings. Idempotent.
/// </summary>
public static class CaptureUiStartup
{
    public static void Start(IServiceProvider services, bool showWindows = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<ICaptureCoordinator>() is CaptureCoordinator c) c.Start(showWindows);
    }
}
