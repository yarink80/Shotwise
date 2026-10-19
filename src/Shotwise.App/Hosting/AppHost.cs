using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shotwise.App.Logging;
using Shotwise.App.Tray;
using Shotwise.Core.Platform;
using Shotwise.Core.Settings;

namespace Shotwise.App.Hosting;

/// <summary>Composition root: builds the Generic Host and registers every library's services.</summary>
public static class AppHost
{
    public static IHost Build(string[] args, AppPaths paths, SettingsService settings, bool smoke = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Shotwise",
        });

        // Logging: rolling files in %LOCALAPPDATA%\Shotwise\logs only (no console in a WinExe).
        builder.Logging.ClearProviders();
        var minLevel = Enum.TryParse<LogLevel>(settings.Current.Advanced.LogLevel, true, out var lvl) ? lvl : LogLevel.Information;
        builder.Logging.SetMinimumLevel(minLevel);
        builder.Logging.AddProvider(new RollingFileLoggerProvider(paths.Logs, settings.Current.Advanced.MaxLogFiles, minLevel));

        // Instances created before the host (needed for logging/theme/language) are shared, not duplicated.
        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<ISettingsService>(settings);

        builder.Services
            .AddShotwiseCore()
            .AddShotwiseEditor()
            .AddShotwiseRendering()
            .AddShotwiseCapture()
            .AddShotwiseRecording()
            .AddShotwiseMedia()
            .AddShotwiseVision()
            .AddShotwiseLibrary()
            .AddShotwiseSharing()
            .AddShotwisePlatform()
            .AddShotwiseUiCommon()
            .AddShotwiseUiCapture()
            .AddShotwiseUiDialogs()
            .AddShotwiseUiEditor();

        // Composition-root adapters (P3 integration).
        builder.Services.AddSingleton<Shotwise.Library.Catalog.IMediaThumbnailSource, MediaThumbnailSource>();   // CONTRACT A19
        builder.Services.AddSingleton<CaptureLibraryBridge>();                                                    // CONTRACT A22/A26
        builder.Services.AddSingleton<Shotwise.Core.Library.ICaptureAutosave>(sp => sp.GetRequiredService<CaptureLibraryBridge>());
        builder.Services.AddSingleton<StartupCoordinator>();
        builder.Services.AddSingleton<UriImportHandler>();                                                        // PLT-06 / R2-P2 shotwise: scheme

        // Updater (docs/UPDATES.md): Velopack + GitHub Releases for installed copies; replaces Platform's "unsupported" defaults.
        builder.Services.AddSingleton<Shotwise.Platform.IUpdateEngine>(sp => VelopackUpdateEngine.Create(smoke, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Velopack")));
        builder.Services.AddSingleton<IUpdateRestartHost, AppUpdateRestartHost>();
        builder.Services.AddSingleton<UpdateNotifier>();

        builder.Services.AddSingleton<TrayIconManager>();
        builder.Services.AddSingleton<MainWindow>();

        // Fail fast on missing / mis-scoped registrations (P3 B1).
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        return builder.Build();
    }

    /// <summary>Deletes stale files from %TEMP%\Shotwise (best effort; first instance only).</summary>
    public static void CleanTemp(IAppPaths paths, ILogger log)
    {
        try
        {
            if (!Directory.Exists(paths.Temp)) return;
            var cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (var f in Directory.EnumerateFiles(paths.Temp, "*", SearchOption.AllDirectories))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Temp cleanup skipped");
        }
    }
}
