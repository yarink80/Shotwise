using System.Runtime.InteropServices;
using Shotwise.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Shotwise.Capture;

internal static class GraphicsCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(in Guid iid); }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, in Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    internal static BgraImage Capture(IntPtr handle, bool window, bool hdr, double whiteLevel, CancellationToken ct)
    {
        using var source = new FrameSource(handle, window, hdr, whiteLevel);
        return source.NextFrame(ct);
    }
    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool window)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Graphics capture is unavailable.");
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        GraphicsCaptureItem item;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, typeof(IGraphicsCaptureItemInterop).GUID, out var factory));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                try
                {
                    var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                    var ptr = window ? interop.CreateForWindow(handle, iid) : interop.CreateForMonitor(handle, iid);
                    try { item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr); }
                    finally { Marshal.Release(ptr); }
                }
                finally { Marshal.ReleaseComObject(interop); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstring); }
        return item;
    }
    internal sealed class FrameSource : IDisposable
    {
        private readonly ID3D11Device device;
        private readonly ID3D11DeviceContext context;
        private IDirect3DDevice? winDevice;
        private Direct3D11CaptureFramePool? pool;
        private GraphicsCaptureSession? session;
        private readonly bool hdr;
        private readonly double whiteLevel;
        private BgraImage? latest;
        internal FrameSource(IntPtr handle, bool window, bool hdr, double whiteLevel)
        {
            this.hdr = hdr; this.whiteLevel = whiteLevel;
            var item = CreateItem(handle, window);
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device, out context).CheckError();
            try
            {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
            try { winDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
            finally { Marshal.Release(ptr); }
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winDevice, hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                session.StartCapture();
            }
            catch { Dispose(); throw; }
        }
        internal BgraImage NextFrame(CancellationToken ct)
        {
                var deadline = Environment.TickCount64 + (latest is null ? 2000 : 40);
                while (Environment.TickCount64 < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    using var frame = pool!.TryGetNextFrame();
                    if (frame is null) { ct.WaitHandle.WaitOne(10); continue; }
                    var surfacePtr = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
                    try
                    {
                        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfacePtr);
                        try
                        {
                            var texturePtr = access.GetInterface(typeof(ID3D11Texture2D).GUID);
                            using var texture = new ID3D11Texture2D(texturePtr);
                            using var toneMapped = hdr ? HdrToneMapper.Map(device, texture, whiteLevel) : null;
                            var captureTexture = toneMapped ?? texture;
                            var desc = captureTexture.Description;
                            desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                            using var staging = device.CreateTexture2D(desc); context.CopyResource(staging, captureTexture);
                            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            try { latest = Read(mapped, (int)desc.Width, (int)desc.Height); return latest.Clone(); }
                            finally { context.Unmap(staging, 0); }
                        }
                        finally { Marshal.ReleaseComObject(access); }
                    }
                    finally { Marshal.Release(surfacePtr); }
                }
                ct.ThrowIfCancellationRequested();
                return latest?.Clone() ?? throw new TimeoutException("Graphics capture produced no frame.");
        }
        public void Dispose() { session?.Dispose(); pool?.Dispose(); (winDevice as IDisposable)?.Dispose(); context.Dispose(); device.Dispose(); }
    }
    private static BgraImage Read(MappedSubresource mapped, int width, int height)
    {
        var result = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, result.Pixels, y * result.Stride, result.Stride);
        }
        for (int i = 3; i < result.Pixels.Length; i += 4) result.Pixels[i] = 255;
        return result;
    }
}
