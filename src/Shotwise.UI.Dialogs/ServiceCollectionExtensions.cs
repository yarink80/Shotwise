namespace Microsoft.Extensions.DependencyInjection;

public static class ShotwiseUiDialogsServiceCollectionExtensions
{
    public static IServiceCollection AddShotwiseUiDialogs(this IServiceCollection services)
    {
        services.AddSingleton<Shotwise.UI.Dialogs.DialogService>();
        services.AddSingleton<Shotwise.UI.Common.Dialogs.IDialogService>(p => p.GetRequiredService<Shotwise.UI.Dialogs.DialogService>());
        services.AddSingleton<Shotwise.Core.Sharing.IShareUi>(p => p.GetRequiredService<Shotwise.UI.Dialogs.DialogService>());
        services.AddSingleton<Shotwise.Core.Sharing.IPrintService, Shotwise.UI.Dialogs.PrintService>();
        return services;
    }
    public static IServiceCollection AddShotwiseUIDialogs(this IServiceCollection services) => services.AddShotwiseUiDialogs();
}

