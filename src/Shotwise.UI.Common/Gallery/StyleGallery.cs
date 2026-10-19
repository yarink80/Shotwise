using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shotwise.UI.Common.Theming;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Shotwise.UI.Common.Gallery;

/// <summary>Helpers for the style gallery (also usable by tools): resource bootstrap + theme switching without a settings service.</summary>
public static class StyleGallery
{
    private static readonly Uri SharedResourcesUri = new("pack://application:,,,/Shotwise.UI.Common;component/Resources/SharedResources.xaml", UriKind.Absolute);

    /// <summary>Makes sure WPF-UI and the Shotwise dictionaries are merged into the application (no-op in the real app).</summary>
    public static void EnsureResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var needWpfUi = !app.Resources.Contains("ApplicationBackgroundBrush"); // Contains searches merged dictionaries too
        var needShotwise = !app.Resources.Contains("Shotwise.FocusVisual");
        if (!needWpfUi && !needShotwise) return;
        // Assign a new dictionary through the Resources setter (like App.xaml does): adding merged dictionaries to the implicit,
        // lazily created Application.Resources does not reliably reach StaticResource lookups made while loading BAML.
        var root = new ResourceDictionary();
        var old = app.Resources;
        foreach (var d in old.MergedDictionaries.ToList()) { old.MergedDictionaries.Remove(d); root.MergedDictionaries.Add(d); }
        foreach (var key in old.Keys.Cast<object>().ToList()) root[key] = old[key];
        if (needWpfUi)
        {
            root.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Light });
            root.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        }
        if (needShotwise) root.MergedDictionaries.Add(new ResourceDictionary { Source = SharedResourcesUri });
        app.Resources = root;
    }

    /// <summary>Applies WPF-UI + Shotwise tokens for a theme (what ThemeService does, minus settings).</summary>
    public static void ApplyTheme(EffectiveTheme theme)
    {
        EnsureResources();
        ApplicationThemeManager.Apply(theme == EffectiveTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, false);
        ShotwiseTheme.Apply(theme);
    }
}

/// <summary>
/// Window hosting the <see cref="StyleGalleryView"/> (for a hidden <c>--gallery</c> switch in the App:
/// <c>new StyleGalleryWindow().Show()</c>). Light/Dark toggle in the header switches the whole app's tokens.
/// </summary>
public class StyleGalleryWindow : Window
{
    public StyleGalleryWindow()
    {
        StyleGallery.EnsureResources();
        Title = "Shotwise design system";
        Width = 1540;
        Height = 960;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Shotwise.Surface0Brush");
        Content = new ScrollViewer { Content = new StyleGalleryView(), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

/// <summary>Renders the gallery to PNG (e.g. design-system-light.png / -dark.png) for visual checks of the design system.</summary>
public static class StyleGalleryRenderer
{
    /// <param name="outDir">Target directory.</param>
    /// <param name="only">"light", "dark" or null for both.</param>
    public static void RenderAll(string outDir, string? only = null)
    {
        Directory.CreateDirectory(outDir);
        StyleGallery.EnsureResources();
        foreach (var theme in new[] { EffectiveTheme.Light, EffectiveTheme.Dark })
        {
            var name = theme == EffectiveTheme.Dark ? "dark" : "light";
            if (only is not null && !string.Equals(only, name, StringComparison.OrdinalIgnoreCase)) continue;
            StyleGallery.ApplyTheme(theme);
            var path = Path.Combine(outDir, $"design-system-{name}.png");
            Render(new StyleGalleryView(), path);
        }
    }

    /// <summary>Lays out an element off-screen in a borderless window and saves it as PNG at 96 DPI.</summary>
    public static void Render(FrameworkElement element, string path, double scale = 1.0)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, AllowsTransparency = false,
            Left = -30000, Top = -30000, SizeToContent = SizeToContent.WidthAndHeight, Content = element,
        };
        window.Show();
        element.Measure(new Size(element.Width > 0 ? element.Width : 1600, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        // let bindings, templates and loaded handlers settle
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        var w = (int)Math.Ceiling(element.ActualWidth * scale);
        var h = (int)Math.Ceiling(element.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) encoder.Save(fs);
        window.Close();
    }
}
