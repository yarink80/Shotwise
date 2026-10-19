using System.IO;
using System.Runtime.CompilerServices;
using Shotwise.Core.Documents;
using Shotwise.Core.Imaging;
using SkiaSharp;
using Svg.Skia;
using System.Xml;
using System.Xml.Linq;
using Shotwise.Core;

namespace Shotwise.Rendering;

/// <summary>A drawable resolved from an asset: raster image or vector picture, with its natural size.</summary>
internal sealed class AssetDrawable
{
    public SKImage? Image;
    public SKPicture? Picture;
    public object? Owner;
    public float Width;
    public float Height;
    public bool IsVector => Picture is not null;
}

/// <summary>Per-asset-store cache of decoded rasters, parsed SVG pictures and rasterized PDF pages.</summary>
internal sealed class AssetRasterCache
{
    private sealed class StoreCache
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, WeakReference<AssetDrawable>> Items = new();
    }

    private readonly ConditionalWeakTable<AssetStore, StoreCache> _caches = new();
    private readonly IImageCodec? _codec;

    public AssetRasterCache(IImageCodec? codec) => _codec = codec;

    /// <summary>
    /// Resolves an asset to a drawable. <paramref name="targetPixelWidth"/> is only used for PDF assets (rendered at that width, bucketed).
    /// Returns null when the asset is missing or cannot be decoded.
    /// </summary>
    public AssetDrawable? Get(AssetStore store, AssetId id, int targetPixelWidth = 0)
    {
        if (id.IsEmpty) return null;
        var cache = _caches.GetOrCreateValue(store);
        if (!store.TryGet(id, out var data, out var mediaType)) return null;
        var isPdf = mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase) || PdfRasterizer.LooksLikePdf(data);
        var bucket = isPdf ? Bucket(targetPixelWidth) : 0;
        var key = id.Value + "|" + bucket;
        lock (cache.Gate)
        {
            if (cache.Items.TryGetValue(key, out var hit) && hit.TryGetTarget(out var drawable)) return drawable;
        }
        var made = Create(data, mediaType, isPdf, bucket);
        lock (cache.Gate)
        {
            if (cache.Items.Count >= 32) cache.Items.Clear();
            if (made is not null) cache.Items[key] = new WeakReference<AssetDrawable>(made);
        }
        return made;
    }

    private static int Bucket(int w) => w <= 0 ? 512 : Math.Clamp((int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(64, w)))), 64, 4096);

    private AssetDrawable? Create(byte[] data, string mediaType, bool isPdf, int pdfWidth)
    {
        try
        {
            if (mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase) || LooksLikeSvg(data))
            {
                if (!mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) || !LooksLikeSvg(data)) return null;
                ValidateSvg(data);
                Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
                Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
                Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;
                Svg.SvgDocument.DisableDtdProcessing = true;
                var svg = new SKSvg(); // kept alive by the drawable: disposing it would dispose the picture
                using var ms = new MemoryStream(data);
                var pic = svg.Load(ms);
                if (pic is null) return null;
                var cull = pic.CullRect;
                if (!float.IsFinite(cull.Width) || !float.IsFinite(cull.Height) || cull.Width <= 0 || cull.Height <= 0 || cull.Width > 16384 || cull.Height > 16384 || (double)cull.Width * cull.Height > BgraImage.MaxPixels)
                { svg.Dispose(); return null; }
                return new AssetDrawable { Picture = pic, Owner = svg, Width = cull.Width, Height = cull.Height };
            }
            if (isPdf)
            {
                if (!mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) || !PdfRasterizer.LooksLikePdf(data)) return null;
                var img = PdfRasterizer.RenderPage(data, 0, pdfWidth);
                if (img is null) return null;
                return FromBgra(img);
            }
            var extension = AssetStore.ExtensionForMediaType(mediaType);
            SkiaImageCodec.ValidateExtension("asset." + extension, data);
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > BgraImage.MaxPixels || codec.FrameCount > 200) return null;
            using var bmp = SKBitmap.Decode(codec);
            if (bmp is not null)
            {
                var im = SKImage.FromBitmap(bmp);
                return new AssetDrawable { Image = im, Width = im.Width, Height = im.Height };
            }
            if (_codec is not null)
                return FromBgra(_codec.Decode(data));
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static AssetDrawable FromBgra(BgraImage img)
    {
        var im = SkiaInterop.ToImage(img);
        return new AssetDrawable { Image = im, Width = im.Width, Height = im.Height };
    }

    private static bool LooksLikeSvg(byte[] data)
    {
        var n = Math.Min(data.Length, 512);
        var head = System.Text.Encoding.UTF8.GetString(data, 0, n);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateSvg(byte[] data)
    {
        if (data.Length > 4 * 1024 * 1024) throw new ShotwiseException(ErrorCode.FileTooLarge, "SVG exceeds size limit.");
        using var input = new MemoryStream(data);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty SVG.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Invalid SVG.");
        foreach (var name in new[] { "width", "height" })
        {
            if (root.Attribute(name) is not { } dimension) continue;
            var match = System.Text.RegularExpressions.Regex.Match(dimension.Value, @"^\s*([0-9]+(?:\.[0-9]+)?)\s*(px)?\s*$");
            if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0 || number > 16384)
                throw new InvalidDataException("SVG dimensions exceed supported limits.");
        }
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "style" or "script" or "foreignObject" or "image") throw new InvalidDataException("External SVG content is disabled.");
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if ((attribute.Name.LocalName is "href" or "src" or "base") && !value.StartsWith('#')) throw new InvalidDataException("External SVG reference.");
                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^url\(\s*#[a-zA-Z0-9_-]+\s*\)$")) throw new InvalidDataException("External SVG URL.");
            }
        }
    }
}
