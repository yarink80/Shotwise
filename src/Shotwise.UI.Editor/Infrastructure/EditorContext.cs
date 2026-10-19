using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Imaging;
using Shotwise.Core.Library;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Shotwise.Core.Settings;
using Shotwise.Core.Sharing;
using Shotwise.Core.Styles;
using Shotwise.Core.Vision;
using Shotwise.Editor;
using Shotwise.UI.Common.Dialogs;

namespace Shotwise.UI.Editor.Infrastructure;

/// <summary>Everything the editor UI needs from DI, resolved once. Optional engines are null when not registered.</summary>
internal sealed class EditorContext
{
    public EditorContext(IServiceProvider sp)
    {
        Services = sp;
        Settings = sp.GetRequiredService<ISettingsService>();
        Renderer = sp.GetRequiredService<IDocumentRenderer>();
        Effects = sp.GetRequiredService<IEffectProcessor>();
        Codec = sp.GetRequiredService<IImageCodec>();
        Dialogs = sp.GetRequiredService<IDialogService>();
        Serializer = sp.GetRequiredService<IShotxSerializer>();
        Snagx = sp.GetService<ISnagxImporter>();
        Library = sp.GetService<ILibraryService>();
        Share = sp.GetService<IShareService>();
        ShareUi = sp.GetService<IShareUi>();
        Styles = sp.GetService<IStyleRepository>();
        Stamps = sp.GetService<IStampCatalog>();
        Templates = sp.GetService<ITemplateCatalog>();
        Video = sp.GetService<IVideoService>();
        Gif = sp.GetService<IGifService>();
        Capture = sp.GetService<ICaptureCoordinator>();
        Ocr = sp.GetService<IOcrService>();
        Tables = sp.GetService<ITableExtractor>();
        Clipboard = sp.GetService<IClipboardWriter>();
        Policy = sp.GetService<IPolicyService>();
        Updates = sp.GetService<IUpdateService>();
        CaptureAutosave = sp.GetService<ICaptureAutosave>();
        VideoFromImages = sp.GetService<IVideoFromImagesSessionFactory>();
        Models = sp.GetService<IModelManager>();
        Paths =sp.GetService<IAppPaths>() ?? new AppPaths();
        LoggerFactory = sp.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        EditorServices = EditorServices.FromServiceProvider(sp);
        Layout = new LayoutStore(Path.Combine(Paths.AppData, "editor-layout.json"));
        QuickEffects = new QuickEffectStore(Path.Combine(Paths.AppData, "quick-effects.json"));
        UiKit.ColorPicker = async (initial, alpha, title) =>
        {
            var r = await Dialogs.ShowColorPicker(new ColorPickerRequest(initial, alpha, title) { Owner = ActiveWindow });
            return r?.Color;
        };
        // Inline colour drop-down sources (CONTRACT A28).
        var styles = Styles;
        ColorSwatch.ThemeColorsProvider = () => styles?.ActiveTheme.ThemeColors ?? (IReadOnlyList<Argb>)Array.Empty<Argb>();
        ColorSwatch.MyColorsProvider = () => Settings.Current.Editor.MyColors;
    }

    public IServiceProvider Services { get; }
    public ISettingsService Settings { get; }
    public IDocumentRenderer Renderer { get; }
    public IEffectProcessor Effects { get; }
    public IImageCodec Codec { get; }
    public IDialogService Dialogs { get; }
    public IShotxSerializer Serializer { get; }
    public ISnagxImporter? Snagx { get; }
    public ILibraryService? Library { get; }
    public IShareService? Share { get; }
    public IShareUi? ShareUi { get; }
    public IStyleRepository? Styles { get; }
    public IStampCatalog? Stamps { get; }
    public ITemplateCatalog? Templates { get; }
    public IVideoService? Video { get; }
    public IGifService? Gif { get; }
    public ICaptureCoordinator? Capture { get; }
    public IOcrService? Ocr { get; }
    public ITableExtractor? Tables { get; }
    public IClipboardWriter? Clipboard { get; }
    public IPolicyService? Policy { get; }
    public IUpdateService? Updates { get; }
    /// <summary>Single owner of new-capture library autosave (App); the editor only links to its items (CONTRACT A26).</summary>
    public ICaptureAutosave? CaptureAutosave { get; }
    /// <summary>"Video from Images" recorder (REC-12); null when Media/Recording are not registered.</summary>
    public IVideoFromImagesSessionFactory? VideoFromImages { get; }
    /// <summary>Downloadable vision models (faces, high-quality inpainting); null when Vision is not registered.</summary>
    public IModelManager? Models { get; }
    public IAppPaths Paths { get; }

    /// <summary>
    /// Makes sure the downloadable model behind a feature is installed: when it is missing, explains why and offers the
    /// download (licence + size consent and progress are handled by the dialog service — FX-5 Models flow). Returns true when
    /// the model is available afterwards; features fall back to their basic behaviour otherwise.
    /// </summary>
    public async Task<bool> EnsureModelAsync(string modelId, string reason)
    {
        if (Models is not { } models) return false;
        if (models.IsAvailable(modelId)) return true;
        if (!models.Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))) return false;
        var ok = await Dialogs.ShowModelDownload(new ModelDownloadRequest(new[] { modelId }, reason) { Owner = ActiveWindow });
        return ok || models.IsAvailable(modelId);
    }
    public ILoggerFactory LoggerFactory { get; }
    public Dispatcher Dispatcher { get; }
    public EditorServices EditorServices { get; }
    public LayoutStore Layout { get; }
    public QuickEffectStore QuickEffects { get; }

    /// <summary>The editor window (owner for dialogs); set by the host.</summary>
    public Window? ActiveWindow { get; set; }

    public EditorSettings Prefs => Settings.Current.Editor;

    public ILogger<T> Log<T>() => LoggerFactory.CreateLogger<T>();

    /// <summary>Shows a transient error/info message through the dialog service.</summary>
    public Task ShowErrorAsync(Exception ex)
    {
        var code = ex is ShotwiseException se ? se.Code : ErrorCode.Unknown;
        return Dialogs.ShowMessage(new MessageRequest(ES.T("Error_Title", "Something went wrong"), Shotwise.UI.Common.Resources.Localizer.ErrorMessage(code), MessageKind.Error, ex.Message) { Owner = ActiveWindow });
    }

    public Task InfoAsync(string title, string message, MessageKind kind = MessageKind.Info)
        => Dialogs.ShowMessage(new MessageRequest(title, message, kind) { Owner = ActiveWindow });
}
