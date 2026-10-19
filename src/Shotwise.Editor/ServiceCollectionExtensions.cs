using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Platform;
using Shotwise.Editor;
using Shotwise.Editor.Tools;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseEditorServiceCollectionExtensions
{
    /// <summary>
    /// Registers editor-engine services: the tool factory and the shared "last style per tool" memory (persisted to
    /// %APPDATA%\Shotwise\editor-tool-styles.json when <see cref="IAppPaths"/> is registered). Sessions are created per
    /// document by UI.Editor via <c>new EditorSession(doc, EditorServices.FromServiceProvider(sp))</c>.
    /// </summary>
    public static IServiceCollection AddShotwiseEditor(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolFactory, DefaultToolFactory>();
        services.TryAddSingleton<IToolStyleMemory>(sp =>
        {
            var paths = sp.GetService<IAppPaths>();
            var path = paths is null ? null : Path.Combine(paths.AppData, ToolStyleMemory.FileName);
            return new ToolStyleMemory(path, sp.GetService<ILogger<ToolStyleMemory>>());
        });
        return services;
    }
}
