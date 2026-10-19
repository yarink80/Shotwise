using System.Runtime.InteropServices;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;
using VideoInfo = Shotwise.Core.Media.VideoInfo;

namespace Shotwise.Media;

internal sealed class MediaRuntime : IDisposable
{
    private static readonly object Gate = new();
    private static int references;
    private readonly bool uninitialize;
    public MediaRuntime()
    {
        int hr = CoInitializeEx(0, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        uninitialize = true;
        try { lock (Gate) { if (references == 0) MFStartup().CheckError(); references++; } }
        catch { CoUninitialize(); throw; }
    }
    public void Dispose() { lock (Gate) { if (--references == 0) MFShutdown().CheckError(); } if (uninitialize) CoUninitialize(); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}

internal sealed class VideoReader : IDisposable
{
    private readonly IMFSourceReader reader;
    public VideoInfo Info { get; }
    private int stride;
    public VideoReader(string path)
    {
        using var attributes = MFCreateAttributes(2);
        attributes.Set(new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3"), 1u);
        reader = MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes);
        try
        {
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            ulong size = native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            ulong rate = native.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            var duration = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            long ticks = Convert.ToInt64(duration.Value);
            bool audio;
            try { using var a = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0); audio = true; }
            catch { audio = false; }
            double fps = (uint)rate == 0 ? 30 : (double)(rate >> 32) / (uint)rate;
            Info = new(TimeSpan.FromTicks(ticks), (int)(size >> 32), (int)(uint)size, fps, audio, (long)Math.Round(ticks / 1e7 * fps));
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using var output = MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
            using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint value).Success ? unchecked((int)value) : Info.Width * 4;
        }
        catch { reader.Dispose(); throw; }
    }
    public void Seek(long ticks) => reader.SetCurrentPosition(Math.Max(0, ticks));

    /// <summary>
    /// H.264 decoders report a media-type change on the first samples when the coded size is padded to whole macroblocks
    /// (e.g. 640x360 → 640x368, 1920x1080 → 1920x1088). The picture keeps its top-left origin, so refresh the stride and
    /// keep copying the display size; a genuinely smaller frame is still rejected (P3 fix: recordings with heights that
    /// are not multiples of 16 could not be thumbnailed, stepped or converted to GIF).
    /// </summary>
    private void RefreshOutputType()
    {
        using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        ulong size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int w = (int)(size >> 32), h = (int)(uint)size;
        if (w < Info.Width || h < Info.Height) throw new InvalidDataException($"Video frame size changed to {w}x{h} (expected {Info.Width}x{Info.Height}).");
        stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint value).Success ? unchecked((int)value) : w * 4;
    }
    public (BgraImage Image, long Time, long Duration)? Read(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long time);
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidDataException("Video decoder reported an error.");
            if (sample is null) { if ((flags & SourceReaderFlag.EndOfStream) != 0) return null; continue; }
            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0) RefreshOutputType();
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out nint pointer, out _, out int length);
            try
            {
                int row = Info.Width * 4;
                if (length < (long)Math.Abs(stride) * (Info.Height - 1) + row) throw new InvalidDataException("Truncated video frame.");
                var image = new BgraImage(Info.Width, Info.Height);
                for (int y = 0; y < Info.Height; y++) Marshal.Copy(pointer + (stride < 0 ? Info.Height - 1 - y : y) * Math.Abs(stride), image.Pixels, y * row, row);
                for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
                long d;
                try { d = sample.SampleDuration; } catch { d = (long)Math.Round(1e7 / Info.Fps); }
                return (image, time, Math.Max(1, d));
            }
            finally { buffer.Unlock(); }
        }
    }
    public BgraImage At(long ticks, CancellationToken ct)
    {
        Seek(ticks); BgraImage? last = null;
        while (Read(ct) is { } frame)
        {
            last = frame.Image;
            if (frame.Time + frame.Duration > ticks || frame.Time >= ticks) return last;
        }
        return last ?? throw new InvalidDataException("Video contains no frames.");
    }
    public void Dispose() => reader.Dispose();
}

internal sealed class Mp4Writer : IDisposable
{
    private readonly IMFSinkWriter writer;
    private readonly int videoStream, audioStream = -1;
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    public Mp4Writer(string path, int width, int height, double fps, VideoExportOptions options, bool audio)
    {
        if (width is < 2 or > 8192 || height is < 2 or > 8192 || width % 2 != 0 || height % 2 != 0 || !double.IsFinite(fps) || fps is <= 0 or > 240) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Fps = fps;
        using var attributes = MFCreateAttributes(2);
        attributes.Set(new Guid("a634a91c-822b-41b9-a494-4de4643612b0"), options.HardwareEncoding ? 1u : 0u);
        writer = MFCreateSinkWriterFromURL(path, null, attributes);
        try
        {
            using var output = VideoType(VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(width * (long)height * fps * (.025 + Math.Clamp(options.Quality, 0, 100) / 100.0 * .15), 128000, 100000000));
            videoStream = writer.AddStream(output);
            using var input = VideoType(VideoFormatGuids.Rgb32);
            writer.SetInputMediaType(videoStream, input, null);
            if (audio)
            {
                using var aout = AudioType(AudioFormatGuids.Aac);
                int bitrate = new[] { 96, 128, 160, 192 }.MinBy(value => Math.Abs(value - (long)options.AudioBitrateKbps));
                aout.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(bitrate * 125));
                aout.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                audioStream = writer.AddStream(aout);
                using var ain = AudioType(AudioFormatGuids.Pcm);
                writer.SetInputMediaType(audioStream, ain, null);
            }
            writer.BeginWriting();
        }
        catch { writer.Dispose(); throw; }
    }
    private IMFMediaType VideoType(Guid subtype)
    {
        var type = MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video); type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)Width << 32) | (uint)Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Math.Round(Fps * 10000) << 32) | 10000u);
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        type.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(Width * 4));
        return type;
    }
    internal static IMFMediaType AudioType(Guid subtype)
    {
        var type = MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio); type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u); type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u);
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        if (subtype == AudioFormatGuids.Pcm) { type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u); type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192000u); }
        return type;
    }
    public void Video(BgraImage frame, long time, long duration)
    {
        if (frame.Width != Width || frame.Height != Height) frame = PixelScaler.Resize(frame, Width, Height, true);
        Write(videoStream, frame.Pixels, time, duration);
    }
    public void Audio(byte[] pcm, long sampleOffset) { if (audioStream >= 0 && pcm.Length > 0) Write(audioStream, pcm, sampleOffset * 10000000 / 48000, (pcm.Length / 4L) * 10000000 / 48000); }
    private void Write(int stream, byte[] data, long time, long duration)
    {
        using var sample = MFCreateSample(); using var buffer = MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out nint ptr, out _, out _);
        try { Marshal.Copy(data, 0, ptr, data.Length); } finally { buffer.Unlock(); }
        buffer.CurrentLength = data.Length; sample.AddBuffer(buffer); sample.SampleTime = time; sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }
    public void Complete() => writer.Finalize();
    public void Dispose() => writer.Dispose();
}
