using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Media;

public enum WebcamShape { Circle, RoundedSquare, RoundedRectangle }

public enum RecorderState { Idle, Ready, Countdown, Recording, Paused, Stopping, Faulted }

/// <summary>Video recording options (per preset). Colours default to transparent; see <see cref="PresetDefaults.Recording"/> for our defaults.</summary>
public sealed record RecordingOptions(
    int Fps = 30, int Quality = 70, bool HardwareEncoding = true, bool ScaleTo1080p = true,
    bool RecordMicrophone = true, string? MicrophoneId = null, bool RemoveNoise = false, bool RecordSystemAudio = true,
    bool Webcam = false, string? WebcamId = null, WebcamShape PipShape = WebcamShape.Circle, Corner PipCorner = Corner.BottomRight, double PipSizeFraction = 0.2,
    bool CursorHighlight = false, Argb CursorHighlightColor = default, bool ClickAnimation = false, Argb ClickColor = default,
    bool IncludeCursor = true, bool Countdown = true, bool ShowReadyWindow = true, bool AutoStart = false)
{
    /// <summary>Supported frame rates (SPEC REC-03).</summary>
    public static IReadOnlyList<int> SupportedFps { get; } = new[] { 5, 10, 15, 30, 60 };
}

/// <summary>What to record: a region, a window, or a monitor (exactly one should be set).</summary>
public sealed record RecordingTarget(RectI? RegionPx, IntPtr? Window, int? MonitorIndex);

/// <summary>Peak levels 0..1 for the VU meters (~20 Hz).</summary>
public sealed record AudioLevels(double Microphone, double SystemAudio);

public enum DeviceKind { Microphone, Webcam, Speaker }

public sealed record DeviceInfo(string Id, string Name, DeviceKind Kind, bool IsDefault, bool IsAvailable = true);

/// <summary>Simple vector drawing API used by frame overlays (ScreenDraw, cursor FX); implemented by the recorder via Direct2D.</summary>
public interface IOverlayCanvas
{
    /// <summary>Size of the frame in pixels.</summary>
    SizeI FrameSize { get; }
    void DrawLine(Point2 from, Point2 to, Argb color, double width);
    void DrawRectangle(Rect2 rect, Argb? stroke, double strokeWidth, Argb? fill, double cornerRadius = 0);
    void DrawEllipse(Rect2 bounds, Argb? stroke, double strokeWidth, Argb? fill);
    void DrawPolyline(IReadOnlyList<Point2> points, Argb color, double width, bool closed = false);
    void DrawText(string text, Point2 origin, string fontFamily, double fontSize, Argb color, bool bold = false);
    void DrawImage(BgraImage image, Rect2 destination, double opacity = 1);
}

/// <summary>Per-frame overlay composited by the recorder. <paramref name="capturePx"/> is the captured area in screen pixels.</summary>
public interface IFrameOverlay
{
    void Render(IOverlayCanvas canvas, TimeSpan timestamp, RectI capturePx);
}

public interface IVideoRecorder : IAsyncDisposable
{
    RecorderState State { get; }
    TimeSpan Elapsed { get; }
    event EventHandler<RecorderState>? StateChanged;
    event EventHandler<AudioLevels>? Levels;
    /// <summary>Low disk, device lost, etc. (localized by UI from the message key).</summary>
    event EventHandler<string>? Warning;
    Task PrepareAsync(RecordingTarget target, RecordingOptions options, string outputMp4Path, CancellationToken ct);
    /// <summary>
    /// Prepares a recording whose video frames come from <paramref name="source"/> instead of the desktop (SPEC REC-12
    /// "video from images"); audio (microphone/system), webcam PiP, pause/resume, restart and cancel behave exactly as
    /// for desktop targets. <see cref="IFrameSource.GetFrame"/> is called on the recorder's worker thread.
    /// </summary>
    Task PrepareFramesAsync(IFrameSource source, RecordingOptions options, string outputMp4Path, CancellationToken ct);
    Task StartAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task<VideoCaptureResult> StopAsync();
    Task CancelAsync();
    Task RestartAsync();
    void SetMicrophoneMuted(bool muted);
    void SetSystemAudioMuted(bool muted);
    void SetWebcamFullscreen(bool fullscreen);
    void AddOverlay(IFrameOverlay overlay);
}

public interface IVideoRecorderFactory { IVideoRecorder Create(); }

public interface IAudioDeviceService
{
    IReadOnlyList<DeviceInfo> Microphones();
    IReadOnlyList<DeviceInfo> Webcams();
    DeviceInfo? DefaultMicrophone();
    event EventHandler DevicesChanged;
    bool IsMicrophoneMuted(string id);
}

public interface IWebcamPreview : IDisposable
{
    event EventHandler<BgraImage>? Frame;
    Task StartAsync(string deviceId, CancellationToken ct);
}

public interface IWebcamPreviewFactory { IWebcamPreview Create(); }

/// <summary>Noise suppression host (implemented in Vision; consumed by Recording).</summary>
public interface INoiseSuppressor
{
    bool IsAvailable { get; }
    INoiseSuppressorStream CreateStream(int sampleRate, int channels);
}

public interface INoiseSuppressorStream : IDisposable
{
    /// <summary>Processes interleaved float samples in place.</summary>
    void Process(Span<float> interleaved);
}
