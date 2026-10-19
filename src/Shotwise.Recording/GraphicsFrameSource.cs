using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Shotwise.Recording;

/// <summary>Free-threaded WGC frame pools; all texture access and teardown stay on the recording thread.</summary>
internal sealed class GraphicsFrameSource : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISurfaceAccess { nint GetInterface(in Guid iid); }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly RectI _bounds;
    private readonly List<Item> _items = new();
    private IDirect3DDevice? _winDevice;
    private sealed class Item : IDisposable
    {
        public required GraphicsCaptureItem Capture;
        public required Direct3D11CaptureFramePool Pool;
        public required GraphicsCaptureSession Session;
        public required RectI Bounds;
        public ID3D11Texture2D? Last;
        public Windows.Graphics.SizeInt32 PoolSize;
        public void Dispose() { Last?.Dispose(); Session.Dispose(); Pool.Dispose(); }
    }
    public GraphicsFrameSource(ID3D11Device device, ID3D11DeviceContext context, RecordingTarget target, RectI bounds, bool cursor, ILogger logger)
    {
        _device = device; _context = context; _bounds = bounds;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Graphics capture unavailable.");
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
            try { _winDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pointer); } finally { Marshal.Release(pointer); }
            if (target.Window is { } window) Add(window, false, bounds, cursor, logger);
            else foreach (var monitor in DesktopNative.Monitors().Where(m => Intersects(m.Bounds, bounds))) Add(monitor.Handle, true, monitor.Bounds, cursor, logger);
            if (_items.Count == 0) throw new InvalidOperationException("No intersecting monitor.");
        }
        catch { Dispose(); throw; }
    }
    private static bool Intersects(RectI a, RectI b) => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    private static GraphicsCaptureItem Create(nint handle, bool monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        nint factory = 0;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, typeof(ICaptureInterop).GUID, out factory));
            var interop = (ICaptureInterop)Marshal.GetObjectForIUnknown(factory);
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint result;
            try { result = monitor ? interop.CreateForMonitor(handle, iid) : interop.CreateForWindow(handle, iid); }
            finally { Marshal.ReleaseComObject(interop); }
            try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally { if (factory != 0) Marshal.Release(factory); WindowsDeleteString(hstring); }
    }
    private void Add(nint handle, bool monitor, RectI bounds, bool cursor, ILogger logger)
    {
        var capture = Create(handle, monitor);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, capture.Size);
        GraphicsCaptureSession? session = null;
        try
        {
            session = pool.CreateCaptureSession(capture); session.IsCursorCaptureEnabled = cursor;
            try { session.IsBorderRequired = false; } catch (Exception e) { logger.LogDebug(e, "Borderless capture unavailable"); }
            session.StartCapture();
            _items.Add(new Item { Capture = capture, Pool = pool, Session = session, Bounds = bounds, PoolSize = capture.Size });
        }
        catch { session?.Dispose(); pool.Dispose(); throw; }
    }
    public ID3D11Texture2D? Capture()
    {
        var result = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_bounds.Width, (uint)_bounds.Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using (var view = _device.CreateRenderTargetView(result)) _context.ClearRenderTargetView(view, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        try
        {
        foreach (var item in _items)
        {
            using var frame = item.Pool.TryGetNextFrame();
            if (frame != null)
            {
                using var surface = frame.Surface;
                var access = surface.As<ISurfaceAccess>();
                nint pointer;
                try { pointer = access.GetInterface(typeof(ID3D11Texture2D).GUID); }
                finally { if (Marshal.IsComObject(access)) Marshal.ReleaseComObject(access); }
                using var texture = new ID3D11Texture2D(pointer);
                var desc = texture.Description;
                // WGC window resize is intentionally clipped/padded to the prepared frame size.
                var left = Math.Max(_bounds.X, item.Bounds.X); var top = Math.Max(_bounds.Y, item.Bounds.Y);
                var width = Math.Min(_bounds.X + _bounds.Width, item.Bounds.X + (int)desc.Width) - left;
                var height = Math.Min(_bounds.Y + _bounds.Height, item.Bounds.Y + (int)desc.Height) - top;
                if (width > 0 && height > 0)
                {
                    item.Last?.Dispose();
                    item.Last = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
                    _context.CopySubresourceRegion(item.Last, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(left - item.Bounds.X, top - item.Bounds.Y, 0, left - item.Bounds.X + width, top - item.Bounds.Y + height, 1));
                }
                if (frame.ContentSize.Width > 0 && frame.ContentSize.Height > 0 && (frame.ContentSize.Width != item.PoolSize.Width || frame.ContentSize.Height != item.PoolSize.Height))
                {
                    item.Pool.Recreate(_winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
                    item.PoolSize = frame.ContentSize;
                }
            }
            if (item.Last == null) { result.Dispose(); return null; }
            _context.CopySubresourceRegion(result, 0, (uint)Math.Max(0, item.Bounds.X - _bounds.X), (uint)Math.Max(0, item.Bounds.Y - _bounds.Y), 0, item.Last, 0);
        }
        return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal static void Copy(BgraImage source, BgraImage destination, int x, int y)
    {
        var width = Math.Min(source.Width, destination.Width - x); var height = Math.Min(source.Height, destination.Height - y);
        if (width <= 0 || height <= 0) return;
        for (var row = 0; row < height; row++) Buffer.BlockCopy(source.Pixels, row * source.Width * 4, destination.Pixels, ((row + y) * destination.Width + x) * 4, width * 4);
    }
    public void Dispose()
    {
        foreach (var item in _items) item.Dispose(); _items.Clear();
        (_winDevice as IDisposable)?.Dispose(); _winDevice = null;
    }
}
