using Shotwise.Core.Documents;
using Shotwise.Core.Effects;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Settings;
using Shotwise.Core.Vision;

namespace Shotwise.UI.Common.Dialogs;

/// <summary>
/// Every modal dialog of the editor, declared here so UI.Editor / UI.Capture can open dialogs without referencing
/// UI.Dialogs (which implements it). All methods must be called on the UI thread; they return
/// null (or false) when the user cancels.
/// </summary>
public interface IDialogService
{
    // File / image
    Task<NewImageResult?> ShowNewImage(NewImageRequest request);
    Task<ResizeImageResult?> ShowResizeImage(ResizeImageRequest request);
    Task<ResizeCanvasResult?> ShowResizeCanvas(ResizeCanvasRequest request);
    Task<CreateGifResult?> ShowCreateGif(CreateGifRequest request);
    Task<CombineImagesResult?> ShowCombineImages(CombineImagesRequest request);
    Task<CombineVideosResult?> ShowCombineVideos(CombineVideosRequest request);
    Task<TemplatePickerResult?> ShowTemplatePicker(TemplatePickerRequest request);
    Task ShowOcrResult(OcrResultRequest request);

    // Save / export / print
    Task<SaveAsResult?> ShowSaveAs(SaveAsRequest request);
    Task<AutoNamingResult?> ShowAutoNaming(AutoNamingRequest request);
    Task<BatchExportResult?> ShowBatchExport(BatchExportRequest request);
    Task<PrintResult?> ShowPrint(PrintRequest request);
    Task<PageSetupResult?> ShowPageSetup(PageSetupRequest request);
    Task ShowPrintPreview(PrintPreviewRequest request);

    // Preferences / accounts / presets
    Task ShowPreferences(PreferencesRequest request);
    Task ShowShareAccounts(ShareAccountsRequest request);
    Task ShowDestinationOptions(DestinationOptionsRequest request);
    Task<PresetImportExportResult?> ShowPresetImportExport(PresetImportExportRequest request);

    // Editors / pickers
    Task<EffectEditorResult?> ShowEffectEditor(EffectEditorRequest request);
    Task<ColorPickerResult?> ShowColorPicker(ColorPickerRequest request);
    Task<TagEditorResult?> ShowTagEditor(TagEditorRequest request);
    Task<LibraryLocationResult?> ShowLibraryLocation(LibraryLocationRequest request);
    Task ShowMobileConnect(MobileConnectRequest request);
    Task ShowAbout(AboutRequest request);

    // Messages
    Task ShowMessage(MessageRequest request);
    Task<ConfirmResult> Confirm(ConfirmRequest request);

    // Additive (FX-5). Default bodies keep older implementers/fakes compiling.

    /// <summary>
    /// Shows the licence + size consent and a progress bar for the given downloadable models (Smart Redact faces, HQ inpainting,
    /// noise removal) and downloads them through IModelManager. Returns true when every requested model is available afterwards.
    /// </summary>
    Task<bool> ShowModelDownload(ModelDownloadRequest request) => Task.FromResult(false);

    /// <summary>Full-screen colour eyedropper; returns the sampled colour or null when cancelled.</summary>
    Task<ColorPickerResult?> ShowEyedropper(EyedropperRequest request) => Task.FromResult<ColorPickerResult?>(null);
}

/// <summary>Models to download (ids from IModelManager.Models); <paramref name="Reason"/> is shown above the list, e.g. "Face detection needs a model".</summary>
public sealed record ModelDownloadRequest(IReadOnlyList<string> ModelIds, string? Reason = null) : DialogRequest;

public sealed record EyedropperRequest : DialogRequest;

/// <summary>Base of every dialog request. <see cref="Owner"/> may be a WPF Window, an HWND (IntPtr) or null (active window).</summary>
public abstract record DialogRequest
{
    public object? Owner { get; init; }
}

// ---------------------------------------------------------------- file / image

public sealed record NewImageRequest(Size2 DefaultSize, Argb DefaultBackground, double DefaultDpi = 96) : DialogRequest;
public sealed record NewImageResult(Size2 Size, Argb Background, double Dpi);

public sealed record ResizeImageRequest(Size2 CurrentSize, double Dpi) : DialogRequest;
public sealed record ResizeImageResult(ResizeEffect Resize);

public sealed record ResizeCanvasRequest(Size2 CurrentSize, Argb CurrentColor, double Dpi) : DialogRequest;
public sealed record ResizeCanvasResult(Size2 Size, Anchor9 Anchor, Argb? Fill);

public sealed record CreateGifRequest(string VideoPath, VideoInfo Info, TimeRange? Selection, string SuggestedOutputPath) : DialogRequest;
public sealed record CreateGifResult(GifOptions Options, string OutputPath);

/// <summary>One image offered to Combine Images (tray item).</summary>
public sealed record CombineImageItem(Guid Id, string Name, BgraImage Thumbnail, Size2 Size);
public sealed record CombineImagesRequest(IReadOnlyList<CombineImageItem> Items) : DialogRequest;
/// <summary>Chosen layout template, slot order (item ids) and optional caption.</summary>
public sealed record CombineImagesResult(TemplateDefinition Template, IReadOnlyList<Guid> Order, string? Caption);

public sealed record CombineVideosRequest(IReadOnlyList<string> Paths, string SuggestedOutputPath) : DialogRequest;
public sealed record CombineVideosResult(IReadOnlyList<string> OrderedPaths, string OutputPath, VideoExportOptions Options);

public sealed record TemplatePickerRequest(int? ImageCount = null) : DialogRequest;
public sealed record TemplatePickerResult(TemplateDefinition Template);

public sealed record OcrResultRequest(OcrResult Result, TableResult? Table = null) : DialogRequest;

// ---------------------------------------------------------------- save / export / print

/// <summary>Save As: <paramref name="Formats"/> come from IImageCodec.SupportedFormats (plus "shotx", "mp4", "gif" as applicable).</summary>
public sealed record SaveAsRequest(string SuggestedFileName, string? InitialFolder, bool IsVideo, IReadOnlyList<ImageFormatInfo> Formats, string DefaultFormat, EncodeOptions? DefaultOptions = null) : DialogRequest;
public sealed record SaveAsResult(string Path, string Format, EncodeOptions Options);

public sealed record AutoNamingRequest(NamingSettings Current, DocumentMetadata? Sample = null) : DialogRequest;
public sealed record AutoNamingResult(NamingSettings Settings);

public sealed record BatchExportRequest(IReadOnlyList<string> Files) : DialogRequest;
public sealed record BatchExportResult(IReadOnlyList<string> Files, IReadOnlyList<EffectSpec> Effects, string OutputFolder, string Format, EncodeOptions Options, bool UseAutomaticNaming, NamingSettings? Naming);

/// <summary>Print configuration (printer, page setup, layout, multi-image grid).</summary>
public sealed record PrintSettings
{
    public string? PrinterName { get; init; }
    public int Copies { get; init; } = 1;
    public PaperSize Paper { get; init; } = PaperSize.Letter;
    public PageOrientation Orientation { get; init; } = PageOrientation.Auto;
    public PageImageLayout Layout { get; init; } = PageImageLayout.FitToPage;
    public Anchor9 Position { get; init; } = Anchor9.Center;
    public Thickness2 MarginsInches { get; init; } = new(0.5);
    public double ScalePercent { get; init; } = 100;
    public bool KeepAspect { get; init; } = true;
    /// <summary>Multiple images per page: grid columns × rows (1×1 = one image per page).</summary>
    public int Columns { get; init; } = 1;
    public int Rows { get; init; } = 1;
    public double SpacingInches { get; init; } = 0.25;
    public bool PrintFileNames { get; init; }
    public bool RememberSettings { get; init; } = true;

    public static PrintSettings Default { get; } = new();
}

public sealed record PrintRequest(IReadOnlyList<BgraImage> Pages, IReadOnlyList<string>? Names = null, PrintSettings? Settings = null, bool QuickPrint = false) : DialogRequest;
public sealed record PrintResult(bool Printed, PrintSettings Settings);

public sealed record PageSetupRequest(PrintSettings Current) : DialogRequest;
public sealed record PageSetupResult(PrintSettings Settings);

public sealed record PrintPreviewRequest(IReadOnlyList<BgraImage> Pages, PrintSettings Settings) : DialogRequest;

// ---------------------------------------------------------------- preferences / accounts / presets

public enum PreferencesPage { CaptureGeneral, Capture, Hotkeys, General, Editor, Library, Share, Advanced, Experiments, Models }

public sealed record PreferencesRequest(PreferencesPage InitialPage = PreferencesPage.General) : DialogRequest;

public sealed record ShareAccountsRequest(string? DestinationId = null) : DialogRequest;

public sealed record DestinationOptionsRequest(string DestinationId) : DialogRequest;

public enum PresetTransferMode { Import, Export }
public sealed record PresetImportExportRequest(PresetTransferMode Mode, IReadOnlyList<string>? PreselectedIds = null) : DialogRequest;
public sealed record PresetImportExportResult(string Path, IReadOnlyList<string> PresetIds);

// ---------------------------------------------------------------- editors / pickers

public sealed record EffectEditorRequest(EffectSpec Initial, BgraImage? PreviewSource = null) : DialogRequest;
public sealed record EffectEditorResult(EffectSpec Effect);

public sealed record ColorPickerRequest(Argb Initial, bool AllowAlpha = true, string? Title = null) : DialogRequest;
public sealed record ColorPickerResult(Argb Color);

public sealed record TagEditorRequest(IReadOnlyList<string> CurrentTags, IReadOnlyList<string> AllTags) : DialogRequest;
public sealed record TagEditorResult(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public enum LibraryLocationAction { Move, Merge, UseWithoutMoving }
public sealed record LibraryLocationRequest(string CurrentRoot) : DialogRequest;
public sealed record LibraryLocationResult(string NewRoot, LibraryLocationAction Action);

public sealed record MobileConnectRequest : DialogRequest;

public sealed record AboutRequest : DialogRequest;

// ---------------------------------------------------------------- messages

public enum MessageKind { Info, Success, Warning, Error }

public sealed record MessageRequest(string Title, string Message, MessageKind Kind = MessageKind.Info, string? Details = null) : DialogRequest;

/// <param name="DontAskAgainKey">When set, the dialog shows a "Don't ask again" checkbox (caller persists the choice).</param>
public sealed record ConfirmRequest(string Title, string Message, string? ConfirmText = null, string? CancelText = null, bool IsDestructive = false, string? DontAskAgainKey = null) : DialogRequest;
public sealed record ConfirmResult(bool Confirmed, bool DontAskAgain);
