using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Shotwise.Core;
using Shotwise.Core.Media;

namespace Shotwise.Recording;

internal sealed class RecordingAudio : IDisposable
{
    private readonly List<Source> _sources = new();
    private readonly Func<TimeSpan> _elapsed;
    private readonly ILogger _logger;
    private int _accepting;
    /// <summary>Device-free instance for tests; sources are attached with <see cref="Attach"/>.</summary>
    internal RecordingAudio(Func<TimeSpan> elapsed, ILogger logger) { _elapsed = elapsed; _logger = logger; }
    internal Source Attach(IWaveIn capture, bool mic)
    {
        var source = new Source(capture, mic, null, this); _sources.Add(source); source.Start(); return source;
    }
    public RecordingAudio(RecordingOptions options, INoiseSuppressor? suppressor, Func<TimeSpan> elapsed, ILogger logger)
    {
        _elapsed = elapsed; _logger = logger;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (options.RecordMicrophone)
            {
                var device = options.MicrophoneId == null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : enumerator.GetDevice(options.MicrophoneId);
                var denoiser = options.RemoveNoise && suppressor?.IsAvailable == true ? suppressor.CreateStream(48000, 2) : null;
                try { _sources.Add(new Source(new WasapiCapture(device), true, denoiser, this, device)); }
                catch { device.Dispose(); denoiser?.Dispose(); throw; }
            }
            if (options.RecordSystemAudio) _sources.Add(new Source(new WasapiLoopbackCapture(), false, null, this));
            foreach (var source in _sources) source.Start();
        }
        catch (Exception e)
        {
            Dispose();
            var code = e is UnauthorizedAccessException || e.HResult == unchecked((int)0x80070005) ? ErrorCode.PrivacyDenied :
                e.HResult == unchecked((int)0x8889000A) ? ErrorCode.DeviceBusy :
                e.HResult is unchecked((int)0x80070490) or unchecked((int)0x88890004) ? ErrorCode.DeviceNotFound : ErrorCode.AudioInitFailed;
            throw new ShotwiseException(code, "Audio capture could not initialize.", e);
        }
    }
    public void SetAccepting(bool enabled, bool clear = true)
    {
        Volatile.Write(ref _accepting, enabled ? 1 : 0);
        foreach (var source in _sources) source.Synchronize(clear);
    }
    public void Mute(bool mic, bool muted) { foreach (var source in _sources.Where(s => s.IsMic == mic)) source.Muted = muted; }
    public AudioLevels Levels => new(_sources.Where(s => s.IsMic).Select(s => s.Peak).DefaultIfEmpty().Max(), _sources.Where(s => !s.IsMic).Select(s => s.Peak).DefaultIfEmpty().Max());
    public float[] Read(long firstFrame, int frames)
    {
        var samples = new float[frames * 2];
        foreach (var source in _sources) source.Queue.Mix(firstFrame, samples, source.Muted);
        return samples;
    }
    public Exception? Failure => _sources.Select(s => s.Failure).FirstOrDefault(e => e != null);
    public void Dispose() { foreach (var source in _sources) source.Dispose(); _sources.Clear(); }
    internal sealed class Source : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly INoiseSuppressorStream? _denoiser;
        private readonly RecordingAudio _owner;
        private readonly MMDevice? _device;
        private readonly BufferedWaveProvider _buffer;
        private readonly ISampleProvider _resampler;
        private readonly object _gate = new();
        private long _nextFrame = -1;
        private int _disposed;
        public bool IsMic { get; }
        public volatile bool Muted;
        public double Peak;
        public Exception? Failure;
        public AudioTimelineQueue Queue { get; } = new();
        public Source(IWaveIn capture, bool mic, INoiseSuppressorStream? denoiser, RecordingAudio owner, MMDevice? device = null)
        {
            _capture = capture; IsMic = mic; _denoiser = denoiser; _owner = owner; _device = device;
            _buffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = false };
            ISampleProvider provider = _buffer.ToSampleProvider();
            if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
            else if (provider.WaveFormat.Channels != 2)
            {
                var mux = new MultiplexingSampleProvider(new[] { provider }, 2);
                mux.ConnectInputToOutput(0, 0); mux.ConnectInputToOutput(1, 1); provider = mux;
            }
            _resampler = new WdlResamplingSampleProvider(provider, 48000);
            capture.DataAvailable += Data; capture.RecordingStopped += Stopped;
        }
        public void Start() => _capture.StartRecording();
        public void Reset() { lock (_gate) { Queue.Clear(); _buffer.ClearBuffer(); _nextFrame = -1; Peak = 0; } }
        public void Synchronize(bool clear) { lock (_gate) { if (clear) Reset(); } }
        private void Stopped(object? sender, StoppedEventArgs e) { if (_disposed == 0) Failure = e.Exception ?? new InvalidOperationException("Audio capture stopped unexpectedly."); }
        private void Data(object? sender, WaveInEventArgs e)
        {
            lock (_gate)
            {
                if (_disposed != 0 || Volatile.Read(ref _owner._accepting) == 0) return;
                try
                {
                    _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    var result = new List<float>(); var scratch = new float[4096]; int read;
                    while ((read = _resampler.Read(scratch, 0, scratch.Length)) > 0) result.AddRange(scratch.AsSpan(0, read).ToArray());
                    var samples = result.ToArray();
                    if (samples.Length == 0) return;
                    _denoiser?.Process(samples);
                    Peak = Muted ? 0 : Math.Min(1, samples.Select(v => Math.Abs((double)v)).DefaultIfEmpty().Max());
                    var observedStart = Math.Max(0, (long)(_owner._elapsed().TotalSeconds * 48000) - samples.Length / 2);
                    if (_nextFrame < 0 || Math.Abs(observedStart - _nextFrame) > 4800) _nextFrame = observedStart;
                    Queue.Enqueue(_nextFrame, samples); _nextFrame += samples.Length / 2;
                }
                catch (Exception ex) { Failure = ex; _owner._logger.LogError(ex, "Audio source failed"); }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _capture.DataAvailable -= Data; _capture.RecordingStopped -= Stopped;
            try { _capture.StopRecording(); }
            finally
            {
                // Disposing the capture joins its thread, which may be about to enter Data and take _gate.
                // Holding _gate here would deadlock that join, so only the state Data touches is released under it.
                try { try { _capture.Dispose(); } finally { _device?.Dispose(); } }
                finally { lock (_gate) { _denoiser?.Dispose(); Queue.Clear(); } }
            }
        }
    }
}



