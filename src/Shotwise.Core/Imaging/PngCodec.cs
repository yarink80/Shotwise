using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Shotwise.Core.Imaging;

/// <summary>
/// Dependency-free PNG encoder/decoder used by Core (thumbnails, raster undo assets, .shotx/.snagx I/O, tests).
/// Rendering's IImageCodec is preferred for speed when available; this codec is the platform-neutral fallback.
/// Supports all standard colour types and bit depths, PLTE/tRNS, Adam7 interlace and pHYs DPI.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    public const string MediaType = "image/png";

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>Reads width/height from the IHDR chunk without decoding pixels.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 24 || !IsPng(data)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        return width > 0 && height > 0;
    }

    // ------------------------------------------------------------------ encode

    /// <summary>Encodes a premultiplied BGRA image as 8-bit RGBA PNG (straight alpha) with adaptive filtering.</summary>
    public static byte[] Encode(BgraImage image, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        var rowBytes = w * 4;
        var raw = new byte[(rowBytes + 1) * h];
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        var src = image.Pixels;

        for (var y = 0; y < h; y++)
        {
            // unpremultiply BGRA -> RGBA
            var si = y * rowBytes;
            for (var x = 0; x < w; x++, si += 4)
            {
                var c = BgraImage.Unpremultiply(src[si], src[si + 1], src[si + 2], src[si + 3]);
                var o = x * 4;
                cur[o] = c.R; cur[o + 1] = c.G; cur[o + 2] = c.B; cur[o + 3] = c.A;
            }

            byte bestType = 0;
            long bestScore = long.MaxValue;
            for (byte ft = 0; ft <= 4; ft++)
            {
                ApplyFilter(ft, cur, prev, candidate, 4);
                long score = 0;
                for (var i = 0; i < rowBytes; i++) score += candidate[i] < 128 ? candidate[i] : 256 - candidate[i];
                if (score < bestScore) { bestScore = score; bestType = ft; Buffer.BlockCopy(candidate, 0, best, 0, rowBytes); }
            }
            var ro = y * (rowBytes + 1);
            raw[ro] = bestType;
            Buffer.BlockCopy(best, 0, raw, ro + 1, rowBytes);
            (prev, cur) = (cur, prev);
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outp = new MemoryStream(idat.Length + 128);
        outp.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(outp, "IHDR", ihdr);
        if (Math.Abs(image.DpiX - 96) > 0.01 || Math.Abs(image.DpiY - 96) > 0.01)
        {
            Span<byte> phys = stackalloc byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)Math.Round(image.DpiX / 0.0254));
            BinaryPrimitives.WriteUInt32BigEndian(phys[4..], (uint)Math.Round(image.DpiY / 0.0254));
            phys[8] = 1;
            WriteChunk(outp, "pHYs", phys);
        }
        WriteChunk(outp, "IDAT", idat);
        WriteChunk(outp, "IEND", ReadOnlySpan<byte>.Empty);
        return outp.ToArray();
    }

    private static void ApplyFilter(byte type, byte[] cur, byte[] prev, byte[] dst, int bpp)
    {
        var n = cur.Length;
        switch (type)
        {
            case 0: Buffer.BlockCopy(cur, 0, dst, 0, n); break;
            case 1: for (var i = 0; i < n; i++) dst[i] = (byte)(cur[i] - (i >= bpp ? cur[i - bpp] : 0)); break;
            case 2: for (var i = 0; i < n; i++) dst[i] = (byte)(cur[i] - prev[i]); break;
            case 3: for (var i = 0; i < n; i++) dst[i] = (byte)(cur[i] - (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1)); break;
            case 4: for (var i = 0; i < n; i++) dst[i] = (byte)(cur[i] - Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0)); break;
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    // ------------------------------------------------------------------ decode

    /// <summary>Decodes a PNG into a premultiplied BGRA image. Throws <see cref="ShotwiseException"/> on malformed data.</summary>
    public static BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data)) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Not a PNG image.");
        var pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        double dpiX = 96, dpiY = 96;
        using var idat = new MemoryStream();
        var seenEnd = false;

        while (pos + 8 <= data.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
            if (len < 0 || pos + 12L + len > data.Length) throw Corrupt("chunk length out of range");
            var type = data.Slice(pos + 4, 4);
            var body = data.Slice(pos + 8, len);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8 + len, 4));
            var crc = Crc32.Update(0xFFFFFFFFu, data.Slice(pos + 4, 4 + len)) ^ 0xFFFFFFFFu;
            if (crc != storedCrc) throw Corrupt("CRC mismatch");
            pos += 12 + len;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (len != 13) throw Corrupt("bad IHDR");
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                bitDepth = body[8];
                colorType = body[9];
                if (body[10] != 0 || body[11] != 0) throw Corrupt("unsupported compression/filter method");
                interlace = body[12];
            }
            else if (type.SequenceEqual("PLTE"u8)) palette = body.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) trns = body.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(body);
            else if (type.SequenceEqual("pHYs"u8) && len == 9 && body[8] == 1)
            {
                dpiX = BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0254;
                dpiY = BinaryPrimitives.ReadUInt32BigEndian(body[4..]) * 0.0254;
            }
            else if (type.SequenceEqual("IEND"u8)) { seenEnd = true; break; }
        }

        if (width <= 0 || height <= 0) throw Corrupt("missing IHDR");
        if (!seenEnd && idat.Length == 0) throw Corrupt("missing image data");
        if ((long)width * height > BgraImage.MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, $"PNG {width}x{height} is too large.");

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw Corrupt($"colour type {colorType}") };
        var validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth) throw Corrupt($"bit depth {bitDepth} for colour type {colorType}");
        if (colorType == 3 && palette is null) throw Corrupt("missing palette");

        var bitsPerPixel = channels * bitDepth;
        var filterBpp = Math.Max(1, bitsPerPixel / 8);

        // expected decompressed size
        long expected = 0;
        var passes = interlace == 1 ? Adam7 : NoInterlace;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var pw = (width - x0 + dx - 1) / dx;
            var ph = (height - y0 + dy - 1) / dy;
            if (pw <= 0 || ph <= 0) continue;
            expected += (long)ph * (1 + ((long)pw * bitsPerPixel + 7) / 8);
        }
        if (expected > int.MaxValue) throw new ShotwiseException(ErrorCode.ImageTooLarge, "PNG too large.");

        var raw = new byte[expected];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < raw.Length) throw Corrupt("truncated image data");
        }

        var img = new BgraImage(width, height) { DpiX = dpiX > 1 ? dpiX : 96, DpiY = dpiY > 1 ? dpiY : 96 };
        var offset = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var pw = (width - x0 + dx - 1) / dx;
            var ph = (height - y0 + dy - 1) / dy;
            if (pw <= 0 || ph <= 0) continue;
            var rowBytes = (pw * bitsPerPixel + 7) / 8;
            var prev = new byte[rowBytes];
            var cur = new byte[rowBytes];
            for (var py = 0; py < ph; py++)
            {
                var ft = raw[offset];
                Buffer.BlockCopy(raw, offset + 1, cur, 0, rowBytes);
                offset += rowBytes + 1;
                Unfilter(ft, cur, prev, filterBpp);
                var y = y0 + py * dy;
                for (var px = 0; px < pw; px++)
                {
                    var x = x0 + px * dx;
                    var c = ReadPixel(cur, px, colorType, bitDepth, palette, trns);
                    var o = (y * width + x) * 4;
                    var (b, g, r, a) = BgraImage.Premultiply(c);
                    img.Pixels[o] = b; img.Pixels[o + 1] = g; img.Pixels[o + 2] = r; img.Pixels[o + 3] = a;
                }
                (prev, cur) = (cur, prev);
            }
        }
        return img;
    }

    private static readonly (int, int, int, int)[] NoInterlace = { (0, 0, 1, 1) };
    private static readonly (int, int, int, int)[] Adam7 = { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) };

    private static void Unfilter(byte type, byte[] cur, byte[] prev, int bpp)
    {
        var n = cur.Length;
        switch (type)
        {
            case 0: break;
            case 1: for (var i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]); break;
            case 2: for (var i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]); break;
            case 3: for (var i = 0; i < n; i++) cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1)); break;
            case 4: for (var i = 0; i < n; i++) cur[i] = (byte)(cur[i] + Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0)); break;
            default: throw Corrupt($"filter type {type}");
        }
    }

    private static int Sample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8: return row[index];
            case 16: return (row[index * 2] << 8) | row[index * 2 + 1];
            default:
                {
                    var bitPos = index * bitDepth;
                    var b = row[bitPos >> 3];
                    var shift = 8 - bitDepth - (bitPos & 7);
                    return (b >> shift) & ((1 << bitDepth) - 1);
                }
        }
    }

    private static byte To8(int v, int bitDepth) => bitDepth switch
    {
        16 => (byte)(v >> 8),
        8 => (byte)v,
        4 => (byte)(v * 17),
        2 => (byte)(v * 85),
        1 => (byte)(v * 255),
        _ => (byte)v,
    };

    private static Argb ReadPixel(byte[] row, int px, int colorType, int bitDepth, byte[]? palette, byte[]? trns)
    {
        switch (colorType)
        {
            case 0:
                {
                    var v = Sample(row, px, bitDepth);
                    var g = To8(v, bitDepth);
                    var a = trns is { Length: >= 2 } && ((trns[0] << 8) | trns[1]) == v ? (byte)0 : (byte)255;
                    return Argb.FromArgb(a, g, g, g);
                }
            case 2:
                {
                    int r = Sample(row, px * 3, bitDepth), g = Sample(row, px * 3 + 1, bitDepth), b = Sample(row, px * 3 + 2, bitDepth);
                    var a = trns is { Length: >= 6 } && ((trns[0] << 8) | trns[1]) == r && ((trns[2] << 8) | trns[3]) == g && ((trns[4] << 8) | trns[5]) == b ? (byte)0 : (byte)255;
                    return Argb.FromArgb(a, To8(r, bitDepth), To8(g, bitDepth), To8(b, bitDepth));
                }
            case 3:
                {
                    var i = Sample(row, px, bitDepth);
                    if (palette is null || i * 3 + 2 >= palette.Length) return Argb.Transparent;
                    var a = trns is not null && i < trns.Length ? trns[i] : (byte)255;
                    return Argb.FromArgb(a, palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);
                }
            case 4:
                {
                    var g = To8(Sample(row, px * 2, bitDepth), bitDepth);
                    var a = To8(Sample(row, px * 2 + 1, bitDepth), bitDepth);
                    return Argb.FromArgb(a, g, g, g);
                }
            default:
                return Argb.FromArgb(To8(Sample(row, px * 4 + 3, bitDepth), bitDepth), To8(Sample(row, px * 4, bitDepth), bitDepth),
                    To8(Sample(row, px * 4 + 1, bitDepth), bitDepth), To8(Sample(row, px * 4 + 2, bitDepth), bitDepth));
        }
    }

    private static ShotwiseException Corrupt(string what) => new(ErrorCode.CorruptFile, $"Corrupt PNG: {what}.");

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
