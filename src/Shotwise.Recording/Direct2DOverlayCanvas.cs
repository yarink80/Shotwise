using System.Numerics;
using System.Runtime.InteropServices;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using CoreSize = Shotwise.Core.Geometry.SizeI;
using static Vortice.Direct3D11.D3D11;

namespace Shotwise.Recording;

/// <summary>Direct2D/DirectWrite canvas bound to an owned BGRA D3D11 render texture.</summary>
public sealed class Direct2DOverlayCanvas : IOverlayCanvas, IDisposable
{
    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    private readonly ID3D11Texture2D _texture, _readback;
    private readonly ID2D1Factory _factory;
    private readonly IDWriteFactory _writeFactory;
    private readonly ID2D1RenderTarget _target;
    public CoreSize FrameSize { get; }
    public Direct2DOverlayCanvas(CoreSize size)
    {
        FrameSize = size;
        var created = D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
        if (created.Failure)
        {
            device?.Dispose(); context?.Dispose();
            D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out device, out context).CheckError();
        }
        Device = device; Context = context;
        using (var multi = Context.QueryInterface<ID3D11Multithread>()) multi.SetMultithreadProtected(true);
        try
        {
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource);
            _texture = Device.CreateTexture2D(description);
            description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
            _readback = Device.CreateTexture2D(description);
            _factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
            _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            using var surface = _texture.QueryInterface<IDXGISurface>();
            _target = _factory.CreateDxgiSurfaceRenderTarget(surface, new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        }
        catch { Dispose(); throw; }
    }
    internal void Begin(BgraImage image)
    {
        _target.BeginDraw(); _target.Clear(new Color4(0, 0, 0, 1));
        DrawImage(image, new Rect2(0, 0, FrameSize.Width, FrameSize.Height));
    }
    internal void Begin(ID3D11Texture2D texture)
    {
        _target.BeginDraw(); _target.Clear(new Color4(0, 0, 0, 1));
        using var surface = texture.QueryInterface<IDXGISurface>();
        using var bitmap = _target.CreateSharedBitmap(surface, new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        _target.DrawBitmap(bitmap, 1, BitmapInterpolationMode.Linear, new Vortice.Mathematics.Rect(0, 0, FrameSize.Width, FrameSize.Height));
    }
    internal BgraImage End()
    {
        _target.EndDraw().CheckError();
        Context.CopyResource(_readback, _texture);
        Context.Map(_readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var image = new BgraImage(FrameSize.Width, FrameSize.Height);
            for (var y = 0; y < FrameSize.Height; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, image.Pixels, y * FrameSize.Width * 4, FrameSize.Width * 4);
            return image;
        }
        finally { Context.Unmap(_readback, 0); }
    }
    internal ID3D11Texture2D EndTexture()
    {
        _target.EndDraw().CheckError();
        var texture = Device.CreateTexture2D(_texture.Description);
        Context.CopyResource(texture, _texture);
        return texture;
    }
    private static Color4 Color(Argb c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    private ID2D1SolidColorBrush Brush(Argb color) => _target.CreateSolidColorBrush(Color(color));
    private static Vortice.Mathematics.Rect Rect(Rect2 r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    private static Vector2 Point(Point2 p) => new((float)p.X, (float)p.Y);
    public void DrawLine(Point2 from, Point2 to, Argb color, double width)
    {
        using var brush = Brush(color); _target.DrawLine(Point(from), Point(to), brush, (float)width);
    }
    public void DrawRectangle(Rect2 rect, Argb? stroke, double strokeWidth, Argb? fill, double cornerRadius = 0)
    {
        var rounded = new RoundedRectangle(new System.Drawing.RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height), (float)cornerRadius, (float)cornerRadius);
        if (fill is { } f) { using var b = Brush(f); _target.FillRoundedRectangle(rounded, b); }
        if (stroke is { } s) { using var b = Brush(s); _target.DrawRoundedRectangle(rounded, b, (float)strokeWidth); }
    }
    public void DrawEllipse(Rect2 bounds, Argb? stroke, double strokeWidth, Argb? fill)
    {
        var ellipse = new Ellipse(new Vector2((float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2)), (float)bounds.Width / 2, (float)bounds.Height / 2);
        if (fill is { } f) { using var b = Brush(f); _target.FillEllipse(ellipse, b); }
        if (stroke is { } s) { using var b = Brush(s); _target.DrawEllipse(ellipse, b, (float)strokeWidth); }
    }
    public void DrawPolyline(IReadOnlyList<Point2> points, Argb color, double width, bool closed = false)
    {
        if (points.Count < 2) return;
        using var geometry = _factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(Point(points[0]), FigureBegin.Hollow);
            foreach (var p in points.Skip(1)) sink.AddLine(Point(p));
            sink.EndFigure(closed ? FigureEnd.Closed : FigureEnd.Open); sink.Close();
        }
        using var brush = Brush(color); _target.DrawGeometry(geometry, brush, (float)width);
    }
    public void DrawText(string text, Point2 origin, string fontFamily, double fontSize, Argb color, bool bold = false)
    {
        using var format = _writeFactory.CreateTextFormat(fontFamily, bold ? FontWeight.Bold : FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, (float)fontSize);
        using var brush = Brush(color);
        _target.DrawText(text, format, new Vortice.Mathematics.Rect((float)origin.X, (float)origin.Y, FrameSize.Width, FrameSize.Height), brush);
    }
    public unsafe void DrawImage(BgraImage image, Rect2 destination, double opacity = 1)
    {
        fixed (byte* pixels = image.Pixels)
        {
            using var bitmap = _target.CreateBitmap(new Vortice.Mathematics.SizeI(image.Width, image.Height), (nint)pixels, (uint)(image.Width * 4), new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            _target.DrawBitmap(bitmap, (float)Math.Clamp(opacity, 0, 1), BitmapInterpolationMode.Linear, Rect(destination));
        }
    }
    public void Dispose()
    {
        _target?.Dispose(); _writeFactory?.Dispose(); _factory?.Dispose(); _readback?.Dispose(); _texture?.Dispose(); Context?.Dispose(); Device?.Dispose();
    }
}




