using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Media;

public sealed class VideoFromImagesSessionFactory(ILogger<VideoFromImagesSession> logger, IWebcamPreviewFactory? webcams = null, IVideoRecorderFactory? recorders = null) : IVideoFromImagesSessionFactory
{
    public IVideoFromImagesSession Create() => new VideoFromImagesSession(logger, webcams, recorders);
}

/// <summary>
/// "Video from images" (SPEC REC-12). When a Core <see cref="IVideoRecorderFactory"/> is registered (the composed app),
/// frames, narration (microphone/system audio), webcam PiP, pause/resume and encoding are delegated to
/// <see cref="IVideoRecorder.PrepareFramesAsync"/>. Without a recorder (stand-alone/unit tests) a local video-only
/// single-owner MTA encoder is used; audio requests then fail with <see cref="ErrorCode.AudioInitFailed"/>.
/// </summary>
public sealed class VideoFromImagesSession : IVideoFromImagesSession
{
    private readonly ILogger<VideoFromImagesSession> logger;
    private readonly IWebcamPreviewFactory? webcams;
    private readonly IVideoRecorderFactory? recorders;
    private IVideoRecorder? recorder;
    private string? delegatedTemporary, delegatedOutput;
    private readonly BlockingCollection<Action> commands = new();
    private readonly Thread worker;
    private readonly Stopwatch clock = new();
    private readonly object cameraGate = new();
    private readonly object submissionGate = new();
    private int state, disposed;
    private long elapsed, frameIndex;
    private bool exit;
    private MediaRuntime? runtime;
    private Mp4Writer? writer;
    private IWebcamPreview? camera;
    private BgraImage? cameraFrame;
    private IFrameSource? source;
    private VideoFromImagesOptions? options;
    private string? temporary, output;
    private CancellationToken cancellation;
    private Exception? fault;
    private DateTimeOffset captureDate;
    /// <summary>Test hook replacing the final move of a finalized video to its destination.</summary>
    internal Action<string, string>? MoveFile { get; set; }
    public RecorderState State => (RecorderState)Volatile.Read(ref state);
    public TimeSpan Elapsed => recorder?.Elapsed ?? TimeSpan.FromTicks(Interlocked.Read(ref elapsed));
    public event EventHandler<RecorderState>? StateChanged;
    public event EventHandler<AudioLevels>? Levels;
    public VideoFromImagesSession(ILogger<VideoFromImagesSession> logger, IWebcamPreviewFactory? webcams = null, IVideoRecorderFactory? recorders = null)
    {
        this.logger = logger; this.webcams = webcams; this.recorders = recorders;
        worker = new Thread(Loop) { IsBackground = true, Name = "Shotwise image video encoder" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    private Task<T> Enqueue<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (submissionGate)
        {
            if (disposed != 0) return Task.FromException<T>(new ObjectDisposedException(nameof(VideoFromImagesSession)));
            commands.Add(() => { try { result.TrySetResult(action()); } catch (Exception ex) { result.TrySetException(ex); } });
        }
        return result.Task;
    }
    private void Transition(RecorderState value)
    {
        Volatile.Write(ref state, (int)value);
        try { StateChanged?.Invoke(this, value); } catch (Exception ex) { logger.LogWarning(ex, "Recording state observer failed"); }
    }
    public Task StartAsync(IFrameSource frameSource, VideoFromImagesOptions recordingOptions, string outputMp4Path, CancellationToken ct)
        => recorders is not null ? StartDelegatedAsync(frameSource, recordingOptions, outputMp4Path, ct) : StartLocal(frameSource, recordingOptions, outputMp4Path, ct);

    // ---- delegated path (Core IVideoRecorder.PrepareFramesAsync) ----

    private async Task StartDelegatedAsync(IFrameSource frameSource, VideoFromImagesOptions o, string outputMp4Path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frameSource);
        ct.ThrowIfCancellationRequested();
        if (State != RecorderState.Idle || recorder is not null) throw new InvalidOperationException("Session is already active.");
        if (o.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(o));
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(VideoFromImagesSession));
        // The recorder supports a fixed set of frame rates (REC-03); use the nearest one.
        int fps = RecordingOptions.SupportedFps.OrderBy(f => Math.Abs(f - o.Fps)).ThenByDescending(f => f).First();
        var options = new RecordingOptions(
            Fps: fps, ScaleTo1080p: false,
            RecordMicrophone: o.RecordMicrophone, MicrophoneId: o.MicrophoneId, RecordSystemAudio: o.RecordSystemAudio,
            Webcam: o.Webcam, WebcamId: o.WebcamId, PipShape: o.PipShape, PipCorner: o.PipCorner,
            IncludeCursor: false, Countdown: false, ShowReadyWindow: false, AutoStart: true);
        string output = Path.GetFullPath(outputMp4Path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // The recorder never overwrites; record next to the destination and replace it atomically on Stop.
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".shotwise-imgvideo-{Guid.NewGuid():N}.mp4");
        var rec = recorders!.Create();
        rec.StateChanged += OnRecorderState;
        rec.Levels += OnRecorderLevels;
        recorder = rec; delegatedOutput = output; delegatedTemporary = temporary;
        try
        {
            await rec.PrepareFramesAsync(new FlattenedFrameSource(frameSource, o.Background), options, temporary, ct).ConfigureAwait(false);
            await rec.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await ReleaseRecorderAsync(cancel: true).ConfigureAwait(false);
            Transition(RecorderState.Idle);
            throw;
        }
    }

    private void OnRecorderState(object? sender, RecorderState s)
    {
        // Ready/Countdown are internal recorder phases; the session reports Recording once frames flow.
        if (s is RecorderState.Ready or RecorderState.Countdown) return;
        Transition(s);
    }

    private void OnRecorderLevels(object? sender, AudioLevels levels)
    {
        try { Levels?.Invoke(this, levels); } catch (Exception ex) { logger.LogWarning(ex, "Audio level observer failed"); }
    }

    private async Task ReleaseRecorderAsync(bool cancel)
    {
        var rec = recorder; recorder = null;
        if (rec is not null)
        {
            rec.StateChanged -= OnRecorderState; rec.Levels -= OnRecorderLevels;
            if (cancel)
                try { await rec.CancelAsync().ConfigureAwait(false); } catch (Exception ex) { logger.LogDebug(ex, "Recorder cancel during cleanup failed"); }
            try { await rec.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { logger.LogWarning(ex, "Recorder disposal failed"); }
        }
        if (delegatedTemporary is not null) { try { OutputFile.Delete(delegatedTemporary); } catch (Exception ex) { logger.LogDebug(ex, "Temporary image video cleanup failed"); } }
        delegatedTemporary = null; delegatedOutput = null;
    }

    private async Task<VideoCaptureResult> StopDelegatedAsync()
    {
        var rec = recorder ?? throw new InvalidOperationException("Session is not recording.");
        try
        {
            var result = await rec.StopAsync().ConfigureAwait(false);
            // Finalized (possibly a partial recording after a source fault, possibly published by the recorder to a
            // sibling name): from here on the file is never deleted, even when it cannot be moved to the destination.
            delegatedTemporary = null;
            var path = OutputFile.PublishOrKeep(result.Mp4Path, delegatedOutput!, logger, MoveFile);
            var final = result with { Mp4Path = path, IsGif = false };
            logger.LogInformation("Saved image video {Path}, {Duration}", final.Mp4Path, final.Duration);
            await ReleaseRecorderAsync(cancel: false).ConfigureAwait(false);
            Transition(RecorderState.Idle);
            return final;
        }
        catch
        {
            await ReleaseRecorderAsync(cancel: true).ConfigureAwait(false);
            Transition(RecorderState.Faulted);
            throw;
        }
    }

    /// <summary>Composites the editor frame over the background colour (the encoder needs opaque pixels).</summary>
    private sealed class FlattenedFrameSource(IFrameSource inner, Argb background) : IFrameSource
    {
        private BgraImage? lastIn, lastOut;
        public SizeI FrameSize => inner.FrameSize;
        public BgraImage GetFrame(TimeSpan timestamp)
        {
            var raw = inner.GetFrame(timestamp);
            if (ReferenceEquals(raw, lastIn) && lastOut is not null) return lastOut;
            var image = raw.Clone();
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                int inverse = 255 - image.Pixels[i + 3];
                if (inverse == 0) continue;
                image.Pixels[i] = (byte)Math.Min(255, image.Pixels[i] + background.B * inverse / 255);
                image.Pixels[i + 1] = (byte)Math.Min(255, image.Pixels[i + 1] + background.G * inverse / 255);
                image.Pixels[i + 2] = (byte)Math.Min(255, image.Pixels[i + 2] + background.R * inverse / 255);
                image.Pixels[i + 3] = 255;
            }
            lastIn = raw; lastOut = image;
            return image;
        }
    }

    // ---- local video-only path ----

    private Task StartLocal(IFrameSource frameSource, VideoFromImagesOptions recordingOptions, string outputMp4Path, CancellationToken ct) => Enqueue(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (State != RecorderState.Idle) throw new InvalidOperationException("Session is already active.");
        if (recordingOptions.Fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(recordingOptions));
        if (recordingOptions.RecordMicrophone || recordingOptions.RecordSystemAudio) throw new ShotwiseException(ErrorCode.AudioInitFailed, "Narration needs the video recorder service (IVideoRecorderFactory), which is not registered.");
        if (recordingOptions.Webcam && webcams is null) throw new ShotwiseException(ErrorCode.DeviceNotFound, "No webcam preview service is registered.");
        source = frameSource ?? throw new ArgumentNullException(nameof(frameSource)); options = recordingOptions;
        output = Path.GetFullPath(outputMp4Path); temporary = OutputFile.Temporary(output, ".mp4"); cancellation = ct; fault = null;
        try
        {
            runtime = new MediaRuntime();
            writer = new Mp4Writer(temporary, (source.FrameSize.Width + 1) & ~1, (source.FrameSize.Height + 1) & ~1, options.Fps, new(), false);
            if (options.Webcam)
            {
                camera = webcams!.Create(); camera.Frame += OnCameraFrame;
                camera.StartAsync(options.WebcamId ?? throw new ArgumentException("A webcam device ID is required."), ct).GetAwaiter().GetResult();
            }
            captureDate = DateTimeOffset.UtcNow; frameIndex = 0; Interlocked.Exchange(ref elapsed, 0); clock.Restart(); Transition(RecorderState.Recording);
            try { Levels?.Invoke(this, new(0, 0)); } catch (Exception ex) { logger.LogWarning(ex, "Audio level observer failed"); }
        }
        catch { Cleanup(); Transition(RecorderState.Idle); throw; }
        return true;
    });
    private void OnCameraFrame(object? sender, BgraImage frame) { lock (cameraGate) cameraFrame = frame.Clone(); }
    public Task PauseAsync() => recorder is { } r ? r.PauseAsync() : Enqueue(() => { Require(RecorderState.Recording); clock.Stop(); Transition(RecorderState.Paused); return true; });
    public Task ResumeAsync() => recorder is { } r ? r.ResumeAsync() : Enqueue(() => { Require(RecorderState.Paused); clock.Start(); Transition(RecorderState.Recording); return true; });
    public Task<VideoCaptureResult> StopAsync() => recorder is not null ? StopDelegatedAsync() : Enqueue(() =>
    {
        if (fault is not null) throw new ShotwiseException(ErrorCode.RecordingFailed, "Image video recording failed.", fault);
        if (State is not (RecorderState.Recording or RecorderState.Paused)) throw new InvalidOperationException("Session is not recording.");
        Transition(RecorderState.Stopping); clock.Stop();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (frameIndex == 0) EncodeFrame();
            writer!.Complete(); var size = new Size2(writer.Width, writer.Height); writer.Dispose(); writer = null;
            cancellation.ThrowIfCancellationRequested();
            // Finalized: never deleted from here on, even when it cannot be moved to the destination.
            var finalized = temporary!; temporary = null;
            var path = OutputFile.PublishOrKeep(finalized, output!, logger, MoveFile);
            var result = new VideoCaptureResult(path, Elapsed, size, DocumentMetadata.Create(captureDate), false);
            logger.LogInformation("Saved image video {Path}, {Duration}", path, Elapsed);
            Cleanup(); Transition(RecorderState.Idle); return result;
        }
        catch { Cleanup(); Transition(RecorderState.Faulted); throw; }
    });
    public Task CancelAsync() => recorder is not null
        ? CancelDelegatedAsync()
        : Enqueue(() => { Cleanup(); fault = null; Transition(RecorderState.Idle); return true; });
    private async Task CancelDelegatedAsync() { await ReleaseRecorderAsync(cancel: true).ConfigureAwait(false); Transition(RecorderState.Idle); }
    private void Require(RecorderState expected) { if (State != expected) throw new InvalidOperationException($"Session state must be {expected}."); }
    private void Loop()
    {
        while (!exit)
        {
            if (commands.TryTake(out var command, 5)) command();
            if (exit) break;
            if ((State is RecorderState.Recording or RecorderState.Paused) && cancellation.IsCancellationRequested)
            { Cleanup(); Transition(RecorderState.Idle); continue; }
            if (State != RecorderState.Recording || options is null) continue;
            if (clock.Elapsed.TotalSeconds < frameIndex / (double)options.Fps) continue;
            try { EncodeFrame(); }
            catch (Exception ex) { fault = ex; logger.LogError(ex, "Image video frame failed"); Cleanup(); Transition(RecorderState.Faulted); }
        }
        Cleanup();
    }
    private void EncodeFrame()
    {
        cancellation.ThrowIfCancellationRequested();
        long t = frameIndex * 10000000 / options!.Fps, end = (frameIndex + 1) * 10000000 / options.Fps;
        var raw = source!.GetFrame(TimeSpan.FromTicks(t));
        var image = PixelScaler.Resize(raw, writer!.Width, writer.Height, true, false);
        var color = options.Background;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            int inverse = 255 - image.Pixels[i + 3];
            image.Pixels[i] = (byte)Math.Min(255, image.Pixels[i] + color.B * inverse / 255);
            image.Pixels[i + 1] = (byte)Math.Min(255, image.Pixels[i + 1] + color.G * inverse / 255);
            image.Pixels[i + 2] = (byte)Math.Min(255, image.Pixels[i + 2] + color.R * inverse / 255); image.Pixels[i + 3] = 255;
        }
        BgraImage? preview; lock (cameraGate) preview = cameraFrame;
        if (preview is not null) CompositeCamera(image, preview);
        writer.Video(image, t, end - t); frameIndex++; Interlocked.Exchange(ref elapsed, end);
    }
    private void CompositeCamera(BgraImage image, BgraImage preview)
    {
        int w = Math.Max(1, image.Width / 4), h = options!.PipShape == WebcamShape.RoundedRectangle ? Math.Max(1, w * 3 / 4) : w;
        h = Math.Min(h, image.Height); var cameraImage = PixelScaler.Resize(preview, w, h);
        bool right = options.PipCorner is Corner.TopRight or Corner.BottomRight, bottom = options.PipCorner is Corner.BottomLeft or Corner.BottomRight;
        int ox = right ? image.Width - w : 0, oy = bottom ? image.Height - h : 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double nx = (x + .5 - w / 2.0) / (w / 2.0), ny = (y + .5 - h / 2.0) / (h / 2.0);
            if (options.PipShape == WebcamShape.Circle && nx * nx + ny * ny > 1) continue;
            double radius = Math.Min(w, h) * .12, dx = Math.Max(0, Math.Abs(x + .5 - w / 2.0) - w / 2.0 + radius), dy = Math.Max(0, Math.Abs(y + .5 - h / 2.0) - h / 2.0 + radius);
            if (options.PipShape != WebcamShape.Circle && dx * dx + dy * dy > radius * radius) continue;
            int src = (y * w + x) * 4, dst = ((y + oy) * image.Width + x + ox) * 4;
            Buffer.BlockCopy(cameraImage.Pixels, src, image.Pixels, dst, 4);
        }
    }
    private void Cleanup()
    {
        void Release(Action release) { try { release(); } catch (Exception ex) { logger.LogWarning(ex, "Image video resource cleanup failed"); } }
        clock.Stop();
        if (camera is not null) { camera.Frame -= OnCameraFrame; Release(camera.Dispose); camera = null; }
        lock (cameraGate) cameraFrame = null;
        if (writer is not null) Release(writer.Dispose); writer = null;
        if (runtime is not null) Release(runtime.Dispose); runtime = null;
        if (temporary is not null) { Release(() => OutputFile.Delete(temporary)); temporary = null; }
        source = null; options = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (recorder is not null) await ReleaseRecorderAsync(cancel: true).ConfigureAwait(false);
        lock (submissionGate)
        {
            if (disposed != 0) return;
            disposed = 1; commands.Add(() => exit = true);
        }
        await Task.Run(worker.Join).ConfigureAwait(false); commands.Dispose();
    }
}
