using System.Runtime.InteropServices;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;

namespace Shotwise.Vision.Internal;

/// <summary>Conversions between the Core raster types and OpenCV matrices.</summary>
internal static class CvInterop
{
    /// <summary>Copies premultiplied BGRA into a new CV_8UC4 matrix.</summary>
    public static Mat ToMatBgra(BgraImage img)
    {
        var m = new Mat(img.Height, img.Width, MatType.CV_8UC4);
        Marshal.Copy(img.Pixels, 0, m.Data, img.Width * img.Height * 4);
        return m;
    }

    public static Mat ToMatBgra(byte[] bgra, int w, int h)
    {
        var m = new Mat(h, w, MatType.CV_8UC4);
        Marshal.Copy(bgra, 0, m.Data, w * h * 4);
        return m;
    }

    public static Mat ToMatBgr(byte[] bgr, int w, int h)
    {
        var m = new Mat(h, w, MatType.CV_8UC3);
        Marshal.Copy(bgr, 0, m.Data, w * h * 3);
        return m;
    }

    public static byte[] ToBytes(Mat m)
    {
        using var c = m.IsContinuous() ? null : m.Clone();
        var src = c ?? m;
        var len = (int)(src.Total() * src.ElemSize());
        var bytes = new byte[len];
        Marshal.Copy(src.Data, bytes, 0, len);
        return bytes;
    }

    /// <summary>Opaque 3-channel BGR copy (alpha un-premultiplied against white when translucent) of an optional pixel region.</summary>
    public static Mat ToBgr(BgraImage img, RectI? region = null)
    {
        var r = (region ?? img.Bounds).Intersect(img.Bounds);
        if (r.IsEmpty) throw new ArgumentOutOfRangeException(nameof(region));
        var bgr = new byte[r.Width * r.Height * 3];
        var px = img.Pixels;
        var o = 0;
        for (var y = r.Y; y < r.Bottom; y++)
        {
            var i = (y * img.Width + r.X) * 4;
            for (var x = 0; x < r.Width; x++, i += 4, o += 3)
            {
                var a = px[i + 3];
                if (a == 255) { bgr[o] = px[i]; bgr[o + 1] = px[i + 1]; bgr[o + 2] = px[i + 2]; }
                else
                {
                    // composite premultiplied colour over white
                    var inv = 255 - a;
                    bgr[o] = (byte)Math.Min(255, px[i] + inv);
                    bgr[o + 1] = (byte)Math.Min(255, px[i + 1] + inv);
                    bgr[o + 2] = (byte)Math.Min(255, px[i + 2] + inv);
                }
            }
        }
        var m = new Mat(r.Height, r.Width, MatType.CV_8UC3);
        Marshal.Copy(bgr, 0, m.Data, bgr.Length);
        return m;
    }

    public static Mat ToGray(BgraImage img, RectI? region = null)
    {
        using var bgr = ToBgr(img, region);
        var g = new Mat();
        Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        return g;
    }

    /// <summary>Opaque BgraImage from a CV_8UC3 (BGR) or CV_8UC4 (premultiplied BGRA) matrix.</summary>
    public static BgraImage FromMat(Mat m, double dpiX = 96, double dpiY = 96)
    {
        var bytes = ToBytes(m);
        var w = m.Cols;
        var h = m.Rows;
        if (m.Channels() == 4) return new BgraImage(w, h, bytes) { DpiX = dpiX, DpiY = dpiY };
        if (m.Channels() == 1)
        {
            var o4 = new byte[w * h * 4];
            for (int i = 0, j = 0; i < w * h; i++, j += 4) { o4[j] = o4[j + 1] = o4[j + 2] = bytes[i]; o4[j + 3] = 255; }
            return new BgraImage(w, h, o4) { DpiX = dpiX, DpiY = dpiY };
        }
        var outp = new byte[w * h * 4];
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
        {
            outp[j] = bytes[i * 3]; outp[j + 1] = bytes[i * 3 + 1]; outp[j + 2] = bytes[i * 3 + 2]; outp[j + 3] = 255;
        }
        return new BgraImage(w, h, outp) { DpiX = dpiX, DpiY = dpiY };
    }

    public static Mat MaskToMat(Mask m)
    {
        var mat = new Mat(m.Height, m.Width, MatType.CV_8UC1);
        Marshal.Copy(m.Data, 0, mat.Data, m.Width * m.Height);
        return mat;
    }

    public static Mask MatToMask(Mat m)
    {
        var bytes = ToBytes(m);
        return new Mask(m.Cols, m.Rows, bytes);
    }

    public static Rect ToCv(RectI r) => new(r.X, r.Y, r.Width, r.Height);
    public static RectI ToRectI(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Float CIE L*a*b* matrix (L 0..100, a/b about -128..127) of a BGR byte matrix.</summary>
    public static Mat ToLab32(Mat bgr8)
    {
        using var f = new Mat();
        bgr8.ConvertTo(f, MatType.CV_32FC3, 1.0 / 255.0);
        var lab = new Mat();
        Cv2.CvtColor(f, lab, ColorConversionCodes.BGR2Lab);
        return lab;
    }
}
