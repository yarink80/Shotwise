using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Shotwise.Core.Capture;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Resources;

namespace Shotwise.App;

/// <summary>Placeholder main window shown until UI.Editor provides IEditorHost. Closing hides it to the tray.</summary>
public partial class MainWindow
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        if (_services.GetService<ICaptureCoordinator>() is { } coordinator)
            _ = coordinator.RunAsync(_services.GetRequiredService<IPresetRepository>().Active);
        else
            System.Windows.MessageBox.Show(this, Localizer.Get("MainWindow_NotAvailable"), Localizer.Get("App_Name"));
    }

    private void OnExit(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApplication();

    protected override void OnClosing(CancelEventArgs e)
    {
        var stayInTray = _services.GetService<ISettingsService>()?.Current.General.StayInTray ?? true;
        if (stayInTray && Application.Current is App { ShutdownMode: ShutdownMode.OnExplicitShutdown })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        if (!stayInTray) ((App)Application.Current).ExitApplication();
    }
}
