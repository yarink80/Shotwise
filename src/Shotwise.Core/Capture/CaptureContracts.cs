using Shotwise.Core.Annotations;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Core.Capture;

public enum CaptureMode { AllInOne, Image, Video }

public enum SelectionType { Region, Window, Fullscreen, FixedRegion, Freehand, Object, MultipleArea, Menu, ScrollingWindow, Panoramic, GrabText, StepCapture, Clipboard, ListenToClipboard, File }

public enum ScrollDirection { Vertical, Horizontal, Both }

public enum StitchDirection { Vertical, Horizontal, Auto }

public sealed record MonitorInfo(string DeviceName, RectI BoundsPx, RectI WorkAreaPx, double DpiScale, bool IsPrimary, bool IsHdr, double SdrWhiteLevelNits, IntPtr HMonitor);

public sealed record WindowInfo(IntPtr Hwnd, string Title, string ProcessName, int ProcessId, RectI BoundsPx, RectI ClientPx, bool IsMinimized);

public sealed record ElementInfo(string Name, string ControlType, RectI BoundsPx, IntPtr OwnerHwnd);

public sealed record CursorSnapshot(BgraImage Image, PointI Hotspot, PointI PositionPx);

/// <summary>Frozen frame of every monitor (in virtual-screen physical pixels) used by the selection overlay.</summary>
public sealed record ScreenSnapshot(DateTimeOffset Taken, IReadOnlyList<(MonitorInfo Monitor, BgraImage Image)> Monitors, CursorSnapshot? Cursor);

/// <summary>Result of an image capture: raster + provenance + optional initial annotations (e.g. cursor, step badges).</summary>
public sealed record CaptureResult(BgraImage Image, DocumentMetadata Metadata, IReadOnlyList<Annotation>? InitialAnnotations = null);

public sealed record VideoCaptureResult(string Mp4Path, TimeSpan Duration, Size2 FrameSize, DocumentMetadata Metadata, bool IsGif);

/// <summary>What a capture run produced (null outcome = cancelled).</summary>
public sealed record CaptureOutcome(CaptureMode Mode, CaptureResult? Image = null, VideoCaptureResult? Video = null, string? Text = null);

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    RectI VirtualScreenPx { get; }
    event EventHandler DisplaysChanged;
}

public interface IScreenCaptureService
{
    /// <summary>Frozen frame of all monitors for the overlay.</summary>
    Task<ScreenSnapshot> SnapshotAllAsync(bool includeCursor, bool hdrToneMap, CancellationToken ct = default);
    Task<BgraImage> CaptureRectAsync(RectI virtualPx, bool includeCursor, CancellationToken ct = default);
    /// <summary>PrintWindow PW_RENDERFULLCONTENT / Windows.Graphics.Capture.</summary>
    Task<BgraImage> CaptureWindowAsync(IntPtr hwnd, bool clientOnly, bool includeCursor, CancellationToken ct = default);
    BgraImage CropSnapshot(ScreenSnapshot s, RectI virtualPx);
    BgraImage ApplyFreehandMask(BgraImage img, IReadOnlyList<PointI> polygonPx);
}

public interface IWindowFinder
{
    WindowInfo? WindowAt(PointI screenPx, IEnumerable<IntPtr> ignore);
    IReadOnlyList<WindowInfo> EnumerateTopLevel();
    WindowInfo? Foreground();
}

public interface IElementFinder
{
    Task<ElementInfo?> ElementAtAsync(PointI screenPx, CancellationToken ct = default);
    Task<IReadOnlyList<RectI>> ElementRectsForWindowAsync(IntPtr hwnd, CancellationToken ct = default);
}

/// <summary>App name/company/version, window title, browser URL (UIA), OS version, cursor position.</summary>
public interface ICaptureMetadataProvider
{
    Task<DocumentMetadata> CollectAsync(IntPtr? hwnd, PointI? cursor, CancellationToken ct = default);
}

public sealed record ScrollProbe(bool CanScrollVertical, bool CanScrollHorizontal, RectI ScrollAreaPx);

public sealed record ScrollCaptureRequest(IntPtr Hwnd, RectI AreaPx, ScrollDirection Direction, bool StartAtTopLeft, int StepDelayMs, int MaxFrames = 400);

public sealed record ScrollProgress(int Frames, BgraImage? Preview);

public interface IScrollingCaptureService
{
    Task<ScrollProbe> ProbeAsync(IntPtr hwnd, PointI at, CancellationToken ct);
    Task<BgraImage> CaptureAsync(ScrollCaptureRequest req, IProgress<ScrollProgress>? progress, CancellationToken ct);
}

public interface IPanoramicSession : IAsyncDisposable
{
    event EventHandler<BgraImage>? PreviewUpdated;
    Task StartAsync(RectI areaPx, CancellationToken ct);
    Task<BgraImage> StopAndStitchAsync(CancellationToken ct);
}

public interface IPanoramicCaptureService { IPanoramicSession CreateSession(); }

public interface IMenuCaptureService
{
    Task<BgraImage?> CaptureMenusAsync(bool includeMenuBar, bool cascaded, TimeSpan wait, CancellationToken ct);
}

/// <summary>Options for step capture (click recording).</summary>
public sealed record StepCaptureOptions(
    StepCaptureArea Area = StepCaptureArea.Window,
    int RegionSizePx = 800,
    bool IncludeCursor = true,
    bool HighlightClick = true,
    int DelayAfterClickMs = 150,
    int MaxSteps = 200,
    string? OutputFolder = null);

public enum StepCaptureArea { Region, Window, Monitor }

public sealed record StepCaptured(int Index, BgraImage Image, PointI ClickPx, string Action, string? ElementName, DateTimeOffset At)
{
    /// <summary>
    /// Physical virtual-screen rectangle that <see cref="Image"/> was captured from (null when unknown). The click point in
    /// image pixels is <c>ClickPx - CaptureBoundsPx.Location</c> (scaled if the image was resized).
    /// </summary>
    public RectI? CaptureBoundsPx { get; init; }
}

public sealed record StepCaptureResult(IReadOnlyList<StepCaptured> Steps, string ActionsJsonPath);

public interface IStepCaptureSession : IAsyncDisposable
{
    event EventHandler<StepCaptured>? Captured;
    int Count { get; }
    Task StartAsync(CancellationToken ct);
    void Pause();
    void Resume();
    Task<StepCaptureResult> StopAsync();
}

public interface IStepCaptureService { IStepCaptureSession CreateSession(StepCaptureOptions o); }

public interface IClipboardImageWatcher
{
    event EventHandler<BgraImage>? ImageCopied;
    bool IsEnabled { get; set; }
    /// <summary>
    /// Marks the clipboard's current sequence number as our own write so it is not re-imported ("Listen to clipboard").
    /// Call immediately after every in-process clipboard write (ownerless writes cannot be attributed otherwise).
    /// </summary>
    void IgnoreCurrentSequence();
}

public interface IPrinterCaptureService
{
    bool IsInstalled { get; }
    Task InstallAsync(string printerName);
    Task UninstallAsync();
    event EventHandler<IReadOnlyList<BgraImage>>? PagesCaptured;
    string WatchFolder { get; set; }
}

/// <summary>Image stitching factory (implemented in Vision; used by scrolling + panoramic capture).</summary>
public interface IStitcher
{
    IIncrementalStitcher Create(StitchDirection dir);
}

public interface IIncrementalStitcher : IDisposable
{
    /// <summary>Adds the next frame; false when it could not be registered (no overlap / no movement).</summary>
    bool AddFrame(BgraImage frame);
    BgraImage Preview(int maxSide);
    BgraImage Result();
}

/// <summary>In-process capture event hub: capture coordinator publishes, editor + library subscribe.</summary>
public interface ICaptureEvents
{
    event EventHandler<CaptureResult>? ImageCaptured;
    event EventHandler<VideoCaptureResult>? VideoCaptured;
    void Publish(CaptureResult r);
    void Publish(VideoCaptureResult r);
}

/// <summary>Capture orchestration (implemented in UI.Capture: owns overlay windows).</summary>
public interface ICaptureCoordinator
{
    Task<CaptureOutcome?> RunAsync(CapturePreset preset, CancellationToken ct = default);
    Task RepeatLastAsync();
    void ShowCaptureWindow();
    void ToggleWidget(bool show);
    bool IsCapturing { get; }
}

/// <summary>Default <see cref="ICaptureEvents"/> implementation (thread-safe; handlers run on the publisher's thread).</summary>
public sealed class CaptureEventHub : ICaptureEvents
{
    public event EventHandler<CaptureResult>? ImageCaptured;
    public event EventHandler<VideoCaptureResult>? VideoCaptured;
    public void Publish(CaptureResult r) => ImageCaptured?.Invoke(this, r ?? throw new ArgumentNullException(nameof(r)));
    public void Publish(VideoCaptureResult r) => VideoCaptured?.Invoke(this, r ?? throw new ArgumentNullException(nameof(r)));
}
