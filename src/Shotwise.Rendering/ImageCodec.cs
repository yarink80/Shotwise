using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shotwise.Core;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using SkiaSharp;

namespace Shotwise.Rendering;

/// <summary>
/// Image decode/encode: Skia for PNG / JPEG / WebP (and decoding of BMP / GIF / ICO), WIC (System.Windows.Media.Imaging)
/// for BMP / GIF / TIFF (multi-page) encoding and TIFF / multi-frame decoding, hand-written ICO container, PDF via PDFsharp / Windows.Data.Pdf.
/// </summary>
public sealed class SkiaImageCodec : IImageCodec
{
    private readonly ILogger<SkiaImageCodec> _log;

    public SkiaImageCodec(ILogger<SkiaImageCodec>? log = null) => _log = log ?? NullLogger<SkiaImageCodec>.Instance;

    public IReadOnlyList<ImageFormatInfo> SupportedFormats { get; } = new ImageFormatInfo[]
    {
        new(ImageFormat.Png, ".png", "PNG", true, true, true, false, new[] { "bitDepth" }),
        new(ImageFormat.Jpeg, ".jpg", "JPEG", true, true, false, false, new[] { "jpegQuality", "subsampling", "matteColor" }),
        new(ImageFormat.Bmp, ".bmp", "BMP", true, true, true, false, new[] { "bitDepth", "transparency", "matteColor" }),
        new(ImageFormat.Gif, ".gif", "GIF", true, true, true, true, new[] { "transparency" }),
        new(ImageFormat.Tiff, ".tif", "TIFF", true, true, true, true, new[] { "tiffCompression", "transparency" }),
        new(ImageFormat.WebP, ".webp", "WebP", true, true, true, false, new[] { "webpQuality", "webpLossless" }),
        new(ImageFormat.Ico, ".ico", "Icon", true, true, true, true, new[] { "icoSizes" }),
        new(ImageFormat.Pdf, ".pdf", "PDF", true, true, false, true, new[] { "pdf", "pdfSearchableText" }),
    };

    // ------------------------------------------------------------------ decode

    public BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Empty image data.");
        if (data.Length > 512 * 1024 * 1024) throw new ShotwiseException(ErrorCode.FileTooLarge, "Encoded image exceeds size limit.");
        var bytes = data.ToArray();
        if (PdfRasterizer.LooksLikePdf(bytes))
        {
            var pages = PdfRasterizer.RenderAll(bytes, 150, 1);
            return pages.Count > 0 ? pages[0] : throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Unreadable PDF.");
        }
        if (IsTiff(bytes)) return DecodeWic(bytes, firstOnly: true).First();
        var img = DecodeSkia(bytes);
        if (img is not null) return img;
        try { return DecodeWic(bytes, firstOnly: true).First(); }
        catch (Exception e) when (e is not ShotwiseException)
        {
            throw new ShotwiseException(ErrorCode.UnsupportedFormat, "The image format is not recognised or the file is corrupt.", e);
        }
    }

    public BgraImage Load(string path)
    {
        var bytes = ReadFile(path);
        ValidateExtension(path, bytes);
        return Decode(bytes);
    }

    public IReadOnlyList<BgraImage> LoadAllPages(string path)
    {
        var bytes = ReadFile(path);
        ValidateExtension(path, bytes);
        if (PdfRasterizer.LooksLikePdf(bytes))
        {
            var pages = PdfRasterizer.RenderAll(bytes);
            if (pages.Count == 0) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Unreadable PDF.");
            return pages;
        }
        if (IsTiff(bytes) || IsGif(bytes) || IsIco(bytes))
        {
            try
            {
                var frames = DecodeWic(bytes);
                if (frames.Count > 0) return frames;
            }
            catch (Exception e) when (e is not ShotwiseException)
            {
                _log.LogDebug(e, "WIC multi-frame decode failed for {Path}; falling back to Skia", path);
            }
        }
        return new[] { Decode(bytes) };
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new ShotwiseException(ErrorCode.FileTooLarge, "Encoded image exceeds size limit.");
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException e) { throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}", e); }
        catch (DirectoryNotFoundException e) { throw new ShotwiseException(ErrorCode.FileNotFound, $"File not found: {path}", e); }
        catch (UnauthorizedAccessException e) { throw new ShotwiseException(ErrorCode.AccessDenied, $"Access denied: {path}", e); }
    }

    internal static void ValidateExtension(string path, byte[] bytes)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var valid = ext switch
        {
            ".png" => PngCodec.IsPng(bytes),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
            ".bmp" => bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M',
            ".gif" => IsGif(bytes),
            ".tif" or ".tiff" => IsTiff(bytes),
            ".ico" => IsIco(bytes),
            ".webp" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            ".pdf" => PdfRasterizer.LooksLikePdf(bytes),
            _ => false,
        };
        if (!valid) throw new ShotwiseException(ErrorCode.InvalidFileFormat, "Image extension and content do not agree.");
    }

    private static bool IsTiff(byte[] d) => d.Length > 4 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42));
    private static bool IsGif(byte[] d) => d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F';
    private static bool IsIco(byte[] d) => d.Length > 6 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0;

    private static BgraImage? DecodeSkia(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null) return null;
        var info = codec.Info;
        if (codec.FrameCount > 200) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Too many image frames.");
        if (info.Width <= 0 || info.Height <= 0) return null;
        if ((long)info.Width * info.Height > BgraImage.MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Image too large.");
        using var bmp = new SKBitmap(SkiaInterop.Info(info.Width, info.Height));
        var res = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        var (dx, dy) = PngDpi(data);
        var origin = codec.EncodedOrigin;
        if (origin != SKEncodedOrigin.TopLeft && origin != SKEncodedOrigin.Default)
        {
            using var rotated = ApplyOrigin(bmp, origin);
            return SkiaInterop.ToBgra(rotated, dx, dy);
        }
        return SkiaInterop.ToBgra(bmp, dx, dy);
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin o)
    {
        var swap = o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(SkiaInterop.Info(w, h));
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.Transparent);
        var m = o switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, src.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, src.Width, 0, -1, src.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, src.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, src.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, src.Height, -1, 0, src.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, src.Width, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        c.SetMatrix(m);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Reads DPI from a PNG pHYs chunk (defaults to 96).</summary>
    private static (double X, double Y) PngDpi(byte[] d)
    {
        if (!PngCodec.IsPng(d)) return (96, 96);
        var i = 8;
        while (i + 12 <= d.Length)
        {
            var len = (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
            if (len < 0 || i + 12 + len > d.Length) break;
            if (d[i + 4] == 'p' && d[i + 5] == 'H' && d[i + 6] == 'Y' && d[i + 7] == 's' && len >= 9 && d[i + 16] == 1)
            {
                var px = (uint)((d[i + 8] << 24) | (d[i + 9] << 16) | (d[i + 10] << 8) | d[i + 11]);
                var py = (uint)((d[i + 12] << 24) | (d[i + 13] << 16) | (d[i + 14] << 8) | d[i + 15]);
                if (px > 0 && py > 0) return (px * 0.0254, py * 0.0254);
            }
            if (d[i + 4] == 'I' && d[i + 5] == 'D' && d[i + 6] == 'A' && d[i + 7] == 'T') break;
            i += 12 + len;
        }
        return (96, 96);
    }

    private static List<BgraImage> DecodeWic(byte[] data, bool firstOnly = false)
    {
        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count > 200) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Too many image frames.");
        long pixels = 0;
        foreach (var frame in decoder.Frames)
        {
            pixels += (long)frame.PixelWidth * frame.PixelHeight;
            if (pixels > BgraImage.MaxPixels) throw new ShotwiseException(ErrorCode.ImageTooLarge, "Image frames exceed pixel budget.");
        }
        if (decoder is GifBitmapDecoder && decoder.Frames.Count > 0 &&
            (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight * decoder.Frames.Count > BgraImage.MaxPixels)
            throw new ShotwiseException(ErrorCode.ImageTooLarge, "Composited GIF exceeds pixel budget.");
        var res = new List<BgraImage>();
        // GIF frames are partial; composite them onto a full-size canvas.
        var isGif = decoder is GifBitmapDecoder;
        BgraImage? canvas = null;
        foreach (var frame in decoder.Frames)
        {
            var img = FromSource(frame);
            if (isGif)
            {
                canvas ??= new BgraImage(Math.Max(1, (int)(decoder.Frames[0].PixelWidth)), Math.Max(1, (int)decoder.Frames[0].PixelHeight));
                canvas.DrawOver(img, 0, 0);
                res.Add(canvas.Clone());
            }
            else res.Add(img);
            if (firstOnly) break;
        }
        return res;
    }

    private static BgraImage FromSource(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        var w = conv.PixelWidth;
        var h = conv.PixelHeight;
        var img = new BgraImage(w, h) { DpiX = conv.DpiX > 1 ? conv.DpiX : 96, DpiY = conv.DpiY > 1 ? conv.DpiY : 96 };
        conv.CopyPixels(img.Pixels, w * 4, 0);
        return img;
    }

    private static BitmapSource ToSource(BgraImage img, bool straightAlpha = false)
    {
        var bs = BitmapSource.Create(img.Width, img.Height, img.DpiX, img.DpiY, PixelFormats.Pbgra32, null, img.Pixels, img.Stride);
        BitmapSource res = straightAlpha ? new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0) : bs;
        res.Freeze();
        return res;
    }

    // ------------------------------------------------------------------ encode

    public byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        o ??= new EncodeOptions();
        switch (fmt)
        {
            case ImageFormat.Png: return EncodePng(Prepare(img, o, alphaCapable: true));
            case ImageFormat.Jpeg: return EncodeJpeg(Prepare(img, o, false), o);
            case ImageFormat.WebP: return EncodeWebp(Prepare(img, o, true), o);
            case ImageFormat.Bmp: return EncodeWic(new BmpBitmapEncoder(), new[] { Prepare(img, o, o.BitDepth == 32 && o.Transparency) }, o, o.BitDepth == 24 || !o.Transparency);
            case ImageFormat.Gif: return EncodeWic(new GifBitmapEncoder(), new[] { Prepare(img, o, true) }, o, false);
            case ImageFormat.Tiff: return EncodeWic(MakeTiff(o), new[] { Prepare(img, o, true) }, o, false);
            case ImageFormat.Ico: return EncodeIco(Prepare(img, o, true), o);
            case ImageFormat.Pdf:
            {
                var flat = new[] { Prepare(img, o, false) };
                return PdfExporter.Build(flat, o.Pdf ?? new PdfPageOptions(), SearchableLayer(flat, o));
            }
            default: throw new ShotwiseException(ErrorCode.UnsupportedFormat, $"Cannot encode {fmt}.");
        }
    }

    /// <summary>Builds an invisible text layer on page 1 from <see cref="EncodeOptions.PdfSearchableText"/> (one line per text line, spread over the page).</summary>
    private static IReadOnlyList<Shotwise.Core.Vision.OcrResult?>? SearchableLayer(IReadOnlyList<BgraImage> pages, EncodeOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.PdfSearchableText) || pages.Count == 0) return null;
        var lines = o.PdfSearchableText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var img = pages[0];
        var lh = (double)img.Height / lines.Length;
        var ocrLines = new List<Shotwise.Core.Vision.OcrLine>();
        for (var i = 0; i < lines.Length; i++)
        {
            var b = new Shotwise.Core.Geometry.Rect2(0, i * lh, img.Width, lh);
            ocrLines.Add(new Shotwise.Core.Vision.OcrLine(lines[i], b, new[] { new Shotwise.Core.Vision.OcrWord(lines[i], b, 1) }));
        }
        var res = new Shotwise.Core.Vision.OcrResult?[pages.Count];
        res[0] = new Shotwise.Core.Vision.OcrResult("", string.Join("\n", lines), ocrLines, 0);
        return res;
    }

    public void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null)
    {
        var bytes = Encode(img, fmt, o);
        WriteFile(path, bytes);
    }

    public void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ShotwiseException(ErrorCode.InvalidArgument, "No pages to save.");
        o ??= new EncodeOptions();
        byte[] bytes;
        switch (fmt)
        {
            case ImageFormat.Tiff:
                bytes = EncodeWic(MakeTiff(o), pages.Select(p => Prepare(p, o, true)).ToList(), o, false);
                break;
            case ImageFormat.Pdf:
            {
                var flat = pages.Select(p => Prepare(p, o, false)).ToList();
                bytes = PdfExporter.Build(flat, o.Pdf ?? new PdfPageOptions(), SearchableLayer(flat, o));
                break;
            }
            case ImageFormat.Gif:
                bytes = EncodeWic(new GifBitmapEncoder(), pages.Select(p => Prepare(p, o, true)).ToList(), o, false);
                break;
            default: throw new ShotwiseException(ErrorCode.UnsupportedFormat, $"{fmt} does not support multiple pages.");
        }
        WriteFile(path, bytes);
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllBytes(path, bytes);
        }
        catch (UnauthorizedAccessException e) { throw new ShotwiseException(ErrorCode.AccessDenied, $"Access denied: {path}", e); }
        catch (IOException e) { throw new ShotwiseException(ErrorCode.ExportFailed, $"Unable to write {path}: {e.Message}", e); }
    }

    /// <summary>Flattens onto the matte colour when the format/options have no alpha.</summary>
    private static BgraImage Prepare(BgraImage img, EncodeOptions o, bool alphaCapable)
        => !alphaCapable || !o.Transparency ? (img.IsOpaque() ? img : ImageOps.Flatten(img, o.MatteColor)) : img;

    private static byte[] EncodePng(BgraImage img)
    {
        using var bmp = SkiaInterop.ToBitmap(img);
        using var sk = SKImage.FromBitmap(bmp);
        using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
        return WithPngDpi(data.ToArray(), img.DpiX, img.DpiY);
    }

    /// <summary>Inserts a pHYs chunk after IHDR so DPI round-trips.</summary>
    private static byte[] WithPngDpi(byte[] png, double dpiX, double dpiY)
    {
        if (png.Length < 33 || Math.Abs(dpiX - 96) < 0.01 && Math.Abs(dpiY - 96) < 0.01) return png;
        var chunk = new byte[21];
        chunk[3] = 9;
        chunk[4] = (byte)'p'; chunk[5] = (byte)'H'; chunk[6] = (byte)'Y'; chunk[7] = (byte)'s';
        void Put(int off, uint v) { chunk[off] = (byte)(v >> 24); chunk[off + 1] = (byte)(v >> 16); chunk[off + 2] = (byte)(v >> 8); chunk[off + 3] = (byte)v; }
        Put(8, (uint)Math.Round(dpiX / 0.0254));
        Put(12, (uint)Math.Round(dpiY / 0.0254));
        chunk[16] = 1;
        var crc = Crc32(chunk.AsSpan(4, 13));
        Put(17, crc);
        var res = new byte[png.Length + chunk.Length];
        Buffer.BlockCopy(png, 0, res, 0, 33);
        Buffer.BlockCopy(chunk, 0, res, 33, chunk.Length);
        Buffer.BlockCopy(png, 33, res, 33 + chunk.Length, png.Length - 33);
        return res;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] EncodeJpeg(BgraImage img, EncodeOptions o)
    {
        using var bmp = SkiaInterop.ToBitmap(img);
        using var pm = bmp.PeekPixels();
        var ds = o.Subsampling switch
        {
            JpegSubsampling.S444 => SKJpegEncoderDownsample.Downsample444,
            JpegSubsampling.S422 => SKJpegEncoderDownsample.Downsample422,
            _ => SKJpegEncoderDownsample.Downsample420,
        };
        var opts = new SKJpegEncoderOptions(Math.Clamp(o.JpegQuality, 1, 100), ds, SKJpegEncoderAlphaOption.Ignore);
        using var data = pm.Encode(opts);
        if (data is null) throw new ShotwiseException(ErrorCode.ExportFailed, "JPEG encoding failed.");
        return data.ToArray();
    }

    private static byte[] EncodeWebp(BgraImage img, EncodeOptions o)
    {
        using var bmp = SkiaInterop.ToBitmap(img);
        using var pm = bmp.PeekPixels();
        var opts = new SKWebpEncoderOptions(o.WebPLossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, Math.Clamp(o.WebPQuality, 1, 100));
        using var data = pm.Encode(opts);
        if (data is null) throw new ShotwiseException(ErrorCode.ExportFailed, "WebP encoding failed.");
        return data.ToArray();
    }

    private static BitmapEncoder MakeTiff(EncodeOptions o)
    {
        var t = new TiffBitmapEncoder
        {
            Compression = (o.TiffCompression ?? "lzw").ToLowerInvariant() switch
            {
                "none" => TiffCompressOption.None,
                "zip" => TiffCompressOption.Zip,
                _ => TiffCompressOption.Lzw,
            },
        };
        return t;
    }

    private static byte[] EncodeWic(BitmapEncoder enc, IReadOnlyList<BgraImage> frames, EncodeOptions o, bool opaque24)
    {
        foreach (var f in frames)
        {
            BitmapSource src = ToSource(f, straightAlpha: !opaque24);
            if (opaque24)
            {
                src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
                src.Freeze();
            }
            enc.Frames.Add(BitmapFrame.Create(src));
        }
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeIco(BgraImage img, EncodeOptions o)
    {
        var sizes = o.IcoSizes.Count > 0
            ? o.IcoSizes.Where(s => s is >= 1 and <= 256).Distinct().OrderBy(s => s).ToList()
            : new List<int> { Math.Min(256, Math.Max(img.Width, img.Height)) };
        if (sizes.Count == 0) sizes.Add(256);
        var entries = new List<(int Size, byte[] Png)>();
        foreach (var s in sizes)
        {
            using var bmp = new SKBitmap(SkiaInterop.Info(s, s));
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                using var src = SkiaInterop.ToImage(img);
                var k = Math.Min((double)s / img.Width, (double)s / img.Height);
                var w = (float)(img.Width * k);
                var h = (float)(img.Height * k);
                using var p = new SKPaint { IsAntialias = true };
                c.DrawImage(src, new SKRect(0, 0, img.Width, img.Height), new SKRect((s - w) / 2, (s - h) / 2, (s + w) / 2, (s + h) / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
            }
            using var im = SKImage.FromBitmap(bmp);
            using var data = im.Encode(SKEncodedImageFormat.Png, 100);
            entries.Add((s, data.ToArray()));
        }
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)entries.Count);
        var offset = 6 + 16 * entries.Count;
        foreach (var (size, png) in entries)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(png.Length);
            bw.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in entries) bw.Write(png);
        bw.Flush();
        return ms.ToArray();
    }
}
