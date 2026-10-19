using System.Runtime.InteropServices;
using Shotwise.Core.Media;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Shotwise.Media;

/// <summary>Pulls bounded 48 kHz stereo PCM blocks, trimming at sample boundaries and filling gaps.</summary>
internal sealed class AudioRangeReader : IDisposable
{
    private readonly IMFSourceReader? reader;
    private readonly long sourceStart, outputStart, total;
    private long position, blockStart;
    private byte[]? block;
    private bool ended;
    public AudioRangeReader(string path, bool hasAudio, TimeRange range, long timeline)
    {
        sourceStart = (long)Math.Round(range.Start.TotalSeconds * 48000);
        outputStart = (long)Math.Round(timeline / 1e7 * 48000);
        total = (long)Math.Round(range.Duration.TotalSeconds * 48000);
        if (!hasAudio) { ended = true; return; }
        reader = MFCreateSourceReaderFromURL(Path.GetFullPath(path), null);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false); reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using var pcm = Mp4Writer.AudioType(AudioFormatGuids.Pcm);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, pcm); reader.SetCurrentPosition(range.Start.Ticks);
        }
        catch { reader.Dispose(); throw; }
    }
    public void Advance(Mp4Writer writer, long rangeTicks, CancellationToken ct)
    {
        long target = Math.Min(total, (long)Math.Round(rangeTicks / 1e7 * 48000));
        while (position < target)
        {
            ct.ThrowIfCancellationRequested();
            long sourcePosition = sourceStart + position;
            while (!ended && (block is null || blockStart + block.Length / 4 <= sourcePosition)) ReadBlock(ct);
            int count = (int)Math.Min(4800, target - position);
            byte[] data;
            if (block is null || ended && blockStart + block.Length / 4 <= sourcePosition) data = new byte[count * 4];
            else if (sourcePosition < blockStart) { count = (int)Math.Min(count, blockStart - sourcePosition); data = new byte[count * 4]; }
            else
            {
                int offset = checked((int)(sourcePosition - blockStart) * 4);
                count = Math.Min(count, (block.Length - offset) / 4); data = new byte[count * 4]; Buffer.BlockCopy(block, offset, data, 0, data.Length);
            }
            writer.Audio(data, outputStart + position); position += count;
        }
    }
    private void ReadBlock(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var sample = reader!.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out long time);
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidDataException("Audio decoder failed.");
            if (sample is null) { if ((flags & SourceReaderFlag.EndOfStream) != 0) { ended = true; block = null; return; } continue; }
            using var buffer = sample.ConvertToContiguousBuffer(); buffer.Lock(out nint pointer, out _, out int length);
            try { block = new byte[length - length % 4]; Marshal.Copy(pointer, block, 0, block.Length); blockStart = (long)Math.Round(time / 1e7 * 48000); }
            finally { buffer.Unlock(); }
            if (block.Length > 0) return;
        }
    }
    public void Dispose() => reader?.Dispose();
}
