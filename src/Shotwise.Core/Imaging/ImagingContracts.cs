using Shotwise.Core.Geometry;
using Shotwise.Core.Vision;

namespace Shotwise.Core.Imaging;

public enum ImageFormat { Png, Jpeg, Bmp, Gif, Tiff, WebP, Ico, Pdf }

public enum JpegSubsampling { S444, S422, S420 }

public enum PaperSize { Letter, Legal, A3, A4, A5, Tabloid, FitToImage, Custom }
public enum PageOrientation { Portrait, Landscape, Auto }
public enum PageImageLayout { FitToPage, FillPage, FitWidth, ActualSize, CustomScale }

/// <summary>PDF page setup for export/print-to-PDF.</summary>
public sealed record PdfPageOptions(
    PaperSize Paper = PaperSize.FitToImage,
    PageOrientation Orientation = PageOrientation.Auto,
    PageImageLayout Layout = PageImageLayout.FitToPage,
    Anchor9 Position = Anchor9.Center,
    Thickness2 MarginsPoints = default,
    double CustomWidthPoints = 612,
    double CustomHeightPoints = 792,
    double ScalePercent = 100,
    string? Title = null,
    string? Author = null,
    string? Subject = null,
    string? Keywords = null);

/// <summary>Encoder options shared by all formats; irrelevant fields are ignored by each encoder.</summary>
public sealed record EncodeOptions(
    int JpegQuality = 90,
    JpegSubsampling Subsampling = JpegSubsampling.S420,
    int BitDepth = 32,
    bool Transparency = true,
    PdfPageOptions? Pdf = null,
    string? PdfSearchableText = null)
{
    /// <summary>Background used when <see cref="Transparency"/> is false or the format has no alpha (JPEG/BMP 24).</summary>
    public Argb MatteColor { get; init; } = Argb.White;
    /// <summary>WebP quality 1..100; 100 with <see cref="WebPLossless"/> = true is lossless.</summary>
    public int WebPQuality { get; init; } = 90;
    public bool WebPLossless { get; init; }
    /// <summary>ICO: sizes to embed (e.g. 16, 32, 48, 256). Empty = derive from image.</summary>
    public IReadOnlyList<int> IcoSizes { get; init; } = Array.Empty<int>();
    /// <summary>TIFF compression: "lzw", "zip", "none".</summary>
    public string TiffCompression { get; init; } = "lzw";
}

/// <summary>Describes one supported file format (used by Save As format pickers).</summary>
public sealed record ImageFormatInfo(
    ImageFormat Format,
    string Extension,
    string DisplayName,
    bool CanRead,
    bool CanWrite,
    bool SupportsTransparency,
    bool SupportsMultiplePages,
    IReadOnlyList<string> OptionKeys);

/// <summary>Decoded animation (GIF) — frames are full composited canvases.</summary>
public sealed record AnimatedImage(int Width, int Height, IReadOnlyList<BgraImage> Frames, IReadOnlyList<TimeSpan> Delays, int LoopCount = 0)
{
    public TimeSpan Duration => Delays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
}

/// <summary>Image decode/encode service (implemented in Rendering with Skia + WIC + PDFsharp).</summary>
public interface IImageCodec
{
    BgraImage Decode(ReadOnlySpan<byte> data);
    BgraImage Load(string path);
    /// <summary>All pages/frames of multi-image files (TIFF, PDF, ICO); single-item list for others.</summary>
    IReadOnlyList<BgraImage> LoadAllPages(string path);
    byte[] Encode(BgraImage img, ImageFormat fmt, EncodeOptions? o = null);
    void Save(BgraImage img, string path, ImageFormat fmt, EncodeOptions? o = null);
    /// <summary>Multi-page output: <see cref="ImageFormat.Tiff"/> or <see cref="ImageFormat.Pdf"/>.</summary>
    void SaveMultipage(IReadOnlyList<BgraImage> pages, string path, ImageFormat fmt, EncodeOptions? o = null);
    IReadOnlyList<ImageFormatInfo> SupportedFormats { get; }
}

/// <summary>PDF writer with optional invisible OCR text layers (implemented in Rendering).</summary>
public interface IPdfExporter
{
    Task ExportAsync(IReadOnlyList<BgraImage> pages, string path, PdfPageOptions o, IReadOnlyList<OcrResult?>? textLayers, CancellationToken ct);
}

public static class ImageFormatExtensions
{
    public static string DefaultExtension(this ImageFormat f) => f switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Tiff => ".tif",
        ImageFormat.WebP => ".webp",
        ImageFormat.Ico => ".ico",
        ImageFormat.Pdf => ".pdf",
        _ => ".bin",
    };

    public static string MediaType(this ImageFormat f) => f switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Tiff => "image/tiff",
        ImageFormat.WebP => "image/webp",
        ImageFormat.Ico => "image/x-icon",
        ImageFormat.Pdf => "application/pdf",
        _ => "application/octet-stream",
    };

    /// <summary>Maps a file extension (with or without dot, any case) to a format.</summary>
    public static bool TryFromExtension(string? ext, out ImageFormat format)
    {
        format = ImageFormat.Png;
        if (string.IsNullOrEmpty(ext)) return false;
        switch (ext.TrimStart('.').ToLowerInvariant())
        {
            case "png": format = ImageFormat.Png; return true;
            case "jpg" or "jpeg" or "jpe" or "jfif": format = ImageFormat.Jpeg; return true;
            case "bmp" or "dib": format = ImageFormat.Bmp; return true;
            case "gif": format = ImageFormat.Gif; return true;
            case "tif" or "tiff": format = ImageFormat.Tiff; return true;
            case "webp": format = ImageFormat.WebP; return true;
            case "ico": format = ImageFormat.Ico; return true;
            case "pdf": format = ImageFormat.Pdf; return true;
            default: return false;
        }
    }
}
