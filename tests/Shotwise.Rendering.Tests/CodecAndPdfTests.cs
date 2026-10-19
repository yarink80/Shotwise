using PdfSharp.Pdf.IO;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class CodecTests
{
    private static BgraImage Sample(bool alpha = false)
    {
        var img = T.Gradient(48, 32);
        if (alpha)
        {
            for (var y = 0; y < 8; y++) for (var x = 0; x < 48; x++) img.SetPixel(x, y, Argb.FromArgb(100, 200, 50, 20));
        }
        return img;
    }

    [Fact]
    public void Png_RoundTrip_IsLossless_IncludingAlpha_AndDpi()
    {
        var src = Sample(alpha: true);
        var withDpi = new BgraImage(src.Width, src.Height, src.Pixels) { DpiX = 144, DpiY = 144 };
        var bytes = T.Codec.Encode(withDpi, ImageFormat.Png);
        Assert.True(PngCodec.IsPng(bytes));
        var back = T.Codec.Decode(bytes);
        Assert.Equal(src.Pixels, back.Pixels);
        Assert.Equal(144, back.DpiX, 0.6);
    }

    [Fact]
    public void Png_PreventTransparency_FlattensOnMatte()
    {
        var src = new BgraImage(4, 4);
        var bytes = T.Codec.Encode(src, ImageFormat.Png, new EncodeOptions(Transparency: false) { MatteColor = Argb.FromRgb(10, 20, 30) });
        var back = T.Codec.Decode(bytes);
        T.AssertColor(back, 1, 1, Argb.FromRgb(10, 20, 30), 0);
    }

    [Fact]
    public void Jpeg_QualityAffectsSize_AndRoundTripsApproximately()
    {
        var src = T.Gradient(96, 64);
        var hi = T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 95));
        var lo = T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 10));
        Assert.True(hi.Length > lo.Length);
        var back = T.Codec.Decode(hi);
        Assert.True(T.MeanDiff(src, back) < 3, T.MeanDiff(src, back).ToString());
        Assert.Equal(0xFF, hi[0]);
        Assert.Equal(0xD8, hi[1]);
    }

    [Fact]
    public void Jpeg_Subsampling444_IsSharperThan420OnColourEdges()
    {
        var src = T.Checker(64, 64, 1, Argb.Red, Argb.Blue);
        var s444 = T.Codec.Decode(T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 95, Subsampling: JpegSubsampling.S444)));
        var s420 = T.Codec.Decode(T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 95, Subsampling: JpegSubsampling.S420)));
        Assert.True(T.MeanDiff(src, s444) < T.MeanDiff(src, s420));
    }

    [Fact]
    public void Jpeg_TransparentSource_UsesMatte()
    {
        var src = new BgraImage(8, 8);
        var back = T.Codec.Decode(T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 100) { MatteColor = Argb.FromRgb(255, 0, 0) }));
        T.AssertColor(back, 4, 4, Argb.FromRgb(255, 0, 0), 8);
    }

    [Fact]
    public void WebP_Lossless_RoundTripsExactly_Lossy_IsSmaller()
    {
        var src = Sample(alpha: true);
        var lossless = T.Codec.Encode(src, ImageFormat.WebP, new EncodeOptions { WebPLossless = true, WebPQuality = 100 });
        var back = T.Codec.Decode(lossless);
        Assert.True(T.MeanDiff(src, back) < 0.5);
        var photo = T.Gradient(128, 128);
        var lossy = T.Codec.Encode(photo, ImageFormat.WebP, new EncodeOptions { WebPQuality = 40 });
        var lossy2 = T.Codec.Encode(photo, ImageFormat.WebP, new EncodeOptions { WebPLossless = true });
        Assert.True(lossy.Length < lossy2.Length);
    }

    [Fact]
    public void Bmp_RoundTrip_24And32()
    {
        var src = Sample();
        var b24 = T.Codec.Encode(src, ImageFormat.Bmp, new EncodeOptions(BitDepth: 24));
        Assert.True(T.MeanDiff(src, T.Codec.Decode(b24)) < 0.5);
        var b32 = T.Codec.Encode(Sample(true), ImageFormat.Bmp, new EncodeOptions(BitDepth: 32));
        Assert.True(b32.Length > b24.Length);
        Assert.Equal((byte)'B', b32[0]);
    }

    [Fact]
    public void Gif_Encodes_AndDecodes()
    {
        var src = T.Checker(32, 32, 8, Argb.Red, Argb.Blue);
        var bytes = T.Codec.Encode(src, ImageFormat.Gif);
        Assert.Equal((byte)'G', bytes[0]);
        var back = T.Codec.Decode(bytes);
        Assert.Equal((32, 32), (back.Width, back.Height));
        Assert.True(T.MeanDiff(src, back) < 12);
    }

    [Fact]
    public void Tiff_MultiPage_RoundTrip()
    {
        var pages = new[] { BgraImage.CreateFilled(30, 20, Argb.Red), BgraImage.CreateFilled(25, 25, Argb.Green), BgraImage.CreateFilled(10, 40, Argb.Blue) };
        var path = T.TempFile(".tif");
        try
        {
            T.Codec.SaveMultipage(pages, path, ImageFormat.Tiff);
            var back = T.Codec.LoadAllPages(path);
            Assert.Equal(3, back.Count);
            Assert.Equal((25, 25), (back[1].Width, back[1].Height));
            T.AssertColor(back[2], 5, 5, Argb.Blue, 0);
            // single-image Load returns the first page
            Assert.Equal(30, T.Codec.Load(path).Width);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("lzw")]
    [InlineData("zip")]
    [InlineData("none")]
    public void Tiff_Compression_Options_AllDecode(string compression)
    {
        var src = Sample(true);
        var bytes = T.Codec.Encode(src, ImageFormat.Tiff, new EncodeOptions { TiffCompression = compression });
        var back = T.Codec.Decode(bytes);
        Assert.Equal(src.Pixels.Length, back.Pixels.Length);
        Assert.True(T.MeanDiff(src, back) < 1.5);
    }

    [Fact]
    public void Ico_ContainsRequestedSizes_AndLoadsAllFrames()
    {
        var src = T.Gradient(64, 64);
        var path = T.TempFile(".ico");
        try
        {
            T.Codec.Save(src, path, ImageFormat.Ico, new EncodeOptions { IcoSizes = new[] { 16, 32, 48 } });
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(3, bytes[4]);
            var frames = T.Codec.LoadAllPages(path);
            Assert.Equal(3, frames.Count);
            Assert.Contains(frames, f => f.Width == 32);
            var one = T.Codec.Load(path);
            Assert.True(one.Width >= 16);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Decode_GarbageThrowsShotwiseException()
    {
        var ex = Assert.Throws<ShotwiseException>(() => T.Codec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Equal(ErrorCode.UnsupportedFormat, ex.Code);
        Assert.Throws<ShotwiseException>(() => T.Codec.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Load_MissingFile_MapsToFileNotFound()
    {
        var ex = Assert.Throws<ShotwiseException>(() => T.Codec.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".png")));
        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void Save_CreatesDirectory_AndWritesAtomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shotwise-render-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "a.png");
        try
        {
            T.Codec.Save(Sample(), path, ImageFormat.Png);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SupportedFormats_CoverEveryFormat()
    {
        foreach (var f in Enum.GetValues<ImageFormat>())
            Assert.Contains(T.Codec.SupportedFormats, i => i.Format == f && i.CanWrite && i.CanRead);
        Assert.True(T.Codec.SupportedFormats.First(i => i.Format == ImageFormat.Tiff).SupportsMultiplePages);
    }

    [Fact]
    public void Core_AssetStore_CanUseThisCodec()
    {
        var doc = Shotwise.Core.Documents.ShotDocument.FromImage(Sample(), null, T.Codec);
        var id = doc.Assets.Add(T.Codec.Encode(Sample(), ImageFormat.Jpeg), "image/jpeg");
        var img = doc.Assets.GetImage(id);
        Assert.Equal(48, img.Width);
    }

    [Fact]
    public void ExifOrientation_IsApplied_OnJpegDecode()
    {
        // 2x1 red/blue jpeg with an EXIF orientation 6 (rotate 90 CW) APP1 segment injected after SOI.
        var src = BgraImage.CreateFilled(16, 8, Argb.Red);
        var jpg = T.Codec.Encode(src, ImageFormat.Jpeg, new EncodeOptions(JpegQuality: 95));
        byte[] exif =
        {
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        var withExif = new byte[jpg.Length + exif.Length];
        withExif[0] = jpg[0]; withExif[1] = jpg[1];
        Buffer.BlockCopy(exif, 0, withExif, 2, exif.Length);
        Buffer.BlockCopy(jpg, 2, withExif, 2 + exif.Length, jpg.Length - 2);
        var back = T.Codec.Decode(withExif);
        Assert.Equal((8, 16), (back.Width, back.Height));
    }
}

public class PdfTests
{
    private static (int Count, List<(double W, double H)> Sizes, string? Title) Inspect(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        return (doc.PageCount, doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => (p.Width.Point, p.Height.Point)).ToList(), doc.Info.Title);
    }

    [Fact]
    public async Task Export_FitToImage_PageMatchesImageSizeInPoints_AndMetadata()
    {
        var path = T.TempFile(".pdf");
        try
        {
            var img = BgraImage.CreateFilled(400, 200, Argb.Red); // 96 dpi => 300 x 150 pt
            await new PdfExporter().ExportAsync(new[] { img }, path, new PdfPageOptions(Title: "Hello", Author: "Me"), null, CancellationToken.None);
            var info = Inspect(path);
            Assert.Equal(1, info.Count);
            Assert.Equal(300, info.Sizes[0].W, 1);
            Assert.Equal(150, info.Sizes[0].H, 1);
            Assert.Equal("Hello", info.Title);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(PaperSize.A4, PageOrientation.Portrait, 595.28, 841.89)]
    [InlineData(PaperSize.A4, PageOrientation.Landscape, 841.89, 595.28)]
    [InlineData(PaperSize.Letter, PageOrientation.Portrait, 612, 792)]
    [InlineData(PaperSize.Legal, PageOrientation.Portrait, 612, 1008)]
    [InlineData(PaperSize.A5, PageOrientation.Portrait, 419.53, 595.28)]
    [InlineData(PaperSize.Tabloid, PageOrientation.Portrait, 792, 1224)]
    public void PaperSizes_AndOrientation(PaperSize paper, PageOrientation orient, double w, double h)
    {
        var bytes = PdfExporter.Build(new[] { BgraImage.CreateFilled(50, 50, Argb.Blue) }, new PdfPageOptions(paper, orient));
        var path = T.TempFile(".pdf");
        try
        {
            File.WriteAllBytes(path, bytes);
            var s = Inspect(path).Sizes[0];
            Assert.Equal(w, s.W, 1);
            Assert.Equal(h, s.H, 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AutoOrientation_FollowsImageAspect()
    {
        var path = T.TempFile(".pdf");
        try
        {
            File.WriteAllBytes(path, PdfExporter.Build(new[] { BgraImage.CreateFilled(200, 50, Argb.Blue) }, new PdfPageOptions(PaperSize.A4, PageOrientation.Auto)));
            var s = Inspect(path).Sizes[0];
            Assert.True(s.W > s.H);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CustomPaper_UsesCustomPoints()
    {
        var path = T.TempFile(".pdf");
        try
        {
            File.WriteAllBytes(path, PdfExporter.Build(new[] { BgraImage.CreateFilled(10, 10, Argb.Blue) }, new PdfPageOptions(PaperSize.Custom, PageOrientation.Portrait, CustomWidthPoints: 300, CustomHeightPoints: 500)));
            var s = Inspect(path).Sizes[0];
            Assert.Equal((300, 500), (Math.Round(s.W), Math.Round(s.H)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MultiplePages_OnePerImage()
    {
        var path = T.TempFile(".pdf");
        try
        {
            T.Codec.SaveMultipage(new[] { BgraImage.CreateFilled(20, 20, Argb.Red), BgraImage.CreateFilled(30, 30, Argb.Green), BgraImage.CreateFilled(40, 40, Argb.Blue) }, path, ImageFormat.Pdf);
            Assert.Equal(3, Inspect(path).Count);
            var pages = T.Codec.LoadAllPages(path);
            Assert.Equal(3, pages.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FitWidth_TallImage_PaginatesAcrossPages()
    {
        var path = T.TempFile(".pdf");
        try
        {
            var tall = BgraImage.CreateFilled(600, 3000, Argb.Red);
            File.WriteAllBytes(path, PdfExporter.Build(new[] { tall }, new PdfPageOptions(PaperSize.A4, PageOrientation.Portrait, PageImageLayout.FitWidth)));
            Assert.True(Inspect(path).Count >= 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Margins_AndPosition_AffectPlacement_ViaRasterization()
    {
        var bytes = PdfExporter.Build(new[] { BgraImage.CreateFilled(100, 100, Argb.FromRgb(0, 0, 0)) },
            new PdfPageOptions(PaperSize.Letter, PageOrientation.Portrait, PageImageLayout.ActualSize, Anchor9.TopLeft, new Thickness2(72)));
        var path = T.TempFile(".pdf");
        try
        {
            File.WriteAllBytes(path, bytes);
            var page = T.Codec.LoadAllPages(path)[0]; // 150 dpi => 72pt margin = 150px
            // black square (75pt) starts at the margin
            Assert.True(page.GetPixel(page.Width / 8 + 10, page.Height / 11 + 10).R < 40 || page.GetPixel(170, 170).R < 40);
            Assert.True(page.GetPixel(20, 20).R > 240);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OcrTextLayer_IsInvisible_ButChangesTheFile()
    {
        var img = T.Gradient(300, 200);
        var ocr = new OcrResult("en", "hello world", new[]
        {
            new OcrLine("hello world", new Rect2(20, 20, 150, 30), new[]
            {
                new OcrWord("hello", new Rect2(20, 20, 70, 30), 0.9),
                new OcrWord("world", new Rect2(100, 20, 70, 30), 0.9),
            }),
        }, 0);
        var plain = PdfExporter.Build(new[] { img }, new PdfPageOptions());
        var searchable = PdfExporter.Build(new[] { img }, new PdfPageOptions(), new OcrResult?[] { ocr });
        Assert.True(searchable.Length > plain.Length);

        var p1 = T.TempFile(".pdf");
        var p2 = T.TempFile(".pdf");
        try
        {
            File.WriteAllBytes(p1, plain);
            File.WriteAllBytes(p2, searchable);
            var a = T.Codec.LoadAllPages(p1)[0];
            var b = T.Codec.LoadAllPages(p2)[0];
            Assert.True(T.MeanDiff(a, b) < 0.3, "text layer must be invisible: " + T.MeanDiff(a, b));
        }
        finally { File.Delete(p1); File.Delete(p2); }
    }

    [Fact]
    public void SearchableText_ViaEncodeOptions()
    {
        var img = T.Gradient(100, 100);
        var bytes = T.Codec.Encode(img, ImageFormat.Pdf, new EncodeOptions(PdfSearchableText: "some searchable text\nsecond line"));
        var plain = T.Codec.Encode(img, ImageFormat.Pdf);
        Assert.True(bytes.Length > plain.Length);
        Assert.Equal((byte)'%', bytes[0]);
    }

    [Fact]
    public async Task Export_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfExporter().ExportAsync(new[] { BgraImage.CreateFilled(10, 10, Argb.Red) }, T.TempFile(".pdf"), new PdfPageOptions(), null, cts.Token));
    }

    [Fact]
    public void Export_NoPages_Throws()
    {
        Assert.Throws<ShotwiseException>(() => PdfExporter.Build(Array.Empty<BgraImage>(), new PdfPageOptions()));
    }

    [Fact]
    public void AlphaImage_EmbedsInPdf()
    {
        var img = new BgraImage(50, 50);
        for (var y = 10; y < 40; y++) for (var x = 10; x < 40; x++) img.SetPixel(x, y, Argb.FromArgb(200, 255, 0, 0));
        var bytes = PdfExporter.Build(new[] { img }, new PdfPageOptions());
        Assert.True(bytes.Length > 300);
    }

    [Fact]
    public void DependencyInjection_RegistersAllFourServices()
    {
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ShotwiseRenderingServiceCollectionExtensions.AddShotwiseRendering(sc);
        var types = sc.Select(d => d.ServiceType).ToHashSet();
        Assert.Contains(typeof(Shotwise.Core.Documents.IDocumentRenderer), types);
        Assert.Contains(typeof(Shotwise.Core.Effects.IEffectProcessor), types);
        Assert.Contains(typeof(IImageCodec), types);
        Assert.Contains(typeof(IPdfExporter), types);
    }
}
