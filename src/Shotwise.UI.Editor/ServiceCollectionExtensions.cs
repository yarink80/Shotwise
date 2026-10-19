using Microsoft.Extensions.DependencyInjection.Extensions;
using Shotwise.Core.Documents;
using Shotwise.UI.Editor;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseUiEditorServiceCollectionExtensions
{
    /// <summary>
    /// Registers editor UI: <see cref="IEditorHost"/> (main editor window, tray, library view, video view, panes, share menu).
    /// Requires AddShotwiseCore, AddShotwiseEditor, AddShotwiseRendering and AddShotwiseUiCommon/UiDialogs; library, sharing,
    /// media, vision and capture services are optional (features that need a missing service report themselves unavailable).
    /// </summary>
    public static IServiceCollection AddShotwiseUiEditor(this IServiceCollection services)
    {
        services.TryAddSingleton<EditorHost>();
        services.TryAddSingleton<IEditorHost>(sp => sp.GetRequiredService<EditorHost>());
        return services;
    }

    /// <summary>Alias with the capitalisation used in the work-package text.</summary>
    public static IServiceCollection AddShotwiseUIEditor(this IServiceCollection services) => services.AddShotwiseUiEditor();
}
