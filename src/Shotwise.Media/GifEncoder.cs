using System.Text;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;

namespace Shotwise.Media;

/// <summary>Streaming GIF89a writer; local adaptive octree palettes, transparent deltas, and LZW.</summary>
internal sealed class GifEncoder : IDisposable
{
    private readonly BinaryWriter writer;
    private readonly int width, height;
    private BgraImage? previous;
    private double requestedDelay;
    private long writtenDelay;
    private bool finished;
    private bool clearedLast;

    public GifEncoder(Stream stream, int width, int height, bool loop)
    {
        this.width = width; this.height = height;
        writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("GIF89a")); U16(width); U16(height);
        writer.Write(new byte[] { 0x70, 0, 0 });
        if (loop) writer.Write(new byte[] { 0x21, 0xff, 11, 78, 69, 84, 83, 67, 65, 80, 69, 50, 46, 48, 3, 1, 0, 0, 0 });
    }

    public void Write(BgraImage image, TimeSpan delay, bool dither, CancellationToken ct, GifDitherMode mode = GifDitherMode.FloydSteinberg, bool clearAfter = false)
    {
        if (finished) throw new ObjectDisposedException(nameof(GifEncoder));
        if (image.Width != width || image.Height != height || delay <= TimeSpan.Zero) throw new ArgumentException("Invalid GIF frame.");
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (previous is null || clearedLast || clearAfter || !image.Pixels.AsSpan(i, 4).SequenceEqual(previous.Pixels.AsSpan(i, 4)))
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = y; }
            }
        }
        if (right < 0) { left = top = right = bottom = 0; }
        int w = right - left + 1, h = bottom - top + 1;
        var crop = image.Crop(new(left, top, w, h));
        var palette = OctreePalette.Create(crop, ct);
        byte[] indices = palette.Map(crop, dither, ct, mode);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int src = ((y + top) * width + x + left) * 4;
            if (image.Pixels[src + 3] < 128 || !clearedLast && !clearAfter && previous is not null && image.Pixels.AsSpan(src, 4).SequenceEqual(previous.Pixels.AsSpan(src, 4))) indices[y * w + x] = 0;
        }
        requestedDelay += delay.TotalSeconds * 100;
        long remaining = Math.Max(1, (long)Math.Round(requestedDelay) - writtenDelay);
        writtenDelay += remaining;
        do
        {
            int part = (int)Math.Min(65535, remaining);
            writer.Write(new byte[] { 0x21, 0xf9, 4, clearAfter ? (byte)9 : (byte)5 }); U16(part); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)0x2c); U16(left); U16(top); U16(w); U16(h); writer.Write((byte)0x87);
            writer.Write(palette.Rgb);
            writer.Write((byte)8);
            byte[] data = Lzw(indices);
            for (int i = 0; i < data.Length; i += 255) { int n = Math.Min(255, data.Length - i); writer.Write((byte)n); writer.Write(data, i, n); }
            writer.Write((byte)0);
            remaining -= part;
        } while (remaining > 0);
        previous = image.Clone();
        clearedLast = clearAfter;
    }

    internal static byte[] Lzw(byte[] pixels)
    {
        using var stream = new MemoryStream();
        int bits = 0, count = 0, size = 9, next = 258;
        void Emit(int code) { bits |= code << count; count += size; while (count >= 8) { stream.WriteByte((byte)bits); bits >>= 8; count -= 8; } }
        var dictionary = new Dictionary<int, int>();
        Emit(256);
        if (pixels.Length > 0)
        {
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int key = (prefix << 8) | pixels[i];
                if (dictionary.TryGetValue(key, out int code)) { prefix = code; continue; }
                Emit(prefix);
                // The decoder adds an entry one output code later than the encoder.
                if (next >= (1 << size) && size < 12) size++;
                if (next < 4096) dictionary[key] = next++;
                else { Emit(256); dictionary.Clear(); next = 258; size = 9; }
                prefix = pixels[i];
            }
            Emit(prefix);
            if (next >= (1 << size) && size < 12) size++;
        }
        Emit(257); if (count > 0) stream.WriteByte((byte)bits);
        return stream.ToArray();
    }
    private void U16(int value) => writer.Write(checked((ushort)value));
    public void Dispose() { if (finished) return; finished = true; writer.Write((byte)0x3b); writer.Flush(); writer.Dispose(); }
}

internal sealed class OctreePalette
{
    private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    private sealed class Node
    {
        public readonly Node?[] Children = new Node?[8];
        public long R, G, B, Count;
        public bool Leaf;
        public byte Index;
    }
    private readonly Node root;
    public byte[] Rgb { get; } = new byte[768];
    private OctreePalette(Node root) => this.root = root;
    public static OctreePalette Create(BgraImage image, CancellationToken ct)
    {
        var root = new Node();
        var levels = Enumerable.Range(0, 8).Select(_ => new List<Node>()).ToArray();
        int leaves = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            if (image.Pixels[i + 3] < 128) continue;
            int a = image.Pixels[i + 3], r = Math.Min(255, image.Pixels[i + 2] * 255 / a), g = Math.Min(255, image.Pixels[i + 1] * 255 / a), b = Math.Min(255, image.Pixels[i] * 255 / a);
            Node n = root;
            for (int depth = 0; ; depth++)
            {
                n.R += r; n.G += g; n.B += b; n.Count++;
                if (n.Leaf) break;
                if (depth == 8) { n.Leaf = true; leaves++; break; }
                int shift = 7 - depth, child = ((r >> shift & 1) << 2) | ((g >> shift & 1) << 1) | (b >> shift & 1);
                if (n.Children[child] is null) { n.Children[child] = new Node(); if (depth < 7) levels[depth + 1].Add(n.Children[child]!); }
                n = n.Children[child]!;
            }
            while (leaves > 255)
            {
                Node? reduce = null;
                for (int depth = 7; depth >= 1 && reduce is null; depth--)
                {
                    var list = levels[depth];
                    while (list.Count > 0 && reduce is null) { var candidate = list[^1]; list.RemoveAt(list.Count - 1); if (!candidate.Leaf) reduce = candidate; }
                }
                reduce ??= root;
                int CountLeaves(Node node) => node.Leaf ? 1 : node.Children.Where(c => c is not null).Sum(c => CountLeaves(c!));
                leaves -= CountLeaves(reduce) - 1; reduce.Leaf = true; Array.Clear(reduce.Children);
            }
        }
        var result = new OctreePalette(root); int index = 1;
        void Visit(Node n)
        {
            if (n.Leaf && n.Count > 0) { n.Index = (byte)index; result.Rgb[index * 3] = (byte)(n.R / n.Count); result.Rgb[index * 3 + 1] = (byte)(n.G / n.Count); result.Rgb[index++ * 3 + 2] = (byte)(n.B / n.Count); }
            else foreach (Node? c in n.Children) if (c is not null) Visit(c);
        }
        Visit(root); return result;
    }
    public byte[] Map(BgraImage image, bool dither, CancellationToken ct, GifDitherMode mode = GifDitherMode.FloydSteinberg)
    {
        var output = new byte[image.Width * image.Height];
        var errors = new double[(image.Width + 2) * 3]; var next = new double[errors.Length];
        Span<double> rgb = stackalloc double[3];
        for (int y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4, a = image.Pixels[i + 3]; if (a < 128) continue;
                double ordered = (Bayer[(y % 4) * 4 + x % 4] - 7.5) * 2;
                for (int c = 0; c < 3; c++) rgb[c] = Math.Clamp(image.Pixels[i + 2 - c] * 255.0 / a + (dither ? mode == GifDitherMode.Ordered ? ordered : errors[(x + 1) * 3 + c] : 0), 0, 255);
                Node n = root;
                for (int depth = 0; !n.Leaf && depth < 8; depth++)
                {
                    int s = 7 - depth, child = (((int)rgb[0] >> s & 1) << 2) | (((int)rgb[1] >> s & 1) << 1) | ((int)rgb[2] >> s & 1);
                    if (n.Children[child] is null) break; n = n.Children[child]!;
                }
                byte idx = n.Index;
                if (idx == 0) { double best = double.MaxValue; for (int k = 1; k < 256; k++) { double d = 0; for (int c = 0; c < 3; c++) d += Math.Pow(rgb[c] - Rgb[k * 3 + c], 2); if (d < best) { best = d; idx = (byte)k; } } }
                output[y * image.Width + x] = idx;
                if (dither && mode == GifDitherMode.FloydSteinberg) for (int c = 0; c < 3; c++) { double e = rgb[c] - Rgb[idx * 3 + c]; errors[(x + 2) * 3 + c] += e * 7 / 16; next[x * 3 + c] += e * 3 / 16; next[(x + 1) * 3 + c] += e * 5 / 16; next[(x + 2) * 3 + c] += e / 16; }
            }
            (errors, next) = (next, errors); Array.Clear(next);
        }
        return output;
    }
}
