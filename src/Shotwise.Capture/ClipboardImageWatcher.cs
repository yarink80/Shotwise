using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;

namespace Shotwise.Capture;

public sealed class ClipboardImageWatcher : IClipboardImageWatcher, IDisposable
{
    private readonly MessageThread worker;
    private readonly ILogger<ClipboardImageWatcher> logger;
    private volatile bool enabled;
    private uint sequence, ignoredSequence;
    private int disposed;
    public event EventHandler<BgraImage>? ImageCopied;
    public bool IsEnabled { get => enabled; set => enabled = value; }
    public ClipboardImageWatcher(ILogger<ClipboardImageWatcher> logger)
    {
        this.logger = logger; worker = new(logger);
        worker.Message += msg => { if (msg == 0x31d && enabled) Read(); };
        worker.Invoke(() => { Native.Check(Native.AddClipboardFormatListener(worker.Hwnd)); return true; }).GetAwaiter().GetResult();
    }
    /// <summary>Call immediately after an in-process clipboard write if it has no owner window.</summary>
    public void IgnoreCurrentSequence() => Volatile.Write(ref ignoredSequence, Native.GetClipboardSequenceNumber());
    private void Read()
    {
        var current = Native.GetClipboardSequenceNumber();
        if (current == sequence || current == Volatile.Read(ref ignoredSequence)) return;
        Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var pid);
        if (pid == Environment.ProcessId) { sequence = current; Volatile.Write(ref ignoredSequence, current); return; }
        if (!Native.OpenClipboard(worker.Hwnd)) return;
        BgraImage? image = null;
        try
        {
            var png = CopyFormat(Native.RegisterClipboardFormat("PNG"));
            if (png is not null) image = PngCodec.Decode(png);
            else if (CopyFormat(17) is { } dib) image = DecodeDib(dib);
            sequence = current;
        }
        catch (Exception ex) { logger.LogWarning(ex, "Clipboard image could not be decoded"); }
        finally { Native.CloseClipboard(); }
        if (image is not null) ImageCopied?.Invoke(this, image);
    }
    private static byte[]? CopyFormat(uint format)
    {
        var memory = Native.GetClipboardData(format); if (memory == IntPtr.Zero) return null;
        var size = Native.GlobalSize(memory); if (size == 0 || size > 512 * 1024 * 1024) return null;
        var ptr = Native.GlobalLock(memory); if (ptr == IntPtr.Zero) return null;
        try { var bytes = new byte[(int)size]; Marshal.Copy(ptr, bytes, 0, bytes.Length); return bytes; }
        finally { Native.GlobalUnlock(memory); }
    }
    internal static BgraImage DecodeDib(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40) throw new InvalidDataException("Truncated DIB header.");
        int header = BinaryPrimitives.ReadInt32LittleEndian(data), width = BinaryPrimitives.ReadInt32LittleEndian(data[4..]), signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]); uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (header < 40 || header > data.Length || signedHeight == int.MinValue || bits is not (24 or 32) || compression is not (0 or 3)) throw new InvalidDataException("Unsupported DIB layout.");
        int height = Math.Abs(signedHeight), stride = checked((checked(width * bits) + 31) / 32 * 4), offset = header + (compression == 3 && header == 40 ? 12 : 0);
        if (width <= 0 || height == 0 || (long)offset + (long)stride * height > data.Length) throw new InvalidDataException("Truncated DIB pixels.");
        uint r = 0xff0000, g = 0xff00, b = 0xff, a = 0;
        if (compression == 3)
        {
            r = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]); g = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]); b = BinaryPrimitives.ReadUInt32LittleEndian(data[48..]);
            if (header >= 56) a = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        }
        static byte Channel(uint pixel, uint mask)
        {
            if (mask == 0) return 0;
            int shift = System.Numerics.BitOperations.TrailingZeroCount(mask); uint max = mask >> shift;
            return (byte)(((ulong)((pixel & mask) >> shift) * 255 + max / 2) / max);
        }
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int src = offset + (signedHeight < 0 ? y : height - 1 - y) * stride + x * (bits / 8), dst = y * image.Stride + x * 4;
            uint pixel = bits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(data[src..]) : (uint)(data[src] | data[src + 1] << 8 | data[src + 2] << 16);
            int alpha = a == 0 ? 255 : Channel(pixel, a);
            image.Pixels[dst] = (byte)((Channel(pixel, b) * alpha + 127) / 255); image.Pixels[dst + 1] = (byte)((Channel(pixel, g) * alpha + 127) / 255); image.Pixels[dst + 2] = (byte)((Channel(pixel, r) * alpha + 127) / 255); image.Pixels[dst + 3] = (byte)alpha;
        }
        return image;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        enabled = false;
        try { worker.Invoke(() => Native.RemoveClipboardFormatListener(worker.Hwnd)).GetAwaiter().GetResult(); }
        finally { worker.Dispose(); }
    }
}
