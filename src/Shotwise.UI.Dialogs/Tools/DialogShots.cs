using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Core.Vision;
using Shotwise.UI.Common.Dialogs;
using Wpf.Ui.Appearance;

namespace Shotwise.UI.Dialogs.Tools;

/// <summary>Opt-in (ShotwiseContentTool) visual review: renders the hand-built dialogs with fake services to PNG files.</summary>
internal static class DialogShots
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        Shotwise.UI.Common.Gallery.StyleGallery.ApplyTheme(Dark ? Shotwise.UI.Common.Theming.EffectiveTheme.Dark : Shotwise.UI.Common.Theming.EffectiveTheme.Light);

        var provider = new ToolServices(); var state = new AppSettings(); state.General.Theme = Dark ? AppTheme.Dark : AppTheme.Light;
        var settings = ToolProxy.Make<ISettingsService>(("get_Current", state));
        IShareDestination Dest(string id, string name, bool account, bool options, bool configured = false)
            => ToolProxy.Make<IShareDestination>(("get_Id", id), ("get_DisplayName", name), ("get_RequiresAccount", account), ("get_CanShowOptions", options), ("get_IsConfigured", configured));
        var destinations = new[] { Dest("file", "Save to file", false, true, true), Dest("ftp", "FTP / SFTP", true, true), Dest("s3", "S3-compatible storage", true, true), Dest("dropbox", "Dropbox", true, false), Dest("googledrive", "Google Drive", true, false, true), Dest("onedrive", "OneDrive", true, true), Dest("slack", "Slack", true, true), Dest("custom-uploader", "Custom uploader", false, true) };
        provider.Items[typeof(IShareService)] = ToolProxy.Make<IShareService>(("get_Destinations", destinations));
        provider.Items[typeof(IEnumerable<Shotwise.UI.Common.Preferences.IPreferencesPage>)] = Array.Empty<Shotwise.UI.Common.Preferences.IPreferencesPage>();
        provider.Items[typeof(IModelManager)] = ToolProxy.Make<IModelManager>(("get_Models", new[]
        {
            new ModelInfo("yunet", "YuNet face detector", "Finds faces for Smart Redact.", new Uri("https://example.org/models/yunet.onnx"), "00", 345_000, "MIT", new Uri("https://example.org/license")),
            new ModelInfo("lama", "LaMa inpainting", "High-quality object removal.", new Uri("https://example.org/models/lama.onnx"), "00", 208_000_000, "Apache-2.0", new Uri("https://example.org/license2")),
        }));
        var png = new ImageFormatInfo(ImageFormat.Png, ".png", "PNG", true, true, true, false, Array.Empty<string>());
        var jpg = new ImageFormatInfo(ImageFormat.Jpeg, ".jpg", "JPEG", true, true, false, false, Array.Empty<string>());
        var pdf = new ImageFormatInfo(ImageFormat.Pdf, ".pdf", "PDF", true, true, false, true, Array.Empty<string>());
        provider.Items[typeof(IImageCodec)] = ToolProxy.Make<IImageCodec>(("get_SupportedFormats", new[] { png, jpg, pdf }));
        var service = new DialogService(provider, settings, NullLogger<DialogService>.Instance);

        foreach (var (page, name) in new[] { (PreferencesPage.General, "general"), (PreferencesPage.Editor, "editor"), (PreferencesPage.Library, "library"), (PreferencesPage.Models, "models"), (PreferencesPage.Share, "share"), (PreferencesPage.Advanced, "advanced"), (PreferencesPage.Experiments, "experiments") })
            Capture(() => service.ShowPreferences(new PreferencesRequest(page)), Path.Combine(directory, "prefs-" + name + ".png"));
        foreach (var id in new[] { "dropbox", "ftp", "slack" })
            Capture(() => service.ShowShareAccounts(new ShareAccountsRequest(id)), Path.Combine(directory, "accounts-" + id + ".png"));
        Capture(() => service.ShowCreateGif(new CreateGifRequest("in.mp4", new VideoInfo(TimeSpan.FromSeconds(3), 640, 360, 30, false, 1200), null, Path.Combine(directory, "out.gif"))), Path.Combine(directory, "dialog-create-gif.png"));
        foreach (var format in new[] { "png", "jpg", "pdf" })
            Capture(() => service.ShowSaveAs(new SaveAsRequest("capture." + format, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), false, new[] { png, jpg, pdf }, format)), Path.Combine(directory, "dialog-save-as" + (format == "png" ? "" : "-" + format) + ".png"));
        Capture(() => service.ShowColorPicker(new ColorPickerRequest(Argb.FromRgb(0x2D, 0x7F, 0xF9))), Path.Combine(directory, "dialog-color.png"));
        Capture(() => service.ShowEffectEditor(new EffectEditorRequest(EffectDefaults.Caption)), Path.Combine(directory, "effect-caption.png"));
        Capture(() => service.ShowEffectEditor(new EffectEditorRequest(new ColorReplaceEffect(new[] { (Argb.White, Argb.Black) }, 0.1))), Path.Combine(directory, "effect-colorreplace.png"));
        Capture(() => service.ShowModelDownload(new ModelDownloadRequest(new[] { "yunet" }, "Face detection needs a model that is not installed yet.")), Path.Combine(directory, "dialog-model-download.png"));
        Capture(() => service.ShowAutoNaming(new AutoNamingRequest(new NamingSettings())), Path.Combine(directory, "dialog-naming.png"));
        Capture(() => service.ShowPageSetup(new PageSetupRequest(Shotwise.UI.Common.Dialogs.PrintSettings.Default)), Path.Combine(directory, "dialog-page-setup.png"));
        app.Shutdown(); Console.WriteLine("Screenshots written to " + directory);
    }

    /// <summary>When set (second command-line argument), only screenshots whose file name equals this are produced.</summary>
    public static bool Dark { get; set; }
    public static string? Only { get; set; }

    private static void Capture(Func<Task> show, string path)
    {
        if (Only is not null && !string.Equals(Path.GetFileNameWithoutExtension(path), Only, StringComparison.OrdinalIgnoreCase)) return;
        var task = show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Application.Current.Windows.OfType<FormWindow>().FirstOrDefault(w => w.IsVisible) is { } window)
            {
                window.UpdateLayout();
                var surface = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                window.Close();
            }
        };
        timer.Start();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        timer.Stop(); task.GetAwaiter().GetResult();
    }
}
