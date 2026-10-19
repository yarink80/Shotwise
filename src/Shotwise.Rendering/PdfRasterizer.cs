using System.Runtime.InteropServices.WindowsRuntime;
using Shotwise.Core.Imaging;
using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Shotwise.Rendering;

/// <summary>Rasterizes PDF pages with the built-in Windows.Data.Pdf engine (used for PDF stamps and PDF import).</summary>
internal static class PdfRasterizer
{
    public static bool LooksLikePdf(ReadOnlySpan<byte> data)
        => data.Length > 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-';

    /// <summary>Page size in 96-dpi units (DIPs) of the first page; null when the data is not a readable PDF.</summary>
    public static (double Width, double Height)? GetFirstPageSize(byte[] data)
    {
        try
        {
            return Run(async () =>
            {
                var doc = await LoadAsync(data).ConfigureAwait(false);
                if (doc.PageCount == 0) return ((double, double)?)null;
                using var page = doc.GetPage(0);
                return (page.Size.Width, page.Size.Height);
            });
        }
        catch { return null; }
    }

    public static int GetPageCount(byte[] data)
    {
        try { return Run(async () => (int)(await LoadAsync(data).ConfigureAwait(false)).PageCount); }
        catch { return 0; }
    }

    /// <summary>Renders one page (0-based) to a transparent-background bitmap with the given pixel width (height follows aspect).</summary>
    public static BgraImage? RenderPage(byte[] data, int pageIndex, int pixelWidth, bool whitePaper = false)
    {
        try
        {
            var png = Run(async () =>
            {
                var doc = await LoadAsync(data).ConfigureAwait(false);
                if (pageIndex < 0 || pageIndex >= doc.PageCount) return null;
                if (doc.PageCount > 200) return null;
                using var page = doc.GetPage((uint)pageIndex);
                var w = (uint)Math.Clamp(pixelWidth, 8, 16384);
                var h = (uint)Math.Clamp(Math.Round(w * page.Size.Height / Math.Max(1, page.Size.Width)), 8, 16384);
                using var stream = new InMemoryRandomAccessStream();
                var opts = new PdfPageRenderOptions { DestinationWidth = w, DestinationHeight = h, BackgroundColor = Windows.UI.Color.FromArgb(whitePaper ? (byte)255 : (byte)0, 255, 255, 255) };
                await page.RenderToStreamAsync(stream, opts).AsTask().ConfigureAwait(false);
                var bytes = new byte[stream.Size];
                stream.Seek(0);
                await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().ConfigureAwait(false);
                return bytes;
            });
            if (png is null) return null;
            using var bmp = SKBitmap.Decode(png);
            return bmp is null ? null : SkiaInterop.ToBgra(bmp);
        }
        catch { return null; }
    }

    /// <summary>Renders every page at the requested DPI.</summary>
    public static IReadOnlyList<BgraImage> RenderAll(byte[] data, double dpi = 150, int maxPages = 200)
    {
        var res = new List<BgraImage>();
        var pageCount = GetPageCount(data);
        if (pageCount > 200) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.ImageTooLarge, "PDF exceeds 200 pages.");
        var count = Math.Min(pageCount, maxPages);
        long pixels = 0;
        for (var i = 0; i < count; i++)
        {
            var size = GetPageSizeAt(data, i);
            var w = (int)Math.Round((size?.Width ?? 612) * dpi / 96.0);
            var rw = Math.Clamp(w, 8, 16384);
            var rh = Math.Clamp(Math.Round(rw * (size?.Height ?? 792) / Math.Max(1, size?.Width ?? 612)), 8, 16384);
            pixels += (long)(rw * rh);
            if (pixels > BgraImage.MaxPixels) throw new Shotwise.Core.ShotwiseException(Shotwise.Core.ErrorCode.ImageTooLarge, "PDF exceeds pixel budget.");
            var img = RenderPage(data, i, w, whitePaper: true);
            if (img is not null) res.Add(new BgraImage(img.Width, img.Height, img.Pixels) { DpiX = dpi, DpiY = dpi });
        }
        return res;
    }

    private static (double Width, double Height)? GetPageSizeAt(byte[] data, int index)
    {
        try
        {
            return Run(async () =>
            {
                var doc = await LoadAsync(data).ConfigureAwait(false);
                if (index >= doc.PageCount) return ((double, double)?)null;
                using var page = doc.GetPage((uint)index);
                return (page.Size.Width, page.Size.Height);
            });
        }
        catch { return null; }
    }

    private static async Task<PdfDocument> LoadAsync(byte[] data)
    {
        var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(data.AsBuffer()).AsTask().ConfigureAwait(false);
        ms.Seek(0);
        return await PdfDocument.LoadFromStreamAsync(ms).AsTask().ConfigureAwait(false);
    }

    private static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
}
