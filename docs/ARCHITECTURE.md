# Shotwise — Architecture & Contracts

The design of Shotwise: technology choices, project layout, dependency rules and the service contracts in `Shotwise.Core`. Core contracts are shared by every library, so change them deliberately: propose the change in an issue or pull request, and prefer an adapter in the implementing library when a contract is only slightly insufficient.

## 1. Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 8, C# 12, `net8.0-windows10.0.22621.0` (WinRT projections), `SupportedOSPlatformVersion 10.0.19041.0`, x64 only. TFM set once in `Directory.Build.props` (`$(ShotwiseTfm)`); pure-logic libs use `net8.0`. |
| UI | WPF + **WPF-UI** (lepoco, MIT) Fluent controls/themes; **CommunityToolkit.Mvvm**; Segoe Fluent Icons. |
| Rendering | **SkiaSharp** (+ `SkiaSharp.Views.WPF` SKElement) for document rendering, annotations, effects. |
| Win32/DirectX/MF interop | **Microsoft.Windows.CsWin32** (source-generated P/Invoke) + **Vortice.Windows** (Direct3D11, DXGI, Direct2D1, MediaFoundation) + WinRT projections (Windows.Graphics.Capture, Windows.Media.Ocr, Windows.Media.Capture, Windows.Data.Pdf). |
| Audio | **NAudio** (WASAPI capture + loopback, resampling). |
| CV / ML | **OpenCvSharp4** (+ `OpenCvSharp4.runtime.win`), **Microsoft.ML.OnnxRuntime** (CPU; DirectML optional later). |
| PDF | **PDFsharp** 6 (write); Windows.Data.Pdf (read/render). |
| DB | **Microsoft.Data.Sqlite** (FTS5). |
| DI/Hosting/Logging | Microsoft.Extensions.Hosting / DependencyInjection / Logging (+ simple rolling file logger). |
| Tray | **H.NotifyIcon.Wpf**. Toasts: **Microsoft.Toolkit.Uwp.Notifications** (or CommunityToolkit equivalent). |
| Sharing SDKs | Microsoft.Identity.Client (+Extensions.Msal), FluentFTP, SSH.NET, MimeKit, QRCoder. Graph/Google/Dropbox/Box/Slack/Imgur/S3/custom via documented REST APIs over HttpClient (own SigV4). No vendor SDKs (Microsoft.Graph, Google.Apis, Dropbox.Api, Box) are used. |
| Mobile server | ASP.NET Core Kestrel (FrameworkReference Microsoft.AspNetCore.App) minimal APIs, self-signed cert. |
| Shell ext | C++20 DLL (MSVC v143, Windows SDK) — IThumbnailProvider + IPropertyStore for `.shotx`. |
| Tests | xUnit + plain `Assert` (no FluentAssertions). |

All package versions are pinned centrally in `Directory.Packages.props` (Central Package Management). New packages need a permissive licence (MIT, Apache-2.0 or BSD) and an entry in `THIRD-PARTY-NOTICES.md`.

## 2. Solution layout

```
Shotwise/
  Shotwise.sln
  Directory.Build.props        # TFMs, nullable, LangVersion, TreatWarningsAsErrors=false, Platform x64
  Directory.Packages.props
  src/
    Shotwise.Core/             # net8.0 — model, serialization, commands/undo, settings, presets, styles, all service interfaces
    Shotwise.Editor/           # net8.0 — EditorSession, tool controllers, hit-testing, selection, clipboard model (no WPF, no Skia)
    Shotwise.Rendering/        # TFM-windows — Skia renderer, annotation painters, effects, image codecs, PDF export
    Shotwise.Capture/          # TFM-windows — screen/window/element capture, overlays' data providers, scrolling, menu, step, clipboard watch, printer capture
    Shotwise.Recording/        # TFM-windows — video recorder, audio, webcam, compositor, cursor FX
    Shotwise.Media/            # TFM-windows — video probe/frames/edit render/combine, GIF encoder/decoder
    Shotwise.Vision/           # TFM-windows — OCR, tables, Smart Move, Simplify, Smart Redact, inpainting, wand, stitching, faces, denoise host, model manager
    Shotwise.Library/          # TFM-windows — SQLite catalog, autosave, file watcher, tags, backup/restore, share history
    Shotwise.Sharing/          # TFM-windows — destination registry + all destinations, credential store, OAuth helper
    Shotwise.Platform/         # TFM-windows — hotkeys, single instance/IPC, autostart, URI/file assoc, toasts, updater, mobile connect server, policy/feature switches
    Shotwise.UI.Common/        # WPF lib — theme, resources (Strings.resx), converters, controls (ColorPicker, ShadowEditor, NumericBox, AnglePicker, HotkeyBox), DialogService base
    Shotwise.UI.Capture/       # WPF lib — selection overlay, capture window, widget, recording HUD, panoramic/step HUDs, ScreenDraw, webcam preview, countdown, capture prefs pages
    Shotwise.UI.Dialogs/       # WPF lib — all editor modal dialogs (resize, canvas, GIF, combine, templates, OCR result, save/naming/batch, print/page setup, editor prefs, share settings, accounts, presets import/export, about)
    Shotwise.UI.Editor/        # WPF lib — editor main window, canvas view, toolbars, quick styles, properties & effects panels, tray strip, library view, video view, share menu, undo stack window, smart redact/simplify panes
    Shotwise.App/              # WPF exe — composition root, App.xaml, tray icon, startup, command line
  native/
    Shotwise.ShellExt/         # C++ DLL (vcxproj), built separately by build script
  tests/
    Shotwise.Core.Tests/ Shotwise.Editor.Tests/ Shotwise.Rendering.Tests/ Shotwise.Vision.Tests/ Shotwise.Media.Tests/ Shotwise.Library.Tests/ Shotwise.Sharing.Tests/
  tools/  (build.ps1, run.ps1, package.ps1, release.ps1, icon/stamp/README-image generators)
  docs/
```

### Dependency rules (enforced by review)

```
Core ← Editor ← (UI.Editor)
Core ← Rendering, Capture, Recording, Media, Vision, Library, Sharing, Platform   (libs never reference each other; they talk via Core interfaces resolved from DI)
UI.Common ← UI.Capture, UI.Dialogs, UI.Editor            (UI libs reference Core, Editor (UI.Editor only), UI.Common — never implementation libs)
App → everything (composition root only)
```
Exceptions allowed: Capture → (none; uses `IStitcher` from Core, implemented in Vision). Recording → (uses `INoiseSuppressor` from Core). Every library exposes exactly one DI entry point:

```csharp
namespace Microsoft.Extensions.DependencyInjection;
public static class ShotwiseXxxServiceCollectionExtensions
{
    public static IServiceCollection AddShotwiseXxx(this IServiceCollection services);
}
```

## 3. Core model (Shotwise.Core)

Namespaces: `Shotwise.Core.Geometry`, `.Imaging`, `.Documents`, `.Annotations`, `.Effects`, `.Commands`, `.Styles`, `.Capture`, `.Media`, `.Vision`, `.Library`, `.Sharing`, `.Platform`, `.Settings`, `.Serialization`.

### 3.1 Primitives
```csharp
public readonly record struct Point2(double X, double Y);         // canvas pixels, origin top-left
public readonly record struct Size2(double Width, double Height);
public readonly record struct Rect2(double X, double Y, double Width, double Height) { /* Left/Top/Right/Bottom, Contains, Union, Intersect, Inflate, FromPoints, Normalize */ }
public readonly record struct Argb(uint Value) { byte A,R,G,B; static FromArgb(a,r,g,b); static Parse("#AARRGGBB"|"#RRGGBB"); string ToHex(); static Transparent, Black, White, Red... }

public sealed class BgraImage            // premultiplied BGRA8, row stride = Width*4
{
    public int Width { get; } public int Height { get; } public byte[] Pixels { get; }
    public double DpiX { get; init; } = 96; public double DpiY { get; init; } = 96;
    public BgraImage(int w, int h); public BgraImage(int w, int h, byte[] pixels);
    public BgraImage Clone(); public BgraImage Crop(RectI r);
}
public readonly record struct RectI(int X, int Y, int Width, int Height);
public readonly record struct PointI(int X, int Y);
```

### 3.2 Documents
```csharp
public sealed class ShotDocument                     // one capture / editable file
{
    public Guid Id; public List<Page> Pages;  public DocumentMetadata Metadata;
    public string? FilePath;  public bool IsDirty;    public int FormatVersion = 1;
    public AssetStore Assets;                          // id → bytes (png/svg/pdf), refcounted
}
public sealed class Page
{
    public Guid Id; public Size2 CanvasSize; public double Dpi = 96;
    public CanvasBackground Background;               // Color (Argb), optional Gradient, optional BaseImage (AssetId + offset)
    public double CornerRadius; public ShadowStyle? CanvasShadow;
    public List<Annotation> Annotations;               // list order == z-order (0 = bottom)
    public List<TemplateSection> Sections;             // templates
    public StepCounters StepCounters;                  // next Number / Upper / Lower
    public SimplifySettings Simplify; public SmartRedactSettings SmartRedact;
    public ViewState View;                             // zoom, scroll, fit
}
public sealed record DocumentMetadata(
    DateTimeOffset CaptureDate, string? AppName, string? AppCompany, string? AppVersion,
    string? WindowTitle, string? Url, string? OsVersion, string? LanguageCode,
    string? OcrText, IReadOnlyList<string> Tags, PointI? CursorPosition, string? SourcePresetId);
```
`AssetStore`: `AssetId Add(byte[] data, string mediaType)`, `byte[] Get(AssetId)`, `BgraImage GetImage(AssetId)` (decoded via `IImageCodec` from DI — store holds bytes only), `Remove`, enumerate.

### 3.3 Annotations
Abstract base, polymorphic JSON with discriminator `"kind"`:
```csharp
public abstract class Annotation
{
    public Guid Id; public abstract AnnotationKind Kind { get; }
    public Point2 Start; public Point2 End; public double RotationDegrees;  // bounding definition; points for pen/polygon
    public StrokeStyle Stroke;     // Color, Width, Dash (Solid/Dash/Dot/DashDot), LineJoin
    public FillStyle Fill;         // None | Solid(Argb) | Linear/Radial gradient
    public double Opacity = 1.0;   // 0..1
    public ShadowStyle? Shadow;    // Color, AngleDegrees, Distance, Blur, Opacity
    public bool IsLocked; public Guid? GroupId; public bool IgnoresUndoAll;
    public Dictionary<string, JsonElement>? Extra;   // preserve unknown fields on round-trip
    public abstract Rect2 Bounds { get; }
    public abstract Annotation DeepClone();
}
public enum AnnotationKind { Arrow, Line, Shape, Callout, Text, Stamp, Step, Pen, Highlight, Magnify, Blur, Image, Cursor, DropZone, SimplifyShape }
```
Concrete classes (fields; all public settable, defaults sensible):
- `ArrowAnnotation`: `ArrowCap StartCap, EndCap` (None, Arrow, OpenArrow, Triangle, Round, Square, Diamond, TBar); `double StartCapScale, EndCapScale`; `bool Bezier; Point2? Control1, Control2`.
- `LineAnnotation`: `bool DoubleLine; bool Bezier; Point2? Control1, Control2; ArrowCap StartCap, EndCap` (round/square caps only).
- `ShapeAnnotation`: `ShapeKind Shape` (Rectangle, RoundedRectangle, Ellipse, Polygon); `double CornerRadius; List<Point2> Points` (polygon).
- `CalloutAnnotation`: `string ShapePresetId; List<CalloutTail> Tails` (Tip Point2, Width, Curved); `RichText Text; TextLayoutStyle TextStyle`.
- `TextAnnotation`: `RichText Text; TextLayoutStyle TextStyle; TextDeselectBehavior DeselectBehavior` (DeleteIfEmpty, Keep).
  - `RichText { List<TextRun> Runs }`, `TextRun { string Text; string FontFamily; double FontSize; bool Bold, Italic, Underline, Strikethrough; Argb Color }` (paragraph break = "\n").
  - `TextLayoutStyle { HAlign; VAlign; Thickness Padding; Argb? OutlineColor; double OutlineWidth; double LineSpacing = 1.0; bool AutoSize }`.
- `StampAnnotation`: `AssetId Asset; string? StampId; bool PreserveAspect`.
- `StepAnnotation`: `StepStyle Style` (Circle, Square, CirclePointer, SquarePointer); `StepSequence Sequence` (Number, Upper, Lower); `int Value; Argb TextColor; string FontFamily; Point2? PointerTip`.
- `PenAnnotation`: `List<Point2> Points; PenNib Nib` (Circle, Square, Horizontal, Vertical, Diagonal); `bool Smoothing, Antialias`.
- `HighlightAnnotation`: `List<Point2>? Points` (null = rectangle); blend multiply.
- `MagnifyAnnotation`: `ShapeKind LensShape; double Scale = 2.0; Rect2 SourceRect; bool ShowConnector`.
- `BlurAnnotation`: `ShapeKind Region; BlurKind Mode` (Gaussian, Pixelate, Solid); `double Intensity; Argb SolidColor; RedactCategory? Category`.
- `ImageAnnotation`: `AssetId Asset; RectI? SourceCrop`.
- `CursorAnnotation`: `CursorKind Cursor` (Arrow, Hand, IBeam, Wait, Cross, Move); `double Scale`.
- `DropZoneAnnotation`: `Guid SectionId; AssetId? Content; Point2 ContentOffset; double ContentZoom = 1`.
- `SimplifyShapeAnnotation`: `SimplifyShapeKind Shape` (Rectangle, RoundedRectangle, Ellipse, TextLeft, TextCenter, TextRight, Placeholder); `int PaletteIndex; string? PlaceholderId`.

Template: `TemplateSection { Guid Id; SectionType Type (Header, Body, Footer, Repeatable); Rect2 Bounds; List<Annotation> Graphics; }` plus `TemplateDefinition { string Id, Name; int MaxImages; Page Prototype; }`.

### 3.4 Effects (data only; processed by `IEffectProcessor`)
```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")] public abstract record EffectSpec { public bool Enabled { get; init; } = true; }
public sealed record BorderEffect(Argb Color, double Width, bool Outside = true) : EffectSpec;
public sealed record EdgeEffect(EdgeStyle Style /*DropShadow,Fade,Torn,Wave,Saw,Sharktooth,Bevel*/, EdgeSides Sides, double Size, bool WithShadow, bool WithBorder) : EffectSpec;
public sealed record ShadowEffect(Argb Color, ShadowDirection Direction, double Size, double Blur, double Opacity) : EffectSpec;
public sealed record PerspectiveEffect(bool Shear, double Horizontal, double Vertical, bool Border, bool Shadow) : EffectSpec;
public sealed record PageCurlEffect(Corner Corner, double Size) : EffectSpec;
public sealed record ColorAdjustEffect(double Brightness, double Contrast, double Hue, double Saturation, double Gamma) : EffectSpec;
public sealed record ColorReplaceEffect(IReadOnlyList<(Argb From, Argb To)> Pairs, double Tolerance) : EffectSpec;
public sealed record FilterEffect(FilterKind Kind /*Grayscale,Monochrome,Invert,Sharpen,Soften,Emboss,Sepia*/, double Intensity, double Opacity = 1) : EffectSpec;
public sealed record WatermarkEffect(string ImagePath, double Scale, Anchor9 Position, bool Underlay, double Transparency, bool Emboss, double EmbossDepth, double EmbossAngle) : EffectSpec;
public sealed record CaptionEffect(CaptionFields Fields, string? CustomText, bool PromptAtCapture, string FontFamily, double FontSize, Argb TextColor, Argb BackColor, bool Outside, Anchor9 Position) : EffectSpec;
public sealed record ResizeEffect(double? Percent, int? Width, int? Height, bool KeepAspect, double? Dpi, bool Smooth = true) : EffectSpec;
public sealed record RotateEffect(double Degrees, bool ResizeCanvas, bool FlipH, bool FlipV) : EffectSpec;
public sealed record TrimEffect(bool Auto, int Left, int Top, int Right, int Bottom, double Tolerance = 0) : EffectSpec;
public sealed record SpotlightMagnifyEffect(Rect2 Region, double DimOpacity, double Blur, double Magnify) : EffectSpec;
public sealed record TransparencyEffect(Argb Color, double Tolerance) : EffectSpec;
public sealed record QuickEffect(string Id, string Name, IReadOnlyList<EffectSpec> Effects);
```
Applying an effect to a page = `ApplyEffectCommand`: flattens annotations? **No** — effects apply to the base raster; canvas size may change; annotations are offset if the effect adds margins (processor returns `EffectResult { BgraImage Image; Point2 ContentOffset; }`).

### 3.5 Commands & undo
```csharp
public interface IEditCommand { string Label { get; } void Do(ShotDocument doc); void Undo(ShotDocument doc); bool TryMerge(IEditCommand next) => false; }
public sealed class UndoStack { event Changed; bool CanUndo/CanRedo; void Execute(IEditCommand, ShotDocument); void Undo(); void Redo(); void UndoAll(); IReadOnlyList<string> History; int Position; void Clear(); int MaxDepth = 500; }
public sealed class CompositeCommand : IEditCommand { ... }
// Provided implementations: AddAnnotation, RemoveAnnotations, ModifyAnnotations(before/after clones), ReorderAnnotations,
// Group/Ungroup, FlattenAnnotations (needs renderer: takes a Func<Page, BgraImage> delegate), ReplaceBaseImage(before/after asset ids),
// ResizeCanvas, SetCanvasBackground, ApplyEffect, Crop, CutOut, SetStepCounters.
```
Raster commands store **asset ids** of before/after images (AssetStore keeps them alive while referenced by undo history).

### 3.6 Styles
```csharp
public sealed class QuickStyle { string Id; string Name; ToolKind Tool; Annotation Prototype; bool IsFavorite; bool IsBuiltIn; }
public sealed class Theme { string Id; string Name; int Version = 1; List<QuickStyle> QuickStyles; List<Argb> ThemeColors; string FontFamily; bool DropShadow; bool IsDefault; }
public sealed class Palette { string Id; string Name; List<Argb> Colors; }
public enum ToolKind { Select, Arrow, Line, Shape, Callout, Text, Stamp, Step, Pen, Highlighter, Fill, Eraser, Magnify, Blur, Crop, CutOut, Selection, MagicWand, Move, Simplify, GrabText, Cursor, Favorites }
public interface IStyleRepository { Task<IReadOnlyList<Theme>> GetThemesAsync(); Theme ActiveTheme { get; } Task SaveThemeAsync(Theme); Task DeleteThemeAsync(string id); Task<Theme> ImportAsync(string path); Task ExportAsync(Theme, string path); Task RestoreDefaultsAsync(); IReadOnlyList<Palette> Palettes { get; } event EventHandler Changed; }
public interface IStampCatalog { Task<IReadOnlyList<StampCategory>> GetCategoriesAsync(); Task<IReadOnlyList<StampInfo>> SearchAsync(string query, string? category, string uiCulture); Task<byte[]> LoadAsync(string stampId); Task ImportPackAsync(string path); void ToggleFavorite(string stampId); }
public interface ITemplateCatalog { Task<IReadOnlyList<TemplateDefinition>> GetTemplatesAsync(); Task ImportAsync(string path); Task ExportAsync(TemplateDefinition, string path); }
```
Built-in stamps/templates/themes live in `src/Shotwise.App/Content/` (copied to output) — original content made for Shotwise.

### 3.7 Capture contracts
```csharp
public enum CaptureMode { AllInOne, Image, Video }
public enum SelectionType { Region, Window, Fullscreen, FixedRegion, Freehand, Object, MultipleArea, Menu, ScrollingWindow, Panoramic, GrabText, StepCapture, Clipboard, ListenToClipboard, File }
public sealed record MonitorInfo(string DeviceName, RectI BoundsPx, RectI WorkAreaPx, double DpiScale, bool IsPrimary, bool IsHdr, double SdrWhiteLevelNits, IntPtr HMonitor);
public sealed record WindowInfo(IntPtr Hwnd, string Title, string ProcessName, int ProcessId, RectI BoundsPx, RectI ClientPx, bool IsMinimized);
public sealed record ElementInfo(string Name, string ControlType, RectI BoundsPx, IntPtr OwnerHwnd);
public sealed record ScreenSnapshot(DateTimeOffset Taken, IReadOnlyList<(MonitorInfo Monitor, BgraImage Image)> Monitors, CursorSnapshot? Cursor);
public sealed record CursorSnapshot(BgraImage Image, PointI Hotspot, PointI PositionPx);
public sealed record CaptureResult(BgraImage Image, DocumentMetadata Metadata, IReadOnlyList<Annotation>? InitialAnnotations = null);

public interface IMonitorService { IReadOnlyList<MonitorInfo> GetMonitors(); RectI VirtualScreenPx { get; } event EventHandler DisplaysChanged; }
public interface IScreenCaptureService
{
    Task<ScreenSnapshot> SnapshotAllAsync(bool includeCursor, bool hdrToneMap, CancellationToken ct = default);  // frozen frame for overlay
    Task<BgraImage> CaptureRectAsync(RectI virtualPx, bool includeCursor, CancellationToken ct = default);
    Task<BgraImage> CaptureWindowAsync(IntPtr hwnd, bool clientOnly, bool includeCursor, CancellationToken ct = default); // PrintWindow PW_RENDERFULLCONTENT / WGC
    BgraImage CropSnapshot(ScreenSnapshot s, RectI virtualPx);
    BgraImage ApplyFreehandMask(BgraImage img, IReadOnlyList<PointI> polygonPx);
}
public interface IWindowFinder { WindowInfo? WindowAt(PointI screenPx, IEnumerable<IntPtr> ignore); IReadOnlyList<WindowInfo> EnumerateTopLevel(); WindowInfo? Foreground(); }
public interface IElementFinder { Task<ElementInfo?> ElementAtAsync(PointI screenPx, CancellationToken ct = default); Task<IReadOnlyList<RectI>> ElementRectsForWindowAsync(IntPtr hwnd, CancellationToken ct = default); }
public interface ICaptureMetadataProvider { Task<DocumentMetadata> CollectAsync(IntPtr? hwnd, PointI? cursor, CancellationToken ct = default); } // app name/company/version, title, browser URL via UIA
public interface IScrollingCaptureService { Task<ScrollProbe> ProbeAsync(IntPtr hwnd, PointI at, CancellationToken ct); Task<BgraImage> CaptureAsync(ScrollCaptureRequest req, IProgress<ScrollProgress>? progress, CancellationToken ct); }
public sealed record ScrollProbe(bool CanScrollVertical, bool CanScrollHorizontal, RectI ScrollAreaPx);
public sealed record ScrollCaptureRequest(IntPtr Hwnd, RectI AreaPx, ScrollDirection Direction /*Vertical,Horizontal,Both*/, bool StartAtTopLeft, int StepDelayMs, int MaxFrames = 400);
public sealed record ScrollProgress(int Frames, BgraImage? Preview);
public interface IPanoramicSession : IAsyncDisposable { event EventHandler<BgraImage>? PreviewUpdated; Task StartAsync(RectI areaPx, CancellationToken ct); Task<BgraImage> StopAndStitchAsync(CancellationToken ct); }
public interface IPanoramicCaptureService { IPanoramicSession CreateSession(); }
public interface IMenuCaptureService { Task<BgraImage?> CaptureMenusAsync(bool includeMenuBar, bool cascaded, TimeSpan wait, CancellationToken ct); }
public interface IStepCaptureSession : IAsyncDisposable { event EventHandler<StepCaptured>? Captured; int Count { get; } Task StartAsync(CancellationToken ct); void Pause(); void Resume(); Task<StepCaptureResult> StopAsync(); }
public sealed record StepCaptured(int Index, BgraImage Image, PointI ClickPx, string Action, string? ElementName, DateTimeOffset At);
public sealed record StepCaptureResult(IReadOnlyList<StepCaptured> Steps, string ActionsJsonPath);
public interface IStepCaptureService { IStepCaptureSession CreateSession(StepCaptureOptions o); }
public interface IClipboardImageWatcher { event EventHandler<BgraImage>? ImageCopied; bool IsEnabled { get; set; } }
public interface IPrinterCaptureService { bool IsInstalled { get; } Task InstallAsync(string printerName); Task UninstallAsync(); event EventHandler<IReadOnlyList<BgraImage>>? PagesCaptured; string WatchFolder { get; set; } }
public interface IStitcher   // implemented in Vision
{
    IIncrementalStitcher Create(StitchDirection dir);
}
public interface IIncrementalStitcher : IDisposable { bool AddFrame(BgraImage frame); BgraImage Preview(int maxSide); BgraImage Result(); }
```
Capture **orchestration** (`ICaptureCoordinator`) lives in **UI.Capture** (it owns overlay windows): `Task<CaptureOutcome?> RunAsync(CapturePreset preset, CancellationToken ct)` → shows overlay, performs capture through engine services, applies preset effects (`IEffectProcessor`), runs outputs (`IShareService`) and/or raises `ICaptureEvents.ImageCaptured / VideoCaptured` consumed by Editor + Library.

```csharp
public interface ICaptureEvents { event EventHandler<CaptureResult>? ImageCaptured; event EventHandler<VideoCaptureResult>? VideoCaptured; void Publish(CaptureResult r); void Publish(VideoCaptureResult r); }
public sealed record VideoCaptureResult(string Mp4Path, TimeSpan Duration, Size2 FrameSize, DocumentMetadata Metadata, bool IsGif);
```

### 3.8 Presets
```csharp
public sealed class CapturePreset
{
    public string Id; public string Name; public string? GroupId; public int Order;
    public CaptureMode Mode; public SelectionType Selection; public SelectionOptions SelectionOptions;  // fixed W/H/X/Y, window active/client, menu flags, scroll dir/start, multi-area
    public TimingOptions Timing;      // DelaySeconds?, IntervalSeconds?, ScheduleAt?
    public bool IncludeCursor; public bool PreviewInEditor = true; public bool CopyToClipboard; public string? OpenInProgramId;
    public List<EffectSpec> Effects; public List<OutputBinding> Outputs;  // OutputBinding(DestinationId, JsonElement? Options)
    public RecordingOptions Recording; // fps, quality, mic/system/webcam, cursor fx, countdown, scale1080
    public Hotkey? Hotkey; public bool IsBuiltIn;
}
public readonly record struct Hotkey(VirtualKey Key, ModifierKeys Modifiers) { string ToDisplayString(); static bool TryParse(string, out Hotkey); }
public interface IPresetRepository { IReadOnlyList<CapturePreset> All { get; } IReadOnlyList<PresetGroup> Groups { get; } CapturePreset Active { get; set; } CapturePreset? LastUsed { get; } Task SaveAsync(CapturePreset); Task DeleteAsync(string id); Task ReorderAsync(IReadOnlyList<string> ids); Task ImportAsync(string path); Task ExportAsync(IEnumerable<string> ids, string path); Task ResetDefaultsAsync(); event EventHandler Changed; }
```

### 3.9 Recording contracts
```csharp
public sealed record RecordingOptions(int Fps = 30, int Quality = 70, bool HardwareEncoding = true, bool ScaleTo1080p = true,
    bool RecordMicrophone = true, string? MicrophoneId = null, bool RemoveNoise = false, bool RecordSystemAudio = true,
    bool Webcam = false, string? WebcamId = null, WebcamShape PipShape = WebcamShape.Circle, Corner PipCorner = Corner.BottomRight, double PipSizeFraction = 0.2,
    bool CursorHighlight = false, Argb CursorHighlightColor = default, bool ClickAnimation = false, Argb ClickColor = default,
    bool IncludeCursor = true, bool Countdown = true, bool ShowReadyWindow = true, bool AutoStart = false);
public enum RecorderState { Idle, Ready, Countdown, Recording, Paused, Stopping, Faulted }
public interface IVideoRecorder : IAsyncDisposable
{
    RecorderState State { get; } TimeSpan Elapsed { get; } event EventHandler<RecorderState>? StateChanged;
    event EventHandler<AudioLevels>? Levels;            // mic/system peak 0..1, ~20 Hz
    event EventHandler<string>? Warning;                 // low disk, device lost …
    Task PrepareAsync(RecordingTarget target, RecordingOptions options, string outputMp4Path, CancellationToken ct);
    Task StartAsync(); Task PauseAsync(); Task ResumeAsync(); Task<VideoCaptureResult> StopAsync(); Task CancelAsync(); Task RestartAsync();
    void SetMicrophoneMuted(bool muted); void SetSystemAudioMuted(bool muted); void SetWebcamFullscreen(bool fullscreen);
    void AddOverlay(IFrameOverlay overlay);              // ScreenDraw, cursor fx are overlays composited per frame
}
public sealed record RecordingTarget(RectI? RegionPx, IntPtr? Window, int? MonitorIndex);
public interface IFrameOverlay { void Render(IOverlayCanvas canvas, TimeSpan timestamp, RectI capturePx); } // IOverlayCanvas: simple vector API (DrawLine/Rect/Ellipse/Text/Image) implemented by recorder via Direct2D
public interface IVideoRecorderFactory { IVideoRecorder Create(); }
public interface IAudioDeviceService { IReadOnlyList<DeviceInfo> Microphones(); IReadOnlyList<DeviceInfo> Webcams(); DeviceInfo? DefaultMicrophone(); event EventHandler DevicesChanged; bool IsMicrophoneMuted(string id); }
public interface IWebcamPreview : IDisposable { event EventHandler<BgraImage>? Frame; Task StartAsync(string deviceId, CancellationToken ct); }
public interface IWebcamPreviewFactory { IWebcamPreview Create(); }
public interface INoiseSuppressor { bool IsAvailable { get; } INoiseSuppressorStream CreateStream(int sampleRate, int channels); }  // implemented in Vision
public interface INoiseSuppressorStream : IDisposable { void Process(Span<float> interleaved); }
```

### 3.10 Media contracts
```csharp
public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double Fps, bool HasAudio, long FrameCount);
public sealed record TimeRange(TimeSpan Start, TimeSpan End);
public sealed class VideoEditList { string SourcePath; TimeRange Trim; List<TimeRange> Cuts; IEnumerable<TimeRange> KeptRanges(); } // + undoable commands in Core.Media
public interface IVideoService
{
    Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default);
    Task<BgraImage> GetFrameAsync(string path, TimeSpan at, CancellationToken ct = default);
    Task<IReadOnlyList<BgraImage>> GetThumbnailsAsync(string path, int count, int height, CancellationToken ct = default);
    Task RenderAsync(VideoEditList edl, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct);
    Task CombineAsync(IReadOnlyList<string> inputs, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct);
    Task WriteMetadataAsync(string mp4Path, VideoMetadata md, CancellationToken ct = default);
}
public sealed record GifOptions(GifPreset Preset, int Width, int Height, double Fps, bool Loop = true, bool Dither = true, bool FadeToBlack = false, TimeRange? Range = null);
public enum GifPreset { ScreenVideo, HighMotion, ReducedFileSize, Custom }   // defaults per SPEC MED-04
public interface IGifService
{
    Task CreateFromVideoAsync(string videoPath, string gifPath, GifOptions o, IProgress<double>? p, CancellationToken ct);
    Task CreateFromFramesAsync(IAsyncEnumerable<(BgraImage Frame, TimeSpan Delay)> frames, string gifPath, GifOptions o, CancellationToken ct);
    Task<AnimatedImage> DecodeAsync(string gifPath, CancellationToken ct);   // frames + delays
    GifOptions DefaultsFor(GifPreset preset, int srcWidth, int srcHeight);
}
public interface IVideoFromImagesSession : IAsyncDisposable { /* start/pause/resume/stop over an editor-provided frame source + mic/webcam */ }
```

### 3.11 Rendering contracts
```csharp
public interface IDocumentRenderer
{
    BgraImage RenderPage(ShotDocument doc, Page page, RenderOptions o);           // background + effects-baked base + annotations
    void RenderAnnotation(object skCanvas, ShotDocument doc, Page page, Annotation a);   // skCanvas is SKCanvas (object to keep Core Skia-free)
    BgraImage RenderThumbnail(ShotDocument doc, int maxSide);
    Rect2 MeasureBounds(Annotation a);                                              // includes caps, shadow, text layout
    TextLayoutInfo LayoutText(RichText text, TextLayoutStyle style, double maxWidth);  // caret/hit info for editing
    Geometry2 GetOutline(Annotation a);                                             // for hit testing: list of polylines/closed shapes
}
public sealed record RenderOptions(double Scale = 1, bool IncludeAnnotations = true, IReadOnlySet<Guid>? Exclude = null, bool TransparentBackground = false);
public interface IEffectProcessor { EffectResult Apply(BgraImage src, EffectSpec effect, DocumentMetadata? md); EffectResult ApplyAll(BgraImage src, IEnumerable<EffectSpec> effects, DocumentMetadata? md); BgraImage Preview(BgraImage src, EffectSpec e, int maxSide); }
public sealed record EffectResult(BgraImage Image, Point2 ContentOffset);
public interface IImageCodec
{
    BgraImage Decode(ReadOnlySpan<byte> data);  BgraImage Load(string path);  IReadOnlyList<BgraImage> LoadAllPages(string path); // TIFF/PDF/ICO
    byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null); void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null);
    void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt /*Tiff,Pdf*/, EncodeOptions? o = null);
    IReadOnlyList<ImageFormatInfo> SupportedFormats { get; }  // ext, name, canRead, canWrite, supportsTransparency, options
}
public enum ImageFormat { Png, Jpeg, Bmp, Gif, Tiff, WebP, Ico, Pdf }
public sealed record EncodeOptions(int JpegQuality = 90, JpegSubsampling Subsampling = JpegSubsampling.S420, int BitDepth = 32, bool Transparency = true, PdfPageOptions? Pdf = null, string? PdfSearchableText = null);
public interface IPdfExporter { Task ExportAsync(IReadOnlyList<BgraImage> pages, string path, PdfPageOptions o, IReadOnlyList<OcrResult?>? textLayers, CancellationToken ct); }
public interface IShotxSerializer { Task SaveAsync(ShotDocument doc, string path, BgraImage thumbnail, CancellationToken ct = default); Task<ShotDocument> LoadAsync(string path, CancellationToken ct = default); }  // implemented in Core (System.IO.Compression + System.Text.Json)
public interface ISnagxImporter { bool CanImport(string path); Task<ShotDocument> ImportAsync(string path, CancellationToken ct = default); } // Core, best-effort
```

### 3.12 Vision contracts
```csharp
public sealed record OcrWord(string Text, Rect2 Bounds, double Confidence);
public sealed record OcrLine(string Text, Rect2 Bounds, IReadOnlyList<OcrWord> Words);
public sealed record OcrResult(string Language, string Text, IReadOnlyList<OcrLine> Lines, double Angle);
public interface IOcrService { IReadOnlyList<string> AvailableLanguages { get; } string EngineName { get; } Task<OcrResult> RecognizeAsync(BgraImage img, Rect2? region = null, string? language = null, CancellationToken ct = default); }
public interface ITableExtractor { TableResult Extract(OcrResult ocr); }  // TableResult: rows×cols of strings + ToTsv()/ToHtml()
public sealed record TextStyleSample(Argb TextColor, Argb BackgroundColor, double FontSizePx, bool Bold, string SuggestedFont);
public interface ITextEditService { Task<IReadOnlyList<(OcrLine Line, TextStyleSample Style)>> AnalyzeAsync(BgraImage img, Rect2? region, CancellationToken ct); }
public interface IInpaintingService { InpaintQuality[] Available { get; } Task<BgraImage> InpaintAsync(BgraImage img, Mask mask, InpaintQuality q = InpaintQuality.Fast, CancellationToken ct = default); }
public sealed class Mask { int Width, Height; byte[] Data; /* 0/255 */ static Mask FromRects(..), FromPolygon(..); Rect2 Bounds; }
public sealed record SmartComponent(Guid Id, RectI Bounds, Mask Mask);
public interface ISmartMoveService { Task<double> AutoToleranceAsync(BgraImage img, CancellationToken ct); Task<IReadOnlyList<SmartComponent>> DetectAsync(BgraImage img, double tolerance, CancellationToken ct); }
public interface ISimplifyService { Task<IReadOnlyList<SimplifyShapeAnnotation>> GenerateAsync(BgraImage img, int detail, Palette palette, CancellationToken ct); Task<Palette> DominantColorsAsync(BgraImage img, int k, CancellationToken ct); }
public enum RedactCategory { Email, Phone, CreditCard, Ssn, IpAddress, Url, Date, PostalAddress, Face, Other }
public sealed record RedactionHit(RedactCategory Category, Rect2 Bounds, string? Text);
public interface ISmartRedactService { Task<IReadOnlyList<RedactionHit>> FindAsync(BgraImage img, IReadOnlySet<RedactCategory> categories, CancellationToken ct); }
public interface IMagicWandService { Mask Select(BgraImage img, PointI seed, double tolerance, bool contiguous); Mask Refine(Mask m, int radius); }
public interface IFaceDetector { Task<IReadOnlyList<Rect2>> DetectAsync(BgraImage img, CancellationToken ct); }
public interface IModelManager { IReadOnlyList<ModelInfo> Models { get; } bool IsAvailable(string modelId); Task<string> EnsureAsync(string modelId, IProgress<double>? p, CancellationToken ct); } // download+sha256 to %LOCALAPPDATA%\Shotwise\models
```

### 3.13 Library contracts
```csharp
public sealed record LibraryItem(Guid Id, string Path, MediaKind Kind /*Image,Video,Gif,Other*/, string Name, DateTimeOffset Created, DateTimeOffset Modified,
    long SizeBytes, int Width, int Height, TimeSpan? Duration, string? AppName, string? WindowTitle, string? Url, IReadOnlyList<string> Tags, bool IsFavorite, string ThumbnailPath);
public sealed record LibraryQuery(string? Text = null, MediaKind? Kind = null, string? App = null, string? Website = null, string? Tag = null, DateOnly? From = null, DateOnly? To = null, bool FavoritesOnly = false, LibrarySort Sort = LibrarySort.NewestFirst, int Skip = 0, int Take = 200);
public interface ILibraryService
{
    string LibraryRoot { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task<LibraryItem> AddCaptureAsync(ShotDocument doc, BgraImage flattenedPreview, CancellationToken ct = default); // autosave .shotx
    Task<LibraryItem> AddVideoAsync(VideoCaptureResult video, CancellationToken ct = default);
    Task<LibraryItem> ImportFileAsync(string path, CancellationToken ct = default);
    Task UpdateAsync(LibraryItem item, ShotDocument? doc = null, CancellationToken ct = default);   // after save; reindex text
    Task<IReadOnlyList<LibraryItem>> QueryAsync(LibraryQuery q, CancellationToken ct = default);
    Task<IReadOnlyList<(string Key, int Count)>> GroupCountsAsync(LibraryGroupBy by, CancellationToken ct = default); // Date, App, Website, Tag, Kind
    Task AddTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags); Task RemoveTagsAsync(IEnumerable<Guid> ids, IEnumerable<string> tags); Task<IReadOnlyList<string>> AllTagsAsync();
    Task SetFavoriteAsync(Guid id, bool fav);
    Task DeleteAsync(IEnumerable<Guid> ids, bool toRecycleBin = true);
    Task MoveLibraryAsync(string newRoot, IProgress<double>? p, CancellationToken ct);
    Task BackupAsync(string archivePath, IProgress<double>? p, CancellationToken ct); Task RestoreAsync(string archivePath, IProgress<double>? p, CancellationToken ct);
    Task SetOcrTextAsync(Guid id, string text);
    Task AddShareHistoryAsync(Guid itemId, ShareHistoryEntry e); Task<IReadOnlyList<ShareHistoryEntry>> GetShareHistoryAsync(Guid? itemId = null);
    event EventHandler<LibraryChangedEventArgs>? Changed;
}
public sealed record ShareHistoryEntry(DateTimeOffset At, string DestinationId, string DestinationName, string? Link, string? Title);
```

### 3.14 Sharing contracts
```csharp
public sealed record ShareFile(string Path, MediaKind Kind, string Format /*ext without dot*/, string Title, int Width, int Height, TimeSpan? Duration, bool IsStepCapture);
public sealed record ShareResult(ShareOutcome Outcome /*Succeeded,Failed,Cancelled*/, string? Message = null, string? Link = null, string? ClipboardText = null, IReadOnlyList<string>? Locations = null, Action? OpenDestination = null);
public enum DestinationCategory { BuiltIn, Microsoft, Storage, Social, Uploader, Custom }
public interface IShareDestination
{
    string Id { get; } string DisplayName { get; } string IconGlyph { get; } DestinationCategory Category { get; }
    IReadOnlySet<string> SupportedFormats { get; }    // "png","jpg","gif","agif","mp4","pdf","tiff","bmp","webp","shotx"
    bool SupportsMultiple { get; } bool SupportsStepCapture { get; } bool CanShowOptions { get; } bool RequiresAccount { get; }
    bool IsConfigured { get; }                         // e.g. client id + signed in
    Task<bool> PrepareAsync(IShareUi ui, IReadOnlyList<ShareFile> files, CancellationToken ct);   // may show per-share dialog (title, folder, channel…)
    Task<ShareResult> ShareAsync(IReadOnlyList<ShareFile> files, IProgress<double>? progress, CancellationToken ct);
    Task ShowOptionsAsync(IShareUi ui);                // persistent settings dialog
    Task SignInAsync(IShareUi ui, CancellationToken ct); Task SignOutAsync();
}
public interface IShareUi   // implemented in UI.Dialogs; lets destinations show generic dialogs without referencing WPF
{
    Task<bool> ShowFormAsync(string title, ShareForm form);          // declarative form: fields (text, multiline, choice, checkbox, folder-tree via async loader)
    Task<string?> PickFolderAsync(string title, Func<string?, Task<IReadOnlyList<RemoteFolder>>> loadChildren);
    Task ShowMessageAsync(string title, string message, bool isError = false);
    Task<bool> OpenBrowserForAuthAsync(Uri url);                     // system browser
    IntPtr OwnerHwnd { get; }
}
public interface IShareService
{
    IReadOnlyList<IShareDestination> Destinations { get; }         // enabled + disabled
    IEnumerable<IShareDestination> For(IReadOnlyList<ShareFile> files);   // capability + enablement filtered
    bool IsEnabled(string id); void SetEnabled(string id, bool enabled);
    Task<ShareResult> ShareAsync(string destinationId, IReadOnlyList<ShareFile> files, IShareUi ui, IProgress<double>? p, CancellationToken ct); // prepare+share+history+clipboard link+toast
    Task<IReadOnlyList<ShareFile>> MaterializeAsync(ShotDocument doc, string format, EncodeOptions? o, CancellationToken ct); // export to temp per naming prefs
}
public interface ICredentialStore { void Write(string key, string secret); string? Read(string key); void Delete(string key); }   // Windows Credential Manager
public interface IOAuthHelper { Task<OAuthTokens> AuthorizeAsync(OAuthClientConfig cfg, IShareUi ui, CancellationToken ct); Task<OAuthTokens> RefreshAsync(OAuthClientConfig cfg, OAuthTokens t, CancellationToken ct); } // PKCE + loopback http://127.0.0.1:{port}/
```

### 3.15 Platform contracts
```csharp
public interface IHotkeyService { HotkeyRegistrationResult Register(string id, Hotkey key, Action callback, bool allowOverride); void Unregister(string id); void UnregisterAll(); IReadOnlyDictionary<string, Hotkey> Registered { get; } bool IsPrintScreenTakenBySnipping(); }
public enum HotkeyRegistrationResult { Ok, Duplicate, Unsupported, InUseBySystemOrOtherApp }
public interface ISingleInstance { bool IsFirstInstance { get; } event EventHandler<string[]>? ArgumentsReceived; Task<bool> ForwardToFirstInstanceAsync(string[] args); }
public interface IAutostartService { bool IsEnabled { get; } void SetEnabled(bool enabled); }
public interface IShellRegistration { void RegisterFileAssociations(); void RegisterUriScheme(); bool IsShellExtensionRegistered { get; } }
public interface INotificationService { void Show(string title, string message, NotificationKind kind = NotificationKind.Info, IReadOnlyList<NotificationAction>? actions = null); }
public interface IUpdateService { Task<UpdateInfo?> CheckAsync(CancellationToken ct); Task DownloadAndInstallAsync(UpdateInfo u, IProgress<double>? p, CancellationToken ct); }
public interface IMobileConnectServer { bool IsRunning { get; } int Port { get; } Task StartAsync(CancellationToken ct); Task StopAsync(); PairingInfo CreatePairing(TimeSpan validity); /* QR payload = URL with one-time token */ event EventHandler<ReceivedFile>? FileReceived; }
public interface IPolicyService { bool IsAllowed(PolicyFeature f); }   // HKLM\Software\Policies\Shotwise + settings
public interface IAppPaths { string AppData { get; } string LocalAppData { get; } string Logs { get; } string Temp { get; } string Models { get; } string Content { get; } string DefaultLibraryRoot { get; } }
```

### 3.16 Settings
```csharp
public sealed class AppSettings { GeneralSettings General; CaptureSettings Capture; HotkeySettings Hotkeys; EditorSettings Editor; LibrarySettings Library; ShareSettings Share; AdvancedSettings Advanced; ExperimentsSettings Experiments; PrinterCaptureSettings Printer; MobileSettings Mobile; UpdateSettings Updates; PrivacySettings Privacy; }
// Every preference on the Preferences pages (capture and editor) maps to a property with our default.
public interface ISettingsService { AppSettings Current { get; } Task SaveAsync(); void Update(Action<AppSettings> mutate); event EventHandler<SettingsChangedEventArgs>? Changed; Task ResetAsync(); }  // %APPDATA%\Shotwise\settings.json, atomic write
```

### 3.17 UI-facing services (interfaces in Core, implemented in UI libs)
```csharp
public interface IEditorHost { Task OpenAsync(ShotDocument doc, bool activate = true); Task OpenFileAsync(string path); Task OpenVideoAsync(string path); void ShowEditor(); void ShowLibrary(); bool IsVisible { get; } }   // UI.Editor
public interface ICaptureCoordinator { Task<CaptureOutcome?> RunAsync(CapturePreset preset, CancellationToken ct = default); Task RepeatLastAsync(); void ShowCaptureWindow(); void ToggleWidget(bool show); bool IsCapturing { get; } }   // UI.Capture
public interface IDialogService { /* typed Show* methods for each dialog in UI.Dialogs; returns result records */ }   // UI.Dialogs (methods defined there; UI.Editor references UI.Dialogs' interface assembly section)
```
`IDialogService` is declared in **UI.Common** (so UI.Editor/UI.Capture can depend on it); implemented in UI.Dialogs. Its method list is in `src/Shotwise.UI.Common/Dialogs/IDialogService.cs`.

## 4. Editor logic (Shotwise.Editor)

UI-agnostic interaction engine used by `UI.Editor`'s canvas view.
```csharp
public sealed class EditorSession                    // one per open document
{
    public ShotDocument Document { get; } public Page ActivePage { get; set; } public UndoStack Undo { get; }
    public ToolKind ActiveTool { get; set; } public ITool CurrentTool { get; }
    public SelectionModel Selection { get; }          // selected annotation ids + pixel selection (Mask + bounds) 
    public QuickStyle? ActiveStyle(ToolKind t);  public void SetToolStyle(ToolKind t, QuickStyle s);
    public event EventHandler? Invalidated;           // redraw request (with optional dirty rect)
    public event EventHandler? SelectionChanged; public event EventHandler<TextEditRequest>? BeginTextEdit;
    // input (canvas coordinates, already un-zoomed):
    public void PointerDown(PointerInput e); public void PointerMove(PointerInput e); public void PointerUp(PointerInput e);
    public void KeyDown(KeyInput e); public void DoubleClick(PointerInput e); public void PointerLeave();
    public IReadOnlyList<OverlayPrimitive> GetOverlay(double zoom);   // handles, marquee, crop frame, cutout band, snapping guides, hover outlines — drawn by the view
    public CursorHint CurrentCursor { get; }
    // commands used by menus/shortcuts:
    public void SelectAll(); public void DeselectAll(); public void InvertSelection(); public void DeleteSelection();
    public ClipboardPayload? Copy(); public ClipboardPayload? Cut(); public void Paste(ClipboardPayload p, Point2? at);
    public void Duplicate(); public void Group(); public void Ungroup(); public void BringForward(); public void SendBackward(); public void BringToFront(); public void SendToBack();
    public void Align(AlignKind k); public void Distribute(bool horizontal); public void Flatten(bool all); public void Lock(bool locked);
    public void ApplyStyleToSelection(QuickStyle s); public void ModifySelection(Action<Annotation> mutate, string label);
    public Task ApplyEffectAsync(EffectSpec e); public void ResizeCanvas(Size2 size, Anchor9 anchor, Argb? fill); public void Crop(Rect2 r); public void CutOut(...);
    public bool SnappingEnabled { get; set; }
}
public interface ITool { ToolKind Kind { get; } void Activate(EditorSession s); void Deactivate(); void PointerDown(PointerInput e); void PointerMove(PointerInput e); void PointerUp(PointerInput e); void KeyDown(KeyInput e); IEnumerable<OverlayPrimitive> Overlay(double zoom); CursorHint Cursor { get; } }
public readonly record struct PointerInput(Point2 Position, PointerButton Button, ModifierKeys Modifiers, int ClickCount, double Pressure = 1);
public abstract record OverlayPrimitive;  // OverlayRect, OverlayEllipse, OverlayPath, OverlayHandle(id, center, kind), OverlayLine, OverlayText, OverlayImageTint
```
Services the session needs (injected via `EditorServices` record): `IDocumentRenderer` (bounds/outline/text layout/flatten), `IEffectProcessor`, `ISmartMoveService`, `IInpaintingService`, `IMagicWandService`, `ISimplifyService`, `ISmartRedactService`, `IOcrService`, `ITextEditService`, `IImageCodec`, `IStyleRepository`. Editor unit tests use fakes.

Hit testing: geometry from `IDocumentRenderer.GetOutline` with tolerance `max(4/zoom, strokeWidth/2)`; topmost first; locked objects skipped for drag but selectable.

Text editing: session raises `BeginTextEdit(annotationId, bounds)`; the WPF view hosts a transparent RichTextBox overlay with the run formatting, and on commit calls `session.ModifySelection(...)` with new `RichText`.

## 5. Process & threading model

- One process `Shotwise.exe` (single instance via named mutex `Local\Shotwise.SingleInstance` + named pipe `\\.\pipe\Shotwise.{user-sid}` for argument forwarding).
- UI thread = WPF dispatcher. Capture overlay windows are WPF too (per-monitor, PerMonitorV2 manifest).
- Engine services are thread-safe and async; CPU work on `Task.Run`; MF/D3D work on dedicated threads owned by Recording; COM Office automation on a dedicated STA thread in Sharing.
- Events from services may arrive on any thread; UI libs marshal with `Dispatcher.InvokeAsync`.

## 6. Persistence locations

| Data | Location |
|---|---|
| Settings | `%APPDATA%\Shotwise\settings.json` |
| Presets | `%APPDATA%\Shotwise\presets.json` |
| Themes / palettes / user stamps / user templates | `%APPDATA%\Shotwise\{themes,palettes,stamps,templates}\` |
| Destination settings | `%APPDATA%\Shotwise\share\{destinationId}.json` (no secrets) ; secrets in Credential Manager `Shotwise:{destinationId}:{account}` |
| Library files | default `%USERPROFILE%\Documents\Shotwise\Library\yyyy\MM\` |
| Catalog | `%LOCALAPPDATA%\Shotwise\library.db` (+ `thumbnails\`) |
| Models | `%LOCALAPPDATA%\Shotwise\models\` |
| Logs / crash | `%LOCALAPPDATA%\Shotwise\logs\` |
| Temp | `%TEMP%\Shotwise\` (cleaned at start) |

## 7. `.shotx` format v1

ZIP (Deflate; PNG entries Stored):
```
thumbnail.png              # FIRST entry, Stored, ≤1024 px longest side (shell extension reads it without inflating)
manifest.json              # { "format":"shotx", "version":1, "app":"Shotwise x.y", "documentId":"…", "pages":["pages/{id}.json"], "created":"…", "modified":"…" }
metadata.json              # DocumentMetadata
pages/{pageId}.json        # Page (annotations polymorphic "kind"; colours "#AARRGGBB"; points {x,y})
assets/{assetId}.{png|svg|pdf}
```
Readers must ignore unknown fields (preserve in `Extra`) and reject: path traversal, >2 GB uncompressed, >10 000 entries.

## 8. Error handling, logging, cancellation

- Services throw `ShotwiseException(code, message)` for user-presentable failures (`ErrorCode` enum in Core) — UI maps codes to localized messages.
- `ILogger<T>` everywhere; no `Console.WriteLine`.
- Every `async` API takes a `CancellationToken`.

## 9. Localization

`Shotwise.UI.Common/Resources/Strings.resx` (en-US) with `Strings.de.resx` etc. stubs; XAML uses `{x:Static res:Strings.Key}` or a `Loc` markup extension. Engine libraries do not produce UI text except via `ErrorCode`.
