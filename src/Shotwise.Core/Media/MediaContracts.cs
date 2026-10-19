using Shotwise.Core.Capture;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Core.Media;

public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double Fps, bool HasAudio, long FrameCount);

/// <summary>Half-open time interval [Start, End).</summary>
public sealed record TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    public bool IsEmpty => End <= Start;
    public bool Contains(TimeSpan t) => t >= Start && t < End;
    public bool Overlaps(TimeRange o) => Start < o.End && o.Start < End;
    public TimeRange Clamp(TimeRange bounds) => new(Max(Start, bounds.Start), Min(End, bounds.End));
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

public sealed record VideoExportOptions(int? Width = null, int? Height = null, double? Fps = null, int Quality = 70, bool HardwareEncoding = true, bool IncludeAudio = true, int AudioBitrateKbps = 160);

public sealed record VideoMetadata(string? Title, string? Comments, IReadOnlyList<string> Tags, DateTimeOffset? CaptureDate, string? SourceApp);

public enum GifPreset { ScreenVideo, HighMotion, ReducedFileSize, Custom }

/// <summary>Colour-quantisation dithering used by the GIF encoder.</summary>
public enum GifDitherMode { None, FloydSteinberg, Ordered }

public sealed record GifOptions(GifPreset Preset, int Width, int Height, double Fps, bool Loop = true, bool Dither = true, bool FadeToBlack = false, TimeRange? Range = null)
{
    private readonly GifDitherMode _ditherMode = GifDitherMode.FloydSteinberg;

    /// <summary>
    /// Dithering algorithm. <see cref="Dither"/> is kept for compatibility: <c>Dither = false</c> always means
    /// <see cref="GifDitherMode.None"/>; with <c>Dither = true</c> this returns the configured mode (default Floyd–Steinberg,
    /// and a configured <see cref="GifDitherMode.None"/> falls back to Floyd–Steinberg).
    /// </summary>
    public GifDitherMode DitherMode
    {
        get => !Dither ? GifDitherMode.None : _ditherMode == GifDitherMode.None ? GifDitherMode.FloydSteinberg : _ditherMode;
        init => _ditherMode = value;
    }

    /// <summary>Returns a copy with the given dithering mode; <see cref="GifDitherMode.None"/> also clears <see cref="Dither"/>.</summary>
    public GifOptions WithDitherMode(GifDitherMode mode) => this with { Dither = mode != GifDitherMode.None, DitherMode = mode };
}

public interface IVideoService
{
    Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default);
    Task<BgraImage> GetFrameAsync(string path, TimeSpan at, CancellationToken ct = default);
    Task<IReadOnlyList<BgraImage>> GetThumbnailsAsync(string path, int count, int height, CancellationToken ct = default);
    /// <summary>Frame-accurate render of the kept ranges of <paramref name="edl"/>.</summary>
    Task RenderAsync(VideoEditList edl, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct);
    /// <summary>Joins videos, scaling/letterboxing to the first video's size.</summary>
    Task CombineAsync(IReadOnlyList<string> inputs, string outputPath, VideoExportOptions o, IProgress<double>? p, CancellationToken ct);
    Task WriteMetadataAsync(string mp4Path, VideoMetadata md, CancellationToken ct = default);
}

public interface IGifService
{
    Task CreateFromVideoAsync(string videoPath, string gifPath, GifOptions o, IProgress<double>? p, CancellationToken ct);
    Task CreateFromFramesAsync(IAsyncEnumerable<(BgraImage Frame, TimeSpan Delay)> frames, string gifPath, GifOptions o, CancellationToken ct);
    Task<AnimatedImage> DecodeAsync(string gifPath, CancellationToken ct);
    /// <summary>SPEC MED-04 presets: ScreenVideo 1280×720 @5, HighMotion 640×360 @15, ReducedFileSize 400 px wide @3 (aspect kept).</summary>
    GifOptions DefaultsFor(GifPreset preset, int srcWidth, int srcHeight) => DefaultsFor(preset, srcWidth, srcHeight, 30);
    /// <summary>As the 3-argument overload; <paramref name="srcFps"/> lets Custom keep the source frame rate (capped by the encoder's limits).</summary>
    GifOptions DefaultsFor(GifPreset preset, int srcWidth, int srcHeight, double srcFps);
}

/// <summary>Frames for "Video from Images": the editor renders the current canvas region on demand.</summary>
public interface IFrameSource
{
    SizeI FrameSize { get; }
    /// <summary>Returns the frame to encode at <paramref name="timestamp"/>; may return the same instance while unchanged.</summary>
    BgraImage GetFrame(TimeSpan timestamp);
}

public sealed record VideoFromImagesOptions(
    int Fps = 15,
    bool RecordMicrophone = true,
    string? MicrophoneId = null,
    bool RecordSystemAudio = false,
    bool Webcam = false,
    string? WebcamId = null,
    WebcamShape PipShape = WebcamShape.Circle,
    Corner PipCorner = Corner.BottomRight,
    Argb Background = default);

/// <summary>Records an editor-provided frame source plus audio/webcam into an MP4 (SPEC REC-12).</summary>
public interface IVideoFromImagesSession : IAsyncDisposable
{
    RecorderState State { get; }
    TimeSpan Elapsed { get; }
    event EventHandler<RecorderState>? StateChanged;
    event EventHandler<AudioLevels>? Levels;
    Task StartAsync(IFrameSource source, VideoFromImagesOptions options, string outputMp4Path, CancellationToken ct);
    Task PauseAsync();
    Task ResumeAsync();
    Task<VideoCaptureResult> StopAsync();
    Task CancelAsync();
}

public interface IVideoFromImagesSessionFactory { IVideoFromImagesSession Create(); }
