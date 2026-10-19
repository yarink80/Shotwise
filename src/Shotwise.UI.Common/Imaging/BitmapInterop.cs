using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwise.Core.Imaging;

namespace Shotwise.UI.Common.Imaging;

/// <summary>Conversions between Core's premultiplied BGRA images and WPF bitmaps.</summary>
public static class BitmapInterop
{
    /// <summary>Creates a frozen Pbgra32 BitmapSource (copies the pixels).</summary>
    public static BitmapSource ToBitmapSource(this BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bs = BitmapSource.Create(image.Width, image.Height, image.DpiX, image.DpiY, PixelFormats.Pbgra32, null, image.Pixels, image.Stride);
        bs.Freeze();
        return bs;
    }

    /// <summary>Writes the image into an existing WriteableBitmap of the same size (fast path for live previews).</summary>
    public static void CopyTo(this BgraImage image, WriteableBitmap target)
    {
        if (target.PixelWidth != image.Width || target.PixelHeight != image.Height) throw new ArgumentException("Size mismatch.", nameof(target));
        target.WritePixels(new System.Windows.Int32Rect(0, 0, image.Width, image.Height), image.Pixels, image.Stride, 0);
    }

    /// <summary>Converts any BitmapSource to a premultiplied BGRA image.</summary>
    public static BgraImage ToBgraImage(this BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource src = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var w = src.PixelWidth;
        var h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        return new BgraImage(w, h, pixels) { DpiX = src.DpiX > 0 ? src.DpiX : 96, DpiY = src.DpiY > 0 ? src.DpiY : 96 };
    }

    public static Color ToColor(this Argb c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static Argb ToArgb(this Color c) => Argb.FromArgb(c.A, c.R, c.G, c.B);

    public static SolidColorBrush ToBrush(this Argb c)
    {
        var b = new SolidColorBrush(c.ToColor());
        b.Freeze();
        return b;
    }
}
