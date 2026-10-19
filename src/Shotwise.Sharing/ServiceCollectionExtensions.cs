using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Destinations;
using Shotwise.Sharing.Destinations.Graph;
using Shotwise.Sharing.Infrastructure;
using Shotwise.Sharing.Naming;
using Shotwise.Sharing.Office;

namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseSharingServiceCollectionExtensions
{
    /// <summary>
    /// Registers sharing services: IShareService, every IShareDestination, ICredentialStore, IOAuthHelper, IBatchExporter.
    /// IClipboardWriter and IShareUi are provided by the UI layers; IPolicyService, ILibraryService, INotificationService,
    /// IDocumentRenderer, IImageCodec, IEffectProcessor are optional/used when registered.
    /// </summary>
    public static IServiceCollection AddShotwiseSharing(this IServiceCollection services)
    {
        services.TryAddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.TryAddSingleton<IOAuthHelper>(sp => new OAuthHelper(log: sp.GetService<ILogger<OAuthHelper>>()));
        services.TryAddSingleton(sp => new ShareContext(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<IOAuthHelper>(),
            sp.GetService<ISettingsService>(),
            sp.GetService<IClipboardWriter>(),
            http: null,
            loggerFactory: sp.GetService<ILoggerFactory>(),
            codec: sp.GetService<IImageCodec>()));
        services.TryAddSingleton<IMicrosoftTokenProvider>(sp => new MsalTokenProvider(sp.GetRequiredService<ShareContext>()));

        // Order = default order in the Share menu.
        AddDestination(services, c => new FileDestination(c));
        AddDestination(services, c => new ClipboardDestination(c));
        // IPrintService (UI.Dialogs) is optional and resolved lazily on first print (CONTRACT A17).
        services.AddSingleton<IShareDestination>(sp => new PrintDestination(sp.GetRequiredService<ShareContext>(), () => sp.GetService<IPrintService>()));
        AddDestination(services, c => new EmailDestination(c));
        AddDestination(services, c => new FtpDestination(c));
        AddDestination(services, c => new ProgramDestination(c));
        AddDestination(services, c => new WordDestination(c));
        AddDestination(services, c => new ExcelDestination(c));
        AddDestination(services, c => new PowerPointDestination(c));
        AddDestination(services, c => new OneNoteDestination(c));
        services.AddSingleton<IShareDestination>(sp => new OneDriveDestination(sp.GetRequiredService<ShareContext>(), sp.GetRequiredService<IMicrosoftTokenProvider>()));
        services.AddSingleton<IShareDestination>(sp => new SharePointDestination(sp.GetRequiredService<ShareContext>(), sp.GetRequiredService<IMicrosoftTokenProvider>()));
        services.AddSingleton<IShareDestination>(sp => new TeamsDestination(sp.GetRequiredService<ShareContext>(), sp.GetRequiredService<IMicrosoftTokenProvider>()));
        AddDestination(services, c => new GoogleDriveDestination(c));
        AddDestination(services, c => new YouTubeDestination(c));
        AddDestination(services, c => new DropboxDestination(c));
        AddDestination(services, c => new BoxDestination(c));
        AddDestination(services, c => new SlackDestination(c));
        AddDestination(services, c => new ImgurDestination(c));
        AddDestination(services, c => new S3Destination(c));
        AddDestination(services, c => new CustomHttpDestination(c));
        AddDestination(services, c => new XDestination(c));
        AddDestination(services, c => new EvernoteDestination(c));

        services.TryAddSingleton<IShareService>(sp => new ShareService(
            sp.GetServices<IShareDestination>(),
            sp.GetRequiredService<IAppPaths>(),
            sp.GetService<ISettingsService>(),
            sp.GetService<IPolicyService>(),
            sp.GetService<ILibraryService>(),
            sp.GetService<INotificationService>(),
            sp.GetService<IClipboardWriter>(),
            sp.GetService<IDocumentRenderer>(),
            sp.GetService<IImageCodec>(),
            sp.GetService<IShotxSerializer>(),
            sp.GetService<ILogger<ShareService>>()));

        services.TryAddSingleton<IBatchExporter>(sp => new BatchExporter(
            sp.GetRequiredService<IImageCodec>(),
            sp.GetService<IEffectProcessor>(),
            sp.GetService<ISettingsService>(),
            sp.GetService<ILogger<BatchExporter>>()));
        return services;
    }

    private static void AddDestination(IServiceCollection services, Func<ShareContext, IShareDestination> factory)
        => services.AddSingleton(sp => factory(sp.GetRequiredService<ShareContext>()));
}
