using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using static Vortice.MediaFoundation.MediaFactory;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Microsoft.Extensions.Logging;

namespace Shotwise.Recording;

/// <summary>
/// Owned by the recording thread. NV12 input also supports software-only H.264 systems.
/// While the hardware path is on probation every submitted sample is retained, so any native
/// failure (initialization, an asynchronously reported WriteSample error, or Finalize) can
/// recreate the file on the software encoder and re-feed everything written so far.
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    [ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig] int IsSupported(in Guid key);
        [PreserveSig] int IsModifiable(in Guid key);
        [PreserveSig] int GetParameterRange(in Guid key, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(in Guid key, out nint values, out uint count);
        [PreserveSig] int GetDefaultValue(in Guid key, out object value);
        [PreserveSig] int GetValue(in Guid key, out object value);
        [PreserveSig] int SetValue(in Guid key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
    /// <summary>Hardware video frames accepted before the replay buffer is released.</summary>
    internal const int ProbationFrames = 60;
    /// <summary>Retained-sample budget; probation ends early once exceeded (after <see cref="MinimumProbationFrames"/>).</summary>
    private const long ProbationBytes = 192L * 1024 * 1024;
    private const int MinimumProbationFrames = 10;
    private sealed class RetainedSample(bool video, long at, long duration, byte[]? data, ID3D11Texture2D? nv12) : IDisposable
    {
        public bool IsVideo { get; } = video;
        public long At { get; } = at;
        public long Duration { get; } = duration;
        public byte[]? Data { get; } = data;
        public ID3D11Texture2D? Nv12 { get; private set; } = nv12;
        public void Dispose() { Nv12?.Dispose(); Nv12 = null; }
    }
    private IMFSinkWriter? _writer;
    private int _video, _audio = -1;
    private bool _startup;
    private IMFDXGIDeviceManager? _manager;
    private GpuVideoConverter? _converter;
    private readonly string _path;
    private readonly SizeI _size;
    private readonly RecordingOptions _options;
    private readonly ID3D11Device? _device;
    private readonly ILogger? _logger;
    private readonly Action<string>? _hardwareProbe;
    private bool _hardware, _videoAccepted, _retaining, _loggedTexture;
    private int _hardwareFrames;
    private long _retainedBytes;
    private readonly List<RetainedSample> _retained = new();
    private readonly List<(float[] Samples, long FirstFrame)> _pendingAudio = new();
    internal string ActivePath => _hardware ? (_manager != null ? "HardwareDxgi" : "HardwareMemory") : "SoftwareNv12";
    public bool GpuInput => _manager != null;
    /// <summary>True once at least one video sample was accepted (a finalized file then contains content).</summary>
    public bool HasVideo => _videoAccepted;
    /// <summary>True while hardware samples are retained for a possible software re-encode.</summary>
    internal bool OnProbation => _retaining;
    /// <param name="hardwareProbe">Test hook invoked before hardware-path operations: "BeginWriting", "FirstSample", "Sample" (every video sample), "Finalize".</param>
    public Mp4Writer(string path, SizeI size, RecordingOptions options, ID3D11Device? device = null, ILogger? logger = null, Action<string>? hardwareProbe = null)
    {
        _path = path; _size = size; _options = options; _device = device; _logger = logger;
        _hardwareProbe = hardwareProbe; _hardware = options.HardwareEncoding;
        if (size.Width <= 0 || size.Height <= 0 || (size.Width & 1) != 0 || (size.Height & 1) != 0)
            throw new ArgumentException("NV12 requires positive even dimensions.", nameof(size));
        try
        {
            MFStartup(); _startup = true;
            try { Initialize(); }
            catch (Exception e) when (_hardware && IsNativeFailure(e)) { Recover(e, "initialization"); }
        }
        catch { Dispose(); throw; }
    }
    private static bool IsNativeFailure(Exception e) => e is COMException or SharpGen.Runtime.SharpGenException;
    private void Initialize()
    {
        var path = _path; var size = _size; var options = _options; var device = _device;
        using var attributes = MFCreateAttributes(3);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, _hardware ? 1u : 0u);
        if (_hardware && device != null)
        {
            _manager = MFCreateDXGIDeviceManager();
            _manager.ResetDevice(device);
            attributes.Set(SinkWriterAttributeKeys.D3DManager, _manager);
            _converter = new GpuVideoConverter(device, size, options.Fps);
        }
        _logger?.LogInformation("Creating MP4 sink writer {Width}x{Height}@{Fps}: hardwareTransforms={Hardware}, d3dManager={Manager}, adapter={Adapter}",
            size.Width, size.Height, options.Fps, _hardware, _manager != null, _manager != null ? AdapterName(device!) : "none");
        _writer = MFCreateSinkWriterFromURL(path, null, attributes);
        using var output = VideoType(size, options.Fps, VideoFormatGuids.H264);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(size.Width * (double)size.Height * options.Fps * (0.025 + options.Quality * 0.0015), 250_000, 40_000_000));
        _video = _writer.AddStream(output);
        using var input = VideoType(size, options.Fps, VideoFormatGuids.NV12);
        _writer.SetInputMediaType(_video, input, null);
        if (options.RecordMicrophone || options.RecordSystemAudio)
        {
            using var aac = AudioType(AudioFormatGuids.Aac, 16, 24000, 1);
            _audio = _writer.AddStream(aac);
            using var pcm = AudioType(AudioFormatGuids.Pcm, 16, 192000, 4);
            _writer.SetInputMediaType(_audio, pcm, null);
        }
        _writer.BeginWriting();
        if (_hardware) _hardwareProbe?.Invoke("BeginWriting");
        _retaining = _hardware; _hardwareFrames = 0; _loggedTexture = false;
        _logger?.LogInformation("MP4 sink writer started on {Path}; video transforms: {Transforms}", ActivePath, DescribeTransforms());
        TrySetQuality(options.Quality, _logger);
    }
    private static string AdapterName(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var description = adapter.Description;
            return $"{description.Description} (LUID {description.Luid.HighPart:X}:{description.Luid.LowPart:X})";
        }
        catch (Exception e) when (IsNativeFailure(e)) { return "unknown"; }
    }
    /// <summary>Encoder/processor chain the sink writer negotiated for the video stream, with current subtypes.</summary>
    internal string DescribeTransforms()
    {
        if (_writer == null) return "no writer";
        var text = new System.Text.StringBuilder();
        try
        {
            using var ex = _writer.QueryInterface<IMFSinkWriterEx>();
            for (var i = 0; ; i++)
            {
                Guid category; IMFTransform? transform;
                try { ex.GetTransformForStream(_video, i, out category, out transform); }
                catch (Exception e) when (IsNativeFailure(e)) { break; }
                if (transform == null) break;
                using (transform)
                {
                    string name = "unnamed";
                    try { using var a = transform.Attributes; name = a.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? name; }
                    catch (Exception e) when (IsNativeFailure(e)) { }
                    static string Describe(Func<IMFMediaType> get)
                    {
                        try
                        {
                            using var t = get();
                            var sub = t.GetGUID(MediaTypeAttributeKeys.Subtype);
                            var fs = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                            var fourCc = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(BitConverter.ToUInt32(sub.ToByteArray(), 0)));
                            return $"{fourCc} {fs >> 32}x{(uint)fs}";
                        }
                        catch (Exception e) when (IsNativeFailure(e)) { return "n/a"; }
                    }
                    var kind = category == TransformCategoryGuids.VideoEncoder ? "encoder" : category == TransformCategoryGuids.VideoProcessor ? "processor" : category.ToString();
                    text.Append($"[{i}] {kind} '{name}' {Describe(() => transform.GetInputCurrentType(0))} -> {Describe(() => transform.GetOutputCurrentType(0))}; ");
                }
            }
        }
        catch (Exception e) when (IsNativeFailure(e)) { text.Append($"transform query failed 0x{e.HResult:X8}"); }
        return text.Length == 0 ? "none reported" : text.ToString();
    }
    private void ReleaseWriter()
    {
        _writer?.Dispose(); _writer = null;
        _converter?.Dispose(); _converter = null;
        _manager?.Dispose(); _manager = null;
        _audio = -1;
    }
    private void ReleaseRetained()
    {
        foreach (var sample in _retained) sample.Dispose();
        _retained.Clear(); _retainedBytes = 0; _retaining = false;
    }
    /// <summary>
    /// Recreates the owned MP4 on the software encoder and re-feeds every retained sample.
    /// The caller then submits the sample whose hardware submission failed.
    /// </summary>
    private void Recover(Exception error, string stage)
    {
        var replay = _retained.ToArray();
        _retained.Clear(); _retainedBytes = 0;
        try
        {
            _logger?.LogWarning(error, "Hardware video encoding failed during {Stage} after {Frames} accepted frames (HRESULT 0x{HResult:X8}); re-encoding {Count} retained samples with software H.264",
                stage, _hardwareFrames, error.HResult, replay.Length);
            ReleaseWriter();
            _hardware = false; _retaining = false;
            // The failed sink may have written a partial MP4 header. Recreate the same owned file.
            File.Delete(_path);
            Initialize();
            foreach (var sample in replay)
                Write(sample.IsVideo ? _video : _audio, sample.Data ?? ReadbackNv12(sample.Nv12!), sample.At, sample.Duration);
        }
        finally { foreach (var sample in replay) sample.Dispose(); }
    }
    private void TrySetQuality(int quality, ILogger? logger)
    {
        try
        {
            var pointer = _writer!.GetServiceForStream(_video, Guid.Empty, typeof(ICodecApi).GUID);
            try
            {
                var codec = (ICodecApi)Marshal.GetObjectForIUnknown(pointer);
                try
                {
                    var key = new Guid("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");
                    object value = (uint)quality;
                    if (codec.IsSupported(key) == 0 && codec.IsModifiable(key) == 0)
                        Marshal.ThrowExceptionForHR(codec.SetValue(key, ref value));
                }
                finally { Marshal.ReleaseComObject(codec); }
            }
            finally { Marshal.Release(pointer); }
        }
        catch (Exception e) when (e is COMException or SharpGen.Runtime.SharpGenException)
        { logger?.LogDebug(e, "Encoder does not expose quality control; using mapped bitrate"); }
    }
    private static IMFMediaType VideoType(SizeI size, int fps, Guid format)
    {
        var type = MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, format);
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)size.Width << 32) | (uint)size.Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)fps << 32) | 1);
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        return type;
    }
    private static IMFMediaType AudioType(Guid format, uint bits, uint bytes, uint alignment)
    {
        var type = MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        type.Set(MediaTypeAttributeKeys.Subtype, format);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, bits);
        type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, bytes);
        type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, alignment);
        return type;
    }
    public void Video(BgraImage frame, TimeSpan at, TimeSpan duration)
    {
        var nv12 = ToNv12(frame);
        Submit(true, () => { Write(_video, nv12, at.Ticks, duration.Ticks); Retain(true, at, duration, nv12, null); },
            () => Write(_video, nv12, at.Ticks, duration.Ticks));
    }
    public void Video(ID3D11Texture2D frame, TimeSpan at, TimeSpan duration)
    {
        void Software() => Write(_video, ToNv12(Readback(frame)), at.Ticks, duration.Ticks);
        Submit(true, () =>
        {
            if (!GpuInput)
            {
                var bytes = ToNv12(Readback(frame));
                Write(_video, bytes, at.Ticks, duration.Ticks); Retain(true, at, duration, bytes, null);
                return;
            }
            // A fresh NV12 surface per sample: the async encoder may still read earlier ones.
            var converted = _converter!.Convert(frame);
            try
            {
                if (!_loggedTexture)
                {
                    _loggedTexture = true; var d = converted.Description;
                    _logger?.LogDebug("DXGI encoder input {Width}x{Height} {Format} bind={Bind} misc={Misc} at={At} duration={Duration}",
                        d.Width, d.Height, d.Format, d.BindFlags, d.MiscFlags, at, duration);
                }
                using (var buffer = MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, converted, 0, false))
                {
                    using var twoD = buffer.QueryInterface<IMF2DBuffer>();
                    buffer.CurrentLength = twoD.ContiguousLength;
                    using var sample = MFCreateSample();
                    sample.AddBuffer(buffer); sample.SampleTime = at.Ticks; sample.SampleDuration = duration.Ticks;
                    _writer!.WriteSample(_video, sample);
                }
                if (_retaining) { Retain(true, at, duration, null, converted); converted = null; }
            }
            finally { converted?.Dispose(); }
        }, Software);
    }
    private void Retain(bool video, TimeSpan at, TimeSpan duration, byte[]? data, ID3D11Texture2D? nv12)
        => Retain(video, at.Ticks, duration.Ticks, data, nv12);
    private void Retain(bool video, long at, long duration, byte[]? data, ID3D11Texture2D? nv12)
    {
        if (!_retaining) { nv12?.Dispose(); return; }
        _retained.Add(new RetainedSample(video, at, duration, data, nv12));
        _retainedBytes += data?.Length ?? _size.Width * (long)_size.Height * 3 / 2;
    }
    /// <summary>
    /// Submits on the active path. A native failure while samples are retained switches to software,
    /// replays the retained samples and resubmits this one; afterwards failures propagate.
    /// </summary>
    private void Submit(bool video, Action hardware, Action software)
    {
        if (!_hardware) software();
        else
        {
            try
            {
                if (video && !_videoAccepted) _hardwareProbe?.Invoke("FirstSample");
                if (video) _hardwareProbe?.Invoke("Sample");
                hardware();
            }
            catch (Exception e) when (_retaining && IsNativeFailure(e))
            {
                Recover(e, video ? "video WriteSample" : "audio WriteSample");
                software();
            }
            if (video && _hardware && _retaining && (++_hardwareFrames >= ProbationFrames || (_retainedBytes > ProbationBytes && _hardwareFrames >= MinimumProbationFrames)))
            {
                _logger?.LogInformation("Hardware encoder accepted {Frames} frames on {Path}; releasing {Bytes} bytes of retained samples", _hardwareFrames, ActivePath, _retainedBytes);
                ReleaseRetained();
            }
        }
        if (video && !_videoAccepted)
        {
            _videoAccepted = true;
            foreach (var block in _pendingAudio) Audio(block.Samples, block.FirstFrame);
            _pendingAudio.Clear();
        }
    }
    private BgraImage Readback(ID3D11Texture2D frame)
    {
        var description = frame.Description;
        description.BindFlags = BindFlags.None; description.MiscFlags = ResourceOptionFlags.None;
        description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device!.CreateTexture2D(description);
        using var context = _device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        context.CopyResource(staging, frame);
        context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var image = new BgraImage(_size.Width, _size.Height);
            for (var y = 0; y < _size.Height; y++)
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, image.Pixels, y * _size.Width * 4, _size.Width * 4);
            return image;
        }
        finally { context.Unmap(staging, 0); }
    }
    private byte[] ReadbackNv12(ID3D11Texture2D frame)
    {
        var description = frame.Description;
        description.BindFlags = BindFlags.None; description.MiscFlags = ResourceOptionFlags.None;
        description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device!.CreateTexture2D(description);
        using var context = _device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        context.CopyResource(staging, frame);
        context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            int w = _size.Width, h = _size.Height, pitch = (int)mapped.RowPitch;
            var data = new byte[checked(w * h * 3 / 2)];
            // D3D11 maps NV12 as the luma plane followed by the interleaved chroma plane at the same pitch.
            for (var y = 0; y < h; y++) Marshal.Copy(mapped.DataPointer + y * pitch, data, y * w, w);
            for (var y = 0; y < h / 2; y++) Marshal.Copy(mapped.DataPointer + (h + y) * pitch, data, w * h + y * w, w);
            return data;
        }
        finally { context.Unmap(staging, 0); }
    }
    public void Audio(float[] samples, long firstFrame)
    {
        if (_audio < 0) return;
        // Keep audio out of the sink until the video path has accepted input.
        if (!_videoAccepted) { _pendingAudio.Add(((float[])samples.Clone(), firstFrame)); return; }
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * short.MaxValue);
            bytes[i * 2] = (byte)value; bytes[i * 2 + 1] = (byte)(value >> 8);
        }
        long at = firstFrame * TimeSpan.TicksPerSecond / 48000, duration = samples.Length / 2 * TimeSpan.TicksPerSecond / 48000;
        Submit(false, () => { Write(_audio, bytes, at, duration); Retain(false, at, duration, bytes, null); },
            () => Write(_audio, bytes, at, duration));
    }
    private void Write(int stream, byte[] data, long at, long duration)
    {
        using var buffer = MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var pointer, out _, out _);
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = data.Length;
        using var sample = MFCreateSample();
        sample.AddBuffer(buffer); sample.SampleTime = at; sample.SampleDuration = duration;
        _writer!.WriteSample(stream, sample);
    }
    internal static byte[] ToNv12(BgraImage image)
    {
        var w = image.Width; var h = image.Height; var src = image.Pixels;
        if ((w & 1) != 0 || (h & 1) != 0) throw new ArgumentException("NV12 requires even dimensions.", nameof(image));
        var dst = new byte[checked(w * h * 3 / 2)];
        static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var p = (y * w + x) * 4; int b = src[p], g = src[p + 1], r = src[p + 2];
            dst[y * w + x] = Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            if ((x & 1) != 0 || (y & 1) != 0) continue;
            int sumU = 0, sumV = 0;
            for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++)
            {
                var q = ((y + dy) * w + x + dx) * 4;
                sumU += ((-38 * src[q + 2] - 74 * src[q + 1] + 112 * src[q] + 128) >> 8) + 128;
                sumV += ((112 * src[q + 2] - 94 * src[q + 1] - 18 * src[q] + 128) >> 8) + 128;
            }
            var uv = w * h + y / 2 * w + x;
            dst[uv] = Clamp(sumU / 4); dst[uv + 1] = Clamp(sumV / 4);
        }
        return dst;
    }
    public void Finish()
    {
        if (_writer == null) return;
        try
        {
            if (_hardware) _hardwareProbe?.Invoke("Finalize");
            _writer.Finalize();
        }
        catch (Exception e) when (_hardware && _retaining && IsNativeFailure(e))
        {
            Recover(e, "Finalize");
            _writer!.Finalize();
        }
        ReleaseRetained();
    }
    public void Dispose()
    {
        ReleaseWriter();
        ReleaseRetained();
        _pendingAudio.Clear();
        if (_startup) { MFShutdown(); _startup = false; }
    }
}
