using System.Text;
using Shotwise.Core;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using Xunit;

namespace Shotwise.Rendering.Tests;

public class Fix4BTests
{
    [Fact]
    public void ExtensionMustMatchMagic()
    {
        var bytes = PngCodec.Encode(new BgraImage(1, 1));
        Assert.Throws<ShotwiseException>(() => SkiaImageCodec.ValidateExtension("image.tiff", bytes));
        SkiaImageCodec.ValidateExtension("image.png", bytes);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///secret'>]><svg>&x;</svg>")]
    [InlineData("<svg><image href='https://example.com/a.png'/></svg>")]
    [InlineData("<svg width='999999' height='999999'/>")]
    [InlineData("<svg><style>@import 'https://example.com/x';</style></svg>")]
    public void UnsafeSvgIsRejectedBeforeLoading(string xml) => Assert.ThrowsAny<Exception>(() => AssetRasterCache.ValidateSvg(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void AssetMediaTypeMustMatchContent()
    {
        var store = new AssetStore();
        var id = store.Add(PngCodec.Encode(new BgraImage(1, 1)), "image/jpeg");
        Assert.Null(new AssetRasterCache(null).Get(store, id));
    }

    [Fact]
    public void TiffFrameLimitAppliesBeforeDecoding()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tiff");
        try
        {
            var codec = new SkiaImageCodec();
            codec.SaveMultipage(Enumerable.Repeat(new BgraImage(1, 1), 201).ToArray(), path, ImageFormat.Tiff);
            Assert.Throws<ShotwiseException>(() => codec.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PdfPageLimitRejectsInsteadOfTruncating()
    {
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < 201; i++) pdf.AddPage();
        using var stream = new MemoryStream();
        pdf.Save(stream, false);
        Assert.Throws<ShotwiseException>(() => PdfRasterizer.RenderAll(stream.ToArray()));
    }
}
