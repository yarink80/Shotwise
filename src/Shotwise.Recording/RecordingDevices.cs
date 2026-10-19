using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Shotwise.Core;
using Shotwise.Core.Media;
using Shotwise.Core.Imaging;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Shotwise.Recording;

public sealed class RecordingDeviceService : IAudioDeviceService, IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILogger<RecordingDeviceService> _logger;
    private readonly DeviceWatcher _watcher;
    private int _disposed;
    public RecordingDeviceService(ILogger<RecordingDeviceService> logger)
    {
        _logger = logger;
        _enumerator.RegisterEndpointNotificationCallback(this);
        _watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        _watcher.Added += CameraAdded; _watcher.Removed += CameraRemoved; _watcher.Updated += CameraUpdated;
        _watcher.Start();
    }
    public event EventHandler? DevicesChanged;
    public IReadOnlyList<DeviceInfo> Microphones()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            string? defaultId = null;
            try { using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); defaultId = d.ID; }
            catch (COMException) { }
            var result = new List<DeviceInfo>();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (device) result.Add(new DeviceInfo(device.ID, device.FriendlyName, DeviceKind.Microphone, device.ID == defaultId));
            return result;
        }
        catch (Exception e) { throw DeviceError(e); }
    }
    public IReadOnlyList<DeviceInfo> Webcams()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            // This synchronous Core contract is called on a worker to avoid blocking a UI apartment.
            return Task.Run(async () => (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Select((d, i) => new DeviceInfo(d.Id, d.Name, DeviceKind.Webcam, i == 0, d.IsEnabled)).ToArray()).GetAwaiter().GetResult();
        }
        catch (Exception e) { throw DeviceError(e); }
    }
    public DeviceInfo? DefaultMicrophone()
    {
        var devices = Microphones(); return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
    }
    public bool IsMicrophoneMuted(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try { using var device = _enumerator.GetDevice(id); return device.AudioEndpointVolume.Mute; }
        catch (Exception e) { throw DeviceError(e); }
    }
    internal static ShotwiseException DeviceError(Exception e) => new(e is UnauthorizedAccessException || e.HResult == unchecked((int)0x80070005) ? ErrorCode.PrivacyDenied : ErrorCode.DeviceNotFound, "Recording device unavailable.", e);
    private void Changed()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed != 0) return;
            try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { _logger.LogWarning(e, "Device change subscriber failed"); }
        });
    }
    private void CameraAdded(DeviceWatcher sender, DeviceInformation args) => Changed();
    private void CameraRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => Changed();
    private void CameraUpdated(DeviceWatcher sender, DeviceInformationUpdate args) => Changed();
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed();
    public void OnDeviceAdded(string deviceId) => Changed();
    public void OnDeviceRemoved(string deviceId) => Changed();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Changed();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => Changed();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watcher.Added -= CameraAdded; _watcher.Removed -= CameraRemoved; _watcher.Updated -= CameraUpdated;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        _enumerator.UnregisterEndpointNotificationCallback(this); _enumerator.Dispose();
    }
}

public sealed class WebcamPreviewFactory(ILoggerFactory loggerFactory) : IWebcamPreviewFactory
{
    public IWebcamPreview Create() => new WebcamPreview(loggerFactory.CreateLogger<WebcamPreview>());
}

public sealed class WebcamPreview : IWebcamPreview
{
    private readonly ILogger<WebcamPreview> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private int _disposed, _reading;
    public WebcamPreview(ILogger<WebcamPreview> logger) => _logger = logger;
    public event EventHandler<BgraImage>? Frame;
    public async Task StartAsync(string deviceId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_capture != null) throw new InvalidOperationException("Preview already started.");
            var capture = new MediaCapture(); _capture = capture;
            await capture.InitializeAsync(new MediaCaptureInitializationSettings { VideoDeviceId = deviceId, StreamingCaptureMode = StreamingCaptureMode.Video, SharingMode = MediaCaptureSharingMode.SharedReadOnly, MemoryPreference = MediaCaptureMemoryPreference.Cpu }).AsTask(ct).ConfigureAwait(false);
            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ?? throw new ShotwiseException(ErrorCode.DeviceNotFound, "No camera color source.");
            _reader = await capture.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8).AsTask(ct).ConfigureAwait(false);
            _reader.FrameArrived += OnFrame;
            if (await _reader.StartAsync().AsTask(ct).ConfigureAwait(false) != MediaFrameReaderStartStatus.Success)
                throw new ShotwiseException(ErrorCode.DeviceBusy, "Camera could not start.");
        }
        catch (Exception e)
        {
            Cleanup();
            if (e is OperationCanceledException or ShotwiseException) throw;
            throw RecordingDeviceService.DeviceError(e);
        }
        finally { _gate.Release(); }
    }
    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed != 0 || Interlocked.Exchange(ref _reading, 1) != 0) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var original = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (original == null) return;
            using var bitmap = SoftwareBitmap.Convert(original, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = new BgraImage(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap.CopyToBuffer(result.Pixels.AsBuffer());
            if (_disposed == 0) Frame?.Invoke(this, result);
        }
        catch (Exception e) { _logger.LogWarning(e, "Camera frame delivery failed"); }
        finally { Volatile.Write(ref _reading, 0); }
    }
    private void Cleanup()
    {
        if (_reader != null) { _reader.FrameArrived -= OnFrame; _reader.Dispose(); _reader = null; }
        _capture?.Dispose(); _capture = null;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try { Cleanup(); } finally { _gate.Release(); }
    }
}

