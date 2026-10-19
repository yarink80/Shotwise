using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Sharing;
using Shotwise.UI.Common.Clipboard;
using Shotwise.UI.Common.Theming;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseUiCommonServiceCollectionExtensions
{
    /// <summary>Registers shared UI services: <see cref="IThemeService"/> and the WPF <see cref="IClipboardWriter"/>.</summary>
    public static IServiceCollection AddShotwiseUiCommon(this IServiceCollection services)
    {
        services.TryAddSingleton<IThemeService, ThemeService>();
        services.TryAddSingleton<IClipboardWriter, WpfClipboardWriter>();
        return services;
    }
}
