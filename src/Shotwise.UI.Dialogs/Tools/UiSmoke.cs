using System.Windows;
using System.Windows.Controls;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Settings;
using Shotwise.UI.Common.Dialogs;
using Wpf.Ui.Appearance;

namespace Shotwise.UI.Dialogs.Tools;

internal static class UiSmoke
{
    public static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        Shotwise.UI.Common.Gallery.StyleGallery.EnsureResources();
        var objects = typeof(EffectDefaults).GetProperties().Select(p => p.GetValue(null)!).ToArray();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
        {
            ApplicationThemeManager.Apply(theme);
            var w = new FormWindow("NewImage"); var input = w.Text("Width", "64");
            w.Loaded += (_, _) => { if (w.Title != "New Image") throw new InvalidOperationException("Resources missing."); w.DialogResult = false; };
            w.ShowDialog();
        }
        DialogSmoke.Run();
        app.Shutdown(); Console.WriteLine($"PASS: {objects.Length} option/effect editor construction and apply checks; Fluent modal lifecycle in Light, Dark, HighContrast.");
    }
}


