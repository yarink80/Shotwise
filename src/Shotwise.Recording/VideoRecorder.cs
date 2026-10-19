using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Documents;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Shotwise.Core.Platform;
using System.Runtime.InteropServices;

namespace Shotwise.Recording;

public sealed class VideoRecorderFactory(ILoggerFactory loggerFactory, IWebcamPreviewFactory webcams, INoiseSuppressor? suppressor = null, IPolicyService? policy = null, Shotwise.Core.Settings.ISettingsService? settings = null) : IVideoRecorderFactory
{
    public IVideoRecorder Create() => new VideoRecorder(loggerFactory.CreateLogger<VideoRecorder>(), webcams, suppressor, policy, settings);
}

/// <summary>
/// Serializes lifecycle, native capture, composition and encoding on one owned MTA thread.
/// A failure of the recorded source (window closed, audio device lost, capture or composition/device failure)
/// finalizes and keeps what was already encoded: the recorder raises <c>Recording_SourceLost</c>, returns to
/// <see cref="RecorderState.Idle"/> and <see cref="StopAsync"/> returns the partial result. Only a failure of the
/// MP4 writer itself faults the recording and discards the unfinalized file. A finalized file is never deleted.
/// </summary>
public sealed class VideoRecorder : IVideoRecorder
{
    private readonly ILogger<VideoRecorder> _logger;
    private readonly IWebcamPreviewFactory _webcams;
    private readonly INoiseSuppressor? _suppressor;
    private readonly IPolicyService? _policy;
    private readonly Shotwise.Core.Settings.ISettingsService? _settings;
    private RecordingDiskThresholds _thresholds = RecordingDiskThresholds.Default;
    private DocumentMetadata? _metadata;
    /// <summary>Test hook on the worker: "Source" before each capture, "Compose" before each composition.</summary>
    internal Action<string>? StageProbe { get; set; }
    /// <summary>Test hook replacing the final <c>File.Move(finalized, destination)</c>.</summary>
    internal Action<string, string>? MoveFile { get; set; }
    /// <summary>Test hook replacing WASAPI device audio.</summary>
    internal Func<RecordingOptions, RecordingAudio>? AudioFactory { get; set; }
    /// <summary>Wraps a failure of the recorded source; the encoded content is still finalizable.</summary>
    private sealed class SourceFault(Exception inner) : Exception(inner.Message, inner);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RecordingTimeline _timeline = new(Stopwatch.Frequency);
    private readonly List<IFrameOverlay> _overlays = new();
    private int _state, _disposeRequested;
    private bool _exit, _micMuted, _systemMuted, _fullscreen, _warned;
    private long _elapsedTicks, _audioFrame, _lastLevels, _lastDisk, _startQpc, _sourceStartQpc;
    private string? _output, _temporary;
    private RecordingTarget? _recordingTarget;
    private IFrameSource? _frames;
    private RecordingOptions? _options;
    private RectI _bounds;
    private SizeI _size;
    private FrameGovernor? _governor;
    private Direct2DOverlayCanvas? _canvas;
    private GraphicsFrameSource? _graphics;
    private DuplicationFrameSource? _duplication;
    private GdiFrameSource? _gdi;
    private Mp4Writer? _writer;
    private RecordingAudio? _audio;
    private IWebcamPreview? _webcam;
    private BgraImage? _webcamFrame, _pending;
    private Vortice.Direct3D11.ID3D11Texture2D? _pendingTexture;
    private TimeSpan _pendingAt;
    private CursorEffects? _cursor;
    private VideoCaptureResult? _result;
    private Exception? _failure;
    public VideoRecorder(ILogger<VideoRecorder> logger, IWebcamPreviewFactory webcams, INoiseSuppressor? suppressor = null, IPolicyService? policy = null, Shotwise.Core.Settings.ISettingsService? settings = null)
    {
        _logger = logger; _webcams = webcams; _suppressor = suppressor; _policy = policy; _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shotwise recording" };
        _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
    }
    public RecorderState State => (RecorderState)Volatile.Read(ref _state);
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));
    public event EventHandler<RecorderState>? StateChanged;
    public event EventHandler<AudioLevels>? Levels;
    public event EventHandler<string>? Warning;
    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers == null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch (Exception e) { _logger.LogWarning(e, "Recording event subscriber failed"); }
    }
    private void SetState(RecorderState state) { Volatile.Write(ref _state, (int)state); Raise(StateChanged, state); }
    private Task OnWorker(Action action) => OnWorker(() => { action(); return true; });
    private Task<T> OnWorker<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_commands)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested != 0, this);
            _commands.Add(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
        }
        return completion.Task;
    }
    public Task PrepareAsync(RecordingTarget target, RecordingOptions options, string outputMp4Path, CancellationToken ct) => OnWorker(() => Prepare(target, options, outputMp4Path, ct, null));
    /// <summary>REC-12 frame-source recording (Core <see cref="IVideoRecorder.PrepareFramesAsync"/>). GetFrame runs on the recording worker.</summary>
    public Task PrepareFramesAsync(IFrameSource source, RecordingOptions options, string outputMp4Path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return OnWorker(() => Prepare(new RecordingTarget(new RectI(0, 0, source.FrameSize.Width, source.FrameSize.Height), null, null), options, outputMp4Path, ct, source));
    }
    private void Prepare(RecordingTarget target, RecordingOptions options, string path, CancellationToken ct, IFrameSource? frames)
    {
        if (State != RecorderState.Idle && State != RecorderState.Faulted) throw new InvalidOperationException("Recorder must be idle before preparing.");
        ct.ThrowIfCancellationRequested();
        if (_policy?.IsAllowed(PolicyFeature.Video) == false) throw new ShotwiseException(ErrorCode.VideoDisabledByPolicy, "Recording is disabled by policy.");
        if ((options.RecordMicrophone && _policy?.IsAllowed(PolicyFeature.Microphone) == false) || (options.RecordSystemAudio && _policy?.IsAllowed(PolicyFeature.SystemAudio) == false) || (options.Webcam && _policy?.IsAllowed(PolicyFeature.Webcam) == false))
            throw new ShotwiseException(ErrorCode.PolicyDisabled, "A requested recording device is disabled by policy.");
        if (!RecordingOptions.SupportedFps.Contains(options.Fps) || options.Quality is < 0 or > 100 || !double.IsFinite(options.PipSizeFraction) || options.PipSizeFraction is < 0.05 or > 1)
            throw new ShotwiseException(ErrorCode.InvalidArgument, "Invalid recording options.");
        var bounds = DesktopNative.Resolve(target);
        var size = RecordingDimensions.Fit(bounds.Width, bounds.Height, options.ScaleTo1080p);
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new ShotwiseException(ErrorCode.InvalidArgument, "An MP4 output path is required.");
        if (File.Exists(path)) throw new ShotwiseException(ErrorCode.FileInUse, "Output already exists.");
        Cleanup(true);
        _result = null; _failure = null; _recordingTarget = target; _options = options; _output = path; _bounds = bounds; _size = size; _frames = frames;
        _warned = false; _audioFrame = 0; _pending = null; Interlocked.Exchange(ref _elapsedTicks, 0);
        _thresholds = _settings?.Current?.Capture is { } capture ? RecordingDiskThresholds.FromMegabytes(capture.LowDiskWarningMb, capture.LowDiskStopMb) : RecordingDiskThresholds.Default;
        _metadata = DocumentMetadata.Create();
        if (frames == null && target.Window is { } recorded)
        {
            // Captured now: the window may be gone by the time a partial recording is finalized.
            var (title, app) = DesktopNative.WindowInfo(recorded);
            _metadata = _metadata with { WindowTitle = title, AppName = app };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _temporary = Path.Combine(Path.GetDirectoryName(path)!, $".shotwise-{Guid.NewGuid():N}.mp4");
        try
        {
            CheckDisk(true);
            _canvas = new Direct2DOverlayCanvas(size);
            ct.ThrowIfCancellationRequested();
            if (frames == null)
            {
                _sourceStartQpc = Stopwatch.GetTimestamp();
                try { _graphics = new GraphicsFrameSource(_canvas.Device, _canvas.Context, target, bounds, options.IncludeCursor, _logger); }
                catch (Exception e) { _logger.LogWarning(e, "WGC unavailable; trying desktop duplication"); OpenDuplication(); }
            }
            ct.ThrowIfCancellationRequested();
            try { _writer = new Mp4Writer(_temporary, size, options, _canvas.Device, _logger); }
            catch (Exception e) when (options.HardwareEncoding)
            {
                _logger.LogWarning(e, "Hardware encoder unavailable; trying software H.264");
                if (File.Exists(_temporary)) File.Delete(_temporary);
                _writer = new Mp4Writer(_temporary, size, options with { HardwareEncoding = false }, logger: _logger);
            }
            _audio = AudioFactory?.Invoke(options) ?? new RecordingAudio(options, _suppressor, () => Elapsed, _logger);
            _audio.Mute(true, _micMuted); _audio.Mute(false, _systemMuted);
            if (options.RemoveNoise && _suppressor?.IsAvailable != true) Raise(Warning, "Recording_NoiseSuppressionUnavailable");
            if (options.Webcam)
            {
                _webcam = _webcams.Create(); _webcam.Frame += WebcamFrame;
                var id = options.WebcamId ?? Task.Run(async () => (await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture)).FirstOrDefault()?.Id).GetAwaiter().GetResult();
                if (id == null) throw new ShotwiseException(ErrorCode.DeviceNotFound, "No webcam is available.");
                _webcam.StartAsync(id, ct).GetAwaiter().GetResult();
            }
            _cursor = new CursorEffects(options, _logger);
            _governor = new FrameGovernor(options.Fps, Stopwatch.Frequency);
            ct.ThrowIfCancellationRequested(); SetState(RecorderState.Ready);
        }
        catch (Exception e)
        {
            Cleanup(true); SetState(RecorderState.Faulted);
            if (e is ShotwiseException or OperationCanceledException) throw;
            throw MapError(e);
        }
    }
    private void OpenDuplication()
    {
        _sourceStartQpc = Stopwatch.GetTimestamp();
        try { _duplication = new DuplicationFrameSource(_bounds); }
        catch (Exception e) { _logger.LogWarning(e, "Desktop duplication unavailable; using GDI"); _gdi = new GdiFrameSource(_bounds, _options!.IncludeCursor); }
    }
    public Task StartAsync() => OnWorker(() =>
    {
        Require(RecorderState.Ready); _startQpc = Stopwatch.GetTimestamp(); _timeline.Start(_startQpc);
        _lastDisk = _lastLevels = _startQpc; _audio!.SetAccepting(true); SetState(RecorderState.Recording);
    });
    public Task PauseAsync() => OnWorker(() =>
    {
        Require(RecorderState.Recording); _timeline.Pause(Stopwatch.GetTimestamp()); UpdateElapsed();
        _audio!.SetAccepting(false, clear: false); FlushPending(Elapsed); PumpAudio(true);
        _audio.SetAccepting(false); SetState(RecorderState.Paused);
    });
    public Task ResumeAsync() => OnWorker(() =>
    {
        Require(RecorderState.Paused); _timeline.Resume(Stopwatch.GetTimestamp()); _audio!.SetAccepting(true); SetState(RecorderState.Recording);
    });
    public Task<VideoCaptureResult> StopAsync() => OnWorker(Stop);
    private VideoCaptureResult Stop()
    {
        if (_result != null) return _result;
        if (_failure != null) throw new ShotwiseException(ErrorCode.RecordingFailed, "Recording failed.", _failure);
        if (State is not (RecorderState.Recording or RecorderState.Paused)) throw new InvalidOperationException("Recording has not started.");
        return Complete(null);
    }
    /// <summary>
    /// Finalizes the MP4 and publishes it. <paramref name="sourceFault"/> is set when the recorded source failed:
    /// whatever the writer already accepted is kept. Once <see cref="Mp4Writer.Finish"/> succeeds the file is never deleted.
    /// </summary>
    private VideoCaptureResult Complete(Exception? sourceFault)
    {
        UpdateElapsed(); SetState(RecorderState.Stopping); _audio?.SetAccepting(false, clear: false);
        try
        {
            if (sourceFault == null) { FlushPending(Elapsed); PumpAudio(true); }
            else
            {
                // The pending frame may belong to a lost device; dropping it must not cost the rest of the recording.
                try { FlushPending(Elapsed); } catch (Exception e) { _logger.LogWarning(e, "Last frame was lost while finalizing a partial recording"); }
                try { PumpAudio(true); } catch (Exception e) { _logger.LogWarning(e, "Trailing audio was lost while finalizing a partial recording"); }
                // Nothing was recorded before the source failed: there is no partial recording to keep.
                if (!_writer!.HasVideo) throw sourceFault;
            }
            _writer!.Finish();
        }
        catch (Exception e) { Fault(e); throw MapError(e); }
        var finalized = _temporary!; _temporary = null;
        var writer = _writer; _writer = null;
        try { writer.Dispose(); } catch (Exception e) { _logger.LogWarning(e, "Recording writer teardown failed after finalization"); }
        string path;
        try { path = RecordingOutput.Publish(finalized, _output!, _logger, MoveFile); }
        catch (Exception e) { Fault(e); throw MapError(e); }
        _result = new VideoCaptureResult(path, Elapsed, new Size2(_size.Width, _size.Height), _metadata ?? DocumentMetadata.Create(), false);
        Cleanup(false);
        if (!string.Equals(path, _output, StringComparison.OrdinalIgnoreCase)) Raise(Warning, "Recording_SavedToAlternatePath");
        if (sourceFault != null) Raise(Warning, "Recording_SourceLost");
        SetState(RecorderState.Idle); return _result;
    }
    private void Salvage(Exception cause)
    {
        _logger.LogWarning(cause, "Recording source failed after {Elapsed}; finalizing the partial recording", Elapsed);
        try { Complete(cause); }
        catch (Exception e) { _logger.LogDebug(e, "Partial recording could not be finalized"); }
    }
    public Task CancelAsync() => OnWorker(() => { Cleanup(true); _result = null; _failure = null; Interlocked.Exchange(ref _elapsedTicks, 0); SetState(RecorderState.Idle); });
    public Task RestartAsync() => OnWorker(() =>
    {
        if (_recordingTarget == null || _options == null || _output == null) throw new InvalidOperationException("No prepared recording to restart.");
        if (State is not (RecorderState.Recording or RecorderState.Paused or RecorderState.Ready)) throw new InvalidOperationException("No active recording to restart.");
        Cleanup(true); SetState(RecorderState.Idle); Prepare(_recordingTarget, _options, _output, CancellationToken.None, _frames);
    });
    public void SetMicrophoneMuted(bool muted) => QueueSetting(() => { _micMuted = muted; _audio?.Mute(true, muted); });
    public void SetSystemAudioMuted(bool muted) => QueueSetting(() => { _systemMuted = muted; _audio?.Mute(false, muted); });
    public void SetWebcamFullscreen(bool fullscreen) => QueueSetting(() => _fullscreen = fullscreen);
    public void AddOverlay(IFrameOverlay overlay) { ArgumentNullException.ThrowIfNull(overlay); QueueSetting(() => { if (!_overlays.Contains(overlay)) _overlays.Add(overlay); }); }
    private void QueueSetting(Action action)
    {
        lock (_commands) { ObjectDisposedException.ThrowIf(_disposeRequested != 0, this); _commands.Add(action); }
    }
    private void Require(RecorderState expected) { if (State != expected) throw new InvalidOperationException($"Expected {expected}, found {State}."); }
    private void WebcamFrame(object? sender, BgraImage frame) => Volatile.Write(ref _webcamFrame, frame);
    private void Run()
    {
        var initialized = RoInitialize(1) >= 0;
        try
        {
            while (!_exit)
            {
                if (_commands.TryTake(out var command, State == RecorderState.Recording ? 1 : 50)) command();
                if (State != RecorderState.Recording) continue;
                try { Tick(); }
                catch (SourceFault lost) { Salvage(lost.InnerException!); }
                catch (Exception e) { Fault(e); }
            }
        }
        catch (Exception e) { _logger.LogCritical(e, "Recording worker terminated"); _exited.TrySetException(e); }
        finally { Cleanup(true); if (initialized) RoUninitialize(); _exited.TrySetResult(); }
    }
    private void UpdateElapsed() => Interlocked.Exchange(ref _elapsedTicks, _timeline.At(Stopwatch.GetTimestamp()).Ticks);
    private void Tick()
    {
        UpdateElapsed(); var now = Stopwatch.GetTimestamp();
        if (_recordingTarget?.Window is { } window && _frames == null)
        {
            try { TrackWindow(window); }
            catch (Exception e) { throw new SourceFault(e); }
        }
        if (now - _lastDisk >= Stopwatch.Frequency)
        {
            _lastDisk = now;
            if (!CheckDisk(false))
            {
                // Stop already faulted the recorder if finalization failed; do not fault it twice.
                try { Stop(); } catch (ShotwiseException e) when (State == RecorderState.Faulted) { _logger.LogDebug(e, "Disk-limit stop failed"); }
                return;
            }
        }
        if (now - _lastLevels >= Stopwatch.Frequency / 20) { _lastLevels = now; Raise(Levels, _audio!.Levels); }
        if (_audio?.Failure is { } failure) throw new SourceFault(new ShotwiseException(ErrorCode.AudioInitFailed, "An audio device stopped.", failure));
        PumpAudio(false);
        if (!_governor!.ShouldCapture(now)) return;
        BgraImage? image; Vortice.Direct3D11.ID3D11Texture2D? capturedTexture;
        try { if (!CaptureFrame(now, out image, out capturedTexture)) return; }
        catch (Exception e) { throw new SourceFault(e); }
        BgraImage? frame; Vortice.Direct3D11.ID3D11Texture2D? frameTexture;
        try { (frame, frameTexture) = Compose(image, capturedTexture); }
        catch (Exception e) { throw new SourceFault(e); }
        var at = Elapsed;
        try { FlushPending(at); }
        catch { frameTexture?.Dispose(); throw; }
        _pending = frame; _pendingTexture = frameTexture; _pendingAt = at;
    }
    private void TrackWindow(nint window)
    {
        if (!DesktopNative.TryGetWindowBounds(window, out var current)) throw new ShotwiseException(ErrorCode.WindowNotFound, "Recording window was closed.");
        if (current.X == _bounds.X && current.Y == _bounds.Y) return;
        // Size stays fixed (WGC clips/pads resizes); only the origin follows the window's DWM frame.
        _bounds = new RectI(current.X, current.Y, _bounds.Width, _bounds.Height);
        if (_graphics == null)
        {
            _duplication?.Dispose(); _duplication = null; _gdi?.Dispose(); _gdi = null; OpenDuplication();
        }
    }
    /// <summary>Returns false when no new frame is available yet. Every exception is a source failure.</summary>
    private bool CaptureFrame(long now, out BgraImage? image, out Vortice.Direct3D11.ID3D11Texture2D? capturedTexture)
    {
        image = null; capturedTexture = null;
        StageProbe?.Invoke("Source");
        if (_frames != null) image = _frames.GetFrame(Elapsed);
        else if (_graphics != null)
        {
            try { capturedTexture = _graphics.Capture(); }
            catch (Exception e) { _logger.LogWarning(e, "WGC failed during recording"); _graphics.Dispose(); _graphics = null; OpenDuplication(); Raise(Warning, "Recording_CaptureFallback"); image = null; }
        }
        else if (_duplication != null)
        {
            try { image = _duplication.Capture(); }
            catch (Exception e) { _logger.LogWarning(e, "Desktop duplication failed during recording"); _duplication.Dispose(); _duplication = null; _gdi = new GdiFrameSource(_bounds, _options!.IncludeCursor); Raise(Warning, "Recording_CaptureFallback"); image = null; }
        }
        else image = _gdi!.Capture();
        if (image == null && capturedTexture == null)
        {
            if (_pending == null && _pendingTexture == null && Stopwatch.GetElapsedTime(_sourceStartQpc, now) > TimeSpan.FromSeconds(5))
            {
                if (_graphics != null) { _graphics.Dispose(); _graphics = null; OpenDuplication(); Raise(Warning, "Recording_CaptureFallback"); }
                else if (_duplication != null) { _duplication.Dispose(); _duplication = null; _gdi = new GdiFrameSource(_bounds, _options!.IncludeCursor); _sourceStartQpc = now; Raise(Warning, "Recording_CaptureFallback"); }
                else throw new ShotwiseException(ErrorCode.CaptureFailed, "Capture source produced no frames.");
            }
            return false;
        }
        return true;
    }
    /// <summary>Composites webcam, cursor and overlays (D2D on the recorder device). Every exception is a source/device failure.</summary>
    private (BgraImage? Frame, Vortice.Direct3D11.ID3D11Texture2D? Texture) Compose(BgraImage? image, Vortice.Direct3D11.ID3D11Texture2D? capturedTexture)
    {
        if (capturedTexture != null)
        {
            using (capturedTexture) { StageProbe?.Invoke("Compose"); _canvas!.Begin(capturedTexture); }
        }
        else { StageProbe?.Invoke("Compose"); _canvas!.Begin(image!); }
        DrawWebcam();
        if (_duplication != null && _options!.IncludeCursor && !_fullscreen && DesktopNative.Cursor() is { } cursor)
            _canvas.DrawImage(cursor.Image, new Rect2((cursor.Position.X - _bounds.X) * _size.Width / (double)_bounds.Width, (cursor.Position.Y - _bounds.Y) * _size.Height / (double)_bounds.Height, cursor.Image.Width * _size.Width / (double)_bounds.Width, cursor.Image.Height * _size.Height / (double)_bounds.Height));
        if (!_fullscreen || Volatile.Read(ref _webcamFrame) == null) _cursor!.Render(_canvas, Elapsed, _bounds);
        foreach (var overlay in _overlays) overlay.Render(_canvas, Elapsed, _bounds);
        return _writer!.GpuInput ? (null, _canvas.EndTexture()) : (_canvas.End(), null);
    }
    private void FlushPending(TimeSpan until)
    {
        if (_pending == null && _pendingTexture == null) return;
        var duration = until - _pendingAt;
        try
        {
            if (duration > TimeSpan.Zero)
            {
                if (_pendingTexture != null) _writer!.Video(_pendingTexture, _pendingAt, duration);
                else _writer!.Video(_pending!, _pendingAt, duration);
            }
        }
        finally { _pendingTexture?.Dispose(); _pendingTexture = null; _pending = null; }
    }
    private void PumpAudio(bool final)
    {
        if (_audio == null || _options is not { } options || (!options.RecordMicrophone && !options.RecordSystemAudio)) return;
        var end = Math.Max(0, (long)((Elapsed - (final ? TimeSpan.Zero : TimeSpan.FromMilliseconds(200))).TotalSeconds * 48000));
        while (_audioFrame < end)
        {
            var frames = (int)Math.Min(480, end - _audioFrame);
            _writer!.Audio(_audio.Read(_audioFrame, frames), _audioFrame); _audioFrame += frames;
        }
    }
    private void DrawWebcam()
    {
        var frame = Volatile.Read(ref _webcamFrame); if (frame == null) return;
        if (_fullscreen)
        {
            var scale = Math.Min(_size.Width / (double)frame.Width, _size.Height / (double)frame.Height);
            var w = frame.Width * scale; var h = frame.Height * scale;
            _canvas!.DrawRectangle(new Rect2(0, 0, _size.Width, _size.Height), null, 0, Argb.Black);
            _canvas.DrawImage(frame, new Rect2((_size.Width - w) / 2, (_size.Height - h) / 2, w, h)); return;
        }
        var options = _options!;
        var width = Math.Max(2, (int)(_size.Width * options.PipSizeFraction));
        var height = options.PipShape == WebcamShape.RoundedRectangle ? (int)(width * 0.75) : width;
        height = Math.Min(height, _size.Height - 16); width = Math.Min(width, _size.Width - 16);
        if (width < 2 || height < 2) return;
        var masked = new BgraImage(width, height);
        var radius = options.PipShape == WebcamShape.Circle ? width / 2d : Math.Min(width, height) * 0.18;
        var sourceScale = Math.Max(width / (double)frame.Width, height / (double)frame.Height);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var dx = Math.Max(Math.Max(radius - x - 0.5, x + 0.5 - (width - radius)), 0);
            var dy = Math.Max(Math.Max(radius - y - 0.5, y + 0.5 - (height - radius)), 0);
            var alpha = Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            var srcX = Math.Clamp((int)((x + .5 - width / 2d) / sourceScale + frame.Width / 2d), 0, frame.Width - 1);
            var srcY = Math.Clamp((int)((y + .5 - height / 2d) / sourceScale + frame.Height / 2d), 0, frame.Height - 1);
            for (var c = 0; c < 4; c++) masked.Pixels[(y * width + x) * 4 + c] = (byte)(frame.Pixels[(srcY * frame.Width + srcX) * 4 + c] * alpha);
        }
        var right = options.PipCorner is Corner.TopRight or Corner.BottomRight;
        var bottom = options.PipCorner is Corner.BottomLeft or Corner.BottomRight;
        _canvas!.DrawImage(masked, new Rect2(right ? _size.Width - width - 8 : 8, bottom ? _size.Height - height - 8 : 8, width, height));
    }
    private bool CheckDisk(bool preparing)
    {
        var free = new DriveInfo(Path.GetPathRoot(_output!)!).AvailableFreeSpace;
        var status = RecordingDiskGuard.Evaluate(free, _thresholds);
        if (status == DiskSpaceStatus.Stop)
        {
            if (preparing) throw new ShotwiseException(ErrorCode.LowDiskSpace, "Insufficient recording space.");
            Raise(Warning, "Recording_DiskLimitReached"); return false;
        }
        if (!_warned && status == DiskSpaceStatus.Warning) { _warned = true; Raise(Warning, "Recording_LowDiskSpace"); }
        return true;
    }
    private static ShotwiseException MapError(Exception e) => e as ShotwiseException ?? new ShotwiseException(
        e is DllNotFoundException or EntryPointNotFoundException ? ErrorCode.MediaFeaturePackMissing :
        e is UnauthorizedAccessException || e.HResult == unchecked((int)0x80070005) ? ErrorCode.PrivacyDenied :
        e.HResult == unchecked((int)0xC00D5212) ? ErrorCode.EncoderUnavailable : ErrorCode.RecordingFailed, "Recording failed.", e);
    private void Fault(Exception e)
    {
        _failure = e; _logger.LogError(e, "Recording failed"); Cleanup(true); SetState(RecorderState.Faulted); Raise(Warning, "Error_" + MapError(e).Code);
    }
    private void Cleanup(bool delete)
    {
        // A single failed teardown must not prevent releasing the remaining native resources.
        void Release(Action action) { try { action(); } catch (Exception e) { _logger.LogWarning(e, "Recording resource cleanup failed"); } }
        if (_webcam != null) { _webcam.Frame -= WebcamFrame; Release(_webcam.Dispose); _webcam = null; }
        Volatile.Write(ref _webcamFrame, null);
        if (_audio != null) { Release(_audio.Dispose); _audio = null; }
        if (_cursor != null) { Release(_cursor.Dispose); _cursor = null; }
        if (_writer != null) { Release(_writer.Dispose); _writer = null; }
        if (_graphics != null) { Release(_graphics.Dispose); _graphics = null; }
        if (_duplication != null) { Release(_duplication.Dispose); _duplication = null; }
        if (_gdi != null) { Release(_gdi.Dispose); _gdi = null; }
        if (_canvas != null) { Release(_canvas.Dispose); _canvas = null; }
        _pending = null;
        if (_pendingTexture != null) { Release(_pendingTexture.Dispose); _pendingTexture = null; }
        // _temporary is cleared as soon as the MP4 is finalized, so only unfinalized output is ever deleted here.
        if (delete && _temporary != null) { Release(() => File.Delete(_temporary)); _temporary = null; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_commands)
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) == 0) _commands.Add(() => _exit = true);
        }
        await _exited.Task.ConfigureAwait(false);
    }
}

