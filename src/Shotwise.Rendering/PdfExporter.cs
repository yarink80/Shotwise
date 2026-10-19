using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Serialization;
using Shotwise.Core.Vision;

namespace Shotwise.Rendering;

/// <summary>
/// Writes images to PDF with PDFsharp (MIT). Supports paper sizes, orientation, image layout/position, margins, document info,
/// pagination of tall images and an invisible (fully transparent) OCR text layer so the PDF is searchable.
/// </summary>
public sealed class PdfExporter : IPdfExporter
{
    private static readonly object InitLock = new();
    private static bool _fontsInitialised;
    private readonly ILogger<PdfExporter> _log;

    public PdfExporter(ILogger<PdfExporter>? log = null) => _log = log ?? NullLogger<PdfExporter>.Instance;

    public Task ExportAsync(IReadOnlyList<BgraImage> pages, string path, PdfPageOptions o, IReadOnlyList<OcrResult?>? textLayers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var bytes = Build(pages, o ?? new PdfPageOptions(), textLayers, ct);
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllBytes(path, bytes);
            _log.LogDebug("Exported {Count} page(s) to PDF {Path}", pages.Count, path);
        }, ct);
    }

    /// <summary>Builds the PDF in memory.</summary>
    public static byte[] Build(IReadOnlyList<BgraImage> images, PdfPageOptions o, IReadOnlyList<OcrResult?>? textLayers = null, CancellationToken ct = default)
    {
        if (images.Count == 0) throw new ShotwiseException(ErrorCode.InvalidArgument, "No pages to export.");
        EnsureFonts();
        using var doc = new PdfDocument();
        doc.Info.Creator = "Shotwise";
        if (!string.IsNullOrWhiteSpace(o.Title)) doc.Info.Title = o.Title;
        if (!string.IsNullOrWhiteSpace(o.Author)) doc.Info.Author = o.Author;
        if (!string.IsNullOrWhiteSpace(o.Subject)) doc.Info.Subject = o.Subject;
        if (!string.IsNullOrWhiteSpace(o.Keywords)) doc.Info.Keywords = o.Keywords;

        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var img = images[i];
            var ocr = textLayers is not null && i < textLayers.Count ? textLayers[i] : null;
            AddImagePages(doc, img, o, ocr);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static void EnsureFonts()
    {
        lock (InitLock)
        {
            if (_fontsInitialised) return;
            try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch { /* already locked by earlier use */ }
            _fontsInitialised = true;
        }
    }

    internal static (double W, double H) PaperPoints(PdfPageOptions o, double imgWpt, double imgHpt)
    {
        return o.Paper switch
        {
            PaperSize.Letter => (612, 792),
            PaperSize.Legal => (612, 1008),
            PaperSize.A3 => (841.89, 1190.55),
            PaperSize.A4 => (595.28, 841.89),
            PaperSize.A5 => (419.53, 595.28),
            PaperSize.Tabloid => (792, 1224),
            PaperSize.Custom => (Math.Max(36, o.CustomWidthPoints), Math.Max(36, o.CustomHeightPoints)),
            _ => (imgWpt + o.MarginsPoints.Horizontal, imgHpt + o.MarginsPoints.Vertical), // FitToImage
        };
    }

    private static void AddImagePages(PdfDocument doc, BgraImage img, PdfPageOptions o, OcrResult? ocr)
    {
        var dpiX = img.DpiX > 1 ? img.DpiX : 96;
        var dpiY = img.DpiY > 1 ? img.DpiY : 96;
        var wPt = img.Width * 72.0 / dpiX;
        var hPt = img.Height * 72.0 / dpiY;
        var m = o.MarginsPoints;

        var (pw, ph) = PaperPoints(o, wPt, hPt);
        if (o.Paper != PaperSize.FitToImage)
        {
            var landscape = o.Orientation switch
            {
                PageOrientation.Landscape => true,
                PageOrientation.Portrait => false,
                _ => img.Width > img.Height,
            };
            if (landscape != pw > ph) (pw, ph) = (ph, pw);
        }
        var availW = Math.Max(1, pw - m.Horizontal);
        var availH = Math.Max(1, ph - m.Vertical);

        // scale in "points per image point"
        double scale = o.Layout switch
        {
            PageImageLayout.ActualSize => 1,
            PageImageLayout.CustomScale => Math.Max(1, o.ScalePercent) / 100.0,
            PageImageLayout.FillPage => Math.Max(availW / wPt, availH / hPt),
            PageImageLayout.FitWidth => availW / wPt,
            _ => Math.Min(availW / wPt, availH / hPt),
        };
        if (o.Paper == PaperSize.FitToImage && o.Layout is PageImageLayout.FitToPage or PageImageLayout.FillPage or PageImageLayout.FitWidth) scale = 1;
        var drawW = wPt * scale;
        var drawH = hPt * scale;

        var pageCount = drawH > availH + 0.5 && o.Layout is PageImageLayout.FitWidth or PageImageLayout.ActualSize or PageImageLayout.CustomScale
            ? (int)Math.Ceiling((drawH - 0.5) / availH)
            : 1;
        pageCount = Math.Min(pageCount, 200);

        using var xi = LoadXImage(img);
        for (var pi = 0; pi < pageCount; pi++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(pw);
            page.Height = XUnit.FromPoint(ph);
            using var gfx = XGraphics.FromPdfPage(page);
            var content = new XRect(m.Left, m.Top, availW, availH);
            gfx.IntersectClip(content);
            var pos = o.Position;
            var x = m.Left + (availW - drawW) * pos.HorizontalFactor();
            double y;
            if (pageCount > 1) y = m.Top - pi * availH;
            else y = m.Top + (availH - drawH) * pos.VerticalFactor();
            gfx.DrawImage(xi, x, y, drawW, drawH);

            if (ocr is { Lines.Count: > 0 })
                DrawInvisibleText(gfx, ocr, x, y, drawW / img.Width, drawH / img.Height, content);
        }
    }

    private static XImage LoadXImage(BgraImage img)
    {
        return XImage.FromStream(new MemoryStream(EncodePng(img)));
    }

    private static byte[] EncodePng(BgraImage img)
    {
        using var bmp = SkiaInterop.ToBitmap(img);
        using var sk = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = sk.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawInvisibleText(XGraphics gfx, OcrResult ocr, double ox, double oy, double sx, double sy, XRect clip)
    {
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var line in ocr.Lines)
        {
            var words = line.Words.Count > 0 ? line.Words : new[] { new OcrWord(line.Text, line.Bounds, 1) };
            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text)) continue;
                var bx = ox + w.Bounds.X * sx;
                var by = oy + w.Bounds.Y * sy;
                var bw = w.Bounds.Width * sx;
                var bh = w.Bounds.Height * sy;
                if (bh < 1 || bw < 1) continue;
                if (bx + bw < clip.Left || bx > clip.Right || by + bh < clip.Top || by > clip.Bottom) continue;
                try
                {
                    var font = new XFont("Arial", Math.Max(2, bh * 0.85), XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
                    var measured = gfx.MeasureString(w.Text, font).Width;
                    var state = gfx.Save();
                    gfx.TranslateTransform(bx, by);
                    if (measured > 0.1) gfx.ScaleTransform(Math.Clamp(bw / measured, 0.2, 5), 1);
                    gfx.DrawString(w.Text, font, invisible, new XPoint(0, bh * 0.82), XStringFormats.Default);
                    gfx.Restore(state);
                }
                catch (Exception)
                {
                    // fonts unavailable: skip the text layer for this word rather than failing the export
                }
            }
        }
    }
}
